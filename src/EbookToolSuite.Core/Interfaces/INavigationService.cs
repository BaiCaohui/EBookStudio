using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 导航入口。业务服务与 ViewModel 只依赖这个接口，不碰 WPF。
/// 实现是 App/Navigation/NavigationService.cs（它要实例化页面，所以只能在有 XAML 的程序集里）。
/// </summary>
public interface INavigationService
{
    /// <summary>当前路由。初始值是 Routes.Default。</summary>
    string CurrentRoute { get; }

    /// <summary>是否有可回退的历史。</summary>
    bool CanGoBack { get; }

    /// <summary>左栏要显示的条目（不含被隐藏的诊断页）。</summary>
    IReadOnlyList<NavigationItem> Items { get; }

    /// <summary>导航完成后触发，参数是新路由。</summary>
    event EventHandler<string>? Navigated;

    /// <summary>
    /// 导航到指定路由。未知路由不抛异常，记 WRN 后回落到 Routes.Default。
    /// 当前页的 CanNavigateAwayAsync 返回 false 时，本次导航被取消。
    /// </summary>
    Task NavigateAsync(string route, object? parameter = null, CancellationToken ct = default);

    /// <summary>回退到上一个路由。没有历史时返回 false。</summary>
    Task<bool> GoBackAsync();
}