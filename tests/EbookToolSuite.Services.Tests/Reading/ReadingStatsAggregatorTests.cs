using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Services.Reading;

namespace EbookToolSuite.Services.Tests.Reading;

/// <summary>
/// 统计聚合口径（T7-3B）。纯内存函数，直接造会话断言：
/// 本地日拆分、四个范围边界、趋势 14 天、连续天数、阅读速度。
/// </summary>
public sealed class ReadingStatsAggregatorTests
{
    /// <summary>
    /// 固定 +08:00 的测试时区：断言不依赖跑测试的机器在哪个时区。
    /// （仓库目标用户在国内；正式代码用的是 TimeZoneInfo.Local。）
    /// </summary>
    private static readonly TimeZoneInfo Tz =
        TimeZoneInfo.CreateCustomTimeZone("Test+08", TimeSpan.FromHours(8), "Test+08", "Test+08");

    private static DateTimeOffset At(int year, int month, int day, int hour = 0, int minute = 0)
        => new(year, month, day, hour, minute, 0, TimeSpan.FromHours(8));

    private static ReadingSessionRecord Session(
        Guid bookId,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        int pages = 0,
        long chars = 0)
        => new(0, bookId, startedAt, endedAt, pages, chars);

    private static ReadingStatsSnapshot Aggregate(
        IReadOnlyList<ReadingSessionRecord> sessions,
        DateTimeOffset now,
        ReadingStatsRange range,
        IReadOnlyList<BookReadingState>? books = null,
        IReadOnlyList<ReadingFormatCount>? formats = null,
        IReadOnlyList<RecentReadingBook>? recent = null,
        int totalBooks = 0)
        => ReadingStatsAggregator.Aggregate(
            sessions,
            books ?? [],
            formats ?? [],
            recent ?? [],
            totalBooks,
            range,
            now,
            Tz);

    // ---------- 范围边界 ----------

    [Fact]
    public void Today_CountsOnlyLocalToday()
    {
        // 2026-10-21 是周三
        var now = At(2026, 10, 21, 12);
        var book = Guid.NewGuid();

        var sessions = new[]
        {
            Session(book, At(2026, 10, 21, 8), At(2026, 10, 21, 9), pages: 10, chars: 3000),
            Session(book, At(2026, 10, 20, 8), At(2026, 10, 20, 9), pages: 5, chars: 1000),
        };

        var snapshot = Aggregate(sessions, now, ReadingStatsRange.Today);

        Assert.Equal(TimeSpan.FromHours(1), snapshot.Totals.Duration);
        Assert.Equal(10, snapshot.Totals.PagesRead);
        Assert.Equal(3000L, snapshot.Totals.CharsRead);
        Assert.Equal(2, snapshot.StreakDays);
        Assert.Equal(TimeSpan.FromHours(1), snapshot.Trend[^1].Duration);
        Assert.Equal(TimeSpan.FromHours(1), snapshot.Trend[^2].Duration);
    }

    [Fact]
    public void Week_StartsOnMonday()
    {
        // 2026-10-21 周三，本周一 = 10-19
        var now = At(2026, 10, 21, 12);
        var book = Guid.NewGuid();

        var sessions = new[]
        {
            Session(book, At(2026, 10, 19, 10), At(2026, 10, 19, 11), pages: 7),   // 本周一
            Session(book, At(2026, 10, 18, 10), At(2026, 10, 18, 11), pages: 3),   // 上周日
            Session(book, At(2026, 10, 21, 9), At(2026, 10, 21, 9, 30), pages: 2), // 今天
        };

        var snapshot = Aggregate(sessions, now, ReadingStatsRange.Week);

        Assert.Equal(TimeSpan.FromMinutes(90), snapshot.Totals.Duration);
        Assert.Equal(9, snapshot.Totals.PagesRead);

        // 上周日那条仍进趋势，但不进本周汇总
        Assert.Equal(TimeSpan.FromHours(1), snapshot.Trend[^4].Duration);
    }

