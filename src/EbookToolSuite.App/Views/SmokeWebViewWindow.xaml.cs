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
/// WebView2 冒烟测试（T0-6）。把设计文档里承诺的六件事逐条验一遍：
///   1. 窗口能显示虚拟主机 http://ebook.local/ 的内容
///   2. C# 调 JS 成功
///   3. JS 调 C# 成功
///   4. userDataFolder 落在数据目录内，没有污染 %LOCALAPPDATA%
///   5. 相对路径图片能加载
///   6. 安全开关按预期生效
///
/// 结果写 JSON 到磁盘，脚本靠它判定成败，不靠人眼看窗口。
/// </summary>
public partial class SmokeWebViewWindow : Window
{
    private const int OverallTimeoutSeconds = 90;
    private const int NavigateTimeoutSeconds = 40;
    private const int MessageTimeoutSeconds = 15;

    private readonly AppPaths _paths;
    private readonly string _reportPath;
    private readonly List<SmokeCheck> _checks = new();

    private readonly TaskCompletionSource<bool> _navigated =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource<string> _jsToHost =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly HashSet<string> _localAppDataBefore =
        new(StringComparer.OrdinalIgnoreCase);

    public SmokeWebViewWindow(AppPaths paths, string reportPath)
    {
        _paths = paths;
        _reportPath = reportPath;
        InitializeComponent();
    }

    public async Task<SmokeReport> RunAsync()
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();

        SnapshotLocalAppData();

        try
        {
            var work = RunChecksAsync();
            var timeout = Task.Delay(TimeSpan.FromSeconds(OverallTimeoutSeconds));

            if (await Task.WhenAny(work, timeout).ConfigureAwait(true) == timeout)
            {
                Add("overall-timeout", false,
                    $"超过 {OverallTimeoutSeconds} 秒仍未跑完，已中止");
            }
            else
            {
                await work.ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Add("unhandled-exception", false,
                ex.GetType().Name + ": " + ex.Message);
        }

        var version = Web.CoreWebView2?.Environment?.BrowserVersionString;

        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            stopwatch.ElapsedMilliseconds,
            _paths.DataRoot,
            _paths.WebView2DataDir,
            version,
            _checks.ToList());

