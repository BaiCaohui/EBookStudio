using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Reading;

/// <summary>
/// 阅读会话采集（T7-3B）。口径见 docs/DECISIONS.md 的 T7-3B 条目。
/// </summary>
/// <remarks>
/// 【为什么心跳与空闲判定做在这里】阅读事件（翻页 / 滚动 / 选区）一秒可能来几十次，
/// 每次都写库既慢又占着 SQLite 的写锁。这里只在内存里累计，服务自己每分钟落库一次；
/// 两次事件间隔超过空闲阈值（默认 5 分钟）说明是挂机后回来，旧会话按"上次活动时间"
/// 收尾、当前事件另起一条，两段之间的空白不计时。
///
/// 【写失败不算错】统计是锦上添花的数据：写不进去只记 WRN，不影响阅读。
/// </remarks>
public sealed class ReadingSessionService : IReadingSessionService, IDisposable
{
    /// <summary>默认心跳间隔：最多每分钟落库一次。</summary>
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(60);

    /// <summary>默认空闲阈值：两次阅读事件间隔超过它视为挂机。</summary>
    public static readonly TimeSpan DefaultIdleGap = TimeSpan.FromMinutes(5);

    private readonly IReadingStatsRepository _repo;
    private readonly IClock _clock;
    private readonly ILogger<ReadingSessionService> _log;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _idleGap;

    /// <summary>进行中的会话：一本书最多一条。</summary>
    private readonly Dictionary<Guid, ActiveSession> _active = new();

    /// <summary>写库串行化。SQLite 同一时刻只允许一个写者，排队比让它们互相撞更简单。</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private readonly object _gate = new();
    private readonly Timer _timer;
    private bool _disposed;

    public ReadingSessionService(
        IReadingStatsRepository repo,
        IClock clock,
        ILogger<ReadingSessionService> log,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? idleGap = null)
    {
        _repo = repo;
        _clock = clock;
        _log = log;
        _heartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval;
        _idleGap = idleGap ?? DefaultIdleGap;

        // 一个定时器管所有书：每心跳间隔扫一遍"有活动且没落库"的会话
        _timer = new Timer(_ => _ = HeartbeatAsync(), null, _heartbeatInterval, _heartbeatInterval);
    }

    public async Task StartAsync(Guid bookId, CancellationToken ct = default)
    {
        ActiveSession session;

        lock (_gate)
        {
            if (_disposed || _active.ContainsKey(bookId))
            {
                return;
            }

            var now = _clock.Now;
            session = new ActiveSession(bookId, now);
            _active[bookId] = session;
        }

        // 立即落一条空会话：即使进程随后被强杀，统计里也能看到"打开过"
        try
        {
            await PersistAsync(session, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 关窗 / 退出时取消属于正常：id 留 0，End / Flush 会补插
            MarkDirty(session);
        }
        catch (Exception ex)
        {
            // 库被锁、磁盘满都不该影响打开书
            _log.LogWarning(ex, "新建阅读会话失败：{BookId}", bookId);
            MarkDirty(session);
        }
    }

    public void ReportActivity(Guid bookId, int pagesDelta, long charsDelta)
    {
        if (_disposed)
        {
            return;
        }

        // 负数增量归 0：调用方估算误差不该把统计减成负的
        var pages = Math.Max(0, pagesDelta);
        var chars = Math.Max(0L, charsDelta);

        ActiveSession? idleSession = null;

        lock (_gate)
        {
            if (!_active.TryGetValue(bookId, out var session))
            {
                return;
            }

            var now = _clock.Now;

            if (now - session.LastActivityAt > _idleGap)
            {
                // 挂机后回来：旧会话收尾在"上次活动时间"，当前事件另起一段
                idleSession = session;
                session = new ActiveSession(bookId, now);
                _active[bookId] = session;
            }

            session.PagesRead += pages;
            session.CharsRead += chars;
            session.LastActivityAt = now;
            session.Dirty = true;
        }

        if (idleSession is not null)
        {
            // 不阻塞阅读事件：旧会话的收尾交给写信号量后面的异步任务
            _ = PersistQuietlyAsync(idleSession);
        }
    }

    public async Task EndAsync(Guid bookId, CancellationToken ct = default)
    {
        ActiveSession? session;

        lock (_gate)
        {
            if (!_active.Remove(bookId, out session))
            {
                return;
            }
        }

        try
        {
            await PersistAsync(session, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 关窗时调用方可能已经取消了 token：会话已移出活动表，这一条不能丢，补写一次
            await PersistQuietlyAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "收尾阅读会话失败：{BookId}", bookId);
        }
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        List<ActiveSession> pending;

        lock (_gate)
        {
            if (_active.Count == 0)
            {
                return;
            }

            pending = new List<ActiveSession>(_active.Values);
            _active.Clear();
        }

        foreach (var session in pending)
        {
            try
            {
                await PersistAsync(session, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 退出路径上的取消不该造成丢数据：取消一次之后剩下的都按"不取消"写完
                ct = CancellationToken.None;
                await PersistQuietlyAsync(session).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "退出前落库阅读会话失败：{BookId}", session.BookId);
            }
        }
    }

    public bool IsActive(Guid bookId)
    {
        lock (_gate)
        {
            return _active.ContainsKey(bookId);
        }
    }

    /// <summary>定时器到点：把"有活动且未持久化"（以及"插入还没成功"）的会话更新一次。</summary>
    private async Task HeartbeatAsync()
    {
        try
        {
            List<ActiveSession> due;

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                due = _active.Values
                    .Where(s => s.Dirty || s.Id == 0)
                    .ToList();

                // 先认领，避免下一次心跳重复写同一批；写失败会在下面重新置脏
                foreach (var session in due)
                {
                    session.Dirty = false;
                }
            }

            foreach (var session in due)
            {
                // 已被 End / Flush 移出活动表的会话由它们自己收尾，心跳不写旧值
                lock (_gate)
                {
                    if (!_active.TryGetValue(session.BookId, out var current)
                        || !ReferenceEquals(current, session))
                    {
                        continue;
                    }
                }

                try
                {
                    await PersistAsync(session, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "心跳落库阅读会话失败：{BookId}", session.BookId);
                    MarkDirty(session);
                }
            }
        }
        catch (Exception ex)
        {
            // 定时器回调里的异常会变成未观察任务异常，绝不能放出去
            _log.LogWarning(ex, "阅读会话心跳失败");
        }
    }

