using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>
/// 笔记 / 高亮（T6-5）。设计文档 5.2 的 notes 表：高亮与批注共用一行结构，
/// 差别只在 <see cref="Type"/> 与 <see cref="Content"/> 是否为空。
/// </summary>
public sealed class Note
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid BookId { get; init; }

    /// <summary>spine 序号。PDF / 漫画为 null。</summary>
    public int? SpineIndex { get; set; }

    /// <summary>页码（0 起）。EPUB / TXT 为 null。</summary>
    public int? PageIndex { get; set; }

    /// <summary>选区锚点（含起止路径与 PDF 页内字符区间）。</summary>
    public NoteAnchor? Anchor { get; set; }

    /// <summary>颜色（设计文档 F-25 的 4 色高亮）；这一版 UI 只用 0。</summary>
    public int Color { get; set; }

    /// <summary>标记样式：高亮 / 下划线 / 波浪线 / 删除线（T6-5 补短）。</summary>
    public MarkStyle Style { get; set; }

    public NoteType Type { get; set; }

    /// <summary>选中原文。列表与导出都用它当标题；位置失效时也靠它重新搜索。</summary>
    public string? Quote { get; set; }

    /// <summary>用户输入的内容。高亮（<see cref="NoteType.Highlight"/>）为 null。</summary>
    public string? Content { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedAt { get; set; }
}
