using System.Windows;
using System.Windows.Controls;

namespace EbookToolSuite.App.Controls;

/// <summary>
/// 统一的加载态（T7-3）：一条不确定进度条 +「正在做什么」的人话说明。
/// 三态里的"加载"以前要么没有、要么只有一行灰字，抽成控件后活动/备份/队列共用。
/// </summary>
public partial class LoadingState : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LoadingState),
        new PropertyMetadata("正在加载…"));

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail), typeof(string), typeof(LoadingState),
        new PropertyMetadata(string.Empty));

    public LoadingState() => InitializeComponent();

    /// <summary>主文案，例如「正在读取还原点…」。</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>补充说明，可空。</summary>
    public string Detail
    {
        get => (string)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }
}
