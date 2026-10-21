using System.Text;
using System.Text.Json;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Conversion;

/// <summary>
/// 转换队列（执行计划 T2-4）。单例。
/// 形态说明：不继承 BackgroundService，而是自己起一个 worker 循环
/// 与 ImportQueue 保持同一种风格，也免得把 IHost 生命周期牵扯进来。
/// 真正的转换工作全部复用 ConversionService.ConvertAsync，这里只管排队、限并发、重试、取消。
/// </summary>
public sealed class ConversionQueue : IConversionQueue, IAsyncDisposable
{
    private readonly IConversionTaskRepository _repository;
    private readonly IConversionService _conversion;
    private readonly IBookRepository _books;
    private readonly IAppPaths _paths;
    private readonly IClock _clock;
    private readonly ConversionQueueOptions _options;
    private readonly ILogger<ConversionQueue>? _log;

    private readonly List<ConversionTask> _tasks = new();
    private readonly Dictionary<Guid, DateTimeOffset> _retryAt = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _cancellations = new();
    private readonly Dictionary<Guid, LineCollector> _outputs = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _slots;

    private Task? _worker;
    private CancellationTokenSource? _shutdown;
    private bool _disposed;

    public ConversionQueue(
        IConversionTaskRepository repository,
        IConversionService conversion,
        IBookRepository books,
        IAppPaths paths,
        IClock clock,
        ConversionQueueOptions? options = null,
        ILogger<ConversionQueue>? log = null)
    {
        _repository = repository;
        _conversion = conversion;
        _books = books;
        _paths = paths;
        _clock = clock;
        _options = options ?? ConversionQueueOptions.Default;
        _slots = new SemaphoreSlim(
            Math.Max(1, _options.MaxParallel), Math.Max(1, _options.MaxParallel));
        _log = log;
    }

    public event Action<ConversionTask>? TaskChanged;

    public event Action? TasksChanged;

    /// <summary>快照：返回副本，UI 拿去绑定不会与后台线程抢集合。</summary>
    public IReadOnlyList<ConversionTask> Tasks
    {
        get
        {
            lock (_gate)
            {
                return _tasks.ToArray();
            }
        }
    }

    public int QueuedCount
    {
        get
        {
            lock (_gate)
            {
                return _tasks.Count(t => t.Status is ConversionTaskStatus.Queued
                    or ConversionTaskStatus.WaitingRetry);
            }
        }
    }

    public int RunningCount
    {
        get
        {
            lock (_gate)
            {
                return _tasks.Count(t => t.Status == ConversionTaskStatus.Running);
            }
        }
    }

