namespace EbookToolSuite.Core.Models;

/// <summary>
/// 只读探测一本 EPUB 得到的原始信息。不含库内 Id，也不代表已入库
/// （设计文档 7.1.1 的第 2 步"探测"）。
/// </summary>
public sealed class EpubProbeResult
{
    public required string SourcePath { get; init; }
    public required long FileSize { get; init; }

    // ---------- 元数据 ----------

    /// <summary>标题。EPUB 允许标题缺失，此时由文件名兜底，永远非空。</summary>
    public required string Title { get; init; }

    public string? Authors { get; init; }
    public string? Publisher { get; init; }
    public string? Description { get; init; }
    public string? Language { get; init; }
    public string? Isbn { get; init; }
    public string? Series { get; init; }
    public double? SeriesIndex { get; init; }
    public DateOnly? PublishedDate { get; init; }

    /// <summary>OPF package 的 unique-identifier 值，原样保留。</summary>
    public string? Identifier { get; init; }

    // ---------- 结构 ----------

    /// <summary>true 表示 EPUB 3.x（含 3.1）；false 表示 EPUB 2。</summary>
    public bool IsEpub3 { get; init; }

    public int SpineCount { get; init; }
    public int ManifestCount { get; init; }

    // ---------- 封面 ----------

    /// <summary>
    /// 封面原始字节。null 表示这本书没有可用封面，调用方应改用占位图。
    /// </summary>
    public byte[]? CoverBytes { get; init; }

    /// <summary>封面在图包里的键（诊断用，不写库）。</summary>
    public string? CoverKey { get; init; }

    /// <summary>
    /// true 表示封面来自兜底策略（取体积最大的图片资源），
    /// 而不是书里声明的封面。UI 上可以提示"封面可能是正文插图"。
    /// </summary>
    public bool CoverFromFallback { get; init; }

    // ---------- 体检结论 ----------

    /// <summary>
    /// true 表示这是**宽松兜底**读出来的（没通过 VersOne 的严格规范校验）。
    /// 书能用，但可能缺章节或元数据不全，UI 上要显示"未通过严格校验"角标。
    /// </summary>
    public bool Damaged { get; init; }

    /// <summary>
    /// 书里用了 IDPF/Adobe **字体混淆**。这不是 DRM（正文没加密），
    /// 但读取/转换时要按需解混淆，改 dc:identifier 前也要先处理（设计文档 6.1.9）。
    /// </summary>
    public bool FontObfuscated { get; init; }

    public bool HasCover => CoverBytes is { Length: > 0 };
}