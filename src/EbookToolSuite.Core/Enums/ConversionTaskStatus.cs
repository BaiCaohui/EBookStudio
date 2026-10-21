namespace EbookToolSuite.Core.Enums;

/// <summary>
/// conversion_tasks.status 的取值（设计文档 5.2 DDL）。
/// 数值直接入库，既有取值不得改动，只允许往后追加。
/// </summary>
public enum ConversionTaskStatus
{
    /// <summary>已入队，等待执行。</summary>
    Queued = 0,

    /// <summary>正在转换。</summary>
    Running = 1,

    /// <summary>转换成功，产物已登记进 book_formats。</summary>
    Completed = 2,

    /// <summary>失败且不再重试（终态，等用户点重试或移除）。</summary>
    Failed = 3,

    /// <summary>用户取消（终态）。</summary>
    Canceled = 4,

    /// <summary>失败但还会重试，正在退避等待。</summary>
    WaitingRetry = 5,
}

/// <summary>
/// conversion_tasks.progress_kind：这个进度是百分比还是不确定（设计文档 6.3.3）。
/// </summary>
public enum ConversionTaskProgressKind
{
    /// <summary>不确定进度。UI 画不确定进度条 + 已耗时，绝不伪造成 50%。</summary>
    Unknown = 0,

    /// <summary>progress 列是 0..1 的百分比。</summary>
    Percent = 1,
}
