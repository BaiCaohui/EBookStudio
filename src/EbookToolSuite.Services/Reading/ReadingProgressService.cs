using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Reading;

/// <summary>
/// 阅读进度的保存与恢复。规格见执行计划 T1-6、设计文档 8.4。
/// </summary>
/// <remarks>
/// 【防抖为什么做在这里】滚动一次会上报多次位置，每次都写库既慢又没意义。
/// 这里把每本书"最新的一条"攒在内存里，1.5 秒内没有新上报才落盘一次。
/// 队列是集中式的，所以程序退出时能一次性把没写完的都补上（FlushAsync）。
///
/// 【写不进去不算错】进度是锦上添花的数据：磁盘满、库被锁住都不该打断阅读，
/// 一律记 WRN 后继续。
/// </remarks>
public sealed class ReadingProgressService : IReadingProgressService, IDisposable
{
    /// <summary>滚动停止多久后落盘。执行计划 T1-6 定的是 1.5 秒。</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(1500);

    private readonly IBookRepository _repo;
    private readonly ILogger<ReadingProgressService> _log;
    private readonly TimeSpan _debounce;

    /// <summary>待写队列：一本书最多一条，新位置覆盖旧位置。</summary>
    private readonly Dictionary<Guid, ReaderProgressSnapshot> _pending = new();

    /// <summary>写库串行化。SQLite 同一时刻只允许一个写者，排队比让它们互相撞更简单。</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private readonly Timer _timer;
    private readonly object _gate = new();

    private long _savedCount;
    private bool _disposed;

    public ReadingProgressService(
        IBookRepository repo,
        ILogger<ReadingProgressService> log,
        TimeSpan? debounce = null)
    {
        _repo = repo;
        _log = log;
        _debounce = debounce ?? DefaultDebounce;

        // 一个定时器管所有书：Change 会重置计时，所以"连续滚动期间不写库"天然成立
        _timer = new Timer(_ => _ = DrainAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public long SavedCount => Interlocked.Read(ref _savedCount);

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public async Task<ReaderProgressSnapshot?> LoadAsync(
        Guid bookId, int chapterCount, CancellationToken ct = default)
    {
        /* 先看队列：关掉阅读器又立刻重开同一本书时，库里那条还是 1.5 秒前的
           旧位置，队列里的才是最新的。 */
        lock (_gate)
        {
            if (_pending.TryGetValue(bookId, out var queued))
            {
                return queued;
            }
        }

        var row = await _repo.GetReadingProgressAsync(bookId, ct).ConfigureAwait(false);
        return ReaderProgressSnapshot.FromRow(row, chapterCount);
    }

    public void ScheduleSave(ReaderProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _pending[snapshot.BookId] = snapshot;
        }

        try
        {
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 正在退出，忽略
        }
    }

    public Task SaveNowAsync(ReaderProgressSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            // 已经排进防抖队列的那条被这次立即保存取代，不能重复写
            _pending.Remove(snapshot.BookId);
        }

        return WriteAsync(snapshot, ct);
    }
    public async Task FlushAsync(CancellationToken ct = default)
    {
        var snapshots = TakePending();

        foreach (var snapshot in snapshots)
        {
            await WriteAsync(snapshot, ct).ConfigureAwait(false);
        }
    }

    /// <summary>定时器到点后调用：把攒着的全部落盘。</summary>
    private async Task DrainAsync()
    {
        var snapshots = TakePending();

        foreach (var snapshot in snapshots)
        {
            await WriteAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private List<ReaderProgressSnapshot> TakePending()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return new List<ReaderProgressSnapshot>();
            }

            var list = new List<ReaderProgressSnapshot>(_pending.Values);
            _pending.Clear();
            return list;
        }
    }

    private async Task WriteAsync(ReaderProgressSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                await _repo.SaveReadingProgressAsync(snapshot.ToRow(), ct).ConfigureAwait(false);
                Interlocked.Increment(ref _savedCount);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // 退出时被取消属于正常：最多回到上一次保存的位置
        }
        catch (Exception ex)
        {
            // 库被锁、磁盘满、文件被删：都不该影响阅读
            _log.LogWarning(ex, "保存阅读进度失败：{BookId}", snapshot.BookId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }

        _timer.Dispose();
        _writeGate.Dispose();
    }
}
