using System.Windows;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;

namespace EbookToolSuite.App.Navigation;

/// <summary>
/// 路由到页面工厂的注册表。存 Func 而不是 Type：页面的依赖由 DI 显式给出。
/// 不用反射 new，因为构造函数出问题时报错很难懂，而且会和 DI 的生命周期打架。
/// </summary>
public sealed class PageRegistry
{
    private readonly Dictionary<string, Func<FrameworkElement>> _factories = new(StringComparer.Ordinal);

    public void Register(string route, Func<FrameworkElement> factory) => _factories[route] = factory;

    public bool Contains(string route) => _factories.ContainsKey(route);

    public IReadOnlyCollection<string> Routes => _factories.Keys;

    public FrameworkElement Create(string route)
        => _factories.TryGetValue(route, out var factory)
            ? factory()
            : throw new AppException(
                ErrorCode.InternalError,
                "路由未注册：" + route,
                technical: "PageRegistry 里没有 " + route);
}