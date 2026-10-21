using System.Collections.Concurrent;
using System.Diagnostics;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Library;

/// <summary>
/// 书库文件状态扫描（T2-6）：把 book_formats 里的记录与磁盘上的真实文件比一遍。
/// 三种状态见 <see cref="BookFileState"/>。
/// </summary>
/// <remarks>
/// 为什么不用 FileSystemWatcher 当主力（设计文档 7.1.3）：它在网络盘/大目录下会漏事件。
/// 这里的全量扫描才是唯一判定依据；watcher 只负责"提醒你该扫了"。
/// </remarks>
public sealed class LibraryScanService : ILibraryScanService
{
    /// <summary>
    /// 修改时间的容差。库里存的是 Unix 毫秒（截断过），FAT/exFAT 只有 2 秒精度，
    /// 不留容差会把刚导入的书误判成"被外部修改"。
    /// </summary>
    public static readonly TimeSpan ModifiedTolerance = TimeSpan.FromSeconds(2);

    /// <summary>并发探测文件数。1000 本要跑进 3 秒，IO 是瓶颈。</summary>
    private const int MaxParallelism = 8;

    private readonly IBookRepository _repo;
    private readonly IHashUtil _hasher;
    private readonly IClock _clock;
    private readonly ILogger<LibraryScanService> _log;

    /// <summary>同一时刻只跑一次扫描，并发调用排队。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyDictionary<Guid, BookFileState> _bookStates =
        new Dictionary<Guid, BookFileState>();

    public LibraryScanService(
        IBookRepository repo, IHashUtil hasher, IClock clock, ILogger<LibraryScanService> log)
    {
        _repo = repo;
        _hasher = hasher;
        _clock = clock;
        _log = log;
    }

    public LibraryScanReport? LastReport { get; private set; }

    public event Action<LibraryScanReport>? ScanCompleted;

    public BookFileState GetBookState(Guid bookId)
        => _bookStates.TryGetValue(bookId, out var state) ? state : BookFileState.Normal;

    public async Task<LibraryScanReport> ScanAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var startedAt = _clock.Now;
            var stopwatch = Stopwatch.StartNew();

            var rows = await _repo.ListFormatsForScanAsync(ct).ConfigureAwait(false);

            var problems = new ConcurrentBag<BookFileStatus>();
            var markMissing = new ConcurrentBag<Guid>();
            var clearMissing = new ConcurrentBag<Guid>();

