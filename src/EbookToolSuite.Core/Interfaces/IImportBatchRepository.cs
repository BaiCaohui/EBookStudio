using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// import_batch 表的读写（执行计划 T2-1）。活动中心按这张表列历史。
/// </summary>
public interface IImportBatchRepository
{
    Task AddAsync(ImportBatch batch, CancellationToken ct = default);

    /// <summary>整行覆盖写（队列在批次结束时调一次，取消时也调一次）。</summary>
    Task UpdateAsync(ImportBatch batch, CancellationToken ct = default);

    Task<ImportBatch?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>最近的批次，按 started_at 倒序。活动中心用。</summary>
    Task<IReadOnlyList<ImportBatch>> ListRecentAsync(int limit = 50, CancellationToken ct = default);
}