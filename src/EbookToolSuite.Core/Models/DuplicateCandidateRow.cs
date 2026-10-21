namespace EbookToolSuite.Core.Models;

/// <summary>重复检测的候选行：sample的某个格式（仓储层返回，服务层据此分组）。</summary>
public sealed record DuplicateCandidateRow(
    Guid FormatId,
    Guid BookId,
    string Title,
    string Authors,
    string Format,
    string FilePath,
    long FileSize,
    DateTimeOffset? FileModifiedAt,
    string? ContentHash,
    int Rating,
    DateTimeOffset AddedAt);

