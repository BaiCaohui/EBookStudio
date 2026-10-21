using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Infrastructure.Tests.Data;

/// <summary>
/// T2-3 的查询测试：搜索、筛选、排序、分页、隐藏副本。
/// 这一层是手写的动态 WHERE + LIKE 转义，mock 掉就什么都不验了，所以一律用真库。
/// </summary>
public sealed class BookQueryTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    private static Book NewBook(
        string title,
        string authors = "Someone",
        ReadingStatus status = ReadingStatus.Unread,
        bool withCover = true,
        long size = 1000,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? lastReadAt = null,
        params BookFormatKind[] formats)
    {
        var book = new Book
        {
            Title = title,
            Authors = authors,
            Status = status,
            CoverPath = withCover ? "Covers/x.jpg" : null,
            CreatedAt = createdAt ?? T0,
            UpdatedAt = createdAt ?? T0,
            LastReadAt = lastReadAt,
        };

        var kinds = formats.Length == 0 ? new[] { BookFormatKind.Epub } : formats;

        foreach (var kind in kinds)
        {
            book.Formats.Add(new BookFormat
            {
                BookId = book.Id,
                Format = kind,
                FilePath = "Library/" + book.Id.ToString("N") + "." + kind.ToString().ToLowerInvariant(),
                FileSize = size,
                FileModifiedAt = T0,
                AddedAt = T0,
            });
        }

        return book;
    }

    private static async Task<DataSandbox> SeedAsync(params Book[] books)
    {
        var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        foreach (var b in books)
        {
            await sb.Repository.AddAsync(b);
        }

        return sb;
    }

    private static async Task<List<string>> TitlesAsync(DataSandbox sb, BookQuery query)
    {
        var books = await sb.Repository.QueryAsync(query);
        return books.Select(b => b.Title).ToList();
    }

    [Fact]
    public async Task Query_关键词命中书名或作者_多词之间是AND()
    {
        await using var sb = await SeedAsync(
            NewBook("射雕英雄传", "金庸"),
            NewBook("天龙八部", "金庸"),
            NewBook("活着", "余华"));

        var byAuthor = await TitlesAsync(sb, new BookQuery { SearchText = "金庸" });
        var andBoth = await TitlesAsync(sb, new BookQuery { SearchText = "金庸 射雕" });
        var byTitle = await TitlesAsync(sb, new BookQuery { SearchText = "射雕" });

        Assert.Equal(2, byAuthor.Count);
        Assert.Equal(new[] { "射雕英雄传" }, andBoth);
        Assert.Equal(new[] { "射雕英雄传" }, byTitle);
    }

    [Fact]
    public async Task Query_关键词大小写不敏感()
    {
        await using var sb = await SeedAsync(NewBook("Harry Potter", "J.K. Rowling"));

        Assert.Single(await TitlesAsync(sb, new BookQuery { SearchText = "harry" }));
        Assert.Single(await TitlesAsync(sb, new BookQuery { SearchText = "HARRY" }));
        Assert.Single(await TitlesAsync(sb, new BookQuery { SearchText = "rowling" }));
    }

    [Fact]
    public async Task Query_LIKE通配符被转义_搜百分号不会变成前缀匹配()
    {
        await using var sb = await SeedAsync(
            NewBook("100%纯棉"),
            NewBook("100本好书"));

        var hits = await TitlesAsync(sb, new BookQuery { SearchText = "100%" });

        Assert.Equal(new[] { "100%纯棉" }, hits);
    }

    [Fact]
    public async Task Query_按格式筛选_文件缺失的不算()
    {
        var missing = NewBook("书还在但文件没了", formats: new[] { BookFormatKind.Epub });
        missing.Formats[0].Missing = true;

        await using var sb = await SeedAsync(
            NewBook("有 EPUB 的"),
            NewBook("只有 MOBI 的", formats: new[] { BookFormatKind.Mobi }),
            missing);

        var hits = await sb.Repository.QueryAsync(new BookQuery { Format = BookFormatKind.Epub });

        var only = Assert.Single(hits);
        Assert.Equal("有 EPUB 的", only.Title);
        Assert.Single(only.Formats);
    }

    [Fact]
    public async Task Query_按状态与封面筛选()
    {
        await using var sb = await SeedAsync(
            NewBook("没读过有封面"),
            NewBook("在读有封面", status: ReadingStatus.Reading),
            NewBook("在读没封面", status: ReadingStatus.Reading, withCover: false));

        var reading = await TitlesAsync(sb, new BookQuery { Status = ReadingStatus.Reading });
        var readingWithCover = await TitlesAsync(
            sb, new BookQuery { Status = ReadingStatus.Reading, OnlyWithCover = true });

        Assert.Equal(2, reading.Count);
        Assert.Equal(new[] { "在读有封面" }, readingWithCover);
    }

    [Fact]
    public async Task Query_四种排序()
    {
        var older = T0.AddDays(-10);
        var newer = T0.AddDays(10);

        await using var sb = await SeedAsync(
            // 作者用 ASCII：SQLite 的 NOCASE 只管 ASCII，中文是按码点排的，不是拼音
            NewBook("B 书", "zeta", size: 3000, createdAt: T0, lastReadAt: T0),
            NewBook("A 书", "alpha", size: 1000, createdAt: newer, lastReadAt: newer),
            NewBook("C 书", "mike", size: 2000, createdAt: older));

        Assert.Equal(new[] { "A 书", "B 书", "C 书" },
            await TitlesAsync(sb, new BookQuery { Sort = BookSort.Title }));

        // 按作者：alpha(A) < mike(C) < zeta(B)。中文书名不参与比较，但排序键仍是作者。
        Assert.Equal(new[] { "A 书", "C 书", "B 书" },
            await TitlesAsync(sb, new BookQuery { Sort = BookSort.Author }));

        // 最近加入的在前
        Assert.Equal(new[] { "A 书", "B 书", "C 书" },
            await TitlesAsync(sb, new BookQuery { Sort = BookSort.AddedAt, Descending = true }));

        // 文件大的在前
        Assert.Equal(new[] { "B 书", "C 书", "A 书" },
            await TitlesAsync(sb, new BookQuery { Sort = BookSort.FileSize, Descending = true }));

        // 最近读过的在前；从没读过的（last_read_at 为 NULL）排最后
        Assert.Equal(new[] { "A 书", "B 书", "C 书" },
            await TitlesAsync(sb, new BookQuery { Sort = BookSort.LastReadAt, Descending = true }));
    }

    [Fact]
    public async Task Query_被标为副本的书不出现在结果里()
    {
        var kept = NewBook("留下的");
        var copy = NewBook("副本");
        await using var sb = await SeedAsync(kept, copy);

        var duplicates = new EbookToolSuite.Infrastructure.Data.Repositories.DuplicateRepository(sb.Factory);
        await duplicates.HideCopiesAsync(kept.Id, new[] { copy.Id });

        var hits = await TitlesAsync(sb, new BookQuery());

        Assert.Equal(new[] { "留下的" }, hits);
        Assert.Equal(1, await sb.Repository.CountQueryAsync(new BookQuery()));
    }

    [Fact]
    public async Task Query_分页与总数()
    {
        await using var sb = await SeedAsync(
            NewBook("书 1"), NewBook("书 2"), NewBook("书 3"), NewBook("书 4"), NewBook("书 5"));

        var page1 = await TitlesAsync(sb, new BookQuery { Limit = 2, Offset = 0 });
        var page2 = await TitlesAsync(sb, new BookQuery { Limit = 2, Offset = 2 });
        var page3 = await TitlesAsync(sb, new BookQuery { Limit = 2, Offset = 4 });

        Assert.Equal(new[] { "书 1", "书 2" }, page1);
        Assert.Equal(new[] { "书 3", "书 4" }, page2);
        Assert.Equal(new[] { "书 5" }, page3);
        Assert.Equal(5, await sb.Repository.CountQueryAsync(new BookQuery()));
        Assert.Equal(5, await sb.Repository.CountQueryAsync(new BookQuery { SearchText = "书" }));
    }
}