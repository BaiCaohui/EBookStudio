using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace EbookToolSuite.App.Themes;

/// <summary>
/// AvalonEdit 编辑器的主题适配（用户 2026-10-21：TXT 全文编辑 / 章节源码编辑中间还是白底）。
///
/// AvalonEdit 这些颜色不全是 XAML 能直接挂的：底色/正文/行号在 TextEditor 上，
/// 选区色在 TextArea 这个模板子元素上。所以统一在这里按当前主题字典取色并赋值，
/// 主题热切换时（ThemeManager.Changed）再取一次——和 DynamicResource 等效。
///
/// 只认书库主题（Config.ui.theme），和阅读器主题（reader.theme）无关。
/// </summary>
internal static class AvalonTheme
{
    /// <summary>各高亮定义的原始前景色（第一次用到时记下来，换回浅色主题要还原）。</summary>
    private static readonly Dictionary<IHighlightingDefinition, Dictionary<string, HighlightingBrush?>> OriginalSyntaxColors
        = new();

    /// <summary>把编辑器底色/正文色/行号色/选区色换成书库主题色。InitializeComponent 之后调用。</summary>
    public static void Apply(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);

        Paint(editor);

        EventHandler onThemeChanged = (_, _) => Paint(editor);
        ThemeManager.Changed += onThemeChanged;
        editor.Unloaded += (_, _) => ThemeManager.Changed -= onThemeChanged;
    }

    private static void Paint(TextEditor editor)
    {
        var background = ResourceColor("LayerBackgroundBrush", Colors.White);
        editor.Background = new SolidColorBrush(background);
        editor.Foreground = ResourceBrush("TextPrimaryBrush", Colors.Black);
        editor.LineNumbersForeground = ResourceBrush("TextTertiaryBrush", Colors.Gray);

        PaintSyntaxColors(editor, background);

        if (editor.TextArea is not null)
        {
            // 半透明强调色垫在选中的文字下面：不换字色，选中的字仍然按正文色读（深浅主题都可读）
            var accent = ResourceColor("AccentColorBrush", Color.FromRgb(0x00, 0x67, 0xC0));
            editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B));
            editor.TextArea.SelectionForeground = null;
        }
    }

    /// <summary>
    /// AvalonEdit 的语法高亮配色是照白底设计的（XML 的元素名是纯蓝 #0000FF、注释是深灰）。
    /// 深色主题下这些颜色跟底色几乎一样暗，注释直接看不见（用户 2026-10-21 的同一类问题）。
    /// 这里按对比度把看不清的颜色往白/黑方向提，浅色主题则还原成原配色。
    /// </summary>
    private static void PaintSyntaxColors(TextEditor editor, Color background)
    {
        var definition = editor.SyntaxHighlighting;

        if (definition is null)
        {
            return;
        }

        if (!OriginalSyntaxColors.TryGetValue(definition, out var originals))
        {
            originals = new Dictionary<string, HighlightingBrush?>(StringComparer.Ordinal);

            foreach (var color in definition.NamedHighlightingColors)
            {
                originals[color.Name ?? string.Empty] = color.Foreground;
            }

            OriginalSyntaxColors[definition] = originals;
        }

        var dark = RelativeLuminance(background) < 0.5;

        foreach (var color in definition.NamedHighlightingColors)
        {
            if (!originals.TryGetValue(color.Name ?? string.Empty, out var original))
            {
                continue;
            }

            if (!dark || original is not SimpleHighlightingBrush simple)
            {
                color.Foreground = original;
                continue;
            }

            // GetColor 返回可空：拿不到具体色值就跳过（系统色刷子之类）
            var baseColor = simple.GetColor(null!);

            if (baseColor is null)
            {
                color.Foreground = original;
                continue;
            }

            color.Foreground = new SimpleHighlightingBrush(
                EnsureContrast(baseColor.Value, background, 3.0));
        }
    }

    /// <summary>对比度不够就往白（深底）或黑（浅底）方向混，直到读得清为止。</summary>
    private static Color EnsureContrast(Color color, Color background, double minimumRatio)
    {
        if (ContrastRatio(color, background) >= minimumRatio)
        {
            return color;
        }

        var target = RelativeLuminance(background) < 0.5 ? Colors.White : Colors.Black;

        for (var amount = 0.15; amount <= 1.0; amount += 0.15)
        {
            var mixed = Color.FromRgb(
                (byte)Math.Round(color.R + ((target.R - color.R) * amount)),
                (byte)Math.Round(color.G + ((target.G - color.G) * amount)),
                (byte)Math.Round(color.B + ((target.B - color.B) * amount)));

            if (ContrastRatio(mixed, background) >= minimumRatio)
            {
                return mixed;
            }
        }

        return target;
    }

    private static double ContrastRatio(Color a, Color b)
    {
        var lighter = Math.Max(RelativeLuminance(a), RelativeLuminance(b)) + 0.05;
        var darker = Math.Min(RelativeLuminance(a), RelativeLuminance(b)) + 0.05;
        return lighter / darker;
    }

    private static double RelativeLuminance(Color color)
        => (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));

    private static double Linear(byte value)
    {
        var s = value / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static Color ResourceColor(string key, Color fallback)
        => Application.Current?.TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;

    private static SolidColorBrush ResourceBrush(string key, Color fallback)
        => new(ResourceColor(key, fallback));
}
