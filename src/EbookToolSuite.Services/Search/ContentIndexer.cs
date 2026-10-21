using System.Threading.Channels;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Search;

/// <summary>
/// 正文索引的后台构建器（T6-4）。导入成功只 Enqueue，不阻塞导入；
/// 抽取与写库在后台单线程串行跑（SQLite 写是单写者，多线程反而互相等）。
/// </summary>
/// <remarks>
/// <para><b>可中断可续</b>：进度不放在内存里当唯一真相，而是每本书一行
/// <c>content_index_state</c>。三处会续建：
/// ①App 启动 <see cref="StartAsync"/>；②导入完成 <see cref="EnqueueAsync"/>；
/// ③用户手动触发（本轮不做）。正在索引时被杀进程，状态停在 Indexing，启动时
/// <see cref="IContentRepository.ListBookIdsNeedingIndexAsync"/> 会把它当待建捡回来。</para>
/// <para><b>为什么要等 idle 的方法</b>：冒烟与单测要断言"导入后索引确实建好了"，
/// 不能靠固定 Sleep。</para>
/// </remarks>
public sealed class ContentIndexer : IContentIndexer
{
    private readonly IContentRepository _repo;
    private readonly IBookRepository _books;
    private readonly ContentTextExtractor _extractor;
    private readonly ILogger<ContentIndexer> _log;

    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly HashSet<Guid> _queued = new();
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _pending;
    private int _done;
    private int _failed;
    private int _total;
    private string? _current;

    private ContentIndexProgress _progress = new();

    public ContentIndexer(
        IContentRepository repo,
        IBookRepository books,
        ContentTextExtractor extractor,
        ILogger<ContentIndexer> log)
    {
        _repo = repo;
        _books = books;
        _extractor = extractor;
        _log = log;
    }

    public ContentIndexProgress Progress => _progress;

