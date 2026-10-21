using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using EbookToolSuite.App.Settings;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Infrastructure.Data;
using EbookToolSuite.Infrastructure.Files;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.App.Services;

/// <summary>
/// 数据目录迁移（设计文档 §4.4 / 执行计划 T7-4）。
///
/// 三类动作刻意拆开：
///   1. <see cref="PreflightAsync"/> 只检查、不产生业务影响（唯一的写入是 writable 的真写探测）；
///   2. <see cref="MigrateAsync"/> 复制 + 校验 + 切换指针，失败/取消都回滚本次复制的文件；
///   3. <see cref="SwitchToExistingAsync"/> 目标已经是数据目录时只切指针，不复制。
///
/// 为什么必须重启：WebView2 的 userDataFolder 不支持热切换，SQLite 连接也全部绑在旧路径上，
/// 所以这里绝不尝试"运行中切目录"，只把指针写好，让下次启动从新目录开始。
/// </summary>
public sealed class DataDirectoryMigrationService : IDataDirectoryMigrationService
{
    private const string ConfigFileName = "Config.json";
    private const string DbFileName = "Library.db";
    private const string MovedNoteFileName = "MOVED-TO.txt";

    /// <summary>
    /// SQLite 的 WAL/SHM 伴生文件不复制：WAL 已 checkpoint 进主库，SHM 只是共享内存索引，
    /// 复制过去不但没用，还可能让只读校验打开时被"陈旧 SHM"绊住。
    /// </summary>
    private static readonly string DbWalFileName = DbFileName + "-wal";

    private static readonly string DbShmFileName = DbFileName + "-shm";

    /// <summary>空间预算的余量系数（设计文档 §4.4：待复制内容 x 1.2）。</summary>
    private const double SpaceFactor = 1.2;

    /// <summary>大到这个尺寸的文件每复制完一个就报一次进度，免得界面长时间不动。</summary>
    private const long LargeFileBytes = 32L * 1024 * 1024;

    private const int ProgressEveryFiles = 20;

    /// <summary>默认不搬的目录：可重建（Cache/Temp）或被运行中的进程占用（WebView2Data）。</summary>
    private const string CacheFolderName = "Cache";

    private const string TempFolderName = "Temp";

    private const string WebView2FolderName = "WebView2Data";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Config.json 的键名（从设置键的点分路径里取，避免两处字面量漂移）。</summary>
    private static readonly string PathsNodeName = AppSettingsKeys.DataDirMirror.Split('.')[0];

    private static readonly string DataDirNodeName = AppSettingsKeys.DataDirMirror.Split('.')[^1];

    private static readonly string PreviousDataDirNodeName = AppSettingsKeys.PreviousDataDir.Split('.')[^1];

    /// <summary>写 Config.json 时保留中文原样（与 JsonConfigStore 的写法一致）。</summary>
    private static readonly JsonSerializerOptions ConfigWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IAppPaths _paths;
    private readonly IConfigStore _config;
    private readonly IConversionQueue _queue;
    private readonly SqliteConnectionFactory _db;
    private readonly ILogger<DataDirectoryMigrationService> _log;

    public DataDirectoryMigrationService(
        IAppPaths paths,
        IConfigStore config,
        IConversionQueue queue,
        SqliteConnectionFactory db,
        ILogger<DataDirectoryMigrationService> log)
    {
        _paths = paths;
        _config = config;
        _queue = queue;
        _db = db;
        _log = log;
    }

    // ---------- 基本状态 ----------

    public string CurrentDataDir => Path.GetFullPath(_paths.DataRoot);

