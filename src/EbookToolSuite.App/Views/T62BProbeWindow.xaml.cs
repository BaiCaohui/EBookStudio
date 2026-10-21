using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using EbookToolSuite.App.Smoke;
using EbookToolSuite.Core;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Reader.WebView;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace EbookToolSuite.App.Views;

/// <summary>
/// T6-2B「移动端图片通道」探针：只回答三个技术问题，**不做业务功能**。
///
///   1. 内存虚拟主机通道：不调 SetVirtualHostNameToFolderMapping，只靠
///      AddWebResourceRequestedFilter + WebResourceRequested + GetDeferral，
///      从内存字节回一张 JPEG（http://ebook-pdf.local/p/0.jpg）。
///   2. postMessage data URL 通道：宿主把同一页 JPEG 转 base64 塞进
///      PostWebMessageAsJson，页面收到后设到 &lt;img&gt;.src。
///   3. 渲染吞吐：RenderPage 每页耗时/字节，以及同页重复渲染有没有缓存加速。
///
/// 为什么要有这个探针：移动端外壳（Android WebView / iOS WKWebView）上
/// `SetVirtualHostNameToFolderMapping` 与 `CreateWebResourceResponse` 不是同一套 API，
/// 选中哪条通道决定后面所有工作。选之前必须拿到真实数字，而不是"应该可以"。
/// </summary>
public partial class T62BProbeWindow : Window
{
    /// <summary>内存虚拟主机名。**刻意不**出现在 WebEnvironmentProvider 的 MAP 列表里，
    /// 也不调 SetVirtualHostNameToFolderMapping——这就是要验的场景。</summary>
    private const string MemoryHostName = "ebook-pdf.local";

    private const string MemoryHostRoot = "http://" + MemoryHostName + "/";

    /// <summary>问题 2 要验"大消息不丢"，所以 data URL 这一路用扫描件 + longEdge 2000，
    /// 目标 JPEG ≥ 800 KB。</summary>
    private const int DataUrlLongEdge = 2000;

    /// <summary>问题 3 的文本页渲染长边。</summary>
    private const int TextLongEdge = 1600;

    /// <summary>问题 3 的扫描件对照长边。</summary>
    private const int ScanSmallLongEdge = 1600;
    private const int ScanLargeLongEdge = 2400;

    private readonly AppPaths _paths;
    private readonly string _pdfPath;
    private readonly string _reportPath;
    private readonly IPdfDocumentService _pdf;

    private readonly List<string> _notes = new();
    private readonly List<string> _netFailures = new();
    private readonly List<string> _netResponses = new();
    private readonly List<long> _requestTickMs = new();
    private readonly Dictionary<int, byte[]> _probeBytes = new();

    private WebView2 _web;
    private readonly WebView2 _plainCore;
    private CoreWebView2? _core;
    private string? _browserVersion;
    private string _scanPdfPath = string.Empty;
    private int _requestCount;
    private int _deferralActive;
    private int _deferralMax;
    private string _plainVersion = string.Empty;
    private bool _plainWebMessageEnabled;
    private bool _plainReady;
    private int _plainReceived;
    private string _plainLastJson = string.Empty;
    private string _plainJsMsgs = string.Empty;
    private T62BDataUrl? _dataUrlOffScreen;
    private T62BDataUrl? _dataUrlOnScreen;
    private T62BDataUrl? _dataUrlSecondRun;

    private TaskCompletionSource<bool>? _navTcs;

    public T62BProbeWindow(AppPaths paths, string pdfPath, string reportPath, IPdfDocumentService pdf)
    {
        _paths = paths;
        _pdfPath = pdfPath;
        _reportPath = reportPath;
        _pdf = pdf;
        _web = null!;

        // 屏幕外显示：WebView2 需要窗口真 Show 过才会初始化，但探针不该抢焦点。
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;

        InitializeComponent();
        _web = Web;

        _plainCore = new WebView2 { Visibility = Visibility.Collapsed };
        Plain.Children.Add(_plainCore);
    }

    private CoreWebView2 ActiveCore => _core ?? throw new InvalidOperationException("CoreWebView2 未就绪");

    public async Task<T62BProbeReport> RunAsync()
    {
        var startedAt = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();

        try
        {
            await InitAsync().ConfigureAwait(true);

            ResetNav();

            await StepAsync("问题 1 内存虚拟主机", Probe1MemoryHostAsync).ConfigureAwait(true);

            // 屏幕外 / 屏幕上各测一轮问题 2：这是"WebView2 对不可见窗口不提交通道消息"
            // 这条假设的对照实验（同一进程、同一环境、只改窗口位置）。
            await StepAsync("问题 2 data URL（屏幕外窗口）", () => Probe2DataUrlAsync(onScreen: false))
                .ConfigureAwait(true);

            await StepAsync("问题 2 data URL（屏幕内窗口）", () => Probe2DataUrlAsync(onScreen: true))
                .ConfigureAwait(true);

            await StepAsync("问题 2 对照（纯洁环境 WebView2）", ProbePlainCoreAsync).ConfigureAwait(true);
            await StepAsync("问题 3 渲染吞吐", Probe3ThroughputAsync).ConfigureAwait(true);

            // 屏幕内那一轮的同参数对照：证明通道与窗口无关（如果两轮一样）。
            await StepAsync("问题 2 对照（同参数、屏幕外重测）", Probe2SameUrlControlAsync)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _notes.Add("探针整体异常：" + ex.GetType().Name + ": " + ex.Message);
        }

        clock.Stop();

        var report = BuildReport(startedAt, clock.ElapsedMilliseconds);
        WriteReport(report);
        return report;
    }

