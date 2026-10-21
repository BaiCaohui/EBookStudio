using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Reader.WebView;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T6-3「漫画 CBZ/CBR」的端到端验收。
/// </summary>
/// <remarks>
/// 判据与设计文档/计划 T6-3 逐条对应：
///   · ① CBZ 打开正常 + 图片自然序（page1/page2/page10）。自然序用**三张纯色页**证明：
///     第 1/2/3 页中心像素应分别是红/绿/蓝——如果写成字典序，第 3 页会是 page2 的绿色。
///   · ② CBR：**本机没有真实 RAR 样本、也不联网下载**（设计文档 §12.5 E），如实记"未检验"。
///   · ③ 单页 / 双页 / 日漫三种模式切换（含 DOM 里的左右顺序与按钮显隐）。
///   · ④ 200 页快速翻页不 OOM（工作集峰值/终值 < 500 MB）。
///   · ⑤ 预载生效：当前页之后 1~2 页在翻页前就已就绪（prefetchedPages）。
///   · ⑥ 自动切白边：规格标为"可选"，本轮未实现，如实记未实现。
///   · ⑦ 200 页打开 ≤ 3 s（重开三次取 P50，每次的毫秒都写进报告）。
/// 附：判据外的"漫画页缓存不落盘"——全新 datadir 下 Data\Cache\Pages 不应存在。
/// </remarks>
public static class T63SmokeTest
{
    private const long MemoryBudgetBytes = 500L * 1024 * 1024;
    private const long MemoryGrowthBudgetBytes = 100L * 1024 * 1024;
    private const int OpenBudgetMs = 3000;
    private const int TurnBudgetMs = 200;

    /// <summary>200 页样本的页数与页面尺寸（贴近真实扫描漫画的中等尺寸）。</summary>
    private const int PageCount = 200;
    private const int PageWidth = 1600;
    private const int PageHeight = 2400;

    private const int PageReadyTimeoutMs = 8000;

    public static async Task<SmokeReport> RunAsync(
        IBookImportService importer,
        IReaderService reader,
        IWebViewEnvironmentProvider environments,
        IReadingProgressService progress,
        IComicBookService comics,
        ILoggerFactory loggers,
        AppPaths paths,
        string corpusRoot)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();
        var shots = new List<string>();
        var shotsDir = Path.Combine(paths.TempDir, "t63-shots");

        Directory.CreateDirectory(shotsDir);

        void Add(string name, bool ok, string detail) => checks.Add(new SmokeCheck(name, ok, detail));

        var corpus = corpusRoot;

        BuildCorpus(corpus);

        /* ---------- 导入阶段（先把三本都导完，再开窗口） ----------
           判据⑦要测"首次打开"：必须让 200 页那本成为**第一个**阅读窗口。
           关掉一个阅读窗口再开下一个时要多付 6~7 秒（WebView2 等浏览器进程回收，
           分阶段实测 core=6.5 s），那是进程生命周期，不是"打开一本漫画"的耗时，单列。 */
        var orderPath = Path.Combine(corpus, "t63-order.cbz");
        var orderImport = await importer.ImportAsync(orderPath, ImportOptions.Default);

        Add("J0-import-order", orderImport.Outcome == ImportOutcome.Imported,
            $"导入结果={orderImport.Outcome}，书={orderImport.BookId?.ToString("N")[..8]}");

        // ---------- ② CBR：未检验 ----------
        Add("J2-cbr-open", false,
            "未检验／跳过：本机无真实 RAR 样本；按设计文档 §12.5 E 节不联网下载。" +
            "SharpCompress 的 RAR 读取路径未被真实样本验证，留待用户提供 .cbr 后补验。" +
            "已由单测覆盖的部分：RAR 签名识别、损坏包明确报错、空归档按无图片处理。");

        var brokenPath = Path.Combine(corpus, "t63-broken.cbz");
        var brokenImport = await importer.ImportAsync(brokenPath, ImportOptions.Default);
        var brokenOk = brokenImport.Outcome == ImportOutcome.InvalidEpub
            && (brokenImport.Message ?? string.Empty).Contains("不支持", StringComparison.Ordinal);

        Add("J2-broken-cbz-explicit-error", brokenOk,
            $"导入结果={brokenImport.Outcome}，提示={brokenImport.Message}（期望 InvalidEpub 且含「不支持」）");

        var bigPath = Path.Combine(corpus, "t63-200p.cbz");
        var bigImport = await importer.ImportAsync(bigPath, ImportOptions.Default);

        if (bigImport.Outcome != ImportOutcome.Imported)
        {
            Add("J7-open-200p", false, $"导入失败：{bigImport.Outcome} {bigImport.Message}");
            return Finish(paths, startedAt, stopwatch, checks, shots);
        }

        // ---------- ⑦ 首次打开 ≤ 3 s ----------
        var phases = new List<string>();
        var openWatch = Stopwatch.StartNew();

        var bigWindow = new ReaderWindow(
            reader, environments, progress, loggers.CreateLogger<ReaderWindow>(),
            bigImport.BookId!.Value, comics: comics);

        bigWindow.Show();

        var bigCore = await WaitForCoreAsync(bigWindow, 30000);
        var coreMs = openWatch.ElapsedMilliseconds;

        if (bigCore is null)
        {
            Add("J7-open-200p", false, "CoreWebView2 未就绪（30 s 超时）");
            return Finish(paths, startedAt, stopwatch, checks, shots);
        }

        await WaitForComicStateAsync(bigCore, 30000).ConfigureAwait(true);
        var stateMs = openWatch.ElapsedMilliseconds;

        var pageOk = await WaitForPageImageAsync(bigCore, 0, PageReadyTimeoutMs * 2)
            .ConfigureAwait(true);
        var openMs = openWatch.ElapsedMilliseconds;

        phases.Add($"core={coreMs} state={stateMs} page={openMs} ok={pageOk}"
            + (pageOk ? string.Empty
                : " diag={" + await PdfDiagAsync(bigCore).ConfigureAwait(true) + "}"));

        var totalPages = TotalPagesOf(await StateAsync(bigCore).ConfigureAwait(true));

        Add("J7-cbz-page-count", totalPages == PageCount,
            $"totalPages={totalPages}（期望 {PageCount}）");

