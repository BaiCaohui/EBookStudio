namespace EbookToolSuite.Core.Models;

/// <summary>
/// CBZ 漫画的书籍信息（写进包内 ComicInfo.xml 的内容）。
/// </summary>
/// <remarks>
/// <para>
/// 字段名与 Anansi <c>ComicInfo</c> 规范对齐：阅读器（静读天下 / Perfect Viewer /
/// ComicRack / CDisplayEx）打开 CBZ 时显示的就是这些。
/// </para>
/// <para>
/// ★ 这里**不记录页序**：CBZ 没有页序字段，阅读器一律按包内文件名自然序排页。
/// 所以「用户排的顺序」由文件名本身承载（见 <see cref="Common.CbzEntryNames"/>），
/// 本对象只装书籍信息。
/// </para>
/// <para>
/// <see cref="Isbn"/> 是 ComicInfo 规范里没有的扩展字段：只在用户填了才写，
/// 阅读器不认识会忽略，不影响其它字段。
/// </para>
/// </remarks>
public sealed record ComicMetadata
{
    // ---------- 基本信息 ----------

    public string? Title { get; init; }
    public string? Series { get; init; }

    /// <summary>系列内序号（ComicInfo 的 Number，字符串：可以是 "1" 也可以是 "1.5"）。</summary>
    public string? Number { get; init; }

    public string? Count { get; init; }
    public string? Volume { get; init; }
    public string? AlternateSeries { get; init; }
    public string? AlternateNumber { get; init; }
    public string? AlternateCount { get; init; }

    // ---------- 创作者 ----------

    public string? Writer { get; init; }
    public string? Penciller { get; init; }
    public string? Inker { get; init; }
    public string? Colorist { get; init; }
    public string? Letterer { get; init; }
    public string? CoverArtist { get; init; }
    public string? Editor { get; init; }

    // ---------- 出版 ----------

    public string? Publisher { get; init; }
    public string? Imprint { get; init; }
    public int? Year { get; init; }
    public int? Month { get; init; }
    public int? Day { get; init; }

    // ---------- 分类与内容 ----------

    public string? Genre { get; init; }
    public string? Tags { get; init; }
    public string? Characters { get; init; }
    public string? Teams { get; init; }
    public string? Locations { get; init; }
    public string? StoryArc { get; init; }
    public string? SeriesGroup { get; init; }
    public string? AgeRating { get; init; }

    // ---------- 说明 / 来源 ----------

    public string? Summary { get; init; }
    public string? Notes { get; init; }
    public string? Web { get; init; }
    public string? LanguageISO { get; init; }
    public string? Format { get; init; }

    /// <summary>是否黑白。null = 不写这一项。</summary>
    public bool? BlackAndWhite { get; init; }

    /// <summary>右→左阅读（日漫）。null = 不写，由阅读器按默认处理。</summary>
    public bool? RightToLeft { get; init; }

    // ---------- 打包时自动填 ----------

    /// <summary>页数（写 ComicInfo 的 PageCount；由打包器按实际写入的图片数填）。</summary>
    public int PageCount { get; init; }

    /// <summary>扫描/制作信息（ComicInfo 的 ScanInformation）。</summary>
    public string? ScanInformation { get; init; }

    /// <summary>ISBN（规范里没有的扩展字段，见类备注）。</summary>
    public string? Isbn { get; init; }

    /// <summary>
    /// 用户到底填了没有（不含 <see cref="PageCount"/> 这种自动项）。
    /// false 时打包器不写 ComicInfo.xml：一个只有页数的空文件没有意义。
    /// </summary>
    public bool HasUserValues =>
        !string.IsNullOrWhiteSpace(Title)
        || !string.IsNullOrWhiteSpace(Series)
        || !string.IsNullOrWhiteSpace(Number)
        || !string.IsNullOrWhiteSpace(Count)
        || !string.IsNullOrWhiteSpace(Volume)
        || !string.IsNullOrWhiteSpace(AlternateSeries)
        || !string.IsNullOrWhiteSpace(AlternateNumber)
        || !string.IsNullOrWhiteSpace(AlternateCount)
        || !string.IsNullOrWhiteSpace(Writer)
        || !string.IsNullOrWhiteSpace(Penciller)
        || !string.IsNullOrWhiteSpace(Inker)
        || !string.IsNullOrWhiteSpace(Colorist)
        || !string.IsNullOrWhiteSpace(Letterer)
        || !string.IsNullOrWhiteSpace(CoverArtist)
        || !string.IsNullOrWhiteSpace(Editor)
        || !string.IsNullOrWhiteSpace(Publisher)
        || !string.IsNullOrWhiteSpace(Imprint)
        || Year is not null
        || Month is not null
        || Day is not null
        || !string.IsNullOrWhiteSpace(Genre)
        || !string.IsNullOrWhiteSpace(Tags)
        || !string.IsNullOrWhiteSpace(Characters)
        || !string.IsNullOrWhiteSpace(Teams)
        || !string.IsNullOrWhiteSpace(Locations)
        || !string.IsNullOrWhiteSpace(StoryArc)
        || !string.IsNullOrWhiteSpace(SeriesGroup)
        || !string.IsNullOrWhiteSpace(AgeRating)
        || !string.IsNullOrWhiteSpace(Summary)
        || !string.IsNullOrWhiteSpace(Notes)
        || !string.IsNullOrWhiteSpace(Web)
        || !string.IsNullOrWhiteSpace(LanguageISO)
        || !string.IsNullOrWhiteSpace(Format)
        || BlackAndWhite is not null
        || RightToLeft is not null
        || !string.IsNullOrWhiteSpace(ScanInformation)
        || !string.IsNullOrWhiteSpace(Isbn);
}
