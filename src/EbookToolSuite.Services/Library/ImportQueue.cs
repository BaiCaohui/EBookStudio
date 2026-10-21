using System.Diagnostics;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Library;

/// <summary>
/// 批量导入队列（执行计划 T2-1）：一次扫一批、预检去重、单线程逐本导入、可暂停可取消。
/// </summary>
/// <remarks>
/// 【线程约定】
/// 1. StartAsync 在**调用方线程**上完成"扫描 + 预检 + 弹重复对话框"，
///    因此刻意不用 ConfigureAwait(false)：重复对话框要在 UI 线程上弹。
/// 2. 真正的导入在 Task.Run 出来的后台线程上跑，全程 ConfigureAwait(false)，
///    绝不回到 UI 线程（否则 200 本书会把界面锁死，违背 T2-1 判据）。
/// 3. 所有事件都在后台线程触发，UI 订阅者自行 Dispatcher.Invoke。
/// 【并发度 = 1】导入是 IO 密集且要写库，并发的收益远小于复杂度（执行计划 T2-1 决策表）。
/// 【暂停与取消】都是"完成当前这本后停"，不做中途打断单本导入：避免半成品进正式区。
/// </remarks>
public sealed class ImportQueue : IImportQueue
{
    private readonly IImportScanner _scanner;
    private readonly IBookImportService _pipeline;
    private readonly IBookRepository _books;
    private readonly IImportBatchRepository _batches;
    private readonly IHashUtil _hash;
    private readonly IDuplicateResolver _resolver;
    private readonly IClock _clock;
    private readonly ILogger<ImportQueue> _log;

    /// <summary>
    /// T6-4：导入成功后排进正文索引队列（不阻塞导入）。可为 null——单测里不想拉整个索引链路时。
    /// </summary>
    private readonly IContentIndexer? _indexer;

    private readonly List<ImportJob> _jobs = new();
    private readonly object _stateGate = new();

    private ImportBatch? _batch;
    private ImportQueueState _state = ImportQueueState.Idle;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private TaskCompletionSource<bool>? _completion;
    private int _completed;

    // UI 线程写、Worker 线程读，必须 volatile（否则可能读到永不刷新的缓存值而卡在暂停里）。
    private volatile bool _paused;
    private volatile bool _cancelRequested;

    public ImportQueue(
        IImportScanner scanner,
        IBookImportService pipeline,
        IBookRepository books,
        IImportBatchRepository batches,
        IHashUtil hash,
        IDuplicateResolver resolver,
        IClock clock,
        ILogger<ImportQueue> log,
        IContentIndexer? indexer = null)
    {
        _scanner = scanner;
        _pipeline = pipeline;
        _books = books;
        _batches = batches;
        _hash = hash;
        _resolver = resolver;
        _clock = clock;
        _log = log;
        _indexer = indexer;
    }

    public ImportQueueState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public ImportBatch? CurrentBatch => _batch;

    public IReadOnlyList<ImportJob> Jobs => _jobs;

    public int TotalCount => _jobs.Count;

    public int CompletedCount => Volatile.Read(ref _completed);

    public event EventHandler<ImportJob>? JobStateChanged;

    public event EventHandler<ImportProgress>? JobProgress;

    public event EventHandler<ImportQueueState>? StateChanged;

    public event EventHandler<ImportBatch>? BatchFinished;
    public async Task<ImportBatch> StartAsync(
        IReadOnlyList<string> paths, ImportQueueOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);

        if (State is not (ImportQueueState.Idle or ImportQueueState.Finished))
        {
            throw new InvalidOperationException("上一批导入还没结束（当前状态：" + State + "）。");
        }

        SetState(ImportQueueState.Preparing);

        var scan = _scanner.Scan(paths, options.Recursive, ct);
        if (scan.Count == 0)
        {
            SetState(ImportQueueState.Idle);
            throw new ArgumentException(scan.Summary + "没有可导入的文件。", nameof(paths));
        }

        _jobs.Clear();
        Interlocked.Exchange(ref _completed, 0);
        _paused = false;
        _cancelRequested = false;

        foreach (var file in scan.Files)
        {
            _jobs.Add(new ImportJob { Id = Guid.NewGuid(), SourcePath = file });
        }

