using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Engines.Calibre;

namespace EbookToolSuite.Engines.Tests.Calibre;

/// <summary>进度解析：中英双轨、诚实策略。</summary>
public sealed class ConversionProgressParserTests
{
    [Theory]
    [InlineData("1% Converting input to HTML...", 0.01)]
    [InlineData("34% Running transforms on e-book...", 0.34)]
    [InlineData("67% Running MOBI Output plugin", 0.67)]
    [InlineData("1% 将输入转换为HTML中...", 0.01)]
    [InlineData("34% 正在对电子书进行转换...", 0.34)]
    public void Feed_中英双轨都能解析出百分比(string line, double expected)
    {
        var parser = new ConversionProgressParser();

        var update = parser.Feed(line, TimeSpan.FromSeconds(3));

        Assert.NotNull(update);
        Assert.Equal(expected, update!.Percent!.Value, 3);
        Assert.Equal(TimeSpan.FromSeconds(3), update.Elapsed);
    }

    [Fact]
    public void Feed_普通输出行不产生进度_绝不伪造百分比()
    {
        var parser = new ConversionProgressParser();

        Assert.Null(parser.Feed("InputFormatPlugin: EPUB Input running", TimeSpan.Zero));
        Assert.Null(parser.Feed("Conversion options changed from defaults:", TimeSpan.Zero));
        Assert.Null(parser.LastPercent);
    }

    [Fact]
    public void Feed_章节行只更新阶段文本_不推进百分比()
    {
        var parser = new ConversionProgressParser();
        parser.Feed("34% Running transforms on e-book...", TimeSpan.Zero);

        var update = parser.Feed("Detected chapter: 第一章 绪论", TimeSpan.FromSeconds(1));

        Assert.NotNull(update);
        Assert.Equal(0.34, update!.Percent!.Value, 3);
        Assert.Contains("第一章", update.StageText);
    }

    [Fact]
    public void Feed_OutputSaved_是唯一的100来源()
    {
        var parser = new ConversionProgressParser();

        var update = parser.Feed(@"Output saved to   E:\x\out.mobi", TimeSpan.FromSeconds(6));

        Assert.NotNull(update);
        Assert.Equal(1d, update!.Percent);
    }

    [Fact]
    public void Feed_超过100或不在范围内的数字不采信()
    {
        var parser = new ConversionProgressParser();

        Assert.Null(parser.Feed("999% nonsense", TimeSpan.Zero));
    }

    [Fact]
    public void Feed_页码形式也能换算()
    {
        var parser = new ConversionProgressParser();

        var update = parser.Feed("Processing page 3 of 12", TimeSpan.Zero);

        Assert.NotNull(update);
        Assert.Equal(0.25, update!.Percent!.Value, 3);
    }
}

/// <summary>高级参数文本框解析。</summary>
public sealed class CalibreAdvancedArgsTests
{
    [Fact]
    public void Parse_空格分隔的键值对()
    {
        var args = CalibreAdvancedArgs.Parse("--base-font-size 18 --line-height 160");

        Assert.Equal(2, args.Count);
        Assert.Equal("--base-font-size", args[0].Key);
        Assert.Equal("18", args[0].Value);
        Assert.Equal("160", args[1].Value);
    }

    [Fact]
    public void Parse_等号写法与开关型()
    {
        var args = CalibreAdvancedArgs.Parse("--base-font-size=20 --no-inline-toc");

        Assert.Equal("20", args[0].Value);
        Assert.Null(args[1].Value);
    }

    [Fact]
    public void Parse_引号里的空格是值的一部分()
    {
        var args = CalibreAdvancedArgs.Parse("--extra-css \"D:\\我的 样式\\a.css\"");

        Assert.Equal(@"D:\我的 样式\a.css", Assert.Single(args).Value);
    }

    [Fact]
    public void Parse_引号不配对要报错()
        => Assert.Throws<AppException>(() => CalibreAdvancedArgs.Parse("--extra-css \"没关引号"));

    [Fact]
    public void Parse_不是以双减号开头要报错()
        => Assert.Throws<AppException>(() => CalibreAdvancedArgs.Parse("base-font-size 18"));

    [Fact]
    public void Parse_空文本返回空()
        => Assert.Empty(CalibreAdvancedArgs.Parse("   "));
}

/// <summary>--help 文本解析：白名单的来源。</summary>
public sealed class CalibreArgsCatalogParseTests
{
    [Fact]
    public void Parse_带等号的选项识别为需要值()
    {
        var options = CalibreArgsCatalog.Parse(Sample);

        var profile = options.Single(o => o.Name == "--output-profile");
        Assert.True(profile.TakesValue);

        var flag = options.Single(o => o.Name == "--no-inline-toc");
        Assert.False(flag.TakesValue);
    }

    [Fact]
    public void Parse_短选项在前的写法不能漏()
        => Assert.Contains(
            CalibreArgsCatalog.Parse(Sample),
            o => o.Name == "--embed-fonts");

    [Fact]
    public void Parse_说明文字不会被当成选项()
        => Assert.DoesNotContain(
            CalibreArgsCatalog.Parse(Sample),
            o => o.Name.Contains("Specify", StringComparison.Ordinal));

    private const string Sample = @"
Options:
  --version             show program's version number and exit

  -h, --help            show this help message and exit

  --output-profile=OUTPUT_PROFILE
                        Specify the output profile.

    --no-inline-toc     Don't add Table of Contents to the book.

  -e, --embed-fonts     Embed all fonts.
";
}
