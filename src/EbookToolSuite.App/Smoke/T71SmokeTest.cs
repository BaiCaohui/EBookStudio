using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EbookToolSuite.App.ViewModels;
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
/// T7-1 性能达标验收（执行计划 10.1 的测量清单）。
/// </summary>
/// <remarks>
/// 【量什么】1000 本书库首屏、1000 本搜索、60 秒滚动最大阻塞、大 EPUB 首屏、
/// 转换取消响应与残留、20 次取消后的句柄增长、连续读 10 本后的工作集。
/// 冷/暖启动跨进程量，由 <c>build/smoke-t71.ps1</c> 用 <c>--smoke-t71-boot=</c> 跑 21 次，
/// 不在这个进程里伪造。
/// 【大 EPUB 的口径】仓库里没有正好 5 MB 的书；脚本挑最接近且不小于 5 MB 的真实书，
/// 报告里写实际体积。比 5 MB 大属于偏严格，不是放宽。
/// </remarks>
internal static class T71SmokeTest
{
    private const int BookCount = 1000;
    private const int LibraryLimitMs = 2000;
    private const int ReaderLimitMs = 1500;
    private const int SearchLimitMs = 1000;
    private const int ScrollSeconds = 60;
    private const int ScrollBlockLimitMs = 200;
    private const int CancelLimitMs = 3000;
    private const int ResidualLimitSeconds = 10;
    private const int HandleGrowthLimit = 50;
    private const int MemoryLimitMb = 1024;

    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<SmokeReport> RunAsync(
        IServiceProvider services, AppPaths paths, string fixturesDirectory)
    {
        var startedAt = DateTimeOffset.Now;
        var total = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        void Add(string name, bool ok, string detail) => checks.Add(new SmokeCheck(name, ok, detail));

        var window = services.GetRequiredService<MainWindow>();
        var page = services.GetRequiredService<LibraryPage>();
        var vm = page.ViewModel;
        var repo = services.GetRequiredService<IBookRepository>();
        var importer = services.GetRequiredService<IBookImportService>();
        var reader = services.GetRequiredService<IReaderService>();
        var environments = services.GetRequiredService<IWebViewEnvironmentProvider>();
        var progressService = services.GetRequiredService<IReadingProgressService>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var conversion = services.GetRequiredService<IConversionService>();

        window.Show();

        var ready = await WaitUntilAsync(() => App.StartupMs > 0, TimeSpan.FromSeconds(30))
            .ConfigureAwait(true);

        Add(
            "perf-process-startup",
            ready,
            "本进程启动（暖启动口径，跨进程分位由脚本另算）= " + App.StartupMs + " ms");

        if (!ready)
        {
            return Build(paths, startedAt, total, checks);
        }

        // 主窗口 Loaded 里的 InitializeAsync 可能还在跑；等它把空库页装完再动手。
        await vm.LoadAsync().ConfigureAwait(true);
        window.UpdateLayout();

        // ---------- 1. 1000 本书库首屏 ----------

        var seedWatch = Stopwatch.StartNew();
        await SeedAsync(repo).ConfigureAwait(true);
        seedWatch.Stop();

        var libraryWatch = Stopwatch.StartNew();
        await vm.LoadAsync().ConfigureAwait(true);
        window.UpdateLayout();
        libraryWatch.Stop();

        Add(
            "perf-library-1000-first-screen",
            vm.Books.Count == BookCount && libraryWatch.ElapsedMilliseconds <= LibraryLimitMs,
            "加载 + 布局 " + libraryWatch.ElapsedMilliseconds + " ms（判据 <= " + LibraryLimitMs
                + "），本页 " + vm.Books.Count + " 本；造样本另花 " + seedWatch.ElapsedMilliseconds + " ms");

        // ---------- 2. 1000 本搜索 ----------

        vm.ClearAllFilters();
        await vm.FlushSearchAsync().ConfigureAwait(true);

        var searchWatch = Stopwatch.StartNew();
        vm.SearchText = "冒烟书 0500";
        var searchFound = await WaitUntilAsync(() => vm.Books.Count == 1, TimeSpan.FromSeconds(5))
            .ConfigureAwait(true);
        searchWatch.Stop();

        Add(
            "perf-search-1000",
            searchFound && searchWatch.ElapsedMilliseconds <= SearchLimitMs,
            "停止输入到出结果 " + searchWatch.ElapsedMilliseconds + " ms（判据 <= " + SearchLimitMs
                + "，含 200 ms 防抖），命中 " + vm.Books.Count + " 本");

        vm.ClearAllFilters();
        await vm.FlushSearchAsync().ConfigureAwait(true);

        // ---------- 3. 滚动 60 秒，UI 线程单次阻塞 ----------

        var list = (ListBox?)page.FindName("BookList");
        var scrollViewer = list is null ? null : FindScrollViewer(list);

        var beats = 0;
        var maxStall = 0L;
        var lastBeat = Stopwatch.StartNew();

        var heartbeat = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(25),
        };