        var batch = new ImportBatch
        {
            Id = Guid.NewGuid(),
            Source = options.SourceLabel ?? DescribeSource(paths, scan),
            StartedAt = _clock.Now,
            Total = _jobs.Count,
        };

        _batch = batch;

        try
        {
            await PreflightAsync(batch, options, ct).ConfigureAwait(true);
            await _batches.AddAsync(batch, ct).ConfigureAwait(true);
        }
        catch
        {
            SetState(ImportQueueState.Idle);
            _batch = null;
            throw;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _worker = Task.Run(() => RunAsync(batch, options, _cts.Token), CancellationToken.None);

        _log.LogInformation("导入批次开始：" + batch.Summary + "，来源：" + batch.Source);
        return batch;
    }

    // ---------- 预检：扫描之后、真正导入之前 ----------

    private async Task PreflightAsync(ImportBatch batch, ImportQueueOptions options, CancellationToken ct)
    {
        await ComputeFingerprintsAsync(options, ct).ConfigureAwait(true);
        await MarkDuplicatesAsync(batch, options, ct).ConfigureAwait(true);
    }

    private async Task ComputeFingerprintsAsync(ImportQueueOptions options, CancellationToken ct)
    {
        if (!options.ParallelPreflight)
        {
            foreach (var job in _jobs)
            {
                ct.ThrowIfCancellationRequested();
                await FingerprintOneAsync(job, ct).ConfigureAwait(true);
            }

            return;
        }

        // 并发度封顶 8：200 个文件同时开流会把机械盘打成随机读，反而更慢。
        var degree = Math.Max(1, Math.Min(8, Environment.ProcessorCount));
        using var gate = new SemaphoreSlim(degree, degree);

        var tasks = _jobs.Select(job => Task.Run(async () =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await FingerprintOneAsync(job, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }, ct)).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    private async Task FingerprintOneAsync(ImportJob job, CancellationToken ct)
    {
        try
        {
            job.QuickFingerprint = await _hash.QuickFingerprintAsync(job.SourcePath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 算不出指纹不妨碍导入，只是这一次去重抓不到它；流水线里还会再算一次。
            _log.LogWarning("预检算指纹失败，这一本不参与去重：" + job.SourcePath + "（" + ex.GetType().Name + "）");
        }
    }
    private async Task MarkDuplicatesAsync(ImportBatch batch, ImportQueueOptions options, CancellationToken ct)
    {
        var candidates = _jobs.Where(j => !string.IsNullOrEmpty(j.QuickFingerprint)).ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var fingerprints = candidates
            .Select(j => j.QuickFingerprint!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        IReadOnlyList<FingerprintMatch> hits;
        try
        {
            // 一次 IN 查询搞定全部指纹，不逐本查库（200 本就是 200 次往返）。
            hits = await _books.FindBooksByFingerprintsAsync(fingerprints, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 查库失败不该让整批停摆：最坏结果是重复的书也导进去一份，事后用 T2-2 处理。
            _log.LogWarning("预检查重失败，本批不做去重：" + ex.GetType().Name + "：" + ex.Message);
            return;
        }

        var existing = new Dictionary<string, FingerprintMatch>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in hits)
        {
            existing[match.QuickFingerprint] = match;
        }

        var firstInBatch = new Dictionary<string, ImportJob>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<ImportJob>();

        foreach (var job in _jobs)
        {
            var fingerprint = job.QuickFingerprint;
            if (string.IsNullOrEmpty(fingerprint))
            {
                continue;
            }

            if (existing.TryGetValue(fingerprint, out var match))
            {
                job.DuplicateOfBookId = match.BookId;
                job.DuplicateOfTitle = match.Title;
                duplicates.Add(job);
                continue;
            }

            if (firstInBatch.TryGetValue(fingerprint, out var first))
            {
                // 同一批里前后撞上：同一个文件被选进来两次，或者同一个文件的副本。
                // 这一本还没入库，所以没有 BookId，只能拿先到的那一本的文件名做说明。
                job.DuplicateOfBookId = null;
                job.DuplicateOfTitle = first.FileName;
                duplicates.Add(job);
                continue;
            }

            firstInBatch[fingerprint] = job;
        }

        if (duplicates.Count == 0)
        {
            return;
        }

        var policy = options.PromptOnDuplicates
            ? await _resolver.ResolveBatchAsync(duplicates, ct).ConfigureAwait(true)
            : DuplicatePolicy.SkipAll;

if (policy == DuplicatePolicy.ImportAll || policy == DuplicatePolicy.Replace)
          {
              var replacing = policy == DuplicatePolicy.Replace;

              foreach (var job in duplicates)
              {
                  /* 同批重复副本（还没入库、没有 BookId）：替换时没有可删的目标  只保留先到的那一份，本份跳过。 */
                  if (replacing && job.DuplicateOfBookId is null)
                  {
                      MarkSkippedDuplicate(job);
                      continue;
                  }

                  job.ImportAnyway = true;
                  job.ReplaceExisting = replacing;

                  /* 替换时要留着 DuplicateOfBookId（要删的就是它）；只有"全部导入"才清掉重复标记。 */
                  if (!replacing)
                  {
                      job.DuplicateOfBookId = null;
                      job.DuplicateOfTitle = null;
                  }
              }

            batch.Note = AppendNote(batch.Note, "重复 " + duplicates.Count + " 本，用户选择全部仍要导入");
            _log.LogInformation("预检发现 " + duplicates.Count + " 本重复，用户选择全部导入。");
            return;
        }

        var skipped = 0;
        for (var i = 0; i < duplicates.Count; i++)
        {
            var job = duplicates[i];
            var decision = policy == DuplicatePolicy.AskEach
                  ? await _resolver.ResolveSingleAsync(job, i + 1, duplicates.Count, ct).ConfigureAwait(true)
                  : DuplicatePolicy.SkipAll;

            if (decision == DuplicatePolicy.ImportAll || decision == DuplicatePolicy.Replace)
            {
                job.ImportAnyway = true;
                /* 同批重复副本（没有 BookId）：逐项选了替换也只能跳过，避免多导入一份。 */
                  if (decision == DuplicatePolicy.Replace && job.DuplicateOfBookId is null)
                  {
                      MarkSkippedDuplicate(job);
                      continue;
                  }

                  job.ReplaceExisting = decision == DuplicatePolicy.Replace;
                
                /* 替换时要留着 DuplicateOfBookId（要删的就是它）；只有"仍然导入"才清掉重复标记。 */
                if (decision != DuplicatePolicy.Replace)
                {
                    job.DuplicateOfBookId = null;
                    job.DuplicateOfTitle = null;
                }
                continue;
            }

            MarkSkippedDuplicate(job);
            skipped++;
        }

        if (skipped > 0)
        {
            Interlocked.Add(ref _completed, skipped);
        }

        batch.Note = AppendNote(batch.Note, "跳过重复 " + skipped + " 本");
        _log.LogInformation("预检跳过 " + skipped + " 本重复（策略 " + policy + "）。");
    }

    /// <summary>
    /// 把一本重复书直接判成终态。**预检阶段就定终态**：UI 列表立刻能看到
    /// 那 15 本"跳过重复"，不用等 Worker 一本书一本书走到它跟前（执行计划 T2-1 的交互要求）。
    /// </summary>
    private void MarkSkippedDuplicate(ImportJob job)
    {
        job.State = ImportJobState.Finished;
        job.Outcome = ImportOutcome.Duplicate;
        job.Message = job.DuplicateOfBookId is not null && job.DuplicateOfTitle is not null
            ? "已跳过：书库中已有《" + job.DuplicateOfTitle + "》"
            : "已跳过：与本次前面的文件重复";
        RaiseJobStateChanged(job);
    }
    // ---------- Worker：单线程逐本导入 ----------

    private async Task RunAsync(ImportBatch batch, ImportQueueOptions options, CancellationToken ct)
    {
        SetState(ImportQueueState.Running);

        try
        {
            for (var i = 0; i < _jobs.Count; i++)
            {
                var job = _jobs[i];

                if (job.IsTerminal)
                {
                    // 预检阶段已经定过终态的（跳过的重复），也已经计过数了。
                    continue;
                }

                if (_cancelRequested)
                {
                    MarkCanceled(job);
                    continue;
                }

                await WaitWhilePausedAsync(ct).ConfigureAwait(false);

                if (_cancelRequested)
                {
                    MarkCanceled(job);
                    continue;
                }

                await RunOneAsync(job, i + 1, batch, options, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("导入队列被取消，正在把没开始的标记掉。");
            foreach (var job in _jobs)
            {
                MarkCanceled(job);
            }
        }
        catch (Exception ex)
        {
            // Worker 自身炸了（不该发生）：剩下的任务全部记失败，但**照样写库收尾**，
            // 否则活动中心会永远留一条"进行中"的僵尸批次。
            _log.LogError(ex, "导入队列异常终止。");
            foreach (var job in _jobs)
            {
                if (job.IsTerminal)
                {
                    continue;
                }

                job.State = ImportJobState.Finished;
                job.Outcome = ImportOutcome.Failed;
                job.Message = "队列异常：" + ex.SafeMessage();
                Interlocked.Increment(ref _completed);
                RaiseJobStateChanged(job);
            }
        }
        finally
        {
            await FinishBatchAsync(batch).ConfigureAwait(false);
        }
    }

    private async Task RunOneAsync(
        ImportJob job, int index, ImportBatch batch, ImportQueueOptions options, CancellationToken ct)
    {
        job.State = ImportJobState.Running;
        RaiseJobStateChanged(job);

        var stopwatch = Stopwatch.StartNew();
        try
        {

            var importOptions = new ImportOptions
            {
                ExtractCover = options.ExtractCover,
                CoverFullSizePx = options.CoverFullSizePx,
                CoverThumbWidthPx = options.CoverThumbWidthPx,
                Recursive = options.Recursive,
                BatchId = batch.Id,
                KnownQuickFingerprint = job.QuickFingerprint,
ImportDuplicates = job.ImportAnyway,
                ReplaceBookId = job.ReplaceExisting ? job.DuplicateOfBookId : null,
            };

            var progress = new QueueProgress(this, job, index);

            // 流水线自己不抛异常表达业务失败：InvalidEpub / Drm / Duplicate 都在 Outcome 里。
            var result = await _pipeline
                .ImportAsync(job.SourcePath, importOptions, progress, ct)
                .ConfigureAwait(false);

            stopwatch.Stop();
            job.Outcome = result.Outcome;
            job.BookId = result.BookId;
            job.Message = result.Message;

            /* T6-4：书进库了就排进正文索引队列。后台线程建索引，不阻塞这一本的导入收尾；
               排不进去只记日志——索引失败不该让"导入成功"看起来失败。 */
            if (result.Succeeded && result.BookId is Guid importedId && _indexer is not null)
            {
                try
                {
                    await _indexer.EnqueueAsync(new[] { importedId }, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "正文索引入队失败 {BookId}", importedId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            throw;
        }
        catch (Exception ex)
        {
            // 一本坏书绝不能拖垮整批（执行计划 T2-1 判据）。
            stopwatch.Stop();
            _log.LogError(ex, "导入这一本时抛异常，已跳过：" + job.SourcePath);
            job.Outcome = ImportOutcome.Failed;
            job.Message = "导入失败：" + ex.SafeMessage();
        }

        job.Duration = stopwatch.Elapsed;
        job.State = ImportJobState.Finished;
        Tally(batch, job.Outcome);
        Interlocked.Increment(ref _completed);
        RaiseJobStateChanged(job);
    }

    private static void Tally(ImportBatch batch, ImportOutcome? outcome)
    {
        switch (outcome)
        {
            case ImportOutcome.Imported:
                batch.Succeeded++;
                break;
            case ImportOutcome.Duplicate:
                // 跳过的重复既不算成功也不算失败（记在 batch.Note 里）。
                break;
            case ImportOutcome.Canceled:
                break;
            case null:
                break;
            default:
                batch.Failed++;
                break;
        }
    }

    /// <summary>
    /// 把流水线的进度映射成"队列视角"的进度（第几本 / 共几本），
    /// 直接用 Progress&lt;T&gt; 会派发到线程池，阶段时序不可控，所以自己实现。
    /// </summary>
    private sealed class QueueProgress : IProgress<ImportProgress>
    {
        private readonly ImportQueue _owner;
        private readonly ImportJob _job;
        private readonly int _index;

        public QueueProgress(ImportQueue owner, ImportJob job, int index)
        {
            _owner = owner;
            _job = job;
            _index = index;
        }

        public void Report(ImportProgress value)
        {
            var mapped = value with
            {
                Index = _index,
                Total = _owner._jobs.Count,
                FileName = _job.FileName,
            };

            try
            {
                _owner.JobProgress?.Invoke(_owner, mapped);
            }
            catch (Exception ex)
            {
                _owner._log.LogWarning(ex, "JobProgress 订阅者抛异常，已忽略。");
            }
        }
    }
    // ---------- 收尾与队列控制 ----------

    private async Task FinishBatchAsync(ImportBatch batch)
    {
        batch.FinishedAt = _clock.Now;

        var canceled = _jobs.Count(j => j.State == ImportJobState.Canceled);
        if (canceled > 0)
        {
            batch.Note = AppendNote(batch.Note, "用户取消，未开始 " + canceled + " 本");
        }

        try
        {
            // 收尾写库用 CancellationToken.None：批次记录必须落盘，不能被调用方的取消令牌打断。
            await _batches.UpdateAsync(batch, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "批次记录写库失败：" + batch.Id);
        }

        SetState(ImportQueueState.Finished);
        _log.LogInformation("导入批次结束：" + batch.Summary + "，备注：" + (batch.Note ?? "无"));

        try
        {
            BatchFinished?.Invoke(this, batch);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "BatchFinished 订阅者抛异常，已忽略。");
        }
        finally
        {
            _completion?.TrySetResult(true);
        }
    }

    private void MarkCanceled(ImportJob job)
    {
        if (job.IsTerminal)
        {
            return;
        }

        job.State = ImportJobState.Canceled;
        job.Message = "已取消：还没轮到它开始";
        Interlocked.Increment(ref _completed);
        RaiseJobStateChanged(job);
    }

    private async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        // 轮询而不是信号量：暂停的语义是"人停下来看一眼"，120ms 的粒度完全够，
        // 而轮询不会因为忘记 Release 而永久卡死队列。
        while (_paused && !_cancelRequested)
        {
            await Task.Delay(120, ct).ConfigureAwait(false);
        }
    }

    public void Pause()
    {
        if (State is not ImportQueueState.Running)
        {
            return;
        }

        _paused = true;
        SetState(ImportQueueState.Paused);
        _log.LogInformation("导入队列已暂停（当前这本跑完才停）。");
    }

    public void Resume()
    {
        if (!_paused)
        {
            return;
        }

        _paused = false;
        if (!_cancelRequested)
        {
            SetState(ImportQueueState.Running);
            _log.LogInformation("导入队列继续。");
        }
    }

    public void Cancel()
    {
        if (State is ImportQueueState.Idle or ImportQueueState.Finished or ImportQueueState.Canceling)
        {
            return;
        }

        _cancelRequested = true;
        _paused = false;
        SetState(ImportQueueState.Canceling);
        _log.LogInformation("导入队列收到取消请求（当前这本跑完才停）。");
    }

    public async Task WaitForCompletionAsync(CancellationToken ct = default)
    {
        var completion = _completion;
        var worker = _worker;
        if (completion is null || worker is null)
        {
            return;
        }

        await completion.Task.WaitAsync(ct).ConfigureAwait(false);
        await worker.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();

        var worker = _worker;
        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "等待导入队列收尾时出错，已忽略。");
            }
        }

        _cts?.Dispose();
        _cts = null;
        _worker = null;
    }
    // ---------- 小工具 ----------

    private void RaiseJobStateChanged(ImportJob job)
    {
        try
        {
            JobStateChanged?.Invoke(this, job);
        }
        catch (Exception ex)
        {
            // 订阅者（UI）抛异常不能把 Worker 带走。
            _log.LogWarning(ex, "JobStateChanged 订阅者抛异常，已忽略。");
        }
    }

    private void SetState(ImportQueueState value)
    {
        lock (_stateGate)
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
        }

        try
        {
            StateChanged?.Invoke(this, value);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "StateChanged 订阅者抛异常，已忽略。");
        }
    }

    private static string DescribeSource(IReadOnlyList<string> paths, ImportScanResult scan)
    {
        if (paths.Count == 1 && !string.IsNullOrWhiteSpace(paths[0]))
        {
            return paths[0];
        }

        return paths.Count + " 个选择项（展开出 " + scan.Count + " 个文件）";
    }

    private static string AppendNote(string? existing, string extra)
        => string.IsNullOrWhiteSpace(existing) ? extra : existing + "；" + extra;
}
