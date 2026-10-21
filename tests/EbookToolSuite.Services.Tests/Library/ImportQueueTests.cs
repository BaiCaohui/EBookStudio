using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Infrastructure.Data.Repositories;
using EbookToolSuite.Infrastructure.Files;
using EbookToolSuite.Services.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests;

/// <summary>
/// 导入队列用**真实的**扫描器 + 真实流水线 + 真实 SQLite（ImportSandbox）。
/// 队列的价值全在"多本一起导的时候会不会乱"，mock 掉任一段就测不出这个。
/// 唯一替换掉的是 IDuplicateResolver：对话框点不了，但"被问了几次、问了哪几本"能精确验证。
/// </summary>
public sealed class ImportQueueTests
{
    private static async Task<ImportSandbox> NewSandboxAsync()
    {
        var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();
        return sandbox;
    }

    private static ImportQueue BuildQueue(ImportSandbox sandbox, IDuplicateResolver resolver)
        => new(
            new ImportScanner(NullLogger<ImportScanner>.Instance),
            sandbox.Pipeline,
            sandbox.Repository,
            new ImportBatchRepository(sandbox.Factory),
            new HashUtil(),
            resolver,
            SystemClock.Instance,
            NullLogger<ImportQueue>.Instance);

    /// <summary>轮询等待条件成立（队列的事件都在后台线程，测试不能靠固定 Delay 猜时序）。</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    /// <summary>造 n 本内容各不相同的书（ChapterCount 不同 => 文件大小不同 => 指纹必不同）。</summary>
    private static List<string> WriteBooks(ImportSandbox sandbox, int count, int firstChapterCount = 2)
    {
        var files = new List<string>();
        for (var i = 0; i < count; i++)
        {
            files.Add(sandbox.WriteEpub(
                new EpubBuilder { Title = "第" + (i + 1) + "本", ChapterCount = firstChapterCount + i },
                "book-" + i + ".epub"));
        }

        return files;
    }

    private sealed class ScriptedResolver : IDuplicateResolver
    {
        private readonly DuplicatePolicy _policy;
        private readonly Func<ImportJob, int, bool>? _perItem;

        public ScriptedResolver(DuplicatePolicy policy, Func<ImportJob, int, bool>? perItem = null)
        {
            _policy = policy;
            _perItem = perItem;
        }

        public int BatchCalls { get; private set; }

        public int SingleCalls { get; private set; }

        public IReadOnlyList<ImportJob> LastDuplicates { get; private set; } = Array.Empty<ImportJob>();

        public Task<DuplicatePolicy> ResolveBatchAsync(
            IReadOnlyList<ImportJob> duplicates, CancellationToken ct = default)
        {
            BatchCalls++;
            LastDuplicates = duplicates;
            return Task.FromResult(_policy);
        }

        public Task<DuplicatePolicy> ResolveSingleAsync(
            ImportJob duplicate, int index, int total, CancellationToken ct = default)
        {
            SingleCalls++;
            return Task.FromResult(_perItem is not null && _perItem(duplicate, index)
                  ? DuplicatePolicy.ImportAll
                  : DuplicatePolicy.SkipAll);
        }
    }
    [Fact]
    public async Task Start_三本无重复_全部导入且批次行落盘()
    {
        await using var sandbox = await NewSandboxAsync();
        var files = WriteBooks(sandbox, 3);
        var resolver = new ScriptedResolver(DuplicatePolicy.SkipAll);
        await using var queue = BuildQueue(sandbox, resolver);

        var batch = await queue.StartAsync(files, ImportQueueOptions.Default);
        await queue.WaitForCompletionAsync();

        Assert.Equal(ImportQueueState.Finished, queue.State);
        Assert.Equal(3, queue.CompletedCount);
        Assert.Equal(3, queue.TotalCount);
        Assert.Equal(3, batch.Succeeded);
        Assert.Equal(0, batch.Failed);
        Assert.NotNull(batch.FinishedAt);
        Assert.Equal(0, resolver.BatchCalls);
        Assert.All(queue.Jobs, j => Assert.Equal(ImportOutcome.Imported, j.Outcome));
        Assert.All(queue.Jobs, j => Assert.True(j.Duration > TimeSpan.Zero));
        Assert.Equal(3, await sandbox.BookCountAsync());

        // 批次行必须落盘：活动中心事后要能查到"哪天导的、成了几本"。
        var rows = await new ImportBatchRepository(sandbox.Factory).ListRecentAsync();
        Assert.Single(rows);
        Assert.Equal(batch.Id, rows[0].Id);
        Assert.Equal(3, rows[0].Total);
        Assert.Equal(3, rows[0].Succeeded);
        Assert.Equal(0, rows[0].Failed);
        Assert.True(rows[0].IsFinished);
    }

