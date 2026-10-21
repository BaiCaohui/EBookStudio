using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Reader.WebView;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T6-2B「PDF 自绘引擎」的端到端验收（判据 ①~⑥）。
///
/// 与 T6-2 的区别：正文不再交给 Chromium 内建查看器（没有 iframe、没有插件），
/// 页面由宿主用 pdfium 渲染成 JPEG 走内存虚拟主机送进来，排版 / 旋转 / 双页 /
/// 目录 / 查找全在 reader.js 里自己做。所以这里的断言不再看"插件里发生了什么"
/// ——那已经看不见了——而是看**我们自己的 DOM 与像素**：
///   · 判据①：pdfState().greyed 四项可用 + 真用一遍（旋转看宽高互换、双页看互斥、目录看 56 条）；
///   · 判据②：连续模式下逐页量"pdfGoto → .pdfImage complete && naturalWidth>0"；
///   · 判据③：114.8 MB 那本翻 100 页，量宿主进程 WorkingSet；
///   · 判据⑥：Data/Cache/Pages 不存在（用全新 datadir 跑才有意义）。
///
/// 判据④（全库搜索命中）与判据⑤（书签笔记位置）已由用户决定转交 T6-4 / T6-5，
/// 这里**如实记未验收**（ok=false），不假装通过。
/// </summary>
public static class T62BSmokeTest
{
    /// <summary>判据②的稳态门槛：连续模式下平均每页 ≤ 300 ms。</summary>
    private const int SteadyBudgetMs = 300;

    /// <summary>判据③的内存门槛。</summary>
    private const long MemoryBudgetBytes = 500L * 1024 * 1024;

    /// <summary>判据③的增量门槛：翻完 100 页相对"刚打开"不应涨超过它。</summary>
    private const long MemoryGrowthBudgetBytes = 100L * 1024 * 1024;

    /// <summary>判据②的样本数：0..29 共 30 页。</summary>
    private const int PageTurnSamples = 30;

    /// <summary>判据③翻的页数。</summary>
    private const int MemoryTurnCount = 100;

    /// <summary>页面区域"算出墨迹"的阈值（与 PngStats 同一套 luma &lt; 200 口径）。</summary>
    private const double RegionInkThreshold = 0.005;

    /// <summary>单页等待上限：一页渲染 + 传输超过它就只能算失败。</summary>
    private const int PageTimeoutMs = 25000;

    public static async Task<SmokeReport> RunAsync(
        IBookImportService importer,
        IReaderService reader,
        IBookRepository repo,
        IWebViewEnvironmentProvider environments,
        IReadingProgressService progress,
        IPdfDocumentService pdfPages,
        ILoggerFactory loggerFactory,
        AppPaths paths,
        string corpusDir)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();
        var shots = new List<string>();
        var shotsDir = Path.Combine(paths.TempDir, "t62b-shots");

        var ctx = new Ctx
        {
            Importer = importer,
            Reader = reader,
            Repo = repo,
            Environments = environments,
            Progress = progress,
            PdfPages = pdfPages,
            Loggers = loggerFactory,
            Paths = paths,
            Checks = checks,
            Shots = shots,
            ShotsDir = shotsDir,
        };

        // ---------- 0. 样本 ----------
        var pdfs = Directory.Exists(corpusDir)
            ? Directory.GetFiles(corpusDir, "*.pdf").OrderBy(f => new FileInfo(f).Length).ToList()
            : new List<string>();

        var moe = pdfs.FirstOrDefault(f => Path.GetFileName(f).Contains("萌版", StringComparison.Ordinal));
        var text200 = pdfs.FirstOrDefault(f => Path.GetFileName(f).Contains("200p", StringComparison.OrdinalIgnoreCase));
        var scan = pdfs.FirstOrDefault(f => Path.GetFileName(f).Contains("sample-pdf-2", StringComparison.Ordinal));
        var big = pdfs.Count > 0 ? pdfs[^1] : null;

        var corpusOk = moe is not null && text200 is not null && scan is not null && big is not null;

        ctx.Add("setup-corpus", corpusOk,
            corpusOk
                ? $"萌版(判据①)={Name(moe)} {Mb(moe)} | 200页文字版(判据②)={Name(text200)} {Mb(text200)} | " +
                  $"扫描件(不反色)={Name(scan)} {Mb(scan)} | 大文件(判据③)={Name(big)} {Mb(big)}"
                : $"样本不全：目录={corpusDir}，找到 {pdfs.Count} 个 PDF；" +
                  $"萌版={(moe is null ? "缺" : "有")} 200p={(text200 is null ? "缺" : "有")} " +
                  $"sample-pdf-2={(scan is null ? "缺" : "有")} 最大={Name(big)}");

        if (!corpusOk)
        {
            return Finish(paths, startedAt, stopwatch, checks, shots);
        }

