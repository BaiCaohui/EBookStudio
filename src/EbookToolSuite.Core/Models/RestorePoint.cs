namespace EbookToolSuite.Core.Models;

/// <summary>还原点里的一个文件（一本书的一个格式）。</summary>
public sealed record RestorePointItem(
    Guid BookId,
    Guid FormatId,
    string OriginalPath,
    string BackupFile,
    long OriginalSize,
    string OriginalHash);

/// <summary>
/// 一个还原点。权威来源是 Data/Backup/RestorePoints/{Id}/manifest.json，
/// 数据库里的 restore_points 只是索引（列表与统计用）。
/// </summary>
public sealed record RestorePoint(
    string Id,
    string Type,
    DateTimeOffset CreatedAt,
    IReadOnlyList<RestorePointItem> Items,
    string? Note = null)
{
    public int ItemCount => Items.Count;

    public long TotalBytes
    {
        get
        {
            long total = 0;
            foreach (var item in Items)
            {
                total += item.OriginalSize;
            }

            return total;
        }
    }
}

/// <summary>要备份哪个文件（保存前调用）。</summary>
public sealed record RestorePointRequest(Guid BookId, Guid FormatId, string SourcePath);

/// <summary>还原结果。SafetyPointId 是"还原前的还原点"还原错了还能退回来。</summary>
public sealed record RestoreResult(
    string RestorePointId,
    string SafetyRestorePointId,
    int RestoredCount,
    IReadOnlyList<string> Failures)
{
    public bool Success => Failures.Count == 0;
}

/// <summary>按上限清理的结果。Removed 为空表示什么都没删（不该打扰用户）。</summary>
public sealed record BackupPruneReport(
    IReadOnlyList<string> RemovedRestorePoints,
    long FreedBytes,
    int RemovedByPerBookLimit)
{
    public bool AnythingRemoved => RemovedRestorePoints.Count > 0;
}
