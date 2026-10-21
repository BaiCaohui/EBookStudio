using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Services.Reading;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests.Reading;

/// <summary>
/// 阅读进度服务（T1-6）。用真实的 SQLite 沙箱，不 mock 仓储：
/// "防抖真的减少了写库次数"这件事只有抓到真库上才算数。
/// </summary>
public sealed class ReadingProgressServiceTests
{
    /// <summary>测试用的防抖窗口。比正式的 1.5 秒短，但走的是同一条代码路径。</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private static ReadingProgressService NewService(ImportSandbox sandbox, TimeSpan? debounce = null)
        => new(sandbox.Repository, NullLogger<ReadingProgressService>.Instance, debounce ?? Debounce);

    private static Book NewBook(string title = "测试书") => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static ReaderProgressSnapshot Snapshot(
        Guid bookId,
        int chapterIndex = 2,
        int chapterCount = 10,
        double fraction = 0.5,
        double? scrollTop = 1200,
        double? scrollHeight = 8000) => new()
    {
        BookId = bookId,
        ChapterIndex = chapterIndex,
        ChapterCount = chapterCount,
        Locator = new ReaderLocator
        {
            ChapterIndex = chapterIndex,
            ChapterHref = "OEBPS/ch" + (chapterIndex + 1) + ".xhtml",
            PercentInChapter = fraction,
            ScrollTop = scrollTop,
            ScrollHeight = scrollHeight,
        },
        Fingerprint = new TextFingerprint { Pre = "这一章的开头", Mid = "这一章的开头", Post = "这一章的结尾" },
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<(ImportSandbox Sandbox, Book Book)> NewSandboxAsync()
    {
        var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();

        var book = NewBook();
        await sandbox.Repository.AddAsync(book);

        return (sandbox, book);
    }

    // ---------- 读 ----------

    [Fact]
    public async Task Load_NoRecord_ReturnsNull()
    {
        var (sandbox, book) = await NewSandboxAsync();
        await using var _ = sandbox;

        var service = NewService(sandbox);
        using var __ = service;

        Assert.Null(await service.LoadAsync(book.Id, 10));
    }

    [Fact]
    public async Task Load_RoundTripsAllThreeLevels()
    {
        var (sandbox, book) = await NewSandboxAsync();
        await using var _ = sandbox;

        var service = NewService(sandbox);
        using var __ = service;

        await service.SaveNowAsync(Snapshot(book.Id));

        var loaded = await service.LoadAsync(book.Id, 10);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.ChapterIndex);
        Assert.Equal("OEBPS/ch3.xhtml", loaded.Locator.ChapterHref);
        Assert.Equal(0.5, loaded.Locator.PercentInChapter!.Value, 3);
        Assert.Equal(1200, loaded.Locator.ScrollTop!.Value, 3);
        Assert.Equal(8000, loaded.Locator.ScrollHeight!.Value, 3);
        Assert.Equal("这一章的开头", loaded.Fingerprint!.Pre);

        // 全书比例 = (2 + 0.5) / 10
        Assert.Equal(0.25, loaded.Percent, 3);
    }
    // ---------- 防抖（判据 3：连续滚动期间写库次数必须少） ----------

    [Fact]
    public async Task ScheduleSave_RapidUpdates_WritesOnlyOncePerWindow()
    {
        var (sandbox, book) = await NewSandboxAsync();
        await using var _ = sandbox;

        var service = NewService(sandbox, TimeSpan.FromMilliseconds(300));
        using var __ = service;

        // 模拟"滚动期间不停上报"：20 次上报全挤在同一个防抖窗口里
        for (var i = 0; i < 20; i++)
        {
            service.ScheduleSave(Snapshot(book.Id, scrollTop: 100 + i));
            await Task.Delay(5);
        }

        Assert.True(service.PendingCount > 0, "上报之后应还在队列里，不该立刻写库");
        Assert.Equal(0L, service.SavedCount);

        await Task.Delay(700);

        Assert.Equal(1L, service.SavedCount);
        Assert.Equal(0, service.PendingCount);

        // 落盘的是最后一次的位置，不是第一次
        var loaded = await service.LoadAsync(book.Id, 10);
        Assert.Equal(119, loaded!.Locator.ScrollTop!.Value, 3);
    }