        try
        {
            await JudgeFaceAsync(ctx, moe!).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ctx.Add("J1-exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            await JudgeTurnAsync(ctx, text200!).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ctx.Add("J2-exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            await JudgeMemoryAsync(ctx, big!).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ctx.Add("J3-exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            await JudgeScanThemeAsync(ctx, scan!, moe!).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ctx.Add("X3-exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        // ---------- 判据⑥：Data/Cache/Pages 仍不存在 ----------
        var pagesDir = paths.PagesCacheDir;
        var pagesExists = Directory.Exists(pagesDir);
        var pagesFiles = pagesExists
            ? Directory.GetFiles(pagesDir, "*", SearchOption.AllDirectories).Length
            : 0;

        ctx.Add("J6-no-pages-cache-dir", !pagesExists,
            $"路径={pagesDir}；存在={pagesExists}；文件数={pagesFiles}" +
            (pagesExists
                ? "。判据⑥要求这个目录**仍不存在**（页位图只在内存里），请查是哪段代码建了它。"
                : "。页位图只在内存虚拟主机里，磁盘上没有页缓存。"));

        // ---------- 判据④ / ⑤：用户已决定转交，如实记"未验收" ----------
        ctx.Add("J4-deferred-cross-library-search", false,
            "**未验收**：已由用户决定转交 T6-4（PDF 正文的全库搜索 / 命中跳转 / 高亮）。本次不报 OK。");
        ctx.Add("J5-deferred-bookmark-position", false,
            "**未验收**：已由用户决定转交 T6-5（PDF 书签笔记重启后位置准确）。本次不报 OK。");

        return Finish(paths, startedAt, stopwatch, checks, shots);
    }

    // ==================== 判据①：四个按钮 + 界面与 T6-2 一致 ====================

    private static async Task JudgeFaceAsync(Ctx c, string moeFile)
    {
        var imported = await c.Importer.ImportAsync(moeFile, ImportOptions.Default, null).ConfigureAwait(true);

        c.Add("J1-import-moe", imported.Outcome is ImportOutcome.Imported or ImportOutcome.Duplicate,
            $"{Name(moeFile)} -> {imported.Outcome}（{imported.Message}）");

        if (imported.BookId is not { } bookId)
        {
            return;
        }

        /* 目录条数的"事实来源"是 pdfium（宿主打开时日志打的就是它）：
           DOM 上的 .tocItem 数量必须与它一致，而不能只是"看起来有目录"。 */
        var outline = c.PdfPages.ReadOutline(moeFile);
        var jumpableOutline = outline.Count(e => e.PageIndex >= 0);

        c.Add("J1-outline-source", outline.Count > 0,
            $"pdfium 读到 {outline.Count} 条目录（其中可跳转 {jumpableOutline} 条，L1+L2 都拍平在里面）");

        var recorder = new RecordingShellLauncher();
        ReaderWindow? window = null;

        try
        {
            window = c.NewWindow(bookId, recorder);

            var clock = Stopwatch.StartNew();
            window.Show();

            var core = await WaitForCoreAsync(window, 25000).ConfigureAwait(true);

            if (core is null)
            {
                c.Add("J1-pdf-mode", false, "WebView2 25 秒内没就绪");
                return;
            }

            /* 从"内核就绪"就开始记网络：页位图走内存虚拟主机（http://ebook-pdf.local/p/…），
               一旦它没被宿主拦下来，请求就会打到网络上并以超时收场——那条线索只有 CDP 看得见。 */
            var netLog = new List<string>();

            await StartNetworkWatchAsync(core, netLog).ConfigureAwait(true);

            var state = await WaitForPdfStateAsync(core, 30000).ConfigureAwait(true);

            if (state is null)
            {
                c.Add("J1-pdf-mode", false, "30 秒内没进入 PDF 模式（pdfState 读不到 kind=pdf）");
                return;
            }

            var totalPages = IntOf(state, "totalPages");
            var openMs = clock.ElapsedMilliseconds;

            c.Add("J1-pdf-mode", totalPages == 57,
                $"进入 PDF 模式用了 {openMs} ms；totalPages={totalPages}（期望 57）；" +
                $"bodyHasPdfClass={BoolOf(state, "bodyHasPdfClass")}；theme={StrOf(state, "theme")}");

            // ---- 四个按钮：从置灰变可用 ----
            var greyedOk = true;
            var greyedDetail = new StringBuilder();

            foreach (var item in state["greyed"]?.AsArray() ?? new JsonArray())
            {
                var disabled = item?["disabled"]?.GetValue<bool>() ?? true;
                var id = item?["id"]?.GetValue<string>() ?? "?";

                greyedOk &= !disabled;
                greyedDetail.Append($"{id}:disabled={disabled} ");
            }

            c.Add("J1-buttons-enabled", greyedOk,
                "旋转 / 单页-连续 / 单页-双页 / 目录 四项应全部可用：" + greyedDetail.ToString().Trim());

            // ---- 界面与 T6-2 完全一致：同一套工具栏 / 目录抽屉 / 查找栏 / 状态条 ----
            var dom = await EvalAsync(core, DomIdentityJs).ConfigureAwait(true);
            var missing = StrOf(dom, "missing");
            var missingBar = StrOf(dom, "missingBar");
            var sameUi = missing.Length == 0
                && missingBar.Length == 0
                && IntOf(dom, "toolbarCount") == 1
                && IntOf(dom, "boardCount") == 1
                && IntOf(dom, "barCount") == 1
                && BoolOf(dom, "pdfBarInsideToolbar")
                && BoolOf(dom, "pagesInsideViewport");

            c.Add("J1-ui-same-as-t62", sameUi,
                $"PDF 模式下缺的元素=[{missing}]；#pdfBar 缺的 id=[{missingBar}]（共 14 个）；" +
                $"#toolbar×{IntOf(dom, "toolbarCount")} #tocPanel×{IntOf(dom, "boardCount")} #pdfBar×{IntOf(dom, "barCount")}；" +
                $"#pdfBar 在 #toolbar 内={BoolOf(dom, "pdfBarInsideToolbar")}；" +
                $"#pdfPages 在 #viewport 内={BoolOf(dom, "pagesInsideViewport")}；body.class={StrOf(dom, "bodyClass")}" +
                "（不是 PDF 专用副本：同一批元素，只是多挂了一个 pdfBar）");

            /* 用户 2026-10-19 实测要求：PDF/漫画工具栏第一行 = 上一页 目录 书签 笔记
               （页码 · 总页数 · − · +）下一页；第二行 = 适应页面/适应宽度/旋转/模式/预载/全屏/外部打开 … 主题。
               原先「正在打开」那一块标题整块去掉，位置让给页码与加减按钮。 */
            const string toolbarRowsJs = """
                (function(){
                  var g = function(id){ return document.getElementById(id); };
                  var r = function(el){ return el ? el.getBoundingClientRect() : null; };
                  var prev = r(g('pdfPrev')), input = r(g('pdfPageInput')),
                      zoomIn = r(g('pdfZoomIn')), next = r(g('pdfNext')),
                      fitPage = r(g('pdfFitPage')), theme = r(g('themeSelect')),
                      heading = g('heading');
                  var same = function(a, b){ return !!a && !!b && Math.abs(a.top - b.top) <= 8; };
                  return {
                    headingHidden: heading ? window.getComputedStyle(heading).display === 'none' : false,
                    inputWithPrev: same(input, prev),
                    zoomWithPrev: same(zoomIn, prev),
                    nextWithPrev: same(next, prev),
                    prevLeft: prev ? Math.round(prev.left) : -1,
                    nextRight: next ? Math.round(window.innerWidth - next.right) : -1,
                    fitOnOwnRow: !!fitPage && !!prev && fitPage.top >= prev.bottom - 1,
                    themeWithZoom: same(theme, zoomIn),
                    themeRightOfZoom: !!theme && !!zoomIn && theme.left >= zoomIn.right - 2,
                    themeRight: theme ? Math.round(window.innerWidth - theme.right) : -1,
                    /* 缩放按钮是图标：图标放大了，按钮本身不能变大（用户要求）。 */
                    zoomSameSize: !!zoomIn && !!prev
                      && Math.abs(zoomIn.height - prev.height) <= 2 && zoomIn.width <= 64,
                    wide: document.body.classList.contains('ets-pdf-wide')
                  };
                })()
                """;

            var rows = await EvalAsync(core, toolbarRowsJs).ConfigureAwait(true);
            var prevLeft = IntOf(rows, "prevLeft");
            var nextRight = IntOf(rows, "nextRight");

            c.Add("J1-toolbar-rows",
                BoolOf(rows, "headingHidden") && BoolOf(rows, "inputWithPrev") && BoolOf(rows, "zoomWithPrev")
                && BoolOf(rows, "nextWithPrev")
                && Math.Abs(prevLeft - 12) <= 16 && Math.Abs(nextRight - 12) <= 16
                && BoolOf(rows, "fitOnOwnRow") && BoolOf(rows, "themeWithZoom")
                && BoolOf(rows, "themeRightOfZoom") && BoolOf(rows, "zoomSameSize")
                && !BoolOf(rows, "wide"),
                $"标题块隐藏={BoolOf(rows, "headingHidden")}（原来「正在打开」的位置，现在放页码）；" +
                $"页码框/加减与上一页同行={BoolOf(rows, "inputWithPrev")}/{BoolOf(rows, "zoomWithPrev")}；" +
                $"下一页与上一页同行={BoolOf(rows, "nextWithPrev")}（左 {prevLeft}px / 右 {nextRight}px，容差 16px）；" +
                $"适应页面在新一行={BoolOf(rows, "fitOnOwnRow")}；" +
                $"主题在加号右边且同一行={BoolOf(rows, "themeWithZoom")}/{BoolOf(rows, "themeRightOfZoom")}" +
                $"（主题右边距 {IntOf(rows, "themeRight")}px，固定在加号后，不再靠最右）；" +
                $"缩放图标按钮尺寸未变={BoolOf(rows, "zoomSameSize")}；" +
                $"宽窗口判定 wide={BoolOf(rows, "wide")}（本窗口宽度下应为两行）");

            /* 用户 2026-10-19 要求：宽度足够（全屏 / 大屏）时工具栏合并成**一行**，
               且按钮不许出画面；一行放不放得下由 reader.js 实测高度决定，不写死窗口宽度。
               屏幕够宽才做这条（不够宽时拉大窗口也不会变成一行，那是正确行为）。 */
            var workWidth = SystemParameters.WorkArea.Width;

            if (workWidth >= 1600)
            {
                var originalWidth = window.Width;
                var wideTarget = Math.Min(1900, workWidth - 40);

                window.Width = wideTarget;
                await Task.Delay(800).ConfigureAwait(true);

                var wideState = await EvalAsync(core, "window.__readerDebug.pdfState()").ConfigureAwait(true);
                var wideMetrics = await EvalAsync(
                    core,
                    "(function(){var b=document.getElementById('toolbar');return {sw:b.scrollWidth,cw:b.clientWidth,ow:window.innerWidth};})()")
                    .ConfigureAwait(true);

                await RecordShotAsync(c, core, "01b-toolbar-one-row").ConfigureAwait(true);

                window.Width = originalWidth;
                await Task.Delay(800).ConfigureAwait(true);

                var backState = await EvalAsync(core, "window.__readerDebug.pdfState()").ConfigureAwait(true);

                c.Add("J1-toolbar-wide-one-row",
                    BoolOf(wideState, "toolbarWide") && !BoolOf(backState, "toolbarWide"),
                    $"窗口拉到 {wideTarget:0}px：合并成一行={BoolOf(wideState, "toolbarWide")}" +
                    $"（工具栏内容宽 {IntOf(wideMetrics, "sw")}px / 可用宽 {IntOf(wideMetrics, "cw")}px，" +
                    $"内宽 {IntOf(wideMetrics, "ow")}px；按钮高 {IntOf(wideState, "toolbarOneRowHeight")}px，" +
                    $"合并后工具栏高 {IntOf(wideState, "toolbarTotalHeight")}px）；" +
                    $"拉回 {originalWidth:0}px：恢复两行={!BoolOf(backState, "toolbarWide")}");
            }
            else
            {
                c.Add("J1-toolbar-wide-one-row", true,
                    $"屏幕可用宽度 {workWidth:0}px < 1600px，一行放不下（判定按实测高度），跳过宽窗口断言");
            }

            await RecordShotAsync(c, core, "01-continuous").ConfigureAwait(true);

            /* ---- 首屏那一页到底画出来了没有 ----
               这是判据②「冷启动」与判据③「打开 114.8 MB」的公共前提：自绘引擎里
               "文字层有了"不等于"位图画出来了"（前者走消息通道，后者走虚拟主机）。
               所以这里同时看 DOM 事实（complete && naturalWidth>0）与网络事实（CDP）。 */
            var openPage = IntOf(state, "page");
            var firstPageDrawn = await WaitAsync(() => ImgReadyAsync(core, openPage), 15000).ConfigureAwait(true);
            var firstPageDiag = await EvalAsync(core, PageDiagJs).ConfigureAwait(true);
            var firstPageRecovered = false;

            if (!firstPageDrawn)
            {
                // 离开再回来：区分"这一页的请求丢了/没被拦下"与"这一页永远不会画"
                await EvalAsync(core, "window.__readerDebug.pdfGoto(3)").ConfigureAwait(true);
                await Task.Delay(2000).ConfigureAwait(true);
                await EvalAsync(core, $"window.__readerDebug.pdfGoto({openPage})").ConfigureAwait(true);
                firstPageRecovered = await WaitAsync(() => ImgReadyAsync(core, openPage), 15000).ConfigureAwait(true);
            }

            var netLines = netLog
                .Where(l => l.Contains("ebook-pdf", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("loadingFailed", StringComparison.Ordinal))
                .Take(8)
                .ToList();

            c.Add("J1-first-page-bitmap", firstPageDrawn || firstPageRecovered,
                $"打开后当前页={openPage}；15 秒内 .pdfImage 有像素={firstPageDrawn}；" +
                $"离开再回来={firstPageRecovered}；" +
                $"页诊断[图片元素 {IntOf(firstPageDiag, "okImgCount")}/{IntOf(firstPageDiag, "imgCount")} 张有像素；" +
                $"当前页 src={StrOf(firstPageDiag, "src")}；complete={BoolOf(firstPageDiag, "complete")}；" +
                $"natural={IntOf(firstPageDiag, "nw")}×{IntOf(firstPageDiag, "nh")}；" +
                $"pending 类={BoolOf(firstPageDiag, "pending")}；.pdfFail={IntOf(firstPageDiag, "fail")} 个；" +
                $"failedPages=[{StrOf(firstPageDiag, "failedPages")}]；" +
                $"renderedPages=[{StrOf(firstPageDiag, "renderedPages")}]；" +
                $"requestCount={IntOf(firstPageDiag, "requestCount")}；charCount={IntOf(firstPageDiag, "charCount")}]；" +
                $"页位图网络事件：{(netLines.Count == 0 ? "(CDP 一条都没抓到)" : string.Join(" | ", netLines))}");

            await RecordShotAsync(c, core, "01b-first-page-settled").ConfigureAwait(true);

            // ---- 旋转：rotation==90 且图片宽高互换 ----
            // 在**有内容的一页**上测旋转（首屏那页可能正好是封面/整页图，与旋转无关）
            await EvalAsync(core, ButtonWatchJs).ConfigureAwait(true);
            await EvalAsync(core, "window.__readerDebug.pdfGoto(1)").ConfigureAwait(true);
            await WaitAsync(() => ImgReadyAsync(core, 1), PageTimeoutMs).ConfigureAwait(true);

            var before = await EvalAsync(core, CurrentImgJs).ConfigureAwait(true);
            var beforeW = IntOf(before, "w");
            var beforeH = IntOf(before, "h");
            var rotatedPage = IntOf(before, "page");
            var beforeZoom = StrOf(before, "zoomMode");

            /* toggle 会把"点完之后的状态"直接返回：这是最可靠的事实（不会被随后的重排抹掉）。 */
            var toggleState = await EvalAsync(core, "window.__readerDebug.pdfToggle('rotate')")
                .ConfigureAwait(true);
            var rotationAfterToggle = IntOf(toggleState, "rotation");
            var zoomAfterToggle = StrOf(toggleState, "zoomMode");

            var swapped = await WaitAsync(
                async () =>
                {
                    var node = await EvalAsync(core, CurrentImgJs).ConfigureAwait(true);
                    var w = IntOf(node, "w");
                    var h = IntOf(node, "h");

                    if (w <= 0 || h <= 0 || beforeW <= 0 || beforeH <= 0)
                    {
                        return false;
                    }

                    /* 位图可能同时被按新的长边重新渲染（旋转后"适应宽度"的页更矮），
                       所以判"宽高比反转"，而不是判像素逐个相等。 */
                    var ratioBefore = (double)beforeW / beforeH;
                    var ratioAfter = (double)w / h;

                    return Math.Abs((ratioAfter * ratioBefore) - 1) < 0.12;
                },
                PageTimeoutMs).ConfigureAwait(true);

            var rotated = await EvalAsync(core, CurrentImgJs).ConfigureAwait(true);
            var clicks = await EvalAsync(core, "JSON.stringify(window.__btnClicks || {})").ConfigureAwait(true);
            var afterW = IntOf(rotated, "w");
            var afterH = IntOf(rotated, "h");

            c.Add("J1-rotate-button", rotationAfterToggle == 90 && swapped,
                $"点一次旋转：返回 rotation={rotationAfterToggle}（期望 90）、zoomMode={zoomAfterToggle}；" +
                $"第 {rotatedPage} 页图片 {beforeW}×{beforeH} → {afterW}×{afterH}" +
                $"（宽高比 {Ratio(beforeW, beforeH)} → {Ratio(afterW, afterH)}，判据是反转）；" +
                $"旋转前的 zoomMode={beforeZoom}；期间按钮点击计数={clicks?.ToJsonString() ?? "(读不到)"}");

            await RecordShotAsync(c, core, "04-rotate90").ConfigureAwait(true);

            await EvalAsync(core, "window.__readerDebug.pdfSetView({rotation:0})").ConfigureAwait(true);
            await WaitAsync(
                async () => IntOf(await EvalAsync(core, CurrentImgJs).ConfigureAwait(true), "rotation") == 0,
                10000).ConfigureAwait(true);

            // ---- 单页 / 连续：continuous 翻转 ----
            var beforeLayout = await EvalAsync(core, StateJs).ConfigureAwait(true);
            var continuousBefore = BoolOf(beforeLayout, "continuous");

            var afterToggle = await EvalAsync(core, "window.__readerDebug.pdfToggle('layout')").ConfigureAwait(true);
            var continuousAfter = BoolOf(afterToggle, "continuous");

            c.Add("J1-layout-toggle", continuousAfter != continuousBefore,
                $"点一次「单页/连续」：continuous {continuousBefore} → {continuousAfter}（应翻转）；" +
                $"此时 spread={BoolOf(afterToggle, "spread")}");

            // 单页（continuous=false）：截图存证
            await RecordShotAsync(c, core, "02-single").ConfigureAwait(true);

            /* 用户 2026-10-16 实测反馈①：单页模式还能向下滑动。真因是那些 display:none 的空行
               仍留在流里、每行都吃 `.pdfRow + .pdfRow { margin-top: 14px }`——几百行就是几千像素
               的空白滚动区。量之前先切"适应页面"：那时页高正好等于视口高，容器只要还有死区，
               scrollHeight 就会明显大于 clientHeight。 */
            await EvalAsync(core, "window.__readerDebug.pdfSetView({fit:'Fit'})").ConfigureAwait(true);

            var singleMode = await EvalAsync(core, PdfScrollJs).ConfigureAwait(true);
            var singleLabel = StrOf(singleMode, "mode");
            var singleSpreadLabel = StrOf(singleMode, "spread");

            c.Add("J1-single-page-not-scrollable",
                IntOf(singleMode, "sh") > 0 && IntOf(singleMode, "ch") > 0
                && IntOf(singleMode, "sh") <= IntOf(singleMode, "ch") + 2
                && IntOf(singleMode, "st") == 0,
                $"单页 + 适应页面：正文容器 scrollHeight={IntOf(singleMode, "sh")} / " +
                $"clientHeight={IntOf(singleMode, "ch")}（不该有溢出，允差 2px）/ " +
                $"scrollTop={IntOf(singleMode, "st")}（应为 0）");

            /* 用户 2026-10-18 实测反馈③：拖滚动条 / 往下滑时画面会跳回拖动前或随机位置。
               真因是 updatePdfLayout 结尾拿 pdfCursor.top（过期锚点）做**绝对**恢复，
               图片加载完就触发一次 → 把用户拽回去。现在只补"锚点页新旧 offsetTop 的高度差"。
               判据：连续模式下滚到全书 40%，等可见页/预载页加载完，同一页在视口里的
               位置不应移动（容差 12px）。 */
            await EvalAsync(core, "window.__readerDebug.pdfSetView({continuous:true,spread:false,fit:'FitH'})")
                .ConfigureAwait(true);
            await EvalAsync(
                core,
                "(function(){var w=document.getElementById('pdfPages');w.scrollTop=Math.round(w.scrollHeight*0.4);return Math.round(w.scrollTop);})()")
                .ConfigureAwait(true);

            const string pageTopJs = """
                (function(){
                  var w = document.getElementById('pdfPages');
                  var st = window.__readerDebug.pdfState();
                  var el = document.querySelector('#pdfPages .pdfPage[data-page="' + st.page + '"]');
                  return {
                    page: st.page,
                    st: Math.round(w.scrollTop),
                    top: el ? Math.round(el.getBoundingClientRect().top - w.getBoundingClientRect().top) : -9999
                  };
                })()
                """;

            await Task.Delay(250).ConfigureAwait(true);

            var anchorBefore = await EvalAsync(core, pageTopJs).ConfigureAwait(true);
            var anchorPage = IntOf(anchorBefore, "page");
            var anchorSt = IntOf(anchorBefore, "st");

            await Task.Delay(2200).ConfigureAwait(true);

            var anchorAfter = await EvalAsync(
                core,
                "(function(){var w=document.getElementById('pdfPages');"
                + "var el=document.querySelector('#pdfPages .pdfPage[data-page=\"" + anchorPage + "\"]');"
                + "return {st:Math.round(w.scrollTop),"
                + "top:el?Math.round(el.getBoundingClientRect().top-w.getBoundingClientRect().top):-9999};})()")
                .ConfigureAwait(true);

            var anchorDrift = Math.Abs(IntOf(anchorAfter, "top") - IntOf(anchorBefore, "top"));

            c.Add("J1-scroll-anchor-stable",
                IntOf(anchorBefore, "top") > -9000 && IntOf(anchorAfter, "top") > -9000
                && anchorDrift <= 12 && IntOf(anchorAfter, "st") > 0,
                $"连续模式下滚到 40%：第 {anchorPage + 1} 页在视口里的位置 " +
                $"{IntOf(anchorBefore, "top")}px → 等 2.2 s 图片加载完 → {IntOf(anchorAfter, "top")}px " +
                $"（漂移 {anchorDrift}px，容差 12px；scrollTop {anchorSt} → {IntOf(anchorAfter, "st")}，" +
                ">0 说明没被拉回开头）");

            /* 收回单页 + 适应宽度：后面几条判据都以"单页（continuous=false, spread=false）"为起点。 */
            await EvalAsync(core, "window.__readerDebug.pdfSetView({continuous:false,spread:false,fit:'FitH'})")
                .ConfigureAwait(true);

            /* 用户 2026-10-16 实测反馈③：单页模式下切双页"没反应"。原先的 J1-spread-toggle 是
               从**连续**模式切的，从单页切这条路径根本没人测过——这里补上。数的是"当前页所在
               那一行里有几页"（不是全屏可见页数：LRU 会让别的页元素也带 offsetParent）。 */
            const string rowPagesJs = """
                (function(){
                  var st = window.__readerDebug.pdfState();
                  var row = st.rowPages || [];
                  var vis = 0;
                  for (var i = 0; i < row.length; i++) {
                    var el = document.querySelector('#pdfPages .pdfPage[data-page="' + row[i] + '"]');
                    if (el && el.offsetParent !== null) { vis += 1; }
                  }
                  return {
                    page: st.page, spread: st.spread, continuous: st.continuous,
                    inRow: vis, rowPages: row, rowMode: st.rowMode || '', rowCount: st.rowCount || 0,
                    rowSizes: (st.rowSizes || []).join('/')
                  };
                })()
                """;

            await EvalAsync(core, "window.__readerDebug.pdfGoto(1)").ConfigureAwait(true);

            var spreadFromSingle = await EvalAsync(core, "window.__readerDebug.pdfToggle('spread')")
                .ConfigureAwait(true);

            await WaitAsync(
                async () => IntOf(await EvalAsync(core, rowPagesJs).ConfigureAwait(true), "inRow") >= 1,
                8000).ConfigureAwait(true);

            var spreadRow = await EvalAsync(core, rowPagesJs).ConfigureAwait(true);

            c.Add("J1-spread-from-single",
                BoolOf(spreadFromSingle, "spread") && !BoolOf(spreadFromSingle, "continuous")
                && IntOf(spreadRow, "inRow") == 2,
                $"在单页模式下点「单页/双页」：spread={BoolOf(spreadFromSingle, "spread")}、" +
                $"continuous={BoolOf(spreadFromSingle, "continuous")}（单页本来就不连续，所以仍是 false）；" +
                $"第 {IntOf(spreadRow, "page") + 1} 页那一行里的页数={IntOf(spreadRow, "inRow")}（期望 2）；" +
                $"该行 DOM 页号=[{string.Join(",", (spreadRow?["rowPages"]?.AsArray() ?? new JsonArray()).Select(n => n is null ? -1 : n.GetValue<int>()).Select(n => n + 1))}]");

            /* 封面那一页：双页模式下按书籍惯例单独一行 → 那一行只有 1 页。
               这是**正当行为**，不是失败；写进报告免得下次又被当成"切不过去"。 */
            await EvalAsync(core, "window.__readerDebug.pdfGoto(0)").ConfigureAwait(true);

            var coverRow = await EvalAsync(core, rowPagesJs).ConfigureAwait(true);

            c.Add("J1-spread-cover-alone",
                BoolOf(coverRow, "spread") && IntOf(coverRow, "inRow") == 1,
                $"双页模式下跳到第 1 页：spread={BoolOf(coverRow, "spread")}、" +
                $"该行的页数={IntOf(coverRow, "inRow")}——第 1 页是封面，按书籍惯例单独一行" +
                "（所以在封面上点双页只会看到一页，模式其实已经切过去了）");

            // 退出双页，回到单页
            await EvalAsync(core, "window.__readerDebug.pdfToggle('spread')").ConfigureAwait(true);

            // 回到连续
            var backToContinuous = await EvalAsync(core, "window.__readerDebug.pdfToggle('layout')").ConfigureAwait(true);
            await RecordShotAsync(c, core, "03b-continuous-after").ConfigureAwait(true);

            /* 用户 2026-10-18 实测要求：两枚模式按钮的**文案固定**写成「单页/滚动」「单页/双页」，
               当前状态只靠颜色——白（按钮默认底色）＝单页模式，绿＝滚动 / 双页。
               原先写"目标模式"（点下去会变成什么），单页时两枚按钮的文字会互换，
               反而看不出当前是哪个模式。 */
            var scrollMode = await EvalAsync(core, PdfScrollJs).ConfigureAwait(true);
            var scrollLabel = StrOf(scrollMode, "mode");

            const string pdfGreen = "rgb(15, 123, 108)";

            c.Add("J1-mode-toggle-labels",
                singleLabel == "单页/滚动" && scrollLabel == "单页/滚动"
                && singleSpreadLabel == "单页/双页"
                && !BoolOf(singleMode, "layoutActive") && !BoolOf(singleMode, "spreadActive")
                && BoolOf(scrollMode, "layoutActive") && !BoolOf(scrollMode, "spreadActive")
                && StrOf(scrollMode, "layoutBg") == pdfGreen,
                $"单页模式下「{singleLabel}」/「{singleSpreadLabel}」，都不高亮" +
                $"（active={BoolOf(singleMode, "layoutActive")}/{BoolOf(singleMode, "spreadActive")}，白＝单页）；" +
                $"切到连续后文案仍「{scrollLabel}」，active={BoolOf(scrollMode, "layoutActive")}、" +
                $"底色={StrOf(scrollMode, "layoutBg")}（期望 {pdfGreen} 绿＝滚动）");

            /* ---- 双页与滚动是两个独立维度（用户 2026-10-18 实测要求）----
               在连续模式下点「单页/双页」：spread 打开、continuous **必须保持** →
               两枚按钮同时亮 = 双页滚动。再关掉「单页/滚动」：spread 仍开着、
               只显示当前一行 = 双页非滚动（一屏两页地翻）。
               先离开封面：第 1 页按书籍惯例单独一行，不能代表"双页两页一行"。 */
            await EvalAsync(core, "window.__readerDebug.pdfGoto(3)").ConfigureAwait(true);

            var spreadState = await EvalAsync(core, "window.__readerDebug.pdfToggle('spread')").ConfigureAwait(true);
            var comboCell = await EvalAsync(core, PdfScrollJs).ConfigureAwait(true);

            c.Add("J1-spread-continuous-combo",
                BoolOf(spreadState, "spread") && BoolOf(spreadState, "continuous")
                && BoolOf(comboCell, "layoutActive") && BoolOf(comboCell, "spreadActive"),
                $"连续模式下点「单页/双页」：spread={BoolOf(spreadState, "spread")}、" +
                $"continuous={BoolOf(spreadState, "continuous")}（两者独立，必须都 true）；" +
                $"两枚按钮同时高亮 layout={BoolOf(comboCell, "layoutActive")}/spread={BoolOf(comboCell, "spreadActive")}");

            await RecordShotAsync(c, core, "03-spread-scroll").ConfigureAwait(true);

            var spreadNoScroll = await EvalAsync(core, "window.__readerDebug.pdfToggle('layout')").ConfigureAwait(true);
            var spreadCell = await EvalAsync(core, PdfScrollJs).ConfigureAwait(true);

            var spreadRowAfter = await EvalAsync(core, rowPagesJs).ConfigureAwait(true);

            c.Add("J1-spread-non-scroll",
                BoolOf(spreadNoScroll, "spread") && !BoolOf(spreadNoScroll, "continuous")
                && IntOf(spreadRowAfter, "inRow") == 2,
                $"再关掉连续滚动：spread={BoolOf(spreadNoScroll, "spread")}、" +
                $"continuous={BoolOf(spreadNoScroll, "continuous")}（双页非滚动＝一屏两页）；" +
                $"当前行可见页数={IntOf(spreadRowAfter, "inRow")}（期望 2；页高超过视口时允许纵向滚动，" +
                $"实测滚动高度 {IntOf(spreadCell, "sh")} / 视口 {IntOf(spreadCell, "ch")}）；" +
                $"该行 DOM 页号=[{string.Join(",", (spreadRowAfter?["rowPages"]?.AsArray() ?? new JsonArray()).Select(n => n is null ? -1 : n.GetValue<int>()).Select(n => n + 1))}]；" +
                $"rowMode={StrOf(spreadRowAfter, "rowMode")}、行数={IntOf(spreadRowAfter, "rowCount")}、" +
                $"前四行页数={StrOf(spreadRowAfter, "rowSizes")}");

            await RecordShotAsync(c, core, "03-spread").ConfigureAwait(true);

            // 复位成连续 + 单页，后面判据的截图口径才统一
            await EvalAsync(core, "window.__readerDebug.pdfSetView({spread:false,continuous:true})").ConfigureAwait(true);

            // ---- 目录抽屉：56 条，点第 3 项跳到它的页 ----
            await EvalAsync(core, "document.getElementById('btnToc').click();1").ConfigureAwait(true);

            var toc = await WaitAsync(
                async () => BoolOf(await EvalAsync(core, TocJs).ConfigureAwait(true), "open"),
                8000).ConfigureAwait(true)
                ? await EvalAsync(core, TocJs).ConfigureAwait(true)
                : null;

            var tocCount = IntOf(toc, "count");
            var tocOpen = BoolOf(toc, "open");

            c.Add("J1-toc-panel-opens", tocOpen && tocCount > 0,
                $"点 #btnToc 后 #tocPanel 打开={tocOpen}；#tocList .tocItem 数量={tocCount}");

            c.Add("J1-toc-56-items", tocCount == 56 && tocCount == outline.Count,
                $"DOM .tocItem={tocCount}（判据①要求 56）；pdfium outline={outline.Count}；" +
                $"可跳转项={IntOf(toc, "jumpable")}；列表第 3 项=「{StrOf(toc, "thirdTitle")}」" +
                $"data-page={IntOf(toc, "thirdPage")}");

            await RecordShotAsync(c, core, "06-toc-open").ConfigureAwait(true);

            var tocTarget = IntOf(toc, "page");
            var tocIndex = IntOf(toc, "targetIndex");

            await EvalAsync(
                core,
                $"document.querySelectorAll('#tocList .tocItem')[{tocIndex}].click();1").ConfigureAwait(true);

            var jumped = await WaitAsync(
                async () => IntOf(await EvalAsync(core, StateJs).ConfigureAwait(true), "page") == tocTarget,
                15000).ConfigureAwait(true);

            var jumpedState = await EvalAsync(core, StateJs).ConfigureAwait(true);

            c.Add("J1-toc-jump", jumped && tocTarget >= 0,
                $"点目录里的第 3 个可跳转项（DOM 下标 {tocIndex}，「{StrOf(toc, "title")}」，" +
                $"data-page={tocTarget}）后 pdfState().page={IntOf(jumpedState, "page")}；" +
                $"tocPanel 自动收起={!BoolOf(await EvalAsync(core, TocJs).ConfigureAwait(true), "open")}");

            // ---- 查找：宿主检索 + 文字层高亮 ----
            await EvalAsync(core, "document.getElementById('pdfSearch').click();1").ConfigureAwait(true);

            var findOpen = await WaitAsync(
                async () => await EvalBoolAsync(core, "!document.getElementById('findBar').hidden")
                    .ConfigureAwait(true),
                6000).ConfigureAwait(true);

            var pageText = c.PdfPages.ReadPageText(moeFile, 1, null).Text ?? string.Empty;
            var query = BuildQuery(pageText);

            await EvalAsync(core, FindJs(query)).ConfigureAwait(true);

            var hits = await WaitAsync(
                async () => IntOf(await EvalAsync(core, StateJs).ConfigureAwait(true), "hitCount") > 0,
                20000).ConfigureAwait(true);

            var hitNodes = await WaitAsync(
                async () => await EvalIntAsync(core, HitNodesJs).ConfigureAwait(true) > 0,
                15000, 200).ConfigureAwait(true);

            var hitState = await EvalAsync(core, StateJs).ConfigureAwait(true);
            var hitNodeCount = await EvalIntAsync(core, HitNodesJs).ConfigureAwait(true);

            c.Add("J1-find-hits", findOpen && hits && hitNodes,
                $"查找栏打开={findOpen}；查询「{query}」（取自第 1 页正文）命中 " +
                $"{IntOf(hitState, "hitCount")} 处；当前页 .pdfChar.hit/.hitCurrent={hitNodeCount} 个" +
                $"（跳到了第 {IntOf(hitState, "page")} 页）");

            await RecordShotAsync(c, core, "07-find").ConfigureAwait(true);

            // ---- 文本可选中：文字层字符盒 + 真实鼠标拖选 + 剪贴板 ----
            await EvalAsync(core, "window.__readerDebug.pdfGoto(1)").ConfigureAwait(true);
            await WaitAsync(() => ImgReadyAsync(core, 1), PageTimeoutMs).ConfigureAwait(true);
            await WaitAsync(
                async () => await EvalIntAsync(core, "window.__readerDebug.pdfCharNodes()").ConfigureAwait(true) > 0,
                PageTimeoutMs).ConfigureAwait(true);

            var charNodes = await EvalIntAsync(core, "window.__readerDebug.pdfCharNodes()").ConfigureAwait(true);

            /* 先等"当前页不再 pending"再拖：图片到位的 load 回调会重排文字层，
               而重排会把已经选中的内容清掉（用户拖到一半图才加载完就会看到选区消失）。
               拖选最多试 3 次，取最长的那个选区。 */
            await WaitAsync(
                async () => !BoolOf(await EvalAsync(core, CurrentImgJs).ConfigureAwait(true), "pending"),
                PageTimeoutMs).ConfigureAwait(true);

            try
            {
                Clipboard.Clear();
            }
            catch (Exception)
            {
                // 剪贴板被别的进程占着；清不掉只会让结论更保守
            }

            string? selectionShot = null;
            var selected = string.Empty;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(1200).ConfigureAwait(true);

                selectionShot = await DragSelectAndCaptureAsync(core, window, c.ShotsDir, "08-text-selection")
                    .ConfigureAwait(true);

                var picked = await EvalStrAsync(core, "window.__readerDebug.pdfSelection()").ConfigureAwait(true);

                if (picked.Length > selected.Length)
                {
                    selected = picked;
                }

                if (selected.Length > 0)
                {
                    break;
                }
            }

            /* 真实 OS 级 Ctrl+C 只当**补充证据**：无人值守时它取决于窗口是否真的拿到前台焦点，
               偶尔拿不到（T6-2 的插件时代也是这样）。判据是"真实鼠标拖选后拿到非空选中文本"，
               所以 pdfSelection() 才是硬断言。 */
            var clip = string.Empty;

            for (var attempt = 0; attempt < 2 && clip.Length == 0; attempt++)
            {
                window.Activate();
                window.Web.Focus();
                await Task.Delay(400).ConfigureAwait(true);

                SendRealCtrlKey(0x43);   // C
                await Task.Delay(1200).ConfigureAwait(true);

                clip = ReadClipboardText();

                if (clip.Length > 0)
                {
                    break;
                }

                // 选区可能被重排吃掉，补一次拖选再复制
                await DragSelectAndCaptureAsync(core, window, c.ShotsDir, "08b-text-selection-retry")
                    .ConfigureAwait(true);
                selected = await EvalStrAsync(core, "window.__readerDebug.pdfSelection()").ConfigureAwait(true);
            }

            c.Add("X1-text-selectable", charNodes > 0 && selected.Length > 0,
                $"第 1 页文字层字符盒={charNodes} 个；**真实鼠标拖选**后 pdfSelection()={selected.Length} 字" +
                $"（「{Head(selected)}」）；真实 Ctrl+C 后剪贴板={clip.Length} 字（「{Head(clip)}」，补充证据）；" +
                $"截图={(selectionShot is null ? "没生成" : Path.GetFileName(selectionShot))}");

            // ---- 暗色主题：宿主按主题重新渲染 ----
            var beforeTheme = await EvalAsync(core, StateJs).ConfigureAwait(true);
            var requestBefore = IntOf(beforeTheme, "requestCount");
            var lightInk = await RegionInkAsync(core).ConfigureAwait(true);

            await EvalAsync(core, ThemeJs("dark")).ConfigureAwait(true);

            var themeDark = await WaitAsync(
                async () =>
                {
                    var node = await EvalAsync(core, StateJs).ConfigureAwait(true);

                    return StrOf(node, "theme") == "dark" && IntOf(node, "requestCount") > requestBefore;
                },
                PageTimeoutMs).ConfigureAwait(true);

            await Task.Delay(2500).ConfigureAwait(true);

            var darkState = await EvalAsync(core, StateJs).ConfigureAwait(true);
            var darkInk = await RegionInkAsync(core).ConfigureAwait(true);

            c.Add("X2-dark-theme-render", themeDark,
                $"切到 dark 后 pdfState().theme={StrOf(darkState, "theme")}；requestCount {requestBefore} → " +
                $"{IntOf(darkState, "requestCount")}（宿主按新主题重渲染）；" +
                $"页面区域墨迹占比 {lightInk.Ratio:F4} → {darkInk.Ratio:F4}" +
                "（文字页反色后底色变深，墨迹占比必然上升）");

            await RecordShotAsync(c, core, "05-dark").ConfigureAwait(true);

            await EvalAsync(core, ThemeJs("light")).ConfigureAwait(true);
            await WaitAsync(
                async () => StrOf(await EvalAsync(core, StateJs).ConfigureAwait(true), "theme") == "normal",
                15000).ConfigureAwait(true);

            // ---- 外部打开常驻可用 ----
            var externalEnabled = await EvalBoolAsync(
                core, "(function(){var b=document.getElementById('pdfOpenExternal');return !!(b && !b.disabled);})()")
                .ConfigureAwait(true);

            var expectedPath = (await c.Repo.GetByIdAsync(bookId).ConfigureAwait(true))
                ?.Formats.FirstOrDefault(f => f.Format == BookFormatKind.Pdf)?.FilePath ?? string.Empty;

            await EvalAsync(core, "document.getElementById('pdfOpenExternal').click();1").ConfigureAwait(true);
            await Task.Delay(1200).ConfigureAwait(true);

            var handed = string.Equals(
                recorder.LastOpenedFile, expectedPath, StringComparison.OrdinalIgnoreCase);

            c.Add("X4-external-open", externalEnabled && handed,
                $"#pdfOpenExternal 可用={externalEnabled}；点击后交给系统默认程序={handed}" +
                $"（{recorder.LastOpenedFile}；期望 {expectedPath}）");

            // ---- 截图数量 ----
            var shotNames = string.Join(", ", c.Shots.Select(p => Path.GetFileName(p)));

            c.Add("X5-screenshots", c.Shots.Count >= 7,
                $"本次落了 {c.Shots.Count} 张（要求 ≥7）：{shotNames}");
        }
        finally
        {
            window?.Close();
        }
    }

    // ==================== 判据②：200 页文字版连续翻页 ====================

    private static async Task JudgeTurnAsync(Ctx c, string textFile)
    {
        var imported = await c.Importer.ImportAsync(textFile, ImportOptions.Default, null).ConfigureAwait(true);

        if (imported.BookId is not { } bookId)
        {
            c.Add("J2-open-200p", false, $"{Name(textFile)} 导入失败：{imported.Outcome}");
            return;
        }

        ReaderWindow? window = null;

        try
        {
            window = c.NewWindow(bookId, new RecordingShellLauncher());

            var clock = Stopwatch.StartNew();
            window.Show();

            var core = await WaitForCoreAsync(window, 25000).ConfigureAwait(true);
            var state = core is null ? null : await WaitForPdfStateAsync(core, 30000).ConfigureAwait(true);

            if (core is null || state is null)
            {
                c.Add("J2-open-200p", false, "阅读窗口或 PDF 模式没就绪");
                return;
            }

            var totalPages = IntOf(state, "totalPages");

            c.Add("J2-open-200p", totalPages == 196,
                $"{Name(textFile)} totalPages={totalPages}（期望 196，Calibre 按「200 页」生成）；" +
                $"打开到进入 PDF 模式 {clock.ElapsedMilliseconds} ms");

            /* 预载窗口（用户 2026-10-18 实测反馈②：往下滑时下面的图要等很久才出来）：
               默认 10 页，可在工具栏「预载」下拉里改。判据：载荷带下来 10、页缓存上限随之放大，
               并且当前页往后确实有 3 页以上已经拿到图（不是等滚到那儿才请求）。 */
            var preload = await EvalAsync(core, StateJs).ConfigureAwait(true);

            await EvalAsync(
                core,
                "(function(){var w=document.getElementById('pdfPages');w.scrollTop=Math.round(w.scrollHeight*0.05);return 1;})()")
                .ConfigureAwait(true);
            await Task.Delay(2500).ConfigureAwait(true);

            var preloadAfter = await EvalAsync(core, StateJs).ConfigureAwait(true);
            var prefetchedAhead = (preloadAfter?["prefetchedPages"]?.AsArray() ?? new JsonArray())
                .Select(n => n is null ? -1 : n.GetValue<int>())
                .Where(n => n >= 0)
                .ToList();
            var pageNow = IntOf(preloadAfter, "page");

            c.Add("J2-preload-window",
                IntOf(preload, "preloadPages") == 10
                && IntOf(preload, "cacheLimit") >= 18
                && prefetchedAhead.Count >= 3
                && prefetchedAhead.All(p => p > pageNow && p <= pageNow + 10),
                $"预载窗口={IntOf(preload, "preloadPages")} 页（默认 10）、页缓存上限={IntOf(preload, "cacheLimit")}；" +
                $"滚到第 {pageNow + 1} 页后已就绪的前方页=[{string.Join(",", prefetchedAhead.Select(p => p + 1))}]" +
                "（应 ≥3 页，且都在当前页之后 10 页以内）");

            // 统一成"适应宽度 + 连续 + 不旋转"，与判据②的口径一致
            await EvalAsync(core, "window.__readerDebug.pdfSetView({continuous:true,spread:false,rotation:0,fit:'FitH'})")
                .ConfigureAwait(true);

            var firstPage = IntOf(state, "page");
            var cold = await WaitAsync(() => ImgReadyAsync(core, firstPage), PageTimeoutMs).ConfigureAwait(true);
            var coldMs = clock.ElapsedMilliseconds;
            var coldRecovered = false;

            if (!cold)
            {
                /* 首屏页没画出来时再走一次"离开再回来"：把"首屏那一页的请求丢了"与
                   "这一页永远画不出来"分开记，报告里两个数都要有。 */
                await EvalAsync(core, "window.__readerDebug.pdfGoto(5)").ConfigureAwait(true);
                await Task.Delay(2000).ConfigureAwait(true);
                await EvalAsync(core, $"window.__readerDebug.pdfGoto({firstPage})").ConfigureAwait(true);
                coldRecovered = await WaitAsync(() => ImgReadyAsync(core, firstPage), PageTimeoutMs).ConfigureAwait(true);
            }

            c.Add("J2-first-page-cold", cold,
                $"第 1 页（第 {firstPage} 页）冷启动 {coldMs} ms" +
                "（从 ReaderWindow.Show() 到 .pdfImage complete && naturalWidth>0，" +
                "含 WebView2 初始化 + pdfium 打开 196 页 + 渲染第 1 页）" +
                (cold ? "" : $"，**{PageTimeoutMs} ms 内没画出来**；离开再回来={coldRecovered}"));

            // 逐页翻 30 页（0..29），逐页计时
            var samples = new List<long>();
            var prefetched = 0;
            var failures = new List<int>();

            for (var page = 0; page < PageTurnSamples; page++)
            {
                var rendered = await EvalAsync(core, StateJs).ConfigureAwait(true);
                var wasRendered = (rendered?["renderedPages"]?.AsArray() ?? new JsonArray())
                    .Any(n => n is not null && n.GetValue<int>() == page);

                var sw = Stopwatch.StartNew();

                await EvalAsync(core, $"window.__readerDebug.pdfGoto({page})").ConfigureAwait(true);

                var ready = await WaitAsync(() => ImgReadyAsync(core, page), PageTimeoutMs, 20).ConfigureAwait(true);

                sw.Stop();

                if (!ready)
                {
                    failures.Add(page);
                }

                if (wasRendered)
                {
                    prefetched++;
                }

                samples.Add(sw.ElapsedMilliseconds);
            }

            // 第 0 页是打开时就已经渲染好的首屏，单独报，不计入稳态
            var steady = samples.Skip(1).ToList();
            var (min, avg, max, p95) = Stats(steady);

            c.Add("J2-page-turn-steady", failures.Count == 0 && avg <= SteadyBudgetMs,
                $"连续模式下 pdfGoto 翻第 1..29 页共 {steady.Count} 页：min={min} ms / avg={avg} ms / " +
                $"max={max} ms / p95={p95} ms（门槛 avg ≤ {SteadyBudgetMs} ms）；" +
                $"第 0 页样本={samples[0]} ms（打开时已渲染的首屏，不计入稳态）；" +
                $"其中 {prefetched}/{PageTurnSamples} 页在翻之前就已在 DOM 里（引擎预取可见页 ±2）" +
                (failures.Count == 0 ? "" : $"；**{failures.Count} 页超时未画出**：{string.Join(",", failures)}"));

            if (max > SteadyBudgetMs)
            {
                c.Add("J2-over-budget-pages", true,
                    $"稳态里有单页超过 {SteadyBudgetMs} ms：max={max} ms；逐页样本=" +
                    string.Join(",", samples));
            }
        }
        finally
        {
            window?.Close();
        }
    }

    // ==================== 判据③：内存 ====================

    private static async Task JudgeMemoryAsync(Ctx c, string bigFile)
    {
        var imported = await c.Importer.ImportAsync(bigFile, ImportOptions.Default, null).ConfigureAwait(true);

        if (imported.BookId is not { } bookId)
        {
            c.Add("J3-open-large", false, $"{Name(bigFile)} 导入失败：{imported.Outcome}");
            return;
        }

        var baseline = WorkingSetMb();
        ReaderWindow? window = null;

        try
        {
            window = c.NewWindow(bookId, new RecordingShellLauncher());

            var clock = Stopwatch.StartNew();
            window.Show();

            var core = await WaitForCoreAsync(window, 30000).ConfigureAwait(true);
            var state = core is null ? null : await WaitForPdfStateAsync(core, 60000).ConfigureAwait(true);

            if (core is null || state is null)
            {
                c.Add("J3-open-large", false, "阅读窗口或 PDF 模式没就绪");
                return;
            }

            var firstPage = IntOf(state, "page");
            await WaitAsync(() => ImgReadyAsync(core, firstPage), PageTimeoutMs).ConfigureAwait(true);

            var openMs = clock.ElapsedMilliseconds;
            var afterOpen = WorkingSetMb();
            var peak = afterOpen;
            var turns = new List<long>();
            var failures = 0;

            var limit = Math.Min(MemoryTurnCount, IntOf(state, "totalPages") - 1);

            for (var page = 1; page <= limit; page++)
            {
                var sw = Stopwatch.StartNew();

                await EvalAsync(core, $"window.__readerDebug.pdfGoto({page})").ConfigureAwait(true);

                if (!await WaitAsync(() => ImgReadyAsync(core, page), PageTimeoutMs, 20).ConfigureAwait(true))
                {
                    failures++;
                }

                sw.Stop();
                turns.Add(sw.ElapsedMilliseconds);
                peak = Math.Max(peak, WorkingSetMb());
            }

            await Task.Delay(3000).ConfigureAwait(true);

            var end = WorkingSetMb();
            var growth = end - afterOpen;
            var (min, avg, max, p95) = Stats(turns);

            c.Add("J3-memory-budget", peak <= MemoryBudgetBytes / 1024 / 1024,
                $"{Mb(bigFile)} 扫描件：打开前 {baseline} MB → 首屏后 {afterOpen} MB → " +
                $"翻 {limit} 页峰值 {peak} MB → 结束后 {end} MB" +
                $"（门槛 ≤ {MemoryBudgetBytes / 1024 / 1024} MB）；打开到首屏 {openMs} ms；" +
                $"翻页 min/avg/max/p95 = {min}/{avg}/{max}/{p95} ms" +
                (failures == 0 ? "" : $"；**{failures} 页超时未画出**"));

            c.Add("J3-memory-growth", growth <= MemoryGrowthBudgetBytes / 1024 / 1024,
                $"首屏后 {afterOpen} MB → 翻完 {limit} 页并等 3 秒后 {end} MB，增量 {growth} MB" +
                $"（门槛 ≤ {MemoryGrowthBudgetBytes / 1024 / 1024} MB，即「翻很多页也不该持续涨」）");
        }
        finally
        {
            window?.Close();
        }
    }

    // ==================== 扫描件：暗色下图片页不反色 ====================

    private static async Task JudgeScanThemeAsync(Ctx c, string scanFile, string textFile)
    {
        /* 第一层证据（决定性）：宿主渲染服务对同一页分别用 Normal / Dark 渲染。
           PdfDocumentService 只在 Dark 且 !isImagePage 时反色，所以
           图片页两次渲染**逐字节相同**，文字页两次必然不同。 */
        var scanNormal = c.PdfPages.RenderPage(scanFile, 0, new PdfPageRenderOptions(1600, 0, PdfPageTheme.Normal), null);
        var scanDark = c.PdfPages.RenderPage(scanFile, 0, new PdfPageRenderOptions(1600, 0, PdfPageTheme.Dark), null);
        var scanSame = Same(scanNormal.Jpeg, scanDark.Jpeg);

        var textNormal = c.PdfPages.RenderPage(textFile, 1, new PdfPageRenderOptions(1600, 0, PdfPageTheme.Normal), null);
        var textDark = c.PdfPages.RenderPage(textFile, 1, new PdfPageRenderOptions(1600, 0, PdfPageTheme.Dark), null);
        var textSame = Same(textNormal.Jpeg, textDark.Jpeg);

        c.Add("X3-scan-image-page-not-inverted", scanNormal.IsImagePage && scanSame,
            $"{Name(scanFile)} 第 0 页 isImagePage={scanNormal.IsImagePage}（无文本层 → 图片页）；" +
            $"Normal {scanNormal.Width}×{scanNormal.Height}/{scanNormal.Jpeg.Length} B 与 " +
            $"Dark {scanDark.Width}×{scanDark.Height}/{scanDark.Jpeg.Length} B " +
            $"逐字节相同={scanSame} —— Dark 下**没有**被反色");

        c.Add("X3-text-page-inverted", !textNormal.IsImagePage && !textSame,
            $"{Name(textFile)} 第 1 页 isImagePage={textNormal.IsImagePage}（有文本层 → 文字页）；" +
            $"Normal {textNormal.Jpeg.Length} B vs Dark {textDark.Jpeg.Length} B 逐字节相同={textSame} " +
            "—— 文字页在 Dark 下被反色（两个方向都对，才说明「按主题渲染」是按页判定的）");

        /* 第二层证据（前端像素）：真开窗口、真切主题，比较**页面区域**的墨迹占比。
           图片页不反色 ⇒ 切暗色前后页面区域亮度几乎不变；文字页反色 ⇒ 明显变深。 */
        var imported = await c.Importer.ImportAsync(scanFile, ImportOptions.Default, null).ConfigureAwait(true);

        if (imported.BookId is not { } bookId)
        {
            c.Add("X3-scan-dark-frontend", false, $"{Name(scanFile)} 导入失败：{imported.Outcome}");
            return;
        }

        ReaderWindow? window = null;

        try
        {
            window = c.NewWindow(bookId, new RecordingShellLauncher());
            window.Show();

            var core = await WaitForCoreAsync(window, 30000).ConfigureAwait(true);
            var state = core is null ? null : await WaitForPdfStateAsync(core, 60000).ConfigureAwait(true);

            if (core is null || state is null)
            {
                c.Add("X3-scan-dark-frontend", false, "阅读窗口或 PDF 模式没就绪");
                return;
            }

            await EvalAsync(core, "window.__readerDebug.pdfGoto(0)").ConfigureAwait(true);
            await WaitAsync(() => ImgReadyAsync(core, 0), PageTimeoutMs).ConfigureAwait(true);
            await Task.Delay(1200).ConfigureAwait(true);

            var normal = await RegionInkAsync(core).ConfigureAwait(true);
            var requestBefore = IntOf(await EvalAsync(core, StateJs).ConfigureAwait(true), "requestCount");

            await EvalAsync(core, ThemeJs("dark")).ConfigureAwait(true);

            var switched = await WaitAsync(
                async () =>
                {
                    var node = await EvalAsync(core, StateJs).ConfigureAwait(true);

                    return StrOf(node, "theme") == "dark" && IntOf(node, "requestCount") > requestBefore;
                },
                PageTimeoutMs).ConfigureAwait(true);

            await Task.Delay(2500).ConfigureAwait(true);

            var dark = await RegionInkAsync(core).ConfigureAwait(true);
            var delta = dark.Ratio - normal.Ratio;

            await RecordShotAsync(c, core, "09-scan-dark").ConfigureAwait(true);

            c.Add("X3-scan-dark-frontend", switched && normal.Found && delta <= 0.10,
                $"切到 dark（theme={StrOf(await EvalAsync(core, StateJs).ConfigureAwait(true), "theme")}，" +
                $"requestCount {requestBefore} → {IntOf(await EvalAsync(core, StateJs).ConfigureAwait(true), "requestCount")}）；" +
                $"页面区域（{normal.Width}×{normal.Height} CSS px 的第 0 页）墨迹占比 " +
                $"{normal.Ratio:F4} → {dark.Ratio:F4}（Δ={delta:+0.0000;-0.0000;0}）—— " +
                "扫描页在暗色下不得被反色，所以 Δ 必须很小（文字页在别处实测会明显变深）");
        }
        finally
        {
            window?.Close();
        }
    }

    // ==================== 样本/统计小工具 ====================

    private static string Name(string? path) => path is null ? "(缺)" : Path.GetFileName(path);

    private static string Mb(string? path) => path is null
        ? "(缺)"
        : (new FileInfo(path).Length / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " MB";

    private static long WorkingSetMb() => Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;

    private static bool Same(byte[] a, byte[] b) =>
        a.Length == b.Length && a.AsSpan().SequenceEqual(b);

    private static (long Min, long Avg, long Max, long P95) Stats(IReadOnlyList<long> values)
    {
        if (values.Count == 0)
        {
            return (0, 0, 0, 0);
        }

        var sorted = values.OrderBy(v => v).ToList();
        var index = (int)Math.Ceiling(sorted.Count * 0.95) - 1;

        return (sorted[0], (long)Math.Round(sorted.Average()), sorted[^1], sorted[Math.Clamp(index, 0, sorted.Count - 1)]);
    }

    /// <summary>从真实文本里取一段连续（无空白）的字串当查询词，保证宿主检索一定命中。</summary>
    private static string BuildQuery(string text)
    {
        var run = new StringBuilder();

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (run.Length >= 6)
                {
                    break;
                }

                run.Clear();
                continue;
            }

            run.Append(ch);

            if (run.Length >= 8)
            {
                break;
            }
        }

        return run.Length >= 4 ? run.ToString() : "的";
    }

    private static string Head(string text)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ');

        return flat.Length <= 24 ? flat : flat[..24];
    }

