using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Reader.WebView;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T6-2B「移动端图片通道」探针的入口。只做三件事：
/// 建探针窗口（WebView2 需要真窗口）→ 跑三个问题 → 把报告写到
/// <c>&lt;Data&gt;\Temp\t62b-probe.json</c>。
///
/// 它**不改任何业务逻辑**：不碰 reader.html / reader.js / reader.css，
/// 页面里要用的脚本一律 ExecuteScriptAsync 注入；也不碰 ReaderService /
/// PdfDocumentService 的现有行为，只用它们的公开接口量数字。
/// </summary>
public static class T62BProbeTest
{
    public static async Task<T62BProbeReport> RunAsync(
        AppPaths paths,
        string pdfPath,
        IPdfDocumentService pdfPages,
        IWebViewEnvironmentProvider environments)
    {
        var reportPath = Path.Combine(paths.TempDir, "t62b-probe.json");

        var window = new Views.T62BProbeWindow(paths, pdfPath, reportPath, pdfPages);

        window.Show();

        var report = await window.RunAsync().ConfigureAwait(true);

        window.Close();

        return report;
    }

    /// <summary>报告落盘位置，供 App 与脚本对齐。</summary>
    public static string ReportPathFor(AppPaths paths) => Path.Combine(paths.TempDir, "t62b-probe.json");
}

/// <summary>问题 1（内存虚拟主机通道）的实测记录。</summary>
public sealed record T62BMemory(
    int EventCalls,
    int MaxConcurrentDeferrals,
    string ImgSrc,
    int NaturalWidth,
    int NaturalHeight,
    bool Complete,
    long SrcToOnloadMs,
    int HostRenderedBytes,
    string Error,
    string Note)
{
    public bool Loaded => Error.Length == 0 && NaturalWidth > 0 && Complete;

    public string Verdict => Loaded
        ? $"通：事件 {EventCalls} 次，{NaturalWidth}×{NaturalHeight}，{SrcToOnloadMs} ms"
        : $"不通：事件 {EventCalls} 次，error=\"{Error}\"，w={NaturalWidth}，complete={Complete}";
}

/// <summary>问题 1 的并发附加项：6 张图同时请求（翻页预取就是这个模式）。</summary>
public sealed record T62BConcurrent(
    int Requested,
    int EventCalls,
    int MaxConcurrentDeferrals,
    int Loaded,
    int Failed,
    long AllLoadedMs,
    string Detail)
{
    public string Verdict => Loaded == Requested
        ? $"{Requested} 张并发全部 onload，用时 {AllLoadedMs} ms，事件 {EventCalls} 次，" +
          $"GetDeferral 同时最多挂 {MaxConcurrentDeferrals} 个"
        : $"并发未全成：onload={Loaded} onerror={Failed} 事件={EventCalls} 次（{Detail}）";
}

/// <summary>问题 2（PostWebMessageAsJson + data URL）的实测记录。</summary>
public sealed record T62BDataUrl(
    bool Received,
    int HostMessageChars,
    int PageDataChars,
    int NaturalWidth,
    int NaturalHeight,
    long AssignToOnloadMs,
    int JpegBytes,
    bool PingReceived,
    int PingChars,
    string BridgeProbe,
    int MessageEventsAfterJsonPost,
    int MessageEventsAfterStringPost,
    int MessageEventsViaWindow,
    int MessageEventsViaChromeBridge,
    int MessageEventsViaWindowAfterString,
    int MessageEventsViaChromeBridgeAfterString,
    string PingRoute,
    bool StringPostOk,
    bool PageReadySignalReceived,
    string PageReadyJson,
    bool SyntheticFallbackLoaded,
    long SyntheticFallbackMs,
    string HostPostError,
    string Error,
    string Trigger)
{
    public bool Loaded => Received && NaturalWidth > 0;

    /// <summary>
    /// 注意 Error 可能是 "onerror"：实测这条通道有时会把同一个 data URL 投递两次，
    /// 第二次解码失败会覆盖 t.err，但**第一次已经成功解码**（NaturalWidth &gt; 0 就是证据）。
    /// 所以判定只认 NaturalWidth。
    /// </summary>
    public string Verdict
    {
        get
        {
            var route = $"投递路径={PingRoute}（window 路 {MessageEventsViaWindow} 条，" +
                $"chrome.webview 路 {MessageEventsViaChromeBridge} 条）";

            if (!Received)
            {
                return $"不通：小消息(ping)收到={PingReceived}（{PingChars} 字符）；{route}；" +
                    $"大消息 {HostMessageChars} 字符没到页面；postError=\"{HostPostError}\"";
            }

            return (SyntheticFallbackLoaded ? "合成 MessageEvent 通：" : "原生通道通：")
                + $"收到 {PageDataChars} 字符，{NaturalWidth}×{NaturalHeight}，{AssignToOnloadMs} ms；{route}";
        }
    }
}

