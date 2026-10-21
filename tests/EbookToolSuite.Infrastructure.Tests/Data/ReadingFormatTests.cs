using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Infrastructure.Tests.Data;

/// <summary>
/// 用户 2026-10-21：book_reading_format 旁表（0011）。
/// 两个字段各有用途：current_format = 用户选的当前阅读格式（点阅读打开哪个文件）；
/// progress_format = 现有进度是在哪个格式下保存的（换格式续读时决定怎么换算）。
/// </summary>
public sealed class ReadingFormatTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    [Fact]
    public async Task 当前阅读格式_写入读回_批量映射_清空后回到没选过()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var book = NewBook("多格式的书");
        await sb.Repository.AddAsync(book);

        // 没选过 = null（阅读服务按默认优先级链挑）
        Assert.Null(await sb.Repository.GetReadingFormatAsync(book.Id));

        await sb.Repository.SetReadingFormatAsync(book.Id, BookFormatKind.Pdf);
        Assert.Equal(BookFormatKind.Pdf, await sb.Repository.GetReadingFormatAsync(book.Id));

        var map = await sb.Repository.GetReadingFormatMapAsync(new[] { book.Id, Guid.NewGuid() });
        Assert.Equal(BookFormatKind.Pdf, map[book.Id]);
        Assert.Single(map);

        // 改选别的格式
        await sb.Repository.SetReadingFormatAsync(book.Id, BookFormatKind.Txt);
        Assert.Equal(BookFormatKind.Txt, await sb.Repository.GetReadingFormatAsync(book.Id));

        // 传 null = 清掉选择（回到默认链），不是删行
        await sb.Repository.SetReadingFormatAsync(book.Id, null);
        Assert.Null(await sb.Repository.GetReadingFormatAsync(book.Id));
        Assert.Empty(await sb.Repository.GetReadingFormatMapAsync(new[] { book.Id }));
    }

    [Fact]
    public async Task 保存进度时记下来源格式_没传时保留旧值_删书级联清掉()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var book = NewBook("换格式续读的书");
        await sb.Repository.AddAsync(book);

        await sb.Repository.SaveReadingProgressAsync(new ReadingProgress
        {
            BookId = book.Id,
            SpineIndex = 3,
            Location = "{\"chapterIndex\":3}",
            Percent = 0.4,
            UpdatedAt = T0,
            SourceFormat = BookFormatKind.Txt,
        });

        var loaded = await sb.Repository.GetReadingProgressAsync(book.Id);
        Assert.NotNull(loaded);
        Assert.Equal(BookFormatKind.Txt, loaded!.SourceFormat);

        /* 老路径（阅读器没传来源格式）保存时不能把已知来源抹掉：
           否则下一次换格式续读会退化成"只看 PageIndex"的旧判断。 */
        await sb.Repository.SaveReadingProgressAsync(new ReadingProgress
        {
            BookId = book.Id,
            SpineIndex = 4,
            Percent = 0.5,
            UpdatedAt = T0.AddMinutes(1),
        });

        var again = await sb.Repository.GetReadingProgressAsync(book.Id);
        Assert.Equal(4, again!.SpineIndex);
        Assert.Equal(BookFormatKind.Txt, again.SourceFormat);

        // 删书：旁表跟着级联删（不然会留下永远清不掉的行）
        Assert.True(await sb.Repository.DeleteAsync(book.Id));
        Assert.Empty(await sb.QueryStringsAsync("SELECT book_id FROM book_reading_format;"));
        Assert.Empty(await sb.QueryStringsAsync("SELECT book_id FROM reading_progress;"));
    }

    [Fact]
    public async Task 不认识的格式串按没记过处理()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var book = NewBook("脏数据");
        await sb.Repository.AddAsync(book);

        await sb.RawExecuteAsync(
            "INSERT INTO book_reading_format (book_id, current_format, progress_format, updated_at) "
            + "VALUES ('" + book.Id.ToString("D") + "', 'DOCX', '???', 1);");

        Assert.Null(await sb.Repository.GetReadingFormatAsync(book.Id));
    }

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

        foreach (var kind in new[] { BookFormatKind.Epub, BookFormatKind.Txt, BookFormatKind.Pdf })
        {
            book.Formats.Add(new BookFormat
            {
                BookId = book.Id,
                Format = kind,
                FilePath = "Library/" + book.Id.ToString("N") + "." + kind.ToString().ToLowerInvariant(),
                FileSize = 1024,
                AddedAt = T0,
            });
        }

        return book;
    }
}
