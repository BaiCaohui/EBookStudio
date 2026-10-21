using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 还原点（备份与还原，T3-6）。目录约定：Data/Backup/RestorePoints/{pointId}/
/// + 同目录下的 manifest.json（权威清单）。
/// </summary>
public interface IRestorePointService
{
    /// <summary>默认总上限（1 GB）与单本书保留份数（5）。</summary>
    public const long DefaultMaxTotalBytes = 1024L * 1024 * 1024;

    public const int DefaultMaxPerBook = 5;

    /// <summary>把若干文件备份成一个还原点（保存流程的第 6 步）。</summary>
    Task<RestorePoint> CreateAsync(
        string type,
        IReadOnlyList<RestorePointRequest> items,
        string? note = null,
        CancellationToken ct = default);

    /// <summary>列出全部还原点，新的在前。</summary>
    Task<IReadOnlyList<RestorePoint>> ListAsync(CancellationToken ct = default);

    Task<RestorePoint?> GetAsync(string pointId, CancellationToken ct = default);

    /// <summary>
    /// 还原：**先给当前状态建一个反向还原点**，再逐个原子替换回备份，最后核对 hash。
    /// </summary>
    Task<RestoreResult> RestoreAsync(string pointId, CancellationToken ct = default);

    /// <summary>按上限清理最旧的（超上限时调用；结果要提示用户）。</summary>
    Task<BackupPruneReport> EnforceLimitsAsync(
        long maxTotalBytes = DefaultMaxTotalBytes,
        int maxPerBook = DefaultMaxPerBook,
        CancellationToken ct = default);

    /// <summary>当前备份总占用字节数。</summary>
    Task<long> TotalBytesAsync(CancellationToken ct = default);

    /// <summary>还原点目录（{RestorePointsDir}/{pointId}）。编号非法时抛 InvalidPath。</summary>
    string PointDirectory(string pointId);
}
