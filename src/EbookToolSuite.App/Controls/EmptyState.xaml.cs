using System.Windows;
using System.Windows.Controls;

namespace EbookToolSuite.App.Controls;

/// <summary>
/// 空状态 / 占位页的统一样子。规格见设计文档 9.9.12。
/// </summary>
/// <remarks>
/// 占位页不能是"一片空白加一行灰字"，那看起来像软件坏了。
/// 图标 + 标题 + 一句说明 + 徽标（"将在 Tx-y 实现"），四件套成本很低但观感完全不同。
/// </remarks>
public partial class EmptyState : UserControl
{
    public static readonly DependencyProperty IconGlyphProperty =
        DependencyProperty.Register(nameof(IconGlyph), typeof(string), typeof(EmptyState),
            new PropertyMetadata("\uE946"));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty BodyProperty =
        DependencyProperty.Register(nameof(Body), typeof(string), typeof(EmptyState),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty BadgeProperty =
        DependencyProperty.Register(nameof(Badge), typeof(string), typeof(EmptyState),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ExtraProperty =
        DependencyProperty.Register(nameof(Extra), typeof(object), typeof(EmptyState),
            new PropertyMetadata(null));

    public EmptyState() => InitializeComponent();

    /// <summary>图标字形（Segoe Fluent Icons 码位，见 NavIcons）。</summary>
    public string IconGlyph
    {
        get => (string)GetValue(IconGlyphProperty);
        set => SetValue(IconGlyphProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>一句说明。不要写"功能开发中"这种废话，写这个页面将来会做什么。</summary>
    public string Body
    {
        get => (string)GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    /// <summary>徽标文字，例如 "将在 T2-4 实现"。空字符串则不显示徽标。</summary>
    public string Badge
    {
        get => (string)GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    /// <summary>追加内容槽。放按钮之类。</summary>
    public object? Extra
    {
        get => GetValue(ExtraProperty);
        set => SetValue(ExtraProperty, value);
    }
}