using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Infrastructure.Files;
using EbookToolSuite.Services.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// 批量导入队列的端到端冒烟（执行计划 T2-1 的五条完成判据）。
/// 全部用真实文件系统 + 真实 SQLite + 真实流水线，只替换掉"重复对话框"
/// （无头环境点不了按钮，但"被问了几次"是可以精确断言的）。
/// </summary>
/// <remarks>
/// 样本不是凭空造的：拿真实 EPUB 当模板，把 zip 条目原样复制一遍，
/// 再往 META-INF 里塞一个第 i 号的标记文件。这样每一本都是**合法**的 EPUB、
/// 又因为文件大小与内容不同而拥有互不相同的快速指纹（否则 200 本会被判成重复）。
/// </remarks>
public static class BatchImportSmokeTest
{
    private const int GoodBookCount = 200;

    public static async Task<SmokeReport> RunAsync(IServiceProvider services, AppPaths paths, string templateEpub)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        var repo = services.GetRequiredService<IBookRepository>();
        var scanner = services.GetRequiredService<IImportScanner>();
        var batches = services.GetRequiredService<IImportBatchRepository>();

        var sourceDir = Path.Combine(paths.TempDir, "batch-smoke-src");
        var cancelDir = Path.Combine(paths.TempDir, "batch-smoke-cancel");

        ResetDirectory(sourceDir);
        ResetDirectory(cancelDir);

        // ---------- 场景 0：扫描器（递归 + 扩展名白名单 + 跳过读不到的） ----------
        SmokeProbe.BuildCorpus(templateEpub, sourceDir, GoodBookCount, markerBase: 0);
        var scan = scanner.Scan(new[] { sourceDir }, recursive: true);

        checks.Add(new SmokeCheck(
            "A1-scan-recursive",
            scan.Count == GoodBookCount + 1,
            "扫到 " + scan.Count + " 个（期望 " + (GoodBookCount + 1) + "：200 本好 + 1 本坏），"
                + "跳过非电子书 " + scan.SkippedUnsupported + " 个，扫描目录 " + scan.FoldersScanned + " 个"));

        checks.Add(new SmokeCheck(
            "A2-scan-skips-non-ebook",
            scan.SkippedUnsupported == 1,
            "非电子书跳过数=" + scan.SkippedUnsupported + "（期望 1，即那个 .jpg）"));

        var booksBefore = await repo.CountAsync().ConfigureAwait(true);
        // ---------- 场景 A：递归导入 200 本（目录里还混着 1 本坏书） ----------
        var resolverA = new ScriptedDuplicateResolver();
        await using var queue = BuildQueue(services, resolverA);

        var probe = new UiLivenessProbe();
        probe.Start();

        var batchA = await queue
            .StartAsync(
                new[] { sourceDir },
                new ImportQueueOptions { PromptOnDuplicates = false, SourceLabel = "批量导入冒烟 200 本" })
            .ConfigureAwait(true);

        await queue.WaitForCompletionAsync().ConfigureAwait(true);
        probe.Stop();

        var jobs = queue.Jobs.ToList();
        var importedA = jobs.Count(j => j.Outcome == ImportOutcome.Imported);
        var failedA = jobs.Count(j => j.Outcome is ImportOutcome.InvalidEpub or ImportOutcome.Failed);

        checks.Add(new SmokeCheck(
            "A3-import-200-all-succeeded",
            importedA == GoodBookCount && failedA == 1 && jobs.Count == GoodBookCount + 1,
            "成功 " + importedA + " / " + GoodBookCount + "，失败 " + failedA
                + "，用时 " + (batchA.Duration?.TotalSeconds.ToString("F1") ?? "?") + " 秒"));

        var brokenIndex = jobs.FindIndex(j => j.FileName.StartsWith("aaa-broken", StringComparison.OrdinalIgnoreCase));
        var afterBroken = brokenIndex >= 0
            ? jobs.Skip(brokenIndex + 1).Count(j => j.Outcome == ImportOutcome.Imported)
            : 0;

