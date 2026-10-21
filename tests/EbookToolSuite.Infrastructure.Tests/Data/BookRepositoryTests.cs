using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;
using Microsoft.Data.Sqlite;

namespace EbookToolSuite.Infrastructure.Tests.Data;

public sealed class BookRepositoryTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    [Fact]
    public async Task 回写元数据_改到的列变了_没让改的列一个都没动()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var book = NewBook("旧书名", BookFormatKind.Epub);
        book.Status = ReadingStatus.Finished;
        book.CoverPath = "Covers/keep.jpg";
        book.WordCount = 999;
        await sb.Repository.AddAsync(book);

        var changed = await sb.Repository.UpdateMetadataAsync(new BookMetadataUpdate(
            BookId: book.Id,
            Title: "新书名",
            TitleSort: "新书名",
            Authors: "新作者",
            AuthorSort: "新作者",
            Publisher: "新出版社",
            PublishedDate: "2024-05-01",
            Isbn: "978-7-111-11111-1",
            Language: "en",
            Description: "新简介",
            Series: "新系列",
            SeriesIndex: 3.5,
            Rating: 5));

        Assert.True(changed);

        var reloaded = await sb.Repository.GetByIdAsync(book.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("新书名", reloaded!.Title);
        Assert.Equal("新书名", reloaded.TitleSort);
        Assert.Equal("新作者", reloaded.Authors);
        Assert.Equal("新出版社", reloaded.Publisher);
        Assert.Equal("978-7-111-11111-1", reloaded.Isbn);
        Assert.Equal("en", reloaded.Language);
        Assert.Equal("新简介", reloaded.Description);
        Assert.Equal("新系列", reloaded.Series);
        Assert.Equal(3.5, reloaded.SeriesIndex);
        Assert.Equal(5, reloaded.Rating);

        // 不归这条路管的列：一个都不许被顺手改掉
        Assert.Equal(ReadingStatus.Finished, reloaded.Status);
        Assert.Equal("Covers/keep.jpg", reloaded.CoverPath);
        Assert.Equal(999, reloaded.WordCount);

        // updated_at 必须动（书库靠它知道"这本刚被改过"）
        Assert.True(reloaded.UpdatedAt > book.UpdatedAt);
    }

    private static Book NewBook(string title, params BookFormatKind[] formats)
    {
        var book = new Book
        {
            Title = title,
            TitleSort = title,
            Authors = "Someone",
            AuthorSort = "mou ren",
            Translators = "Someone 译",
            Publisher = "某出版社",
            PublishedDate = new DateOnly(2020, 5, 17),
            Isbn = "978-7-000-00000-0",
            Language = "zh",
            Description = "简介",
            Series = "某系列",
            SeriesIndex = 2.5,
            Rating = 4,
            Status = ReadingStatus.Reading,
            Identifier = "urn:uuid:test",
            WordCount = 12345,
            ChapterCount = 12,
            CoverPath = "Covers/x.jpg",
            Damaged = false,
            CreatedAt = T0,
            UpdatedAt = T0,
        };

        foreach (var kind in formats)
        {
            book.Formats.Add(MakeFormat(book.Id, kind, "Library/" + book.Id.ToString("N") + "_" + kind));
        }

        return book;
    }

    private static BookFormat MakeFormat(Guid bookId, BookFormatKind kind, string path) => new()
    {
        BookId = bookId,
        Format = kind,
        FilePath = path,
        FileSize = 1024,
        FullHash = "hash-" + kind,
        QuickFingerprint = "fp-" + kind,
        FileModifiedAt = T0,
        Missing = false,
        AddedAt = T0,
    };

    [Fact]
    public async Task AddAsync_与GetByIdAsync_字段往返一致()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var book = NewBook("测试书", BookFormatKind.Epub, BookFormatKind.Txt);
        await sb.Repository.AddAsync(book);

        var loaded = await sb.Repository.GetByIdAsync(book.Id);

        Assert.NotNull(loaded);
        Assert.Equal(book.Id, loaded!.Id);
        Assert.Equal("测试书", loaded.Title);
        Assert.Equal("Someone", loaded.Authors);
        Assert.Equal("Someone 译", loaded.Translators);
        Assert.Equal("某出版社", loaded.Publisher);
        Assert.Equal(new DateOnly(2020, 5, 17), loaded.PublishedDate);
        Assert.Equal("978-7-000-00000-0", loaded.Isbn);
        Assert.Equal("zh", loaded.Language);
        Assert.Equal("某系列", loaded.Series);
        Assert.Equal(2.5, loaded.SeriesIndex!.Value, 3);
        Assert.Equal(4, loaded.Rating);
        Assert.Equal(ReadingStatus.Reading, loaded.Status);
        Assert.Equal(12345, loaded.WordCount);
        Assert.Equal(12, loaded.ChapterCount);
        Assert.Equal("Covers/x.jpg", loaded.CoverPath);
        Assert.False(loaded.Damaged);
        Assert.Null(loaded.ImportBatchId);
        Assert.Equal(T0, loaded.CreatedAt);
        Assert.Equal(T0, loaded.UpdatedAt);
        Assert.Null(loaded.LastReadAt);
    }

    [Fact]
    public async Task AddAsync_写入全部格式文件_顺序按加入时间()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var book = NewBook("多格式", BookFormatKind.Epub, BookFormatKind.Mobi, BookFormatKind.Txt);
        await sb.Repository.AddAsync(book);

        var loaded = await sb.Repository.GetByIdAsync(book.Id);

        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Formats.Count);
        Assert.Equal(
            [BookFormatKind.Epub, BookFormatKind.Mobi, BookFormatKind.Txt],
            loaded.Formats.Select(f => f.Format));

        var epub = loaded.Formats[0];
        Assert.Equal(book.Id, epub.BookId);
        Assert.Equal(1024, epub.FileSize);
        Assert.Equal("hash-Epub", epub.FullHash);
        Assert.False(epub.Missing);
        Assert.Equal(T0, epub.AddedAt);
    }

    [Fact]
    public async Task GetByIdAsync_不存在的Id_返回null()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        Assert.Null(await sb.Repository.GetByIdAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ListAsync_按书名排序_并支持分页()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        foreach (var t in new[] { "D", "B", "A", "C" })
        {
            await sb.Repository.AddAsync(NewBook(t, BookFormatKind.Epub));
        }

        Assert.Equal(4, await sb.Repository.CountAsync());

        var first = await sb.Repository.ListAsync(0, 2);
        var second = await sb.Repository.ListAsync(2, 2);

        Assert.Equal(["A", "B"], first.Select(b => b.Title));
        Assert.Equal(["C", "D"], second.Select(b => b.Title));

        // 这一页的书必须带着自己的格式文件回来（一次 IN 查询填充，不能是空的）
        Assert.All(first, b => Assert.Single(b.Formats));
    }

    [Fact]
    public async Task FormatPathExistsAsync_按规范化绝对路径判定()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var book = NewBook("路径检查", BookFormatKind.Epub);
        var realPath = Path.Combine(sb.Paths.LibraryDir, "a.epub");
        book.Formats[0].FilePath = realPath;
        await sb.Repository.AddAsync(book);

        Assert.True(await sb.Repository.FormatPathExistsAsync(realPath));
        Assert.False(await sb.Repository.FormatPathExistsAsync(
            Path.Combine(sb.Paths.LibraryDir, "b.epub")));
    }

    [Fact]
    public async Task 同一本书同一格式重复_触发唯一约束()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var book = NewBook("重复格式");
        book.Formats.Add(MakeFormat(book.Id, BookFormatKind.Epub, "Library/one.epub"));
        book.Formats.Add(MakeFormat(book.Id, BookFormatKind.Epub, "Library/two.epub"));

        var ex = await Assert.ThrowsAsync<SqliteException>(() => sb.Repository.AddAsync(book));
        Assert.Contains("UNIQUE", ex.Message, StringComparison.OrdinalIgnoreCase);

        // 整个事务必须回滚：不能留下半本书
        Assert.Equal(0, await sb.Repository.CountAsync());
    }

    [Fact]
    public async Task 不同书引用同一文件路径_触发唯一约束()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        const string shared = "Library/shared.epub";
        var a = NewBook("甲");
        a.Formats.Add(MakeFormat(a.Id, BookFormatKind.Epub, shared));
        await sb.Repository.AddAsync(a);

        var b = NewBook("乙");
        b.Formats.Add(MakeFormat(b.Id, BookFormatKind.Epub, shared));

        await Assert.ThrowsAsync<SqliteException>(() => sb.Repository.AddAsync(b));
    }

    [Fact]
    public async Task 阅读进度_可新增可覆盖_并同步last_read_at()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var book = NewBook("进度", BookFormatKind.Epub);
        await sb.Repository.AddAsync(book);

        Assert.Null(await sb.Repository.GetReadingProgressAsync(book.Id));

        await sb.Repository.SaveReadingProgressAsync(new ReadingProgress
        {
            BookId = book.Id,
            SpineIndex = 3,
            PageIndex = 10,
            TotalPages = 200,
            Location = "#chapter3",
            Fingerprint = "abc123",
            Percent = 12.5,
            UpdatedAt = T0,
        });

        var p1 = await sb.Repository.GetReadingProgressAsync(book.Id);
        Assert.NotNull(p1);
        Assert.Equal(3, p1!.SpineIndex);
        Assert.Equal(10, p1.PageIndex);
        Assert.Equal(200, p1.TotalPages);
        Assert.Equal("#chapter3", p1.Location);
        Assert.Equal("abc123", p1.Fingerprint);
        Assert.Equal(12.5, p1.Percent, 3);
        Assert.Equal(T0, p1.UpdatedAt);

        var later = T0.AddMinutes(30);
        await sb.Repository.SaveReadingProgressAsync(new ReadingProgress
        {
            BookId = book.Id,
            SpineIndex = 4,
            Percent = 40,
            UpdatedAt = later,
        });

        var p2 = await sb.Repository.GetReadingProgressAsync(book.Id);
        Assert.NotNull(p2);
        Assert.Equal(4, p2!.SpineIndex);
        Assert.Equal(40, p2.Percent, 3);
        Assert.Null(p2.Location);
        Assert.Equal(later, p2.UpdatedAt);

        // 只有一行进度，不是两行
        Assert.Equal(1, await sb.RawScalarAsync<int>("SELECT COUNT(1) FROM reading_progress;"));

        var lastRead = await sb.Repository.GetByIdAsync(book.Id);
        Assert.Equal(later, lastRead!.LastReadAt);
    }

    [Fact]
    public async Task 批量进度查询_只返回有进度的书()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var a = NewBook("甲", BookFormatKind.Epub);
        var b = NewBook("乙", BookFormatKind.Epub);
        var c = NewBook("丙", BookFormatKind.Epub);

        await sb.Repository.AddAsync(a);
        await sb.Repository.AddAsync(b);
        await sb.Repository.AddAsync(c);

        await sb.Repository.SaveReadingProgressAsync(new ReadingProgress
        {
            BookId = a.Id, SpineIndex = 1, Percent = 10, UpdatedAt = T0,
        });

        await sb.Repository.SaveReadingProgressAsync(new ReadingProgress
        {
            BookId = c.Id, SpineIndex = 5, Percent = 55, UpdatedAt = T0,
        });

        // 网格角标用的是一次 IN 查询：没读过的书不该出现在字典里
        var map = await sb.Repository.GetProgressMapAsync(new[] { a.Id, b.Id, c.Id, Guid.NewGuid() });

        Assert.Equal(2, map.Count);
        Assert.Equal(10, map[a.Id].Percent, 3);
        Assert.Equal(55, map[c.Id].Percent, 3);
        Assert.False(map.ContainsKey(b.Id));

        // 空列表不能炸，也不能拼出非法 SQL
        Assert.Empty(await sb.Repository.GetProgressMapAsync(Array.Empty<Guid>()));
    }

    [Fact]
    public async Task 删除书_级联清掉格式文件与进度()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var book = NewBook("待删", BookFormatKind.Epub);
        await sb.Repository.AddAsync(book);
        await sb.Repository.SaveReadingProgressAsync(new ReadingProgress
        {
            BookId = book.Id,
            Percent = 1,
            UpdatedAt = T0,
        });

        await sb.RawExecuteAsync($"DELETE FROM books WHERE id = '{book.Id}';");

        Assert.Equal(0, await sb.RawScalarAsync<int>("SELECT COUNT(1) FROM book_formats;"));
        Assert.Equal(0, await sb.RawScalarAsync<int>("SELECT COUNT(1) FROM reading_progress;"));
    }
    [Fact]
    public async Task ListFormatsForScanAsync_带书名_按书名与格式排序()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var second = NewBook("B书", BookFormatKind.Epub, BookFormatKind.Mobi);
        var first = NewBook("A书", BookFormatKind.Epub);
        await sb.Repository.AddAsync(second);
        await sb.Repository.AddAsync(first);

        var rows = await sb.Repository.ListFormatsForScanAsync();

        Assert.Equal(3, rows.Count);
        Assert.Equal("A书", rows[0].Title);
        Assert.Equal("B书", rows[1].Title);
        Assert.Equal(BookFormatKind.Epub, rows[0].Format);
        Assert.Equal(first.Id, rows[0].BookId);
        Assert.Equal(1024, rows[0].FileSize);
        Assert.Equal(T0, rows[0].FileModifiedAt);
        Assert.False(rows[0].Missing);
    }

    [Fact]
    public async Task UpdateMissingFlagsAsync_标记与撤销分两个方向()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var book = NewBook("改缺失标记", BookFormatKind.Epub, BookFormatKind.Txt);
        await sb.Repository.AddAsync(book);

        var epubId = book.Formats.First(f => f.Format == BookFormatKind.Epub).Id;
        var txtId = book.Formats.First(f => f.Format == BookFormatKind.Txt).Id;

        // 扫描发现 TXT 的文件没了
        Assert.Equal(1, await sb.Repository.UpdateMissingFlagsAsync(new[] { txtId }, Array.Empty<Guid>()));

        var rows = await sb.Repository.ListFormatsForScanAsync();
        Assert.True(rows.Single(r => r.Format == BookFormatKind.Txt).Missing);
        Assert.False(rows.Single(r => r.Format == BookFormatKind.Epub).Missing);

        // 文件又回来了
        Assert.Equal(1, await sb.Repository.UpdateMissingFlagsAsync(Array.Empty<Guid>(), new[] { txtId }));

        rows = await sb.Repository.ListFormatsForScanAsync();
        Assert.False(rows.Single(r => r.Format == BookFormatKind.Txt).Missing);
    }

    [Fact]
    public async Task UpdateMissingFlagsAsync_两个方向都空_直接返回零()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        Assert.Equal(0, await sb.Repository.UpdateMissingFlagsAsync(
            Array.Empty<Guid>(), Array.Empty<Guid>()));
    }
}