    private async Task StepAsync(string label, Func<Task> step)
    {
        SetStatus(label + " …");

        try
        {
            await step().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _notes.Add(label + " 抛异常：" + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 初始化

    private async Task InitAsync()
    {
        Directory.CreateDirectory(_paths.TempDir);

        var provider = new WebViewEnvironmentProvider(_paths);
        var host = new ReaderWebHost(provider);

        SetStatus("WebView2 初始化中…");

        await host.InitializeAsync(Web, _paths.TempDir).ConfigureAwait(true);

        _core = Web.CoreWebView2 ?? throw new InvalidOperationException("CoreWebView2 未就绪");
        _browserVersion = _core.Environment.BrowserVersionString;

        _core.NavigationCompleted += (_, e) => _navTcs?.TrySetResult(true);
        _core.ProcessFailed += (_, e) => _notes.Add("ProcessFailed: " + e.ProcessFailedKind);

        _core.AddWebResourceRequestedFilter(MemoryHostRoot + "*", CoreWebView2WebResourceContext.All);
        _core.WebResourceRequested += OnMemoryHostRequest;

        await HookNetworkAsync(_core).ConfigureAwait(true);

        // 问题 2 要验 postMessage 通道，而"监听器装没装上"本身是个变量：
        // 用 AddScriptToExecuteOnDocumentCreatedAsync 在**文档创建时**就装好，
        // 排除"宿主发得比监听器早"这条竞态。
        await _core.AddScriptToExecuteOnDocumentCreatedAsync(P2ListenerScript).ConfigureAwait(true);
        _core.WebMessageReceived += (_, e) =>
        {
            _pageReady = true;
            _pageReadyJson = e.WebMessageAsJson;
        };

        await Task.Delay(300).ConfigureAwait(true);

        SetStatus("WebView2 " + _browserVersion + " 就绪；内存主机 " + MemoryHostName);
    }

    /// <summary>
    /// 问题 2 的页面侧监听器，文档创建时装一次（AddScriptToExecuteOnDocumentCreatedAsync）。
    ///
    /// **实测结论（2026-10-16，Edge 155.0.4283.45）**：宿主 PostWebMessageAsJson 的消息
    /// 只从 <c>window.chrome.webview.addEventListener('message')</c> 出来；
    /// <c>window.addEventListener('message')</c> 收不到（对照实验里 6 条消息全部走 chrome 路径，
    /// window 路径 0 条）。两条路都挂上，并按路径分别计数，免得下次再踩。
    ///
    /// 同时用 window.chrome.webview.postMessage 给宿主回一个 ready 信号——它顺带证明
    /// 页面→宿主这个方向是通的。
    /// </summary>
    private const string P2ListenerScript =
        "window.__p2={ready:false,ping:false,pingChars:0,pingType:'',got:false,chars:0,s0:0,loadMs:-1," +
        "w:0,h:0,err:'',complete:false,syn:false,viaWin:0,viaChrome:0,route:''};" +
        "window.__p2handler=function(ev,route){var d=ev.data;var t=window.__p2;" +
        "if(route==='chrome'){t.viaChrome++;}else{t.viaWin++;}" +
        "t.route=route;" +
        "if(d&&typeof d==='object'&&d.type==='probe.ping'){t.ping=true;t.pingType=String(d.type);" +
        "t.pingChars=String(d.data||'').length;return;}" +
        "var s=(typeof d==='string')?d:(d&&typeof d.data==='string'?d.data:'');" +
        "if(!s){t.err='空数据';return;}" +
        "t.got=true;t.chars=s.length;t.s0=performance.now();" +
        "var img=new Image();" +
        "img.onload=function(){t.loadMs=performance.now()-t.s0;t.w=img.naturalWidth;t.h=img.naturalHeight;" +
        "t.complete=img.complete;};" +
        "img.onerror=function(){t.loadMs=performance.now()-t.s0;t.err='onerror';};" +
        "window.__p2img=img;img.src=s;};" +
        "try{window.chrome.webview.addEventListener('message',function(ev){window.__p2handler(ev,'chrome');});}" +
        "catch(e){}" +
        "window.addEventListener('message',function(ev){window.__p2handler(ev,'window');});" +
        "try{window.chrome.webview.postMessage({type:'probe.ready'});window.__p2.ready=true;}catch(e){window.__p2.ready=false;}";

    /// <summary>
    /// 接 CDP 的 Network 域，专门记 ebook-pdf.local 的网络失败原文。
    /// 为什么必须记：img.onerror 事件里没有错误码，只写 "onerror" 说明不了
    /// 到底是 DNS 解析失败（net::ERR_NAME_NOT_RESOLVED）、被拦（blockedReason），
    /// 还是我们自己的响应器没接住。
    /// </summary>
    private async Task HookNetworkAsync(CoreWebView2 core)
    {
        var enabled = await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}").ConfigureAwait(true);

        if (string.IsNullOrEmpty(enabled))
        {
            lock (_notes)
            {
                _notes.Add("Network.enable 返回空");
            }
        }

        var failed = core.GetDevToolsProtocolEventReceiver("Network.loadingFailed");
        failed.DevToolsProtocolEventReceived += (_, e) =>
        {
            try
            {
                var node = JsonNode.Parse(e.ParameterObjectAsJson);

                lock (_notes)
                {
                    _netFailures.Add(
                        (node?["requestId"]?.GetValue<string>() ?? "?") + " " +
                        (node?["errorText"]?.GetValue<string>() ?? "?") + " blocked=" +
                        (node?["blockedReason"]?.GetValue<string>() ?? "-"));
                }
            }
            catch (Exception ex)
            {
                lock (_notes)
                {
                    _notes.Add("Network.loadingFailed 解析失败：" + ex.Message);
                }
            }
        };

        var response = core.GetDevToolsProtocolEventReceiver("Network.responseReceived");
        response.DevToolsProtocolEventReceived += (_, e) =>
        {
            try
            {
                var node = JsonNode.Parse(e.ParameterObjectAsJson);
                var url = node?["response"]?["url"]?.GetValue<string>() ?? string.Empty;

                if (!url.Contains(MemoryHostName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                lock (_notes)
                {
                    _netResponses.Add(
                        url + " -> " + (node?["response"]?["status"]?.GetValue<int>() ?? -1));
                }
            }
            catch (Exception)
            {
                // 记网络证据失败不该让探针炸
            }
        };
    }

    // ---------------------------------------------------------------- 问题 1：内存虚拟主机

    /// <summary>
    /// 内存虚拟主机响应器。实测到了两条硬约束（2026-10-16）：
    ///   * <c>CoreWebView2.Environment</c> / <c>CreateWebResourceResponse</c> 只能从
    ///     UI 线程调，后台线程调会抛 "CoreWebView2 members can only be accessed from
    ///     the UI thread."，请求随即 net::ERR_ABORTED。
    ///   * 渲染本身很便宜（文本页 ~50 ms / 扫描件 1600 长边 77 ms、2400 长边 128 ms），
    ///     所以"后台渲染"这一步实际上不是瓶颈——可以同步做在 UI 线程上。
    /// 结论：GetDeferral 的价值不在"腾出渲染时间"，而在"把渲染放在响应之前"。
    /// </summary>
    private void OnMemoryHostRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        Interlocked.Increment(ref _requestCount);

        lock (_requestTickMs)
        {
            _requestTickMs.Add(Stopwatch.GetTimestamp() / TimeSpan.TicksPerMillisecond);
        }

        // 异步那条路也会完成它，这里立刻返回。
        var deferral = e.GetDeferral();
        var uri = e.Request.Uri;

        lock (this)
        {
            _deferralActive++;

            if (_deferralActive > _deferralMax)
            {
                _deferralMax = _deferralActive;
            }
        }

        // 渲染放线程池，响应创建与 e.Response 赋值仍在 UI 线程（Event 回调本就在 UI 线程上）。
        _ = Task.Run(() =>
        {
            byte[]? bytes = null;
            string? error = null;

            try
            {
                bytes = _pdf.RenderPage(_pdfPath, 0, TextLongEdge);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
            }

            var core = _core;
            var renderError = error;
            var rendered = bytes;

            void Respond()
            {
                try
                {
                    if (rendered is null)
                    {
                        e.Response = null;
                        return;
                    }

                    if (core is null)
                    {
                        throw new InvalidOperationException("CoreWebView2 未就绪");
                    }

                    lock (_probeBytes)
                    {
                        _probeBytes[0] = rendered;
                    }

                    // 关键：响应必须在 UI 线程上创建。
                    e.Response = core.Environment.CreateWebResourceResponse(
                        new MemoryReadStream(rendered), 200, "OK",
                        "Content-Type: image/jpeg\r\nContent-Length: " + rendered.Length);
                }
                catch (Exception ex)
                {
                    renderError ??= ex.GetType().Name + ": " + ex.Message;
                }
                finally
                {
                    lock (this)
                    {
                        _deferralActive--;
                    }

                    deferral.Complete();
                }
            }

            var dispatcher = Application.Current?.Dispatcher;

            if (dispatcher is null)
            {
                return;
            }

            dispatcher.Invoke(Respond);

            if (renderError is not null)
            {
                lock (_notes)
                {
                    _notes.Add("内存主机响应器失败（" + uri + "）：" + renderError);
                }
            }
        });
    }

    private async Task Probe1MemoryHostAsync()
    {
        await NavigateAsync("about:blank").ConfigureAwait(true);

        // 先渲染一份，拿到"宿主侧字节数"作为对照（同时验证 RenderPage 本身没问题）。
        var sw = Stopwatch.StartNew();
        var hostBytes = await Task.Run(() => _pdf.RenderPage(_pdfPath, 0, TextLongEdge)).ConfigureAwait(true);
        sw.Stop();

        await ExecuteAsync(
            "window.__m1={requests:0,src:'',s0:0,loadMs:-1,err:'',w:0,h:0,complete:false};(function(){" +
            "var t=window.__m1;t.requests=" + _requestCount + ";" +
            "var img=new Image();" +
            "img.onload=function(){t.loadMs=performance.now()-t.s0;t.w=img.naturalWidth;t.h=img.naturalHeight;" +
            "t.complete=img.complete;};" +
            "img.onerror=function(ev){t.loadMs=performance.now()-t.s0;t.err='onerror';};" +
            "window.__m1img=img;t.s0=performance.now();" +
            "img.src=" + JsString(MemoryHostRoot + "p/0.jpg?t=" + DateTime.UtcNow.Ticks) + ";" +
            "})();1").ConfigureAwait(true);

        await Task.Delay(6000).ConfigureAwait(true);

        var state = await EvalAsync(
            "(function(){var t=window.__m1;var i=window.__m1img||{};" +
            "return JSON.stringify({requests:t.requests,src:i.src||'',s0:t.s0,loadMs:t.loadMs," +
            "err:t.err,w:t.w,h:t.h,complete:!!t.complete,curSrc:i.currentSrc||''," +
            "evSrc:i.src||''});})()").ConfigureAwait(true);

        var requests = (int)(state?["requests"]?.GetValue<double>() ?? 0);
        var loadMs = (long)(state?["loadMs"]?.GetValue<double>() ?? -1);
        var err = state?["err"]?.GetValue<string>() ?? string.Empty;
        var w = (int)(state?["w"]?.GetValue<double>() ?? 0);
        var h = (int)(state?["h"]?.GetValue<double>() ?? 0);
        var complete = state?["complete"]?.GetValue<bool>() ?? false;
        var src = state?["src"]?.GetValue<string>() ?? string.Empty;

        _memory = new T62BMemory(
            _requestCount,
            Interlocked.CompareExchange(ref _deferralMax, 0, 0),
            src,
            w,
            h,
            complete,
            loadMs,
            hostBytes.Length,
            err,
            "宿主侧同参数 RenderPage " + sw.ElapsedMilliseconds + " ms");

        // 再验一次"并发"：6 张图同时请求，看 GetDeferral 能不能同时挂多个、
        // 以及 6 张全部 onload 要多久（翻页预取就是这个模式）。
        _concurrent = await Probe1ConcurrentAsync().ConfigureAwait(true);
    }

    /// <summary>6 张图同时 src，等全部 onload/onerror，记录耗时与事件次数。</summary>
    private async Task<T62BConcurrent> Probe1ConcurrentAsync()
    {
        var callsBefore = _requestCount;
        var sw = Stopwatch.StartNew();

        await ExecuteAsync(
            "window.__m1c={done:0,err:0,t:-1,s0:performance.now(),dims:[]};" +
            "(function(){var t=window.__m1c;var base=" + JsString(MemoryHostRoot + "p/1.jpg?t=") + ";" +
            "for(var i=0;i<6;i++){(function(i){var img=new Image();" +
            "img.onload=function(){t.done++;t.dims.push(img.naturalWidth+'x'+img.naturalHeight);" +
            "if(t.done+t.err>=6){t.t=performance.now()-t.s0;}window.__m1cimgs=window.__m1cimgs||[];" +
            "window.__m1cimgs.push(img);};" +
            "img.onerror=function(){t.err++;if(t.done+t.err>=6){t.t=performance.now()-t.s0;}};" +
            "img.src=base+i;})(i);}})();1").ConfigureAwait(true);

        var deadline = Stopwatch.StartNew();

        while (deadline.ElapsedMilliseconds < 15000)
        {
            var s = await EvalAsync(
                "JSON.stringify({done:window.__m1c.done,err:window.__m1c.err,t:window.__m1c.t})")
                .ConfigureAwait(true);

            var done = (int)(s?["done"]?.GetValue<double>() ?? 0);
            var err = (int)(s?["err"]?.GetValue<double>() ?? 0);

            if (done + err >= 6)
            {
                sw.Stop();

                return new T62BConcurrent(
                    6,
                    _requestCount - callsBefore,
                    Interlocked.CompareExchange(ref _deferralMax, 0, 0),
                    done,
                    err,
                    (long)(s?["t"]?.GetValue<double>() ?? -1),
                    s?["dims"] is null ? string.Empty : "见页面 dims");
            }

            await Task.Delay(50).ConfigureAwait(true);
        }

        sw.Stop();

        return new T62BConcurrent(6, _requestCount - callsBefore, _deferralMax, -1, -1, -1, "15 秒没全部完成");
    }

    // ---------------------------------------------------------------- 问题 2：data URL

    private async Task Probe2DataUrlAsync(bool onScreen)
    {
        EnsureCorpus();

        // 假设 A 的对照：把窗口搬到屏幕内（可见），看通道消息能不能到。
        if (onScreen)
        {
            Left = 40;
            Top = 40;
        }
        else
        {
            Left = -32000;
            Top = -32000;
        }

        SetStatus("问题 2：窗口位置 Left=" + Left + "…");

        // 用**真主机页**而不是 about:blank：about:blank 的 postMessage 语义与真实
        // reader.html（http://ebook.local/...）不同，必须按生产同款页面测。
        _pageReady = false;

        await NavigateAsync(EnsurePageAsync()).ConfigureAwait(true);

        // 关键：等页面自己报 ready（它用 window.chrome.webview.postMessage 回信），
        // 而不是信 NavigationCompleted——早发会被丢在文档还没建立的时候。
        var readyDeadline = Stopwatch.StartNew();

        while (!_pageReady && readyDeadline.ElapsedMilliseconds < 8000)
        {
            await Task.Delay(100).ConfigureAwait(true);
        }

        var pageState = await EvalAsync(
            "JSON.stringify({p2:!!window.__p2,ready:!!(window.__p2&&window.__p2.ready)})")
            .ConfigureAwait(true);

        _bridgeProbe = (pageState?.ToJsonString() ?? "(读不到)") + " | hostReady=" + _pageReady
            + " | readyJson=" + _pageReadyJson;

        // 扫描件 2000 长边
        var sw = Stopwatch.StartNew();
        var jpeg = await Task.Run(() => _pdf.RenderPage(_scanPdfPath, 0, DataUrlLongEdge)).ConfigureAwait(true);
        sw.Stop();

        _dataUrlRenderMs = sw.ElapsedMilliseconds;

        var payload = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);

        // 监听器已由 AddScriptToExecuteOnDocumentCreatedAsync 在文档创建时装好。
        // 这里只兜底：万一那段脚本没生效，补装一次（不重置已经收到的状态）。
        await ExecuteAsync(
            "if(!window.__p2){" + P2ListenerScript + "}1").ConfigureAwait(true);

        // --- 2a. 先发一个小消息，验通道本身通不通（排除"大消息被丢"这个假设） ---
        var ping = JsonSerializer.Serialize(new { type = "probe.ping", data = "ping" });

        try
        {
            ActiveCore.PostWebMessageAsJson(ping);
        }
        catch (Exception ex)
        {
            _notes.Add("PostWebMessageAsJson(ping) 抛异常：" + ex.GetType().Name + ": " + ex.Message);
        }

        await Task.Delay(1500).ConfigureAwait(true);

        var pingState = await EvalAsync(
            "JSON.stringify({ping:window.__p2.ping,pingChars:window.__p2.pingChars," +
            "pingType:window.__p2.pingType,err:window.__p2.err,route:window.__p2.route," +
            "msgs:(window.__p2.viaWin||0)+(window.__p2.viaChrome||0)," +
            "viaWin:window.__p2.viaWin||0,viaChrome:window.__p2.viaChrome||0})").ConfigureAwait(true);

        _pingReceived = pingState?["ping"]?.GetValue<bool>() ?? false;
        _pingChars = (int)(pingState?["pingChars"]?.GetValue<double>() ?? 0);
        _pingMsgs = (int)(pingState?["msgs"]?.GetValue<double>() ?? 0);
        _pingViaWin = (int)(pingState?["viaWin"]?.GetValue<double>() ?? 0);
        _pingViaChrome = (int)(pingState?["viaChrome"]?.GetValue<double>() ?? 0);
        _pingRoute = pingState?["route"]?.GetValue<string>() ?? string.Empty;

        // --- 2a-2. 换个投递方式再试一次（纯粹为了区分"内容/编码问题"与"投递问题"） ---
        _stringPostOk = true;

        try
        {
            ActiveCore.PostWebMessageAsString("probe-string-ping");
        }
        catch (Exception ex)
        {
            _stringPostOk = false;
            _notes.Add("PostWebMessageAsString 抛异常：" + ex.GetType().Name + ": " + ex.Message);
        }

        await Task.Delay(1200).ConfigureAwait(true);

        var stringState = await EvalAsync(
            "JSON.stringify({msgs:(window.__p2.viaWin||0)+(window.__p2.viaChrome||0)," +
            "viaWin:window.__p2.viaWin||0,viaChrome:window.__p2.viaChrome||0," +
            "route:window.__p2.route,ping:window.__p2.ping,got:window.__p2.got})").ConfigureAwait(true);

        _pingMsgsAfterString = (int)(stringState?["msgs"]?.GetValue<double>() ?? 0);
        _pingViaWinAfterString = (int)(stringState?["viaWin"]?.GetValue<double>() ?? 0);
        _pingViaChromeAfterString = (int)(stringState?["viaChrome"]?.GetValue<double>() ?? 0);

        // --- 2b. 再发真正的大消息（data URL，扫描件 2000 长边） ---
        var message = JsonSerializer.Serialize(new { type = "probe.dataUrl", data = payload });
        var postError = string.Empty;

        try
        {
            ActiveCore.PostWebMessageAsJson(message);
        }
        catch (Exception ex)
        {
            postError = ex.GetType().Name + ": " + ex.Message;
        }

        await Task.Delay(9000).ConfigureAwait(true);

        var state = await EvalAsync(
            "(function(){var t=window.__p2;var i=window.__p2img||{};" +
            "return JSON.stringify({got:t.got,chars:t.chars,loadMs:t.loadMs,err:t.err," +
            "w:t.w,h:t.h,complete:!!t.complete,curSrc:(i.currentSrc||'').slice(0,40)," +
            "natSrc:!!i.naturalWidth});})()").ConfigureAwait(true);

        var got = state?["got"]?.GetValue<bool>() ?? false;

        // --- 2c. 通道不通时的对照：用同一个 handler 合成一次 MessageEvent。
        //         它能区分"通道没把消息送到页面"和"送到了但 data URL 没渲染"。 ---
        var synthetic = false;
        var syntheticMs = -1L;

        if (!got)
        {
            var synSw = Stopwatch.StartNew();

            try
            {
                await ActiveCore.ExecuteScriptAsync(
                    "window.dispatchEvent(new MessageEvent('message',{data:" +
                    JsString(payload) + "}));1").ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _notes.Add("合成 MessageEvent 失败：" + ex.GetType().Name + ": " + ex.Message);
            }

            await Task.Delay(9000).ConfigureAwait(true);

            var synState = await EvalAsync(
                "(function(){var t=window.__p2;return JSON.stringify({got:t.got,chars:t.chars," +
                "w:t.w,h:t.h,err:t.err,complete:!!t.complete});})()").ConfigureAwait(true);

            synthetic = synState?["got"]?.GetValue<bool>() ?? false;
            syntheticMs = synSw.ElapsedMilliseconds;

            if (synthetic)
            {
                got = true;
                state = synState;
            }
        }

        var result = new T62BDataUrl(
            got,
            message.Length,
            (int)(state?["chars"]?.GetValue<double>() ?? 0),
            (int)(state?["w"]?.GetValue<double>() ?? 0),
            (int)(state?["h"]?.GetValue<double>() ?? 0),
            (long)(state?["loadMs"]?.GetValue<double>() ?? -1),
            jpeg.Length,
            _pingReceived,
            _pingChars,
            _bridgeProbe,
            _pingMsgs,
            _pingMsgsAfterString,
            _pingViaWin,
            _pingViaChrome,
            _pingViaWinAfterString,
            _pingViaChromeAfterString,
            _pingRoute,
            _stringPostOk,
            _pageReady,
            _pageReadyJson,
            synthetic,
            syntheticMs,
            postError,
            state?["err"]?.GetValue<string>() ?? string.Empty,
            "宿主渲染 " + _dataUrlRenderMs + " ms（扫描件第 0 页，longEdge=" + DataUrlLongEdge + "）");

        if (onScreen)
        {
            _dataUrlOnScreen = result;
        }
        else
        {
            _dataUrlOffScreen = result;
        }
    }

    /// <summary>
    /// 屏幕外那一轮的重测：与 Probe2DataUrlAsync(onScreen:false) 完全同参数，
    /// 用来证明这一条结论可复现（不是一次偶发）。
    /// </summary>
    private async Task Probe2SameUrlControlAsync()
    {
        await Probe2DataUrlAsync(onScreen: false).ConfigureAwait(true);
        _dataUrlSecondRun = _dataUrlOffScreen;
    }

    // ---------------------------------------------------------------- 问题 2 对照：纯洁环境

    /// <summary>
    /// 对照组：另起一个 WebView2 控件 + 全新的 CoreWebView2Environment
    /// （不传 AdditionalBrowserArguments、不套 ReaderWebHost 的安全设置），
    /// 页面用 file:// 加载。它回答的是"WebView2 的宿主→页面通道本身在不在"，
    /// 从而把"运行时/API 层面不通"与"我们的环境配置挡了"分开。
    /// </summary>
    private async Task ProbePlainCoreAsync()
    {
        var plainData = Path.Combine(_paths.TempDir, "t62b-plain-wv2");
        Directory.CreateDirectory(plainData);

        var pagePath = Path.Combine(_paths.TempDir, "t62b-plain.html");

        File.WriteAllText(
            pagePath,
            "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"></head><body>" +
            "<div id=\"s\">plain</div></body></html>",
            new UTF8Encoding(false));

        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: plainData,
            options: null).ConfigureAwait(true);

        await _plainCore.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        var core = _plainCore.CoreWebView2
            ?? throw new InvalidOperationException("对照 WebView2 未就绪");

        _plainVersion = core.Environment.BrowserVersionString;
        _plainWebMessageEnabled = core.Settings.IsWebMessageEnabled;

        core.WebMessageReceived += (_, e) =>
        {
            _plainReady = true;
            _plainReceived++;
            _plainLastJson = e.WebMessageAsJson;
        };

        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "window.__pl={msgs:0,last:''};" +
            "window.addEventListener('message',function(ev){window.__pl.msgs++;" +
            "window.__pl.last=(typeof ev.data==='string')?ev.data.slice(0,60):typeof ev.data;});" +
            "window.chrome.webview.addEventListener('message',function(ev){window.__pl.msgs++;" +
            "window.__pl.last='chrome:'+((typeof ev.data==='string')?ev.data.slice(0,60):typeof ev.data);});" +
            "try{window.chrome.webview.postMessage({type:'plain.ready'});}catch(e){}").ConfigureAwait(true);

        core.Navigate(new Uri(pagePath).AbsoluteUri);

        for (var i = 0; i < 12 && !_plainReady; i++)
        {
            await Task.Delay(120).ConfigureAwait(true);
        }

        // 连发 6 次，间隔 400 ms——排除"只发一次恰好错过"。
        for (var i = 0; i < 6; i++)
        {
            try
            {
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "plain.ping", n = i }));
            }
            catch (Exception ex)
            {
                _notes.Add("对照 PostWebMessageAsJson 抛异常：" + ex.GetType().Name + ": " + ex.Message);
                break;
            }

            await Task.Delay(400).ConfigureAwait(true);
        }

        await Task.Delay(600).ConfigureAwait(true);

        var raw = await core.ExecuteScriptAsync("JSON.stringify(window.__pl||{msgs:-1})")
            .ConfigureAwait(true);

        // ExecuteScriptAsync 的结果是"脚本返回值的 JSON 编码"，字符串要解一层。
        try
        {
            _plainJsMsgs = JsonSerializer.Deserialize<string>(raw) ?? raw;
        }
        catch (JsonException)
        {
            _plainJsMsgs = raw;
        }
    }

    // ---------------------------------------------------------------- 问题 3：渲染吞吐

    private async Task Probe3ThroughputAsync()
    {
        EnsureCorpus();

        var pages = new List<T62BRender>();

        // 10 页顺序渲染（原文件路径，不复制）
        for (var page = 0; page <= 9; page++)
        {
            var index = page;
            var bytes = 0;
            var sw = Stopwatch.StartNew();

            try
            {
                var data = await Task.Run(() => _pdf.RenderPage(_pdfPath, index, TextLongEdge))
                    .ConfigureAwait(true);

                sw.Stop();
                bytes = data.Length;
                _pageBytes[index] = data;
            }
            catch (Exception ex)
            {
                sw.Stop();
                bytes = -1;
                _notes.Add("RenderPage(" + index + ") 异常：" + ex.GetType().Name + ": " + ex.Message);
            }

            pages.Add(new T62BRender(index, sw.ElapsedMilliseconds, bytes, null));
        }

        _pages = pages;

        var ok = pages.Where(p => p.Bytes > 0).ToList();

        if (ok.Count > 0)
        {
            _pageMin = ok.Min(p => p.Ms);
            _pageMax = ok.Max(p => p.Ms);
            _pageAvg = (long)ok.Average(p => p.Ms);
        }

        // 同一页重复渲染第 2 次（page 2），看 RenderPage 有没有缓存加速
        var repeatSw = Stopwatch.StartNew();
        var repeatBytes = await Task.Run(() => _pdf.RenderPage(_pdfPath, 2, TextLongEdge)).ConfigureAwait(true);
        repeatSw.Stop();

        var firstMs = pages.FirstOrDefault(p => p.PageIndex == 2)?.Ms ?? -1;

        _repeat = new T62BRepeat(2, firstMs, repeatSw.ElapsedMilliseconds, repeatBytes.Length);

        // 扫描件 1600 / 2400
        var scanSmallSw = Stopwatch.StartNew();
        var scanSmall = await Task.Run(() => _pdf.RenderPage(_scanPdfPath, 0, ScanSmallLongEdge))
            .ConfigureAwait(true);
        scanSmallSw.Stop();

        var scanLargeSw = Stopwatch.StartNew();
        var scanLarge = await Task.Run(() => _pdf.RenderPage(_scanPdfPath, 0, ScanLargeLongEdge))
            .ConfigureAwait(true);
        scanLargeSw.Stop();

        // 2400 那一张留一份给小尺寸通道验证用（后面的 data URL 判断会引用大小）
        _scan = new T62BScan(
            Path.GetFileName(_scanPdfPath),
            new T62BRender(0, scanSmallSw.ElapsedMilliseconds, scanSmall.Length, ScanSmallLongEdge),
            new T62BRender(0, scanLargeSw.ElapsedMilliseconds, scanLarge.Length, ScanLargeLongEdge));

        _scanBytes2400 = scanLarge.Length;
        // 仓库现在多了个"带渲染参数 + 20 页 LRU"的重载；用反射量一下它到底快不快。
        await ProbeOptionsOverloadCacheAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 可选附加项。**为什么用反射**：这个重载是别的改动引进来的，T6-2B 探针不该硬依赖它；
    /// 探不到就当"本构建没有"如实记一笔，不影响三个主问题的结论。
    /// </summary>
    private async Task ProbeOptionsOverloadCacheAsync()
    {
        try
        {
            // 刻意全部走反射（连参数类型也不写死）：这个重载是别的改动引进来的，
            // 探针不该在编译期依赖它，探不到就当"本构建没有"。
            var optionsType = Type.GetType("EbookToolSuite.Core.Models.PdfPageRenderOptions, EbookToolSuite.Core");

            var method = optionsType is null
                ? null
                : _pdf.GetType().GetMethod(
                    "RenderPage",
                    new[] { typeof(string), typeof(int), optionsType, typeof(string) });

            if (method is null || optionsType is null)
            {
                _optionsCache = new T62BOptionsCache(
                    false, -1, -1, 0, 0,
                    "method=" + (method is null ? "无" : "有") + " type=" + (optionsType is null ? "无" : "有"));
                return;
            }

            // 这个类型可能没有无参构造（实测 PdfPageRenderOptions 就是 record 风格），
            // 所以按"参数最少的构造器"用默认值造一个，再设 MaxLongEdge。
            var ctor = optionsType.GetConstructors()
                .OrderBy(c => c.GetParameters().Length)
                .FirstOrDefault();

            if (ctor is null)
            {
                _optionsCache = new T62BOptionsCache(false, -1, -1, 0, 0, "没有可用构造器");
                return;
            }

            var ctorArgs = ctor.GetParameters()
                .Select(p => p.HasDefaultValue
                    ? p.DefaultValue
                    : (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null))
                .ToArray();

            var options = ctor.Invoke(ctorArgs);
            optionsType.GetProperty("MaxLongEdge")?.SetValue(options, TextLongEdge);

            (long Ms, int Bytes) Once()
            {
                var sw = Stopwatch.StartNew();
                var image = method.Invoke(_pdf, new object?[] { _pdfPath, 2, options, null });
                sw.Stop();

                // 全部走反射：不同的 PdfPageImage 版本字节属性名可能不同，两个名字都试。
                var imageType = image?.GetType();
                var bytes = (imageType?.GetProperty("Jpeg")?.GetValue(image)
                    ?? imageType?.GetProperty("JpegBytes")?.GetValue(image)) as byte[];

                return (sw.ElapsedMilliseconds, bytes?.Length ?? -1);
            }

            var first = await Task.Run(Once).ConfigureAwait(true);
            var second = await Task.Run(Once).ConfigureAwait(true);

            _optionsCache = new T62BOptionsCache(
                true, first.Ms, second.Ms, first.Bytes, second.Bytes,
                "page 2 longEdge=" + TextLongEdge + "，第二次紧接第一次（同一进程同一路径）");
        }
        catch (Exception ex)
        {
            _optionsCache = new T62BOptionsCache(
                false, -1, -1, 0, 0,
                "探测失败：" + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>样本分工：问题 2 用扫描件（图大字少），其余用命令行指定的那本。</summary>
    private void EnsureCorpus()
    {
        if (!string.IsNullOrEmpty(_scanPdfPath))
        {
            return;
        }

        var dir = Path.GetDirectoryName(_pdfPath) ?? string.Empty;
        var files = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.pdf").OrderBy(f => new FileInfo(f).Length).ToList()
            : new List<string>();

        // 问题 2 要验"大消息不丢"，所以刻意挑扫描件：本机样本里 38.6 MB /
        // 320 页纯扫描的那本（sample-pdf-2）。找不到就退回最小的另一本，并在备注里写明。
        var preferred = files.FirstOrDefault(f =>
            f.Contains("sample-pdf-2", StringComparison.Ordinal)
            || f.Contains("捕縄", StringComparison.Ordinal));

        if (preferred is not null)
        {
            _scanPdfPath = preferred;
            return;
        }

        else
        {
            _scanPdfPath = files.FirstOrDefault(f =>
                !string.Equals(f, _pdfPath, StringComparison.OrdinalIgnoreCase)) ?? _pdfPath;

            lock (_notes)
            {
                _notes.Add("没找到 sample-pdf-2 扫描件，问题 2 退用 " + Path.GetFileName(_scanPdfPath));
            }
        }
    }

    // ---------------------------------------------------------------- 基础设施

    private void SetStatus(string text)
    {
        try
        {
            StatusText.Text = text;
        }
        catch (Exception)
        {
            // 窗口已关，忽略
        }
    }

    private void ResetNav() => _navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task NavigateAsync(string url)
    {
        ResetNav();
        ActiveCore.Navigate(url);

        var tcs = _navTcs;

        if (tcs is not null)
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(15000)).ConfigureAwait(true);

            if (done != tcs.Task)
            {
                _notes.Add("导航超时：" + url);
            }
        }

        await Task.Delay(250).ConfigureAwait(true);
    }

    private Task<string> ExecuteAsync(string script) => ActiveCore.ExecuteScriptAsync(script);

    /// <summary>ExecuteScriptAsync 的结果是"脚本返回值的 JSON 编码"，字符串要再解一层。</summary>
    private async Task<JsonNode?> EvalAsync(string script)
    {
        var raw = await ActiveCore.ExecuteScriptAsync(script).ConfigureAwait(true);

        if (string.IsNullOrEmpty(raw) || raw == "null")
        {
            return null;
        }

        try
        {
            var inner = JsonSerializer.Deserialize<string>(raw);

            if (inner is null)
            {
                return null;
            }

            return JsonNode.Parse(inner);
        }
        catch (JsonException ex)
        {
            _notes.Add("解析脚本结果失败：" + ex.Message + "，原文前缀=" + raw[..Math.Min(80, raw.Length)]);
            return null;
        }
    }

    private static string JsString(string value) => JsonSerializer.Serialize(value);

    /// <summary>
    /// 往临时目录写一个最小宿主页（属于 ebook.local，与 reader.html 同一个源），
    /// 返回它的 http URL。**不碰 Assets 下的 reader.html / reader.js / reader.css**。
    /// </summary>
    private string EnsurePageAsync()
    {
        var path = Path.Combine(_paths.TempDir, "t62b-probe-page.html");

        const string html =
            "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">" +
            "<title>T6-2B 通道探针页</title>" +
            "<style>html,body{margin:0;background:#fff;font:14px sans-serif}</style></head>" +
            "<body><div id=\"s\">T6-2B 探针页</div></body></html>";

        File.WriteAllText(path, html, new UTF8Encoding(false));

        return ReaderEndpoints.VirtualHostRootUrl + "t62b-probe-page.html";
    }

    private T62BProbeReport BuildReport(DateTimeOffset startedAt, long elapsedMs)
    {
        return new T62BProbeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            elapsedMs,
            _browserVersion ?? "未知",
            ReaderEndpoints.VirtualHostName,
            MemoryHostName,
            _paths.TempDir,
            Path.GetFileName(_pdfPath),
            _memory,
            _concurrent,
            _dataUrlOffScreen,
            _dataUrlOnScreen,
            _dataUrlSecondRun,
            new T62BPlainEnv(
                _plainVersion,
                _plainWebMessageEnabled,
                _plainReady,
                _plainReceived,
                _plainLastJson,
                _plainJsMsgs),
            _pages,
            _pageMin,
            _pageAvg,
            _pageMax,
            _repeat,
            _scan,
            _optionsCache,
            _netFailures.ToList(),
            _netResponses.ToList(),
            _notes);
    }

    private void WriteReport(T62BProbeReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_reportPath)!);

        File.WriteAllText(
            _reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    // ---------------------------------------------------------------- 状态字段

    private T62BMemory? _memory;
    private T62BConcurrent? _concurrent;
    private IReadOnlyList<T62BRender> _pages = Array.Empty<T62BRender>();
    private T62BRepeat? _repeat;
    private T62BScan? _scan;
    private T62BOptionsCache? _optionsCache;
    private long _pageMin = -1;
    private long _pageAvg = -1;
    private long _pageMax = -1;
    private long _dataUrlRenderMs = -1;
    private bool _pingReceived;
    private int _pingChars;
    private string _bridgeProbe = string.Empty;
    private bool _pageReady;
    private string _pageReadyJson = string.Empty;
    private int _pingMsgs;
    private int _pingMsgsAfterString;
    private int _pingViaWin;
    private int _pingViaChrome;
    private int _pingViaWinAfterString;
    private int _pingViaChromeAfterString;
    private string _pingRoute = string.Empty;
    private bool _stringPostOk = true;
    private int _scanBytes2400;
    private readonly Dictionary<int, byte[]> _pageBytes = new();

    /// <summary>只暴露内存里那一段字节的只读流（MemoryStream 是 seekable 的，
    /// 这里刻意用非 seekable 的包装，跟真实实现一致：真实场景是"一次性回一段字节"。</summary>
    private sealed class MemoryReadStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public MemoryReadStream(byte[] data) => _data = data;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _data.Length - _position;

            if (remaining <= 0 || count <= 0)
            {
                return 0;
            }

            var take = Math.Min(count, remaining);
            Array.Copy(_data, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
