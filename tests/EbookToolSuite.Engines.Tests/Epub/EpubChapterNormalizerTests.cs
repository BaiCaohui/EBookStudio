using EbookToolSuite.Core;
using EbookToolSuite.Engines.Epub;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>章节规范化：charset 注入、相对路径改写、XML 语义保住（T1-5）。</summary>
public sealed class EpubChapterNormalizerTests
{
    private const string Source = "OEBPS/Text/ch1.xhtml";

    private static string Render(string body)
        => "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>t</title></head><body>"
           + body + "</body></html>";

    private static EpubChapterNormalizer.Result Normalize(string body)
    {
        var result = EpubChapterNormalizer.Normalize(
            Render(body), Source, ReaderEndpoints.ContentUrlPrefix);

        Assert.NotNull(result);
        return result!;
    }

    [Fact]
    public void 注入_charset_且只注入一次()
    {
        var html = Normalize("<p>中文</p>").Html;

        Assert.Contains("charset=\"utf-8\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, html.Split("charset=", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void 相对图片路径改写成虚拟主机绝对地址()
    {
        var html = Normalize("<img src=\"../Images/a.jpg\"/>").Html;

        Assert.Contains(
            "http://ebook.local/content/OEBPS/Images/a.jpg", html, StringComparison.Ordinal);
    }

    [Fact]
    public void SVG_的_xlink_href_也要改写()
    {
        var html = Normalize(
            "<svg xmlns:xlink=\"http://www.w3.org/1999/xlink\">" +
            "<image xlink:href=\"../Images/cover.png\"/></svg>").Html;

        Assert.Contains(
            "http://ebook.local/content/OEBPS/Images/cover.png", html, StringComparison.Ordinal);
    }

    [Fact]
    public void 样式表与超链接也改写()
    {
        var html = Normalize(
            "<link rel=\"stylesheet\" href=\"../Styles/main.css\"/>" +
            "<a href=\"ch2.xhtml#sec1\">下一章</a>").Html;

        Assert.Contains(
            "http://ebook.local/content/OEBPS/Styles/main.css", html, StringComparison.Ordinal);

        // 链接要保留 fragment，否则章内跳转就断了
        Assert.Contains(
            "http://ebook.local/content/OEBPS/Text/ch2.xhtml#sec1", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com/a.jpg")]
    [InlineData("http://example.com/a.jpg")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("//cdn.example.com/a.jpg")]
    [InlineData("#anchor")]
    public void 绝对地址与锚点不动(string value)
    {
        var html = Normalize($"<img src=\"{value}\"/>").Html;

        Assert.Contains(value, html, StringComparison.Ordinal);
        Assert.DoesNotContain("ebook.local", html, StringComparison.Ordinal);
    }

    [Fact]
    public void 点段可以回退()
    {
        // 章节在 OEBPS/Text/ 下，往上两级正好到包根，所以结果是 content/Images/a.jpg
        var html = Normalize("<img src=\"../../Images/a.jpg\"/>").Html;

        Assert.Contains(
            "http://ebook.local/content/Images/a.jpg", html, StringComparison.Ordinal);
    }

    [Fact]
    public void 越出包根目录的引用不改写()
    {
        // ../../../../ 已经跑到包外，改写它只会造出一个必然 404 的地址，
        // 保持原样更诚实：浏览器会照常报 404，而宿主在日志里能看到。
        var html = Normalize("<img src=\"../../../../../etc/passwd\"/>").Html;

        Assert.DoesNotContain("ebook.local/content/OEBPS/../../", html, StringComparison.Ordinal);
        Assert.Contains("etc/passwd", html, StringComparison.Ordinal);
    }

    [Fact]
    public void 中文文件名做百分号编码()
    {
        var html = Normalize("<img src=\"../Images/封面 图.jpg\"/>").Html;

        Assert.Contains("%E5%B0%81%E9%9D%A2", html, StringComparison.Ordinal);
    }

    [Fact]
    public void 自闭合标签保持_XML_形态()
    {
        // 这一项守的是决定 S8：绝不能用 HTML5 解析器。
        // AngleSharp 之流会把 <br/> 变成 <br>，破坏 XHTML 语义。
        var html = Normalize("<p>甲<br/>乙</p>").Html;

        Assert.Contains("<br />", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<br>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void 命名空间与元素大小写不被改动()
    {
        var html = Normalize("<p>正文</p>").Html;

        Assert.Contains("http://www.w3.org/1999/xhtml", html, StringComparison.Ordinal);
        Assert.Contains("<body>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void 不是合法_XML_时按原文返回并给出说明()
    {
        var broken = "<html><body><p>没闭合";

        var result = EpubChapterNormalizer.Normalize(
            broken, Source, ReaderEndpoints.ContentUrlPrefix);

        Assert.NotNull(result);
        Assert.False(result!.Rewritten);
        Assert.Equal(broken, result.Html);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void 没有_html_根元素时拒绝()
    {
        var result = EpubChapterNormalizer.Normalize(
            "<?xml version=\"1.0\"?><nothtml><body/></nothtml>",
            Source,
            ReaderEndpoints.ContentUrlPrefix);

        Assert.Null(result);
    }

    [Fact]
    public void 空内容返回_null()
    {
        Assert.Null(EpubChapterNormalizer.Normalize(
            "   ", Source, ReaderEndpoints.ContentUrlPrefix));
    }

    [Fact]
    public void 带_DOCTYPE_的章节不炸()
    {
        var xml = "<!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\">" +
                  "<head/><body><p>文字</p></body></html>";

        var result = EpubChapterNormalizer.Normalize(
            xml, Source, ReaderEndpoints.ContentUrlPrefix);

        Assert.NotNull(result);
        Assert.Contains("文字", result!.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void 输出带_doctype_以便浏览器按标准模式渲染()
    {
        var html = Normalize("<p>x</p>").Html;

        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
    }
}