        checks.Add(new SmokeCheck(
            "A4-bad-book-does-not-block",
            brokenIndex >= 0
                && jobs[brokenIndex].Outcome == ImportOutcome.InvalidEpub
                && afterBroken == GoodBookCount,
            "坏书排在第 " + (brokenIndex + 1) + " 个，结果 " + (brokenIndex >= 0 ? jobs[brokenIndex].Outcome?.ToString() : "?")
                + "；它后面仍然成功导入 " + afterBroken + " 本（期望 " + GoodBookCount + "，说明队列没被卡死）"));

        var libraryAfterA = CountFiles(paths.LibraryDir);
        var booksAfterA = await repo.CountAsync().ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "A5-db-and-library-line-up",
            booksAfterA == booksBefore + GoodBookCount && libraryAfterA == GoodBookCount,
            "库中 " + booksAfterA + " 本（期望 " + (booksBefore + GoodBookCount) + "），"
                + "Library 下 " + libraryAfterA + " 个文件（期望 " + GoodBookCount + "）"));

        checks.Add(new SmokeCheck(
            "A6-ui-stays-responsive",
            probe.Ticks > 10 && probe.MaxGapMs < 600,
            "导入期间 UI 线程心跳 " + probe.Ticks + " 次，最大停顿 " + probe.MaxGapMs.ToString("F0")
                + " ms（判据：最大停顿 < 600 ms，即界面没被锁死）"));

        var recent = await batches.ListRecentAsync(20).ConfigureAwait(true);
        var savedA = recent.FirstOrDefault(b => b.Id == batchA.Id);

        checks.Add(new SmokeCheck(
            "A7-import-batch-row-saved",
            savedA is not null
                && savedA.IsFinished
                && savedA.Total == GoodBookCount + 1
                && savedA.Succeeded == GoodBookCount
                && savedA.Failed == 1,
            savedA is null
                ? "import_batch 里查不到这一批"
                : savedA.Summary + "，失败 " + savedA.Failed + "，备注=" + (savedA.Note ?? "无")));
        // ---------- 场景 B：同一批再导一次，200 本重复只准问一次 ----------
        var resolverB = new ScriptedDuplicateResolver();
        await using var queueB = BuildQueue(services, resolverB);

        await queueB
            .StartAsync(
                new[] { sourceDir },
                new ImportQueueOptions { PromptOnDuplicates = true, SourceLabel = "批量导入冒烟 重复" })
            .ConfigureAwait(true);

        // StartAsync 一返回就该判完：不能等 Worker 一本一本走到跟前才标
        var skippedRightAway = queueB.Jobs.Count(j => j.Outcome == ImportOutcome.Duplicate);
        await queueB.WaitForCompletionAsync().ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "B1-duplicate-prompt-asked-once",
            resolverB.BatchCalls == 1 && resolverB.SingleCalls == 0 && resolverB.LastDuplicateCount == GoodBookCount,
            "整批对话框被调用 " + resolverB.BatchCalls + " 次（期望 1），逐项对话框 "
                + resolverB.SingleCalls + " 次（期望 0），一次列出 " + resolverB.LastDuplicateCount
                + " 本重复（期望 " + GoodBookCount + "）"));

        checks.Add(new SmokeCheck(
            "B2-duplicates-settled-during-preflight",
            skippedRightAway == GoodBookCount,
            "StartAsync 返回时已是终态的跳过数=" + skippedRightAway + "（期望 " + GoodBookCount + "）"));

        var booksAfterB = await repo.CountAsync().ConfigureAwait(true);
        var libraryAfterB = CountFiles(paths.LibraryDir);

        checks.Add(new SmokeCheck(
            "B3-nothing-imported-twice",
            booksAfterB == booksAfterA && libraryAfterB == libraryAfterA,
            "二次导入后库中 " + booksAfterB + " 本（期望不变 " + booksAfterA + "），"
                + "Library 下 " + libraryAfterB + " 个文件（期望不变 " + libraryAfterA + "）"));

        // ---------- 场景 C：取消（完成当前这本后停，未开始的没动） ----------
        SmokeProbe.BuildCorpus(templateEpub, cancelDir, 40, markerBase: 100000);

        var resolverC = new ScriptedDuplicateResolver();
        await using var queueC = BuildQueue(services, resolverC);

        // 第一本一成功就取消：处理器跑在 Worker 线程上、且在 RunOneAsync 返回之前，
        // 所以"后面还会不会被导入"是确定性的，不靠 sleep 猜。
        queueC.JobStateChanged += (_, job) =>
        {
            if (job.State == ImportJobState.Finished && job.Outcome == ImportOutcome.Imported)
            {
                queueC.Cancel();
            }
        };

        await queueC
            .StartAsync(new[] { cancelDir }, new ImportQueueOptions { PromptOnDuplicates = false })
            .ConfigureAwait(true);

        await queueC.WaitForCompletionAsync().ConfigureAwait(true);

        var importedC = queueC.Jobs.Count(j => j.Outcome == ImportOutcome.Imported);
        var canceledC = queueC.Jobs.Count(j => j.State == ImportJobState.Canceled);

        var totalC = queueC.TotalCount;

        checks.Add(new SmokeCheck(
            "C1-cancel-finishes-current-then-stops",
            importedC == 1 && canceledC == totalC - 2 && queueC.CompletedCount == totalC,
            "取消后：目录里 " + totalC + " 个文件（40 本好 + 1 本坏），已导入 " + importedC
                + "（期望 1），已取消 " + canceledC + "（期望 " + (totalC - 2)
                + "），进入终态 " + queueC.CompletedCount + " / " + totalC));
        var booksAfterC = await repo.CountAsync().ConfigureAwait(true);


        var libraryAfterC = CountFiles(paths.LibraryDir);

        checks.Add(new SmokeCheck(
            "C2-cancel-leaves-no-leftover",
            booksAfterC == booksAfterB + 1 && libraryAfterC == libraryAfterB + 1,
            "取消后库中 " + booksAfterC + " 本（期望 " + (booksAfterB + 1) + "），"
                + "Library 下 " + libraryAfterC + " 个文件（期望 " + (libraryAfterB + 1) + "）"));
        return Finish(paths, startedAt, stopwatch, checks);
    }

    // ---------- 样本与工具 ----------

    private static ImportQueue BuildQueue(IServiceProvider services, IDuplicateResolver resolver)
        => new(
            services.GetRequiredService<IImportScanner>(),
            services.GetRequiredService<IBookImportService>(),
            services.GetRequiredService<IBookRepository>(),
            services.GetRequiredService<IImportBatchRepository>(),
            services.GetRequiredService<IHashUtil>(),
            resolver,
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<ILogger<ImportQueue>>());

    private static void ResetDirectory(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
    }

    private static int CountFiles(string dir)
        => Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length : 0;

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
            WebView2Version: null,
            checks);

        File.WriteAllText(
            Path.Combine(paths.TempDir, "batch-import-smoke.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return report;
    }

    /// <summary>脚本化的重复决策者：不弹窗，只记"被问了几次、一次列了几本"。</summary>
    private sealed class ScriptedDuplicateResolver : IDuplicateResolver
    {
        public int BatchCalls { get; private set; }

        public int SingleCalls { get; private set; }

        public int LastDuplicateCount { get; private set; }

        public Task<DuplicatePolicy> ResolveBatchAsync(
            IReadOnlyList<ImportJob> duplicates, CancellationToken ct = default)
        {
            BatchCalls++;
            LastDuplicateCount = duplicates.Count;
            return Task.FromResult(DuplicatePolicy.SkipAll);
        }

        public Task<DuplicatePolicy> ResolveSingleAsync(
            ImportJob duplicate, int index, int total, CancellationToken ct = default)
        {
            SingleCalls++;
            return Task.FromResult(DuplicatePolicy.SkipAll);
        }
    }

    /// <summary>
    /// "导入 200 本期间 UI 不卡"这条判据怎么测：
    /// 挂一个 25 ms 的 DispatcherTimer 当心跳，记最大间隔。
    /// 界面被锁死时心跳会停，最大间隔就会飙上去。
    /// </summary>
    private sealed class UiLivenessProbe
    {
        private readonly DispatcherTimer _timer;
        private DateTime _last;

        public UiLivenessProbe()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            _timer.Tick += OnTick;
        }

        public int Ticks { get; private set; }

        public double MaxGapMs { get; private set; }

        public void Start()
        {
            _last = DateTime.UtcNow;
            _timer.Start();
        }

        public void Stop() => _timer.Stop();

        private void OnTick(object? sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            var gap = (now - _last).TotalMilliseconds;
            _last = now;
            Ticks++;

            if (gap > MaxGapMs)
            {
                MaxGapMs = gap;
            }
        }
    }
}
