using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EbookToolSuite.App.Commands;
using EbookToolSuite.App.Navigation;
using EbookToolSuite.App.Settings;
using EbookToolSuite.App.Views;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Serilog;

namespace EbookToolSuite.App.Views.Pages;

/// <summary>
/// 书库页：封面网格 + 书籍详情浮层。规格见执行计划 T1-3。
/// </summary>
/// <remarks>
/// 详情做成页内浮层而不是单独一个页面/窗口：返回时网格的滚动位置天然保持，
/// 不用特地去恢复 ScrollViewer 的偏移量。
/// </remarks>
public partial class LibraryPage : UserControl, INavigationPage
{
    private readonly LibraryViewModel _vm;
    private readonly ImportCommand _import;
    private readonly ConvertCommand _convert;
    private readonly ReadCommand _read;
    private readonly EditCommand _edit;
    private readonly DuplicateScanViewModel _duplicateScan;
    private readonly IConversionQueue _queue;
    private readonly IConversionService _conversionService;
    private readonly IConfigStore _config;
    private readonly Button _importButton;
    private readonly StackPanel _emptyActions;
    private readonly Button _scanFolderButton;
    private readonly Button _emptyActionButton;

public LibraryPage(
        LibraryViewModel vm,
        ImportCommand import,
        ConvertCommand convert,
        ReadCommand read,
        EditCommand edit,
        DuplicateScanViewModel duplicateScan,
        IConversionQueue queue,
        IConversionService conversionService,
        IConfigStore config)
    {
        _vm = vm;
        _import = import;
        _convert = convert;
        _read = read;
        _edit = edit;
        _duplicateScan = duplicateScan;
        _queue = queue;
        _conversionService = conversionService;
        _config = config;

        InitializeComponent();

        DataContext = vm;

        _importButton = new Button
        {
            Content = "导入 EPUB...",
            Height = 34,
            Padding = new Thickness(16, 0, 16, 0),
        };

        _importButton.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        _importButton.Click += OnImportClick;

        /* T2-3：空状态有三种（库是空的 / 搜索无结果 / 筛选后为空），
           共用一个 EmptyState 控件，文案与按钮由 UpdateEmptyState 按 VM 的状态换。 */
        _scanFolderButton = new Button
        {
            Content = "扫描文件夹",
            Height = 34,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(16, 0, 16, 0),
        };

        _scanFolderButton.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        _scanFolderButton.Click += OnScanFolderClick;

        _emptyActionButton = new Button
        {
            Height = 34,
            Padding = new Thickness(16, 0, 16, 0),
        };

        _emptyActionButton.SetResourceReference(StyleProperty, "PrimaryButtonStyle");

        _emptyActions = new StackPanel { Orientation = Orientation.Horizontal };

        UpdateEmptyState();

        // 从菜单导入也要刷新网格：两处入口共用同一个 ImportCommand
        _import.Completed += OnImportCompleted;

        // 入队成功后先刷一次（这时还没产物）；真正跑完了由队列事件再刷一次。
        _convert.Enqueued += OnConvertEnqueued;
        _queue.TaskChanged += OnConversionTaskChanged;

        // 阅读窗口关掉后网格上的进度角标要自己跟上（用户要求：关窗即刷新）。
        _read.Closed += OnReaderClosed;

        // T6-4：全文搜索结果里点一行 → 打开那本书的那一章并高亮。
        _vm.FullText.OpenRequested += OnFullTextOpenRequested;

        // 编辑器保存成功后刷新这张卡片（刷新卡片缓存 + 重新解码封面）。
        _edit.Saved += OnEditSaved;

        // 工具栏「转换」按钮的可用性跟着选中状态走。
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        // 网格宽度变了就重算每行几列（虚拟化的行是按列数预先折好的）。
        BookList.SizeChanged += OnGridSizeChanged;
    }