    public string? PreviousDataDir
    {
        get
        {
            var raw = _config.GetString(AppSettingsKeys.PreviousDataDir, null);

            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                var full = Path.GetFullPath(raw.Trim());

                if (!Directory.Exists(full))
                {
                    return null;
                }

                // 脏值保护：旧目录等于当前目录时绝不暴露给界面，否则设置页会提供一个
                // 「删除旧数据目录」入口去删正在用的数据目录。
                return IsSameDirectory(full, CurrentDataDir) ? null : full;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
    }

    public bool LooksLikeDataDir(string targetDir)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(targetDir.Trim());
            return File.Exists(Path.Combine(full, ConfigFileName))
                || File.Exists(Path.Combine(full, DbFileName));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // ---------- 前置检查 ----------

    /// <summary>
    /// 6 条检查，Id 固定（冒烟按 Id 断言）：target-valid / target-not-data-dir /
    /// target-space / target-writable / local-drive / no-busy-tasks。
    /// 任何一条内部抛异常都翻成"Ok=false + 人话"，绝不把异常漏给界面。
    /// </summary>
    public async Task<MigrationPreflight> PreflightAsync(string targetDir, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var current = CurrentDataDir;
        var (full, pathError) = NormalizeTarget(targetDir);

        var checks = new List<MigrationCheck>(6)
        {
            CheckTargetValid(full, pathError, current),
            CheckTargetNotDataDir(full, pathError),
        };

        var (spaceCheck, requiredBytes, freeBytes) =
            await CheckSpaceAsync(full, pathError, current, ct).ConfigureAwait(false);

        checks.Add(spaceCheck);
        checks.Add(CheckTargetWritable(full, pathError));
        checks.Add(CheckLocalDrive(full, pathError));
        checks.Add(CheckNoBusyTasks());

        return new MigrationPreflight(
            checks.All(check => check.Ok),
            requiredBytes,
            freeBytes,
            checks);
    }

    private MigrationCheck CheckTargetValid(string? full, string? pathError, string current)
    {
        try
        {
            if (full is null)
            {
                return Fail("target-valid", "目标路径", pathError ?? "目标路径不合法，请重新选择。");
            }

            var appRoot = Path.GetFullPath(_paths.AppRoot);

            if (IsSameDirectory(full, current))
            {
                return Fail("target-valid", "目标路径", "目标目录就是当前数据目录，请换一个位置。");
            }

            if (IsSameOrInside(full, current))
            {
                return Fail("target-valid", "目标路径", "目标目录不能位于当前数据目录内部（否则会把自己复制到自己身上）。请选择数据目录之外的位置。");
            }

            if (IsSameOrInside(current, full))
            {
                return Fail("target-valid", "目标路径", "当前数据目录不能位于目标目录内部，请选择另一个位置。");
            }

            if (IsSameOrInside(full, appRoot))
            {
                return Fail(
                    "target-valid",
                    "目标路径",
                    $"目标目录不能放在程序目录里（{appRoot}）。请选择程序目录之外的固定盘，换机器时只把 Data 拷走即可。");
            }

            return Ok("target-valid", "目标路径", "目标路径可用，与当前数据目录相互独立。");
        }
        catch (Exception ex)
        {
            return Fail("target-valid", "目标路径", "检查目标路径时出错：" + ex.Message);
        }
    }

    private MigrationCheck CheckTargetNotDataDir(string? full, string? pathError)
    {
        try
        {
            if (full is null)
            {
                return Fail("target-not-data-dir", "目标目录内容", pathError ?? "目标路径不合法，无法判断目录内容。");
            }

            if (LooksLikeDataDir(full))
            {
                return Fail(
                    "target-not-data-dir",
                    "目标目录内容",
                    "目标目录已经是一个数据目录（里面有 Config.json 或 Library.db）。可以换一个空目录；"
                    + "如果确定要用它，请点下面的「直接切换到该目录…」。");
            }

            return Ok("target-not-data-dir", "目标目录内容", "目标目录还不是数据目录，可以安全写入。");
        }
        catch (Exception ex)
        {
            return Fail("target-not-data-dir", "目标目录内容", "检查目标目录内容时出错：" + ex.Message);
        }
    }

    private async Task<(MigrationCheck Check, long RequiredBytes, long FreeBytes)> CheckSpaceAsync(
        string? full,
        string? pathError,
        string sourceDir,
        CancellationToken ct)
    {
        if (full is null)
        {
            return (Fail("target-space", "目标盘空间", pathError ?? "目标路径不合法，无法检查空间。"), 0, 0);
        }

        try
        {
            // 空间按"默认不搬缓存"估算（设计文档 §4.4 明确扣除 Cache/Temp）
            var plan = await Task.Run(() => BuildCopyPlan(sourceDir, includeCaches: false, ct), ct)
                .ConfigureAwait(false);

            var sourceBytes = plan.Sum(item => item.Length);
            var required = DiskSpaceGuard.RequiredBytes(sourceBytes, SpaceFactor);
            var (free, freeError) = GetFreeBytes(full);

            if (freeError is not null)
            {
                return (Fail("target-space", "目标盘空间", freeError), required, 0);
            }

            if (free < required)
            {
                return (Fail(
                    "target-space",
                    "目标盘空间",
                    $"目标盘空间不足：需要约 {FormatBytes(required)}（待复制内容 {FormatBytes(sourceBytes)} 的 1.2 倍），"
                    + $"当前可用 {FormatBytes(free)}。请清理目标盘或换一个空间更大的位置。"), required, free);
            }

            return (Ok(
                "target-space",
                "目标盘空间",
                $"需要约 {FormatBytes(required)}（已含 20% 余量），目标盘可用 {FormatBytes(free)}。"), required, free);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (Fail("target-space", "目标盘空间", "统计待复制内容或目标盘空间时出错：" + ex.Message), 0, 0);
        }
    }

    private MigrationCheck CheckTargetWritable(string? full, string? pathError)
    {
        try
        {
            if (full is null)
            {
                return Fail("target-writable", "目标目录权限", pathError ?? "目标路径不合法，无法做写入探测。");
            }

            if (AppPaths.IsWritable(full, out var error))
            {
                return Ok("target-writable", "目标目录权限", "目标目录可写（已实际写入并删除探测文件）。");
            }

            return Fail(
                "target-writable",
                "目标目录权限",
                "目标目录不可写，请换一个位置或检查权限。技术原因：" + (error ?? "未知"));
        }
        catch (Exception ex)
        {
            return Fail("target-writable", "目标目录权限", "检查目标目录权限时出错：" + ex.Message);
        }
    }

    private MigrationCheck CheckLocalDrive(string? full, string? pathError)
    {
        try
        {
            if (full is null)
            {
                return Fail("local-drive", "目标盘类型", pathError ?? "目标路径不合法，无法判断盘符类型。");
            }

            var root = Path.GetPathRoot(full);

            if (string.IsNullOrEmpty(root))
            {
                return Fail("local-drive", "目标盘类型", "目标路径没有盘符，请选择本机固定盘上的目录。");
            }

            DriveInfo drive;

            try
            {
                drive = new DriveInfo(root);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return Fail(
                    "local-drive",
                    "目标盘类型",
                    "目标看起来是网络路径或无法识别的盘符。SQLite 的 WAL 需要本机固定盘的文件锁语义，"
                    + "网络盘/可移动盘上可能出现数据库损坏，请选择本机固定盘。");
            }

            if (!drive.IsReady)
            {
                return Fail("local-drive", "目标盘类型", $"目标盘 {root} 当前不可用（可能已拔出或未就绪），请换一个位置。");
            }

            if (drive.DriveType != DriveType.Fixed)
            {
                var typeText = drive.DriveType switch
                {
                    DriveType.Network => "网络盘",
                    DriveType.Removable => "可移动盘（U 盘 / 移动硬盘）",
                    DriveType.CDRom => "光盘",
                    DriveType.Ram => "内存盘",
                    _ => drive.DriveType.ToString(),
                };

                return Fail(
                    "local-drive",
                    "目标盘类型",
                    $"{typeText}不能作为数据目录：SQLite 的 WAL 不支持网络盘/可移动盘的文件锁语义，"
                    + "可能出现数据库损坏。请选择本机固定盘。");
            }

            return Ok("local-drive", "目标盘类型", "目标是本机固定盘，SQLite 的 WAL 可以正常工作。");
        }
        catch (Exception ex)
        {
            return Fail("local-drive", "目标盘类型", "判断目标盘类型时出错：" + ex.Message);
        }
    }

    private MigrationCheck CheckNoBusyTasks()
    {
        try
        {
            var queueBusy = _queue.IsBusy;
            var queued = queueBusy ? _queue.QueuedCount : 0;
            var running = queueBusy ? _queue.RunningCount : 0;
            var windows = CollectBlockingWindows();

            if (!queueBusy && windows.Count == 0)
            {
                return Ok("no-busy-tasks", "后台任务", "转换队列空闲，也没有其它打开的阅读/编辑/转换窗口。");
            }

            var details = new List<string>();

            if (queueBusy)
            {
                details.Add($"转换队列还有任务没跑完（排队 {queued}，运行 {running}）");
            }

            if (windows.Count > 0)
            {
                details.Add(
                    "还有窗口开着：" + string.Join("、", windows.Take(5))
                    + (windows.Count > 5 ? " 等" : string.Empty));
            }

            return Fail(
                "no-busy-tasks",
                "后台任务",
                "请先关闭阅读/编辑器/转换窗口，并等转换队列空闲。" + string.Join("；", details) + "。");
        }
        catch (Exception ex)
        {
            return Fail(
                "no-busy-tasks",
                "后台任务",
                "检查后台任务状态时出错：" + ex.Message + "。请关闭所有其它窗口后重试。");
        }
    }

    /// <summary>
    /// 枚举"除主窗口之外还开着的窗口"。本窗口与首次运行向导是常驻在迁移流程里的，
    /// 必须按类型排除，否则向导一开，检查就永远不通过。
    /// 后台线程调用时切到 UI 线程枚举（WPF 的 Windows 集合不是线程安全的）。
    /// </summary>
    private static IReadOnlyList<string> CollectBlockingWindows()
    {
        var app = Application.Current;

        if (app is null)
        {
            return Array.Empty<string>();
        }

        if (!app.Dispatcher.CheckAccess())
        {
            try
            {
                return app.Dispatcher.Invoke(() => CollectBlockingWindows());
            }
            catch (Exception)
            {
                // 枚举窗口失败不该把迁移卡死：按"没有其它窗口"处理
                return Array.Empty<string>();
            }
        }

        var titles = new List<string>();

        foreach (Window window in app.Windows)
        {
            if (window is null
                || ReferenceEquals(window, app.MainWindow)
                || !window.IsVisible
                || window is MainWindow or FirstRunWizardWindow or DataMigrationWindow)
            {
                continue;
            }

            titles.Add(string.IsNullOrWhiteSpace(window.Title) ? "未命名窗口" : window.Title);
        }

        return titles;
    }

    // ---------- 迁移 ----------

    public async Task<MigrationResult> MigrateAsync(
        string targetDir,
        bool includeCaches,
        IProgress<MigrationProgress>? progress,
        CancellationToken ct = default)
    {
        var preflight = await PreflightAsync(targetDir, ct).ConfigureAwait(false);

        if (!preflight.CanMigrate)
        {
            var failed = preflight.Failed.FirstOrDefault();
            var reason = failed is null
                ? "前置检查没有通过，已取消迁移。"
                : $"{failed.Title}：{failed.Message}";

            return new MigrationResult(false, reason, 0, 0, false, targetDir);
        }

        var (target, pathError) = NormalizeTarget(targetDir);

        if (target is null)
        {
            return new MigrationResult(false, pathError ?? "目标路径不合法，已取消迁移。", 0, 0, false, targetDir);
        }

        var source = CurrentDataDir;
        var copiedFiles = new List<string>();
        var copiedDirs = new List<string>();
        var copiedCount = 0;
        var copiedBytes = 0L;

        // 2. 把 WAL 落进主库。必须在复制前做：只拷 Library.db 而漏掉 -wal，
        //    复制过去的就是一份少了最后几条事务的旧库。
        try
        {
            await CheckpointWalAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "迁移前 WAL checkpoint 失败，已中止迁移（未复制任何文件）");
            return new MigrationResult(
                false,
                "数据库忙，无法在迁移前把 WAL 落盘，已中止（没有复制任何文件，指针未改动）。"
                + "请关闭正在阅读/编辑的窗口后重试。技术原因：" + ex.Message,
                0,
                0,
                false,
                target);
        }

        try
        {
            // 3. 枚举 + 逐文件复制
            var plan = BuildCopyPlan(source, includeCaches, ct);
            var totalFiles = plan.Count;
            var totalBytes = plan.Sum(item => item.Length);

            _log.LogInformation(
                "开始迁移数据目录：{Source} -> {Target}，{Count} 个文件 / {Bytes} 字节（{CacheMode}）",
                source,
                target,
                totalFiles,
                totalBytes,
                includeCaches ? "连缓存一起搬" : "不含缓存");

            // 只记录"本次新建"的目录：回滚时绝不能删用户本来就有的空目录
            if (!Directory.Exists(target))
            {
                Directory.CreateDirectory(target);
                copiedDirs.Add(target);
            }
            else
            {
                Directory.CreateDirectory(target);
            }

            var index = 0;

            foreach (var item in plan)
            {
                ct.ThrowIfCancellationRequested();

                var destination = Path.Combine(target, item.RelativePath);
                var destinationDir = Path.GetDirectoryName(destination);

                if (!string.IsNullOrEmpty(destinationDir))
                {
                    if (!Directory.Exists(destinationDir))
                    {
                        Directory.CreateDirectory(destinationDir);
                        copiedDirs.Add(destinationDir);
                    }
                    else
                    {
                        Directory.CreateDirectory(destinationDir);
                    }
                }

                // overwrite:false：目标目录里若已有同名文件，宁可失败回滚，也不覆盖用户的文件
                File.Copy(item.SourcePath, destination, overwrite: false);
                copiedFiles.Add(destination);
                copiedCount++;
                copiedBytes += item.Length;
                index++;

                if (index % ProgressEveryFiles == 0 || item.Length >= LargeFileBytes || index == totalFiles)
                {
                    progress?.Report(new MigrationProgress(
                        copiedCount,
                        totalFiles,
                        copiedBytes,
                        totalBytes,
                        item.RelativePath));
                }
            }

            // 5. 校验：相对路径集合、逐文件大小、数据库完整性/版本
            var verifyError = await VerifyCopyAsync(source, target, plan, ct).ConfigureAwait(false);

            if (verifyError is not null)
            {
                _log.LogWarning("数据目录迁移校验失败，开始回滚：{Reason}", verifyError);
                RollbackCopied(target, copiedFiles, copiedDirs);

                return new MigrationResult(false, verifyError, copiedCount, copiedBytes, false, target);
            }

            // 6. 写指针 + 旧目录便条 + Config.json 镜像
            await WriteSwitchStateAsync(source, target, writeMovedNote: true, ct).ConfigureAwait(false);

            // 复制过去的 Config.json 还带着旧目录的镜像值；改成新值，重启后设置页才能看到
            // "删除旧数据目录"入口，也不会多打一条镜像不一致的 WARNING。
            TryPatchTargetConfig(source, target);

            _log.LogInformation(
                "数据目录迁移完成：{Source} -> {Target}，{Count} 个文件 / {Bytes} 字节，校验通过",
                source,
                target,
                copiedCount,
                copiedBytes);

            return new MigrationResult(true, null, copiedCount, copiedBytes, true, target);
        }
        catch (OperationCanceledException)
        {
            // 取消 = 删掉本次复制的文件与空目录，指针/Config 一个字没改（切换在复制与校验之后）
            RollbackCopied(target, copiedFiles, copiedDirs);
            _log.LogInformation(
                "数据目录迁移已取消：删除本次复制的 {Count} 个文件，指针与 Config.json 未改动",
                copiedCount);

            throw;
        }
        catch (Exception ex)
        {
            RollbackCopied(target, copiedFiles, copiedDirs);
            _log.LogError(ex, "数据目录迁移失败，已回滚本次复制的 {Count} 个文件", copiedCount);

            return new MigrationResult(
                false,
                BuildMigrationError(ex),
                copiedCount,
                copiedBytes,
                false,
                target);
        }
    }

