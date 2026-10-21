using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Reading;

/// <summary>
/// 统计页的聚合查询（T7-3B）：读 5 个只读查询，交给 <see cref="ReadingStatsAggregator"/> 算快照。
/// </summary>
/// <remarks>
/// 【为什么不在这里算口径】口径要在没有界面的单测里验，所以聚合逻辑做成纯静态；
/// 这个服务只负责"取数 + 选本地时区"，取数失败不吞（统计页要能显示错误态，
/// 吞掉异常只会变成一份看起来正常的空数据）。
/// </remarks>
public sealed class ReadingStatsService : IReadingStatsService
{
    /// <summary>最近在读固定取 8 本。</summary>
    public const int RecentBooksTake = 8;

    private readonly IReadingStatsRepository _repo;
    private readonly IClock _clock;
    private readonly ILogger<ReadingStatsService> _log;

    public ReadingStatsService(
        IReadingStatsRepository repo,
        IClock clock,
        ILogger<ReadingStatsService> log)
    {
        _repo = repo;
        _clock = clock;
        _log = log;
    }

    public async Task<ReadingStatsSnapshot> GetAsync(
        ReadingStatsRange range, CancellationToken ct = default)
    {
        // SQLite 单连接：顺序读比并发读更稳（并发只会排队，还可能撞上"库忙"）
        var sessions = await _repo.ListSessionsAsync(ct).ConfigureAwait(false);
        var books = await _repo.ListBookReadingStatesAsync(ct).ConfigureAwait(false);
        var formats = await _repo.ListFormatCountsAsync(ct).ConfigureAwait(false);
        var recent = await _repo.ListRecentBooksAsync(RecentBooksTake, ct).ConfigureAwait(false);
        var totalBooks = await _repo.CountBooksAsync(ct).ConfigureAwait(false);

        var snapshot = ReadingStatsAggregator.Aggregate(
            sessions,
            books,
            formats,
            recent,
            totalBooks,
            range,
            _clock.Now,
            TimeZoneInfo.Local);

        _log.LogDebug(
            "阅读统计快照：范围={Range}，会话={SessionCount} 条，时长={Duration}",
            range,
            sessions.Count,
            snapshot.Totals.Duration);

        return snapshot;
    }
}
