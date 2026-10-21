using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Infrastructure.Data.Repositories;
using EbookToolSuite.Services.Conversion;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests.Conversion;

/// <summary>
/// 转换队列的行为测试（执行计划 T2-4）。
/// 用假的 IConversionService 顶替 Calibre：队列的价值在排队、限并发、重试、取消、
/// 崩溃恢复，这些不需要真跑 Calibre 才能验，而且这样才跑得快（秒级）。
/// 「真跑 Calibre」那部分由 build\smoke-convert-queue.ps1 负责。
/// </summary>
public sealed class ConversionQueueTests
{
    /// <summary>退避改成 30ms，否则一条重试用例要等 5 秒。</summary>
    private static ConversionQueueOptions FastOptions => new()
    {
        MaxParallel = 1,
        MaxRetries = 2,
        TimeoutMaxRetries = 1,
        RetryDelays = new[] { TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30) },
        PollInterval = TimeSpan.FromMilliseconds(10),
    };

    private sealed class FakeConversionService : IConversionService
    {
        private readonly object _gate = new();

        public int Calls { get; private set; }

        /// <summary>真实调用顺序（按 BookId），给优先级测试用。</summary>
        public List<Guid> BookOrder { get; } = new();

        /// <summary>返回非 null 就抛它。第二个参数是第几次调用（从 1 起）。</summary>
        public Func<ConversionRequest, int, Exception?>? FailWhen { get; set; }

        /// <summary>卡住用（模拟长转换，给取消留窗口）。</summary>
        public Func<ConversionRequest, CancellationToken, Task>? Block { get; set; }

        public Task<ConversionResult> ConvertAsync(
            ConversionRequest request,
            IProgress<ConversionProgress>? progress = null,
            CancellationToken ct = default,
            IProgress<ProcessOutputLine>? rawOutput = null)
        {
            int call;
            lock (_gate)
            {
                Calls++;
                call = Calls;
                BookOrder.Add(request.BookId);
            }

            rawOutput?.Report(new ProcessOutputLine(
                DateTimeOffset.UtcNow, false, "第 " + call + " 次尝试的输出"));
            rawOutput?.Report(new ProcessOutputLine(DateTimeOffset.UtcNow, true, "这一行来自 stderr"));
            progress?.Report(new ConversionProgress { Percent = 0.5, StageText = "半路" });

            return RunAsync(request, call, ct);
        }

        private async Task<ConversionResult> RunAsync(
            ConversionRequest request, int call, CancellationToken ct)
        {
            if (Block is not null)
            {
                await Block(request, ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();

            var failure = FailWhen?.Invoke(request, call);
            if (failure is not null)
            {
                throw failure;
            }

            var directory = request.OutputDirectory ?? Path.GetTempPath();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "队列测试书" + request.TargetFormat.ToExtension());
            await File.WriteAllTextAsync(path, "converted", ct).ConfigureAwait(false);

            return new ConversionResult(
                path, 9, TimeSpan.FromMilliseconds(5), request.TargetFormat, request.TemplateId, "fake");
        }

        public IReadOnlyList<ConversionTemplate> GetTemplates(BookFormatKind targetFormat)
            => Array.Empty<ConversionTemplate>();

        public bool IsSupported(BookFormatKind source, BookFormatKind target) => true;

        public IReadOnlyList<BookFormatKind> GetSupportedTargets(BookFormatKind source)
            => new[] { BookFormatKind.Epub };

        /// <summary>队列只做「换格式」，不涉及 polish，所以这里恒为空。</summary>
        public IReadOnlyList<ConversionTemplate> GetFormatKeepingTemplates(BookFormatKind sourceFormat)
            => Array.Empty<ConversionTemplate>();
    }

    /// <summary>造一本书：源文件真写一个（PickSource 会检查它存在），格式行直接进库。</summary>
    private static async Task<Book> SeedBookAsync(
        ImportSandbox sb, string title, BookFormatKind kind = BookFormatKind.Epub)
    {
        var path = Path.Combine(sb.SourceDir, title + kind.ToExtension());
        await File.WriteAllTextAsync(path, "source bytes").ConfigureAwait(false);

        var now = SystemClock.Instance.Now;
        var book = new Book { Id = Guid.NewGuid(), Title = title, CreatedAt = now, UpdatedAt = now };
        book.Formats.Add(new BookFormat
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            Format = kind,
            FilePath = path,
            FileSize = 12,
            AddedAt = now,
        });

        await sb.Repository.AddAsync(book).ConfigureAwait(false);
        return book;
    }

    private static ConversionQueue CreateQueue(ImportSandbox sb, IConversionService conversion)
        => new(
            new ConversionTaskRepository(sb.Factory),
            conversion,
            sb.Repository,
            sb.Paths,
            SystemClock.Instance,
            FastOptions,
            NullLogger<ConversionQueue>.Instance);

    private static ConversionRequest Request(Guid bookId, BookFormatKind target = BookFormatKind.Mobi)
        => new() { BookId = bookId, TargetFormat = target };

    /// <summary>等某个条件成立，超时就失败。给"等它跑到运行中"这类断言用。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        Assert.Fail("等不到：" + what);
    }

    [Fact]
    public async Task 入队后自动跑完_状态完成_并落库()
    {
        await using var sb = new ImportSandbox();
        await sb.InitializeAsync();
        var book = await SeedBookAsync(sb, "队列测试书");
        var fake = new FakeConversionService();
        await using var queue = CreateQueue(sb, fake);
        await queue.StartAsync();

        var task = await queue.EnqueueAsync(Request(book.Id));
        await queue.WaitForIdleAsync();

        Assert.Equal(ConversionTaskStatus.Completed, task.Status);
        Assert.Equal(1d, task.Progress);
        Assert.Equal(1, fake.Calls);
        Assert.True(File.Exists(task.OutputPath), "产物应该真的落盘了");
        Assert.False(queue.IsBusy);

        // 「查看日志」拿到的是完整输出（验收判据 f 的底层能力）
        var output = queue.GetTaskOutput(task.Id);
        Assert.NotNull(output);
        Assert.Contains("第 1 次尝试的输出", output!);
        Assert.Contains("stderr", output!);

        var stored = await new ConversionTaskRepository(sb.Factory).GetByIdAsync(task.Id);
        Assert.NotNull(stored);
        Assert.Equal(ConversionTaskStatus.Completed, stored!.Status);
        Assert.Equal(task.OutputPath, stored.OutputPath);
    }

    [Fact]
    public async Task 高优先级任务插队_且RegisterFormat透传入库()
    {
        await using var sb = new ImportSandbox();
        await sb.InitializeAsync();

        var bookA = await SeedBookAsync(sb, "低优先级A");
        var bookB = await SeedBookAsync(sb, "高优先级B");
        var bookGate = await SeedBookAsync(sb, "挡路的C");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = 0;

        var fake = new FakeConversionService
        {
            Block = async (_, ct) =>
            {
                if (Interlocked.Increment(ref call) == 1)
                {
                    started.TrySetResult();
                    await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            },
        };

        await using var queue = CreateQueue(sb, fake);
        await queue.StartAsync();

        // C 占住唯一槽位；A、B 只能老老实实排队。
        await queue.EnqueueAsync(Request(bookGate.Id));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var low = await queue.EnqueueAsync(Request(bookA.Id));
        var high = await queue.EnqueueAsync(new ConversionRequest
        {
            BookId = bookB.Id,
            TargetFormat = BookFormatKind.Mobi,
            Priority = 1000,
            RegisterFormat = false,
        });

        gate.TrySetResult();
        await queue.WaitForIdleAsync();

        // T6-1 判据 5：A 先入队，但 B 优先级高，必须先跑。
        Assert.Equal(new[] { bookGate.Id, bookB.Id, bookA.Id }, fake.BookOrder);
        Assert.Equal(0, low.Priority);
        Assert.Equal(1000, high.Priority);
        Assert.False(high.RegisterFormat);

        // 两个字段都要落库，崩溃重启后才不会把缓存产物登记成书库格式。
        var stored = await new ConversionTaskRepository(sb.Factory).GetByIdAsync(high.Id);
        Assert.NotNull(stored);
        Assert.Equal(1000, stored!.Priority);
        Assert.False(stored.RegisterFormat);
    }

    [Fact]
    public async Task 同一本书同一格式_重复入队被拒()
    {
        await using var sb = new ImportSandbox();
        await sb.InitializeAsync();
        var book = await SeedBookAsync(sb, "队列测试书");
        var fake = new FakeConversionService
        {
            Block = async (_, ct) => await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false),
        };
        await using var queue = CreateQueue(sb, fake);
        await queue.StartAsync();

        var first = await queue.EnqueueAsync(Request(book.Id));
        await WaitUntilAsync(
            () => first.Status == ConversionTaskStatus.Running, "第一条进入运行中");

        var ex = await Assert.ThrowsAsync<AppException>(
            () => queue.EnqueueAsync(Request(book.Id)));
        Assert.Equal(ErrorCode.ConversionOutputExists, ex.Code);

        // 换个目标格式就能排进去
        var other = await queue.EnqueueAsync(Request(book.Id, BookFormatKind.Pdf));
        Assert.NotEqual(first.Id, other.Id);

        await queue.CancelAllAsync();
    }

    [Fact]
    public async Task 磁盘满_一条也不重试_直接失败()
    {
        await using var sb = new ImportSandbox();
        await sb.InitializeAsync();
        var book = await SeedBookAsync(sb, "队列测试书");
        var fake = new FakeConversionService
        {
            FailWhen = (_, _) => new AppException(
                ErrorCode.DiskFull,
                "目标位置空间不够：这次大约需要 3.00 GB，当前只剩 1.00 GB。",
                technical: "DiskSpaceGuard required=3221225472 available=1073741824"),
        };
        await using var queue = CreateQueue(sb, fake);
        await queue.StartAsync();

        var task = await queue.EnqueueAsync(Request(book.Id));
        await queue.WaitForIdleAsync();

        Assert.Equal(ConversionTaskStatus.Failed, task.Status);
        Assert.Equal(1, fake.Calls);          // 关键：磁盘满绝不重试
        Assert.Equal(0, task.RetryCount);
        Assert.Equal("DiskFull", task.ErrorCode);
        Assert.NotNull(task.ErrorMessage);
    }

    [Fact]
    public async Task 文件被占用_自动重试后成功()
    {
        await using var sb = new ImportSandbox();
        await sb.InitializeAsync();
        var book = await SeedBookAsync(sb, "队列测试书");
        var fake = new FakeConversionService
        {
            FailWhen = (_, call) => call == 1
                ? new AppException(
                    ErrorCode.ConversionFailed,
                    "转换进程出错了，可能是文件正被别的程序占用。",
                    technical: "ebook-convert: Permission denied")
                : null,
        };
        await using var queue = CreateQueue(sb, fake);
        await queue.StartAsync();

        var task = await queue.EnqueueAsync(Request(book.Id));
        await queue.WaitForIdleAsync();

        Assert.Equal(ConversionTaskStatus.Completed, task.Status);
        Assert.Equal(2, fake.Calls);          // 第一次失败，第二次成功
        Assert.Equal(1, task.RetryCount);
    }

    [Fact]
    public async Task 取消运行中的任务_进已取消_且不再重试()
    {
        await using var sb = new ImportSandbox();
        await sb.InitializeAsync();
        var book = await SeedBookAsync(sb, "队列测试书");
        var fake = new FakeConversionService
        {
            Block = async (_, ct) => await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false),
        };
        await using var queue = CreateQueue(sb, fake);
        await queue.StartAsync();

        var task = await queue.EnqueueAsync(Request(book.Id));
        await WaitUntilAsync(() => task.Status == ConversionTaskStatus.Running, "进入运行中");

        await queue.CancelAsync(task.Id);
        await queue.WaitForIdleAsync();

        Assert.Equal(ConversionTaskStatus.Canceled, task.Status);
        Assert.False(queue.IsBusy);
        Assert.Equal(1, fake.Calls);   // 用户主动取消，不是失败，不该重试
    }

    [Fact]
    public async Task 全部取消_所有任务立刻进终态_队列不再忙()
    {
        await using var sb = new ImportSandbox();
        await sb.InitializeAsync();
        var fake = new FakeConversionService
        {
            Block = async (_, ct) => await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false),
        };
        await using var queue = CreateQueue(sb, fake);
        await queue.StartAsync();

        for (var i = 0; i < 3; i++)
        {
            var book = await SeedBookAsync(sb, "队列测试书" + i);
            await queue.EnqueueAsync(Request(book.Id));
        }

        await WaitUntilAsync(() => queue.RunningCount == 1, "有一条在跑");

        var canceled = await queue.CancelAllAsync();

        Assert.Equal(3, canceled);
        Assert.All(queue.Tasks, t => Assert.True(t.IsTerminal));
        Assert.False(queue.IsBusy);
    }

    [Fact]
    public async Task 崩溃恢复_库里残留的运行中任务_启动后接着跑完()
    {
        await using var sb = new ImportSandbox();
        await sb.InitializeAsync();
        var book = await SeedBookAsync(sb, "队列测试书");
        var repo = new ConversionTaskRepository(sb.Factory);
        var now = SystemClock.Instance.Now;
        var sourcePath = book.Formats[0].FilePath;

        // 模拟「上次程序在转换中途被强杀」：库里留一条 Running，进度停在 67%
        var orphan = new ConversionTask
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            InputPath = sourcePath,
            OutputPath = Path.Combine(
                Path.GetDirectoryName(sourcePath)!, "队列测试书.mobi"),
            OutFormat = BookFormatKind.Mobi,
            Status = ConversionTaskStatus.Running,
            Progress = 0.67,
            ProgressKind = ConversionTaskProgressKind.Percent,
            CreatedAt = now,
            StartedAt = now,
        };
        await repo.AddAsync(orphan);

        var fake = new FakeConversionService();
        await using var queue = CreateQueue(sb, fake);
        await queue.StartAsync();    // 这一步要把它归位成排队中，并接着跑
        await queue.WaitForIdleAsync();

        /* 【2026-10-09 T4-7 复核项】这里原来是"WaitForIdleAsync 之后直接断言 Completed"，
           三个测试工程并行跑时会偶发拿到 Running  队列报"空闲"与写库落定之间差一个拍子
           （单独跑 Services 时 211/211 全过）。改成轮询等待，不再赌时序。 */
        var after = await WaitForStatusAsync(repo, orphan.Id, ConversionTaskStatus.Completed);

        Assert.NotNull(after);
        Assert.Equal(ConversionTaskStatus.Completed, after!.Status);
        Assert.Equal(1d, after.Progress);    // 进度被重置过，不是残留的 0.67
        Assert.Equal(1, fake.Calls);
    }

    /// <summary>轮询等某个任务落到目标状态（最多 5 秒）。返回最后一次读到的值，便于断言里看实际状态。</summary>
    private static async Task<ConversionTask?> WaitForStatusAsync(
        ConversionTaskRepository repo, Guid id, ConversionTaskStatus wanted)
    {
        var deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(5);
        ConversionTask? current = null;

        while (DateTimeOffset.Now < deadline)
        {
            current = await repo.GetByIdAsync(id);

            if (current?.Status == wanted)
            {
                return current;
            }

            await Task.Delay(50);
        }

        return current;
    }

    [Fact]
    public async Task 重试失败的任务_回到排队并再跑一次_清除已完成只清终态()
    {
        await using var sb = new ImportSandbox();
        await sb.InitializeAsync();
        var book = await SeedBookAsync(sb, "队列测试书");
        var fake = new FakeConversionService
        {
            FailWhen = (_, _) => new AppException(
                ErrorCode.ConversionArgsRejected, "参数不被支持。", technical: "invalid choice"),
        };
        await using var queue = CreateQueue(sb, fake);
        await queue.StartAsync();

        var task = await queue.EnqueueAsync(Request(book.Id));
        await queue.WaitForIdleAsync();
        Assert.Equal(ConversionTaskStatus.Failed, task.Status);
        Assert.Equal(1, fake.Calls);

        // 用户点「重试」：不能再失败一次，这次让它成功
        fake.FailWhen = null;
        await queue.RetryAsync(task.Id);
        await queue.WaitForIdleAsync();

        Assert.Equal(ConversionTaskStatus.Completed, task.Status);
        Assert.Equal(2, fake.Calls);
        Assert.Equal(0, task.RetryCount);    // 重试会把计数归零
        Assert.Null(task.ErrorCode);

        // 清除已完成：终态的清掉，队列空了
        var cleared = await queue.ClearFinishedAsync();
        Assert.Equal(1, cleared);
        Assert.Empty(queue.Tasks);
    }
}
