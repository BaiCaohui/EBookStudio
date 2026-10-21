using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using EbookToolSuite.App.Commands;
using EbookToolSuite.App.Views;
using EbookToolSuite.App.Views.Pages;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using Microsoft.Extensions.DependencyInjection;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// 「关掉阅读窗口后书库网格要自己刷新进度角标」这条要求的回归判据（用户要求）。
/// 全程走真路径：导入 -> 书库页 -> ReadCommand 开窗 -> 翻章滚动 -> 关窗 -> 看卡片。
/// </summary>
/// <remarks>
/// 【为什么必须走 ReadCommand】订阅关窗事件的地方就在 ReadCommand 里，
/// 自己 new 一个 ReaderWindow 永远测不到这条链路。
/// 【为什么要找"可滚动的章节"】样本里大量章节只有一屏高，
/// 拿一屏高的章节验"滚动 -> 关窗 -> 进度对上"等于拿 0 比 0，看着通过其实什么都没验。
/// 【为什么关窗前不等 1.5 秒】防抖没到点，库里还是旧值；
/// 卡片若显示的是滚动后的位置，就证明 ReadCommand 真的先 FlushAsync 再通知。
/// </remarks>
internal static class ReaderRefreshSmokeTest
{
    /// <summary>往下探多少章去找一个可滚动的章节。</summary>
    private const int MaxScrollProbeChapters = 30;

