using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.IO;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Editing;

/// <summary>
/// 还原点（备份与还原，执行计划 T3-6）。
///
/// 【目录与权威来源】每个还原点一个目录 <c>Data/Backup/RestorePoints/{pointId}/</c>，
/// 里面是 manifest.json（清单）+ 被备份的文件本体。**清单文件是唯一权威**，
/// 数据库里没有还原点表还原点数量很少（受 1 GB 总上限与单本 5 份约束），
/// 扫盘比维护一张可能与磁盘不一致的索引表安全。
///
/// 【为什么还原前先建反向还原点】计划原文：否则还原错了就没退路。
/// 【为什么备份文件用复制而不是 File.Replace】计划示例写的是
/// <c>File.Replace(backup, original)</c>，但那样会把备份文件**消耗掉**，
/// 还原点就成了一次性的、也再没法重放。这里改为"把备份复制到目标同目录的临时文件，
/// 再 File.Replace(临时文件 -> 原文件)"：同样是原子替换，还原点还能反复用。
/// </summary>
public sealed class RestorePointService : IRestorePointService
{
    /// <summary>清单文件名（每个还原点目录里一个）。</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>还原时的临时文件名后缀（与目标同目录，保证同卷可原子替换）。</summary>
    public const string RestoreTempSuffix = ".ets-restore.tmp";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IAppPaths _paths;
    private readonly IOperationLogRepository? _operations;
    private readonly ILogger<RestorePointService>? _log;

    public RestorePointService(
        IAppPaths paths,
        IOperationLogRepository? operations = null,
        ILogger<RestorePointService>? log = null)
    {
        _paths = paths;
        _operations = operations;
        _log = log;
    }

    // ---------- 创建 ----------

    public async Task<RestorePoint> CreateAsync(
        string type,
        IReadOnlyList<RestorePointRequest> items,
        string? note = null,
        CancellationToken ct = default)
    {
        if (items is null || items.Count == 0)
        {
            throw new AppException(ErrorCode.InvalidPath, "没有要备份的文件，还原点未创建。");
        }

        _paths.EnsureCreated();

        var id = NewPointId();
        var directory = PointDirectory(id);
        Directory.CreateDirectory(directory);

        try
        {
            var entries = new List<RestorePointItem>();
            foreach (var request in items)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(request.SourcePath) || !File.Exists(request.SourcePath))
                {
                    throw new AppException(
                        ErrorCode.FileNotFound,
                        "要备份的文件不在磁盘上，保存已中止（原文件没有被改动）。",
                        request.SourcePath);
                }

                var backupFile = UniqueBackupName(directory, Path.GetFileName(request.SourcePath));
                var destination = Path.Combine(directory, backupFile);

                // T7-2 判据 4：还原点是被用来"救命"的，半截备份比没有备份更危险。
                AtomicFile.Copy(request.SourcePath, destination);

                entries.Add(new RestorePointItem(
                    request.BookId,
                    request.FormatId,
                    request.SourcePath,
                    backupFile,
                    new FileInfo(destination).Length,
                    await HashAsync(destination, ct).ConfigureAwait(false)));
            }

            var point = new RestorePoint(id, type, DateTimeOffset.Now, entries, note);
            await WriteManifestAsync(directory, point, ct).ConfigureAwait(false);

            _log?.LogInformation(
                "还原点已创建 {Id}（{Type}，{Count} 个文件，{Bytes} 字节）",
                id,
                type,
                point.ItemCount,
                point.TotalBytes);

