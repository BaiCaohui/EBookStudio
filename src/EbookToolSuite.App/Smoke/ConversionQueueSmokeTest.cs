using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Services.Conversion;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// 转换队列的冒烟测试（执行计划 T2-4 的 6 条完成判据）。
/// 真跑 Calibre：队列的价值就在于「排一堆、按序跑、能取消、能重试、崩了能接着跑」，
/// 用假服务顶替就把要验的东西验没了（那些归 Services 层的单测）。
/// </summary>
public static class ConversionQueueSmokeTest
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(20);

    public static async Task<SmokeReport> RunAsync(
        IServiceProvider services, AppPaths paths, string sourceFolder)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        var importer = services.GetRequiredService<IBookImportService>();
        var queue = services.GetRequiredService<IConversionQueue>();
        var repo = services.GetRequiredService<IBookRepository>();
        var config = services.GetRequiredService<IConfigStore>();
        var queueVm = services.GetRequiredService<ConversionQueueViewModel>();

        // ---------- 准备：把脚本造好的 10 本导进库 ----------
        var files = Directory.Exists(sourceFolder)
            ? Directory.GetFiles(sourceFolder, "*.epub", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();

        checks.Add(new SmokeCheck(
            "prep-source-files",
            files.Length >= 10,
            "源目录里找到 " + files.Length + " 个 EPUB（判据 >= 10）"));

        foreach (var file in files)
        {
            try
            {
                await importer.ImportAsync(file, new ImportOptions(), null, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "队列冒烟：导入失败 {File}", file);
            }
        }

        var books = await repo.ListAsync(0, 50, CancellationToken.None).ConfigureAwait(true);
        var epubs = books
            .Where(b => b.Formats.Any(f => f.Format == BookFormatKind.Epub))
            .ToList();

        checks.Add(new SmokeCheck(
            "prep-imported",
            epubs.Count >= 10,
            "库里 EPUB 书 " + epubs.Count + " 本（判据 >= 10）"));

        // ---------- 队列页的 XAML 能真的构造并布局出来 ----------
        // 这一步专门抓绑定写错、资源键写错这类只有运行时才炸的问题。
        Exception? pageError = null;
        var rowCount = -1;
        Views.Pages.QueuePage? page = null;   // 提出来：后面还要用它验行内的产物按钮
        try
        {
            await queueVm.StartAsync().ConfigureAwait(true);

            page = new Views.Pages.QueuePage(queueVm, new SmokeProbe.NoopShellLauncher());
            page.Measure(new System.Windows.Size(1200, 800));
            page.Arrange(new System.Windows.Rect(0, 0, 1200, 800));
            page.UpdateLayout();

            rowCount = (page.FindName("TaskList")
                as System.Windows.Controls.ItemsControl)?.Items.Count ?? -1;
        }
        catch (Exception ex)
        {
            pageError = ex;
        }

        checks.Add(new SmokeCheck(
            "queue-page-loads",
            pageError is null,
            pageError is null
                ? "QueuePage 构造并布局成功，列表里 " + rowCount + " 行"
                : pageError.GetType().Name + "：" + pageError.Message));

        // ---------- 判据 a：排 10 本，依次执行，全部成功 ----------
        var target = BookFormatKind.Mobi;
        var enqueued = 0;
        var enqueueErrors = new List<string>();
        foreach (var book in epubs.Take(10))
        {
            try
            {
                await queue.EnqueueAsync(new ConversionRequest
                {
                    BookId = book.Id,
                    TargetFormat = target,
                }).ConfigureAwait(true);
                enqueued++;
            }
            catch (AppException ex)
            {
                enqueueErrors.Add(ex.Code.ToString());
            }
        }

        checks.Add(new SmokeCheck(
            "enqueue-10",
            enqueued >= 10,
            "成功入队 " + enqueued + " 条"
                + (enqueueErrors.Count > 0 ? "；被拒 " + string.Join("、", enqueueErrors) : string.Empty)));

        var idle = await WaitIdleAsync(queue).ConfigureAwait(true);
        var mine = queue.Tasks.Where(t => t.OutFormat == target).ToList();
        var completed = mine.Count(t => t.Status == ConversionTaskStatus.Completed);
        var failed = mine.Count(t => t.Status == ConversionTaskStatus.Failed);

        checks.Add(new SmokeCheck(
            "all-completed",
            idle && completed >= 10,
            "完成 " + completed + " 条 / 失败 " + failed + " 条 / 共 " + mine.Count + " 条"));

        var outputs = mine.Count(t => t.Status == ConversionTaskStatus.Completed
            && File.Exists(t.OutputPath));
        checks.Add(new SmokeCheck("outputs-exist", outputs >= 10, "产物文件真的存在 " + outputs + " 个"));

        /* T2-7 用户要求：队列里要能看到产物在哪、能把它拿走（「跳转」/「导出」两个按钮）。
           两层都验：VM 上"能不能用"的判断要对（含"产物被删掉要变灰"，否则用户点了只弹错误），
           界面上两个按钮要真的出现。 */
        var completedRow = queueVm.Rows.FirstOrDefault(r => r.Task.Status == ConversionTaskStatus.Completed);
        var rowPathOk = completedRow is not null
            && completedRow.OutputPath.Length > 0
            && File.Exists(completedRow.OutputPath)
            && completedRow.CanOpenOutput;

        var probePath = completedRow?.OutputPath;
        var turnsOffWhenMissing = false;

        if (probePath is not null && File.Exists(probePath))
        {
            File.Delete(probePath);
            completedRow!.Refresh();
            turnsOffWhenMissing = !completedRow.CanOpenOutput;
        }

        // 行是虚拟化的：先把页面重新布局一遍，行内的按钮才会被实现出来
        page?.Measure(new System.Windows.Size(1200, 800));
        page?.Arrange(new System.Windows.Rect(0, 0, 1200, 800));
        page?.UpdateLayout();

        var rowButtons = new List<Button>();

        if (page is not null)
        {
            CollectButtons(page, rowButtons);
        }

        var jumpButton = rowButtons.FirstOrDefault(b => (b.Content as string) == "跳转");
        var exportButton = rowButtons.FirstOrDefault(b => (b.Content as string) == "导出");

        checks.Add(new SmokeCheck(
            "output-actions-wired",
            rowPathOk && turnsOffWhenMissing && jumpButton is not null && exportButton is not null,
            "产物路径可用=" + rowPathOk + "，删掉产物后变灰=" + turnsOffWhenMissing
                + "，跳转按钮=" + (jumpButton is not null) + "，导出按钮=" + (exportButton is not null)));

        // ---------- 没有孤儿进程 / 没有残留临时目录 ----------
        var leaked = SmokeProbe.CountToolProcesses(paths);
        checks.Add(new SmokeCheck("no-calibre-left", leaked == 0, "残留 Calibre 进程=" + leaked + "（判据 0）"));

        var leftovers = SmokeProbe.CountLeftoverRunDirectories(paths);
        checks.Add(new SmokeCheck("no-leftover-dirs", leftovers == 0, "残留 conv-* 临时目录=" + leftovers + "（判据 0）"));

        // ---------- 判据 f：查看日志能看到 Calibre 的完整输出 ----------
        var sample = mine.FirstOrDefault(t => t.Status == ConversionTaskStatus.Completed);
        var log = sample is null ? null : queue.GetTaskOutput(sample.Id);
        checks.Add(new SmokeCheck(
            "log-has-output",
            !string.IsNullOrWhiteSpace(log) && log!.Length > 50,
            "这条任务的外部输出 " + (log?.Length ?? 0) + " 字符（判据 > 50）"));

        // ---------- 判据 c：磁盘满 -> 明确失败，且一条也不重试 ----------
        // 把倍率调到天文数字，预检必然判 DiskFull（真把磁盘写满太危险）。
        await config.SetAsync("conversion.diskSpaceFactor", JsonValue.Create(1000000000d)).ConfigureAwait(true);
        await config.SaveAsync().ConfigureAwait(true);

        try
        {
            await queue.EnqueueAsync(new ConversionRequest
            {
                BookId = epubs[0].Id,
                TargetFormat = BookFormatKind.Pdf,
            }).ConfigureAwait(true);

            await WaitIdleAsync(queue).ConfigureAwait(true);

            var guarded = queue.Tasks.First(t => t.OutFormat == BookFormatKind.Pdf);
            var detail = "状态=" + guarded.Status + " 错误码=" + (guarded.ErrorCode ?? "(无)") + " 重试=" + guarded.RetryCount;
            checks.Add(new SmokeCheck(
                "disk-full-fails",
                guarded.Status == ConversionTaskStatus.Failed && guarded.ErrorCode == nameof(ErrorCode.DiskFull),
                detail));

            checks.Add(new SmokeCheck(
                "disk-full-no-retry",
                guarded.RetryCount == 0,
                "重试次数=" + guarded.RetryCount + "（判据 0）"));
        }
        finally
        {
            await config.SetAsync("conversion.diskSpaceFactor", JsonValue.Create(3.0d)).ConfigureAwait(true);
            await config.SaveAsync().ConfigureAwait(true);
        }

        // ---------- 库里已有该格式：入队就被拒 ----------
        Exception? dupError = null;
        try
        {
            await queue.EnqueueAsync(new ConversionRequest
            {
                BookId = epubs[0].Id,
                TargetFormat = target,
            }).ConfigureAwait(true);
        }
        catch (AppException ex)
        {
            dupError = ex;
        }

        checks.Add(new SmokeCheck(
            "duplicate-enqueue-rejected",
            dupError is AppException { Code: ErrorCode.ConversionOutputExists },
            dupError is null ? "（没有被拒绝）" : ((AppException)dupError).Code.ToString()));

        // ---------- 判据 e：全部取消后 10 秒内所有外部进程消失 ----------
        var cancelBooks = epubs.Skip(3).Take(3).ToList();
        foreach (var book in cancelBooks)
        {
            await queue.EnqueueAsync(new ConversionRequest
            {
                BookId = book.Id,
                TargetFormat = BookFormatKind.Pdf,
            }).ConfigureAwait(true);
        }

        var started = await WaitForToolProcessAsync(paths, TimeSpan.FromSeconds(90)).ConfigureAwait(true);

        var cancelWatch = Stopwatch.StartNew();
        var canceled = await queue.CancelAllAsync().ConfigureAwait(true);
        cancelWatch.Stop();

        var afterCancel = SmokeProbe.CountToolProcesses(paths);
        checks.Add(new SmokeCheck(
            "cancel-all-fast",
            started && cancelWatch.Elapsed <= TimeSpan.FromSeconds(10),
            "取消 " + canceled + " 条，耗时 " + cancelWatch.Elapsed.TotalSeconds.ToString("F1")
                + " 秒（取消前有进程=" + started + "，判据 <= 10 秒）"));

        checks.Add(new SmokeCheck(
            "cancel-all-no-processes",
            afterCancel == 0,
            "取消后残留 Calibre 进程=" + afterCancel + "（判据 0）"));

        // ---------- 清除已完成：只清终态 ----------
        var terminalBefore = queue.Tasks.Count(t => t.IsTerminal);
        var cleared = await queue.ClearFinishedAsync().ConfigureAwait(true);
        checks.Add(new SmokeCheck(
            "clear-finished",
            cleared == terminalBefore && queue.Tasks.All(t => !t.IsTerminal),
            "清掉 " + cleared + " 条（清之前终态 " + terminalBefore + " 条）"));

        // ---------- 判据 d：文件被占用 -> 自动重试并成功 ----------
        // 用独占句柄锁住源文件：Calibre 起来读不到它，报 Permission denied 退出（exit=1），
        // 这条错误被判成「会自己好」，于是退避 5 秒后重试；那时锁已经放开，第二次成功。
        var lockedBook = epubs[6];
        var lockedSource = lockedBook.Formats.First(f => f.Format == BookFormatKind.Epub).FilePath;

        ConversionTask? lockedTask = null;
        var lockStream = new FileStream(lockedSource, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            lockedTask = await queue.EnqueueAsync(new ConversionRequest
            {
                BookId = lockedBook.Id,
                TargetFormat = BookFormatKind.Pdf,
            }).ConfigureAwait(true);

            // 必须等它真的跑起来再放锁，否则 Calibre 还没读到文件、占用没被撞上
            await WaitUntilAsync(
                () => lockedTask.Status == ConversionTaskStatus.Running,
                "被锁的那条进入运行中",
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);

            await Task.Delay(2500).ConfigureAwait(true);
        }
        finally
        {
            lockStream.Dispose();
        }

        await WaitIdleAsync(queue).ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "locked-file-retried",
            lockedTask is { Status: ConversionTaskStatus.Completed } && lockedTask.RetryCount >= 1,
            "状态=" + (lockedTask?.Status.ToString() ?? "(无)")
                + " 重试=" + (lockedTask?.RetryCount ?? -1) + " 次（判据 完成且至少重试 1 次）"));

        // ---------- 判据 b（数据库层模拟）：启动时把残留的 Running 归位并接着跑 ----------
        // 真正的「强杀进程再重启」要另一个进程配合；这里用「新建一个队列实例」走同一条
        // ResetInterruptedAsync 路径（Services 层单测已覆盖逻辑本身）。
        var taskRepo = services.GetRequiredService<IConversionTaskRepository>();
        var clock = services.GetRequiredService<IClock>();
        var resumeBook = epubs[7];
        var resumeSource = resumeBook.Formats.First(f => f.Format == BookFormatKind.Epub).FilePath;
        var resumeNow = DateTimeOffset.Now;
        var orphan = new ConversionTask
        {
            Id = Guid.NewGuid(),
            BookId = resumeBook.Id,
            InputPath = resumeSource,
            OutputPath = Path.Combine(Path.GetDirectoryName(resumeSource)!, "resume.txt"),
            OutFormat = BookFormatKind.Txt,
            Status = ConversionTaskStatus.Running,
            Progress = 0.42,
            ProgressKind = ConversionTaskProgressKind.Percent,
            CreatedAt = resumeNow,
            StartedAt = resumeNow,
        };
        await taskRepo.AddAsync(orphan).ConfigureAwait(true);

        await using (var restarted = new ConversionQueue(
            taskRepo,
            services.GetRequiredService<IConversionService>(),
            repo,
            paths,
            clock,
            ConversionQueueOptions.Default,
            null))
        {
            await restarted.StartAsync().ConfigureAwait(true);
            await WaitIdleAsync(restarted).ConfigureAwait(true);

            var after = await taskRepo.GetByIdAsync(orphan.Id).ConfigureAwait(true);
            checks.Add(new SmokeCheck(
                "crash-resume-completes",
                after is { Status: ConversionTaskStatus.Completed },
                "状态=" + (after?.Status.ToString() ?? "(读不到)")
                    + " 进度=" + (after?.Progress?.ToString("F2") ?? "(空)")));
        }

        return Finish(paths, startedAt, stopwatch, checks);
    }

    /// <summary>等队列彻底空闲。超时返回 false（不抛，让冒烟照样出报告）。</summary>
    /// <summary>把可视树里的按钮全收出来（按内容找"跳转""导出"用）。</summary>
    private static void CollectButtons(DependencyObject root, List<Button> into)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (child is Button button)
            {
                into.Add(button);
            }

            CollectButtons(child, into);
        }
    }
    private static async Task<bool> WaitIdleAsync(IConversionQueue queue)
    {
        using var cts = new CancellationTokenSource(IdleTimeout);
        try
        {
            await queue.WaitForIdleAsync(cts.Token).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>等到真有一个外部工具进程在跑。判「取消」与「孤儿」都得先看见进程，
    /// 否则「取消后 0 残留」是空验。</summary>
    private static async Task<bool> WaitForToolProcessAsync(AppPaths paths, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            if (SmokeProbe.CountToolProcesses(paths) > 0)
            {
                return true;
            }

            await Task.Delay(100).ConfigureAwait(true);
        }

        return false;
    }

    /// <summary>
    /// 判据 b 的第一阶段：排 10 本，等第一本真的跑起来，然后【强杀自己】。
    /// 用 Kill 而不是 Environment.Exit：Exit 会走正常清理路径，那就不算「崩溃」了。
    /// </summary>
    public static async Task<int> RunCrashPhaseAsync(
        IServiceProvider services, AppPaths paths, string sourceFolder)
    {
        var importer = services.GetRequiredService<IBookImportService>();
        var queue = services.GetRequiredService<IConversionQueue>();
        var repo = services.GetRequiredService<IBookRepository>();

        var files = Directory.GetFiles(sourceFolder, "*.epub", SearchOption.TopDirectoryOnly);
        foreach (var file in files)
        {
            try
            {
                await importer.ImportAsync(file, new ImportOptions(), null, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "崩溃冒烟：导入失败 {File}", file);
            }
        }

        var books = await repo.ListAsync(0, 50, CancellationToken.None).ConfigureAwait(true);
        var epubs = books
            .Where(b => b.Formats.Any(f => f.Format == BookFormatKind.Epub))
            .Take(10)
            .ToList();

        foreach (var book in epubs)
        {
            await queue.EnqueueAsync(new ConversionRequest
            {
                BookId = book.Id,
                TargetFormat = BookFormatKind.Mobi,
            }).ConfigureAwait(true);
        }

        Log.Information("崩溃冒烟：已排 {Count} 本，等第一本跑起来就强杀自己", epubs.Count);

        if (!await WaitForToolProcessAsync(paths, TimeSpan.FromSeconds(90)).ConfigureAwait(true))
        {
            Log.Error("崩溃冒烟：等不到 Calibre 进程起来，放弃");
            return 3;
        }

        await Task.Delay(1500).ConfigureAwait(true);   // 让它真的产出一点东西

        // 记录「杀的那一刻已经完成了几条」，第二阶段靠它证明「确实有过要恢复的东西」
        var completedAtKill = queue.Tasks.Count(t => t.Status == ConversionTaskStatus.Completed);
        Directory.CreateDirectory(paths.TempDir);
        var marker = new Dictionary<string, object?>
        {
            ["enqueued"] = epubs.Count,
            ["completedAtKill"] = completedAtKill,
            ["killedAt"] = DateTimeOffset.Now.ToString("O"),
        };
        File.WriteAllText(
            Path.Combine(paths.TempDir, "queue-crash-marker.json"),
            JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        Log.Warning("崩溃冒烟：现在强杀自己（模拟断电），已完成 {Done}/{Total}", completedAtKill, epubs.Count);
        await Log.CloseAndFlushAsync().ConfigureAwait(true);

        Process.GetCurrentProcess().Kill();
        return 99;   // 走不到这里
    }

    /// <summary>
    /// 判据 b 的第二阶段：App 启动时已经调过 queue.StartAsync()，
    /// 它会把上次残留的 Running 归位成排队并接着跑。这里等它跑完并验收。
    /// </summary>
    public static async Task<SmokeReport> RunResumeAsync(IServiceProvider services, AppPaths paths)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        var queue = services.GetRequiredService<IConversionQueue>();
        var taskRepo = services.GetRequiredService<IConversionTaskRepository>();

        // 第一阶段留下的现场：排了几条、被杀时完成了几条
        var enqueued = 0;
        var completedAtKill = -1;
        var markerPath = Path.Combine(paths.TempDir, "queue-crash-marker.json");
        if (File.Exists(markerPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(markerPath, Encoding.UTF8));
            enqueued = doc.RootElement.GetProperty("enqueued").GetInt32();
            completedAtKill = doc.RootElement.GetProperty("completedAtKill").GetInt32();
        }

        checks.Add(new SmokeCheck(
            "crash-marker-read",
            enqueued >= 10 && completedAtKill >= 0,
            "第一阶段排了 " + enqueued + " 条，被杀时已完成 " + completedAtKill + " 条"));

        var idle = await WaitIdleAsync(queue).ConfigureAwait(true);

        var mine = queue.Tasks.Where(t => t.OutFormat == BookFormatKind.Mobi).ToList();
        var completed = mine.Count(t => t.Status == ConversionTaskStatus.Completed);
        var failed = mine.Count(t => t.Status == ConversionTaskStatus.Failed);

        checks.Add(new SmokeCheck(
            "resume-completed",
            idle && enqueued > 0 && completed >= enqueued,
            "重启后完成 " + completed + " / 共 " + enqueued + " 条（失败 " + failed + " 条）"));

        // 被杀时还没跑完，重启后才补齐，这才是真的验到了「接着跑」
        checks.Add(new SmokeCheck(
            "resume-was-needed",
            completedAtKill < enqueued,
            "被杀时已完成 " + completedAtKill + " 条（小于 " + enqueued + " 才算真需要恢复）"));

        var stillActive = await taskRepo.ListActiveAsync().ConfigureAwait(true);
        checks.Add(new SmokeCheck(
            "resume-no-active-left",
            stillActive.Count == 0,
            "库里还剩未终态任务 " + stillActive.Count + " 条（判据 0）"));

        var leaked = SmokeProbe.CountToolProcesses(paths);
        checks.Add(new SmokeCheck("resume-no-processes", leaked == 0, "残留 Calibre 进程=" + leaked));

        return Finish(paths, startedAt, stopwatch, checks);
    }

    /// <summary>等某个条件成立。超时返回 false，让调用方的判据去报红，不抛。</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, string what, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100).ConfigureAwait(true);
        }

        Log.Warning("队列冒烟：等不到 {What}", what);
        return false;
    }

    private static SmokeReport Finish(
        AppPaths paths, DateTimeOffset startedAt, Stopwatch stopwatch, List<SmokeCheck> checks)
    {
        stopwatch.Stop();
        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            stopwatch.ElapsedMilliseconds,
            paths.DataRoot,
            paths.WebView2DataDir,
            null,
            checks);

        Directory.CreateDirectory(paths.TempDir);
        File.WriteAllText(
            Path.Combine(paths.TempDir, "convert-queue-smoke.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return report;
    }
}
