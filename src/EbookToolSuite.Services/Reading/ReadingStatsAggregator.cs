using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Services.Reading;

/// <summary>
/// 阅读统计的内存聚合（T7-3B）。口径见 docs/DECISIONS.md 的 T7-3B 条目。
/// </summary>
/// <remarks>
/// 【为什么是纯静态】本地日拆分、范围边界、连续天数这些口径是这一期的验收重点，
/// 把它和仓储、界面解耦之后，单测可以造任意天数的会话直接断言，不需要真库。
/// 会话在库里按 Unix 毫秒存，这里统一换成 <see cref="DateTimeOffset"/> 后按本地时区求重叠。
/// </remarks>
public static class ReadingStatsAggregator
{
    /// <summary>趋势固定为含今天在内的近 14 天。</summary>
    public const int TrendDays = 14;

    /// <summary>状态分布固定输出四行（缺的补 0）。</summary>
    private static readonly ReadingStatus[] AllStatuses =
    [
        ReadingStatus.Unread,
        ReadingStatus.Reading,
        ReadingStatus.Finished,
        ReadingStatus.Abandoned,
    ];

    /// <summary>把会话、书库状态与范围口径合成一份统计快照。</summary>
    /// <param name="sessions">全部会话（顺序无所谓，内部按时间求重叠）。</param>
    /// <param name="books">每本书的阅读状态与最后阅读时间。</param>
    /// <param name="formats">格式分布，原样带进快照。</param>
    /// <param name="recent">最近在读，原样带进快照。</param>
    /// <param name="totalBooks">书库总本数。</param>
    /// <param name="range">统计范围。</param>
    /// <param name="now">当前时间（生成时间与本地日边界的基准）。</param>
    /// <param name="tz">本地时区。</param>
    public static ReadingStatsSnapshot Aggregate(
        IEnumerable<ReadingSessionRecord> sessions,
        IReadOnlyList<BookReadingState> books,
        IReadOnlyList<ReadingFormatCount> formats,
        IReadOnlyList<RecentReadingBook> recent,
        int totalBooks,
        ReadingStatsRange range,
        DateTimeOffset now,
        TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(books);
        ArgumentNullException.ThrowIfNull(formats);
        ArgumentNullException.ThrowIfNull(recent);
        ArgumentNullException.ThrowIfNull(tz);

        var sessionList = sessions as IReadOnlyList<ReadingSessionRecord> ?? sessions.ToList();

        // ---------- 本地日边界 ----------

        var localNow = TimeZoneInfo.ConvertTime(now, tz);
        var today = DateOnly.FromDateTime(localNow.DateTime);

        // 左闭右开：[范围第一天 0 点, 范围结束日 0 点)。All 没有下界。
        var (fromDay, toDay) = range switch
        {
            ReadingStatsRange.Today => (today, today.AddDays(1)),
            ReadingStatsRange.Week => (MondayOf(today), MondayOf(today).AddDays(7)),
            ReadingStatsRange.Month => (FirstOfMonth(today), FirstOfMonth(today).AddMonths(1)),
            _ => ((DateOnly?)null, today.AddDays(1)),
        };

        var fromInstant = fromDay is { } low ? StartOfLocalDay(low, tz) : (DateTimeOffset?)null;
        var toInstant = StartOfLocalDay(toDay, tz);

        // ---------- 会话按本地日拆分 ----------

        var daily = new Dictionary<DateOnly, TimeSpan>();
        var pagesRead = 0;
        var charsRead = 0L;

        foreach (var session in sessionList)
        {
            // pages/chars 只记到 startedAt 所在日，避免跨天会话在两天里各算一次
            if (IsInRange(session.StartedAt, fromInstant, toInstant))
            {
                pagesRead += session.PagesRead;
                charsRead += session.CharsRead;
            }

            // ended_at <= started_at 时长为 0：跳过即可，pages/chars 已经在上面算过
            if (session.Duration <= TimeSpan.Zero)
            {
                continue;
            }

            var startLocal = TimeZoneInfo.ConvertTime(session.StartedAt, tz);
            var endLocal = TimeZoneInfo.ConvertTime(session.EndedAt, tz);
            var startDay = DateOnly.FromDateTime(startLocal.DateTime);
            var endDay = DateOnly.FromDateTime(endLocal.DateTime);

            for (var day = startDay; day <= endDay; day = day.AddDays(1))
            {
                var dayStart = StartOfLocalDay(day, tz);
                var dayEnd = StartOfLocalDay(day.AddDays(1), tz);

                var overlapStart = session.StartedAt > dayStart ? session.StartedAt : dayStart;
                var overlapEnd = session.EndedAt < dayEnd ? session.EndedAt : dayEnd;

                if (overlapEnd > overlapStart)
                {
                    daily[day] = daily.GetValueOrDefault(day) + (overlapEnd - overlapStart);
                }
            }
        }

        // ---------- 范围汇总 ----------

        var duration = TimeSpan.Zero;

        foreach (var (day, dayDuration) in daily)
        {
            if (fromDay is { } rangeStart && day < rangeStart)
            {
                continue;
            }

            // All 也只在"到明天 0 点"之前结算，未来时间的会话不算进任何范围
            if (day >= toDay)
            {
                continue;
            }

            duration += dayDuration;
        }

        var finishedBooks = books.Count(b => b.Status == ReadingStatus.Finished
            && (range == ReadingStatsRange.All
                || (b.LastReadAt is { } lastReadAt && IsInRange(lastReadAt, fromInstant, toInstant))));

        // ---------- 趋势：含今天在内的近 14 天，升序、没读的补 0 ----------

        var trend = new List<DailyReading>(TrendDays);

        for (var i = TrendDays - 1; i >= 0; i--)
        {
            var day = today.AddDays(-i);
            trend.Add(new DailyReading(day, daily.GetValueOrDefault(day)));
        }

        // ---------- 连续天数 ----------

        var streakDays = ComputeStreak(daily, today);

        // ---------- 书库构成 ----------

        var statusCounts = new List<ReadingStatusCount>(AllStatuses.Length);

        foreach (var status in AllStatuses)
        {
            statusCounts.Add(new ReadingStatusCount(status, books.Count(b => b.Status == status)));
        }

        var charsPerMinute = charsRead > 0 && duration.TotalMinutes > 0
            ? charsRead / duration.TotalMinutes
            : (double?)null;

        return new ReadingStatsSnapshot
        {
            Range = range,
            GeneratedAt = now,
            Totals = new ReadingRangeTotals(duration, pagesRead, charsRead, finishedBooks),
            StreakDays = streakDays,
            Trend = trend,
            StatusCounts = statusCounts,
            FormatCounts = formats,
            RecentBooks = recent,
            TotalBooks = totalBooks,
            HasSessions = sessionList.Count > 0,
            CharsPerMinute = charsPerMinute,
        };
    }

