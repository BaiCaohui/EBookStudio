using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>
/// 阅读进度。三级定位（设计文档 5.2，精度从高到低）：
/// spine_index 章节粗定位 -> location 元素锚点 -> fingerprint 文本指纹兜底。
/// </summary>
public sealed class ReadingProgress
{
    public Guid BookId { get; set; }
    public int? SpineIndex { get; set; }
    public int? PageIndex { get; set; }
    public int? TotalPages { get; set; }

    /// <summary>CSS 选择器 / 锚点。</summary>
    public string? Location { get; set; }

    /// <summary>文本指纹：前 20 字 + 偏移比例。</summary>
    public string? Fingerprint { get; set; }

    public double Percent { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// **不是数据库列**（reading_progress 表里没有它）：本次保存是不是发生在"书的最末尾"。
    /// 由 <see cref="ReaderProgressSnapshot.ToRow"/> 从快照算出来，仓储拿它决定要不要把书
    /// 标成"已读完"。为什么不让仓储自己算：那需要总章数，而总章数只有阅读器那一侧知道。
    /// </summary>
    public bool IsAtEnd { get; set; }

    /// <summary>
    /// **不是数据库列**（读写由仓储走 book_reading_format 旁表）：
    /// 这条进度是在哪个格式下保存的（用户 2026-10-21）。
    /// </summary>
    /// <remarks>
    /// 用途只有一个：换格式继续读时判断"要不要换算"。
    /// 章号对 EPUB 与 TXT 的含义不同、对 PDF 更是页码，直接拿来恢复必然落到错处。
    /// 旧数据没有这一列，读出来是 null——那时只能退回原来那条线索（PageIndex 有没有值）。
    /// </remarks>
    public BookFormatKind? SourceFormat { get; set; }
}