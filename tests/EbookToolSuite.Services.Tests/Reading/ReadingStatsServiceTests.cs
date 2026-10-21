using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Services.Reading;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests.Reading;

/// <summary>
/// 统计查询服务（T7-3B）：只验"读 5 个查询 → 交给聚合器 → 用本地时区"这条线，
/// 聚合口径本身由 <see cref="ReadingStatsAggregatorTests"/> 覆盖。
/// </summary>
public sealed class ReadingStatsServiceTests
{
    private static readonly TimeZoneInfo Local = TimeZoneInfo.Local;

    private static DateTimeOffset LocalNoon()
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Local).DateTime);
        var local = today.ToDateTime(new TimeOnly(12, 0));
        return new DateTimeOffset(local, Local.GetUtcOffset(local));
    }

    private static DateTimeOffset LocalAt(DateOnly day, int hour)
    {
        var local = day.ToDateTime(new TimeOnly(hour, 0));
        return new DateTimeOffset(local, Local.GetUtcOffset(local));
    }

    private static DateOnly LocalToday()
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Local).DateTime);

    private static DateOnly WeekStart(DateOnly day)
        => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static ReadingStatsService NewService(FakeReadingStatsRepository repo, FakeClock clock)
        => new(repo, clock, NullLogger<ReadingStatsService>.Instance);

    [Fact]
    public async Task GetAsync_Today_ReadsFiveQueriesAndAggregates()
    {
        var now = LocalNoon();
        var clock = new FakeClock(now);
        var repo = new FakeReadingStatsRepository { TotalBooks = 42 };

        var bookId = Guid.NewGuid();
        repo.AddSession(new ReadingSessionRecord(
            1, bookId, now.AddHours(-4), now.AddHours(-3), PagesRead: 10, CharsRead: 3000));

        repo.BookStates.AddRange(
        [
            new BookReadingState(bookId, ReadingStatus.Finished, now.AddHours(-3)),
            new BookReadingState(Guid.NewGuid(), ReadingStatus.Reading, null),
            new BookReadingState(Guid.NewGuid(), ReadingStatus.Unread, null),
        ]);
        repo.FormatCounts.Add(new ReadingFormatCount(BookFormatKind.Epub, 3));
        repo.RecentBooks.Add(new RecentReadingBook(bookId, "测试书", 0.42, now.AddHours(-3)));

        var service = NewService(repo, clock);
        var snapshot = await service.GetAsync(ReadingStatsRange.Today);

        Assert.Equal(ReadingStatsRange.Today, snapshot.Range);
        Assert.Equal(now, snapshot.GeneratedAt);
        Assert.Equal(TimeSpan.FromHours(1), snapshot.Totals.Duration);
        Assert.Equal(10, snapshot.Totals.PagesRead);
        Assert.Equal(3000L, snapshot.Totals.CharsRead);
        Assert.Equal(1, snapshot.Totals.FinishedBooks);
        Assert.Equal(50.0, snapshot.CharsPerMinute!.Value, 3);
        Assert.Equal(1, snapshot.StreakDays);
        Assert.Equal(14, snapshot.Trend.Count);
        Assert.Equal(42, snapshot.TotalBooks);
        Assert.True(snapshot.HasSessions);
        Assert.Single(snapshot.FormatCounts);
        Assert.Single(snapshot.RecentBooks);
        Assert.Equal(4, snapshot.StatusCounts.Count);

        // 五个查询各读一次；最近在读固定 take=8
        Assert.Equal(1, repo.SessionsQueryCount);
        Assert.Equal(1, repo.BooksQueryCount);
        Assert.Equal(1, repo.FormatsQueryCount);
        Assert.Equal(1, repo.RecentQueryCount);
        Assert.Equal(1, repo.CountQueryCount);
        Assert.Equal(8, repo.LastRecentTake);
    }

    [Fact]
    public async Task GetAsync_Week_UsesLocalMondayBoundary()
    {
        var now = LocalNoon();
        var clock = new FakeClock(now);
        var repo = new FakeReadingStatsRepository { TotalBooks = 2 };

        var today = LocalToday();
        var monday = WeekStart(today);
        var bookId = Guid.NewGuid();

        // 本周一 10:00-11:00（必在本周范围内）
        repo.AddSession(new ReadingSessionRecord(
            1, bookId, LocalAt(monday, 10), LocalAt(monday, 11), PagesRead: 7, CharsRead: 0));

        // 上周日 10:00-11:00（必不在本周范围内）
        var previousSunday = monday.AddDays(-1);
        repo.AddSession(new ReadingSessionRecord(
            2, bookId, LocalAt(previousSunday, 10), LocalAt(previousSunday, 11), PagesRead: 3, CharsRead: 0));

        var snapshot = await NewService(repo, clock).GetAsync(ReadingStatsRange.Week);

        Assert.Equal(ReadingStatsRange.Week, snapshot.Range);
        Assert.Equal(TimeSpan.FromHours(1), snapshot.Totals.Duration);
        Assert.Equal(7, snapshot.Totals.PagesRead);
    }

    [Fact]
    public async Task GetAsync_All_IncludesEverySession()
    {
        var now = LocalNoon();
        var clock = new FakeClock(now);
        var repo = new FakeReadingStatsRepository { TotalBooks = 1 };

        var bookId = Guid.NewGuid();
        repo.AddSession(new ReadingSessionRecord(
            1, bookId, now.AddDays(-90), now.AddDays(-90).AddHours(1), PagesRead: 4, CharsRead: 400));
        repo.AddSession(new ReadingSessionRecord(
            2, bookId, now.AddHours(-2), now.AddHours(-1), PagesRead: 2, CharsRead: 200));

        var snapshot = await NewService(repo, clock).GetAsync(ReadingStatsRange.All);

        Assert.Equal(TimeSpan.FromHours(2), snapshot.Totals.Duration);
        Assert.Equal(6, snapshot.Totals.PagesRead);
        Assert.Equal(600L, snapshot.Totals.CharsRead);
    }

    [Fact]
    public async Task GetAsync_EmptyLibrary_ReturnsEmptySnapshotWithoutThrowing()
    {
        var now = LocalNoon();
        var repo = new FakeReadingStatsRepository();
        var service = NewService(repo, new FakeClock(now));

        var snapshot = await service.GetAsync(ReadingStatsRange.All);

        Assert.False(snapshot.HasSessions);
        Assert.Equal(0, snapshot.TotalBooks);
        Assert.Equal(TimeSpan.Zero, snapshot.Totals.Duration);
        Assert.Equal(0, snapshot.StreakDays);
        Assert.Equal(14, snapshot.Trend.Count);
        Assert.All(snapshot.Trend, day => Assert.Equal(TimeSpan.Zero, day.Duration));
        Assert.Equal(4, snapshot.StatusCounts.Count);
        Assert.All(snapshot.StatusCounts, status => Assert.Equal(0, status.Count));
        Assert.Null(snapshot.CharsPerMinute);
        Assert.Empty(snapshot.RecentBooks);
        Assert.Empty(snapshot.FormatCounts);
    }

    [Fact]
    public async Task GetAsync_RepositoryFailure_IsNotSwallowed()
    {
        var clock = new FakeClock(LocalNoon());
        var repo = new FakeReadingStatsRepository
        {
            ReadError = new InvalidOperationException("库坏了"),
        };

        // 统计页要能显示错误态：服务不能把取数异常吞掉换成一份"看起来正常"的空数据
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService(repo, clock).GetAsync(ReadingStatsRange.Today));
    }
}
