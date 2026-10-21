using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.IO;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Editing;

/// <summary>
/// 编辑会话的文件级快照（T5-4）。存储：<c>{WorkDir}/.snapshots/{snapshotId}/manifest.json</c>
/// + <c>files/</c>（只放这次点名要备份的文件）。清单是唯一权威，扫盘即可列出（与还原点同一套思路，
/// 不建数据库索引，避免磁盘与索引不一致）。
///
/// 【还原语义】见 <see cref="WorkspaceSnapshot"/> 的说明：全量基线整份拷回；增量目标用
/// "把比它新的快照按时间倒序覆盖回去"实现。之所以能这样，是因为每份增量存的是**操作之前**的内容。
/// 【清理】只从最旧端删（保留最新 10 份非基线；基线永远保留）：删中间一个会让比它新的快照回放错。
/// </summary>
public sealed class WorkspaceSnapshotService : IWorkspaceSnapshotService
{
    /// <summary>工作区里的快照目录名（打包器已把它排除，绝不会打进 EPUB）。</summary>
    public const string SnapshotsDirName = ".snapshots";

    /// <summary>每个快照目录里的清单文件名。</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>备份本体所在子目录名。</summary>
    public const string FilesDirName = "files";

    /// <summary>会话文件的文件名（它属于工作区元数据，不进快照也不进 EPUB）。</summary>
    public const string SessionFileName = ".session.json";

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

    /// <summary>进程内单调序号（见 <see cref="WorkspaceSnapshot.Order"/>）。</summary>
    private static long _sequence;

    private readonly ILogger<WorkspaceSnapshotService>? _log;

    public WorkspaceSnapshotService(ILogger<WorkspaceSnapshotService>? log = null) => _log = log;

    // ---------- 目录 ----------

    public string SnapshotDirectory(IEpubWorkspace workspace, string snapshotId)
        => Path.Combine(SnapshotRoot(workspace), ValidateId(snapshotId));

    private static string SnapshotRoot(IEpubWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(workspace.WorkDir))
        {
            throw new AppException(ErrorCode.InvalidPath, "工作区路径为空，没法使用快照。");
        }

