using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// conversion_tasks 表的读写（执行计划 T2-4）。
/// 队列每改一次状态就整行 Update 一次，不做增量字段更新。
/// </summary>
public interface IConversionTaskRepository
{
    Task AddAsync(ConversionTask task, CancellationToken ct = default);

    /// <summary>整行覆盖写。队列在状态、进度、错误、重试计数变化时调。</summary>
    Task UpdateAsync(ConversionTask task, CancellationToken ct = default);

    Task<ConversionTask?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>未终态的任务（排队/运行/等待重试），按 created_at 升序  启动时重建队列用。</summary>
    Task<IReadOnlyList<ConversionTask>> ListActiveAsync(CancellationToken ct = default);

    /// <summary>最近的若干条（含终态），按 created_at 降序  启动时把上次的历史也载进来。</summary>
    Task<IReadOnlyList<ConversionTask>> ListRecentAsync(int limit = 200, CancellationToken ct = default);

    /// <summary>
    /// 崩溃恢复：把 Running / WaitingRetry 一律改回 Queued，并清空进度与开始时间。
    /// 返回改了几条。幂等（重复调第二次返回 0）。
    /// </summary>
    Task<int> ResetInterruptedAsync(CancellationToken ct = default);

    /// <summary>删掉一条（用户点"移除"）。</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>删掉所有终态任务（用户点"清除已完成"）。返回删了几条。</summary>
    Task<int> DeleteFinishedAsync(CancellationToken ct = default);
}
