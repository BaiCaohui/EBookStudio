namespace EbookToolSuite.Core.Models;

/// <summary>
/// 查找替换选项（执行计划 T5-3）。
/// </summary>
/// <remarks>
/// 2026-10-12 用户确认：正则模式是一个**勾选项**，勾上走 .NET 正则（支持 <c>$1</c> 与 <c>${name}</c>），
/// 不勾就是普通模式（查找串与替换串一律按字面量）。默认不勾。
/// </remarks>
public sealed record FindReplaceOptions(
    string Find,
    string Replace,
    bool UseRegex = false,
    bool MatchCase = false)
{
    /// <summary>预览里最多列几处上下文（计划：前 20 处）。</summary>
    public int MaxPreview { get; init; } = 20;

    /// <summary>整轮命中的硬上限：超过就中止、不写盘（防灾难性正则把整本书刷成几百万处）。</summary>
    public int MaxMatches { get; init; } = 50_000;

    /// <summary>单章匹配超时（正则回溯保护；.NET Regex 的 matchTimeout）。</summary>
    public TimeSpan PerChapterTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>整轮总超时（从开始扫描算起）。</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 只替换本章第 N 处命中（0 起；null = 全部）。给阅读器工具栏的【替换】（单处）用：
    /// 预览/校验/写盘链路与全部替换完全一致，只是命中集合缩到一处。
    /// </summary>
    public int? OnlyMatchOrdinal { get; init; }
}

/// <summary>一处命中的上下文（预览用）：命中前 20 字 / 命中 / 命中后 20 字。</summary>
public sealed record FindReplacePreviewItem(
    int ChapterIndex,
    string ChapterEntry,
    string ChapterTitle,
    int MatchIndexInChapter,
    string Before,
    string Match,
    string After);

/// <summary>一章的改动：<see cref="NewSource"/> 为空表示这一章没有命中、不需要写。</summary>
public sealed record FindReplaceChapterChange(
    int ChapterIndex,
    string Entry,
    string Title,
    int MatchCount,
    string? NewSource);

/// <summary>
/// 预览结果（同时就是"待执行的计划"）：<see cref="TotalMatches"/> 就是弹窗里那句"将替换 N 处"。
/// <see cref="Error"/> 非空 = 中止（非法正则 / 超时 / 命中超上限 / 章节读不了），此时绝不写盘。
/// </summary>
public sealed record FindReplacePlan(
    FindReplaceOptions Options,
    IReadOnlyList<FindReplaceChapterChange> Changes,
    IReadOnlyList<FindReplacePreviewItem> Preview,
    int TotalMatches,
    string? Error)
{
    public bool Ok => Error is null;

    /// <summary>有命中的章节数（= 真正会被写的文件数）。</summary>
    public int ChangedChapterCount => Changes.Count;
}

/// <summary>执行结果。</summary>
public sealed record FindReplaceOutcome(bool Ok, int ReplacedCount, int ChangedChapters, string Message)
{
    /// <summary>
    /// 替换前建的文件级快照编号（T5-4）。非 null 时可用"撤销上一次替换"按它把工作区还原回来；
    /// 为 null 表示这次没建快照（没注入快照服务的单测、或根本没有命中）。
    /// </summary>
    public string? SnapshotId { get; init; }
}

/// <summary>要参与查找替换的一章：spine 序号 + 包内路径 + 标题。</summary>
public sealed record FindReplaceChapter(int Index, string Entry, string Title);
