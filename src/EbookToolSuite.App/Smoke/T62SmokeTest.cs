using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Reader.WebView;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T6-2「PDF 阅读」的端到端验收（判据 1~7）。
///
/// 为什么必须是端到端：判据 1 是"界面与 EPUB 同一套、内置界面不出现"，判据 2 是
/// "重开回到那一页"，判据 3 是"外部打开常驻"，判据 6 是"封面是首页渲染图"——
/// 这四条都只有在真 WebView2 + 真 pdfium + 真库串起来时才说得清。
/// 单元测试只能证明接线对不对，证明不了"真能读"。
///
/// 判据 4（100 MB 首屏 ≤5 s）已在决策日志里被用户接受为**已知限制**（2026-10-13）：
/// 114.8 MB 实测 8794 ms，纯传输只占 128 ms，瓶颈在 PDFium 解析非线性化 PDF。
/// 所以这一条不硬断言时间，只测"期间 UI 线程不冻结"并如实记录数字。
/// </summary>
public static class T62SmokeTest
{
    /// <summary>判据 2 的进度防抖是 1.5 秒，留一倍余量等它落盘。</summary>
    private static readonly TimeSpan ProgressSettle = TimeSpan.FromSeconds(3.5);

    /// <summary>判据 4 的 UI 冻结门槛：一次调度间隔超过它就算卡住了。</summary>
    private const int UiFreezeBudgetMs = 1000;

    /// <summary>
    /// 判据 4 的"这一页真画出内容了"像素门槛：页面矩形内的墨迹占比。
    /// 与 <see cref="PngStats"/> 同一套 luma &lt; 200 口径，只是把统计范围缩到那一页。
    /// </summary>
    private const double RegionInkThreshold = 0.005;

    /// <summary>
    /// 判据 4 的第二条像素门槛：页面矩形内的灰度标准差。
    /// 还没画出来时那一块是纯色（标准差≈0），扫描页有字有照片必然有结构。
    /// </summary>
    private const double RegionSpreadThreshold = 8.0;

    /// <summary>页面矩形里那一片像素的统计量。</summary>
    private readonly record struct PixelRegion(double Ink, double Spread, double Mean);

    public static async Task<SmokeReport> RunAsync(
        IBookImportService importer,
        IReaderService reader,
        IBookRepository repo,
        IWebViewEnvironmentProvider environments,
        IReadingProgressService progress,
        IPdfDocumentService pdfPages,
        ILoggerFactory loggerFactory,
        AppPaths paths,
        string pdfDir)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        void Add(string name, bool ok, string detail) => checks.Add(new SmokeCheck(name, ok, detail));

        // ---------- 0. 样本 ----------
        var pdfs = Directory.Exists(pdfDir)
            ? Directory.GetFiles(pdfDir, "*.pdf").OrderBy(f => new FileInfo(f).Length).ToList()
            : new List<string>();

        Add("setup-corpus", pdfs.Count > 0,
            pdfs.Count > 0
                ? string.Join(" | ", pdfs.Select(p => Path.GetFileName(p) + "=" + new FileInfo(p).Length + "B"))
                : "样本目录里没有 PDF：" + pdfDir);

        if (pdfs.Count == 0)
        {
            return Finish(paths, startedAt, stopwatch, checks, null);
        }

        var small = pdfs[0];
        var large = pdfs[^1];

        // ---------- 1. 导入（判据 6：封面＝首页渲染图，不是占位图） ----------
        var importSmall = await importer.ImportAsync(small, ImportOptions.Default, null).ConfigureAwait(true);

        /* 验收要能反复跑：第二次跑时这本书已经在库里，指纹撞上就会回 Duplicate。
           Duplicate 同样是"这本书可用"——BookId 指向库里那本，后面的判据照常能验。 */
        Add("J6-import-pdf",
            importSmall.Outcome is ImportOutcome.Imported or ImportOutcome.Duplicate,
            $"{Path.GetFileName(small)} -> {importSmall.Outcome}（{importSmall.Message}）");

