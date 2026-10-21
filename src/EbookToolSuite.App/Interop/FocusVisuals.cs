using System.Windows;

namespace EbookToolSuite.App.Interop;

/// <summary>
/// 焦点环只在键盘导航时显示（设计文档 9.9.4）。
/// </summary>
/// <remarks>
/// 为什么要这个东西：WPF 里 IsKeyboardFocusWithin 在**鼠标点击**时同样为真。
/// 直接用它在模板里显示焦点环，用户点一下导航项就会冒出一个蓝圈，观感很廉价。
/// 做法是额外维护一个"当前输入是不是键盘"的开关，模板里两个条件都满足才显示。
/// </remarks>
public static class FocusVisuals
{
    /// <summary>true 表示最近一次输入来自键盘。挂在 Window 上，模板里向祖先取。</summary>
    public static readonly DependencyProperty KeyboardModeProperty =
        DependencyProperty.RegisterAttached(
            "KeyboardMode",
            typeof(bool),
            typeof(FocusVisuals),
            new PropertyMetadata(true));

    public static void SetKeyboardMode(DependencyObject element, bool value)
        => element.SetValue(KeyboardModeProperty, value);

    public static bool GetKeyboardMode(DependencyObject element)
        => (bool)element.GetValue(KeyboardModeProperty);

    /// <summary>在 Window 的构造函数里调用一次。</summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.PreviewMouseDown += (_, _) => SetKeyboardMode(window, false);
        window.PreviewKeyDown += (_, _) => SetKeyboardMode(window, true);
    }
}