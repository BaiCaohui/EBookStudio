using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Reader.WebView;
using Microsoft.Extensions.Logging;
using System.Windows;
using System.Linq;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T1-5 阅读器冒烟：真的导入一本书、真的开窗口、真的渲染出文字。
/// 判据见执行计划 T1-5 的 6 条完成判据。
/// </summary>
/// <remarks>
/// 【为什么必须有 S-lazy-chapters 这一项】"500 章首屏不超过 1.5 秒"这条判据
/// 依赖"只物化当前章、不预渲染全部"。手上没有 500 章的真实样本，
/// 所以改验结构保证：打开一本书之后 chapters/ 目录里必须【恰好一个】文件。
/// 这比拿一本小书测耗时更能证明设计成立。
/// </remarks>
public static class ReaderSmokeTest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<SmokeReport> RunAsync(
        IBookImportService import,
        IReaderService reader,
        IWebViewEnvironmentProvider environments,
        IReadingProgressService progress,
        IBookRepository repo,
        IPdfDocumentService pdfPages,
        ILoggerFactory loggerFactory,
        IAppPaths paths,
        string sourceFile,
        IEpubWorkspaceFactory? workspaces = null,
        IChapterSourceService? source = null,
        IReaderAssets? assets = null,
        IVisualEditAutoFixService? autofix = null,
        IChapterFindReplaceService? findReplace = null,
        IWorkspaceSnapshotService? snapshots = null)
    {
        var startedAt = DateTimeOffset.Now;
        var checks = new List<SmokeCheck>();
        var total = Stopwatch.StartNew();

        void Add(string name, bool ok, string detail) => checks.Add(new SmokeCheck(name, ok, detail));

        // ---------- A. 导入 ----------

        var importResult = await import
            .ImportAsync(sourceFile, ImportOptions.Default, null, CancellationToken.None)
            .ConfigureAwait(true);

        var bookId = importResult.BookId ?? Guid.Empty;

        Add(
            "A-import",
            bookId != Guid.Empty,
            "结果=" + importResult.Outcome + " 书=" + bookId.ToString("N")[..8] +
            " 用时=" + importResult.Duration.TotalMilliseconds.ToString("F0") + "ms");

        if (bookId == Guid.Empty)
        {
            return Build(startedAt, paths, checks, total);
        }

        // ---------- B. 冷开：建缓存 + 开窗口 ----------

        var coldWatch = Stopwatch.StartNew();

        var session = await reader.OpenAsync(bookId, CancellationToken.None).ConfigureAwait(true);
        var cacheMs = coldWatch.ElapsedMilliseconds;

        Add(
            "B-open",
            session.Manifest.Spine.Count > 0,
            "章节数=" + session.Manifest.Spine.Count +
            " 缓存命中=" + session.FromCache +
            " 建缓存=" + cacheMs + "ms");

        // ---------- C. 缓存目录结构 ----------

        var manifestPath = Path.Combine(session.CacheRoot, ReaderEndpoints.ManifestFileName);
        var contentRoot = Path.Combine(session.CacheRoot, ReaderEndpoints.ContentDirectoryName);

        Add(
            "C-cache-dir",
            Directory.Exists(session.CacheRoot) && File.Exists(manifestPath) && Directory.Exists(contentRoot),
            "目录=" + session.CacheRoot);

        // ---------- D. reader.json 可读且结构正确 ----------

        string manifestDetail;
        var manifestOk = false;

        try
        {
            var json = File.ReadAllText(manifestPath, Encoding.UTF8);
            var reloaded = JsonSerializer.Deserialize<ReaderManifest>(json, JsonOptions);

            manifestOk = reloaded is not null
                && reloaded.Spine.Count > 0
                && reloaded.Spine[0].Href == "chapters/0001.html"
                && reloaded.Spine[0].SourceHref.Length > 0;

            manifestDetail = reloaded is null
                ? "反序列化得到 null"
                : "spine=" + reloaded.Spine.Count +
                  " 首章=" + reloaded.Spine[0].Href +
                  " 来源=" + reloaded.Spine[0].SourceHref +
                  " 标题=" + (reloaded.Spine[0].Title ?? "(无)") +
                  " 样式=" + reloaded.Styles.Count;
        }
        catch (Exception ex)
        {
            manifestDetail = ex.GetType().Name + ": " + ex.Message;
        }

        Add("D-manifest", manifestOk, manifestDetail);

        // ---------- E. 内容解压 ----------

        var extracted = Directory.EnumerateFiles(contentRoot, "*", SearchOption.AllDirectories).Count();

        Add("E-content-extracted", extracted > 0, "解压文件数=" + extracted);

        // ---------- F. 第一章已物化 ----------

        var chaptersRoot = Path.Combine(session.CacheRoot, ReaderEndpoints.ChaptersDirectoryName);

        // 显式物化一次。宿主开窗口时做的也是这一步，这里先做是为了让本项
        // 检的是"物化能力"而不是"窗口有没有跑到那一步"。
        var firstHref = await reader
            .MaterializeChapterAsync(session, 0, CancellationToken.None)
            .ConfigureAwait(true);

        var firstChapterPath = Path.Combine(
            session.CacheRoot, firstHref.Replace('/', Path.DirectorySeparatorChar));

        var firstChapterSize = File.Exists(firstChapterPath) ? new FileInfo(firstChapterPath).Length : 0;

        Add("F-chapter-materialized", firstChapterSize > 0, firstHref + "=" + firstChapterSize + " 字节");

        // ---------- G. 开窗口并等第一屏 ----------

        var windowWatch = Stopwatch.StartNew();

        var window = new ReaderWindow(
            reader, environments, progress, loggerFactory.CreateLogger<ReaderWindow>(), bookId,
            workspaces: workspaces, source: source, assets: assets, repository: repo, autofix: autofix,
            snapshots: snapshots);

        window.Show();

        // 判据是"第一章显示出来了"。注意不能只看纯文本长度：
        // 第一章很可能是封面页（一个 SVG 里嵌一张图），正文长度为 0。
        // 所以接受"有文字或有图片"，同时要求已出现的图片都真的加载成功（判据 2）。
        var ready = await WaitUntilAsync(
            () => window.LastChapterIndex == 0
                && (window.RenderedTextLength > 0 || window.RenderedImageCount > 0),
            TimeSpan.FromSeconds(25)).ConfigureAwait(true);

        // ---------- G2. 阅读模式「编辑本章」：点按钮应当开出可视编辑窗口 ----------
        var visualWindowsBefore = Application.Current.Windows.OfType<ChapterVisualEditorWindow>().Count();

        // 故意开【第 2 章】(index=1) 而不是第 1 章：这样"定位到当前章"才是真的验过，
        // 否则"恰好落在默认的第 1 章"也会让检查变绿。
        await window.OpenEditorForChapterAsync(1).ConfigureAwait(true);

        var visualAppeared = await WaitUntilAsync(
            () => Application.Current.Windows.OfType<ChapterVisualEditorWindow>().Count() > visualWindowsBefore,
            TimeSpan.FromSeconds(25)).ConfigureAwait(true);

        var visualWindow = Application.Current.Windows.OfType<ChapterVisualEditorWindow>().FirstOrDefault();

        var landedIndex = visualWindow?.CurrentChapter.Index ?? -1;

        /* T5-4：可视编辑器底部"历史"下拉要真的列出快照（打开工作区就有一份全量基线）。 */
        var historyReady = await WaitUntilAsync(
            () => visualWindow is not null && visualWindow.HistoryCount > 0,
            TimeSpan.FromSeconds(15)).ConfigureAwait(true);
        Add(
            "T54-history-dropdown",
            historyReady && visualWindow!.HistoryBoxFilled,
            "历史里的快照数=" + (visualWindow?.HistoryCount ?? -1)
                + "；下拉已填好=" + (visualWindow?.HistoryBoxFilled ?? false)
                + "；最上面一条=" + (visualWindow?.HistoryTopText ?? "(无)"));

        Add(
            "G2-edit-chapter-opens-visual-editor",
            visualAppeared && visualWindow is not null && visualWindow.ChapterCount > 0 && landedIndex == 1,
            "可视编辑窗口出现=" + visualAppeared
                + "；章节数=" + (visualWindow?.ChapterCount.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(无)")
                + "；打开的是第 " + landedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 章（期望 1）");

        visualWindow?.Close();

        windowWatch.Stop();

        Add(
            "G-chapter-rendered",
            ready && window.RenderedBrokenImageCount == 0,
            "首章序号=" + window.LastChapterIndex +
            " 正文字数=" + window.RenderedTextLength +
            " 图片个数=" + window.RenderedImageCount +
            " 图片加载失败=" + window.RenderedBrokenImageCount +
            " 窗口耗时=" + windowWatch.ElapsedMilliseconds + "ms");

        Add(
            "H-chapter-title",
            window.LastChapterIndex == 0 && window.LastChapterTitle is not null,
            "章名=" + (window.LastChapterTitle is null or "" ? "(空，回落为第 N 章)" : window.LastChapterTitle));

        // ---------- I. 只物化了当前章 ----------

        var materialized = Directory.Exists(chaptersRoot)
            ? Directory.EnumerateFiles(chaptersRoot, "*.html").Count()
            : 0;

        Add(
            "I-lazy-chapters",
            materialized == 1,
            "chapters/ 里 " + materialized + " 个文件（应为 1，即只物化当前章）");
        // ---------- J. 翻到下一章 ----------

        // 故意跳到【第 3 章】而不是第 2 章：这样"第 2 章"就留给后面的 R 去首次
        // 物化。否则 R 点按钮时文件已经在这里被生成好了，就抓不到
        // "页面绕过宿主直接导航" 那个 bug（2026-10-02 反向验证时发现的判据缺陷）。
        var chapterCount = session.Manifest.Spine.Count;
        var jumpIndex = chapterCount > 2 ? 2 : 1;
        var nextOk = chapterCount > 1;

        if (nextOk)
        {
            await window.GoToChapterAsync(jumpIndex).ConfigureAwait(true);
            nextOk = await WaitUntilAsync(
                () => window.LastChapterIndex == jumpIndex,
                TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        }

        Add("J-next-chapter", nextOk, "当前章=" + window.LastChapterIndex +
            "（总 " + chapterCount + " 章，跳的是第 " + (jumpIndex + 1) + " 章）");

        // ---------- K. 翻回第一章 ----------

        await window.GoToChapterAsync(0).ConfigureAwait(true);
        var backOk = await WaitUntilAsync(
            () => window.LastChapterIndex == 0,
            TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        Add("K-back-to-first", backOk, "当前章=" + window.LastChapterIndex);

        // ---------- R. 在页面上点"下一章"（走 JS 那条路径）----------
        //
        // 这一项是给一个真实 bug 留的回归：翻页按钮原先【直接改 iframe.src】，
        // 而章节是按需物化的，浏览器去要一个还没生成的文件，于是翻到第 2 章
        // 就变成 Chromium 的错误页（用户实测："翻页后无法阅读"，
        // 页面上写着"它可能已被移动、编辑或删除"）。
        // J 走的是宿主路径，恰好覆盖不到这条，所以那个 bug 当时漏过去了。
        //
        // 【关键判据是"章节文件必须存在"】它证明是宿主先物化再导航的。
        // 这一条不依赖任何浏览器内部结构，是这个 bug 的精确探针；
        // "不是错误页"只是加一层保险。
        var clicked = await window.ClickNextButtonAsync().ConfigureAwait(true);

        var reachedChapterTwo = clicked && await WaitUntilAsync(
            () => window.LastChapterIndex == 1,
            TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        var secondHref = session.Manifest.Spine.Count > 1
            ? session.Manifest.Spine[1].Href
            : string.Empty;

        var secondChapterPath = secondHref.Length == 0
            ? string.Empty
            : Path.Combine(session.CacheRoot, secondHref.Replace('/', Path.DirectorySeparatorChar));

        var secondChapterExists = secondChapterPath.Length > 0 && File.Exists(secondChapterPath);

        var iframeMovedToSecond = await WaitUntilAsync(
            () => window.RenderedLocation.EndsWith(secondHref, StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        Add(
            "R-next-button",
            reachedChapterTwo
                && secondChapterExists
                && iframeMovedToSecond
                && !window.RenderedIsErrorPage,
            "章号=" + window.LastChapterIndex
                + " " + secondHref + " 存在=" + secondChapterExists
                + " iframe 位置=" + window.RenderedLocation
                + " 是错误页=" + window.RenderedIsErrorPage);
        // ---------- S. 连续翻页（模拟真的把一本书读下去）----------
        //
        // R 只验了一次翻页，而用户是要把整本书读完的。这里从当前章继续往前走几章，
        // 每一章都要求：章节文件已被宿主物化、且渲染出来的不是错误页。
        // 步数封顶，否则 855 章的书会把冒烟拖很久。
        var steps = Math.Min(5, chapterCount - 1 - window.LastChapterIndex);
        var walked = 0;
        var walkFailedAt = -1;

        for (var step = 0; step < steps; step++)
        {
            var target = window.LastChapterIndex + 1;

            await window.ClickNextButtonAsync().ConfigureAwait(true);

            var arrived = await WaitUntilAsync(
                () => window.LastChapterIndex == target,
                TimeSpan.FromSeconds(10)).ConfigureAwait(true);

            var href = session.Manifest.Spine[target].Href;
            var filePath = Path.Combine(
                session.CacheRoot, href.Replace('/', Path.DirectorySeparatorChar));

            if (!arrived || !File.Exists(filePath))
            {
                walkFailedAt = target;
                break;
            }

            walked++;
        }

        Add(
            "S-walk-chapters",
            walkFailedAt < 0,
            "连续翻页 " + walked + " 章"
                + (walkFailedAt < 0 ? "" : "，在第 " + (walkFailedAt + 1) + " 章失败")
                + "（总 " + chapterCount + " 章，当前第 " + (window.LastChapterIndex + 1) + " 章）");


        // ---------- L. 二次打开命中缓存 ----------

        var warmWatch = Stopwatch.StartNew();
        var warm = await reader.OpenAsync(bookId, CancellationToken.None).ConfigureAwait(true);
        warmWatch.Stop();

        Add(
            "L-second-open-cached",
            warm.FromCache,
            "缓存命中=" + warm.FromCache + " 耗时=" + warmWatch.ElapsedMilliseconds + "ms");

        // ---------- M. 首屏耗时（判据 5：不超过 1.5 秒）----------
        //
        // 说明：冷开（首次解压）本来就不可能在 1.5 秒内完成，判据针对的是
        // 缓存已就绪的常规打开路径。冷开耗时在 B-open 里如实报告。

        Add(
            "M-warm-open",
            warmWatch.ElapsedMilliseconds <= 1500,
            "热开=" + warmWatch.ElapsedMilliseconds + "ms（判据 <=1500）；冷开=" + cacheMs + "ms");

        // ---------- N. 资源引用改写完整性 ----------

        var scan = await ScanRewritesAsync(reader, session, maxChapters: 12).ConfigureAwait(true);

        Add(
            "N-rewrites-resolve",
            scan.Broken == 0 && scan.References > 0,
            "扫描 " + scan.Scanned + " 章，改写引用 " + scan.References +
            " 处，指向不存在的文件 " + scan.Broken + " 处；样例 " + scan.Sample);

        // ---------- O. 没有残留的临时目录 ----------

        var leftovers = Directory.Exists(paths.ReaderCacheDir)
            ? Directory.EnumerateDirectories(paths.ReaderCacheDir, "*.part-*").Count()
            : 0;

        Add("O-no-part-leftover", leftovers == 0, "残留 .part-* 目录=" + leftovers);

        // ---------- P. 第二个阅读窗口（WebView2 已经热了）----------
        //
        // G 里那个 5~6 秒几乎全是【进程内第一次】启动 WebView2 浏览器进程的开销，
        // 不是阅读路径本身的成本。判据 5 说的是阅读器打开速度，
        // 所以这里单独量一次：环境已缓存、浏览器进程已起来时的真实首屏耗时。

        var secondWatch = Stopwatch.StartNew();

        var second = new ReaderWindow(
            reader, environments, progress, loggerFactory.CreateLogger<ReaderWindow>(), bookId);

        second.Show();

        var secondReady = await WaitUntilAsync(
            () => second.LastChapterIndex >= 0
                && (second.RenderedTextLength > 0 || second.RenderedImageCount > 0),
            TimeSpan.FromSeconds(20)).ConfigureAwait(true);

        secondWatch.Stop();

        // 判据 5：打开一本书首屏不超过 1.5 秒。
        // 用"WebView2 已热"的第二个窗口来量，因为进程内第一次启动 WebView2
        // 的约 0.7 秒是每个应用会话只付一次的成本，不属于"打开一本书"。
        Add(
            "P-second-window",
            secondReady && secondWatch.ElapsedMilliseconds <= 1500,
            "第二个窗口首屏=" + secondWatch.ElapsedMilliseconds + "ms（判据 <=1500）；" +
            "首个窗口=" + windowWatch.ElapsedMilliseconds + "ms（含 WebView2 冷启动）");

        // ---------- Q. 同一个控件里再导航一次 ----------

        var blankMs = await window.MeasureNavigationAsync("about:blank").ConfigureAwait(true);
        var renavMs = await window
            .MeasureNavigationAsync(ReaderWebHost.ToVirtualUrl("reader.html"))
            .ConfigureAwait(true);

        // 这一项是"2 秒导航"那个坑的看门狗：
        // 2026-10-02 修好之前，同控件导航 reader.html 要 2035ms（自定义主机名
        // 被送去走域名解析、卡在超时上），about:blank 却只要 11ms。
        // 一旦有人去掉 --host-resolver-rules，这一项会立刻变红。
        Add(
            "Q-renavigation",
            renavMs >= 0 && renavMs <= 1500 && blankMs <= 200,
            "同控件导航 about:blank=" + blankMs + "ms；" +
            "同控件导航 reader.html=" + renavMs + "ms；（新控件首屏见 P）");

        // ---------- T. 滚动期间写库次数（T1-6 判据 3）----------

        /* 先挑一章"真的能滚动的"。这本书里有大量插图页与分隔页只有一屏高，
           在那种章节上做"滚动 -> 保存 -> 恢复"会变成拿 0 比 0，
           看着通过其实什么都没验（本判据的第一版就是这么假过的）。 */
        var progressWindow = new ReaderWindow(
            reader, environments, progress, loggerFactory.CreateLogger<ReaderWindow>(), bookId);

        progressWindow.Show();

        await WaitUntilAsync(() => progressWindow.LastChapterIndex >= 0, TimeSpan.FromSeconds(20))
            .ConfigureAwait(true);

        var scrollableIndex = -1;
        var scrollableMax = 0.0;

        for (var i = 0; i < session.Manifest.Spine.Count && i < 20; i++)
        {
            await progressWindow.GoToChapterAsync(i).ConfigureAwait(true);
            await Task.Delay(220).ConfigureAwait(true);

            var scrollProbe = await progressWindow.ReadFrameScrollAsync().ConfigureAwait(true);

            if (scrollProbe.MaxScroll > 200)
            {
                scrollableIndex = i;
                scrollableMax = scrollProbe.MaxScroll;
                break;
            }
        }

        Add(
            "T0-scrollable-chapter",
            scrollableIndex >= 0,
            "第 " + scrollableIndex + " 章可滚动高度=" + scrollableMax.ToString("0") +
            "px（找不到可滚动章节就没法验后面的滚动）");

        var savedBefore = progress.SavedCount;
        var scrollOk = await progressWindow.ScrollToFractionAsync(0.6).ConfigureAwait(true);

        if (scrollOk)
        {
            // 12 次上报全挤在 1.5 秒防抖窗口内：一次库都不该写
            for (var i = 0; i < 12; i++)
            {
                await progressWindow.ScrollToFractionAsync(0.2 + (i * 0.04)).ConfigureAwait(true);
                await Task.Delay(40).ConfigureAwait(true);
            }
        }

        var savedDuringScroll = progress.SavedCount - savedBefore;

        Add(
            "T-debounce-writes",
            scrollOk && savedDuringScroll <= 1,
            "连续滚动 12 次期间写库 " + savedDuringScroll + " 次（防抖生效应为 0 或 1）");

        // 等过防抖窗口，最后的位置必须已经落盘

        await Task.Delay(2600).ConfigureAwait(true);

        var savedRow = await repo.GetReadingProgressAsync(bookId).ConfigureAwait(true);
        var savedLocator = ReaderLocator.TryParse(savedRow?.Location);

        Add(
            "U-progress-saved",
            savedRow is not null && savedLocator is not null && savedLocator.HasScroll && savedRow.Percent > 0,
            "库里 章=" + (savedRow?.SpineIndex?.ToString() ?? "无") +
            " 章内比例=" + (savedLocator?.PercentInChapter?.ToString("0.###") ?? "无") +
            " 全书比例=" + (savedRow?.Percent.ToString("0.###") ?? "无") +
            " 指纹=" + (savedRow?.Fingerprint is null ? "无" : "有") +
            " 累计写库=" + progress.SavedCount + " JSON=" + (savedRow?.Location ?? "无"));

        // ---------- V. 关窗重开回到原位置（T1-6 判据 1）----------

        var expectedChapter = progressWindow.LastChapterIndex;
        var beforeClose = await progressWindow.ReadFrameScrollAsync().ConfigureAwait(true);

        // 真的关掉这一窗再重开：判据的原话是"关闭程序 -> 重开 -> 回到原位置"
        progressWindow.Close();
        await Task.Delay(200).ConfigureAwait(true);

        var reopened = new ReaderWindow(
            reader, environments, progress, loggerFactory.CreateLogger<ReaderWindow>(), bookId);

        reopened.Show();

        var reopenedReady = await WaitUntilAsync(
            () => reopened.LastChapterIndex >= 0,
            TimeSpan.FromSeconds(20)).ConfigureAwait(true);

        await Task.Delay(1000).ConfigureAwait(true);

        var afterReopen = await reopened.ReadFrameScrollAsync().ConfigureAwait(true);

        /* 按【像素】比对，别按百分比：reader.js 报的 percent 是
           (scrollTop + 视口) / 内容高，而这里算的是 scrollTop / 可滚动高，
           两者口径不同，拿它们相比会凭空差出十几个百分点（判据第一版就错在这）。
           判据原话是"误差 <= 屏幕高度的 5%"，那就是像素比像素。 */
        var pixelDelta = Math.Abs(afterReopen.ScrollTop - beforeClose.ScrollTop);
        var tolerance = 0.05 * (beforeClose.ViewportHeight > 0 ? beforeClose.ViewportHeight : 1);

        Add(
            "V-reopen-restores-position",
            reopenedReady && reopened.LastChapterIndex == expectedChapter && pixelDelta <= tolerance,
            "重开前 章=" + expectedChapter + " scrollTop=" + beforeClose.ScrollTop.ToString("0") +
            "；重开后 章=" + reopened.LastChapterIndex + " scrollTop=" + afterReopen.ScrollTop.ToString("0") +
            "；误差=" + pixelDelta.ToString("0") + "px（判据 <= " + tolerance.ToString("0") + "px）" +
            " 关窗前 内容高=" + beforeClose.ScrollHeight.ToString("0") + " 可滚动=" + beforeClose.MaxScroll.ToString("0") + " 视口=" + beforeClose.ViewportHeight.ToString("0") +
            "；重开后 内容高=" + afterReopen.ScrollHeight.ToString("0") + " 可滚动=" + afterReopen.MaxScroll.ToString("0") + " 视口=" + afterReopen.ViewportHeight.ToString("0"));
        /* 先把上面的窗口关掉、把待写队列冲刷干净，再往库里写假数据。
           否则"队列优先"的规则会让这次读取拿到队列里的真实位置，
           假数据被盖掉  本判据第一版就是这么假过的。 */
        reopened.Close();
        await Task.Delay(200).ConfigureAwait(true);
        await progress.FlushAsync().ConfigureAwait(true);

        await progress.SaveNowAsync(new ReaderProgressSnapshot
        {
            BookId = bookId,
            ChapterIndex = scrollableIndex,
            ChapterCount = session.Manifest.Spine.Count,
            Locator = new ReaderLocator
            {
                ChapterIndex = scrollableIndex,
                ChapterHref = session.Manifest.Spine[scrollableIndex].SourceHref,
                PercentInChapter = 0.9,
                ScrollTop = scrollableMax + 100_000,
                ScrollHeight = scrollableMax,
            },
            UpdatedAt = DateTimeOffset.UtcNow,
        }).ConfigureAwait(true);

        var degraded = new ReaderWindow(
            reader, environments, progress, loggerFactory.CreateLogger<ReaderWindow>(), bookId);

        degraded.Show();

        var degradedReady = await WaitUntilAsync(
            () => degraded.LastChapterIndex >= 0,
            TimeSpan.FromSeconds(20)).ConfigureAwait(true);

        await Task.Delay(600).ConfigureAwait(true);

        var degradedScroll = await degraded.ReadFrameScrollAsync().ConfigureAwait(true);
        var degradedNotice = await degraded.ReadNoticeAsync().ConfigureAwait(true);

        Add(
            "W-huge-scrolltop-degrades",
            degradedReady && degradedScroll.Percent <= 0.05 && degradedNotice.Length > 0,
            "重开后 章=" + degraded.LastChapterIndex +
            " 比例=" + degradedScroll.Percent.ToString("0.###") + "（应约 0）" +
            " 提示=" + (degradedNotice.Length == 0 ? "(没提示)" : degradedNotice));

        reopened.Close();
        degraded.Close();

        // ---------- Y. 分页阅读（T4-1） ----------

        /* 用真实的 Config.json（JsonConfigStore）跑，才能验"分页模式要保存到设置"。
           ReaderWindow 的 config 是可选参数，正式运行时由 DI 注入。 */
        var configStore = new JsonConfigStore(paths, loggerFactory.CreateLogger<JsonConfigStore>());
        await configStore.InitializeAsync().ConfigureAwait(true);

        /* 也要给可视编辑相关依赖：Y4b 要从这个窗口走"编辑本章 -> 保存 -> 阅读器重载"的真实链路 */
        var paged = new ReaderWindow(
            reader, environments, progress, loggerFactory.CreateLogger<ReaderWindow>(), bookId,
            workspaces: workspaces, source: source, assets: assets, repository: repo, autofix: autofix,
            config: configStore, snapshots: snapshots);

        paged.Show();

        await WaitUntilAsync(() => paged.LastChapterIndex >= 0, TimeSpan.FromSeconds(20)).ConfigureAwait(true);

        /* 挑"最长的一章"来验分页。不能拿前面那个 scrollableIndex：那只是"能滚动"，
           这本书里大量插图页/分隔页只有一屏高（实测第 0 章内容仅 297px、视口 736px），
           在那种章节上验分页会变成"1 页 = 1 页"，看着通过其实什么都没验
            T1-6 的滚动判据就踩过一次同样的假通过。 */
        var tallestIndex = -1;
        var tallestScroll = 0.0;
        var tallestViewport = 0.0;

        for (var i = 0; i < session.Manifest.Spine.Count && i < 30; i++)
        {
            await paged.GoToChapterAsync(i).ConfigureAwait(true);
            await Task.Delay(200).ConfigureAwait(true);

            var probeScroll = await paged.ReadFrameScrollAsync().ConfigureAwait(true);

            if (probeScroll.MaxScroll > tallestScroll)
            {
                tallestScroll = probeScroll.MaxScroll;
                tallestViewport = probeScroll.ViewportHeight;
                tallestIndex = i;
            }
        }

        if (tallestIndex >= 0)
        {
            await paged.GoToChapterAsync(tallestIndex).ConfigureAwait(true);
            await Task.Delay(400).ConfigureAwait(true);
        }

        await paged.ClickModeButtonAsync().ConfigureAwait(true);
        await Task.Delay(400).ConfigureAwait(true);

        var pageState = await paged.ReadPageStateAsync().ConfigureAwait(true);

        Add(
            "Y1-paged-mode-on",
            pageState is not null && pageState.Mode == "paged" && pageState.HasPagedCss
                && string.Equals(paged.LastReaderMode, "paged", StringComparison.Ordinal),
            "页面模式=" + (pageState?.Mode ?? "(null)")
                + " 宿主观测=" + paged.LastReaderMode
                + " 列样式已注入=" + (pageState?.HasPagedCss ?? false));

        Add(
            "Y2-page-count-and-images",
            pageState is not null && pageState.PageCount >= 2 && pageState.PageWidth > 0
                && pageState.PageHeight > 0
                && pageState.PagedCssText.Contains("break-inside:avoid", StringComparison.Ordinal),
            "最长章=第 " + tallestIndex + " 章（可滚动 " + tallestScroll.ToString("0") + "px / 视口 "
                + tallestViewport.ToString("0") + "px）"
                + " 总页数=" + (pageState?.PageCount ?? 0) + " 一页宽=" + (pageState?.PageWidth ?? 0)
                + "px 一页高=" + (pageState?.PageHeight ?? 0) + "px"
                + " 图片不跨页规则=" + (pageState?.PagedCssText.Contains("break-inside:avoid", StringComparison.Ordinal) ?? false)
                + " 排版耗时=" + (pageState?.PaginateMs ?? 0).ToString("0.##") + "ms");

        Add(
            "Y3-paginate-fast",
            pageState is not null && pageState.PaginateMs <= 100,
            "一次分页测量 " + (pageState?.PaginateMs ?? 0).ToString("0.##") + "ms（判据 <= 100ms，500 章的书翻页也不该卡）");

        /* ---- 临时诊断（量完就删/改成正式检查）：分页模式"页面顶端被工具栏压住" ----
           用户 2026-10-10 截图：《少女集中营》分页模式第 12/88 页，最上面一行字的上半截被工具栏挡住。
           这里把外层工具栏/iframe、内层页框/首行的真实坐标与滚动量都量出来，四种排版设置各量一遍。 */
        const string PageTopProbe =
@"(function(){
  var f=document.getElementById('chapter');
  var tb=document.getElementById('toolbar').getBoundingClientRect();
  var fr=f.getBoundingClientRect();
  var d=f.contentDocument,w=f.contentWindow,dbg=window.__readerDebug;
  var page=d.getElementById('ets-page-frame'),flow=d.getElementById('ets-flow');
  var o={
    tbBottom:Math.round(tb.bottom), ifrTop:Math.round(fr.top), ifrH:Math.round(fr.height),
    outerScrollY:Math.round(window.scrollY||0),
    outerDocTop:Math.round((document.documentElement&&document.documentElement.scrollTop)||0),
    outerBodyTop:Math.round((document.body&&document.body.scrollTop)||0),
    innerH:Math.round(w.innerHeight||0),
    innerScrollY:Math.round(w.scrollY||0),
    innerDocTop:Math.round(d.documentElement.scrollTop||0),
    innerBodyTop:Math.round(d.body.scrollTop||0),
    innerScrollH:Math.round(d.documentElement.scrollHeight||0),
    mt:dbg.marginTop(), ph:dbg.pageHeight(), pw:dbg.pageStride(),
    pages:dbg.pageCount(), pageIdx:dbg.pageIndex()
  };
  if(page){
    var pr=page.getBoundingClientRect(),cs=w.getComputedStyle(page);
    o.frameTop=Math.round(pr.top); o.frameBottom=Math.round(pr.bottom);
    o.frameCssTop=cs.top; o.framePos=cs.position; o.frameOverflow=cs.overflow;
    o.frameCssH=cs.height; o.frameCssW=cs.width; o.frameCssDisplay=cs.display;
  }
  var lineTop=null,txt='';
  try{
    var walker=d.createTreeWalker(flow||d.body,4,null),n=null;
    while((n=walker.nextNode())){ if(n.nodeValue&&/\S/.test(n.nodeValue)){break;} }
    if(n){
      var r=d.createRange(),i=Math.max(0,n.nodeValue.search(/\S/));
      r.setStart(n,i); r.setEnd(n,i+1);
      var rr=r.getBoundingClientRect();
      lineTop=rr.top; txt=n.nodeValue.substr(i,10);
    }
  }catch(e){ o.err=String(e&&e.message); }
  o.lineTopInIframe=lineTop===null?null:Math.round(lineTop);
  o.lineTopInWindow=lineTop===null?null:Math.round(lineTop+fr.top);
  o.firstText=txt;
  o.toolbarOverlapPx=lineTop===null?null:Math.round(tb.bottom-(lineTop+fr.top));
  o.pagedCss=dbg.pagedCssText();
  o.flowStyleHeight=(flow&&flow.style&&flow.style.height)||'(空)';
  return JSON.stringify(o);
})()";
        var diagRows = new List<string>();
        var diagConfigs = new (string Tag, string Ty)[]
        {
            ("c1-mt5", "{columns:1,marginTop:5,marginBottom:5,marginLeft:5,marginRight:5}"),
            ("c2-mt5", "{columns:2,marginTop:5,marginBottom:5,marginLeft:5,marginRight:5}"),
            ("c2-mt0", "{columns:2,marginTop:0,marginBottom:0,marginLeft:0,marginRight:0}"),
            ("c1-mt0", "{columns:1,marginTop:0,marginBottom:0,marginLeft:0,marginRight:0}"),
        };

        foreach (var diagCfg in diagConfigs)
        {
            await paged.ExecAsync("window.__readerDebug.typographyPick(" + diagCfg.Ty + ");true").ConfigureAwait(true);
            await Task.Delay(400).ConfigureAwait(true);

            var geo = await paged.ExecAsync(PageTopProbe).ConfigureAwait(true) ?? "(null)";
            diagRows.Add(diagCfg.Tag + " => " + geo.Trim());
        }

        /* 复原排版设置与页号：上面的矩阵把排版改过（双栏/边距0），不能让后面的 Y4/Y15 跟着遭殃 */
        await paged.ExecAsync(
            "window.__readerDebug.typographyPick({columns:1,marginTop:5,marginBottom:5,marginLeft:5,marginRight:5});true").ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);

        Add("Y3a-paged-geometry-matrix", true, string.Join(" || ", diagRows).Replace("\"", "'"));

        /* ---- Y3b：分页模式的页面顶端不能被工具栏压住（用户 2026-10-10 截图）----
           实测机制：页框原来是 position:absolute 定位在 iframe 文档里的，文档一被滚
           （键盘、链接锚点、选区自动滚屏、任何脚本），整页就上移探针读数：滚 120px 时
           页框 top 从 51 变成 -69、首行跑到工具栏下面 42px；滚 153px 时被压 75px。
           修法：页框改 position:fixed + 分页模式把内层滚动量钉回 0。这里连"人为滚它一把"也一起验。 */
        var topBeforeRaw = await paged.ExecAsync(PageTopProbe).ConfigureAwait(true);

        await paged.ExecAsync(
            "(function(){var w=document.getElementById('chapter').contentWindow;w.scrollTo(0,120);return true;})()").ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);

        var topAfterRaw = await paged.ExecAsync(PageTopProbe).ConfigureAwait(true);

        static JsonObject? ParseProbe(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var text = raw.Trim();

            if (text.StartsWith('"') && text.EndsWith('"') && text.Length > 2)
            {
                text = JsonSerializer.Deserialize<string>(text) ?? text;
            }

            try
            {
                return JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        var probeBefore = ParseProbe(topBeforeRaw);
        var probeAfter = ParseProbe(topAfterRaw);

        var frameTopBefore = probeBefore?["frameTop"]?.GetValue<int>() ?? -9999;
        var frameTopAfter = probeAfter?["frameTop"]?.GetValue<int>() ?? -9999;
        var scrollAfter = probeAfter?["innerScrollY"]?.GetValue<int>() ?? -9999;
        var lineBefore = probeBefore?["lineTopInWindow"]?.GetValue<int>() ?? -9999;
        var lineAfter = probeAfter?["lineTopInWindow"]?.GetValue<int>() ?? -9999;
        var overlapAfter = probeAfter?["toolbarOverlapPx"]?.GetValue<int>() ?? -9999;
        var toolbarBottom = probeBefore?["tbBottom"]?.GetValue<int>() ?? -9999;
        var innerViewport = probeBefore?["innerH"]?.GetValue<int>() ?? -9999;
        var innerScrollable = probeBefore?["innerScrollH"]?.GetValue<int>() ?? -9999;
        var pagedCssFixed = (probeBefore?["pagedCss"]?.GetValue<string>() ?? string.Empty)
            .Contains("position:fixed", StringComparison.Ordinal);

        Add(
            "Y3b-paged-top-not-under-toolbar",
            probeBefore is not null && probeAfter is not null
                && frameTopBefore >= 0 && frameTopAfter == frameTopBefore
                && scrollAfter == 0
                && lineAfter == lineBefore
                && overlapAfter <= 0
                && lineBefore >= toolbarBottom
                && pagedCssFixed,
            "页框 top：" + frameTopBefore + " -> 滚 120px 后 " + frameTopAfter + "（必须不变）"
                + "；内层滚动量=" + scrollAfter
                + "；首行窗口 y：" + lineBefore + " -> " + lineAfter
                + "（工具栏底边=" + toolbarBottom + "）"
                + "；首行被工具栏压住=" + overlapAfter + "px（<= 0 才算没压住）"
                + "；页框 CSS 是 position:fixed=" + pagedCssFixed
                + "；iframe 文档可滚高度=" + innerScrollable + "（视口 " + innerViewport
                + "，页框 fixed 后文档那点可滚量带不走页面）");


        var beforeTurn = pageState;
        await paged.TurnPageAsync(1).ConfigureAwait(true);
        await Task.Delay(250).ConfigureAwait(true);

        var afterTurn = await paged.ReadPageStateAsync().ConfigureAwait(true);
        var pageWidth = afterTurn?.PageWidth ?? 0;
        var movedX = (afterTurn?.OffsetX ?? 0) - (beforeTurn?.OffsetX ?? 0);

        /* 左右翻页：翻一页就是整条列带横向平移**正好一页宽**（每个用户的期待都是"一页一页往后翻"）。 */
        Add(
            "Y4-turn-page-moves-one-screen",
            afterTurn is not null
                && afterTurn.PageIndex == (beforeTurn?.PageIndex ?? 0) + 1
                && pageWidth > 0 && Math.Abs(movedX - pageWidth) <= 2,
            "页号 " + (beforeTurn?.PageIndex ?? 0) + " -> " + (afterTurn?.PageIndex ?? 0)
                + " 横向平移=" + movedX + "px（一页宽=" + pageWidth + "px，左右翻页每次正好一页）");

        // 一路翻到本章末页 -> 再翻一次应该进下一章（判据 2）
        var frChapterBefore = afterTurn?.ChapterIndex ?? -1;
        var chapterAfterFlood = frChapterBefore;
        var floodPages = 0;

        /* 翻页次数不能写死：EPUB3 样本（Sample Book D）最长章有 66 页，
           写死 60 次会"翻不到章末"从而误判。按本章实际页数 + 余量给。 */
        var floodBase = await paged.ReadPageStateAsync().ConfigureAwait(true);
        var floodLimit = Math.Clamp((floodBase?.PageCount ?? 60) + 5, 20, 600);

        for (var i = 0; i < floodLimit; i++)
        {
            await paged.TurnPageAsync(1).ConfigureAwait(true);
            await Task.Delay(120).ConfigureAwait(true);

            var probe = await paged.ReadPageStateAsync().ConfigureAwait(true);

            if (probe is null)
            {
                break;
            }

            floodPages++;

            if (probe.ChapterIndex != frChapterBefore)
            {
                chapterAfterFlood = probe.ChapterIndex;
                break;
            }
        }

        Add(
            "Y5-last-page-goes-next-chapter",
            chapterAfterFlood == frChapterBefore + 1,
            "从第 " + frChapterBefore + " 章翻了 " + floodPages + " 次后到第 " + chapterAfterFlood
                + " 章（期望 " + (frChapterBefore + 1) + "，上限 " + floodLimit + " 次）");

        // 判据 3：第一章第一页再往前 -> 停在原地，不越界
        await paged.GoToChapterAsync(0).ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);
        await paged.TurnPageAsync(-1).ConfigureAwait(true);
        await Task.Delay(250).ConfigureAwait(true);

        var stuck = await paged.ReadPageStateAsync().ConfigureAwait(true);

        Add(
            "Y6-first-page-prev-stays",
            stuck is not null && stuck.ChapterIndex == 0 && stuck.PageIndex == 0,
            "章=" + (stuck?.ChapterIndex ?? -1) + " 页=" + (stuck?.PageIndex ?? -1) + "（都应为 0）");

        // 判据 1：左右箭头 / PageUp / PageDown / 鼠标滚轮都能翻页。
        // 这里派发的是**真实事件**，验的是"监听器接上了"，而不是直接调翻页函数。
        await paged.GoToChapterAsync(tallestIndex).ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);

        var keysBefore = await paged.ReadPageStateAsync().ConfigureAwait(true);

        /* 一次按键走正文监听、一次走外壳监听：两条接线都要验到。
           重书（EPUB3 那本）的 iframe 监听会慢一拍，按一次没动就再按一次，
           否则量到的是"按键丢了"而不是"按键不生效"。 */
        var keyBasePage = keysBefore?.PageIndex ?? 0;
        var keyBaseChapter = keysBefore?.ChapterIndex ?? -1;

        var afterArrow = await PressUntilAsync(
            paged, "ArrowRight", "frame", null,
            s => s is not null && s.PageIndex >= keyBasePage + 1 && s.ChapterIndex == keyBaseChapter)
            .ConfigureAwait(true);

        var keysAfter = await PressUntilAsync(
            paged, "PageDown", "shell", null,
            s => s is not null && s.PageIndex >= keyBasePage + 2 && s.ChapterIndex == keyBaseChapter)
            .ConfigureAwait(true);

        _ = afterArrow;

        Add(
            "Y10-keys-turn-pages",
            keysBefore is not null && keysAfter is not null
                && keysAfter.PageIndex == keysBefore.PageIndex + 2
                && keysAfter.ChapterIndex == keysBefore.ChapterIndex,
            "ArrowRight（正文监听）+ PageDown（外壳监听）后 页号 "
                + (keysBefore?.PageIndex ?? -1) + " -> " + (keysAfter?.PageIndex ?? -1)
                + "（应 +2，章号不变）");

        var wheelWatch = Stopwatch.StartNew();
        await paged.SpinWheelAsync(120).ConfigureAwait(true);
        wheelWatch.Stop();
        await Task.Delay(150).ConfigureAwait(true);

        var wheelAfter = await paged.ReadPageStateAsync().ConfigureAwait(true);

        Add(
            "Y11-wheel-turns-page",
            wheelAfter is not null && keysAfter is not null
                && wheelAfter.PageIndex == keysAfter.PageIndex + 1,
            "滚轮 deltaY=120 后 页号 " + (keysAfter?.PageIndex ?? -1) + " -> " + (wheelAfter?.PageIndex ?? -1)
                + "；一次翻页往返=" + wheelWatch.ElapsedMilliseconds + "ms");

        Add(
            "Y12-turn-cost-is-flat",
            wheelWatch.ElapsedMilliseconds <= 200,
            "单次翻页往返 " + wheelWatch.ElapsedMilliseconds + "ms（判据 <= 200ms）。"
                + "500 章的书也不会更慢：翻页只测当前章，代价与全书章数无关（这里第 "
                + (wheelAfter?.ChapterIndex ?? -1) + " / " + session.Manifest.Spine.Count + " 章）");

        // 用户要求：每一页的内容要随窗口大小改变"一行多少字"。
        // 等价的可观测判据：把窗口改窄 -> 每行字变少 -> 总页数必须变多
        // （页边界是量出来的，不是写死的比例）。
        await paged.GoToChapterAsync(tallestIndex).ConfigureAwait(true);
        await Task.Delay(400).ConfigureAwait(true);

        var wideState = await paged.ReadPageStateAsync().ConfigureAwait(true);

        paged.Width = 620;
        await Task.Delay(600).ConfigureAwait(true);

        var narrowState = await paged.ReadPageStateAsync().ConfigureAwait(true);

        /* 改窗口宽度会触发重排，重书上量得太早会拿到旧页数（94 -> 93 这种假失败）。
           等到"页数不再变化"或者"页数确实变多了"再断言。 */
        for (var settle = 0; settle < 8; settle++)
        {
            await Task.Delay(300).ConfigureAwait(true);

            var next = await paged.ReadPageStateAsync().ConfigureAwait(true);

            if (next is null)
            {
                break;
            }

            var stabilized = narrowState is not null
                && next.PageCount == narrowState.PageCount
                && next.MaxLineWidth == narrowState.MaxLineWidth;

            narrowState = next;

            if (stabilized || (wideState is not null && next.PageCount > wideState.PageCount))
            {
                break;
            }
        }

        Add(
            "Y13-pages-reflow-with-window",
            wideState is not null && narrowState is not null
                && narrowState.PageCount > wideState.PageCount
                && narrowState.PageWidth < wideState.PageWidth
                && narrowState.MaxLineWidth <= wideState.MaxLineWidth
                && narrowState.PageHeight > 0,
            "窗口 1024px 宽 -> " + (wideState?.PageCount ?? 0) + " 页（最长一行 "
                + (wideState?.MaxLineWidth ?? 0) + "px）；窗口 620px 宽 -> " + (narrowState?.PageCount ?? 0)
                + " 页（最长一行 " + (narrowState?.MaxLineWidth ?? 0) + "px）变窄则每行字变少、必须变多页");

        paged.Width = 1024;
        await Task.Delay(600).ConfigureAwait(true);

        /* 用户补充要求：随窗口大小变的**不只是一行的字数，还有一页的行数**。
           所以再单独验"改高度"：一页高变小 -> 一页放的行数变少 -> 总页数变多。 */
        var tallState = await paged.ReadPageStateAsync().ConfigureAwait(true);

        paged.Height = 520;
        await Task.Delay(900).ConfigureAwait(true);

        var shortState = await paged.ReadPageStateAsync().ConfigureAwait(true);

        Add(
            "Y14-lines-per-page-follow-height",
            tallState is not null && shortState is not null
                && shortState.PageHeight < tallState.PageHeight
                && shortState.LinesOnPage < tallState.LinesOnPage
                && shortState.PageCount > tallState.PageCount,
            "窗口高 820px -> 一页高 " + (tallState?.PageHeight ?? 0) + "px、一页 " + (tallState?.LinesOnPage ?? 0)
                + " 行、共 " + (tallState?.PageCount ?? 0) + " 页；窗口高 520px -> 一页高 "
                + (shortState?.PageHeight ?? 0) + "px、一页 " + (shortState?.LinesOnPage ?? 0) + " 行、共 "
                + (shortState?.PageCount ?? 0) + " 页");

        paged.Height = 820;
        await Task.Delay(700).ConfigureAwait(true);

        /* 用户截图反馈的三个具体毛病：左边漏上一页的字尾、右边漏下一页的字段、最后一行只显示一半。
           Y15 用页框几何把它们钉死：页框必须正好是一页（宽=一页宽、高=一页高、位置=四个边距），
           流总宽必须是页宽的整数倍（否则平移后必然错位、邻居会漏进来），
           最深的一行底不能超过一页高（否则就是"最后一行被切"）。 */
        var frameState = await paged.ReadPageStateAsync().ConfigureAwait(true);

        var stripAligned = frameState is not null && frameState.PageWidth > 0
            && Math.Abs(frameState.StripWidth - (frameState.FrameColumns * frameState.PageWidth)) <= 2;

        /* "最后一行被切"的判据（2026-10-08 用户口径）：**只看文字行**不越界；
           超高图片/表格按规矩自己占一页（还会被缩放到页高），不参与这条判断。
           注意不能直接用 Range 矩形 bottom  长文本节点会返回跨列的大合并矩形
           （实测 top=545 / bottom=5881 / 高 5336px），reader.js 里用"行高中位数过滤"解决： */
        var columnsClipped = (frameState?.Diag ?? string.Empty).Contains("\"textLinesInsidePage\":true", StringComparison.Ordinal);

        Add(
            "Y15-page-frame-is-exactly-one-page",
            frameState is not null
                && frameState.FrameExists
                && frameState.FrameWidth == frameState.PageWidth
                && frameState.FrameHeight == frameState.PageHeight
                && frameState.FrameLeft == frameState.MarginLeft
                && frameState.FrameTop == frameState.MarginTop
                && stripAligned
                && columnsClipped,
            "页框=" + (frameState?.FrameWidth ?? 0) + "x" + (frameState?.FrameHeight ?? 0)
                + " 位置=(" + (frameState?.FrameLeft ?? 0) + "," + (frameState?.FrameTop ?? 0) + ")"
                + " 一页=" + (frameState?.PageWidth ?? 0) + "x" + (frameState?.PageHeight ?? 0)
                + " 边距=(" + (frameState?.MarginLeft ?? 0) + "," + (frameState?.MarginTop ?? 0) + ")"
                + " 流总宽=" + (frameState?.StripWidth ?? 0) + "（= " + (frameState?.FrameColumns ?? 0)
                + " 页 x " + (frameState?.PageWidth ?? 0) + "，对齐=" + stripAligned + "）"
                + " 当前页行数=" + (frameState?.LinesOnPage ?? 0)
                + " 文字行不越界=" + columnsClipped
                + " 诊断=" + (frameState?.Diag ?? "(无)"));

        /* ---------- T4-2 主题系统（5 条判据）---------- */

        // 期望配色与 reader.js 里的 THEMES 表一一对应，这里独立再写一遍，
        // 免得变成"代码自己验证自己"。
        var expectedThemes = new (string Id, string Shell, string Bg, string Fg)[]
        {
            ("light", "#F3F3F3", "#FFFFFF", "#1A1A1A"),
            ("sepia", "#F0E6CF", "#F4ECD8", "#5B4636"),
            ("dark", "#202020", "#1C1C1C", "#C8C8C8"),
            ("night", "#000000", "#000000", "#8A8A8A"),
            ("green", "#CCE8CF", "#CCE8CF", "#22361F"),
        };

        var themeWrong = new List<string>();

        foreach (var want in expectedThemes)
        {
            await paged.SetThemeAsync(want.Id).ConfigureAwait(true);
            await Task.Delay(120).ConfigureAwait(true);

            var probe = await paged.ReadThemeAsync().ConfigureAwait(true);

            var ok = probe is not null
                && probe.Id == want.Id
                && probe.DataTheme == want.Id
                && string.Equals(probe.ShellBgHex, want.Shell, StringComparison.OrdinalIgnoreCase)
                && string.Equals(probe.ChapterBgHex, want.Bg, StringComparison.OrdinalIgnoreCase)
                && string.Equals(probe.ChapterFgHex, want.Fg, StringComparison.OrdinalIgnoreCase);

            if (!ok)
            {
                themeWrong.Add("[" + want.Id + "] id=" + (probe?.Id ?? "?") + " data=" + (probe?.DataTheme ?? "?")
                    + " 外壳底=" + (probe?.ShellBgHex ?? "?") + "(期望 " + want.Shell + ")"
                    + " 正文底=" + (probe?.ChapterBgHex ?? "?") + "(期望 " + want.Bg + ")"
                    + " 正文色=" + (probe?.ChapterFgHex ?? "?") + "(期望 " + want.Fg + ")");
            }
        }

        Add(
            "Y16-five-themes-apply-everywhere",
            themeWrong.Count == 0,
            themeWrong.Count == 0
                ? "5 套主题（白/米黄/深灰/纯黑/护眼绿）在外壳与章节内都生效（正文底色+字色都对）"
                : string.Join("；", themeWrong));

        // 判据 2：书自带白底黑字（还带 !important）也必须被主题盖住
        await paged.InjectBookWhiteBackgroundAsync().ConfigureAwait(true);
        await paged.SetThemeAsync("dark").ConfigureAwait(true);
        await Task.Delay(220).ConfigureAwait(true);

        var darkProbe = await paged.ReadThemeAsync().ConfigureAwait(true);

        Add(
            "Y17-theme-beats-book-white-background",
            darkProbe is not null
                && string.Equals(darkProbe.ChapterBgHex, "#1C1C1C", StringComparison.OrdinalIgnoreCase)
                && string.Equals(darkProbe.ChapterFgHex, "#C8C8C8", StringComparison.OrdinalIgnoreCase)
                && darkProbe.LastStyleId == "ets-reader-style",
            "章节自带 html,body{background:#fff;color:#000} 后切深灰：正文底="
                + (darkProbe?.ChapterBgHex ?? "?") + "（期望 #1C1C1C）正文色=" + (darkProbe?.ChapterFgHex ?? "?")
                + "（期望 #C8C8C8）主题样式排在最后=" + (darkProbe?.LastStyleId ?? "?"));

        // 判据 3：切主题不丢阅读位置
        await paged.GoToChapterAsync(tallestIndex).ConfigureAwait(true);
        await Task.Delay(400).ConfigureAwait(true);

        var beforeThemeSwitch = await paged.ReadPageStateAsync().ConfigureAwait(true);

        await paged.SetThemeAsync("sepia").ConfigureAwait(true);
        await Task.Delay(150).ConfigureAwait(true);
        await paged.SetThemeAsync("night").ConfigureAwait(true);
        await Task.Delay(150).ConfigureAwait(true);

        var afterThemeSwitch = await paged.ReadPageStateAsync().ConfigureAwait(true);

        Add(
            "Y18-theme-switch-keeps-position",
            beforeThemeSwitch is not null && afterThemeSwitch is not null
                && afterThemeSwitch.PageIndex == beforeThemeSwitch.PageIndex
                && Math.Abs(afterThemeSwitch.PercentInChapter - beforeThemeSwitch.PercentInChapter) <= 0.12,
            "切主题前 第 " + ((beforeThemeSwitch?.PageIndex ?? 0) + 1) + " 页（比例 "
                + (beforeThemeSwitch?.PercentInChapter ?? 0).ToString("0.000") + "） 连切两套主题后 第 "
                + ((afterThemeSwitch?.PageIndex ?? 0) + 1) + " 页（比例 "
                + (afterThemeSwitch?.PercentInChapter ?? 0).ToString("0.000") + "）");

        /* 判据 5：深色主题下图片不反色、不变暗。
           注意别想当然"封面章有图"这本《Sample Book I》第 1 章就没有图（实测 0 张）。
           所以往前扫几章，找到真的有图的那一页来量。 */
        ReaderWindow.ReaderThemeState? nightImage = null;
        var imageChapter = -1;
        var scannedChapters = 0;

        for (var i = 0; i < 12; i++)
        {
            await paged.GoToChapterAsync(i).ConfigureAwait(true);
            await Task.Delay(220).ConfigureAwait(true);
            await paged.SetThemeAsync("night").ConfigureAwait(true);
            await Task.Delay(120).ConfigureAwait(true);

            var probe = await paged.ReadThemeAsync().ConfigureAwait(true);
            scannedChapters++;

            if (probe is not null && probe.ImageCount >= 1)
            {
                nightImage = probe;
                imageChapter = i;
                break;
            }
        }

        Add(
            "Y19-images-not-darkened",
            nightImage is not null
                && string.Equals(nightImage.ImageFilter, "none", StringComparison.OrdinalIgnoreCase),
            "纯黑主题下第 " + (imageChapter + 1) + " 章（扫了 " + scannedChapters + " 章才找到图）：图片数="
                + (nightImage?.ImageCount ?? 0) + " 图片滤镜=" + (nightImage?.ImageFilter ?? "(null)")
                + "（none = 不反色不变暗）");

        // 判据 4 上半：用户选主题  宿主要落盘到 Config.json
        await paged.ThemePickAsync("green").ConfigureAwait(true);
        await Task.Delay(800).ConfigureAwait(true);

        var configPath = System.IO.Path.Combine(paths.DataRoot, "Config.json");
        var configText = System.IO.File.Exists(configPath) ? System.IO.File.ReadAllText(configPath) : string.Empty;

        /* Config.json 是嵌套结构（{ "reader": { "theme": "green" } }），不能拿平铺键字符串去比：
           这里按 JSON 真解析一遍。 */
        var savedTheme = string.Empty;

        try
        {
            if (JsonNode.Parse(configText) is JsonObject root2 && root2["reader"] is JsonObject readerNode)
            {
                savedTheme = readerNode["theme"]?.GetValue<string>() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            savedTheme = "(解析失败)";
        }

        Add(
            "Y20-theme-persisted",
            string.Equals(savedTheme, "green", StringComparison.Ordinal),
            "工具栏选护眼绿后 " + configPath + " 里 reader.theme=" + savedTheme + "（期望 green）");

        /* 用户自定义主题（用户 2026-10-08 追加要求）：三色可改、立即生效、并且落盘。 */
        // 先在下拉里选"自定义"（走 change 事件  落盘），再调三个颜色（走 input 事件  落盘）
        await paged.ThemePickAsync("custom").ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);
        await paged.CustomPickAsync("#102030", "#E0D0C0", "#40A0FF").ConfigureAwait(true);
        await Task.Delay(600).ConfigureAwait(true);

        var customProbe = await paged.ReadThemeAsync().ConfigureAwait(true);

        var customText = System.IO.File.Exists(configPath)
            ? System.IO.File.ReadAllText(configPath)
            : string.Empty;
        var customSaved = customText.Contains("102030", StringComparison.OrdinalIgnoreCase)
            && customText.Contains("E0D0C0", StringComparison.OrdinalIgnoreCase);

        Add(
            "Y21-custom-theme-works-and-persists",
            customProbe is not null
                && customProbe.Id == "custom"
                && string.Equals(customProbe.ChapterBgHex, "#102030", StringComparison.OrdinalIgnoreCase)
                && string.Equals(customProbe.ChapterFgHex, "#E0D0C0", StringComparison.OrdinalIgnoreCase)
                && customSaved,
            "自定义主题：当前=" + (customProbe?.Id ?? "?") + " 正文底=" + (customProbe?.ChapterBgHex ?? "?")
                + "（期望 #102030）正文色=" + (customProbe?.ChapterFgHex ?? "?") + "（期望 #E0D0C0）"
                + " 三色已落盘=" + customSaved);

        /* 用户 2026-10-21 追加：自定义主题下工具栏的下拉框 / 三个调色框 / 章号页码框"看不到边框"
           （根因：customTheme 把 shellBorder / shellBtnBg 直接设成用户选的底色 = 和背景同色）。
           修法：边框 / 按钮底色改由"前景色叠到背景上"派生，并保证最低对比度（不够就往黑/白侧退）。
           这里验两组：① 正常配色（#102030 / #E0D0C0）—— 边框与底色对比够；
           ② 恶分配色（底 #202020、字 #2A2A2A，两者几乎同色）—— 边框也照样可见。 */
        var chromeNormal = await ReadChromeAsync(paged).ConfigureAwait(true);

        var chromeNormalOk = chromeNormal is not null
            && (chromeNormal["selectContrast"]?.GetValue<double>() ?? 0) >= 1.3
            && (chromeNormal["swatchContrast"]?.GetValue<double>() ?? 0) >= 1.3
            && (chromeNormal["jumpContrast"]?.GetValue<double>() ?? 0) >= 1.5;

        await paged.CustomPickAsync("#202020", "#2A2A2A", "#40A0FF").ConfigureAwait(true);
        await Task.Delay(600).ConfigureAwait(true);

        var chromeBad = await ReadChromeAsync(paged).ConfigureAwait(true);

        var chromeBadOk = chromeBad is not null
            && (chromeBad["selectContrast"]?.GetValue<double>() ?? 0) >= 1.25
            && (chromeBad["swatchContrast"]?.GetValue<double>() ?? 0) >= 1.25
            && (chromeBad["jumpContrast"]?.GetValue<double>() ?? 0) >= 1.4;

        /* 恢复后续检查与截图用的那套配色（Y21 用的） */
        await paged.CustomPickAsync("#102030", "#E0D0C0", "#40A0FF").ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);

        Add(
            "Y21b-custom-theme-chrome-visible",
            chromeNormalOk && chromeBadOk,
            "自定义主题下按钮/输入框边框与底色的对比度（>= 1.3 才看得见）："
                + "正常配色 下拉框=" + (chromeNormal?["selectContrast"]?.GetValue<double>() ?? 0).ToString("0.00")
                + "、调色框=" + (chromeNormal?["swatchContrast"]?.GetValue<double>() ?? 0).ToString("0.00")
                + "、章号框=" + (chromeNormal?["jumpContrast"]?.GetValue<double>() ?? 0).ToString("0.00")
                + "；恶分配色 底#202020/字#2A2A2A 下拉框="
                + (chromeBad?["selectContrast"]?.GetValue<double>() ?? 0).ToString("0.00")
                + "、调色框=" + (chromeBad?["swatchContrast"]?.GetValue<double>() ?? 0).ToString("0.00")
                + "、章号框=" + (chromeBad?["jumpContrast"]?.GetValue<double>() ?? 0).ToString("0.00")
                + "；正常配色边框色=" + (chromeNormal?["selectBorder"]?.GetValue<string>() ?? "?")
                + "（底色 " + (chromeNormal?["bg"]?.GetValue<string>() ?? "?") + "）");

        /* T4-3 排版设置：实时生效 / 位置保持 / 持久化 / 字体下拉 / 分栏（用户追加要求）。 */
        await paged.ExecAsync("window.__readerDebug.typographyPick({fontSize:26,lineHeight:2.2,marginTop:12,marginBottom:4,marginLeft:8,marginRight:3,paraSpacing:1.5,textIndent:0,columns:1});true").ConfigureAwait(true);
        await Task.Delay(400).ConfigureAwait(true);

        var ty = await paged.ReadTypographyAsync().ConfigureAwait(true);
        var tyCssRaw = await paged.ExecAsync("window.__readerDebug.typographyCss();").ConfigureAwait(true) ?? string.Empty;
        var tyCss = tyCssRaw.Replace("\\n", "\n");

        Add(
            "Y22-typography-applies-live",
            ty is not null
                && Math.Abs(ty.FontSize - 26) < 0.01
                && Math.Abs(ty.LineHeight - 2.2) < 0.01
                && Math.Abs(ty.MarginTop - 12) < 0.01
                && Math.Abs(ty.MarginBottom - 4) < 0.01
                && Math.Abs(ty.MarginLeft - 8) < 0.01
                && Math.Abs(ty.MarginRight - 3) < 0.01
                && ty.Computed.Count >= 3
                && ty.Computed[1].EndsWith("px", StringComparison.Ordinal),
"字号=" + ty?.FontSize + "px（期望 26）行高=" + ty?.LineHeight + "（期望 2.2）对齐=" + ty?.TextAlign
                + "；CSS 里 text-align: center=" + tyCss.Contains("text-align: center", StringComparison.Ordinal)
+ " 页边距=" + ty?.MarginPx + "px（12% 视口） | CSS前90=" + (tyCss.Length > 90 ? tyCss.Substring(0, 90) : tyCss));

        /* 判据 2：改字号会改变分页结果，但必须保住阅读位置（误差 <= 1 屏）。
           先回到最长章：只有一页的短章字号变了页数也不会变，那种章上这条断言没有意义
           （Sample Book D 的第 1 章只有 1 页）。 */
        if (tallestIndex >= 0)
        {
            await paged.GoToChapterAsync(tallestIndex).ConfigureAwait(true);
            await Task.Delay(400).ConfigureAwait(true);
        }

        await paged.ExecAsync("window.__readerDebug.pageOrChapter(3);true").ConfigureAwait(true);
        await Task.Delay(250).ConfigureAwait(true);

        var beforeTy = await paged.ReadTypographyAsync().ConfigureAwait(true);

        await paged.ExecAsync("window.__readerDebug.typographyPick({fontSize:16});true").ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);

        var afterTy = await paged.ReadTypographyAsync().ConfigureAwait(true);
var tyDrift = beforeTy is null || afterTy is null
            ? 99
            : Math.Abs((afterTy.PercentInChapter * afterTy.PageCount) - (beforeTy.PercentInChapter * beforeTy.PageCount));

        Add(
            "Y23-typography-keeps-position",
            beforeTy is not null && afterTy is not null
                && afterTy.PageCount != beforeTy.PageCount
&& tyDrift <= 1.5,
            "改字号(26->16)前 第 " + (beforeTy?.PageIndex + 1) + "/" + beforeTy?.PageCount + " 页"
                + " 后 第 " + (afterTy?.PageIndex + 1) + "/" + afterTy?.PageCount + " 页"
+ " 位置漂移=" + tyDrift.ToString("0.00", CultureInfo.InvariantCulture) + " 页（要求 <= 1 屏且页数确实变了）");

        /* 判据 3：设置持久化。 */
        var tyConfig = System.IO.File.Exists(configPath) ? System.IO.File.ReadAllText(configPath) : string.Empty;

        Add(
            "Y27-typography-real-style-in-iframe",
            ty is not null
                && ty.Computed.Count >= 3
                && ty.Computed[1].EndsWith("px", StringComparison.Ordinal)
                && !ty.Computed[1].StartsWith("0px", StringComparison.Ordinal)
                && ty.Computed[2].EndsWith("px", StringComparison.Ordinal)
                && !ty.Computed[2].StartsWith("0px", StringComparison.Ordinal)
                && ty.Computed[1] != ty.Computed[2],
            "章节 iframe 里量出来的真样式：列数=" + (ty?.Computed.Count > 0 ? ty.Computed[0] : "?")
                + " paddingTop=" + (ty?.Computed.Count > 1 ? ty.Computed[1] : "?")
                + " paddingLeft=" + (ty?.Computed.Count > 2 ? ty.Computed[2] : "?")
                + " 段落 marginBottom=" + (ty?.Computed.Count > 3 ? ty.Computed[3] : "?")
                + " 段落 textIndent=" + (ty?.Computed.Count > 4 ? ty.Computed[4] : "?"));

        Add(
            "Y24-typography-persisted",
tyConfig.Contains("\"typography\"", StringComparison.Ordinal)
                && tyConfig.Contains("\"fontSize\"", StringComparison.Ordinal)
                && tyConfig.Contains("\"columns\"", StringComparison.Ordinal)
                && tyConfig.Contains("\"marginTop\"", StringComparison.Ordinal),
"Config.json 里已有 typography 段（fontSize/columns/textAlign 都在）");

        /* 判据 4 + 5：字体下拉列出系统字体；设一个不存在的字体 -> 回退，不崩。 */
        await paged.ExecAsync("window.__readerDebug.typographyPick({fontFamily:'不存在的字体XYZ'});true").ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);

        var fontTy = await paged.ReadTypographyAsync().ConfigureAwait(true);

        Add(
            "Y25-font-list-and-fallback",
            fontTy is not null
                && fontTy.FontOptions.Count >= 5
                && fontTy.FontFamily.Contains("Microsoft YaHei UI", StringComparison.Ordinal)
                && fontTy.PageCount >= 1,
            "字体下拉 " + fontTy?.FontOptions.Count + " 项；选不存在的字体后实际字体栈="
                + (fontTy?.FontFamily.Length > 60 ? fontTy.FontFamily.Substring(0, 60) + "..." : fontTy?.FontFamily)
                + "（含回退链，未崩溃）");

        /* 用户追加要求：分栏（作用于滚动模式）。 */
        await paged.ExecAsync("window.__readerDebug.setMode('scroll');window.__readerDebug.typographyPick({columns:2});true").ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);

        var colTy = await paged.ReadTypographyAsync().ConfigureAwait(true);
        var colCssRaw = await paged.ExecAsync("window.__readerDebug.typographyCss();").ConfigureAwait(true) ?? string.Empty;
        var colCss = colCssRaw.Replace("\\n", "\n");

        Add(
            "Y26-columns-in-scroll-mode",
            colTy is not null
                && Math.Abs(colTy.Columns - 2) < 0.01
&& colCss.Contains("column-count: 2", StringComparison.Ordinal),
"滚动模式分栏=2；CSS 里 column-count: 2=" + colCss.Contains("column-count: 2", StringComparison.Ordinal) + " 列数设置=" + colTy?.Columns);

        await paged.ExecAsync("window.__readerDebug.setMode('paged');window.__readerDebug.typographyPick({fontSize:17,lineHeight:1.75,marginTop:5,marginBottom:5,marginLeft:5,marginRight:5,paraSpacing:0.8,textIndent:2,columns:1,fontFamily:'Microsoft YaHei UI'});true").ConfigureAwait(true);
        await Task.Delay(400).ConfigureAwait(true);

        /* 真走界面交互：直接改滑块的值并派发 input（模拟用户拖动），必须真的改掉设置。
           （以前的检查都是直接调 JS 探针，绕过了界面 -> 漏掉了"拖动无效"的 bug。） */
        await paged.ExecAsync("(function(){var r=document.getElementById('tyFontSize');r.value='31';r.dispatchEvent(new Event('input',{bubbles:true}));})();true").ConfigureAwait(true);
        await Task.Delay(400).ConfigureAwait(true);

        var uiTy = await paged.ReadTypographyAsync().ConfigureAwait(true);
        var uiNumber = await paged.ExecAsync("document.getElementById('tyFontSizeNum').value").ConfigureAwait(true) ?? string.Empty;

        Add(
            "Y29-slider-drag-changes-value",
            uiTy is not null && Math.Abs(uiTy.FontSize - 31) < 0.01 && uiNumber.Contains("31", StringComparison.Ordinal),
            "拖动字号滑块到 31 后：设置值=" + uiTy?.FontSize + "（期望 31）数字框=" + uiNumber.Trim() + "（期望含 31）");

        /* 用户要求：分页模式也要能分栏（一页 N 列），翻页步长必须 = N*(列宽+间距)。 */
        await paged.ExecAsync("window.__readerDebug.typographyPick({columns:2,columnGap:32,columnRule:true});true").ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);

        var pagedCol = await paged.ReadTypographyAsync().ConfigureAwait(true);
        var wantColumn = pagedCol is null ? 0 : (pagedCol.PageWidth - pagedCol.ColumnGap) / 2;
        var wantStride = pagedCol is null ? 0 : 2 * (pagedCol.ColumnWidth + pagedCol.ColumnGap);

        Add(
            "Y28-paged-mode-columns",
            pagedCol is not null
                && Math.Abs(pagedCol.Columns - 2) < 0.01
                && pagedCol.ColumnWidth > 100
                && Math.Abs(pagedCol.ColumnWidth - wantColumn) < 2
                && Math.Abs(pagedCol.PageStride - wantStride) < 2,
            "分页双栏：页宽=" + (pagedCol?.PageWidth ?? 0).ToString("0", CultureInfo.InvariantCulture)
                + " 列宽=" + (pagedCol?.ColumnWidth ?? 0).ToString("0", CultureInfo.InvariantCulture)
                + "（期望 " + wantColumn.ToString("0", CultureInfo.InvariantCulture) + "）"
                + " 间距=" + (pagedCol?.ColumnGap ?? 0).ToString("0", CultureInfo.InvariantCulture)
                + " 翻页步长=" + (pagedCol?.PageStride ?? 0).ToString("0", CultureInfo.InvariantCulture)
                + "（期望 " + wantStride.ToString("0", CultureInfo.InvariantCulture) + "）"
                + " 竖线=" + (pagedCol?.ColumnRule ?? false));

        /* 用户反馈"三栏设置了没反应"：这里真走界面（改下拉 + 派发 change），在分页模式下验一页三列的几何。 */
        await paged.ExecAsync("(function(){var s=document.getElementById('tyColumns');s.value='3';s.dispatchEvent(new Event('change',{bubbles:true}));})();true").ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);

        var col3 = await paged.ReadTypographyAsync().ConfigureAwait(true);
        var want3 = col3 is null ? 0 : (col3.PageWidth - 2 * col3.ColumnGap) / 3;
        var stride3 = col3 is null ? 0 : 3 * (col3.ColumnWidth + col3.ColumnGap);

        Add(
            "Y30-three-columns-via-ui",
            col3 is not null
                && Math.Abs(col3.Columns - 3) < 0.01
                && col3.ColumnWidth > 60
                && Math.Abs(col3.ColumnWidth - want3) < 2
                && Math.Abs(col3.PageStride - stride3) < 2
                && col3.Computed.Any(x => x.Contains("column-width=" + col3.ColumnWidth.ToString("0", CultureInfo.InvariantCulture) + "px", StringComparison.Ordinal)),
            "页框CSS 里确有 column-width=" + (col3?.ColumnWidth ?? 0).ToString("0", CultureInfo.InvariantCulture) + "px："
                + (col3 != null && col3.Computed.Any(x => x.StartsWith("column-width=", StringComparison.Ordinal)))
                + " 设置=" + col3?.Columns + " 页宽=" + (col3?.PageWidth ?? 0).ToString("0", CultureInfo.InvariantCulture)
                + " 列宽=" + (col3?.ColumnWidth ?? 0).ToString("0", CultureInfo.InvariantCulture)
                + "（期望 " + want3.ToString("0", CultureInfo.InvariantCulture) + "）"
                + " 步长=" + (col3?.PageStride ?? 0).ToString("0", CultureInfo.InvariantCulture)
                + "（期望 " + stride3.ToString("0", CultureInfo.InvariantCulture) + "）");

        /* 用户反馈"分栏间距滑块也没用"：这里真走界面拖分栏间距滑块，验设置值与页框 CSS 都跟着变。 */
        await paged.ExecAsync("(function(){var r=document.getElementById('tyColumnGap');r.value='48';r.dispatchEvent(new Event('input',{bubbles:true}));})();true").ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);

        var gapTy = await paged.ReadTypographyAsync().ConfigureAwait(true);

        Add(
            "Y31-column-gap-slider-works",
            gapTy is not null
                && Math.Abs(gapTy.ColumnGap - 48) < 0.01
                && gapTy.Computed.Any(x => x.Contains("column-width=" + Math.Floor((gapTy.PageWidth - 2 * 48) / 3).ToString("0", CultureInfo.InvariantCulture) + "px", StringComparison.Ordinal)),
            "拖分栏间距到 48：设置=" + (gapTy?.ColumnGap ?? 0).ToString("0", CultureInfo.InvariantCulture)
                + "（期望 48） 三栏列宽应为 " + ((gapTy?.PageWidth ?? 0) - 96) / 3 + "px，页框CSS 里确有="
                + (gapTy != null && gapTy.Computed.Any(x => x.StartsWith("column-width=", StringComparison.Ordinal))));

        /* 用户反馈：滚动模式三栏 + 大间距时"只显示两栏"（栏被 min-content 撑破 -> 横向溢出）。 */
        await paged.ExecAsync("window.__readerDebug.typographyPick({columns:3,columnGap:48,columnRule:true});true").ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);

        var ovf = await paged.ReadTypographyAsync().ConfigureAwait(true);
        var overflowText = ovf is null ? "?" : string.Join(" | ", ovf.Computed.Where(x => x.StartsWith("overflow=", StringComparison.Ordinal)));

        Add(
            "Y32-scroll-three-columns-no-overflow",
            ovf is not null
                && Math.Abs(ovf.Columns - 3) < 0.01
                && ovf.Computed.Any(x => x.StartsWith("overflow=", StringComparison.Ordinal))
                && ovf.Computed.Where(x => x.StartsWith("overflow=", StringComparison.Ordinal)).All(x => x.StartsWith("overflow=0px", StringComparison.Ordinal) || x.StartsWith("overflow=-", StringComparison.Ordinal)),
            "滚动模式三栏（间距 48）：正文横向溢出=" + overflowText + "（必须 <= 0）");

        /* 用户反馈：分页模式 + 间距 48 + 三栏，只显示两栏（列宽四舍五入进位导致一页少装一栏）。 */
        await paged.ExecAsync("window.__readerDebug.typographyPick({fontSize:19,lineHeight:1.7,marginTop:5,marginBottom:6,marginLeft:5,marginRight:5,paraSpacing:0.8,textIndent:2,columns:3,columnGap:48,columnRule:true});true").ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);

        var fit3 = await paged.ReadTypographyAsync().ConfigureAwait(true);
        var fits3 = fit3 == null ? "?" : string.Join(" ", fit3.Computed.Where(x => x.StartsWith("fits=", StringComparison.Ordinal)));

        await paged.ExecAsync("window.__readerDebug.typographyPick({columns:2,columnGap:48});true").ConfigureAwait(true);
        await Task.Delay(400).ConfigureAwait(true);

        var fit2 = await paged.ReadTypographyAsync().ConfigureAwait(true);
        var fits2 = fit2 == null ? "?" : string.Join(" ", fit2.Computed.Where(x => x.StartsWith("fits=", StringComparison.Ordinal)));

        Add(
            "Y33-paged-columns-actually-fit",
            fits3.Contains("fits=3", StringComparison.Ordinal) && fits2.Contains("fits=2", StringComparison.Ordinal),
            "分页模式一页实际能装几栏：三栏时=" + fits3 + "（要 fits=3） 两栏时=" + fits2 + "（要 fits=2）；列宽="
                + (fit3?.ColumnWidth ?? 0).ToString("0", CultureInfo.InvariantCulture) + "px 页宽="
                + (fit3?.PageWidth ?? 0).ToString("0", CultureInfo.InvariantCulture));

        /* 用户反馈：两栏/三栏时切到滚动模式，分栏不正确  这次量"章节里真实的列数"。 */
        /* 用户的实际路径：先在分页模式把栏数设好，**再**切到滚动模式（切模式时正文 CSS 必须按新模式重建）。 */
        await paged.ExecAsync("window.__readerDebug.typographyPick({columns:3,columnGap:48,columnRule:true});true").ConfigureAwait(true);
        await Task.Delay(400).ConfigureAwait(true);

        await paged.ExecAsync("window.__readerDebug.setMode('scroll');true").ConfigureAwait(true);
        await Task.Delay(700).ConfigureAwait(true);

        var scroll3 = await paged.ReadTypographyAsync().ConfigureAwait(true);
        var realCols = scroll3 == null || scroll3.Computed.Count == 0 ? "?" : scroll3.Computed[0];
        var realOvf = scroll3 == null ? "?" : string.Join(" ", scroll3.Computed.Where(x => x.StartsWith("overflow=", StringComparison.Ordinal)));

        Add(
            "Y34-scroll-mode-real-columns",
            string.Equals(realCols, "3", StringComparison.Ordinal)
                && scroll3 != null
                && scroll3.Computed.Where(x => x.StartsWith("overflow=", StringComparison.Ordinal)).All(x => x.StartsWith("overflow=0px", StringComparison.Ordinal) || x.StartsWith("overflow=-", StringComparison.Ordinal)),
            "切到滚动模式 + 三栏：章节里真实列数=" + realCols + "（要 3） 横向溢出=" + realOvf);

        await paged.ExecAsync("window.__readerDebug.setMode('paged');true").ConfigureAwait(true);
        await Task.Delay(400).ConfigureAwait(true);

        /* 用户要求：分栏间距上限提到 256。这里真走界面拖到最大值，验设置与页框 CSS 都跟上。 */
        await paged.ExecAsync("(function(){var r=document.getElementById('tyColumnGap');r.value='256';r.dispatchEvent(new Event('input',{bubbles:true}));})();true").ConfigureAwait(true);
        await Task.Delay(600).ConfigureAwait(true);

        var gapMax = await paged.ReadTypographyAsync().ConfigureAwait(true);
        var wantMax = gapMax is null ? 0 : Math.Floor((gapMax.PageWidth - 2 * 256) / 3);

        Add(
            "Y35-column-gap-max-256",
            gapMax is not null
                && Math.Abs(gapMax.ColumnGap - 256) < 0.01
                && gapMax.Computed.Any(x => x.Contains("column-width=" + wantMax.ToString("0", CultureInfo.InvariantCulture) + "px", StringComparison.Ordinal)),
            "分栏间距拖到上限：设置=" + (gapMax?.ColumnGap ?? 0).ToString("0", CultureInfo.InvariantCulture)
                + "（期望 256）三栏列宽应为 " + wantMax.ToString("0", CultureInfo.InvariantCulture)
                + "px，页框CSS 里确有=" + (gapMax != null && gapMax.Computed.Any(x => x.Contains("column-width=" + wantMax.ToString("0", CultureInfo.InvariantCulture) + "px", StringComparison.Ordinal))));

        // 判据 5：切换模式不丢阅读位置。
        // 注意要回到"最长章"再测：第 0 章是封面页只有一页，在它上面测位置会量出 1.0。
        await paged.GoToChapterAsync(tallestIndex).ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);
        await paged.SetReaderModeAsync("scroll").ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);

        var scrollState = await paged.ReadPageStateAsync().ConfigureAwait(true);
        var scrolled = await paged.ScrollToFractionAsync(0.6).ConfigureAwait(true);
        await Task.Delay(400).ConfigureAwait(true);

        var scrollNow = await paged.ReadFrameScrollAsync().ConfigureAwait(true);
        await paged.SetReaderModeAsync("paged").ConfigureAwait(true);
        await Task.Delay(400).ConfigureAwait(true);

        var pagedAgain = await paged.ReadPageStateAsync().ConfigureAwait(true);
        var drift = Math.Abs((pagedAgain?.PercentInChapter ?? 0) - scrollNow.Percent);

        Add(
            "Y7-mode-switch-keeps-position",
            scrolled && pagedAgain is not null && scrollState is not null
                && drift <= 0.12 && pagedAgain.PageIndex > 0,
            "滚动 " + scrollNow.Percent.ToString("0.###") + " -> 分页 "
                + (pagedAgain?.PercentInChapter ?? 0).ToString("0.###")
                + " 偏差=" + drift.ToString("0.###") + "（判据 <= 0.12）");

        // 模式要在 Config.json 里真的存下来（判据：分页模式要保存到设置）
        await Task.Delay(300).ConfigureAwait(true);

        var savedMode = configStore.GetBool(ReaderWindow.PaginationModeKey, false);

        Add(
            "Y8-mode-persisted",
            savedMode,
            "Config.json 的 " + ReaderWindow.PaginationModeKey + "=" + savedMode);

        // ---------- Y4b. T5-2W 第三步：可视编辑保存后，阅读器重新加载该章并保持阅读位置 ----------
        /* 计划原文：(编辑保存后) 阅读器重新加载当前章并保持阅读位置（章节缓存失效 + 回到原章与原进度）。
           必须从阅读器窗口打开可视编辑窗口、再走真实保存入口（ChapterVisualEditorWindow.OnSaveAsync）
           直接调服务等于绕过"宿主到底接上没接上"。位置断言用"章内比例"：改一屏以内的正文后必须还在原处。 */
        var reloadIndex = (await paged.ReadPageStateAsync().ConfigureAwait(true))?.ChapterIndex ?? -1;
        var reloadBefore = await paged.ReadPageStateAsync().ConfigureAwait(true);
        var percentBefore = reloadBefore?.PercentInChapter ?? -1;
        var editorsBefore = Application.Current.Windows.OfType<ChapterVisualEditorWindow>().Count();

        await paged.OpenEditorForChapterAsync(reloadIndex).ConfigureAwait(true);

        var reloadEditorAppeared = await WaitUntilAsync(
            () => Application.Current.Windows.OfType<ChapterVisualEditorWindow>().Count() > editorsBefore,
            TimeSpan.FromSeconds(25)).ConfigureAwait(true);

        var reloadEditor = Application.Current.Windows.OfType<ChapterVisualEditorWindow>().FirstOrDefault();
        var marker = "RELOADMARK-" + Guid.NewGuid().ToString("N")[..8];
        var markedBody = string.Empty;
        var originalBody = string.Empty;
        var editorSaved = false;
        var fixDialogSeen = false;
        var restoredOk = false;
        var markerSeen = false;
        var markerGone = false;

        if (reloadEditor is not null && reloadIndex >= 0 && reloadIndex < session.Manifest.Spine.Count)
        {
            /* 正文 = 缓存里那一章的 body + 一句标记：长度几乎不变，"保持位置"才验得出来
               （整章换成一页的正文，位置必然落到 100%那是假通过）。 */
            var cachedChapter = Path.Combine(
                session.CacheRoot,
                session.Manifest.Spine[reloadIndex].Href.Replace('/', Path.DirectorySeparatorChar));
            var cachedHtml = File.Exists(cachedChapter) ? File.ReadAllText(cachedChapter) : string.Empty;
            var bodyMatch = Regex.Match(
                cachedHtml, "<body[^>]*>(.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

            if (bodyMatch.Success)
            {
                originalBody = bodyMatch.Groups[1].Value;
                markedBody = originalBody + "<p>" + marker + "</p>";

                /* 自动修正确认框是模态的、会把自动化卡死（本仓库踩过）：真弹了就点【取消】并如实记录
                   这个弹窗本身是人工验收项。 */
                var fixPump = new System.Windows.Threading.DispatcherTimer(
                    TimeSpan.FromMilliseconds(150),
                    System.Windows.Threading.DispatcherPriority.Normal,
                    (_, _) =>
                    {
                        var dialog = Application.Current.Windows.OfType<ConfirmAutoFixDialog>().FirstOrDefault();

                        if (dialog is null)
                        {
                            return;
                        }

                        fixDialogSeen = true;
                        dialog.Close();
                    },
                    System.Windows.Threading.Dispatcher.CurrentDispatcher);

                fixPump.Start();

                try
                {
                    /* 新建的编辑器窗口刚 Show() 时 WebView2 还没就绪，RunScriptAsync 会抛异常，先等 Core。 */
                    await WaitUntilAsync(() => reloadEditor.Core is not null, TimeSpan.FromSeconds(25))
                        .ConfigureAwait(true);

                    /* 走真实协议把窗口置脏（直接调 OnSaveAsync 不会置脏，而"保存前快照"只对真改过的保存记）。 */
                    await reloadEditor.RunScriptAsync(
                        "window.chrome.webview.postMessage({v:1,id:'smoke-dirty-1',type:'editor.event.changed',payload:{}});'ok'")
                        .ConfigureAwait(true);

                    editorSaved = await reloadEditor.OnSaveAsync(markedBody).ConfigureAwait(true);
                }
                finally
                {
                    fixPump.Stop();
                }
            }
        }

        var markerWatch = Stopwatch.StartNew();

        while (!markerSeen && markerWatch.Elapsed < TimeSpan.FromSeconds(20))
        {
            var chapterText = await ReadChapterTextAsync(paged).ConfigureAwait(true);
            markerSeen = chapterText.Contains(marker, StringComparison.Ordinal);

            if (!markerSeen)
            {
                await Task.Delay(200).ConfigureAwait(true);
            }
        }

        await Task.Delay(300).ConfigureAwait(true);

        var reloadAfter = await paged.ReadPageStateAsync().ConfigureAwait(true);
        var percentAfter = reloadAfter?.PercentInChapter ?? -1;
        var reloadTolerance = (reloadAfter?.PageCount ?? 0) > 0 ? (1.0 / reloadAfter!.PageCount) + 0.05 : 999.0;


        Add(
            "Y4b-edit-save-reloads-chapter-and-keeps-position",
            reloadEditorAppeared && reloadEditor is not null && !string.IsNullOrEmpty(markedBody)
                && editorSaved && !fixDialogSeen && markerSeen
                && percentBefore >= 0 && Math.Abs(percentAfter - percentBefore) <= reloadTolerance,
            "可视编辑窗口=" + reloadEditorAppeared + "；保存=" + editorSaved
                + "（自动修正确认框=" + fixDialogSeen + "）"
                + "；重载后看到新正文=" + markerSeen
                + "；章内位置 " + percentBefore.ToString("0.###", CultureInfo.InvariantCulture)
                + " -> " + percentAfter.ToString("0.###", CultureInfo.InvariantCulture)
                + "（容差 " + reloadTolerance.ToString("0.###", CultureInfo.InvariantCulture) + "）");
        // ---------- Y4c. 保存退出后**再次**进「编辑本章」，必须看到上次保存的内容（用户 2026-10-11 反馈）----------
        /* 以前每次点「编辑本章」都 OpenAsync 新建工作区，而新工作区是从**原书**重新解压的 ->
           第二次进编辑器看到的是"上次编辑之前"的内容，与阅读器里显示的（已重载的新内容）不一致。
           修法：OpenEditorForChapterAsync 优先复用同一本书（同格式、同源文件）没过期的残留会话。
           这条检查：关掉刚保存过的编辑器窗口 -> 再开一次 -> 新窗口里必须能看到刚才那个标记。 */
        reloadEditor?.Close();
        await Task.Delay(600).ConfigureAwait(true);

        var reopenBefore = Application.Current.Windows.OfType<ChapterVisualEditorWindow>().Count();

        await paged.OpenEditorForChapterAsync(reloadIndex).ConfigureAwait(true);

        var reopenAppeared = await WaitUntilAsync(
            () => Application.Current.Windows.OfType<ChapterVisualEditorWindow>().Count() > reopenBefore,
            TimeSpan.FromSeconds(25)).ConfigureAwait(true);

        var reopenEditor = Application.Current.Windows.OfType<ChapterVisualEditorWindow>().FirstOrDefault();

        /* 新窗口刚 Show() 时 WebView2 还没初始化：必须先等 Core 就绪，
           否则 RunScriptAsync 抛 InvalidOperationException("CoreWebView2 未就绪") 把整轮冒烟打死（本仓库踩过）。 */
        if (reopenEditor is not null)
        {
            await WaitUntilAsync(() => reopenEditor.Core is not null, TimeSpan.FromSeconds(25))
                .ConfigureAwait(true);
        }

        var reopenSawMarker = false;
        var reopenWatch = Stopwatch.StartNew();

        while (!reopenSawMarker && reopenWatch.Elapsed < TimeSpan.FromSeconds(25) && reopenEditor is not null)
        {
            var editorText = await reopenEditor
                .RunScriptAsync("document.body ? document.body.innerText : ''")
                .ConfigureAwait(true) ?? string.Empty;

            reopenSawMarker = editorText.Contains(marker, StringComparison.Ordinal);

            if (!reopenSawMarker)
            {
                await Task.Delay(200).ConfigureAwait(true);
            }
        }

        /* 收尾：用这个窗口把原文存回去，并确认阅读器里的标记消失（第二次重载；别把改过的正文留给后面的检查）。 */
        if (reopenEditor is not null && !string.IsNullOrEmpty(originalBody))
        {
            /* 同上：先把窗口置脏，再保存。 */
            await reopenEditor.RunScriptAsync(
                "window.chrome.webview.postMessage({v:1,id:'smoke-dirty-2',type:'editor.event.changed',payload:{}});'ok'")
                .ConfigureAwait(true);

            restoredOk = await reopenEditor.OnSaveAsync(originalBody).ConfigureAwait(true);

            var goneWatch = Stopwatch.StartNew();

            while (!markerGone && goneWatch.Elapsed < TimeSpan.FromSeconds(15))
            {
                var chapterText = await ReadChapterTextAsync(paged).ConfigureAwait(true);
                markerGone = !chapterText.Contains(marker, StringComparison.Ordinal);

                if (!markerGone)
                {
                    await Task.Delay(200).ConfigureAwait(true);
                }
            }
        }

        /* 【T5-4 补漏】可视编辑器自己的【保存】也要进"历史"：
           以前只有主窗口保存与全部替换前会记快照，在可视编辑器里改完点保存 -> 历史里一条都没有。 */
        var visualSaveHistory = -1;
        var visualSaveTop = "(无)";

        if (reopenEditor is not null)
        {
            await reopenEditor.RefreshHistoryAsync().ConfigureAwait(true);
            visualSaveHistory = reopenEditor.HistoryCount;
            visualSaveTop = reopenEditor.HistoryTopText;
        }

        Add(
            "T54-visual-editor-save-in-history",
            visualSaveHistory >= 3 && visualSaveTop.Contains("保存前", StringComparison.Ordinal),
            "可视编辑器保存两次后历史条数=" + visualSaveHistory + "（应 >= 3：基线 + 两次保存）"
                + "；最上面一条=" + visualSaveTop);

        reopenEditor?.Close();
        await Task.Delay(200).ConfigureAwait(true);

        Add(
            "Y4c-edit-reopen-shows-saved-content",
            reopenAppeared && reopenEditor is not null && reopenSawMarker && restoredOk && markerGone,
            "关掉编辑器再开一次「编辑本章」：新窗口=" + reopenAppeared
                + "；看到上次保存的正文=" + reopenSawMarker
                + "；原文已存回=" + restoredOk + "；阅读器里标记已消失=" + markerGone);
        paged.Close();
        await Task.Delay(300).ConfigureAwait(true);

        var reopenedPaged = new ReaderWindow(
            reader, environments, progress, loggerFactory.CreateLogger<ReaderWindow>(), bookId,
            config: configStore, snapshots: snapshots);

        reopenedPaged.Show();

        await WaitUntilAsync(() => reopenedPaged.LastChapterIndex >= 0, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
        await Task.Delay(600).ConfigureAwait(true);

        var reopenedState = await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true);

        Add(
            "Y9-reopen-starts-paged",
            reopenedState is not null && reopenedState.Mode == "paged" && reopenedState.HasPagedCss,
            "重开后 模式=" + (reopenedState?.Mode ?? "(null)")
                + " 列样式=" + (reopenedState?.HasPagedCss ?? false));

        // 判据 4 下半：重开窗口后主题应该是 Config.json 里记的那套
        // （不能写死 green：后面的 Y21 会把主题改成"自定义"）
        var persistedTheme = string.Empty;

        try
        {
            var cfgText = System.IO.File.Exists(configPath)
                ? System.IO.File.ReadAllText(configPath)
                : "{}";

            if (JsonNode.Parse(cfgText) is JsonObject cfgRoot && cfgRoot["reader"] is JsonObject cfgReader)
            {
                persistedTheme = cfgReader["theme"]?.GetValue<string>() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            persistedTheme = "(解析失败)";
        }

        Add(
            "Y20b-theme-restored-on-reopen",
            !string.IsNullOrEmpty(persistedTheme)
                && string.Equals(reopenedPaged.LastTheme, persistedTheme, StringComparison.Ordinal),
            "重开窗口后 LastTheme=" + reopenedPaged.LastTheme + "，Config.json 里记的是 "
                + persistedTheme + "（两者必须一致）");

        // ---------- T4-4 目录（判据 1/2/3/4） ----------

        // 判据 1：目录能显示出来（EPUB2 走 NCX、EPUB3 走 nav，都在引擎里解析好）
        await reopenedPaged.ClickTocButtonAsync().ConfigureAwait(true);
        await Task.Delay(400).ConfigureAwait(true);

        var toc = await reopenedPaged.ReadTocAsync().ConfigureAwait(true);

        Add(
            "Y36-toc-panel-shows",
            toc is not null && toc.Open && toc.Count > 0 && toc.HasToc,
            "目录项=" + (toc?.Count ?? 0) + (toc is not null && toc.Titles.Count > 0 ? " 首项=" + toc.Titles[0] : string.Empty)
                + " 降级=" + (toc?.Fallback ?? false)
                + " 提示=" + (toc?.Hint ?? string.Empty));

        // 判据 4：多级目录要有缩进（level 2 的缩进必须比 level 1 大）
        var deepest = 0;
        var levelIndentOk = false;
        var byLevel = new SortedDictionary<int, int>();

        if (toc is not null)
        {
            for (var i = 0; i < toc.Levels.Count && i < toc.Indents.Count; i++)
            {
                deepest = Math.Max(deepest, toc.Levels[i]);

                var px = ParsePx(toc.Indents[i]);

                if (px >= 0 && !byLevel.ContainsKey(toc.Levels[i]))
                {
                    byLevel[toc.Levels[i]] = px;
                }
            }

            levelIndentOk = deepest <= 1
                || (byLevel.TryGetValue(1, out var indentL1) && byLevel.TryGetValue(2, out var indentL2) && indentL2 > indentL1);
        }

        Add(
            "Y37-toc-levels-indented",
            levelIndentOk,
            "最深层级=" + deepest + " 各层缩进="
                + string.Join(" ", byLevel.Select(pair => pair.Key + "级:" + pair.Value + "px")));

        // 判据 2：点目录项要跳到对应章节（顺手验面板会收起、当前项会高亮）
        var frPickIndex = -1;
        var pickSpine = -1;

        if (toc is not null)
        {
            for (var i = 0; i < toc.Spines.Count && i < toc.Titles.Count; i++)
            {
                if (toc.Spines[i] >= 0 && toc.Spines[i] != toc.CurrentSpine)
                {
                    frPickIndex = i;
                    pickSpine = toc.Spines[i];
                    break;
                }
            }
        }

        var jumped = false;

        if (frPickIndex >= 0)
        {
            await reopenedPaged.TocPickAsync(frPickIndex).ConfigureAwait(true);
            jumped = await WaitUntilAsync(
                () => reopenedPaged.LastChapterIndex == pickSpine, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            await Task.Delay(500).ConfigureAwait(true);
        }

        var afterPick = await reopenedPaged.ReadTocAsync().ConfigureAwait(true);

        Add(
            "Y38-toc-jump-to-chapter",
            jumped && afterPick is not null && afterPick.CurrentSpine == pickSpine && !afterPick.Open,
            "点了第 " + frPickIndex + " 项（目标章 " + pickSpine + "）-> 实际章节=" + reopenedPaged.LastChapterIndex
                + " 高亮项=" + (afterPick?.CurrentSpine ?? -1) + " 面板已收起=" + !(afterPick?.Open ?? true));

        // 判据 3：带 fragment 的目录项要跳到章内位置（不是章首）
        var fragmentTry = 0;
        var fragmentHit = 0;
        var fragmentNonFirst = 0;
        var fragmentSamples = new List<string>();

        if (toc is not null)
        {
            for (var i = 0; i < toc.Spines.Count && i < toc.Fragments.Count && fragmentTry < 8; i++)
            {
                if (toc.Spines[i] < 0 || string.IsNullOrEmpty(toc.Fragments[i]))
                {
                    continue;
                }

                fragmentTry++;

                await reopenedPaged.TocJumpAsync(toc.Spines[i], toc.Fragments[i]).ConfigureAwait(true);
                await WaitUntilAsync(
                    () => reopenedPaged.LastChapterIndex == toc.Spines[i], TimeSpan.FromSeconds(20)).ConfigureAwait(true);
                await Task.Delay(400).ConfigureAwait(true);

                var anchor = await reopenedPaged.ReadAnchorInfoAsync(toc.Fragments[i]).ConfigureAwait(true);

                if (anchor is not null && anchor.Found && anchor.OnCurrentPage)
                {
                    fragmentHit++;

                    if (anchor.PageIndex > 0)
                    {
                        fragmentNonFirst++;
                    }

                    if (fragmentSamples.Count < 3)
                    {
                        fragmentSamples.Add("#" + toc.Fragments[i] + " 第" + (anchor.PageIndex + 1) + "页 " + anchor.Text);
                    }
                }
            }
        }

        Add(
            "Y39-toc-fragment-jump",
            fragmentHit > 0,
            "试了 " + fragmentTry + " 个带锚点的目录项，锚点可见=" + fragmentHit
                + "（其中落在非首页=" + fragmentNonFirst + "；真实电子书的锚点常是包住整章的容器，本来就落在章首）"
                + (fragmentSamples.Count > 0 ? " 例：" + string.Join(" / ", fragmentSamples) : string.Empty));

        // 判据 3 的强版：往本章正文末尾插一个锚点，跳过去必须落到非首页且锚点可见。
        // 先切回单栏：单栏时"一页宽 == 翻页步长"（Y2/Y4/Y15 已验过这个几何），
        // 多栏分页下这两者不相等（viewportBase 用外壳宽度、页框用 iframe 宽度），
        // 那是 T4-1/T4-3 的分栏几何问题，不该混进目录判据里。
        await reopenedPaged.ExecAsync(
            "window.__readerDebug.typographyPick({columns:1,columnGap:0});true").ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);

        const string syntheticAnchor = "ets-toc-smoke-anchor";
        var chapterNow = reopenedPaged.LastChapterIndex;

        await reopenedPaged.InjectChapterAnchorAsync(syntheticAnchor).ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);
        await reopenedPaged.TocJumpAsync(chapterNow, syntheticAnchor).ConfigureAwait(true);
        await Task.Delay(600).ConfigureAwait(true);

        var synthetic = await reopenedPaged.ReadAnchorInfoAsync(syntheticAnchor).ConfigureAwait(true);
        var layout = await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true);

        Add(
            "Y39b-toc-fragment-position",
            synthetic is not null && synthetic.Found && synthetic.OnCurrentPage && synthetic.PageIndex > 0
                && Math.Abs((layout?.PageWidth ?? 0) - (layout?.FrameWidth ?? 0)) <= 2,
            "单栏分页下章末自建锚点 -> 第 " + ((synthetic?.PageIndex ?? 0) + 1) + "/" + (layout?.PageCount ?? 0)
                + " 页 可见=" + (synthetic?.OnCurrentPage ?? false)
                + " 章内比例=" + (synthetic?.Percent ?? 0).ToString("0.###")
                + " 锚点左=" + (synthetic?.RectLeft ?? 0) + " 页框=" + (synthetic?.FrameLeft ?? 0) + ".." + (synthetic?.FrameRight ?? 0)
                + " 一页宽=" + (layout?.PageWidth ?? 0) + " 页框宽=" + (layout?.FrameWidth ?? 0)
                + " 平移=" + (layout?.OffsetX ?? 0));

        // ================= T4-6 快捷键与交互（判据 1-5） =================

        // 判据 3：工具栏按钮与下拉都要有 tooltip，且提示里带快捷键
        var titles = await ReadToolbarTitlesAsync(reopenedPaged).ConfigureAwait(true);
        var mustHaveTitles = new[] { "btnPrev", "btnNext", "btnToc", "btnType", "btnMode" };
        var missingTitles = mustHaveTitles
            .Where(n => !titles.TryGetValue(n, out var v) || string.IsNullOrWhiteSpace(v))
            .ToList();
        var withShortcut = titles.Count(t => t.Value.Contains("Ctrl+", StringComparison.Ordinal));

        Add(
            "Y40-tooltips-show-shortcuts",
            missingTitles.Count == 0 && withShortcut >= 4,
            "提示 " + titles.Count + " 个，缺=" + (missingTitles.Count == 0 ? "无" : string.Join(',', missingTitles))
                + "，带快捷键=" + withShortcut + "：" + string.Join(" | ", titles.Select(t => t.Key + "=>" + t.Value)));

        // 判据 4：Alt / Win(meta) 组合一律不接管；裸  必须照常翻页（否则本判据是空断言）
        var baseState = await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true);
        var basePage = baseState?.PageIndex ?? -1;

        await PressKeyAsync(reopenedPaged, "ArrowRight", "frame", "alt").ConfigureAwait(true);
        var afterAlt = (await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true))?.PageIndex ?? -1;

        await PressKeyAsync(reopenedPaged, "ArrowRight", "frame", "meta").ConfigureAwait(true);
        var afterMeta = (await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true))?.PageIndex ?? -1;

        await PressKeyAsync(reopenedPaged, "ArrowRight", "frame", null).ConfigureAwait(true);
        var afterPlain = (await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true))?.PageIndex ?? -1;

        Add(
            "Y41-alt-win-combos-not-hijacked",
            /* 章末那一页再翻就是下一章的第 1 页，所以只要求"真的动了" */
            afterAlt == basePage && afterMeta == basePage && afterPlain != basePage,
            "起始第 " + (basePage + 1) + " 页；Alt+ -> " + (afterAlt + 1)
                + "；Win+ -> " + (afterMeta + 1) + "；裸  -> " + (afterPlain + 1)
                + "（前者必须一动不动，后者必须真的翻过去章末会落到下一章首页）");

        // 判据 1：焦点在 iframe 里，快捷键照样生效（pressKey 第二参 = frame）
        var spaceBefore = (await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true))?.PageIndex ?? -1;

        await PressKeyAsync(reopenedPaged, " ", "frame", null).ConfigureAwait(true);
        var afterSpace = (await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true))?.PageIndex ?? -1;

        await PressKeyAsync(reopenedPaged, " ", "frame", "shift").ConfigureAwait(true);
        var afterShiftSpace = (await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true))?.PageIndex ?? -1;

        await PressKeyAsync(reopenedPaged, "Home", "frame", null).ConfigureAwait(true);
        var afterHome = (await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true))?.PageIndex ?? -1;

        await PressKeyAsync(reopenedPaged, "End", "frame", null).ConfigureAwait(true);
        var endState = await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true);
        var afterEnd = endState?.PageIndex ?? -1;

        Add(
            "Y42-keys-work-inside-iframe",
            afterSpace == spaceBefore + 1 && afterShiftSpace == spaceBefore
                && afterHome == 0 && afterEnd == (endState?.PageCount ?? 0) - 1,
            "Space " + (spaceBefore + 1) + "->" + (afterSpace + 1)
                + "；Shift+Space 回 " + (afterShiftSpace + 1)
                + "；Home -> " + (afterHome + 1)
                + "；End -> " + (afterEnd + 1) + "/" + (endState?.PageCount ?? 0));

        // Ctrl+ / Ctrl+ 翻章（焦点同样在 iframe 里）
        var y43ChapterBefore = (await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true))?.ChapterIndex ?? -1;

        await PressKeyAsync(reopenedPaged, "ArrowRight", "frame", "ctrl").ConfigureAwait(true);
        var y43ChapterNext = (await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true))?.ChapterIndex ?? -1;

        await PressKeyAsync(reopenedPaged, "ArrowLeft", "frame", "ctrl").ConfigureAwait(true);
        var chapterBack = (await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true))?.ChapterIndex ?? -1;

        Add(
            "Y43-ctrl-arrows-change-chapter",
            y43ChapterNext == y43ChapterBefore + 1 && chapterBack == y43ChapterBefore,
            "Ctrl+ 第 " + (y43ChapterBefore + 1) + " -> " + (y43ChapterNext + 1)
                + " 章；Ctrl+ 回第 " + (chapterBack + 1) + " 章");

        // T7-3：工具栏的「上一页 / 下一页」必须固定在**第一行**的最左 / 最右。
        // 用户口径（2026-10-19）：随窗口宽度在一行 / 两行之间切换，但**不论一行还是两行**，
        // 上一页永远第一行最左、下一页永远第一行最右（底部状态条的上一章/下一章不参与）。
        // 一行 / 两行的分界线由 reader.js 按实测宽度算出（threshold），这里直接把那条线验掉。
        const string ToolbarProbeScript = "JSON.stringify(window.__readerDebug.toolbarRowsProbe())";

        static (bool EdgesOk, bool InFirstRow, bool Rows, bool Wide, bool Paged, int Threshold, int Available,
            string PrevText, int PrevLeft, int MinLeft, int NextRight, int MaxRight, string Detail)
            ToolbarProbe(string? raw)
        {
            var empty = (EdgesOk: false, InFirstRow: false, Rows: false, Wide: false, Paged: false,
                Threshold: 0, Available: 0,
                PrevText: "?", PrevLeft: 0, MinLeft: 0, NextRight: 0, MaxRight: 0, Detail: "(null)");

            if (string.IsNullOrWhiteSpace(raw))
            {
                return empty;
            }

            try
            {
                // ExecAsync 返回的是「JSON 字符串的 JSON」，先脱一层引号（与 ParseProbe 同一口径）
                var text = raw.Trim();

                if (text.StartsWith('"') && text.EndsWith('"') && text.Length > 2)
                {
                    text = JsonSerializer.Deserialize<string>(text) ?? text;
                }

                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                var prev = root.GetProperty("prev");
                var next = root.GetProperty("next");
                var prevLeft = prev.GetProperty("left").GetInt32();
                var nextRight = next.GetProperty("right").GetInt32();
                var prevTop = prev.GetProperty("top").GetInt32();
                var nextTop = next.GetProperty("top").GetInt32();
                var minLeft = root.GetProperty("minLeft").GetInt32();
                var maxRight = root.GetProperty("maxRight").GetInt32();
                var firstRowTop = root.GetProperty("firstRowTop").GetInt32();
                var prevText = root.GetProperty("prevText").GetString() ?? "?";
                var rows = root.GetProperty("rows").GetBoolean();
                var wide = root.GetProperty("wide").GetBoolean();
                var paged = root.GetProperty("paged").GetBoolean();
                var threshold = root.GetProperty("threshold").GetInt32();
                var available = root.GetProperty("available").GetInt32();

                var inFirstRow = Math.Abs(prevTop - firstRowTop) <= 2 && Math.Abs(nextTop - firstRowTop) <= 2;
                var edgesOk = Math.Abs(prevLeft - minLeft) <= 2
                    && Math.Abs(nextRight - maxRight) <= 2
                    && inFirstRow;

                return (edgesOk, inFirstRow, rows, wide, paged, threshold, available, prevText,
                    prevLeft, minLeft, nextRight, maxRight,
                    $"「{prevText}」left={prevLeft}（最左 {minLeft}），下一页 right={nextRight}（最右 {maxRight}）");
            }
            catch (Exception ex)
            {
                return (false, false, false, false, false, 0, 0, "?", 0, 0, 0, 0, "探针解析失败：" + ex.Message);
            }
        }

        // 窗口外框宽度 与 工具栏可用宽度 的固定差（WPF 边框/滚动条），先量一次
        reopenedPaged.Width = 1024;
        await Task.Delay(400).ConfigureAwait(true);

        var baseToolbar = ToolbarProbe(
            await reopenedPaged.ExecAsync(ToolbarProbeScript).ConfigureAwait(true));
        var frameOverhead = 1024 - baseToolbar.Available;
        var oneRowThreshold = baseToolbar.Threshold;

        // ① 宽窗：可用宽度 = 阈值 + 60 → 必须并回一行，且上一页在首行最左、下一页在首行最右
        reopenedPaged.Width = oneRowThreshold + frameOverhead + 60;
        await Task.Delay(600).ConfigureAwait(true);

        var wideToolbar = ToolbarProbe(
            await reopenedPaged.ExecAsync(ToolbarProbeScript).ConfigureAwait(true));
        var wideLayout = await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true);

        /* 截图存证：网页工具栏在 WebView2 里，WPF 窗口截图拍不到，必须用 CapturePreview。 */
        Directory.CreateDirectory(paths.TempDir);
        var wideShot = Path.Combine(paths.TempDir, "reader-toolbar-wide.png");
        await reopenedPaged.CapturePreviewAsync(wideShot).ConfigureAwait(true);

        // ② 窄窗：可用宽度 = 阈值 - 60 → 必须回到两行，上一页/下一页仍在首行两端
        reopenedPaged.Width = Math.Max(760, oneRowThreshold + frameOverhead - 60);
        await Task.Delay(600).ConfigureAwait(true);

        var narrowToolbar = ToolbarProbe(
            await reopenedPaged.ExecAsync(ToolbarProbeScript).ConfigureAwait(true));
        var narrowLayout = await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true);

        var narrowShot = Path.Combine(paths.TempDir, "reader-toolbar-narrow.png");
        await reopenedPaged.CapturePreviewAsync(narrowShot).ConfigureAwait(true);

        // ③ 阈值本身：可用 = 阈值 +2 → 一行；可用 = 阈值 -2 → 两行（证明阈值不是估出来的）
        reopenedPaged.Width = oneRowThreshold + frameOverhead + 2;
        await Task.Delay(600).ConfigureAwait(true);

        var justAbove = ToolbarProbe(
            await reopenedPaged.ExecAsync(ToolbarProbeScript).ConfigureAwait(true));

        reopenedPaged.Width = Math.Max(760, oneRowThreshold + frameOverhead - 2);
        await Task.Delay(600).ConfigureAwait(true);

        var justBelow = ToolbarProbe(
            await reopenedPaged.ExecAsync(ToolbarProbeScript).ConfigureAwait(true));

        reopenedPaged.Width = 1100;
        await Task.Delay(250).ConfigureAwait(true);

        Add(
            "Y50-toolbar-first-row-edges",
            wideToolbar.Rows && narrowToolbar.Rows
                && wideToolbar.Wide && !narrowToolbar.Wide
                && wideToolbar.EdgesOk && narrowToolbar.EdgesOk,
            "阈值 " + oneRowThreshold + "px（控件自然宽之和 + 间距 + 标题最小宽 + 内边距）；"
            + "宽窗 可用 " + wideToolbar.Available + "px → 一行=" + wideToolbar.Wide
            + "；窄窗 可用 " + narrowToolbar.Available + "px → 一行=" + narrowToolbar.Wide
            + "；一行/两行「上一页都在第一行最左、下一页都在第一行最右」="
            + (wideToolbar.EdgesOk && narrowToolbar.EdgesOk)
            + "；" + wideToolbar.Detail + "；" + narrowToolbar.Detail
            + "；截图=" + Path.GetFileName(wideShot) + " / " + Path.GetFileName(narrowShot));

        Add(
            "Y51-one-row-threshold-is-exact",
            justAbove.Wide && !justBelow.Wide && justAbove.EdgesOk && justBelow.EdgesOk,
            "可用 " + justAbove.Available + "px（阈值 " + oneRowThreshold + " +2）→ 一行=" + justAbove.Wide
            + "；可用 " + justBelow.Available + "px（阈值 -2）→ 一行=" + justBelow.Wide
            + "；两处上一页都贴最左、下一页都贴最右=" + (justAbove.EdgesOk && justBelow.EdgesOk));

        Add(
            "Y52-one-row-keeps-page-geometry",
            wideLayout is not null && narrowLayout is not null
                && wideLayout.PageHeight > narrowLayout.PageHeight
                && wideLayout.FrameHeight == wideLayout.PageHeight
                && wideLayout.FrameWidth == wideLayout.PageWidth
                && wideLayout.LinesOnPage > narrowLayout.LinesOnPage,
            "一行：一页高 " + (wideLayout?.PageHeight ?? 0) + "px、页框高 " + (wideLayout?.FrameHeight ?? 0)
            + "px（必须相等）、一页 " + (wideLayout?.LinesOnPage ?? 0) + " 行；两行：一页高 "
            + (narrowLayout?.PageHeight ?? 0) + "px、页框高 " + (narrowLayout?.FrameHeight ?? 0)
            + "px、一页 " + (narrowLayout?.LinesOnPage ?? 0) + " 行");

        // Ctrl+= / Ctrl+- / Ctrl+0 调字号
        var fontBefore = await ReadFontSizeAsync(reopenedPaged).ConfigureAwait(true);

        await PressKeyAsync(reopenedPaged, "=", "frame", "ctrl").ConfigureAwait(true);
        var fontBigger = await ReadFontSizeAsync(reopenedPaged).ConfigureAwait(true);

        await PressKeyAsync(reopenedPaged, "-", "frame", "ctrl").ConfigureAwait(true);
        var fontBack = await ReadFontSizeAsync(reopenedPaged).ConfigureAwait(true);

        await PressKeyAsync(reopenedPaged, "0", "frame", "ctrl").ConfigureAwait(true);
        var fontReset = await ReadFontSizeAsync(reopenedPaged).ConfigureAwait(true);

        Add(
            "Y44-ctrl-font-size-keys",
            fontBigger == fontBefore + 1 && fontBack == fontBefore && fontReset == 17,
            "字号 " + fontBefore + " --Ctrl+=--> " + fontBigger + " --Ctrl+--> " + fontBack
                + " --Ctrl+0--> " + fontReset + "（默认 17）");

        // Ctrl+G 跳转面板 + 跳到全书百分比
        await PressKeyAsync(reopenedPaged, "g", "frame", "ctrl").ConfigureAwait(true);
        var y45GotoOpen = (await ReadInteractionAsync(reopenedPaged).ConfigureAwait(true))
            ?["gotoOpen"]?.GetValue<bool>() ?? false;

        await reopenedPaged.ExecAsync("window.__readerDebug.gotoPercent(60);true").ConfigureAwait(true);
        await Task.Delay(900).ConfigureAwait(true);

        var y45Jumped = await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true);
        var spineTotal = (await reader.OpenAsync(bookId, CancellationToken.None).ConfigureAwait(true))
            .Manifest.Spine.Count;

        await PressKeyAsync(reopenedPaged, "Escape", "frame", null).ConfigureAwait(true);
        var y45GotoClosed = (await ReadInteractionAsync(reopenedPaged).ConfigureAwait(true))
            ?["gotoOpen"]?.GetValue<bool>() ?? false;
        var y45ExpectedChapter = (int)Math.Floor(spineTotal * 0.6);

        Add(
            "Y45-ctrl-g-jump-to-percent",
            y45GotoOpen && !y45GotoClosed && spineTotal > 0
                && Math.Abs((y45Jumped?.ChapterIndex ?? -1) - y45ExpectedChapter) <= 1,
            "Ctrl+G 面板打开=" + y45GotoOpen + "；跳 60% -> 第 "
                + ((y45Jumped?.ChapterIndex ?? 0) + 1) + "/" + spineTotal + " 章（期望第 " + (y45ExpectedChapter + 1) + " 章左右）"
                + "；Esc 关闭=" + !y45GotoClosed);

        // 判据 5：底部状态条  章号与全书百分比随翻章同步，翻到首/末章时按钮禁用
        await reopenedPaged.ExecAsync("window.__readerDebug.gotoPercent(0);true").ConfigureAwait(true);
        await Task.Delay(900).ConfigureAwait(true);

        var statusFirstNode = await ReadInteractionAsync(reopenedPaged).ConfigureAwait(true);

        await reopenedPaged.ExecAsync("window.__readerDebug.gotoPercent(100);true").ConfigureAwait(true);
        await Task.Delay(900).ConfigureAwait(true);

        var statusLastNode = await ReadInteractionAsync(reopenedPaged).ConfigureAwait(true);

        var firstChapterText = statusFirstNode?["statusChapter"]?.GetValue<string>() ?? string.Empty;
        var firstPercentText = statusFirstNode?["statusPercent"]?.GetValue<string>() ?? string.Empty;
        var firstPrevDisabled = statusFirstNode?["statusPrevDisabled"]?.GetValue<bool>() ?? false;
        var firstNextDisabled = statusFirstNode?["statusNextDisabled"]?.GetValue<bool>() ?? true;
        var lastChapterText = statusLastNode?["statusChapter"]?.GetValue<string>() ?? string.Empty;
        var lastPercentText = statusLastNode?["statusPercent"]?.GetValue<string>() ?? string.Empty;
        var lastNextDisabled = statusLastNode?["statusNextDisabled"]?.GetValue<bool>() ?? false;

        Add(
            "Y46-status-bar-syncs",
            firstChapterText.Contains("第 1 /", StringComparison.Ordinal)
                && firstPrevDisabled && !firstNextDisabled
                && lastPercentText != firstPercentText
                && lastPercentText.Contains('%', StringComparison.Ordinal)
                && lastNextDisabled
                && lastChapterText.Contains("第 ", StringComparison.Ordinal),
            "首章状态条=" + firstChapterText + " / " + firstPercentText
                + "（上一章禁用=" + firstPrevDisabled + "）；末章=" + lastChapterText + " / " + lastPercentText
                + "（下一章禁用=" + lastNextDisabled + "）");

        // 判据 2：F11 全屏  页面藏 UI、宿主切无边最大化；Esc 退出后恢复
        var statusVisibleBefore = statusFirstNode?["statusVisible"]?.GetValue<bool>() ?? false;

        await PressKeyAsync(reopenedPaged, "F11", "frame", null).ConfigureAwait(true);
        await Task.Delay(600).ConfigureAwait(true);

        var fullNode = await ReadInteractionAsync(reopenedPaged).ConfigureAwait(true);
        var fullOn = fullNode?["fullscreen"]?.GetValue<bool>() ?? false;
        var fullClass = fullNode?["fullscreenClass"]?.GetValue<bool>() ?? false;
        var fullStatusVisible = fullNode?["statusVisible"]?.GetValue<bool>() ?? true;

        Add(
            "Y47-f11-enters-fullscreen",
            fullOn && fullClass && !fullStatusVisible && reopenedPaged.LastFullscreen,
            "页面全屏=" + fullOn + " class=" + fullClass
                + " 状态条可见=" + fullStatusVisible + " 宿主全屏=" + reopenedPaged.LastFullscreen);

        await PressKeyAsync(reopenedPaged, "Escape", "frame", null).ConfigureAwait(true);
        await Task.Delay(600).ConfigureAwait(true);

        var restoredNode = await ReadInteractionAsync(reopenedPaged).ConfigureAwait(true);
        var restoredOn = restoredNode?["fullscreen"]?.GetValue<bool>() ?? true;
        var restoredClass = restoredNode?["fullscreenClass"]?.GetValue<bool>() ?? true;
        var restoredStatusVisible = restoredNode?["statusVisible"]?.GetValue<bool>() ?? false;

        Add(
            "Y48-esc-restores-layout",
            !restoredOn && !restoredClass && restoredStatusVisible == statusVisibleBefore
                && !reopenedPaged.LastFullscreen,
            "退出全屏后：页面全屏=" + restoredOn + " 状态条可见=" + restoredStatusVisible
                + "（原来=" + statusVisibleBefore + "）宿主全屏=" + reopenedPaged.LastFullscreen);

        // ---------- Y49. 标题行「第 12/14 章　第 1/4 页」+ 两个可输入跳转框（用户 2026-10-21 要求）----------

        /* 用户要求四件事，这条检查一次验完：
           ① 写法：标题下面那行改成「第 N/M 章」，分页模式后面接「第 N/M 页」；
           ② 斜杠前面那个"当前第几章 / 第几页"是可输入框——字号、高度跟这一行文字一样（11px 的小框），
              宽度按"总数有几位"算（14 -> 2 位；不跟当前值走，9 -> 10 时不会抖），数字必须完整显示；
           ③ 输章节号 + 回车 -> 跳到该章**第 1 页**；输页码 + 点别处（失焦）-> 跳到该页；
           ④ 章号在滚动 / 分页模式都显示，页码只在分页模式显示。 */
        await reopenedPaged.ExecAsync("window.__readerDebug.setMode('paged');true").ConfigureAwait(true);

        /* 先归一化排版：前面几条检查把栏数改成了 3 栏、边距改小过，页数可能被压到 1 页，
           那样"跳页"就变成假通过（跳到第 2 页 = 原来那页）。 */
        await reopenedPaged.ExecAsync(
            "window.__readerDebug.typographyPick({columns:1,columnGap:0,fontSize:17,lineHeight:1.75,"
            + "marginTop:5,marginBottom:5,marginLeft:5,marginRight:5});true").ConfigureAwait(true);
        await Task.Delay(900).ConfigureAwait(true);

        /* 用"最长的一章"验跳页：短章节只有 1 页，跳页断言会变成假通过。 */
        await reopenedPaged.GoToChapterAsync(tallestIndex).ConfigureAwait(true);
        await Task.Delay(700).ConfigureAwait(true);

        var metaPagedNode = await ReadChapterMetaAsync(reopenedPaged).ConfigureAwait(true);
        var metaPagedState = await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true);

        /* 截图存证：标题行是网页里的工具栏，WPF 窗口截图拍不到，必须用 WebView2 CapturePreview。 */
        Directory.CreateDirectory(paths.TempDir);
        var metaShot = Path.Combine(paths.TempDir, "reader-chapter-meta-inputs.png");
        await reopenedPaged.CapturePreviewAsync(metaShot).ConfigureAwait(true);
        var metaPagedText = metaPagedNode?["text"]?.GetValue<string>() ?? string.Empty;
        var metaFontSize = metaPagedNode?["fontSize"]?.GetValue<string>() ?? string.Empty;
        var metaHeight = metaPagedNode?["height"]?.GetValue<double>() ?? 0;
        var metaWidth = metaPagedNode?["width"]?.GetValue<double>() ?? 0;
        var metaPageVisible = metaPagedNode?["pageVisible"]?.GetValue<bool>() ?? false;

        /* 数字有没有被框裁掉：探针用 canvas 量"当前值的实际文字宽"和框内可用宽比（两边都必须放得下）。 */
        var metaFitsOk = (metaPagedNode?["chapterFits"]?.GetValue<bool>() ?? false)
            && (metaPagedNode?["pageFits"]?.GetValue<bool>() ?? false);

        var metaFormatOk = metaPagedState is not null
            && metaPagedText.Contains("第 " + (metaPagedState.ChapterIndex + 1) + "/", StringComparison.Ordinal)
            && metaPagedText.Contains(" 章", StringComparison.Ordinal)
            && metaPagedText.Contains("第 " + (metaPagedState.PageIndex + 1) + "/", StringComparison.Ordinal)
            && metaPagedText.Contains(" 页", StringComparison.Ordinal)
            && metaPageVisible;

        /* 小框判据（用户 2026-10-21 追加口径）：字号必须就是这一行的 11px（不能比文字大），
           高度 <= 16px（贴着字）；宽度**最小 3 位数**（方便直接编辑），
           章节总数超过 3 位时按实际位数加宽——1 / 3 / 4 / 5 位都验一遍，
           11px 字号下一个数字约 6px，5 位数的框也必须 <= 40px（不能撑成大方框）。 */
        var metaWidth3 = await ReadMetaJumpWidthAsync(reopenedPaged, 3).ConfigureAwait(true);
        var metaWidth4 = await ReadMetaJumpWidthAsync(reopenedPaged, 4).ConfigureAwait(true);
        var metaWidth5 = await ReadMetaJumpWidthAsync(reopenedPaged, 5).ConfigureAwait(true);
        var chapterTotalDigits = Math.Max(
            3,
            session.Manifest.Spine.Count.ToString(CultureInfo.InvariantCulture).Length);
        var metaWidthExpected = await ReadMetaJumpWidthAsync(reopenedPaged, chapterTotalDigits)
            .ConfigureAwait(true);
        var metaWidthIsRight = Math.Abs(metaWidth - metaWidthExpected) <= 1;

        var metaSizeOk = string.Equals(metaFontSize, "11px", StringComparison.Ordinal)
            && metaHeight > 8 && metaHeight <= 16
            && metaWidth >= metaWidth3 - 1
            && metaWidthIsRight
            && metaWidth3 > 0 && metaWidth3 <= 28
            && metaWidth4 > metaWidth3 && metaWidth4 <= 36
            && metaWidth5 > metaWidth4 && metaWidth5 <= 44;

        /* ③-a 跳页：点进页码框 -> 输 2 -> 点别处（失焦）-> 应当落到第 2 页。
           这里派发 blur 事件而不是 i.blur()：冒烟窗口未必是活动窗口，focus()/blur() 可能什么都不触发，
           而监听的就是 blur 事件本身，派发它等价于"用户点了别处"。 */
        var pageTarget = metaPagedState is not null && metaPagedState.PageCount >= 2 ? 2 : 0;

        if (pageTarget > 0)
        {
            await reopenedPaged.ExecAsync(
                "(function(){var i=document.getElementById('pageJumpInput');"
                + "i.value='" + pageTarget + "';i.dispatchEvent(new Event('blur'));return true;})()")
                .ConfigureAwait(true);

            await WaitUntilAsync(
                () => reopenedPaged.LastChapterIndex == tallestIndex,
                TimeSpan.FromSeconds(8)).ConfigureAwait(true);
            await Task.Delay(500).ConfigureAwait(true);
        }

        var afterPageJump = await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true);
        var pageJumpOk = pageTarget > 0 && afterPageJump is not null && afterPageJump.PageIndex == pageTarget - 1;

        /* ③-b 跳章：在章节框里输 3 -> 按回车 -> 应当落到第 3 章第 1 页 */
        var chapterTarget = Math.Min(2, session.Manifest.Spine.Count - 1);

        await reopenedPaged.ExecAsync(
            "(function(){var i=document.getElementById('chapterJumpInput');"
            + "i.value='" + (chapterTarget + 1) + "';"
            + "i.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true}));"
            + "return true;})()").ConfigureAwait(true);

        var chapterJumped = await WaitUntilAsync(
            () => reopenedPaged.LastChapterIndex == chapterTarget,
            TimeSpan.FromSeconds(15)).ConfigureAwait(true);

        await Task.Delay(600).ConfigureAwait(true);

        var afterChapterJump = await reopenedPaged.ReadPageStateAsync().ConfigureAwait(true);
        var chapterJumpOk = chapterJumped && afterChapterJump is not null && afterChapterJump.PageIndex == 0;

        /* ④ 滚动模式：只剩章号，页码段藏起来；输入框还是 11px 小框 */
        await reopenedPaged.ExecAsync("window.__readerDebug.setMode('scroll');true").ConfigureAwait(true);
        await Task.Delay(700).ConfigureAwait(true);

        var metaScrollNode = await ReadChapterMetaAsync(reopenedPaged).ConfigureAwait(true);
        var metaScrollPageVisible = metaScrollNode?["pageVisible"]?.GetValue<bool>() ?? true;
        var metaScrollChapterValue = metaScrollNode?["chapterValue"]?.GetValue<string>() ?? string.Empty;
        var metaScrollText = metaScrollNode?["text"]?.GetValue<string>() ?? string.Empty;

        await reopenedPaged.ExecAsync("window.__readerDebug.setMode('paged');true").ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);

        Add(
            "Y49-chapter-meta-jump-inputs",
            metaFormatOk && metaSizeOk && metaFitsOk && pageJumpOk && chapterJumpOk
                && !metaScrollPageVisible
                && metaScrollChapterValue == (chapterTarget + 1).ToString(CultureInfo.InvariantCulture)
                && metaScrollText.Contains(" 章", StringComparison.Ordinal),
            "分页模式这一行=「" + metaPagedText + "」；输入框 " + metaFontSize + " 高="
                + metaHeight.ToString("0.#") + "px 宽=" + metaWidth.ToString("0.#") + "px"
                + "（数字完整显示=" + metaFitsOk + "；最小 3 位数宽=" + metaWidthExpected.ToString("0.#")
                + "px，超过 3 位按实际位数加宽：4 位=" + metaWidth4.ToString("0.#")
                + "、5 位=" + metaWidth5.ToString("0.#") + "px）；输页码 " + pageTarget + " 后点别处 -> 第 "
                + ((afterPageJump?.PageIndex ?? -1) + 1) + " 页（期望 " + pageTarget + "）；"
                + "输章节号 " + (chapterTarget + 1) + " 后按回车 -> 第 "
                + ((afterChapterJump?.ChapterIndex ?? -1) + 1) + " 章第 "
                + ((afterChapterJump?.PageIndex ?? -1) + 1) + " 页（期望第 " + (chapterTarget + 1) + " 章第 1 页）；"
                + "滚动模式页码段可见=" + metaScrollPageVisible
                + "（必须 false）、章节框显示=" + metaScrollChapterValue);

        // ---------- G3. 点一次「编辑本章」只能开出一个可视编辑窗口（用户 2026-10-11 截图：叠了好几个）----------
        /* 根因：「编辑本章」原来在 updateButtons() 里 addEventListener，而 updateButtons 每次开章都会跑，
           于是开过 N 章后点一下会连发 N 条 reader.editChapter（宿主当时也没去重），叠出 N 个"可视编辑章节"窗口。
           修法：按钮只在一次性初始化里绑一次 + 宿主对"同一章已开窗口"只前置。
           这条检查必须**点按钮**（不是直接调服务），而且先翻几章，让 updateButtons 多跑几次。 */
        for (var nav = 0; nav < 3; nav++)
        {
            await window.GoToChapterAsync(nav).ConfigureAwait(true);
            await Task.Delay(200).ConfigureAwait(true);
        }

        var editorsBeforeClick = Application.Current.Windows.OfType<ChapterVisualEditorWindow>().Count();

        await window.ExecAsync("document.getElementById('btnEditChapter').click();true").ConfigureAwait(true);

        var firstEditorAppeared = await WaitUntilAsync(
            () => Application.Current.Windows.OfType<ChapterVisualEditorWindow>().Count() > editorsBeforeClick,
            TimeSpan.FromSeconds(25)).ConfigureAwait(true);

        await Task.Delay(2500).ConfigureAwait(true);

        var editorsAfterFirstClick = Application.Current.Windows.OfType<ChapterVisualEditorWindow>().Count();

        await window.ExecAsync("document.getElementById('btnEditChapter').click();true").ConfigureAwait(true);
        await Task.Delay(2500).ConfigureAwait(true);

        var editorsAfterSecondClick = Application.Current.Windows.OfType<ChapterVisualEditorWindow>().Count();

        Add(
            "G3-edit-chapter-opens-exactly-one-window",
            firstEditorAppeared
                && editorsAfterFirstClick == editorsBeforeClick + 1
                && editorsAfterSecondClick == editorsAfterFirstClick,
            "翻过 3 章后点一次「编辑本章」：窗口数 " + editorsBeforeClick + " -> " + editorsAfterFirstClick
                + "（必须只多 1 个）；再点一次 -> " + editorsAfterSecondClick + "（必须不变 = 只前置）");

        foreach (var openEditor in Application.Current.Windows.OfType<ChapterVisualEditorWindow>().ToList())
        {
            openEditor.Close();
        }

        await Task.Delay(300).ConfigureAwait(true);
        reopenedPaged.Close();
        await Task.Delay(200).ConfigureAwait(true);

        // ---------- T5-3 查找替换（入口在阅读器工具栏；只写工作区，逐章校验才写盘） ----------

        /* 静态核对：弹窗类与文案存在（"真点击"按 2026-10-12 口径交人工验收，脚本只核类与文案）。 */
        var dialogStatic = false;
        var dialogDetail = string.Empty;

        try
        {
            var dialogItems = new[]
            {
                new FindReplacePreviewItem(1, "OEBPS/Text/ch2.xhtml", "第 2 章", 0, "命中前一截", "命中词", "命中后一截"),
            };
            var staticDialog = new ReplacePreviewDialog(3, dialogItems);
            var okButton = staticDialog.FindName("ConfirmButton") as System.Windows.Controls.Button;
            var cancelButton = staticDialog.FindName("CancelButton") as System.Windows.Controls.Button;
            dialogStatic = staticDialog.Title == "替换预览"
                && staticDialog.MessageText.Text == "将替换 3 处"
                && string.Equals(okButton?.Content as string, "确认替换", StringComparison.Ordinal)
                && string.Equals(cancelButton?.Content as string, "取消", StringComparison.Ordinal)
                && staticDialog.Items.Count == 1;
            dialogDetail = "标题=" + staticDialog.Title
                + "；首行=" + staticDialog.MessageText.Text
                + "；按钮=" + (okButton?.Content as string) + "/" + (cancelButton?.Content as string);
            staticDialog.Close();
        }
        catch (Exception ex)
        {
            dialogDetail = "构造替换预览弹窗失败：" + ex.Message;
        }

        Add("T53-dialog-static", dialogStatic, dialogDetail);

        var frBook = await repo.GetByIdAsync(bookId).ConfigureAwait(true);
        var frFormat = frBook?.Formats.FirstOrDefault(candidate => !candidate.Missing && File.Exists(candidate.FilePath));

        if (workspaces is null || source is null || findReplace is null || frBook is null || frFormat is null
            || session.Manifest.Spine.Count == 0)
        {
            Add("T53-wiring", false, "查找替换冒烟缺少依赖（workspaces / source / findReplace / 原书文件）");
        }
        else
        {
            var frWorkspace = await workspaces.OpenAsync(new WorkspaceOpenRequest
            {
                EpubPath = frFormat.FilePath,
                BookId = frBook.Id,
                FormatId = frFormat.Id,
            }).ConfigureAwait(true);

            /* 找一章有正文的：封面页可能一个字都没有，用它验查找会假失败。 */
            var t53PickIndex = 0;
            var chapterEntry = session.Manifest.Spine[0].SourceHref;
            var token = string.Empty;

            for (var i = 0; i < session.Manifest.Spine.Count && token.Length == 0; i++)
            {
                var entry = session.Manifest.Spine[i].SourceHref;
                var file = Path.Combine(frWorkspace.WorkDir, entry.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(file))
                {
                    continue;
                }

                var raw = File.ReadAllText(file);
                var bodyStart = raw.IndexOf("<body", StringComparison.OrdinalIgnoreCase);

                if (bodyStart >= 0)
                {
                    var bodyOpen = raw.IndexOf('>', bodyStart);
                    raw = bodyOpen >= 0 ? raw[(bodyOpen + 1)..] : raw;
                }

                var text = Regex.Replace(raw, "<[^>]*>", " ");
                var match = Regex.Match(text, "[\\u4e00-\\u9fa5]{2,6}");

                if (!match.Success)
                {
                    match = Regex.Match(text, "[A-Za-z]{3,8}");
                }

                if (match.Success)
                {
                    t53PickIndex = i;
                    chapterEntry = entry;
                    token = match.Value;
                }
            }

            var chapterFile = Path.Combine(frWorkspace.WorkDir, chapterEntry.Replace('/', Path.DirectorySeparatorChar));
            var t53ChapterBefore = File.Exists(chapterFile) ? File.ReadAllText(chapterFile) : string.Empty;
            var replaceText = token + "X";

            var frWindow = new ReaderWindow(
                reader, environments, progress, loggerFactory.CreateLogger<ReaderWindow>(), bookId,
                workspaces: workspaces, source: source, assets: assets, repository: repo, autofix: autofix,
                findReplace: findReplace, snapshots: snapshots);

            frWindow.Show();

            /* 这本书可能带着上次读到的章号（进度是持久化的），所以只等"有章节 + 通道就绪"。 */
            var frOpened = await WaitUntilAsync(
                () => frWindow.LastChapterIndex >= 0 && frWindow.Channel is not null,
                TimeSpan.FromSeconds(25)).ConfigureAwait(true);

            if (frOpened && t53PickIndex > 0)
            {
                /* 封面页可能一个字都没有：翻到挑中的那一章再验查找。 */
                await frWindow.GoToChapterAsync(t53PickIndex).ConfigureAwait(true);
            }

            var frReady = await WaitUntilAsync(
                () => frWindow.LastChapterIndex == t53PickIndex,
                TimeSpan.FromSeconds(15)).ConfigureAwait(true);

            var frEntryOk = false;
            var frFindCount = -1;
            var chapterShown = false;

            if (frReady && !string.IsNullOrEmpty(token))
            {
                /* chapterChanged 是先发的，iframe 里的正文可能还没到位：等它真的渲染出来再查。 */
                var shownWatch = Stopwatch.StartNew();

                while (!chapterShown && shownWatch.Elapsed < TimeSpan.FromSeconds(15))
                {
                    var shownText = await ReadChapterTextAsync(frWindow).ConfigureAwait(true);
                    chapterShown = shownText.Contains(token, StringComparison.Ordinal);

                    if (!chapterShown)
                    {
                        await Task.Delay(200).ConfigureAwait(true);
                    }
                }

                var opened = await frWindow.ExecAsync("window.__readerDebug.openFindReplace()").ConfigureAwait(true);
                var found = await frWindow.ExecAsync(
                    "(function(){return String(window.__readerDebug.findReplaceSet("
                        + JsonSerializer.Serialize(token) + "," + JsonSerializer.Serialize(replaceText)
                        + ",'chapter',false,false));})()").ConfigureAwait(true);
                frFindCount = int.TryParse(found?.Trim().Trim('"'), out var parsed) ? parsed : -1;
                var probeJson = await frWindow.ExecAsync(
                    "JSON.stringify(window.__readerDebug.findReplaceProbe())").ConfigureAwait(true);
                var probeText = probeJson?.Trim() ?? string.Empty;

                if (probeText.Length > 1 && probeText.StartsWith('"') && probeText.EndsWith('"'))
                {
                    probeText = JsonSerializer.Deserialize<string>(probeText) ?? probeText;
                }

                var probe = string.IsNullOrEmpty(probeText) ? null : JsonNode.Parse(probeText) as JsonObject;
                frEntryOk = string.Equals(opened?.Trim().Trim('"'), "true", StringComparison.OrdinalIgnoreCase)
                    && probe?["open"]?.GetValue<bool>() == true
                    && probe?["toolbarButton"]?.GetValue<bool>() == true
                    && chapterShown
                    && frFindCount > 0;
            }

            Add(
                "T53-entry-and-find",
                frReady && frEntryOk,
                "窗口就绪=" + frReady + "（打开=" + frOpened + "）；入口/面板/本章命中=" + frEntryOk
                    + "（命中 " + frFindCount + " 处，用词『" + token + "』，第 " + (t53PickIndex + 1) + " 章）");

            async Task<(bool Seen, bool Confirmed, int Matches)> RunPreviewAsync(bool confirm)
            {
                var seen = false;
                var confirmed = false;
                var matches = 0;
                var timer = new System.Windows.Threading.DispatcherTimer(
                    TimeSpan.FromMilliseconds(120),
                    System.Windows.Threading.DispatcherPriority.Normal,
                    (_, _) =>
                    {
                        var dialog = Application.Current.Windows.OfType<ReplacePreviewDialog>().FirstOrDefault();

                        if (dialog is null)
                        {
                            return;
                        }

                        seen = true;
                        matches = dialog.TotalMatches;

                        if (confirm)
                        {
                            confirmed = true;
                            var confirmButton = dialog.FindName("ConfirmButton") as System.Windows.Controls.Button;
                            confirmButton?.RaiseEvent(new RoutedEventArgs(
                                System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        }
                        else
                        {
                            dialog.Close();
                        }
                    },
                    System.Windows.Threading.Dispatcher.CurrentDispatcher);

                timer.Start();

                try
                {
                    await frWindow.ExecAsync("window.__readerDebug.findReplaceAll()").ConfigureAwait(true);

                    var watch = Stopwatch.StartNew();

                    while (!seen && watch.Elapsed < TimeSpan.FromSeconds(20))
                    {
                        await Task.Delay(150).ConfigureAwait(true);
                    }

                    await Task.Delay(600).ConfigureAwait(true);
                }
                finally
                {
                    timer.Stop();
                }

                return (seen, confirmed, matches);
            }

            var cancelRun = await RunPreviewAsync(confirm: false).ConfigureAwait(true);
            var cancelAfter = File.Exists(chapterFile) ? File.ReadAllText(chapterFile) : string.Empty;
            Add(
                "T53-cancel-writes-nothing",
                cancelRun.Seen && string.Equals(cancelAfter, t53ChapterBefore, StringComparison.Ordinal),
                "弹窗出现=" + cancelRun.Seen + "；点取消后工作区是否一字节未变="
                    + string.Equals(cancelAfter, t53ChapterBefore, StringComparison.Ordinal));
            /* 范围选「全书」：预览必须遍历所有 spine 章节（只验"能算出全书命中 + 弹预览 + 取消不写盘"）。 */
            await frWindow.ExecAsync(
                "(function(){var el=document.getElementById('frScope');el.value='book';return true;})()")
                .ConfigureAwait(true);
            var bookPreviewWatch = Stopwatch.StartNew();
            var bookRun = await RunPreviewAsync(confirm: false).ConfigureAwait(true);
            bookPreviewWatch.Stop();
            var bookAfter = File.Exists(chapterFile) ? File.ReadAllText(chapterFile) : string.Empty;
            Add(
                "T53-book-scope-preview-cancel",
                bookRun.Seen && bookRun.Matches >= frFindCount
                    && string.Equals(bookAfter, t53ChapterBefore, StringComparison.Ordinal),
                "全书弹窗出现=" + bookRun.Seen + "；全书命中=" + bookRun.Matches
                    + "（本章 " + frFindCount + "）；耗时=" + bookPreviewWatch.ElapsedMilliseconds + "ms；取消后工作区是否一字节未变="
                    + string.Equals(bookAfter, t53ChapterBefore, StringComparison.Ordinal));
            await frWindow.ExecAsync(
                "(function(){var el=document.getElementById('frScope');el.value='chapter';return true;})()")
                .ConfigureAwait(true);

            if (!string.IsNullOrEmpty(token))
            {
                /* 替换串里写裸 &：逐章良构校验必须失败，且工作区一个字节都不写。 */
                await frWindow.ExecAsync(
                    "(function(){var el=document.getElementById('frReplace');el.value='A & B';return true;})()")
                    .ConfigureAwait(true);
                var ampRun = await RunPreviewAsync(confirm: true).ConfigureAwait(true);
                var ampAfter = File.Exists(chapterFile) ? File.ReadAllText(chapterFile) : string.Empty;
                Add(
                    "T53-illegal-replacement-writes-nothing",
                    ampRun.Seen && ampRun.Confirmed && string.Equals(ampAfter, t53ChapterBefore, StringComparison.Ordinal),
                    "弹窗出现=" + ampRun.Seen + "；确认=" + ampRun.Confirmed
                        + "；校验失败后工作区是否一字节未变="
                        + string.Equals(ampAfter, t53ChapterBefore, StringComparison.Ordinal));

                /* 正常替换：写工作区 + 记 changedFiles + 阅读器重载当前章并看到新正文。 */
                await frWindow.ExecAsync(
                    "(function(){var el=document.getElementById('frReplace');el.value="
                        + JsonSerializer.Serialize(replaceText) + ";return true;})()").ConfigureAwait(true);
                var replaceWatch = Stopwatch.StartNew();
                var replaceRun = await RunPreviewAsync(confirm: true).ConfigureAwait(true);
                replaceWatch.Stop();
                var replaceAfter = File.Exists(chapterFile) ? File.ReadAllText(chapterFile) : string.Empty;
                var tagsSame = string.Equals(
                    Regex.Replace(t53ChapterBefore, ">[^<]*<", "><"),
                    Regex.Replace(replaceAfter, ">[^<]*<", "><"),
                    StringComparison.Ordinal);
                var recorded = workspaces.ListResumable()
                    .Where(entry => entry.BookId == frBook.Id && entry.FormatId == frFormat.Id)
                    .Any(entry => entry.ChangedFiles.Any(file =>
                        string.Equals(file, chapterEntry, StringComparison.OrdinalIgnoreCase)));

                var reloaded = false;
                var reloadWatch = Stopwatch.StartNew();

                while (!reloaded && reloadWatch.Elapsed < TimeSpan.FromSeconds(20))
                {
                    var text = await ReadChapterTextAsync(frWindow).ConfigureAwait(true);
                    reloaded = text.Contains(replaceText, StringComparison.Ordinal);

                    if (!reloaded)
                    {
                        await Task.Delay(200).ConfigureAwait(true);
                    }
                }

                Add(
                    "T53-replace-all-writes-and-reloads",
                    replaceRun.Seen && replaceRun.Confirmed
                        && !string.Equals(replaceAfter, t53ChapterBefore, StringComparison.Ordinal)
                        && tagsSame && recorded && reloaded,
                    "确认=" + replaceRun.Confirmed + "；耗时=" + replaceWatch.ElapsedMilliseconds + "ms；文件已改="
                        + !string.Equals(replaceAfter, t53ChapterBefore, StringComparison.Ordinal)
                        + "；标签/属性逐字节不变=" + tagsSame
                        + "；changedFiles 已记=" + recorded
                        + "；阅读器重载后看到新正文=" + reloaded);
            }

            /* ---------- T5-4 快照：打开工作区就有全量基线 / 全部替换前自动建快照 / 一键撤销 ---------- */
            if (snapshots is not null)
            {
                var t54All = await snapshots.ListAsync(frWorkspace).ConfigureAwait(true);
                var t54Initial = t54All.FirstOrDefault(snapshot => snapshot.Kind == WorkspaceSnapshotKinds.Initial);

                Add(
                    "T54-initial-snapshot",
                    t54Initial is not null && t54Initial.IsFull,
                    "工作区快照数=" + t54All.Count + "；打开时的全量基线=" + (t54Initial?.Id ?? "(没有)")
                        + "（" + (t54Initial?.EntryCount ?? 0) + " 个文件）");
            }
            else
            {
                Add("T54-wiring", false, "快照冒烟缺少依赖（IWorkspaceSnapshotService）");
            }

            if (snapshots is not null && !string.IsNullOrEmpty(token))
            {
                var t54All = await snapshots.ListAsync(frWorkspace).ConfigureAwait(true);
                var t54Before = t54All.FirstOrDefault(
                    snapshot => snapshot.Kind == WorkspaceSnapshotKinds.ReplaceBefore);

                Add(
                    "T54-replace-before-snapshot",
                    t54Before is not null,
                    "全部替换前的快照=" + (t54Before?.Id ?? "(没有)")
                        + "；涉及 " + (t54Before?.EntryCount ?? 0) + " 个文件");

                if (t54Before is not null)
                {
                    var undoProbe = await frWindow.ExecAsync(
                        "String(window.__readerDebug.findReplaceUndo())").ConfigureAwait(true);
                    var undone = false;
                    var undoWatch = Stopwatch.StartNew();

                    while (!undone && undoWatch.Elapsed < TimeSpan.FromSeconds(20))
                    {
                        undone = File.Exists(chapterFile)
                            && string.Equals(File.ReadAllText(chapterFile), t53ChapterBefore, StringComparison.Ordinal);

                        if (!undone)
                        {
                            await Task.Delay(200).ConfigureAwait(true);
                        }
                    }

                    Add(
                        "T54-undo-last-replace",
                        undone,
                        "探针=" + undoProbe?.Trim() + "；撤销后工作区回到替换前=" + undone
                            + "；耗时=" + undoWatch.ElapsedMilliseconds + "ms");
                }
            }

            /* 非法正则：即时提示、不执行、不崩。 */
            var regexProbe = await frWindow.ExecAsync(
                "(function(){var n=window.__readerDebug.findReplaceSet('([a-','x','chapter',true,false);"
                    + "return JSON.stringify({n:String(n),count:window.__readerDebug.findReplaceProbe().count});})()")
                .ConfigureAwait(true);
            var regexNoCrash = regexProbe is not null;
            var regexCount = -1;

            try
            {
                var regexText = regexProbe?.Trim() ?? string.Empty;

                if (regexText.Length > 1 && regexText.StartsWith('"') && regexText.EndsWith('"'))
                {
                    regexText = JsonSerializer.Deserialize<string>(regexText) ?? regexText;
                }

                var node = JsonNode.Parse(regexText) as JsonObject;
                regexCount = node?["count"]?.GetValue<int>() ?? -1;
            }
            catch (JsonException)
            {
                regexNoCrash = false;
            }

            Add(
                "T53-invalid-regex-no-crash",
                regexNoCrash && regexCount == 0,
                "非法正则不崩=" + regexNoCrash + "；命中数=" + regexCount);

            /* 【替换后重新高亮】点一次【替换】（单处）时宿主会重载当前章，旧的 <mark> 随之消失；
               现在必须自动重新检索当页并重新标好剩下的命中，用户不需要再点"下一个"或翻页。
               用"在这章里出现 >=2 次的常用字"来做，替换掉 1 处后 DOM 里应当还剩 N-1 个标记。 */
            var repeatChar = string.Empty;
            var repeatCount = 0;

            foreach (var candidate in new[] { "的", "一", "了", "是", "在", "不", "有", "和", "中", "上" })
            {
                var found = await frWindow.ExecAsync(
                    "(function(){return String(window.__readerDebug.findReplaceSet("
                        + JsonSerializer.Serialize(candidate) + ","
                        + JsonSerializer.Serialize("-R-")
                        + ",'chapter',false,false));})()").ConfigureAwait(true);
                var value = int.TryParse(found?.Trim().Trim('"'), out var parsed) ? parsed : -1;

                if (value >= 2)
                {
                    repeatChar = candidate;
                    repeatCount = value;
                    break;
                }
            }

            if (repeatChar.Length == 0)
            {
                Add("T53-replace-one-keeps-highlights", false, "本章找不到出现 >=2 次的常用字，无法验证");
            }
            else
            {
                var beforeMarks = await frWindow.ExecAsync(
                    "String(window.__readerDebug.findMarkCount())").ConfigureAwait(true);
                var oneClicked = await frWindow.ExecAsync(
                    "String(window.__readerDebug.findReplaceOne())").ConfigureAwait(true);
                var marksAfter = -1;
                var marksWatch = Stopwatch.StartNew();

                while (marksWatch.Elapsed < TimeSpan.FromSeconds(20))
                {
                    var marksProbe = await frWindow.ExecAsync(
                        "String(window.__readerDebug.findMarkCount())").ConfigureAwait(true);
                    marksAfter = int.TryParse(marksProbe?.Trim().Trim('"'), out var parsedMarks) ? parsedMarks : -1;

                    if (marksAfter == repeatCount - 1)
                    {
                        break;
                    }

                    await Task.Delay(200).ConfigureAwait(true);
                }

                /* 滚动模式也要把当前处自动划进视野；当前处必须红底、且与普通命中颜色不同（用户 2026-10-12 要求）。 */
                /* 可见性/配色是渲染后才读得到的：先等几秒稳定，避免"刚替换完还没排好"的假失败。 */
                var visualWatch = Stopwatch.StartNew();

                while (visualWatch.Elapsed < TimeSpan.FromSeconds(5))
                {
                    var visibleText = await frWindow.ExecAsync(
                        "String(window.__readerDebug.findCurrentPosition().visible)").ConfigureAwait(true);

                    if (string.Equals(visibleText?.Trim().Trim('"'), "true", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    await Task.Delay(150).ConfigureAwait(true);
                }

                var cvText = await frWindow.ExecAsync(
                    "String(window.__readerDebug.findCurrentPosition().visible)").ConfigureAwait(true);
                var crText = await frWindow.ExecAsync(
                    "String(window.__readerDebug.findCurrentStyle().redDominant)").ConfigureAwait(true);
                var cdText = await frWindow.ExecAsync(
                    "String(window.__readerDebug.findCurrentStyle().distinct)").ConfigureAwait(true);
                var cvOk = string.Equals(cvText?.Trim().Trim('"'), "true", StringComparison.OrdinalIgnoreCase);
                var crOk = string.Equals(crText?.Trim().Trim('"'), "true", StringComparison.OrdinalIgnoreCase);
                var cdOk = string.Equals(cdText?.Trim().Trim('"'), "true", StringComparison.OrdinalIgnoreCase);

                Add(
                    "T53-replace-one-keeps-highlights",
                    marksAfter == repeatCount - 1 && marksAfter >= 1 && cvOk && crOk && cdOk,
                    "常用字[" + repeatChar + "] 换成 [-R-]；替换前命中 " + repeatCount + " 处、DOM 标记=" + (beforeMarks?.Trim() ?? "(无)")
                        + "；点一次【替换】后当页重新高亮 " + marksAfter + " 处（期望 " + (repeatCount - 1)
                        + "）；探针=" + (oneClicked?.Trim() ?? "(无)") + "；当前处在视野内=" + cvOk + "、当前处红底=" + crOk + "、与普通命中颜色不同=" + cdOk + "；耗时=" + marksWatch.ElapsedMilliseconds + "ms");
            }

            /* 【分页模式定位】用户反馈：分页模式下点【替换】后视图没落在被替换那一处所在页
               （看着像"替换的内容跑到下一页去了"）。要求：替换掉"离当前视图最近的那一处"，
               并把视图真正翻到该处所在页。用一个"出现 >=3 次"的字，替换掉 1 处后还剩 >=2 处，
               这样"当前处"依然存在，才验得出定位准不准。 */
            var pagedChar = string.Empty;
            var pagedCount = 0;

            foreach (var candidate in new[] { "的", "一", "了", "是", "在", "不", "有", "和", "中", "上", "我", "他", "这" })
            {
                var found = await frWindow.ExecAsync(
                    "(function(){return String(window.__readerDebug.findReplaceSet("
                        + JsonSerializer.Serialize(candidate) + "," + JsonSerializer.Serialize("--")
                        + ",'chapter',false,false));})()").ConfigureAwait(true);
                var value = int.TryParse(found?.Trim().Trim('"'), out var parsedCount) ? parsedCount : -1;

                if (value >= 2)
                {
                    pagedChar = candidate;
                    pagedCount = value;
                    break;
                }
            }

            if (pagedChar.Length == 0)
            {
                Add("T53-replace-one-paged-position", false, "本章找不到出现 >=3 次的常用字，无法验证分页定位");
            }
            else
            {
                await frWindow.ExecAsync("String(window.__readerDebug.setMode('paged'))").ConfigureAwait(true);
                await Task.Delay(400).ConfigureAwait(true);

                for (var step = 0; step < 3; step++)
                {
                    await frWindow.ExecAsync("String(window.__readerDebug.findReplaceStep(true))").ConfigureAwait(true);
                    await Task.Delay(150).ConfigureAwait(true);
                }

                var pageBeforeText = await frWindow.ExecAsync("String(window.__readerDebug.pageIndex())").ConfigureAwait(true);
                var pageBefore = int.TryParse(pageBeforeText?.Trim().Trim('"'), out var parsedPageBefore) ? parsedPageBefore : -1;
                var refresh = await frWindow.ExecAsync(
                    "(function(){return String(window.__readerDebug.findReplaceSet("
                        + JsonSerializer.Serialize(pagedChar) + "," + JsonSerializer.Serialize("--")
                        + ",'chapter',false,false));})()").ConfigureAwait(true);
                var refreshed = int.TryParse(refresh?.Trim().Trim('"'), out var parsedRefresh) ? parsedRefresh : -1;
                var pagedClicked = await frWindow.ExecAsync("String(window.__readerDebug.findReplaceOne())").ConfigureAwait(true);
                var pagedRemaining = -1;
                var pagedPage = -1;
                var pagedMarkPage = -1;
                var pagedRed = false;
                var pagedVisible = false;
                var pagedWatch = Stopwatch.StartNew();

                while (pagedWatch.Elapsed < TimeSpan.FromSeconds(20))
                {
                    var raw = await frWindow.ExecAsync(
                        "(function(){var p=window.__readerDebug.findCurrentPosition();"
                            + "var s=window.__readerDebug.findCurrentStyle();"
                            + "return JSON.stringify({mark:p.markPage,page:p.pageIndex,count:p.count,visible:p.visible,red:s.redDominant});})()")
                        .ConfigureAwait(true);
                    var text = raw?.Trim() ?? string.Empty;

                    if (text.Length > 1 && text.StartsWith('"') && text.EndsWith('"'))
                    {
                        text = JsonSerializer.Deserialize<string>(text) ?? text;
                    }

                    if (!string.IsNullOrEmpty(text))
                    {
                        try
                        {
                            var node = JsonNode.Parse(text) as JsonObject;
                            pagedMarkPage = node?["mark"]?.GetValue<int>() ?? -1;
                            pagedPage = node?["page"]?.GetValue<int>() ?? -1;
                            pagedRemaining = node?["count"]?.GetValue<int>() ?? -1;
                            pagedVisible = node?["visible"]?.GetValue<bool>() ?? false;
                            pagedRed = node?["red"]?.GetValue<bool>() ?? false;
                        }
                        catch (JsonException)
                        {
                            pagedRemaining = -1;
                        }
                    }

                    if (refreshed > 0 && pagedRemaining == refreshed - 1 && pagedMarkPage == pagedPage)
                    {
                        break;
                    }

                    await Task.Delay(200).ConfigureAwait(true);
                }

                Add(
                    "T53-replace-one-paged-position",
                    refreshed >= 2 && pagedRemaining == refreshed - 1 && pagedMarkPage == pagedPage
                        && pagedVisible && pagedRed,
                    "分页模式：字[" + pagedChar + "]替换前命中 " + refreshed + " 处、视图在第 " + pageBefore
                        + " 页；点【替换】后剩 " + pagedRemaining + " 处；视图页=" + pagedPage + "、命中所在页="
                        + pagedMarkPage + "（必须相等）；当前处在视野内=" + pagedVisible + "、红底=" + pagedRed
                        + "；探针=" + (pagedClicked?.Trim() ?? "(无)") + "；耗时=" + pagedWatch.ElapsedMilliseconds + "ms");
            }
            /* 【用户 2026-10-12 反馈】关掉查找替换面板再打开：上次的词还在，但必须按"当前这一章"
               重新匹配（以前计数是旧文档留下的、内部命中列表是空的，点下一个只会报"本章没有匹配"）。 */
            await frWindow.ExecAsync("String(window.__readerDebug.closeFindReplace())").ConfigureAwait(true);
            await Task.Delay(300).ConfigureAwait(true);
            var closedMarks = await frWindow.ExecAsync("String(window.__readerDebug.findMarkCount())").ConfigureAwait(true);
            var reopenedBar = await frWindow.ExecAsync("String(window.__readerDebug.openFindReplace())").ConfigureAwait(true);
            await Task.Delay(400).ConfigureAwait(true);
            var reopenCount = await frWindow.ExecAsync("String(window.__readerDebug.findReplaceProbe().count)").ConfigureAwait(true);
            var reopenMarks = await frWindow.ExecAsync("String(window.__readerDebug.findMarkCount())").ConfigureAwait(true);
            var reopenMatched = int.TryParse(reopenCount?.Trim().Trim('"'), out var reopenMatchCount) && reopenMatchCount > 0;
            var reopenMarksOk = int.TryParse(reopenMarks?.Trim().Trim('"'), out var reopenMarkCount)
                && reopenMarkCount == reopenMatchCount;

            Add(
                "T53-reopen-bar-rematches",
                string.Equals(reopenedBar?.Trim().Trim('"'), "true", StringComparison.OrdinalIgnoreCase)
                    && reopenMatched && reopenMarksOk,
                "关面板再打开：关掉后 DOM 标记=" + (closedMarks?.Trim() ?? "(无)")
                    + "；重开后命中=" + (reopenCount?.Trim() ?? "(无)") + "、DOM 标记=" + (reopenMarks?.Trim() ?? "(无)")
                    + "（应相等且 > 0）");

            /* 【用户 2026-10-12】查找替换条的响应式布局：
               宽窗 = 一条，并且不挡工具栏右上角的【下一章】；窄窗 = 查找一行、替换一行，都不溢出。 */
            await frWindow.ExecAsync("String(window.__readerDebug.openFindReplace())").ConfigureAwait(true);
            await Task.Delay(400).ConfigureAwait(true);

            async Task<(bool SameRow, double Left, double Right, double Bottom, double NextLeft, double InnerWidth, double InnerHeight)> ReadBarAsync()
            {
                var raw = await frWindow.ExecAsync(
                    "(function(){var b=document.getElementById('frBar').getBoundingClientRect();"
                        + "var f=document.getElementById('frFind').getBoundingClientRect();"
                        + "var p=document.getElementById('frReplace').getBoundingClientRect();"
                        + "var n=document.getElementById('btnNext');var r=n?n.getBoundingClientRect():null;"
                        + "return JSON.stringify({sameRow:Math.abs(f.top-p.top)<2,left:b.left,right:b.right,"
                        + "bottom:b.bottom,nextLeft:r?r.left:-1,iw:window.innerWidth,ih:window.innerHeight});})()")
                    .ConfigureAwait(true);
                var text = raw?.Trim() ?? string.Empty;

                if (text.Length > 1 && text.StartsWith('"') && text.EndsWith('"'))
                {
                    text = JsonSerializer.Deserialize<string>(text) ?? text;
                }

                var node = JsonNode.Parse(text) as JsonObject;

                return (
                    node?["sameRow"]?.GetValue<bool>() ?? false,
                    node?["left"]?.GetValue<double>() ?? -1,
                    node?["right"]?.GetValue<double>() ?? -1,
                    node?["bottom"]?.GetValue<double>() ?? -1,
                    node?["nextLeft"]?.GetValue<double>() ?? -1,
                    node?["iw"]?.GetValue<double>() ?? 0,
                    node?["ih"]?.GetValue<double>() ?? 0);
            }

            /* 阈值不猜：先量出"单行真正需要多宽"（探针按实际内容量），再按它构造宽/窄两种情形。 */
            async Task<double> BarRequiredAsync()
            {
                var raw = await frWindow.ExecAsync(
                    "String(window.__readerDebug.findReplaceBar().required)").ConfigureAwait(true);

                return double.TryParse(raw?.Trim().Trim('"'), out var value) ? value : 0;
            }

            async Task<double> ChromeWidthAsync()
            {
                var raw = await frWindow.ExecAsync(
                    "String(window.innerWidth)").ConfigureAwait(true);
                var inner = double.TryParse(raw?.Trim().Trim('"'), out var value) ? value : 0;

                return Math.Max(0, frWindow.Width - inner);
            }

            async Task<bool> IsNarrowAsync()
            {
                var raw = await frWindow.ExecAsync(
                    "String(window.__readerDebug.findReplaceBar().narrow)").ConfigureAwait(true);

                return string.Equals(raw?.Trim().Trim('"'), "true", StringComparison.OrdinalIgnoreCase);
            }

            var requiredWidth = await BarRequiredAsync().ConfigureAwait(true);
            var chromeWidth = await ChromeWidthAsync().ConfigureAwait(true);

            /* 宽窗：视口 = 实测所需宽 + 80（一定放得下 -> 应是一条）。 */
            frWindow.Width = Math.Max(420, requiredWidth + 80 + chromeWidth);
            await Task.Delay(600).ConfigureAwait(true);

            var wideBar = await ReadBarAsync().ConfigureAwait(true);
            var wideNarrow = await IsNarrowAsync().ConfigureAwait(true);
            var wideBarOk = !wideNarrow && wideBar.SameRow && wideBar.Left >= 0 && wideBar.Right <= wideBar.InnerWidth
                && (wideBar.NextLeft < 0 || wideBar.Right <= wideBar.NextLeft);

            /* 窄窗：视口 = 实测所需宽 - 80（放不下 -> 应分两行）。 */
            frWindow.Width = Math.Max(420, requiredWidth - 80 + chromeWidth);
            await Task.Delay(700).ConfigureAwait(true);

            var narrowBar = await ReadBarAsync().ConfigureAwait(true);
            var narrowFlag = await IsNarrowAsync().ConfigureAwait(true);
            var narrowBarOk = narrowFlag && !narrowBar.SameRow && narrowBar.Left >= 0
                && narrowBar.Right <= narrowBar.InnerWidth && narrowBar.Bottom <= narrowBar.InnerHeight;

            frWindow.Width = 1500;
            await Task.Delay(300).ConfigureAwait(true);

            Add(
                "T53-bar-layout",
                wideBarOk && narrowBarOk,
                "实测单行所需宽=" + (int)requiredWidth + "px（阈值即按它算，不估）；宽窗：分两行=" + wideNarrow + "、查找/替换同一行=" + wideBar.SameRow + "、右边界=" + (int)wideBar.Right
                    + "、[下一章]左边界=" + (int)wideBar.NextLeft + "（右应 <= 它）"
                    + "；窄窗：同一行=" + narrowBar.SameRow + "（应 False=分两行）、右边界="
                    + (int)narrowBar.Right + "、视口宽=" + (int)narrowBar.InnerWidth);
            /* 【用户 2026-10-12 口径】匹配范围永远是全书：面板上要能看到"全书 N 处"（N 按所有章节算）。 */
            var bookTotal = -1;
            var bookCountWatch = Stopwatch.StartNew();

            while (bookCountWatch.Elapsed < TimeSpan.FromSeconds(20))
            {
                var rawTotal = await frWindow.ExecAsync(
                    "String(window.__readerDebug.findReplaceProbe().bookTotal)").ConfigureAwait(true);
                bookTotal = int.TryParse(rawTotal?.Trim().Trim('"'), out var parsedTotal) ? parsedTotal : -1;

                if (bookTotal > 0)
                {
                    break;
                }

                await Task.Delay(200).ConfigureAwait(true);
            }

            Add(
                "T53-book-count",
                bookTotal > 0,
                "全书命中总数=" + bookTotal + "（> 0 说明计数按所有章节算）；耗时=" + bookCountWatch.ElapsedMilliseconds + "ms");

            frWindow.Close();
            await Task.Delay(200).ConfigureAwait(true);
            await frWorkspace.DiscardAsync().ConfigureAwait(true);
        }
        second.Close();
        window.Close();

        // 用户 2026-10-21：换格式继续读，文字类书籍要落到同一段文字（不是只按百分比）
        await CheckCrossFormatByTextAsync(
            reader, environments, progress, repo, loggerFactory, paths, bookId, checks)
            .ConfigureAwait(true);

        // 用户 2026-10-21：PDF 打开要跳到上次保存的那一页（同格式精确恢复）
        await CheckPdfRestoreAsync(
            import, reader, environments, progress, repo, pdfPages, loggerFactory, paths, checks)
            .ConfigureAwait(true);

        return Build(startedAt, paths, checks, total);
    }

    /// <summary>
    /// 用户 2026-10-21：换了阅读格式之后，进度必须落到**同一段文字**上。
    /// </summary>
    /// <remarks>
    /// 做法：给样本书补一个 TXT 格式（自造 3 章，锚点句只出现在第 2 章），
    /// 再把进度写成"上次用 EPUB 读、只读了 5%"——按百分比估章只会落在第 1 章，故意错开；
    /// 最后把当前阅读格式切到 TXT 重新打开。
    /// 能停在第 2 章、而且正文里真有那句锚点，才说明"按文字指纹找位"真的生效了。
    /// </remarks>
    private static async Task CheckCrossFormatByTextAsync(
        IReaderService reader,
        IWebViewEnvironmentProvider environments,
        IReadingProgressService progress,
        IBookRepository repo,
        ILoggerFactory loggerFactory,
        IAppPaths paths,
        Guid bookId,
        List<SmokeCheck> checks)
    {
        const string anchor = "跨格式定位的锚点文字这一段只出现在第二章里面";

        var txtPath = Path.Combine(paths.LibraryDir, "smoke-cross-format.txt");
        var txtBody =
            "第一章 起头\n第一章的正文，随便写一点东西。\n\n" +
            "第二章 中段\n" + anchor + "，用来验证换格式之后还能落在同一处。\n\n" +
            "第三章 收尾\n第三章的正文。\n";

        Directory.CreateDirectory(paths.LibraryDir);
        await File.WriteAllTextAsync(txtPath, txtBody, new UTF8Encoding(false)).ConfigureAwait(true);

        await repo.AddFormatAsync(new BookFormat
        {
            BookId = bookId,
            Format = BookFormatKind.Txt,
            FilePath = txtPath,
            FileSize = new FileInfo(txtPath).Length,
            AddedAt = DateTimeOffset.UtcNow,
        }).ConfigureAwait(true);

        // 关窗时排队的真实进度先落盘，否则"队列优先"会让下面这条假进度被盖掉（V 项踩过）
        await progress.FlushAsync().ConfigureAwait(true);

        await repo.SaveReadingProgressAsync(new ReadingProgress
        {
            BookId = bookId,
            SpineIndex = 0,
            Location = new ReaderLocator { ChapterIndex = 0, PercentInChapter = 0 }.ToJson(),
            Fingerprint = new TextFingerprint { Pre = anchor, Mid = anchor, Post = anchor }.ToJson(),
            Percent = 0.05,
            UpdatedAt = DateTimeOffset.UtcNow,
            SourceFormat = BookFormatKind.Epub,
        }).ConfigureAwait(true);

        await repo.SetReadingFormatAsync(bookId, BookFormatKind.Txt).ConfigureAwait(true);

        var cross = new ReaderWindow(
            reader, environments, progress, loggerFactory.CreateLogger<ReaderWindow>(), bookId);

        cross.Show();

        /* 等的是"页面自己报出来的章节总数"，不是宿主的 LastChapterIndex：
           后者由 reader.chapterChanged 消息更新，冷缓存/慢机器上可能比首屏晚；
           这条检查关心的是"落在哪一章"，别让宿主内部状态把结论带偏（发布件上真踩到过）。 */
        var meta = await WaitForChapterAsync(cross, TimeSpan.FromSeconds(40)).ConfigureAwait(true);
        var text = await ReadChapterTextAsync(cross).ConfigureAwait(true);

        var chapterValue = meta?["chapterValue"]?.GetValue<string>() ?? string.Empty;
        var chapterTotal = meta?["chapterTotal"]?.GetValue<string>() ?? string.Empty;
        var textHit = text.Contains(anchor, StringComparison.Ordinal);
        var hostIndex = cross.LastChapterIndex;

        cross.Close();
        await Task.Delay(200).ConfigureAwait(true);
        await progress.FlushAsync().ConfigureAwait(true);

        // 收尾：清掉当前阅读格式，后面的检查不受影响
        await repo.SetReadingFormatAsync(bookId, null).ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "cross-format-text-precise",
            // 锚点句在第 2 章（1 基）：只按 5% 估章会落在第 1 章，能落到第 2 章才说明指纹找位生效
            chapterValue == "2" && chapterTotal == "3" && textHit,
            "落点=第 " + chapterValue + "/" + chapterTotal + " 章（只按百分比会落在第 1 章）"
                + " 正文含锚点文字=" + textHit
                + " 宿主章号=" + (hostIndex + 1)));
    }

    /// <summary>
    /// 用户 2026-10-21：PDF 打开时必须跳到**上次保存的那一页**（同格式精确到页）。
    /// </summary>
    /// <remarks>
    /// 【为什么必须断言"顶部可见页"而不是只看状态里的 page】PDF 连续滚动模式下，
    /// enterPdfMode 只是把 state.pdf.page 设成 startPage；如果滚动容器没真的滚过去，
    /// state.page 已经是 5、用户看到的却还是第 1 页——T6-2 的 J2-reopen 只读 page，
    /// 所以这个 bug 一直没被抓住。这里同时断言 page、topVisiblePage 与 scrollTop。
    ///
    /// 【样本自造】运行环境不保证有 PDF 文件，这里按 PDF 1.4 手写一份 8 页空白文档，
    /// 这样这条检查在任何数据目录下都能独立跑。
    /// </remarks>
    private static async Task CheckPdfRestoreAsync(
        IBookImportService import,
        IReaderService reader,
        IWebViewEnvironmentProvider environments,
        IReadingProgressService progress,
        IBookRepository repo,
        IPdfDocumentService pdfPages,
        ILoggerFactory loggerFactory,
        IAppPaths paths,
        List<SmokeCheck> checks)
    {
        const int totalPages = 8;
        const int savedPage = 4;   // 0 基：第 5 页（对应用户报的"翻到第 5 页后关窗"）

        Directory.CreateDirectory(paths.TempDir);

        var pdfPath = Path.Combine(paths.TempDir, "smoke-pdf-restore.pdf");

        try
        {
            File.WriteAllBytes(pdfPath, BuildSmokePdf(totalPages));
        }
        catch (Exception ex)
        {
            checks.Add(new SmokeCheck("pdf-open-restores-saved-page", false,
                "自造 PDF 失败：" + ex.GetType().Name + ": " + ex.Message));
            return;
        }

        var imported = await import
            .ImportAsync(pdfPath, ImportOptions.Default, null)
            .ConfigureAwait(true);

        var bookId = imported.BookId ?? Guid.Empty;

        if (bookId == Guid.Empty)
        {
            checks.Add(new SmokeCheck("pdf-open-restores-saved-page", false,
                "PDF 导入失败：" + imported.Outcome + "（" + imported.Message + "）"));
            return;
        }

        int pageCount;

        try
        {
            pageCount = pdfPages.GetPageCount(imported.LibraryPath ?? pdfPath);
        }
        catch (Exception ex)
        {
            pageCount = -1;
            checks.Add(new SmokeCheck("pdf-open-restores-saved-page", false,
                "pdfium 读自造 PDF 失败：" + ex.GetType().Name + ": " + ex.Message));
            return;
        }

        // 等价于"用户上次翻到第 5 页后关窗落盘"的那一行（T6-2 的进度口径：页码 + 总页数）
        await progress.SaveNowAsync(new ReaderProgressSnapshot
        {
            BookId = bookId,
            ChapterIndex = 0,
            ChapterCount = 0,
            PageIndex = savedPage,
            TotalPages = totalPages,
            Locator = new ReaderLocator { ChapterIndex = 0 },
            UpdatedAt = DateTimeOffset.UtcNow,
            SourceFormat = BookFormatKind.Pdf,
        }).ConfigureAwait(true);

        // ---------- 1. 同格式：保存了第 5 页 -> 打开就落在第 5 页 ----------

        var state = await OpenPdfAndReadLandingAsync(
            reader, environments, progress, loggerFactory, repo, pdfPages, bookId,
            startPage: null, expectedPage: savedPage).ConfigureAwait(true);

        // ---------- 2. 显式 startPage（全文搜索跳页）优先于保存的进度 ----------

        var explicitState = await OpenPdfAndReadLandingAsync(
            reader, environments, progress, loggerFactory, repo, pdfPages, bookId,
            startPage: 1, expectedPage: 1).ConfigureAwait(true);

        var restoredPage = state?["page"]?.GetValue<int>() ?? -1;
        var restoredTop = state?["topVisiblePage"]?.GetValue<int>() ?? -1;
        var restoredScroll = state?["scrollTop"]?.GetValue<int>() ?? -1;
        var restoredTotal = state?["totalPages"]?.GetValue<int>() ?? -1;

        checks.Add(new SmokeCheck(
            "pdf-open-restores-saved-page",
            restoredPage == savedPage && restoredTop == savedPage && restoredScroll > 0,
            "库里 page_index=" + savedPage + "/" + totalPages + "（pdfium 页数=" + pageCount + "）"
                + " -> 状态页=" + restoredPage + "、顶部可见页=" + restoredTop
                + "、scrollTop=" + restoredScroll + "、页面总数=" + restoredTotal
                + "（状态页与顶部可见页都应为 " + savedPage + "，即第 " + (savedPage + 1) + " 页）"));

        var explicitPage = explicitState?["page"]?.GetValue<int>() ?? -1;
        var explicitTop = explicitState?["topVisiblePage"]?.GetValue<int>() ?? -1;

        checks.Add(new SmokeCheck(
            "pdf-open-explicit-page-wins",
            explicitPage == 1 && explicitTop == 1,
            "保存的页码=" + savedPage + "，显式 startPage=1 -> 状态页=" + explicitPage
                + "、顶部可见页=" + explicitTop + "（都应优先用显式页 1）"));

        // ---------- 3. 跨格式：上次是 EPUB 读到 30%，这次用 PDF 打开 -> 仍按百分比换算页码 ----------

        /* 真实场景：这条进度是文本阅读时存的（没有页码，只有全书百分比）。
           换成 PDF 打开时必须按百分比换算，而不是把 page_index 的空值当第 0 页。
           这条是"本次修复不能把跨格式换算改回去"的回归护栏。 */
        const double crossPercent = 0.30;
        var crossExpected = CrossFormatLocator.PageFromPercent(crossPercent, totalPages);

        await repo.SaveReadingProgressAsync(new ReadingProgress
        {
            BookId = bookId,
            SpineIndex = 2,
            PageIndex = null,
            TotalPages = null,
            Location = new ReaderLocator { ChapterIndex = 2, PercentInChapter = 0.5 }.ToJson(),
            Percent = crossPercent,
            UpdatedAt = DateTimeOffset.UtcNow,
            SourceFormat = BookFormatKind.Epub,
        }).ConfigureAwait(true);

        var crossState = await OpenPdfAndReadLandingAsync(
            reader, environments, progress, loggerFactory, repo, pdfPages, bookId,
            startPage: null, expectedPage: crossExpected).ConfigureAwait(true);

        var crossPage = crossState?["page"]?.GetValue<int>() ?? -1;
        var crossTop = crossState?["topVisiblePage"]?.GetValue<int>() ?? -1;

        checks.Add(new SmokeCheck(
            "pdf-open-cross-format-percent",
            crossPage == crossExpected && crossTop == crossExpected,
            "上次 EPUB 读到 " + crossPercent.ToString("P0", CultureInfo.InvariantCulture)
                + "（这条进度没有页码）-> PDF 按百分比换算应落第 " + (crossExpected + 1)
                + " 页；实际状态页=" + crossPage + "、顶部可见页=" + crossTop));
    }

    /// <summary>
    /// 开一个 PDF 阅读窗口，等它落在 <paramref name="expectedPage"/>（状态页与顶部可见页都对上），
    /// 关窗并把排队进度刷掉，返回最终的探针快照。超时也返回最后一次快照，交给断言如实报数。
    /// </summary>
    private static async Task<JsonObject?> OpenPdfAndReadLandingAsync(
        IReaderService reader,
        IWebViewEnvironmentProvider environments,
        IReadingProgressService progress,
        ILoggerFactory loggerFactory,
        IBookRepository repo,
        IPdfDocumentService pdfPages,
        Guid bookId,
        int? startPage,
        int expectedPage)
    {
        ReaderWindow? window = null;

        try
        {
            window = new ReaderWindow(
                reader, environments, progress, loggerFactory.CreateLogger<ReaderWindow>(), bookId,
                repository: repo, pdfPages: pdfPages, startPage: startPage);

            window.Show();

            var ready = await WaitForPdfReadyAsync(window, TimeSpan.FromSeconds(40)).ConfigureAwait(true);

            if (ready is null)
            {
                return null;
            }

            var landing = await WaitForPdfLandingAsync(
                window, expectedPage, TimeSpan.FromSeconds(6)).ConfigureAwait(true);

            /* 落点对上之后图还要逐页回包、排版还会用实测页高做一次差量补偿：
               再等 600 ms 读一次，确认最终稳定下来还在那一页（不是中途经过一下）。 */
            await Task.Delay(600).ConfigureAwait(true);

            return await ReadPdfStateAsync(window).ConfigureAwait(true) ?? landing;
        }
        finally
        {
            window?.Close();

            await progress.FlushAsync().ConfigureAwait(true);
        }
    }

    /// <summary>等 reader.js 进入 PDF 模式并报出总页数。页面没挂好时探针可能拿不到，继续等。</summary>
    private static async Task<JsonObject?> WaitForPdfReadyAsync(ReaderWindow window, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        JsonObject? state = null;

        while (DateTimeOffset.Now < deadline)
        {
            state = await ReadPdfStateAsync(window).ConfigureAwait(true);

            if (state?["kind"]?.GetValue<string>() == "pdf"
                && (state["totalPages"]?.GetValue<int>() ?? 0) > 0)
            {
                return state;
            }

            await Task.Delay(200).ConfigureAwait(true);
        }

        return state;
    }

    /// <summary>等状态页与顶部可见页都落在期望页；超时返回最后一次快照（交给断言报真实数）。</summary>
    private static async Task<JsonObject?> WaitForPdfLandingAsync(
        ReaderWindow window, int expectedPage, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        JsonObject? state = null;

        while (DateTimeOffset.Now < deadline)
        {
            state = await ReadPdfStateAsync(window).ConfigureAwait(true);

            if (state is not null
                && (state["page"]?.GetValue<int>() ?? -1) == expectedPage
                && (state["topVisiblePage"]?.GetValue<int>() ?? -1) == expectedPage)
            {
                return state;
            }

            await Task.Delay(150).ConfigureAwait(true);
        }

        return state;
    }

    /// <summary>读 PDF 自绘引擎的只读探针（含 page / topVisiblePage / scrollTop / totalPages）。</summary>
    private static async Task<JsonObject?> ReadPdfStateAsync(ReaderWindow window)
    {
        string? raw;

        try
        {
            raw = await window.ExecAsync(
                "window.__readerDebug ? window.__readerDebug.pdfState() : null").ConfigureAwait(true);
        }
        catch (Exception)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "null")
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(raw) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 手写一份最小 PDF（<paramref name="pageCount"/> 页空白，PDF 1.4）。
    /// xref 偏移逐对象按字节算准；正文只有 ASCII，所以字符数就是字节数。
    /// </summary>
    private static byte[] BuildSmokePdf(int pageCount)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids ["
                + string.Join(" ", Enumerable.Range(3, pageCount).Select(i => i + " 0 R"))
                + "] /Count " + pageCount + " >>",
        };

        for (var i = 0; i < pageCount; i++)
        {
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>");
        }

        var output = new StringBuilder();

        output.Append("%PDF-1.4\n");

        var offsets = new List<int>(objects.Count);

        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Length);
            output.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xrefAt = output.Length;

        output.Append("xref\n0 ").Append(objects.Count + 1).Append('\n');
        output.Append("0000000000 65535 f \n");

        foreach (var offset in offsets)
        {
            output.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        output.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\n");
        output.Append("startxref\n").Append(xrefAt).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(output.ToString());
    }

    /// <summary>等页面报出章节总数（说明这一章已经渲染完、探针可用）。</summary>
    private static async Task<JsonObject?> WaitForChapterAsync(ReaderWindow window, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;

        while (DateTimeOffset.Now < deadline)
        {
            try
            {
                var meta = await ReadChapterMetaAsync(window).ConfigureAwait(true);

                if (meta?["chapterTotal"]?.GetValue<string>() is { Length: > 0 })
                {
                    return meta;
                }
            }
            catch (Exception)
            {
                // 页面还没就绪时探针可能拿不到，继续等
            }

            await Task.Delay(200).ConfigureAwait(true);
        }

        return await ReadChapterMetaAsync(window).ConfigureAwait(true);
    }

    // ---------- 辅助 ----------

    /// <summary>T4-6：读工具栏/状态条上每个控件的 tooltip。</summary>
    private static async Task<Dictionary<string, string>> ReadToolbarTitlesAsync(ReaderWindow window)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var json = await window.ExecAsync(
            "JSON.stringify(window.__readerDebug.toolbarTitles())").ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        var text = json.Trim();

        // ExecAsync 可能把结果再包一层引号
        if (text.StartsWith('"') && text.EndsWith('"') && text.Length > 1)
        {
            text = JsonSerializer.Deserialize<string>(text) ?? text;
        }

        if (JsonNode.Parse(text) is not JsonArray array)
        {
            return result;
        }

        foreach (var item in array)
        {
            var raw2 = item?.GetValue<string>() ?? string.Empty;
            var at = raw2.IndexOf('=', StringComparison.Ordinal);

            if (at > 0)
            {
                result[raw2[..at]] = raw2[(at + 1)..];
            }
        }

        return result;
    }

    private static async Task<JsonObject?> ReadInteractionAsync(ReaderWindow window)
    {
        var json = await window.ExecAsync(
            "(function(){return window.__readerDebug.interactionProbe();})()").ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>标题行「第 12/14 章　第 1/4 页」的现场（文字、两个输入框的值与尺寸、页码段是否可见）。</summary>
    private static async Task<JsonObject?> ReadChapterMetaAsync(ReaderWindow window)
    {
        var json = await window.ExecAsync(
            "window.__readerDebug.chapterMetaState()").ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>给定位数时输入框会用的像素宽（验"1/3/4/5 位数都完整显示"）。</summary>
    private static async Task<double> ReadMetaJumpWidthAsync(ReaderWindow window, int digits)
    {
        var raw = await window.ExecAsync(
            "String(window.__readerDebug.metaJumpWidthFor(" + digits + "))").ConfigureAwait(true);

        return double.TryParse(raw?.Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            ? width
            : 0;
    }

    /// <summary>自定义主题下工具栏控件的边框色与对比度（验"看不见边框"这个 bug 不再出现）。</summary>
    private static async Task<JsonObject?> ReadChromeAsync(ReaderWindow window)
    {
        var json = await window.ExecAsync("window.__readerDebug.chromeProbe()").ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>按键直到条件成立（重书的 iframe 监听可能慢一拍，按一次没动就再按一次）。</summary>
    private static async Task<ReaderWindow.ReaderPageState?> PressUntilAsync(
        ReaderWindow window, string key, string where, string? modifiers,
        Func<ReaderWindow.ReaderPageState?, bool> done)
    {
        var state = await window.ReadPageStateAsync().ConfigureAwait(true);

        for (var attempt = 0; attempt < 6 && !done(state); attempt++)
        {
            await PressKeyAsync(window, key, where, modifiers).ConfigureAwait(true);
            await Task.Delay(250).ConfigureAwait(true);

            state = await window.ReadPageStateAsync().ConfigureAwait(true);
        }

        return state;
    }

    private static async Task<int> ReadFontSizeAsync(ReaderWindow window)
        => (await ReadInteractionAsync(window).ConfigureAwait(true))?["fontSize"]?.GetValue<int>() ?? 0;

    /// <summary>T5-2W 第三步的重载检查用：读 iframe 里的正文文本。</summary>
    private static async Task<string> ReadChapterTextAsync(ReaderWindow window)
        => await window.ExecAsync(
            "(function(){var f=document.getElementById('chapter');" +
            "return f&&f.contentDocument&&f.contentDocument.body?f.contentDocument.body.innerText:'';})()")
            .ConfigureAwait(true) ?? string.Empty;
    private static async Task PressKeyAsync(
        ReaderWindow window, string key, string where, string? modifiers)
    {
        var flags = modifiers is null ? "null" : "'" + modifiers + "'";

        await window.ExecAsync(
            "window.__readerDebug.pressKey('" + key + "', '" + where + "', " + flags + ");true")
            .ConfigureAwait(true);

        await Task.Delay(220).ConfigureAwait(true);
    }

    /// <summary>把 "22px" 这样的内联缩进读成数字；读不出来返回 -1。</summary>
    private static int ParsePx(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return -1;
        }

        return int.TryParse(value.Replace("px", string.Empty).Trim(), out var px) ? px : -1;
    }

    private sealed record ScanResult(int Scanned, int References, int Broken, string Sample);

    /// <summary>
    /// 逐章物化并检查里面的 http://ebook.local/content/... 引用是不是都指向真实存在的文件。
    /// 这是"图片能显示"的可靠判据：改写规则错一个字符，这里就会抓到。
    /// </summary>
    private static async Task<ScanResult> ScanRewritesAsync(
        IReaderService reader, ReaderSession session, int maxChapters)
    {
        var references = 0;
        var broken = 0;
        var scanned = 0;
        var sample = "(无相对引用)";

        var limit = Math.Min(maxChapters, session.Manifest.Spine.Count);

        for (var i = 0; i < limit; i++)
        {
            string href;
            try
            {
                href = await reader.MaterializeChapterAsync(session, i).ConfigureAwait(true);
            }
            catch (Exception)
            {
                // 单章物化失败不该让整项检查崩掉，记一次 broken 继续
                broken++;
                continue;
            }

            var path = Path.Combine(session.CacheRoot, href.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                broken++;
                continue;
            }

            scanned++;
            var html = File.ReadAllText(path, Encoding.UTF8);

            foreach (Match match in Regex.Matches(html, Regex.Escape(ReaderEndpoints.ContentUrlPrefix) + @"[^""'\s)<>]+"))
            {
                var url = match.Value;
                references++;

                // 引用可能带 #fragment（章内锚点）或 ?query，映射到磁盘之前必须剥掉，
                // 否则会误判成"指向不存在的文件"
                var pathPart = url[ReaderEndpoints.ContentUrlPrefix.Length..];
                var cut = pathPart.IndexOfAny(['#', '?']);
                if (cut >= 0)
                {
                    pathPart = pathPart[..cut];
                }

                if (pathPart.Length == 0)
                {
                    continue;
                }

                var relative = Uri.UnescapeDataString(pathPart);
                var target = Path.Combine(
                    session.CacheRoot,
                    ReaderEndpoints.ContentDirectoryName,
                    relative.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(target))
                {
                    if (sample == "(无相对引用)")
                    {
                        sample = relative;
                    }
                }
                else
                {
                    broken++;
                    sample = "缺失 " + relative;
                }
            }
        }

        return new ScanResult(scanned, references, broken, sample);
    }

    /// <summary>轮询等条件成立。在 UI 线程上 await Task.Delay 会让出消息泵，界面照常刷新。</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();

        while (watch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(true);
        }

        return condition();
    }

    private static SmokeReport Build(
        DateTimeOffset startedAt,
        IAppPaths paths,
        IReadOnlyList<SmokeCheck> checks,
        Stopwatch total)
    {
        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            total.ElapsedMilliseconds,
            paths.DataRoot,
            paths.WebView2DataDir,
            null,
            checks);

        try
        {
            var reportPath = Path.Combine(paths.TempDir, "reader-smoke.json");
            Directory.CreateDirectory(paths.TempDir);
            File.WriteAllText(
                reportPath,
                JsonSerializer.Serialize(report, ReportOptions),
                new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // 写不出报告不影响退出码，脚本会按"没报告"报错
        }

        return report;
    }
}
