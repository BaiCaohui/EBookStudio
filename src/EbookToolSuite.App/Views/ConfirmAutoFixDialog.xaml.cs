using System.Globalization;
using System.Windows;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.App.Views;

/// <summary>
/// 自建"自动修正确认"弹窗（T5-2W 第 5 步）：逐条列出"第几处、原文、改成什么、为什么"，
/// 用户点【确认修改】才动手；点【取消】则一个字节都不改。
/// 不用 MessageBox：它的字号改不了，条目多了也不能滚动（和 ConfirmSaveDialog 同一个理由）。
/// 注意：这里**只用字面量样式**，不引用 StaticResource  跨窗口的 StaticResource 会抛
/// XamlParseException（T3-UI 线上排障踩过）。
/// </summary>
public partial class ConfirmAutoFixDialog : Window
{
    public ConfirmAutoFixDialog(IReadOnlyList<SourceFix> fixes)
    {
        ArgumentNullException.ThrowIfNull(fixes);

        InitializeComponent();

        Fixes = fixes;
        FixList.ItemsSource = fixes.Select(fix => fix.Display).ToList();
        MessageText.Text = "这一章有 "
            + fixes.Count.ToString(CultureInfo.InvariantCulture)
            + " 处需要自动修正，改法如下：";
    }

    /// <summary>弹窗逐条展示的修正项（自动化核对用）。</summary>
    internal IReadOnlyList<SourceFix> Fixes { get; }

    /// <summary>用户点了【确认修改】（自动化核对用；没点过就是 false）。</summary>
    public bool Confirmed { get; private set; }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }
}