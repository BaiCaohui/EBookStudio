using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EbookToolSuite.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Infrastructure.Config;

/// <summary>
/// Config.json 的读写实现。规格见设计文档 9.10.4。
/// </summary>
/// <remarks>
/// 三条不能破的规则：
/// 1. 全程在 JsonObject 上按点分路径增删改，从不做 POCO 映射（否则会静默丢掉未知字段）。
/// 2. 配置文件损坏不能让软件起不来：坏文件改名 Config.bad.json，用空配置继续启动。
/// 3. 落盘先写 .tmp 再同卷改名，断电不会留下半截 Config.json。
/// </remarks>
public sealed class JsonConfigStore : IConfigStore
{
    /// <summary>读的时候容忍注释与尾随逗号（用户会手改这个文件）。写出去的一律是纯 JSON。</summary>
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // 中文不做转义：这个文件是要给人看、给人手改的
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>保护内存里那棵树。只有序列化那一小段持有它，不跨 IO。</summary>
    private readonly object _sync = new();

    /// <summary>串行化落盘：两个后台任务同时保存时，后来者不能先改名。</summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private readonly ILogger<JsonConfigStore> _log;

    private JsonObject _root = new();

    public JsonConfigStore(IAppPaths paths, ILogger<JsonConfigStore> log)
        : this(paths.ConfigFile, log)
    {
    }

    /// <summary>直接给文件路径。单测用；业务代码走上面那个构造。</summary>
    public JsonConfigStore(string filePath, ILogger<JsonConfigStore> log)
    {
        FilePath = filePath;
        _log = log;
    }

    /// <summary>配置文件路径（诊断页要显示它）。</summary>
    public string FilePath { get; }

    // ---------- 加载 ----------

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        string? text = null;

        if (File.Exists(FilePath))
        {
            try
            {
                text = await File.ReadAllTextAsync(FilePath, Utf8NoBom, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不出来只能先当空的用；文件本身不动（可能只是被别的进程占着）
                _log.LogError(ex, "配置文件读不出来，本次启动用空配置：{Path}", FilePath);
            }
        }

        var parsed = Parse(text);

        lock (_sync)
        {
            _root = parsed;
        }

        _log.LogInformation("配置已加载：{Path}（{Count} 个顶层节点）", FilePath, parsed.Count);
    }

    private JsonObject Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            // 全新安装：文件还不存在，视为空对象，首次 SaveAsync 时创建
            return new JsonObject();
        }

        try
        {
            var node = JsonNode.Parse(text, nodeOptions: null, documentOptions: ReadOptions);
            return node as JsonObject ?? throw new JsonException("根节点不是 JSON 对象");
        }
        catch (JsonException ex)
        {
            Quarantine(ex);
            return new JsonObject();
        }
    }

    /// <summary>坏文件改名隔离。绝不在启动路径上抛异常：自用软件没有 IT 支持。</summary>
    private void Quarantine(JsonException ex)
    {
        var dir = Path.GetDirectoryName(FilePath) ?? ".";
        var bad = Path.Combine(dir, Path.GetFileNameWithoutExtension(FilePath) + ".bad.json");

        try
        {
            File.Move(FilePath, bad, overwrite: true);
            _log.LogError(ex, "配置文件不是合法 JSON，已改名为 {Bad}，本次启动用空配置", bad);
        }
        catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
        {
            _log.LogError(moveEx, "配置文件不是合法 JSON，且无法改名隔离：{Path}", FilePath);
        }
    }

    // ---------- 读 ----------

    public JsonNode? Get(string dottedPath)
    {
        lock (_sync)
        {
            return Resolve(_root, dottedPath);
        }
    }

    public string? GetString(string dottedPath, string? fallback = null)
        => Get(dottedPath) is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : fallback;

    public bool GetBool(string dottedPath, bool fallback)
        => Get(dottedPath) is JsonValue value && value.TryGetValue<bool>(out var flag)
            ? flag
            : fallback;

    public double GetDouble(string dottedPath, double fallback)
        => Get(dottedPath) is JsonValue value && value.TryGetValue<double>(out var number)
            ? number
            : fallback;

    private static JsonNode? Resolve(JsonObject root, string dottedPath)
    {
        if (string.IsNullOrWhiteSpace(dottedPath))
        {
            return root;
        }

        JsonNode? current = root;

        foreach (var segment in dottedPath.Split('.'))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    // ---------- 写 ----------

    public Task SetAsync(string dottedPath, JsonNode? value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            SetCore(dottedPath, value);
        }

        return Task.CompletedTask;
    }

    private void SetCore(string dottedPath, JsonNode? value)
    {
        var segments = dottedPath.Split('.');

        if (segments.Length == 0 || segments.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException("配置路径不能为空：" + dottedPath, nameof(dottedPath));
        }

        var current = _root;

        // 中间层级不存在就逐级创建
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!current.TryGetPropertyValue(segments[i], out var next) || next is not JsonObject child)
            {
                child = new JsonObject();
                current[segments[i]] = child;
            }

            current = child;
        }

        current[segments[^1]] = value;
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        string text;

        // 先在锁里把内存树序列化成字符串，IO 不占着锁
        lock (_sync)
        {
            text = _root.ToJsonString(WriteOptions) + "\n";
        }

        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            WriteAtomic(text);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void WriteAtomic(string text)
    {
        var dir = Path.GetDirectoryName(FilePath);

        try
        {
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, text, Utf8NoBom);

            // 同目录同卷，改名是原子的；将来若把临时文件放到别的盘，这里要换成 File.Replace
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteTmp();

            // 记 WRN 后原样抛出：调用方要知道没写成功，但这件事本身不能让软件崩掉
            _log.LogWarning(ex, "配置保存失败（只读？被占用？），改动只留在内存里：{Path}", FilePath);
            throw;
        }
    }

    private void TryDeleteTmp()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "残留的临时文件删不掉：{Path}.tmp", FilePath);
        }
    }
}