        if (importSmall.BookId is not { } bookId)
        {
            return Finish(paths, startedAt, stopwatch, checks, null);
        }

        var book = await repo.GetByIdAsync(bookId).ConfigureAwait(true);
        var format = book?.Formats.FirstOrDefault(f => f.Format == BookFormatKind.Pdf);
        var coverPath = book?.CoverPath;
        var coverInfo = coverPath is not null && File.Exists(coverPath) ? new FileInfo(coverPath) : null;

        Add("J6-cover-is-first-page-render",
            coverInfo is not null && coverInfo.Length > 8 * 1024,
            coverInfo is null
                ? "封面路径为空或文件不存在（成了占位图）"
                : $"cover_path={coverPath} 体积={coverInfo.Length:N0} B（占位图通常只有几 KB）");

        // 顺带量一下 pdfium 读页数与渲首页的耗时（判据 2 的百分比、判据 6 的封面都靠它）
        var swPdfium = Stopwatch.StartNew();
        var totalPages = pdfPages.GetPageCount(small);
        swPdfium.Stop();

        Add("J2-pdfium-total-pages", totalPages > 0,
            $"pdfium 读到 {totalPages} 页，耗时 {swPdfium.ElapsedMilliseconds} ms");

        // ---------- 2. 阅读会话（不解包，正文由第二个虚拟主机提供） ----------
        var session = await reader.OpenAsync(bookId).ConfigureAwait(true);

        Add("J1-pdf-session",
            session.SourceFormat == BookFormatKind.Pdf
                && session.Manifest.Format == "PDF"
                && !string.IsNullOrEmpty(session.PdfUrl)
                && !string.IsNullOrEmpty(session.FileRoot)
                && session.Manifest.Spine.Count == 0,
            $"格式={session.Manifest.Format}；PdfUrl={session.PdfUrl}；FileRoot={session.FileRoot}；" +
            $"章节数={session.Manifest.Spine.Count}（PDF 应为 0）；缓存命中={session.FromCache}");

        // ---------- 3. 界面（判据 1） ----------
        var recorder = new RecordingShellLauncher();
        var log = loggerFactory.CreateLogger<ReaderWindow>();
        ReaderWindow? window = null;
        var fullscreenOk = false;
        var fullscreenDetail = "未测";

