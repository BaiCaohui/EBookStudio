using EbookToolSuite.Core.Models;

namespace EbookToolSuite.App.Navigation;

/// <summary>左栏条目的构造。单独成类，让"诊断页是否可见"只有一处判断，也方便单测。</summary>
public static class NavigationCatalog
{
    public static IReadOnlyList<NavigationItem> Build(bool showDiagnostics)
    {
        var items = new List<NavigationItem>
        {
            new(Routes.Library, "书库", NavIcons.Library, "Ctrl+1"),
            new(Routes.Queue, "转换队列", NavIcons.Queue, "Ctrl+2"),
            new(Routes.Activity, "活动中心", NavIcons.Activity, "Ctrl+3"),
            new(Routes.Stats, "统计", NavIcons.Stats, "Ctrl+4"),
            new(Routes.Backup, "备份与还原", NavIcons.Backup, ""),
            new(Routes.Settings, "设置", NavIcons.Settings, "Ctrl+5"),
        };

        if (showDiagnostics)
        {
            items.Add(new(Routes.Diagnostics, "诊断", NavIcons.Diagnostics, "Ctrl+6"));
        }

        return items;
    }
}