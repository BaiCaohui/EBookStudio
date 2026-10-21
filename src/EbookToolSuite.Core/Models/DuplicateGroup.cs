namespace EbookToolSuite.Core.Models;

/// <summary>可能重复组里的一个成员（sample的某个格式）。</summary>
public sealed record DuplicateMember(
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

/// <summary>
/// 一组可能重复的书。组成员来自「归一化书名 + 作者 + 格式」相同的候选，
/// 再按 content_hash 复核（懒计算）。
/// </summary>
public sealed record DuplicateGroup(
    string Signature,
    IReadOnlyList<DuplicateMember> Members,
    bool AllHashesEqual,
    string Suggestion)
{
    public int Count => Members.Count;

    public long TotalSize => Members.Sum(m => m.FileSize);
}

/// <summary>一次重复检测的结果。</summary>
public sealed record DuplicateScanReport(
    IReadOnlyList<DuplicateGroup> Groups,
    int BooksScanned,
    int HashesComputed,
    TimeSpan Duration)
{
    public int GroupCount => Groups.Count;

    public int SuspectBookCount => Groups.Sum(g => g.Count);

    public bool IsEmpty => Groups.Count == 0;

    public int ConfirmedGroupCount => Groups.Count(g => g.AllHashesEqual);
}

