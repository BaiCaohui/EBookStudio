using System.Text;
using System.Text.Json.Nodes;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Infrastructure.Files;
using EbookToolSuite.Services.Reader;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests.Reading;

/// <summary>
/// T6-1：MOBI/AZW3 惰性转 EPUB 缓存 + 缓存上限淘汰。
/// 这里用假的 IEpubCacheService 造「转换产物」，验的是 ReaderService 的缓存键/命中/失效；
/// 真跑 Calibre 的端到端在 build\smoke-t61.ps1。
/// </summary>
public sealed class T61ReaderCacheTests
{
    [Fact]
    public async Task 打开MOBI_首次转换_二次命中_源文件替换后缓存自动失效()
    {
        await using var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();

        // 假的"转换产物"：必须是结构合法的 EPUB，ReaderService 要真解包它。
        var convertedEpub = Path.Combine(sandbox.Root, "converted.epub");
        new EpubBuilder { Title = "转换后的书", ChapterCount = 3 }.WriteTo(convertedEpub);

        // 造一本只有 MOBI 的书。
        var mobiPath = Path.Combine(sandbox.SourceDir, "sample.mobi");
        await File.WriteAllBytesAsync(mobiPath, new byte[2048]);

        var now = SystemClock.Instance.Now;
        var book = new Book
        {
            Id = Guid.NewGuid(), Title = "MOBI 测试书", CreatedAt = now, UpdatedAt = now,
        };
        book.Formats.Add(new BookFormat
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            Format = BookFormatKind.Mobi,
            FilePath = mobiPath,
            FileSize = 2048,
            AddedAt = now,
        });
        await sandbox.Repository.AddAsync(book);

        var cache = new RecordingEpubCache(convertedEpub, sandbox.Paths.ReaderCacheDir);
        var reader = new ReaderService(
            sandbox.Repository,
            sandbox.Paths,
            new HashUtil(),
            MakeAssets(sandbox),
            cache,
            NullLogger<ReaderService>.Instance);

        // 判据 1：第一次打开走转换，能读出正文（3 章）。
        var first = await reader.OpenAsync(book.Id);

        Assert.Equal(BookFormatKind.Mobi, first.SourceFormat);
        Assert.False(first.FromCache);
        Assert.Equal(3, first.Manifest.Spine.Count);
        Assert.Equal(1, cache.Conversions);

        // 判据 2：第二次打开命中缓存，不再转换。
        var second = await reader.OpenAsync(book.Id);

        Assert.True(second.FromCache);
        Assert.Equal(1, cache.Conversions);
        Assert.Equal(first.CacheRoot, second.CacheRoot);

        // 判据 3：源 MOBI 被替换（长度也变了） 缓存键变  自动失效、重新转换。
        await File.WriteAllBytesAsync(mobiPath, new byte[4096]);
        File.SetLastWriteTimeUtc(mobiPath, DateTime.UtcNow.AddSeconds(3));

        var third = await reader.OpenAsync(book.Id);

        Assert.False(third.FromCache);
        Assert.Equal(2, cache.Conversions);
        Assert.NotEqual(first.CacheRoot, third.CacheRoot);
    }

    [Fact]
    public async Task 缓存超上限_删最久没用的_跳过正在读的()
    {
        await using var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();

        var root = sandbox.Paths.ReaderCacheDir;
        Directory.CreateDirectory(root);

        MakeCache(root, "old", 600 * 1024, DateTime.UtcNow.AddHours(-2));
        MakeCache(root, "new", 600 * 1024, DateTime.UtcNow);

        var service = new EpubCacheService(
            sandbox.Paths,
            new ThrowingQueue(),
            new LimitConfig(1.0),
            NullLogger<EpubCacheService>.Instance);

        // "new" 正在读：清理必须跳过它。
        service.BeginRead(Guid.NewGuid(), Path.Combine(root, "new"));

        var freed = await service.EnforceLimitAsync();

        Assert.True(freed > 0, "1.2 MB > 1 MB，必须回收");
        Assert.False(Directory.Exists(Path.Combine(root, "old")), "最久没用的 old 应被删");
        Assert.True(Directory.Exists(Path.Combine(root, "new")), "正在读的 new 必须留着");
    }

    private static void MakeCache(string root, string name, int bytes, DateTime lastWriteUtc)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);

        var manifest = Path.Combine(dir, "reader.json");
        File.WriteAllBytes(manifest, new byte[bytes]);
        File.SetLastWriteTimeUtc(manifest, lastWriteUtc);
        Directory.SetLastWriteTimeUtc(dir, lastWriteUtc);
    }

    private static IReaderAssets MakeAssets(ImportSandbox sandbox)
    {
        var dir = Path.Combine(sandbox.Root, "reader-assets");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "reader.html"), "<html></html>", new UTF8Encoding(false));
        return new DirReaderAssets(dir);
    }

    private sealed class DirReaderAssets(string directory) : IReaderAssets
    {
        public string Directory { get; } = directory;
    }

    /// <summary>假的转换缓存：直接把预造好的 EPUB 复制成缓存产物，并记录转了几次。</summary>
    private sealed class RecordingEpubCache(string convertedEpub, string cacheRoot) : IEpubCacheService
    {
        public int Conversions { get; private set; }

        public Task<string> GetOrConvertAsync(
            Guid bookId, BookFormatKind sourceFormat, string sourcePath,
            string cacheKey, CancellationToken ct = default)
        {
            var artifact = GetArtifactPath(cacheKey);

            // 与真实 EpubCacheService 一致：产物已在 = 缓存命中，不再转换。
            if (File.Exists(artifact))
            {
                return Task.FromResult(artifact);
            }

            Conversions++;
            Directory.CreateDirectory(cacheRoot);
            File.Copy(convertedEpub, artifact, overwrite: true);
            return Task.FromResult(artifact);
        }

        public string GetArtifactPath(string cacheKey)
            => Path.Combine(cacheRoot, cacheKey + ".source.epub");

        public long? GetConvertedArtifactSize(BookFormat format) => null;

        public void BeginRead(Guid bookId, string cacheRoot)
        {
        }

        public void EndRead(Guid bookId)
        {
        }

        public Task<long> EnforceLimitAsync(CancellationToken ct = default) => Task.FromResult(0L);
    }

    private sealed class LimitConfig(double limitMb) : IConfigStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public JsonNode? Get(string dottedPath) => null;

        public string? GetString(string dottedPath, string? fallback = null) => fallback;

        public bool GetBool(string dottedPath, bool fallback) => fallback;

        public double GetDouble(string dottedPath, double fallback)
            => dottedPath == EpubCacheService.CacheLimitKey ? limitMb : fallback;

        public Task SetAsync(string dottedPath, JsonNode? value, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>淘汰测试不会入队，碰到入队就该失败。</summary>
    private sealed class ThrowingQueue : IConversionQueue
    {
        public IReadOnlyList<ConversionTask> Tasks => Array.Empty<ConversionTask>();

        public int QueuedCount => 0;

        public int RunningCount => 0;

        public bool IsBusy => false;

        public event Action<ConversionTask>? TaskChanged
        {
            add { }
            remove { }
        }

        public event Action? TasksChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ConversionTask> EnqueueAsync(ConversionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ConversionTask>> EnqueueManyAsync(
            IReadOnlyList<ConversionRequest> requests, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ConversionTask> RetryAsync(Guid taskId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RemoveAsync(Guid taskId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> ClearFinishedAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task CancelAsync(Guid taskId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> CancelAllAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task WaitForIdleAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public string? GetTaskOutput(Guid taskId) => null;
    }
}