        return Path.Combine(Path.GetFullPath(workspace.WorkDir), SnapshotsDirName);
    }

    // ---------- 创建 ----------

    public Task<WorkspaceSnapshot> TakeFullAsync(
        IEpubWorkspace workspace,
        string? note = null,
        CancellationToken ct = default)
        => Task.Run(
            () => TakeCore(
                workspace,
                ListWorkspaceFiles(workspace),
                WorkspaceSnapshotKinds.Initial,
                note ?? "打开工作区时的全量快照",
                ct),
            ct);

    public Task<WorkspaceSnapshot> TakeIncrementalAsync(
        IEpubWorkspace workspace,
        IReadOnlyList<string> entries,
        string kind = WorkspaceSnapshotKinds.Save,
        string? note = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return Task.Run(() => TakeCore(workspace, entries, kind, note, ct), ct);
    }

    private WorkspaceSnapshot TakeCore(
        IEpubWorkspace workspace,
        IEnumerable<string> relativePaths,
        string kind,
        string? note,
        CancellationToken ct,
        ISet<string>? protect = null)
    {
        var directory = Path.Combine(SnapshotRoot(workspace), NewSnapshotId(kind));
        Directory.CreateDirectory(directory);

        try
        {
            var entries = new List<WorkspaceSnapshotEntry>();

            foreach (var raw in Normalize(relativePaths))
            {
                ct.ThrowIfCancellationRequested();

                var relative = NormalizeRelative(raw);
                if (relative.Length == 0 || IsWorkspaceInternal(relative))
                {
                    continue;
                }

                var absolute = ResolveInside(workspace.WorkDir, relative);

                if (!File.Exists(absolute))
                {
                    /* 此刻不存在：记下来，还原时把目标删掉（例如"替换时新加的文件"） */
                    entries.Add(new WorkspaceSnapshotEntry(relative, null, 0, null));
                    continue;
                }

                var backupRelative = FilesDirName + "/" + relative;
                var backupPath = Path.Combine(
                    directory, backupRelative.Replace('/', Path.DirectorySeparatorChar));
                var backupDirectory = Path.GetDirectoryName(backupPath);
                if (!string.IsNullOrEmpty(backupDirectory))
                {
                    Directory.CreateDirectory(backupDirectory);
                }

                // T7-2 判据 4：快照备份原子写，避免还原时拿到半截内容。
                AtomicFile.Copy(absolute, backupPath);
                entries.Add(new WorkspaceSnapshotEntry(
                    relative,
                    backupRelative,
                    new FileInfo(backupPath).Length,
                    HashFile(backupPath)));
            }

            var snapshot = new WorkspaceSnapshot(
                Path.GetFileName(directory),
                kind,
                DateTimeOffset.Now,
                entries,
                note,
                ParentId: null,
                Order: Interlocked.Increment(ref _sequence));
            WriteManifest(directory, snapshot);

            // 建完就按上限收拾一次：只从最旧端删，新快照一定不会被删掉。
            if (!snapshot.IsFull)
            {
                PruneCore(workspace, IWorkspaceSnapshotService.DefaultMaxSnapshots, protect);
            }

            _log?.LogInformation(
                "快照已创建 {Id}（{Kind}，{Count} 个文件，{Bytes} 字节）",
                snapshot.Id, kind, snapshot.EntryCount, snapshot.TotalBytes);
            return snapshot;
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    // ---------- 查询 ----------

    public Task<IReadOnlyList<WorkspaceSnapshot>> ListAsync(
        IEpubWorkspace workspace,
        CancellationToken ct = default)
        => Task.Run(() => ListCore(workspace), ct);

    public Task<WorkspaceSnapshot?> GetAsync(
        IEpubWorkspace workspace,
        string snapshotId,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            var directory = SnapshotDirectory(workspace, snapshotId);
            return Directory.Exists(directory) ? ReadManifest(directory) : null;
        }, ct);

    public async Task<WorkspaceSnapshot?> FindLatestAsync(
        IEpubWorkspace workspace,
        string kind,
        CancellationToken ct = default)
    {
        var all = await ListAsync(workspace, ct).ConfigureAwait(false);
        return all.FirstOrDefault(snapshot => string.Equals(snapshot.Kind, kind, StringComparison.Ordinal));
    }

    private static IReadOnlyList<WorkspaceSnapshot> ListCore(IEpubWorkspace workspace)
    {
        var root = SnapshotRoot(workspace);
        if (!Directory.Exists(root))
        {
            return Array.Empty<WorkspaceSnapshot>();
        }

        var snapshots = new List<WorkspaceSnapshot>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var snapshot = ReadManifest(directory);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }

        // 新的在前；同一毫秒内连续创建用 Id 兜底（否则"删最旧"会变成随机挑一个）
        snapshots.Sort((left, right) => Compare(right, left));
        return snapshots;
    }

    // ---------- 还原 ----------

    public async Task<WorkspaceRestoreResult> RestoreAsync(
        IEpubWorkspace workspace,
        string snapshotId,
        CancellationToken ct = default)
    {
        var target = await GetAsync(workspace, snapshotId, ct).ConfigureAwait(false);
        if (target is null)
        {
            throw new AppException(ErrorCode.FileNotFound, "找不到这个快照，可能已经被清理掉了。", snapshotId);
        }

        var directory = Path.Combine(SnapshotRoot(workspace), target.Id);

        /* 1) 先把"这次还原要覆盖的那些文件"的当前内容存成反向快照（判据：还原前自动创建"还原前的快照"）。
              于是"还原错了/反悔了"还能再还原它退回来。 */
        WorkspaceSnapshot? safety = null;
        if (target.Entries.Count > 0)
        {
            try
            {
                /* 注意：建这份快照会顺带触发清理（restore 类型只留 1 份）。
                   必须把"当前正在还原的目标"保护起来，否则会把它自己删掉，
                   紧接着的覆盖就找不到备份文件了（判据 4 的反悔链路会直接断掉）。 */
                var protect = new HashSet<string>(StringComparer.Ordinal) { target.Id };
                var entries = target.Entries.Select(entry => entry.RelativePath).ToList();
                safety = await Task.Run(
                    () => TakeCore(
                        workspace,
                        entries,
                        WorkspaceSnapshotKinds.Restore,
                        "还原 " + target.Id + " 之前的自动快照",
                        ct,
                        protect),
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "还原前的自动快照没建成 {Id}", target.Id);
                return new WorkspaceRestoreResult(
                    false,
                    0,
                    Array.Empty<string>(),
                    null,
                    "还原前的自动快照没建成，已中止（工作区一个字节都没改）：" + ex.SafeMessage());
            }
        }

        /* 2) 把这个快照存的内容覆盖回去：
               全量快照 = 整份工作区（"一切复原"）；增量快照 = 那一次操作涉及的文件（等于"撤销这一次操作"）。 */
        var failures = new List<string>();
        var restored = 0;

        foreach (var entry in target.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (ApplyEntry(workspace, directory, entry, failures))
            {
                restored++;
            }
        }

        if (target.IsFull)
        {
            /* "一切复原"：基线里没有、现在却多出来的文件（编辑期间新建的）也要清掉。
               只碰工作区里的书内容，不碰 .snapshots / .session.json 这些我们自己的东西。 */
            var known = new HashSet<string>(
                target.Entries.Select(entry => entry.RelativePath), StringComparer.OrdinalIgnoreCase);

            foreach (var relative in ListWorkspaceFiles(workspace))
            {
                if (known.Contains(relative))
                {
                    continue;
                }

                try
                {
                    File.Delete(ResolveInside(workspace.WorkDir, relative));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AppException)
                {
                    failures.Add(relative + "：" + ex.SafeMessage());
                }
            }
        }

        var message = failures.Count == 0
            ? $"已还原快照 {target.Id}（{restored} 个文件）"
            : $"还原快照 {target.Id} 时有 {failures.Count} 个文件失败（成功 {restored} 个）";

        _log?.LogInformation("快照还原 {Id}：成功 {Restored}、失败 {Failed}", target.Id, restored, failures.Count);
        return new WorkspaceRestoreResult(failures.Count == 0, restored, failures, safety?.Id, message);
    }
    private static bool ApplyEntry(
        IEpubWorkspace workspace,
        string snapshotDirectory,
        WorkspaceSnapshotEntry entry,
        List<string> failures)
    {
        try
        {
            var target = ResolveInside(workspace.WorkDir, entry.RelativePath);

            if (entry.BackupFile is null)
            {
                // 快照时这个文件还不存在 -> 还原时把它删掉
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                return true;
            }

            var backup = Path.Combine(
                snapshotDirectory, entry.BackupFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(backup))
            {
                failures.Add(entry.RelativePath + "：快照里的备份文件不在了");
                return false;
            }

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = target + RestoreTempSuffix;
            File.Copy(backup, temp, overwrite: true);
            if (File.Exists(target))
            {
                File.Replace(temp, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, target, overwrite: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AppException)
        {
            failures.Add(entry.RelativePath + "：" + ex.SafeMessage());
            return false;
        }
    }

    // ---------- 清理 ----------

    public Task<WorkspacePruneReport> PruneAsync(
        IEpubWorkspace workspace,
        int maxSnapshots = IWorkspaceSnapshotService.DefaultMaxSnapshots,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return PruneCore(workspace, maxSnapshots);
        }, ct);

    private WorkspacePruneReport PruneCore(
        IEpubWorkspace workspace, int maxSnapshots, ISet<string>? protect = null)
    {
        var all = ListCore(workspace);
        var root = SnapshotRoot(workspace);

        /* 每类各留几份（计划 T5-4 的"保留"那一列）：
             initial        永远保留（"还原到打开时"靠它）
             save           最近 maxSnapshots（默认 10）份
             replace-before 1 份（"撤销上一次替换"用）
             delete-before  1 份
             restore        1 份（"还原前的快照"，留最新那份就够反悔）
             manual         5 份
           每个快照都是"自己完整可用"的（还原只覆盖它自己记的那些文件），
           所以可以按类型独立清理，不存在"删了中间一个就回放不出来"的问题。 */
        var doomed = new List<WorkspaceSnapshot>();
        foreach (var group in all.Where(snapshot => !snapshot.IsFull).GroupBy(snapshot => snapshot.Kind, StringComparer.Ordinal))
        {
            var keep = group.Key switch
            {
                WorkspaceSnapshotKinds.Save => Math.Max(1, maxSnapshots),
                WorkspaceSnapshotKinds.DeleteBefore => 1,
                WorkspaceSnapshotKinds.Restore => 1,
                WorkspaceSnapshotKinds.Manual => 5,
                _ => 1,
            };

            doomed.AddRange(group
                .Where(snapshot => protect is null || !protect.Contains(snapshot.Id))
                .OrderByDescending(snapshot => snapshot.CreatedAt)
                .ThenByDescending(snapshot => snapshot.Order)
                .Skip(keep));
        }

        var removed = new List<string>();
        long freed = 0;
        foreach (var snapshot in doomed)
        {
            var directory = Path.Combine(root, snapshot.Id);
            freed += snapshot.TotalBytes;
            TryDeleteDirectory(directory);
            if (!Directory.Exists(directory))
            {
                removed.Add(snapshot.Id);
            }
        }

        if (removed.Count > 0)
        {
            _log?.LogInformation("快照清理：删除 {Count} 个最旧的（释放 {Bytes} 字节）", removed.Count, freed);
        }

        return new WorkspacePruneReport(removed, freed, TotalBytes(workspace));
    }
    // ---------- 占用 ----------

    public long TotalBytes(IEpubWorkspace workspace)
    {
        var root = SnapshotRoot(workspace);
        if (!Directory.Exists(root))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            total += new FileInfo(file).Length;
        }

        return total;
    }

    public long IncrementalBytes(IEpubWorkspace workspace)
    {
        long total = 0;
        foreach (var snapshot in ListCore(workspace))
        {
            if (!snapshot.IsFull)
            {
                total += snapshot.TotalBytes;
            }
        }

        return total;
    }

    // ---------- 工具 ----------

    /// <summary>工作区里由我们自己管理、不属于书内容的路径（不进快照、不进 EPUB）。</summary>
    private static bool IsWorkspaceInternal(string relative)
    {
        if (relative.Length == 0)
        {
            return true;
        }

        if (string.Equals(relative.Split('/')[0], SnapshotsDirName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(relative, SessionFileName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return relative.EndsWith(".ets-tmp", StringComparison.OrdinalIgnoreCase)
            || relative.EndsWith(".ets-bak", StringComparison.OrdinalIgnoreCase)
            || relative.EndsWith(RestoreTempSuffix, StringComparison.OrdinalIgnoreCase)
            || relative.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ListWorkspaceFiles(IEpubWorkspace workspace)
    {
        var root = Path.GetFullPath(workspace.WorkDir);
        var files = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = NormalizeRelative(Path.GetRelativePath(root, path));
            if (relative.Length == 0 || IsWorkspaceInternal(relative))
            {
                continue;
            }

            files.Add(relative);
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static IEnumerable<string> Normalize(IEnumerable<string> relativePaths)
        => relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeRelative)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal);

    private static string NormalizeRelative(string relativePath)
        => relativePath.Replace('\\', '/').Trim().TrimStart('/');

    /// <summary>把包内相对路径钉在工作区里（防路径穿越，与 ChapterSourceEditor 同一口径）。</summary>
    private static string ResolveInside(string workDir, string relativePath)
    {
        var root = Path.GetFullPath(workDir);
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(ErrorCode.UnsafeArchiveEntry, "快照路径越出了工作区，已拒绝。", relativePath);
        }

        return full;
    }

    private static string ValidateId(string snapshotId)
    {
        if (string.IsNullOrWhiteSpace(snapshotId)
            || snapshotId.Contains("..", StringComparison.Ordinal)
            || snapshotId.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
        {
            throw new AppException(ErrorCode.InvalidPath, "快照编号不合法。", snapshotId);
        }

        return snapshotId;
    }

    private static string NewSnapshotId(string kind)
    {
        var prefix = string.Equals(kind, WorkspaceSnapshotKinds.Initial, StringComparison.Ordinal) ? "full" : "inc";
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();
        return prefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + random;
    }

    private static int Compare(WorkspaceSnapshot left, WorkspaceSnapshot right)
    {
        var byTime = left.CreatedAt.CompareTo(right.CreatedAt);
        if (byTime != 0)
        {
            return byTime;
        }

        var byOrder = left.Order.CompareTo(right.Order);
        return byOrder != 0 ? byOrder : string.CompareOrdinal(left.Id, right.Id);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void WriteManifest(string directory, WorkspaceSnapshot snapshot)
        => AtomicFile.WriteAllText(
            Path.Combine(directory, ManifestFileName),
            JsonSerializer.Serialize(snapshot, JsonOptions),
            new UTF8Encoding(false));

    private static WorkspaceSnapshot? ReadManifest(string directory)
    {
        var file = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WorkspaceSnapshot>(File.ReadAllText(file), JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
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
            // 删不掉只多占一点空间，不该把调用方的事情搞砸
        }
    }
}