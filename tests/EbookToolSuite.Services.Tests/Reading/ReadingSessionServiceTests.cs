using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Services.Reading;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests.Reading;

/// <summary>
/// 阅读会话采集（T7-3B）。用自写的 FakeReadingStatsRepository + FakeClock，
/// 不依赖 Infrastructure 的真库：时间要能随意前进、写库要能按次断言。
/// </summary>
public sealed class ReadingSessionServiceTests
{
    /// <summary>测试用空闲阈值：与正式口径一致（5 分钟）。</summary>
    private static readonly TimeSpan IdleGap = TimeSpan.FromMinutes(5);

    /// <summary>不想让心跳插手的用例给它 1 小时，定时器在测试存活期内不会触发。</summary>
    private static readonly TimeSpan NoHeartbeat = TimeSpan.FromHours(1);

    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 9, 0, 0, TimeSpan.FromHours(8));

    private static ReadingSessionService NewService(
        FakeReadingStatsRepository repo,
        FakeClock clock,
        TimeSpan? heartbeat = null)
        => new(
            repo,
            clock,
            NullLogger<ReadingSessionService>.Instance,
            heartbeat ?? NoHeartbeat,
            IdleGap);

    [Fact]
    public async Task Start_CreatesRowImmediately()
    {
        var repo = new FakeReadingStatsRepository();
        var clock = new FakeClock(T0);
        using var service = NewService(repo, clock);

        var bookId = Guid.NewGuid();
        await service.StartAsync(bookId);

        Assert.True(service.IsActive(bookId));

        var row = Assert.Single(repo.Sessions);
        Assert.Equal(bookId, row.BookId);
        Assert.Equal(T0, row.StartedAt);
        Assert.Equal(T0, row.EndedAt);
        Assert.Equal(0, row.PagesRead);
        Assert.Equal(0L, row.CharsRead);
    }

    [Fact]
    public async Task Start_SameBookTwice_KeepsOneSession()
    {
        var repo = new FakeReadingStatsRepository();
        var clock = new FakeClock(T0);
        using var service = NewService(repo, clock);

        var bookId = Guid.NewGuid();
        await service.StartAsync(bookId);
        await service.StartAsync(bookId);

        Assert.Equal(1, repo.InsertCount);
        Assert.Single(repo.Sessions);
    }

    [Fact]
    public async Task ReportActivity_AccumulatesAndHeartbeatPersists()
    {
        var repo = new FakeReadingStatsRepository();
        var clock = new FakeClock(T0);
        using var service = NewService(repo, clock, TimeSpan.FromMilliseconds(40));

        var bookId = Guid.NewGuid();
        await service.StartAsync(bookId);

        clock.Advance(TimeSpan.FromSeconds(10));
        service.ReportActivity(bookId, 1, 500);

        clock.Advance(TimeSpan.FromSeconds(20));
        service.ReportActivity(bookId, 2, 700);

        // 心跳把"最后活动时间 + 累计量"落库
        await WaitUntilAsync(() =>
        {
            var rows = repo.Sessions;
            return rows.Count == 1
                && rows[0].PagesRead == 3
                && rows[0].CharsRead == 1200
                && rows[0].EndedAt == clock.Now;
        });
    }

    [Fact]
    public async Task ReportActivity_DoesNotWriteBeforeHeartbeat()
    {
        var repo = new FakeReadingStatsRepository();
        var clock = new FakeClock(T0);
        using var service = NewService(repo, clock);

        var bookId = Guid.NewGuid();
        await service.StartAsync(bookId);
        var inserts = repo.InsertCount;

        clock.Advance(TimeSpan.FromSeconds(10));
        service.ReportActivity(bookId, 1, 500);

        // 1 小时的心跳在测试存活期内不会触发：上报只更新内存
        Assert.Equal(inserts, repo.InsertCount);
        Assert.Equal(0, repo.UpdateCount);

        var row = Assert.Single(repo.Sessions);
        Assert.Equal(0, row.PagesRead);
    }

    [Fact]
    public async Task End_FinalizesAtLastActivityAndIsNoLongerActive()
    {
        var repo = new FakeReadingStatsRepository();
        var clock = new FakeClock(T0);
        using var service = NewService(repo, clock);

        var bookId = Guid.NewGuid();
        await service.StartAsync(bookId);

        clock.Advance(TimeSpan.FromSeconds(60));
        service.ReportActivity(bookId, 5, 900);

        // 关窗前又挂了 30 秒没翻页：ended_at 仍是最后一次事件的时间
        clock.Advance(TimeSpan.FromSeconds(30));
        await service.EndAsync(bookId);

        Assert.False(service.IsActive(bookId));

        var row = Assert.Single(repo.Sessions);
        Assert.Equal(T0, row.StartedAt);
        Assert.Equal(T0.AddSeconds(60), row.EndedAt);
        Assert.Equal(TimeSpan.FromSeconds(60), row.Duration);
        Assert.Equal(5, row.PagesRead);
        Assert.Equal(900L, row.CharsRead);
    }

    [Fact]
    public async Task IdleGap_ClosesOldSessionAndStartsNewOne()
    {
        var repo = new FakeReadingStatsRepository();
        var clock = new FakeClock(T0);
        using var service = NewService(repo, clock);

        var bookId = Guid.NewGuid();
        await service.StartAsync(bookId);

        clock.Advance(TimeSpan.FromSeconds(60));
        service.ReportActivity(bookId, 1, 100);

        // 挂机 6 分钟（超过 5 分钟阈值）后回来
        clock.Advance(TimeSpan.FromMinutes(6));
        service.ReportActivity(bookId, 2, 200);

        await service.EndAsync(bookId);

        Assert.False(service.IsActive(bookId));

        var rows = repo.Sessions;
        Assert.Equal(2, rows.Count);

        // 旧会话收尾在"上次活动时间"，空白不计时
        Assert.Equal(T0, rows[0].StartedAt);
        Assert.Equal(T0.AddSeconds(60), rows[0].EndedAt);
        Assert.Equal(1, rows[0].PagesRead);
        Assert.Equal(100L, rows[0].CharsRead);

        // 新会话从恢复阅读那一刻起算
        Assert.Equal(T0.AddMinutes(7), rows[1].StartedAt);
        Assert.Equal(T0.AddMinutes(7), rows[1].EndedAt);
        Assert.Equal(2, rows[1].PagesRead);
        Assert.Equal(200L, rows[1].CharsRead);
    }

    [Fact]
    public async Task ReportActivity_GapBelowIdleThreshold_StaysInSameSession()
    {
        var repo = new FakeReadingStatsRepository();
        var clock = new FakeClock(T0);
        using var service = NewService(repo, clock);

        var bookId = Guid.NewGuid();
        await service.StartAsync(bookId);

        service.ReportActivity(bookId, 1, 100);
        clock.Advance(TimeSpan.FromMinutes(4));
        service.ReportActivity(bookId, 1, 100);

        await service.EndAsync(bookId);

        var row = Assert.Single(repo.Sessions);
        Assert.Equal(2, row.PagesRead);
        Assert.Equal(TimeSpan.FromMinutes(4), row.Duration);
    }

    [Fact]
    public async Task Flush_PersistsAllActiveSessions()
    {
        var repo = new FakeReadingStatsRepository();
        var clock = new FakeClock(T0);
        using var service = NewService(repo, clock);

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await service.StartAsync(first);
        await service.StartAsync(second);

        clock.Advance(TimeSpan.FromSeconds(45));
        service.ReportActivity(first, 3, 600);
        service.ReportActivity(second, 1, 200);

        await service.FlushAsync();

        Assert.False(service.IsActive(first));
        Assert.False(service.IsActive(second));

        var rows = repo.Sessions;
        Assert.Equal(2, rows.Count);
        Assert.Equal(3, rows.Single(r => r.BookId == first).PagesRead);
        Assert.Equal(600L, rows.Single(r => r.BookId == first).CharsRead);
        Assert.Equal(T0.AddSeconds(45), rows.Single(r => r.BookId == first).EndedAt);
        Assert.Equal(1, rows.Single(r => r.BookId == second).PagesRead);
    }

    [Fact]
    public async Task NegativeDeltas_ClampToZero()
    {
        var repo = new FakeReadingStatsRepository();
        var clock = new FakeClock(T0);
        using var service = NewService(repo, clock);

        var bookId = Guid.NewGuid();
        await service.StartAsync(bookId);

        service.ReportActivity(bookId, -5, -100);
        service.ReportActivity(bookId, -1, -1);

        await service.EndAsync(bookId);

        var row = Assert.Single(repo.Sessions);
        Assert.Equal(0, row.PagesRead);
        Assert.Equal(0L, row.CharsRead);
    }

    [Fact]
    public async Task ReportActivity_UnknownBook_DoesNothing()
    {
        var repo = new FakeReadingStatsRepository();
        var clock = new FakeClock(T0);
        using var service = NewService(repo, clock);

        // 没有 Start 就上报（例如关窗后的迟到事件）：不能抛、不能建行
        service.ReportActivity(Guid.NewGuid(), 1, 100);
        await service.EndAsync(Guid.NewGuid());

        Assert.Empty(repo.Sessions);
        Assert.Equal(0, repo.InsertCount);
    }

    [Fact]
    public async Task WriteFailure_IsSwallowedAndSessionStaysInMemory()
    {
        var repo = new FakeReadingStatsRepository { FailWrites = true };
        var clock = new FakeClock(T0);
        using var service = NewService(repo, clock);

        var bookId = Guid.NewGuid();

        // Start / Report / End 都不该把异常抛给调用方
        await service.StartAsync(bookId);
        Assert.True(service.IsActive(bookId));

        service.ReportActivity(bookId, 1, 100);
        await service.EndAsync(bookId);

        Assert.False(service.IsActive(bookId));
        Assert.Empty(repo.Sessions);
    }

    [Fact]
    public async Task Dispose_StopsHeartbeat()
    {
        var repo = new FakeReadingStatsRepository();
        var clock = new FakeClock(T0);
        var service = NewService(repo, clock, TimeSpan.FromMilliseconds(30));

        var bookId = Guid.NewGuid();
        await service.StartAsync(bookId);
        service.ReportActivity(bookId, 1, 100);
        await WaitUntilAsync(() => repo.UpdateCount > 0);

        service.Dispose();

        // 等已在进行中的那一次心跳结束，再确认之后不再有新的写库
        await Task.Delay(100);
        var afterDispose = repo.UpdateCount;
        await Task.Delay(150);
        Assert.Equal(afterDispose, repo.UpdateCount);
    }

    /// <summary>轮询到条件成立，超时判失败。心跳在定时器线程上写，测试只能等。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "等待条件成立超时");
    }
}

