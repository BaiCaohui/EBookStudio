using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using EbookToolSuite.App.Commands;
using EbookToolSuite.App.Navigation;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.App.ViewModels;

/// <summary>
/// 外壳的 ViewModel。规格见设计文档 9.9.11。
/// 手写 INotifyPropertyChanged，不引 MVVM 框架（阶段 1 只有一个 VM，不值得加依赖）。
/// </summary>
public sealed class ShellViewModel : INotifyPropertyChanged
{
    private readonly NavigationService _nav;

    /// <summary>防止"导航改选中项"与"用户改选中项触发导航"互相递归。</summary>
    private bool _syncingSelection;

    private NavigationItem? _selected;
    private FrameworkElement? _currentPage;
    private string _currentTitle = "书库";
    private string? _errorMessage;
    private bool _isNavCollapsed;

    public ShellViewModel(
        NavigationService nav,
        LibraryViewModel library,
        IReadOnlyList<NavigationItem> items,
        ILogger<ShellViewModel> log)
    {
        _nav = nav;
        Items = items;
        Library = library;
        _nav.Configure(items);

        NavigateCommand = new NavigateCommand(nav);
        GoBackCommand = new RelayCommand(_ => { _ = _nav.GoBackAsync(); });
        RefreshCommand = new RelayCommand(_ => { _ = _nav.NavigateAsync(_nav.CurrentRoute); });

        _nav.ContentChanged += (_, page) =>
        {
            CurrentPage = page;
            ErrorMessage = null;
        };

        _nav.Navigated += (_, route) =>
        {
            CurrentTitle = TitleOf(route);
            IsLibraryRoute = route == Routes.Library;
            SyncSelection(route);
            log.LogDebug("导航到 {Route}", route);
        };

        _nav.PageFailed += (_, failure) =>
        {
            // 内联错误面板：不弹模态框，用户还能切到别的页面去
            ErrorMessage = "这个页面加载失败了：" + failure.Exception.SafeMessage();
        };

        SyncSelection(_nav.CurrentRoute);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<NavigationItem> Items { get; }

    /// <summary>书库页的 VM。顶栏搜索框直接绑它（两侧必须是同一个实例）。</summary>
    public LibraryViewModel Library { get; }

    private bool _isLibraryRoute;

    /// <summary>当前是不是停在书库页。顶栏的搜索框只在书库页出现。</summary>
    public bool IsLibraryRoute
    {
        get => _isLibraryRoute;
        private set => Set(ref _isLibraryRoute, value);
    }

    /// <summary>当前停留的路由。关窗时写进配置，下次启动直接回到这一页。</summary>
    public string CurrentRoute => _nav.CurrentRoute;

    public ICommand NavigateCommand { get; }

    public ICommand GoBackCommand { get; }

    public ICommand RefreshCommand { get; }

    public FrameworkElement? CurrentPage
    {
        get => _currentPage;
        private set => Set(ref _currentPage, value);
    }

    public string CurrentTitle
    {
        get => _currentTitle;
        private set => Set(ref _currentTitle, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (Set(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _errorMessage is not null;

    public bool IsNavCollapsed
    {
        get => _isNavCollapsed;
        private set => Set(ref _isNavCollapsed, value);
    }

    public NavigationItem? SelectedItem
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value) || _syncingSelection || value is null)
            {
                return;
            }

            // 用户在左栏点了。异步导航由 NavigationService 内部兜住所有异常，
            // 所以这里 fire-and-forget 是安全的（绑定 setter 不能是 async）。
            _ = _nav.NavigateAsync(value.Route);
        }
    }

    /// <summary>
    /// 启动时把初始路由改成"上次停留的页面"。只接受当前左栏里可见的路由：
    /// 路由注册表里六个页面永远都在，可见性只有 NavigationCatalog 一个判断点，
    /// 所以上次停在诊断页、而这次诊断页隐藏时必须落回书库（设计文档 9.10.5）。
    /// </summary>
    public bool SetInitialRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route) || Items.All(i => i.Route != route))
        {
            return false;
        }

        return _nav.SetInitialRoute(route);
    }

    /// <summary>首次显示时调用一次，把初始路由激活（触发第一次页面构造）。</summary>
    public Task InitializeAsync() => _nav.NavigateAsync(_nav.CurrentRoute);

    public void SetNavCollapsed(bool collapsed) => IsNavCollapsed = collapsed;

    private void SyncSelection(string route)
    {
        var match = Items.FirstOrDefault(i => i.Route == route);
        if (match is null)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            SelectedItem = match;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private string TitleOf(string route)
        => Items.FirstOrDefault(i => i.Route == route)?.Title ?? "书库";

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}