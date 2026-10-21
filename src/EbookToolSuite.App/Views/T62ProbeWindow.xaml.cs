using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using EbookToolSuite.App.Smoke;
using EbookToolSuite.Core;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Reader.WebView;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace EbookToolSuite.App.Views;

/// <summary>
/// T6-2 探针：把设计文档 §6.5.3 的九项一次跑完，结论写成 JSON + Markdown，
/// **每一步都存一张 PNG**。
///
/// 为什么要存图：内置 PDF 界面到底是"浏览器 UI 画的"还是"PDF 查看器扩展页 DOM 画的"，
/// 纯靠 API 判定会误判（扩展页的 DOM 脚本可能根本注入不进去）。所以策略是
/// 自动断言先给像素指标，截图留档供人眼复核——报告里两者都写。
///
/// 判定口径：
///   * 墨迹占比（亮度 &lt; 200 的像素比例）—— 白屏 ≈ 0
///   * 两帧差异 —— URL 参数（#page / #zoom / #view / #toolbar）到底有没有生效
///   * 对照帧 —— 同一模板指到一个不存在的 PDF，用来区分"渲染了"与"错误页"
/// </summary>
public partial class T62ProbeWindow : Window
{
    private const int NavTimeoutSeconds = 20;
    private const int SettleMs = 1600;
    private const int LargeFirstInkBudgetMs = 180000;

    /// <summary>超过这个墨迹占比就算"画出了东西"。1.28M 像素里 0.4% ≈ 5100 像素。</summary>
    private const double InkThreshold = 0.004;

    /// <summary>
    /// 大文件首屏的"进度条"门槛：0.2% 的墨迹只可能是那条蓝色加载条。
    /// 第一轮探针（2026-10-13）就是把它当成了首屏墨迹，属假阳性。
    /// </summary>
    private const double ProgressBarInkThreshold = 0.002;

    /// <summary>真画出页面内容：一页文字/扫描图至少占 2% 的像素。</summary>
    private const double ContentInkThreshold = 0.02;

    /// <summary>判据 §12.3.1：100 MB 扫描件点开到第一页可见 ≤5 s。</summary>
    private const int ContentBudgetMs = 5000;

    /// <summary>两帧差异超过 1% 就算"画面变了"。</summary>
    private const double DiffThreshold = 0.01;

    private static readonly string[] Questions =
    [
        "现在的启动参数下，PDF 到底能不能渲染",
        "内置工具栏还在不在",
        "滚动翻页时我们能否知道页码",
        "#page=N 跳页是否生效、是否重载",
        "#zoom= / #view=FitH 是否生效",
        "iframe 内嵌 PDF 能否渲染",
        "PDF 主机名映射是否支持 Range 请求",
        "DenyCors 会不会挡住 PDF",
        "浏览器加速键开没开",
    ];

    private readonly AppPaths _paths;
    private readonly string _pdfDir;
    private readonly string _reportPath;
    private readonly string _markdownPath;
    private readonly string _shotsDir;
    private readonly string _probeRoot;

