using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Infrastructure.Files;
using EbookToolSuite.Services.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests.Library;

/// <summary>
/// 文件状态扫描（T2-6）的单元测试。
/// 跟导入/删除测试同样的理由：这里的价值就在于「磁盘上真的少了一个文件」，
/// 把文件系统 mock 掉，测试就变成自说自话了。
/// </summary>
public sealed class LibraryScanServiceTests
{
    private static async Task<ImportSandbox> NewSandboxAsync()
    {
        var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();
        return sandbox;
    }

    private static LibraryScanService CreateService(ImportSandbox sandbox)
        => new(sandbox.Repository, new HashUtil(), SystemClock.Instance,
               NullLogger<LibraryScanService>.Instance);

    /// <summary>导一本真实电子书进沙箱，返回书 Id 与正式区文件路径。</summary>
    private static async Task<(Guid Id, string Path)> ImportOneAsync(ImportSandbox sandbox, string title)
    {
        var source = sandbox.WriteEpub(
            new EpubBuilder { Title = title },
            Guid.NewGuid().ToString("N") + ".epub");

        var result = await sandbox.Pipeline.ImportAsync(source, ImportOptions.Default);
        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        return (result.BookId!.Value, result.LibraryPath!);
    }

    private static async Task<bool> IsMissingInDbAsync(ImportSandbox sandbox, Guid bookId)
    {
        var book = await sandbox.Repository.GetByIdAsync(bookId);
        return book!.Formats.Any(f => f.Missing);
    }

    [Fact]
    public async Task 刚导入的书_状态是正常()
    {
        await using var sandbox = await NewSandboxAsync();
        await ImportOneAsync(sandbox, "刚导进来的书");

        var report = await CreateService(sandbox).ScanAsync();

        Assert.True(report.IsEmpty);
        Assert.Equal(1, report.ScannedFormats);
        Assert.Equal(1, report.ScannedBooks);
        Assert.Equal(1, report.NormalCount);
        Assert.Equal(0, report.MissingCount);
        Assert.Equal(0, report.ModifiedCount);
    }

    [Fact]
    public async Task 文件被删掉_判缺失并把缺失标记落库()
    {
        await using var sandbox = await NewSandboxAsync();
        var (id, path) = await ImportOneAsync(sandbox, "会被删掉的书");

        File.Delete(path);

        var report = await CreateService(sandbox).ScanAsync();

        Assert.Equal(1, report.MissingCount);
        var problem = Assert.Single(report.Problems);
        Assert.Equal(BookFileState.Missing, problem.State);
        Assert.Equal(id, problem.BookId);
        Assert.Contains("文件缺失", report.Summary);
        Assert.True(await IsMissingInDbAsync(sandbox, id));
    }

    [Fact]
    public async Task 文件体积变了_判外部修改()
    {
        await using var sandbox = await NewSandboxAsync();
        var (_, path) = await ImportOneAsync(sandbox, "会被外部改的书");

        await File.AppendAllTextAsync(path, "外部加进来的一段内容");

        var report = await CreateService(sandbox).ScanAsync();

        Assert.Equal(1, report.ModifiedCount);
        var problem = Assert.Single(report.Problems);
        Assert.Equal(BookFileState.ExternallyModified, problem.State);
        Assert.NotEqual(problem.ExpectedSize, problem.ActualSize);
    }

    [Fact]
    public async Task 只有修改时间变了_也判外部修改()
    {
        await using var sandbox = await NewSandboxAsync();
        var (_, path) = await ImportOneAsync(sandbox, "只改了时间的书");

        // 体积不变，只把修改时间推后一小时（容差只有 2 秒）
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddHours(1));

        var report = await CreateService(sandbox).ScanAsync();

