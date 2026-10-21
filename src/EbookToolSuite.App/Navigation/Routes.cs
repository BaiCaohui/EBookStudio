namespace EbookToolSuite.App.Navigation;

/// <summary>
/// 全部路由字符串。**禁止在页面或 ViewModel 里写字面量**（设计文档 9.9.6 规则 1）。
/// </summary>
public static class Routes
{
    public const string Library = "library";
    public const string Queue = "queue";
    public const string Activity = "activity";
    public const string Stats = "stats";

    /// <summary>备份与还原（T3-7 补的页面）。</summary>
    public const string Backup = "backup";
    public const string Settings = "settings";
    public const string Diagnostics = "diagnostics";

    /// <summary>首次启动或 lastRoute 失效时用的路由。</summary>
    public const string Default = Library;
}