using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// reading_sessions 的读写与统计页需要的只读查询（T7-3B）。
/// 采集写入与聚合读取分开成两个服务，这里只负责"库能存什么、能取什么"。
/// </summary>
public interface IReadingStatsRepository
{
    /// <summary>插入一条会话，返回自增 id。</summary>
    Task<long> InsertSessionAsync(ReadingSessionWrite write, CancellationToken ct = default);

    /// <summary>
    /// 更新一条会话的收尾时间与累计量。会话不存在时静默返回 false（不抛）。
    /// </summary>
    Task<bool> UpdateSessionAsync(
        long sessionId,
        DateTimeOffset endedAt,
        int pagesRead,
        long charsRead,
        CancellationToken ct = default);

    /// <summary>全部会话，按 started_at 升序。统计页一次读全量再在内存里按本地日拆分。</summary>
    Task<IReadOnlyList<ReadingSessionRecord>> ListSessionsAsync(CancellationToken ct = default);

    /// <summary>书库每本书的阅读状态与最后阅读时间（没有进度的书 LastReadAt 为 null）。</summary>
    Task<IReadOnlyList<BookReadingState>> ListBookReadingStatesAsync(CancellationToken ct = default);

    /// <summary>格式分布：按 book_formats 的文件份数统计。</summary>
    Task<IReadOnlyList<ReadingFormatCount>> ListFormatCountsAsync(CancellationToken ct = default);

    /// <summary>最近在读：按 reading_progress.updated_at 倒序，最多 take 本。</summary>
    Task<IReadOnlyList<RecentReadingBook>> ListRecentBooksAsync(
        int take = 8,
        CancellationToken ct = default);

    /// <summary>书库总本数。</summary>
    Task<int> CountBooksAsync(CancellationToken ct = default);
}