/// <summary>一次 RenderPage 的耗时与产物大小。</summary>
public sealed record T62BRender(int PageIndex, long Ms, int Bytes, int? LongEdge);

/// <summary>同一页渲染两次的对照。</summary>
public sealed record T62BRepeat(int PageIndex, long FirstMs, long SecondMs, int SecondBytes)
{
    public string Verdict => SecondMs < FirstMs
        ? $"有加速：{FirstMs} ms → {SecondMs} ms（{FirstMs - SecondMs} ms）"
        : $"无加速：{FirstMs} ms → {SecondMs} ms（变快为负）";
}

/// <summary>扫描件两个长边的对照。</summary>
public sealed record T62BScan(string File, T62BRender LongEdge1600, T62BRender LongEdge2400);

/// <summary>问题 2 的对照组：另起一个控件 + 全新环境（不套本项目的启动参数与安全设置）。</summary>
public sealed record T62BPlainEnv(
    string BrowserVersion,
    bool IsWebMessageEnabled,
    bool ReadySignalReceived,
    int HostMessagesReceivedByHost,
    string LastHostMessageJson,
    string PageReportedState);

/// <summary>
/// 可选附加项：仓库现在多了一个带渲染参数的 RenderPage 重载（接口注释说有 20 页 LRU）。
/// 用反射探一下它在不在、同页第二次调用到底有没有加速——**探不到不影响主结论**。
/// </summary>
public sealed record T62BOptionsCache(
    bool OverloadPresent,
    long FirstMs,
    long SecondMs,
    int FirstBytes,
    int SecondBytes,
    string Detail)
{
    public string Verdict => !OverloadPresent
        ? "本构建里没有带参数的重载：" + Detail
        : (SecondMs < FirstMs
            ? $"有加速：{FirstMs} ms → {SecondMs} ms（{FirstMs - SecondMs} ms），{FirstBytes} B"
            : $"无加速：{FirstMs} ms → {SecondMs} ms，{FirstBytes} B");
}

/// <summary>T6-2B 探针报告。三个问题各一段，全部是实测数字。</summary>
public sealed record T62BProbeReport(
    string StartedAt,
    string FinishedAt,
    long ElapsedMs,
    string BrowserVersion,
    string ShellHost,
    string MemoryHost,
    string TempDir,
    string PdfFileName,
    T62BMemory? Memory,
    T62BConcurrent? MemoryConcurrent,
    T62BDataUrl? DataUrlOffScreen,
    T62BDataUrl? DataUrlOnScreen,
    T62BDataUrl? DataUrlSecondRun,
    T62BPlainEnv? PlainEnv,
    IReadOnlyList<T62BRender> Pages,
    long PageMinMs,
    long PageAvgMs,
    long PageMaxMs,
    T62BRepeat? Repeat,
    T62BScan? Scan,
    T62BOptionsCache? OptionsOverloadCache,
    IReadOnlyList<string> NetFailures,
    IReadOnlyList<string> NetResponses,
    IReadOnlyList<string> Notes)
{
    /// <summary>
    /// 退出码 2 的条件：**三个问题都必须给出数字**。
    /// 结论可以是"不通"（那也是有价值的实测结论），但不能是"没测到"。
    /// </summary>
    [JsonIgnore]
    public bool Passed =>
        Memory is not null
        && DataUrlOffScreen is not null
        && DataUrlOnScreen is not null
        && DataUrlSecondRun is not null
        && PlainEnv is not null
        && Pages.Count == 10
        && Pages.All(p => p.Bytes > 0)
        && Scan is not null;
}