        heartbeat.Tick += (_, _) =>
        {
            beats++;
            var gap = lastBeat.ElapsedMilliseconds;
            lastBeat.Restart();

            if (gap > maxStall)
            {
                maxStall = gap;
            }
        };

        heartbeat.Start();

        var scrollWatch = Stopwatch.StartNew();

        while (scrollViewer is not null && scrollWatch.Elapsed < TimeSpan.FromSeconds(ScrollSeconds))
        {
            var ratio = (scrollWatch.ElapsedMilliseconds % 4000) / 4000.0;
            scrollViewer.ScrollToVerticalOffset(scrollViewer.ScrollableHeight * ratio);
            await Task.Delay(50).ConfigureAwait(true);
        }

        heartbeat.Stop();

        Add(
            "perf-scroll-60s",
            scrollViewer is not null
                && scrollWatch.Elapsed >= TimeSpan.FromSeconds(ScrollSeconds - 1)
                && beats >= 100
                && maxStall <= ScrollBlockLimitMs,
            "滚动 " + scrollWatch.Elapsed.TotalSeconds.ToString("0.0") + " 秒：UI 心跳 " + beats
                + " 次，最大间隔 " + maxStall + " ms（判据 <= " + ScrollBlockLimitMs + "）");

        // ---------- 4. 大 EPUB 首屏 ----------

