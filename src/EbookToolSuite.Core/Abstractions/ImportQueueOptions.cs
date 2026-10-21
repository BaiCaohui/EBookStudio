namespace EbookToolSuite.Core.Abstractions;

/// <summary>
/// 一次批量导入的选项（执行计划 T2-1）。与 ImportOptions 的区别：
/// ImportOptions 描述"怎么导一本"，本类型描述"这一批怎么导"。
/// </summary>
public sealed record ImportQueueOptions
{
    /// <summary>展开文件夹时是否递归子目录。默认 true。</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>是否提取封面并生成两档图片。</summary>
    public bool ExtractCover { get; init; } = true;

    /// <summary>
    /// 预检发现重复时是否走 IDuplicateResolver 弹对话框。
    /// false 时按 SkipAll 静默处理（无 UI 的自动化路径用）。
    /// </summary>
    public bool PromptOnDuplicates { get; init; } = true;

    public int CoverFullSizePx { get; init; } = 1200;

    public int CoverThumbWidthPx { get; init; } = 320;

    /// <summary>写进 import_batch.source 的来源描述，例如文件夹路径。空则由队列自己拼。</summary>
    public string? SourceLabel { get; init; }

    /// <summary>预检阶段是否并发算指纹。默认 true（IO 密集，并发能明显缩短等待）。</summary>
    public bool ParallelPreflight { get; init; } = true;

    public static ImportQueueOptions Default { get; } = new();
}