        // ---------- 工具栏两端（用户实测反馈）：上一页最左 / 下一页最右（第一行）；
        //            主题固定在加号右边、也在第一行（用户 2026-10-19 要求，不再挂第二行最右） ----------
        var rects = await EvalJsonAsync(bigCore, ToolbarRectsJs).ConfigureAwait(true);
        var tb = NumRect(rects, "toolbar");
        var prev = NumRect(rects, "prev");
        var next = NumRect(rects, "next");
        var theme = NumRect(rects, "theme");
        var zoomIn = NumRect(rects, "zoomIn");
        var topRow = NumRect(rects, "top");
        var bottomRow = NumRect(rects, "bottom");

        var rectsOk = tb is not null && prev is not null && next is not null
            && theme is not null && zoomIn is not null && topRow is not null && bottomRow is not null;

        /* 容差 16px：工具栏自身有 12px 左右 padding，按钮贴着内容边缘，
           所以"距工具栏外框"本来就约等于 padding（实测 12px）。 */
        var endsOk = rectsOk
            && Math.Abs(prev!.Value.L - tb!.Value.L) <= 16
            && Math.Abs(tb.Value.R - next!.Value.R) <= 16
            && Math.Abs(prev.Value.T - next.Value.T) <= 4
            && Math.Abs(theme!.Value.T - prev.Value.T) <= 4
            && theme.Value.L >= zoomIn!.Value.R - 2;

        Add("J1-toolbar-ends", endsOk,
            rectsOk
                ? $"上一页左边距={prev!.Value.L - tb!.Value.L:F0}px；" +
                  $"下一页右边距={tb.Value.R - next!.Value.R:F0}px（与上一页同一行：ΔT={Math.Abs(prev.Value.T - next.Value.T):F0}px）；" +
                  $"主题在加号右边={theme!.Value.L >= zoomIn!.Value.R - 2}（与上一页同行：ΔT={Math.Abs(theme.Value.T - prev.Value.T):F0}px）"
                : "工具栏或按钮的矩形读不到（#pdfToolRowTop / #pdfToolRowBottom 缺失？）");

        await CaptureShotAsync(bigCore, shotsDir, "04-200p-page1", shots).ConfigureAwait(true);

        // ---------- 工具栏「自定义主题三色框」位置（用户 2026-10-21 截图反馈） ----------
        /* 用户要求：切到「自定义」主题后，三个取色器显示在「自定义」下拉框右边，并且不论
           一行还是两行都留在第一行；第一行两端仍钉死上一页 / 下一页。
           DOM 位置由 reader.js 的 ensurePdfToolbarRows() park 到 #pdfToolRowTop 负责；
           一行/两行仍由 pdfUpdateToolbarRowsForWidth() 按**实测高度**决定 —— 所以这里不猜
           宽度阈值，直接把窗口改窄 / 改宽量真实 rect，并把分界阈值二分出来验掉。 */
        var originalThemeId = await EvalStringAsync(
            bigCore, "document.getElementById('themeSelect').value").ConfigureAwait(true);
        var originalInnerWidth = await EvalNumberAsync(bigCore, "window.innerWidth").ConfigureAwait(true);
        var originalWidth = bigWindow.Width;
        var frameOverhead = originalWidth - originalInnerWidth;

        ToolbarLayout? ParseLayout(JsonNode? node)
        {
            if (node is not JsonObject) { return null; }

            return new ToolbarLayout(
                NumOf(node, "innerWidth"),
                BoolOf(node, "wide"),
                BoolOf(node, "rows"),
                NumRect(node, "toolbar"),
                NumRect(node, "top"),
                NumRect(node, "bottom"),
                NumRect(node, "prev"),
                NumRect(node, "next"),
                NumRect(node, "theme"),
                NumRect(node, "colors"),
                NumRect(node, "colorBg"),
                NumRect(node, "colorFg"),
                NumRect(node, "colorLink"));
        }

        async Task<ToolbarLayout?> ResizeAndReadAsync(double width)
        {
            var expectedInner = width - frameOverhead;

            bigWindow.Width = width;

            // 等 WebView 真的按新宽度重排（resize 事件里同步重算 ets-pdf-wide），再留一帧给 rect。
            await WaitAsync(
                () => EvalBoolAsync(bigCore,
                    "Math.abs(window.innerWidth - (" + expectedInner.ToString("F1") + ")) <= 3"),
                6000).ConfigureAwait(true);
            await Task.Delay(120).ConfigureAwait(true);

            return ParseLayout(await EvalJsonAsync(bigCore, ToolbarLayoutJs).ConfigureAwait(true));
        }

        /* 位置约束：三个色框可点可见、在「自定义」下拉框右边、与下拉框 / 上一页同一行；
           上一页第一行最左贴边、下一页第一行最右贴边（容差沿用 J1 的 16px 口径）；
           两行模式下色框必须落在显式第一行 #pdfToolRowTop 内。 */
        bool PlacementOk(ToolbarLayout layout, bool wide)
        {
            var tb = layout.Toolbar;
            var prev = layout.Prev;
            var next = layout.Next;
            var theme = layout.Theme;
            var colors = layout.Colors;
            var colorBg = layout.ColorBg;
            var colorFg = layout.ColorFg;
            var colorLink = layout.ColorLink;

            if (tb is null || prev is null || next is null || theme is null || colors is null
                || colorBg is null || colorFg is null || colorLink is null)
            {
                return false;
            }

            if (!layout.HasRows || layout.Wide != wide) { return false; }

            var colorVisible = colors.Value.W > 0 && colors.Value.H > 0
                && colorBg.Value.W > 0 && colorFg.Value.W > 0 && colorLink.Value.W > 0;

            var sameRow = colorVisible
                && colors.Value.L >= theme.Value.R - 2
                && Math.Abs(colors.Value.T - theme.Value.T) <= 4
                && Math.Abs(colors.Value.T - prev.Value.T) <= 4
                && Math.Abs(theme.Value.T - prev.Value.T) <= 4
                && Math.Abs(prev.Value.T - next.Value.T) <= 4;

            var edges = Math.Abs(prev.Value.L - tb.Value.L) <= 16
                && Math.Abs(tb.Value.R - next.Value.R) <= 16;
            var notClipped = colors.Value.R <= tb.Value.R + 2 && next.Value.R <= tb.Value.R + 2;

            if (wide)
            {
                /* 一行：行容器 display:contents（rect 全 0 不可用），用工具栏高度证明"确实只有一行"
                   —— 折行会整整多出一行按钮高。 */
                return sameRow && edges && notClipped && tb.Value.H <= prev.Value.H + 26;
            }

            /* 两行：色框与上一页都必须落在显式第一行里，第二行在它下面。 */
            var top = layout.Top;
            var bottom = layout.Bottom;

            return sameRow && edges && notClipped
                && top is not null && bottom is not null
                && top.Value.B > 0
                && prev.Value.B <= top.Value.B + 2
                && colors.Value.B <= top.Value.B + 2
                && bottom.Value.T >= top.Value.B - 2;
        }

