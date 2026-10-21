using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>
/// 导入队列里的一个任务（执行计划 T2-1）。**可变对象**：队列在后台线程
/// 直接改 State / Outcome，UI 列表订阅事件后自行刷新（不靠 WPF 绑定通知，
/// 因为列表可能上千条，逐条通知没必要）。
/// </summary>
public sealed class ImportJob
{
    public required Guid Id { get; init; }

    /// <summary>源文件的绝对路径。</summary>
    public required string SourcePath { get; init; }

    /// <summary>文件名（含扩展名），UI 列表直接显示。</summary>
    public string FileName => System.IO.Path.GetFileName(SourcePath);

    /// <summary>队列进展。</summary>
    public ImportJobState State { get; set; } = ImportJobState.Queued;

    /// <summary>业务结果。State 变成 Finished 之后才有值。</summary>
    public ImportOutcome? Outcome { get; set; }

    /// <summary>成功导入后的新书 Id。</summary>
    public Guid? BookId { get; set; }

    /// <summary>面向用户的一句话。</summary>
    public string? Message { get; set; }

    /// <summary>
    /// 预检阶段算出来的快速指纹。去重与 import_batch.note 都用它，
    /// 避免导入时重算（200 本书就是 200 次多余的 128KB 读取）。
    /// </summary>
    public string? QuickFingerprint { get; set; }

    /// <summary>预检发现重复时，已存在的那本书的 Id 与书名（对话框要显示）。</summary>
    public Guid? DuplicateOfBookId { get; set; }

    public string? DuplicateOfTitle { get; set; }

    /// <summary>
    /// 预检判定为重复、但用户选择"仍然导入"。
    /// 队列会据此把它翻成 ImportOptions.ImportDuplicates = true；
    /// 否则流水线自己会再判一次重复、把这次导入静默吞掉（T2-1 实测坑）。
    /// </summary>
public bool ImportAnyway { get; set; }

      /// <summary>
      /// 用户在重复对话框里选了"替换书籍"：先把库里那本旧的删掉，再把本次的文件当作这本书导入。
      /// </summary>
      public bool ReplaceExisting { get; set; }

    public TimeSpan Duration { get; set; }

    public bool IsTerminal => State is ImportJobState.Finished or ImportJobState.Canceled;

    public bool Succeeded => Outcome == ImportOutcome.Imported;
}
