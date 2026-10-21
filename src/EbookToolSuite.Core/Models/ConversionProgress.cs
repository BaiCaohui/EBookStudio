namespace EbookToolSuite.Core.Models;

/// <summary>
/// 转换进度。Percent 为 null 时【绝不许伪造成 50%】，
/// UI 必须改成不确定进度条 + 已耗时（设计文档 6.3.3）。
/// </summary>
public sealed record ConversionProgress
{
    /// <summary>0..1，null = 不确定。</summary>
    public double? Percent { get; init; }

    /// <summary>原始进度行，例如 "34% Running transforms on e-book..."。</summary>
    public string? StageText { get; init; }

    /// <summary>已耗时。任何时候都要显示，这是唯一诚实的量化指标。</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>最近一行输出，用于界面上的「最近输出：」。</summary>
    public string? LastLine { get; init; }
}
