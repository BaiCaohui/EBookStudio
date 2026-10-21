using EbookToolSuite.Core;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Services.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests.Library;

/// <summary>
/// 删除服务（T1-3 补丁需求：单本删除 + 批量删除）的单元测试。
/// 和导入测试同样的理由：删除的价值在于「真的删了文件、真的删了库记录」，
/// mock 掉任何一段都会让测试变成自说自话。
/// </summary>
public sealed class BookDeletionServiceTests
{
    private static async Task<ImportSandbox> NewSandboxAsync()
    {
        var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();
        return sandbox;
    }

    private static BookDeletionService CreateService(ImportSandbox sandbox)
        => new(sandbox.Repository, sandbox.Paths, NullLogger<BookDeletionService>.Instance);

    /// <summary>导一本带封面的书，返回书 Id 与正式区文件路径。</summary>
    private static async Task<(Guid Id, string Path)> ImportOneAsync(ImportSandbox sandbox, string title)
    {
        var source = sandbox.WriteEpub(
            new EpubBuilder { Title = title, CoverBytes = EpubBuilder.MakeJpeg(400, 600) },
            Guid.NewGuid().ToString("N") + ".epub");

        var result = await sandbox.Pipeline.ImportAsync(source, ImportOptions.Default);
        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        return (result.BookId!.Value, result.LibraryPath!);
    }

    [Fact]
    public async Task DeleteAsync_库记录_正式区文件_两档封面与阅读缓存一起删掉()
    {
        await using var sandbox = await NewSandboxAsync();
        var (id, libraryPath) = await ImportOneAsync(sandbox, "要删掉的书");

        var book = await sandbox.Repository.GetByIdAsync(id);
        var fingerprint = book!.Formats[0].QuickFingerprint!;

        // 造一个阅读缓存目录，验证它也会被一起清掉（否则删书会留一堆永久垃圾）
        var readerCache = Path.Combine(
            sandbox.Paths.ReaderCacheDir, ReaderEndpoints.CacheFolderName(fingerprint));
        Directory.CreateDirectory(readerCache);
        File.WriteAllText(Path.Combine(readerCache, "reader.json"), "{}");

        Assert.Equal(2, sandbox.CoverFiles().Length);
        Assert.True(Directory.Exists(readerCache));

        var report = await CreateService(sandbox).DeleteAsync(new[] { id });

        Assert.True(report.AllRemoved);
        Assert.Equal(1, report.RemovedCount);
        Assert.Null(report.Items[0].Warning);
        Assert.Null(await sandbox.Repository.GetByIdAsync(id));
        Assert.Equal(0, await sandbox.BookCountAsync());
        Assert.False(File.Exists(libraryPath));
        Assert.Empty(sandbox.LibraryFiles());
        Assert.Empty(sandbox.CoverFiles());
        Assert.False(Directory.Exists(readerCache));
    }

    [Fact]
    public async Task DeleteAsync_阅读进度靠外键级联一起删掉()
    {
        await using var sandbox = await NewSandboxAsync();
        var (id, _) = await ImportOneAsync(sandbox, "读过一半的书");

        await sandbox.Repository.SaveReadingProgressAsync(new ReadingProgress
        {
            BookId = id,
            SpineIndex = 3,
            Percent = 0.42,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        Assert.NotNull(await sandbox.Repository.GetReadingProgressAsync(id));

        var report = await CreateService(sandbox).DeleteAsync(new[] { id });

        Assert.True(report.AllRemoved);
        Assert.Null(await sandbox.Repository.GetReadingProgressAsync(id));
    }

    [Fact]
    public async Task DeleteAsync_批量_一次删掉多本()
    {
        await using var sandbox = await NewSandboxAsync();
        var a = await ImportOneAsync(sandbox, "第一本");
        var b = await ImportOneAsync(sandbox, "第二本");
        var c = await ImportOneAsync(sandbox, "第三本");

        Assert.Equal(3, await sandbox.BookCountAsync());
        Assert.Equal(3, sandbox.LibraryFiles().Length);

        var report = await CreateService(sandbox).DeleteAsync(new[] { a.Id, b.Id, c.Id });

        Assert.Equal(3, report.RemovedCount);
        Assert.Equal(0, report.FailedCount);
        Assert.Equal(0, await sandbox.BookCountAsync());
        Assert.Empty(sandbox.LibraryFiles());
    }

    [Fact]
    public async Task DeleteAsync_书不存在_不抛异常且逐本报告未删除()
    {
        await using var sandbox = await NewSandboxAsync();

        var report = await CreateService(sandbox).DeleteAsync(new[] { Guid.NewGuid() });

        Assert.Single(report.Items);
        Assert.False(report.Items[0].Removed);
        Assert.Equal(1, report.FailedCount);
        Assert.NotNull(report.Items[0].Warning);
    }

    [Fact]
    public async Task DeleteAsync_同一个Id传两次_只删一次()
    {
        await using var sandbox = await NewSandboxAsync();
        var (id, _) = await ImportOneAsync(sandbox, "重复 Id");

        var report = await CreateService(sandbox).DeleteAsync(new[] { id, id });

        Assert.Single(report.Items);
        Assert.True(report.AllRemoved);
    }

    [Fact]
    public async Task DeleteAsync_文件被占用删不掉时_仍然从书库移除并给出警告()
    {
        await using var sandbox = await NewSandboxAsync();
        var (id, libraryPath) = await ImportOneAsync(sandbox, "被占用的书");

        // 独占打开正式区文件，让 File.Delete 必然失败（模拟阅读器还占着这本书）
        await using var locked = new FileStream(
            libraryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var report = await CreateService(sandbox).DeleteAsync(new[] { id });

        // 主契约是「从书库消失」，所以 Removed 必须为真，只是带一条警告
        Assert.True(report.Items[0].Removed);
        Assert.NotNull(report.Items[0].Warning);
        Assert.Equal(1, report.WarningCount);
        Assert.Null(await sandbox.Repository.GetByIdAsync(id));
        Assert.Equal(0, await sandbox.BookCountAsync());

        // 文件还在（这就是那条警告说的东西）
        Assert.True(File.Exists(libraryPath));
    }
}