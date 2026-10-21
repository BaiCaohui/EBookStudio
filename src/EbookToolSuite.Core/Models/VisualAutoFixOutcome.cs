namespace EbookToolSuite.Core.Models;

/// <summary>
/// 可视编辑"自动修正"执行结果（T5-2W 第 5 步）。
/// <see cref="RestorePointId"/> 非空表示：修正前把这一章的原文备份成了还原点（可用它退回修正前）。
/// <see cref="Applied"/> 表示有没有真的应用修正（Ok 为 false 时它若有值，说明是"修正后仍不合格"）。
/// </summary>
public sealed record VisualAutoFixOutcome(
    bool Ok,
    bool Applied,
    IReadOnlyList<SourceFix> Fixes,
    string Message,
    string? RestorePointId);