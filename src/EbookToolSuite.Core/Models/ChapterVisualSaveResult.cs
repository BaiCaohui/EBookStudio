namespace EbookToolSuite.Core.Models;

/// <summary>
/// 可视模式保存的结果（T5-2W）。
/// <see cref="NeedsFix"/> 为 true 表示"有自动修正建议、等用户确认"，此时还没有写盘。
/// </summary>
public sealed record ChapterVisualSaveResult(
    bool Ok,
    bool NeedsFix,
    IReadOnlyList<SourceFix> Fixes,
    ChapterSourceCheck Check,
    string Message);
