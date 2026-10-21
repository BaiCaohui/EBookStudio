using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using EbookToolSuite.App.Smoke;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Reader.WebView;
using Microsoft.Web.WebView2.Core;

namespace EbookToolSuite.App.Views;

/// <summary>
/// T0-9 渲染探针。把一个文件直接交给 WebView2 打开，记录四件事：
///   1. 导航有没有完成、成功，失败码是什么
///   2. document.contentType  浏览器自己认定的 MIME
///   3. 正文文本长度  白屏的特征是导航成功但正文为空
///   4. 有没有触发下载  Chromium 遇到不认识的类型时的默认行为
///
/// 结论写成 JSON 到数据目录，由 build\smoke-render-probe.ps1 判定，
/// 不靠人眼看窗口。
/// </summary>
public partial class RenderProbeWindow : Window
{
    private const int NavigateTimeoutSeconds = 45;
    private const int BodyHeadMaxChars = 200;

    private readonly AppPaths _paths;
    private readonly string _targetFile;
    private readonly string _reportPath;
    private readonly List<string> _notes = new();

    private readonly TaskCompletionSource<bool> _navFinished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _navCompleted;
    private bool _navSuccess;
    private string? _navError;
    private bool _downloadTriggered;
    private string? _downloadName;
    private bool _downloadCancelled;

    public RenderProbeWindow(AppPaths paths, string targetFile, string reportPath)
    {
        _paths = paths;
        _targetFile = targetFile;
        _reportPath = reportPath;
        InitializeComponent();
    }

    public async Task<RenderProbeReport> RunAsync()
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();

        var fileUri = new Uri(_targetFile).AbsoluteUri;
        string? version = null;
        string? contentType = null;
        var bodyLength = 0;
        string? bodyHead = null;

        try
        {
            var provider = new WebViewEnvironmentProvider(_paths);
            var host = new ReaderWebHost(provider);

            await host.InitializeAsync(Web, _paths.ReaderCacheDir).ConfigureAwait(true);

            var core = Web.CoreWebView2
                ?? throw new InvalidOperationException("CoreWebView2 未就绪");

            version = core.Environment.BrowserVersionString;

            core.NavigationCompleted += OnNavigationCompleted;
            core.DownloadStarting += OnDownloadStarting;

            SetStatus("导航到 " + fileUri);
            core.Navigate(fileUri);

            var finished = await WaitAsync(_navFinished.Task, NavigateTimeoutSeconds)
                .ConfigureAwait(true);

            if (!finished)
            {
                _notes.Add($"导航在 {NavigateTimeoutSeconds} 秒内没有产生 NavigationCompleted");
            }

            // 导航结束后再探 DOM。这两种情况下这些调用会失败，各兜各的：
            // 页面已经被下载流程接管，或者页面已被销毁。
            contentType = await TryEvalAsync("document.contentType").ConfigureAwait(true);

            var text = await TryEvalAsync(
                "document.body ? document.body.innerText : ''").ConfigureAwait(true);

            bodyLength = text?.Length ?? 0;
            bodyHead = text is null
                ? null
                : Truncate(text.Replace('\r', ' ').Replace('\n', ' ').Trim(),
                    BodyHeadMaxChars);
        }
        catch (Exception ex)
        {
            _notes.Add("探针异常：" + ex.GetType().Name + ": " + ex.Message);
        }

        stopwatch.Stop();

        if (_downloadTriggered)
        {
            _notes.Add("WebView2 触发了下载：它不认这个格式，只是把文件当二进制丢给下载器");
        }

        if (_navCompleted && !_navSuccess)
        {
            _notes.Add("导航失败：" + _navError);
        }

        if (_navSuccess && !_downloadTriggered && bodyLength == 0)
        {
            _notes.Add("导航成功但正文为空，等于白屏");
        }

        var report = new RenderProbeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            stopwatch.ElapsedMilliseconds,
            _targetFile,
            new FileInfo(_targetFile).Length,
            fileUri,
            version,
            _navCompleted,
            _navSuccess,
            _navError,
            contentType,
            bodyLength,
            bodyHead,
            _downloadTriggered,
            _downloadName,
            _downloadCancelled,
            _notes.ToList());

        WriteReport(report);
        return report;
    }

    // ---------- WebView2 事件 ----------

    private void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        _navCompleted = true;
        _navSuccess = e.IsSuccess;
        _navError = e.IsSuccess ? null : e.WebErrorStatus.ToString();
        _navFinished.TrySetResult(true);
    }

    private void OnDownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs e)
    {
        _downloadTriggered = true;
        _downloadName = string.IsNullOrEmpty(e.ResultFilePath)
            ? null
            : Path.GetFileName(e.ResultFilePath);

        // 不真的落盘：探针只要现象，不要副作用
        e.Cancel = true;
        _downloadCancelled = true;

        // 走下载路径时 NavigationCompleted 不一定来，主动放行，别干等超时
        _navFinished.TrySetResult(true);
    }

    // ---------- 辅助 ----------

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        _notes.Add(text);
    }

    private async Task<string?> TryEvalAsync(string script)
    {
        try
        {
            var raw = await Web.CoreWebView2!.ExecuteScriptAsync(script)
                .ConfigureAwait(true);

            if (string.IsNullOrEmpty(raw))
            {
                return raw;
            }

            try
            {
                return JsonSerializer.Deserialize<string>(raw);
            }
            catch (JsonException)
            {
                return raw;
            }
        }
        catch (Exception ex)
        {
            _notes.Add("求值失败 " + script + " -> " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    private static async Task<T?> WaitAsync<T>(Task<T> task, int seconds)
    {
        var delay = Task.Delay(TimeSpan.FromSeconds(seconds));
        var winner = await Task.WhenAny(task, delay).ConfigureAwait(true);
        return winner == task ? await task.ConfigureAwait(true) : default;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";

    private void WriteReport(RenderProbeReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_reportPath)!);
        File.WriteAllText(
            _reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
            }),
            new UTF8Encoding(false));
    }
}