using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using EbookToolSuite.App.Views;
using EbookToolSuite.App.Views.Pages;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Reader.WebView;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T1-7 VS1 端到端验收。执行计划的 11 步脚本拆成两个进程阶段，由
/// <c>build/smoke-vs1.ps1</c> 串起来跑：
/// <list type="bullet">
///   <item>阶段 A（步骤 1-5）：全新 Data -> 冷启动计时 -> 导入 EPUB -> 网格出现封面 ->
///     开阅读器读到第 2 章中间 -> 等进度真的落盘 -> 关窗退出。</item>
///   <item>阶段 B（步骤 6-11）：暖启动计时 -> 开同一本书校验回到原位 ->
///     转 MOBI 并校验产物 -> 再转一次中途取消（量响应、查残留进程）-> 退出。</item>
/// </list>
/// </summary>
/// <remarks>
/// 【为什么拆成两个进程】步骤 5 与 6 之间要求"关闭程序、重新启动"，
/// 同一个进程里重启自己既做不到也不可信  冷/暖启动耗时必须跨越真实进程边界才有意义。
/// 【为什么用 IServiceProvider】这一套要用到七八个服务，逐个当参数传只会让调用行超长；
/// 各冒烟脚本本来就是"跑在应用自己的容器里"，直接取更贴近真实调用路径。
/// </remarks>
internal static class Vs1SmokeTest
{
    /// <summary>步骤 4 说的"第 2 章"（0 基索引）。</summary>
    private const int TargetChapterIndex = 1;

    /// <summary>冷启动门槛（空库，执行计划 4.3）。</summary>
    private const int ColdStartLimitMs = 3500;

