using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EbookToolSuite.App.Navigation;
using EbookToolSuite.App.Services;
using EbookToolSuite.App.Settings;
using EbookToolSuite.App.Themes;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Interfaces;
using Serilog;

namespace EbookToolSuite.App.Views.Pages;

/// <summary>
/// 设置页（T7-4）。九个分组，全部改动**立即写 Config.json**，没有"保存"按钮。
///
/// 布局与命名约定：
/// - 每个分组一张卡片，标题 TextBlock 都起名（AppearanceGroupTitle / GeneralGroupTitle / …），
///   冒烟脚本按名字找标题，确认九个分组都在；
/// - 数值输入框（预加载页数、字号、行高、并发、超时、缓存 / 备份上限）能解析就夹到范围并立刻落盘，
///   空 / 中间态不落盘；失焦或回车时解析不了就回填最后一次有效值；
/// - 主题六档仍是 T7-3 的独立「外观」组（米黄与自定义是用户 2026-10-21 追加的），这里不重复做。
/// </summary>
public partial class SettingsPage : UserControl, INavigationPage
{
    private readonly IAppPaths _paths;
    private readonly IConfigStore _config;
    private readonly IShellLauncher _shell;
    private readonly IDataDirectoryMigrationService _migration;
    private readonly IAppMaintenanceService _maintenance;
    private readonly ILibraryWatcher _watcher;
    private readonly LibraryViewModel _library;
    private readonly IToolLocator _tools;
    private readonly NavigationService _navigation;

    /// <summary>整数输入框的绑定表（Tag -> 规则）。省掉六个几乎相同的 TextChanged 处理器。</summary>
    private readonly Dictionary<string, IntBinding> _intBindings = new(StringComparer.Ordinal);

    /// <summary>程序化改控件值时的重入保护（TextChanged / SelectionChanged / Checked 都会被自己触发）。</summary>
    private bool _syncing;

    /// <summary>预加载页数最后一次有效值：框里是空 / 非法时按它回填。</summary>
    private int _lastPreloadPages = AppSettingsKeys.DefaultPreloadPages;

    /// <summary>书库 ViewModel 的属性订阅只挂一次（Loaded / Unloaded 会反复触发）。</summary>
    private bool _librarySubscribed;

    private const string DefaultFontFamily = "Microsoft YaHei UI";

    private const int DefaultFontSize = 17;

    private const double DefaultLineHeight = 1.75;

    private const int DefaultConversionTimeoutMinutes = 60;

    public SettingsPage(
        IAppPaths paths,
        IConfigStore config,
        IShellLauncher shell,
        IDataDirectoryMigrationService migration,
        IAppMaintenanceService maintenance,
        ILibraryWatcher watcher,
        LibraryViewModel library,
        IToolLocator tools,
        NavigationService navigation)
    {
        InitializeComponent();

        _paths = paths;
        _config = config;
        _shell = shell;
        _migration = migration;
        _maintenance = maintenance;
        _watcher = watcher;
        _library = library;
        _tools = tools;
        _navigation = navigation;

        RegisterIntBindings();
        InitializeFontFamilyOptions();

        foreach (var radio in new[]
                 {
                     ThemeSystemRadio, ThemeLightRadio, ThemeSepiaRadio,
                     ThemeDarkRadio, ThemeGreenRadio, ThemeCustomRadio,
                 })
        {
            radio.Checked += OnThemeRadioChecked;
        }

        BuildColorPalette();

        AccentFollowCheck.Checked += OnAccentFollowChanged;
        AccentFollowCheck.Unchecked += OnAccentFollowChanged;

        // 日志级别的候选值集中定义在 AppSettingsKeys，避免设置页与启动日志配置漂移。
        LogLevelCombo.ItemsSource = AppSettingsKeys.LogLevels;

        // 页面是懒加载 + 缓存的：离开时退订，回来时重订，避免重复订阅
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        SyncFromTheme();
        SyncFromConfig();
        RefreshDataDir();
        RefreshCachePath();
    }

    public Task OnNavigatedToAsync(object? parameter, CancellationToken ct)
    {
        SyncFromTheme();
        SyncFromConfig();
        RefreshDataDir();
        RefreshCachePath();
        StopWatcherIfAutoScanDisabled();

        // 书库视图要与书库工具栏双向同步：冒烟会直接构造页面（不触发 Loaded），
        // 所以订阅放在这里，Loaded 里再补一次幂等调用。
        SubscribeLibraryViewModel();

        // 异步刷新项一律自己吞异常写进界面：进设置页不许弹模态框。
        _ = RefreshCacheUsageAsync();
        _ = RefreshToolRowsAsync(runProbes: false);

        return Task.CompletedTask;
    }

    public Task<bool> CanNavigateAwayAsync() => Task.FromResult(true);

    // ==================== 外观（T7-3 保留） ====================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.Changed += OnThemeChanged;

        SubscribeLibraryViewModel();

        SyncFromTheme();
        SyncLibraryViewFromViewModel();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.Changed -= OnThemeChanged;