    private readonly List<string> _notes = new();
    private readonly List<T62ProbeFinding> _findings = new();
    private readonly List<T62ProbeShot> _shots = new();
    private readonly List<T62ProbePdf> _files = new();
    private readonly List<T62ProbeNet> _network = new();
    private readonly Dictionary<string, PngStats> _statsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Url, int Status, string Mime, string? AcceptRanges, string? ContentRange)> _pending = new(StringComparer.Ordinal);

    private CoreWebView2DevToolsProtocolEventReceiver? _responseReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _finishedReceiver;

    private WebView2 _web;
    private CoreWebView2? _core;

    private TaskCompletionSource<bool>? _navTcs;
    private bool _navSuccess;
    private string? _navError;
    private bool _downloadTriggered;
    private int _sourceChanged;
    private int _frameCreated;

    private string _smallPdf = string.Empty;
    private string _midPdf = string.Empty;
    private string _largePdf = string.Empty;
    private string? _browserVersion;
    private string _accessKind = nameof(CoreWebView2HostResourceAccessKind.DenyCors);
    private bool _hidBuiltInToolbarItems;
    private int _harnessSeq;
    private int _rangeRequestCount;
    private readonly List<string> _rangeLog = new();

    public T62ProbeWindow(AppPaths paths, string pdfDirectory, string reportPath, string markdownPath)
    {
        _paths = paths;
        _pdfDir = pdfDirectory;
        _reportPath = reportPath;
        _markdownPath = markdownPath;
        _shotsDir = Path.Combine(paths.TempDir, "t62-probe-shots");
        _probeRoot = Path.Combine(paths.TempDir, "t62-probe-root");
        _web = null!;

        InitializeComponent();
        _web = Web;
    }

    private CoreWebView2 ActiveCore =>
        _core ?? throw new InvalidOperationException("CoreWebView2 未就绪");

    public async Task<T62ProbeReport> RunAsync()
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            PrepareCorpus();
            await InitPrimaryAsync().ConfigureAwait(true);

            await StepAsync("探针 1", Probe1RenderAsync).ConfigureAwait(true);
            await StepAsync("探针 2", Probe2BuiltInToolbarAsync).ConfigureAwait(true);
            await StepAsync("探针 6/8", Probe6And8IFrameAsync).ConfigureAwait(true);
            await StepAsync("探针 4", Probe4GotoPageAsync).ConfigureAwait(true);
            await StepAsync("探针 5", Probe5ZoomFitAsync).ConfigureAwait(true);
            await StepAsync("探针 3", Probe3PageObservationAsync).ConfigureAwait(true);
            await StepAsync("探针 7", Probe7LargeFirstPaintAsync).ConfigureAwait(true);
            await StepAsync("探针 9", Probe9AcceleratorKeysAsync).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _notes.Add("探针整体异常：" + ex.GetType().Name + ": " + ex.Message);
        }

        stopwatch.Stop();

        var report = BuildReport(startedAt, stopwatch.ElapsedMilliseconds);
        WriteReport(report);
        return report;
    }

    // ---------------------------------------------------------------- 准备

    private void PrepareCorpus()
    {
        Directory.CreateDirectory(_paths.TempDir);

        if (Directory.Exists(_shotsDir))
        {
            Directory.Delete(_shotsDir, true);
        }

        if (Directory.Exists(_probeRoot))
        {
            Directory.Delete(_probeRoot, true);
        }

        Directory.CreateDirectory(_shotsDir);
        Directory.CreateDirectory(_probeRoot);

        var ordered = Directory.GetFiles(_pdfDir, "*.pdf")
            .OrderBy(f => new FileInfo(f).Length)
            .ToList();

        if (ordered.Count == 0)
        {
            throw new InvalidOperationException("目录里没有 PDF：" + _pdfDir);
        }

        _smallPdf = ordered[0];
        _largePdf = ordered[^1];
        _midPdf = ordered[ordered.Count / 2];

        foreach (var path in ordered)
        {
            var info = new FileInfo(path);
            _files.Add(new T62ProbePdf(
                info.Name,
                info.Length,
                RoleOf(path),
                Sha256Prefix(path)));
        }

        SetStatus($"样本 {ordered.Count} 个：小={Path.GetFileName(_smallPdf)} 大={Path.GetFileName(_largePdf)}");
    }

    private string RoleOf(string path)
    {
        if (string.Equals(path, _largePdf, StringComparison.OrdinalIgnoreCase))
        {
            return "大（探针 7 用它测首屏）";
        }

        if (string.Equals(path, _smallPdf, StringComparison.OrdinalIgnoreCase))
        {
            return "小（探针 1/2/4/5/9 用它，重载快）";
        }

        if (string.Equals(path, _midPdf, StringComparison.OrdinalIgnoreCase))
        {
            return "中";
        }

        return "备用";
    }

    private static string Sha256Prefix(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private async Task InitPrimaryAsync()
    {
        var provider = new WebViewEnvironmentProvider(_paths);
        var host = new ReaderWebHost(provider);

        await host.InitializeAsync(Web, _probeRoot).ConfigureAwait(true);

        _core = Web.CoreWebView2 ?? throw new InvalidOperationException("CoreWebView2 未就绪");
        _browserVersion = _core.Environment.BrowserVersionString;

        HookCore(_core);
        MapPdfHost(_core, CoreWebView2HostResourceAccessKind.DenyCors);
        await HookNetworkAsync(_core).ConfigureAwait(true);

        await Task.Delay(400).ConfigureAwait(true);
        SetStatus("WebView2 " + _browserVersion + " 就绪");
    }

    private void HookCore(CoreWebView2 core)
    {
        core.NavigationCompleted += (_, e) =>
        {
            _navSuccess = e.IsSuccess;
            _navError = e.IsSuccess ? null : e.WebErrorStatus.ToString();
            _navTcs?.TrySetResult(true);
        };

        core.DownloadStarting += (_, e) =>
        {
            _downloadTriggered = true;
            e.Cancel = true;
            _navTcs?.TrySetResult(true);
        };

        core.SourceChanged += (_, _) => _sourceChanged++;
        core.FrameCreated += (_, _) => _frameCreated++;
    }

    private void MapPdfHost(CoreWebView2 core, CoreWebView2HostResourceAccessKind kind)
    {
        core.SetVirtualHostNameToFolderMapping(
            ReaderEndpoints.FileHostName, _pdfDir, kind);
        _accessKind = kind.ToString();
    }

    private async Task HookNetworkAsync(CoreWebView2 core)
    {
        _responseReceiver = core.GetDevToolsProtocolEventReceiver("Network.responseReceived");
        _responseReceiver.DevToolsProtocolEventReceived += OnResponseReceived;

        _finishedReceiver = core.GetDevToolsProtocolEventReceiver("Network.loadingFinished");
        _finishedReceiver.DevToolsProtocolEventReceived += OnLoadingFinished;

        await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}").ConfigureAwait(true);
    }

    private void OnResponseReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;
            var requestId = root.TryGetProperty("requestId", out var rid) ? rid.GetString() : null;
            if (requestId is null || !root.TryGetProperty("response", out var response))
            {
                return;
            }

            var url = response.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
            var status = response.TryGetProperty("status", out var s) ? s.GetInt32() : 0;
            var mime = response.TryGetProperty("mimeType", out var m) ? m.GetString() ?? string.Empty : string.Empty;

            string? acceptRanges = null;
            string? contentRange = null;

            if (response.TryGetProperty("headers", out var headers)
                && headers.ValueKind == JsonValueKind.Object)
            {
                foreach (var header in headers.EnumerateObject())
                {
                    if (header.Name.Equals("accept-ranges", StringComparison.OrdinalIgnoreCase))
                    {
                        acceptRanges = header.Value.GetString();
                    }
                    else if (header.Name.Equals("content-range", StringComparison.OrdinalIgnoreCase))
                    {
                        contentRange = header.Value.GetString();
                    }
                }
            }

            _pending[requestId] = (url, status, mime, acceptRanges, contentRange);
        }
        catch (JsonException ex)
        {
            _notes.Add("网络事件解析失败：" + ex.Message);
        }
    }

    private void OnLoadingFinished(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;
            var requestId = root.TryGetProperty("requestId", out var rid) ? rid.GetString() : null;

            if (requestId is null || !_pending.TryGetValue(requestId, out var pending))
            {
                return;
            }

            var bytes = root.TryGetProperty("encodedDataLength", out var len) ? (long)len.GetDouble() : 0;
            _pending.Remove(requestId);
            _network.Add(new T62ProbeNet(
                pending.Url, pending.Status, pending.Mime,
                pending.AcceptRanges, pending.ContentRange, bytes));
        }
        catch (JsonException ex)
        {
            _notes.Add("网络事件解析失败：" + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 探针 1

    private async Task Probe1RenderAsync()
    {
        var url = PdfUrl(Path.GetFileName(_smallPdf)) + "#toolbar=0&navpanes=0";

        await NavigateAsync(ActiveCore, url).ConfigureAwait(true);
        var stats = await ShotAsync("p1-01-顶层导航-现有参数").ConfigureAwait(true);

        var contentType = await EvalAsync("document.contentType").ConfigureAwait(true);
        var embed = await EvalAsync("document.querySelector('embed')?'有embed':'无embed'").ConfigureAwait(true);

        var evidence = $"墨迹占比 {Pct(stats.InkRatio)}（阈值 {Pct(InkThreshold)}）；" +
            $"document.contentType={contentType}；{embed}；导航成功={_navSuccess}；触发下载={_downloadTriggered}";

        if (stats.InkRatio > InkThreshold)
        {
            AddFinding("1", "能渲染（保留现有启动参数）",
                "不动 --disable-features=msWebOOUI,msPdfOOUI，继续走内置渲染", evidence);
            return;
        }

        _notes.Add("探针 1：现有参数下没画出来，起对照环境（不关 msPdfOOUI）看是不是这条参数造成的");

        var altUserData = Path.Combine(_paths.TempDir, "wv2-t62-alt");
        Directory.CreateDirectory(altUserData);

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = WebViewEnvironmentProvider.BuildBrowserArguments(disablePdfOoui: false),
        };

        var altEnv = await CoreWebView2Environment
            .CreateAsync(null, altUserData, options)
            .ConfigureAwait(true);

        Web.Visibility = Visibility.Collapsed;
        Web2.Visibility = Visibility.Visible;
        await Web2.EnsureCoreWebView2Async(altEnv).ConfigureAwait(true);

        _core = Web2.CoreWebView2 ?? throw new InvalidOperationException("对照环境 CoreWebView2 未就绪");
        _web = Web2;
        HookCore(_core);
        MapPdfHost(_core, CoreWebView2HostResourceAccessKind.DenyCors);
        await HookNetworkAsync(_core).ConfigureAwait(true);
        await Task.Delay(400).ConfigureAwait(true);

        await NavigateAsync(_core, url).ConfigureAwait(true);
        var altStats = await ShotAsync("p1-02-顶层导航-不关msPdfOOUI").ConfigureAwait(true);
        var altContentType = await EvalAsync("document.contentType").ConfigureAwait(true);

        var altEvidence = $"对照环境墨迹占比 {Pct(altStats.InkRatio)}；document.contentType={altContentType}";

        if (altStats.InkRatio > InkThreshold)
        {
            AddFinding("1", "现有参数下不能渲染；去掉 msPdfOOUI 后能渲染",
                "必须去掉 msPdfOOUI（改全局启动参数），并补一次 EPUB 阅读回归",
                evidence + "；" + altEvidence);
        }
        else
        {
            AddFinding("1", "两种启动参数都画不出来",
                "PDF 不能走 WebView2 内置渲染 → 直接进 T6-2B 自绘；本条要报给用户",
                evidence + "；" + altEvidence +
                "；截图见 p1-01 / p1-02（注意确认不是“捕获不到独立进程合成”造成的假白屏）");
        }
    }

    // ---------------------------------------------------------------- 探针 2

    private async Task Probe2BuiltInToolbarAsync()
    {
        var name = Path.GetFileName(_smallPdf);

        await NavigateAsync(ActiveCore, PdfUrl(name) + "#toolbar=1&navpanes=1").ConfigureAwait(true);
        var on = await ShotAsync("p2-01-显式要求显示内置工具栏").ConfigureAwait(true);

        // 再拿一本对照文档看内置界面会不会随文档变：小 PDF 里权限受限，
        // 它顶上那条"此文件具有受限权限"提示条到底是文档级的还是恒有的？
        if (!string.Equals(_midPdf, _smallPdf, StringComparison.OrdinalIgnoreCase))
        {
            await NavigateAsync(ActiveCore, PdfUrl(Path.GetFileName(_midPdf)) + "#toolbar=1&navpanes=1")
                .ConfigureAwait(true);
            await ShotAsync("p2-03-中PDF-toolbar1").ConfigureAwait(true);

            await NavigateAsync(ActiveCore, PdfUrl(Path.GetFileName(_midPdf)) + "#toolbar=0&navpanes=0")
                .ConfigureAwait(true);
            await ShotAsync("p2-04-中PDF-toolbar0").ConfigureAwait(true);
        }

        var off = _statsByName.TryGetValue("p1-01-顶层导航-现有参数", out var first)
            ? first
            : on;

        var diff = on.DiffRatio(off);
        var paramEffective = diff > DiffThreshold;

        // 第三道手段（退化路径）：逐项隐藏内置按钮。环境+profile 级，下次导航生效。
        var hide = CoreWebView2PdfToolbarItems.Save
            | CoreWebView2PdfToolbarItems.SaveAs
            | CoreWebView2PdfToolbarItems.Print
            | CoreWebView2PdfToolbarItems.FullScreen
            | CoreWebView2PdfToolbarItems.MoreSettings;

        ActiveCore.Settings.HiddenPdfToolbarItems = hide;
        _hidBuiltInToolbarItems = true;

        await NavigateAsync(ActiveCore, PdfUrl(name) + "#toolbar=1&navpanes=1").ConfigureAwait(true);
        var afterHide = await ShotAsync("p2-02-显式工具栏再叠加HiddenPdfToolbarItems").ConfigureAwait(true);
        var hideDiff = afterHide.DiffRatio(on);

        var evidence =
            $"#toolbar=0 与 #toolbar=1 两帧差异 {Pct(diff)}（阈值 {Pct(DiffThreshold)}）；" +
            $"顶部 8% 墨迹占比：toolbar=0 时 {Pct(off.TopBandInkRatio)}，toolbar=1 时 {Pct(on.TopBandInkRatio)}；" +
            $"叠加 HiddenPdfToolbarItems(Save/SaveAs/Print/FullScreen/MoreSettings) 后画面差异 {Pct(hideDiff)}";

        if (paramEffective)
        {
            AddFinding("2", "#toolbar 参数有效：内置工具栏可以靠 URL 关掉",
                "内置信内置界面走前两道手段（msPdfOOUI + #toolbar=0&navpanes=0）；HiddenPdfToolbarItems 作为兜底一并设上",
                evidence);
        }
        else
        {
            AddFinding("2", "#toolbar 参数没有任何视觉效果（两帧一致）",
                "内置界面本就没画出来（msPdfOOUI 已生效）；HiddenPdfToolbarItems 已按退化路径设上，" +
                "隐藏 Save/SaveAs/Print/FullScreen/MoreSettings，属" +
                "最差情况兜底（它只能隐藏按钮、藏不掉工具栏容器）",
                evidence);
        }
    }

    // ---------------------------------------------------------------- 探针 6 / 8

    private async Task Probe6And8IFrameAsync()
    {
        var name = Path.GetFileName(_smallPdf);
        var pdfUrl = PdfUrl(name) + "#toolbar=0&navpanes=0";
        var missingUrl = ReaderEndpoints.FileHostRootUrl + "__t62-missing__.pdf";

        var okHarness = WriteHarness("h-小PDF", pdfUrl);
        var badHarness = WriteHarness("h-对照-缺失PDF", missingUrl);

        await NavigateAsync(ActiveCore, okHarness).ConfigureAwait(true);
        var ok = await ShotAsync("p6-01-iframe内嵌小PDF-DenyCors").ConfigureAwait(true);

        await NavigateAsync(ActiveCore, badHarness).ConfigureAwait(true);
        var bad = await ShotAsync("p6-02-iframe内嵌缺失PDF-DenyCors对照").ConfigureAwait(true);

        var diff = ok.DiffRatio(bad);
        var rendered = ok.InkRatio > InkThreshold && diff > DiffThreshold;
        var evidence =
            $"DenyCors 下：PDF 帧墨迹 {Pct(ok.InkRatio)}、对照（缺失文件）帧墨迹 {Pct(bad.InkRatio)}、" +
            $"两帧差异 {Pct(diff)}；iframe FrameCreated 次数 {_frameCreated}；" +
            $"PDF 响应 {DescribePdfResponses()}";

        if (rendered)
        {
            AddFinding("6", "iframe 内嵌 PDF 能渲染",
                "按 §6.5.1 走“保留外壳 + 正文区 iframe”路线，不做顶层导航",
                evidence);

            AddFinding("8", "DenyCors 不挡 PDF（iframe 导航不受 CORS 限制）",
                "保持 ebook.local 与 ebook-file.local 都用 DenyCors，不放宽到 Allow",
                evidence);
            return;
        }

        _notes.Add("探针 6/8：DenyCors 下 iframe 没渲染出来，改 Allow 重试（只放宽 PDF 文件主机名）");
        MapPdfHost(ActiveCore, CoreWebView2HostResourceAccessKind.Allow);

        await NavigateAsync(ActiveCore, okHarness).ConfigureAwait(true);
        var okAllow = await ShotAsync("p6-03-iframe内嵌小PDF-Allow").ConfigureAwait(true);
        var diffAllow = okAllow.DiffRatio(bad);
        var renderedAllow = okAllow.InkRatio > InkThreshold && diffAllow > DiffThreshold;

        var allowEvidence = evidence + $"；Allow 下墨迹 {Pct(okAllow.InkRatio)}、与对照差异 {Pct(diffAllow)}";

        if (renderedAllow)
        {
            AddFinding("6", "iframe 内嵌 PDF 能渲染（需把 PDF 主机名放宽到 Allow）",
                "按“保留外壳 + iframe”路线；PDF 文件主机名用 Allow，ebook.local 保持 DenyCors",
                allowEvidence);

            AddFinding("8", "DenyCors 会挡住 PDF，必须放宽到 Allow",
                "只对 ebook-file.local 放宽为 Allow，不动 ebook.local（按设计文档“只在必须时放宽”）",
                allowEvidence);
        }
        else
        {
            AddFinding("6", "iframe 内嵌 PDF 渲染不出来（DenyCors 与 Allow 都试过）",
                "退路：顶层导航 + WPF 侧另画一条“外观照抄 reader.css”的顶栏（两套 UI）；" +
                "这会让结构变差，必须写进 T6-2 报告并告知用户",
                allowEvidence);

            AddFinding("8", "Allow 也挡不住失败，说明问题不在 CORS",
                "保持 DenyCors；真正原因见探针 6 与探针 1 的结论",
                allowEvidence);
        }
    }

    // ---------------------------------------------------------------- 探针 4

    private async Task Probe4GotoPageAsync()
    {
        var name = Path.GetFileName(_smallPdf);
        var baseUrl = PdfUrl(name);

        var harness = WriteHarness("h-跳页", baseUrl + "?r=1#toolbar=0&page=1");
        await NavigateAsync(ActiveCore, harness).ConfigureAwait(true);
        var page1 = await ShotAsync("p4-01-首次导航带page1").ConfigureAwait(true);

        // A：只改 fragment —— 给 iframe 赋一个"只差 hash"的相同 URI = 同文档导航
        await EvalAsync("(function(){document.getElementById('f').src=" +
            JsString(baseUrl + "?r=1#toolbar=0&page=8") + ";return 1;})()").ConfigureAwait(true);
        await Task.Delay(SettleMs + 900).ConfigureAwait(true);
        var frag = await ShotAsync("p4-02-只改fragment到page8").ConfigureAwait(true);

        // B：整 src 替换（多一个 ?r=2，强制重新解析整个 PDF）
        await EvalAsync("(function(){document.getElementById('f').src=" +
            JsString(baseUrl + "?r=2#toolbar=0&page=8") + ";return 1;})()").ConfigureAwait(true);
        await Task.Delay(SettleMs + 1500).ConfigureAwait(true);
        var reload = await ShotAsync("p4-03-整src替换到page8").ConfigureAwait(true);

        var fragDiff = frag.DiffRatio(page1);
        var reloadDiff = reload.DiffRatio(page1);

        var evidence =
            $"page=1 帧 vs 只改 fragment 到 page=8：差异 {Pct(fragDiff)}；" +
            $"page=1 帧 vs 整 src 替换到 page=8：差异 {Pct(reloadDiff)}；" +
            $"SourceChanged 次数 {_sourceChanged}（外壳 URL 不变属正常，PDF 在 iframe 里）";

        if (reloadDiff <= DiffThreshold)
        {
            AddFinding("4", "#page=N 没有产生可见变化（整 src 替换也没变）",
                "跳页不可用 → 页码框改为“显示当前页”用途；若探针 3 也失败，页码只能靠翻页键累加，" +
                "必须写进报告并评估是否降级",
                evidence + "；注意样本都是多页书，排除“本来就一页”的可能");
            return;
        }

        if (fragDiff > DiffThreshold)
        {
            AddFinding("4", "#page=N 生效；只改 fragment 也能跳（不必强制重载）",
                "跳页优先只改 fragment（外壳不闪、PDF 不重新解析）；失败再退整 src 替换",
                evidence);
        }
        else
        {
            AddFinding("4", "#page=N 生效，但**只改 fragment 不生效**，必须整 src 替换",
                "每次跳页都重载 iframe（已知副作用：100 MB 文件会明显卡一下）；" +
                "这条与设计文档 §6.5.1 的预期一致",
                evidence);
        }
    }

    // ---------------------------------------------------------------- 探针 5

    private async Task Probe5ZoomFitAsync()
    {
        var name = Path.GetFileName(_smallPdf);
        var baseUrl = PdfUrl(name);

        var cases = new (string Param, string Shot)[]
        {
            ("zoom=100", "p5-01-zoom100"),
            ("zoom=200", "p5-02-zoom200"),
            ("view=FitH", "p5-03-viewFitH"),
            ("view=FitV", "p5-04-viewFitV"),
            ("view=Fit", "p5-05-viewFit"),
        };

        var stats = new Dictionary<string, PngStats>(StringComparer.Ordinal);
        var seq = 0;

        foreach (var (param, shot) in cases)
        {
            seq++;
            var harness = WriteHarness("h-视图-" + seq, baseUrl + "?r=" + seq + "#toolbar=0&" + param);
            await NavigateAsync(ActiveCore, harness).ConfigureAwait(true);
            stats[param] = await ShotAsync(shot).ConfigureAwait(true);
        }

        var zoomDiff = stats["zoom=100"].DiffRatio(stats["zoom=200"]);
        var fitDiff = stats["view=FitH"].DiffRatio(stats["view=FitV"]);
        var fitAllDiff = stats["view=FitH"].DiffRatio(stats["view=Fit"]);

        var zoomEffective = zoomDiff > DiffThreshold;
        var fitEffective = fitDiff > DiffThreshold || fitAllDiff > DiffThreshold;

        var evidence =
            $"#zoom=100 vs #zoom=200 差异 {Pct(zoomDiff)}；" +
            $"#view=FitH vs #view=FitV 差异 {Pct(fitDiff)}；" +
            $"#view=FitH vs #view=Fit 差异 {Pct(fitAllDiff)}；" +
            $"（阈值 {Pct(DiffThreshold)}）";

        var zoomVerdict = zoomEffective
            ? "#zoom 生效（重载式）"
            : "#zoom 不生效";

        var fitVerdict = fitEffective
            ? "#view=FitH/FitV/Fit 生效（重载式）"
            : "#view 不生效";

        var branch = zoomEffective || fitEffective
            ? "缩放按钮阶段一可用：改 iframe src 带 #zoom / #view（重载式，不适合连续微调）"
            : "缩放按钮阶段一置灰（Tooltip 写“自绘 PDF 引擎（T6-2B）上线后可用”）";

        AddFinding("5", zoomVerdict + "；" + fitVerdict, branch, evidence);
    }

    // ---------------------------------------------------------------- 探针 3

    private async Task Probe3PageObservationAsync()
    {
        var name = Path.GetFileName(_smallPdf);

        // (a) 顶层导航：Source / location.hash 会不会跟着滚动变
        await NavigateAsync(ActiveCore, PdfUrl(name) + "#toolbar=0&page=2").ConfigureAwait(true);
        var topBefore = await ShotAsync("p3-00-顶层滚动前").ConfigureAwait(true);

        var hashBefore = await EvalAsync("location.hash").ConfigureAwait(true);
        var srcBefore = ActiveCore.Source;
        var changedBefore = _sourceChanged;

        await WheelAsync(10, ctrl: false).ConfigureAwait(true);
        await Task.Delay(1800).ConfigureAwait(true);

        var hashAfter = await EvalAsync("location.hash").ConfigureAwait(true);
        var srcAfter = ActiveCore.Source;
        var sourceChanges = _sourceChanged - changedBefore;

        var scrolledShot = await ShotAsync("p3-01-顶层滚10档之后").ConfigureAwait(true);
        var topScrollDiff = topBefore.DiffRatio(scrolledShot);

        var viewerReach = await EvalAsync(
            "(function(){try{return 'elements='+document.querySelectorAll('*').length+" +
            "',embed='+!!document.querySelector('embed')}catch(e){return 'err:'+e.name}})()")
            .ConfigureAwait(true);

        var hashChanged = !string.Equals(hashBefore, hashAfter, StringComparison.Ordinal);
        var srcChanged = !string.Equals(srcBefore, srcAfter, StringComparison.Ordinal);
        var autoPage = (hashChanged && (hashAfter ?? string.Empty).Contains("page=", StringComparison.Ordinal))
            || (srcChanged && (srcAfter ?? string.Empty).Contains("page=", StringComparison.Ordinal));

        // (b) iframe 里的情况
        var harness = WriteHarness("h-页码观测", PdfUrl(name) + "#toolbar=0&page=2");
        await NavigateAsync(ActiveCore, harness).ConfigureAwait(true);
        var beforeScroll = await ShotAsync("p3-02-iframe滚动前").ConfigureAwait(true);

        await WheelAsync(10, ctrl: false).ConfigureAwait(true);
        await Task.Delay(1800).ConfigureAwait(true);

        var afterScroll = await ShotAsync("p3-03-iframe滚动后").ConfigureAwait(true);
        var scrolledInFrame = beforeScroll.DiffRatio(afterScroll) > DiffThreshold;

        var frameReach = await EvalAsync(
            "(function(){try{var d=document.getElementById('f').contentDocument;" +
            "return d?'可达':'null'}catch(e){return '被拦:'+e.name}})()")
            .ConfigureAwait(true);

        var evidence =
            $"顶层：location.hash {hashBefore} -> {hashAfter}，Source changed={srcChanged}，" +
            $"SourceChanged={sourceChanges} 次；滚 10 档画面差异 {Pct(topScrollDiff)}（证明真的滚了，" +
            "不是没滚所以没变）；查看器脚本可达性 [" + viewerReach + "]；" +
            $"iframe：滚 10 档后画面差异 {Pct(beforeScroll.DiffRatio(afterScroll))}（证明内核滚动本身可用），" +
            $"从外壳脚本够 iframe 文档 [{frameReach}]";

        if (autoPage)
        {
            AddFinding("3", "能观测到当前页（URL/#page 会随滚动变化）",
                "自动页码：外壳订阅 Source/SourceChanged 上报 reader.pdfPageChanged；" +
                "total_pages 由 pdfium 给，百分比自动可达",
                evidence);
        }
        else
        {
            AddFinding("3", "观测不到当前页（与设计文档预期一致，查看器是黑盒）",
                "手动页码：用户在页码框跳页或按翻页键时由外壳记账并上报 reader.pdfPageChanged；" +
                "total_pages 仍由 pdfium 提供，所以“只有页码没百分比”的降级不发生",
                evidence + $"；顶层滚动后画面见 p3-01（墨迹 {Pct(scrolledShot.InkRatio)}）");
        }
    }

    // ---------------------------------------------------------------- 探针 7

    private async Task Probe7LargeFirstPaintAsync()
    {
        // 三段式：
        //   第一段：三个档位各测一次首屏（走默认虚拟主机映射，无 Range）——基线。
        //   第二段：给 PDF 主机装上"支持 Range"的响应器（Accept-Ranges + 206），
        //           再测大文件首屏——回答"Range 到底救不救得了 100 MB 扫描件"。
        //   为什么怀疑 Range 是关键：这本 114.8 MB **不是 linearized PDF**
        //   （无 /Linearized、无 /ObjStm、有 %%EOF），PDFium 必须先去文件尾部读 xref，
        //   没有 Range 就得先把整份文件传完才能开始解析。
        var sizes = new[] { _smallPdf, _midPdf, _largePdf }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var baseline = new List<string>();
        long baselineLargeContentMs = -1;

        for (var i = 0; i < sizes.Count; i++)
        {
            var mb = new FileInfo(sizes[i]).Length / 1024 / 1024.0;
            var measured = await MeasureFirstContentAsync(sizes[i], $"p7-base-{i + 1:D2}").ConfigureAwait(true);
            baseline.Add($"{mb:F1} MB -> 内容 {Ms(measured.ContentMs)}（进度条 {Ms(measured.ProgressMs)}）");

            if (string.Equals(sizes[i], _largePdf, StringComparison.OrdinalIgnoreCase))
            {
                baselineLargeContentMs = measured.ContentMs;
            }
        }

        // 第二段：装上 Range 响应器
        _rangeLog.Clear();
        _rangeRequestCount = 0;
        EnableRangeServing(ActiveCore);

        var largeResult = await MeasureFirstContentAsync(_largePdf, "p7-range").ConfigureAwait(true);

        DisableRangeServing(ActiveCore);

        // 第三段：分清楚 8.8 秒是"传输慢"还是"PDFium 解析慢"。
        // 做法：把大文件硬链接进探针根目录（与 ebook.local 同源，绕开 CORS），
        // 用 fetch 直接量这份文件走虚拟主机的纯传输耗时。
        // 这一条决定"Range / 本地 HTTP 服务"值不值得做——传输快就说明是解析慢，做了也没用。
        var linkName = "t62-big-link.pdf";
        var linkPath = Path.Combine(_probeRoot, linkName);
        long transportMs = -1;
        long transportBytes = 0;
        var transportError = string.Empty;

        try
        {
            if (File.Exists(linkPath))
            {
                File.Delete(linkPath);
            }

            if (!CreateHardLinkW(linkPath, _largePdf, IntPtr.Zero))
            {
                _notes.Add("硬链接失败（Win32 错误 " + Marshal.GetLastWin32Error() + "），改用复制");
                File.Copy(_largePdf, linkPath, overwrite: true);
            }

            var fetched = await MeasureFetchAsync(ReaderEndpoints.VirtualHostRootUrl + linkName, "p7-fetch")
                .ConfigureAwait(true);
            transportMs = fetched.Ms;
            transportBytes = fetched.Bytes;
            transportError = fetched.Error;
        }
        catch (Exception ex)
        {
            transportError = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            try
            {
                if (File.Exists(linkPath))
                {
                    File.Delete(linkPath);
                }
            }
            catch (IOException)
            {
                // 探针根目录反正会被重建，删不掉不算事
            }
        }

        var transportText = transportMs >= 0
            ? $"纯传输 {transportMs} ms（{transportBytes / 1024 / 1024.0:F1} MB，约 " +
              $"{transportBytes / 1024.0 / Math.Max(1, transportMs):F1} MB/s）"
            : $"纯传输没测出来（{transportError}）";

        var baselineText = string.Join("；", baseline);
        var rangeText = $"装 Range 后 {new FileInfo(_largePdf).Length / 1024 / 1024.0:F1} MB -> " +
            $"内容 {Ms(largeResult.ContentMs)}（进度条 {Ms(largeResult.ProgressMs)}）";

        var logText = _rangeLog.Count == 0
            ? "响应器一次都没被调用"
            : $"响应器被调用 {_rangeLog.Count} 次，其中带 Range 的 {_rangeRequestCount} 次；" +
              "前几次：" + string.Join(" | ", _rangeLog.Take(5));

        var improved = largeResult.ContentMs >= 0
            && largeResult.ContentMs <= ContentBudgetMs;

        var evidence =
            $"文件 {new FileInfo(_largePdf).Length / 1024 / 1024.0:F1} MB；" +
            $"基线（默认映射）：{baselineText}；{rangeText}；{transportText}；" +
            $"门槛：进度条 {Pct(ProgressBarInkThreshold)}、内容 {Pct(ContentInkThreshold)}；" +
            $"Range 请求日志：{logText}";

        if (improved)
        {
            AddFinding("7", $"默认映射不支持 Range（整文件传输，大文件首屏 {Ms(baselineLargeContentMs)}）；" +
                $"自己实现 Range（Accept-Ranges + 206）后大文件首屏 {Ms(largeResult.ContentMs)}，**达到 ≤{ContentBudgetMs / 1000} s 判据**",
                "在 ReaderWebHost 里给 ebook-file.local 注册 WebResourceRequested 响应器，" +
                "按 Range 头返回 206（只读需要的字节）；这是 100 MB 首屏达标的唯一手段",
                evidence);
        }
        else
        {
            AddFinding("7", $"默认映射不支持 Range（整文件传输，大文件首屏 {Ms(baselineLargeContentMs)}）；" +
                $"自己实现 Range 后首屏仍是 {Ms(largeResult.ContentMs)}，**没有改善**",
                "Range 救不了这本（PDFium 仍需整文件才能开始解析）→ 把“100 MB 扫描件首屏超过 5 s”" +
                "作为已知限制写进 T6-2 验收报告并告知用户，不要假装达标",
                evidence);
        }
    }

    /// <summary>量一次首屏：进度条（0.2%）与真内容（2%）分别是第几毫秒。</summary>
    private async Task<(long ProgressMs, long ContentMs)> MeasureFirstContentAsync(
        string pdfPath, string shotPrefix)
    {
        var harness = WriteHarness("h-首屏", PdfUrl(Path.GetFileName(pdfPath)) + "#toolbar=0");

        // 先清成白屏，否则会拿上一页的像素当"首屏墨迹"
        await NavigateAsync(ActiveCore, "about:blank", settleMs: 250).ConfigureAwait(true);

        var stopwatch = Stopwatch.StartNew();
        ActiveCore.Navigate(harness);

        long progressMs = -1;
        long contentMs = -1;
        var progressShotDone = false;

        while (stopwatch.ElapsedMilliseconds < LargeFirstInkBudgetMs)
        {
            await Task.Delay(300).ConfigureAwait(true);

            PngStats stats;
            try
            {
                stats = await CaptureStatsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _notes.Add("首屏轮询取图失败：" + ex.GetType().Name);
                break;
            }

            // 两级判定。第一轮探针只有一个 0.2% 的门槛，结果把那条蓝色加载进度条
            // 当成了"首屏墨迹"（2026-10-13，那张近 9.7 KB 的空白页就是证据）。
            if (progressMs < 0 && stats.InkRatio > ProgressBarInkThreshold)
            {
                progressMs = stopwatch.ElapsedMilliseconds;

                if (!progressShotDone)
                {
                    await ShotAsync(shotPrefix + "-只有进度条").ConfigureAwait(true);
                    progressShotDone = true;
                }
            }

            if (stats.InkRatio > ContentInkThreshold)
            {
                contentMs = stopwatch.ElapsedMilliseconds;
                break;
            }
        }

        await Task.Delay(300).ConfigureAwait(true);
        await ShotAsync(shotPrefix + "-首屏").ConfigureAwait(true);

        return (progressMs, contentMs);
    }

    private static string Ms(long value) => value < 0 ? "未出现" : value + " ms";

    /// <summary>
    /// 纯传输计时：页面里 fetch 整份文件，把耗时写到 window 上，外面轮询读回。
    /// 用 fetch 而不是导航，是为了把"传输"与"PDFium 解析/首帧绘制"分开。
    /// </summary>
    private async Task<(long Ms, long Bytes, string Error)> MeasureFetchAsync(string url, string shotPrefix)
    {
        var harness = WriteFetchHarness(url);
        await NavigateAsync(ActiveCore, harness, settleMs: 300).ConfigureAwait(true);

        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < LargeFirstInkBudgetMs)
        {
            await Task.Delay(300).ConfigureAwait(true);

            var msText = await EvalAsync("String(window.__probeFetchMs)").ConfigureAwait(true);

            if (string.IsNullOrEmpty(msText) || msText == "-1")
            {
                continue;
            }

            var bytesText = await EvalAsync("String(window.__probeFetchBytes)").ConfigureAwait(true);
            var errorText = await EvalAsync("String(window.__probeFetchErr)").ConfigureAwait(true);
            await ShotAsync(shotPrefix + "-传输完成后").ConfigureAwait(true);

            _ = double.TryParse(msText, out var ms);
            _ = long.TryParse(bytesText, out var bytes);

            return (msText == "-2" ? -1 : (long)ms, bytes, errorText ?? string.Empty);
        }

        return (-1, 0, "超时");
    }

    private string WriteFetchHarness(string url)
    {
        _harnessSeq++;
        var fileName = $"harness-{_harnessSeq:D2}.html";

        var html = "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">" +
            "<style>html,body{margin:0;background:#fff;font:14px sans-serif}</style></head><body>" +
            "<div id=\"s\">传输计时中…</div><script>" +
            "window.__probeFetchMs=-1;window.__probeFetchBytes=0;window.__probeFetchErr='';" +
            "var t0=performance.now();" +
            "fetch(" + JsString(url) + ").then(function(r){return r.arrayBuffer()})" +
            ".then(function(b){window.__probeFetchBytes=b.byteLength;" +
            "window.__probeFetchMs=performance.now()-t0;})" +
            ".catch(function(e){window.__probeFetchErr=String(e);window.__probeFetchMs=-2;});" +
            "</script></body></html>";

        File.WriteAllText(Path.Combine(_probeRoot, fileName), html, new UTF8Encoding(false));
        return ReaderEndpoints.VirtualHostRootUrl + fileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    // ---------------------------------------------------------------- Range 实验

    /// <summary>
    /// 给 PDF 文件主机装一个"支持 Range"的响应器，盖掉默认的文件夹映射。
    /// 这是探针里的实验代码：默认映射不给 Accept-Ranges，PDFium 只能整文件下载，
    /// 114.8 MB 那本要 9 秒才出页面。这里验证"补上 Range 能不能进 5 秒"。
    /// </summary>
    private void EnableRangeServing(CoreWebView2 core)
    {
        core.AddWebResourceRequestedFilter(
            ReaderEndpoints.FileHostRootUrl + "*",
            CoreWebView2WebResourceContext.All);

        core.WebResourceRequested += OnWebResourceRequested;
    }

    private void DisableRangeServing(CoreWebView2 core)
        => core.WebResourceRequested -= OnWebResourceRequested;

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = new Uri(e.Request.Uri);
            var fileName = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            var path = Path.Combine(_pdfDir, fileName);

            if (!File.Exists(path))
            {
                // 不接管：让默认映射去回 404（探针 6 的对照帧要用这个行为）
                return;
            }

            var total = new FileInfo(path).Length;
            var rangeHeader = e.Request.Headers.GetHeader("Range");
            var environment = ActiveCore.Environment;

            if (string.IsNullOrEmpty(rangeHeader))
            {
                _rangeLog.Add($"200 整份 ({total} B)");

                // 整份用 FileStream 直接喂，别读进内存：这是 114.8 MB
                var full = File.OpenRead(path);
                e.Response = environment.CreateWebResourceResponse(
                    full, 200, "OK",
                    "Content-Type: application/pdf\r\n" +
                    "Accept-Ranges: bytes\r\n" +
                    "Content-Length: " + total);
                return;
            }

            var (start, end) = ParseRange(rangeHeader, total);
            var length = end - start + 1;

            _rangeRequestCount++;
            _rangeLog.Add($"206 {rangeHeader} -> {start}-{end}/{total}");

            var bounded = new BoundedReadStream(path, start, length);
            e.Response = environment.CreateWebResourceResponse(
                bounded, 206, "Partial Content",
                "Content-Type: application/pdf\r\n" +
                "Accept-Ranges: bytes\r\n" +
                $"Content-Range: bytes {start}-{end}/{total}\r\n" +
                "Content-Length: " + length);
        }
        catch (Exception ex)
        {
            _notes.Add("Range 响应器异常：" + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>解析 "bytes=start-end"（end 可空、可为 "start-" 形式）。</summary>
    private static (long Start, long End) ParseRange(string header, long total)
    {
        var spec = header.Replace("bytes=", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        var parts = spec.Split('-', 2);

        var start = long.TryParse(parts[0], out var s) ? s : 0;
        var end = parts.Length > 1 && long.TryParse(parts[1], out var e2) ? e2 : total - 1;

        if (start < 0)
        {
            start = 0;
        }

        if (end > total - 1)
        {
            end = total - 1;
        }

        if (end < start)
        {
            end = start;
        }

        return (start, end);
    }

    /// <summary>只暴露文件里 [offset, offset+length) 这一段的只读流。</summary>
    private sealed class BoundedReadStream : Stream
    {
        private readonly FileStream _file;
        private readonly long _length;
        private long _position;

        public BoundedReadStream(string path, long offset, long length)
        {
            _file = File.OpenRead(path);
            _file.Seek(offset, SeekOrigin.Begin);
            _length = length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - _position;

            if (remaining <= 0)
            {
                return 0;
            }

            var read = _file.Read(buffer, offset, (int)Math.Min(count, remaining));
            _position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var remaining = _length - _position;

            if (remaining <= 0)
            {
                return ValueTask.FromResult(0);
            }

            // 同步读：Range 段都很小，为它做真异步不值得
            var read = _file.Read(buffer.Span[..(int)Math.Min(buffer.Length, remaining)]);
            _position += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _file.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // ---------------------------------------------------------------- 探针 9

    private async Task Probe9AcceleratorKeysAsync()
    {
        var core = ActiveCore;
        var settings = core.Settings;

        var name = Path.GetFileName(_smallPdf);
        var harness = WriteHarness("h-加速键", PdfUrl(name) + "#toolbar=0&page=1");
        await NavigateAsync(core, harness).ConfigureAwait(true);

        _web.Focus();
        await EvalAsync("window.focus();1").ConfigureAwait(true);

        // 关键一步：键盘事件要送到 PDF 插件，得先让插件拿到焦点。真实用户会先点一下
        // PDF 再按 PageDown/Ctrl+F，探针也必须照做——第一轮探针没点，结论就不可信。
        await ClickCenterAsync().ConfigureAwait(true);

        // 顺序很要紧：第一轮探针先按了 Ctrl+F 再按 PageDown，焦点落在查找框里，
        // PageDown 当然不动 —— 那是探针的缺陷，不是产品的结论。所以翻页放在最前面，
        // 每一步之间都用 Esc 把上一步留下的界面收掉。
        var beforeDown = await ShotAsync("p9-01-PageDown之前").ConfigureAwait(true);
        await KeyAsync(core, 0x22, key: "PageDown", code: "PageDown").ConfigureAwait(true);
        await Task.Delay(1300).ConfigureAwait(true);
        var afterDown = await ShotAsync("p9-02-PageDown之后").ConfigureAwait(true);
        var downDiff = beforeDown.DiffRatio(afterDown);

        await KeyAsync(core, 0x1B, key: "Escape", code: "Escape").ConfigureAwait(true);
        await Task.Delay(700).ConfigureAwait(true);

        // 真实 OS 级 PageDown：CDP 注入走渲染进程，真实按键走窗口消息，
        // 两条路都测，才能分清"没送到"和"插件不响应"。
        var beforeDownReal = await ShotAsync("p9-09-PageDown之前-真实按键").ConfigureAwait(true);
        Activate();
        _web.Focus();
        await Task.Delay(400).ConfigureAwait(true);
        SendRealKey(0x22);
        await Task.Delay(1300).ConfigureAwait(true);
        var afterDownReal = await ShotAsync("p9-10-PageDown之后-真实按键").ConfigureAwait(true);
        var downRealDiff = beforeDownReal.DiffRatio(afterDownReal);

        await KeyAsync(core, 0x1B, key: "Escape", code: "Escape").ConfigureAwait(true);
        await Task.Delay(700).ConfigureAwait(true);

        var beforeZoom = await ShotAsync("p9-03-Ctrl滚轮之前").ConfigureAwait(true);
        await WheelAsync(10, ctrl: true).ConfigureAwait(true);
        await Task.Delay(1300).ConfigureAwait(true);
        var afterZoom = await ShotAsync("p9-04-Ctrl滚轮之后").ConfigureAwait(true);
        var zoomDiff = beforeZoom.DiffRatio(afterZoom);

        await KeyAsync(core, 0x1B, key: "Escape", code: "Escape").ConfigureAwait(true);
        await Task.Delay(700).ConfigureAwait(true);

        // Ctrl+F 要分两次测：
        //   (a) CDP 注入的按键走渲染进程，**不经过浏览器加速键**，测不到属正常；
        //   (b) 真实 OS 级按键（keybd_event）才会被当成浏览器加速键处理。
        // 只有 (b) 才回答得了"搜索按钮转发 Ctrl+F 行不行"。
        var beforeFindCdp = await ShotAsync("p9-05-CtrlF之前-CDP注入").ConfigureAwait(true);
        await KeyAsync(core, 0x46, key: "f", code: "KeyF", ctrl: true).ConfigureAwait(true);
        await Task.Delay(1300).ConfigureAwait(true);
        var afterFindCdp = await ShotAsync("p9-06-CtrlF之后-CDP注入").ConfigureAwait(true);
        var findCdpDiff = beforeFindCdp.DiffRatio(afterFindCdp);

        await KeyAsync(core, 0x1B, key: "Escape", code: "Escape").ConfigureAwait(true);
        await Task.Delay(700).ConfigureAwait(true);

        var beforeFindReal = await ShotAsync("p9-07-CtrlF之前-真实按键").ConfigureAwait(true);
        Activate();
        _web.Focus();
        await Task.Delay(500).ConfigureAwait(true);
        SendRealCtrlF();
        await Task.Delay(1500).ConfigureAwait(true);
        var afterFindReal = await ShotAsync("p9-08-CtrlF之后-真实按键").ConfigureAwait(true);
        var findRealDiff = beforeFindReal.DiffRatio(afterFindReal);

        await KeyAsync(core, 0x1B, key: "Escape", code: "Escape").ConfigureAwait(true);

        var evidence =
            $"设置实测：AreBrowserAcceleratorKeysEnabled={settings.AreBrowserAcceleratorKeysEnabled}、" +
            $"IsZoomControlEnabled={settings.IsZoomControlEnabled}、" +
            $"IsPinchZoomEnabled={settings.IsPinchZoomEnabled}、" +
            $"AreDefaultContextMenusEnabled={settings.AreDefaultContextMenusEnabled}、" +
            $"HiddenPdfToolbarItems={settings.HiddenPdfToolbarItems}；" +
            $"画面变化：PageDown（CDP 注入，已先点一下给插件焦点）{Pct(downDiff)}、" +
            $"PageDown（真实按键）{Pct(downRealDiff)}、Ctrl+滚轮 {Pct(zoomDiff)}、" +
            $"Ctrl+F（CDP 注入）{Pct(findCdpDiff)}、Ctrl+F（真实按键）{Pct(findRealDiff)}" +
            $"（阈值 {Pct(DiffThreshold)}）";

        var missing = new List<string>();
        if (!settings.AreBrowserAcceleratorKeysEnabled)
        {
            missing.Add("AreBrowserAcceleratorKeysEnabled=false");
        }

        if (downDiff <= DiffThreshold && downRealDiff <= DiffThreshold)
        {
            missing.Add("PageDown 两种注入都无效");
        }

        if (zoomDiff <= DiffThreshold)
        {
            missing.Add("Ctrl+滚轮无效");
        }

        var findNote = findRealDiff > DiffThreshold
            ? "Ctrl+F 打开查找条（真实按键可见）"
            : findRealDiff > 0 || findCdpDiff > 0
                ? "Ctrl+F 有画面变化但很弱"
                : "Ctrl+F 两种注入方式都没观测到画面变化——可能是查找条不在 CapturePreviewAsync 的抠图范围内，" +
                  "也可能是没送到；不据此单独判死，验收时以人眼复看 p9-07/p9-08 为准";

        if (missing.Count == 0)
        {
            AddFinding("9", "加速键开着，翻页/缩放都有效；" + findNote,
                "保持 AreBrowserAcceleratorKeysEnabled / IsZoomControlEnabled = true（设计文档 §8.9 旧示例的 false 是错的）；" +
                "搜索按钮转发 Ctrl+F 的做法保留",
                evidence);
        }
        else
        {
            AddFinding("9", "有加速键失效：" + string.Join("、", missing) + "；" + findNote,
                "把对应按钮改成“自绘上线后才可用”并置灰——不留按了没反应的按钮（设计文档 §6.5.3 探针 9）",
                evidence);
        }
    }

    /// <summary>
    /// 真实 OS 级 Ctrl+F。CDP 的 Input.dispatchKeyEvent 只到渲染进程，
    /// 浏览器加速键（查找条）不在它的覆盖范围内，所以这里必须用 keybd_event。
    /// </summary>
    private static void SendRealCtrlF()
    {
        const byte vkControl = 0x11;
        const byte vkF = 0x46;
        const uint keyUp = 0x0002;

        keybd_event(vkControl, 0, 0, UIntPtr.Zero);
        keybd_event(vkF, 0, 0, UIntPtr.Zero);
        keybd_event(vkF, 0, keyUp, UIntPtr.Zero);
        keybd_event(vkControl, 0, keyUp, UIntPtr.Zero);
    }

    /// <summary>真实 OS 级单键（如 VK_NEXT = 0x22 = PageDown）。</summary>
    private static void SendRealKey(byte virtualKey)
    {
        const uint keyUp = 0x0002;

        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, keyUp, UIntPtr.Zero);
    }

    /// <summary>在控件正中点一下左键——把焦点交给 PDF 插件（真实用户就是这么做的）。</summary>
    private async Task ClickCenterAsync()
    {
        var x = (int)Math.Max(60, _web.ActualWidth / 2);
        var y = (int)Math.Max(60, _web.ActualHeight / 2);

        foreach (var type in new[] { "mousePressed", "mouseReleased" })
        {
            var payload = new Dictionary<string, object?>
            {
                ["type"] = type,
                ["x"] = x,
                ["y"] = y,
                ["button"] = "left",
                ["buttons"] = 1,
                ["clickCount"] = 1,
            };

            await CdpAsync(ActiveCore, "Input.dispatchMouseEvent", JsonSerializer.Serialize(payload))
                .ConfigureAwait(true);
        }

        await Task.Delay(600).ConfigureAwait(true);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    // ---------------------------------------------------------------- 通用动作

    private async Task StepAsync(string label, Func<Task> body)
    {
        SetStatus(label + " 开始");
        try
        {
            await body().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _notes.Add(label + " 抛异常：" + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private async Task<bool> NavigateAsync(
        CoreWebView2 core, string url, int settleMs = SettleMs, int waitSeconds = NavTimeoutSeconds)
    {
        _navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _navSuccess = false;
        _navError = null;
        _downloadTriggered = false;

        core.Navigate(url);
        await WaitAsync(_navTcs.Task, waitSeconds).ConfigureAwait(true);

        var ok = _navSuccess;
        await Task.Delay(settleMs).ConfigureAwait(true);
        return ok;
    }

    private async Task<byte[]> CaptureAsync()
    {
        using var stream = new MemoryStream();
        await ActiveCore
            .CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream)
            .ConfigureAwait(true);
        return stream.ToArray();
    }

    private async Task<PngStats> CaptureStatsAsync()
    {
        var png = await CaptureAsync().ConfigureAwait(true);
        return PngStats.Load(png);
    }

    private async Task<PngStats> ShotAsync(string name)
    {
        var png = await CaptureAsync().ConfigureAwait(true);
        var path = Path.Combine(_shotsDir, name + ".png");
        await File.WriteAllBytesAsync(path, png).ConfigureAwait(true);

        var stats = PngStats.Load(png);
        _statsByName[name] = stats;
        _shots.Add(new T62ProbeShot(
            name, path, stats.Width, stats.Height,
            Math.Round(stats.InkRatio, 5), Math.Round(stats.TopBandInkRatio, 5)));

        SetStatus($"截图 {name}：墨迹 {Pct(stats.InkRatio)}，顶部带 {Pct(stats.TopBandInkRatio)}");
        return stats;
    }

    private async Task<string?> EvalAsync(string script)
    {
        try
        {
            var raw = await ActiveCore.ExecuteScriptAsync(script).ConfigureAwait(true);

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
            return "求值失败:" + ex.GetType().Name;
        }
    }

    private async Task<string> CdpAsync(CoreWebView2 core, string method, string parameters)
        => await core.CallDevToolsProtocolMethodAsync(method, parameters).ConfigureAwait(true);

    private async Task KeyAsync(
        CoreWebView2 core, int virtualKey, string? key = null, string? code = null,
        bool ctrl = false, bool shift = false, bool alt = false)
    {
        var modifiers = (alt ? 1 : 0) | (ctrl ? 2 : 0) | (shift ? 8 : 0);

        var payload = new Dictionary<string, object?>
        {
            ["type"] = "rawKeyDown",
            ["windowsVirtualKeyCode"] = virtualKey,
            ["nativeVirtualKeyCode"] = virtualKey,
            ["modifiers"] = modifiers,
        };

        if (key is not null)
        {
            payload["key"] = key;
        }

        if (code is not null)
        {
            payload["code"] = code;
        }

        await CdpAsync(core, "Input.dispatchKeyEvent", JsonSerializer.Serialize(payload)).ConfigureAwait(true);

        payload["type"] = "keyUp";
        await CdpAsync(core, "Input.dispatchKeyEvent", JsonSerializer.Serialize(payload)).ConfigureAwait(true);
    }

    private async Task WheelAsync(int notches, bool ctrl)
    {
        var x = (int)Math.Max(60, _web.ActualWidth / 2);
        var y = (int)Math.Max(60, _web.ActualHeight / 2);

        var payload = new Dictionary<string, object?>
        {
            ["type"] = "mouseWheel",
            ["x"] = x,
            ["y"] = y,
            ["deltaX"] = 0,
            ["deltaY"] = notches * 120,
            ["modifiers"] = ctrl ? 2 : 0,
        };

        await CdpAsync(ActiveCore, "Input.dispatchMouseEvent", JsonSerializer.Serialize(payload))
            .ConfigureAwait(true);
    }

    private string PdfUrl(string fileName)
        => ReaderEndpoints.FileHostRootUrl + Uri.EscapeDataString(fileName);

    private string WriteHarness(string label, string iframeSrc)
    {
        _harnessSeq++;
        var fileName = $"harness-{_harnessSeq:D2}.html";

        // 骨架故意极简：正文区就是一个铺满的 iframe，底色纯白，
        // 这样"渲染了 / 没渲染 / 错误页"三态靠像素就能分开，
        // 也不会给顶部 8% 墨迹指标引入我们自己画的装饰条。
        var html = "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">" +
            "<style>html,body{margin:0;height:100%;background:#fff}" +
            "#f{border:0;display:block;width:100%;height:100%}</style></head><body>" +
            "<iframe id=\"f\" src=\"" + iframeSrc + "\"></iframe></body></html>";

        File.WriteAllText(Path.Combine(_probeRoot, fileName), html, new UTF8Encoding(false));
        return ReaderEndpoints.VirtualHostRootUrl + fileName;
    }

    private static string JsString(string value) => JsonSerializer.Serialize(value);

    private string DescribePdfResponses()
    {
        var pdf = _network
            .Where(n => n.Url.Contains(".pdf", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return pdf.Count == 0
            ? "没抓到 PDF 响应"
            : string.Join(" | ", pdf.Select(Describe));
    }

    private static string Describe(T62ProbeNet net)
        => $"{net.Status} {net.MimeType} {net.EncodedBytes}B " +
           $"accept-ranges={net.AcceptRanges ?? "-"} content-range={net.ContentRange ?? "-"} " +
           Path.GetFileName(net.Url);

    private void AddFinding(string id, string verdict, string branch, string evidence)
    {
        _findings.Add(new T62ProbeFinding(
            id,
            Questions[int.Parse(id) - 1],
            verdict,
            branch,
            evidence));

        SetStatus("探针 " + id + " 结论：" + verdict);
    }

    private static string Pct(double value) => (value * 100).ToString("F2") + "%";

    private void SetStatus(string text) => StatusText.Text = text;

    private static async Task<T?> WaitAsync<T>(Task<T> task, int seconds)
    {
        var delay = Task.Delay(TimeSpan.FromSeconds(seconds));
        var winner = await Task.WhenAny(task, delay).ConfigureAwait(true);
        return winner == task ? await task.ConfigureAwait(true) : default;
    }

    // ---------------------------------------------------------------- 报告

    private T62ProbeReport BuildReport(DateTimeOffset startedAt, long elapsedMs)
    {
        // 九项必须都有行：没跑出来的显式写成"未得出结论"，不掩盖失败。
        var byId = _findings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var all = new List<T62ProbeFinding>();

        for (var i = 1; i <= 9; i++)
        {
            var id = i.ToString();

            all.Add(byId.TryGetValue(id, out var finding)
                ? finding
                : new T62ProbeFinding(id, Questions[i - 1], "未得出结论（探针抛异常）", "见备注", "-"));
        }

        return new T62ProbeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            elapsedMs,
            _browserVersion ?? "未知",
            _pdfDir,
            ReaderEndpoints.VirtualHostName,
            ReaderEndpoints.FileHostName,
            _accessKind,
            _hidBuiltInToolbarItems,
            _files.ToList(),
            _shots.ToList(),
            _network.ToList(),
            all,
            _notes.ToList());
    }

    private void WriteReport(T62ProbeReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_reportPath)!);
        File.WriteAllText(
            _reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        Directory.CreateDirectory(Path.GetDirectoryName(_markdownPath)!);
        File.WriteAllText(_markdownPath, BuildMarkdown(report), new UTF8Encoding(false));
    }

    private string BuildMarkdown(T62ProbeReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# T6-2 探针报告（设计文档 §6.5.3 九项）");
        sb.AppendLine();
        sb.AppendLine($"> 生成时间 {report.StartedAt} → {report.FinishedAt}（用时 {report.ElapsedMs} ms）");
        sb.AppendLine($"> WebView2 运行时 {report.BrowserVersion}");
        sb.AppendLine($"> 外壳主机 `{report.ShellHost}`（映射探针根目录）；PDF 文件主机 `{report.FileHost}`（映射 `TestCorpus/`）");
        sb.AppendLine($"> PDF 主机名访问类型 {report.PdfAccessKind}；HiddenPdfToolbarItems 是否已设 {report.HidBuiltInToolbarItems}");
        sb.AppendLine();
        sb.AppendLine("**这张表决定 T6-2 里哪些按钮是真按钮、哪些先置灰，也决定进度是自动还是手动。**");
        sb.AppendLine();

        sb.AppendLine("## 1. 样本");
        sb.AppendLine();
        sb.AppendLine("| 文件 | 大小 | 档位 | SHA256 前 12 位 |");
        sb.AppendLine("|---|---:|---|---|");

        foreach (var file in report.Files)
        {
            sb.AppendLine($"| {file.Name} | {file.Bytes / 1024 / 1024.0:F1} MB | {file.Role} | `{file.Sha256Prefix}` |");
        }

        sb.AppendLine();
        sb.AppendLine("> 设计文档 §12.2 D 写的 `pdf-text.pdf` / `pdf-100mb.pdf` 两个文件名在本机不存在；" +
            "实际用的是用户放进 `TestCorpus/` 的三个真实 PDF，按大小分档对应（小=常规、大=100 MB 级扫描件）。");
        sb.AppendLine();

        sb.AppendLine("## 2. 九项结论");
        sb.AppendLine();
        sb.AppendLine("| # | 要验的事 | 判定 | 据此走的分支 |");
        sb.AppendLine("|---|---|---|---|");

        foreach (var finding in report.Findings)
        {
            sb.AppendLine($"| {finding.Id} | {finding.Question} | {finding.Verdict} | {finding.Branch} |");
        }

        sb.AppendLine();
        sb.AppendLine("### 证据");
        sb.AppendLine();

        foreach (var finding in report.Findings)
        {
            sb.AppendLine($"- **{finding.Id}**：{finding.Evidence}");
        }

        sb.AppendLine();
        sb.AppendLine("## 3. 截图（每帧都存了，供人眼复核）");
        sb.AppendLine();
        sb.AppendLine("| 截图 | 尺寸 | 墨迹占比 | 顶部 8% 墨迹 | 文件 |");
        sb.AppendLine("|---|---:|---:|---:|---|");

        foreach (var shot in report.Shots)
        {
            sb.AppendLine(
                $"| {shot.Name} | {shot.Width}×{shot.Height} | {shot.InkRatio * 100:F2}% | " +
                $"{shot.TopBandInkRatio * 100:F2}% | `{Path.GetFileName(shot.Path)}` |");
        }

        sb.AppendLine();
        sb.AppendLine("## 4. 网络证据（PDF 相关）");
        sb.AppendLine();
        sb.AppendLine("| 状态 | MIME | 字节 | accept-ranges | content-range | 文件 |");
        sb.AppendLine("|---:|---|---:|---|---|---|");

        var pdfNets = report.Network
            .Where(n => n.Url.Contains(".pdf", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (pdfNets.Count == 0)
        {
            sb.AppendLine("| - | - | - | - | - | 没抓到 PDF 响应（虚拟主机映射可能不走网络域） |");
        }
        else
        {
            foreach (var net in pdfNets)
            {
                sb.AppendLine(
                    $"| {net.Status} | {net.MimeType} | {net.EncodedBytes} | {net.AcceptRanges ?? "-"} | " +
                    $"{net.ContentRange ?? "-"} | {Path.GetFileName(net.Url)} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## 5. 备注（按发生顺序）");
        sb.AppendLine();

        foreach (var note in report.Notes)
        {
            sb.AppendLine("- " + note);
        }

        sb.AppendLine();
        sb.AppendLine("## 6. 自动断言的边界（必须写清楚）");
        sb.AppendLine();
        sb.AppendLine("- 像素指标只能回答“画面变没变”“有没有画出东西”，**不能**直接回答“那条灰色条是不是内置工具栏”。");
        sb.AppendLine("  所以每帧都落盘，报告结论须经人眼复核（见 `build/t62-probe-shots/`）。");
        sb.AppendLine("- `#toolbar=0` 与 `#toolbar=1` 两帧一致时，有两种可能：内置界面本来就没画出来（好），" +
            "或者 URL 参数被忽略而工具栏一直在（坏）。区分办法是人眼看 `p1-01`/`p2-01` 两张图。");
        sb.AppendLine("- Ctrl+F 若画面无变化，可能是查找条不在 `CapturePreviewAsync` 的抠图范围内，" +
            "不能据此判定“搜索不可用”。");
        sb.AppendLine();

        return sb.ToString();
    }
}
