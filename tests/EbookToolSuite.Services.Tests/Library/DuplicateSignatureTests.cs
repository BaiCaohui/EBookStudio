using EbookToolSuite.Core.Common;

namespace EbookToolSuite.Services.Tests.Library;

/// <summary>重复检测的归一化键（T2-2）。纯函数，不碰 IO。</summary>
public sealed class DuplicateSignatureTests
{
    [Theory]
    [InlineData("《Sample Book B》多看精校版", "Sample Book B")]
    [InlineData("Sample Book A", "Sample Book A")]
    [InlineData("民国投机者全集", "民国投机者")]
    [InlineData("Sample Book J", "Sample Book J")]
    [InlineData("  Sample Book J  ", "Sample Book J")]
    [InlineData("TITLE", "title")]
    [InlineData("小 说：第一部", "小说第一部")]
    public void 书名归一化(string input, string expected)
        => Assert.Equal(expected, DuplicateSignature.NormalizeTitle(input));

    /// <summary>执行计划 T2-2 判据 2：同一本书的 EPUB 与 MOBI 不算重复。</summary>
    [Fact]
    public void 格式不同则键不同()
        => Assert.NotEqual(
            DuplicateSignature.For("Sample Book J", "Sample Author", "EPUB"),
            DuplicateSignature.For("Sample Book J", "Sample Author", "MOBI"));

    [Fact]
    public void 作者只取第一位()
        => Assert.Equal(
            DuplicateSignature.NormalizeAuthor("Sample Author"),
            DuplicateSignature.NormalizeAuthor("Sample Author; 张三"));

    [Fact]
    public void 空输入不炸()
    {
        Assert.Equal(string.Empty, DuplicateSignature.NormalizeTitle(null));
        Assert.Equal(string.Empty, DuplicateSignature.NormalizeAuthor(null));
        Assert.Equal("||", DuplicateSignature.For(null, null, null));
    }

    /// <summary>噪声词不能把书名掏空（一本真叫《全集》的书不能变成空串）。</summary>
    [Fact]
    public void 全长等于噪声词时不清空()
        => Assert.Equal("全集", DuplicateSignature.NormalizeTitle("全集"));
}

