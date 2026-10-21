namespace EbookToolSuite.Core.Common;

/// <summary>
/// operation_log.operation 的取值。**全工程唯一一处定义**，别再写字面量。
/// 值进数据库，改名等于破坏历史，所以只增不改。
/// </summary>
public static class OperationNames
{
    /// <summary>保存（校验通过，静默替换）。</summary>
    public const string Save = "save";

    /// <summary>保存（校验没过/超时，用户选择仍然保存）。</summary>
    public const string SaveForced = "save_forced";

    /// <summary>从还原点还原。</summary>
    public const string Restore = "restore";

    /// <summary>按上限清理还原点。</summary>
    public const string Prune = "prune";

    /// <summary>换封面（编辑器里的封面替换，最终也走一次保存）。</summary>
    public const string ReplaceCover = "replace_cover";

    /// <summary>导入（T2 的导入管线暂未写日志，留在这里给活动中心统一取值）。</summary>
    public const string Import = "import";

    /// <summary>可视编辑保存时的自动修正（T5-2W：用户确认后改工作区里的这一章）。</summary>
    public const string AutoFix = "autofix";
}