    /// <summary>可滚动高度的门槛：小于这个值就是一屏到底，拿来验滚动没意义。</summary>
    private const double ScrollableThreshold = 200;

    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };

    public static async Task<SmokeReport> RunAsync(
        IServiceProvider services,
        MainWindow window,
        AppPaths paths,
        string sourceFile)
    {
        var startedAt = DateTimeOffset.Now;
        var total = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        void Add(string name, bool ok, string detail) => checks.Add(new SmokeCheck(name, ok, detail));

        var import = services.GetRequiredService<IBookImportService>();
        var repo = services.GetRequiredService<IBookRepository>();
        var readerService = services.GetRequiredService<IReaderService>();
        var readCommand = services.GetRequiredService<ReadCommand>();
        var libraryPage = services.GetRequiredService<LibraryPage>();
        var library = libraryPage.ViewModel;

        // 窗口不显示时 ItemsControl 不生成容器，网格状态就测不准
        window.Show();
        await Task.Delay(250).ConfigureAwait(true);

        // ---------- 1. 导入 ----------

        var imported = await import
            .ImportAsync(sourceFile, ImportOptions.Default, null, CancellationToken.None)
            .ConfigureAwait(true);

        var bookId = imported.BookId ?? Guid.Empty;

        Add(
            "import-ok",
            imported.Outcome == ImportOutcome.Imported && bookId != Guid.Empty,
            "结果=" + imported.Outcome + " BookId=" + bookId);

        if (bookId == Guid.Empty)
        {
            return Build(paths, startedAt, total, checks);
        }

        var book = await repo.GetByIdAsync(bookId, CancellationToken.None).ConfigureAwait(true);

        // ---------- 2. 网格里找到这张卡片，初始必须是"未读、没进度" ----------

        await library.LoadAsync(CancellationToken.None).ConfigureAwait(true);

        var card = library.Books.FirstOrDefault(c => c.Id == bookId);

        Add(
            "grid-card-present",
            card is not null,
            "网格书本数=" + library.Books.Count + " 找到卡片=" + (card is not null));

        if (card is null || book is null)
        {
            return Build(paths, startedAt, total, checks);
        }

        var booksBefore = library.Books.Count;

        Add(
            "initial-unread",
            card.ProgressPercent <= 0 && card.Book.Status == ReadingStatus.Unread,
            "进度=" + card.ProgressPercent.ToString("0.###") +
            " 状态=" + card.Book.Status +
            " 角标文案=" + card.ReadingStateText);

        // ---------- 3. 用真入口开阅读窗口（ReadCommand）----------

        readCommand.Run(window, book);

        ReaderWindow? reader = null;

        var opened = await WaitUntilAsync(
            () =>
            {
                reader = Application.Current.Windows.OfType<ReaderWindow>().FirstOrDefault();
                return reader is not null;
            },
            TimeSpan.FromSeconds(20)).ConfigureAwait(true);

        var rendered = opened
            && await WaitUntilAsync(
                () => reader!.LastChapterIndex >= 0,
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);

        Add(
            "reader-opened-and-rendered",
            opened && rendered,
            "窗口已开=" + opened +
            " 首页已渲染=" + rendered +
            " 章=" + (reader?.LastChapterIndex.ToString() ?? "无") +
            " 正文字数=" + (reader?.RenderedTextLength ?? -1));

        if (reader is null || !rendered)
        {
            reader?.Close();
            return Build(paths, startedAt, total, checks);
        }

        // ---------- 3.5 窗口独立：主窗口最小化不能把阅读窗口一起带走（用户要求）----------

        /* 【回归点】WPF 里设了 Owner 的窗口会跟着拥有者一起最小化，
           以前的任务栏最小化主界面会把正在读的窗口一起收走。
           这里直接把主窗口最小化，看阅读窗口是不是还留在正常状态。 */
        window.WindowState = WindowState.Minimized;

        var mainMinimized = await WaitUntilAsync(
                () => window.WindowState == WindowState.Minimized,
                TimeSpan.FromSeconds(3))
            .ConfigureAwait(true);

        // 留出一段联动时间：如果哪天又把 Owner 设回去，这里必须能抓到
        await Task.Delay(400).ConfigureAwait(true);

        Add(
            "main-minimize-keeps-reader",
            mainMinimized && reader.WindowState == WindowState.Normal,
            "主窗口已最小化=" + mainMinimized +
            " 阅读窗口状态=" + reader.WindowState +
            " 阅读窗口可见=" + reader.IsVisible);

        window.WindowState = WindowState.Normal;

        await WaitUntilAsync(
                () => window.WindowState == WindowState.Normal,
                TimeSpan.FromSeconds(3))
            .ConfigureAwait(true);

        // 反向：阅读窗口最小化时主窗口不能跟着最小化（这条现状必须保留）
        reader.WindowState = WindowState.Minimized;

        var readerMinimized = await WaitUntilAsync(
                () => reader.WindowState == WindowState.Minimized,
                TimeSpan.FromSeconds(3))
            .ConfigureAwait(true);

        await Task.Delay(400).ConfigureAwait(true);

        Add(
            "reader-minimize-keeps-main",
            readerMinimized && window.WindowState == WindowState.Normal,
            "阅读窗口状态=" + reader.WindowState +
            " 主窗口状态=" + window.WindowState);

        // 还原后读者还得能拿到页面几何：最小化/还原不能把 WebView2 弄坏（刷新不能坏）
        reader.WindowState = WindowState.Normal;

        await WaitUntilAsync(
                () => reader.WindowState == WindowState.Normal,
                TimeSpan.FromSeconds(3))
            .ConfigureAwait(true);

        await Task.Delay(300).ConfigureAwait(true);

        var restoredProbe = await reader.ReadFrameScrollAsync().ConfigureAwait(true);

        Add(
            "restore-keeps-reader-usable",
            reader.WindowState == WindowState.Normal
                && reader.IsVisible
                && restoredProbe.ScrollHeight > 0,
            "阅读窗口状态=" + reader.WindowState +
            " 可见=" + reader.IsVisible +
            " 内容高=" + restoredProbe.ScrollHeight.ToString("0"));

        // ---------- 4. 找一章可滚动的，滚到一半后【立刻】关窗 ----------
        // 故意不睡 1.5 秒防抖窗口：那一瞬间库里还是旧值，
        // 卡片若能显示滚动后的新位置，才证明 ReadCommand 真的先落盘再通知。

        var session = await readerService.OpenAsync(bookId, CancellationToken.None).ConfigureAwait(true);
        var chapterCount = Math.Max(1, session.Manifest.Spine.Count);

        var scrollChapter = -1;
        var probeLimit = Math.Min(chapterCount, MaxScrollProbeChapters);

        for (var i = 0; i < probeLimit; i++)
        {
            await reader.GoToChapterAsync(i).ConfigureAwait(true);
            await Task.Delay(120).ConfigureAwait(true);

            var probe = await reader.ReadFrameScrollAsync().ConfigureAwait(true);

            if (probe.MaxScroll > ScrollableThreshold)
            {
                scrollChapter = i;
                break;
            }
        }

        var scrolled = scrollChapter >= 0 && await reader.ScrollToFractionAsync(0.5).ConfigureAwait(true);

        // 页面侧的滚动上报是 300ms 节流（带尾部补发）：不等它到宿主，
        // 关窗时宿主手上的位置可能还是章首，判据会变成看运气的。
        // 等 600ms 只保证"宿主知道了新位置"，防抖窗口是 1.5 秒，库里此时仍是旧值。
        await Task.Delay(600).ConfigureAwait(true);
        var beforeClose = await reader.ReadFrameScrollAsync().ConfigureAwait(true);

        Add(
            "scrollable-chapter-found",
            scrolled && beforeClose.ScrollTop > 0,
            "章节数=" + chapterCount +
            " 可滚动章（0 基）=" + scrollChapter +
            " scrollTop=" + beforeClose.ScrollTop.ToString("0") +
            " maxScroll=" + beforeClose.MaxScroll.ToString("0"));

        reader.Close();

        // ---------- 5. 卡片必须自己跟上（不用切页、不用手动刷新）----------

        var refreshed = await WaitUntilAsync(
            () => card.ProgressPercent > 0,
            TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        var row = await repo.GetReadingProgressAsync(bookId, CancellationToken.None).ConfigureAwait(true);

        Add(
            "close-refreshed-card-progress",
            refreshed,
            "刷新后卡片进度=" + card.ProgressPercent.ToString("0.###") +
            " 库里进度=" + (row?.Percent.ToString("0.###") ?? "无") +
            " 十秒内轮询到=" + refreshed);

        Add(
            "card-matches-db",
            row is not null && Math.Abs(card.ProgressPercent - row.Percent) < 0.001,
            "卡片=" + card.ProgressPercent.ToString("0.####") +
            " 库=" + (row?.Percent.ToString("0.####") ?? "无"));

        Add(
            "status-becomes-reading",
            card.Book.Status == ReadingStatus.Reading && card.ReadingStateText != "未读",
            "状态=" + card.Book.Status +
            " 角标文案=" + card.ReadingStateText +
            " ToolTip=" + card.ReadingStateFullText);

        // 关窗前没等防抖窗口，库里这条必须是"滚动之后"的位置，而不是章首
        var chapterFloor = (Math.Max(0, scrollChapter) + 0.05) / chapterCount;

        Add(
            "flush-wrote-scrolled-position",
            row is not null && row.Percent > chapterFloor,
            "库里进度=" + (row?.Percent.ToString("0.####") ?? "无") +
            " 章首基准=" + chapterFloor.ToString("0.####") +
            " 库里的章号=" + (row?.SpineIndex?.ToString() ?? "无") +
            " 滚动的章号=" + scrollChapter);

        // 刷新必须是"只重读这一本"，不能整页重载（重载会清掉选中、滚动位置与筛选）
        var cardAfter = library.Books.FirstOrDefault(c => c.Id == bookId);

        Add(
            "refresh-is-not-full-reload",
            ReferenceEquals(cardAfter, card) && library.Books.Count == booksBefore,
            "还是同一个卡片实例=" + ReferenceEquals(cardAfter, card) +
            " 书本数 " + booksBefore + " -> " + library.Books.Count);

        return Build(paths, startedAt, total, checks);
    }

    /// <summary>轮询直到条件成立。超时返回 false（判据自己把实际值写进明细）。</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;

        while (DateTimeOffset.Now < deadline)
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
        AppPaths paths,
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
            Directory.CreateDirectory(paths.TempDir);

            File.WriteAllText(
                Path.Combine(paths.TempDir, "reader-refresh-smoke.json"),
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
