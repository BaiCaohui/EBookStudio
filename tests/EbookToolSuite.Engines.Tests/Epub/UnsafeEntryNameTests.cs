using EbookToolSuite.Engines.Epub;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>T7-2 第 6 项：Zip Slip / 绝对路径 / 盘符判定。</summary>
public sealed class UnsafeEntryNameTests
{
    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("OEBPS/../../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("\\absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("OEBPS/a:b.xhtml")]
    public void 越界与绝对路径判为不安全(string name)
    {
        Assert.True(EpubPackageIndex.IsUnsafeEntryName(name), name + " 应该被判为不安全");
    }

    [Theory]
    [InlineData("OEBPS/ch1.xhtml")]
    [InlineData("META-INF/container.xml")]
    [InlineData("OEBPS/chapter..1.xhtml")]   // 两个点是合法文件名，不是路径穿越
    [InlineData("OEBPS/..hidden/x.xhtml")]
    [InlineData("")]
    public void 正常包内路径与合法双点文件名不被误杀(string name)
    {
        Assert.False(EpubPackageIndex.IsUnsafeEntryName(name), name + " 被误判为不安全");
    }
}
