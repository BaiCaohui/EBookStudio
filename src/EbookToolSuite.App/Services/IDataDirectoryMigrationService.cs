namespace EbookToolSuite.App.Services;

/// <summary>迁移前置检查的一条。<c>Id</c> 是给界面与冒烟断言用的稳定标识。</summary>
public sealed record MigrationCheck(string Id, string Title, bool Ok, string Message);

/// <summary>前置检查汇总。任一 <see cref="MigrationCheck.Ok"/> 为 false 都不许开始迁移。</summary>
public sealed record MigrationPreflight(
    bool CanMigrate,
    long RequiredBytes,
    long FreeBytes,
    IReadOnlyList<MigrationCheck> Checks)
{
    public IReadOnlyList<MigrationCheck> Failed => Checks.Where(c => !c.Ok).ToList();
}

/// <summary>复制进度（后台线程回调；界面自己切 UI 线程）。</summary>
public sealed record MigrationProgress(
    int CopiedFiles,
    int TotalFiles,
    long CopiedBytes,
    long TotalBytes,
    string CurrentItem);

/// <summary>一次迁移 / 切换的结果。</summary>
public sealed record MigrationResult(
    bool Success,
    string? Error,
    int CopiedFiles,
    long CopiedBytes,
    bool Verified,
    string TargetDir);

/// <summary>
/// 数据目录迁移（设计文档 4.4）。契约：
/// - 前置检查与复制分离：界面先跑检查，用户确认后才开始搬；
/// - 复制中途取消 = 删掉本次已复制的文件，指针文件一个字不改；
/// - 校验 = 文件数 + 逐文件大小 + 复制后数据库 PRAGMA integrity_check；
/// - 成功后写指针文件（datadir.txt）、在旧目录留 MOVED-TO.txt、
///   在 Config.json 记 paths.dataDir 与 paths.previousDataDir；
/// - 迁移要求重启：WebView2 的 userDataFolder 不支持热切换，所以不做热迁移。
/// </summary>
public interface IDataDirectoryMigrationService
{
    /// <summary>当前数据目录（绝对路径）。</summary>
    string CurrentDataDir { get; }

    /// <summary>上次迁移留下的旧目录；没有则 null。</summary>
    string? PreviousDataDir { get; }

    /// <summary>目标目录是否已经是一个数据目录（存在 Config.json 或 Library.db）。</summary>
    bool LooksLikeDataDir(string targetDir);

    /// <summary>
    /// 跑 5 条前置检查（目标合法 / 空间 / 可写 / 无占用的后台任务与窗口 / 本地固定盘）。
    /// </summary>
    Task<MigrationPreflight> PreflightAsync(string targetDir, CancellationToken ct = default);

    /// <summary>
    /// 复制 + 校验 + 写指针。默认排除 Cache/Temp/WebView2Data（可重建或被占用）；
    /// <paramref name="includeCaches"/> 为 true 时带上 Cache 与 Temp。
    /// </summary>
    Task<MigrationResult> MigrateAsync(
        string targetDir,
        bool includeCaches,
        IProgress<MigrationProgress>? progress,
        CancellationToken ct = default);

    /// <summary>目标已经是数据目录：直接切换指针（不复制；二次确认由界面负责）。</summary>
    Task<MigrationResult> SwitchToExistingAsync(string targetDir, CancellationToken ct = default);

    /// <summary>旧数据目录占用（字节）；没有旧目录返回 0。</summary>
    Task<long> OldDataSizeAsync(CancellationToken ct = default);

    /// <summary>删除旧数据目录（界面二次确认后调用；失败抛异常，界面给出原因）。</summary>
    Task DeleteOldDataAsync(CancellationToken ct = default);
}
