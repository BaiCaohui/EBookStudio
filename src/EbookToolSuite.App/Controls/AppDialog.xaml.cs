using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EbookToolSuite.App.Navigation;
using EbookToolSuite.App.Themes;

namespace EbookToolSuite.App;

/// <summary>
/// 统一的提示 / 确认对话框（T7-3）。替掉全站 53 处系统 <see cref="MessageBox"/>：
/// 系统消息框不跟应用主题走，手动切暗色 / 护眼后会很突兀。
///
/// 刻意保持与 <see cref="MessageBox"/> 相同的入参与返回值（<see cref="MessageBoxButton"/> /
/// <see cref="MessageBoxImage"/> / <see cref="MessageBoxResult"/>），
/// 这样调用点只需要把系统消息框换成 AppDialog.Show，其余逻辑一个字都不用改。
/// </summary>
public partial class AppDialog : Window
{
    private MessageBoxResult _result = MessageBoxResult.Cancel;

    /// <summary>Esc 键对应的返回值（「取消」或「否」）。</summary>
    private MessageBoxResult _escapeResult = MessageBoxResult.Cancel;

    private AppDialog()
    {
        InitializeComponent();

        // 不用 Button.IsCancel：它和"我们自己的 Click 处理"会同时写 DialogResult，
        // 容易在窗口已关时抛 InvalidOperationException。Esc 自己处理更稳。
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                _result = _escapeResult;
                Close();
                e.Handled = true;
            }
        };
    }

    /// <summary>message box 风格的四参版本（最常用）。</summary>
    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon)
        => Show(Application.Current?.MainWindow, messageBoxText, caption, button, icon, defaultResult: null);

    /// <summary>带 owner 的版本。</summary>
    public static MessageBoxResult Show(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon)
        => Show(owner, messageBoxText, caption, button, icon, defaultResult: null);

    /// <summary>带 owner 的两参版本：只有「确定」。</summary>
    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption)
        => Show(owner, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>两参版本：只有「确定」。</summary>
    public static MessageBoxResult Show(string messageBoxText, string caption)
        => Show(messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>三参版本。</summary>
    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button)
        => Show(messageBoxText, caption, button, MessageBoxImage.Information);

    /// <summary>带 owner 的三参版本。</summary>
    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption, MessageBoxButton button)
        => Show(owner, messageBoxText, caption, button, MessageBoxImage.Information, defaultResult: null);

    /// <summary>带默认结果的五参版本（Esc / 关闭时用默认结果）。</summary>
    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon, MessageBoxResult? defaultResult)
        => Show(Application.Current?.MainWindow, messageBoxText, caption, button, icon, defaultResult);

    /// <summary>带 owner + 默认结果的六参版本。</summary>
    public static MessageBoxResult Show(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon, MessageBoxResult? defaultResult)
    {
        var dialog = Create(messageBoxText, caption, button, icon, defaultResult);
        if (owner is not null && !ReferenceEquals(owner, dialog) && owner.IsVisible)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        dialog.ShowDialog();
        return dialog._result;
    }

    /// <summary>警告 / 失败提示的便捷写法。</summary>
    public static MessageBoxResult Warn(Window? owner, string messageBoxText, string caption)
        => Show(owner, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>失败提示（危险色图标）。</summary>
    public static MessageBoxResult Error(Window? owner, string messageBoxText, string caption)
        => Show(owner, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.Error);

    /// <summary>是 / 否确认，选「否」返回 No。</summary>
    public static MessageBoxResult Confirm(Window? owner, string messageBoxText, string caption)
        => Show(owner, messageBoxText, caption, MessageBoxButton.YesNo, MessageBoxImage.Question);

    /// <summary>
    /// 自定义按钮文案的确认框：点 <paramref name="confirmText"/> 返回 true，
    /// 点 <paramref name="cancelText"/> 或 Esc 返回 false。
    /// </summary>
    /// <remarks>
    /// 为什么需要它：系统 YesNo 的「是 / 否」在「是否把这本书导入书库」这类**动作**上
    /// 语义不够明确，用户要看到的是「导入书库 / 取消」。只加一个入口，不动既有
    /// <see cref="Show"/> / <see cref="Confirm"/> 的行为（全站 50 多处调用不受影响）。
    /// </remarks>
    public static bool ConfirmCustom(
        Window? owner,
        string messageBoxText,
        string caption,
        string confirmText,
        string cancelText)
    {
        var dialog = new AppDialog
        {
            Title = string.IsNullOrWhiteSpace(caption) ? "提示" : caption,
        };

        dialog.TitleText.Text = dialog.Title;
        dialog.MessageText.Text = messageBoxText;

        ApplyIcon(dialog, MessageBoxImage.Question);

        dialog._escapeResult = MessageBoxResult.Cancel;
        dialog.AddButton(confirmText, MessageBoxResult.Yes, primary: true, isDefault: true);
        dialog.AddButton(cancelText, MessageBoxResult.Cancel, primary: false);

        if (owner is not null && owner.IsVisible)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        dialog.ShowDialog();
        return dialog._result == MessageBoxResult.Yes;
    }

    private static AppDialog Create(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult? defaultResult)
    {
        var dialog = new AppDialog
        {
            Title = string.IsNullOrWhiteSpace(caption) ? "提示" : caption,
        };

        dialog.TitleText.Text = dialog.Title;
        dialog.MessageText.Text = messageBoxText;

        ApplyIcon(dialog, icon);
        BuildButtons(dialog, button);

        // 显式指定了默认结果时以调用方为准（比如"关闭当否"）
        if (defaultResult.HasValue)
        {
            dialog._escapeResult = defaultResult.Value;
        }

        return dialog;
    }

    private static void ApplyIcon(AppDialog dialog, MessageBoxImage icon)
    {
        // 只用已验证存在的两个字形：E946 Info / E7BA Warning（见 NavIcons 的登记表）
        switch (icon)
        {
            case MessageBoxImage.Warning:
                dialog.IconText.Text = NavIcons.Warning;
                dialog.IconText.Foreground = Resolve("WarningBrush", Brushes.DarkOrange);
                break;

            case MessageBoxImage.Error:
                dialog.IconText.Text = NavIcons.Warning;
                dialog.IconText.Foreground = Resolve("DangerBackgroundBrush", Brushes.Firebrick);
                break;

            case MessageBoxImage.Question:
                dialog.IconText.Text = NavIcons.Info;
                dialog.IconText.Foreground = Resolve("AccentColorBrush", Brushes.SteelBlue);
                break;

            default:
                dialog.IconText.Text = NavIcons.Info;
                dialog.IconText.Foreground = Resolve("AccentColorBrush", Brushes.SteelBlue);
                break;
        }
    }

    private static Brush Resolve(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private static void BuildButtons(AppDialog dialog, MessageBoxButton button)
    {
        switch (button)
        {
            case MessageBoxButton.OKCancel:
                dialog._escapeResult = MessageBoxResult.Cancel;
                dialog.AddButton("确定", MessageBoxResult.OK, primary: true, isDefault: true);
                dialog.AddButton("取消", MessageBoxResult.Cancel, primary: false);
                break;

            case MessageBoxButton.YesNo:
                dialog._escapeResult = MessageBoxResult.No;
                dialog.AddButton("是", MessageBoxResult.Yes, primary: true, isDefault: true);
                dialog.AddButton("否", MessageBoxResult.No, primary: false);
                break;

            case MessageBoxButton.YesNoCancel:
                dialog._escapeResult = MessageBoxResult.Cancel;
                dialog.AddButton("是", MessageBoxResult.Yes, primary: true, isDefault: true);
                dialog.AddButton("否", MessageBoxResult.No, primary: false);
                dialog.AddButton("取消", MessageBoxResult.Cancel, primary: false);
                break;

            default:
                dialog._escapeResult = MessageBoxResult.OK;
                dialog.AddButton("确定", MessageBoxResult.OK, primary: true, isDefault: true);
                break;
        }
    }

    private void AddButton(
        string content,
        MessageBoxResult result,
        bool primary,
        bool isDefault = false)
    {
        var button = new Button
        {
            Content = content,
            MinWidth = 88,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault,
            Tag = result,
        };

        button.SetResourceReference(StyleProperty, primary ? "PrimaryButtonStyle" : "SecondaryButtonStyle");
        button.Click += (_, _) =>
        {
            _result = result;
            DialogResult = true;
        };

        ButtonHost.Children.Add(button);

        // 主按钮拿初始焦点（键盘用户一进来就能直接回车）
        if (isDefault)
        {
            Loaded += (_, _) => button.Focus();
        }
    }
}
