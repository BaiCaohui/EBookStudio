using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Infrastructure.Files;

namespace EbookToolSuite.Infrastructure.Tests.Files;

/// <summary>
/// 磁盘预检（执行计划 T2-4 第 4 条：输入 x 3 + 200MB）。
/// 这个公式是「提前拦住」而不是「跑一半才炸」的唯一防线，值得钉死。
/// </summary>
public sealed class DiskSpaceGuardTests
{
    private const long Mb200 = 200L * 1024 * 1024;

    [Fact]
    public void 需求等于倍数加固定余量()
    {
        Assert.Equal(3000L + Mb200, DiskSpaceGuard.RequiredBytes(1000, 3.0, Mb200));
    }

    [Fact]
    public void 需求向上取整()
    {
        Assert.Equal(5L, DiskSpaceGuard.RequiredBytes(3, 1.5));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-5d)]
    public void 倍数不是正数时按一倍算(double factor)
    {
        Assert.Equal(1000L, DiskSpaceGuard.RequiredBytes(1000, factor));
    }

    [Fact]
    public void 余量为负时当零()
    {
        Assert.Equal(2000L, DiskSpaceGuard.RequiredBytes(1000, 2.0, -100));
    }

    [Fact]
    public void 空间够就不抛()
    {
        DiskSpaceGuard.Ensure(Path.GetTempPath(), 1, 1.0, 0);
    }

    [Fact]
    public void 空间不够抛DiskFull_并且带上需要与可用两个数字()
    {
        var ex = Assert.Throws<AppException>(
            () => DiskSpaceGuard.Ensure(Path.GetTempPath(), long.MaxValue / 4, 2.0));

        Assert.Equal(ErrorCode.DiskFull, ex.Code);
        Assert.True(ex.Context.ContainsKey("requiredBytes"));
        Assert.True(ex.Context.ContainsKey("availableBytes"));
    }
}