    [Fact]
    public async Task SaveNow_WritesWithoutWaiting()
    {
        var (sandbox, book) = await NewSandboxAsync();
        await using var _ = sandbox;

        var service = NewService(sandbox);
        using var __ = service;

        await service.SaveNowAsync(Snapshot(book.Id));

        Assert.Equal(1L, service.SavedCount);
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public async Task Flush_WritesQueuedSnapshotImmediately()
    {
        var (sandbox, book) = await NewSandboxAsync();
        await using var _ = sandbox;

        var service = NewService(sandbox, TimeSpan.FromHours(1));
        using var __ = service;

        service.ScheduleSave(Snapshot(book.Id));
        Assert.Equal(0L, service.SavedCount);

        await service.FlushAsync();

        Assert.Equal(1L, service.SavedCount);
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public async Task Load_PrefersQueuedSnapshotOverDatabase()
    {
        var (sandbox, book) = await NewSandboxAsync();
        await using var _ = sandbox;

        var service = NewService(sandbox, TimeSpan.FromHours(1));
        using var __ = service;

        // 关掉阅读器又立刻重开同一本书：库里那条还是旧位置，队列里的才是最新的
        service.ScheduleSave(Snapshot(book.Id, scrollTop: 4321));

        var loaded = await service.LoadAsync(book.Id, 10);

        Assert.NotNull(loaded);
        Assert.Equal(4321, loaded!.Locator.ScrollTop!.Value, 3);
    }

    [Fact]
    public async Task Save_FailureIsSwallowed()
    {
        var service = new ReadingProgressService(
            new ThrowingRepository(),
            NullLogger<ReadingProgressService>.Instance,
            Debounce);

        using var _ = service;

        // 三处都不该把异常抛给调用方：进度存不下来最多回到上次位置
        await service.SaveNowAsync(Snapshot(Guid.NewGuid()));
        service.ScheduleSave(Snapshot(Guid.NewGuid()));
        await service.FlushAsync();

        Assert.Equal(0L, service.SavedCount);
    }

    [Fact]
    public void Percent_UsesChapterIndexPlusFraction()
    {
        Assert.Equal(0.25, ReaderProgressSnapshot.ComputePercent(2, 10, 0.5), 3);
        Assert.Equal(0.0, ReaderProgressSnapshot.ComputePercent(0, 10, 0.0), 3);
        Assert.Equal(1.0, ReaderProgressSnapshot.ComputePercent(9, 10, 1.0), 3);

        // 章数缺失、越界、比例缺失都不能炸
        Assert.Equal(0.0, ReaderProgressSnapshot.ComputePercent(3, 0, 0.5), 3);
        Assert.Equal(1.0, ReaderProgressSnapshot.ComputePercent(99, 10, 1.0), 3);
        Assert.Equal(0.0, ReaderProgressSnapshot.ComputePercent(0, 10, null), 3);
    }

    /// <summary>写库永远失败的仓储，用来验"进度存不下来不该影响阅读"。</summary>
    private sealed class ThrowingRepository : IBookRepository
    {
        public Task AddAsync(Book book, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<Book?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<Book>> ListAsync(int offset, int limit, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<int> CountAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<Book>> QueryAsync(BookQuery query, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<int> CountQueryAsync(BookQuery query, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> SetReadingStatusAsync(Guid bookId, ReadingStatus status, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<Guid?> FindBookIdByFingerprintAsync(string quickFingerprint, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<FormatScanRow>> ListFormatsForScanAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<int> UpdateMissingFlagsAsync(
            IReadOnlyList<Guid> markMissing, IReadOnlyList<Guid> clearMissing, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> FormatPathExistsAsync(string filePath, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<FingerprintMatch>> FindBooksByFingerprintsAsync(
            IReadOnlyList<string> quickFingerprints, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task SaveReadingProgressAsync(ReadingProgress progress, CancellationToken ct = default)
            => throw new InvalidOperationException("库坏了");

        public Task<ReadingProgress?> GetReadingProgressAsync(Guid bookId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyDictionary<Guid, ReadingProgress>> GetProgressMapAsync(
            IReadOnlyList<Guid> bookIds, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<BookFormatKind?> GetReadingFormatAsync(Guid bookId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyDictionary<Guid, BookFormatKind>> GetReadingFormatMapAsync(
            IReadOnlyList<Guid> bookIds, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task SetReadingFormatAsync(
            Guid bookId, BookFormatKind? format, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> HasFormatAsync(Guid bookId, BookFormatKind format, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task AddFormatAsync(BookFormat format, CancellationToken ct = default)
            => throw new NotImplementedException();

        /// <summary>用户 2026-10-20：格式记录可以单独删（详情里的「移除该格式」）。</summary>
        public Task<bool> DeleteFormatAsync(Guid formatId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> UpdateFormatMetadataAsync(
            Guid bookId,
            BookFormatKind format,
            long fileSize,
            string? quickFingerprint,
            string? fullHash,
            DateTimeOffset? modifiedAt = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> UpdateMetadataAsync(BookMetadataUpdate update, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
