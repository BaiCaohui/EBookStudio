using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.IO;

namespace EbookToolSuite.Infrastructure.Config;

/// <summary>
/// 目录布局与数据目录解析（设计文档 4.1 / 4.4）。
///
/// 解析顺序（第一个可用者胜出）：
///   1. 命令行 --datadir=&lt;绝对路径&gt;
///   2. 指针文件 %LOCALAPPDATA%\EbookToolSuite\datadir.txt
///   3. 程序目录下的 Data/
///   4. 兜底：按 DataDirFallback 抛异常或切到 %LOCALAPPDATA%\EbookToolSuite\Data
///
/// 指针文件是定位数据目录的唯一权威来源。Config.json 里的 paths.dataDir 只是镜像值：
/// 因为 Config.json 本身就在数据目录内，数据目录一旦外置就无法自举定位。
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public const string DataFolderName = "Data";
    public const string PointerFileName = "datadir.txt";

    private AppPaths(string appRoot, string userLocalRoot, string dataRoot, DataDirSource source)
    {
        AppRoot = appRoot;
        UserLocalRoot = userLocalRoot;
        DataRoot = dataRoot;
        Source = source;
    }

    // ---------- 程序目录 ----------

    public string AppRoot { get; }

    public string ToolsRoot => Path.Combine(AppRoot, "Tools");

    public string ToolsManifestFile => Path.Combine(ToolsRoot, "manifest.json");

    // ---------- 数据目录 ----------

    public string DataRoot { get; }

    /// <summary>数据目录是从哪一级解析出来的。用于日志与诊断导出。</summary>
    public DataDirSource Source { get; }

    /// <summary>用户本地根目录（%LOCALAPPDATA%\EbookToolSuite），指针文件的存放处。</summary>
    public string UserLocalRoot { get; }

    /// <summary>指针文件绝对路径。它在数据目录之外，这是它能当锚点的前提。</summary>
    public string PointerFile => Path.Combine(UserLocalRoot, PointerFileName);

    public string ConfigFile => Path.Combine(DataRoot, "Config.json");

    public string DbFile => Path.Combine(DataRoot, "Library.db");

    public string LibraryDir => Path.Combine(DataRoot, "Library");

    public string CalibreConfigDir => Path.Combine(DataRoot, "CalibreConfig");

    public string WebView2DataDir => Path.Combine(DataRoot, "WebView2Data");

    public string WorkspacesDir => Path.Combine(DataRoot, "Workspaces");

    public string TempDir => Path.Combine(DataRoot, "Temp");

    public string LogsDir => Path.Combine(DataRoot, "Logs");

    public string CacheDir => Path.Combine(DataRoot, "Cache");

    public string CoversCacheDir => Path.Combine(CacheDir, "Covers");

    public string ReaderCacheDir => Path.Combine(CacheDir, "Reader");

    public string CalibreCacheDir => Path.Combine(CacheDir, "Calibre");

    public string PagesCacheDir => Path.Combine(CacheDir, "Pages");

    /// <summary>导出产物（Data/Exports/）。书签与笔记的 Markdown 导出落在这里（T6-5）。</summary>
    public string ExportsDir => Path.Combine(DataRoot, "Exports");

    public string BackupDir => Path.Combine(DataRoot, "Backup");

    public string BackupFilesDir => Path.Combine(BackupDir, "Files");

    public string RestorePointsDir => Path.Combine(BackupDir, "RestorePoints");

    public string BackupDatabaseDir => Path.Combine(BackupDir, "Database");

    // ---------- 生命周期 ----------

    /// <summary>需要预先创建的目录清单（对齐设计文档 4.1 的目录树）。</summary>
    public IEnumerable<string> EnumerateRequiredDirectories()
    {
        yield return DataRoot;
        yield return LibraryDir;
        yield return CalibreConfigDir;
        yield return WebView2DataDir;
        yield return WorkspacesDir;
        yield return TempDir;
        yield return LogsDir;
        yield return CacheDir;
        yield return CoversCacheDir;
        yield return ReaderCacheDir;
        yield return CalibreCacheDir;
        yield return ExportsDir;

        /* T6-2B：Pages 目录**故意不预建**。自绘 PDF 引擎的页位图只在内存里
           （判据⑥："Data/Cache/Pages 仍不存在"），仓库里没有任何代码往这里写文件；
           预建一个永远空的目录，只会把这条判据变成"存在但为空"这种验收不了的措辞。
           属性本身留着：将来真要做磁盘页缓存，落点还是它（设计文档 11.4）。 */
        yield return BackupDir;
        yield return BackupFilesDir;
        yield return RestorePointsDir;
        yield return BackupDatabaseDir;
    }

    public void EnsureCreated()
    {
        foreach (var dir in EnumerateRequiredDirectories())
        {
            Directory.CreateDirectory(dir);
        }
    }

    // ---------- 解析 ----------

    /// <summary>
    /// 按 4.4 的顺序解析数据目录。<paramref name="warn"/> 用于把降级原因写进日志。
    /// </summary>
    /// <exception cref="AppException">DataDirNotWritable：没有任何候选可用。</exception>
    public static AppPaths Create(AppPathsOptions options, Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var appRoot = Path.GetFullPath(options.AppRoot);
        var userRoot = Path.GetFullPath(options.UserLocalRoot);

        // 1. 命令行覆盖。这是显式指令，不可写就报错，绝不静默换目录。
        if (!string.IsNullOrWhiteSpace(options.CommandLineDataRoot))
        {
            var explicitDir = Path.GetFullPath(options.CommandLineDataRoot);
            if (!IsWritable(explicitDir, out var explicitErr))
            {
                throw new AppException(
                    ErrorCode.DataDirNotWritable,
                    $"--datadir 指定的目录不可写：{explicitDir}。请检查路径是否存在、是否有写入权限，或换一个目录。",
                    technical: $"Command-line data dir is not writable: {explicitDir}. {explicitErr}",
                    context: new Dictionary<string, object?>
                    {
                        ["dataRoot"] = explicitDir,
                        ["source"] = nameof(DataDirSource.CommandLine),
                        ["error"] = explicitErr,
                    });
            }

            return new AppPaths(appRoot, userRoot, explicitDir, DataDirSource.CommandLine);
        }

        // 2. 指针文件。存在但失效（路径被删、盘符消失、不可写）时降级到下一候选并告警。
        var pointerPath = Path.Combine(userRoot, PointerFileName);
        if (File.Exists(pointerPath))
        {
            var target = TryReadPointerFile(pointerPath, out var readError);
            if (target is null)
            {
                warn?.Invoke($"指针文件无法解析，已忽略：{pointerPath}。{readError}");
            }
            else if (!IsWritable(target, out var pointerErr))
            {
                warn?.Invoke($"指针文件指向的目录不可写，已忽略并改用默认位置：{target}。{pointerErr}");
            }
            else
            {
                return new AppPaths(appRoot, userRoot, target, DataDirSource.PointerFile);
            }
        }

        // 3. 程序目录下的 Data/（默认值）。
        var defaultDir = Path.Combine(appRoot, DataFolderName);
        if (IsWritable(defaultDir, out var defaultErr))
        {
            return new AppPaths(appRoot, userRoot, defaultDir, DataDirSource.ProgramDirectory);
        }

        // 4. 兜底分支（对应设计文档 4.3 规则 2）。
        var fallbackDir = Path.Combine(userRoot, DataFolderName);

        if (options.Fallback == DataDirFallback.UseUserLocalAppData
            && IsWritable(fallbackDir, out var fallbackErr))
        {
            var paths = new AppPaths(appRoot, userRoot, fallbackDir, DataDirSource.UserLocalAppData);
            paths.WritePointerFile(fallbackDir);
            return paths;
        }

        throw new AppException(
            ErrorCode.DataDirNotWritable,
            $"程序目录下的 {DataFolderName}/ 不可写，用户目录也不可用。请在「设置 - 高级 - 数据目录」选择另一个位置，或把程序移到有写入权限的目录。",
            technical: $"No writable data dir. programDir={defaultDir} ({defaultErr}); "
                     + $"userLocal={fallbackDir}; fallbackPolicy={options.Fallback}",
            context: new Dictionary<string, object?>
            {
                ["programDataDir"] = defaultDir,
                ["userLocalDataDir"] = fallbackDir,
                ["fallbackPolicy"] = options.Fallback.ToString(),
            });
    }

    /// <summary>
    /// 真写探测。只看 Directory.Exists 是不够的：目录可能存在但 ACL 只读，
    /// 也可能父级是只读介质。必须实际写一个文件再删掉。
    /// </summary>
    public static bool IsWritable(string dir, out string? error)
    {
        string? probe = null;
        try
        {
            Directory.CreateDirectory(dir);
            probe = Path.Combine(dir, ".wtest-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            // 探测文件可能已创建但删除失败，尽力清理，失败也不改变结论。
            if (probe is not null)
            {
                try { File.Delete(probe); } catch { /* 清理失败无所谓 */ }
            }

            return false;
        }
    }

    public static bool IsWritable(string dir) => IsWritable(dir, out _);

    /// <summary>读指针文件。返回 null 表示文件不存在或内容不是合法的绝对路径。</summary>
    public static string? TryReadPointerFile(string pointerPath, out string? error)
    {
        try
        {
            if (!File.Exists(pointerPath))
            {
                error = "文件不存在";
                return null;
            }

            var raw = File.ReadAllText(pointerPath).Trim();
            if (raw.Length == 0)
            {
                error = "文件为空";
                return null;
            }

            if (!Path.IsPathRooted(raw))
            {
                error = "内容不是绝对路径：" + raw;
                return null;
            }

            error = null;
            return Path.GetFullPath(raw);
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>写指针文件。数据目录在程序目录下时不应调用它（没必要时不留锚点）。</summary>
    public void WritePointerFile(string dataRoot)
    {
        Directory.CreateDirectory(UserLocalRoot);

        // T7-2 判据 4：数据目录指针是"自举锚点"，写坏一半会让程序下次找不到 Data/。
        AtomicFile.WriteAllText(
            PointerFile,
            Path.GetFullPath(dataRoot),
            new System.Text.UTF8Encoding(false));
    }

    public void DeletePointerFile()
    {
        if (File.Exists(PointerFile))
        {
            File.Delete(PointerFile);
        }
    }
}