    [Fact]
    public void Month_StartsOnFirstDay()
    {
        var now = At(2026, 10, 21, 12);
        var book = Guid.NewGuid();

        var sessions = new[]
        {
            Session(book, At(2026, 10, 1, 0, 30), At(2026, 10, 1, 1, 30), pages: 4),  // 本月 1 日
            Session(book, At(2026, 9, 30, 22), At(2026, 9, 30, 23), pages: 2),         // 上月
        };

        var snapshot = Aggregate(sessions, now, ReadingStatsRange.Month);

        Assert.Equal(TimeSpan.FromHours(1), snapshot.Totals.Duration);
        Assert.Equal(4, snapshot.Totals.PagesRead);
    }

    [Fact]
    public void All_IncludesHistoryUpToTomorrow()
    {
        var now = At(2026, 10, 21, 12);
        var book = Guid.NewGuid();

        var sessions = new[]
        {
            Session(book, At(2026, 9, 5, 10), At(2026, 9, 5, 11), pages: 4, chars: 4000), // 一个多月前
            Session(book, At(2026, 10, 21, 9), At(2026, 10, 21, 9, 30), pages: 2, chars: 600), // 今天
        };

        var snapshot = Aggregate(sessions, now, ReadingStatsRange.All);

        Assert.Equal(TimeSpan.FromMinutes(90), snapshot.Totals.Duration);
        Assert.Equal(6, snapshot.Totals.PagesRead);
        Assert.Equal(4600L, snapshot.Totals.CharsRead);

        // 昨天没读，所以连续天数只有今天一天
        Assert.Equal(1, snapshot.StreakDays);

        // 全部范围也不统计"明天以后"的会话
        Assert.Equal(TimeSpan.FromMinutes(30), snapshot.Trend[^1].Duration);
    }

    [Fact]
    public void CrossDay_SplitsDurationAndKeepsPagesOnStartDay()
    {
        var now = At(2026, 10, 21, 12);
        var book = Guid.NewGuid();

        // 10-20 23:00 → 10-21 01:00：两天各 60 分钟，pages/chars 只记到 10-20
        var sessions = new[]
        {
            Session(book, At(2026, 10, 20, 23), At(2026, 10, 21, 1), pages: 10, chars: 1000),
        };

        var today = Aggregate(sessions, now, ReadingStatsRange.Today);
        Assert.Equal(TimeSpan.FromHours(1), today.Totals.Duration);
        Assert.Equal(0, today.Totals.PagesRead);
        Assert.Equal(0L, today.Totals.CharsRead);
        Assert.Equal(TimeSpan.FromHours(1), today.Trend[^1].Duration);

        var week = Aggregate(sessions, now, ReadingStatsRange.Week);
        Assert.Equal(TimeSpan.FromHours(2), week.Totals.Duration);
        Assert.Equal(10, week.Totals.PagesRead);
        Assert.Equal(1000L, week.Totals.CharsRead);

        // 拆分后昨天也是 60 分钟
        Assert.Equal(TimeSpan.FromHours(1), week.Trend[^2].Duration);
    }

    [Fact]
    public void CrossDay_MonthBoundary_SplitsIntoTwoMonths()
    {
        // 09-30 23:30 → 10-01 00:30 跨月：本月只算 10-01 那 30 分钟
        var now = At(2026, 10, 21, 12);
        var book = Guid.NewGuid();

        var sessions = new[]
        {
            Session(book, At(2026, 9, 30, 23, 30), At(2026, 10, 1, 0, 30), pages: 6),
        };

        var month = Aggregate(sessions, now, ReadingStatsRange.Month);

        Assert.Equal(TimeSpan.FromMinutes(30), month.Totals.Duration);
        Assert.Equal(0, month.Totals.PagesRead); // 起点在 9 月，不计入本月 pages
    }

    // ---------- 连续天数 ----------

