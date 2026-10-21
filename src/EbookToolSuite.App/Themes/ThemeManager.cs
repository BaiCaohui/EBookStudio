using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using EbookToolSuite.Core.Interfaces;
using Microsoft.Win32;

namespace EbookToolSuite.App.Themes;

/// <summary>
/// 主题管理（T7-3）：六档（跟随系统 / 亮 / 米黄 / 暗 / 护眼绿 / 自定义）+ 运行时热切换 + 跟随系统强调色。
///
/// 机制没变：主题字典整份替换（各文件的 key 完全一致），控件里一律用 DynamicResource，
/// 所以替换完已打开的窗口会自动跟着变，不需要逐个窗口刷。
/// 「自定义」没有 XAML 文件：用户选的四个颜色在 <see cref="BuildCustomDictionary"/> 里生成同 key 的字典，
/// 卡片/描边/二级文字/悬停色按这四个颜色自动推导。
/// 标题栏归系统画，靠 DwmDarkMode 单独切。
/// </summary>
public static class ThemeManager
{
    private const string LightSource = "Themes/Colors.xaml";
    private const string DarkSource = "Themes/Dark.xaml";
    private const string GreenSource = "Themes/Green.xaml";
    private const string SepiaSource = "Themes/Sepia.xaml";

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKey = @"Software\Microsoft\Windows\CurrentVersion\DWM";

    /// <summary>主题档位（"system" / "light" / "sepia" / "dark" / "green" / "custom"）。</summary>
    public const string ThemeConfigKey = "ui.theme";

    /// <summary>强调色是否跟随系统（默认 true）。</summary>
    public const string AccentFollowConfigKey = "ui.accentFollowSystem";

    /// <summary>自定义配色：窗口底色（#RRGGBB）。</summary>
    public const string CustomBackgroundConfigKey = "ui.customTheme.background";

    /// <summary>自定义配色：卡片底色。</summary>
    public const string CustomCardConfigKey = "ui.customTheme.card";

    /// <summary>自定义配色：正文字色。</summary>
    public const string CustomTextConfigKey = "ui.customTheme.text";

    /// <summary>自定义配色：强调色。</summary>
    public const string CustomAccentConfigKey = "ui.customTheme.accent";

    private static readonly Color LightAccent = Color.FromRgb(0x00, 0x67, 0xC0);
    private static readonly Color DarkAccent = Color.FromRgb(0x4C, 0xC2, 0xFF);
    private static readonly Color GreenAccent = Color.FromRgb(0x2D, 0x5A, 0x27);
    private static readonly Color SepiaAccent = Color.FromRgb(0x8B, 0x5A, 0x2B);

    /// <summary>自定义配色的出厂值（= 亮色档），首次选「自定义」时会被当前主题色覆盖。</summary>
    private static readonly Color DefaultCustomBackground = Color.FromRgb(0xF3, 0xF3, 0xF3);

    private static readonly Color DefaultCustomCard = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color DefaultCustomText = Color.FromRgb(0x1A, 0x1A, 0x1A);
    private static readonly Color DefaultCustomAccent = Color.FromRgb(0x00, 0x67, 0xC0);

    private static IConfigStore? _config;
    private static bool _eventsHooked;

    /// <summary>用户选的档位（跟随系统 / 亮 / 米黄 / 暗 / 护眼 / 自定义）。</summary>
    public static AppThemeMode Mode { get; private set; } = AppThemeMode.System;

    /// <summary>当前实际是不是暗色（米黄/护眼算浅色；自定义按所选底色的亮度判断，标题栏用同一判定）。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>强调色是否跟随系统。关掉就用默认蓝 / 护眼绿 / 米黄棕 / 自定义强调色。</summary>
    public static bool AccentFollowsSystem { get; private set; } = true;

    /// <summary>自定义配色的窗口底色。</summary>
    public static Color CustomBackground { get; private set; } = DefaultCustomBackground;

    /// <summary>自定义配色的卡片底色。</summary>
    public static Color CustomCard { get; private set; } = DefaultCustomCard;

    /// <summary>自定义配色的正文字色。</summary>
    public static Color CustomText { get; private set; } = DefaultCustomText;

