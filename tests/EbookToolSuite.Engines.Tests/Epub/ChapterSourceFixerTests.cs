using EbookToolSuite.Engines.Epub;
using Xunit;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>章节源码自动修正（T5-2W）：白名单里那些确定性修正。</summary>
public sealed class ChapterSourceFixerTests
{
    private const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html xmlns=\"http://www.w3.org/1999/xhtml\">\n<head><title>t</title></head>\n";
    private const string Tail = "\n</html>";

    private readonly ChapterSourceFixer _fixer = new();
    private readonly ChapterSourceEditor _editor = new();

    private (string Fixed, int Count) Fix(string body)
    {
        var source = Head + "<body>" + body + "</body>" + Tail;
        var (fixedText, fixes) = _fixer.Fix(source);

        return (fixedText, fixes.Count);
    }

    [Fact]
    public void 空标签没自闭合_补成自闭合()
    {
        var (fixedText, count) = Fix("<p>甲<br>乙<img src=\"a.png\"></p>");

        Assert.True(count >= 2);
        Assert.Contains("<br/>", fixedText, StringComparison.Ordinal);
        Assert.Contains("<img src=\"a.png\"/>", fixedText, StringComparison.Ordinal);
        Assert.True(_editor.Check(fixedText).Ok);
    }

    [Fact]
    public void 裸露的与号_改成实体()
    {
        var (fixedText, count) = Fix("<p>甲 & 乙</p>");

        Assert.Equal(1, count);
        Assert.Contains("甲 &amp; 乙", fixedText, StringComparison.Ordinal);
        Assert.True(_editor.Check(fixedText).Ok);
    }

    [Fact]
    public void 命名实体_转成数字实体()
    {
        var (fixedText, count) = Fix("<p>甲&nbsp;乙&mdash;丙</p>");

        Assert.Equal(2, count);
        Assert.Contains("&#160;", fixedText, StringComparison.Ordinal);
        Assert.Contains("&#8212;", fixedText, StringComparison.Ordinal);
        Assert.True(_editor.Check(fixedText).Ok);
    }

    [Fact]
    public void 属性没引号_补双引号()
    {
        var (fixedText, count) = Fix("<p class=noind>甲</p>");

        Assert.Equal(1, count);
        Assert.Contains("class=\"noind\"", fixedText, StringComparison.Ordinal);
        Assert.True(_editor.Check(fixedText).Ok);
    }

    [Fact]
    public void 正文里的裸小于号_转义()
    {
        var (fixedText, count) = Fix("<p>如果 a < b 那么</p>");

        Assert.Equal(1, count);
        Assert.Contains("a &lt; b", fixedText, StringComparison.Ordinal);
        Assert.True(_editor.Check(fixedText).Ok);
    }

    [Fact]
    public void 标签没闭合_在出错的位置补上结束标签()
    {
        // 用户真实撞到的形态：两个 div 没关，报错落在 </body>
        var (fixedText, count) = Fix("<div class=\"keeptext\"><p>甲</p><div class=\"sidebar\"><p>乙</p>");

        Assert.Equal(2, count);
        Assert.True(_editor.Check(fixedText).Ok);
        Assert.EndsWith("</div></div></body>" + Tail, fixedText, StringComparison.Ordinal);
    }

    [Fact]
    public void 多余的结束标签_删掉()
    {
        var (fixedText, count) = Fix("<p>甲</p></div>");

        Assert.Equal(1, count);
        Assert.True(_editor.Check(fixedText).Ok);
        Assert.DoesNotContain("</div>", fixedText, StringComparison.Ordinal);
    }

    [Fact]
    public void 交叉嵌套_修正后仍然良构()
    {
        var (fixedText, count) = Fix("<b><i>甲</b>乙</i>");

        Assert.True(count >= 1);
        Assert.True(_editor.Check(fixedText).Ok);
    }

    [Fact]
    public void 已经良构的源码_一个字节都不改()
    {
        var body = "<p class=\"noind\">甲&nbsp;已经转好&#160;了</p>";
        var source = Head + "<body>" + body.Replace("&nbsp;", "&#160;", StringComparison.Ordinal) + "</body>" + Tail;

        var (fixedText, fixes) = _fixer.Fix(source);

        Assert.Empty(fixes);
        Assert.Equal(source, fixedText);
    }

    [Fact]
    public void 修正之后再修一遍_不会再改()
    {
        var (onceFixed, _) = Fix("<p>甲<br>乙&nbsp;丙</p>");
        var (twiceFixed, second) = _fixer.Fix(onceFixed);

        Assert.Empty(second);
        Assert.Equal(onceFixed, twiceFixed);
    }

    [Fact]
    public void 建议里带行号与改法说明()
    {
        var source = Head + "<body>\n<p>甲<br>乙</p>\n</body>" + Tail;
        var fixes = _fixer.Scan(source);

        Assert.Single(fixes);
        Assert.Equal(5, fixes[0].Line);        // Head 里有 3 个换行，<br> 落在第 5 行
        Assert.Contains("<br/>", fixes[0].Display, StringComparison.Ordinal);
    }
}
