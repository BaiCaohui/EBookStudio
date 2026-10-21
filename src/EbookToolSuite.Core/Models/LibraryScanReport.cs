using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>一次全库扫描的结果（T2-6）。</summary>
public sealed record LibraryScanReport(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int ScannedFormats,
    int ScannedBooks,
    IReadOnlyList<BookFileStatus> Problems)
{
    public int MissingCount => Problems.Count(p => p.State == BookFileState.Missing);

    public int ModifiedCount => Problems.Count(p => p.State == BookFileState.ExternallyModified);

    public int NormalCount => ScannedFormats - Problems.Count;

    public bool IsEmpty => Problems.Count == 0;

    /// <summary>每本书一个状态（多个格式时取最严重的那个：缺失优先于外部修改）。</summary>
    public IReadOnlyDictionary<Guid, BookFileState> BuildBookStates()
    {
        var map = new Dictionary<Guid, BookFileState>();

        foreach (var p in Problems)
        {
            if (!map.TryGetValue(p.BookId, out var existing) || (int)p.State < (int)existing)
            {
                map[p.BookId] = p.State;
            }
        }

        return map;
    }

    /// <summary>状态栏/提示用的一句话，正常时是空串。</summary>
    public string Summary => Problems.Count == 0
        ? string.Empty
        : MissingCount + " 个文件缺失" + (ModifiedCount > 0 ? "、" + ModifiedCount + " 个文件被外部修改" : string.Empty);
}
