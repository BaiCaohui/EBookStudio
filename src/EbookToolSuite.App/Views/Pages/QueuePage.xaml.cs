using System.IO;
using System.Windows;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.IO;
using Microsoft.Win32;
using Serilog;
using System.Windows.Controls;
using EbookToolSuite.App.Navigation;
using EbookToolSuite.App.ViewModels;

namespace EbookToolSuite.App.Views.Pages;

/// <summary>
/// 转换队列页（执行计划 T2-4 第 5 条）。
/// 2026-10-04 起所有转换都排进队列，所以这里是唯一的进度与结果观景台。
/// </summary>
public partial class QueuePage : UserControl, INavigationPage
{
    private readonly ConversionQueueViewModel _vm;
    private readonly IShellLauncher _shell;

    public QueuePage(ConversionQueueViewModel vm, IShellLauncher shell)
    {
        _vm = vm;
        _shell = shell;

        InitializeComponent();

        DataContext = vm;
        vm.ViewLog = ShowLog;
        vm.OpenLocation = OpenLocation;
        vm.ExportOutput = ExportOutput;
    }

    public Task OnNavigatedToAsync(object? parameter, CancellationToken ct)
        => _vm.StartAsync();

    public Task<bool> CanNavigateAwayAsync() => Task.FromResult(true);

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await _vm.RefreshAsync().ConfigureAwait(true);

    /// <summary>
    /// 跳到文件位置。用 /select 打开资源管理器并选中文件，而不是只打开目录：
    /// 用户点这个按钮的意思就是"我要找这个文件"，让他自己在一堆文件里翻是没做完。
    /// </summary>
    private void OpenLocation(ConversionQueueRow? row)
    {
        if (row is null || !row.CanOpenOutput)
        {
            return;
        }

        try
        {
            _shell.RevealInExplorer(row.OutputPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开文件位置失败 {Path}", row.OutputPath);
            AppDialog.Show(
                Window.GetWindow(this),
                "打不开文件位置：" + ex.SafeMessage(),
                "打开位置",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 导出文件：弹另存为，然后把产物**复制**过去（原文件不动，队列里的记录也不受影响）。
    /// 刻意用复制而不是移动：队列里那条记录还指着原路径，移动会让它变成失效记录。
    /// </summary>
    private async void ExportOutput(ConversionQueueRow? row)
    {
        if (row is null || !row.CanOpenOutput)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出文件",
            FileName = Path.GetFileName(row.OutputPath),
            DefaultExt = Path.GetExtension(row.OutputPath),
            Filter = "所有文件 (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(row.OutputPath),
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            // T7-3：大文件在 UI 线程同步 File.Copy 会把界面冻住；改成后台复制 + 统一忙碌反馈
            using (BusyScope.Begin("正在导出 " + Path.GetFileName(row.OutputPath) + "…"))
            {
                var source = row.OutputPath;
                var target = dialog.FileName;
                await Task.Run(() => AtomicFile.Copy(source, target)).ConfigureAwait(true);
            }

            AppDialog.Show(
                Window.GetWindow(this),
                "已导出到：" + dialog.FileName,
                "导出文件",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "导出文件失败 {Source} -> {Target}", row.OutputPath, dialog.FileName);
            AppDialog.Show(
                Window.GetWindow(this),
                "导出失败：" + ex.SafeMessage(),
                "导出文件",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ShowLog(ConversionQueueRow? row)
    {
        if (row is null)
        {
            return;
        }

        var window = new ConversionLogWindow(row.Title, _vm.GetTaskOutput(row.Id))
        {
            Owner = Window.GetWindow(this),
        };

        window.ShowDialog();
    }
}
