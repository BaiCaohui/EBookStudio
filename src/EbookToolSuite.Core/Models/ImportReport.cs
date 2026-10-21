using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>
/// 导入进度快照。Index 从 1 开始，Total 为本次待处理文件数
/// （T1-2 单文件时恒为 1/1）。
/// </summary>
public sealed record ImportProgress(
    int Index,
    int Total,
    string FileName,
    ImportPhase Phase,
    double Percent = 0);

/// <summary>批量导入的汇总。T1-2 只会产生一条 Item。</summary>
public sealed record ImportReport
{
    public List<ImportResult> Items { get; init; } = new();

    public int ImportedCount => Items.Count(i => i.Outcome == ImportOutcome.Imported);
    public int DuplicateCount => Items.Count(i => i.Outcome == ImportOutcome.Duplicate);
    public int FailedCount => Items.Count(i => i.Outcome is ImportOutcome.Failed
        or ImportOutcome.InvalidEpub or ImportOutcome.SourceUnreadable);

    /// <summary>给 UI 的一句话汇总，例如"导入 1 本，重复 1 本，失败 0 本"。</summary>
    public string Summary =>
        $"导入 {ImportedCount} 本，重复 {DuplicateCount} 本，失败 {FailedCount} 本";
}