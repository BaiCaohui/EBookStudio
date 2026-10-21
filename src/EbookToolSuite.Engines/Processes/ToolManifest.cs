namespace EbookToolSuite.Engines.Processes;

/// <summary>
/// Tools/manifest.json 的映射。**手写、人可读、程序只读**，
/// 所以这里只做反序列化，不做序列化。
/// 字段名与设计文档 2.4 一致，反序列化时大小写不敏感。
/// </summary>
public sealed class ToolManifest
{
    public int SchemaVersion { get; set; }

    public string? GeneratedAt { get; set; }

    public List<ToolManifestEntry> Tools { get; set; } = new();
}

public sealed class ToolManifestEntry
{
    /// <summary>唯一键：calibre / jre / epubcheck / pdfium / webview2。</summary>
    public string Key { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>钉住的版本。探测结果与它不一致时报告 VersionMismatch。</summary>
    public string? Version { get; set; }

    public bool Required { get; set; }

    /// <summary>逻辑名到相对路径（相对 Tools/）的映射。</summary>
    private Dictionary<string, string> _entryPoints = new();

    /// <summary>逻辑名到相对路径（相对 Tools/）的映射。JSON 里为 null 时视为空。</summary>
    public Dictionary<string, string> EntryPoints
    {
        get => _entryPoints;
        set => _entryPoints = value ?? new();
    }

    /// <summary>相对路径到 SHA256 小写十六进制的映射。可只覆盖关键入口文件。</summary>
    private Dictionary<string, string> _sha256 = new();

    public Dictionary<string, string> Sha256
    {
        get => _sha256;
        set => _sha256 = value ?? new();
    }

    /// <summary>调用该工具时必须注入的环境变量。{DataDir} 会被替换为真实数据目录。</summary>
    private Dictionary<string, string> _env = new();

    public Dictionary<string, string> Env
    {
        get => _env;
        set => _env = value ?? new();
    }

    public ToolProbeSpec? Probe { get; set; }

    /// <summary>缺失时给用户的补充说明（修复指引由代码按 key 生成，这里是额外备注）。</summary>
    public string? Notes { get; set; }
}

public sealed class ToolProbeSpec
{
    /// <summary>process = 跑进程看输出；fileExists = 只看文件；webview2 = 查注册表。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>相对 Tools/ 的入口文件。process 与 fileExists 用。</summary>
    public string? File { get; set; }

    private List<string> _args = new();

    public List<string> Args
    {
        get => _args;
        set => _args = value ?? new();
    }

    public int ExpectExitCode { get; set; }

    /// <summary>
    /// 依赖的其他工具 key。依赖缺失时直接判 ProbeFailed，
    /// 不去跑注定失败的探测（例如 EpubCheck 依赖 JRE）。
    /// </summary>
    private List<string> _dependsOn = new();

    public List<string> DependsOn
    {
        get => _dependsOn;
        set => _dependsOn = value ?? new();
    }
    /// <summary>
    /// 从 stdout + stderr 里抽取版本号的正则，**必须含一个捕获组**。
    /// 抽不到 -> ProbeFailed；抽到但与 version 不一致 -> VersionMismatch。
    /// </summary>
    public string? VersionPattern { get; set; }
}