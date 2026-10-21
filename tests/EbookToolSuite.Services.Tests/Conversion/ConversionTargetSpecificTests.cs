using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Services.Conversion;

namespace EbookToolSuite.Services.Tests.Conversion;

/// <summary>
/// 判据 5：--mobi-file-type 只能用于 MOBI 输出。
/// 这条不能只靠参数白名单：白名单是按「输入->输出」组合探测出来的，
/// 探测失败时会退化成一份兜底清单，那里面也列了这个参数。
/// </summary>
public sealed class ConversionTargetSpecificTests
{
    private static IReadOnlyList<ConversionArgument> WithMobiFileType() =>
        new[] { new ConversionArgument("--mobi-file-type", "both") };

    [Theory]
    [InlineData(BookFormatKind.Epub)]
    [InlineData(BookFormatKind.Pdf)]
    [InlineData(BookFormatKind.Txt)]
    [InlineData(BookFormatKind.Azw3)]
    public void 非MOBI目标带mobi_file_type会被拦下(BookFormatKind target)
    {
        var ex = Assert.Throws<AppException>(
            () => ConversionService.ValidateTargetSpecificOptions(target, WithMobiFileType()));

        Assert.Equal(ErrorCode.ConversionArgsRejected, ex.Code);
    }

    [Fact]
    public void MOBI目标放行()
        => ConversionService.ValidateTargetSpecificOptions(
            BookFormatKind.Mobi, WithMobiFileType());

    [Fact]
    public void 大小写不同的写法也拦得下()
    {
        var ex = Assert.Throws<AppException>(() => ConversionService.ValidateTargetSpecificOptions(
            BookFormatKind.Epub,
            new[] { new ConversionArgument("--MOBI-FILE-TYPE", "old") }));

        Assert.Equal(ErrorCode.ConversionArgsRejected, ex.Code);
    }

    [Fact]
    public void 别的参数不受影响()
        => ConversionService.ValidateTargetSpecificOptions(
            BookFormatKind.Epub,
            new[] { new ConversionArgument("--epub-version", "3") });
}
