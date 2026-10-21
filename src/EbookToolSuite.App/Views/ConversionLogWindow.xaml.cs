using System.Windows;

namespace EbookToolSuite.App.Views;

/// <summary>
/// 「查看日志」窗口：显示某条转换任务本次运行的 Calibre 原始输出（stdout + stderr）。
/// 执行计划 T2-4 判据 f 要的就是这个：失败时用户能看到 Calibre 到底说了什么，
/// 而不是只看到一句「转换失败」。
/// </summary>
public partial class ConversionLogWindow : Window
{
    private const string MissingText =
        "这次运行还没有输出。\n\n"
        + "（任务还在排队的话，等它开始跑就有了；若转换成功却还是空白，说明进程没产生任何输出。）";

    public ConversionLogWindow(string title, string? output)
    {
        InitializeComponent();

        BookText.Text = title;
        LogBox.Text = string.IsNullOrWhiteSpace(output) ? MissingText : output;

        LogBox.CaretIndex = 0;
        LogBox.ScrollToHome();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogBox.Text);
            CopyButton.Content = "已复制";
        }
        catch (Exception)
        {
            // 剪贴板被别的进程占着时 SetText 会抛，这里不值得打扰用户
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
