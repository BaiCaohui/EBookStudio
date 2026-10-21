using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Engines.Comics;

/// <summary>
/// 读写 CBZ 包内的 <c>ComicInfo.xml</c>（Anansi ComicInfo 规范）。
/// </summary>
/// <remarks>
/// <para>
/// 【为什么手写而不是序列化 <see cref="ComicMetadata"/>】字段名是阅读器之间的跨软件契约
/// （ComicRack / CDisplayEx / 静读天下 / Perfect Viewer 都按这套名字取值），
/// 用 record 的属性名直接序列化等于把 C# 命名绑到外部协议上；这里逐字段显式映射，
/// 将来改模型属性名不会悄悄改掉文件格式。
/// </para>
/// <para>
/// 【只写用户填过的字段】空元素会让阅读器把「未填写」显示成空白，和「没这一项」不是一回事；
/// 只有 <see cref="ComicMetadata.PageCount"/> 这类自动项恒写。
/// </para>
/// <para>
/// 【编码】UTF-8 无 BOM + 缩进：BOM 对任何 XML 解析器都是多余的不可见字节，
/// 而缩进是给人直接打开包看的时候用的。
/// </para>
/// </remarks>
public static class ComicInfoXml
{
    /// <summary>包内固定条目名。探测与打包共用，避免两处各写一遍字符串。</summary>
    public const string EntryName = "ComicInfo.xml";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 把书籍信息写成 <paramref name="output"/> 上的一个 XML 文档。
    /// </summary>
    /// <param name="output">目标流（打包时就是 zip 条目流），调用方负责关闭。</param>
    /// <param name="metadata">书籍信息（这里不判断 <see cref="ComicMetadata.HasUserValues"/>，由调用方决定写不写）。</param>
    /// <param name="pageCount">实际写进包里的图片页数（打包时机填）。</param>
    public static void Write(Stream output, ComicMetadata metadata, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(metadata);

        /* xmlns:xsd / xmlns:xsi 是 ComicRack 当初生成文件时的固定头。虽然本文件不用
           xsi 类型，但部分老阅读器/校验器会按这份「标准头」判断是不是 ComicInfo；
           用前缀声明（而不是默认命名空间），保证 <Title> 这些元素仍是不带命名空间的本地名。 */
        var root = new XElement(
            "ComicInfo",
            new XAttribute(XNamespace.Xmlns + "xsd", "http://www.w3.org/2001/XMLSchema"),
            new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"));

        // ---------- 基本信息 ----------
        AddText(root, "Title", metadata.Title);
        AddText(root, "Series", metadata.Series);
        AddText(root, "Number", metadata.Number);
        AddText(root, "Count", metadata.Count);
        AddText(root, "Volume", metadata.Volume);
        AddText(root, "AlternateSeries", metadata.AlternateSeries);
        AddText(root, "AlternateNumber", metadata.AlternateNumber);
        AddText(root, "AlternateCount", metadata.AlternateCount);

        // ---------- 创作者 ----------
        AddText(root, "Writer", metadata.Writer);
        AddText(root, "Penciller", metadata.Penciller);
        AddText(root, "Inker", metadata.Inker);
        AddText(root, "Colorist", metadata.Colorist);
        AddText(root, "Letterer", metadata.Letterer);
        AddText(root, "CoverArtist", metadata.CoverArtist);
        AddText(root, "Editor", metadata.Editor);

        // ---------- 出版 ----------
        AddText(root, "Publisher", metadata.Publisher);
        AddText(root, "Imprint", metadata.Imprint);
        AddNumber(root, "Year", metadata.Year);
        AddNumber(root, "Month", metadata.Month);
        AddNumber(root, "Day", metadata.Day);

        // ---------- 分类与内容 ----------
        AddText(root, "Genre", metadata.Genre);
        AddText(root, "Tags", metadata.Tags);
        AddText(root, "Characters", metadata.Characters);
        AddText(root, "Teams", metadata.Teams);
        AddText(root, "Locations", metadata.Locations);
        AddText(root, "StoryArc", metadata.StoryArc);
        AddText(root, "SeriesGroup", metadata.SeriesGroup);
        AddText(root, "AgeRating", metadata.AgeRating);

        // ---------- 说明 / 来源 ----------
        AddText(root, "Summary", metadata.Summary);
        AddText(root, "Notes", metadata.Notes);
        AddText(root, "Web", metadata.Web);
        AddText(root, "LanguageISO", metadata.LanguageISO);
        AddText(root, "Format", metadata.Format);
        AddText(root, "ScanInformation", metadata.ScanInformation);

        /* Manga 是 ComicInfo 里的三态枚举（Unknown / No / Yes / YesAndRightToLeft）。
           我们只表达「右→左（true）/ 左→右（false）/ 没表态（null）」：
           null 不写这一项，让阅读器用自己的默认值；写 No 才是用户明确要求左→右。 */
        if (metadata.RightToLeft is bool rightToLeft)
        {
            root.Add(new XElement("Manga", rightToLeft ? "YesAndRightToLeft" : "No"));
        }

        // 黑白同理：null 不写，避免把彩色漫画误标成黑白（有些阅读器会据此做去色处理）。
        if (metadata.BlackAndWhite is bool blackAndWhite)
        {
            root.Add(new XElement("BlackAndWhite", blackAndWhite ? "Yes" : "No"));
        }

        /* PageCount 是唯一恒写的字段（>0 时）：它由打包器按实际写入的图片数填，
           不是用户输入。参数优先，其次才用 metadata 自带的（用于「解析后再写回」）。 */
        var effectivePageCount = pageCount > 0 ? pageCount : metadata.PageCount;
        if (effectivePageCount > 0)
        {
            root.Add(new XElement("PageCount", effectivePageCount));
        }

        /* Isbn 是 ComicInfo 规范里没有的扩展字段：只有用户填了才写，
           阅读器不认识会直接忽略，不影响其它字段（见 ComicMetadata 的类备注）。 */
        AddText(root, "Isbn", metadata.Isbn);

        var settings = new XmlWriterSettings
        {
            Encoding = Utf8NoBom,
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\r\n",
            OmitXmlDeclaration = false,
            CloseOutput = false,
        };

        using var writer = XmlWriter.Create(output, settings);
        writer.WriteStartDocument();
        root.WriteTo(writer);
        writer.WriteEndDocument();
        writer.Flush();
    }

    /// <summary>
    /// 解析 <c>ComicInfo.xml</c> 的文本。失败（空串、坏 XML、根节点不对）返回 false，**不抛异常**。
    /// </summary>
    /// <returns>true 表示解析成功；此时 <paramref name="metadata"/> 至少是一个空对象。</returns>
    public static bool TryParse(string? xml, out ComicMetadata metadata)
    {
        metadata = new ComicMetadata();

        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        XDocument document;

        try
        {
            /* 禁 DTD / 外部实体：ComicInfo.xml 来自用户文件，打开外部实体等于让一个漫画包
               能读本机任意文件（XXE）。这里只接受纯数据文档。 */
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };

            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException)
        {
            return false;
        }

        var root = document.Root;

        if (root is null
            || !string.Equals(root.Name.LocalName, "ComicInfo", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        metadata = new ComicMetadata
        {
            Title = ReadText(root, "Title"),
            Series = ReadText(root, "Series"),
            Number = ReadText(root, "Number"),
            Count = ReadText(root, "Count"),
            Volume = ReadText(root, "Volume"),
            AlternateSeries = ReadText(root, "AlternateSeries"),
            AlternateNumber = ReadText(root, "AlternateNumber"),
            AlternateCount = ReadText(root, "AlternateCount"),
            Writer = ReadText(root, "Writer"),
            Penciller = ReadText(root, "Penciller"),
            Inker = ReadText(root, "Inker"),
            Colorist = ReadText(root, "Colorist"),
            Letterer = ReadText(root, "Letterer"),
            CoverArtist = ReadText(root, "CoverArtist"),
            Editor = ReadText(root, "Editor"),
            Publisher = ReadText(root, "Publisher"),
            Imprint = ReadText(root, "Imprint"),
            Year = ReadInt(root, "Year"),
            Month = ReadInt(root, "Month"),
            Day = ReadInt(root, "Day"),
            Genre = ReadText(root, "Genre"),
            Tags = ReadText(root, "Tags"),
            Characters = ReadText(root, "Characters"),
            Teams = ReadText(root, "Teams"),
            Locations = ReadText(root, "Locations"),
            StoryArc = ReadText(root, "StoryArc"),
            SeriesGroup = ReadText(root, "SeriesGroup"),
            AgeRating = ReadText(root, "AgeRating"),
            Summary = ReadText(root, "Summary"),
            Notes = ReadText(root, "Notes"),
            Web = ReadText(root, "Web"),
            LanguageISO = ReadText(root, "LanguageISO"),
            Format = ReadText(root, "Format"),
            ScanInformation = ReadText(root, "ScanInformation"),
            BlackAndWhite = ReadYesNo(root, "BlackAndWhite"),
            RightToLeft = ReadManga(root),
            PageCount = ReadInt(root, "PageCount") ?? 0,
            Isbn = ReadText(root, "Isbn"),
        };

        return true;
    }

    private static void AddText(XElement root, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            // 去首尾空白：阅读器里书名前后的空格只会显示成莫名其妙的缩进。
            root.Add(new XElement(name, value.Trim()));
        }
    }

    private static void AddNumber(XElement root, string name, int? value)
    {
        if (value is int number)
        {
            root.Add(new XElement(name, number.ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>按本地名找子元素：命名空间可有可无，兼容别的工具写出来的变体。</summary>
    private static XElement? Find(XElement root, string name)
        => root.Elements().FirstOrDefault(
            element => string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));

    private static string? ReadText(XElement root, string name)
    {
        var value = Find(root, name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static int? ReadInt(XElement root, string name)
        => int.TryParse(ReadText(root, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static bool? ReadYesNo(XElement root, string name)
        => ReadText(root, name)?.ToLowerInvariant() switch
        {
            "yes" => true,
            "no" => false,
            _ => null,
        };

    private static bool? ReadManga(XElement root)
        => ReadText(root, "Manga")?.ToLowerInvariant() switch
        {
            "yesandrighttoleft" => true,
            "no" => false,
            /* 「Yes」只说这是日漫风格、没声明翻页方向，不能替用户当成右→左：
               写回时会变成 YesAndRightToLeft，等于篡改了别人的文件。 */
            _ => null,
        };
}