        if (_librarySubscribed)
        {
            _library.PropertyChanged -= OnLibraryPropertyChanged;
            _librarySubscribed = false;
        }
    }

    /// <summary>订阅书库 ViewModel 的属性通知（幂等：Loaded / 导航 / 构造都可能调用）。</summary>
    private void SubscribeLibraryViewModel()
    {
        if (_librarySubscribed)
        {
            return;
        }

        _library.PropertyChanged += OnLibraryPropertyChanged;
        _librarySubscribed = true;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => SyncFromTheme();

    /// <summary>把当前主题状态刷到界面上。不触发写配置。</summary>
    private void SyncFromTheme()
    {
        _syncing = true;
        try
        {
            var mode = ThemeManager.Mode;
            ThemeSystemRadio.IsChecked = mode == AppThemeMode.System;
            ThemeLightRadio.IsChecked = mode == AppThemeMode.Light;
            ThemeSepiaRadio.IsChecked = mode == AppThemeMode.Sepia;
            ThemeDarkRadio.IsChecked = mode == AppThemeMode.Dark;
            ThemeGreenRadio.IsChecked = mode == AppThemeMode.Green;
            ThemeCustomRadio.IsChecked = mode == AppThemeMode.Custom;

            // 自定义配色只在选「自定义」时展开（面板就在这排按钮正下方）；
            // 「强调色跟随系统」对自定义无效（用户选了强调色就以用户为准），顺手藏起来免得点了没反应。
            var custom = mode == AppThemeMode.Custom;
            CustomThemePanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            AccentFollowCheck.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;

            if (custom)
            {
                UpdateCustomSwatches();
            }

            AccentFollowCheck.IsChecked = ThemeManager.AccentFollowsSystem;
        }
        finally
        {
            _syncing = false;
        }
    }

    private async void OnThemeRadioChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton radio)
        {
            return;
        }

        var mode = ThemeManager.ParseMode(radio.Tag as string);

        // 第一次选「自定义」：以当前生效主题的四个颜色为起点，用户不用从白底重新配色。
        // 已保存过自定义配色时这一步是空操作。
        if (mode == AppThemeMode.Custom)
        {
            await SafeRunAsync("初始化自定义配色", () => ThemeManager.SeedCustomColorsIfNeededAsync());
        }

        await SafeRunAsync("切换主题", () => ThemeManager.SetModeAsync(mode));
        Log.Information("主题切换为 {Mode}", ThemeManager.ToConfigValue(mode));
    }

    private async void OnAccentFollowChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var follow = AccentFollowCheck.IsChecked == true;
        await SafeRunAsync("保存强调色设置", () => ThemeManager.SetAccentFollowsSystemAsync(follow));
    }

    // ==================== 自定义配色（用户 2026-10-21） ====================

    /// <summary>调色板的 24 个常用色（3 行 × 8 列）：浅色底、常用彩色、深色底各占一行。</summary>
    private static readonly string[] PaletteColors =
    {
        "#FFFFFF", "#FAF4E6", "#F4ECD8", "#F3F3F3", "#EDF3E7", "#E8F4FA", "#FDEBEB", "#F3E8FD",
        "#DCCFB0", "#D9D9D9", "#B8C4B0", "#A9C7E8", "#E8B4B4", "#C9B3E0", "#9E9E9E", "#6E6E6E",
        "#000000", "#202020", "#404040", "#5B4636", "#8B5A2B", "#0067C0", "#2D6A2D", "#C62828",
    };

    /// <summary>当前正在选色的目标：background / card / text / accent。</summary>
    private string _paletteTarget = "background";

    /// <summary>把 24 个预设色做成色块按钮，点一下就套到当前目标上。</summary>
    private void BuildColorPalette()
    {
        foreach (var hex in PaletteColors)
        {
            if (!ThemeManager.TryParseColor(hex, out var color))
            {
                continue;
            }

            var swatch = new Border
            {
                // 按钮模板把内容固定居中，给内容 Stretch 不生效；这里直接给尺寸才不会缩成 0
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(color),
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

            var button = new Button
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                Tag = hex,
                ToolTip = hex,
                Content = swatch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            button.Click += OnPaletteColorClick;

            ColorPalettePanel.Children.Add(button);
        }
    }

    /// <summary>点某一行的色块：记住目标、刷新标题与色值，然后把调色板贴在按钮下方弹出。</summary>
    private void OnCustomColorButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target } button)
        {
            return;
        }

        _paletteTarget = target;
        ColorPaletteTitle.Text = target switch
        {
            "card" => "选择卡片底色",
            "text" => "选择文字颜色",
            "accent" => "选择强调色",
            _ => "选择窗口底色",
        };

        ColorPaletteHexBox.Text = ThemeManager.FormatColor(CustomColorOf(target));
        ColorPalettePopup.PlacementTarget = button;
        ColorPalettePopup.IsOpen = true;
    }

    private async void OnPaletteColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex } || !ThemeManager.TryParseColor(hex, out var color))
        {
            return;
        }

        ColorPalettePopup.IsOpen = false;
        await ApplyCustomColorAsync(_paletteTarget, color);
    }

    private async void OnPaletteHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await ApplyPaletteHexAsync();
    }

    private async void OnPaletteHexLostFocus(object sender, RoutedEventArgs e)
    {
        if (!ThemeManager.TryParseColor(ColorPaletteHexBox.Text, out _))
        {
            ColorPaletteHexBox.Text = ThemeManager.FormatColor(CustomColorOf(_paletteTarget));
            return;
        }

        await ApplyPaletteHexAsync();
    }

    private async Task ApplyPaletteHexAsync()
    {
        if (!ThemeManager.TryParseColor(ColorPaletteHexBox.Text, out var color))
        {
            ColorPaletteHexBox.Text = ThemeManager.FormatColor(CustomColorOf(_paletteTarget));
            return;
        }

        ColorPalettePopup.IsOpen = false;
        await ApplyCustomColorAsync(_paletteTarget, color);
    }

    /// <summary>面板里四行的色值框：回车 / 失焦提交，非法值回填当前颜色。</summary>
    private async void OnCustomHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box)
        {
            return;
        }

        e.Handled = true;
        await ApplyCustomHexBoxAsync(box);
    }

    private async void OnCustomHexLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            await ApplyCustomHexBoxAsync(box);
        }
    }

    private async Task ApplyCustomHexBoxAsync(TextBox box)
    {
        var target = TargetOfCustomHexBox(box);
        if (target is null)
        {
            return;
        }

        if (!ThemeManager.TryParseColor(box.Text, out var color))
        {
            box.Text = ThemeManager.FormatColor(CustomColorOf(target));
            return;
        }

        await ApplyCustomColorAsync(target, color);
    }

    /// <summary>改其中一项：其余三项保持现值，交给 ThemeManager 生成整套主题并落盘。</summary>
    private async Task ApplyCustomColorAsync(string target, Color color)
    {
        var background = ThemeManager.CustomBackground;
        var card = ThemeManager.CustomCard;
        var text = ThemeManager.CustomText;
        var accent = ThemeManager.CustomAccent;

        switch (target)
        {
            case "card":
                card = color;
                break;
            case "text":
                text = color;
                break;
            case "accent":
                accent = color;
                break;
            default:
                background = color;
                break;
        }

        await SafeRunAsync(
            "保存自定义配色",
            () => ThemeManager.SetCustomColorsAsync(background, card, text, accent));

        UpdateCustomSwatches();
    }

    /// <summary>把自定义配色刷到四个色块与色值框上（程序化写文本，不触发保存）。</summary>
    private void UpdateCustomSwatches()
    {
        CustomBackgroundSwatch.Background = new SolidColorBrush(ThemeManager.CustomBackground);
        CustomCardSwatch.Background = new SolidColorBrush(ThemeManager.CustomCard);
        CustomTextSwatch.Background = new SolidColorBrush(ThemeManager.CustomText);
        CustomAccentSwatch.Background = new SolidColorBrush(ThemeManager.CustomAccent);

        SetBoxText(CustomBackgroundHex, ThemeManager.FormatColor(ThemeManager.CustomBackground));
        SetBoxText(CustomCardHex, ThemeManager.FormatColor(ThemeManager.CustomCard));
        SetBoxText(CustomTextHex, ThemeManager.FormatColor(ThemeManager.CustomText));
        SetBoxText(CustomAccentHex, ThemeManager.FormatColor(ThemeManager.CustomAccent));
    }

    private static Color CustomColorOf(string target) => target switch
    {
        "card" => ThemeManager.CustomCard,
        "text" => ThemeManager.CustomText,
        "accent" => ThemeManager.CustomAccent,
        _ => ThemeManager.CustomBackground,
    };

    private string? TargetOfCustomHexBox(TextBox box)
    {
        if (ReferenceEquals(box, CustomCardHex))
        {
            return "card";
        }

        if (ReferenceEquals(box, CustomTextHex))
        {
            return "text";
        }

        if (ReferenceEquals(box, CustomAccentHex))
        {
            return "accent";
        }

        return ReferenceEquals(box, CustomBackgroundHex) ? "background" : null;
    }

    // ==================== 从配置读初值 ====================

    /// <summary>把所有控件的初值从 Config.json 刷一遍。期间不触发任何写盘。</summary>
    private void SyncFromConfig()
    {
        _syncing = true;
        try
        {
            // 常规
            SelectComboItemByTag(LanguageCombo, "zh-CN");
            OpenLastBookCheck.IsChecked = _config.GetBool(AppSettingsKeys.OpenLastBook, false);

            // 书库
            SelectComboItemByTag(
                CoverSizeCombo,
                AppSettingsKeys.NormalizeCoverSize(_config.GetString(AppSettingsKeys.CoverSize, null)));
            SelectComboItemByTag(
                LibraryViewCombo,
                string.Equals(_config.GetString(AppSettingsKeys.LibraryView, null), "list", StringComparison.OrdinalIgnoreCase)
                    ? "list"
                    : "grid");
            AutoScanCheck.IsChecked = _config.GetBool(AppSettingsKeys.AutoScan, true);

            // 转换
            SelectComboItemByTag(
                DefaultFormatCombo,
                NormalizeTargetFormat(_config.GetString(AppSettingsKeys.ConversionDefaultTarget, null)));

            // 阅读
            SelectComboItemByTag(
                ReaderThemeCombo,
                NormalizeReaderTheme(_config.GetString(AppSettingsKeys.ReaderTheme, null)));
            PaginationCheck.IsChecked = _config.GetBool(AppSettingsKeys.PaginationMode, false);

            _lastPreloadPages = AppSettingsKeys.ReadPreloadPages(_config);
            SetBoxText(PreloadBox, _lastPreloadPages.ToString(CultureInfo.InvariantCulture));

            SelectFontFamily(ReadFontFamily());
            SetBoxText(LineHeightBox, FormatLineHeight(ReadLineHeight()));

            // 数值框统一按各自绑定回填（读出来的值已经夹过范围）
            foreach (var binding in _intBindings.Values)
            {
                SetBoxText(binding.Box, binding.Read().ToString(CultureInfo.InvariantCulture));
            }

            // 高级
            SelectLogLevel(AppSettingsKeys.NormalizeLogLevel(
                _config.GetString(AppSettingsKeys.LogLevel, null)));
        }
        finally
        {
            _syncing = false;
        }

        UpdateParallelWarning(ReadMaxParallel());
    }

    /// <summary>转换默认目标格式：只认 epub / mobi / pdf / txt，读不到就按 epub（与现在的"第一个"一致）。</summary>
    private static string NormalizeTargetFormat(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "mobi" => "mobi",
        "pdf" => "pdf",
        "txt" => "txt",
        _ => "epub",
    };

    /// <summary>阅读主题 id 与 reader.js 的 THEMES 一致；读不到默认白。</summary>
    private static string NormalizeReaderTheme(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "sepia" => "sepia",
        "dark" => "dark",
        "green" => "green",
        _ => "light",
    };

    private string ReadFontFamily()
    {
        var stored = _config.GetString(AppSettingsKeys.FontFamily, null);
        return string.IsNullOrWhiteSpace(stored) ? DefaultFontFamily : stored.Trim();
    }

    private int ReadFontSize()
        => Math.Clamp(AppSettingsKeys.GetInt(_config, AppSettingsKeys.FontSize, DefaultFontSize), 12, 36);

    private double ReadLineHeight()
        => Math.Clamp(AppSettingsKeys.GetDouble(_config, AppSettingsKeys.LineHeight, DefaultLineHeight), 1.2, 2.5);

    private int ReadMaxParallel()
        => AppSettingsKeys.NormalizeMaxParallel(
            AppSettingsKeys.GetInt(_config, AppSettingsKeys.ConversionMaxParallel, AppSettingsKeys.MinMaxParallel));

    private int ReadConversionTimeoutMinutes()
    {
        var minutes = (int)Math.Round(
            AppSettingsKeys.GetDouble(_config, AppSettingsKeys.ConversionTimeoutMinutes, DefaultConversionTimeoutMinutes));

        // 转换引擎的既有口径是"<= 0 按 60 处理"，设置页直接显示生效值。
        return minutes <= 0 ? DefaultConversionTimeoutMinutes : Math.Clamp(minutes, 1, 1440);
    }

    // ==================== 常规 ====================

    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || GetSelectedTag(LanguageCombo) is not { } tag)
        {
            return;
        }

        await SafeRunAsync("保存语言", () => AppSettingsKeys.SetStringAsync(_config, AppSettingsKeys.Language, tag));
    }

    private async void OnOpenLastBookChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var enabled = OpenLastBookCheck.IsChecked == true;
        await SafeRunAsync(
            "保存启动设置",
            () => AppSettingsKeys.SetBoolAsync(_config, AppSettingsKeys.OpenLastBook, enabled));
    }

    // ==================== 书库 ====================

    /// <summary>数据目录与「删除旧数据目录」按钮的可见性。只读内存/查目录存在性，不查盘。</summary>
    private void RefreshDataDir()
    {
        var current = string.IsNullOrWhiteSpace(_migration.CurrentDataDir)
            ? _paths.DataRoot
            : _migration.CurrentDataDir;

        DataDirText.Text = current;
        DataDirText.ToolTip = current;

        var previous = _migration.PreviousDataDir;
        var hasPrevious = !string.IsNullOrWhiteSpace(previous) && Directory.Exists(previous);

        DeleteOldDataButton.Visibility = hasPrevious ? Visibility.Visible : Visibility.Collapsed;
        DeleteOldDataButton.ToolTip = hasPrevious ? "删除旧数据目录：" + previous : null;
    }

    private void OnChangeDataDirClick(object sender, RoutedEventArgs e)
    {
        // 没有宿主窗口（冒烟直接构造页面）时不弹模态框：迁移窗口本身是模态的。
        if (Window.GetWindow(this) is not { } owner)
        {
            return;
        }

        SafeRun("打开数据迁移窗口", () =>
        {
            var window = new DataMigrationWindow(_paths, _config, _migration, _shell)
            {
                Owner = owner,
            };

            window.ShowDialog();
            RefreshDataDir();
        });
    }

    private async void OnDeleteOldDataClick(object sender, RoutedEventArgs e)
    {
        var previous = _migration.PreviousDataDir;

        if (string.IsNullOrWhiteSpace(previous) || !Directory.Exists(previous))
        {
            RefreshDataDir();
            return;
        }

        var answer = AppDialog.Show(
            Window.GetWindow(this),
            "确定删除旧数据目录吗？\n\n" + previous + "\n\n"
            + "里面的书库旧副本会被永久删除。新的数据目录已经可以用，删除后无法撤销。",
            "删除旧数据目录",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        DeleteOldDataButton.IsEnabled = false;

        try
        {
            await _migration.DeleteOldDataAsync().ConfigureAwait(true);
            RefreshDataDir();
            AppDialog.Show(
                Window.GetWindow(this),
                "旧数据目录已删除。",
                "删除旧数据目录",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "删除旧数据目录失败");
            AppDialog.Show(
                Window.GetWindow(this),
                ex.SafeMessage(),
                "删除旧数据目录失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            DeleteOldDataButton.IsEnabled = true;
        }
    }

    /// <summary>书库视图下拉 → ViewModel。ViewModel 负责落盘并通知工具栏按钮，两边自动同步。</summary>
    private async void OnLibraryViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || GetSelectedTag(LibraryViewCombo) is not { } tag)
        {
            return;
        }

        var listView = string.Equals(tag, "list", StringComparison.OrdinalIgnoreCase);
        await SafeRunAsync("切换书库视图", () => _library.SetListViewAsync(listView));
    }

    /// <summary>工具栏按钮切换视图后，把设置页下拉同步过来（带重入保护，不会来回写盘）。</summary>
    private void OnLibraryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null
            or nameof(LibraryViewModel.IsListView)
            or nameof(LibraryViewModel.ViewToggleText))
        {
            SyncLibraryViewFromViewModel();
        }
    }

    private void SyncLibraryViewFromViewModel()
    {
        _syncing = true;
        try
        {
            SelectComboItemByTag(LibraryViewCombo, _library.IsListView ? "list" : "grid");
        }
        finally
        {
            _syncing = false;
        }
    }

    private async void OnCoverSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || GetSelectedTag(CoverSizeCombo) is not { } tag)
        {
            return;
        }

        var normalized = AppSettingsKeys.NormalizeCoverSize(tag);
        await SafeRunAsync(
            "保存封面尺寸",
            () => AppSettingsKeys.SetStringAsync(_config, AppSettingsKeys.CoverSize, normalized));
    }

    private async void OnAutoScanChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var enabled = AutoScanCheck.IsChecked == true;

        await SafeRunAsync(
            "保存自动扫描设置",
            () => AppSettingsKeys.SetBoolAsync(_config, AppSettingsKeys.AutoScan, enabled));

        // 实时生效：不需要重启。目录不存在等失败只记日志，不打断设置页。
        try
        {
            if (enabled)
            {
                _watcher.Start();
            }
            else
            {
                _watcher.Stop();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "切换书库目录监听失败（不影响设置保存）");
        }
    }

    /// <summary>
    /// 用户上次关掉了自动扫描：进设置页时顺手把 watcher 停掉（启动时 App 会无条件 Start）。
    /// 只在关闭方向动 watcher，避免冒烟直接构造页面时把监听启动起来。
    /// </summary>
    private void StopWatcherIfAutoScanDisabled()
    {
        if (_config.GetBool(AppSettingsKeys.AutoScan, true))
        {
            return;
        }

        try
        {
            _watcher.Stop();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "停止书库目录监听失败（不影响使用）");
        }
    }

    // ==================== 转换 ====================

    private async void OnDefaultFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || GetSelectedTag(DefaultFormatCombo) is not { } tag)
        {
            return;
        }

        /* 直接存 ComboBoxItem.Tag（小写 epub/mobi/pdf/txt）。转换窗口用
           BookFormatKinds.FromStorageString 读取，大小写都兼容；读不到就保持原来选第一项的行为。 */
        await SafeRunAsync(
            "保存默认输出格式",
            () => AppSettingsKeys.SetStringAsync(_config, AppSettingsKeys.ConversionDefaultTarget, tag));
    }

    /// <summary>并发 > 1 的黄色警告：只有选了 2–4 才出现。</summary>
    private void UpdateParallelWarning(int parallel)
    {
        ParallelWarnText.Visibility = parallel > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================== 阅读 ====================

    private async void OnReaderThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || GetSelectedTag(ReaderThemeCombo) is not { } tag)
        {
            return;
        }

        await SafeRunAsync(
            "保存阅读主题",
            () => AppSettingsKeys.SetStringAsync(_config, AppSettingsKeys.ReaderTheme, tag));
    }

    private async void OnPaginationChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var paged = PaginationCheck.IsChecked == true;
        await SafeRunAsync(
            "保存阅读模式",
            () => AppSettingsKeys.SetBoolAsync(_config, AppSettingsKeys.PaginationMode, paged));
    }

    /// <summary>系统字体候选：与阅读器工具栏同一个来源（ReaderWindow.FontOptions）。</summary>
    private List<string> _fontOptions = new();

    /// <summary>
    /// 组装字体下拉：系统已安装字体 + 当前配置值（配置值可能来自旧机器或已卸载的字体，
    /// 必须留在列表里，否则一进设置页就把用户的选择显示成空白）。
    /// </summary>
    private void InitializeFontFamilyOptions()
    {
        var options = new List<string>(ReaderWindow.FontOptions());
        var current = ReadFontFamily();

        if (!options.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            options.Insert(0, current);
        }

        _fontOptions = options;
        FontFamilyBox.ItemsSource = _fontOptions;
    }

    private async void OnFontFamilySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || FontFamilyBox.SelectedItem is not string family || family.Length == 0)
        {
            return;
        }

        await SafeRunAsync(
            "保存字体",
            () => AppSettingsKeys.SetStringAsync(_config, AppSettingsKeys.FontFamily, family));
    }

    /// <summary>把配置里的字体刷到下拉上（期间不触发写盘）。</summary>
    private void SelectFontFamily(string family)
    {
        var previous = _syncing;
        _syncing = true;

        try
        {
            FontFamilyBox.SelectedItem = _fontOptions.FirstOrDefault(
                option => string.Equals(option, family, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _syncing = previous;
        }
    }

    // ---- 数字行的上下箭头（统一入口：按钮 Tag = "绑定名:+delta"） ----

    /// <summary>
    /// 所有数字输入框右侧的 ▲/▼ 都走这一个入口：
    /// Tag 形如 "maxParallel:+1"、"lineHeight:-0.1"，按绑定名找范围与写盘规则。
    /// 行高是小数（±0.1），其余都是整数（±1）。
    /// </summary>
    private void OnStepperClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string spec })
        {
            return;
        }

        var parts = spec.Split(':', 2);

        if (parts.Length != 2)
        {
            return;
        }

        if (parts[0] == "lineHeight")
        {
            if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lineDelta))
            {
                StepLineHeight(lineDelta);
            }

            return;
        }

        if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta))
        {
            StepIntValue(parts[0], delta);
        }
    }

    /// <summary>行高 ±0.1：夹到 1.2–2.5（保留两位小数）后落盘；到边界值不动。</summary>
    private void StepLineHeight(double delta)
    {
        var current = TryParseDouble(LineHeightBox.Text, out var parsed)
            ? Math.Clamp(parsed, 1.2, 2.5)
            : ReadLineHeight();

        var next = Math.Round(Math.Clamp(current + delta, 1.2, 2.5), 2);
        var text = FormatLineHeight(next);

        SetBoxText(LineHeightBox, text);

        if (Math.Abs(next - current) < 0.0001)
        {
            // 到边界：值不动、不重复写盘
            return;
        }

        _ = SafeRunAsync(
            "保存行高",
            () => AppSettingsKeys.SetStringAsync(_config, AppSettingsKeys.LineHeight, text));
    }

    private void OnLineHeightChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        if (!TryParseDouble(LineHeightBox.Text, out var value))
        {
            return;
        }

        var clamped = Math.Clamp(value, 1.2, 2.5);
        var text = FormatLineHeight(clamped);

        if (clamped != value)
        {
            SetBoxText(LineHeightBox, text);
        }

        _ = SafeRunAsync(
            "保存行高",
            () => AppSettingsKeys.SetStringAsync(_config, AppSettingsKeys.LineHeight, text));
    }

    private void OnLineHeightLostFocus(object sender, RoutedEventArgs e)
    {
        if (!TryParseDouble(LineHeightBox.Text, out _))
        {
            SetBoxText(LineHeightBox, FormatLineHeight(ReadLineHeight()));
        }
    }

    private void OnLineHeightKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;

        if (!TryParseDouble(LineHeightBox.Text, out var value))
        {
            SetBoxText(LineHeightBox, FormatLineHeight(ReadLineHeight()));
            return;
        }

        var clamped = Math.Clamp(value, 1.2, 2.5);
        var text = FormatLineHeight(clamped);

        SetBoxText(LineHeightBox, text);
        _ = SafeRunAsync(
            "保存行高",
            () => AppSettingsKeys.SetStringAsync(_config, AppSettingsKeys.LineHeight, text));
    }

    private static string FormatLineHeight(double value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);

    // ==================== 预加载页数（1–1000） ====================

    private void OnPreloadTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        if (!TryParseInt(PreloadBox.Text, out var pages))
        {
            /* 清空（用户删光准备重输）与其它中间态不同：空框没有任何含义，立刻回填最后一次
               有效值，避免留下一个"配置是旧值、框里是空"的不一致状态。
               非空中间态（如只敲了 "-"）保持原样，等失焦或回车处理。 */
            if (PreloadBox.Text.Length == 0)
            {
                SetBoxText(PreloadBox, _lastPreloadPages.ToString(CultureInfo.InvariantCulture));
            }

            return;
        }

        ApplyPreloadPages(pages, persist: true);
    }

    private void OnPreloadLostFocus(object sender, RoutedEventArgs e)
    {
        if (!TryParseInt(PreloadBox.Text, out var pages))
        {
            SetBoxText(PreloadBox, _lastPreloadPages.ToString(CultureInfo.InvariantCulture));
            return;
        }

        ApplyPreloadPages(pages, persist: true);
    }

    private void OnPreloadKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;

        if (!TryParseInt(PreloadBox.Text, out var pages))
        {
            SetBoxText(PreloadBox, _lastPreloadPages.ToString(CultureInfo.InvariantCulture));
            return;
        }

        ApplyPreloadPages(pages, persist: true);
    }

    private void OnPreloadUpClick(object sender, RoutedEventArgs e) => StepPreloadPages(1);

    private void OnPreloadDownClick(object sender, RoutedEventArgs e) => StepPreloadPages(-1);

    /// <summary>把输入值夹到 1–1000、必要时改写框内文字，然后立刻落盘。</summary>
    private void ApplyPreloadPages(int value, bool persist)
    {
        var normalized = AppSettingsKeys.NormalizePreloadPages(value);

        if (normalized != value)
        {
            SetBoxText(PreloadBox, normalized.ToString(CultureInfo.InvariantCulture));
        }

        _lastPreloadPages = normalized;

        if (persist)
        {
            _ = SafeRunAsync("保存预加载页数", () => PersistPreloadPagesAsync(normalized));
        }
    }

    /// <summary>上下箭头：当前值 ±1，夹到 1–1000；到边界时按钮不报错、值不动。</summary>
    private void StepPreloadPages(int delta)
    {
        var current = TryParseInt(PreloadBox.Text, out var parsed)
            ? AppSettingsKeys.NormalizePreloadPages(parsed)
            : _lastPreloadPages;

        var next = AppSettingsKeys.NormalizePreloadPages(current + delta);

        if (next == current)
        {
            // 到边界：把框里的文字也修正成合法值，但不弹错、不重复写盘。
            SetBoxText(PreloadBox, current.ToString(CultureInfo.InvariantCulture));
            return;
        }

        SetBoxText(PreloadBox, next.ToString(CultureInfo.InvariantCulture));
        ApplyPreloadPages(next, persist: true);
    }

    /// <summary>
    /// 预加载页数按**字符串**存：冒烟与 <see cref="AppSettingsKeys.ReadPreloadPages"/> 都用
    /// <c>config.GetString</c> 读它（阅读器旧版本也是这么写的），因此不能走写数字的
    /// <see cref="AppSettingsKeys.WritePreloadPagesAsync"/>，否则读回来永远是默认值。
    /// </summary>
    private Task PersistPreloadPagesAsync(int pages)
        => AppSettingsKeys.SetStringAsync(
            _config,
            AppSettingsKeys.PdfPreloadPages,
            pages.ToString(CultureInfo.InvariantCulture));

    // ==================== 工具 ====================

    private async void OnRecheckToolsClick(object sender, RoutedEventArgs e)
    {
        // 手动重检才跑版本探测，并先丢弃缓存（与诊断页、主窗口「重新检测」一致）。
        if (_tools is EbookToolSuite.Engines.Processes.ToolLocator concrete)
        {
            concrete.InvalidateHealthCache();
        }

        await RefreshToolRowsAsync(runProbes: true);
    }

    private async Task RefreshToolRowsAsync(bool runProbes)
    {
        ToolRowsPanel.Children.Clear();
        ToolRowsPanel.Children.Add(MakeLine("正在检测工具…", "TextSecondaryBrush"));

        ToolHealthReport report;

        try
        {
            report = await _tools.VerifyAllAsync(runProbes).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 进设置页不许弹模态框：失败写在行里，用户还能点「重新检测」。
            ToolRowsPanel.Children.Clear();
            ToolRowsPanel.Children.Add(MakeLine("检查失败：" + ex.SafeMessage(), "DangerBackgroundBrush"));
            Log.Warning(ex, "设置页工具检测失败");
            return;
        }

        ToolRowsPanel.Children.Clear();

        if (report.Items.Count == 0)
        {
            ToolRowsPanel.Children.Add(MakeLine("（还没有体检结果）", "TextTertiaryBrush"));
            return;
        }

        foreach (var item in report.Items)
        {
            ToolRowsPanel.Children.Add(MakeToolRow(item));
        }
    }

    /// <summary>一行：显示名 / 状态 / 版本 / 缺失时的 FixHint。</summary>
    private static Grid MakeToolRow(ToolHealthItem item)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = MakeCell(item.DisplayName + (item.Required ? string.Empty : "（可选）"), "TextPrimaryBrush");
        var state = MakeCell(DescribeToolState(item.State), ToolStateBrushKey(item.State));
        var version = MakeCell(item.Version ?? string.Empty, "TextTertiaryBrush");
        var hint = MakeCell(item.FixHint ?? item.Detail ?? string.Empty, "TextSecondaryBrush", wrap: true);

        Grid.SetColumn(name, 0);
        Grid.SetColumn(state, 1);
        Grid.SetColumn(version, 2);
        Grid.SetColumn(hint, 3);

        row.Children.Add(name);
        row.Children.Add(state);
        row.Children.Add(version);
        row.Children.Add(hint);

        return row;
    }

    private static string DescribeToolState(ToolState state) => state switch
    {
        ToolState.Ok => "正常",
        ToolState.Missing => "缺失",
        ToolState.VersionMismatch => "版本不符",
        ToolState.ProbeFailed => "探测失败",
        ToolState.OptionalDisabled => "已关闭",
        _ => state.ToString(),
    };

    private static string ToolStateBrushKey(ToolState state) => state switch
    {
        ToolState.Ok => "SuccessBrush",
        ToolState.OptionalDisabled => "NeutralBrush",
        _ => "DangerBackgroundBrush",
    };

    private static TextBlock MakeCell(string text, string brushKey, bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Margin = new Thickness(0, 1, 8, 1),
        };

        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    private static TextBlock MakeLine(string text, string brushKey) => MakeCell(text, brushKey, wrap: true);

    // ==================== 缓存 ====================

    private void RefreshCachePath() => CachePathText.Text = _paths.CacheDir;

    private async Task RefreshCacheUsageAsync()
    {
        try
        {
            var usage = await _maintenance.GetCacheUsageAsync().ConfigureAwait(true);

            CacheUsageText.Text = usage.FileCount == 0
                ? "0 B（还没有缓存文件）"
                : FormatBytes(usage.TotalBytes) + "（" + usage.FileCount + " 个文件）";
        }
        catch (Exception ex)
        {
            CacheUsageText.Text = "统计失败：" + ex.SafeMessage();
            Log.Warning(ex, "统计缓存占用失败");
        }
    }

    private async void OnCleanCacheClick(object sender, RoutedEventArgs e)
    {
        CleanCacheButton.IsEnabled = false;

        try
        {
            var result = await _maintenance.CleanCacheAsync().ConfigureAwait(true);
            await RefreshCacheUsageAsync().ConfigureAwait(true);

            var message = "已删除 " + result.DeletedFiles + " 个文件，释放 " + FormatBytes(result.FreedBytes) + "。";

            if (result.Failures.Count > 0)
            {
                message += Environment.NewLine
                    + Environment.NewLine
                    + "有 " + result.Failures.Count
                    + " 个文件正被占用，没删掉（关掉阅读窗口后再试）："
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, result.Failures.Take(5));
            }

            AppDialog.Show(
                Window.GetWindow(this),
                message,
                "清理缓存",
                MessageBoxButton.OK,
                result.Failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "清理缓存失败");
            AppDialog.Show(
                Window.GetWindow(this),
                ex.SafeMessage(),
                "清理缓存失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            CleanCacheButton.IsEnabled = true;
        }
    }

    // ==================== 备份 ====================

    private async void OnBackupNowClick(object sender, RoutedEventArgs e)
    {
        BackupNowButton.IsEnabled = false;

        try
        {
            var result = await _maintenance.CreateImmediateBackupAsync().ConfigureAwait(true);

            AppDialog.Show(
                Window.GetWindow(this),
                "已备份 " + result.FileCount + " 个文件（" + FormatBytes(result.Bytes) + "）："
                + Environment.NewLine
                + Environment.NewLine
                + result.Directory,
                "立即备份",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "立即备份失败");
            AppDialog.Show(
                Window.GetWindow(this),
                ex.SafeMessage(),
                "立即备份失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            BackupNowButton.IsEnabled = true;
        }
    }

    private async void OnManageBackupsClick(object sender, RoutedEventArgs e)
        => await SafeRunAsync("打开还原点管理", () => _navigation.NavigateAsync(Routes.Backup));

    // ==================== 高级 ====================

    private async void OnLogLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || LogLevelCombo.SelectedItem is not string level)
        {
            return;
        }

        var normalized = AppSettingsKeys.NormalizeLogLevel(level);
        await SafeRunAsync(
            "保存日志级别",
            () => AppSettingsKeys.SetStringAsync(_config, AppSettingsKeys.LogLevel, normalized));
    }

    private void SelectLogLevel(string level)
    {
        foreach (var item in LogLevelCombo.Items)
        {
            if (string.Equals(item as string, level, StringComparison.OrdinalIgnoreCase))
            {
                LogLevelCombo.SelectedItem = item;
                return;
            }
        }

        LogLevelCombo.SelectedItem = AppSettingsKeys.NormalizeLogLevel(null);
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
        => SafeRun("打开日志目录", () => _shell.OpenFolder(_paths.LogsDir));

    private async void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        ExportDiagnosticsButton.IsEnabled = false;

        try
        {
            var zipPath = await _maintenance.ExportDiagnosticsAsync().ConfigureAwait(true);
            ShowExportResult(zipPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出诊断信息失败");
            AppDialog.Show(
                Window.GetWindow(this),
                ex.SafeMessage(),
                "导出诊断信息失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            ExportDiagnosticsButton.IsEnabled = true;
        }
    }

    /// <summary>导出成功后的弹窗：显示 zip 路径，并提供「打开所在文件夹」。</summary>
    private void ShowExportResult(string zipPath)
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "诊断信息已导出：",
            FontSize = 13,
        });

        var pathText = new TextBlock
        {
            Text = zipPath,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        pathText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        panel.Children.Add(pathText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        var openButton = new Button
        {
            Content = "打开所在文件夹",
            MinWidth = 120,
            Padding = new Thickness(12, 4, 12, 4),
        };
        openButton.SetResourceReference(StyleProperty, "PrimaryButtonStyle");

        var closeButton = new Button
        {
            Content = "关闭",
            MinWidth = 88,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 4, 12, 4),
            IsCancel = true,
        };
        closeButton.SetResourceReference(StyleProperty, "SecondaryButtonStyle");

        buttons.Children.Add(openButton);
        buttons.Children.Add(closeButton);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "导出诊断信息",
            Content = new Border { Padding = new Thickness(20, 20, 20, 20), Child = panel },
            Owner = Window.GetWindow(this),
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            MinWidth = 420,
        };

        openButton.Click += (_, _) => dialog.DialogResult = true;
        closeButton.Click += (_, _) => dialog.DialogResult = false;

        if (dialog.ShowDialog() == true)
        {
            var folder = Path.GetDirectoryName(zipPath);

            if (!string.IsNullOrEmpty(folder))
            {
                SafeRun("打开诊断文件夹", () => _shell.OpenFolder(folder));
            }
        }
    }

    // ==================== 数值输入框（通用） ====================

    /// <summary>一个整数输入框的规则：范围、从配置读、写入配置。</summary>
    private sealed record IntBinding(
        TextBox Box,
        string Description,
        int Min,
        int Max,
        Func<int> Read,
        Func<int, Task> Write);

    private void RegisterIntBindings()
    {
        _intBindings["maxParallel"] = new IntBinding(
            MaxParallelBox,
            "并发数",
            AppSettingsKeys.MinMaxParallel,
            AppSettingsKeys.MaxMaxParallel,
            ReadMaxParallel,
            value => AppSettingsKeys.SetIntAsync(_config, AppSettingsKeys.ConversionMaxParallel, value));

        _intBindings["timeout"] = new IntBinding(
            TimeoutMinutesBox,
            "超时时间",
            1,
            1440,
            ReadConversionTimeoutMinutes,
            // 转换引擎用 Config.GetDouble 读这个键，所以这里必须存数字（不是字符串）。
            value => AppSettingsKeys.SetDoubleAsync(_config, AppSettingsKeys.ConversionTimeoutMinutes, value));

        _intBindings["cacheLimit"] = new IntBinding(
            CacheLimitBox,
            "缓存上限",
            AppSettingsKeys.MinCacheLimitMb,
            AppSettingsKeys.MaxCacheLimitMb,
            () => AppSettingsKeys.NormalizeCacheLimitMb(
                AppSettingsKeys.GetInt(_config, AppSettingsKeys.CacheLimitMb, AppSettingsKeys.DefaultCacheLimitMb)),
            value => AppSettingsKeys.SetIntAsync(_config, AppSettingsKeys.CacheLimitMb, value));

        _intBindings["backupLimit"] = new IntBinding(
            BackupLimitBox,
            "备份上限",
            AppSettingsKeys.MinBackupLimitMb,
            AppSettingsKeys.MaxBackupLimitMb,
            () => AppSettingsKeys.NormalizeBackupLimitMb(
                AppSettingsKeys.GetInt(_config, AppSettingsKeys.BackupLimitMb, AppSettingsKeys.DefaultBackupLimitMb)),
            value => AppSettingsKeys.SetIntAsync(_config, AppSettingsKeys.BackupLimitMb, value));

        _intBindings["fontSize"] = new IntBinding(
            FontSizeBox,
            "字号",
            12,
            36,
            ReadFontSize,
            // 阅读器按字符串读排版设置（GetString + 解析），这里沿用同一存法。
            value => AppSettingsKeys.SetStringAsync(
                _config,
                AppSettingsKeys.FontSize,
                value.ToString(CultureInfo.InvariantCulture)));
    }

    private void OnIntTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing
            || sender is not TextBox { Tag: string tag } box
            || !_intBindings.TryGetValue(tag, out var binding)
            || !TryParseInt(box.Text, out var value))
        {
            return;
        }

        ApplyIntValue(binding, value, persist: true);
    }

    private void OnIntLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: string tag } box
            || !_intBindings.TryGetValue(tag, out var binding))
        {
            return;
        }

        if (!TryParseInt(box.Text, out var value))
        {
            SetBoxText(box, binding.Read().ToString(CultureInfo.InvariantCulture));
            return;
        }

        ApplyIntValue(binding, value, persist: true);
    }

    private void OnIntKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter
            || sender is not TextBox { Tag: string tag } box
            || !_intBindings.TryGetValue(tag, out var binding))
        {
            return;
        }

        e.Handled = true;

        if (!TryParseInt(box.Text, out var value))
        {
            SetBoxText(box, binding.Read().ToString(CultureInfo.InvariantCulture));
            return;
        }

        ApplyIntValue(binding, value, persist: true);
    }

    /// <summary>能解析 → 夹到范围（被夹时改写框内文字）→ 立刻落盘。</summary>
    private void ApplyIntValue(IntBinding binding, int value, bool persist)
    {
        var normalized = Math.Clamp(value, binding.Min, binding.Max);

        if (normalized != value)
        {
            SetBoxText(binding.Box, normalized.ToString(CultureInfo.InvariantCulture));
        }

        if (persist)
        {
            _ = SafeRunAsync("保存" + binding.Description, () => binding.Write(normalized));
        }

        if (ReferenceEquals(binding.Box, MaxParallelBox))
        {
            UpdateParallelWarning(normalized);
        }
    }

    /// <summary>
    /// 数值行的上下箭头：按绑定的范围 ±delta（字号用 ±1），夹到边界后立刻落盘。
    /// 到边界时值不动、不弹错（与预加载页数的箭头行为一致）。
    /// </summary>
    private void StepIntValue(string tag, int delta)
    {
        if (!_intBindings.TryGetValue(tag, out var binding))
        {
            return;
        }

        var current = TryParseInt(binding.Box.Text, out var parsed)
            ? Math.Clamp(parsed, binding.Min, binding.Max)
            : binding.Read();

        var next = Math.Clamp(current + delta, binding.Min, binding.Max);

        if (next == current)
        {
            SetBoxText(binding.Box, current.ToString(CultureInfo.InvariantCulture));
            return;
        }

        SetBoxText(binding.Box, next.ToString(CultureInfo.InvariantCulture));
        ApplyIntValue(binding, next, persist: true);
    }

    // ==================== 小工具 ====================

    /// <summary>程序化写文本框：带重入保护，不触发保存。</summary>
    private void SetBoxText(TextBox box, string text)
    {
        var previous = _syncing;
        _syncing = true;

        try
        {
            box.Text = text;
        }
        finally
        {
            _syncing = previous;
        }
    }

    private static bool TryParseInt(string? text, out int value)
        => int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryParseDouble(string? text, out double value)
        => double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string? GetSelectedTag(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    /// <summary>按 ComboBoxItem.Tag 选中一项；找不到就清空选择。</summary>
    private static void SelectComboItemByTag(ComboBox combo, string? tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = -1;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
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
            ShowError(ex.SafeMessage(), action + " 失败");
        }
    }

    private async Task SafeRunAsync(string action, Func<Task> body)
    {
        try
        {
            await body().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Action} 失败", action);
            ShowError(ex.SafeMessage(), action + " 失败");
        }
    }

    /// <summary>冒烟会直接构造页面（没有宿主窗口），这时不弹模态框，只记日志。</summary>
    private void ShowError(string message, string caption)
    {
        var owner = Window.GetWindow(this);

        if (owner is null)
        {
            return;
        }

        AppDialog.Show(owner, message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
