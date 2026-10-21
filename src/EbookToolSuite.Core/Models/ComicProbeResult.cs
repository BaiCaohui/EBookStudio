namespace EbookToolSuite.Core.Models;

/// <summary>
/// 漫画包（CBZ/CBR）的只读探测结果。
/// </summary>
/// <remarks>
/// T6-3：导入阶段只判断「是不是漫画包、有多少页」，**不解包、不读图**——
/// 页位图留到阅读时按需取（设计文档 §6.6）。这与 PDF 的 <c>PdfProbe</c> 同一思路：
/// 每个格式都有自己的只读探测，不要把「能不能打开」推迟到阅读窗口（§7.1.1）。
/// </remarks>
public sealed record ComicProbeResult
{
    /// <summary>真的是一个能列出图片的压缩包（zip 或 rar）。false = 按「无法识别的电子书格式」跳过。</summary>
    public bool LooksLikeComic { get; init; }

    /// <summary>图片页数（按自然序排完之后的条目数）。</summary>
    public int PageCount { get; init; }

    /// <summary>RAR（CBR）包。CBZ 是 false。</summary>
    public bool IsRar { get; init; }

    /// <summary>加密（需要密码）的包。允许导入，阅读时由外壳提示。</summary>
    public bool IsEncrypted { get; init; }

    public long FileSize { get; init; }

    /// <summary>解包失败的原因（面向用户的一句话）。null = 没发现问题。</summary>
    public string? Error { get; init; }

    /// <summary>
    /// 包内 <c>ComicInfo.xml</c> 解析出的书籍信息。
    /// null = 包里没有这个文件，或者它不是能解析的 ComicInfo（两种情况对导入等价：按无元数据处理）。
    /// </summary>
    /// <remarks>
    /// 解析失败**不影响** <see cref="LooksLikeComic"/> / <see cref="PageCount"/> /
    /// <see cref="IsEncrypted"/>：元数据只是锦上添花，坏掉的 ComicInfo.xml 不该让一本漫画打不开。
    /// </remarks>
    public ComicMetadata? Metadata { get; init; }
}
