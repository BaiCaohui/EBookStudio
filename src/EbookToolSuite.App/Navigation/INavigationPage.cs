namespace EbookToolSuite.App.Navigation;

/// <summary>
/// 一个可被导航到的页面要实现这个接口。
/// </summary>
/// <remarks>
/// **构造函数只允许 InitializeComponent()**。页面是懒加载的，构造函数里有 IO / 查库 /
/// 建 WebView 会让第一次点击卡住 UI 线程，也会拖累冷启动不超过 2 秒的硬指标。
/// 重活放 OnNavigatedToAsync。
/// </remarks>
public interface INavigationPage
{
    /// <summary>每次被导航到都会调用（含命中缓存、没有重新构造的情况）。</summary>
    Task OnNavigatedToAsync(object? parameter, CancellationToken ct);

    /// <summary>离开前调用。返回 false 可以阻止离开（例如编辑器有未保存的改动）。</summary>
    Task<bool> CanNavigateAwayAsync();
}