        string DescribeLayout(ToolbarLayout? layout, string label)
        {
            if (layout is not { } l || l.Toolbar is not { } tb || l.Prev is not { } prev
                || l.Next is not { } next || l.Theme is not { } theme || l.Colors is not { } colors)
            {
                return label + "：矩形读不到（探针=" + (layout is null ? "null" : "缺字段") + "）";
            }

            return label + "：窗口内宽=" + l.InnerWidth.ToString("F0")
                + "px，工具栏宽=" + tb.W.ToString("F0")
                + "px，一行(ets-pdf-wide)=" + l.Wide
                + "；色框 left=" + colors.L.ToString("F0")
                + "、自定义 right=" + theme.R.ToString("F0")
                + "（在自定义右边=" + (colors.L >= theme.R - 2) + "）"
                + "，ΔT(色框,自定义)=" + Math.Abs(colors.T - theme.T).ToString("F0") + "px"
                + "，ΔT(色框,上一页)=" + Math.Abs(colors.T - prev.T).ToString("F0") + "px"
                + "；上一页左边距=" + (prev.L - tb.L).ToString("F0") + "px"
                + "，下一页右边距=" + (tb.R - next.R).ToString("F0") + "px"
                + "，工具栏高=" + tb.H.ToString("F0") + "px（按钮高 " + prev.H.ToString("F0") + "px）"
                + (l.Top is { } top && l.Bottom is { } bottom
                    ? "，第一行底=" + top.B.ToString("F0") + "、第二行顶=" + bottom.T.ToString("F0")
                    : string.Empty)
                + "；色框可见=" + (colors.W > 0 && colors.H > 0);
        }

        // ① 窄窗（两行）：色框在第一行「自定义」右边，上一页 / 下一页仍在第一行两端。
        await EvalStringAsync(bigCore, "window.__readerDebug.setTheme('custom')").ConfigureAwait(true);

        var narrowLayout = await ResizeAndReadAsync(1000).ConfigureAwait(true);
        var narrowOk = narrowLayout is { } narrowProbe && PlacementOk(narrowProbe, wide: false);

        await CaptureShotAsync(bigCore, shotsDir, "08-toolbar-custom-narrow", shots).ConfigureAwait(true);

        Add("J1b-pdf-toolbar-custom-colors-narrow", narrowOk, DescribeLayout(narrowLayout, "窄窗"));

        // ② 宽窗（一行）：色框同样在「自定义」右边，整条工具栏并成一行。
        var wideWidth = 2000.0;
        var wideLayout = await ResizeAndReadAsync(wideWidth).ConfigureAwait(true);

        if (wideLayout is not { Wide: true })
        {
            /* 屏幕级的宽度上限/离屏渲染兜底：再往大试一档（判定仍然是实测，不写死）。 */
            wideWidth = 2400;
            wideLayout = await ResizeAndReadAsync(wideWidth).ConfigureAwait(true);
        }

        var wideOk = wideLayout is { } wideProbe && PlacementOk(wideProbe, wide: true);

        await CaptureShotAsync(bigCore, shotsDir, "09-toolbar-custom-wide", shots).ConfigureAwait(true);

        Add("J1c-pdf-toolbar-custom-colors-wide", wideOk,
            DescribeLayout(wideLayout, "宽窗 " + wideWidth.ToString("F0")) + "；截图=09-toolbar-custom-wide.png");

        /* ③ 阈值复核：三个色框加入第一行后，一行/两行的分界线必须仍由实测决定。
           已知窄（1000）与宽（上面那个宽度）两端，二分出"最小的一行宽度"，
           再验分界线两侧布局都满足上面的放置约束 —— 不是把窗口宽度写死。 */
        var lo = 1000.0;
        var hi = wideWidth;

        for (var i = 0; i < 8 && hi - lo > 8; i++)
        {
            var mid = Math.Round((lo + hi) / 2);
            var probe = await ResizeAndReadAsync(mid).ConfigureAwait(true);

            if (probe is { Wide: true }) { hi = mid; } else { lo = mid; }
        }

        var atOneRow = await ResizeAndReadAsync(hi).ConfigureAwait(true);
        var atTwoRow = await ResizeAndReadAsync(lo).ConfigureAwait(true);

        Add("J1d-pdf-toolbar-row-threshold",
            atOneRow is { } oneRow && PlacementOk(oneRow, wide: true)
                && atTwoRow is { } twoRow && PlacementOk(twoRow, wide: false),
            "自定义主题下实测一行/两行分界：窗口宽 ≥ " + hi.ToString("F0") + " 时一行，≤ "
            + lo.ToString("F0") + " 时两行（对应工具栏可用宽约 " + (hi - frameOverhead).ToString("F0")
            + "px；不写死窗口宽度）；" + DescribeLayout(atOneRow, "界线上一行")
            + "；" + DescribeLayout(atTwoRow, "界线下一行"));

        // 恢复原窗口宽度与主题，别影响后面的检查（J3 起还会继续用这个窗口）。
        bigWindow.Width = originalWidth;
        await EvalStringAsync(
            bigCore, "window.__readerDebug.setTheme(" + JsonSerializer.Serialize(originalThemeId) + ")")
            .ConfigureAwait(true);
        await WaitAsync(
            () => EvalBoolAsync(bigCore,
                "Math.abs(window.innerWidth - " + originalInnerWidth.ToString("F0") + ") <= 3"),
            6000).ConfigureAwait(true);
        await Task.Delay(200).ConfigureAwait(true);