    private static string Ratio(int width, int height) => width <= 0 || height <= 0
        ? "(无)"
        : ((double)width / height).ToString("F3", CultureInfo.InvariantCulture);

    // ==================== DOM / 探针 JS ====================

    private const string StateJs = "window.__readerDebug.pdfState()";

    /// <summary>单页模式的正文容器有没有可滚动溢出，外加两个模式按钮的固定文案与当前状态色。
    /// 两条都是用户实测反馈的问题（单页还能下滚 / 按钮内容与状态").</summary>
    private const string PdfScrollJs = """
        (function(){
          var w = document.getElementById('pdfPages');
          var l = document.getElementById('pdfLayout');
          var s = document.getElementById('pdfSpread');
          var lc = l ? window.getComputedStyle(l) : null;
          var sc = s ? window.getComputedStyle(s) : null;
          return {
            sh: w ? w.scrollHeight : -1,
            ch: w ? w.clientHeight : -1,
            st: w ? w.scrollTop : -1,
            mode: l ? String(l.textContent) : '',
            spread: s ? String(s.textContent) : '',
            layoutActive: l ? l.classList.contains('active') : false,
            spreadActive: s ? s.classList.contains('active') : false,
            layoutBg: lc ? String(lc.backgroundColor) : '',
            spreadBg: sc ? String(sc.backgroundColor) : ''
          };
        })()
        """;