    /// <summary>暖启动门槛。</summary>
    private const int WarmStartLimitMs = 2000;

    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };

    // ---------- 阶段 A（步骤 1-5） ----------

    public static async Task<SmokeReport> RunPhaseAAsync(
        IServiceProvider services, AppPaths paths, string sourceFile)
    {
        var startedAt = DateTimeOffset.Now;
        var total = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        void Add(string name, bool ok, string detail) => checks.Add(new SmokeCheck(name, ok, detail));

        var window = services.GetRequiredService<MainWindow>();
        var import = services.GetRequiredService<IBookImportService>();
        var repo = services.GetRequiredService<IBookRepository>();

        // ---------- 1. 冷启动（空库）----------

        window.Show();

        var loaded = await WaitUntilAsync(() => App.StartupMs > 0, TimeSpan.FromSeconds(30))
            .ConfigureAwait(true);

        Add(
            "A1-cold-start",
            loaded && App.StartupMs <= ColdStartLimitMs,
            "冷启动（全新数据目录、空库）= " + App.StartupMs + " ms（判据 <= " + ColdStartLimitMs + " ms）");

        // ---------- 2. 导入一本 EPUB ----------

        var importWatch = Stopwatch.StartNew();
        ImportResult? imported = null;
        Exception? importError = null;

        try
        {
            imported = await import.ImportAsync(sourceFile, ImportOptions.Default).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            importError = ex;
        }

        importWatch.Stop();

        var bookId = imported?.BookId ?? Guid.Empty;

        Add(
            "A2-import",
            imported?.Outcome == ImportOutcome.Imported && bookId != Guid.Empty,
            "结果=" + (imported?.Outcome.ToString() ?? importError?.GetType().Name ?? "没结果") +
            " 用时=" + importWatch.ElapsedMilliseconds + "ms" +
            " 书=" + (bookId == Guid.Empty ? "无" : bookId.ToString("D")[..8]));

        if (bookId == Guid.Empty)
        {
            return Build(paths, "vs1-a.json", startedAt, total, checks);
        }

        var book = await repo.GetByIdAsync(bookId).ConfigureAwait(true);

        Add(
            "A3-metadata",
            book is not null
                && !string.IsNullOrWhiteSpace(book.Title)
                && !string.IsNullOrWhiteSpace(book.Authors),
            "书名=" + (book?.Title ?? "无") +
            " 作者=" + (book?.Authors ?? "无") +
            " 格式=" + (book?.Formats.Count ?? 0) + " 个");

        var thumbnail = Path.Combine(paths.CoversCacheDir, bookId.ToString("D") + "_t.jpg");

        Add(
            "A4-cover-file",
            File.Exists(thumbnail) && new FileInfo(thumbnail).Length > 0,
            "缩略图=" + Path.GetFileName(thumbnail) +
            " 存在=" + File.Exists(thumbnail) +
            " 字节=" + (File.Exists(thumbnail) ? new FileInfo(thumbnail).Length : 0));
        // ---------- 3. 网格：封面真的解码出来了（不只是文件在）----------

        var libraryPage = services.GetRequiredService<LibraryPage>();
        var library = libraryPage.ViewModel;

        await library.LoadAsync().ConfigureAwait(true);

        var card = library.Books.FirstOrDefault(c => c.Id == bookId);

        if (card is not null)
        {
            await card.LoadCoverAsync().ConfigureAwait(true);
        }

        Add(
            "A5-grid-card",
            card is not null
                && card.HasCover
                && card.Title == book!.Title
                && card.FormatBadge.Contains("EPUB", StringComparison.OrdinalIgnoreCase),
            "网格里找到卡片=" + (card is not null) +
            " 封面已解码=" + (card?.HasCover ?? false) +
            " 书名=" + (card?.Title ?? "无") +
            " 格式角标=" + (card?.FormatBadge ?? "无"));

        // ---------- 4. 阅读器：读到第 2 章中间 ----------

        var readerService = services.GetRequiredService<IReaderService>();
        var environments = services.GetRequiredService<IWebViewEnvironmentProvider>();
        var progressService = services.GetRequiredService<IReadingProgressService>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();

        var session = await readerService.OpenAsync(bookId, CancellationToken.None).ConfigureAwait(true);
        var chapterCount = session.Manifest.Spine.Count;
        var targetChapter = Math.Min(TargetChapterIndex, Math.Max(0, chapterCount - 1));

        var readerWindow = new ReaderWindow(
            readerService,
            environments,
            progressService,
            loggerFactory.CreateLogger<ReaderWindow>(),
            bookId);

        readerWindow.Show();

        var readerReady = await WaitUntilAsync(
            () => readerWindow.LastChapterIndex >= 0,
            TimeSpan.FromSeconds(30)).ConfigureAwait(true);

        if (readerReady)
        {
            await readerWindow.GoToChapterAsync(targetChapter).ConfigureAwait(true);
        }

        var reached = readerReady
            && await WaitUntilAsync(
                () => readerWindow.LastChapterIndex == targetChapter,
                TimeSpan.FromSeconds(15)).ConfigureAwait(true);

        var scrolled = reached && await readerWindow.ScrollToFractionAsync(0.5).ConfigureAwait(true);

        Add(
            "A6-reader-chapter2-mid",
            readerReady && reached && scrolled,
            "章节数=" + chapterCount +
            " 目标章（0 基）=" + targetChapter +
            " 实际章=" + readerWindow.LastChapterIndex +
            " 已滚到一半=" + scrolled);

        // 防抖是 1.5 秒：必须真的等过窗口，否则校验到的是"还没写库"这个假象
        await Task.Delay(2600).ConfigureAwait(true);

        var saved = await repo.GetReadingProgressAsync(bookId).ConfigureAwait(true);
        var savedLocator = ReaderLocator.TryParse(saved?.Location);

        Add(
            "A7-progress-saved",
            saved is not null
                && saved.SpineIndex == targetChapter
                && savedLocator?.ScrollTop is > 0
                && savedLocator.ScrollHeight is > 0,
            "章号=" + (saved?.SpineIndex?.ToString() ?? "无") +
            " scrollTop=" + (savedLocator?.ScrollTop?.ToString("0") ?? "无") +
            " scrollHeight=" + (savedLocator?.ScrollHeight?.ToString("0") ?? "无") +
            " 全书比例=" + (saved?.Percent.ToString("0.###") ?? "无"));

        // ---------- 5. 关窗（对应脚本第 5 步"关闭程序"）----------

        readerWindow.Close();
        window.Close();

        return Build(paths, "vs1-a.json", startedAt, total, checks);
    }
    // ---------- 阶段 B（步骤 6-11） ----------

    public static async Task<SmokeReport> RunPhaseBAsync(
        IServiceProvider services, AppPaths paths, string sourceFile)
    {
        var startedAt = DateTimeOffset.Now;
        var total = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        void Add(string name, bool ok, string detail) => checks.Add(new SmokeCheck(name, ok, detail));

        var window = services.GetRequiredService<MainWindow>();
        var repo = services.GetRequiredService<IBookRepository>();

        // ---------- 6. 暖启动 ----------

        window.Show();

        var loaded = await WaitUntilAsync(() => App.StartupMs > 0, TimeSpan.FromSeconds(30))
            .ConfigureAwait(true);

        Add(
            "B1-warm-start",
            loaded && App.StartupMs <= WarmStartLimitMs,
            "暖启动 = " + App.StartupMs + " ms（判据 <= " + WarmStartLimitMs + " ms）");

        // ---------- 7. 打开同一本书，校验回到原位 ----------

        var book = (await repo.ListAsync(0, 10).ConfigureAwait(true)).FirstOrDefault();

        Add("B2-book-present", book is not null, "书库第一本=" + (book?.Title ?? "（空）"));

        if (book is null)
        {
            return Build(paths, "vs1-b.json", startedAt, total, checks);
        }

        var saved = await repo.GetReadingProgressAsync(book.Id).ConfigureAwait(true);
        var expected = ReaderLocator.TryParse(saved?.Location);

        var readerService = services.GetRequiredService<IReaderService>();
        var environments = services.GetRequiredService<IWebViewEnvironmentProvider>();
        var progressService = services.GetRequiredService<IReadingProgressService>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();

        var readerWindow = new ReaderWindow(
            readerService,
            environments,
            progressService,
            loggerFactory.CreateLogger<ReaderWindow>(),
            book.Id);

        readerWindow.Show();

        var readerReady = await WaitUntilAsync(
            () => readerWindow.LastChapterIndex >= 0,
            TimeSpan.FromSeconds(30)).ConfigureAwait(true);

        // 恢复动作要先等图片稳定（最多 900ms），这里给足时间再量
        await Task.Delay(1500).ConfigureAwait(true);

        var actual = await readerWindow.ReadFrameScrollAsync().ConfigureAwait(true);
        var chapterOk = readerReady && saved is not null && readerWindow.LastChapterIndex == saved.SpineIndex;

        var tolerance = 0.05 * Math.Max(1, actual.ViewportHeight);
        var expectedTop = expected?.ScrollTop;
        var delta = expectedTop is null ? double.NaN : Math.Abs(actual.ScrollTop - expectedTop.Value);

        Add(
            "B3-restore-position",
            chapterOk && !double.IsNaN(delta) && delta <= tolerance,
            "上次 章=" + (saved?.SpineIndex?.ToString() ?? "无") +
            " scrollTop=" + (expectedTop?.ToString("0") ?? "无") +
            "；本次 章=" + readerWindow.LastChapterIndex +
            " scrollTop=" + actual.ScrollTop.ToString("0") +
            "；误差=" + (double.IsNaN(delta) ? "未知" : delta.ToString("0")) + "px（判据 <= " + tolerance.ToString("0") + "px）");

        readerWindow.Close();
        // ---------- 8. 转 MOBI ----------

        var conversion = services.GetRequiredService<IConversionService>();
        var progressEvents = 0;

        var convertWatch = Stopwatch.StartNew();
        ConversionResult? converted = null;
        Exception? convertError = null;

        try
        {
            converted = await conversion.ConvertAsync(
                new ConversionRequest { BookId = book.Id, TargetFormat = BookFormatKind.Mobi },
                new SmokeProbe.SynchronousProgress<ConversionProgress>(
                    _ => Interlocked.Increment(ref progressEvents)),
                CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            convertError = ex;
        }

        convertWatch.Stop();

        var afterConvert = await repo.GetByIdAsync(book.Id).ConfigureAwait(true);
        var mobiRow = afterConvert?.Formats.FirstOrDefault(f => f.Format == BookFormatKind.Mobi);
        var mobiPath = converted?.OutputPath ?? mobiRow?.FilePath;
        var mobiBytes = mobiPath is not null && File.Exists(mobiPath) ? new FileInfo(mobiPath).Length : 0;
        var mobiHeader = mobiPath is not null && HasMobiHeader(mobiPath);

        Add(
            "B4-convert-mobi",
            converted is not null
                && mobiRow is not null
                && mobiBytes > 0
                && mobiHeader
                && converted.Format == BookFormatKind.Mobi,
            "用时=" + convertWatch.ElapsedMilliseconds + "ms" +
            " 产物=" + (mobiPath is null ? "无" : Path.GetFileName(mobiPath)) +
            " 体积=" + mobiBytes + " 字节" +
            " 文件头合法=" + mobiHeader +
            " 库里登记=" + (mobiRow is not null) +
            (convertError is null ? "" : " 错误=" + convertError.GetType().Name));

        Add(
            "B5-progress-reported",
            progressEvents > 0,
            "转换进度回调次数=" + progressEvents + "（进度条要有反馈，不能全程不动）");

        // 详情页要能看到 MOBI 角标（脚本第 8 步："详情页出现 MOBI 格式标记"）
        var libraryPage = services.GetRequiredService<LibraryPage>();
        var library = libraryPage.ViewModel;

        await library.LoadAsync().ConfigureAwait(true);

        var card = library.Books.FirstOrDefault(c => c.Id == book.Id);

        Add(
            "B6-format-badge",
            card is not null && card.FormatBadge.Contains("MOBI", StringComparison.OrdinalIgnoreCase),
            "卡片格式角标=" + (card?.FormatBadge ?? "找不到卡片"));

        // ---------- 10. 再转一次，中途取消 ----------

        var cancelProgressEvents = 0;
        using var cancelSource = new CancellationTokenSource();
        Exception? cancelError = null;

        var cancelTask = Task.Run(async () =>
        {
            try
            {
                await conversion.ConvertAsync(
                    new ConversionRequest { BookId = book.Id, TargetFormat = BookFormatKind.Pdf },
                    new SmokeProbe.SynchronousProgress<ConversionProgress>(
                        _ => Interlocked.Increment(ref cancelProgressEvents)),
                    cancelSource.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cancelError = ex;
            }
        });

        /* 要等到 Calibre 进程真的在跑再取消。只等"有进度回调"是不够的：
           第一条进度可能发生在外层进程起来之前，那样取消会在瞬间返回，
           而"10 秒内无残留进程"就成了空验（第一版就是这样，取消耗时 0.0 秒）。 */
        var producing = await WaitUntilAsync(
            () => Volatile.Read(ref cancelProgressEvents) > 0
                && SmokeProbe.CountToolProcesses(paths) > 0,
            TimeSpan.FromSeconds(30)).ConfigureAwait(true);

        var runningAtCancel = SmokeProbe.CountToolProcesses(paths);
        var cancelWatch = Stopwatch.StartNew();
        cancelSource.Cancel();

        var returned = await Task.WhenAny(cancelTask, Task.Delay(TimeSpan.FromSeconds(20)))
            .ConfigureAwait(true);

        cancelWatch.Stop();

        await cancelTask.ConfigureAwait(true);

        Add(
            "B7-cancel-response",
            producing
                && returned == cancelTask
                && cancelError is OperationCanceledException
                && cancelWatch.Elapsed <= TimeSpan.FromSeconds(3),
            "已开始产出=" + producing + " 取消瞬间在跑的 Calibre 进程=" + runningAtCancel +
            " 取消到返回=" + cancelWatch.Elapsed.TotalSeconds.ToString("F1") + " 秒（判据 <= 3）" +
            " 结果=" + (cancelError?.GetType().Name ?? "没抛异常"));

        var noOrphan = await WaitUntilAsync(
            () => SmokeProbe.CountToolProcesses(paths) == 0,
            TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        Add(
            "B8-cancel-no-orphan",
            noOrphan,
            "取消后 10 秒内残留 Calibre 进程=" + SmokeProbe.CountToolProcesses(paths) + "（判据 0）");

        // 取消不能留下半个产物，也不能留下 PDF 登记。
        // 书所在目录从已登记的 EPUB 路径推出来，不猜目录命名规则。
        var afterCancel = await repo.GetByIdAsync(book.Id).ConfigureAwait(true);
        var pdfRow = afterCancel?.Formats.FirstOrDefault(f => f.Format == BookFormatKind.Pdf);
        var epubRow = afterCancel?.Formats.FirstOrDefault(f => f.Format == BookFormatKind.Epub);
        var bookDirectory = epubRow is null ? null : Path.GetDirectoryName(epubRow.FilePath);

        var strayPdf = bookDirectory is not null && Directory.Exists(bookDirectory)
            ? Directory.GetFiles(bookDirectory, "*.pdf").Length
            : 0;

        Add(
            "B9-cancel-no-partial",
            pdfRow is null && strayPdf == 0,
            "库里有 PDF 登记=" + (pdfRow is not null) + " 目录下残留 .pdf 文件=" + strayPdf);

        window.Close();

        return Build(paths, "vs1-b.json", startedAt, total, checks);
    }
    // ---------- 辅助 ----------

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

    /// <summary>
    /// 校验产物是不是真的 MOBI：PDB 文件头偏移 60 处必须是 "BOOKMOBI"。
    /// 只查"文件存在且大于 0"抓不到"写出一个空壳"；而"用系统阅读器打开"没法自动验，
    /// 这个文件头是能自动化的最近似判据（人眼那一步仍列在验收记录里）。
    /// </summary>
    private static bool HasMobiHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);

            if (stream.Length < 68)
            {
                return false;
            }

            stream.Seek(60, SeekOrigin.Begin);

            var buffer = new byte[8];
            var read = stream.Read(buffer, 0, 8);

            return read == 8
                && Encoding.ASCII.GetString(buffer).StartsWith("BOOKMOBI", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static SmokeReport Build(
        AppPaths paths,
        string fileName,
        DateTimeOffset startedAt,
        Stopwatch total,
        IReadOnlyList<SmokeCheck> checks)
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
            var reportPath = Path.Combine(paths.TempDir, fileName);
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