    /// <summary>
    /// 连续阅读天数：今天有就从今天往回数；今天没有但昨天有就从昨天往回数；断一天即停。
    /// 只认"时长 &gt; 0"的本地自然日。
    /// </summary>
    private static int ComputeStreak(IReadOnlyDictionary<DateOnly, TimeSpan> daily, DateOnly today)
    {
        DateOnly? cursor = HasReading(daily, today)
            ? today
            : HasReading(daily, today.AddDays(-1))
                ? today.AddDays(-1)
                : null;

        if (cursor is not { } day)
        {
            return 0;
        }

        var streak = 0;

        while (HasReading(daily, day))
        {
            streak++;
            day = day.AddDays(-1);
        }

        return streak;
    }

    private static bool HasReading(IReadOnlyDictionary<DateOnly, TimeSpan> daily, DateOnly day)
        => daily.GetValueOrDefault(day) > TimeSpan.Zero;

    private static bool IsInRange(DateTimeOffset instant, DateTimeOffset? from, DateTimeOffset to)
        => (from is not { } low || instant >= low) && instant < to;

    /// <summary>本地时区下某一天的 0 点。跨夏令时也成立（用当天的实际偏移）。</summary>
    private static DateTimeOffset StartOfLocalDay(DateOnly day, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    /// <summary>本周一。DayOfWeek 里周日是 0，先换算成"距周一几天"。</summary>
    private static DateOnly MondayOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static DateOnly FirstOfMonth(DateOnly day) => new(day.Year, day.Month, 1);
}
