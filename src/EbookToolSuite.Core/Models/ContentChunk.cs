namespace EbookToolSuite.Core.Models;

/// <summary>
/// 一段正文切片（T6-4）。一章按 ~800 字再切几段，是为了 snippet 与排序都落在本地，
/// 而不是每次搜索去截一整章。
/// </summary>
public sealed record ContentChunk
{
    public required Guid BookId { get; init; }

    /// <summary>spine 序号（EPUB 章序 / TXT 章序 / PDF 页序）。点搜索结果就是拿它跳转。</summary>
    public required int SpineIndex { get; init; }

    public string? ChapterTitle { get; init; }

    /// <summary>章节在书内的标识（EPUB 是包内路径，TXT 是 txt:start-end，PDF 是页号）。只用于排障与展示。</summary>
    public string? ChapterHref { get; init; }

    public required string Body { get; init; }
}
