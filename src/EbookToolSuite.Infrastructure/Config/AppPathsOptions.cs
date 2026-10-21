namespace EbookToolSuite.Infrastructure.Config;

/// <summary>数据目录最终是从哪一级解析出来的。用于日志、诊断导出与 UI 提示。</summary>
public enum DataDirSource
{
    /// <summary>命令行 --datadir=&lt;绝对路径&gt;。</summary>
    CommandLine,

    /// <summary>指针文件 %LOCALAPPDATA%\EbookToolSuite\datadir.txt。</summary>
    PointerFile,

    /// <summary>程序目录下的 Data/（默认值）。</summary>
    ProgramDirectory,

    /// <summary>兜底切到 %LOCALAPPDATA%\EbookToolSuite\Data。</summary>
    UserLocalAppData,
}

/// <summary>候选目录都不可写时的行为。</summary>
public enum DataDirFallback
{
    /// <summary>抛 AppException(DataDirNotWritable)，由 UI 弹对话框二选一。这是默认值。</summary>
    Throw,

    /// <summary>自动切到 %LOCALAPPDATA%\EbookToolSuite\Data 并写指针文件。用户选了「使用用户目录数据」后走这条。</summary>
    UseUserLocalAppData,
}

/// <summary>
/// AppPaths.Create 的输入。做成可注入是为了能单测：测试里把 AppRoot 与 UserLocalRoot
/// 指到临时目录，就能在不碰真实程序目录的前提下覆盖全部分支。
/// </summary>
public sealed record AppPathsOptions
{
    /// <summary>程序根目录。生产环境传 AppContext.BaseDirectory。</summary>
    public required string AppRoot { get; init; }

    /// <summary>用户本地根目录，即 %LOCALAPPDATA%\EbookToolSuite。指针文件放在它下面。</summary>
    public required string UserLocalRoot { get; init; }

    /// <summary>命令行 --datadir 的值。null 表示没传。</summary>
    public string? CommandLineDataRoot { get; init; }

    /// <summary>候选都不可写时的行为。</summary>
    public DataDirFallback Fallback { get; init; } = DataDirFallback.Throw;
}