namespace EbookToolSuite.Core.Models;

/// <summary>快照类型（进 manifest.json 的 kind 字段与日志，只增不改）。</summary>
public static class WorkspaceSnapshotKinds
{
    /// <summary>打开工作区时的**全量**基线快照（会话期间一直保留，"还原到打开时"靠它）。</summary>
    public const string Initial = "initial";

    /// <summary>每次保存前的增量快照：只含"将要写回书里的那些文件"。</summary>
    public const string Save = "save";

    /// <summary>全部替换前的增量快照：只含本次替换涉及的章节（T5-3 依赖，1 个）。</summary>
    public const string ReplaceBefore = "replace-before";

    /// <summary>删除章节前的增量快照：只含被删的文件（1 个）。</summary>
    public const string DeleteBefore = "delete-before";

    /// <summary>还原前自动建的反向快照（"还原错了还能退回来"）。</summary>
    public const string Restore = "restore";

    /// <summary>用户手动打的标记。</summary>
    public const string Manual = "manual";
}

/// <summary>
/// 快照里的一个条目（文件级）。
/// <see cref="BackupFile"/> 为 null 表示"快照时这个文件还不存在"，还原时要把目标文件删掉。
/// </summary>
public sealed record WorkspaceSnapshotEntry(
    string RelativePath,
    string? BackupFile,
    long Size,
    string? Sha256);

/// <summary>
/// 一个文件级快照（T5-4）。清单序列化成
/// <c>{工作区}/.snapshots/{snapshotId}/manifest.json</c>，内容本体在同目录的 files/ 下。
/// <para>
/// 【一份快照自己就是完整可用的】全量基线（<see cref="WorkspaceSnapshotKinds.Initial"/>）
/// 存的是打开工作区时**所有**文件，还原它 = 整份拷回（"一切复原"）；其余快照存的是
/// **那一次操作之前**被涉及文件的内容，还原它 = 把这些文件覆盖回去（等于"撤销这一次操作"，
/// 例如"撤销上一次替换"）。所以每份快照都能独立还原，清理也能按类型各留几份。
/// </para>
/// </summary>
public sealed record WorkspaceSnapshot(
    string Id,
    string Kind,
    DateTimeOffset CreatedAt,
    IReadOnlyList<WorkspaceSnapshotEntry> Entries,
    string? Note = null,
    string? ParentId = null,
    long Order = 0)
{
    
    /// <summary>全量快照（打开工作区时的基线）。</summary>
    public bool IsFull => string.Equals(Kind, WorkspaceSnapshotKinds.Initial, StringComparison.Ordinal);

    public int EntryCount => Entries.Count;

    /// <summary>这份快照占的字节数（备份文件本体大小之和）。</summary>
    public long TotalBytes => Entries.Sum(entry => entry.Size);

    /// <summary>给"历史"下拉用的一句话描述（时间由调用方另加）。</summary>
    public string Describe() => Kind switch
    {
        WorkspaceSnapshotKinds.Initial => "打开时的全量快照",
        WorkspaceSnapshotKinds.Save => $"保存前：{EntryCount} 个文件",
        WorkspaceSnapshotKinds.ReplaceBefore => $"全部替换前：{EntryCount} 章",
        WorkspaceSnapshotKinds.DeleteBefore => $"删除章节前：{EntryCount} 个文件",
        WorkspaceSnapshotKinds.Restore => $"还原前自动快照：{EntryCount} 个文件",
        WorkspaceSnapshotKinds.Manual => $"手动标记：{EntryCount} 个文件",
        _ => $"{Kind}：{EntryCount} 个文件",
    };
}

/// <summary>快照还原结果。失败时 <see cref="Failures"/> 里有中文原因，<see cref="Ok"/> 为 false。</summary>
public sealed record WorkspaceRestoreResult(
    bool Ok,
    int RestoredFiles,
    IReadOnlyList<string> Failures,
    string? SafetySnapshotId,
    string Message);

/// <summary>快照清理报告。</summary>
public sealed record WorkspacePruneReport(
    IReadOnlyList<string> Removed,
    long FreedBytes,
    long TotalBytes);