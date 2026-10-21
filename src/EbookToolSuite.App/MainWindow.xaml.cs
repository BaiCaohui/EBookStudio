using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EbookToolSuite.App.Commands;
using EbookToolSuite.App.Interop;
using EbookToolSuite.App.Navigation;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Infrastructure.Logging;
using Serilog;

namespace EbookToolSuite.App;

/// <summary>
/// 主窗口外壳。布局与视觉规格见设计文档 9.9.3 到 9.9.16。
/// </summary>
/// <remarks>
/// 这里是纯外壳：只管左栏、顶栏、内容容器、状态栏，不实现任何业务页面。
/// 页面通过 ShellViewModel.CurrentPage 挂到 PageHost 上。
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>窄窗折叠阈值。只在这一处定义（设计文档 9.9.15）。</summary>
    private const double CollapseThresholdPx = 900;

    private const double PaneExpandedWidth = 240;
    private const double PaneCollapsedWidth = 48;
    private const int CopiedLogLines = 200;

    private readonly IAppPaths _paths;
    private readonly IShellLauncher _shell;
    private readonly IToolLocator _tools;
    private readonly ImportCommand _import;
    private readonly ConvertCommand _convert;
    private readonly ICbzPacker _cbzPacker;
    private readonly ShellViewModel _shellVm;
    private readonly IConfigStore _config;

    /// <summary>T6-4：正文索引进度（状态栏的「索引中 N/M」）。</summary>
    private readonly IContentIndexer? _indexer;

    private bool _showingIndexStatus;

    /// <summary>用户在宽窗下手动折叠/展开过。true 之后宽度跨阈值不再自动改动。</summary>
    private bool _userOverrodePane;

    private bool _busy;

    public MainWindow(
        IAppPaths paths,
        IShellLauncher shell,
        IToolLocator tools,
        ImportCommand import,
        ConvertCommand convert,
        ICbzPacker cbzPacker,
        ShellViewModel shellVm,
        IConfigStore config,
        IContentIndexer? contentIndexer = null)
    {
        _paths = paths;
        _shell = shell;
        _tools = tools;
        _import = import;
        _convert = convert;
        _cbzPacker = cbzPacker;
        _shellVm = shellVm;
        _config = config;
        _indexer = contentIndexer;

        InitializeComponent();

        DataContext = _shellVm;

        // T7-3：BusyScope 的状态栏文案出口（长任务统一显示"正在做什么"）
        BusyScope.StatusSink = text => SetStatus(text is null ? "就绪" : text);

        // 菜单项的可用性跟着"当前选中的书"走（选中态由书库页维护）。
        _convert.CurrentBookChanged += OnCurrentBookChanged;
        ConvertMenuItem.IsEnabled = _convert.CanRun;

        // 焦点环只在键盘导航时出现
        FocusVisuals.Attach(this);

        // 图标字形集中在 NavIcons（码位已实测），这里不写死字符
        HealthIcon.Text = NavIcons.Warning;
        PaneToggleButton.Content = MakeIcon(NavIcons.GlobalNav);
        SettingsButton.Content = MakeIcon(NavIcons.Settings);

        // T6-4：正文索引进度显示在状态栏（后台线程发事件，这里切回 UI 线程）。
        if (_indexer is not null)
        {
            _indexer.ProgressChanged += OnContentIndexProgress;
        }

        SizeChanged += OnSizeChanged;
    }

    // ---------- 生命周期 ----------

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        /* 主窗口首次完成布局 = 执行计划 4.3 说的"到主窗口可交互"。
           T1-7 的冷/暖启动门槛就按这个时间点算（App.StartupWatch 从进程开始计）。 */
        App.MarkStartupComplete();

        // 先把当前页装上（首次导航会构造页面）
        await _shellVm.InitializeAsync().ConfigureAwait(true);

        // 启动路径只查存在性，绝不 spawn 进程（冷启动不超过 2 秒的硬要求）
        await RefreshToolHealthAsync(runProbes: false).ConfigureAwait(true);

        SetStatus("就绪");
    }

    private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        => SavePlacement();

    /// <summary>
    /// 保存窗口位置 / 大小 / 最大化状态 / 最后停留页面。
    /// </summary>
    /// <remarks>
    /// 这里同步等待是刻意的：JsonConfigStore 与 WindowPlacementStore 全程
    /// ConfigureAwait(false) 且不碰 UI，所以不会死锁；换成 async void 一旦中途失败
    /// 就有"关不掉窗口"的风险。保存失败也不能拦着用户关窗，所以吞掉异常。
    /// </remarks>
    private void SavePlacement()
    {
        try
        {
            WindowPlacementStore
                .SaveAsync(this, _config, _shellVm.CurrentRoute)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "保存窗口状态失败（不影响使用）");
        }
    }
    // ---------- 左栏折叠 ----------

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_userOverrodePane)
        {
            return;
        }

        SetPaneCollapsed(e.NewSize.Width < CollapseThresholdPx, animate: true);
    }

    private void OnTogglePaneClick(object sender, RoutedEventArgs e)
    {
        // 用户手动切换过之后，自动折叠逻辑让位
        _userOverrodePane = true;
        SetPaneCollapsed(!_shellVm.IsNavCollapsed, animate: true);
    }

    private void SetPaneCollapsed(bool collapsed, bool animate)
    {
        if (_shellVm.IsNavCollapsed == collapsed)
        {
            return;
        }

        _shellVm.SetNavCollapsed(collapsed);

        var target = collapsed ? PaneCollapsedWidth : PaneExpandedWidth;

        if (!animate)
        {
            NavPane.Width = target;
            return;
        }

        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            // Stop 让动画结束后回落到本地值，避免动画把 Width 永久锁住
            FillBehavior = FillBehavior.Stop,
        };

        NavPane.Width = target;
        NavPane.BeginAnimation(WidthProperty, animation);
    }

    // ---------- 工具健康 ----------

    private async void OnRecheckToolsClick(object sender, RoutedEventArgs e)
    {
        // 手动重检才跑版本探测，并先丢弃缓存
        if (_tools is Engines.Processes.ToolLocator concrete)
        {
            concrete.InvalidateHealthCache();
        }

        await RefreshToolHealthAsync(runProbes: true).ConfigureAwait(true);
    }

    private async Task RefreshToolHealthAsync(bool runProbes)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            SetStatus(runProbes
                ? "正在重新检测工具（会调用各工具的 --version）..."
                : "正在检查工具...");

            var report = await _tools.VerifyAllAsync(runProbes).ConfigureAwait(true);

            if (report.HasBlockingIssue)
            {
                HealthText.Text = "缺少必需工具，相关功能不可用：" + string.Join(
                    "；",
                    report.Blocking.Select(i =>
                        i.DisplayName + "（" + i.State + "）。" + (i.FixHint ?? string.Empty)));

                HealthBar.Visibility = Visibility.Visible;
                SetStatus("有 " + report.Blocking.Count + " 个必需工具不可用。");
            }
            else
            {
                HealthBar.Visibility = Visibility.Collapsed;
                SetStatus(runProbes ? "工具检查通过（含版本探测）。" : "工具检查通过。");
            }

            UpdateHealthIndicator(report);

            Log.Information(
                "工具健康检查完成 runProbes={RunProbes} 阻塞问题={Blocking} 明细={Detail}",
                runProbes,
                report.Blocking.Count,
                string.Join(" | ", report.Items.Select(i => i.Key + ":" + i.State)));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "工具健康检查失败");
            HealthText.Text = "工具检查失败：" + ex.SafeMessage();
            HealthBar.Visibility = Visibility.Visible;
            SetStatus("工具检查失败，详见日志。");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>状态栏右侧的常驻指示灯。绿灯 = 全部必需工具就绪。</summary>
    private void UpdateHealthIndicator(ToolHealthReport report)
    {
        var blocking = report.Blocking;
        var total = report.Items.Count;

        ToolHealthDot.SetResourceReference(
            System.Windows.Shapes.Shape.FillProperty,
            blocking.Count == 0 ? "SuccessBrush" : "DangerBackgroundBrush");

        ToolHealthText.Text = blocking.Count == 0
            ? "工具 " + total + "/" + total
            : blocking[0].DisplayName + " 缺失";

        var lines = report.Items.Select(i =>
            i.DisplayName + "：" + i.State + (i.Version is null ? "" : " " + i.Version));

        ToolHealthDot.ToolTip = string.Join(Environment.NewLine, lines);
        ToolHealthText.ToolTip = ToolHealthDot.ToolTip;
    }

    // ---------- 导入（与书库空状态共用 ImportCommand） ----------

    private async void OnImportClick(object sender, RoutedEventArgs e)
        => await _import.RunAsync(this).ConfigureAwait(true);

    private async void OnImportFolderClick(object sender, RoutedEventArgs e)
        => await _import.RunFolderAsync(this).ConfigureAwait(true);

    private async void OnConvertMenuClick(object sender, RoutedEventArgs e)
        => await _convert.RunAsync(this).ConfigureAwait(true);

    private void OnCurrentBookChanged(object? sender, EventArgs e)
        => ConvertMenuItem.IsEnabled = _convert.CanRun;

    // ---------- 导航 ----------

    /// <summary>T2-3：Ctrl+F 聚焦搜索框（只在书库页）。</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        if (!_shellVm.IsLibraryRoute)
        {
            return;
        }

        SearchBox.Focus();
        SearchBox.SelectAll();
        e.Handled = true;
    }

    /// <summary>Esc 清空并退出聚焦；Enter 立即搜（不等 200 ms 防抖）。</summary>
    private async void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _shellVm.Library.ClearSearch();
            await _shellVm.Library.FlushSearchAsync().ConfigureAwait(true);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            await _shellVm.Library.FlushSearchAsync().ConfigureAwait(true);
            e.Handled = true;
        }
    }

    private async void OnSearchClearClick(object sender, RoutedEventArgs e)
    {
        _shellVm.Library.ClearSearch();
        await _shellVm.Library.FlushSearchAsync().ConfigureAwait(true);
        SearchBox.Focus();
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
        => _shellVm.NavigateCommand.Execute(Routes.Settings);

    private void OnRetryPageClick(object sender, RoutedEventArgs e)
        => _shellVm.RefreshCommand.Execute(null);

    // ---------- 目录与日志 ----------

    private void OnOpenDataDirClick(object sender, RoutedEventArgs e)
        => SafeRun("打开数据目录", () => _shell.OpenFolder(_paths.DataRoot));

    /// <summary>
    /// 把图片文件夹打包成 CBZ。**独立入口**：输入是一个文件夹，不是库里的一本书，
    /// 所以不走转换队列（队列围绕 books / book_formats 建，见 docs/DECISIONS.md）。
    /// </summary>
    /// <remarks>
    /// 流程（用户 2026-10-21 要求）：选文件夹 → **CBZ 信息编辑窗**（填书籍信息、
    /// 调图片顺序、双击预览）→ 另存为 → 按用户排的顺序带 ComicInfo.xml 打包 →
    /// 问「是否导入书库」。
    /// 顺序为什么靠重命名承载：CBZ 没有页序字段，阅读器只按包内文件名自然序排页
    /// （见 docs/DECISIONS.md 2026-10-04 那条），所以编辑窗调序后会把条目名重编号。
    /// </remarks>
    private async void OnPackCbzClick(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要打包的图片文件夹",
            Multiselect = false,
        };

        if (picker.ShowDialog(this) != true || string.IsNullOrWhiteSpace(picker.FolderName))
        {
            return;
        }

        var folder = picker.FolderName;
        var images = _cbzPacker.ScanImages(folder);
        if (images.Count == 0)
        {
            AppDialog.Show(
                this,
                "这个文件夹里没有找到图片。打包 CBZ 支持 jpg / png / webp / bmp / gif。",
                "没有图片",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // 用户点「取消」= 整条流程中止，一个文件都不写。
        var editor = new CbzPackEditWindow(folder, images) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null)
        {
            return;
        }

        var request = editor.Result;

        var save = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存 CBZ",
            Filter = "CBZ 漫画包 (*.cbz)|*.cbz",
            DefaultExt = ".cbz",
            FileName = MakeCbzFileName(request.Metadata.Title, folder),
            InitialDirectory = Directory.GetParent(folder)?.FullName ?? folder,
        };

        if (save.ShowDialog(this) != true || string.IsNullOrWhiteSpace(save.FileName))
        {
            return;
        }

        SetStatus("正在打包 " + request.Entries.Count + " 张图片...");

        // 进度只写状态栏：这个入口是「一次点一下」的工具，不另开进度窗口。
        var progress = new Progress<ConversionProgress>(p =>
        {
            if (p.Percent is { } percent)
            {
                SetStatus("正在打包 " + (int)Math.Round(percent * 100) + "%");
            }
        });

        try
        {
            var result = await _cbzPacker
                .PackAsync(request.Entries, request.Metadata, save.FileName, progress)
                .ConfigureAwait(true);

            SetStatus(result.Summary);

            /* 保存成功后才问"要不要入库"。走 ImportCommand = 和「文件 - 导入文件」
               完全同一条流水线（扫描 / 预检 / 查重 / 进度窗 / import_batch / 完成后刷新书库），
               所以格式检查、去重这些一个都不漏。 */
            var title = string.IsNullOrWhiteSpace(request.Metadata.Title)
                ? Path.GetFileNameWithoutExtension(save.FileName)
                : request.Metadata.Title;

            var import = AppDialog.ConfirmCustom(
                this,
                "已保存到：\n" + save.FileName + "\n\n" + result.Summary + "\n\n是否将《" + title + "》导入书库？",
                "保存完成",
                confirmText: "导入书库",
                cancelText: "取消");

            if (import)
            {
                await _import.RunPathsAsync(this, new[] { save.FileName }).ConfigureAwait(true);
            }
        }
        catch (AppException ex)
        {
            SetStatus("打包失败");
            Log.Warning(ex, "CBZ 打包失败 {Code}", ex.Code);
            AppDialog.Show(this, ex.UserMessage, "打包失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            SetStatus("打包失败");
            Log.Error(ex, "CBZ 打包时发生未预期的异常");
            AppDialog.Show(this, "打包失败：" + ex.SafeMessage(), "打包失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 另存为框的默认文件名：优先用用户在编辑窗填的标题（去掉 Windows 非法字符），
    /// 没填就用文件夹名。.cbz 扩展名由 SaveFileDialog 的 DefaultExt 补。
    /// </summary>
    private static string MakeCbzFileName(string? title, string folder)
    {
        var stem = string.IsNullOrWhiteSpace(title)
            ? new DirectoryInfo(folder).Name
            : title.Trim();

        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            stem = stem.Replace(ch, ' ');
        }

        stem = stem.Trim();
        return (stem.Length == 0 ? "未命名" : stem) + ".cbz";
    }

    private void OnOpenToolsDirClick(object sender, RoutedEventArgs e)
        => SafeRun("打开工具目录", () => _shell.OpenFolder(_paths.ToolsRoot));

    private void OnOpenLogDirClick(object sender, RoutedEventArgs e)
        => SafeRun("打开日志目录", () => _shell.OpenFolder(_paths.LogsDir));

    private void OnCopyLogClick(object sender, RoutedEventArgs e)
        => SafeRun("复制日志", () =>
        {
            // 从内存环形缓冲取，不读文件：文件正被 Serilog 持有，
            // 而且缓冲里也包含还没落盘的那几条（崩溃时最关键的往往正是它们）。
            var lines = LogRingBuffer.Instance.Snapshot(CopiedLogLines);
            if (lines.Count == 0)
            {
                SetStatus("内存缓冲里还没有日志。");
                return;
            }

            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            SetStatus("已复制最近 " + lines.Count + " 行日志到剪贴板。");
        });

    // ---------- 诊断菜单（与诊断页里的按钮作用相同） ----------

    private void OnThrowUiClick(object sender, RoutedEventArgs e)
        => throw new InvalidOperationException("诊断：这是从 UI 线程故意抛出的测试异常。");

    private void OnThrowTaskClick(object sender, RoutedEventArgs e)
    {
        _ = Task.Run(() => throw new InvalidOperationException(
            "诊断：这是故意不观察的测试 Task 异常。"));

        SetStatus("已在后台抛出一个不被观察的异常。等 GC 触发后日志里会有记录（约 10-30 秒）。");
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
        => Application.Current.Shutdown(0);

    // ---------- 辅助 ----------

    private static TextBlock MakeIcon(string glyph)
    {
        var block = new TextBlock { Text = glyph };
        block.SetResourceReference(StyleProperty, "InlineIconStyle");
        return block;
    }

    private void SafeRun(string action, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Action} 失败", action);
            AppDialog.Show(ex.SafeMessage(), action + " 失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ==================== 拖动导入（T7-3） ====================
    //
    // 把电子书拖进主窗口任意位置：整窗盖一层蒙版 + 中间提示「添加到书库」，松手就导入。
    //
    // **和文件选择器完全同一条路**：拖进来的原始路径直接交给 ImportCommand.RunPathsAsync，
    // 走同一个导入队列（扫描 / 预检 / 去重 / 进度窗 / 取消 / import_batch / 完成后刷新书库）。
    // 文件、文件夹都原样传：队列的扫描器本来就会递归展开文件夹，并统计"不支持的格式"，
    // 所以这里不做任何预过滤、也不自己弹窗——那会和选择器产生两套行为。

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        // 有文件拖进来就接受：格式与文件夹都交给导入队列去判定（与选择器一致）
        var accepted = !_import.IsRunning
            && e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };

        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;

        if (_import.IsRunning)
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0)
        {
            return;
        }

        // 原样交给导入队列：同一个 RunCoreAsync 管线（扫描/预检/去重/进度窗/取消/写批次）。
        // 扫描器会递归展开文件夹、统计被跳过的格式，行为与「导入文件 / 导入文件夹」完全一致。
        try
        {
            using (BusyScope.Begin("正在准备导入…"))
            {
                await _import.RunPathsAsync(this, dropped).ConfigureAwait(true);
            }
        }
        finally
        {
            SetStatus("拖放导入已交给导入队列。");
        }
    }

    private void SetStatus(string text) => StatusText.Text = text;

    /// <summary>T6-4：索引进度。索引完成的提示只在自己占着状态栏时清掉，不抢别的消息。</summary>
    private void OnContentIndexProgress(object? sender, ContentIndexProgress progress)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnContentIndexProgress(sender, progress)));
            return;
        }

        if (progress.Running && progress.Total > 0)
        {
            _showingIndexStatus = true;
            StatusText.Text = "正文索引中 " + progress.Done + "/" + progress.Total
                + (progress.Failed > 0 ? "（失败 " + progress.Failed + "）" : string.Empty)
                + (progress.CurrentTitle is null ? string.Empty : "：" + progress.CurrentTitle);
            return;
        }

        if (_showingIndexStatus)
        {
            _showingIndexStatus = false;
            StatusText.Text = "正文索引完成：" + progress.Done + " 本";
        }
    }

    // ---------- 冒烟测试用的只读探针（不改变任何行为） ----------

    /// <summary>左栏是否处于折叠态。</summary>
    internal bool IsPaneCollapsed => _shellVm.IsNavCollapsed;

    /// <summary>左栏当前宽度（折叠动画结束后的落点）。</summary>
    internal double PaneWidth => NavPane.Width;

    /// <summary>冒烟用：折叠按钮是否真的可见且有尺寸（曾经因为它被压成 0 宽而消失）。</summary>
    internal (bool IsVisible, double Width, double Height, bool WindowLoaded) PaneToggleMetrics
        => (PaneToggleButton.IsVisible,
            PaneToggleButton.ActualWidth,
            PaneToggleButton.ActualHeight,
            IsLoaded);

    /// <summary>
    /// 冒烟用：第一个导航项的图标在左栏坐标系里的位置与宽度。
    /// 用来发现"折叠后图标被裁"（图标右边缘超出左栏宽度）。
    /// </summary>
    internal (double IconLeft, double IconWidth, double ItemLeft, double ItemWidth, double PaneWidth)?
        FirstNavIconBounds()
    {
        NavList.UpdateLayout();

        if (NavList.ItemContainerGenerator.ContainerFromIndex(0) is not ListBoxItem item)
        {
            return null;
        }

        item.UpdateLayout();

        if (FindVisualByName(item, "Icon") is not FrameworkElement icon)
        {
            return null;
        }

        var iconOrigin = icon.TransformToAncestor(NavPane).Transform(new Point(0, 0));
        var itemOrigin = item.TransformToAncestor(NavPane).Transform(new Point(0, 0));

        return (iconOrigin.X, icon.ActualWidth, itemOrigin.X, item.ActualWidth, NavPane.ActualWidth);
    }

    /// <summary>
    /// 冒烟用：折叠按钮、细横线、左栏三者在窗口坐标系里的位置关系。
    /// 用来守住"按钮在菜单栏行内、且被一条细横线把它与左栏隔开"这条布局要求。
    /// </summary>
    internal (double BtnTop, double BtnBottom, double BtnLeft, double BtnRight,
        double PaneTop, double PaneLeft, double PaneWidth,
        double SepTop, double SepHeight, double ListTop) HeaderMetrics()
    {
        UpdateLayout();

        var window = (UIElement)this;
        var btn = BtnBox(PaneToggleButton, window);
        var pane = BtnBox(NavPane, window);
        var sep = BtnBox(HeaderSeparator, window);
        var list = BtnBox(NavList, window);

        return (btn.Top, btn.Bottom, btn.Left, btn.Right,
            pane.Top, pane.Left, pane.Width, sep.Top, sep.Height, list.Top);
    }

    private static (double Left, double Top, double Width, double Height, double Bottom, double Right)
        BtnBox(FrameworkElement element, UIElement relativeTo)
    {
        element.UpdateLayout();
        var origin = element.TransformToAncestor(relativeTo).Transform(new Point(0, 0));
        return (origin.X, origin.Y, element.ActualWidth, element.ActualHeight,
            origin.Y + element.ActualHeight, origin.X + element.ActualWidth);
    }

    private static DependencyObject? FindVisualByName(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name)
        {
            return root;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindVisualByName(VisualTreeHelper.GetChild(root, i), name);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>模拟用户点"折叠/展开"，用于验证"手动折叠后不被自动逻辑覆盖"。</summary>
    internal void TogglePaneForSmoke() => OnTogglePaneClick(this, new RoutedEventArgs());
}