    private const string HitNodesJs =
        "document.querySelectorAll('#pdfPages .pdfChar.hit, #pdfPages .pdfChar.hitCurrent').length";

    private const string DomIdentityJs = """
        (function(){
          var want = ['toolbar','tocPanel','findBar','statusBar','notice','pdfBar','pdfPages','tocList','viewport'];
          var missing = [];
          for (var i = 0; i < want.length; i++) { if (!document.getElementById(want[i])) { missing.push(want[i]); } }
          var bar = ['pdfPrev','pdfPageInput','pdfPageTotal','pdfNext','pdfZoomOut','pdfZoomIn','pdfFitPage',
                     'pdfFitWidth','pdfRotate','pdfLayout','pdfSpread','pdfSearch','pdfFullscreen','pdfOpenExternal'];
          var missingBar = [];
          for (var j = 0; j < bar.length; j++) { if (!document.getElementById(bar[j])) { missingBar.push(bar[j]); } }
          var toolbar = document.getElementById('toolbar');
          var pdfBar = document.getElementById('pdfBar');
          var viewport = document.getElementById('viewport');
          var pages = document.getElementById('pdfPages');
          return {
            missing: missing.join(','),
            missingBar: missingBar.join(','),
            toolbarCount: document.querySelectorAll('#toolbar').length,
            boardCount: document.querySelectorAll('#tocPanel').length,
            barCount: document.querySelectorAll('#pdfBar').length,
            pdfBarInsideToolbar: !!(toolbar && pdfBar && toolbar.contains(pdfBar)),
            pagesInsideViewport: !!(viewport && pages && viewport.contains(pages)),
            bodyClass: String(document.body.className || '')
          };
        })()
        """;