    public bool IsBusy => QueuedCount > 0 || RunningCount > 0;

    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_worker is not null)
            {
                return;   // 幂等：重复调也只起一个 worker
            }

            _shutdown = new CancellationTokenSource();
        }

        // 崩溃恢复：上次留下的 Running / WaitingRetry 一律回排队。
        // 绝不续传（Calibre 没有断点续传），半截产物由 RunOneAsync 开跑前清掉。
        var reset = await _repository.ResetInterruptedAsync(ct).ConfigureAwait(false);
        if (reset > 0)
        {
            _log?.LogInformation(
                "队列启动：{Count} 条上次没跑完的任务已归位成排队中，将接着转", reset);
        }

        var active = await _repository.ListActiveAsync(ct).ConfigureAwait(false);
        var recent = await _repository.ListRecentAsync(200, ct).ConfigureAwait(false);

        lock (_gate)
        {
            // 合并而不是清空重载：启动过程中若有新任务入队，别把它们冲掉
            foreach (var t in active.Concat(recent))
            {
                if (!_tasks.Any(x => x.Id == t.Id))
                {
                    _tasks.Add(t);   // 终态的历史也载进来，用户要看得见上次为什么失败
                }
            }

            _tasks.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        }

        TasksChanged?.Invoke();
        SweepStaleRunDirectories();

        var worker = Task.Run(() => WorkerLoopAsync(_shutdown.Token));
        lock (_gate)
        {
            _worker = worker;
        }
    }

    /// <summary>
    /// 清掉上次崩溃留下的转换临时目录（Temp/calibre/conv-*）。
    /// 只删 24 小时以前的：正在跑的那次可能就在几十秒前建的，别误删。
    /// 失败不影响队列，纯粹是磁盘卫生。
    /// </summary>
    private void SweepStaleRunDirectories()
    {
        try
        {
            var root = Path.Combine(_paths.TempDir, "calibre");
            if (!Directory.Exists(root))
            {
                return;
            }

            var cutoff = _clock.Now.AddHours(-24);
            var removed = 0;
            foreach (var dir in Directory.EnumerateDirectories(root, "conv-*"))
            {
                try
                {
                    var created = new DateTimeOffset(
                        Directory.GetCreationTimeUtc(dir), TimeSpan.Zero);
                    if (created < cutoff)
                    {
                        Directory.Delete(dir, recursive: true);
                        removed++;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            if (removed > 0)
            {
                _log?.LogInformation("清理了 {Count} 个过期的转换临时目录", removed);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "清理过期的转换临时目录失败（不影响队列）");
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        _log?.LogInformation("转换队列已启动（并发 {Max}）", _options.MaxParallel);

        while (!ct.IsCancellationRequested)
        {
            // 必须【先抢槽位、再取任务】。反过来的话，抢不到槽位的那条会被标成「运行中」
            // 但其实还没跑，用户这时点取消会找不到它的取消令牌，取消就丢了。
            try
            {
                await _slots.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var task = TakeNext();
            if (task is null)
            {
                _slots.Release();
                try
                {
                    await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            // 故意不 await：槽位已经拿到了，worker 立刻回去抢下一个槽位
            _ = RunOneAsync(task, ct);
        }
    }


    /// <summary>取下一条能跑的：先排队中的，再退避时间已到的等待重试。按创建时间升序。</summary>
    private ConversionTask? TakeNext()
    {
        lock (_gate)
        {
            var now = _clock.Now;

            // T6-1 判据 5：优先级高的先跑（数字大 = 更急），同级按创建时间升序。
            // 用户点开 MOBI 触发的转换 priority=1000，会插到 20 条批量转换（priority=0）前面。
            ConversionTask? best = null;
            foreach (var t in _tasks)
            {
                var runnable = t.Status == ConversionTaskStatus.Queued
                    || (t.Status == ConversionTaskStatus.WaitingRetry
                        && _retryAt.TryGetValue(t.Id, out var due)
                        && now >= due);

                if (!runnable)
                {
                    continue;
                }

                if (best is null
                    || t.Priority > best.Priority
                    || (t.Priority == best.Priority && t.CreatedAt < best.CreatedAt))
                {
                    best = t;
                }
            }

            if (best is not null)
            {
                _retryAt.Remove(best.Id);
                best.Status = ConversionTaskStatus.Running;
            }

            return best;
        }
    }

    private async Task RunOneAsync(ConversionTask task, CancellationToken ct)
    {
        var collector = new LineCollector();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        lock (_gate)
        {
            _cancellations[task.Id] = linked;
            _outputs[task.Id] = collector;
            task.StartedAt = _clock.Now;
            task.FinishedAt = null;
            task.Progress = null;
            task.ProgressKind = ConversionTaskProgressKind.Unknown;
            task.ErrorCode = null;
            task.ErrorMessage = null;
        }

        Raise(task);

        try
        {
            await PersistQuietAsync(task).ConfigureAwait(false);

            // 故意不删 task.OutputPath：那只是「计划」路径，上面可能有用户自己的同名文件。
            // ConversionService 的产物先落在 Temp/calibre/conv-*/ 里，最后才同卷改名，
            // 所以正式路径上不会出现半截产物；重名由 MakeOutputPath 加序号解决。
            // 崩溃留下的临时目录由 SweepStaleRunDirectories 负责清。

            var progress = new Progress<ConversionProgress>(p => OnProgress(task, p));
            var result = await _conversion
                .ConvertAsync(BuildRequest(task), progress, linked.Token, collector)
                .ConfigureAwait(false);

            lock (_gate)
            {
                if (task.Status != ConversionTaskStatus.Running)
                {
                    return;   // 取消抢先了，这次的结果就不要了
                }

                task.OutputPath = result.OutputPath;
                task.Progress = 1d;
                task.ProgressKind = ConversionTaskProgressKind.Percent;
                task.Status = ConversionTaskStatus.Completed;
                task.FinishedAt = _clock.Now;
            }

            await PersistQuietAsync(task).ConfigureAwait(false);
            Raise(task);
            _log?.LogInformation(
                "队列：转换完成 {Id} -> {Output}", task.Id, task.OutputPath);
        }
        catch (OperationCanceledException)
        {
            await MarkCanceledAsync(task).ConfigureAwait(false);
        }
        catch (AppException ex)
        {
            await HandleFailureAsync(task, ex).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(task, new AppException(
                ErrorCode.ConversionFailed,
                "转换过程中出现未预期的错误。可以在活动中心点查看日志了解详情。",
                technical: ex.ToString(),
                inner: ex)).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _cancellations.Remove(task.Id);
            }

            _slots.Release();
        }
    }

    private void OnProgress(ConversionTask task, ConversionProgress p)
    {
        lock (_gate)
        {
            if (task.Status != ConversionTaskStatus.Running)
            {
                return;   // 迟到的回调，别把已经进终态的任务改回去
            }

            task.Progress = p.Percent;
            task.ProgressKind = p.Percent is null
                ? ConversionTaskProgressKind.Unknown
                : ConversionTaskProgressKind.Percent;
        }

        Raise(task);
    }

    private async Task MarkCanceledAsync(ConversionTask task)
    {
        lock (_gate)
        {
            task.Status = ConversionTaskStatus.Canceled;
            task.Progress = null;
            task.ProgressKind = ConversionTaskProgressKind.Unknown;
            task.ErrorCode = null;
            task.ErrorMessage = null;
            task.FinishedAt = _clock.Now;
        }

        await PersistQuietAsync(task).ConfigureAwait(false);
        Raise(task);
        _log?.LogInformation("队列：转换已取消 {Id}", task.Id);
    }

    /// <summary>失败收尾：能重试就进等待重试（退避到点后由 TakeNext 再取），否则直接判失败。</summary>
    private async Task HandleFailureAsync(ConversionTask task, AppException ex)
    {
        var canRetry = ConversionRetryPolicy.ShouldRetry(
            ex.Code, Describe(ex), task.RetryCount, _options);

        if (canRetry)
        {
            task.RetryCount++;
            var delay = _options.DelayFor(task.RetryCount);
            lock (_gate)
            {
                task.Status = ConversionTaskStatus.WaitingRetry;
                task.ErrorCode = ex.Code.ToString();
                task.ErrorMessage = ex.UserMessage;
                _retryAt[task.Id] = _clock.Now + delay;
            }

            await PersistQuietAsync(task).ConfigureAwait(false);
            Raise(task);
            _log?.LogWarning(
                "队列：{Id} 第 {Count} 次失败（{Code}），{Seconds} 秒后重试：{Message}",
                task.Id, task.RetryCount, ex.Code, delay.TotalSeconds, ex.UserMessage);
            return;
        }

        lock (_gate)
        {
            task.Status = ConversionTaskStatus.Failed;
            task.ErrorCode = ex.Code.ToString();
            task.ErrorMessage = ex.UserMessage;
            task.FinishedAt = _clock.Now;
        }

        await PersistQuietAsync(task).ConfigureAwait(false);
        Raise(task);
        _log?.LogError(ex, "队列：转换失败 {Id}（{Code}）", task.Id, ex.Code);
    }

    /// <summary>给重试判定用的文本：技术消息 + 上下文里的字符串（Calibre 的 stdout/stderr 尾部）。</summary>
    private static string Describe(AppException ex)
    {
        var builder = new StringBuilder(ex.Message);
        foreach (var value in ex.Context.Values)
        {
            if (value is string s)
            {
                builder.Append((char)10).Append(s);
            }
        }

        return builder.ToString();
    }

    private ConversionRequest BuildRequest(ConversionTask task) => new()
    {
        BookId = task.BookId ?? Guid.Empty,
        TargetFormat = task.OutFormat,
        TemplateId = task.TemplateId,
        // 源格式传 null：由 ConversionService 自己再解析一次，它是唯一权威。
        // 队列入队时也解析了一回，但那只为了让界面立刻显示源格式到目标格式。
        SourceFormat = null,
        OutputDirectory = Path.GetDirectoryName(task.OutputPath),
        Overrides = ParseArguments(task.ArgumentsJson),
        RegisterFormat = task.RegisterFormat,
    };

    private static IReadOnlyList<ConversionArgument> ParseArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
        {
            return Array.Empty<ConversionArgument>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<ConversionArgument>>(json)
                ?? new List<ConversionArgument>();
        }
        catch (JsonException)
        {
            return Array.Empty<ConversionArgument>();
        }
    }

    private static string SerializeArguments(IReadOnlyList<ConversionArgument>? overrides)
        => overrides is null || overrides.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(overrides);

    /// <summary>
    /// 挑源文件。规则与 ConversionService 保持一致：指定了就用指定的（但文件得在），
    /// 没指定就按 epub -> txt -> fb2 -> mobi -> azw3 -> pdf -> cbz 的顺序挑第一个存在的。
    /// 这里重复了一遍规则，只为了入队时就能把源路径与源格式落进任务行。
    /// </summary>
    private static (string Path, BookFormatKind Kind) PickSource(
        Book book, BookFormatKind? requested)
    {
        if (requested is { } want)
        {
            var hit = book.Formats.FirstOrDefault(
                f => f.Format == want && !f.Missing && File.Exists(f.FilePath));
            if (hit is not null)
            {
                return (hit.FilePath, hit.Format);
            }
        }

        foreach (var kind in SourceOrder)
        {
            var hit = book.Formats.FirstOrDefault(
                f => f.Format == kind && !f.Missing && File.Exists(f.FilePath));
            if (hit is not null)
            {
                return (hit.FilePath, hit.Format);
            }
        }

        throw new AppException(
            ErrorCode.FileNotFound,
            "这本书的原始文件都不在了，可能被移动或删除了。请在书库里检查后重试。",
            technical: $"no readable source: book={book.Id}");
    }

    private static readonly BookFormatKind[] SourceOrder =
    [
        BookFormatKind.Epub,
        BookFormatKind.Txt,
        BookFormatKind.Fb2,
        BookFormatKind.Mobi,
        BookFormatKind.Azw3,
        BookFormatKind.Pdf,
        BookFormatKind.Cbz,
    ];

    public async Task<ConversionTask> EnqueueAsync(
        ConversionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureStartedAsync(ct).ConfigureAwait(false);

        var book = await _books.GetByIdAsync(request.BookId, ct).ConfigureAwait(false)
            ?? throw new AppException(
                ErrorCode.FileNotFound,
                "找不到这本书的记录，可能已被移除。请在书库里刷新后重试。",
                technical: $"book not found: {request.BookId}");

        var (inputPath, sourceKind) = PickSource(book, request.SourceFormat);

        // 「不换格式，只优化」（polish）的产物就是源格式本身，所以那道查重闸对它必然为真。
        // 这一类请求必须放行，否则 polish 永远入不了队。
        var keepsFormat = IsKeepsFormatRequest(book, request);

        // 库里已经有这个格式了：入队时就拒掉，别等到跑起来才失败。
        // （ConversionService 里也有一道同样的检查，那是给直接调它的路径兜底的。）
        if (!keepsFormat
            && request.RegisterFormat
            && await _books.HasFormatAsync(book.Id, request.TargetFormat, ct).ConfigureAwait(false))
        {
            throw new AppException(
                ErrorCode.ConversionOutputExists,
                $"这本书已经有 {request.TargetFormat.ToStorageString()} 文件了。"
                    + "要重新转换请先移除该格式，或换一个输出格式。",
                technical: $"format exists: book={book.Id} format={request.TargetFormat}");
        }
        var outputDirectory = request.OutputDirectory
            ?? Path.GetDirectoryName(inputPath)
            ?? _paths.LibraryDir;

        var task = new ConversionTask
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            InputPath = inputPath,
            OutputPath = Path.Combine(
                outputDirectory,
                Path.GetFileNameWithoutExtension(inputPath) + request.TargetFormat.ToExtension()),
            OutFormat = request.TargetFormat,
            TemplateId = request.TemplateId,
            ArgumentsJson = SerializeArguments(request.Overrides),
            Status = ConversionTaskStatus.Queued,
            CreatedAt = _clock.Now,
            BookTitle = book.Title,
            SourceFormat = sourceKind,
            RegisterFormat = request.RegisterFormat,
            Priority = request.Priority,
        };

        lock (_gate)
        {
            var dup = _tasks.FirstOrDefault(t => t.BookId == task.BookId
                && t.OutFormat == task.OutFormat
                && !t.IsTerminal);
            if (dup is not null)
            {
                throw new AppException(
                    ErrorCode.ConversionOutputExists,
                    $"这本书的 {task.OutFormat.ToStorageString()} 转换已经排在队列里了。"
                        + "等它跑完，或者先在活动中心取消那一条。",
                    technical: $"duplicate enqueue: book={task.BookId} "
                        + $"format={task.OutFormat} existing={dup.Id}");
            }

            _tasks.Add(task);
        }

        await _repository.AddAsync(task, ct).ConfigureAwait(false);

        TasksChanged?.Invoke();
        Raise(task);
        return task;
    }

    /// <summary>批量入队。第一条被拒就整体失败（已排进去的留在队列里，不回收）。</summary>
    public async Task<IReadOnlyList<ConversionTask>> EnqueueManyAsync(
        IReadOnlyList<ConversionRequest> requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var added = new List<ConversionTask>(requests.Count);
        foreach (var request in requests)
        {
            added.Add(await EnqueueAsync(request, ct).ConfigureAwait(false));
        }

        return added;
    }

    public async Task<ConversionTask> RetryAsync(Guid taskId, CancellationToken ct = default)
    {
        ConversionTask task;
        lock (_gate)
        {
            task = Find(taskId);
            if (!task.IsTerminal)
            {
                throw new AppException(
                    ErrorCode.ValidationFailed,
                    "这条任务还在队列里没跑完，不用重试。",
                    technical: $"retry on non-terminal: {task.Status}");
            }

            task.Status = ConversionTaskStatus.Queued;
            task.Progress = null;
            task.ProgressKind = ConversionTaskProgressKind.Unknown;
            task.ErrorCode = null;
            task.ErrorMessage = null;
            task.RetryCount = 0;
            task.StartedAt = null;
            task.FinishedAt = null;
            _retryAt.Remove(task.Id);
        }

        await PersistQuietAsync(task).ConfigureAwait(false);
        Raise(task);
        return task;
    }

    public async Task RemoveAsync(Guid taskId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var task = Find(taskId);
            if (!task.IsTerminal)
            {
                throw new AppException(
                    ErrorCode.ValidationFailed,
                    "这条任务还在跑，先取消再移除。",
                    technical: $"remove on non-terminal: {task.Status}");
            }

            _tasks.Remove(task);
            _outputs.Remove(taskId);
            _retryAt.Remove(taskId);
        }

        await _repository.DeleteAsync(taskId, ct).ConfigureAwait(false);
        TasksChanged?.Invoke();
    }

    public async Task<int> ClearFinishedAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var id in _tasks.Where(t => t.IsTerminal).Select(t => t.Id).ToList())
            {
                _outputs.Remove(id);
                _retryAt.Remove(id);
            }

            _tasks.RemoveAll(t => t.IsTerminal);
        }

        var deleted = await _repository.DeleteFinishedAsync(ct).ConfigureAwait(false);
        TasksChanged?.Invoke();
        _log?.LogInformation("队列：清除了 {Count} 条已结束的任务", deleted);
        return deleted;
    }

    private ConversionTask Find(Guid taskId)
        => _tasks.FirstOrDefault(t => t.Id == taskId)
            ?? throw new AppException(
                ErrorCode.FileNotFound,
                "这条转换任务已经不在队列里了。",
                technical: $"task not found: {taskId}");

    public async Task CancelAsync(Guid taskId, CancellationToken ct = default)
    {
        CancellationTokenSource? cts = null;
        ConversionTask task;
        var markDirectly = false;

        lock (_gate)
        {
            task = Find(taskId);
            if (task.IsTerminal)
            {
                return;   // 已经是终态了，没什么可取消的
            }

            if (task.Status == ConversionTaskStatus.Running)
            {
                _cancellations.TryGetValue(task.Id, out cts);
            }
            else
            {
                // 排队中 / 等待重试：不用等进程，直接判取消
                task.Status = ConversionTaskStatus.Canceled;
                task.FinishedAt = _clock.Now;
                _retryAt.Remove(task.Id);
                markDirectly = true;
            }
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            return;   // 运行中的那条由 RunOneAsync 收尾
        }

        if (markDirectly)
        {
            await PersistQuietAsync(task).ConfigureAwait(false);
            Raise(task);
        }
    }

    /// <summary>
    /// 全部取消。返回时所有外部进程必须已经消失（判据 e）：
    /// 所以这里除了发取消信号，还要等到 _cancellations 真的清空。
    /// </summary>
    public async Task<int> CancelAllAsync(CancellationToken ct = default)
    {
        List<CancellationTokenSource> running;
        List<ConversionTask> canceled;

        lock (_gate)
        {
            running = _cancellations.Values.ToList();
            canceled = _tasks.Where(t => !t.IsTerminal).ToList();
            foreach (var t in canceled)
            {
                // 运行中的也先标成取消：RunOneAsync 回来时会看到状态已经不是 Running，
                // 于是丢掉这次的产物，不会又变成"已完成"。
                t.Status = ConversionTaskStatus.Canceled;
                t.Progress = null;
                t.ProgressKind = ConversionTaskProgressKind.Unknown;
                t.FinishedAt = _clock.Now;
                _retryAt.Remove(t.Id);
            }
        }

        foreach (var cts in running)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        foreach (var t in canceled)
        {
            await PersistQuietAsync(t).ConfigureAwait(false);
            Raise(t);
        }

        // 等真正的收尾（进程被杀掉、槽位归还）。判据是 10 秒内所有外部进程消失。
        var deadline = _clock.Now.AddSeconds(10);
        while (true)
        {
            int left;
            lock (_gate)
            {
                left = _cancellations.Count;
            }

            if (left == 0 || _clock.Now >= deadline)
            {
                break;
            }

            try
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _log?.LogInformation("队列：全部取消，共取消 {Count} 条", canceled.Count);
        return canceled.Count;
    }

    public async Task WaitForIdleAsync(CancellationToken ct = default)
    {
        while (IsBusy)
        {
            await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false);
        }
    }

    public string? GetTaskOutput(Guid taskId)
    {
        lock (_gate)
        {
            return _outputs.TryGetValue(taskId, out var collector) ? collector.Text : null;
        }
    }

    /// <summary>第一次用到时把 worker 起起来。App 启动时一般已经显式调过 StartAsync。</summary>
    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_worker is not null)
            {
                return;
            }
        }

        await StartAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 写库失败绝不把已经定好的结果再抛出去：内存里的状态是对的，界面照常显示，
    /// 下一次启动时崩溃恢复会自己把它归位。
    /// </summary>
    private async Task PersistQuietAsync(ConversionTask task)
    {
        try
        {
            await _repository.UpdateAsync(task).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "写入转换任务状态失败 {Id}", task.Id);
        }
    }

    private void Raise(ConversionTask task)
    {
        try
        {
            TaskChanged?.Invoke(task);
        }
        catch (Exception ex)
        {
            // 订阅者（界面）抛异常不能把队列带崩
            _log?.LogWarning(ex, "转换队列事件的订阅者抛异常");
        }
    }

    /// <summary>把外部进程的每一行原样攒下来，界面「查看日志」看的就是它。</summary>
    private sealed class LineCollector : IProgress<ProcessOutputLine>
    {
        private readonly StringBuilder _builder = new();
        private readonly object _lock = new();

        public void Report(ProcessOutputLine value)
        {
            lock (_lock)
            {
                if (value.IsError)
                {
                    _builder.Append("[stderr] ");
                }

                _builder.AppendLine(value.Text);
            }
        }

        public string Text
        {
            get
            {
                lock (_lock)
                {
                    return _builder.ToString();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        CancellationTokenSource? shutdown;
        Task? worker;
        lock (_gate)
        {
            shutdown = _shutdown;
            worker = _worker;
        }

        if (shutdown is not null)
        {
            await shutdown.CancelAsync().ConfigureAwait(false);
        }

        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        // 正在跑的那几条也要等它们把进程杀掉再走，否则退出时会留下 Calibre 孤儿进程
        var deadline = _clock.Now.AddSeconds(5);
        while (_clock.Now < deadline)
        {
            lock (_gate)
            {
                if (_cancellations.Count == 0)
                {
                    break;
                }
            }

            await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
        }

        _slots.Dispose();
        shutdown?.Dispose();
    }

    /// <summary>
    /// 这次请求是不是「不换格式，只优化」（polish）。
    /// 判据是「模板 id 出现在这本书某个格式的 polish 模板清单里」：
    /// 不能只看「目标格式等于源格式」，那样普通的同格式转换也会被误放行。
    /// </summary>
    private bool IsKeepsFormatRequest(Book book, ConversionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TemplateId))
        {
            return false;
        }

        foreach (var format in book.Formats)
        {
            var matches = _conversion
                .GetFormatKeepingTemplates(format.Format)
                .Any(t => string.Equals(t.Id, request.TemplateId, StringComparison.OrdinalIgnoreCase));

            if (matches)
            {
                return true;
            }
        }

        return false;
    }
}