            return point;
        }
        catch (Exception)
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    // ---------- 查询 ----------

    public Task<IReadOnlyList<RestorePoint>> ListAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<RestorePoint>>(
            () =>
            {
                var root = _paths.RestorePointsDir;
                if (!Directory.Exists(root))
                {
                    return Array.Empty<RestorePoint>();
                }

                var points = new List<RestorePoint>();
                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    ct.ThrowIfCancellationRequested();
                    var point = ReadManifest(directory);
                    if (point is not null)
                    {
                        points.Add(point);
                    }
                }

                // 同一毫秒内连续创建会有 CreatedAt 相同的可能，用 Id 兜底，
                // 否则"清理最旧的"会变成随机挑一个（List.Sort 不稳定）。
                points.Sort((left, right) => right.CreatedAt != left.CreatedAt
                    ? right.CreatedAt.CompareTo(left.CreatedAt)
                    : string.CompareOrdinal(right.Id, left.Id));
                return points;
            },
            ct);

    public Task<RestorePoint?> GetAsync(string pointId, CancellationToken ct = default)
        => Task.Run(
            () =>
            {
                var directory = PointDirectory(ValidateId(pointId));
                return Directory.Exists(directory) ? ReadManifest(directory) : null;
            },
            ct);

    public Task<long> TotalBytesAsync(CancellationToken ct = default)
        => Task.Run(
            () =>
            {
                var root = _paths.RestorePointsDir;
                if (!Directory.Exists(root))
                {
                    return 0L;
                }

                long total = 0;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    total += new FileInfo(file).Length;
                }

                return total;
            },
            ct);

    // ---------- 还原 ----------

    public async Task<RestoreResult> RestoreAsync(string pointId, CancellationToken ct = default)
    {
        var point = await GetAsync(pointId, ct).ConfigureAwait(false)
            ?? throw new AppException(ErrorCode.FileNotFound, "找不到这个还原点，可能已经被清理掉了。", pointId);

        var directory = PointDirectory(point.Id);

        // 1) 先把"现在"的状态备份成一个反向还原点（还原错了还能退回来）
        var current = new List<RestorePointRequest>();
        foreach (var item in point.Items)
        {
            if (File.Exists(item.OriginalPath))
            {
                current.Add(new RestorePointRequest(item.BookId, item.FormatId, item.OriginalPath));
            }
        }

        var safety = current.Count > 0
            ? await CreateAsync(
                RestorePointTypes.Restore,
                current,
                "还原 " + point.Id + " 之前的自动备份",
                ct).ConfigureAwait(false)
            : null;

        // 2) 逐个还原并核对 hash
        var failures = new List<string>();
        var restored = 0;
        foreach (var item in point.Items)
        {
            ct.ThrowIfCancellationRequested();
            var backup = Path.Combine(directory, item.BackupFile);

            if (!File.Exists(backup))
            {
                failures.Add(item.OriginalPath + "：备份文件已不在磁盘上");
                continue;
            }

            try
            {
                var targetDirectory = Path.GetDirectoryName(item.OriginalPath);
                if (!string.IsNullOrEmpty(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                var temp = item.OriginalPath + RestoreTempSuffix;
                File.Copy(backup, temp, overwrite: true);

                if (File.Exists(item.OriginalPath))
                {
                    File.Replace(temp, item.OriginalPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, item.OriginalPath, overwrite: true);
                }

                var hash = await HashAsync(item.OriginalPath, ct).ConfigureAwait(false);
                if (!string.Equals(hash, item.OriginalHash, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(item.OriginalPath + "：还原后的内容与备份不一致（hash 对不上）");
                }
                else
                {
                    restored++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(item.OriginalPath + "：" + ex.SafeMessage());
            }
        }

        var summary = failures.Count == 0
            ? "从还原点 " + point.Id + " 还原了 " + restored + " 本书"
            : "从还原点 " + point.Id + " 还原了 " + restored + " 本，另有 " + failures.Count + " 个失败";

        await LogAsync(
            OperationNames.Restore,
            summary,
            bookId: null,
            formatId: null,
            restorePointId: point.Id,
            detail: JsonSerializer.Serialize(
                new
                {
                    restored,
                    safetyPoint = safety?.Id,
                    failures,
                },
                JsonOptions),
            ct).ConfigureAwait(false);

        return new RestoreResult(point.Id, safety?.Id ?? string.Empty, restored, failures);
    }

    // ---------- 按上限清理 ----------

    public async Task<BackupPruneReport> EnforceLimitsAsync(
        long maxTotalBytes = IRestorePointService.DefaultMaxTotalBytes,
        int maxPerBook = IRestorePointService.DefaultMaxPerBook,
        CancellationToken ct = default)
    {
        var points = await ListAsync(ct).ConfigureAwait(false);
        if (points.Count == 0)
        {
            return new BackupPruneReport(Array.Empty<string>(), 0, 0);
        }

        var doomed = new HashSet<string>(StringComparer.Ordinal);

        // 1) 单本上限：只对"保存单本书"的还原点生效（批量还原点一次含多本，不适用）；
        //    每本书按时间从新到旧保留 maxPerBook 份，超出的标记删除。
        var perBookRemoved = 0;
        var byBook = new Dictionary<Guid, List<RestorePoint>>();
        foreach (var point in points)
        {
            if (point.ItemCount != 1 || point.Type != RestorePointTypes.Save)
            {
                continue;
            }

            var bookId = point.Items[0].BookId;
            if (!byBook.TryGetValue(bookId, out var list))
            {
                list = new List<RestorePoint>();
                byBook[bookId] = list;
            }

            list.Add(point);
        }

        foreach (var list in byBook.Values)
        {
            list.Sort((left, right) => right.CreatedAt != left.CreatedAt
                ? right.CreatedAt.CompareTo(left.CreatedAt)
                : string.CompareOrdinal(right.Id, left.Id));
            for (var i = maxPerBook; i < list.Count; i++)
            {
                if (doomed.Add(list[i].Id))
                {
                    perBookRemoved++;
                }
            }
        }

        // 2) 总量上限：以"将要保留的那些"为基数，从最旧的开始继续删，直到不超上限
        long kept = 0;
        foreach (var point in points)
        {
            if (!doomed.Contains(point.Id))
            {
                kept += DirectoryBytes(PointDirectory(point.Id));
            }
        }

        foreach (var point in points.OrderBy(static p => p.CreatedAt))
        {
            if (kept <= maxTotalBytes)
            {
                break;
            }

            if (!doomed.Add(point.Id))
            {
                continue;
            }

            kept -= DirectoryBytes(PointDirectory(point.Id));
        }

        // 3) 真删（删不掉的从报告里剔除，别谎报）
        var removed = new List<string>();
        long freed = 0;
        foreach (var id in doomed)
        {
            var directory = PointDirectory(id);
            var size = DirectoryBytes(directory);
            TryDeleteDirectory(directory);

            if (!Directory.Exists(directory))
            {
                removed.Add(id);
                freed += size;
            }
        }

        removed.Sort(StringComparer.Ordinal);
        if (removed.Count == 0)
        {
            return new BackupPruneReport(Array.Empty<string>(), 0, 0);
        }

        await LogAsync(
            OperationNames.Prune,
            "备份超过上限，清理了 " + removed.Count + " 个最旧的还原点（释放 " + freed + " 字节）",
            bookId: null,
            formatId: null,
            restorePointId: null,
            detail: JsonSerializer.Serialize(
                new { removed, freed, perBookRemoved, maxTotalBytes, maxPerBook },
                JsonOptions),
            ct).ConfigureAwait(false);

        return new BackupPruneReport(removed, freed, perBookRemoved);
    }


    // ---------- 内部 ----------

    /// <summary>还原点目录（对外的唯一路径构造处）。</summary>
    public string PointDirectory(string pointId)
        => Path.Combine(_paths.RestorePointsDir, ValidateId(pointId));

    /// <summary>id 只用字母数字与短横线、下划线，长度 1-64；挡住路径穿越。</summary>
    public static string ValidateId(string pointId)
    {
        if (string.IsNullOrWhiteSpace(pointId) || pointId.Length > 64)
        {
            throw new AppException(ErrorCode.InvalidPath, "还原点编号不合法。", pointId);
        }

        foreach (var c in pointId)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                throw new AppException(ErrorCode.InvalidPath, "还原点编号不合法。", pointId);
            }
        }

        return pointId;
    }

    private static string NewPointId()
    {
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
        var suffix = Guid.NewGuid().ToString("N")[..4];
        return stamp + "_" + suffix;
    }

    private static string UniqueBackupName(string directory, string? fileName)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "backup.bin" : fileName;
        if (!File.Exists(Path.Combine(directory, name)))
        {
            return name;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = stem + "-" + i + extension;
            if (!File.Exists(Path.Combine(directory, candidate)))
            {
                return candidate;
            }
        }

        return Guid.NewGuid().ToString("N") + extension;
    }

    /// <summary>文件内容 hash，形如 sha256:&lt;hex&gt;（与 manifest 里的 originalHash 同口径）。</summary>
    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task WriteManifestAsync(string directory, RestorePoint point, CancellationToken ct)
    {
        var dto = new ManifestDto
        {
            RestorePointId = point.Id,
            Type = point.Type,
            CreatedAt = point.CreatedAt,
            Note = point.Note,
            Items = point.Items.Select(static item => new ManifestItemDto
            {
                BookId = item.BookId.ToString(),
                FormatId = item.FormatId.ToString(),
                OriginalPath = item.OriginalPath,
                BackupFile = item.BackupFile,
                OriginalSize = item.OriginalSize,
                OriginalHash = item.OriginalHash,
            }).ToList(),
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        await AtomicFile
            .WriteAllTextAsync(
                Path.Combine(directory, ManifestFileName),
                json,
                new UTF8Encoding(false),
                ct)
            .ConfigureAwait(false);
    }

    private RestorePoint? ReadManifest(string directory)
    {
        var path = Path.Combine(directory, ManifestFileName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var dto = JsonSerializer.Deserialize<ManifestDto>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
            if (dto is null || string.IsNullOrWhiteSpace(dto.RestorePointId))
            {
                return null;
            }

            var items = new List<RestorePointItem>();
            foreach (var item in dto.Items ?? new List<ManifestItemDto>())
            {
                items.Add(new RestorePointItem(
                    Guid.TryParse(item.BookId, out var bookId) ? bookId : Guid.Empty,
                    Guid.TryParse(item.FormatId, out var formatId) ? formatId : Guid.Empty,
                    item.OriginalPath ?? string.Empty,
                    item.BackupFile ?? string.Empty,
                    item.OriginalSize,
                    item.OriginalHash ?? string.Empty));
            }

            return new RestorePoint(
                dto.RestorePointId!,
                dto.Type ?? RestorePointTypes.Save,
                dto.CreatedAt,
                items,
                dto.Note);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _log?.LogWarning(ex, "还原点清单读不出来，跳过 {Directory}", directory);
            return null;
        }
    }

    private static long DirectoryBytes(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // 文件正好被删了就算了
            }
        }

        return total;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception)
        {
            // 删不掉不影响上层结论（下次按上限清理还会再试）
        }
    }

    /// <summary>写日志失败绝不能影响保存/还原本身。</summary>
    private async Task LogAsync(
        string operation,
        string summary,
        Guid? bookId,
        Guid? formatId,
        string? restorePointId,
        string? detail,
        CancellationToken ct)
    {
        if (_operations is null)
        {
            return;
        }

        try
        {
            await _operations
                .AddAsync(
                    new OperationLogEntry(operation, summary, bookId, formatId, restorePointId, detail),
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "写 operation_log 失败：{Operation}", operation);
        }
    }

    // ---------- manifest.json 的 DTO（字段名就是设计文档里的名字） ----------

    private sealed class ManifestDto
    {
        public string? RestorePointId { get; set; }

        public string? Type { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public string? Note { get; set; }

        public List<ManifestItemDto>? Items { get; set; }
    }

    private sealed class ManifestItemDto
    {
        public string? BookId { get; set; }

        public string? FormatId { get; set; }

        public string? OriginalPath { get; set; }

        public string? BackupFile { get; set; }

        public long OriginalSize { get; set; }

        public string? OriginalHash { get; set; }
    }
}
