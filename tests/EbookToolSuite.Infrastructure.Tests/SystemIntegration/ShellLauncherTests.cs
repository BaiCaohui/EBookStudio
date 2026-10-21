using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Infrastructure.SystemIntegration;

namespace EbookToolSuite.Infrastructure.Tests.SystemIntegration;

/// <summary>
/// ShellLauncher 里能单测的那部分：explorer 的 /select 参数格式。
/// 这段格式很挑：**逗号后面多一个空格**，资源管理器就会去打开「文档」目录而不是选中文件；
/// 路径含空格或中文时不加引号同样会失败。
/// 真正"弹不弹资源管理器"没法单测（也不该在测试里真的弹窗）。
/// </summary>
public sealed class ShellLauncherTests
{
    [Fact]
    public void BuildRevealArguments_逗号后不能有空格_路径必须带引号()
    {
        var args = ShellLauncher.BuildRevealArguments(@"C:\Books\Sample Book I.mobi");

        Assert.Equal("/select,\"C:\\Books\\Sample Book I.mobi\"", args);
        Assert.StartsWith("/select,\"", args);
        Assert.DoesNotContain(", \"", args);
    }

    [Fact]
    public void BuildRevealArguments_路径带空格也原样放进引号里()
    {
        var args = ShellLauncher.BuildRevealArguments(@"D:\My Books\Library\a b\c.epub");

        Assert.Equal("/select,\"D:\\My Books\\Library\\a b\\c.epub\"", args);
    }

    [Fact]
    public void RevealInExplorer_文件不存在_抛FileNotFound()
    {
        var launcher = new ShellLauncher();
        var missing = Path.Combine(Path.GetTempPath(), "ets-not-here-" + Guid.NewGuid().ToString("N") + ".epub");

        var ex = Assert.Throws<AppException>(() => launcher.RevealInExplorer(missing));

        Assert.Equal(ErrorCode.FileNotFound, ex.Code);
    }

    [Fact]
    public void RevealInExplorer_路径为空_抛参数异常()
    {
        var launcher = new ShellLauncher();

        Assert.Throws<ArgumentException>(() => launcher.RevealInExplorer("  "));
    }
}
