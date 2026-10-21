using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Infrastructure.Tests.Data;

/// <summary>
/// 用户 2026-10-20：「移除该格式」的数据层验收。
/// 关键行为：只删 book_formats 那一行；书的其它格式、阅读进度、阅读状态一件都不许动。
/// </summary>
public sealed class DeletedFormatTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    [Fact]
    public async Task 删格式记录_只删那一条_其它格式还在()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var book = NewBook("双格式书", BookFormatKind.Epub, BookFormatKind.Pdf);
        await sb.Repository.AddAsync(book);

        var epub = book.Formats.Single(f => f.Format == BookFormatKind.Epub);

        var deleted = await sb.Repository.DeleteFormatAsync(epub.Id);

        Assert.True(deleted);

        var reloaded = await sb.Repository.GetByIdAsync(book.Id);
        Assert.NotNull(reloaded);
        Assert.Single(reloaded!.Formats);
        Assert.Equal(BookFormatKind.Pdf, reloaded.Formats[0].Format);

        // 格式记录没了，查重预检也不能再命中
        Assert.False(await sb.Repository.HasFormatAsync(book.Id, BookFormatKind.Epub));
        Assert.True(await sb.Repository.HasFormatAsync(book.Id, BookFormatKind.Pdf));

        // 书本身还在（不是删书）
        Assert.Equal(1, await sb.Repository.CountAsync());
    }

    [Fact]
    public async Task 删格式记录_阅读进度与阅读状态都不变()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var book = NewBook("有进度的书", BookFormatKind.Epub, BookFormatKind.Pdf);
        await sb.Repository.AddAsync(book);

        await sb.Repository.SaveReadingProgressAsync(new ReadingProgress
        {
            BookId = book.Id,
            SpineIndex = 7,
            Location = "{\"chapterIndex\":7,\"percentInChapter\":0.5}",
            Percent = 0.42,
            UpdatedAt = T0,
        });

        await sb.Repository.SetReadingStatusAsync(book.Id, ReadingStatus.Reading);

        var epub = book.Formats.Single(f => f.Format == BookFormatKind.Epub);
        Assert.True(await sb.Repository.DeleteFormatAsync(epub.Id));

        var progress = await sb.Repository.GetReadingProgressAsync(book.Id);
        Assert.NotNull(progress);
        Assert.Equal(7, progress!.SpineIndex);
        Assert.Equal(0.42, progress.Percent, 3);
        Assert.Equal(T0, progress.UpdatedAt);

        var reloaded = await sb.Repository.GetByIdAsync(book.Id);
        Assert.Equal(ReadingStatus.Reading, reloaded!.Status);
    }

    [Fact]
    public async Task 删格式记录_重复删返回false_不会抛()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var book = NewBook("单格式书", BookFormatKind.Epub);
        await sb.Repository.AddAsync(book);

        var formatId = book.Formats[0].Id;

        Assert.True(await sb.Repository.DeleteFormatAsync(formatId));
        Assert.False(await sb.Repository.DeleteFormatAsync(formatId));
        Assert.False(await sb.Repository.DeleteFormatAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task 删掉最后一个格式记录_书还在但零格式()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var book = NewBook("只有 EPUB 的书", BookFormatKind.Epub);
        await sb.Repository.AddAsync(book);

        Assert.True(await sb.Repository.DeleteFormatAsync(book.Formats[0].Id));

        var reloaded = await sb.Repository.GetByIdAsync(book.Id);
        Assert.NotNull(reloaded);
        Assert.Empty(reloaded!.Formats);
    }

    [Fact]
    public async Task 删格式记录_不影响别的书()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var first = NewBook("第一本", BookFormatKind.Epub, BookFormatKind.Pdf);
        var second = NewBook("第二本", BookFormatKind.Epub);
        await sb.Repository.AddAsync(first);
        await sb.Repository.AddAsync(second);

        Assert.True(await sb.Repository.DeleteFormatAsync(
            first.Formats.Single(f => f.Format == BookFormatKind.Epub).Id));

        var secondReloaded = await sb.Repository.GetByIdAsync(second.Id);
        Assert.NotNull(secondReloaded);
        Assert.Single(secondReloaded!.Formats);
        Assert.Equal(BookFormatKind.Epub, secondReloaded.Formats[0].Format);
    }

    private static Book NewBook(string title, params BookFormatKind[] formats)
    {
        var book = new Book
        {
            Title = title,
            TitleSort = title,
            Authors = "Someone",
            Status = ReadingStatus.Unread,
            CreatedAt = T0,
            UpdatedAt = T0,
        };

        foreach (var kind in formats)
        {
            book.Formats.Add(new BookFormat
            {
                BookId = book.Id,
                Format = kind,
                FilePath = "Library/" + book.Id.ToString("N") + kind.ToExtension(),
                FileSize = 2048,
                AddedAt = T0,
            });
        }

        return book;
    }
}
