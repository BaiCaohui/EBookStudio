using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Data;

namespace EbookToolSuite.Infrastructure.Tests.Data;

/// <summary>
/// 阅读状态的维护（T2-3 收尾）。背景：T2-3 之前没有任何代码维护 books.reading_status，
/// 它只在导入时被写成"未读"，于是按状态筛"在读"永远筛不出东西。
/// </summary>
public sealed class ReadingStatusTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    private static Book NewBook(string title)
    {
        var book = new Book
        {
            Title = title,
            Authors = "Someone",
            Status = ReadingStatus.Unread,
            CreatedAt = T0,
            UpdatedAt = T0,
        };

        book.Formats.Add(new BookFormat
        {
            BookId = book.Id,
            Format = BookFormatKind.Epub,
            FilePath = "Library/" + book.Id.ToString("N") + ".epub",
            FileSize = 1000,
            FileModifiedAt = T0,
            AddedAt = T0,
        });

        return book;
    }

    private static ReadingProgress ProgressOf(Guid bookId, double percent, bool atEnd = false) => new()
    {
        BookId = bookId,
        SpineIndex = 0,
        PageIndex = 0,
        TotalPages = 10,
        Percent = percent,
        IsAtEnd = atEnd,
        UpdatedAt = T0,
    };

    [Fact]
    public async Task 存进度时维护阅读状态_未读变在读_读完不变回()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var book = NewBook("会读起来的书");
        await sb.Repository.AddAsync(book);

        Assert.Equal(ReadingStatus.Unread, (await sb.Repository.GetByIdAsync(book.Id))!.Status);

        // 读到一半 -> 在读
        await sb.Repository.SaveReadingProgressAsync(ProgressOf(book.Id, 0.5));
        Assert.Equal(ReadingStatus.Reading, (await sb.Repository.GetByIdAsync(book.Id))!.Status);

        /* 全书进度 100% 但"没到底"（阅读器没报告到末尾）-> 仍然只是在读。
           这条正是用户提问后收紧的那一点：以前只认证进度 >= 99.5%，
           而 855 章的书打开末章就已经是 99.94%。 */
        await sb.Repository.SaveReadingProgressAsync(ProgressOf(book.Id, 1.0, atEnd: false));
        Assert.Equal(ReadingStatus.Reading, (await sb.Repository.GetByIdAsync(book.Id))!.Status);

        // 阅读器报告"在最后一章的末尾" -> 已读完
        await sb.Repository.SaveReadingProgressAsync(ProgressOf(book.Id, 1.0, atEnd: true));
        Assert.Equal(ReadingStatus.Finished, (await sb.Repository.GetByIdAsync(book.Id))!.Status);

        // 重读一本读完的书不该把它变回在读
        await sb.Repository.SaveReadingProgressAsync(ProgressOf(book.Id, 0.2));
        Assert.Equal(ReadingStatus.Finished, (await sb.Repository.GetByIdAsync(book.Id))!.Status);
    }

    [Fact]
    public async Task 迁移0003_把有进度的未读书回填成在读()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var read = NewBook("读过一半的");
        var untouched = NewBook("从没打开过的");
        await sb.Repository.AddAsync(read);
        await sb.Repository.AddAsync(untouched);

        await sb.Repository.SaveReadingProgressAsync(ProgressOf(read.Id, 0.58));

        // 手工退回"未读"，模拟迁移之前就存在的老数据
        await sb.RawExecuteAsync("UPDATE books SET reading_status = 0;");

        // 把版本退到 2，让 0003 再跑一遍
        await sb.RawExecuteAsync("PRAGMA user_version = 2;");
        await sb.Database.InitializeAsync();

        Assert.Equal(ReadingStatus.Reading, (await sb.Repository.GetByIdAsync(read.Id))!.Status);
        Assert.Equal(ReadingStatus.Unread, (await sb.Repository.GetByIdAsync(untouched.Id))!.Status);
        Assert.Equal(SchemaMigrator.TargetVersion, await sb.Database.GetSchemaVersionAsync());
    }

    [Fact]
    public void ReachedEnd_末章顶部不算读完_末章末尾才算()
    {
        static ReaderProgressSnapshot Snapshot(int index, int count, double inChapter) => new()
        {
            BookId = Guid.NewGuid(),
            ChapterIndex = index,
            ChapterCount = count,
            Locator = new ReaderLocator { ChapterIndex = index, PercentInChapter = inChapter },
        };

        /* 855 章的书停在最后一章的开头：全书进度 = 854.5 / 855 = 99.94%，
           但绝不算是读完了  这正是这次要修掉的误判。 */
        Assert.False(Snapshot(854, 855, 0.5).ReachedEnd);

        // 在最后一章、滚到章末 99% -> 读完
        Assert.True(Snapshot(854, 855, 0.99).ReachedEnd);

        // 倒数第二章滚到底也不算
        Assert.False(Snapshot(853, 855, 1.0).ReachedEnd);

        // 末章短到一屏放得下：章内比例就是 1 -> 读完（用户确实把整章看完了）
        Assert.True(Snapshot(0, 1, 1.0).ReachedEnd);

        // 章数未知（0）时一律不算
        Assert.False(Snapshot(0, 0, 1.0).ReachedEnd);
    }

    [Fact]
    public async Task 手动标记阅读状态_四种都能设()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var book = NewBook("要手工标状态的书");
        await sb.Repository.AddAsync(book);

        var all = new[]
        {
            ReadingStatus.Reading,
            ReadingStatus.Finished,
            ReadingStatus.Abandoned,
            ReadingStatus.Unread,
        };

        foreach (var status in all)
        {
            Assert.True(await sb.Repository.SetReadingStatusAsync(book.Id, status));
            Assert.Equal(status, (await sb.Repository.GetByIdAsync(book.Id))!.Status);
        }

        // 不存在的书：一行都没改到
        Assert.False(await sb.Repository.SetReadingStatusAsync(Guid.NewGuid(), ReadingStatus.Finished));
    }
}
