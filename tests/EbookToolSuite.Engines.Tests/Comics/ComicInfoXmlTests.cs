using System.Text;
using System.Xml.Linq;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Comics;

namespace EbookToolSuite.Engines.Tests.Comics;

/// <summary>
/// ComicInfo.xml 读写：字段映射、XML 转义、空值策略（只写用户填过的）、编码与三态映射。
/// </summary>
public sealed class ComicInfoXmlTests
{
    private static string WriteToString(ComicMetadata metadata, int pageCount)
    {
        using var stream = new MemoryStream();
        ComicInfoXml.Write(stream, metadata, pageCount);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static XElement Root(string xml) => XDocument.Parse(xml).Root!;

    [Fact]
    public void Write_中文与特殊字符往返_XML转义正确()
    {
        var metadata = new ComicMetadata
        {
            Title = "漫画 <第一卷> & \"序章\"",
            Writer = "张三 & 李四",
            Summary = "第一行\n第二行",
            Number = "1.5",
            Publisher = "某出版社",
            LanguageISO = "zh",
            Year = 2024,
            Month = 7,
            Day = 15,
            Isbn = "978-7-000-00000-0",
        };

        using var stream = new MemoryStream();
        ComicInfoXml.Write(stream, metadata, 42);
        var bytes = stream.ToArray();

        // UTF-8 无 BOM：第一个字节就该是 '<'（0x3C），而不是 BOM 的 0xEF。
        Assert.Equal((byte)'<', bytes[0]);

        var xml = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        Assert.Contains("&lt;第一卷&gt;", xml);
        Assert.Contains("&amp;", xml);

        Assert.True(ComicInfoXml.TryParse(xml, out var parsed));
        Assert.Equal(metadata.Title, parsed.Title);
        Assert.Equal(metadata.Writer, parsed.Writer);
        Assert.Equal(metadata.Summary, parsed.Summary);
        Assert.Equal(metadata.Number, parsed.Number);
        Assert.Equal(metadata.Publisher, parsed.Publisher);
        Assert.Equal(metadata.LanguageISO, parsed.LanguageISO);
        Assert.Equal(metadata.Isbn, parsed.Isbn);
        Assert.Equal(42, parsed.PageCount);
        Assert.Equal(2024, parsed.Year);
        Assert.Equal(7, parsed.Month);
        Assert.Equal(15, parsed.Day);
    }

    [Fact]
    public void Write_只写用户填过的字段_PageCount按参数恒写()
    {
        var root = Root(WriteToString(new ComicMetadata { Title = "只有标题" }, 7));

        Assert.Equal("只有标题", root.Element("Title")?.Value);
        Assert.Equal("7", root.Element("PageCount")?.Value);
        Assert.Null(root.Element("Writer"));
        Assert.Null(root.Element("Publisher"));
        Assert.Null(root.Element("Year"));
        Assert.Null(root.Element("Manga"));
        Assert.Null(root.Element("BlackAndWhite"));
        Assert.Null(root.Element("Isbn"));

        // 解析旧文件时没写 PageCount：不生成空元素（0 不是有效页数）。
        Assert.Null(Root(WriteToString(new ComicMetadata { Title = "x" }, 0)).Element("PageCount"));
    }

    [Fact]
    public void Write_Manga与黑白三态映射()
    {
        var rightToLeft = Root(WriteToString(
            new ComicMetadata { Title = "t", RightToLeft = true, BlackAndWhite = true }, 1));

        Assert.Equal("YesAndRightToLeft", rightToLeft.Element("Manga")?.Value);
        Assert.Equal("Yes", rightToLeft.Element("BlackAndWhite")?.Value);

        var leftToRight = Root(WriteToString(
            new ComicMetadata { Title = "t", RightToLeft = false, BlackAndWhite = false }, 1));

        Assert.Equal("No", leftToRight.Element("Manga")?.Value);
        Assert.Equal("No", leftToRight.Element("BlackAndWhite")?.Value);

        // null = 没表态，整项不写：写成 No 会把「不知道」变成用户的决定。
        var unknown = Root(WriteToString(new ComicMetadata { Title = "t" }, 1));

        Assert.Null(unknown.Element("Manga"));
        Assert.Null(unknown.Element("BlackAndWhite"));
    }

    [Fact]
    public void TryParse_读回Manga与黑白()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <ComicInfo xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <Title>解析测试</Title>
              <Manga>YesAndRightToLeft</Manga>
              <BlackAndWhite>No</BlackAndWhite>
              <PageCount>9</PageCount>
            </ComicInfo>
            """;

        Assert.True(ComicInfoXml.TryParse(xml, out var metadata));

        Assert.True(metadata.RightToLeft);
        Assert.False(metadata.BlackAndWhite);
        Assert.Equal(9, metadata.PageCount);
    }

    [Fact]
    public void TryParse_非法输入返回false_不抛异常()
    {
        Assert.False(ComicInfoXml.TryParse(null, out _));
        Assert.False(ComicInfoXml.TryParse("   ", out _));
        Assert.False(ComicInfoXml.TryParse("<ComicInfo><Title>没闭合", out _));

        // 根节点不是 ComicInfo：不把任意 XML 当书籍信息
        Assert.False(ComicInfoXml.TryParse("<Other><Title>x</Title></Other>", out _));
    }

    [Fact]
    public void HasUserValues_空元数据为false_自动项不算用户填写()
    {
        Assert.False(new ComicMetadata().HasUserValues);

        // PageCount 由打包器自动填，用户没填任何东西时不该因此生成 ComicInfo.xml。
        Assert.False(new ComicMetadata { PageCount = 120 }.HasUserValues);

        Assert.True(new ComicMetadata { Title = "x" }.HasUserValues);
        Assert.True(new ComicMetadata { Year = 2024 }.HasUserValues);
        Assert.True(new ComicMetadata { RightToLeft = false }.HasUserValues);
        Assert.True(new ComicMetadata { Isbn = "978" }.HasUserValues);
    }
}