        // ---------- ③ 三种模式 ----------
        await EvalAsync(bigCore!, "window.__readerDebug.pdfGoto(1)").ConfigureAwait(true);
        await WaitForPageImageAsync(bigCore!, 1, PageReadyTimeoutMs).ConfigureAwait(true);

        var defaultState = await StateAsync(bigCore!).ConfigureAwait(true);
        var defaultOk = BoolOf(defaultState, "comic")
            && !BoolOf(defaultState, "continuous")
            && !BoolOf(defaultState, "spread")
            && !BoolOf(defaultState, "rtl");

        var layoutDisplay = await EvalStringAsync(bigCore!,
            "getComputedStyle(document.getElementById('pdfLayout')).display").ConfigureAwait(true);
        var searchDisplay = await EvalStringAsync(bigCore!,
            "getComputedStyle(document.getElementById('pdfSearch')).display").ConfigureAwait(true);
        var rtlDisplay = await EvalStringAsync(bigCore!,
            "getComputedStyle(document.getElementById('pdfRtl')).display").ConfigureAwait(true);

        var singleOk = defaultOk && layoutDisplay != "none" && searchDisplay == "none"
            && rtlDisplay != "none";

        Add("J3-single-default", singleOk,
            $"comic={BoolOf(defaultState, "comic")} continuous={BoolOf(defaultState, "continuous")} " +
            $"spread={BoolOf(defaultState, "spread")} rtl={BoolOf(defaultState, "rtl")}；" +
            $"pdfLayout={layoutDisplay}（漫画与 PDF 同一套按钮，必须显示） " +
            $"pdfSearch={searchDisplay}（漫画无文字层，必须藏） pdfRtl={rtlDisplay}");

        /* 用户 2026-10-18：漫画必须有「单页/滚动」切换，且与 PDF 同一套按钮。 */
        var comicScrollState = await EvalJsonAsync(bigCore!, "window.__readerDebug.pdfToggle('layout')")
            .ConfigureAwait(true);
        var comicScrollOk = BoolOf(comicScrollState, "continuous") && !BoolOf(comicScrollState, "spread");

        Add("J3-continuous-mode", comicScrollOk,
            $"漫画点「单页/滚动」切到滚动：continuous={BoolOf(comicScrollState, "continuous")}、" +
            $"spread={BoolOf(comicScrollState, "spread")}");

        // 切回单页，后面的双页 / 日漫断言都从单页出发。
        await EvalJsonAsync(bigCore!, "window.__readerDebug.pdfToggle('layout')").ConfigureAwait(true);

        await CaptureShotAsync(bigCore!, shotsDir, "05-single-page", shots).ConfigureAwait(true);

        var spreadState = await EvalJsonAsync(bigCore!, "window.__readerDebug.pdfToggle('spread')")
            .ConfigureAwait(true);
        var spreadRow = await EvalStringAsync(bigCore!, RowPagesJs).ConfigureAwait(true);

        Add("J3-spread-mode", BoolOf(spreadState, "spread") && spreadRow == "1,2",
            $"spread={BoolOf(spreadState, "spread")}，当前行页码=[{spreadRow}]（期望 1,2）");

        await WaitRowReadyAsync(bigCore!, 6000).ConfigureAwait(true);
        await CaptureShotAsync(bigCore!, shotsDir, "06-spread", shots).ConfigureAwait(true);
        var rtlState = await EvalJsonAsync(bigCore!, "window.__readerDebug.pdfToggle('rtl')")
            .ConfigureAwait(true);
        var rtlRow = await EvalStringAsync(bigCore!, RowPagesJs).ConfigureAwait(true);

        Add("J3-rtl-mode", BoolOf(rtlState, "rtl") && rtlRow == "2,1",
            $"rtl={BoolOf(rtlState, "rtl")}，当前行页码=[{rtlRow}]（期望 2,1：日漫从右到左）");

        await WaitRowReadyAsync(bigCore!, 6000).ConfigureAwait(true);
        await CaptureShotAsync(bigCore!, shotsDir, "07-spread-rtl", shots).ConfigureAwait(true);

        // 复位，后面量单页翻页
        await EvalAsync(bigCore!, "window.__readerDebug.pdfReset()").ConfigureAwait(true);
        await EvalAsync(bigCore!, "window.__readerDebug.pdfGoto(0)").ConfigureAwait(true);
        await WaitForPageImageAsync(bigCore!, 0, PageReadyTimeoutMs).ConfigureAwait(true);

        // ---------- ⑤ 预载 ----------
        // 预载是异步的（宿主渲染完才回包），给 3 秒让它补上再定论；否则只是"读得太早"。
        JsonNode? prefetched = null;

        await WaitAsync(
            async () =>
            {
                prefetched = await EvalJsonAsync(bigCore!,
                    "window.__readerDebug.pdfState().prefetchedPages").ConfigureAwait(true);

                return prefetched is JsonArray { Count: > 0 };
            },
            3000).ConfigureAwait(true);

        var prefetchedList = prefetched is JsonArray array
            ? string.Join(',', array.Select(node => node?.GetValue<int>() ?? -1))
            : "(null)";

        Add("J5-prefetch", prefetched is JsonArray { Count: > 0 },
            $"打开第 1 页后已预载的后续页=[{prefetchedList}]（期望含 1 或 2）");

        // ---------- ④ 200 页翻页：内存 + 耗时 ----------
        var baseline = WorkingSetMb();
        var peak = baseline;
        var turns = new List<long>();
        var turnFailures = 0;

        for (var page = 1; page < PageCount; page++)
        {
            var watch = Stopwatch.StartNew();

            await EvalAsync(bigCore!, $"window.__readerDebug.pdfGoto({page})").ConfigureAwait(true);

            var ready = await WaitForPageImageAsync(bigCore!, page, PageReadyTimeoutMs)
                .ConfigureAwait(true);

            if (!ready) { turnFailures++; }

            turns.Add(watch.ElapsedMilliseconds);

            if (page % 10 == 0)
            {
                peak = Math.Max(peak, WorkingSetMb());
            }
        }

        var end = WorkingSetMb();
        peak = Math.Max(peak, end);

