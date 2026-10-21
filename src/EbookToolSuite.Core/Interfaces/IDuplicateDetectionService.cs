using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 模糊去重：找「同一本书的不同来源 / 版本」，并把多余的隐藏为副本或忽略整组。
/// 合并动作刻意只做「隐藏 + 可恢复」，绝不删文件（见 7.1.5）。
/// </summary>
public interface IDuplicateDetectionService
{
    Task<DuplicateScanReport> ScanAsync(
        DuplicateScanOptions? options,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken ct = default);

    /// <summary>把 copyBookIds 标成 keptBookId 的副本：书库默认隐藏、文件不动、可恢复。</summary>
    Task<int> HideCopiesAsync(Guid keptBookId, IReadOnlyList<Guid> copyBookIds, CancellationToken ct = default);

    Task<int> RestoreCopiesAsync(IReadOnlyList<Guid> copyBookIds, CancellationToken ct = default);

    /// <summary>忽略一组（组内 id 排序后连成主键），该组不再出现在结果里。</summary>
    Task IgnoreGroupAsync(IReadOnlyList<Guid> memberIds, CancellationToken ct = default);

    /// <summary>已经隐藏的副本 id（书库网格要排除它们）。</summary>
    Task<IReadOnlyList<Guid>> ListHiddenBookIdsAsync(CancellationToken ct = default);
}