            await Parallel.ForEachAsync(
                rows,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = ct },
                (row, _) =>
                {
                    var state = Inspect(row, out var actualSize, out var actualModified);

                    if (state == BookFileState.Normal)
                    {
                        // 文件回来了：之前标过缺失就要撤掉标记
                        if (row.Missing)
                        {
                            clearMissing.Add(row.FormatId);
                        }

                        return ValueTask.CompletedTask;
                    }

                    if (row.Missing)
                    {
                        // 记录说缺失、磁盘上有文件：先撤标记，再按外部修改报
                        clearMissing.Add(row.FormatId);
                    }
                    else if (state == BookFileState.Missing)
                    {
                        markMissing.Add(row.FormatId);
                    }

                    problems.Add(new BookFileStatus(
                        row.FormatId, row.BookId, row.Title, row.Format, row.FilePath, state,
                        row.FileSize, actualSize, row.FileModifiedAt, actualModified));

                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

            var changed = await _repo
                .UpdateMissingFlagsAsync(markMissing.ToList(), clearMissing.ToList(), ct)
                .ConfigureAwait(false);

            var ordered = problems
                .OrderBy(p => p.Title, StringComparer.CurrentCulture)
                .ThenBy(p => p.Format)
                .ToList();

            var report = new LibraryScanReport(
                startedAt,
                stopwatch.Elapsed,
                rows.Count,
                rows.Select(r => r.BookId).Distinct().Count(),
                ordered);

            _bookStates = report.BuildBookStates();
            LastReport = report;

            _log.LogInformation(
                "书库扫描完成：{Formats} 个文件 / {Books} 本书，缺失 {Missing} 个、外部修改 {Modified} 个，改了 {Changed} 条标记，耗时 {Ms} ms",
                report.ScannedFormats, report.ScannedBooks, report.MissingCount,
                report.ModifiedCount, changed, (long)report.Duration.TotalMilliseconds);

            ScanCompleted?.Invoke(report);

            return report;
        }
        finally
        {
            _gate.Release();
        }
    }
    /// <summary>
    /// 判定一个格式文件的状态。读不到（路径非法、权限不足、盘符没了）一律当缺失：
    /// 对用户来说"文件用不了"和"文件不在了"要做的事是一样的。
    /// </summary>
    private static BookFileState Inspect(
        FormatScanRow row, out long actualSize, out DateTimeOffset? actualModified)
    {
        actualSize = 0;
        actualModified = null;

        try
        {
            var info = new FileInfo(row.FilePath);

            if (!info.Exists)
            {
                return BookFileState.Missing;
            }

            actualSize = info.Length;
            actualModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

            if (actualSize != row.FileSize)
            {
                return BookFileState.ExternallyModified;
            }

            if (row.FileModifiedAt is { } expected &&
                (actualModified.Value - expected).Duration() > ModifiedTolerance)
            {
                return BookFileState.ExternallyModified;
            }

            return BookFileState.Normal;
        }
        catch (Exception)
        {
            return BookFileState.Missing;
        }
    }
    public async Task<bool> AcceptModificationAsync(
        Guid bookId, BookFormatKind format, CancellationToken ct = default)
    {
        var row = await FindFormatRowAsync(bookId, format, ct).ConfigureAwait(false);

        // 只有"被外部修改"才谈得上接受；本来就正常或已缺失的，直接拒绝（幂等）
        if (row is null || Inspect(row, out _, out _) != BookFileState.ExternallyModified)
        {
            return false;
        }

        return await RefreshStoredFileInfoAsync(row, "已接受外部改动", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 内部保存后同步基线（用户 2026-10-20 要求）：编辑器刚把文件写回磁盘，把库里记的体积、
    /// 修改时间与快速指纹更新成磁盘现状，并把状态重算回"正常"。
    /// 与 <see cref="AcceptModificationAsync"/> 共用同一段落库逻辑，只是**不要求当前是"被外部修改"**。
    /// </summary>
    public async Task<bool> RecordInternalModificationAsync(
        Guid bookId, BookFormatKind format, CancellationToken ct = default)
    {
        var row = await FindFormatRowAsync(bookId, format, ct).ConfigureAwait(false);

        if (row is null)
        {
            return false;
        }

        return await RefreshStoredFileInfoAsync(row, "已同步内部保存后的文件信息", ct).ConfigureAwait(false);
    }

    private async Task<FormatScanRow?> FindFormatRowAsync(
        Guid bookId, BookFormatKind format, CancellationToken ct)
    {
        var rows = await _repo.ListFormatsForScanAsync(ct).ConfigureAwait(false);

        return rows.FirstOrDefault(r => r.BookId == bookId && r.Format == format);
    }

    /// <summary>把库里记的体积 / 修改时间 / 快速指纹更新成磁盘现状，然后重扫一遍让状态立刻变准。</summary>
    private async Task<bool> RefreshStoredFileInfoAsync(
        FormatScanRow row, string logName, CancellationToken ct)
    {
        var info = new FileInfo(row.FilePath);

        if (!info.Exists)
        {
            return false;
        }

        string? fingerprint = null;

        try
        {
            fingerprint = await _hasher.QuickFingerprintAsync(row.FilePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 指纹算不出来不致命：体积与修改时间更新了，就不会再报"外部修改"
            _log.LogWarning(ex, "{Action}时算快速指纹失败 {File}", logName, row.FilePath);
        }

        var ok = await _repo.UpdateFormatMetadataAsync(
            row.BookId,
            row.Format,
            info.Length,
            fingerprint,
            null,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            ct).ConfigureAwait(false);

        if (!ok)
        {
            return false;
        }

        _log.LogInformation("{Action} {BookId} {Format} {Bytes} 字节", logName, row.BookId, row.Format, info.Length);

        // 顺手重扫一遍：让这本书（以及可能同时在别处被改过的书）的状态立刻变准。
        await ScanAsync(ct).ConfigureAwait(false);

        return true;
    }
}