    /// <summary>当前页的图片状态 + 页面盒子的位置（像素证据要靠这个 rect 定位）。</summary>
    private const string CurrentImgJs = """
        (function(){
          var st = window.__readerDebug.pdfState();
          var page = st.page;
          var box = document.querySelector('#pdfPages .pdfPage[data-page="' + page + '"]');
          var el = box ? box.querySelector('.pdfImage') : null;
          var target = box || document.getElementById('pdfPages');
          var r = target.getBoundingClientRect();
          return {
            page: page,
            rotation: st.rotation,
            zoomMode: st.zoomMode,
            zoom: st.zoom,
            continuous: !!st.continuous,
            spread: !!st.spread,
            complete: !!(el && el.complete),
            w: el ? el.naturalWidth : 0,
            h: el ? el.naturalHeight : 0,
            hasBox: !!box,
            pending: !!(box && box.classList.contains('pdfPagePending')),
            x: r.left, y: r.top, rw: r.width, rh: r.height,
            vw: window.innerWidth, vh: window.innerHeight
          };
        })()
        """;

    /// <summary>
    /// 给关键按钮挂一个只读的点击计数器（测试侧监听，不动产品代码）。
    /// 用途：当"状态自己变了"时，能判断到底是不是某个按钮被点了。
    /// </summary>
    private const string ButtonWatchJs = """
        (function(){
          if (window.__btnClicks) { return true; }
          window.__btnClicks = {};
          var ids = ['pdfRotate','pdfFitPage','pdfFitWidth','pdfLayout','pdfSpread','btnToc',
                     'pdfSearch','pdfOpenExternal','pdfPrev','pdfNext','pdfZoomIn','pdfZoomOut'];
          for (var i = 0; i < ids.length; i++) {
            (function(id){
              var el = document.getElementById(id);
              if (!el) { return; }
              el.addEventListener('click', function(){ window.__btnClicks[id] = (window.__btnClicks[id] || 0) + 1; });
            })(ids[i]);
          }
          return true;
        })()
        """;

