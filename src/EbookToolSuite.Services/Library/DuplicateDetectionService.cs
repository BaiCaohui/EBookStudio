using System.Collections.Concurrent;
using System.Diagnostics;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Library;

/// <summary>
/// 模糊去重：两级指纹。
/// 一级 = 归一化「书名 + 第一作者 + 格式」分组（纯内存，零 IO）；
/// 二级 = 只对候选组懒算 content_hash（全文件 SHA256）复核，算完写回 book_formats.full_hash。
/// 设计文档 7.1.2 / 7.1.5，执行计划 T2-2。
/// </summary>
public sealed class DuplicateDetectionService : IDuplicateDetectionService
{
    private readonly IDuplicateRepository _repo;
    private readonly IHashUtil _hash;
    private readonly ILogger<DuplicateDetectionService> _log;

    public DuplicateDetectionService(
        IDuplicateRepository repo,
        IHashUtil hash,
        ILogger<DuplicateDetectionService> log)
    {
        _repo = repo;
        _hash = hash;
        _log = log;
    }

    public Task<int> HideCopiesAsync(Guid keptBookId, IReadOnlyList<Guid> copyBookIds, CancellationToken ct = default)
        => _repo.HideCopiesAsync(keptBookId, copyBookIds, ct);

    public Task<int> RestoreCopiesAsync(IReadOnlyList<Guid> copyBookIds, CancellationToken ct = default)
        => _repo.RestoreCopiesAsync(copyBookIds, ct);

    public Task<IReadOnlyList<Guid>> ListHiddenBookIdsAsync(CancellationToken ct = default)
        => _repo.ListHiddenBookIdsAsync(ct);

    public Task IgnoreGroupAsync(IReadOnlyList<Guid> memberIds, CancellationToken ct = default)
        => _repo.IgnoreGroupAsync(GroupKey(memberIds), ct);


    public async Task<DuplicateScanReport> ScanAsync(
        DuplicateScanOptions? options,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken ct = default)
    {
        var opt = options ?? DuplicateScanOptions.Default;
        var sw = Stopwatch.StartNew();

        var rows = await _repo.ListCandidatesAsync(ct).ConfigureAwait(false);
        var ignored = new HashSet<string>(
            await _repo.ListIgnoredSignaturesAsync(ct).ConfigureAwait(false),
            StringComparer.Ordinal);

        // 一级：纯内存分组（书名 + 第一作者 + 格式）
        var buckets = new Dictionary<string, List<DuplicateCandidateRow>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = DuplicateSignature.For(row.Title, row.Authors, row.Format);
            if (!buckets.TryGetValue(key, out var list))
            {
                list = new List<DuplicateCandidateRow>();
                buckets[key] = list;
            }

            list.Add(row);
        }

        progress?.Report(new DuplicateScanProgress("分组", 1, 1));

        var candidates = buckets
            .Where(p => p.Value.Count > 1 && p.Value.Count <= opt.MaxGroupSize)
            .Where(p => !ignored.Contains(GroupKey(p.Value.Select(r => r.BookId))))
            .OrderByDescending(p => p.Value.Count)
            .ToList();

        var bookCount = rows.Select(r => r.BookId).Distinct().Count();
        if (candidates.Count == 0)
        {
            return new DuplicateScanReport(Array.Empty<DuplicateGroup>(), bookCount, 0, sw.Elapsed);
        }


        var groups = new List<DuplicateGroup>(candidates.Count);
        var hashed = 0;
        var hashWatch = Stopwatch.StartNew();
        using var gate = new SemaphoreSlim(Math.Max(1, opt.MaxParallelism));
        var done = 0;

        foreach (var pair in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // 组内按文件大小降序：大的通常更完整，方便人挑「保留哪本」
            var members = pair.Value.OrderByDescending(m => m.FileSize).ToList();
            var hashById = new ConcurrentDictionary<Guid, string?>();
            foreach (var m in members)
            {
                if (!string.IsNullOrEmpty(m.ContentHash))
                {
                    hashById[m.FormatId] = m.ContentHash;
                }
            }

            // 二级：只算没算过的（懒计算），并受总预算约束
            var need = members.Where(m => !hashById.ContainsKey(m.FormatId)).ToList();
            if (opt.ComputeContentHashes && need.Count > 0 && hashWatch.Elapsed <= opt.HashBudget)
            {
                var tasks = need.Select(async m =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var h = await _hash.ComputeContentHashAsync(m.FilePath, ct).ConfigureAwait(false);
                        hashById[m.FormatId] = h;
                        Interlocked.Increment(ref hashed);
                        await _repo.SaveContentHashAsync(m.FormatId, h, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogWarning(ex, "算 content_hash 失败，这一组不做内容复核：{File}", m.FilePath);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }).ToList();

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            var built = members.Select(m => new DuplicateMember(
                m.BookId,
                m.Title,
                m.Authors,
                m.Format,
                m.FilePath,
                m.FileSize,
                m.FileModifiedAt,
                hashById.TryGetValue(m.FormatId, out var h) ? h : null,
                m.Rating,
                m.AddedAt)).ToList();

            var allEqual = built.All(m => !string.IsNullOrEmpty(m.ContentHash))
                && built.Select(m => m.ContentHash).Distinct(StringComparer.Ordinal).Count() == 1;

            groups.Add(new DuplicateGroup(pair.Key, built, allEqual, Suggest(built, allEqual)));

            done++;
            progress?.Report(new DuplicateScanProgress("复核", done, candidates.Count));
        }

        return new DuplicateScanReport(groups, bookCount, hashed, sw.Elapsed);
    }


    private static string Suggest(IReadOnlyList<DuplicateMember> members, bool allEqual)
    {
        if (allEqual)
        {
            return "内容完全相同（content_hash 一致），可以放心合并。";
        }

        var unhashed = members.Count(m => string.IsNullOrEmpty(m.ContentHash));
        if (unhashed > 0)
        {
            return "书名与作者相同，但有 " + unhashed + " 本还没算全文件哈希，无法确证是不是同一份内容。";
        }

        var sameSize = members.Select(m => m.FileSize).Distinct().Count() == 1;
        return sameSize
            ? "文件大小完全相同但内容不同，多半是同一本书的不同版本。"
            : "书名作者相同、内容不同，多半是同一本书的不同版本。";
    }

    /// <summary>忽略组的键：组内书籍 id 排序后逗号连接（成员变了就是新组，见 0002 迁移脚本注释）。</summary>
    private static string GroupKey(IEnumerable<Guid> bookIds)
        => string.Join(",", bookIds.Select(id => id.ToString()).Distinct().OrderBy(s => s, StringComparer.Ordinal));
}

