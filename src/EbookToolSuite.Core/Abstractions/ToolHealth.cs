namespace EbookToolSuite.Core.Abstractions;

/// <summary>单个外部工具的健康状态。</summary>
public enum ToolState
{
    /// <summary>存在且版本探测通过。缺失探测时（runProbes=false）存在即算 Ok。</summary>
    Ok,

    /// <summary>文件或目录不存在。</summary>
    Missing,

    /// <summary>存在，但探测出的版本与 manifest 里钉住的版本不一致。</summary>
    VersionMismatch,

    /// <summary>存在，但跑探测时进程启动失败、超时或退出码非预期。</summary>
    ProbeFailed,

    /// <summary>可选工具，用户已在设置里关掉。不算问题，UI 应弱化显示。</summary>
    OptionalDisabled,
}

/// <summary>
/// 一个工具的体检结果。FixHint 是"缺失时怎么办"的文案，
/// 三条实现纪律里要求每个工具都必须有，不能只显示一个红叉。
/// </summary>
public sealed record ToolHealthItem(
    string Key,
    string DisplayName,
    bool Required,
    ToolState State,
    string? Version,
    string? Detail,
    string? FixHint);

/// <summary>全部工具的体检报告。主界面顶部的红条由 HasBlockingIssue 决定。</summary>
public sealed record ToolHealthReport(IReadOnlyList<ToolHealthItem> Items)
{
    /// <summary>有必需工具不健康 -> 主界面顶部红条 + 相关功能按钮置灰。</summary>
    public bool HasBlockingIssue =>
        Items.Any(i => i.Required && i.State != ToolState.Ok);

    /// <summary>阻塞性问题的工具清单，供红条里列名用。</summary>
    public IReadOnlyList<ToolHealthItem> Blocking =>
        Items.Where(i => i.Required && i.State != ToolState.Ok).ToList();

    public static ToolHealthReport Empty { get; } = new(Array.Empty<ToolHealthItem>());
}