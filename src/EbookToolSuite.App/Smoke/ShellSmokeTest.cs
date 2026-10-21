using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EbookToolSuite.App.Navigation;
using EbookToolSuite.App.Views.Pages;
using EbookToolSuite.Infrastructure.Config;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// 外壳与导航的自动化冒烟（T1-3a 验收里能自动化的那部分）。
/// 视觉项（六态观感、ToolTip 延迟、焦点环）只能人眼验，报告里会列出来提醒。
/// </summary>
public static class ShellSmokeTest
{
    public static async Task<SmokeReport> RunAsync(
        MainWindow window, NavigationService nav, PageRegistry registry, AppPaths paths)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        FrameworkElement? current = null;
        nav.ContentChanged += (_, element) => current = element;

        var failures = new List<NavigationFailure>();
        nav.PageFailed += (_, failure) => failures.Add(failure);

        // ---------- 六个路由都能导航到正确的页面 ----------
        var expected = new (string Route, Type Page)[]
        {
            (Routes.Library, typeof(LibraryPage)),
            (Routes.Queue, typeof(QueuePage)),
            (Routes.Activity, typeof(ActivityPage)),
            (Routes.Stats, typeof(StatsPage)),
            (Routes.Backup, typeof(BackupPage)),
            (Routes.Settings, typeof(SettingsPage)),
            (Routes.Diagnostics, typeof(DiagnosticsPage)),
        };

        var wrong = new List<string>();
        foreach (var (route, page) in expected)
        {
            await nav.NavigateAsync(route).ConfigureAwait(true);
            if (current?.GetType() != page
                || !string.Equals(nav.CurrentRoute, route, StringComparison.Ordinal))
            {
                wrong.Add(route + " 得到 " + (current?.GetType().Name ?? "(null)"));
            }
        }

        checks.Add(new SmokeCheck(
            "nav-all-six-routes",
            wrong.Count == 0,
            wrong.Count == 0 ? "七个路由页面类型全部正确" : "不匹配：" + string.Join(", ", wrong)));