    [Fact]
    public void Streak_BreaksAtFirstMissingDay()
    {
        var now = At(2026, 10, 21, 12);
        var book = Guid.NewGuid();

        var sessions = new[]
        {
            Session(book, At(2026, 10, 21, 10), At(2026, 10, 21, 11)), // 今天
            Session(book, At(2026, 10, 20, 10), At(2026, 10, 20, 11)), // 昨天
            // 10-19 断一天
            Session(book, At(2026, 10, 18, 10), At(2026, 10, 18, 11)),
        };

        Assert.Equal(2, Aggregate(sessions, now, ReadingStatsRange.Today).StreakDays);
    }

    [Fact]
    public void Streak_TodayUnread_StartsFromYesterday()
    {
        var now = At(2026, 10, 21, 12);
        var book = Guid.NewGuid();

        var sessions = new[]
        {
            Session(book, At(2026, 10, 20, 10), At(2026, 10, 20, 11)), // 昨天
            Session(book, At(2026, 10, 19, 10), At(2026, 10, 19, 11)), // 前天
        };

        var snapshot = Aggregate(sessions, now, ReadingStatsRange.Today);

        Assert.Equal(2, snapshot.StreakDays);
        Assert.Equal(TimeSpan.Zero, snapshot.Totals.Duration);
        Assert.Equal(TimeSpan.Zero, snapshot.Trend[^1].Duration);
    }

    [Fact]
    public void Streak_NoSessions_IsZero()
    {
        var snapshot = Aggregate([], At(2026, 10, 21, 12), ReadingStatsRange.Today);

        Assert.Equal(0, snapshot.StreakDays);
        Assert.False(snapshot.HasSessions);
        Assert.Equal(TimeSpan.Zero, snapshot.Totals.Duration);
        Assert.Null(snapshot.CharsPerMinute);
    }

    [Fact]
    public void Streak_ZeroDurationSession_DoesNotCount()
    {
        var now = At(2026, 10, 21, 12);
        var book = Guid.NewGuid();

        // ended == started：打开就关，没有时长，不该算"今天读过"
        var sessions = new[] { Session(book, At(2026, 10, 21, 10), At(2026, 10, 21, 10)) };

        var snapshot = Aggregate(sessions, now, ReadingStatsRange.Today);

        Assert.Equal(0, snapshot.StreakDays);
        Assert.True(snapshot.HasSessions);
    }

    // ---------- 趋势 ----------

    [Fact]
    public void Trend_IsFourteenAscendingLocalDaysWithZeros()
    {
        var now = At(2026, 10, 21, 12);
        var book = Guid.NewGuid();

        var sessions = new[]
        {
            Session(book, At(2026, 10, 21, 9), At(2026, 10, 21, 9, 30)),
        };

        var trend = Aggregate(sessions, now, ReadingStatsRange.Today).Trend;

        Assert.Equal(14, trend.Count);
        Assert.Equal(new DateOnly(2026, 10, 8), trend[0].Day);   // 含今天在内的 14 天
        Assert.Equal(new DateOnly(2026, 10, 21), trend[^1].Day);

        for (var i = 1; i < trend.Count; i++)
        {
            Assert.True(trend[i].Day > trend[i - 1].Day, "趋势必须按日期升序");
        }

        Assert.Equal(TimeSpan.FromMinutes(30), trend[^1].Duration);
        Assert.All(trend.Take(13), day => Assert.Equal(TimeSpan.Zero, day.Duration));
    }

    // ---------- 阅读速度 ----------

    [Fact]
    public void Speed_UsesCharsOverRangeDuration()
    {
        var now = At(2026, 10, 21, 12);
        var book = Guid.NewGuid();

        var sessions = new[]
        {
            Session(book, At(2026, 10, 21, 9), At(2026, 10, 21, 9, 30), pages: 5, chars: 3000),
        };

        var snapshot = Aggregate(sessions, now, ReadingStatsRange.Today);

        Assert.NotNull(snapshot.CharsPerMinute);
        Assert.Equal(100.0, snapshot.CharsPerMinute!.Value, 3);
    }

