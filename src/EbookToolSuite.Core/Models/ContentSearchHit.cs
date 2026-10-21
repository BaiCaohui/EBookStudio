namespace EbookToolSuite.Core.Models;

/// <summary>
/// 一条全文搜索命中（T6-4）。Body 是整段切片，片段由服务层在这里面切，
/// 这样「片段高亮」与「跳转后高亮」用的是同一份原文。
/// </summary>
public sealed record ContentSearchHit
{
    public required Guid BookId { get; init; }

    public required string BookTitle { get; init; }

    public string? BookAuthors { get; init; }

    public required int SpineIndex { get; init; }

    public string? ChapterTitle { get; init; }

    public required string Body { get; init; }
}
