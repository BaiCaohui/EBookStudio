namespace EbookToolSuite.Core.Common;

/// <summary>还原点类型。进 manifest.json 的 type 字段与日志，只增不改。</summary>
public static class RestorePointTypes
{
    /// <summary>保存单本书前的自动备份。</summary>
    public const string Save = "save";

    /// <summary>还原之前自动建的"反向还原点"（还原错了还能退回来）。</summary>
    public const string Restore = "restore";

    /// <summary>批量操作前的还原点（一次含多本）。</summary>
    public const string Batch = "batch";

    /// <summary>可视编辑自动修正之前，对工作区里那一章的备份（修正后可用它退回修正前）。</summary>
    public const string AutoFix = "autofix";
}
