using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 统计页的聚合查询（T7-3B）。口径见 docs/DECISIONS.md 的 T7-3B 条目。
/// </summary>
public interface IReadingStatsService
{
    /// <summary>按选定的范围取一份统计快照。</summary>
    Task<ReadingStatsSnapshot> GetAsync(ReadingStatsRange range, CancellationToken ct = default);
}