        // ---------- 页面实例被缓存（切走再回来是同一个实例） ----------
        await nav.NavigateAsync(Routes.Queue).ConfigureAwait(true);
        var firstVisit = current;
        await nav.NavigateAsync(Routes.Stats).ConfigureAwait(true);
        await nav.NavigateAsync(Routes.Queue).ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "nav-page-cached",
            ReferenceEquals(firstVisit, current),
            ReferenceEquals(firstVisit, current) ? "重复访问命中缓存，没有重建页面" : "页面被重建了"));

        // ---------- 未知路由静默回落到书库 ----------
        await nav.NavigateAsync("这个路由不存在").ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "nav-unknown-route-falls-back",
            string.Equals(nav.CurrentRoute, Routes.Library, StringComparison.Ordinal)
                && current is LibraryPage,
            "CurrentRoute=" + nav.CurrentRoute));

        // ---------- 历史回退 ----------
        while (await nav.GoBackAsync().ConfigureAwait(true))
        {
        }

        await nav.NavigateAsync(Routes.Library).ConfigureAwait(true);
        await nav.NavigateAsync(Routes.Queue).ConfigureAwait(true);
        await nav.NavigateAsync(Routes.Stats).ConfigureAwait(true);

        var back1 = await nav.GoBackAsync().ConfigureAwait(true);
        var route1 = nav.CurrentRoute;
        var back2 = await nav.GoBackAsync().ConfigureAwait(true);
        var route2 = nav.CurrentRoute;
        var back3 = await nav.GoBackAsync().ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "nav-back-to-previous",
            back1 && string.Equals(route1, Routes.Queue, StringComparison.Ordinal),
            "第一次回退到 " + route1 + "（应为 queue）"));

        // 连按回退不能变成"在两点之间来回弹"：第二次必须继续往前，第三次必须说没有历史了
        checks.Add(new SmokeCheck(
            "nav-back-no-pingpong",
            back2 && string.Equals(route2, Routes.Library, StringComparison.Ordinal)
                && (!back3 || !string.Equals(nav.CurrentRoute, Routes.Stats, StringComparison.Ordinal)),
            "第二次回退到 " + route2 + "，第三次返回 " + back3
                + "，落在 " + nav.CurrentRoute + "（都不应是 stats）"));

        // ---------- 历史栈有上限，狂点不崩 ----------
        for (var i = 0; i < 40; i++)
        {
            await nav.NavigateAsync(i % 2 == 0 ? Routes.Library : Routes.Queue).ConfigureAwait(true);
        }

        var drained = 0;
        while (await nav.GoBackAsync().ConfigureAwait(true))
        {
            drained++;
            if (drained > 100)
            {
                break;
            }
        }

        checks.Add(new SmokeCheck(
            "nav-history-capped",
            drained <= 20,
            "40 次导航后回退 " + drained + " 次（上限 20）"));

        // ---------- 页面抛异常时不崩，走 PageFailed ----------
        registry.Register("smoke-boom", () => new BoomPage());
        failures.Clear();
        await nav.NavigateAsync("smoke-boom").ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "page-exception-contained",
            failures.Count == 1 && string.Equals(failures[0].Route, "smoke-boom", StringComparison.Ordinal),
            failures.Count == 0 ? "PageFailed 没有触发" : "已捕获：" + failures[0].Exception.Message));

        await nav.NavigateAsync(Routes.Library).ConfigureAwait(true);

        // ---------- 导航项目录：诊断页默认隐藏 ----------
        var visible = NavigationCatalog.Build(showDiagnostics: false);
        var withDiagnostics = NavigationCatalog.Build(showDiagnostics: true);

        checks.Add(new SmokeCheck(
            "catalog-diagnostics-hidden-by-default",
            visible.Count == 6 && visible.All(i => i.Route != Routes.Diagnostics),
            "默认 " + visible.Count + " 项，含诊断=" + visible.Any(i => i.Route == Routes.Diagnostics)));

        checks.Add(new SmokeCheck(
            "catalog-diagnostics-shown-when-asked",
            withDiagnostics.Count == 7 && withDiagnostics.Any(i => i.Route == Routes.Diagnostics),
            "开启后 " + withDiagnostics.Count + " 项"));

        // ---------- 窄窗折叠 ----------
        window.Show();
        await PumpAsync().ConfigureAwait(true);

        const double threshold = 900.0;
        window.Width = threshold - 100;
        await PumpAsync().ConfigureAwait(true);
        await Task.Delay(260).ConfigureAwait(true);   // 等 150ms 的折叠动画走完

        var collapsed = window.IsPaneCollapsed;
        var collapsedWidth = window.PaneWidth;

        window.Width = threshold + 100;
        await PumpAsync().ConfigureAwait(true);
        await Task.Delay(260).ConfigureAwait(true);

        var expanded = !window.IsPaneCollapsed;
        var expandedWidth = window.PaneWidth;

        checks.Add(new SmokeCheck(
            "narrow-window-collapses",
            collapsed && Math.Abs(collapsedWidth - 48) < 1.0,
            "窄窗 IsNavCollapsed=" + collapsed + "，左栏宽=" + collapsedWidth.ToString("F0")));

        checks.Add(new SmokeCheck(
            "wide-window-expands",
            expanded && Math.Abs(expandedWidth - 240) < 1.0,
            "宽窗 IsNavCollapsed=" + !expanded + "，左栏宽=" + expandedWidth.ToString("F0")));

        // 用户手动折叠后，把窗口拉宽不应把它自动展开
        window.TogglePaneForSmoke();
        await Task.Delay(260).ConfigureAwait(true);
        window.Width = threshold + 200;
        await PumpAsync().ConfigureAwait(true);
        await Task.Delay(260).ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "manual-collapse-wins",
            window.IsPaneCollapsed,
            "手动折叠后把窗口拉宽，IsNavCollapsed=" + window.IsPaneCollapsed + "（应保持 true）"));


        // ---------- 折叠按钮不能消失、图标不能被裁 ----------
        // 这两条对应 2026-10-02 用户实测报过的两个 bug：
        // 此时左栏处于折叠态（上一条检查刚好确认过），直接量即可。
        // 一是 DockPanel.LastChildFill 把底部按钮挤成 0 宽，二是折叠后图标 Margin 未改导致溢出被裁。


        // ---------- 折叠按钮必须在侧边栏内部的最上方，且用细横线与导航项隔开 ----------
        // 对应用户 2026-10-02 的要求："置于侧边栏内上方，且与侧边栏（按钮）用细横线隔开"。

        var chrome = window.HeaderMetrics();
        var paneRight = chrome.PaneLeft + chrome.PaneWidth;

        checks.Add(new SmokeCheck(
            "toggle-inside-sidebar-top",
            chrome.BtnTop >= chrome.PaneTop - 0.5
                && chrome.BtnBottom <= chrome.ListTop + 0.5
                && chrome.BtnLeft >= chrome.PaneLeft - 0.5
                && chrome.BtnRight <= paneRight + 0.5,
            $"按钮 {chrome.BtnLeft:F1},{chrome.BtnTop:F1} 到 {chrome.BtnRight:F1},{chrome.BtnBottom:F1}；"
                + $"左栏左 {chrome.PaneLeft:F1} 右 {paneRight:F1} 顶 {chrome.PaneTop:F1}；"
                + $"导航列表顶 {chrome.ListTop:F1}"));

        checks.Add(new SmokeCheck(
            "thin-line-between-toggle-and-list",
            Math.Abs(chrome.SepHeight - 1) < 0.6
                && chrome.SepTop >= chrome.BtnBottom - 0.5
                && chrome.SepTop <= chrome.ListTop + 0.5,
            $"线高 {chrome.SepHeight:F1}，线顶 {chrome.SepTop:F1}"
                + $"（应夹在按钮底 {chrome.BtnBottom:F1} 与列表顶 {chrome.ListTop:F1} 之间）"));

        // 折叠按钮的中心必须与导航项图标的中心在同一条竖线上（展开与折叠两种状态都应成立）。
        var iconForAlign = window.FirstNavIconBounds();
        if (iconForAlign is null)
        {
            checks.Add(new SmokeCheck(
                "toggle-aligns-with-nav-icons", false, "取不到导航项图标的布局信息"));
        }
        else
        {
            var btnCenterX = (chrome.BtnLeft + chrome.BtnRight) / 2;
            var iconCenterX = iconForAlign.Value.IconLeft + (iconForAlign.Value.IconWidth / 2);
            checks.Add(new SmokeCheck(
                "toggle-aligns-with-nav-icons",
                Math.Abs(btnCenterX - iconCenterX) <= 1.5,
                $"按钮中心 {btnCenterX:F1} 对图标中心 {iconCenterX:F1}"));
        }


        var toggle = window.PaneToggleMetrics;
        var toggleUsable = toggle.IsVisible && toggle.Width > 0 && toggle.Height > 0;
        checks.Add(new SmokeCheck(
            "toggle-button-visible-when-collapsed",
            toggleUsable,
            $"可见={toggle.IsVisible} 宽={toggle.Width:F0} 高={toggle.Height:F0} 窗口已加载={toggle.WindowLoaded}"));

        var iconBounds = window.FirstNavIconBounds();
        if (iconBounds is null)
        {
            checks.Add(new SmokeCheck(
                "nav-icon-fits-when-collapsed", false, "取不到导航项图标的布局信息"));
        }
        else
        {
            var (iconLeft, iconWidth, itemLeft, itemWidth, paneWidth) = iconBounds.Value;
            var iconRight = iconLeft + iconWidth;
            var itemRight = itemLeft + itemWidth;
            var iconCenter = iconLeft + (iconWidth / 2);
            var paneCenter = paneWidth / 2;

            // 三条都要满足：图标不超出条目、不超出左栏、在左栏里居中。
            // 中间那条是 2026-10-02 那次"折叠后图标显示不全"的直接判据：
            // 当时条目内容宽 32、图标却占 34，右侧被裁掉。
            var fitsItem = iconLeft >= itemLeft && iconRight <= itemRight;
            var fitsPane = iconLeft >= 0 && iconRight <= paneWidth;
            var centered = Math.Abs(iconCenter - paneCenter) <= 2.0;

            checks.Add(new SmokeCheck(
                "nav-icon-fits-when-collapsed",
                fitsItem && fitsPane && centered,
                $"图标 {iconLeft:F1} 到 {iconRight:F1}，条目 {itemLeft:F1} 到 {itemRight:F1}，"
                    + $"左栏宽 {paneWidth:F0}，居中差 {Math.Abs(iconCenter - paneCenter):F1}"));
        }

        window.Close();

        checks.Add(new SmokeCheck(
            "manual-checks-remaining",
            true,
            "以下项只能人眼验：导航项六态观感、折叠态 ToolTip 延迟、"
                + "鼠标点击不出现焦点环而键盘导航出现、空状态与占位页的视觉"));

        return Finish(paths, startedAt, stopwatch, checks);
    }

    private static async Task PumpAsync()
    {
        // 让 WPF 把布局与 SizeChanged 跑完
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private static SmokeReport Finish(
        AppPaths paths, DateTimeOffset startedAt, Stopwatch stopwatch, List<SmokeCheck> checks)
    {
        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            stopwatch.ElapsedMilliseconds,
            paths.DataRoot,
            paths.TempDir,
            null,
            checks);

        Directory.CreateDirectory(paths.TempDir);
        File.WriteAllText(
            Path.Combine(paths.TempDir, "shell-smoke.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return report;
    }

    /// <summary>故意在 OnNavigatedToAsync 里抛异常的页面，用来验证错误被兜住。</summary>
    private sealed class BoomPage : UserControl, INavigationPage
    {
        public Task OnNavigatedToAsync(object? parameter, CancellationToken ct)
            => throw new InvalidOperationException("冒烟：这是页面故意抛出的异常。");

        public Task<bool> CanNavigateAwayAsync() => Task.FromResult(true);
    }
}