    private const string TocJs = """
        (function(){
          var panel = document.getElementById('tocPanel');
          var items = document.querySelectorAll('#tocList .tocItem');
          var jumpable = [];
          for (var i = 0; i < items.length; i++) {
            if (parseInt(items[i].getAttribute('data-page') || '-1', 10) >= 0) { jumpable.push(i); }
          }
          var third = items.length > 2 ? items[2] : null;
          var targetIndex = jumpable.length > 2 ? jumpable[2] : (jumpable.length ? jumpable[0] : -1);
          var target = targetIndex >= 0 ? items[targetIndex] : null;
          return {
            open: !!(panel && !panel.hidden),
            count: items.length,
            jumpable: jumpable.length,
            thirdPage: third ? parseInt(third.getAttribute('data-page') || '-1', 10) : -1,
            thirdTitle: third ? String(third.textContent || '') : '',
            targetIndex: targetIndex,
            page: target ? parseInt(target.getAttribute('data-page') || '-1', 10) : -1,
            title: target ? String(target.textContent || '') : ''
          };
        })()
        """;

    private static string FindJs(string query) => """
        (function(){
          var input = document.getElementById('findInput');
          if (!input) { return false; }
          input.value = "__QUERY__";
          input.dispatchEvent(new Event('input', { bubbles: true }));
          return true;
        })()
        """.Replace("__QUERY__", query.Replace("\\", "\\\\").Replace("\"", "\\\""), StringComparison.Ordinal);

