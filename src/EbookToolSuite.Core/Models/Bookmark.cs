namespace EbookToolSuite.Core.Models;

/// <summary>
/// 书签（T6-5）：一个"纯位置"。可变长 EPUB/TXT 用 <see cref="Location"/>（三级定位），
/// 固定版式的 PDF/漫画用 <see cref="PageIndex"/>。
/// </summary>
public sealed class Bookmark
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid BookId { get; init; }

    /// <summary>spine 序号。PDF / 漫画为 null。</summary>
    public int? SpineIndex { get; set; }

    /// <summary>页码（0 起）。EPUB / TXT 为 null。</summary>
    public int? PageIndex { get; set; }

    /// <summary>三级定位（ReaderLocator 的 JSON）。PDF / 漫画为 null。</summary>
    public ReaderLocator? Location { get; set; }

    /// <summary>列表里显示的文字：章标题或"第 N 页"。由页面在加书签时给。</summary>
    public string? Label { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