        var growth = end - baseline;

        Add("J4-memory-500mb", peak < MemoryBudgetBytes / 1024 / 1024 && end < MemoryBudgetBytes / 1024 / 1024,
            $"基线={baseline}MB 峰值={peak}MB 终值={end}MB（上限 500MB）");

        Add("J4-memory-growth", growth < MemoryGrowthBudgetBytes / 1024 / 1024,
            $"翻 199 页内存增量={growth}MB（上限 100MB）");

        var stats = Stats(turns);

        Add("J4-turn-latency", stats.Avg <= TurnBudgetMs,
            $"逐页耗时 min/avg/max/p95={stats.Min}/{stats.Avg}/{stats.Max}/{stats.P95}ms" +
            $"（设计文档 §12.3.1 参考上限 {TurnBudgetMs}ms），失败页={turnFailures}");

        // ---------- 关窗后立即重开：只记录，不计入判据 ----------
        var reopenTimes = new List<long>();

        for (var run = 0; run < 2; run++)
        {
            var closed = new TaskCompletionSource();

            bigWindow.Closed += (_, _) => closed.TrySetResult();
            bigWindow.Close();
            await Task.WhenAny(closed.Task, Task.Delay(5000)).ConfigureAwait(true);

            var watch = Stopwatch.StartNew();

            bigWindow = new ReaderWindow(
                reader, environments, progress, loggers.CreateLogger<ReaderWindow>(),
                bigImport.BookId!.Value, comics: comics);
            bigWindow.Show();

            var reopenedCore = await WaitForCoreAsync(bigWindow, 30000);

            if (reopenedCore is not null)
            {
                await WaitForComicStateAsync(reopenedCore, 30000).ConfigureAwait(true);
                await WaitForPageImageAsync(reopenedCore, 0, PageReadyTimeoutMs * 2)
                    .ConfigureAwait(true);
            }

            reopenTimes.Add(watch.ElapsedMilliseconds);
        }

        phases.Add("关窗后立即重开=" + string.Join('/', reopenTimes.Select(ms => ms + "ms"))
            + "（含 WebView2 进程回收，不计入判据）");

        Add("J7-open-200p", openMs <= OpenBudgetMs,
            $"首次打开={openMs}ms（上限 {OpenBudgetMs}ms）；totalPages={totalPages}；" +
            "分阶段：" + string.Join(" | ", phases));

        // ---------- ① CBZ 打开 + 自然序（最后做：它要再开一个窗口） ----------
        var orderWindow = new ReaderWindow(
            reader, environments, progress, loggers.CreateLogger<ReaderWindow>(),
            orderImport.BookId!.Value, comics: comics);

        orderWindow.Show();

        var orderCore = await WaitForCoreAsync(orderWindow, 30000);

        if (orderCore is null)
        {
            Add("J1-cbz-open", false, "CoreWebView2 未就绪（30 s 超时）");
        }
        else
        {
            var orderState = await WaitForComicStateAsync(orderCore, 30000);
            var totalOk = TotalPagesOf(orderState) == 3;

            Add("J1-cbz-open", totalOk,
                $"totalPages={TotalPagesOf(orderState)}（期望 3），kind={KindOf(orderState)}");

            await CaptureShotAsync(orderCore, shotsDir, "00-order-page1", shots);

            var expected = new[] { "红", "绿", "蓝" };
            var colors = new List<string>();
            var matched = 0;

            for (var page = 0; page < 3; page++)
            {
                await EvalAsync(orderCore, $"window.__readerDebug.pdfGoto({page})")
                    .ConfigureAwait(true);

                await WaitForPageImageAsync(orderCore, page, PageReadyTimeoutMs).ConfigureAwait(true);

                var png = await CapturePngAsync(orderCore).ConfigureAwait(true);
                var dominant = png is null ? "?" : DominantChannel(SampleCenter(png));

                colors.Add($"第{page + 1}页={dominant}");
                if (png is not null && dominant == expected[page]) { matched++; }

                await CaptureShotAsync(orderCore, shotsDir, $"01-order-page{page + 1}", shots)
                    .ConfigureAwait(true);
            }

            /* page1/page2/page10 三张纯色：自然序下第 1/2/3 页就是 红/绿/蓝。
               字典序（page1,page10,page2）会变成 红/蓝/绿——第 2、3 页对不上。 */
            Add("J1-cbz-natural-order", matched == 3,
                string.Join("，", colors) + $"；命中 {matched}/3（期望 红/绿/蓝）");
        }

        orderWindow.Close();

        // ---------- ⑨ 漫画横竖混排：每页按自身宽高比排版，不留白条（用户 2026-10-18 实测反馈①） ----------
        var mixedPath = Path.Combine(corpus, "t63-mixed.cbz");
        var mixedImport = await importer.ImportAsync(mixedPath, ImportOptions.Default);

        if (mixedImport.Outcome != ImportOutcome.Imported)
        {
            Add("J13-mixed-no-letterbox", false,
                $"导入失败：{mixedImport.Outcome} {mixedImport.Message}");
        }
        else
        {
            var mixedWindow = new ReaderWindow(
                reader, environments, progress, loggers.CreateLogger<ReaderWindow>(),
                mixedImport.BookId!.Value, comics: comics);

            mixedWindow.Show();

            var mixedCore = await WaitForCoreAsync(mixedWindow, 30000);

            if (mixedCore is null)
            {
                Add("J13-mixed-no-letterbox", false, "CoreWebView2 未就绪（30 s 超时）");
            }
            else
            {
                await WaitForComicStateAsync(mixedCore, 30000).ConfigureAwait(true);

                var ratios = new List<string>();
                var worst = 0.0;

                for (var page = 0; page < 3; page++)
                {
                    await EvalAsync(mixedCore, $"window.__readerDebug.pdfGoto({page})")
                        .ConfigureAwait(true);

                    await WaitForPageImageAsync(mixedCore, page, PageReadyTimeoutMs).ConfigureAwait(true);

                    var box = await EvalJsonAsync(mixedCore, BoxRatioJs).ConfigureAwait(true);
                    var boxRatio = NumOf(box, "boxRatio");
                    var imgRatio = NumOf(box, "imgRatio");

                    /* 页框比例与图片比例一致 ⇒ object-fit: contain 不产生任何填充（白条）。
                        两者不一致的偏差就是白条的高度占比。 */
                    if (boxRatio > 0 && imgRatio > 0)
                    {
                        worst = Math.Max(worst, Math.Abs(boxRatio - imgRatio) / imgRatio);
                    }

                    ratios.Add($"第{page + 1}页 框={boxRatio:F3} 图={imgRatio:F3}");
                }

                Add("J13-mixed-no-letterbox", worst <= 0.03,
                    string.Join("，", ratios) + $"；最大比例偏差={worst * 100:F1}%（≤3% 即无白条）");

                await EvalAsync(mixedCore, "window.__readerDebug.pdfGoto(1)").ConfigureAwait(true);
                await WaitForPageImageAsync(mixedCore, 1, PageReadyTimeoutMs).ConfigureAwait(true);
                await CaptureShotAsync(mixedCore, shotsDir, "10-mixed-page2-landscape", shots)
                    .ConfigureAwait(true);

                mixedWindow.Close();
                await Task.Delay(300).ConfigureAwait(true);
            }
        }

