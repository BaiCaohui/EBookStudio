namespace EbookToolSuite.Core.Models;

/// <summary>
/// 一次「读到哪了」的完整快照：前端上报什么就存什么，这一层不丢信息。
/// 落到 reading_progress 表由 <see cref="ToRow"/> 完成。
/// </summary>
public sealed class ReaderProgressSnapshot
{
    public Guid BookId { get; init; }

    /// <summary>章节序号（spine 序号）。</summary>
    public int ChapterIndex { get; init; }

    /// <summary>这本书一共多少章。用来把章内比例换算成全书比例。</summary>
    public int ChapterCount { get; init; }

    public ReaderLocator Locator { get; init; } = new();

    /// <summary>文本指纹。阶段 1 也存下来，恢复时作为最后一级兜底。</summary>
    public TextFingerprint? Fingerprint { get; init; }

    /// <summary>
    /// 这条进度是在哪个格式下保存的（用户 2026-10-21）。
    /// 阅读窗口自己新建快照时填本次打开的 SourceFormat；从库里读出来时由 <see cref="FromRow"/> 填。
    /// </summary>
    public Core.Enums.BookFormatKind? SourceFormat { get; init; }

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// PDF 专用（T6-2）：当前页（0 起）。滚动阅读的书为 null。
    /// </summary>
    /// <remarks>
    /// 为什么 PDF 走页码而不是 locator：内置查看器是黑盒，拿不到文本位置与滚动比例
    /// （探针 3 实测：`SourceChanged` 0 次、查看器 DOM 只有 4 个元素），
    /// 用户跳页/翻页时由外壳记账是唯一可靠的来源（设计文档 §6.5.1）。
    /// </remarks>
    public int? PageIndex { get; init; }

    /// <summary>PDF 专用（T6-2）：总页数，由 pdfium 读出（设计文档 §6.5.4）。滚动阅读的书为 null。</summary>
    public int? TotalPages { get; init; }

    /// <summary>
    /// 全书进度 0..1。
    /// PDF 走"页码 / 总页数"（设计文档 §6.5.1 的口径）；
    /// 滚动阅读走 (章序号 + 章内比例) / 总章数，章节长短不一时只是近似，
    /// 所以网格上的角标按"区间"用，不显示小数点。
    /// </summary>
    public double Percent => TotalPages is > 0
        ? Math.Clamp((double)(PageIndex ?? 0) / TotalPages.Value, 0, 1)
        : ComputePercent(ChapterIndex, ChapterCount, Locator.PercentInChapter);

    /// <summary>
    /// 从库里读出来的**原始**全书百分比（reading_progress.percent）。
    /// </summary>
    /// <remarks>
    /// 为什么不能直接用 <see cref="Percent"/>：它要拿"本次打开这本书的章数"去换算，
    /// 而跨格式打开时章数是 0（PDF 没有章）——Percent 会算成 0，百分比就丢了。
    /// 跨格式定位（<see cref="CrossFormatLocator"/>）必须用库里存的这个原值。
    /// 由 <see cref="FromRow"/> 填；阅读器自己新建的快照为 null（那时 Percent 算得出来）。
    /// 这不是数据库列，不参与 ToRow。
    /// </remarks>
    public double? StoredPercent { get; init; }

    public static double ComputePercent(int chapterIndex, int chapterCount, double? percentInChapter)
    {
        if (chapterCount <= 0)
        {
            return 0;
        }

        var index = Math.Clamp(chapterIndex, 0, chapterCount - 1);
        var inChapter = Math.Clamp(percentInChapter ?? 0, 0, 1);

        return Math.Clamp((index + inChapter) / chapterCount, 0, 1);
    }

    /// <summary>
    /// 是不是"真的读到底了"：必须**在最后一章**，而且视口底部离本章末尾只剩不到 2%。
    ///
    /// 刻意**不用**"全书进度 >= 99.5%"那种算法  全书进度 = (章号 + 章内比例) / 总章数，
    /// 按章数加权，几百章的书一打开末章就已经是 99.9%（855 章的书打开末章 = 100%），
    /// 等于根本没判。2026-10-03 用户提问后改掉，见 docs/DECISIONS.md。
    /// </summary>
    public bool ReachedEnd => TotalPages is > 0
        ? (PageIndex ?? 0) >= TotalPages.Value - 1
        : ChapterCount > 0
            && ChapterIndex >= ChapterCount - 1
            && (Locator.PercentInChapter ?? 0) >= 0.98;

    /// <summary>转成落库模型。</summary>
    /// <remarks>
    /// page_index / total_pages 只对 PDF 有值（T6-2）：分页阅读靠它恢复"读到第几页"，
    /// 百分比也按页码算。滚动阅读的书这两列仍然是空——那时没有"页"这个概念。
    /// </remarks>
    public ReadingProgress ToRow() => new()
    {
        BookId = BookId,
        SpineIndex = Locator.ChapterIndex,
        PageIndex = PageIndex,
        TotalPages = TotalPages,
        Location = Locator.ToJson(),
        Fingerprint = Fingerprint?.ToJson(),
        Percent = Percent,
        IsAtEnd = ReachedEnd,
        UpdatedAt = UpdatedAt,
        SourceFormat = SourceFormat,
    };

    /// <summary>
    /// 从库里的行还原。"内容变了"的判定不在这里：那一刻的 scrollHeight
    /// 只有浏览器知道，所以降级判断交给前端（见 reader.js 的 restoreLocator）。
    /// </summary>
    public static ReaderProgressSnapshot? FromRow(ReadingProgress? row, int chapterCount)
    {
        if (row is null)
        {
            return null;
        }

        var locator = ReaderLocator.TryParse(row.Location) ?? new ReaderLocator { ChapterIndex = row.SpineIndex ?? 0 };
        locator.ChapterIndex = row.SpineIndex ?? locator.ChapterIndex;

        return new ReaderProgressSnapshot
        {
            BookId = row.BookId,
            ChapterIndex = locator.ChapterIndex,
            ChapterCount = chapterCount,
            PageIndex = row.PageIndex,
            TotalPages = row.TotalPages,
            Locator = locator,
            Fingerprint = TextFingerprint.TryParse(row.Fingerprint),
            StoredPercent = row.Percent,
            UpdatedAt = row.UpdatedAt,
            SourceFormat = row.SourceFormat,
        };
    }
}