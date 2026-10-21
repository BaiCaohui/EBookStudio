using EbookToolSuite.Core;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.IO;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Reader;

/// <summary>
/// MOBI / AZW3 -> EPUB 的惰性缓存（执行计划 T6-1）。
///
/// 三个要点：
///   1. 转换走 IConversionQueue，请求带 Priority=1000（判据 5：点开一本书要插队），
///      且 RegisterFormat=false（产物是缓存，不是书库新增的格式）。
///   2. 缓存产物落在 ReaderCacheDir\{cacheKey}.source.epub；cacheKey 含源文件
///      mtime+size，所以源文件被替换后缓存自动失效（判据 3）。
///   3. 清理按 reader.cacheLimitMb 上限淘汰最久没用的，跳过正在读的（判据 6）。
/// </summary>
public sealed class EpubCacheService : IEpubCacheService
{
    /// <summary>缓存上限配置键（MB）。没有这个键时用 DefaultCacheLimitMb。</summary>
    public const string CacheLimitKey = "reader.cacheLimitMb";

    /// <summary>默认 2 GB。0 或负数 = 不限制。</summary>
    public const double DefaultCacheLimitMb = 2048d;

    /// <summary>打开时触发的转换优先级，远高于批量转换（默认 0）。</summary>
    public const int OpenPriority = 1000;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly IAppPaths _paths;
    private readonly IConversionQueue _queue;
    private readonly IConfigStore _config;
    private readonly ILogger<EpubCacheService>? _log;

    private readonly object _leaseGate = new();
    private readonly Dictionary<Guid, HashSet<string>> _leasedByBook = new();
    private readonly Dictionary<string, int> _leases = new(StringComparer.OrdinalIgnoreCase);

    public EpubCacheService(
        IAppPaths paths,
        IConversionQueue queue,
        IConfigStore config,
        ILogger<EpubCacheService>? log = null)
    {
        _paths = paths;
        _queue = queue;
        _config = config;
        _log = log;
    }

    public string GetArtifactPath(string cacheKey)
        => Path.Combine(_paths.ReaderCacheDir, cacheKey + ".source.epub");

