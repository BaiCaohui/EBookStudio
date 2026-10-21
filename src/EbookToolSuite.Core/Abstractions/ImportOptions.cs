namespace EbookToolSuite.Core.Abstractions;

/// <summary>导入行为开关（设计文档 7.1.1）。全部有合理默认值，调用方只改需要改的。</summary>
public sealed record ImportOptions
{
    /// <summary>
    /// true 时即使库中已有同指纹文件也照样导入一份新的。
    /// false（默认）时返回 ImportOutcome.Duplicate，由 UI 提示"这本书已在库中"。
    /// </summary>
    public bool ImportDuplicates { get; init; }

    /// <summary>用户选了"替换书籍"时要先删掉的那本书（旧书 Id）。null 表示不替换。</summary>
    public Guid? ReplaceBookId { get; set; }

    /// <summary>是否提取封面并生成两档图片。关掉可加快批量导入。</summary>
    public bool ExtractCover { get; init; } = true;

    /// <summary>展开文件夹时是否递归。T2-1 用；T1-2 只处理单个文件。</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>导入成功后自动打开阅读器。T1-5 用。</summary>
    public bool OpenAfterImport { get; init; }

    /// <summary>
    /// 本次导入属于哪个批次（T2-1）。写进 books.import_batch_id；单本导入时为 null。
    /// </summary>
    public Guid? BatchId { get; init; }

    /// <summary>
    /// 队列预检阶段已经算好的快速指纹，直接拿来用，省掉一次 128KB 读取。
    /// 为 null 时流水线自己算。
    /// </summary>
    public string? KnownQuickFingerprint { get; init; }

    /// <summary>封面大图的长边上限（px）。默认取 Config.library.coverFullSizePx。</summary>
    public int CoverFullSizePx { get; init; } = 1200;

    /// <summary>封面缩略图的宽度（px）。默认取 Config.library.coverThumbSizePx。</summary>
    public int CoverThumbWidthPx { get; init; } = 320;

    public static ImportOptions Default { get; } = new();
}