    /// <summary>自定义配色的强调色。</summary>
    public static Color CustomAccent { get; private set; } = DefaultCustomAccent;

    /// <summary>主题实际生效后触发（设置页用它刷新单选状态）。</summary>
    public static event EventHandler? Changed;

    /// <summary>系统是否用浅色。读不到（非 Windows 或键不存在）时按浅色处理。</summary>
    public static bool IsSystemLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception)
        {
            // 读注册表失败不是错误：回落到浅色，不影响启动
            return true;
        }
    }

    /// <summary>
    /// 启动时调用一次：读配置、挂系统事件、把主题应用上去。
    /// 必须在建任何窗口之前调用（App.OnStartup 里）。
    /// </summary>
    public static void Initialize(IConfigStore config)
    {
        _config = config;
        Mode = ParseMode(config.GetString(ThemeConfigKey, "system"));
        AccentFollowsSystem = config.GetBool(AccentFollowConfigKey, true);

        CustomBackground = ReadStoredColor(config, CustomBackgroundConfigKey, DefaultCustomBackground);
        CustomCard = ReadStoredColor(config, CustomCardConfigKey, DefaultCustomCard);
        CustomText = ReadStoredColor(config, CustomTextConfigKey, DefaultCustomText);
        CustomAccent = ReadStoredColor(config, CustomAccentConfigKey, DefaultCustomAccent);

        HookSystemEvents();
        Apply();
    }

    /// <summary>切换档位并落盘。失败只记日志，不抛给界面。</summary>
    public static async Task SetModeAsync(AppThemeMode mode, CancellationToken ct = default)
    {
        Mode = mode;
        Apply();

        if (_config is null)
        {
            return;
        }

        try
        {
            await _config.SetAsync(ThemeConfigKey, JsonValue.Create(ToConfigValue(mode)), ct)
                .ConfigureAwait(true);
            await _config.SaveAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "保存主题设置失败：{Mode}", ToConfigValue(mode));
        }
    }

    /// <summary>强调色跟随系统开关并落盘。</summary>
    public static async Task SetAccentFollowsSystemAsync(bool follow, CancellationToken ct = default)
    {
        AccentFollowsSystem = follow;
        Apply();

        if (_config is null)
        {
            return;
        }

        try
        {
            await _config.SetAsync(AccentFollowConfigKey, JsonValue.Create(follow), ct)
                .ConfigureAwait(true);
            await _config.SaveAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "保存强调色设置失败：{Follow}", follow);
        }
    }

    /// <summary>保存自定义配色的四个颜色并立即生效（设置页每改一项就调一次）。</summary>
    public static async Task SetCustomColorsAsync(
        Color background,
        Color card,
        Color text,
        Color accent,
        CancellationToken ct = default)
    {
        CustomBackground = Opaque(background);
        CustomCard = Opaque(card);
        CustomText = Opaque(text);
        CustomAccent = Opaque(accent);

        // 只在「自定义」档生效时重画；用户在别的档调颜色不该把当前主题换掉
        if (Mode == AppThemeMode.Custom)
        {
            Apply();
        }

        await SaveCustomColorsAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 第一次选「自定义」时，以当前生效主题的颜色作为起点（用户不用从白底重新配色）。
    /// 已经保存过自定义配色就直接用保存值。返回是否发生了播种。
    /// </summary>
    public static async Task<bool> SeedCustomColorsIfNeededAsync(CancellationToken ct = default)
    {
        if (_config is not null
            && !string.IsNullOrWhiteSpace(_config.GetString(CustomBackgroundConfigKey, null)))
        {
            return false;
        }

        var app = Application.Current;
        if (app is null)
        {
            return false;
        }

        CustomBackground = ReadBrushColor(app, "WindowBackgroundBrush", CustomBackground);
        CustomCard = ReadBrushColor(app, "CardBackgroundBrush", CustomCard);
        CustomText = ReadBrushColor(app, "TextPrimaryBrush", CustomText);
        CustomAccent = ReadBrushColor(app, "AccentColorBrush", CustomAccent);

        await SaveCustomColorsAsync(ct).ConfigureAwait(true);
        return true;
    }

    /// <summary>配置字符串 → 档位。非法值一律回落跟随系统。</summary>
    public static AppThemeMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => AppThemeMode.Light,
        "sepia" => AppThemeMode.Sepia,
        "dark" => AppThemeMode.Dark,
        "green" => AppThemeMode.Green,
        "custom" => AppThemeMode.Custom,
        _ => AppThemeMode.System,
    };

    /// <summary>档位 → 配置字符串。</summary>
    public static string ToConfigValue(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Light => "light",
        AppThemeMode.Sepia => "sepia",
        AppThemeMode.Dark => "dark",
        AppThemeMode.Green => "green",
        AppThemeMode.Custom => "custom",
        _ => "system",
    };

    /// <summary>颜色 → "#RRGGBB"（设置页色值框与配置文件共用）。</summary>
    public static string FormatColor(Color color)
        => "#" + color.R.ToString("X2", CultureInfo.InvariantCulture)
            + color.G.ToString("X2", CultureInfo.InvariantCulture)
            + color.B.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>解析 "#RRGGBB" / "#AARRGGBB"（可省略 #）。alpha 一律忽略，自定义配色都按不透明处理。</summary>
    public static bool TryParseColor(string? value, out Color color)
    {
        color = default;

        var text = (value ?? string.Empty).Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        if (text.Length is not (6 or 8)
            || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
        {
            return false;
        }

        color = Color.FromRgb((byte)((raw >> 16) & 0xFF), (byte)((raw >> 8) & 0xFF), (byte)(raw & 0xFF));
        return true;
    }

    /// <summary>当前生效的是哪一套配色（跟随系统已解析成实际值；自定义就是自定义）。</summary>
    public static AppThemeMode EffectiveMode => Mode == AppThemeMode.System
        ? (IsSystemLight() ? AppThemeMode.Light : AppThemeMode.Dark)
        : Mode;

    /// <summary>把当前档位应用上去：替换字典 + 强调色 + 标题栏。</summary>
    public static void Apply()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var effective = EffectiveMode;
        ResourceDictionary? themeDictionary = null;

        if (effective == AppThemeMode.Custom)
        {
            try
            {
                themeDictionary = BuildCustomDictionary();
                ReplaceThemeDictionary(app, themeDictionary);
            }
            catch (Exception ex)
            {
                // 生成失败不能挡住界面：下面统一按浅色兜底
                Serilog.Log.Warning(ex, "生成自定义主题字典失败，回退浅色");
                themeDictionary = null;
            }
        }
        else
        {
            var source = effective switch
            {
                AppThemeMode.Dark => DarkSource,
                AppThemeMode.Green => GreenSource,
                AppThemeMode.Sepia => SepiaSource,
                _ => LightSource,
            };

            if (!TryReplaceTheme(app, source, out themeDictionary))
            {
                // 目标主题加载失败：尝试浅色兜底；再不行就保留现状，只记日志，绝不让主题挡住启动
                if (!string.Equals(source, LightSource, StringComparison.OrdinalIgnoreCase)
                    && TryReplaceTheme(app, LightSource, out themeDictionary))
                {
                    effective = AppThemeMode.Light;
                    Serilog.Log.Warning("主题资源加载失败，已回退到默认浅色主题");
                }
            }
        }

        if (themeDictionary is null)
        {
            if (!TryReplaceTheme(app, LightSource, out themeDictionary))
            {
                Serilog.Log.Error("主题资源全部加载失败，界面将使用 App.xaml 里预置的默认资源");
                return;
            }

            effective = AppThemeMode.Light;
        }

        IsDark = effective switch
        {
            AppThemeMode.Dark => true,
            AppThemeMode.Custom => RelativeLuminance(CustomBackground) < 0.5,
            _ => false,
        };

        ApplyAccent(app, themeDictionary, effective);
        ApplyTitleBars();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>把当前主题的窗口外观（背景 / 前景 / 字号 / 标题栏）同步给所有已打开的窗口。</summary>
    public static void ApplyTitleBars()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        foreach (Window window in app.Windows)
        {
            ApplyWindowChrome(window);
        }
    }

    /// <summary>
    /// 给单个窗口套主题（T7-3）。
    /// 为什么不只靠 `Style TargetType="Window"`：WPF 不会给 Window 隐式应用样式
    /// （实测 ConfirmSaveDialog 的 Background 仍是系统白），所以窗口这一层必须在代码里设。
    /// 用 SetResourceReference 挂 DynamicResource，主题一换自动跟着变。
    /// </summary>
    internal static void ApplyWindowChrome(Window window)
    {
        window.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextPrimaryBrush");

        // 统一基础字号（原来三个对话框有窗口级 15/18 的字号）
        window.FontSize = 13;

        DwmDarkMode.Apply(window, IsDark);
    }

    // ==================== 内部实现 ====================

    /// <summary>
    /// 换主题字典。先把目标字典读出来（失败就抛，由调用方兜底），
    /// 再在 MergedDictionaries 里原地替换掉旧的那一份——原地替换会触发资源失效，
    /// 所有 DynamicResource 立即重取，这就是"热切换"的全部魔法。
    /// </summary>
    private static bool TryReplaceTheme(Application app, string source, out ResourceDictionary dictionary)
    {
        dictionary = new ResourceDictionary();
        try
        {
            dictionary.Source = new Uri(source, UriKind.Relative);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "加载主题资源失败：{Source}", source);
            return false;
        }

        ReplaceThemeDictionary(app, dictionary);
        return true;
    }

    /// <summary>在 MergedDictionaries 里原地替换主题字典；找不到（理论上不会）就插到最前面。</summary>
    private static void ReplaceThemeDictionary(Application app, ResourceDictionary dictionary)
    {
        var merged = app.Resources.MergedDictionaries;
        for (var i = 0; i < merged.Count; i++)
        {
            if (IsThemeDictionary(merged[i]))
            {
                merged[i] = dictionary;
                return;
            }
        }

        merged.Insert(0, dictionary);
    }

    /// <summary>
    /// 自定义配色的完整 token 字典：先拿一套与底色深浅匹配的内置主题当底（语义色/遮罩/危险色沿用），
    /// 再覆盖用户能调的四项与由它们推导的中间色。key 集合因此与 XAML 主题完全一致。
    /// </summary>
    private static ResourceDictionary BuildCustomDictionary()
    {
        var background = CustomBackground;
        var card = CustomCard;
        var text = CustomText;
        var accent = CustomAccent;
        var darkBackground = RelativeLuminance(background) < 0.5;

        var baseDictionary = new ResourceDictionary
        {
            Source = new Uri(darkBackground ? DarkSource : LightSource, UriKind.Relative),
        };

        var result = new CustomThemeDictionary();

        foreach (var key in baseDictionary.Keys)
        {
            if (baseDictionary[key] is SolidColorBrush brush)
            {
                result[key] = new SolidColorBrush(brush.Color);
            }
        }

        result["WindowBackgroundBrush"] = Solid(background);
        // 左栏/顶栏：浅色底直接用卡片色（= 亮色档的 #FFFFFF）；深色底取底色与卡片色的中间值
        result["LayerBackgroundBrush"] = Solid(darkBackground ? Mix(background, card, 0.5) : card);
        result["CardBackgroundBrush"] = Solid(card);
        result["BorderSubtleBrush"] = Solid(DeriveCustomBorder(background, text));
        result["TextPrimaryBrush"] = Solid(text);
        result["TextSecondaryBrush"] = Solid(Mix(text, background, 0.35));
        result["TextTertiaryBrush"] = Solid(Mix(text, background, 0.55));
        result["AccentColorBrush"] = Solid(accent);
        result["AccentSurfaceBrush"] = Solid(WithAlpha(accent, 0x1F));
        result["AccentSurfaceHoverBrush"] = Solid(WithAlpha(accent, 0x29));
        result["SurfaceHoverBrush"] = Solid(WithAlpha(text, 0x0F));
        result["SurfacePressedBrush"] = Solid(WithAlpha(text, 0x17));
        result["InfoBrush"] = Solid(accent);

        return result;
    }

    /// <summary>
    /// 强调色：跟随系统时读 DWM 的 AccentColor（DWORD，ABGR），
    /// 对比度不够（跟窗口底色差太近）就回退默认色，避免"按钮看不见"。
    /// 覆盖字典必须排在主题字典**后面**（后合并的优先），并且是独立一份，
    /// 这样主题切换时它会跟着换、不会和主题字典里的默认强调色打架。
    /// </summary>
    private static void ApplyAccent(Application app, ResourceDictionary theme, AppThemeMode effective)
    {
        var merged = app.Resources.MergedDictionaries;
        var themeIndex = IndexOfTheme(merged);
        if (themeIndex < 0)
        {
            return;
        }

        var existing = IndexOfAccentOverride(merged);
        var fallback = effective switch
        {
            AppThemeMode.Dark => DarkAccent,
            AppThemeMode.Green => GreenAccent,
            AppThemeMode.Sepia => SepiaAccent,
            AppThemeMode.Custom => CustomAccent,
            _ => LightAccent,
        };

        var background = (theme["WindowBackgroundBrush"] as SolidColorBrush)?.Color
            ?? (effective == AppThemeMode.Dark ? Color.FromRgb(0x20, 0x20, 0x20) : Colors.White);

        var accent = fallback;

        // 护眼/米黄/自定义档不跟系统强调色：系统蓝配绿底/米黄底/用户自选色底像两套主题拼在一起，
        // 而且阅读器的对应主题本来就是各自强调色（reader.js 的 green / sepia 档）。
        if (effective is not (AppThemeMode.Green or AppThemeMode.Sepia or AppThemeMode.Custom)
            && AccentFollowsSystem
            && TryReadSystemAccent(out var systemAccent)
            && ContrastRatio(systemAccent, background) >= 3.0)
        {
            accent = systemAccent;
        }

        var overrideDictionary = new AccentOverrideDictionary
        {
            ["AccentColorBrush"] = new SolidColorBrush(accent),
            ["AccentSurfaceBrush"] = new SolidColorBrush(Color.FromArgb(0x1F, accent.R, accent.G, accent.B)),
            ["AccentSurfaceHoverBrush"] = new SolidColorBrush(Color.FromArgb(0x29, accent.R, accent.G, accent.B)),
        };

        if (existing >= 0)
        {
            merged[existing] = overrideDictionary;
            // 若它被别的操作挤到主题字典前面，重新插到后面
            if (existing < themeIndex)
            {
                merged.RemoveAt(existing);
                merged.Insert(themeIndex, overrideDictionary);
            }

            return;
        }

        merged.Insert(themeIndex + 1, overrideDictionary);
    }

    private static int IndexOfTheme(Collection<ResourceDictionary> merged)
    {
        for (var i = 0; i < merged.Count; i++)
        {
            if (IsThemeDictionary(merged[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOfAccentOverride(Collection<ResourceDictionary> merged)
    {
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i] is AccentOverrideDictionary)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>是不是一份"主题字典"（四份 XAML 主题 + 代码生成的自定义主题）。</summary>
    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        if (dictionary is CustomThemeDictionary)
        {
            return true;
        }

        var source = dictionary.Source?.OriginalString;
        return source is not null
            && (source.EndsWith("Colors.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("Green.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("Sepia.xaml", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>读系统强调色。读不到返回 false（用默认蓝）。</summary>
    private static bool TryReadSystemAccent(out Color accent)
    {
        accent = default;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DwmKey);
            if (key?.GetValue("AccentColor") is not int raw)
            {
                return false;
            }

            // DWM 的 AccentColor 是 ABGR：低字节是 R
            accent = Color.FromRgb((byte)(raw & 0xFF), (byte)((raw >> 8) & 0xFF), (byte)((raw >> 16) & 0xFF));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>WCAG 对比度（1..21）。</summary>
    private static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a) + 0.05;
        var lb = RelativeLuminance(b) + 0.05;
        return Math.Max(la, lb) / Math.Min(la, lb);
    }

    private static double RelativeLuminance(Color color) =>
        (0.2126 * LinearChannel(color.R)) + (0.7152 * LinearChannel(color.G)) + (0.0722 * LinearChannel(color.B));

    private static double LinearChannel(byte value)
    {
        var s = value / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    // ---------- 自定义配色的小工具 ----------

    /// <summary>
    /// 自定义配色的描边色。内置主题的 14% 混色是照各自底色调过的；用户自选四色时
    /// 底色与文字可能很接近，14% 会让按钮/输入框边框糊进底色里（用户 2026-10-21：
    /// 「自定义的时候按钮的边框要凸显出来」）。所以从 26% 起步，并按 WCAG 对比度
    /// 继续往文字色方向加，保证描边与窗口底色至少 1.5:1 的对比（封顶 50%）。
    /// 只有自定义档走这条规则，内置四套主题的描边一个像素都不变。
    /// </summary>
    private static Color DeriveCustomBorder(Color background, Color text)
    {
        var amount = 0.26;
        var border = Mix(background, text, amount);

        while (amount < 0.5 && ContrastRatio(border, background) < 1.5)
        {
            amount = Math.Min(0.5, amount + 0.06);
            border = Mix(background, text, amount);
        }

        return border;
    }

    private static Color ReadStoredColor(IConfigStore config, string key, Color fallback)
    {
        var stored = config.GetString(key, null);
        return TryParseColor(stored, out var color) ? color : fallback;
    }

    private static Color ReadBrushColor(Application app, string key, Color fallback)
        => app.TryFindResource(key) is SolidColorBrush brush ? Opaque(brush.Color) : fallback;

    private static Color Opaque(Color color) => Color.FromRgb(color.R, color.G, color.B);

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>线性混合：0 = from，1 = to。</summary>
    private static Color Mix(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + ((to.R - from.R) * t)),
            (byte)Math.Round(from.G + ((to.G - from.G) * t)),
            (byte)Math.Round(from.B + ((to.B - from.B) * t)));
    }

    private static SolidColorBrush Solid(Color color) => new(color);

    private static async Task SaveCustomColorsAsync(CancellationToken ct)
    {
        if (_config is null)
        {
            return;
        }

        try
        {
            await _config.SetAsync(CustomBackgroundConfigKey, JsonValue.Create(FormatColor(CustomBackground)), ct)
                .ConfigureAwait(true);
            await _config.SetAsync(CustomCardConfigKey, JsonValue.Create(FormatColor(CustomCard)), ct)
                .ConfigureAwait(true);
            await _config.SetAsync(CustomTextConfigKey, JsonValue.Create(FormatColor(CustomText)), ct)
                .ConfigureAwait(true);
            await _config.SetAsync(CustomAccentConfigKey, JsonValue.Create(FormatColor(CustomAccent)), ct)
                .ConfigureAwait(true);
            await _config.SaveAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "保存自定义配色失败");
        }
    }

    private static void HookSystemEvents()
    {
        if (_eventsHooked)
        {
            return;
        }

        _eventsHooked = true;

        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch (Exception ex)
        {
            // 订阅失败只影响"跟随系统"，主题本身照常工作
            Serilog.Log.Warning(ex, "订阅系统主题变化失败，跟随系统档将只在启动时生效");
        }

        // 所有窗口（含以后新建的对话框）加载完成后统一套主题 + 标题栏颜色
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window)
                {
                    ApplyWindowChrome(window);
                }
            }),
            handledEventsToo: true);
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General
            && e.Category != UserPreferenceCategory.Color
            && e.Category != UserPreferenceCategory.VisualStyle)
        {
            return;
        }

        var app = Application.Current;
        app?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Mode == AppThemeMode.System || AccentFollowsSystem)
            {
                Apply();
            }
        }));
    }

    /// <summary>标记类型：用来在 MergedDictionaries 里认出"强调色覆盖"那一份。</summary>
    private sealed class AccentOverrideDictionary : ResourceDictionary
    {
    }

    /// <summary>标记类型：用来在 MergedDictionaries 里认出"代码生成的自定义主题"那一份。</summary>
    private sealed class CustomThemeDictionary : ResourceDictionary
    {
    }
}
