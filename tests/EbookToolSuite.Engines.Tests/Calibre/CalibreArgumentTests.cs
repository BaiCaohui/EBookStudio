using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Calibre;

namespace EbookToolSuite.Engines.Tests.Calibre;

/// <summary>参数构建器：合并优先级与白名单拦截（T1-4 的核心防线）。</summary>
public sealed class CalibreArgumentBuilderTests
{
    private static CalibreOptions Catalog(bool fallback = false) => new(
        "epub",
        "mobi",
        new[]
        {
            new CalibreOption("--output-profile", true),
            new CalibreOption("--base-font-size", true),
            new CalibreOption("--personal-doc", true),
            new CalibreOption("--no-inline-toc", false),
        },
        fallback);

    [Fact]
    public void Merge_高优先级覆盖同名键且保持原顺序()
    {
        var merged = CalibreArgumentBuilder.Merge(
            new[]
            {
                new ConversionArgument("--base-font-size", "18"),
                new ConversionArgument("--line-height", "160"),
            },
            new[] { new ConversionArgument("--base-font-size", "22") });

        Assert.Equal(2, merged.Count);
        Assert.Equal("--base-font-size", merged[0].Key);
        Assert.Equal("22", merged[0].Value);
        Assert.Equal("--line-height", merged[1].Key);
    }

    [Fact]
    public void NormalizeKey_补上缺失的双减号()
        => Assert.Equal("--base-font-size", CalibreArgumentBuilder.NormalizeKey("base-font-size"));

    [Fact]
    public void Build_未知参数被拦下()
    {
        var error = Assert.Throws<AppException>(() => CalibreArgumentBuilder.Build(
            Catalog(),
            Array.Empty<ConversionArgument>(),
            new[] { new ConversionArgument("--not-an-option", "1") }));

        Assert.Equal(ErrorCode.ConversionArgsRejected, error.Code);
    }

    [Fact]
    public void Build_带值参数缺值被拦下()
    {
        var error = Assert.Throws<AppException>(() => CalibreArgumentBuilder.Build(
            Catalog(),
            new[] { new ConversionArgument("--base-font-size", null) },
            Array.Empty<ConversionArgument>()));

        Assert.Equal(ErrorCode.ConversionArgsRejected, error.Code);
    }

    [Fact]
    public void Build_开关型参数带值也放行_值不参与命令行()
    {
        var built = CalibreArgumentBuilder.Build(
            Catalog(),
            new[] { new ConversionArgument("--no-inline-toc", null) },
            Array.Empty<ConversionArgument>());

        Assert.Equal("--no-inline-toc", Assert.Single(built).Key);
    }

    [Fact]
    public void ToCommandLineOptions_开关只出一个元素_带值出两个()
    {
        var line = CalibreArgumentBuilder.ToCommandLineOptions(new[]
        {
            new ConversionArgument("--output-profile", "default"),
            new ConversionArgument("--no-inline-toc", null),
        });

        Assert.Equal(new[] { "--output-profile", "default", "--no-inline-toc" }, line);
    }
}