    /// <summary>收尾一条已移出活动表的会话，失败只记 WRN。</summary>
    private async Task PersistQuietlyAsync(ActiveSession session)
    {
        try
        {
            await PersistAsync(session, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "收尾阅读会话失败：{BookId}", session.BookId);
        }
    }

    /// <summary>
    /// 把一条会话的当前内存值写进库：有 id 就 Update，没有就 Insert 后记住 id。
    /// 写前在锁内重读 id，保证并发的"补插"只会发生一次（End 与心跳同时撞上 id=0 时）。
    /// </summary>
    private async Task PersistAsync(ActiveSession session, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            long id;
            DateTimeOffset startedAt;
            DateTimeOffset endedAt;
            int pagesRead;
            long charsRead;

            lock (_gate)
            {
                id = session.Id;
                startedAt = session.StartedAt;
                endedAt = session.LastActivityAt;
                pagesRead = session.PagesRead;
                charsRead = session.CharsRead;
            }

            if (id == 0)
            {
                var newId = await _repo.InsertSessionAsync(
                        new ReadingSessionWrite(session.BookId, startedAt, endedAt, pagesRead, charsRead),
                        ct)
                    .ConfigureAwait(false);

                if (newId > 0)
                {
                    lock (_gate)
                    {
                        session.Id = newId;
                    }
                }
            }
            else
            {
                await _repo.UpdateSessionAsync(id, endedAt, pagesRead, charsRead, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void MarkDirty(ActiveSession session)
    {
        lock (_gate)
        {
            session.Dirty = true;
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

    /// <summary>一条进行中的会话。字段都在 <c>_gate</c> 里读写。</summary>
    private sealed class ActiveSession
    {
        public ActiveSession(Guid bookId, DateTimeOffset startedAt)
        {
            BookId = bookId;
            StartedAt = startedAt;
            LastActivityAt = startedAt;
        }

        public Guid BookId { get; }

        /// <summary>库里的自增 id。插入失败时保持 0，后续心跳 / End / Flush 会补插。</summary>
        public long Id { get; set; }

        public DateTimeOffset StartedAt { get; }

        /// <summary>最后一次阅读事件的时间。它就是落库时的 ended_at。</summary>
        public DateTimeOffset LastActivityAt { get; set; }

        public int PagesRead { get; set; }

        public long CharsRead { get; set; }

        /// <summary>自上次落库后有没有新活动。</summary>
        public bool Dirty { get; set; }
    }
}
