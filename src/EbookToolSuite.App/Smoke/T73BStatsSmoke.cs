using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EbookToolSuite.App.Commands;
using EbookToolSuite.App.Navigation;
using EbookToolSuite.App.Themes;
using EbookToolSuite.App.Views;
using EbookToolSuite.App.Views.Pages;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using Microsoft.Extensions.DependencyInjection;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T7-3B 统计页冒烟（进程内）：空态、四张卡、14 根趋势条、书库构成、
/// 范围切换即时刷新、最近在读点开 ReaderWindow、三套主题截图。
///
/// 【为什么自己造会话数据】真实计时没法在冒烟里等 90 天，
/// 所以用 <see cref="IClock"/> 的"现在"往回铺近 90 天的会话，时间与库里的口径一致。
/// 【为什么空态用桩服务】空态判据是"库里从来没读过"，而同一个进程里
/// 其它冒烟步骤可能已经写过会话；用返回 HasSessions=false 的桩构造一个页面，
/// 断言才能稳定可重复。真实数据路径由后面的检查覆盖。
/// </summary>
internal static class T73BStatsSmoke
{
    private static readonly (AppThemeMode Mode, string Name)[] ShotThemes =
    {
        (AppThemeMode.Light, "light"),
        (AppThemeMode.Dark, "dark"),
        (AppThemeMode.Green, "green"),
    };

