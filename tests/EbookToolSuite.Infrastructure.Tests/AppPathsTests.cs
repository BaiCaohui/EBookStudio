using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Infrastructure.Config;

namespace EbookToolSuite.Infrastructure.Tests;

/// <summary>
/// AppPaths 的数据目录解析（设计文档 4.4 的四级顺序）。
///
/// 关于「不可写」怎么造：Windows 上用 ACL 造只读目录很麻烦且要管理员权限。
/// 这里用「在目录该在的位置放一个同名文件」来代替，Directory.CreateDirectory 会抛 IOException，
/// 于是 IsWritable 返回 false。效果与真实的只读介质一致，且不需要任何特权。
/// </summary>
public sealed class AppPathsTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _appRoot;
    private readonly string _userLocalRoot;

    public AppPathsTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "ets-apppaths-" + Guid.NewGuid().ToString("N"));
        _appRoot = Path.Combine(_sandbox, "app");
        _userLocalRoot = Path.Combine(_sandbox, "localappdata", "EbookToolSuite");

        Directory.CreateDirectory(_appRoot);
        Directory.CreateDirectory(_userLocalRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_sandbox, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    private AppPathsOptions Options(
        string? commandLineDataRoot = null,
        DataDirFallback fallback = DataDirFallback.Throw)
        => new()
        {
            AppRoot = _appRoot,
            UserLocalRoot = _userLocalRoot,
            CommandLineDataRoot = commandLineDataRoot,
            Fallback = fallback,
        };

    /// <summary>在目录该在的位置放一个同名文件，让它不可写。</summary>
    private static void BlockDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        File.WriteAllText(path, "blocked");
    }

    // ---------- 分支 1：命令行覆盖 ----------

    [Fact]
    public void Create_UsesCommandLineOverride_WhenProvided()
    {
        var custom = Path.Combine(_sandbox, "custom-data");

        var paths = AppPaths.Create(Options(commandLineDataRoot: custom));

        Assert.Equal(Path.GetFullPath(custom), paths.DataRoot);
        Assert.Equal(DataDirSource.CommandLine, paths.Source);
    }

    [Fact]
    public void Create_Throws_WhenCommandLineOverrideNotWritable()
    {
        var blocked = Path.Combine(_sandbox, "blocked-data");
        BlockDirectory(blocked);

        var ex = Assert.Throws<AppException>(
            () => AppPaths.Create(Options(commandLineDataRoot: blocked)));

        Assert.Equal(ErrorCode.DataDirNotWritable, ex.Code);
    }

    // ---------- 分支 2：指针文件 ----------

    [Fact]
    public void Create_UsesPointerFile_WhenPresent()
    {
        var external = Path.Combine(_sandbox, "external-data");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(_userLocalRoot, "datadir.txt"), external);

        var paths = AppPaths.Create(Options());

        Assert.Equal(Path.GetFullPath(external), paths.DataRoot);
        Assert.Equal(DataDirSource.PointerFile, paths.Source);
    }

    [Fact]
    public void Create_IgnoresStalePointerFile_AndWarns()
    {
        // 指针指向一个不存在的、也创建不出来的位置（同名文件占位）
        var stale = Path.Combine(_sandbox, "stale-data");
        BlockDirectory(stale);
        File.WriteAllText(Path.Combine(_userLocalRoot, "datadir.txt"), stale);

        var warnings = new List<string>();
        var paths = AppPaths.Create(Options(), warnings.Add);

        Assert.Equal(DataDirSource.ProgramDirectory, paths.Source);
        Assert.Single(warnings);
    }

    // ---------- 分支 3：默认（程序目录下 Data/） ----------

    [Fact]
    public void Create_DefaultsToProgramDirectoryData()
    {
        var paths = AppPaths.Create(Options());

        Assert.Equal(Path.Combine(_appRoot, "Data"), paths.DataRoot);
        Assert.Equal(DataDirSource.ProgramDirectory, paths.Source);
    }

    // ---------- 分支 4：只读降级 ----------

    [Fact]
    public void Create_FallsBackToUserLocal_WhenProgramDirNotWritable()
    {
        BlockDirectory(Path.Combine(_appRoot, "Data"));

        var paths = AppPaths.Create(Options(fallback: DataDirFallback.UseUserLocalAppData));

        Assert.Equal(Path.Combine(_userLocalRoot, "Data"), paths.DataRoot);
        Assert.Equal(DataDirSource.UserLocalAppData, paths.Source);

        // 关键：必须写出指针文件，否则下次启动又会回到不可写的程序目录
        Assert.True(File.Exists(paths.PointerFile));
        Assert.Equal(paths.DataRoot, File.ReadAllText(paths.PointerFile).Trim());

        // 写完指针后，下一次启动应该走 PointerFile 分支，结果一致
        var second = AppPaths.Create(Options());
        Assert.Equal(paths.DataRoot, second.DataRoot);
        Assert.Equal(DataDirSource.PointerFile, second.Source);
    }

    // ---------- 分支 5：全部失败 ----------

    [Fact]
    public void Create_Throws_WhenNoCandidateWritable()
    {
        BlockDirectory(Path.Combine(_appRoot, "Data"));
        BlockDirectory(Path.Combine(_userLocalRoot, "Data"));

        var ex = Assert.Throws<AppException>(
            () => AppPaths.Create(Options(fallback: DataDirFallback.UseUserLocalAppData)));

        Assert.Equal(ErrorCode.DataDirNotWritable, ex.Code);
        Assert.False(string.IsNullOrWhiteSpace(ex.UserMessage));
        Assert.NotEmpty(ex.Context);
    }

    // ---------- 附加：EnsureCreated 建全目录 ----------

    [Fact]
    public void EnsureCreated_CreatesEveryRequiredDirectory()
    {
        var paths = AppPaths.Create(Options());

        paths.EnsureCreated();

        var required = paths.EnumerateRequiredDirectories().ToList();

        /* T6-2B：Cache\Pages 不再是"必需目录"——自绘 PDF 引擎的页位图只在内存里（判据⑥），
           预建这个目录会让"磁盘上没有页缓存"这条判据变成一句空话。
           T6-5：加上 Exports（书签/笔记的 Markdown 导出落点），所以是 16 个。 */
        Assert.Equal(16, required.Count);
        foreach (var dir in required)
        {
            Assert.True(Directory.Exists(dir), "未创建: " + dir);
        }

        // 幂等：再调一次不应该抛
        paths.EnsureCreated();

        // 再 EnsureCreated 一次也不该把 Pages 冒出来
        Assert.False(Directory.Exists(paths.PagesCacheDir), @"T6-2B 之后不该预建 Cache\Pages");
    }

    [Fact]
    public void DirectoryLayout_MatchesDesignDocument()
    {
        var paths = AppPaths.Create(Options());

        Assert.Equal(Path.Combine(paths.DataRoot, "Config.json"), paths.ConfigFile);
        Assert.Equal(Path.Combine(paths.DataRoot, "Library.db"), paths.DbFile);
        Assert.Equal(Path.Combine(paths.DataRoot, "Library"), paths.LibraryDir);
        Assert.Equal(Path.Combine(paths.DataRoot, "Cache", "Covers"), paths.CoversCacheDir);
        Assert.Equal(Path.Combine(paths.DataRoot, "Cache", "Calibre"), paths.CalibreCacheDir);
        Assert.Equal(Path.Combine(paths.DataRoot, "Backup", "RestorePoints"), paths.RestorePointsDir);
        Assert.Equal(Path.Combine(paths.AppRoot, "Tools", "manifest.json"), paths.ToolsManifestFile);
    }

    [Fact]
    public void IsWritable_ReturnsFalse_ForBlockedPath_AndTrueForFreshPath()
    {
        var fresh = Path.Combine(_sandbox, "fresh");
        Assert.True(AppPaths.IsWritable(fresh, out var noError));
        Assert.Null(noError);

        var blocked = Path.Combine(_sandbox, "blocked");
        BlockDirectory(blocked);
        Assert.False(AppPaths.IsWritable(blocked, out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));
    }
}