    /// <summary>
    /// 判据（替换书籍）：重复导入选"替换"-> 库里那本旧的被删掉、只剩新导入的一本，
    /// 结果说明写"替换成功"。（用户反复踩过：替换退化成普通导入，库里越换越多。）
    /// </summary>
    [Fact]
    public async Task Replace_替换导入_库里只剩一本且说明是替换成功()
    {
        await using var sandbox = await NewSandboxAsync();

        var original = sandbox.WriteEpub(
            new EpubBuilder { Title = "要替换的书", ChapterCount = 2 }, "old.epub");
        var pre = await sandbox.Pipeline.ImportAsync(original, ImportOptions.Default);
        Assert.Equal(ImportOutcome.Imported, pre.Outcome);
        Assert.Equal(1, await sandbox.BookCountAsync());
        var oldBookId = pre.BookId!.Value;

        /* 重复检测是按"快速指纹"（大小+头尾）判的：必须用**同一个文件**再导一次才会被判成重复，
           否则流水线按新书导入，替换分支根本不会触发。 */
        var replacement = original;

        var resolver = new ScriptedResolver(DuplicatePolicy.Replace);
        await using var queue = BuildQueue(sandbox, resolver);

        var batch = await queue.StartAsync(
            new[] { replacement }, new ImportQueueOptions { PromptOnDuplicates = true });
        await queue.WaitForCompletionAsync();

        Assert.Equal(ImportQueueState.Finished, queue.State);
        Assert.Equal(1, resolver.BatchCalls);
        Assert.Equal(1, batch.Succeeded);

        // 旧书必须被删掉（DB 行 + 文件），库里只剩新导入的一本
        Assert.Null(await sandbox.Repository.GetByIdAsync(oldBookId, CancellationToken.None));
        Assert.Equal(1, await sandbox.BookCountAsync());
        Assert.Single(sandbox.LibraryFiles());

        var job = queue.Jobs.Single();
        Assert.Equal(ImportOutcome.Imported, job.Outcome);
        Assert.Contains("替换成功", job.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_库里已有重复_只弹一次并立刻把那一本标成跳过()
    {
        await using var sandbox = await NewSandboxAsync();
        var existing = sandbox.WriteEpub(
            new EpubBuilder { Title = "已经在库里的书", ChapterCount = 2 }, "dup.epub");
        var fresh = sandbox.WriteEpub(
            new EpubBuilder { Title = "全新的书", ChapterCount = 7 }, "fresh.epub");

        var preImport = await sandbox.Pipeline.ImportAsync(existing, ImportOptions.Default);
        Assert.Equal(ImportOutcome.Imported, preImport.Outcome);

        var resolver = new ScriptedResolver(DuplicatePolicy.SkipAll);
        await using var queue = BuildQueue(sandbox, resolver);
        var batch = await queue.StartAsync(new[] { existing, fresh }, ImportQueueOptions.Default);

        // 预检阶段就定终态：StartAsync 一返回，UI 列表就该显示"已跳过"。
        Assert.Equal(1, resolver.BatchCalls);
        Assert.Single(resolver.LastDuplicates);
        Assert.Equal(ImportJobState.Finished, queue.Jobs[0].State);
        Assert.Equal(ImportOutcome.Duplicate, queue.Jobs[0].Outcome);
        Assert.Contains("已经在库里的书", queue.Jobs[0].Message);
        // 不断言另一本的状态：StartAsync 返回时 Worker 可能已经在跑它了（这是正常的）。

        await queue.WaitForCompletionAsync();

        Assert.Equal(2, queue.CompletedCount);
        Assert.Equal(ImportOutcome.Imported, queue.Jobs[1].Outcome);
        Assert.Equal(1, batch.Succeeded);
        Assert.Contains("跳过重复 1 本", batch.Note);
        Assert.Equal(2, await sandbox.BookCountAsync());
    }

    [Fact]
    public async Task Start_同一批里两份相同文件_第二份被跳过()
    {
        await using var sandbox = await NewSandboxAsync();
        var one = sandbox.WriteEpub(
            new EpubBuilder { Title = "重复的书", ChapterCount = 3 }, "one.epub");
        var two = Path.Combine(sandbox.SourceDir, "two.epub");
        File.Copy(one, two);

        var resolver = new ScriptedResolver(DuplicatePolicy.SkipAll);
        await using var queue = BuildQueue(sandbox, resolver);
        var batch = await queue.StartAsync(new[] { one, two }, ImportQueueOptions.Default);
        await queue.WaitForCompletionAsync();

        Assert.Equal(1, resolver.BatchCalls);
        Assert.Single(resolver.LastDuplicates);
        Assert.Equal(ImportOutcome.Imported, queue.Jobs[0].Outcome);
        Assert.Equal(ImportOutcome.Duplicate, queue.Jobs[1].Outcome);
        Assert.Contains("与本次前面的文件重复", queue.Jobs[1].Message);
        Assert.Equal(1, await sandbox.BookCountAsync());
        Assert.Equal(1, batch.Succeeded);
    }

    [Fact]
    public async Task Start_用户选择全部仍要导入_重复的书真的进库()
    {
        await using var sandbox = await NewSandboxAsync();
        var one = sandbox.WriteEpub(
            new EpubBuilder { Title = "重复的书", ChapterCount = 3 }, "one.epub");
        var fresh = sandbox.WriteEpub(
            new EpubBuilder { Title = "全新的书", ChapterCount = 9 }, "fresh.epub");

        var preImport = await sandbox.Pipeline.ImportAsync(one, ImportOptions.Default);
        Assert.Equal(ImportOutcome.Imported, preImport.Outcome);

        var resolver = new ScriptedResolver(DuplicatePolicy.ImportAll);
        await using var queue = BuildQueue(sandbox, resolver);
        await queue.StartAsync(new[] { one, fresh }, ImportQueueOptions.Default);
        await queue.WaitForCompletionAsync();

        Assert.Equal(1, resolver.BatchCalls);
        Assert.All(queue.Jobs, j => Assert.Equal(ImportOutcome.Imported, j.Outcome));

        // 关键：用户说"仍要导入"就必须真的多出一份，不能被流水线自己再判一次重复吞掉。
        Assert.Equal(3, await sandbox.BookCountAsync());
    }
    [Fact]
    public async Task Start_一本坏书不阻塞队列()
    {
        await using var sandbox = await NewSandboxAsync();
        var good1 = sandbox.WriteEpub(new EpubBuilder { Title = "好书一", ChapterCount = 2 }, "g1.epub");
        var good2 = sandbox.WriteEpub(new EpubBuilder { Title = "好书二", ChapterCount = 4 }, "g2.epub");
        var broken = Path.Combine(sandbox.SourceDir, "broken.epub");
        File.WriteAllBytes(broken, "这不是一个 zip 文件"u8.ToArray());

        var resolver = new ScriptedResolver(DuplicatePolicy.SkipAll);
        await using var queue = BuildQueue(sandbox, resolver);
        var batch = await queue.StartAsync(
            new[] { good1, broken, good2 }, new ImportQueueOptions { PromptOnDuplicates = false });
        await queue.WaitForCompletionAsync();

        Assert.Equal(3, queue.CompletedCount);
        // 扫描器会按路径排序，所以 jobs[0] 其实是 broken.epub（别按入参顺序断言）。
        var byName = queue.Jobs.ToDictionary(j => j.FileName, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ImportOutcome.Imported, byName["g1.epub"].Outcome);
        Assert.Equal(ImportOutcome.Imported, byName["g2.epub"].Outcome);
        Assert.True(byName["broken.epub"].IsTerminal);
        Assert.NotEqual(ImportOutcome.Imported, byName["broken.epub"].Outcome);
        Assert.False(string.IsNullOrWhiteSpace(byName["broken.epub"].Message));
        Assert.Equal(2, batch.Succeeded);
        Assert.Equal(1, batch.Failed);
        Assert.Equal(2, await sandbox.BookCountAsync());
    }

    [Fact]
    public async Task Cancel_完成当前这本后停_未开始的标记取消且没进库()
    {
        await using var sandbox = await NewSandboxAsync();
        var files = WriteBooks(sandbox, 6);
        var resolver = new ScriptedResolver(DuplicatePolicy.SkipAll);
        await using var queue = BuildQueue(sandbox, resolver);

        // 第一本一成功就取消：事件处理器跑在 Worker 线程上、且在 RunOneAsync 返回之前，
        // 所以"下一本会不会被跳过"是确定性的，不靠 sleep 猜。
        queue.JobStateChanged += (_, job) =>
        {
            if (job.State == ImportJobState.Finished && job.Outcome == ImportOutcome.Imported)
            {
                queue.Cancel();
            }
        };

        var batch = await queue.StartAsync(files, new ImportQueueOptions { PromptOnDuplicates = false });
        await queue.WaitForCompletionAsync();

        Assert.Equal(6, queue.CompletedCount);
        Assert.All(queue.Jobs, j => Assert.True(j.IsTerminal));

        var imported = queue.Jobs.Count(j => j.Outcome == ImportOutcome.Imported);
        var canceled = queue.Jobs.Count(j => j.State == ImportJobState.Canceled);
        Assert.Equal(1, imported);
        Assert.Equal(5, canceled);
        Assert.Contains("用户取消", batch.Note);

        // 未开始的书一本都不能动：库里既没有书，也不该留下文件。
        Assert.Equal(imported, await sandbox.BookCountAsync());
        Assert.Equal(imported, sandbox.LibraryFiles().Length);
    }

    [Fact]
    public async Task Pause_暂停后不再推进_Resume继续跑完()
    {
        await using var sandbox = await NewSandboxAsync();
        var files = WriteBooks(sandbox, 5);
        var resolver = new ScriptedResolver(DuplicatePolicy.SkipAll);
        await using var queue = BuildQueue(sandbox, resolver);

        // 只暂停一次：处理器对每一本结束都会触发，不设标记的话 Resume 之后
        // 下一本一跑完又会把队列按回暂停，WaitForCompletionAsync 永远等不到头。
        var pausedOnce = false;
        queue.JobStateChanged += (_, job) =>
        {
            if (job.IsTerminal && !pausedOnce)
            {
                pausedOnce = true;
                queue.Pause();
            }
        };

        await queue.StartAsync(files, new ImportQueueOptions { PromptOnDuplicates = false });
        Assert.True(await WaitUntilAsync(() => queue.State == ImportQueueState.Paused),
            "队列没能在第一本结束后进入暂停态，实际状态：" + queue.State);

        var atPause = queue.CompletedCount;
        await Task.Delay(400);
        Assert.Equal(atPause, queue.CompletedCount);

        queue.Resume();
        await queue.WaitForCompletionAsync();

        Assert.Equal(ImportQueueState.Finished, queue.State);
        Assert.Equal(5, queue.CompletedCount);
        Assert.All(queue.Jobs, j => Assert.Equal(ImportOutcome.Imported, j.Outcome));
        Assert.Equal(5, await sandbox.BookCountAsync());
    }

    [Fact]
    public async Task Start_批次没结束时再启动_抛InvalidOperationException()
    {
        await using var sandbox = await NewSandboxAsync();
        var files = WriteBooks(sandbox, 8);
        var resolver = new ScriptedResolver(DuplicatePolicy.SkipAll);
        await using var queue = BuildQueue(sandbox, resolver);

        var options = new ImportQueueOptions { PromptOnDuplicates = false };
        await queue.StartAsync(files, options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StartAsync(files, options));

        await queue.WaitForCompletionAsync();
        Assert.Equal(ImportQueueState.Finished, queue.State);
    }

    [Fact]
    public async Task Start_一个有效文件都没有_抛ArgumentException并回到Idle()
    {
        await using var sandbox = await NewSandboxAsync();
        var resolver = new ScriptedResolver(DuplicatePolicy.SkipAll);
        await using var queue = BuildQueue(sandbox, resolver);

        var ghost = Path.Combine(sandbox.Root, "没有这个目录");

        await Assert.ThrowsAsync<ArgumentException>(
            () => queue.StartAsync(new[] { ghost }, ImportQueueOptions.Default));

        Assert.Equal(ImportQueueState.Idle, queue.State);
        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public async Task Start_逐项决定_只导入用户点选的那几本()
    {
        await using var sandbox = await NewSandboxAsync();
        var first = sandbox.WriteEpub(new EpubBuilder { Title = "重复一", ChapterCount = 2 }, "d1.epub");
        var second = sandbox.WriteEpub(new EpubBuilder { Title = "重复二", ChapterCount = 4 }, "d2.epub");
        var third = sandbox.WriteEpub(new EpubBuilder { Title = "重复三", ChapterCount = 6 }, "d3.epub");

        foreach (var file in new[] { first, second, third })
        {
            var result = await sandbox.Pipeline.ImportAsync(file, ImportOptions.Default);
            Assert.Equal(ImportOutcome.Imported, result.Outcome);
        }

        // 只对第 2 本回答"仍要导入"。
        var resolver = new ScriptedResolver(DuplicatePolicy.AskEach, (_, index) => index == 2);
        await using var queue = BuildQueue(sandbox, resolver);
        await queue.StartAsync(
            new[] { first, second, third }, new ImportQueueOptions { PromptOnDuplicates = true });
        await queue.WaitForCompletionAsync();

        Assert.Equal(1, resolver.BatchCalls);
        Assert.Equal(3, resolver.SingleCalls);
        Assert.Equal(ImportOutcome.Duplicate, queue.Jobs[0].Outcome);
        Assert.Equal(ImportOutcome.Imported, queue.Jobs[1].Outcome);
        Assert.Equal(ImportOutcome.Duplicate, queue.Jobs[2].Outcome);
        Assert.Equal(4, await sandbox.BookCountAsync());
    }
}
