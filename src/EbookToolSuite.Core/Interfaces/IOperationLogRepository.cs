using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>operation_log 的读写（T3-6）。</summary>
public interface IOperationLogRepository
{
    /// <summary>追加一条。返回落库后的 Id。</summary>
    Task<long> AddAsync(OperationLogEntry entry, CancellationToken ct = default);

    /// <summary>最近若干条（活动中心的时间线）。</summary>
    Task<IReadOnlyList<OperationLogEntry>> ListRecentAsync(int take = 100, CancellationToken ct = default);

    /// <summary>sample的操作历史（详情页用）。</summary>
    Task<IReadOnlyList<OperationLogEntry>> ListForBookAsync(Guid bookId, int take = 50, CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);
}
