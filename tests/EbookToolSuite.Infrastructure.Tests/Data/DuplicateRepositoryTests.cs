using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Data.Repositories;

namespace EbookToolSuite.Infrastructure.Tests.Data;

public sealed class DuplicateRepositoryTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    private static Book NewBook(
        string title,
        params (BookFormatKind Kind, string Fingerprint, bool Missing)[] formats)
    {
        var book = new Book
        {
            Title = title,
            Authors = "Someone",
            CreatedAt = T0,
            UpdatedAt = T0,
        };

        foreach (var f in formats)
        {
            book.Formats.Add(new BookFormat
            {
                BookId = book.Id,
                Format = f.Kind,
                FilePath = "Library/" + book.Id.ToString("N") + "_" + f.Kind,
                FileSize = 1024,
                QuickFingerprint = f.Fingerprint,
                FileModifiedAt = T0,
                Missing = f.Missing,
                AddedAt = T0,
            });
        }

        return book;
    }

    [Fact]
    public async Task ListCandidates_每本书每个格式一行_跳过missing()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        var repo = new DuplicateRepository(sb.Factory);

        await sb.Repository.AddAsync(NewBook(
            "甲",
            (BookFormatKind.Epub, "fp-epub", false),
            (BookFormatKind.Mobi, "fp-mobi", true)));

        var row = Assert.Single(await repo.ListCandidatesAsync());

        Assert.Equal("甲", row.Title);
        Assert.Equal("epub", row.Format, ignoreCase: true);
    }

    [Fact]
    public async Task FindByFingerprint_命中的是missing文件也要返回()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        var repo = new DuplicateRepository(sb.Factory);

        await sb.Repository.AddAsync(NewBook("乙", (BookFormatKind.Epub, "fp-hit", true)));

        var hit = await repo.FindByFingerprintAsync("fp-hit");
        var miss = await repo.FindByFingerprintAsync("fp-none");

        Assert.NotNull(hit);
        Assert.Equal("乙", hit!.Title);
        Assert.NotEqual(Guid.Empty, hit.FormatId);
        Assert.Null(miss);
    }

    [Fact]
    public async Task SaveContentHash_把全文件哈希写回()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        var repo = new DuplicateRepository(sb.Factory);

        var book = NewBook("丙", (BookFormatKind.Epub, "fp-c", false));
        await sb.Repository.AddAsync(book);

        var stored = await sb.Repository.GetByIdAsync(book.Id);
        await repo.SaveContentHashAsync(stored!.Formats[0].Id, "ABCDEF");

        var back = await sb.Repository.GetByIdAsync(book.Id);
        Assert.Equal("ABCDEF", back!.Formats[0].FullHash);
    }

    [Fact]
    public async Task HideCopies_隐藏后不再出现在候选里_恢复后回到候选()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        var repo = new DuplicateRepository(sb.Factory);

        var kept = NewBook("保留的", (BookFormatKind.Epub, "fp-k", false));
        var copy = NewBook("副本", (BookFormatKind.Epub, "fp-copy", false));
        await sb.Repository.AddAsync(kept);
        await sb.Repository.AddAsync(copy);

        Assert.Equal(2, (await repo.ListCandidatesAsync()).Count);

        var marked = await repo.HideCopiesAsync(kept.Id, new[] { copy.Id });

        Assert.Equal(1, marked);
        var left = Assert.Single(await repo.ListCandidatesAsync());
        Assert.Equal(kept.Id, left.BookId);
        Assert.Contains(copy.Id, await repo.ListHiddenBookIdsAsync());

        await repo.RestoreCopiesAsync(new[] { copy.Id });

        Assert.Equal(2, (await repo.ListCandidatesAsync()).Count);
        Assert.Empty(await repo.ListHiddenBookIdsAsync());
    }

    [Fact]
    public async Task IgnoreGroup_记录一次后重复调用不报错()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        var repo = new DuplicateRepository(sb.Factory);

        await repo.IgnoreGroupAsync("id-a,id-b");
        await repo.IgnoreGroupAsync("id-a,id-b");

        Assert.Contains("id-a,id-b", await repo.ListIgnoredSignaturesAsync());
    }
}
