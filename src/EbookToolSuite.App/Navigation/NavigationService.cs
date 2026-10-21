using System.Windows;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.App.Navigation;

/// <summary>页面加载失败时交给 Shell 的信息，用于显示内联错误面板。</summary>
public sealed record NavigationFailure(string Route, Exception Exception);

/// <summary>
/// 导航服务的实现。规格见设计文档 9.9.9。
/// </summary>
/// <remarks>
/// 刻意放在 App 而不是 Services：它要实例化 WPF 页面（FrameworkElement）。
/// 业务代码只依赖 Core 的 INavigationService，看不到这里。
/// </remarks>
public sealed class NavigationService : INavigationService
{
    private const int MaxHistory = 20;

    private readonly PageRegistry _registry;
    private readonly ILogger<NavigationService> _log;

    /// <summary>已构造的页面实例。阶段 1 不做销毁策略（页面少、状态简单）。</summary>
    private readonly Dictionary<string, FrameworkElement> _cache = new(StringComparer.Ordinal);

    /// <summary>用 List 当栈：栈底是最早访问的，超过上限要能从底部丢。</summary>
    private readonly List<string> _history = new();

    public NavigationService(PageRegistry registry, ILogger<NavigationService> log)
    {
        _registry = registry;
        _log = log;
    }

    public string CurrentRoute { get; private set; } = Routes.Default;

    public bool CanGoBack => _history.Count > 0;

    public IReadOnlyList<NavigationItem> Items { get; private set; } = Array.Empty<NavigationItem>();

    public event EventHandler<string>? Navigated;

    /// <summary>Shell 订阅它来换内容区。放具体类型上，不进 INavigationService。</summary>
    public event EventHandler<FrameworkElement>? ContentChanged;

    /// <summary>页面 OnNavigatedToAsync 抛异常时触发，Shell 显示内联错误面板。</summary>
    public event EventHandler<NavigationFailure>? PageFailed;

    /// <summary>App 启动时调用一次，把可见的导航项交给 Shell。</summary>
    public void Configure(IEnumerable<NavigationItem> items) => Items = items.ToList();

    /// <summary>
    /// 启动时把初始路由改成"上次停留的页面"。只接受注册表里存在的路由，
    /// 所以上次停在诊断页、而这次诊断页不可见时会自动落回书库（设计文档 9.10.5）。
    /// </summary>
    public bool SetInitialRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route) || !_registry.Contains(route))
        {
            return false;
        }

        CurrentRoute = route;
        return true;
    }

    public Task NavigateAsync(string route, object? parameter = null, CancellationToken ct = default)
        => NavigateCoreAsync(route, parameter, pushHistory: true, ct);

    public async Task<bool> GoBackAsync()
    {
        if (_history.Count == 0)
        {
            return false;
        }

        var target = _history[^1];
        _history.RemoveAt(_history.Count - 1);

        // 回退不再压栈：否则 A 到 B 再后退，会在 A 与 B 之间来回弹
        await NavigateCoreAsync(target, parameter: null, pushHistory: false, CancellationToken.None)
            .ConfigureAwait(true);

        return true;
    }

    private async Task NavigateCoreAsync(
        string route, object? parameter, bool pushHistory, CancellationToken ct)
    {
        if (!_registry.Contains(route))
        {
            _log.LogWarning("未知路由 {Route}，回落到 {Fallback}", route, Routes.Default);
            route = Routes.Default;
        }

        var sameRoute = string.Equals(route, CurrentRoute, StringComparison.Ordinal);
        var alreadyBuilt = _cache.ContainsKey(route);

        if (sameRoute && alreadyBuilt)
        {
            // 重复点当前项等于刷新：不重建、不记历史
            await ActivateAsync(_cache[route], parameter, route, ct).ConfigureAwait(true);
            return;
        }

        // 离开前询问当前页；返回 false 就整个取消
        if (_cache.TryGetValue(CurrentRoute, out var leaving) && leaving is INavigationPage page)
        {
            bool canLeave;
            try
            {
                canLeave = await page.CanNavigateAwayAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // 页面自己的 bug 不能把用户锁死在当前页
                _log.LogWarning(
                    ex, "页面 {Route} 的 CanNavigateAwayAsync 抛异常，按可离开处理", CurrentRoute);
                canLeave = true;
            }

            if (!canLeave)
            {
                return;
            }
        }

        if (pushHistory && alreadyBuilt)
        {
            PushHistory(CurrentRoute);
        }

        var element = GetOrCreate(route);
        CurrentRoute = route;

        ContentChanged?.Invoke(this, element);
        Navigated?.Invoke(this, route);

        await ActivateAsync(element, parameter, route, ct).ConfigureAwait(true);
    }

    private FrameworkElement GetOrCreate(string route)
    {
        if (_cache.TryGetValue(route, out var cached))
        {
            return cached;
        }

        var created = _registry.Create(route);
        _cache[route] = created;
        return created;
    }

    private async Task ActivateAsync(
        FrameworkElement element, object? parameter, string route, CancellationToken ct)
    {
        if (element is not INavigationPage page)
        {
            return;
        }

        try
        {
            await page.OnNavigatedToAsync(parameter, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 正常取消，不记错误
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "页面 {Route} 加载失败", route);
            PageFailed?.Invoke(this, new NavigationFailure(route, ex));
        }
    }

    private void PushHistory(string route)
    {
        if (string.IsNullOrEmpty(route))
        {
            return;
        }

        _history.Add(route);

        if (_history.Count > MaxHistory)
        {
            _history.RemoveAt(0);
        }
    }
}