namespace EbookToolSuite.Core.Models;

/// <summary>
/// 一次批量导入的批次记录（import_batch 表的映射，执行计划 T2-1）。
/// 活动中心按这张表列历史：导入完 200 本之后，事后还能看到"哪天导的、成了几本、为什么失败"。
/// </summary>
public sealed class ImportBatch
{
    public required Guid Id { get; set; }

    /// <summary>来源描述：用户选的文件夹路径或"12 个文件"，直接显示在活动中心。</summary>
    public string? Source { get; set; }

    public required DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>本次待处理文件数（**预检之后**的数量，不含被扫描阶段跳过的非 EPUB）。</summary>
    public int Total { get; set; }

    public int Succeeded { get; set; }

    public int Failed { get; set; }

    /// <summary>补充说明，例如"跳过重复 15 本"或"用户取消，已完成 42 本"。</summary>
    public string? Note { get; set; }

    public bool IsFinished => FinishedAt is not null;

    public TimeSpan? Duration => FinishedAt is DateTimeOffset f ? f - StartedAt : null;

    /// <summary>活动中心一行文案，例如"导入 200 本（成功 198，失败 2）"。</summary>
    public string Summary
    {
        get
        {
            if (!IsFinished)
            {
                return "进行中 " + Total + " 本";
            }

            return "导入 " + Total + " 本（成功 " + Succeeded + "，失败 " + Failed + "）";
        }
    }
}