        WriteReport(report);
        return report;
    }

    // ---------- 检查主体 ----------

    private async Task RunChecksAsync()
    {
        WriteAssets();

        var provider = new WebViewEnvironmentProvider(_paths);
        var host = new ReaderWebHost(provider);

        await host.InitializeAsync(Web, _paths.ReaderCacheDir).ConfigureAwait(true);

        var core = Web.CoreWebView2
            ?? throw new InvalidOperationException("CoreWebView2 未就绪");

        core.NavigationCompleted += OnNavigationCompleted;
        core.WebMessageReceived += OnWebMessageReceived;

        Add("webview2-runtime", true, core.Environment.BrowserVersionString);

        Add("userdata-in-datadir",
            Directory.Exists(_paths.WebView2DataDir),
            _paths.WebView2DataDir);

        Add("security-settings",
            !core.Settings.AreDevToolsEnabled
                && !core.Settings.AreHostObjectsAllowed
                && core.Settings.IsWebMessageEnabled
                && core.Settings.IsScriptEnabled,
            "DevTools=" + core.Settings.AreDevToolsEnabled
                + " HostObjects=" + core.Settings.AreHostObjectsAllowed
                + " WebMessage=" + core.Settings.IsWebMessageEnabled);

        core.Navigate(ReaderWebHost.ToVirtualUrl("index.html"));

        var navigated = await WaitAsync(_navigated.Task, NavigateTimeoutSeconds)
            .ConfigureAwait(true);
        Add("virtual-host-navigation", navigated,
            navigated
                ? "已加载 " + ReaderWebHost.ToVirtualUrl("index.html")
                : "导航超时");

        if (!navigated)
        {
            return;
        }

        var title = await EvalAsync("document.title").ConfigureAwait(true);
        Add("page-script-ran", title == "EBookStudio Smoke",
            "document.title = " + (title ?? "(null)"));

        await EvalAsync("window.__host.say('from-csharp')").ConfigureAwait(true);
        var hostMsg = await EvalAsync(
            "document.getElementById('hostMsg').textContent").ConfigureAwait(true);
        Add("csharp-to-js", hostMsg == "from-csharp",
            "hostMsg = " + (hostMsg ?? "(null)"));

        var jsMsg = await WaitAsync(_jsToHost.Task, MessageTimeoutSeconds)
            .ConfigureAwait(true);
        Add("js-to-csharp", jsMsg is not null,
            jsMsg ?? "未在超时内收到 postMessage");

        var width = await EvalAsync(
            "document.getElementById('cover').naturalWidth").ConfigureAwait(true);
        Add("relative-image-load",
            !string.IsNullOrEmpty(width) && width != "0",
            "cover.naturalWidth = " + (width ?? "(null)"));

        Add("no-localappdata-write", NoNewLocalAppDataWebView2(),
            "%LOCALAPPDATA% 下未出现新的 WebView2 目录");
    }

    // ---------- WebView2 事件 ----------

    private void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
        => _navigated.TrySetResult(e.IsSuccess);

    private void OnWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        var text = e.TryGetWebMessageAsString();
        if (!string.IsNullOrEmpty(text))
        {
            _jsToHost.TrySetResult(text);
        }
    }

    // ---------- 辅助 ----------

    private void Add(string name, bool ok, string detail)
    {
        _checks.Add(new SmokeCheck(name, ok, detail));
        StatusText.Text = string.Join(
            Environment.NewLine,
            _checks.Select(c => (c.Ok ? "[OK]   " : "[FAIL] ") + c.Name + "  " + c.Detail));
    }

    private async Task<string?> EvalAsync(string script)
    {
        var raw = await Web.CoreWebView2!.ExecuteScriptAsync(script)
            .ConfigureAwait(true);

        if (string.IsNullOrEmpty(raw))
        {
            return raw;
        }

        // ExecuteScriptAsync 返回的是 JSON 编码值：字符串会带引号，数字不带。
        try
        {
            return JsonSerializer.Deserialize<string>(raw);
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private static async Task<T?> WaitAsync<T>(Task<T> task, int seconds)
    {
        var delay = Task.Delay(TimeSpan.FromSeconds(seconds));
        var winner = await Task.WhenAny(task, delay).ConfigureAwait(true);
        return winner == task ? await task.ConfigureAwait(true) : default;
    }

    // ---------- 测试资产 ----------

    private void WriteAssets()
    {
        Directory.CreateDirectory(_paths.ReaderCacheDir);

        File.WriteAllText(
            Path.Combine(_paths.ReaderCacheDir, "index.html"),
            IndexHtml,
            new UTF8Encoding(false));

        File.WriteAllText(
            Path.Combine(_paths.ReaderCacheDir, "app.js"),
            AppJs,
            new UTF8Encoding(false));

        // 1x1 红色 PNG：用来验证相对路径资源能被虚拟主机加载
        File.WriteAllBytes(
            Path.Combine(_paths.ReaderCacheDir, "cover.png"),
            Convert.FromBase64String(CoverPngBase64));
    }

    private const string IndexHtml = """
    <!DOCTYPE html>
    <html>
    <head>
      <meta charset="utf-8">
      <title>EBookStudio Smoke</title>
    </head>
    <body>
      <h1>WebView2 smoke</h1>
      <p>hostMsg: <span id="hostMsg">(none)</span></p>
      <p><img id="cover" src="cover.png" alt="cover"></p>
      <script src="app.js"></script>
    </body>
    </html>
    """;

    private const string AppJs = """
    window.__host = {
      say: function (text) {
        document.getElementById('hostMsg').textContent = text;
        return true;
      }
    };
    window.chrome.webview.postMessage(
      JSON.stringify({ type: 'ready', href: location.href }));
    """;

    private const string CoverPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    // ---------- 绿色性检查 ----------

    private void SnapshotLocalAppData()
    {
        var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Directory.Exists(lad))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(lad))
        {
            _localAppDataBefore.Add(Path.GetFileName(dir));
        }
    }

    private bool NoNewLocalAppDataWebView2()
    {
        var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Directory.Exists(lad))
        {
            return true;
        }

        foreach (var dir in Directory.GetDirectories(lad))
        {
            var name = Path.GetFileName(dir);
            if (_localAppDataBefore.Contains(name))
            {
                continue;
            }

            if (name.Contains("WebView2", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    // ---------- 报告 ----------

    private void WriteReport(SmokeReport report)
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