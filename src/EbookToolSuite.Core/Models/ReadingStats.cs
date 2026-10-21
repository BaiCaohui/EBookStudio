using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>统计页的四个统计范围（T7-3B）。</summary>
public enum ReadingStatsRange
{
    Today,
    Week,
    Month,
    All,
}

/// <summary>
/// 一条阅读会话（reading_sessions 的一行）。
/// 时间列在库里是 Unix 毫秒；这里换成 <see cref="DateTimeOffset"/> 给上层用。
/// </summary>
public sealed record ReadingSessionRecord(
    long Id,
    Guid BookId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int PagesRead,
    long CharsRead)
{
    /// <summary>这条会话的墙钟时长。ended_at 记的是"最后一次有阅读事件的时间"。</summary>
    public TimeSpan Duration => EndedAt > StartedAt ? EndedAt - StartedAt : TimeSpan.Zero;
}

/// <summary>写入一条新会话（打开书时）。</summary>
public sealed record ReadingSessionWrite(
    Guid BookId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int PagesRead,
    long CharsRead);

/// <summary>某个自然日（本地时区）的阅读时长。</summary>
public sealed record DailyReading(DateOnly Day, TimeSpan Duration);

/// <summary>选定范围内的汇总。</summary>
public sealed record ReadingRangeTotals(TimeSpan Duration, int PagesRead, long CharsRead, int FinishedBooks);

/// <summary>书库的阅读状态分布（书库构成的一部分）。</summary>
public sealed record ReadingStatusCount(ReadingStatus Status, int Count);

/// <summary>书库的格式分布。按 book_formats 的文件份数统计（一本多格式的书会各算一次）。</summary>
public sealed record ReadingFormatCount(BookFormatKind Format, int Count);

/// <summary>最近在读的一本书。</summary>
public sealed record RecentReadingBook(
    Guid BookId,
    string Title,
    double Percent,
    DateTimeOffset LastReadAt);

/// <summary>一本书的阅读状态与最后阅读时间（聚合"读完的书数"用）。</summary>
public sealed record BookReadingState(
    Guid BookId,
    ReadingStatus Status,
    DateTimeOffset? LastReadAt);

/// <summary>
/// 统计页一次查询的全部结果。
/// 三个查询（范围汇总 / 近 14 天趋势 / 书库构成）合成一个快照，页面只取一次。
/// </summary>
public sealed record ReadingStatsSnapshot
{
    public required ReadingStatsRange Range { get; init; }

    /// <summary>生成这份快照的时间（本地时区由页面显示）。</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    public required ReadingRangeTotals Totals { get; init; }

    /// <summary>连续阅读天数（本地自然日）。从未读过为 0。</summary>
    public required int StreakDays { get; init; }

    /// <summary>近 14 天（含今天）每天阅读时长，按日期升序。</summary>
    public required IReadOnlyList<DailyReading> Trend { get; init; }

    public required IReadOnlyList<ReadingStatusCount> StatusCounts { get; init; }

    public required IReadOnlyList<ReadingFormatCount> FormatCounts { get; init; }

    /// <summary>最近在读（按最后阅读时间倒序），最多 8 本。</summary>
    public required IReadOnlyList<RecentReadingBook> RecentBooks { get; init; }

    public required int TotalBooks { get; init; }

    /// <summary>库里是否已经有任何阅读会话。空态据此判断（不是"这个范围没有"，是"从来没读过"）。</summary>
    public required bool HasSessions { get; init; }

    /// <summary>
    /// 平均阅读速度（字/分钟）。没有字数数据（PDF / 漫画）或时长太短时为 null，页面显示"—"。
    /// </summary>
    public double? CharsPerMinute { get; init; }
}