    [Fact]
    public void Speed_NullWithoutChars()
    {
        var now = At(2026, 10, 21, 12);
        var book = Guid.NewGuid();

        // PDF / 漫画只有页数，没有字数：不出速度，页面显示"—"
        var sessions = new[]
        {
            Session(book, At(2026, 10, 21, 9), At(2026, 10, 21, 10), pages: 20),
        };

        Assert.Null(Aggregate(sessions, now, ReadingStatsRange.Today).CharsPerMinute);
    }

    // ---------- 书库构成 ----------

    [Fact]
    public void StatusCounts_AlwaysFourRowsInFixedOrder()
    {
        var now = At(2026, 10, 21, 12);
        var books = new[]
        {
            new BookReadingState(Guid.NewGuid(), ReadingStatus.Unread, null),
            new BookReadingState(Guid.NewGuid(), ReadingStatus.Unread, null),
            new BookReadingState(Guid.NewGuid(), ReadingStatus.Reading, now.AddHours(-2)),
            new BookReadingState(Guid.NewGuid(), ReadingStatus.Finished, now.AddHours(-3)),
        };

        var snapshot = Aggregate([], now, ReadingStatsRange.Today, books: books);

        Assert.Equal(4, snapshot.StatusCounts.Count);
        Assert.Equal(
            new[] { ReadingStatus.Unread, ReadingStatus.Reading, ReadingStatus.Finished, ReadingStatus.Abandoned },
            snapshot.StatusCounts.Select(s => s.Status).ToArray());
        Assert.Equal(2, snapshot.StatusCounts[0].Count);
        Assert.Equal(1, snapshot.StatusCounts[1].Count);
        Assert.Equal(1, snapshot.StatusCounts[2].Count);
        Assert.Equal(0, snapshot.StatusCounts[3].Count); // 缺的补 0
    }

    [Fact]
    public void FinishedBooks_FollowSelectedRange()
    {
        var now = At(2026, 10, 21, 12);

        var finishedToday = new BookReadingState(Guid.NewGuid(), ReadingStatus.Finished, At(2026, 10, 21, 9));
        var finishedEarlierThisMonth = new BookReadingState(Guid.NewGuid(), ReadingStatus.Finished, At(2026, 10, 10, 9));
        var finishedLongAgo = new BookReadingState(Guid.NewGuid(), ReadingStatus.Finished, At(2026, 8, 1, 9));
        var stillReading = new BookReadingState(Guid.NewGuid(), ReadingStatus.Reading, At(2026, 10, 21, 9));

        var books = new[] { finishedToday, finishedEarlierThisMonth, finishedLongAgo, stillReading };

        Assert.Equal(1, Aggregate([], now, ReadingStatsRange.Today, books: books).Totals.FinishedBooks);
        Assert.Equal(1, Aggregate([], now, ReadingStatsRange.Week, books: books).Totals.FinishedBooks);
        Assert.Equal(2, Aggregate([], now, ReadingStatsRange.Month, books: books).Totals.FinishedBooks);

        // 全部范围：所有"已读完"的书都算，不看最后阅读时间
        Assert.Equal(3, Aggregate([], now, ReadingStatsRange.All, books: books).Totals.FinishedBooks);
    }

    [Fact]
    public void LibrarySections_ArePassedThroughAndTotalBooksKept()
    {
        var now = At(2026, 10, 21, 12);
        var formatCounts = new[]
        {
            new ReadingFormatCount(BookFormatKind.Epub, 7),
            new ReadingFormatCount(BookFormatKind.Pdf, 2),
        };
        var recent = new[]
        {
            new RecentReadingBook(Guid.NewGuid(), "最近在读", 0.42, now.AddHours(-2)),
        };

        var snapshot = Aggregate(
            [],
            now,
            ReadingStatsRange.Today,
            formats: formatCounts,
            recent: recent,
            totalBooks: 42);

        Assert.Equal(42, snapshot.TotalBooks);
        Assert.Same(formatCounts, snapshot.FormatCounts);
        Assert.Same(recent, snapshot.RecentBooks);
        Assert.Equal(now, snapshot.GeneratedAt);
        Assert.Equal(ReadingStatsRange.Today, snapshot.Range);
    }
}
