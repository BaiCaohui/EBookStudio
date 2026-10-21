namespace EbookToolSuite.Core.Models;

/// <summary>
/// 打开一个 EPUB 编辑工作区的入参（执行计划 T3-1）。
/// BookId/FormatId 允许留空（Guid.Empty）：单测、以及"直接拖一个 epub 进来"时没有库记录。
/// </summary>
public sealed record WorkspaceOpenRequest
{
    /// <summary>要打开的 EPUB 绝对路径或相对路径，服务内部会规范成绝对路径。</summary>
    public required string EpubPath { get; init; }

    /// <summary>库里的书 id（books.id）。</summary>
    public Guid BookId { get; init; }

    /// <summary>库里对应格式行的 id（book_formats.id）。</summary>
    public Guid FormatId { get; init; }
}
