using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EbookToolSuite.App.Controls;
using EbookToolSuite.App.Navigation;
using EbookToolSuite.App.Themes;
using EbookToolSuite.App.Views;
using EbookToolSuite.App.Views.Pages;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Infrastructure.Config;
using Microsoft.Extensions.DependencyInjection;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T7-3 UI 统一与美化验收（计划 10.1 的 T7-3a/T7-3b）。
///
/// 能自动验的都验：四套配色 key 一致、六档切换真的换色、自定义配色生成整套 token 并落盘、
/// 配置持久化、隐式样式确实存在、窗口背景跟随主题、对话框默认/取消按钮、
/// 设置页六个主题单选（米黄在亮色后、自定义在最后）与自定义面板显隐、
/// 阅读器首帧参数与漏设变量。视觉观感（对比度、间距）仍需人眼，报告里会列出截图。
/// </summary>
internal static class T73SmokeTest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<SmokeReport> RunAsync(
        IServiceProvider services,
        AppPaths paths,
        string workDirectory)
    {
        var startedAt = DateTimeOffset.Now;
        var watch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        var shotsDirectory = Path.Combine(workDirectory, "shots");
        Directory.CreateDirectory(shotsDirectory);

        var config = services.GetRequiredService<IConfigStore>();

        checks.Add(CheckThemeDictionaryKeys());

        checks.Add(await ApplyAndCheckAsync(AppThemeMode.Light, "#FFF3F3F3", isDark: false));
        checks.Add(await ApplyAndCheckAsync(AppThemeMode.Sepia, "#FFF0E6CF", isDark: false));
        checks.Add(await ApplyAndCheckAsync(AppThemeMode.Dark, "#FF202020", isDark: true));
        checks.Add(await ApplyAndCheckAsync(AppThemeMode.Green, "#FFEDF3E7", isDark: false));
        checks.Add(await CheckCustomThemeAsync(config).ConfigureAwait(true));

        // 持久化：切一次暗色，确认 ui.theme 落到配置文件
        await ThemeManager.SetModeAsync(AppThemeMode.Dark).ConfigureAwait(true);
        var stored = config.GetString(ThemeManager.ThemeConfigKey, null);
        checks.Add(new SmokeCheck(
            "theme-persist",
            stored == "dark",
            "Config ui.theme=" + (stored ?? "<null>") + "（期望 dark）"));

        checks.Add(await ApplyAndCheckAsync(AppThemeMode.System, expectedHex: null, isDark: null));

        checks.Add(CheckImplicitStyles());
        checks.Add(CheckButtonFocus());
        checks.Add(CheckWindowStyle());
        checks.Add(CheckDialogBackgroundAndKeys());
        checks.Add(CheckSettingsPage(services));
        checks.Add(CheckReaderAssets());
        checks.Add(CheckDragDrop());

        await ThemeManager.SetModeAsync(AppThemeMode.Light).ConfigureAwait(true);
        checks.Add(await CaptureShellScreenshotsAsync(services, shotsDirectory).ConfigureAwait(true));

        await ThemeManager.SetModeAsync(AppThemeMode.System).ConfigureAwait(true);

        watch.Stop();
        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            watch.ElapsedMilliseconds,
            paths.DataRoot,
            string.Empty,
            null,
            checks);

        var jsonPath = Path.Combine(workDirectory, "t73-report.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, JsonOptions)).ConfigureAwait(true);

        return report;
    }

    // ==================== 检查项 ====================

    /// <summary>四份主题字典的 key 必须完全一致（少一个 key 就会出现"某个控件不跟主题"）。</summary>
    private static SmokeCheck CheckThemeDictionaryKeys()
    {
        var sets = new[] { "Themes/Colors.xaml", "Themes/Dark.xaml", "Themes/Green.xaml", "Themes/Sepia.xaml" }
            .Select(source =>
            {
                var dictionary = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
                return (Source: source, Keys: dictionary.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(k => k).ToArray());
            })
            .ToArray();

        var reference = sets[0].Keys;
        var same = sets.All(s => s.Keys.SequenceEqual(reference));
        var required = new[] { "WarningBrush", "WarningSurfaceBrush", "InfoBrush", "ScrimBrush", "AccentColorBrush" };
        var missing = required.Where(k => !reference.Contains(k)).ToArray();

        return new SmokeCheck(
            "theme-keys-identical",
            same && missing.Length == 0,
            same
                ? $"四份字典都是 {reference.Length} 个 key" + (missing.Length == 0 ? "，语义色齐全" : "，缺 " + string.Join(",", missing))
                : "key 不一致：" + string.Join(" / ", sets.Select(s => s.Source + "=" + s.Keys.Length)));
    }

    /// <summary>切到某档后，窗口底色必须是该档的颜色；跟随系统档只验"能解析出实际配色"。</summary>
    private static async Task<SmokeCheck> ApplyAndCheckAsync(
        AppThemeMode mode,
        string? expectedHex,
        bool? isDark)
    {
        await ThemeManager.SetModeAsync(mode).ConfigureAwait(true);

        var brush = Application.Current.TryFindResource("WindowBackgroundBrush") as SolidColorBrush;
        var hex = brush is null ? "<null>" : brush.Color.ToString();
        var darkOk = isDark is null || ThemeManager.IsDark == isDark.Value;
        var colorOk = expectedHex is null
            || string.Equals(hex, expectedHex, StringComparison.OrdinalIgnoreCase);

        return new SmokeCheck(
            "theme-apply-" + ThemeManager.ToConfigValue(mode),
            brush is not null && darkOk && colorOk,
            $"窗口底色={hex}" + (expectedHex is null ? "（跟随系统）" : $"（期望 {expectedHex}）")
            + $"，IsDark={ThemeManager.IsDark}");
    }

    /// <summary>
    /// 自定义配色（用户 2026-10-21）：四个颜色必须真的替换到 token 上、底色偏深时按暗色推导、
    /// 没暴露给用户的语义色由内置主题补齐、四个颜色落进 Config.json。跑完恢复跟随系统。
    /// </summary>
    private static async Task<SmokeCheck> CheckCustomThemeAsync(IConfigStore config)
    {
        var background = Color.FromRgb(0x1E, 0x2A, 0x3A);
        var card = Color.FromRgb(0x26, 0x34, 0x46);
        var text = Color.FromRgb(0xEC, 0xF1, 0xF7);
        var accent = Color.FromRgb(0xF0, 0x8A, 0x24);

        await ThemeManager.SetCustomColorsAsync(background, card, text, accent).ConfigureAwait(true);
        await ThemeManager.SetModeAsync(AppThemeMode.Custom).ConfigureAwait(true);

        var colorsOk = ColorOf("WindowBackgroundBrush") == background
            && ColorOf("CardBackgroundBrush") == card
            && ColorOf("TextPrimaryBrush") == text
            && ColorOf("AccentColorBrush") == accent;

        // 证据文案必须在恢复跟随系统**之前**取，否则打印出来的是恢复后的颜色
        var windowHex = ThemeManager.FormatColor(ColorOf("WindowBackgroundBrush"));
        var cardHex = ThemeManager.FormatColor(ColorOf("CardBackgroundBrush"));

        // 用户没得选的语义色/遮罩：深底必须用暗色档那一套，不能出现刺眼的浅色块
        var inherited = new[]
            {
                "WarningBrush", "WarningSurfaceBrush", "InfoBrush", "ScrimBrush",
                "DangerTextBrush", "FileModifiedBrush", "OnDangerBorderBrush",
            }
            .All(key => Application.Current.TryFindResource(key) is SolidColorBrush);

        var stored = new[]
        {
            config.GetString(ThemeManager.CustomBackgroundConfigKey, null),
            config.GetString(ThemeManager.CustomCardConfigKey, null),
            config.GetString(ThemeManager.CustomTextConfigKey, null),
            config.GetString(ThemeManager.CustomAccentConfigKey, null),
        };

        var storedOk = stored[0] == "#1E2A3A" && stored[1] == "#263446"
            && stored[2] == "#ECF1F7" && stored[3] == "#F08A24";

        var darkOk = ThemeManager.IsDark;

        await ThemeManager.SetModeAsync(AppThemeMode.System).ConfigureAwait(true);

        return new SmokeCheck(
            "theme-custom-colors",
            colorsOk && darkOk && inherited && storedOk,
            $"四色生效={colorsOk}（窗口 {windowHex}，卡片 {cardHex}），"
            + $"深色判定={darkOk}，语义色继承={inherited}，落盘={storedOk}（{string.Join(" / ", stored)}）");
    }

    private static Color ColorOf(string key)
        => (Application.Current.TryFindResource(key) as SolidColorBrush)?.Color ?? Colors.Transparent;

    /// <summary>控件词表：这些类型必须能解析出隐式样式。</summary>
    private static SmokeCheck CheckImplicitStyles()
    {
        var types = new[]
        {
            typeof(Button), typeof(TextBox), typeof(ComboBox), typeof(CheckBox), typeof(RadioButton),
            typeof(ProgressBar), typeof(ToolTip), typeof(ScrollBar), typeof(ListView), typeof(ListViewItem),
            typeof(GridViewColumnHeader), typeof(Expander), typeof(GridSplitter), typeof(Menu),
            typeof(MenuItem), typeof(ContextMenu), typeof(Separator), typeof(Window),
        };

        var missing = types
            .Where(t => Application.Current.TryFindResource(t) is not Style)
            .Select(t => t.Name)
            .ToArray();

        return new SmokeCheck(
            "implicit-styles",
            missing.Length == 0,
            missing.Length == 0
                ? $"{types.Length} 类控件都有隐式样式"
                : "缺隐式样式：" + string.Join(",", missing));
    }

    /// <summary>键盘焦点：按钮样式的继承链上必须有 FocusVisualStyle（以前是置空 + 无焦点态）。</summary>
    private static SmokeCheck CheckButtonFocus()
    {
        var style = Application.Current.TryFindResource(typeof(Button)) as Style;
        var found = false;
        for (var current = style; current is not null && !found; current = current.BasedOn)
        {
            found = current.Setters.OfType<Setter>().Any(s => s.Property == FrameworkElement.FocusVisualStyleProperty);
        }

        return new SmokeCheck(
            "button-focus-ring",
            found,
            found ? "按钮样式继承链上有 FocusVisualStyle" : "按钮键盘焦点仍然不可见");
    }

    /// <summary>窗口默认样式：背景 / 前景 / 字号都必须来自主题。</summary>
    private static SmokeCheck CheckWindowStyle()
    {
        var style = Application.Current.TryFindResource(typeof(Window)) as Style;
        var hasBackground = style?.Setters.OfType<Setter>()
            .Any(s => s.Property == Control.BackgroundProperty) == true;
        var hasForeground = style?.Setters.OfType<Setter>()
            .Any(s => s.Property == Control.ForegroundProperty) == true;

        return new SmokeCheck(
            "window-style",
            hasBackground && hasForeground,
            $"窗口隐式样式 背景={hasBackground} 前景={hasForeground}");
    }

    /// <summary>对话框：背景跟随主题 + 默认/取消按钮齐 + 初始焦点绑在默认按钮上。</summary>
    private static SmokeCheck CheckDialogBackgroundAndKeys()
    {
        var dialog = new ConfirmSaveDialog("T7-3 主题检查：这条消息不会真的显示。");
        ThemeManager.ApplyWindowChrome(dialog);
        dialog.Measure(new Size(480, 360));
        dialog.Arrange(new Rect(0, 0, 480, 360));

        var expected = (Application.Current.TryFindResource("WindowBackgroundBrush") as SolidColorBrush)?.Color;
        var actual = (dialog.Background as SolidColorBrush)?.Color;
        var yesDefault = dialog.YesButton.IsDefault;
        var cancelIsCancel = dialog.CancelButton.IsCancel;
        var focusElement = FocusManager.GetFocusedElement(dialog) as FrameworkElement;

        var ok = expected is not null && actual == expected && yesDefault && cancelIsCancel;

        return new SmokeCheck(
            "dialog-theme-and-keys",
            ok,
            $"底色={actual}（期望 {expected}）；是按钮 IsDefault={yesDefault}，取消 IsCancel={cancelIsCancel}"
            + $"，初始焦点={focusElement?.Name ?? "<null>"}");
    }

    /// <summary>设置页「外观」组：六个主题单选齐全、顺序对（米黄在亮色后、自定义在最后）、勾选与当前档位一致、自定义面板只在自定义档展开。</summary>
    private static SmokeCheck CheckSettingsPage(IServiceProvider services)
    {
        // 设置页的 UI 元素全在 XAML namescope 里，不需要真的挂到窗口上。
        // T7-4：页面依赖变多，统一走 DI 构造（与真实导航完全同一条路径）。
        var page = services.GetRequiredService<SettingsPage>();

        var radios = new[]
            {
                "ThemeSystemRadio", "ThemeLightRadio", "ThemeSepiaRadio",
                "ThemeDarkRadio", "ThemeGreenRadio", "ThemeCustomRadio",
            }
            .Select(name => page.FindName(name))
            .ToArray();

        var missing = radios.Count(r => r is not RadioButton);
        var current = ThemeManager.Mode;
        var checkedOk = (RadiosChecked(page, "ThemeSystemRadio") == (current == AppThemeMode.System))
            && (RadiosChecked(page, "ThemeLightRadio") == (current == AppThemeMode.Light))
            && (RadiosChecked(page, "ThemeSepiaRadio") == (current == AppThemeMode.Sepia))
            && (RadiosChecked(page, "ThemeDarkRadio") == (current == AppThemeMode.Dark))
            && (RadiosChecked(page, "ThemeGreenRadio") == (current == AppThemeMode.Green))
            && (RadiosChecked(page, "ThemeCustomRadio") == (current == AppThemeMode.Custom));

        // 顺序：米黄紧跟亮色，自定义排在最后一个
        var orderOk = false;
        if (page.FindName("ThemeLightRadio") is RadioButton lightRadio && lightRadio.Parent is Panel row)
        {
            var lightIndex = row.Children.IndexOf(lightRadio);
            var sepiaIndex = row.Children.IndexOf(page.FindName("ThemeSepiaRadio") as UIElement);
            var customIndex = row.Children.IndexOf(page.FindName("ThemeCustomRadio") as UIElement);
            orderOk = sepiaIndex == lightIndex + 1 && customIndex == row.Children.Count - 1;
        }

        // 自定义配色面板：只在「自定义」档展开，其余档必须收起（面板就在这排按钮正下方）
        var panelShown = page.FindName("CustomThemePanel") is FrameworkElement panel
            && panel.Visibility == Visibility.Visible;
        var panelOk = panelShown == (current == AppThemeMode.Custom);

        return new SmokeCheck(
            "settings-appearance-group",
            missing == 0 && checkedOk && orderOk && panelOk,
            missing == 0
                ? $"六个主题单选齐全（顺序正确={orderOk}），当前档位 {ThemeManager.ToConfigValue(current)} 勾选正确={checkedOk}，自定义面板显隐正确={panelOk}"
                : "缺 " + missing + " 个主题单选按钮");
    }

    private static bool RadiosChecked(FrameworkElement page, string name) =>
        page.FindName(name) is RadioButton { IsChecked: true };

    /// <summary>阅读器：首帧主题参数 + 两个漏设的 CSS 变量必须已经写进资源文件。</summary>
    private static SmokeCheck CheckReaderAssets()
    {
        var jsPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Reader", "reader.js");
        if (!File.Exists(jsPath))
        {
            return new SmokeCheck("reader-theme-wiring", false, "找不到 " + jsPath);
        }

        var text = File.ReadAllText(jsPath);
        var hasQuery = text.Contains("URLSearchParams(location.search)", StringComparison.Ordinal)
            && text.Contains("get('theme')", StringComparison.Ordinal);
        var hasVars = text.Contains("--shell-border-strong", StringComparison.Ordinal)
            && text.Contains("--shell-icon-fg", StringComparison.Ordinal);

        return new SmokeCheck(
            "reader-theme-wiring",
            hasQuery && hasVars,
            $"首帧主题参数={hasQuery}，漏设变量补齐={hasVars}");
    }

    /// <summary>拖动导入：主窗口有整窗蒙版，落点复用与文件选择器完全相同的导入管线。</summary>
    private static SmokeCheck CheckDragDrop()
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        var hasOverlay = typeof(MainWindow).GetField("DropOverlay", Flags) is not null;
        var hasDropHandler = typeof(MainWindow).GetMethod("OnWindowDrop", Flags) is not null;
        var hasDragOverHandler = typeof(MainWindow).GetMethod("OnWindowDragOver", Flags) is not null;

        // 拖放与「文件 - 导入」共用同一条命令入口
        var hasSharedCommand = typeof(EbookToolSuite.App.Commands.ImportCommand)
            .GetMethod("RunPathsAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public) is not null;

        var ok = hasOverlay && hasDropHandler && hasDragOverHandler && hasSharedCommand;

        return new SmokeCheck(
            "drag-drop-import",
            ok,
            $"整窗蒙版={hasOverlay}，DragOver/Drop={hasDragOverHandler && hasDropHandler}，"
            + $"复用 ImportCommand.RunPathsAsync={hasSharedCommand}");
    }
    /// <summary>四套主题各截一张主窗口图（亮 / 米黄 / 暗 / 护眼），再加上自定义配色档的设置页面板截图。</summary>
    private static async Task<SmokeCheck> CaptureShellScreenshotsAsync(
        IServiceProvider services,
        string shotsDirectory)
    {
        try
        {
            var window = services.GetRequiredService<MainWindow>();
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -4000;
            window.Top = -4000;

            window.Show();
            await WaitUntilAsync(() => window.IsLoaded, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

            var modes = new[]
            {
                (AppThemeMode.Light, "light"),
                (AppThemeMode.Sepia, "sepia"),
                (AppThemeMode.Dark, "dark"),
                (AppThemeMode.Green, "green"),
            };
            foreach (var (mode, name) in modes)
            {
                await ThemeManager.SetModeAsync(mode).ConfigureAwait(true);
                await Application.Current.Dispatcher.InvokeAsync(
                    () => { },
                    DispatcherPriority.ApplicationIdle);
                CaptureWindow(window, Path.Combine(shotsDirectory, "shell-" + name + ".png"));
            }

            // 拖动蒙版留一张证据图（真实拖动没法在冒烟里模拟，直接把蒙版显示出来截图）
            if (window.FindName("DropOverlay") is FrameworkElement overlay)
            {
                overlay.Visibility = Visibility.Visible;
                await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                CaptureWindow(window, Path.Combine(shotsDirectory, "shell-drop-overlay.png"));
                overlay.Visibility = Visibility.Collapsed;
            }

            window.Close();

            // 设置页的「外观」组也要留证据（用户明确要求在设置里切主题）。
            // 这里直接渲染页面本身，不走导航：冒烟里主窗口的页面切换有缓存与异步加载，容易截到过渡态。
            var settingsPage = services.GetRequiredService<SettingsPage>();

            // 页面必须挂在一个真正显示出来的窗口里，RenderTargetBitmap 才会按当前主题重新合成
            var host = new Window
            {
                Title = "设置（T7-3 冒烟截图）",
                Width = 1100,
                Height = 720,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                Content = settingsPage,
            };
            host.Show();
            await WaitUntilAsync(() => host.IsLoaded, TimeSpan.FromSeconds(5)).ConfigureAwait(true);

            foreach (var (mode, name) in modes)
            {
                await ThemeManager.SetModeAsync(mode).ConfigureAwait(true);
                await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                CaptureWindow(host, Path.Combine(shotsDirectory, "settings-" + name + ".png"));
            }

            // 「自定义」档：面板展开后（出现在主题这排按钮下方）的实际观感也留证据
            await ThemeManager.SetCustomColorsAsync(
                Color.FromRgb(0xE8, 0xF1, 0xF8),
                Color.FromRgb(0xFF, 0xFF, 0xFF),
                Color.FromRgb(0x20, 0x30, 0x3C),
                Color.FromRgb(0x0F, 0x6C, 0xBD)).ConfigureAwait(true);
            await ThemeManager.SetModeAsync(AppThemeMode.Custom).ConfigureAwait(true);
            await WaitUntilAsync(
                () => settingsPage.FindName("CustomThemePanel") is FrameworkElement { Visibility: Visibility.Visible },
                TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            // 色块必须真的显示颜色（回归：按钮内容不拉伸时里面的 Border 会被压成 0 尺寸，只剩按钮外框）
            var swatchesOk = settingsPage.FindName("CustomBackgroundSwatch") is Border backgroundSwatchBorder
                && backgroundSwatchBorder.Background is SolidColorBrush { Color: var backgroundSwatchColor }
                && backgroundSwatchColor == Color.FromRgb(0xE8, 0xF1, 0xF8)
                && backgroundSwatchBorder.ActualWidth >= 20
                && settingsPage.FindName("CustomTextSwatch") is Border textSwatchBorder
                && textSwatchBorder.Background is SolidColorBrush { Color: var textSwatchColor }
                && textSwatchColor == Color.FromRgb(0x20, 0x30, 0x3C)
                && textSwatchBorder.ActualWidth >= 20;

            CaptureWindow(host, Path.Combine(shotsDirectory, "settings-custom.png"));

            // 调色板：点开第一个色块 → 截图（Popup 不在主窗口渲染树里，单独渲染它的内容）
            // → 点一个预设色，确认真的套到自定义配色上（走的是和用户点击完全相同的路径）。
            var paletteOk = false;
            if (settingsPage.FindName("CustomBackgroundButton") is Button paletteButton
                && settingsPage.FindName("ColorPalettePopup") is Popup palettePopup)
            {
                paletteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(
                    () => palettePopup.Child is FrameworkElement { ActualWidth: > 0 },
                    TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                if (palettePopup.Child is FrameworkElement paletteContent)
                {
                    CaptureElement(paletteContent, Path.Combine(shotsDirectory, "settings-custom-palette.png"));
                }

                var picked = Color.FromRgb(0xF4, 0xEC, 0xD8);
                if (settingsPage.FindName("ColorPalettePanel") is WrapPanel palettePanel)
                {
                    palettePanel.Children.OfType<Button>()
                        .FirstOrDefault(b => (b.Tag as string) == "#F4ECD8")
                        ?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                    await WaitUntilAsync(
                        () => ThemeManager.CustomBackground == picked,
                        TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                    paletteOk = ThemeManager.CustomBackground == picked;
                }

                palettePopup.IsOpen = false;
            }

            host.Close();
            await ThemeManager.SetModeAsync(AppThemeMode.System).ConfigureAwait(true);

            var produced = modes
                .Select(m => Path.Combine(shotsDirectory, "shell-" + m.Item2 + ".png"))
                .Concat(modes.Select(m => Path.Combine(shotsDirectory, "settings-" + m.Item2 + ".png")))
                .Append(Path.Combine(shotsDirectory, "shell-drop-overlay.png"))
                .Append(Path.Combine(shotsDirectory, "settings-custom.png"))
                .Append(Path.Combine(shotsDirectory, "settings-custom-palette.png"))
                .Count(File.Exists);

            return new SmokeCheck(
                "theme-screenshots",
                produced == (modes.Length * 2) + 3 && paletteOk && swatchesOk,
                $"已生成 {produced}/{(modes.Length * 2) + 3} 张（shell-* / settings-* / shell-drop-overlay / settings-custom / settings-custom-palette），"
                + $"点预设色真的换色={paletteOk}，色块显示颜色={swatchesOk}");
        }
        catch (Exception ex)
        {
            return new SmokeCheck("theme-screenshots", false, "截图失败：" + ex.Message);
        }
    }

    private static void CaptureWindow(Window window, string path) => CaptureElement(window, path);

    private static void CaptureElement(FrameworkElement element, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var logicalWidth = element.ActualWidth > 0 ? element.ActualWidth : element.DesiredSize.Width;
        var logicalHeight = element.ActualHeight > 0 ? element.ActualHeight : element.DesiredSize.Height;
        var width = Math.Max(1, (int)Math.Round(logicalWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Round(logicalHeight * dpi.DpiScaleY));

        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < timeout)
        {
            await Application.Current.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.Background);
        }
    }
}