/// <summary>
/// 可随意设定 / 前进的时钟（T7-3B 单测共用）。
/// 会话服务的心跳与空闲判定都按这个时间走，不需要真的等 5 分钟。
/// </summary>
internal sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; set; }

    public void Advance(TimeSpan delta) => Now += delta;
}

/// <summary>
/// 内存版 IReadingStatsRepository（T7-3B 单测共用）：
/// 会话行存在 List 里，读查询返回预先塞好的书库数据，写库可切换成"永远失败"。
/// 会话服务从定时器线程写、测试从主线程读，所以全部加锁。
/// </summary>
internal sealed class FakeReadingStatsRepository : IReadingStatsRepository
{
    private readonly object _gate = new();
    private readonly List<ReadingSessionRecord> _sessions = new();
    private long _nextId = 1;

    /// <summary>为 true 时所有写操作抛异常（模拟库被锁 / 磁盘满）。</summary>
    public bool FailWrites { get; set; }

    /// <summary>非 null 时所有读查询抛这个异常（验统计服务不吞异常）。</summary>
    public Exception? ReadError { get; set; }

    public List<BookReadingState> BookStates { get; } = new();

    public List<ReadingFormatCount> FormatCounts { get; } = new();

    public List<RecentReadingBook> RecentBooks { get; } = new();

