using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Services.Conversion;

namespace EbookToolSuite.Services.Tests.Conversion;

/// <summary>
/// 重试判定（执行计划 T2-4 判据 b/d/e 的底层逻辑）。
/// 这里把"该重试"与"不该重试"都钉死：两种错都很难在真机上复现，只能靠单测看守。
/// </summary>
public sealed class ConversionRetryPolicyTests
{
    [Theory]
    [InlineData(ErrorCode.DiskFull)]
    [InlineData(ErrorCode.ConversionUnsupported)]
    [InlineData(ErrorCode.ConversionArgsRejected)]
    [InlineData(ErrorCode.ConversionOutputExists)]
    [InlineData(ErrorCode.FileNotFound)]
    [InlineData(ErrorCode.CorruptFile)]
    [InlineData(ErrorCode.ConversionCanceled)]
    [InlineData(ErrorCode.Canceled)]
    public void 不会自愈的错误_一次都不重试(ErrorCode code)
    {
        var options = ConversionQueueOptions.Default;

        // 就算文案里带着"被占用"也不重试：这一类的问题不是占用
        for (var retry = 0; retry <= 5; retry++)
        {
            Assert.False(ConversionRetryPolicy.ShouldRetry(
                code, "Permission denied 文件被占用", retry, options));
        }
    }

    [Fact]
    public void 超时_只额外给一次机会()
    {
        var options = ConversionQueueOptions.Default;
        Assert.Equal(1, options.TimeoutMaxRetries);

        Assert.True(ConversionRetryPolicy.ShouldRetry(
            ErrorCode.ProcessTimeout, "timed out", 0, options));
        Assert.False(ConversionRetryPolicy.ShouldRetry(
            ErrorCode.ProcessTimeout, "timed out", 1, options));
    }

    [Theory]
    [InlineData(ErrorCode.ConversionFailed)]
    [InlineData(ErrorCode.ProcessCrashed)]
    [InlineData(ErrorCode.ConversionNoOutput)]
    public void 文件被占用类失败_重试到上限为止(ErrorCode code)
    {
        var options = ConversionQueueOptions.Default;
        Assert.Equal(3, options.MaxRetries);

        Assert.True(ConversionRetryPolicy.ShouldRetry(code, "Permission denied", 0, options));
        Assert.True(ConversionRetryPolicy.ShouldRetry(code, "Permission denied", 1, options));
        Assert.True(ConversionRetryPolicy.ShouldRetry(code, "Permission denied", 2, options));
        Assert.False(ConversionRetryPolicy.ShouldRetry(code, "Permission denied", 3, options));
    }

    [Theory]
    [InlineData("Permission denied")]
    [InlineData("Access is denied.")]
    [InlineData("The process cannot access the file because it is being used by another process")]
    [InlineData("拒绝访问。")]
    [InlineData("另一个程序正在使用此文件")]
    [InlineData("sharing violation")]
    public void 占用类文案_认得出来(string message)
    {
        Assert.True(ConversionRetryPolicy.LooksTransient(message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("No plugin to handle output format: cbz")]
    [InlineData("invalid choice: kindle_pw5")]
    public void 正经的失败_不认成占用(string? message)
    {
        Assert.False(ConversionRetryPolicy.LooksTransient(message));
        Assert.False(ConversionRetryPolicy.ShouldRetry(
            ErrorCode.ConversionFailed, message, 0, ConversionQueueOptions.Default));
    }

    [Fact]
    public void 没有选项_直接抛()
    {
        Assert.Throws<ArgumentNullException>(() => ConversionRetryPolicy.ShouldRetry(
            ErrorCode.ConversionFailed, "x", 0, null!));
    }
}
