using System.Windows;
using System.Windows.Controls;

namespace EbookToolSuite.App.Controls;

/// <summary>
/// 统一的错误态（T7-3）：标题 + 人话说明 +「重试」按钮。
/// 以前活动/备份页出错只会弹系统消息框，页面本身还是空白。
/// </summary>
public partial class ErrorState : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(ErrorState),
        new PropertyMetadata("这一页没能加载出来"));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(ErrorState),
        new PropertyMetadata(string.Empty));

    public ErrorState() => InitializeComponent();

    /// <summary>用户点「重试」时触发，由页面重新加载。</summary>
    public event EventHandler? Retry;

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => Retry?.Invoke(this, EventArgs.Empty);
}