    public event EventHandler<ContentIndexProgress>? ProgressChanged;

    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false })
            {
                return;
            }

            /* 重启前把上一轮残留在通道里的 id 倒掉：数据库里的状态行才是真相，
               重新从 ListBookIdsNeedingIndexAsync 捞一遍不会漏、也不会重。 */
            while (_queue.Reader.TryRead(out _))
            {
            }

            _queued.Clear();
            _pending = 0;
            _done = 0;
            _failed = 0;
            _total = 0;
            _current = null;

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        }

        await EnqueuePendingAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;

        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        cts?.Cancel();

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常收工
            }
        }

        cts?.Dispose();
    }

    public async Task EnqueueAsync(IReadOnlyCollection<Guid> bookIds, CancellationToken ct = default)
    {
        if (bookIds.Count == 0)
        {
            return;
        }

        var added = new List<Guid>(bookIds.Count);

        lock (_gate)
        {
            foreach (var id in bookIds)
            {
                if (id == Guid.Empty || !_queued.Add(id))
                {
                    continue;
                }

                added.Add(id);
            }

            _pending += added.Count;
            _total += added.Count;
        }

        foreach (var id in added)
        {
            await _queue.Writer.WriteAsync(id, ct).ConfigureAwait(false);
        }

        if (added.Count > 0)
        {
            Publish();
            _log.LogInformation("正文索引入队 {Count} 本", added.Count);
        }
    }

    public async Task<int> EnqueuePendingAsync(CancellationToken ct = default)
    {
        var ids = await _repo.ListBookIdsNeedingIndexAsync(ct).ConfigureAwait(false);

        if (ids.Count == 0)
        {
            return 0;
        }

        await EnqueueAsync(ids.ToList(), ct).ConfigureAwait(false);
        return ids.Count;
    }

    public async Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Volatile.Read(ref _pending) == 0)
            {
                return true;
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        return Volatile.Read(ref _pending) == 0;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var bookId in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await ProcessAsync(bookId, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("正文索引已停止，未完成的书留在 Pending，下次启动续建");
        }
        finally
        {
            Publish();
        }
    }

    private async Task ProcessAsync(Guid bookId, CancellationToken ct)
    {
        /* 三种结局分开计：Done 记成功、Failed 记失败、被取消的不记（它还是待建）。
           succeeded 只表示"这本不必再建了"（含跳过），不能拿它当 Done 的依据。 */
        var countedDone = false;
        var countedFailed = false;

        try
        {
            var state = await _repo.GetStateAsync(bookId, ct).ConfigureAwait(false);

            if (state?.Status is ContentIndexStatus.Done or ContentIndexStatus.Unsupported)
            {
                countedDone = true;
                return;
            }

            var book = await _books.GetByIdAsync(bookId, ct).ConfigureAwait(false);

            if (book is null)
            {
                _log.LogWarning("书已不存在，跳过索引 {BookId}", bookId);
                countedDone = true;
                return;
            }

            _current = book.Title;
            Publish();

            await _repo.SaveStateAsync(new ContentIndexState
            {
                BookId = bookId,
                Status = ContentIndexStatus.Indexing,
            }, ct).ConfigureAwait(false);

            var started = DateTimeOffset.UtcNow;

            // 抽取是纯 CPU/文件 IO：放到线程池，别占着后台循环的调度
            var chunks = await Task.Run(() => _extractor.Extract(book, ct), ct).ConfigureAwait(false);

            await _repo.ReplaceBookChunksAsync(bookId, chunks, ct).ConfigureAwait(false);

            await _repo.SaveStateAsync(new ContentIndexState
            {
                BookId = bookId,
                Status = ContentIndexStatus.Done,
                ChunkCount = chunks.Count,
            }, ct).ConfigureAwait(false);

            countedDone = true;

            _log.LogInformation(
                "正文索引完成：《{Title}》{Chunks} 片，用时 {Ms} ms",
                book.Title, chunks.Count, (DateTimeOffset.UtcNow - started).TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // 正在索引的这一本回到 Pending（用 None 的 token：取消不影响收尾落库）
            await SafeSaveAsync(bookId, ContentIndexStatus.Pending, 0, null).ConfigureAwait(false);
            throw;
        }
        catch (AppException ex) when (ex.Code == ErrorCode.UnsupportedFormat)
        {
            await SafeSaveAsync(bookId, ContentIndexStatus.Unsupported, 0, ex.UserMessage)
                .ConfigureAwait(false);

            _log.LogInformation("这本书不索引正文（{Reason}）", ex.UserMessage);
        }
        catch (Exception ex)
        {
            countedFailed = true;

            await SafeSaveAsync(bookId, ContentIndexStatus.Failed, 0, ex.SafeMessage()).ConfigureAwait(false);

            _log.LogWarning(ex, "正文索引失败 {BookId}", bookId);
        }
        finally
        {
            lock (_gate)
            {
                _queued.Remove(bookId);
                _pending = Math.Max(0, _pending - 1);

                if (countedDone)
                {
                    _done++;
                }
                else if (countedFailed)
                {
                    _failed++;
                }
            }

            _current = null;
            Publish();
        }
    }

    private async Task SafeSaveAsync(Guid bookId, ContentIndexStatus status, int chunkCount, string? error)
    {
        try
        {
            await _repo.SaveStateAsync(new ContentIndexState
            {
                BookId = bookId,
                Status = status,
                ChunkCount = chunkCount,
                Error = error,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "写索引状态失败 {BookId} -> {Status}", bookId, status);
        }
    }

    private void Publish()
    {
        bool running;

        lock (_gate)
        {
            running = _loop is { IsCompleted: false };
        }

        var progress = new ContentIndexProgress
        {
            Running = running && Volatile.Read(ref _pending) > 0,
            Total = Volatile.Read(ref _total),
            Done = Volatile.Read(ref _done),
            Pending = Volatile.Read(ref _pending),
            Failed = Volatile.Read(ref _failed),
            CurrentTitle = _current,
        };

        _progress = progress;

        try
        {
            ProgressChanged?.Invoke(this, progress);
        }
        catch (Exception ex)
        {
            // 订阅者（界面）抛异常不能把索引循环打死
            _log.LogWarning(ex, "索引进度通知的订阅者抛异常");
        }
    }
}