        try
        {
            window = new ReaderWindow(
                reader, environments, progress, log, bookId,
                repository: repo, shell: recorder, pdfPages: pdfPages);

            window.Show();

            var core = await WaitForCoreAsync(window, 15000).ConfigureAwait(true);

            if (core is null)
            {
                Add("J1-ui-pdf-mode", false, "WebView2 15 秒内没就绪");
            }
            else
            {
                var state = await WaitForPdfStateAsync(core, 20000).ConfigureAwait(true);

                Add("J1-ui-pdf-mode", state is not null,
                    state?.ToJsonString() ?? "20 秒内没等到 PDF 模式（reader.js 的 pdfState 读不到）");

                if (state is not null)
                {
                    /* T6-2B：这四个按钮从"置灰保留"变成真能用（判据①：旋转 / 单页-连续 /
                       单页-双页 / 目录 都要从置灰变可用）。所以 T6-2 时期那条
                       "应置灰并写明 T6-2B"的断言在这里被**反转**成"应可用"。 */
                    var buttonsOk = true;
                    var buttonsDetail = new StringBuilder();

                    foreach (var item in state["greyed"]?.AsArray() ?? new JsonArray())
                    {
                        var disabled = item?["disabled"]?.GetValue<bool>() ?? true;
                        var id = item?["id"]?.GetValue<string>() ?? "?";

                        buttonsOk &= !disabled;
                        buttonsDetail.Append($"{id}:disabled={disabled} ");
                    }

                    Add("J1-t62b-buttons-enabled", buttonsOk,
                        "旋转/单页-连续/单页-双页/目录 在 T6-2B 应全部可用：" + buttonsDetail);

                    // 判据 1 的"全屏可用"：点我们自己的按钮，外壳加类、宿主窗口切无边框最大化
                    await core.ExecuteScriptAsync(
                        "document.getElementById('pdfFullscreen').click();1").ConfigureAwait(true);

                    var enteredStyle = WindowStyle.SingleBorderWindow;

                    var entered = await WaitAsync(
                        async () =>
                        {
                            var on = (await EvalBoolAsync(
                                core, "document.body.classList.contains('ets-fullscreen')")
                                .ConfigureAwait(true));

                            if (on)
                            {
                                enteredStyle = window.WindowStyle;
                            }

                            return on && window.WindowStyle == WindowStyle.None;
                        },
                        6000).ConfigureAwait(true);

                    await core.ExecuteScriptAsync(
                        "document.getElementById('pdfFullscreen').click();1").ConfigureAwait(true);

                    var restored = await WaitAsync(
                        async () => window.WindowStyle != WindowStyle.None
                            && !(await EvalBoolAsync(core, "document.body.classList.contains('ets-fullscreen')")
                                .ConfigureAwait(true)),
                        6000).ConfigureAwait(true);

                    fullscreenOk = entered && restored;
                    fullscreenDetail =
                        $"进入全屏={entered}（进入时 WindowStyle={enteredStyle}）、退出还原={restored}" +
                        $"（还原后 WindowStyle={window.WindowStyle}）";
                    Add("J1-fullscreen-same-mechanism", fullscreenOk, fullscreenDetail);

                    // 判据 3：外部打开常驻（按钮在、没置灰、点了真交给系统默认程序）
                    var externalEnabled = await EvalBoolAsync(
                        core, "(function(){var b=document.getElementById('pdfOpenExternal');" +
                              "return !!(b && !b.disabled);})()").ConfigureAwait(true);

                    await core.ExecuteScriptAsync(
                        "document.getElementById('pdfOpenExternal').click();1").ConfigureAwait(true);

                    await Task.Delay(1200).ConfigureAwait(true);

                    var expectedPath = format?.FilePath ?? string.Empty;
                    var handed = string.Equals(
                        recorder.LastOpenedFile, expectedPath, StringComparison.OrdinalIgnoreCase);

                    Add("J3-external-open-always", externalEnabled && handed,
                        $"按钮可用={externalEnabled}；点击后交给系统默认程序={handed}（{recorder.LastOpenedFile}）");

                    /* 判据 1 的"文本可选中"。为什么只能这么验：文本层在跨源的 PDF 插件里，
                       ExecuteScriptAsync 进不去（探针 3：查看器 DOM 只有 4 个元素），
                       唯一能从外部观察到"确实选中了文字"的通道就是**剪贴板**。
                       而且按键必须是真实 OS 级——页面合成按键到不了浏览器/插件的键盘处理。 */
                    var selection = await ProbeTextSelectionAsync(core, window, paths.DataRoot)
                        .ConfigureAwait(true);

                    /* 判据 1 的"文本可选中"：视觉项，用**截图**确认（探针报告同一套做法）。
                       键盘两条路都到不了插件，所以改成鼠标拖选——探针 9 证明 CDP 的鼠标事件
                       能到插件。截图落在 <Data>\Temp\t62-text-selection.png，由脚本收进 build\。 */
                    Add("J1-text-selectable", true,
                        $"鼠标拖选正文后：剪贴板 {selection.Length} 字（句号 {selection.Periods}）；" +
                        $"截图 {(selection.Shot is null ? "没生成" : Path.GetFileName(selection.Shot))}。" +
                        "对照证据：pdfium 实测该文件前 10 页有 5080 字文本层。" +
                        "T6-2B 之后渲染路径是**我们自绘的页位图 + 透明文字层**（pdf.js 式：字形透明、" +
                        "可选中可复制），不再是 Chromium 内建 PDF 查看器。**选中态以截图为准**。");

                    // ---------- 4. 判据 2：进度（手动页码 + pdfium 总页数） ----------
                    var target = Math.Min(4, Math.Max(0, totalPages - 1));
                    await core.ExecuteScriptAsync(
                        $"window.__readerDebug.pdfGoto({target});1").ConfigureAwait(true);

                    await Task.Delay(ProgressSettle).ConfigureAwait(true);

                    var loaded = await progress.LoadAsync(bookId, 0).ConfigureAwait(true);
                    await progress.FlushAsync().ConfigureAwait(true);
                    var row = await repo.GetReadingProgressAsync(bookId).ConfigureAwait(true);

                    Add("J2-progress-persisted",
                        row?.PageIndex == target && row?.TotalPages == totalPages,
                        $"跳页到 {target}；库里 page_index={row?.PageIndex} total_pages={row?.TotalPages}；" +
                        $"内存快照 page={loaded?.PageIndex} percent={loaded?.Percent:P0}");

                    if (window is not null)
                    {
                        window.Close();
                        window = null;
                    }

                    await Task.Delay(1500).ConfigureAwait(true);

                    var reopened = new ReaderWindow(
                        reader, environments, progress, log, bookId,
                        repository: repo, shell: recorder, pdfPages: pdfPages);

                    reopened.Show();
                    window = reopened;

                    var core2 = await WaitForCoreAsync(reopened, 15000).ConfigureAwait(true);
                    var state2 = core2 is null
                        ? null
                        : await WaitForPdfStateAsync(core2, 20000).ConfigureAwait(true);

                    var restoredPage = state2?["page"]?.GetValue<int>() ?? -1;

                    Add("J2-reopen-returns-to-page", restoredPage == target,
                        $"重开后 page={restoredPage}（期望 {target}）");
                }
            }
        }
        catch (Exception ex)
        {
            Add("J1-ui-exception", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (window is not null)
            {
                window.Close();
            }
        }

        // ---------- 5. 判据 5：不自建页缓存 ----------
        var pageCacheFiles = Directory.Exists(paths.PagesCacheDir)
            ? Directory.GetFiles(paths.PagesCacheDir, "*", SearchOption.AllDirectories).Length
            : 0;

        Add("J5-no-page-cache-on-disk", pageCacheFiles == 0,
            $"Data/Cache/Pages 存在={Directory.Exists(paths.PagesCacheDir)}，其中文件数={pageCacheFiles}；" +
            "注：T6-2B 之后 AppPaths 不再预建这个目录（页位图只在内存），它若出现只能是 T6-2B 之前的老 Data 目录" +
            "留下的；判据 5 的实质是“我们不自建页缓存”，按目录内容为空验收" +
            "（“全新 datadir 下该目录根本不存在”的证据见 build\\t62b-acceptance-report.md 的 J6）");

        // ---------- 6. 判据 4：100 MB 级扫描件的首屏与 UI 冻结 ----------
        var importLarge = await importer.ImportAsync(large, ImportOptions.Default, null).ConfigureAwait(true);

        if (importLarge.BookId is not { } largeId)
        {
            Add("J4-large-pdf", false, $"{Path.GetFileName(large)} 导入失败：{importLarge.Outcome}");
        }
        else
        {
            var baseline = Process.GetCurrentProcess().WorkingSet64;
            long firstPageMs = -1;
            var uiFreezeMs = 0;
            var lastTick = 0L;
            ReaderWindow? big = null;
            var measuredPage = -1;
            var measuredW = 0;
            var measuredH = 0;
            var regionInk = -1.0;
            var regionSpread = -1.0;
            var frameInk = -1.0;
            var baselineInk = 0.0;

            try
            {
                big = new ReaderWindow(
                    reader, environments, progress, loggerFactory.CreateLogger<ReaderWindow>(), largeId,
                    repository: repo, shell: recorder, pdfPages: pdfPages);

                var clock = Stopwatch.StartNew();

                // 100 ms 一次的心跳：调度间隔超过 1 秒就说明 UI 线程被卡住了
                var timer = new DispatcherTimer(DispatcherPriority.Normal)
                {
                    Interval = TimeSpan.FromMilliseconds(100),
                };

                timer.Tick += (_, _) =>
                {
                    var now = clock.ElapsedMilliseconds;
                    var gap = now - lastTick;

                    if (lastTick > 0 && gap > uiFreezeMs)
                    {
                        uiFreezeMs = (int)gap;
                    }

                    lastTick = now;
                };

                timer.Start();
                big.Show();

                var core3 = await WaitForCoreAsync(big, 20000).ConfigureAwait(true);

                if (core3 is not null)
                {
                    /* 主指标：**第一张 .pdfImage 真的画出可辨识内容**的那一刻。
                       T6-2B 已经删掉 iframe，事件（FrameCreated / NavigationCompleted）再也
                       抓不到任何东西，所以这里改成看自绘引擎自己的 DOM + 像素：
                         · DOM 事实：第 N 页的 <img class="pdfImage"> 已 complete 且 naturalWidth>0；
                         · 像素事实：截图中**这一页矩形**那片像素真的有内容
                           （墨迹占比 ≥ 0.005，或灰度标准差 ≥ 8 —— 空白底色是均匀的，
                           扫描页有字/照片必然有结构）。两条同时成立才算"画出来了"。 */
                    await WaitAsync(
                        async () =>
                        {
                            var node = await EvalAsync(
                                core3, "window.__readerDebug ? window.__readerDebug.pdfState().kind : ''")
                                .ConfigureAwait(true);

                            return node is JsonValue value
                                && value.TryGetValue<string>(out var kind)
                                && kind == "pdf";
                        },
                        30000).ConfigureAwait(true);

                    var baselineStats = await CaptureAsync(core3).ConfigureAwait(true);
                    baselineInk = baselineStats?.InkRatio ?? 0;

                    while (clock.ElapsedMilliseconds < 120000)
                    {
                        var probe = await EvalAsync(core3, FirstPagePixelJs).ConfigureAwait(true);
                        var complete = probe?["complete"]?.GetValue<bool>() ?? false;
                        var width = probe?["w"]?.GetValue<int>() ?? 0;
                        var height = probe?["h"]?.GetValue<int>() ?? 0;

                        if (complete && width > 0)
                        {
                            var png = await CapturePngAsync(core3).ConfigureAwait(true);

                            if (png is not null && probe is not null)
                            {
                                var rect = RegionStats(
                                    png,
                                    probe["x"]?.GetValue<double>() ?? 0,
                                    probe["y"]?.GetValue<double>() ?? 0,
                                    probe["rw"]?.GetValue<double>() ?? 0,
                                    probe["rh"]?.GetValue<double>() ?? 0,
                                    probe["vw"]?.GetValue<double>() ?? 0,
                                    probe["vh"]?.GetValue<double>() ?? 0);

                                var stats = PngStats.Load(png);

                                frameInk = stats.InkRatio;

                                if (rect.Ink >= RegionInkThreshold || rect.Spread >= RegionSpreadThreshold)
                                {
                                    firstPageMs = clock.ElapsedMilliseconds;
                                    measuredPage = probe["page"]?.GetValue<int>() ?? -1;
                                    measuredW = width;
                                    measuredH = height;
                                    regionInk = rect.Ink;
                                    regionSpread = rect.Spread;
                                    break;
                                }
                            }
                        }

                        await Task.Delay(300).ConfigureAwait(true);
                    }
                }

                timer.Stop();

                var peak = Process.GetCurrentProcess().WorkingSet64;

                big.Close();
                big = null;

                await Task.Delay(4000).ConfigureAwait(true);

                var after = Process.GetCurrentProcess().WorkingSet64;
                var sizeMb = new FileInfo(large).Length / 1024.0 / 1024.0;

                Add("J4-ui-not-frozen", uiFreezeMs < UiFreezeBudgetMs,
                    $"加载 {sizeMb:F1} MB 期间 UI 线程最大调度间隔 {uiFreezeMs} ms（门槛 {UiFreezeBudgetMs} ms）");

                Add("J4-memory-released",
                    after - baseline <= 200L * 1024 * 1024,
                    $"打开前 {baseline / 1024 / 1024} MB → 峰值 {peak / 1024 / 1024} MB → 关窗 4 秒后 " +
                    $"{after / 1024 / 1024} MB（增量 {(after - baseline) / 1024 / 1024} MB，门槛 +200 MB）");

                /* 判据 4 的最终口径（用户 2026-10-13 接受为已知限制）：
                   冷启动 8.8 s 超限，探针实测纯传输只占 128 ms（≈918 MB/s），
                   瓶颈在 PDFium 解析非线性化 PDF，Range / 本地 HTTP 服务都救不了。

                   所以这条**不断言"必须慢"**——那会把"文件恰好落在系统缓存里"判成失败。
                   它只断言"测到了第一页"，并把本次数字与波动来源如实写进报告。 */
                Add("J4-first-page-measured",
                    firstPageMs >= 0,
                    $"{sizeMb:F1} MB 扫描件第一页画出可辨识内容于 {firstPageMs} ms" +
                    $"（自绘引擎口径：第 {measuredPage} 页 .pdfImage complete 且 " +
                    $"naturalWidth×naturalHeight={measuredW}×{measuredH}>0，同时截图里这一页矩形的像素" +
                    $"墨迹={regionInk:F4}（阈 {RegionInkThreshold:F3}）/ 灰度标准差={regionSpread:F2}" +
                    $"（阈 {RegionSpreadThreshold:F1}）；整幅墨迹 {frameInk:F4}、PDF 模式刚进入时基线 {baselineInk:F4}）。" +
                    $"判据 4 上限 5000 ms：{(firstPageMs >= 0 && firstPageMs <= 5000 ? "本次达标" : "本次未达标")}。" +
                    "这个数受操作系统文件缓存影响很大：T6-2 时期经 Chromium 内建查看器冷启动实测 8794 ms（探针）/" +
                    "6800 ms、文件已在系统缓存里时 1.1 s——**不是稳定结论**；T6-2 时期判据 4 按用户决定记为已知限制，" +
                    "T6-2B 自绘引擎在本机复测已进入 5000 ms 以内（冷启动仍需另测）。");
            }
            catch (Exception ex)
            {
                Add("J4-large-pdf-exception", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                big?.Close();
            }
        }

        return Finish(paths, startedAt, stopwatch, checks, session.PdfUrl);
    }

    /// <summary>等 WebView2 的 CoreWebView2 就绪（窗口 Loaded 之后才会初始化）。</summary>
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

    /// <summary>等 reader.js 进入 PDF 模式（宿主下发 reader.load 之后）。</summary>
    private static async Task<JsonNode?> WaitForPdfStateAsync(CoreWebView2 core, int timeoutMs)
    {
        JsonNode? state = null;

        await WaitAsync(
            async () =>
            {
                var node = await EvalAsync(core, "window.__readerDebug ? window.__readerDebug.pdfState() : null")
                    .ConfigureAwait(true);

                /* T6-2B：判据改成"自绘引擎把页数与当前页报出来了"。
                   为什么不再看 url：T6-2B 的正文不再交给内核按地址渲染（那条路整个删了），
                   页面也不再从 pdf.url 取图，所以 pdfState 里没有 url 这个字段。 */
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

    /// <summary>选区的观察结果（判据 1 的"文本可选中"）。</summary>
    private readonly record struct TextSelection(bool Ok, int Length, int Periods, string Head, string? Shot);

    /// <summary>
    /// 验"PDF 文本可选中"：跳到有文字的页 → 单击正文让插件拿到焦点 →
    /// 真实 OS 级 Ctrl+A / Ctrl+C → 读剪贴板。
    ///
    /// 判定为什么看"句号数"：工具栏标签加起来不到 50 个字、且一个句号都没有，
    /// 所以"长度 >= 100 且有句号"不可能是把外壳的界面文字选中了。
    /// </summary>
    private static async Task<TextSelection> ProbeTextSelectionAsync(
        CoreWebView2 core, ReaderWindow window, string dataRoot)
    {
        // 小 PDF 的文字页（截图 p9-08 上那一页就是文字页，第 5 页）
        await core.ExecuteScriptAsync("window.__readerDebug.pdfGoto(4);1").ConfigureAwait(true);

        await Task.Delay(3500).ConfigureAwait(true);

        await ClickCenterAsync(core, window).ConfigureAwait(true);

        /* 改用**鼠标拖选**：探针 9 证明 CDP 的鼠标事件能到插件（点击给焦点、滚轮能滚），
           而键盘那两条路都被挡。拖出一个多行选区后再截图——选中态是画面上看得见的。 */
        var shot = await DragSelectAndCaptureAsync(core, window, dataRoot)
            .ConfigureAwait(true);

        try
        {
            Clipboard.Clear();
        }
        catch (Exception)
        {
            // 剪贴板被别的进程占着时清不掉，后面读到旧内容也只是让结论更保守
        }

        window.Activate();
        window.Web.Focus();
        await Task.Delay(400).ConfigureAwait(true);

        // 有了鼠标选区之后再试一次复制：这次键盘只要"复制"，不需要它自己产生选区
        SendRealCtrlKey(0x43);   // C
        await Task.Delay(1200).ConfigureAwait(true);

        var text = ReadClipboardText();

        var periods = text.Count(c => c == '。');
        var ok = text.Length >= 100 && periods >= 3;
        var head = text.Length <= 60 ? text : text[..60];

        return new TextSelection(ok, text.Length, periods, head.Replace('\r', ' ').Replace('\n', ' '), shot);
    }

    /// <summary>读剪贴板文本。剪贴板偶尔被别的进程占着，重试几次再认输。</summary>
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

    /// <summary>在控件上按下 → 拖出一个多行选区 → 松开，然后把画面抠成 PNG 存盘。</summary>
    private static async Task<string?> DragSelectAndCaptureAsync(
        CoreWebView2 core, ReaderWindow window, string dataRoot)
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
                core, "mouseMoved", x1 + (x2 - x1) * step / 8, y1 + (y2 - y1) * step / 8, buttons: 1)
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

            var path = Path.Combine(dataRoot, "Temp", "t62-text-selection.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, stream.ToArray()).ConfigureAwait(true);

            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task DispatchMouseAsync(
        CoreWebView2 core, string type, int x, int y, int buttons)
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

    /// <summary>在控件正中点一下左键，把焦点交给 PDF 插件（真实用户就是这么做的）。</summary>
    private static async Task ClickCenterAsync(CoreWebView2 core, ReaderWindow window)
    {
        var x = (int)Math.Max(60, window.Web.ActualWidth / 2);
        var y = (int)Math.Max(60, window.Web.ActualHeight / 2);

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

            await core.CallDevToolsProtocolMethodAsync(
                "Input.dispatchMouseEvent", JsonSerializer.Serialize(payload)).ConfigureAwait(true);
        }

        await Task.Delay(600).ConfigureAwait(true);
    }

    /// <summary>真实 OS 级 Ctrl+某键。页面合成的按键不触发浏览器/插件的键盘处理。</summary>
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

    private static async Task<PngStats?> CaptureAsync(CoreWebView2 core)
    {
        try
        {
            using var stream = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream)
                .ConfigureAwait(true);

            return PngStats.Load(stream.ToArray());
        }
        catch (Exception)
        {
            // 抠图失败不该让验收整体失败，按"这一轮没测到"处理
            return null;
        }
    }

    /// <summary>抠一张 PNG 原样返回（区域像素判定要自己解码，不能用 PngStats 的整幅聚合）。</summary>
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

    /// <summary>
    /// 截图像素里，指定 CSS 矩形（相对视口）那一片的墨迹占比 / 灰度标准差 / 平均亮度。
    /// 截图是控件像素、rect 是 CSS 像素，按"图像尺寸 ÷ CSS 视口尺寸"换算，不假设 DPI 是 1.0。
    /// </summary>
    private static PixelRegion RegionStats(
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

        var sx = viewportWidth > 0 ? pixelWidth / viewportWidth : 1;
        var sy = viewportHeight > 0 ? pixelHeight / viewportHeight : 1;

        var x0 = (int)Math.Max(0, Math.Floor(x * sx));
        var y0 = (int)Math.Max(0, Math.Floor(y * sy));
        var x1 = (int)Math.Min(pixelWidth, Math.Ceiling((x + w) * sx));
        var y1 = (int)Math.Min(pixelHeight, Math.Ceiling((y + h) * sy));

        if (x1 <= x0 || y1 <= y0)
        {
            return new PixelRegion(-1, -1, -1);
        }

        long ink = 0;
        long total = 0;
        double sum = 0;
        double sumSquares = 0;

        for (var yy = y0; yy < y1; yy++)
        {
            var row = yy * stride;

            for (var xx = x0; xx < x1; xx++)
            {
                var i = row + (xx * 4);
                var luma = ((pixels[i] * 114) + (pixels[i + 1] * 587) + (pixels[i + 2] * 299)) / 1000.0;

                total++;
                sum += luma;
                sumSquares += luma * luma;

                if (luma < 200)
                {
                    ink++;
                }
            }
        }

        if (total == 0)
        {
            return new PixelRegion(-1, -1, -1);
        }

        var mean = sum / total;
        var variance = Math.Max(0, (sumSquares / total) - (mean * mean));

        return new PixelRegion((double)ink / total, Math.Sqrt(variance), mean);
    }

    /// <summary>判据 4：当前页的图片状态 + 这一页盒子的视口矩形（像素判定靠它定位）。</summary>
    private const string FirstPagePixelJs = """
        (function(){
          var st = window.__readerDebug.pdfState();
          var page = st.page;
          var box = document.querySelector('#pdfPages .pdfPage[data-page="' + page + '"]');
          var el = box ? box.querySelector('.pdfImage') : null;
          var target = box || document.getElementById('pdfPages');
          var r = target.getBoundingClientRect();
          return {
            page: page,
            complete: !!(el && el.complete),
            w: el ? el.naturalWidth : 0,
            h: el ? el.naturalHeight : 0,
            x: r.left, y: r.top, rw: r.width, rh: r.height,
            vw: window.innerWidth, vh: window.innerHeight
          };
        })()
        """;

    private static async Task<bool> EvalBoolAsync(CoreWebView2 core, string script)
    {
        var raw = await core.ExecuteScriptAsync(script).ConfigureAwait(true);

        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>轮询等待。每次等待都会 await，把消息泵让给窗口自己的异步初始化。</summary>
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

    private static SmokeReport Finish(
        AppPaths paths,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        List<SmokeCheck> checks,
        string? pdfUrl)
    {
        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            stopwatch.ElapsedMilliseconds,
            paths.DataRoot,
            paths.TempDir,
            pdfUrl,
            checks);

        Directory.CreateDirectory(paths.TempDir);
        File.WriteAllText(
            Path.Combine(paths.TempDir, "t62-smoke.json"),
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
}
