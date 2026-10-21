using System.Globalization;
using System.Windows;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.App.Views;

/// <summary>
/// 自建"替换预览"确认弹窗（执行计划 T5-3）：标题「替换预览」，正文第一行「将替换 N 处」，
/// 下面逐条列出前 20 处上下文（第几章 + 命中前 20 字 + 命中 + 命中后 20 字）。
/// 点【确认替换】才动手；点【取消】则一个字节都不改。
/// 注意：这里**只用字面量样式**，不引用 StaticResource  跨窗口的 StaticResource 会抛
/// XamlParseException（T3-UI 线上排障踩过，与 ConfirmAutoFixDialog 同一理由）。
/// </summary>
public partial class ReplacePreviewDialog : Window
{
    public ReplacePreviewDialog(int totalMatches, IReadOnlyList<FindReplacePreviewItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        InitializeComponent();

        TotalMatches = totalMatches;
        Items = items;

        MessageText.Text = "将替换 " + totalMatches.ToString(CultureInfo.InvariantCulture) + " 处";
        PreviewList.ItemsSource = items
            .Take(20)
            .Select((item, ordinal) => Format(item, ordinal))
            .ToList();
    }

    /// <summary>弹窗里那句"将替换 N 处"的 N（自动化核对用）。</summary>
    public int TotalMatches { get; }

    /// <summary>这次预览的全部命中上下文（最多前 20 处，自动化核对用）。</summary>
    public IReadOnlyList<FindReplacePreviewItem> Items { get; }

    /// <summary>用户点了【确认替换】（自动化核对用；没点过就是 false）。</summary>
    public bool Confirmed { get; private set; }

    private static string Format(FindReplacePreviewItem item, int ordinal)
        => "第 " + (item.ChapterIndex + 1).ToString(CultureInfo.InvariantCulture)
            + " 章（第 " + (ordinal + 1).ToString(CultureInfo.InvariantCulture) + " 处）："
            + item.Before + "【" + item.Match + "】" + item.After + "";

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
