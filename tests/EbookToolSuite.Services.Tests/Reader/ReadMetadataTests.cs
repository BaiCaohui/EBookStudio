using System.Text;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Files;
using EbookToolSuite.Services.Reader;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests.Reader;

/// <summary>
/// 阅读器目录抽屉的「编辑目录」：保存后宿主用 <see cref="ReaderService.ReadMetadataAsync"/>
/// 重读目录与元数据（不重建阅读缓存）。这两个用例钉住"读回来的是书上现在写的内容"，
/// 以及"源文件不在了要给业务错误、不要抛裸 IO 异常"。
/// </summary>
public sealed class ReadMetadataTests
{
    [Fact]
    public async Task 编辑保存后重读_拿到新的书名作者章数与目录()
    {
        await using var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();

        var epub = Path.Combine(sandbox.Root, "edited.epub");

        new EpubBuilder
        {
            Title = "改过的书名",
            Authors = new List<string> { "改过的作者" },
            ChapterCount = 4,
        }.WriteTo(epub);

        var reader = MakeReader(sandbox);

        var meta = await reader.ReadMetadataAsync(epub);

        Assert.Equal("改过的书名", meta.Title);
        Assert.Equal("改过的作者", meta.Author);
        Assert.Equal(4, meta.SpineCount);
        Assert.NotEmpty(meta.Toc);
    }

    [Fact]
    public async Task 源文件不在了_抛ReaderOpenFailed而不是IO异常()
    {
        await using var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();

        var reader = MakeReader(sandbox);

        var ex = await Assert.ThrowsAsync<AppException>(
            () => reader.ReadMetadataAsync(Path.Combine(sandbox.Root, "missing.epub")));

        Assert.Equal(ErrorCode.ReaderOpenFailed, ex.Code);
    }

    private static ReaderService MakeReader(ImportSandbox sandbox)
        => new(
            sandbox.Repository,
            sandbox.Paths,
            new HashUtil(),
            new DirReaderAssets(sandbox.Root),
            new StubEpubCache(),
            NullLogger<ReaderService>.Instance);

    private sealed class DirReaderAssets(string directory) : IReaderAssets
    {
        public string Directory { get; } = directory;
    }

    /// <summary>ReadMetadataAsync 不碰转换缓存，留个空壳满足构造参数即可。</summary>
    private sealed class StubEpubCache : IEpubCacheService
    {
        public Task<string> GetOrConvertAsync(
            Guid bookId, BookFormatKind sourceFormat, string sourcePath, string cacheKey,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public string GetArtifactPath(string cacheKey) => string.Empty;

        public long? GetConvertedArtifactSize(BookFormat format) => null;

        public void BeginRead(Guid bookId, string cacheRoot)
        {
        }

        public void EndRead(Guid bookId)
        {
        }

        public Task<long> EnforceLimitAsync(CancellationToken ct = default) => Task.FromResult(0L);
    }
}