        Assert.Equal(1, report.ModifiedCount);
        Assert.Equal(BookFileState.ExternallyModified, Assert.Single(report.Problems).State);
    }

    [Fact]
    public async Task 修改时间在容差内_不算外部修改()
    {
        await using var sandbox = await NewSandboxAsync();
        var (_, path) = await ImportOneAsync(sandbox, "时间只差一点点的书");

        // 容差是 2 秒：库里存的是 Unix 毫秒（截断过），不留容差会误报
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));

        var report = await CreateService(sandbox).ScanAsync();

        Assert.True(report.IsEmpty);
    }

    [Fact]
    public async Task 文件改回来_缺失标记被撤掉()
    {
        await using var sandbox = await NewSandboxAsync();
        var (id, path) = await ImportOneAsync(sandbox, "搬走又搬回来的书");
        var service = CreateService(sandbox);

        // 挪走（相当于用户把它拖到别处）
        var moved = path + ".moved";
        File.Move(path, moved);

        Assert.Equal(1, (await service.ScanAsync()).MissingCount);
        Assert.True(await IsMissingInDbAsync(sandbox, id));

        // 再挪回来：体积与修改时间都没变，应该恢复正常
        File.Move(moved, path);

        var report = await service.ScanAsync();

        Assert.True(report.IsEmpty);
        Assert.False(await IsMissingInDbAsync(sandbox, id));
        Assert.Equal(BookFileState.Normal, service.GetBookState(id));
    }

    [Fact]
    public async Task 一本有多个格式_取最严重的那个状态()
    {
        await using var sandbox = await NewSandboxAsync();
        var (id, path) = await ImportOneAsync(sandbox, "两个格式的书");

        // 再补一条格式记录，文件故意不存在（模拟"转出来的 MOBI 被删了"）
        await sandbox.Repository.AddFormatAsync(new BookFormat
        {
            Id = Guid.NewGuid(),
            BookId = id,
            Format = BookFormatKind.Mobi,
            FilePath = Path.Combine(sandbox.Paths.LibraryDir, "不存在的.mobi"),
            FileSize = 100,
            AddedAt = DateTimeOffset.UtcNow,
        });

        // 真文件同时被外部改掉 -> 一本里既有"缺失"又有"外部修改"
        await File.AppendAllTextAsync(path, "外部改的");

        var service = CreateService(sandbox);
        var report = await service.ScanAsync();

        Assert.Equal(2, report.Problems.Count);
        Assert.Equal(BookFileState.Missing, service.GetBookState(id));
    }

    [Fact]
    public async Task 接受外部改动_体积与时间落库_状态回到正常()
    {
        await using var sandbox = await NewSandboxAsync();
        var (id, path) = await ImportOneAsync(sandbox, "要接受改动的书");
        var service = CreateService(sandbox);

        await File.AppendAllTextAsync(path, "外部加进来的一段内容");

        Assert.Equal(1, (await service.ScanAsync()).ModifiedCount);

        var ok = await service.AcceptModificationAsync(id, BookFormatKind.Epub);

        Assert.True(ok);
        Assert.Equal(BookFileState.Normal, service.GetBookState(id));
        Assert.True(service.LastReport!.IsEmpty);

        // 落库的体积是磁盘上的真实体积（不是"计划中"的）
        var book = await sandbox.Repository.GetByIdAsync(id);
        Assert.Equal(new FileInfo(path).Length, book!.Formats[0].FileSize);
        Assert.Equal(
            new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero).ToUnixTimeMilliseconds(),
            book.Formats[0].FileModifiedAt!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task 文件正常时接受改动_返回假_什么都不做()
    {
        await using var sandbox = await NewSandboxAsync();
        var (id, _) = await ImportOneAsync(sandbox, "本来就正常的书");

        var service = CreateService(sandbox);
        await service.ScanAsync();

        Assert.False(await service.AcceptModificationAsync(id, BookFormatKind.Epub));
    }

    [Fact]
    public async Task 文件缺失时接受改动_返回假()
    {
        await using var sandbox = await NewSandboxAsync();
        var (id, path) = await ImportOneAsync(sandbox, "文件没了的书");

        File.Delete(path);

        var service = CreateService(sandbox);
        await service.ScanAsync();

        Assert.False(await service.AcceptModificationAsync(id, BookFormatKind.Epub));
        Assert.Equal(BookFileState.Missing, service.GetBookState(id));
    }

    [Fact]
    public async Task 内部保存后同步基线_不再判外部修改()
    {
        await using var sandbox = await NewSandboxAsync();
        var (id, path) = await ImportOneAsync(sandbox, "用编辑器改过的书");
        var service = CreateService(sandbox);

        // 模拟编辑器写回：内容变了，直接扫描会报"外部修改"
        await File.AppendAllTextAsync(path, "编辑器写回来的一段内容");
        Assert.Equal(1, (await service.ScanAsync()).ModifiedCount);

        var recorded = await service.RecordInternalModificationAsync(id, BookFormatKind.Epub);

        Assert.True(recorded);
        Assert.True(service.LastReport!.IsEmpty);
        Assert.Equal(BookFileState.Normal, service.GetBookState(id));

        // 基线落库：体积就是磁盘上的真实体积（和"接受外部改动"同一条落库口径）
        var book = await sandbox.Repository.GetByIdAsync(id);
        Assert.Equal(new FileInfo(path).Length, book!.Formats[0].FileSize);
    }

    [Fact]
    public async Task 内部保存同步_文件不存在时返回假()
    {
        await using var sandbox = await NewSandboxAsync();
        var (id, path) = await ImportOneAsync(sandbox, "文件已经没了的书");

        File.Delete(path);

        var service = CreateService(sandbox);

        Assert.False(await service.RecordInternalModificationAsync(id, BookFormatKind.Epub));
    }

}