    public long? GetConvertedArtifactSize(BookFormat format)
    {
        // 快速指纹是导入时算好的；没有它就算不出缓存键，只能当"没转换过"。
        if (format is null || string.IsNullOrWhiteSpace(format.QuickFingerprint))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(format.FilePath);
            if (!info.Exists)
            {
                return null;
            }

            var key = ReaderEndpoints.ConvertedCacheKey(
                format.QuickFingerprint, info.LastWriteTimeUtc, info.Length);
            var artifact = GetArtifactPath(ReaderEndpoints.CacheFolderName(key));

            if (!File.Exists(artifact))
            {
                return null;
            }

            return new FileInfo(artifact).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 判断不了就当没有：界面少显示一行，绝不能让详情页打不开
            _log?.LogWarning(ex, "判断 MOBI/AZW3 转换产物是否存在失败：{Path}", format.FilePath);
            return null;
        }
    }

    private string ConversionDir(string cacheKey)
        => Path.Combine(_paths.ReaderCacheDir, ".conv", cacheKey);

    public async Task<string> GetOrConvertAsync(
        Guid bookId,
        BookFormatKind sourceFormat,
        string sourcePath,
        string cacheKey,
        CancellationToken ct = default)
    {
        var artifact = GetArtifactPath(cacheKey);
        if (File.Exists(artifact))
        {
            return artifact;
        }

        Directory.CreateDirectory(_paths.ReaderCacheDir);

        // 队列里可能已经有一条同源同目标的活任务（比如用户手动把这本书转 EPUB）。
        // 有就搭车，既避免重复入队被拒，也避免同一个源转两遍。
        var task = FindReusable(bookId, sourcePath);
        if (task is null)
        {
            try
            {
                task = await _queue.EnqueueAsync(new ConversionRequest
                {
                    BookId = bookId,
                    TargetFormat = BookFormatKind.Epub,
                    SourceFormat = sourceFormat,
                    OutputDirectory = ConversionDir(cacheKey),
                    RegisterFormat = false,
                    Priority = OpenPriority,
                }, ct).ConfigureAwait(false);
            }
            catch (AppException ex) when (ex.Code == ErrorCode.ConversionOutputExists)
            {
                // 另一个阅读窗口刚好抢先入队了：改成等它。
                task = FindReusable(bookId, sourcePath)
                    ?? throw new AppException(
                        ErrorCode.ReaderOpenFailed,
                        "这本书的 EPUB 转换正在队列里，但已经找不到了。请重试一次。",
                        technical: $"duplicate enqueue then not found: book={bookId}",
                        inner: ex);
            }
        }

        await WaitForTerminalAsync(task, ct).ConfigureAwait(false);

        if (task.Status != ConversionTaskStatus.Completed || !File.Exists(task.OutputPath))
        {
            throw new AppException(
                ErrorCode.ReaderOpenFailed,
                "该文件无法转换，请用外部阅读器打开。",
                technical: $"epub cache conversion failed: task={task.Id} "
                    + $"status={task.Status} error={task.ErrorMessage}",
                context: new Dictionary<string, object?> { ["sourcePath"] = sourcePath });
        }

        // 复制而不是移动：搭车的那条任务产物在书库目录里，绝不能搬走。
        Directory.CreateDirectory(_paths.ReaderCacheDir);

        // T7-2 判据 4：缓存产物也要原子落盘——半截 .epub 被 File.Exists 当成可用缓存更糟。
        AtomicFile.Copy(task.OutputPath, artifact);

        var convDir = ConversionDir(cacheKey);
        if (IsUnder(task.OutputPath, convDir))
        {
            TryDeleteDirectory(convDir);
        }

        _log?.LogInformation(
            "MOBI/AZW3 转 EPUB 缓存完成：{Source} -> {Artifact}", sourcePath, artifact);
        return artifact;
    }

    private ConversionTask? FindReusable(Guid bookId, string sourcePath)
        => _queue.Tasks.FirstOrDefault(t =>
            !t.IsTerminal
            && t.BookId == bookId
            && t.OutFormat == BookFormatKind.Epub
            && string.Equals(t.InputPath, sourcePath, StringComparison.OrdinalIgnoreCase));

    private async Task WaitForTerminalAsync(ConversionTask task, CancellationToken ct)
    {
        var timeoutMinutes = _config.GetDouble("conversion.timeoutMinutes", 60d);
        if (timeoutMinutes <= 0)
        {
            timeoutMinutes = 60d;
        }

        // 用 TickCount 而不是注入时钟：这个等待只看真实时间，测试里也不需要拨表。
        var deadline = Environment.TickCount64
            + (long)TimeSpan.FromMinutes(timeoutMinutes + 5d).TotalMilliseconds;

        while (!task.IsTerminal)
        {
            ct.ThrowIfCancellationRequested();

            if (Environment.TickCount64 > deadline)
            {
                throw new AppException(
                    ErrorCode.ReaderOpenFailed,
                    "这本书转换超时了，请稍后再试，或先用外部阅读器打开。",
                    technical: $"wait epub conversion timeout: task={task.Id}");
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    // ---------- 正在读的租约（判据 6） ----------

    public void BeginRead(Guid bookId, string cacheRoot)
    {
        lock (_leaseGate)
        {
            if (!_leasedByBook.TryGetValue(bookId, out var roots))
            {
                roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _leasedByBook[bookId] = roots;
            }

            if (roots.Add(cacheRoot))
            {
                _leases[cacheRoot] = _leases.TryGetValue(cacheRoot, out var n) ? n + 1 : 1;
            }
        }
    }

    public void EndRead(Guid bookId)
    {
        lock (_leaseGate)
        {
            if (!_leasedByBook.Remove(bookId, out var roots))
            {
                return;
            }

            foreach (var root in roots)
            {
                if (_leases.TryGetValue(root, out var n))
                {
                    if (n <= 1)
                    {
                        _leases.Remove(root);
                    }
                    else
                    {
                        _leases[root] = n - 1;
                    }
                }
            }
        }
    }

    private bool IsLeased(string cacheRoot)
    {
        lock (_leaseGate)
        {
            return _leases.TryGetValue(cacheRoot, out var n) && n > 0;
        }
    }

    // ---------- 缓存上限（判据 6） ----------

    public Task<long> EnforceLimitAsync(CancellationToken ct = default)
    {
        var limitMb = _config.GetDouble(CacheLimitKey, DefaultCacheLimitMb);
        if (limitMb <= 0)
        {
            return Task.FromResult(0L);   // 0/负数 = 不限制
        }

        var root = _paths.ReaderCacheDir;
        if (!Directory.Exists(root))
        {
            return Task.FromResult(0L);
        }

        var limit = (long)(limitMb * 1024d * 1024d);
        var entries = new List<CacheEntry>();

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileName(dir);
            if (string.Equals(name, ".conv", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var artifact = GetArtifactPath(name);
            var bytes = DirectoryBytes(dir);
            var lastUsed = DirectoryLastWriteUtc(dir);

            if (File.Exists(artifact))
            {
                bytes += new FileInfo(artifact).Length;
                lastUsed = Later(lastUsed, File.GetLastWriteTimeUtc(artifact));
            }

            entries.Add(new CacheEntry(name, dir, artifact, bytes, lastUsed));
        }

        var total = entries.Sum(e => e.Bytes);
        if (total <= limit)
        {
            return Task.FromResult(0L);
        }

        long freed = 0;
        foreach (var entry in entries.OrderBy(e => e.LastUsedUtc))
        {
            if (total - freed <= limit)
            {
                break;
            }

            if (IsLeased(entry.CacheRoot))
            {
                _log?.LogInformation("阅读缓存清理：跳过正在读的 {Root}", entry.CacheRoot);
                continue;
            }

            try
            {
                Directory.Delete(entry.CacheRoot, recursive: true);
                if (File.Exists(entry.ArtifactPath))
                {
                    File.Delete(entry.ArtifactPath);
                }

                freed += entry.Bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.LogWarning(ex, "阅读缓存清理失败：{Root}", entry.CacheRoot);
            }
        }

        if (freed > 0)
        {
            _log?.LogInformation(
                "阅读缓存清理：释放 {Mb:F1} MB（上限 {Limit} MB）", freed / 1048576d, limitMb);
        }

        return Task.FromResult(freed);
    }

    private static long DirectoryBytes(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static DateTime DirectoryLastWriteUtc(string dir)
    {
        var manifest = Path.Combine(dir, "reader.json");
        if (File.Exists(manifest))
        {
            return File.GetLastWriteTimeUtc(manifest);
        }

        return Directory.GetLastWriteTimeUtc(dir);
    }

    private static DateTime Later(DateTime a, DateTime b) => a >= b ? a : b;

    private static bool IsUnder(string path, string root)
        => path.StartsWith(
            root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响主流程
        }
    }

    private sealed record CacheEntry(
        string Name, string CacheRoot, string ArtifactPath, long Bytes, DateTime LastUsedUtc);
}