    private static string ThemeJs(string id) => """
        (function(){
          var sel = document.getElementById('themeSelect');
          if (!sel) { return false; }
          sel.value = "__ID__";
          sel.dispatchEvent(new Event('change', { bubbles: true }));
          return true;
        })()
        """.Replace("__ID__", id, StringComparison.Ordinal);

    private static string ImgReadyJs(int page) => """
        (function(){
          var el = document.querySelector('#pdfPages .pdfPage[data-page="__PAGE__"] .pdfImage');
          return !!(el && el.complete && el.naturalWidth > 0);
        })()
        """.Replace("__PAGE__", page.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>当前页的位图诊断：DOM 事实 + 网络事实（"文字层有了但位图没画出来"要分得清）。</summary>
    private const string PageDiagJs = """
        (function(){
          var st = window.__readerDebug.pdfState();
          var page = st.page;
          var box = document.querySelector('#pdfPages .pdfPage[data-page="' + page + '"]');
          var el = box ? box.querySelector('.pdfImage') : null;
          var imgs = document.querySelectorAll('#pdfPages .pdfImage');
          var okImgs = 0;
          for (var i = 0; i < imgs.length; i++) {
            if (imgs[i].complete && imgs[i].naturalWidth > 0) { okImgs++; }
          }
          var src = el ? el.getAttribute('src') : null;
          return {
            page: page,
            imgCount: imgs.length,
            okImgCount: okImgs,
            src: src ? String(src).slice(0, 52) : '(空)',
            complete: !!(el && el.complete),
            nw: el ? el.naturalWidth : -1,
            nh: el ? el.naturalHeight : -1,
            pending: !!(box && box.classList.contains('pdfPagePending')),
            fail: document.querySelectorAll('#pdfPages .pdfFail').length,
            failedPages: (st.failedPages || []).join(','),
            renderedPages: (st.renderedPages || []).join(','),
            requestCount: st.requestCount,
            charCount: st.charCount
          };
        })()
        """;

    // ==================== 执行 / 等待 / 截图 ====================

    private static async Task<bool> ImgReadyAsync(CoreWebView2 core, int page) =>
        await EvalBoolAsync(core, ImgReadyJs(page)).ConfigureAwait(true);

    /// <summary>
    /// 记下网络层事实（CDP Network 域）。只管收集，判定留给调用处——
    /// "页位图请求到底有没有被宿主拦下来"只有这里看得见。
    /// </summary>
    private static async Task StartNetworkWatchAsync(CoreWebView2 core, List<string> log)
    {
        var failed = core.GetDevToolsProtocolEventReceiver("Network.loadingFailed");

        failed.DevToolsProtocolEventReceived += (_, e) =>
        {
            var json = e.ParameterObjectAsJson ?? string.Empty;

            if (json.Contains("ebook-pdf", StringComparison.OrdinalIgnoreCase))
            {
                log.Add("loadingFailed=" + Shorten(json, 220));
            }
        };

        var requested = core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");

        requested.DevToolsProtocolEventReceived += (_, e) =>
        {
            var json = e.ParameterObjectAsJson ?? string.Empty;

            if (json.Contains("ebook-pdf", StringComparison.OrdinalIgnoreCase))
            {
                log.Add("requestWillBeSent=" + Shorten(json, 160));
            }
        };

        var response = core.GetDevToolsProtocolEventReceiver("Network.responseReceived");

        response.DevToolsProtocolEventReceived += (_, e) =>
        {
            var json = e.ParameterObjectAsJson ?? string.Empty;

            if (json.Contains("ebook-pdf", StringComparison.OrdinalIgnoreCase))
            {
                log.Add("responseReceived=" + Shorten(json, 160));
            }
        };

        await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}").ConfigureAwait(true);
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

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

    private static async Task<JsonNode?> WaitForPdfStateAsync(CoreWebView2 core, int timeoutMs)
    {
        JsonNode? state = null;

        await WaitAsync(
            async () =>
            {
                var node = await EvalAsync(core, "window.__readerDebug ? window.__readerDebug.pdfState() : null")
                    .ConfigureAwait(true);

                if (node is JsonObject obj && obj["kind"]?.GetValue<string>() == "pdf"
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

    private static async Task<JsonNode?> EvalAsync(CoreWebView2 core, string script)
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

    private static async Task<bool> EvalBoolAsync(CoreWebView2 core, string script)
    {
        var raw = await core.ExecuteScriptAsync(script).ConfigureAwait(true);

        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> EvalIntAsync(CoreWebView2 core, string script)
    {
        var raw = await core.ExecuteScriptAsync(script).ConfigureAwait(true);

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;
    }

    private static async Task<string> EvalStrAsync(CoreWebView2 core, string script)
    {
        var raw = await core.ExecuteScriptAsync(script).ConfigureAwait(true);

        try
        {
            return JsonNode.Parse(raw)?.GetValue<string>() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static async Task<bool> WaitAsync(Func<Task<bool>> probe, int timeoutMs, int stepMs = 150)
    {
        var clock = Stopwatch.StartNew();

        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (await probe().ConfigureAwait(true))
            {
                return true;
            }

            await Task.Delay(stepMs).ConfigureAwait(true);
        }

        return false;
    }

    private static async Task<string?> RecordShotAsync(Ctx c, CoreWebView2 core, string name)
    {
        try
        {
            using var stream = new MemoryStream();

            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream)
                .ConfigureAwait(true);

            Directory.CreateDirectory(c.ShotsDir);

            var path = Path.Combine(c.ShotsDir, name + ".png");

            await File.WriteAllBytesAsync(path, stream.ToArray()).ConfigureAwait(true);

            c.Shots.Add(path);
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>页面区域的墨迹占比（与 PngStats 同一套 luma &lt; 200 口径，但只看这一页那片像素）。</summary>
    private readonly record struct RegionInk(bool Found, double Ratio, int Width, int Height);

    private static async Task<RegionInk> RegionInkAsync(CoreWebView2 core)
    {
        var node = await EvalAsync(core, CurrentImgJs).ConfigureAwait(true);
        var found = BoolOf(node, "hasBox");
        var x = DblOf(node, "x");
        var y = DblOf(node, "y");
        var w = DblOf(node, "rw");
        var h = DblOf(node, "rh");
        var vw = DblOf(node, "vw");
        var vh = DblOf(node, "vh");

        byte[]? png = null;

        try
        {
            using var stream = new MemoryStream();

            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream)
                .ConfigureAwait(true);

            png = stream.ToArray();
        }
        catch (Exception)
        {
            return new RegionInk(found, -1, (int)w, (int)h);
        }

        return new RegionInk(found, InkInRect(png, x, y, w, h, vw, vh), (int)w, (int)h);
    }

    /// <summary>截图像素里，指定 CSS 矩形（相对视口）的墨迹占比。</summary>
    private static double InkInRect(
        byte[] png, double x, double y, double w, double h, double viewportWidth, double viewportHeight)
    {
        using var stream = new MemoryStream(png, writable: false);
        var decoder = new PngBitmapDecoder(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);

        var pixelWidth = converted.PixelWidth;
        var pixelHeight = converted.PixelHeight;
        var stride = pixelWidth * 4;
        var pixels = new byte[stride * pixelHeight];

        converted.CopyPixels(pixels, stride, 0);

        /* 截图是控件像素，rect 是 CSS 像素：按"图像尺寸 / CSS 视口尺寸"换算比例，
           不假设 DPI 缩放是 1.0。 */
        var sx = viewportWidth > 0 ? pixelWidth / viewportWidth : 1;
        var sy = viewportHeight > 0 ? pixelHeight / viewportHeight : 1;

        var x0 = (int)Math.Max(0, Math.Floor(x * sx));
        var y0 = (int)Math.Max(0, Math.Floor(y * sy));
        var x1 = (int)Math.Min(pixelWidth, Math.Ceiling((x + w) * sx));
        var y1 = (int)Math.Min(pixelHeight, Math.Ceiling((y + h) * sy));

        if (x1 <= x0 || y1 <= y0)
        {
            return -1;
        }

        long ink = 0;
        long total = 0;

        for (var yy = y0; yy < y1; yy++)
        {
            var row = yy * stride;

            for (var xx = x0; xx < x1; xx++)
            {
                var i = row + (xx * 4);
                var luma = ((pixels[i] * 114) + (pixels[i + 1] * 587) + (pixels[i + 2] * 299)) / 1000;

                total++;

                if (luma < 200)
                {
                    ink++;
                }
            }
        }

        return total == 0 ? -1 : (double)ink / total;
    }

    // ==================== 值读取 ====================

    private static int IntOf(JsonNode? node, string key, int fallback = -1)
    {
        if (node is JsonObject obj && obj.TryGetPropertyValue(key, out var value)
            && value is JsonValue v && v.TryGetValue<int>(out var n))
        {
            return n;
        }

        return fallback;
    }

    private static double DblOf(JsonNode? node, string key, double fallback = -1)
    {
        if (node is JsonObject obj && obj.TryGetPropertyValue(key, out var value)
            && value is JsonValue v && v.TryGetValue<double>(out var n))
        {
            return n;
        }

        return fallback;
    }

    private static bool BoolOf(JsonNode? node, string key)
    {
        if (node is JsonObject obj && obj.TryGetPropertyValue(key, out var value)
            && value is JsonValue v && v.TryGetValue<bool>(out var b))
        {
            return b;
        }

        return false;
    }

    private static string StrOf(JsonNode? node, string key)
    {
        if (node is JsonObject obj && obj.TryGetPropertyValue(key, out var value)
            && value is JsonValue v && v.TryGetValue<string>(out var s))
        {
            return s;
        }

        return string.Empty;
    }

    // ==================== 鼠标拖选 / 剪贴板 ====================

    private static async Task<string?> DragSelectAndCaptureAsync(
        CoreWebView2 core, ReaderWindow window, string shotsDir, string name)
    {
        var width = window.Web.ActualWidth;
        var height = window.Web.ActualHeight;

        if (width < 200 || height < 200)
        {
            return null;
        }

        var x1 = (int)(width * 0.15);
        var x2 = (int)(width * 0.85);
        var y1 = (int)(height * 0.25);
        var y2 = (int)(height * 0.45);

        await DispatchMouseAsync(core, "mousePressed", x1, y1, buttons: 1).ConfigureAwait(true);

        for (var step = 1; step <= 8; step++)
        {
            await DispatchMouseAsync(
                core, "mouseMoved", x1 + ((x2 - x1) * step / 8), y1 + ((y2 - y1) * step / 8), buttons: 1)
                .ConfigureAwait(true);

            await Task.Delay(70).ConfigureAwait(true);
        }

        await DispatchMouseAsync(core, "mouseReleased", x2, y2, buttons: 0).ConfigureAwait(true);
        await Task.Delay(700).ConfigureAwait(true);

        try
        {
            using var stream = new MemoryStream();

            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream)
                .ConfigureAwait(true);

            Directory.CreateDirectory(shotsDir);

            var path = Path.Combine(shotsDir, name + ".png");

            await File.WriteAllBytesAsync(path, stream.ToArray()).ConfigureAwait(true);

            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task DispatchMouseAsync(CoreWebView2 core, string type, int x, int y, int buttons)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["x"] = x,
            ["y"] = y,
            ["button"] = "left",
            ["buttons"] = buttons,
            ["clickCount"] = type == "mouseMoved" ? 0 : 1,
        };

        await core.CallDevToolsProtocolMethodAsync(
            "Input.dispatchMouseEvent", JsonSerializer.Serialize(payload)).ConfigureAwait(true);
    }

    private static string ReadClipboardText()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            }
            catch (Exception)
            {
                Thread.Sleep(200);
            }
        }

        return string.Empty;
    }

    private static void SendRealCtrlKey(byte virtualKey)
    {
        const byte vkControl = 0x11;
        const uint keyUp = 0x0002;

        keybd_event(vkControl, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, keyUp, UIntPtr.Zero);
        keybd_event(vkControl, 0, keyUp, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    // ==================== 收尾 ====================

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
            shots.Count == 0 ? null : string.Join(" | ", shots.Select(p => Path.GetFileName(p))),
            checks);

        Directory.CreateDirectory(paths.TempDir);
        File.WriteAllText(
            Path.Combine(paths.TempDir, "t62b-smoke.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return report;
    }

    /// <summary>只记录"点了哪个文件"，绝不真的弹外部程序。</summary>
    private sealed class RecordingShellLauncher : IShellLauncher
    {
        public string? LastOpenedFile { get; private set; }

        public void OpenFolder(string path)
        {
        }

        public void RevealInExplorer(string filePath)
        {
        }

        public void OpenFile(string path) => LastOpenedFile = path;

        public void OpenUrl(string url)
        {
        }
    }

    /// <summary>一次验收跑下来的公共上下文（照 T62SmokeTest 的 Add(...) 口径）。</summary>
    private sealed class Ctx
    {
        public required IBookImportService Importer { get; init; }

        public required IReaderService Reader { get; init; }

        public required IBookRepository Repo { get; init; }

        public required IWebViewEnvironmentProvider Environments { get; init; }

        public required IReadingProgressService Progress { get; init; }

        public required IPdfDocumentService PdfPages { get; init; }

        public required ILoggerFactory Loggers { get; init; }

        public required AppPaths Paths { get; init; }

        public required List<SmokeCheck> Checks { get; init; }

        public required List<string> Shots { get; init; }

        public required string ShotsDir { get; init; }

        public void Add(string name, bool ok, string detail) => Checks.Add(new SmokeCheck(name, ok, detail));

        public ReaderWindow NewWindow(Guid bookId, IShellLauncher shell) => new(
            Reader,
            Environments,
            Progress,
            Loggers.CreateLogger<ReaderWindow>(),
            bookId,
            repository: Repo,
            shell: shell,
            pdfPages: PdfPages);
    }
}