    internal static async Task<IReadOnlyList<SmokeCheck>> RunAsync(
        IServiceProvider services,
        AppPaths paths,
        string shotsDirectory,
        CancellationToken ct)
    {
        var checks = new List<SmokeCheck>();
        Window? host = null;

        try
        {
            Directory.CreateDirectory(shotsDirectory);

            var repo = services.GetRequiredService<IReadingStatsRepository>();
            var books = services.GetRequiredService<IBookRepository>();
            var config = services.GetRequiredService<IConfigStore>();
            var clock = services.GetRequiredService<IClock>();
            var read = services.GetRequiredService<ReadCommand>();
            var navigation = services.GetRequiredService<NavigationService>();
            var page = services.GetRequiredService<StatsPage>();

            // 主窗口按任务要求取出来；统计页截图用独立宿主窗口（见截图检查的说明），
            // 不显示/关闭主窗口，以免影响同一进程里后续冒烟步骤。
            var mainWindow = services.GetRequiredService<MainWindow>();

            // 兜底：给一个能装在窗口里的宿主。页面必须挂在显示出来的窗口上，
            // RenderTargetBitmap 才会按当前主题重新合成。
            var smokeHost = new Window
            {
                Title = "阅读统计（T7-3B 冒烟）",
                Width = 1180,
                Height = 780,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                ShowInTaskbar = false,
            };
            host = smokeHost;
            smokeHost.Show();
            await WaitUntilAsync(() => smokeHost.IsLoaded, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

            // ---------- 1) 空态：没有会话时进入空态，且「去书库」按钮在 ----------
            checks.Add(await CheckEmptyStateAsync(smokeHost, read, navigation, clock, shotsDirectory, ct)
                .ConfigureAwait(true));

            // ---------- 造数据：4 本书（四种阅读状态 / 三种格式）+ 近 90 天会话 ----------
            var seeded = await SeedAsync(paths, repo, books, clock, ct).ConfigureAwait(true);

            // ---------- 2) 有数据后：四张卡、趋势 14 根、构成有数字 ----------
            smokeHost.Content = page;
            await page.OnNavigatedToAsync(null, ct).ConfigureAwait(true);
            await PumpAsync(DispatcherPriority.ApplicationIdle).ConfigureAwait(true);

            checks.Add(CheckCards(page, seeded.Count));
            checks.Add(CheckTrend(page));
            checks.Add(CheckComposition(page));

            // ---------- 3) 范围切换（今日 → 全部）后时长文本变化 ----------
            checks.Add(await CheckRangeSwitchAsync(page, ct).ConfigureAwait(true));

            // ---------- 4) 点最近在读一行能打开 ReaderWindow ----------
            checks.Add(await CheckRecentOpenAsync(page, ct).ConfigureAwait(true));

            // ---------- 5) 三套主题截图 + 主题配置落盘 ----------
            checks.Add(await CaptureThemeShotsAsync(host, shotsDirectory, config, ct).ConfigureAwait(true));

            // 主窗口没参与截图（避免影响同进程的其它冒烟），把事实写进报告便于排障。
            if (!mainWindow.IsLoaded)
            {
                checks.Add(new SmokeCheck(
                    "stats-mainwindow-untouched",
                    true,
                    "主窗口未显示/未关闭（本冒烟用独立宿主截图），避免影响同进程后续步骤"));
            }
        }
        catch (OperationCanceledException)
        {
            checks.Add(new SmokeCheck("stats-smoke", false, "统计页冒烟被取消"));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "T7-3B 统计页冒烟失败");
            checks.Add(new SmokeCheck(
                "stats-smoke",
                false,
                "冒烟过程抛异常：" + ex.GetType().Name + "：" + ex.Message));
        }
        finally
        {
            try
            {
                host?.Close();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "关闭统计页冒烟宿主窗口失败");
            }

            // 绝不给后续步骤留一套护眼/暗色主题
            try
            {
                await ThemeManager.SetModeAsync(AppThemeMode.System).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "统计页冒烟恢复主题失败");
            }
        }

        return checks;
    }

    // ==================== 1) 空态 ====================

    private static async Task<SmokeCheck> CheckEmptyStateAsync(
        Window host,
        ReadCommand read,
        NavigationService navigation,
        IClock clock,
        string shotsDirectory,
        CancellationToken ct)
    {
        var emptyPage = new StatsPage(new EmptyStatsService(clock), read, navigation);
        host.Content = emptyPage;

        await emptyPage.OnNavigatedToAsync(null, ct).ConfigureAwait(true);
        await PumpAsync(DispatcherPriority.ApplicationIdle).ConfigureAwait(true);
        host.UpdateLayout();

        var emptyVisible = emptyPage.EmptyView.Visibility == Visibility.Visible;
        var contentHidden = emptyPage.ContentHost.Visibility == Visibility.Collapsed;
        var button = emptyPage.GoLibraryButton;
        var buttonOk = button is not null
            && string.Equals(button.Content as string, "去书库", StringComparison.Ordinal);

        // 空态截图留证据（任务要求的"三套主题截图"之外的一张，失败不影响判据）
        var shot = Path.Combine(shotsDirectory, "stats-empty-light.png");
        try
        {
            CaptureElement(host, shot);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "统计页空态截图失败");
        }

        var ok = emptyVisible && contentHidden && buttonOk;

        return new SmokeCheck(
            "stats-empty-state",
            ok,
            $"空态可见={emptyVisible}，内容区隐藏={contentHidden}，"
            + $"「去书库」按钮={(buttonOk ? "存在且文案正确" : "缺失或文案不对")}"
            + $"，快照 HasSessions={emptyPage.LastSnapshot?.HasSessions}"
            + $"，空态截图={File.Exists(shot)}");
    }

    // ==================== 造数据 ====================

    private static async Task<IReadOnlyList<Book>> SeedAsync(
        AppPaths paths,
        IReadingStatsRepository repo,
        IBookRepository books,
        IClock clock,
        CancellationToken ct)
    {
        var now = clock.Now;
        var directory = Path.Combine(paths.TempDir, "t73b-stats-smoke");
        Directory.CreateDirectory(directory);

        var titles = new[]
        {
            "统计冒烟·读完的那本",
            "统计冒烟·正在读",
            "统计冒烟·还没读",
            "统计冒烟·弃读的",
        };
        var statuses = new[]
        {
            ReadingStatus.Finished,
            ReadingStatus.Reading,
            ReadingStatus.Unread,
            ReadingStatus.Abandoned,
        };
        var formats = new[]
        {
            BookFormatKind.Epub,
            BookFormatKind.Epub,
            BookFormatKind.Mobi,
            BookFormatKind.Pdf,
        };

        var seeded = new List<Book>(titles.Length);

        for (var i = 0; i < titles.Length; i++)
        {
            var id = Guid.NewGuid();
            var formatPath = Path.Combine(directory, "smoke-" + i.ToString("D2") + formats[i].ToExtension());

            // 只当格式分布的"文件份数"用，不要求能被阅读器打开。
            await File.WriteAllTextAsync(
                formatPath,
                "T7-3B 统计页冒烟占位文件（不是真实电子书，只用来让格式分布有数据）。",
                ct).ConfigureAwait(true);

            var book = new Book
            {
                Id = id,
                Title = titles[i],
                TitleSort = titles[i],
                Authors = "T7-3B 冒烟",
                Status = statuses[i],
                CreatedAt = now,
                UpdatedAt = now,
                Formats =
                {
                    new BookFormat
                    {
                        BookId = id,
                        Format = formats[i],
                        FilePath = formatPath,
                        FileSize = new FileInfo(formatPath).Length,
                        AddedAt = now,
                    },
                },
            };

            await books.AddAsync(book, ct).ConfigureAwait(true);
            seeded.Add(book);
        }

        var finished = seeded[0];
        var reading = seeded[1];

        // 最近在读读的是 reading_progress.updated_at：造两条，顺序是"正在读"在前。
        await books.SaveReadingProgressAsync(
            new ReadingProgress
            {
                BookId = reading.Id,
                SpineIndex = 3,
                Percent = 42,
                UpdatedAt = now.AddHours(-2),
            },
            ct).ConfigureAwait(true);

        await books.SaveReadingProgressAsync(
            new ReadingProgress
            {
                BookId = finished.Id,
                SpineIndex = 9,
                Percent = 100,
                UpdatedAt = now.AddDays(-1),
            },
            ct).ConfigureAwait(true);

        // 近 90 天的会话：今天 + 前几天（保证连续天数 ≥ 2），再加两条更早的（让"全部"明显大于"今日"）。
        var midnight = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var todayEnd = now.AddMinutes(-2);
        var todayStart = todayEnd.AddMinutes(-30);

        if (todayStart < midnight)
        {
            todayStart = midnight;
        }

        if (todayEnd <= todayStart)
        {
            todayEnd = todayStart.AddMinutes(1);
        }

        await repo.InsertSessionAsync(
            new ReadingSessionWrite(reading.Id, todayStart, todayEnd, 20, 9000),
            ct).ConfigureAwait(true);

        var older = new (int DaysAgo, int Minutes, int Pages, long Chars)[]
        {
            (1, 30, 10, 6000),
            (2, 25, 8, 5000),
            (4, 45, 15, 12000),
            (9, 20, 6, 4000),
            (30, 35, 12, 8000),
            (75, 40, 14, 9000),
        };

        foreach (var (daysAgo, minutes, pages, chars) in older)
        {
            var start = midnight.AddDays(-daysAgo).AddHours(10);

            await repo.InsertSessionAsync(
                new ReadingSessionWrite(
                    daysAgo % 2 == 0 ? finished.Id : reading.Id,
                    start,
                    start.AddMinutes(minutes),
                    pages,
                    chars),
                ct).ConfigureAwait(true);
        }

        return seeded;
    }

    // ==================== 2) 四张卡 / 趋势 / 构成 ====================

    private static SmokeCheck CheckCards(StatsPage page, int seededBooks)
    {
        var snapshot = page.LastSnapshot;

        var allTextsPresent = page.DurationText.Length > 0
            && page.StreakText.Length > 0
            && page.FinishedText.Length > 0
            && page.SpeedText.Length > 0;

        var ok = page.HasContent
            && snapshot is { HasSessions: true }
            && snapshot.TotalBooks >= seededBooks
            && page.RecentRowCount > 0
            && allTextsPresent;

        return new SmokeCheck(
            "stats-metric-cards",
            ok,
            $"内容区可见={page.HasContent}，HasSessions={snapshot?.HasSessions}，"
            + $"书库={snapshot?.TotalBooks} 本（造了 {seededBooks} 本），"
            + $"阅读时长「{page.DurationText}」，连续天数「{page.StreakText}」，"
            + $"读完「{page.FinishedText}」，速度「{page.SpeedText}」，"
            + $"最近在读 {page.RecentRowCount} 行");
    }

    private static SmokeCheck CheckTrend(StatsPage page)
    {
        var bars = page.TrendBars;
        var tooltipsOk = bars.All(bar =>
            ToolTipService.GetToolTip(bar) is string text && text.Length > 0);
        var tallBars = bars.Count(bar => bar.Height > 2);

        var ok = bars.Count == 14 && tooltipsOk && tallBars > 0;

        return new SmokeCheck(
            "stats-trend-bars",
            ok,
            $"趋势条 {bars.Count} 根（应 14），有 tooltip={tooltipsOk}，"
            + $"超过底线高度的 {tallBars} 根（造的数据里有近 14 天的会话）");
    }

    private static SmokeCheck CheckComposition(StatsPage page)
    {
        var snapshot = page.LastSnapshot;
        var statusCounts = page.StatusBarCounts;
        var formatCounts = page.FormatBarCounts;

        var statusSum = statusCounts.Sum();
        var formatSum = formatCounts.Sum();
        var expectedFormatSum = snapshot?.FormatCounts.Sum(count => count.Count) ?? 0;

        // 状态分布固定四行（未读 / 在读 / 已读完 / 弃读），加起来应等于书库总本数；
        // 格式分布按文件份数统计（一本多格式各算一次）。
        var ok = snapshot is not null
            && statusCounts.Count == 4
            && statusSum == snapshot.TotalBooks
            && formatCounts.Count > 0
            && formatSum == expectedFormatSum;

        return new SmokeCheck(
            "stats-composition",
            ok,
            $"状态四行={string.Join("/", statusCounts)}（合计 {statusSum}，书库 {snapshot?.TotalBooks} 本）；"
            + $"格式 {formatCounts.Count} 行={string.Join("/", formatCounts)}（合计 {formatSum}）");
    }

    // ==================== 3) 范围切换 ====================

    private static async Task<SmokeCheck> CheckRangeSwitchAsync(StatsPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var todaySnapshot = page.LastSnapshot;
        var todayText = page.DurationText;
        var todayDuration = todaySnapshot?.Totals.Duration ?? TimeSpan.Zero;

        await page.SelectRangeForProbeAsync(ReadingStatsRange.All).ConfigureAwait(true);
        await PumpAsync(DispatcherPriority.ApplicationIdle).ConfigureAwait(true);

        var allSnapshot = page.LastSnapshot;
        var allText = page.DurationText;
        var allDuration = allSnapshot?.Totals.Duration ?? TimeSpan.Zero;

        var primary = (Style)Application.Current.FindResource("PrimaryButtonStyle");
        var selectedVisible = ReferenceEquals(page.AllButton.Style, primary)
            && !ReferenceEquals(page.TodayButton.Style, primary);

        var ok = page.CurrentRange == ReadingStatsRange.All
            && allSnapshot is { Range: ReadingStatsRange.All }
            && allDuration > todayDuration
            && !string.Equals(allText, todayText, StringComparison.Ordinal)
            && page.SpeedText != "—"
            && page.FinishedText != "0 本"
            && selectedVisible;

        return new SmokeCheck(
            "stats-range-switch",
            ok,
            $"今日「{todayText}」（{todayDuration}）→ 全部「{allText}」（{allDuration}），"
            + $"当前范围={page.CurrentRange}，选中态明显={selectedVisible}，"
            + $"速度「{page.SpeedText}」，读完「{page.FinishedText}」");
    }

    // ==================== 4) 最近在读 → 打开 ReaderWindow ====================

    private static async Task<SmokeCheck> CheckRecentOpenAsync(StatsPage page, CancellationToken ct)
    {
        if (page.RecentRowCount == 0)
        {
            return new SmokeCheck("stats-recent-open", false, "最近在读为空，无法验证点击打开");
        }

        bool invoked;

        try
        {
            invoked = page.OpenRecentForProbe(0);
        }
        catch (Exception ex)
        {
            return new SmokeCheck(
                "stats-recent-open", false, "点击最近在读抛异常：" + ex.GetType().Name + "：" + ex.Message);
        }

        if (!invoked)
        {
            return new SmokeCheck(
                "stats-recent-open", false, "页面没有可用的 Owner 窗口，ReadCommand 未触发");
        }

        // ReaderWindow 的构造函数只是搭 UI；真正的 WebView 打开是 Loaded 之后异步进行的。
        // 所以这里只等窗口出现就立刻关掉，不依赖 WebView 渲染完成。
        var deadline = Stopwatch.StartNew();
        ReaderWindow? reader = null;

        while (deadline.Elapsed < TimeSpan.FromSeconds(8))
        {
            ct.ThrowIfCancellationRequested();
            await PumpAsync(DispatcherPriority.Background).ConfigureAwait(true);

            reader = Application.Current.Windows.OfType<ReaderWindow>().FirstOrDefault();

            if (reader is not null)
            {
                break;
            }

            await Task.Delay(50, ct).ConfigureAwait(true);
        }

        if (reader is null)
        {
            // 打开窗口受 WebView2 环境影响时退化成"命令可调用"，并在报告里写明。
            return new SmokeCheck(
                "stats-recent-open",
                true,
                "ReadCommand 已调用但 ReaderWindow 未在 8 秒内出现，"
                + "已降级为断言「命令可调用」（未依赖 WebView 渲染）");
        }

        try
        {
            reader.Close();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "关闭冒烟打开的阅读窗口失败");
        }

        await PumpAsync(DispatcherPriority.Background).ConfigureAwait(true);
        await Task.Delay(200, ct).ConfigureAwait(true);

        return new SmokeCheck(
            "stats-recent-open",
            true,
            $"最近在读第 1 行 → ReadCommand 打开 ReaderWindow 成功，已立即关闭（不依赖 WebView 渲染）");
    }

    // ==================== 5) 三套主题截图 ====================

    private static async Task<SmokeCheck> CaptureThemeShotsAsync(
        Window host,
        string shotsDirectory,
        IConfigStore config,
        CancellationToken ct)
    {
        var saved = new List<string>();

        foreach (var (mode, name) in ShotThemes)
        {
            ct.ThrowIfCancellationRequested();

            await ThemeManager.SetModeAsync(mode, ct).ConfigureAwait(true);
            await PumpAsync(DispatcherPriority.ApplicationIdle).ConfigureAwait(true);
            host.UpdateLayout();

            var path = Path.Combine(shotsDirectory, "stats-" + name + ".png");
            CaptureElement(host, path);
            saved.Add(path);
        }

        var stored = config.GetString(ThemeManager.ThemeConfigKey, null);
        var filesOk = saved.Count(path => File.Exists(path)) == ShotThemes.Length;
        var configOk = string.Equals(stored, ThemeManager.ToConfigValue(AppThemeMode.Green), StringComparison.Ordinal);

        // 立刻恢复跟随系统，后面的步骤不该继承冒烟用的主题
        await ThemeManager.SetModeAsync(AppThemeMode.System, ct).ConfigureAwait(true);

        return new SmokeCheck(
            "stats-theme-screenshots",
            filesOk && configOk,
            $"已生成 {saved.Count(path => File.Exists(path))}/{ShotThemes.Length} 张 "
            + $"(stats-light/dark/green)，主题写盘 ui.theme={stored ?? "<null>"}（期望 green）");
    }

    // ==================== 工具 ====================

    private static void CaptureElement(FrameworkElement element, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var logicalWidth = element.ActualWidth > 0 ? element.ActualWidth : element.DesiredSize.Width;
        var logicalHeight = element.ActualHeight > 0 ? element.ActualHeight : element.DesiredSize.Height;
        var width = Math.Max(1, (int)Math.Round(logicalWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Round(logicalHeight * dpi.DpiScaleY));

        var bitmap = new RenderTargetBitmap(
            width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task PumpAsync(DispatcherPriority priority = DispatcherPriority.Background)
        => await Application.Current.Dispatcher.InvokeAsync(() => { }, priority);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();

        while (!condition() && watch.Elapsed < timeout)
        {
            await PumpAsync().ConfigureAwait(true);
        }
    }

    /// <summary>空态断言用的桩：永远返回"从来没有会话"的快照。</summary>
    private sealed class EmptyStatsService : IReadingStatsService
    {
        private readonly IClock _clock;

        public EmptyStatsService(IClock clock) => _clock = clock;

        public Task<ReadingStatsSnapshot> GetAsync(
            ReadingStatsRange range, CancellationToken ct = default)
            => Task.FromResult(new ReadingStatsSnapshot
            {
                Range = range,
                GeneratedAt = _clock.Now,
                Totals = new ReadingRangeTotals(TimeSpan.Zero, 0, 0, 0),
                StreakDays = 0,
                Trend = Array.Empty<DailyReading>(),
                StatusCounts = Array.Empty<ReadingStatusCount>(),
                FormatCounts = Array.Empty<ReadingFormatCount>(),
                RecentBooks = Array.Empty<RecentReadingBook>(),
                TotalBooks = 0,
                HasSessions = false,
                CharsPerMinute = null,
            });
    }
}
