namespace EbookToolSuite.App.Services;

/// <summary>Data/Cache 的占用。</summary>
public sealed record CacheUsage(long TotalBytes, int FileCount);

/// <summary>一次清缓存的结果。被占用的文件不硬删，记进 Failures 让用户知道。</summary>
public sealed record CacheCleanResult(long FreedBytes, int DeletedFiles, IReadOnlyList<string> Failures);

/// <summary>[立即备份] 的结果：备份目录、总字节、文件数。</summary>
public sealed record ImmediateBackupResult(string Directory, long Bytes, int FileCount);

/// <summary>
/// 设置页「缓存 / 备份 / 高级」三组背后的动作（T7-4）。
/// 只做磁盘上的事，不碰 UI：清缓存只清 Data/Cache，立即备份只拷数据库与配置。
/// </summary>
public interface IAppMaintenanceService
{
    /// <summary>Data/Cache 的总占用与文件数。</summary>
    Task<CacheUsage> GetCacheUsageAsync(CancellationToken ct = default);

    /// <summary>清空 Data/Cache（封面缓存 / 阅读缓存 / Calibre 临时产物）；返回释放字节数。</summary>
    Task<CacheCleanResult> CleanCacheAsync(CancellationToken ct = default);

    /// <summary>缓存超出上限时清掉最旧的文件；启动时调用，返回释放字节数（0 = 没超）。</summary>
    Task<long> EnforceCacheLimitAsync(CancellationToken ct = default);

    /// <summary>[立即备份]：WAL checkpoint 后把 Library.db + Config.json 拷进 Backup/Database/{时间戳}/。</summary>
    Task<ImmediateBackupResult> CreateImmediateBackupAsync(CancellationToken ct = default);

    /// <summary>
    /// [导出诊断信息]：生成 zip（脱敏配置 + 最近日志 + 工具体检 + 库统计 + 系统信息），
    /// 返回 zip 绝对路径。不包含书库内容。
    /// </summary>
    Task<string> ExportDiagnosticsAsync(CancellationToken ct = default);
}
