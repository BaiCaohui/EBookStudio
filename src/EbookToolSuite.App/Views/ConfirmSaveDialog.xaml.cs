using System.Windows;

namespace EbookToolSuite.App.Views;

/// <summary>
/// 自建"未保存"确认弹窗（替代 MessageBox）： MessageBox 的字号改不了，也不便加说明文字。
/// 文案按用户指定： "当前修改未保存，是否保存后退出。"，按钮 是 / 否 / 取消。
/// </summary>
public partial class ConfirmSaveDialog : Window
{
    public ConfirmSaveDialog(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    /// <summary>用户的选择。</summary>
    public ConfirmSaveChoice Choice { get; private set; } = ConfirmSaveChoice.Cancel;

    private void OnYes(object sender, RoutedEventArgs e)
    {
        Choice = ConfirmSaveChoice.SaveThenContinue;
        DialogResult = true;
    }

    private void OnNo(object sender, RoutedEventArgs e)
    {
        Choice = ConfirmSaveChoice.DiscardAndContinue;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice = ConfirmSaveChoice.Cancel;
        DialogResult = false;
    }
}

/// <summary>确认弹窗的三个选择。</summary>
public enum ConfirmSaveChoice
{
    /// <summary>是：先保存，再执行动作。</summary>
    SaveThenContinue,

    /// <summary>否：不保存，直接执行动作。</summary>
    DiscardAndContinue,

    /// <summary>取消：什么都不做。</summary>
    Cancel,
}
