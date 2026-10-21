using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Infrastructure.Tests.Data;

/// <summary>
/// 用户 2026-10-20：跨格式定位按"已读百分比"粗略换算。
/// 这里只测纯函数（不碰窗口）：换算规则、边界夹取、以及"来源快照的原始百分比"能不能读出来。
/// </summary>
public sealed class CrossFormatLocatorTests
{
    [Theory]
    [InlineData(0.0, 100, 0)]
    [InlineData(0.5, 100, 50)]
    [InlineData(1.0, 100, 99)]     // 100% 落到最后一页，不能越界
    [InlineData(0.556, 100, 56)]   // 四舍五入：页码要尽可能接近
    [InlineData(0.25, 300, 75)]
    public void 百分比换算PDF页码_大致等于百分比乘总页数(double percent, int totalPages, int expected)
        => Assert.Equal(expected, CrossFormatLocator.PageFromPercent(percent, totalPages));

    [Theory]
    [InlineData(0.0, 20, 0)]
    [InlineData(0.5, 20, 10)]
    [InlineData(1.0, 20, 19)]      // 100% 落到最后一章
    [InlineData(0.55, 20, 11)]     // 向下取整，别跳过整整一章
    public void 百分比换算章号_向下取整且夹在范围内(double percent, int chapterCount, int expected)
        => Assert.Equal(expected, CrossFormatLocator.ChapterFromPercent(percent, chapterCount));

    [Fact]
    public void 换算边界_页数章数未知或百分比越界都不炸()
    {
        Assert.Equal(0, CrossFormatLocator.PageFromPercent(0.5, 0));
        Assert.Equal(0, CrossFormatLocator.PageFromPercent(0.5, -3));
        Assert.Equal(0, CrossFormatLocator.PageFromPercent(-1, 100));
        Assert.Equal(99, CrossFormatLocator.PageFromPercent(3, 100));

        Assert.Equal(0, CrossFormatLocator.ChapterFromPercent(0.5, 0));
        Assert.Equal(0, CrossFormatLocator.ChapterFromPercent(-1, 20));
        Assert.Equal(19, CrossFormatLocator.ChapterFromPercent(2, 20));
    }

    [Theory]
    [InlineData(BookFormatKind.Pdf, true)]
    [InlineData(BookFormatKind.Cbz, true)]
    [InlineData(BookFormatKind.Epub, false)]
    [InlineData(BookFormatKind.Txt, false)]
    [InlineData(BookFormatKind.Mobi, false)]
    public void 固定版式只有PDF与漫画(BookFormatKind kind, bool expected)
        => Assert.Equal(expected, CrossFormatLocator.IsFixedLayout(kind));

    [Fact]
    public void 有页码才算进度来自固定版式()
    {
        Assert.False(CrossFormatLocator.ProgressFromFixedLayout(null));
        Assert.True(CrossFormatLocator.ProgressFromFixedLayout(0));
        Assert.True(CrossFormatLocator.ProgressFromFixedLayout(12));
    }

    [Fact]
    public void 从库里还原的进度_保留原始百分比()
    {
        /* 场景（用户 2026-10-20）：上次用 EPUB 读到 55%，这次 EPUB 文件被删了改用 PDF 打开。
           PDF 清单没有章，LoadAsync 传进来的 chapterCount 是 0，
           快照自己算 Percent 会变成 0 —— 跨格式换算必须拿库里存的这个原始值。 */
        var row = new ReadingProgress
        {
            BookId = Guid.NewGuid(),
            SpineIndex = 5,
            Percent = 0.55,
            Location = "{\"chapterIndex\":5,\"percentInChapter\":0.5}",
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var snapshot = ReaderProgressSnapshot.FromRow(row, chapterCount: 0);

        Assert.NotNull(snapshot);
        Assert.Equal(0.55, snapshot!.StoredPercent);
        Assert.Equal(0.0, snapshot.Percent, 3);   // 章数为 0，算不出来 —— 正是要绕开的那件事
    }

    [Fact]
    public void 同格式仍用精确章号_换算函数不参与()
    {
        // 这条是文档性断言：文本->文本时 PageIndex 为 null，说明不该走百分比换算
        var textRow = new ReadingProgress
        {
            BookId = Guid.NewGuid(),
            SpineIndex = 3,
            Percent = 0.3,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var snapshot = ReaderProgressSnapshot.FromRow(textRow, chapterCount: 10);

        Assert.False(CrossFormatLocator.ProgressFromFixedLayout(snapshot!.PageIndex));
        Assert.Equal(3, snapshot.ChapterIndex);
        Assert.Equal(0.3, snapshot.Percent, 3);
    }
}
