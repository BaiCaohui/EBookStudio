using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>
/// 跨格式定位（用户 2026-10-20）：同一本书换了格式继续读时，按「已读百分比」粗略换算落点。
/// </summary>
/// <remarks>
/// 【为什么要换算】阅读进度只有一条记录（reading_progress 按书 Id 存），
/// 里面可能是 EPUB 的"章号 + 章内比例"，也可能是 PDF 的"页码 + 总页数"。
/// 换成另一种版式打开时，这些字段的含义全变了：拿 EPUB 的章号去当 PDF 页码，
/// 就会永远从第 0 页开始。百分比是两种口径共有的唯一量，所以只在**跨版式**时用它换算。
///
/// 【什么时候不换算】同一版式（文本↔文本、PDF→PDF）继续用原来的精确恢复逻辑：
/// 章号 / 页码本身是准的，换成百分比反而更粗。判断见 <see cref="IsFixedLayout"/> 与
/// <see cref="ProgressFromFixedLayout"/>。
///
/// 【精度】刻意只做到"粗略"：PDF 页码 ≈ 百分比 × 总页数，文本章号 ≈ 百分比 × 总章数。
/// 章节长短不一、PDF 各页文字量也不一，换算结果只能保证"大致在那个位置"，
/// 不能代替用户自己的微调。README / 任务书要的就是这个口径。
/// </remarks>
public static class CrossFormatLocator
{
    /// <summary>固定版式（按页读）：PDF 与漫画 CBZ/CBR。</summary>
    public static bool IsFixedLayout(BookFormatKind kind)
        => kind is BookFormatKind.Pdf or BookFormatKind.Cbz;

    /// <summary>
    /// 这条进度记录是不是"按页存"的（PDF / 漫画）。有页码就视为来自固定版式；
    /// 文本阅读的快照 PageIndex 恒为 null（见 ReaderProgressSnapshot.ToRow）。
    /// </summary>
    public static bool ProgressFromFixedLayout(int? pageIndex) => pageIndex is not null;

    /// <summary>
    /// 已读百分比 → PDF / 漫画页码（0 基）。percent 会被夹到 0..1，
    /// 结果夹到 0..totalPages-1（读到 100% 时落到最后一页，而不是越界的下一页）。
    /// totalPages &lt;= 0（页数还不知道）时返回 0。
    /// </summary>
    public static int PageFromPercent(double percent, int totalPages)
    {
        if (totalPages <= 0)
        {
            return 0;
        }

        var clamped = Math.Clamp(percent, 0, 1);
        var page = (int)Math.Round(clamped * totalPages, MidpointRounding.AwayFromZero);

        return Math.Clamp(page, 0, totalPages - 1);
    }

    /// <summary>
    /// 已读百分比 → 文本 / EPUB 章号（0 基）。向下取整：
    /// "读了 55% 的 20 章"应该落在第 11 章（0 基）附近，向上取整会跳过整整一章。
    /// chapterCount &lt;= 0 时返回 0。
    /// </summary>
    public static int ChapterFromPercent(double percent, int chapterCount)
    {
        if (chapterCount <= 0)
        {
            return 0;
        }

        var clamped = Math.Clamp(percent, 0, 1);
        var chapter = (int)Math.Floor(clamped * chapterCount);

        return Math.Clamp(chapter, 0, chapterCount - 1);
    }
}