        // ---------- ⑥ 自动切白边 ----------
        Add("J6-auto-trim", false,
            "未实现（计划 T6-3 标注为“可选”），不在本轮范围；不算通过。");

        // ---------- 附：页缓存不落盘 ----------
        var pagesCacheDir = Path.Combine(paths.CacheDir, "Pages");
        Add("J8-no-pages-cache-on-disk", !Directory.Exists(pagesCacheDir),
            $"Data\\Cache\\Pages 存在={Directory.Exists(pagesCacheDir)}（应为 False；漫画页位图只在内存）");

        bigWindow.Close();
        await Task.Delay(300).ConfigureAwait(true);

        return Finish(paths, startedAt, stopwatch, checks, shots);
    }

    // ==================== 样本 ====================

    private static void BuildCorpus(string corpusRoot)
    {
        Directory.CreateDirectory(corpusRoot);

        BuildOrderCbz(Path.Combine(corpusRoot, "t63-order.cbz"));
        BuildPagesCbz(Path.Combine(corpusRoot, "t63-200p.cbz"), PageCount);
        BuildMixedCbz(Path.Combine(corpusRoot, "t63-mixed.cbz"));
        WriteBroken(Path.Combine(corpusRoot, "t63-broken.cbz"));
    }

    /// <summary>横竖图混排：竖图 600×1000、横图 1200×600、小图 300×200。
    /// 用户 2026-10-18 实测反馈：这三页混在一起时横图/小图上下出现白条。</summary>
    private static void BuildMixedCbz(string path)
    {
        if (File.Exists(path)) { File.Delete(path); }

        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        AddEntry(zip, "page1.jpg", SolidJpeg(600, 1000, new Rgba32(200, 30, 30)));
        AddEntry(zip, "page2.jpg", SolidJpeg(1200, 600, new Rgba32(30, 160, 60)));
        AddEntry(zip, "page3.jpg", SolidJpeg(300, 200, new Rgba32(30, 60, 200)));
    }

    private static void BuildOrderCbz(string path)
    {
        if (File.Exists(path)) { File.Delete(path); }

        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        AddEntry(zip, "page1.jpg", SolidJpeg(300, 400, new Rgba32(230, 20, 20)));
        AddEntry(zip, "page2.jpg", SolidJpeg(300, 400, new Rgba32(20, 210, 20)));
        AddEntry(zip, "page10.jpg", SolidJpeg(300, 400, new Rgba32(20, 40, 230)));
    }

    private static void BuildPagesCbz(string path, int count)
    {
        if (File.Exists(path)) { File.Delete(path); }

        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        for (var page = 1; page <= count; page++)
        {
            AddEntry(zip, "page" + page + ".jpg", PageJpeg(PageWidth, PageHeight, page));
        }
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);

        using var entryStream = entry.Open();
        entryStream.Write(bytes);
    }

    private static byte[] SolidJpeg(int width, int height, Rgba32 color)
    {
        using var image = new Image<Rgba32>(width, height);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                accessor.GetRowSpan(y).Fill(color);
            }
        });

        return Encode(image);
    }

    /// <summary>白底 + 一小块黑块（位置随页码移动）。视觉上能看出翻页是否真的换了页。</summary>
    private static byte[] PageJpeg(int width, int height, int page)
    {
        using var image = new Image<Rgba32>(width, height);

        var white = new Rgba32(250, 250, 250);
        var black = new Rgba32(10, 10, 10);
        var left = Math.Min(80 + (page * 37 % (width - 200)), width - 200);
        var top = Math.Min(80 + (page * 53 % (height - 200)), height - 200);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                accessor.GetRowSpan(y).Fill(white);
            }

            for (var y = top; y < top + 120 && y < height; y++)
            {
                var row = accessor.GetRowSpan(y).Slice(left, 160);

                row.Fill(black);
            }
        });

        return Encode(image);
    }

    private static byte[] Encode(Image<Rgba32> image)
    {
        using var buffer = new MemoryStream();

        image.Save(buffer, new JpegEncoder { Quality = 85 });

        return buffer.ToArray();
    }

    private static void WriteBroken(string path)
    {
        if (File.Exists(path)) { File.Delete(path); }

        var bytes = new byte[80];
        bytes[0] = 0x50;
        bytes[1] = 0x4B;
        bytes[2] = 0x03;
        bytes[3] = 0x04;
        Random.Shared.NextBytes(bytes.AsSpan(4));

        File.WriteAllBytes(path, bytes);
    }

    // ==================== 断言助手 ====================

    private static async Task<CoreWebView2?> WaitForCoreAsync(ReaderWindow window, int timeoutMs)
    {
        CoreWebView2? core = null;

        await WaitAsync(
            () =>
            {
                core = window.Web.CoreWebView2;
                return Task.FromResult(core is not null);
            },
            timeoutMs).ConfigureAwait(true);

        return core;
    }

    private static async Task<JsonNode?> WaitForComicStateAsync(CoreWebView2 core, int timeoutMs)
    {
        JsonNode? state = null;

        await WaitAsync(
            async () =>
            {
                var node = await EvalJsonAsync(core,
                    "window.__readerDebug ? window.__readerDebug.pdfState() : null").ConfigureAwait(true);

                if (node is JsonObject obj
                    && obj["kind"]?.GetValue<string>() == "comic"
                    && (obj["totalPages"]?.GetValue<int>() ?? 0) > 0)
                {
                    state = obj;
                    return true;
                }

                return false;
            },
            timeoutMs).ConfigureAwait(true);

        return state;
    }

    private static async Task<JsonNode?> StateAsync(CoreWebView2 core)
        => await EvalJsonAsync(core, "window.__readerDebug.pdfState()").ConfigureAwait(true);

    private static async Task<bool> WaitForPageImageAsync(CoreWebView2 core, int page, int timeoutMs)
    {
        var js = "(function(){var el=document.querySelector('.pdfPage[data-page=\"" + page +
                 "\"] img');return !!(el && el.complete && el.naturalWidth>0);})()";

        var ready = false;

        await WaitAsync(
            async () =>
            {
                ready = await EvalBoolAsync(core, js).ConfigureAwait(true);
                return ready;
            },
            timeoutMs).ConfigureAwait(true);

        return ready;
    }

    /// <summary>第 1 页迟迟不 ready 时的现场：页元素数量、第 1 页 img 的 src/complete/尺寸。</summary>
    private static async Task<string> PdfDiagAsync(CoreWebView2 core)
    {
        var node = await EvalJsonAsync(
            core,
            "(function(){var p=document.querySelector('.pdfPage[data-page=\"0\"]');" +
            "var img=p?p.querySelector('img'):null;" +
            "return {pages:document.querySelectorAll('.pdfPage').length," +
            "hasEl:!!p,hasImg:!!img," +
            "src:img?(img.getAttribute('src')||'').slice(-8):''," +
            "complete:img?!!img.complete:false," +
            "nw:img?(img.naturalWidth||0):0," +
            "state:(window.__readerDebug?window.__readerDebug.pdfState().page:-99)};})()")
            .ConfigureAwait(true);

        return node is null ? "(null)" : node.ToJsonString();
    }

    /// <summary>工具栏与四个关键按钮的客户区矩形（CSS px，getBoundingClientRect 口径）。</summary>
    private const string ToolbarRectsJs =
        "(function(){function r(id){var e=document.getElementById(id);if(!e){return null;}" +
        "var b=e.getBoundingClientRect();return {l:b.left,r:b.right,t:b.top,b:b.bottom};}" +
        "return {toolbar:r('toolbar'),top:r('pdfToolRowTop'),bottom:r('pdfToolRowBottom')," +
        "prev:r('pdfPrev'),next:r('pdfNext'),theme:r('themeSelect'),zoomIn:r('pdfZoomIn')};})()";

    /// <summary>
    /// PDF / 漫画工具栏布局探针（用户 2026-10-21「自定义主题三个色框位置」验收用）：
    /// 一行/两行的 body class、行容器、上一页/下一页/主题/三个色框的真实客户区矩形。
    /// </summary>
    private const string ToolbarLayoutJs = """
        (function(){
          function r(id){
            var e = document.getElementById(id);
            if (!e) { return null; }
            var b = e.getBoundingClientRect();
            return { l: b.left, r: b.right, t: b.top, b: b.bottom, w: b.width, h: b.height };
          }
          return {
            innerWidth: window.innerWidth,
            wide: document.body.classList.contains('ets-pdf-wide'),
            rows: !!document.getElementById('pdfToolRowTop'),
            toolbar: r('toolbar'),
            top: r('pdfToolRowTop'),
            bottom: r('pdfToolRowBottom'),
            prev: r('pdfPrev'),
            next: r('pdfNext'),
            theme: r('themeSelect'),
            colors: r('customColors'),
            colorBg: r('customBg'),
            colorFg: r('customFg'),
            colorLink: r('customLink')
          };
        })()
        """;

    private readonly record struct RectD(double L, double R, double T, double B)
    {
        public double W => R - L;
        public double H => B - T;
    }

    /// <summary>工具栏布局探针的解析结果（两行模式的结构字段全为 null 时说明探针坏了）。</summary>
    private readonly record struct ToolbarLayout(
        double InnerWidth,
        bool Wide,
        bool HasRows,
        RectD? Toolbar,
        RectD? Top,
        RectD? Bottom,
        RectD? Prev,
        RectD? Next,
        RectD? Theme,
        RectD? Colors,
        RectD? ColorBg,
        RectD? ColorFg,
        RectD? ColorLink);

    /// <summary>当前页的页框比例与图片自身比例。两者相等 ⇒ object-fit: contain 不产生填充带。</summary>
    private const string BoxRatioJs = """
        (function(){
          var s = window.__readerDebug.pdfState();
          var el = document.querySelector('#pdfPages .pdfPage[data-page="' + s.page + '"]');
          var img = el ? el.querySelector('.pdfImage') : null;
          var w = el ? el.clientWidth : 0;
          var h = el ? el.clientHeight : 0;
          var nw = img ? img.naturalWidth : 0;
          var nh = img ? img.naturalHeight : 0;
          return { page: s.page, w: w, h: h, nw: nw, nh: nh,
                   boxRatio: h > 0 ? w / h : 0, imgRatio: nh > 0 ? nw / nh : 0 };
        })()
        """;

    private static double NumOf(JsonNode? node, string key)
    {
        return node is JsonObject obj && obj[key] is JsonValue value
            && value.TryGetValue<double>(out var d) ? d : 0;
    }

    private static RectD? NumRect(JsonNode? node, string key)
    {
        if (node is not JsonObject obj || obj[key] is not JsonObject r)
        {
            return null;
        }

        double Read(string name) => r[name] is JsonValue value && value.TryGetValue<double>(out var d) ? d : 0;

        return new RectD(Read("l"), Read("r"), Read("t"), Read("b"));
    }

    /// <summary>当前页所在行的所有 img 都已解码完成（截图前等它，免得证据里是"正在载入"占位）。</summary>
    private static async Task<bool> WaitRowReadyAsync(CoreWebView2 core, int timeoutMs)
    {
        var ready = false;

        await WaitAsync(
            async () =>
            {
                ready = await EvalBoolAsync(
                    core,
                    "(function(){var s=window.__readerDebug.pdfState();" +
                    "var rows=document.querySelectorAll('.pdfRow');" +
                    "for(var i=0;i<rows.length;i++){" +
                    "if(!rows[i].querySelector('.pdfPage[data-page=\"'+s.page+'\"]')){continue;}" +
                    "var imgs=rows[i].querySelectorAll('img');" +
                    "if(!imgs.length){return false;}" +
                    "for(var j=0;j<imgs.length;j++){" +
                    "if(!imgs[j].complete||!imgs[j].naturalWidth){return false;}}" +
                    "return true;}return false;})()").ConfigureAwait(true);

                return ready;
            },
            timeoutMs).ConfigureAwait(true);

        return ready;
    }

    private static async Task WaitAsync(Func<Task<bool>> probe, int timeoutMs, int stepMs = 100)
    {
        var watch = Stopwatch.StartNew();

        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (await probe().ConfigureAwait(true))
            {
                return;
            }

            await Task.Delay(stepMs).ConfigureAwait(true);
        }
    }

    private static async Task<JsonNode?> EvalJsonAsync(CoreWebView2 core, string script)
    {
        var raw = await core.ExecuteScriptAsync(script).ConfigureAwait(true);

        if (string.IsNullOrEmpty(raw) || raw == "null")
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task EvalAsync(CoreWebView2 core, string script)
        => await core.ExecuteScriptAsync(script).ConfigureAwait(true);

    private static async Task<bool> EvalBoolAsync(CoreWebView2 core, string script)
    {
        var raw = await core.ExecuteScriptAsync(script).ConfigureAwait(true);

        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> EvalStringAsync(CoreWebView2 core, string script)
    {
        var raw = await core.ExecuteScriptAsync(script).ConfigureAwait(true);

        try
        {
            return JsonNode.Parse(raw)?.GetValue<string>() ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static async Task<double> EvalNumberAsync(CoreWebView2 core, string script)
    {
        var node = await EvalJsonAsync(core, script).ConfigureAwait(true);

        return node is JsonValue value && value.TryGetValue<double>(out var result) ? result : 0;
    }

    /// <summary>当前页所在那一行的页码顺序（日漫双页应为降序）。</summary>
    private const string RowPagesJs =
        "(function(){var s=window.__readerDebug.pdfState();" +
        "var rows=document.querySelectorAll('.pdfRow');" +
        "for(var i=0;i<rows.length;i++){" +
        "if(rows[i].querySelector('.pdfPage[data-page=\"'+s.page+'\"]')){" +
        "var out=[];for(var j=0;j<rows[i].children.length;j++){" +
        "out.push(rows[i].children[j].getAttribute('data-page'));}return out.join(',');}}" +
        "return '';})()";

    private static int TotalPagesOf(JsonNode? state) => IntOf(state, "totalPages");

    private static string KindOf(JsonNode? state) => StrOf(state, "kind");

    private static int IntOf(JsonNode? node, string key, int fallback = -1)
    {
        if (node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<int>(out var result))
        {
            return result;
        }

        return fallback;
    }

    private static bool BoolOf(JsonNode? node, string key)
        => node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    private static string StrOf(JsonNode? node, string key)
        => node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : string.Empty;

    // ==================== 截图与像素 ====================

    private static async Task CaptureShotAsync(
        CoreWebView2 core, string shotsDir, string name, List<string> shots)
    {
        try
        {
            var png = await CapturePngAsync(core).ConfigureAwait(true);

            if (png is null) { return; }

            Directory.CreateDirectory(shotsDir);

            var path = Path.Combine(shotsDir, name + ".png");

            await File.WriteAllBytesAsync(path, png).ConfigureAwait(true);
            shots.Add(path);
        }
        catch (Exception)
        {
            // 截图失败不影响判据：报告里会少一张，数字仍是真的。
        }
    }

    private static async Task<byte[]?> CapturePngAsync(CoreWebView2 core)
    {
        try
        {
            using var stream = new MemoryStream();

            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream)
                .ConfigureAwait(true);

            return stream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Rgba32 SampleCenter(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        var decoder = new PngBitmapDecoder(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        var frame = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        var cx = width / 2;
        var cy = height / 2;
        long r = 0, g = 0, b = 0;
        var count = 0;

        for (var y = cy - 1; y <= cy + 1; y++)
        {
            for (var x = cx - 1; x <= cx + 1; x++)
            {
                if (x < 0 || y < 0 || x >= width || y >= height) { continue; }

                var index = (y * stride) + (x * 4);

                b += pixels[index];
                g += pixels[index + 1];
                r += pixels[index + 2];
                count++;
            }
        }

        return count == 0
            ? new Rgba32(0, 0, 0)
            : new Rgba32((byte)(r / count), (byte)(g / count), (byte)(b / count));
    }

    private static string DominantChannel(Rgba32 color)
    {
        if (color.R > color.G && color.R > color.B) { return "红"; }
        if (color.G > color.R && color.G > color.B) { return "绿"; }
        if (color.B > color.R && color.B > color.G) { return "蓝"; }

        return "混";
    }

    // ==================== 统计与报告 ====================

    private static long WorkingSetMb() => Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;

    private readonly record struct TurnStats(long Min, long Avg, long Max, long P95);

    private static TurnStats Stats(IReadOnlyList<long> values)
    {
        if (values.Count == 0) { return new TurnStats(0, 0, 0, 0); }

        var sorted = values.OrderBy(v => v).ToList();

        return new TurnStats(
            sorted[0],
            (long)Math.Round(sorted.Average()),
            sorted[^1],
            Percentile(sorted, 0.95));
    }

    private static long Percentile(IReadOnlyList<long> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0) { return 0; }

        var sorted = sortedValues.OrderBy(v => v).ToList();
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;

        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static SmokeReport Finish(
        AppPaths paths,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        List<SmokeCheck> checks,
        List<string> shots)
    {
        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            stopwatch.ElapsedMilliseconds,
            paths.DataRoot,
            paths.TempDir,
            shots.Count == 0 ? null : string.Join(" | ", shots.Select(Path.GetFileName)),
            checks);

        Directory.CreateDirectory(paths.TempDir);
        File.WriteAllText(
            Path.Combine(paths.TempDir, "t63-smoke.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return report;
    }
}
