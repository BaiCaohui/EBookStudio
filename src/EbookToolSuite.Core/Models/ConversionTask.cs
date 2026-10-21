using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>
/// conversion_tasks 的一行（执行计划 T2-4）。
/// 可变对象：队列在后台线程直接改 Status/Progress/RetryCount，然后整行写回。
/// </summary>
public sealed class ConversionTask
{
    public required Guid Id { get; init; }

    /// <summary>目标书。书被删掉后置 NULL（外键 ON DELETE SET NULL），任务必然失败。</summary>
    public Guid? BookId { get; set; }

    public string? BatchId { get; set; }

    /// <summary>源文件绝对路径（入队时就解析好，队列不依赖书库状态）。</summary>
    public required string InputPath { get; set; }

    /// <summary>计划产物路径（入队时算好；成功后写成实际路径）。</summary>
    public required string OutputPath { get; set; }

    public required BookFormatKind OutFormat { get; init; }

    public string? TemplateId { get; init; }

    /// <summary>临时覆盖参数的 JSON（ConversionArgument 数组）。</summary>
    public string ArgumentsJson { get; set; } = "[]";

    /// <summary>
    /// 产物是否登记进 book_formats。阅读缓存转换（T6-1）为 false；入库，
    /// 否则程序崩溃重启后重跑这条任务会把缓存产物误登记成书库格式。
    /// </summary>
    public bool RegisterFormat { get; set; } = true;

    /// <summary>队列优先级，越大越先跑（T6-1 判据 5）。入库。</summary>
    public int Priority { get; init; }
    public ConversionTaskStatus Status { get; set; } = ConversionTaskStatus.Queued;

    public double? Progress { get; set; }

    public ConversionTaskProgressKind ProgressKind { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public int RetryCount { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    // ---------- 以下不入库，只为界面显示 ----------

    /// <summary>书名（仓储 LEFT JOIN books 取回；书已删则 null）。</summary>
    public string? BookTitle { get; set; }

    /// <summary>源格式，入队时解析好写进 input_path 的同伴。</summary>
    public BookFormatKind SourceFormat { get; set; }

    /// <summary>终态：不会再自己变了，等用户重试或移除。</summary>
    public bool IsTerminal => Status is ConversionTaskStatus.Completed
        or ConversionTaskStatus.Failed or ConversionTaskStatus.Canceled;

    /// <summary>已耗时。跑着的按 now 算，结束的按 finished_at - started_at 算。</summary>
    public TimeSpan? Elapsed(DateTimeOffset now) => StartedAt is null
        ? null
        : (FinishedAt ?? now) - StartedAt.Value;

    /// <summary>状态的中文说法。界面与冒烟共用，别各写一份。</summary>
    public string StatusText => Status switch
    {
        ConversionTaskStatus.Queued => "排队中",
        ConversionTaskStatus.Running => "转换中",
        ConversionTaskStatus.Completed => "已完成",
        ConversionTaskStatus.Failed => "失败",
        ConversionTaskStatus.Canceled => "已取消",
        ConversionTaskStatus.WaitingRetry => "等待重试",
        _ => Status.ToString(),
    };

    /// <summary>进度说法：有百分比就说百分比，没有就说"进行中"（绝不编数字）。</summary>
    public string ProgressText => ProgressKind == ConversionTaskProgressKind.Percent
        && Progress is { } p
            ? (p * 100).ToString("0") + "%"
            : Status == ConversionTaskStatus.Completed ? "100%" : "进行中";
}