    public async Task<MigrationResult> SwitchToExistingAsync(string targetDir, CancellationToken ct = default)
    {
        var (target, pathError) = NormalizeTarget(targetDir);

        if (target is null)
        {
            return new MigrationResult(false, pathError ?? "目标路径不合法。", 0, 0, false, targetDir);
        }

        var current = CurrentDataDir;

        if (IsSameDirectory(target, current))
        {
            return new MigrationResult(false, "目标目录就是当前数据目录，不需要切换。", 0, 0, false, target);
        }

        if (!LooksLikeDataDir(target))
        {
            return new MigrationResult(
                false,
                "目标目录不是数据目录（里面没有 Config.json 或 Library.db），无法直接切换。",
                0,
                0,
                false,
                target);
        }

        if (!AppPaths.IsWritable(target, out var writeError))
        {
            return new MigrationResult(
                false,
                "目标目录不可写，请检查权限后重试。技术原因：" + (writeError ?? "未知"),
                0,
                0,
                false,
                target);
        }

        try
        {
            await WriteSwitchStateAsync(current, target, writeMovedNote: false, ct).ConfigureAwait(false);
            TryPatchTargetConfig(current, target);
            _log.LogInformation("直接把数据目录切换到已有目录：{Old} -> {New}（未复制文件）", current, target);

            return new MigrationResult(true, null, 0, 0, true, target);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "切换到已有数据目录失败");
            return new MigrationResult(
                false,
                "切换失败：" + ex.Message + "。指针与 Config.json 已尽力恢复为原值。",
                0,
                0,
                false,
                target);
        }
    }

    // ---------- 旧目录 ----------

    public async Task<long> OldDataSizeAsync(CancellationToken ct = default)
    {
        var oldDir = PreviousDataDir;

        if (oldDir is null)
        {
            return 0L;
        }

        return await Task.Run(() => MeasureDirectory(oldDir, ct), ct).ConfigureAwait(false);
    }

    public async Task DeleteOldDataAsync(CancellationToken ct = default)
    {
        var oldDir = PreviousDataDir
            ?? throw new InvalidOperationException("没有可删除的旧数据目录（可能已经删过或从未迁移过）。");

        var current = CurrentDataDir;

        if (IsSameDirectory(oldDir, current))
        {
            throw new InvalidOperationException("旧数据目录就是当前数据目录，拒绝删除。");
        }

        var root = Path.GetPathRoot(oldDir);

        if (string.IsNullOrEmpty(root) || IsSameDirectory(oldDir, root))
        {
            throw new InvalidOperationException("拒绝删除盘符根目录：" + oldDir);
        }

        if (!Directory.Exists(oldDir))
        {
            throw new InvalidOperationException("旧数据目录已经不存在：" + oldDir);
        }

        try
        {
            await Task.Run(() => Directory.Delete(oldDir, recursive: true), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "删除旧数据目录失败：{Dir}", oldDir);
            throw new IOException(
                "删除旧数据目录失败（可能还有窗口占着里面的文件）：" + ex.Message + "。请关闭所有窗口后再试。",
                ex);
        }

        // 删干净了才清配置键：中途失败时键还在，用户可以重试
        await _config.SetAsync(AppSettingsKeys.PreviousDataDir, null, ct).ConfigureAwait(false);
        await _config.SaveAsync(ct).ConfigureAwait(false);

        _log.LogInformation("旧数据目录已删除：{Dir}", oldDir);
    }

    // ---------- 复制清单 ----------

    private sealed record CopyItem(string RelativePath, string SourcePath, long Length);

    /// <summary>
    /// 递归枚举要复制的文件。相对路径的第一段命中排除名单就跳过整棵子树；
    /// 排序让进度与校验顺序稳定（也方便出问题时按日志对照）。
    /// </summary>
    private static List<CopyItem> BuildCopyPlan(string sourceDir, bool includeCaches, CancellationToken ct)
    {
        var items = new List<CopyItem>();

        if (!Directory.Exists(sourceDir))
        {
            return items;
        }

        // includeCaches=true 时只排除 WebView2Data：它的 SQLite 数据库被运行中的 WebView2 占着，
        // 热拷一份没有意义，而且新环境会自己重建。
        var excluded = includeCaches
            ? new[] { WebView2FolderName }
            : new[] { CacheFolderName, TempFolderName, WebView2FolderName };

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(sourceDir, file);
            var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];

            if (excluded.Contains(firstSegment, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = Path.GetFileName(file);

            if (string.Equals(fileName, DbWalFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, DbShmFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long length;

            try
            {
                length = new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不到大小就整份漏掉是危险的（校验会报路径集合不一致并回滚），
                // 所以这里把异常抛给上层，由上层给出人话后终止。
                throw new IOException($"读不到文件信息：{relative}。{ex.Message}", ex);
            }

            items.Add(new CopyItem(relative, file, length));
        }

        items.Sort(static (left, right) =>
            string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));

        return items;
    }

    // ---------- 校验 ----------

    /// <summary>返回 null 表示校验通过；否则返回可直接展示给用户的中文原因。</summary>
    private async Task<string?> VerifyCopyAsync(
        string source,
        string target,
        IReadOnlyList<CopyItem> plan,
        CancellationToken ct)
    {
        HashSet<string> expected;
        HashSet<string> actual;

        try
        {
            expected = new HashSet<string>(plan.Select(item => item.RelativePath), StringComparer.OrdinalIgnoreCase);
            actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                actual.Add(Path.GetRelativePath(target, file));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "校验失败：统计目标目录的文件时出错。技术原因：" + ex.Message + "。已回滚，指针未改动。";
        }

        if (!expected.SetEquals(actual))
        {
            var missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            var extra = actual.Except(expected, StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            var parts = new List<string>();

            if (missing.Count > 0)
            {
                parts.Add("缺少 " + string.Join("、", missing));
            }

            if (extra.Count > 0)
            {
                parts.Add("多出 " + string.Join("、", extra));
            }

            return "校验失败：目标目录的文件清单与源目录不一致（" + string.Join("；", parts)
                + "）。已回滚，指针未改动。";
        }

        foreach (var item in plan)
        {
            ct.ThrowIfCancellationRequested();

            var destination = Path.Combine(target, item.RelativePath);

            try
            {
                var sourceLength = new FileInfo(item.SourcePath).Length;
                var targetLength = new FileInfo(destination).Length;

                if (sourceLength != targetLength)
                {
                    return $"校验失败：{item.RelativePath} 的大小不一致（源 {FormatBytes(sourceLength)}，"
                        + $"复制后 {FormatBytes(targetLength)}）。已回滚，指针未改动。";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"校验失败：读不到 {item.RelativePath} 的大小。技术原因：{ex.Message}。已回滚，指针未改动。";
            }
        }

        var sourceDb = Path.Combine(source, DbFileName);

        if (!File.Exists(sourceDb))
        {
            // 全新数据目录还没有数据库，没有可校验的库
            return null;
        }

        var copiedDb = Path.Combine(target, DbFileName);

        if (!File.Exists(copiedDb))
        {
            return "校验失败：复制后的数据目录里没有 Library.db。已回滚，指针未改动。";
        }

        try
        {
            var sourceChecks = await ReadDatabaseChecksAsync(sourceDb, ct).ConfigureAwait(false);
            var copiedChecks = await ReadDatabaseChecksAsync(copiedDb, ct).ConfigureAwait(false);

            if (!string.Equals(copiedChecks.Integrity.Trim(), "ok", StringComparison.OrdinalIgnoreCase))
            {
                return "校验失败：复制后的 Library.db 完整性检查没有通过"
                    + $"（integrity_check = {copiedChecks.Integrity}）。已回滚，指针未改动。";
            }

            if (sourceChecks.UserVersion != copiedChecks.UserVersion)
            {
                return "校验失败：复制后的 Library.db 结构版本不一致"
                    + $"（源 {sourceChecks.UserVersion}，复制后 {copiedChecks.UserVersion}）。已回滚，指针未改动。";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return "校验失败：检查复制后的 Library.db 时出错。技术原因：" + ex.Message + "。已回滚，指针未改动。";
        }

        return null;
    }

    /// <summary>
    /// 只读打开一个库，跑 integrity_check 与 user_version。
    /// 直接用只读连接而不是 SqliteConnectionFactory：后者的连接串绑在**当前**数据目录上。
    /// </summary>
    private static async Task<(long UserVersion, string Integrity)> ReadDatabaseChecksAsync(
        string dbPath,
        CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var integrity = await connection.ScalarAsync<string>("PRAGMA integrity_check;", ct: ct)
            .ConfigureAwait(false);
        var version = await connection.ScalarAsync<long>("PRAGMA user_version;", ct: ct)
            .ConfigureAwait(false);

        return (version, integrity ?? string.Empty);
    }

    // ---------- 回滚与切换 ----------

    private void RollbackCopied(
        string target,
        IReadOnlyList<string> copiedFiles,
        IReadOnlyList<string> copiedDirs)
    {
        var failures = 0;

        for (var i = copiedFiles.Count - 1; i >= 0; i--)
        {
            try
            {
                File.Delete(copiedFiles[i]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures++;
                _log.LogDebug(ex, "回滚：删除文件失败，继续处理其它文件：{File}", copiedFiles[i]);
            }
        }

        foreach (var dir in copiedDirs
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures++;
                _log.LogDebug(ex, "回滚：删除空目录失败：{Dir}", dir);
            }
        }

        /* copiedDirs 只记了"每个文件所在的叶子目录"和 target 根；Directory.CreateDirectory
           会顺带把中间父目录（例如 target\Library）隐式建出来，这些不在清单里。
           最后整体扫一遍 target 下的空目录、由深到浅删掉，保证取消/失败后目标真正为空。
           target 根目录保留：那是前置检查的真写探测建的，不是本次复制建的。 */
        try
        {
            if (Directory.Exists(target))
            {
                foreach (var dir in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories)
                             .OrderByDescending(path => path.Length))
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        {
                            Directory.Delete(dir);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failures++;
                        _log.LogDebug(ex, "回滚：删除残留空目录失败：{Dir}", dir);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures++;
            _log.LogDebug(ex, "回滚：枚举目标目录下的残留子目录失败：{Dir}", target);
        }

        if (failures > 0)
        {
            _log.LogWarning(
                "回滚时有 {Count} 个文件/目录没删掉，目标目录可能残留部分副本：{Target}",
                failures,
                target);
        }
    }

    /// <summary>
    /// 切换指针、旧目录便条与 Config.json 镜像。
    /// 顺序：Config 镜像 → MOVED-TO → 指针文件。指针是启动时唯一的权威锚点，
    /// 放在最后写：它一成功，重启就进新目录；之前任何一步失败都会把 Config 恢复原值，
    /// 保证"失败时指针文件保持不变"。
    /// </summary>
    private async Task WriteSwitchStateAsync(
        string oldDir,
        string newDir,
        bool writeMovedNote,
        CancellationToken ct)
    {
        var previousMirror = _config.GetString(AppSettingsKeys.DataDirMirror, null);
        var previousOld = _config.GetString(AppSettingsKeys.PreviousDataDir, null);

        try
        {
            await _config.SetAsync(AppSettingsKeys.DataDirMirror, JsonValue.Create(newDir), ct)
                .ConfigureAwait(false);
            await _config.SetAsync(AppSettingsKeys.PreviousDataDir, JsonValue.Create(oldDir), ct)
                .ConfigureAwait(false);
            await _config.SaveAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await TryRestoreConfigAsync(previousMirror, previousOld).ConfigureAwait(false);
            throw;
        }

        if (writeMovedNote)
        {
            try
            {
                File.WriteAllText(Path.Combine(oldDir, MovedNoteFileName), newDir, Utf8NoBom);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 旧目录留个便条只是方便用户，写不了不影响迁移本身
                _log.LogWarning(ex, "在旧数据目录写 {File} 失败（不影响迁移）", MovedNoteFileName);
            }
        }

        try
        {
            _paths.WritePointerFile(newDir);
        }
        catch
        {
            if (writeMovedNote)
            {
                try
                {
                    File.Delete(Path.Combine(oldDir, MovedNoteFileName));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.LogDebug(ex, "指针写入失败后清理 {File} 失败", MovedNoteFileName);
                }
            }

            await TryRestoreConfigAsync(previousMirror, previousOld).ConfigureAwait(false);
            throw;
        }

        _log.LogInformation("数据目录指针已切换：{Old} -> {New}", oldDir, newDir);
    }

    private async Task TryRestoreConfigAsync(string? mirror, string? previousOld)
    {
        try
        {
            await _config.SetAsync(
                    AppSettingsKeys.DataDirMirror,
                    mirror is null ? null : JsonValue.Create(mirror),
                    CancellationToken.None)
                .ConfigureAwait(false);
            await _config.SetAsync(
                    AppSettingsKeys.PreviousDataDir,
                    previousOld is null ? null : JsonValue.Create(previousOld),
                    CancellationToken.None)
                .ConfigureAwait(false);
            await _config.SaveAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 下次启动会按指针文件自动纠正 Config.json 镜像，这里只记日志
            _log.LogWarning(ex, "切换失败后恢复 Config.json 镜像值失败（下次启动会按指针文件自动纠正）");
        }
    }

    /// <summary>
    /// 把新数据目录里那份 Config.json 的 paths 镜像与 previousDataDir 改成迁移后的正确值。
    ///
    /// 为什么必须改：Config.json 本身就在数据目录里，迁移后重启读的是**复制过去的那份**。
    /// 不改的话它会带着旧目录的镜像（启动时虽会按指针纠正，但会多打一条 WARNING），
    /// 而且 previousDataDir 缺失会让设置页「删除旧数据目录」入口消失。
    /// 只动 paths.dataDir / paths.previousDataDir 两个键，其它字段原样保留（绝不丢设置）。
    /// </summary>
    private void TryPatchTargetConfig(string oldDir, string newDir)
    {
        try
        {
            var targetConfig = Path.Combine(newDir, ConfigFileName);
            JsonObject root;

            if (File.Exists(targetConfig))
            {
                root = JsonNode.Parse(File.ReadAllText(targetConfig)) as JsonObject ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            if (root[PathsNodeName] is not JsonObject pathsNode)
            {
                pathsNode = new JsonObject();
                root[PathsNodeName] = pathsNode;
            }

            pathsNode[DataDirNodeName] = newDir;
            pathsNode[PreviousDataDirNodeName] = oldDir;

            File.WriteAllText(targetConfig, root.ToJsonString(ConfigWriteOptions) + "\n", Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 只影响重启后的镜像值与"删除旧数据目录"入口；启动时会按指针自动纠正，不该让迁移算失败
            _log.LogWarning(
                ex,
                "写新数据目录的 Config.json 镜像失败（不影响迁移本身，重启后会按指针自动纠正）");
        }
    }

    private async Task CheckpointWalAsync(CancellationToken ct)
    {
        if (!File.Exists(_paths.DbFile))
        {
            _log.LogInformation("当前数据目录还没有 Library.db，跳过迁移前的 WAL checkpoint");
            return;
        }

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var busy = await connection.ScalarAsync<long>("PRAGMA wal_checkpoint(TRUNCATE);", ct: ct)
            .ConfigureAwait(false);

        if (busy != 0)
        {
            // busy 非 0 = 还有其它连接正在读/写，WAL 没能全部落进主库。
            // 这时复制出来的库可能少最后几条事务，宁可中止也不搬一份可能不一致的数据。
            throw new IOException($"数据库还被其它连接占用（wal_checkpoint busy={busy}）");
        }
    }

    // ---------- 小工具 ----------

    /// <summary>整份迁移只在这里做路径归一化；返回 null 时 Error 一定是人话。</summary>
    private static (string? FullPath, string? Error) NormalizeTarget(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, "目标目录不能为空，请先选择一个位置。");
        }

        // 用户从资源管理器复制来的路径常带引号
        var trimmed = raw.Trim().Trim('"').Trim();

        if (trimmed.Length == 0)
        {
            return (null, "目标目录不能为空，请先选择一个位置。");
        }

        try
        {
            if (!Path.IsPathFullyQualified(trimmed))
            {
                return (null, "请填写完整的绝对路径（例如 D:\\EbookData），不要只写目录名或相对路径。");
            }

            return (Path.GetFullPath(trimmed), null);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, "目标路径格式不合法：" + ex.Message);
        }
    }

    private static bool IsSameDirectory(string left, string right)
    {
        try
        {
            var leftFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
            var rightFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));

            return string.Equals(leftFull, rightFull, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>candidate 是否就是 parent 或位于 parent 内部（用于防自拷贝）。</summary>
    private static bool IsSameOrInside(string candidate, string parent)
    {
        var relative = Path.GetRelativePath(parent, candidate);

        if (relative == ".")
        {
            return true;
        }

        if (Path.IsPathRooted(relative))
        {
            return false;
        }

        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static (long FreeBytes, string? Error) GetFreeBytes(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(directory);

            if (string.IsNullOrEmpty(root))
            {
                return (0, "目标路径没有盘符，无法读取剩余空间。");
            }

            var drive = new DriveInfo(root);

            if (!drive.IsReady)
            {
                return (0, $"目标盘 {root} 当前不可用（可能已拔出或未就绪），请换一个位置。");
            }

            return (drive.AvailableFreeSpace, null);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return (0, "读不到目标盘的剩余空间，请确认选择的是本机固定盘。技术原因：" + ex.Message);
        }
    }

    private static long MeasureDirectory(string dir, CancellationToken ct)
    {
        var bytes = 0L;

        if (!Directory.Exists(dir))
        {
            return bytes;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 统计不是关键路径，读不到就跳过
            }
        }

        return bytes;
    }

    private static string BuildMigrationError(Exception ex) => ex switch
    {
        UnauthorizedAccessException =>
            "迁移失败：没有权限写入目标目录，请换一个有写入权限的位置。"
            + "已回滚本次复制的文件，指针与 Config.json 未改动。技术原因：" + ex.Message,

        IOException =>
            "迁移失败：读写文件时出错（目标目录里可能已有同名文件，或文件被占用）。"
            + "已回滚本次复制的文件，指针与 Config.json 未改动；可以换一个空目录重试。技术原因：" + ex.Message,

        _ =>
            "迁移失败：" + ex.Message
            + "。已回滚本次复制的文件，指针与 Config.json 未改动；可以重试或换一个目录。",
    };

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024d;

        if (bytes >= kb * kb * kb)
        {
            return (bytes / kb / kb / kb).ToString("F2") + " GB";
        }

        if (bytes >= kb * kb)
        {
            return (bytes / kb / kb).ToString("F1") + " MB";
        }

        if (bytes >= kb)
        {
            return (bytes / kb).ToString("F0") + " KB";
        }

        return bytes + " B";
    }

    private static MigrationCheck Ok(string id, string title, string message)
        => new(id, title, true, message);

    private static MigrationCheck Fail(string id, string title, string message)
        => new(id, title, false, message);
}
