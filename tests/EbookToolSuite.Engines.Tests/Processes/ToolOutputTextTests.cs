using EbookToolSuite.Engines.Processes;

namespace EbookToolSuite.Engines.Tests.Processes;

/// <summary>T7-2 第 11 项：外部工具输出乱码兜底。</summary>
public sealed class ToolOutputTextTests
{
    [Fact]
    public void 连续替换符收成明确标记()
    {
        var text = "进度 50%" + '\uFFFD' + '\uFFFD' + '\uFFFD' + " 完成";

        var clean = ToolOutputText.Sanitize(text);

        Assert.DoesNotContain('\uFFFD', clean);
        Assert.Contains(ToolOutputText.EncodingFailureMark, clean);
        Assert.Contains("进度 50%", clean);
        Assert.Contains("完成", clean);
    }

    [Fact]
    public void 单个替换符也会被替换()
    {
        Assert.DoesNotContain('\uFFFD', ToolOutputText.Sanitize("a" + '\uFFFD' + "b"));
    }

    [Fact]
    public void 控制字符被剔除但保留制表符()
    {
        var clean = ToolOutputText.Sanitize("a\u0001b\u0007c\tdef");

        Assert.Equal("abc\tdef", clean);
    }

    [Fact]
    public void 正常中文与英文原样保留()
    {
        const string text = "转换完成：0 errors, 3 warnings";

        Assert.Equal(text, ToolOutputText.Sanitize(text));
    }

    [Fact]
    public void 空值不抛异常()
    {
        Assert.Equal(string.Empty, ToolOutputText.Sanitize(null));
        Assert.Equal(string.Empty, ToolOutputText.Sanitize(string.Empty));
    }
}