    public int TotalBooks { get; set; }

    public int InsertCount { get; private set; }

    public int UpdateCount { get; private set; }

    public int SessionsQueryCount { get; private set; }

    public int BooksQueryCount { get; private set; }

    public int FormatsQueryCount { get; private set; }

    public int RecentQueryCount { get; private set; }

    public int CountQueryCount { get; private set; }

    public int LastRecentTake { get; private set; } = -1;

    /// <summary>当前库里的会话行（按 started_at、id 升序的快照）。</summary>
    public IReadOnlyList<ReadingSessionRecord> Sessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.OrderBy(s => s.StartedAt).ThenBy(s => s.Id).ToList();
            }
        }
    }

    /// <summary>直接塞一条会话（模拟历史数据）。</summary>
    public void AddSession(ReadingSessionRecord session)
    {
        lock (_gate)
        {
            _sessions.Add(session);

            if (session.Id >= _nextId)
            {
                _nextId = session.Id + 1;
            }
        }
    }

    public Task<long> InsertSessionAsync(ReadingSessionWrite write, CancellationToken ct = default)
    {
        ThrowIfWriteFails();

        lock (_gate)
        {
            InsertCount++;

            var id = _nextId++;
            _sessions.Add(new ReadingSessionRecord(
                id,
                write.BookId,
                write.StartedAt,
                write.EndedAt,
                write.PagesRead,
                write.CharsRead));

            return Task.FromResult(id);
        }
    }

    public Task<bool> UpdateSessionAsync(
        long sessionId,
        DateTimeOffset endedAt,
        int pagesRead,
        long charsRead,
        CancellationToken ct = default)
    {
        ThrowIfWriteFails();

        lock (_gate)
        {
            UpdateCount++;

            var index = _sessions.FindIndex(s => s.Id == sessionId);

            if (index < 0)
            {
                return Task.FromResult(false);
            }

            _sessions[index] = _sessions[index] with
            {
                EndedAt = endedAt,
                PagesRead = pagesRead,
                CharsRead = charsRead,
            };

            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<ReadingSessionRecord>> ListSessionsAsync(CancellationToken ct = default)
    {
        ThrowIfReadFails();

        lock (_gate)
        {
            SessionsQueryCount++;
            return Task.FromResult<IReadOnlyList<ReadingSessionRecord>>(
                _sessions.OrderBy(s => s.StartedAt).ThenBy(s => s.Id).ToList());
        }
    }

    public Task<IReadOnlyList<BookReadingState>> ListBookReadingStatesAsync(CancellationToken ct = default)
    {
        ThrowIfReadFails();

        lock (_gate)
        {
            BooksQueryCount++;
            return Task.FromResult<IReadOnlyList<BookReadingState>>(BookStates.ToList());
        }
    }

    public Task<IReadOnlyList<ReadingFormatCount>> ListFormatCountsAsync(CancellationToken ct = default)
    {
        ThrowIfReadFails();

        lock (_gate)
        {
            FormatsQueryCount++;
            return Task.FromResult<IReadOnlyList<ReadingFormatCount>>(FormatCounts.ToList());
        }
    }

    public Task<IReadOnlyList<RecentReadingBook>> ListRecentBooksAsync(
        int take = 8,
        CancellationToken ct = default)
    {
        ThrowIfReadFails();

        lock (_gate)
        {
            RecentQueryCount++;
            LastRecentTake = take;
            return Task.FromResult<IReadOnlyList<RecentReadingBook>>(RecentBooks.Take(take).ToList());
        }
    }

    public Task<int> CountBooksAsync(CancellationToken ct = default)
    {
        ThrowIfReadFails();

        lock (_gate)
        {
            CountQueryCount++;
            return Task.FromResult(TotalBooks);
        }
    }

    private void ThrowIfWriteFails()
    {
        if (FailWrites)
        {
            throw new InvalidOperationException("库坏了：写不进去");
        }
    }

    private void ThrowIfReadFails()
    {
        if (ReadError is not null)
        {
            throw ReadError;
        }
    }
}