        var largePath = Directory
            .GetFiles(fixturesDirectory, "epub-large.epub", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        var largeBytes = largePath is null ? 0 : new FileInfo(largePath).Length;
        var largeBookId = Guid.Empty;
        var largeChapters = 0;
        var firstWindowMs = 0L;
        var warmFirstScreenMs = -1L;
        var warmScreenOk = false;

        if (largePath is not null)
        {
            var imported = await importer
                .ImportAsync(largePath, ImportOptions.Default, null, CancellationToken.None)
                .ConfigureAwait(true);

            largeBookId = imported.BookId ?? Guid.Empty;

            if (largeBookId != Guid.Empty)
            {
                /* 真实首次点击：不预先 OpenAsync、不预先建缓存，让阅读窗口自己
                   解包 + 建缓存 + 起 WebView2 + 渲染首章。这才是"5 MB EPUB 首屏"，
                   也符合"启动期间不初始化 WebView2"的设计。 */
                var firstWindowWatch = Stopwatch.StartNew();
                var firstWindow = NewReaderWindow(
                    reader, environments, progressService, loggerFactory, largeBookId);

                firstWindow.Show();

                var firstWindowReady = await WaitUntilAsync(
                    () => IsRendered(firstWindow),
                    TimeSpan.FromSeconds(30)).ConfigureAwait(true);

                firstWindowWatch.Stop();
                firstWindowMs = firstWindowWatch.ElapsedMilliseconds;
                firstWindow.Close();

                // 缓存已建好，OpenAsync 只剩读清单；章数从这里取。
                largeChapters = (await reader.OpenAsync(largeBookId, CancellationToken.None)
                    .ConfigureAwait(true)).Manifest.Spine.Count;

                await Task.Delay(500).ConfigureAwait(true);

                // 缓存已就绪后再开一次：这个数用来暴露 WebView2 生命周期问题，
                // 不作为 5 MB 首屏门槛（门槛看上面的真实首次点击）。
                var warmWatch = Stopwatch.StartNew();
                var warmWindow = NewReaderWindow(
                    reader, environments, progressService, loggerFactory, largeBookId);

                warmWindow.Show();

                warmScreenOk = await WaitUntilAsync(
                    () => IsRendered(warmWindow),
                    TimeSpan.FromSeconds(20)).ConfigureAwait(true);

                warmWatch.Stop();
                warmFirstScreenMs = warmWatch.ElapsedMilliseconds;

                Add(
                    "perf-epub-large-first-screen",
                    firstWindowReady && firstWindowMs <= ReaderLimitMs,
                    "样本 " + Path.GetFileName(largePath) + " = "
                        + (largeBytes / 1024d / 1024d).ToString("0.00") + " MB（>= 5 MB，偏严格）；"
                        + "首屏（真实首次点击，含建缓存 + WebView2 冷启动）=" + firstWindowMs
                        + " ms（判据 <= " + ReaderLimitMs + "）；关窗后重开（缓存已就绪）="
                        + warmFirstScreenMs + " ms（就绪=" + warmScreenOk + "）；章数="
                        + largeChapters);

                warmWindow.Close();
                await Task.Delay(300).ConfigureAwait(true);
            }
        }

        if (largeBookId == Guid.Empty)
        {
            Add(
                "perf-epub-large-first-screen",
                false,
                "样本目录里没有 epub-large.epub，或它导入失败（" + fixturesDirectory + "）");
        }

        // ---------- 5. 导入 10 本小书（取消与内存都要真实电子书） ----------

        var smallBookIds = new List<Guid>();
        var candidateFiles = Directory
            .GetFiles(fixturesDirectory, "book-*.epub", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in candidateFiles)
        {
            if (smallBookIds.Count >= 10)
            {
                break;
            }

            try
            {
                var imported = await importer
                    .ImportAsync(file, ImportOptions.Default, null, CancellationToken.None)
                    .ConfigureAwait(true);

                if (imported.BookId is { } id && id != Guid.Empty)
                {
                    smallBookIds.Add(id);
                }
            }
            catch (Exception ex)
            {
                checks.Add(new SmokeCheck(
                    "perf-corpus-import-warning",
                    true,
                    Path.GetFileName(file) + " 导入抛异常（跳过）：" + ex.GetType().Name));
            }
        }

        Add(
            "perf-memory-corpus",
            smallBookIds.Count >= 10,
            "可用的真实小书 " + smallBookIds.Count + " 本（判据 >= 10）");

        // ---------- 6. 取消转换：响应、残留、20 次后的句柄 ----------

        var cancelBookId = smallBookIds.Count > 0 ? smallBookIds[0] : Guid.Empty;
        var cancelDurations = new List<long>();
        var residualDurations = new List<long>();
        var cancelFailures = new List<string>();
        var worstUiGap = 0L;

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var handlesBefore = process.HandleCount;

        if (cancelBookId != Guid.Empty)
        {
            for (var round = 1; round <= 20; round++)
            {
                using var cts = new CancellationTokenSource();
                var progressEvents = 0;
                Exception? cancelError = null;

                var convertTask = Task.Run(async () =>
                {
                    try
                    {
                        await conversion.ConvertAsync(
                            new ConversionRequest
                            {
                                BookId = cancelBookId,
                                TargetFormat = BookFormatKind.Pdf,
                            },
                            new SmokeProbe.SynchronousProgress<ConversionProgress>(
                                _ => Interlocked.Increment(ref progressEvents)),
                            cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        cancelError = ex;
                    }
                });

                // 必须等 Calibre 真的在跑再取消，否则"10 秒内无残留"是空验。
                var producing = await WaitUntilAsync(
                    () => Volatile.Read(ref progressEvents) > 0
                        && SmokeProbe.CountToolProcesses(paths) > 0,
                    TimeSpan.FromSeconds(30)).ConfigureAwait(true);

                var uiBeats = 0;
                var uiMaxGap = 0L;
                var uiLast = Stopwatch.StartNew();

                var uiHeartbeat = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(25),
                };

                uiHeartbeat.Tick += (_, _) =>
                {
                    uiBeats++;
                    var gap = uiLast.ElapsedMilliseconds;
                    uiLast.Restart();

                    if (gap > uiMaxGap)
                    {
                        uiMaxGap = gap;
                    }
                };

                uiHeartbeat.Start();

                var cancelWatch = Stopwatch.StartNew();
                cts.Cancel();

                var returned = await Task
                    .WhenAny(convertTask, Task.Delay(TimeSpan.FromSeconds(20)))
                    .ConfigureAwait(true);

                cancelWatch.Stop();
                uiHeartbeat.Stop();

                await convertTask.ConfigureAwait(true);

                if (uiMaxGap > worstUiGap)
                {
                    worstUiGap = uiMaxGap;
                }

                cancelDurations.Add(cancelWatch.ElapsedMilliseconds);

                // 等 Calibre 进程真的消失，最多 10 秒。
                var residualWatch = Stopwatch.StartNew();
                var gone = await WaitUntilAsync(
                    () => SmokeProbe.CountToolProcesses(paths) == 0,
                    TimeSpan.FromSeconds(ResidualLimitSeconds)).ConfigureAwait(true);
                residualWatch.Stop();

                residualDurations.Add(residualWatch.ElapsedMilliseconds);

                await Task.Delay(100).ConfigureAwait(true);

                var after = await repo.GetByIdAsync(cancelBookId).ConfigureAwait(true);
                var pdfRow = after?.Formats.FirstOrDefault(f => f.Format == BookFormatKind.Pdf);
                var epubRow = after?.Formats.FirstOrDefault(f => f.Format == BookFormatKind.Epub);
                var bookDirectory = epubRow is null ? null : Path.GetDirectoryName(epubRow.FilePath);
                var strayParts = bookDirectory is not null && Directory.Exists(bookDirectory)
                    ? Directory.GetFiles(bookDirectory, "*.part").Length
                    : 0;

                if (!producing)
                {
                    cancelFailures.Add("第 " + round + " 次没等到 Calibre 进程");
                }

                if (returned != convertTask)
                {
                    cancelFailures.Add("第 " + round + " 次取消耗时超过 20 秒");
                }

                if (cancelError is not OperationCanceledException)
                {
                    cancelFailures.Add("第 " + round + " 次不是取消异常（"
                        + (cancelError?.GetType().Name ?? "没抛异常") + "）");
                }

                if (!gone)
                {
                    cancelFailures.Add("第 " + round + " 次 " + ResidualLimitSeconds + " 秒后仍有进程");
                }

                if (SmokeProbe.CountLeftoverRunDirectories(paths) > 0)
                {
                    cancelFailures.Add("第 " + round + " 次留下 Calibre 运行目录");
                }

                if (pdfRow is not null || strayParts > 0)
                {
                    cancelFailures.Add("第 " + round + " 次留下产物登记或 .part 文件");
                }
            }
        }

        if (cancelBookId == Guid.Empty)
        {
            cancelFailures.Add("没有可用来取消的书");
        }

        Add(
            "perf-cancel-ui",
            cancelDurations.Count > 0
                && cancelDurations.Max() <= CancelLimitMs,
            "取消到返回 最慢 " + (cancelDurations.Count == 0 ? -1 : cancelDurations.Max())
                + " ms（判据 <= " + CancelLimitMs + "）；期间 UI 心跳最大间隔 " + worstUiGap + " ms");

        Add(
            "perf-cancel-no-residual",
            cancelFailures.Count == 0,
            "20 次取消：最慢进程消失 " + (residualDurations.Count == 0 ? -1 : residualDurations.Max())
                + " ms（判据 <= " + ResidualLimitSeconds * 1000 + "）；"
                + (cancelFailures.Count == 0 ? "无残留" : "异常：" + string.Join("；", cancelFailures)));

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();

        process.Refresh();
        var handlesAfter = process.HandleCount;
        var handleGrowth = handlesAfter - handlesBefore;

        Add(
            "perf-handle-growth",
            handleGrowth <= HandleGrowthLimit,
            "20 次取消后句柄 " + handlesBefore + " -> " + handlesAfter + "，增长 " + handleGrowth
                + "（判据 <= " + HandleGrowthLimit + "；量前已强制 GC）");

        // ---------- 7. 连续读 10 本书，内存 <= 1 GB ----------

        var opened = 0;
        var openFailures = new List<string>();
        var baselineMb = WorkingSetMb();
        var peakMb = baselineMb;

        foreach (var bookId in smallBookIds.Take(10))
        {
            try
            {
                var readerWindow = NewReaderWindow(
                    reader, environments, progressService, loggerFactory, bookId);

                readerWindow.Show();

                /* 就绪口径与阅读窗口自己的日志一致：章节号已定、且不是错误页。
                   不要求首章一定有正文/图片：有些书的第 1 章是只含样式/分隔的扉页。 */
                var windowReady = await WaitUntilAsync(
                    () => IsReady(readerWindow),
                    TimeSpan.FromSeconds(20)).ConfigureAwait(true);

                if (!windowReady)
                {
                    openFailures.Add(bookId.ToString("N")[..8] + " 没进入就绪");
                }
                else
                {
                    opened++;
                }

                peakMb = Math.Max(peakMb, WorkingSetMb());

                readerWindow.Close();
                await Task.Delay(300).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                openFailures.Add(bookId.ToString("N")[..8] + "：" + ex.GetType().Name);
            }
        }

        await Task.Delay(2000).ConfigureAwait(true);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var endMb = WorkingSetMb();

        Add(
            "perf-memory-10-books",
            opened == 10 && endMb <= MemoryLimitMb,
            "连续读 " + opened + "/10 本后工作集 " + endMb + " MB（判据 <= " + MemoryLimitMb
                + "），过程峰值 " + peakMb + " MB，进入前 " + baselineMb + " MB"
                + (openFailures.Count == 0 ? "" : "；失败：" + string.Join("；", openFailures)));

        return Build(paths, startedAt, total, checks);
    }