    public async Task OnNavigatedToAsync(object? parameter, CancellationToken ct)
    {
        // T7-4：每次进书库都按配置刷一次封面尺寸（设置页改完回来立刻生效）。
        ApplyCoverSize();
        SyncImportButtons();
        await _vm.LoadAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// T7-4：封面尺寸三档（设置 → 书库）。默认档与 T1-3 完全一致（150 × 200），
    /// 通过改写两个 double 资源让 BookCardStyle / 封面区 Grid 的 DynamicResource 自动重算。
    /// 列表视图的行不用这两个资源，所以不受影响。
    /// </summary>
    private void ApplyCoverSize()
    {
        var size = _config.GetString(AppSettingsKeys.CoverSize, null);

        Resources["LibraryCardWidth"] = AppSettingsKeys.CardWidth(size);
        Resources["LibraryCoverHeight"] = AppSettingsKeys.CoverHeight(size);
    }

    // ---------- T6-4：全库正文搜索 ----------

    private void OnFullTextSearchClick(object sender, RoutedEventArgs e)
    {
        _vm.FullText.Open(_vm.SearchText);
        FullTextSearchBox.Focus();
    }

    private async void OnFullTextSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _vm.FullText.FlushSearchAsync().ConfigureAwait(true);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _vm.FullText.Close();
        }
    }

    /// <summary>
    /// 点命中：打开阅读窗口，走到那一章（PDF/漫画是那一页），并把查询词带过去当高亮词。
    /// </summary>
    private void OnFullTextOpenRequested(Guid bookId, int spineIndex, string query)
    {
        var owner = Window.GetWindow(this) ?? Application.Current?.MainWindow;

        if (owner is null)
        {
            return;
        }

        try
        {
            /* 只需要 Book.Id：ReadCommand 只用 Id 交给 ReaderWindow，自己会去库/源文件取正文。
               startIndex 给 EPUB/TXT 用，startPage 给 PDF/漫画用——两个都填同一个值，
               各取所需（对方那个会被忽略）。 */
            _read.Run(
                owner,
                new Book { Id = bookId },
                startIndex: spineIndex,
                startPage: spineIndex,
                findText: query);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "从全文搜索打开阅读窗口失败 {BookId}", bookId);
        }
    }

    /// <summary>只给冒烟脚本用的只读入口，不改变任何行为。</summary>
    internal LibraryViewModel ViewModel => _vm;

    public Task<bool> CanNavigateAwayAsync() => Task.FromResult(true);

    /// <summary>
    /// 「刷新」按钮（T2-6）：先扫一遍文件状态，再重新查询。
    /// 光重新查询是看不出"文件被删了/被改了"的  那些信息只存在磁盘上。
    /// </summary>
    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.RefreshWithScanAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "刷新书库失败");
        }
    }

    /// <summary>T2-3 补丁八：网格 / 列表视图切换。落盘失败在 VM 内部记警告，不打扰用户。</summary>
    private async void OnToggleViewClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.ToggleViewAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "切换书库视图失败");
        }
    }

    /// <summary>
    /// T2-3 补丁八：列表视图的表头点击排序。映射到筛选栏那一套排序口径
    /// （改的是同一个 SelectedSort，所以两个入口天然一致）。
    /// 「书名」再点一次在 A-Z / Z-A 之间翻向，对应排序下拉里的两项。
    /// </summary>
    private void OnSortHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        var current = _vm.SelectedSort;
        SortOption? target = null;

        foreach (var option in SortOption.AllOptions)
        {
            var match = tag switch
            {
                "Title" => option.Sort == BookSort.Title
                           && (current.Sort == BookSort.Title
                               ? option.Descending != current.Descending
                               : !option.Descending),
                "Author" => option.Sort == BookSort.Author,
                "FileSize" => option.Sort == BookSort.FileSize,
                "AddedAt" => option.Sort == BookSort.AddedAt,
                _ => false,
            };

            if (match)
            {
                target = option;
                break;
            }
        }

        if (target is not null)
        {
            _vm.SelectedSort = target;
        }
    }

    /// <summary>T2-2：打开重复检测窗口。合并之后副本会从网格消失，所以关窗后重新加载。</summary>
    private async void OnDuplicateScanClick(object sender, RoutedEventArgs e)
    {
        var window = new DuplicateScanWindow(_duplicateScan)
        {
            Owner = Window.GetWindow(this),
        };

        window.ShowDialog();

        await ReloadAsync();
    }


    private async void OnRetryClick(object sender, RoutedEventArgs e)
        => await ReloadAsync().ConfigureAwait(true);

    /// <summary>
    /// 详情浮层里的「接受改动」（T2-6）：把库里记的体积、修改时间与指纹更新成磁盘现状。
    /// **不重新解析元数据**（那属于元数据编辑器 T3-2），所以按钮叫"接受改动"。
    /// </summary>
    private async void OnAcceptChangeClick(object sender, RoutedEventArgs e)
    {
        if (_vm.DetailBook is not { } card)
        {
            return;
        }

        DetailAcceptChangeButton.IsEnabled = false;

        try
        {
            var changed = await _vm.AcceptModificationAsync(card).ConfigureAwait(true);

            if (!changed)
            {
                AppDialog.Show(
                    Window.GetWindow(this),
                    "这个文件的状态没有变化，不需要接受。",
                    "接受改动",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "接受文件改动失败");
        }
        finally
        {
            DetailAcceptChangeButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 用户 2026-10-20：详情里某个格式文件已被删掉时，行尾的「移除该格式」。
    /// 从库里删掉这条格式记录（不删文件，文件早就不在了），主格式/徽标/大小切到剩下的格式，
    /// 阅读进度与阅读状态不受影响。成功时**不弹窗**：行消失 + 提示消失就是反馈。
    /// </summary>
    private async void OnRemoveFormatClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not MetadataRow { FormatKind: { } format })
        {
            return;
        }

        if (_vm.DetailBook is not { } card)
        {
            return;
        }

        button.IsEnabled = false;

        try
        {
            var removed = await _vm.RemoveFormatAsync(card, format).ConfigureAwait(true);

            if (!removed)
            {
                AppDialog.Show(
                    Window.GetWindow(this),
                    "这个格式已经不在书库里了，可能刚被移除过。",
                    "移除该格式",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "移除格式失败 {BookId} {Format}", card.Book.Id, format);
        }
        finally
        {
            // 成功后这一行已经随 Metadata 重建消失，这里恢复一下也无害（按钮对象会被回收）
            button.IsEnabled = true;
        }
    }
    /// <summary>
    /// 用户 2026-10-21：详情里把某个格式设为「当前阅读格式」（行尾的「切换此格式」）。
    /// 切换后点「阅读」打开的就是它；阅读进度由阅读器按"进度来源格式"换算：
    /// PDF / 漫画按已读百分比模糊定位，EPUB / TXT / MOBI·AZW3 按文字指纹精确找位。
    /// 成功时不弹窗：底部那行小字与哪一行还有按钮，本身就是反馈。
    /// </summary>
    private async void OnSwitchFormatClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not MetadataRow { FormatKind: { } format })
        {
            return;
        }

        if (_vm.DetailBook is not { } card)
        {
            return;
        }

        button.IsEnabled = false;

        try
        {
            if (!await _vm.SetReadingFormatAsync(card, format).ConfigureAwait(true))
            {
                AppDialog.Show(
                    Window.GetWindow(this),
                    "切换阅读格式失败，详情见日志。",
                    "切换此格式",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            // 成功后 Metadata 整表重建、这一行会被换掉；对旧按钮对象恢复一下无害
            button.IsEnabled = true;
        }
    }

    private async Task ReloadAsync()
    {
        try
        {
            await _vm.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "刷新书库失败");
        }
    }

    // ---------- 工具栏导入入口：与「文件」菜单、空状态按钮共用同一个 ImportCommand ----------

    /// <summary>工具栏「导入文件」：多选文件，走与菜单完全相同的导入队列。</summary>
    private async void OnToolbarImportFileClick(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (owner is null)
        {
            return;
        }

        // T7-3：先置灰再 await（以前是 await 之后才 Sync，导入期间按钮看着还能点）
        SetImportButtonsEnabled(false);
        try
        {
            await _import.RunAsync(owner).ConfigureAwait(true);
        }
        finally
        {
            SyncImportButtons();
        }
    }

    /// <summary>工具栏「导入文件夹」：递归扫描文件夹，同样共用菜单那条路。</summary>
    private async void OnToolbarImportFolderClick(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (owner is null)
        {
            return;
        }

        SetImportButtonsEnabled(false);
        try
        {
            await _import.RunFolderAsync(owner).ConfigureAwait(true);
        }
        finally
        {
            SyncImportButtons();
        }
    }

    /// <summary>导入期间把三个导入入口一起置灰（ImportCommand 自己也有防重入）。</summary>
    private void SyncImportButtons() => SetImportButtonsEnabled(!_import.IsRunning);

    private void SetImportButtonsEnabled(bool enabled)
    {
        ToolbarImportFileButton.IsEnabled = enabled;
        ToolbarImportFolderButton.IsEnabled = enabled;
        _importButton.IsEnabled = enabled;
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (owner is null)
        {
            return;
        }

        SetImportButtonsEnabled(false);
        try
        {
            await _import.RunAsync(owner).ConfigureAwait(true);
        }
        finally
        {
            SyncImportButtons();
        }
    }

    // 拖放统一由主窗口接管（整窗蒙版 + 导入），页面这一层不再重复处理。

    /// <summary>
    /// 转换入口的统一实现。三个入口（工具栏按钮、卡片右键菜单、详情浮层按钮）都走这里，
    /// 作用对象是当前选中的书；没有选中时退回"详情浮层里那本"。
    /// </summary>
    private async void OnConvertClick(object sender, RoutedEventArgs e)
        => await RunConvertAsync(_vm.SelectedBook ?? _vm.DetailBook).ConfigureAwait(true);

    /// <summary>
    /// 阅读详情浮层里那本。工具栏与菜单没有阅读入口，只此一处，
    /// 所以作用对象优先取详情浮层当前这本。
    /// </summary>
    private void OnReadClick(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var card = _vm.DetailBook ?? _vm.SelectedBook;

        if (owner is null || card is null)
        {
            return;
        }

        _read.Run(owner, card.Book);
    }

    /// <summary>
    /// 编辑入口的统一实现（阶段 3）：工具栏、卡片右键菜单、详情浮层三处都走这里。
    /// 作用对象与「转换」一致：优先当前选中的书，没有就退回详情浮层里那本。
    /// </summary>
    private void OnEditClick(object sender, RoutedEventArgs e)
        => RunEdit(_vm.SelectedBook ?? _vm.DetailBook);

    private void OnContextEditClick(object sender, RoutedEventArgs e)
    {
        // 右键某张卡片时先把选中态挪过去，菜单的作用对象才不会和界面对不上。
        var card = (sender as MenuItem)?.DataContext as BookCardViewModel;
        if (card is not null)
        {
            _vm.Select(card);
        }

        RunEdit(card);
    }

    private void RunEdit(BookCardViewModel? card)
    {
        var owner = Window.GetWindow(this);
        if (owner is null || card is null)
        {
            return;
        }

        _edit.Run(owner, card.Book);
    }

    /// <summary>
    /// 编辑器保存成功后：把卡片上的文件信息重读一遍，再重新解码封面。
    /// 保存时封面缓存文件被重写过，卡片不重新解码的话 WPF 的图片缓存会继续显示旧图。
    /// </summary>
    private async void OnEditSaved(Guid bookId)
    {
        await _vm.RefreshBookByIdAsync(bookId).ConfigureAwait(true);
        await _vm.RefreshBookCoverAsync(bookId).ConfigureAwait(true);
    }

    private async void OnContextConvertClick(object sender, RoutedEventArgs e)
    {
        // 右键某张卡片时先把选中态挪过去，按钮与菜单的作用对象才不会对不上。
        var card = (sender as MenuItem)?.DataContext as BookCardViewModel;
        if (card is not null)
        {
            _vm.Select(card);
        }

        await RunConvertAsync(card).ConfigureAwait(true);
    }

    private void OnContextDetailClick(object sender, RoutedEventArgs e)
    {
        var card = (sender as MenuItem)?.DataContext as BookCardViewModel;
        if (card is not null)
        {
            _vm.OpenDetail(card);
        }
    }

    private async Task RunConvertAsync(BookCardViewModel? card)
    {
        var owner = Window.GetWindow(this);
        if (owner is null || card is null)
        {
            return;
        }

        await _convert.RunAsync(owner, card.Book).ConfigureAwait(true);
    }

    /// <summary>选中状态变了就同步工具栏按钮的可用性。</summary>
    /// <summary>卡片右键菜单「标记为 -> X」：手动改阅读状态（自动判定只能识别"读到末尾"）。</summary>
    private async void OnMarkStatusClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.DataContext is not BookCardViewModel card)
        {
            return;
        }

        var status = item.Tag switch
        {
            "Reading" => ReadingStatus.Reading,
            "Finished" => ReadingStatus.Finished,
            "Abandoned" => ReadingStatus.Abandoned,
            _ => ReadingStatus.Unread,
        };

        await _vm.MarkStatusAsync(card, status).ConfigureAwait(true);
    }

    /// <summary>T2-3：把筛选条件恢复成"全部"，关键词保留。</summary>
    private void OnResetFiltersClick(object sender, RoutedEventArgs e) => _vm.ResetFilters();

    /// <summary>
    /// 卡片被实现出来时才解码封面。虚拟化下只有屏幕上那几行会触发 
    /// 否则 1000 本首屏会一口气解码 1000 张图，首屏门槛必挂。
    /// LoadCoverAsync 自己是幂等的（已解码就早退）。
    /// </summary>
    /// <summary>右键菜单的共享实例（每张卡片内联一份会让快滚卡顿，见 XAML 里的说明）。</summary>
    private ContextMenu? _cardMenu;

    /// <summary>给冒烟用：拿到那份共享菜单（不打开）。</summary>
    internal ContextMenu CardContextMenu => _cardMenu ??= (ContextMenu)Resources["CardContextMenu"];

    /// <summary>
    /// 打开某张卡片的右键菜单。**这是唯一的打开路径**（网格与列表共用一份实例），
    /// 冒烟也必须走这条路径，否则测的不是用户走的那条。
    /// </summary>
    internal ContextMenu OpenCardMenu(BookCardViewModel card, FrameworkElement? target)
    {
        var menu = CardContextMenu;
        menu.DataContext = card;
        menu.PlacementTarget = target;
        menu.IsOpen = true;

        return menu;
    }

    /// <summary>
    /// 右键某张卡片：把共享菜单的 DataContext 换成它再打开。
    /// 空白处右键、或者找不到卡片时直接吞掉（不弹菜单）。
    /// </summary>
    private void OnCardContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var card = FindCardFrom(e.OriginalSource as DependencyObject);

        if (card is null)
        {
            e.Handled = true;
            return;
        }

        OpenCardMenu(card, e.OriginalSource as FrameworkElement);
        e.Handled = true;
    }

    /// <summary>给冒烟用：验证右键时「从被点元素往上找卡片」那段树遍历。</summary>
    internal BookCardViewModel? FindCardForSmoke(DependencyObject? source) => FindCardFrom(source);

    /// <summary>从被点的元素往上找，直到找到 DataContext 是卡片的那一层。</summary>
    private static BookCardViewModel? FindCardFrom(DependencyObject? source)
    {
        var node = source;

        while (node is not null)
        {
            if (node is FrameworkElement { DataContext: BookCardViewModel card })
            {
                return card;
            }

            // FrameworkElement 本身就是 Visual，所以上面那支已经覆盖了绝大多数情况；
            // Run / Hyperlink 这类不是 Visual，退回逻辑树再试一次。
            if (node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
            {
                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
                continue;
            }

            node = node is FrameworkElement element
                ? LogicalTreeHelper.GetParent(element)
                : null;
        }

        return null;
    }
    private void OnCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is BookCardViewModel card)
        {
            _ = card.LoadCoverAsync();
        }
    }

    /// <summary>网格宽度变了：告诉 VM 重算每行几列。</summary>
    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
        => _vm.SetViewportWidth(e.NewSize.Width);

    private async void OnScanFolderClick(object sender, RoutedEventArgs e)
        => await _import.RunFolderAsync(Window.GetWindow(this));

    /// <summary>三种空状态共用一个控件，文案与按钮由 VM 决定。</summary>
    private void UpdateEmptyState()
    {
        // 搜不到/筛空时用放大镜图标，库真空时用书库图标
        State.IconGlyph = _vm.HasEmptyAction ? "\uE721" : NavIcons.Library;
        State.Title = _vm.EmptyTitle;
        State.Body = _vm.EmptyBody;
        State.Badge = _vm.EmptyBadge;

        _emptyActions.Children.Clear();

        if (_vm.HasEmptyAction)
        {
            _emptyActionButton.Content = _vm.EmptyActionText;
            _emptyActionButton.Command = _vm.EmptyActionCommand;
            _emptyActions.Children.Add(_emptyActionButton);
        }
        else
        {
            _emptyActions.Children.Add(_importButton);
            _emptyActions.Children.Add(_scanFolderButton);
        }

        State.Extra = _emptyActions;
    }


    /// <summary>选中 / 勾选 / 删除状态变了就同步工具栏按钮的可用性。</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(LibraryViewModel.HasSelection))
        {
            ToolbarConvertButton.IsEnabled = _vm.HasSelection;
            ToolbarEditButton.IsEnabled = _vm.HasSelection;

            // 同步给共享的转换命令：主窗口菜单项也靠它决定可用性。
            _convert.SetCurrentBook(_vm.SelectedBook?.Book);
        }

        // 勾选数 / 删除中 / 批量模式都会影响按钮可用性，统一在这里刷新（幂等，开销可忽略）。
        UpdateToolbarState();

        // 空状态文案（库空 / 搜不到 / 筛空）也要跟着换。
        if (e.PropertyName is null or nameof(LibraryViewModel.EmptyTitle) or nameof(LibraryViewModel.HasEmptyAction))
        {
            UpdateEmptyState();
        }
    }

    /// <summary>删除期间把会引发下一次操作的按钮全禁掉，避免连点造成两个删除同时在跑。</summary>
    private void UpdateToolbarState()
    {
        var busy = _vm.IsDeleting;

        DeleteSelectedButton.IsEnabled = _vm.HasChecked && !busy;
        BatchConvertButton.IsEnabled = _vm.HasChecked && !busy;
        SelectAllButton.IsEnabled = !busy;
        ClearCheckedButton.IsEnabled = _vm.HasChecked && !busy;
        ToggleSelectionButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        DetailDeleteButton.IsEnabled = !busy;
    }

    private async void OnConvertEnqueued(object? sender, EventArgs e)
    {
        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 转换队列里有任务进终态了。成功的那种要在卡片上多出格式角标，所以刷一下这一本。
    /// 事件是后台线程发的，必须切回 UI 线程再动界面。
    /// </summary>
    private void OnConversionTaskChanged(ConversionTask task)
    {
        if (!task.IsTerminal || task.BookId is not { } bookId)
        {
            return;
        }

        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await _vm.RefreshBookByIdAsync(bookId).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "转换结束后刷新书库卡片失败");
            }
        });
    }
    private async void OnImportCompleted(object? sender, EventArgs e)
    {
        SyncImportButtons();
        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>阅读窗口关掉之后刷新那本书的进度角标。只重读这一本，不整页重载。</summary>
    private async void OnReaderClosed(Guid bookId)
    {
        try
        {
            await _vm.RefreshBookByIdAsync(bookId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 刷新失败不该影响别的窗口；下次进书库页还会重读
            Log.Warning(ex, "关掉阅读窗口后刷新卡片失败");
        }
    }

    // ---------- 删除（T1-3 补丁需求） ----------

    private void OnToggleSelectionModeClick(object sender, RoutedEventArgs e)
        => _vm.ToggleSelectionMode();

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
        => _vm.CheckAll();

    private void OnClearCheckedClick(object sender, RoutedEventArgs e)
        => _vm.ClearChecked();

    /// <summary>删「勾选的那些」。</summary>
    private async void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
        => await DeleteAsync(_vm.Books.Where(b => b.IsChecked).ToList()).ConfigureAwait(true);

    /// <summary>右键某张卡片删这一本。</summary>
    private async void OnContextDeleteClick(object sender, RoutedEventArgs e)
    {
        var card = (sender as MenuItem)?.DataContext as BookCardViewModel;

        // 批量模式下右键菜单也点得到，那时「删一本」会和勾选语义打架，直接不理。
        if (card is null || _vm.IsSelectionMode)
        {
            return;
        }

        await DeleteAsync(new[] { card }).ConfigureAwait(true);
    }

    /// <summary>详情浮层里删当前这本。删完浮层会自己关掉（LoadAsync 把 DetailBook 置空）。</summary>
    private async void OnDetailDeleteClick(object sender, RoutedEventArgs e)
    {
        var card = _vm.DetailBook;
        if (card is null)
        {
            return;
        }

        await DeleteAsync(new[] { card }).ConfigureAwait(true);
    }

    /// <summary>
    /// 把「勾选的那些」一次排进转换队列。逐本入队：已经有该格式、或这个格式转不出来的按
    /// 跳过计数，其它失败按失败计数，不因为某一本出问题就让整批停下来。
    /// </summary>
    private async void OnBatchConvertClick(object sender, RoutedEventArgs e)
        => await BatchConvertAsync(_vm.Books.Where(b => b.IsChecked).ToList()).ConfigureAwait(true);

    private async Task BatchConvertAsync(IReadOnlyList<BookCardViewModel> cards)
    {
        if (cards.Count == 0)
        {
            return;
        }
        var owner = Window.GetWindow(this);
        var books = cards.Select(c => c.Book).ToList();

        var window = new BatchConvertWindow(_conversionService, books) { Owner = owner };
        if (window.ShowDialog() != true)
        {
            return;
        }

        var target = window.SelectedTarget;
        var templateId = window.SelectedTemplateId;
        var enqueued = 0;
        var skipped = 0;
        var failed = 0;
        string? firstError = null;

        foreach (var book in books)
        {
            try
            {
                await _queue.EnqueueAsync(new ConversionRequest
                {
                    BookId = book.Id,
                    TargetFormat = target,
                    TemplateId = templateId,
                }).ConfigureAwait(true);

                enqueued++;
            }
            catch (AppException ex) when (ex.Code == ErrorCode.ConversionOutputExists
                || ex.Code == ErrorCode.ConversionUnsupported)
            {
                skipped++;
            }
            catch (AppException ex)
            {
                failed++;
                firstError ??= ex.UserMessage;
                Log.Warning(ex, "批量转换：这本书排不进去 {Book}", book.Title);
            }
            catch (Exception ex)
            {
                failed++;
                firstError ??= ex.SafeMessage();
                Log.Error(ex, "批量转换：入队时发生未预期的异常 {Book}", book.Title);
            }
        }

        var summary = "目标格式：" + target.ToStorageString()
            + Environment.NewLine + "已排入队列：" + enqueued + " 本";

        if (skipped > 0)
        {
            summary += Environment.NewLine + "已跳过：" + skipped + " 本（已有该格式，或转不出来）";
        }

        if (failed > 0)
        {
            summary += Environment.NewLine + "失败：" + failed + " 本";
        }

        if (firstError is not null)
        {
            summary += Environment.NewLine + Environment.NewLine + "第一条失败原因：" + firstError;
        }

        summary += Environment.NewLine + Environment.NewLine + "进度与结果在左侧「转换队列」页查看。";

        AppDialog.Show(owner, summary, "批量转换", MessageBoxButton.OK,
            failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

        UpdateToolbarState();
    }

    private async Task DeleteAsync(IReadOnlyList<BookCardViewModel> cards)
    {
        if (cards.Count == 0)
        {
            return;
        }

        await _vm.DeleteAsync(cards).ConfigureAwait(true);
        UpdateToolbarState();
    }
}