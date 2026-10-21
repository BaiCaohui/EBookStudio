namespace EbookToolSuite.Core.Models;

/// <summary>一位作者 / 译者 / 编者。</summary>
/// <param name="Name">显示名，例：Sample Author / John Doe。</param>
/// <param name="Role">MARC relator 代码：aut=作者、trl=译者、edt=编者。</param>
/// <param name="FileAs">排序名（OPF 的 opf:file-as）。为空时由编辑器推导。</param>
public sealed record EpubContributor(string Name, string? Role = "aut", string? FileAs = null);

/// <summary>
/// OPF 元数据的可编辑模型（执行计划 T3-2 的字段表）。
/// 只描述"能改的字段"，不含 spine/manifest  那些是 T3-1 的 EpubPackageIndex 与 T3-4 打包器的职责。
/// </summary>
/// <remarks>
/// 【EPUB2 与 EPUB3 的差异都收敛在 OpfMetadataEditor 里】，本模型不区分版本，
/// 唯一例外是 <see cref="Subtitle"/>：EPUB2 的 OPF 没有表达副标题的位置，读出来恒为 null。
/// </remarks>
public sealed class OpfMetadata
{
    public string? Title { get; set; }

    /// <summary>副标题。只有 EPUB3 能写（走 refines 的 title-type）。</summary>
    public string? Subtitle { get; set; }

    /// <summary>作者（OPF 的 dc:creator），保持顺序，第一个视为主作者。</summary>
    public List<EpubContributor> Authors { get; set; } = new();

    /// <summary>其它贡献者（OPF 的 dc:contributor），典型是译者。与作者分开存，写回时才不丢区分。</summary>
    public List<EpubContributor> Contributors { get; set; } = new();

    public string? Series { get; set; }

    public double? SeriesIndex { get; set; }

    public string? Language { get; set; }

    public string? Isbn { get; set; }

    public string? Publisher { get; set; }

    public DateOnly? PublicationDate { get; set; }

    public string? Description { get; set; }

    public List<string> Subjects { get; set; } = new();

    /// <summary>评分，**0-5 星**。Calibre 惯例是 0-10，写盘时乘 2。</summary>
    public int? Rating { get; set; }

    /// <summary>封面在 manifest 里的 item id（EPUB2/3 都写 &lt;meta name="cover"&gt;）。</summary>
    public string? CoverItemId { get; set; }

    /// <summary>package@version 原文，例：2.0 / 3.0。缺失时按 2.0 处理。</summary>
    public string Version { get; set; } = "2.0";

    public bool IsEpub3 { get; set; }

    /// <summary>只看版本分支时用的便捷属性。</summary>
    public bool IsEpub2 => !IsEpub3;
}

/// <summary>
/// manifest 里的一条资源声明（执行计划 T3-3 读写封面条目要用）。
/// 只带 href/media-type/properties 三个字段，够封面替换用；T3-4 打包器要更全的信息时再加。
/// </summary>
/// <param name="Id">item 的 id（全文件唯一）。</param>
/// <param name="Href">item 的 href，包内相对路径，**保持原样**（可能带 % 转义，需要落盘路径时再解码）。</param>
/// <param name="MediaType">media-type，例：image/jpeg。缺失时为空串。</param>
/// <param name="Properties">EPUB3 的 properties 原文（EPUB2 恒为 null）。</param>
public sealed record OpfManifestItem(string Id, string Href, string MediaType, string? Properties = null);

/// <summary>
/// OPF 的 spine 项（T5-1）：阅读顺序由这份清单决定，改顺序/增删章节都要动它。
/// </summary>
/// <param name="Index">在 spine 里的位置（0 起）。</param>
/// <param name="IdRef">指向 manifest 里的 id。</param>
/// <param name="Linear">spine@linear；false = 辅助章节。</param>
public sealed record OpfSpineItem(int Index, string IdRef, bool Linear = true);



