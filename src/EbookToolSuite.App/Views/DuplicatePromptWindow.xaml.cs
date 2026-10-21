using System.Windows;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.App.Views;

/// <summary>
/// 重复导入的对话框（执行计划 T2-1）。**整批只弹这一个**：
/// 导 200 本发现 15 本重复时，不能每本弹一个（会点到手断）。
/// 只有在用户选了"逐项决定"之后，才用同一套界面逐本问一次。
/// 用户直接点右上角关闭 = 保守处理（当跳过），不会误导入。
/// </summary>
public partial class DuplicatePromptWindow : Window
{
    private DuplicatePromptWindow()
    {
        InitializeComponent();
    }

    /// <summary>整批决策结果。窗口被直接关掉时保持默认的"跳过全部"。</summary>
    public DuplicatePolicy Policy { get; private set; } = DuplicatePolicy.SkipAll;

    /// <summary>逐项模式下"这一本是否仍要导入"。</summary>
    public bool ImportThisOne { get; private set; }

    public static DuplicatePolicy AskBatch(Window? owner, IReadOnlyList<ImportJob> duplicates)
    {
        var window = new DuplicatePromptWindow();
        window.ConfigureBatch(duplicates);
        window.AttachOwner(owner);
        window.ShowDialog();
        return window.Policy;
    }

    public static DuplicatePolicy AskSingle(Window? owner, ImportJob duplicate, int index, int total)
    {
        var window = new DuplicatePromptWindow();
        window.ConfigureSingle(duplicate, index, total);
        window.AttachOwner(owner);
        window.ShowDialog();
        return window.Policy;
    }

    private void ConfigureBatch(IReadOnlyList<ImportJob> duplicates)
    {
        Title = "发现重复";
        HeadlineText.Text = "发现 " + duplicates.Count + " 本已经在库中的书";
        DetailText.Text = "这里一次决定整批怎么处理。"
            + "跳过的话文件不会被复制、也不会写进书库。";

        ItemList.ItemsSource = duplicates.Select(Describe).ToList();

        SkipAllButton.Content = "跳过全部重复";
          ReplaceAllButton.Content = "全部替换";
          Height = 520;
          MinHeight = 400;
        ImportAllButton.Content = "全部都导入";
        EachButton.Visibility = Visibility.Visible;
    }

    private void ConfigureSingle(ImportJob duplicate, int index, int total)
    {
        Title = "重复的书";
        HeadlineText.Text = "第 " + index + " / " + total + " 本重复";
        DetailText.Text = "这一本要怎么处理？";

        ItemList.ItemsSource = new List<object> { Describe(duplicate) };

        SkipAllButton.Content = "跳过这一本";
          ReplaceAllButton.Content = "替换这一本";
          Height = 330;
          MinHeight = 300;
        ImportAllButton.Content = "仍然导入";
        EachButton.Visibility = Visibility.Collapsed;
    }

    private static object Describe(ImportJob job)
        => new
        {
            File = job.FileName,
            Detail = job.DuplicateOfBookId is not null && job.DuplicateOfTitle is not null
                ? "书库里已有《" + job.DuplicateOfTitle + "》"
                : "与本次前面的文件重复",
        };

    private void AttachOwner(Window? owner)
    {
        if (owner is not null && !ReferenceEquals(owner, this) && owner.IsLoaded)
        {
            Owner = owner;
        }
    }

private void OnReplaceAllClick(object sender, RoutedEventArgs e)
      {
          Policy = DuplicatePolicy.Replace;
          DialogResult = true;
      }

      private void OnSkipAllClick(object sender, RoutedEventArgs e)
    {
        Policy = DuplicatePolicy.SkipAll;
        ImportThisOne = false;
        DialogResult = true;
    }

    private void OnImportAllClick(object sender, RoutedEventArgs e)
    {
        Policy = DuplicatePolicy.ImportAll;
        Policy = DuplicatePolicy.ImportAll;
          ImportThisOne = true;
        DialogResult = true;
    }

    private void OnEachClick(object sender, RoutedEventArgs e)
    {
        Policy = DuplicatePolicy.AskEach;
        DialogResult = true;
    }
}