    private static ReaderWindow NewReaderWindow(
        IReaderService reader,
        IWebViewEnvironmentProvider environments,
        IReadingProgressService progress,
        ILoggerFactory loggerFactory,
        Guid bookId)
        => new(
            reader,
            environments,
            progress,
            loggerFactory.CreateLogger<ReaderWindow>(),
            bookId);

    private static bool IsReady(ReaderWindow window)
        => window.LastChapterIndex >= 0 && !window.RenderedIsErrorPage;

    private static bool IsRendered(ReaderWindow window)
        => window.LastChapterIndex >= 0
            && (window.RenderedTextLength > 0 || window.RenderedImageCount > 0)
            && !window.RenderedIsErrorPage;

    private static long WorkingSetMb()
        => Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;

    /// <summary>直接写库造 1000 本（与 T2-3 网格冒烟同一套规则）。</summary>
    private static async Task SeedAsync(IBookRepository repo)
    {
        var now = DateTimeOffset.Now;

        for (var i = 0; i < BookCount; i++)
        {
            var book = new Book
            {
                Title = "冒烟书 " + (i + 1).ToString("D4"),
                Authors = "冒烟作者",
                Status = i % 3 == 0 ? ReadingStatus.Reading : ReadingStatus.Unread,
                CoverPath = i % 5 == 0 ? null : "Covers/fake-" + i + ".jpg",
                CreatedAt = now.AddSeconds(-i),
                UpdatedAt = now,
            };

            book.Formats.Add(new BookFormat
            {
                BookId = book.Id,
                Format = i % 2 == 0 ? BookFormatKind.Epub : BookFormatKind.Mobi,
                FilePath = "Library/smoke-" + i.ToString("D4") + (i % 2 == 0 ? ".epub" : ".mobi"),
                FileSize = 1000 + i,
                FileModifiedAt = now,
                AddedAt = now,
            });

            await repo.AddAsync(book).ConfigureAwait(true);
        }
    }

    private static SmokeReport Build(
        AppPaths paths,
        DateTimeOffset startedAt,
        Stopwatch total,
        IReadOnlyList<SmokeCheck> checks)
    {
        total.Stop();

        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            total.ElapsedMilliseconds,
            paths.DataRoot,
            paths.WebView2DataDir,
            null,
            checks);

        File.WriteAllText(
            Path.Combine(paths.TempDir, "t71-smoke.json"),
            JsonSerializer.Serialize(report, ReportOptions),
            new UTF8Encoding(false));

        return report;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();

        while (watch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20).ConfigureAwait(true);
        }

        return condition();
    }

    /// <summary>在可视树里找第一个 ScrollViewer（ListBox 的滚动条藏在里面）。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is ScrollViewer found)
            {
                return found;
            }

            var nested = FindScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
