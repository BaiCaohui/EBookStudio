using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>重复检测与「副本 / 忽略」记录所需的库操作（实现在 Infrastructure，手写 ADO）。</summary>
public interface IDuplicateRepository
{
    /// <summary>取候选：每本书的每个格式一行。已隐藏的副本不参与，否则会被反复报出来。</summary>
    Task<IReadOnlyList<DuplicateCandidateRow>> ListCandidatesAsync(CancellationToken ct = default);

    /// <summary>取已忽略组的键（组内 id 排序后连接）。</summary>
    Task<IReadOnlyList<string>> ListIgnoredSignaturesAsync(CancellationToken ct = default);

    /// <summary>把懒算出来的 content_hash 写回 book_formats.full_hash，避免下次重算。</summary>
    Task SaveContentHashAsync(Guid formatId, string contentHash, CancellationToken ct = default);

    /// <summary>按快速指纹找一条已有的格式行，供导入去重复核。故意不过滤 missing。</summary>
    Task<DuplicateCandidateRow?> FindByFingerprintAsync(string quickFingerprint, CancellationToken ct = default);

    Task<int> HideCopiesAsync(Guid keptBookId, IReadOnlyList<Guid> copyBookIds, CancellationToken ct = default);

    Task<int> RestoreCopiesAsync(IReadOnlyList<Guid> copyBookIds, CancellationToken ct = default);

    Task IgnoreGroupAsync(string memberKey, CancellationToken ct = default);

    /// <summary>书库网格要排除的 id（已隐藏的副本）。</summary>
    Task<IReadOnlyList<Guid>> ListHiddenBookIdsAsync(CancellationToken ct = default);
}

