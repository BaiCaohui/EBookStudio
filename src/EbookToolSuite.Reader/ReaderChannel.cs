using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace EbookToolSuite.Reader;

/// <summary>从阅读器页面收到的消息。</summary>
public sealed record ReaderInboundMessage(string Type, JsonObject Payload);

/// <summary>
/// 宿主与阅读器页面之间的消息通道。协议见设计文档 8.1。
/// </summary>
/// <remarks>
/// 【为什么不用 AddHostObjectToScript】设计文档 8.1 已定：宿主对象在异步场景下会死锁，
/// 且调试困难。全程走 postMessage。
///
/// 【关于 id 配对】协议预留了 id 字段用于请求/响应配对，阶段 1 的命令
/// （load / gotoChapter / setTheme / setTypography）都是单向的，没有真正的
/// "请求-响应"，所以这里只实现"等某个事件到达"（WaitForAsync）。
/// 等出现第一条需要回应的命令再加 id 配对，避免留一段没人调用的代码。
/// </remarks>
public sealed class ReaderChannel : IDisposable
{
    /// <summary>必须与 Assets/Reader/reader.js 里的 PROTOCOL_VERSION 一致。</summary>
    public const int ProtocolVersion = 1;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 握手类消息到达后要留一份：等待者可能在消息【之后】才注册。
    /// 这是实测踩到的真 bug：reader.js 在 DOMContentLoaded 就发 reader.ready，
    /// 而宿主是在 Navigate 返回之后才去等它，结果消息丢了、干等 30 秒超时。
    /// 只缓存这类"一次性状态"，不缓存 chapterChanged 那种流式消息，
    /// 否则等待者会拿到过期结果。
    /// </summary>
    private static readonly HashSet<string> StickyTypes =
        new(StringComparer.Ordinal) { "reader.ready" };

    private readonly CoreWebView2 _core;
    private readonly ILogger _log;
    private readonly List<(string Type, TaskCompletionSource<ReaderInboundMessage> Source)> _waiters = new();
    private readonly ConcurrentDictionary<string, ReaderInboundMessage> _sticky = new(StringComparer.Ordinal);
    private readonly object _waiterLock = new();
    private int _counter;
    private bool _disposed;

    public ReaderChannel(CoreWebView2 core, ILogger log)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _core.WebMessageReceived += OnRawMessage;
    }

    /// <summary>任何一条通过校验的消息都会触发。UI 用它更新章名、进度条。</summary>
    public event EventHandler<ReaderInboundMessage>? MessageReceived;

    /// <summary>宿主发给阅读器的命令。</summary>
    public void Post(string type, object? payload = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        var envelope = new JsonObject
        {
            ["v"] = ProtocolVersion,
            ["id"] = "c-" + Interlocked.Increment(ref _counter).ToString("D6"),
            ["type"] = type,
            ["payload"] = payload is null
                ? new JsonObject()
                : JsonSerializer.SerializeToNode(payload),
        };

        _log.LogDebug("发给阅读器：{Type}", type);
        _core.PostWebMessageAsJson(envelope.ToJsonString());
    }

    /// <summary>等某一种消息到达。超时抛 WebMessageTimeout（ETS-READ-0404）。</summary>
    /// <summary>
    /// 清掉握手缓存（重新加载阅读器页面时用）。
    /// 不清的话第二次 WaitForAsync("reader.ready") 会立刻拿到上一次那条 ready，
    /// 于是 load 会在新页面挂好监听之前发出去 -> 页面永远停在"正在打开"。
    /// </summary>
    public void ResetSticky()
    {
        lock (_waiterLock)
        {
            _sticky.Clear();
            _waiters.Clear();
        }
    }

    public async Task<ReaderInboundMessage> WaitForAsync(
        string type, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        // 消息可能已经到过了（握手场景很常见），有就直接给
        if (StickyTypes.Contains(type) && _sticky.TryGetValue(type, out var replay))
        {
            return replay;
        }

        var source = new TaskCompletionSource<ReaderInboundMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_waiterLock)
        {
            _waiters.Add((type, source));
        }

        using var cancelSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancelSource.CancelAfter(timeout ?? DefaultTimeout);

        using var registration = cancelSource.Token.Register(() =>
        {
            lock (_waiterLock)
            {
                _waiters.RemoveAll(w => ReferenceEquals(w.Source, source));
            }

            source.TrySetCanceled();
        });

        try
        {
            return await source.Task.ConfigureAwait(true);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AppException(
                ErrorCode.WebMessageTimeout,
                "阅读器没有响应，关掉这本书重新打开试试。",
                "timed out waiting for " + type);
        }
    }

    private void OnRawMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;

        try
        {
            json = e.WebMessageAsJson;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "阅读器消息取不出来（可能不是 JSON）");
            return;
        }

        JsonObject? envelope;
        try
        {
            envelope = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "阅读器消息不是合法 JSON：{Json}", Truncate(json));
            return;
        }

        if (envelope is null)
        {
            return;
        }

        var version = envelope["v"]?.GetValue<int>() ?? 0;
        if (version != ProtocolVersion)
        {
            _log.LogWarning("阅读器协议版本不匹配：收到 {Version}", version);
            return;
        }

        var type = envelope["type"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(type))
        {
            return;
        }

        var payload = envelope["payload"] as JsonObject ?? new JsonObject();
        var message = new ReaderInboundMessage(type, payload);

        if (_log.IsEnabled(LogLevel.Debug))
        {
            _log.LogDebug("阅读器消息 {Type} {Payload}", type, payload.ToJsonString());
        }

        if (StickyTypes.Contains(type))
        {
            _sticky[type] = message;
        }

        CompleteWaiters(message);

        try
        {
            MessageReceived?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            // 订阅者的异常不能把消息循环带崩
            _log.LogWarning(ex, "处理阅读器消息 {Type} 时出错", type);
        }
    }

    private void CompleteWaiters(ReaderInboundMessage message)
    {
        List<TaskCompletionSource<ReaderInboundMessage>>? matched = null;

        lock (_waiterLock)
        {
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(_waiters[i].Type, message.Type, StringComparison.Ordinal))
                {
                    continue;
                }

                (matched ??= new()).Add(_waiters[i].Source);
                _waiters.RemoveAt(i);
            }
        }

        if (matched is null)
        {
            return;
        }

        foreach (var source in matched)
        {
            source.TrySetResult(message);
        }
    }

    private static string Truncate(string text)
        => text.Length <= 500 ? text : text[..500] + "...";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _core.WebMessageReceived -= OnRawMessage;

        List<TaskCompletionSource<ReaderInboundMessage>> pending;
        lock (_waiterLock)
        {
            pending = _waiters.Select(w => w.Source).ToList();
            _waiters.Clear();
            _sticky.Clear();
        }

        foreach (var source in pending)
        {
            source.TrySetCanceled();
        }
    }
}