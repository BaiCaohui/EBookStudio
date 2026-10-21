using EbookToolSuite.Core.Common;

namespace EbookToolSuite.Engines.Tests.Comics;

/// <summary>自然序比较器：漫画页码排序的地基（1.jpg 必须在 10.jpg 前面）。</summary>
public sealed class NaturalStringComparerTests
{
    private static IReadOnlyList<string> Sort(params string[] items)
    {
        var list = new List<string>(items);
        list.Sort(NaturalStringComparer.Instance);
        return list;
    }

    [Fact]
    public void 数字段按数值比_而不是按字典序()
    {
        var sorted = Sort("10.jpg", "2.jpg", "1.jpg", "20.jpg", "3.jpg");
        Assert.Equal(new[] { "1.jpg", "2.jpg", "3.jpg", "10.jpg", "20.jpg" }, sorted);
    }

    [Fact]
    public void 数值相同时位数少的排前面()
    {
        var sorted = Sort("1.jpg", "01.jpg", "001.jpg");
        Assert.Equal(new[] { "1.jpg", "01.jpg", "001.jpg" }, sorted);
    }

    [Fact]
    public void 中文页码也能正确排序()
    {
        var sorted = Sort("第10话.jpg", "第2话.jpg", "第1话.jpg");
        Assert.Equal(new[] { "第1话.jpg", "第2话.jpg", "第10话.jpg" }, sorted);
    }

    [Fact]
    public void 大小写不敏感()
    {
        Assert.True(NaturalStringComparer.Instance.Compare("a.jpg", "B.jpg") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("A.jpg", "a.JPG") == 0);
    }

    [Fact]
    public void null_排在最前面且不抛()
    {
        var sorted = Sort("b.jpg", null!, "a.jpg");
        Assert.Null(sorted[0]);
        Assert.Equal("a.jpg", sorted[1]);
    }

    [Fact]
    public void 前缀短的整体排前面()
    {
        var sorted = Sort("page2.jpg", "page10.jpg", "page1.jpg");
        Assert.Equal(new[] { "page1.jpg", "page2.jpg", "page10.jpg" }, sorted);
    }
}
