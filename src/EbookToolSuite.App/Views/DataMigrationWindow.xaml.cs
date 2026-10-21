using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EbookToolSuite.App.Services;
using EbookToolSuite.App.Settings;
using EbookToolSuite.Core.Interfaces;

namespace EbookToolSuite.App.Views;

/// <summary>
/// 数据目录迁移窗口（设计文档 §4.4 / 执行计划 T7-4）。
///
/// 界面只做三件事：跑前置检查并逐条展示、后台复制并显示进度、成功后提示重启。
/// 真正的复制 / 校验 / 回滚全在 <see cref="IDataDirectoryMigrationService"/> 里，
/// 这样冒烟可以不点按钮、直接驱动同一个服务与同一个窗口的测试钩子。
/// </summary>
public partial class DataMigrationWindow : Window
{
    private readonly IAppPaths _paths;
    private readonly IDataDirectoryMigrationService _migration;
    private readonly IShellLauncher _shell;

    private CancellationTokenSource? _cts;
    private MigrationPreflight? _lastPreflight;
    private bool _busy;

    public DataMigrationWindow(
        IAppPaths paths,
        IConfigStore config,
        IDataDirectoryMigrationService migration,
        IShellLauncher shell)
    {
        _paths = paths;
        _migration = migration;
        _shell = shell;

        InitializeComponent();

        CurrentDirText.Text = migration.CurrentDataDir;

        // 配置在这里的唯一用途：把「连缓存一起搬」的代价说清楚（缓存上限来自设置页）
        var cacheLimitMb = AppSettingsKeys.NormalizeCacheLimitMb(
            AppSettingsKeys.GetInt(config, AppSettingsKeys.CacheLimitMb, AppSettingsKeys.DefaultCacheLimitMb));
        IncludeCachesCheck.Content =
            $"连缓存一起搬（Cache / Temp，缓存上限约 {cacheLimitMb} MB；WebView2Data 始终不搬）";

        Loaded += OnLoaded;
    }

    // ---------- 测试钩子（冒烟用：不弹模态框，不依赖真实的用户点击） ----------

    /// <summary>目标目录输入框。x:Name 是 <c>TargetBoxControl</c>，这里按约定暴露成 TargetBox。</summary>
    internal TextBox TargetBox => TargetBoxControl;

    internal bool CanStartForTest => StartButtonControl.IsEnabled;

    /// <summary>跑一次前置检查并把 6 条结果填进界面，返回检查汇总。</summary>
    internal async Task<MigrationPreflight> RunPreflightForTestAsync(string target)
    {
        TargetBoxControl.Text = target;
        var preflight = await _migration.PreflightAsync(target).ConfigureAwait(true);
        ShowChecks(preflight);
        UpdateSwitchHint(target);
        return preflight;
    }

    /// <summary>直接驱动一次迁移，返回是否成功；不弹任何模态框（取消视为 false）。</summary>
    internal async Task<bool> RunMigrationForTestAsync(string target, bool includeCaches, CancellationToken ct)
    {
        try
        {
            var result = await _migration.MigrateAsync(target, includeCaches, progress: null, ct)
                .ConfigureAwait(true);

            return result.Success;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // ---------- 生命周期 ----------

    protected override void OnClosing(CancelEventArgs e)
    {
        // 迁移过程中不允许关窗：复制还在后台跑，关掉窗口只会让用户看不到回滚结果
        if (_busy)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            var usage = await Task.Run(() => MeasureDirectory(_paths.DataRoot)).ConfigureAwait(true);
            CurrentUsageText.Text =
                $"占用约 {FormatBytes(usage.Bytes)}，共 {usage.Files} 个文件。";
        }
        catch (Exception ex)
        {
            CurrentUsageText.Text = "占用统计失败：" + ex.Message;
        }
    }

    // ---------- 检查 ----------

    private async void OnCheckClick(object sender, RoutedEventArgs e)
        => await RunCheckAsync(TargetBoxControl.Text).ConfigureAwait(true);

    private async Task<MigrationPreflight?> RunCheckAsync(string target)
    {
        CheckButtonControl.IsEnabled = false;

        try
        {
            var preflight = await _migration.PreflightAsync(target).ConfigureAwait(true);
            ShowChecks(preflight);
            UpdateSwitchHint(target);

            return preflight;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "数据目录迁移前置检查失败");
            ShowStatus("检查失败：" + ex.Message, isError: true);
            return null;
        }
        finally
        {
            CheckButtonControl.IsEnabled = !_busy;
            StartButtonControl.IsEnabled = !_busy && _lastPreflight?.CanMigrate == true;
        }
    }

    private void ShowChecks(MigrationPreflight preflight)
    {
        _lastPreflight = preflight;

        var okBrush = FindBrush("SuccessBrush", Brushes.Green);
        var failBrush = FindBrush("DangerBackgroundBrush", Brushes.Firebrick);

        ChecksList.ItemsSource = preflight.Checks
            .Select(check => new CheckRowUi(
                check.Ok ? "✓" : "✗",
                check.Title,
                check.Message,
                check.Ok ? okBrush : failBrush))
            .ToList();

        if (preflight.CanMigrate)
        {
            CheckSummaryText.Text = "6 项前置检查全部通过，可以开始迁移。";
            CheckSummaryText.Foreground = okBrush;
        }
        else
        {
            CheckSummaryText.Text = $"有 {preflight.Failed.Count} 项未通过，请按提示处理后重新检查。";
            CheckSummaryText.Foreground = failBrush;
        }

        StartButtonControl.IsEnabled = preflight.CanMigrate && !_busy;
    }

    private void UpdateSwitchHint(string target)
    {
        SwitchPanel.Visibility = _migration.LooksLikeDataDir(target)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 目标已经是数据目录时，复制这条路必然被 target-not-data-dir 拦下，
        // 唯一可走的是"直接切换"，所以别让用户对着灰色的「开始迁移」发呆。
        if (SwitchPanel.Visibility == Visibility.Visible)
        {
            StartButtonControl.IsEnabled = false;
        }
    }

    private void OnTargetTextChanged(object sender, TextChangedEventArgs e)
    {
        // 路径一改，上次的检查结论就作废
        _lastPreflight = null;
        StartButtonControl.IsEnabled = false;
        CheckSummaryText.Text = "路径已修改，请点「检查」重新做前置检查。";
        CheckSummaryText.Foreground = FindBrush("TextTertiaryBrush", Brushes.Gray);
        UpdateSwitchHint(TargetBoxControl.Text);
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择新的数据目录",
            Multiselect = false,
        };

        if (picker.ShowDialog(this) != true || string.IsNullOrWhiteSpace(picker.FolderName))
        {
            return;
        }

        TargetBoxControl.Text = picker.FolderName;
        await RunCheckAsync(picker.FolderName).ConfigureAwait(true);
    }

    private void OnOpenCurrentClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _shell.OpenFolder(_paths.DataRoot);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "打开当前数据目录失败");
            AppDialog.Error(this, ex.Message, "打开数据目录失败");
        }
    }

    // ---------- 直接切换到已有数据目录 ----------

    private async void OnSwitchClick(object sender, RoutedEventArgs e)
    {
        var target = TargetBoxControl.Text;

        if (!_migration.LooksLikeDataDir(target))
        {
            return;
        }

        var answer = AppDialog.Confirm(
            this,
            "目标目录已经是一个数据目录（里面有 Config.json 或 Library.db）。\n\n"
            + "「直接切换」不会复制任何文件，只把数据目录指针指向该目录；旧数据目录保持原样，重启后生效。\n\n"
            + "确定切换吗？",
            "直接切换到已有数据目录");

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SwitchButtonControl.IsEnabled = false;
            var result = await _migration.SwitchToExistingAsync(target).ConfigureAwait(true);

            if (result.Success)
            {
                ShowStatus("已切换数据目录，重启后生效。", isError: false, isSuccess: true);
                RestartPanel.Visibility = Visibility.Visible;
                StartButtonControl.Visibility = Visibility.Collapsed;
            }
            else
            {
                ShowFailure(result.Error ?? "切换失败。", showDialog: true);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "直接切换到已有数据目录失败");
            ShowFailure("切换失败：" + ex.Message, showDialog: true);
        }
        finally
        {
            SwitchButtonControl.IsEnabled = true;
        }
    }

    // ---------- 迁移 ----------

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var target = TargetBoxControl.Text;
        var preflight = await RunCheckAsync(target).ConfigureAwait(true);

        if (preflight is null || !preflight.CanMigrate)
        {
            ShowStatus("前置检查没有通过，暂时不能开始迁移。", isError: true);
            return;
        }

        await RunMigrationAsync(target, IncludeCachesCheck.IsChecked == true, showDialogs: true)
            .ConfigureAwait(true);
    }

    private async Task<bool> RunMigrationAsync(string target, bool includeCaches, bool showDialogs)
    {
        _busy = true;
        _cts = new CancellationTokenSource();
        UpdateBusyUi();

        ProgressPanel.Visibility = Visibility.Visible;
        ProgressBarControl.Value = 0;
        ProgressText.Text = "准备复制…";
        RestartPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = string.Empty;

        var progress = new Progress<MigrationProgress>(OnProgress);

        try
        {
            var result = await Task.Run(
                () => _migration.MigrateAsync(target, includeCaches, progress, _cts.Token)).ConfigureAwait(true);

            if (result.Success)
            {
                ShowSuccess(result);
                return true;
            }

            ShowFailure(result.Error ?? "迁移失败。", showDialogs);
            return false;
        }
        catch (OperationCanceledException)
        {
            ShowCancelled();
            return false;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "数据目录迁移失败");
            ShowFailure("迁移失败：" + ex.Message, showDialogs);
            return false;
        }
        finally
        {
            _busy = false;
            _cts.Dispose();
            _cts = null;
            UpdateBusyUi();
        }
    }

    private void OnProgress(MigrationProgress progress)
    {
        var percent = progress.TotalBytes > 0
            ? progress.CopiedBytes * 100d / progress.TotalBytes
            : progress.TotalFiles > 0
                ? progress.CopiedFiles * 100d / progress.TotalFiles
                : 0d;

        ProgressBarControl.Value = Math.Clamp(percent, 0, 100);
        ProgressText.Text = $"复制中 {progress.CopiedFiles}/{progress.TotalFiles}：{progress.CurrentItem}";
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_cts is null || _cts.IsCancellationRequested)
        {
            return;
        }

        CancelButtonControl.IsEnabled = false;
        ProgressText.Text = "正在取消，回滚本次复制的文件…";
        _cts.Cancel();
    }

    private void ShowSuccess(MigrationResult result)
    {
        ProgressBarControl.Value = 100;
        ProgressText.Text =
            $"复制完成：{result.CopiedFiles} 个文件，{FormatBytes(result.CopiedBytes)}，已逐文件校验。";

        ShowStatus(
            "迁移完成，重启后生效。旧数据目录没有自动删除，重启后可在「设置 → 数据」里确认并删除。",
            isError: false,
            isSuccess: true);

        RestartPanel.Visibility = Visibility.Visible;
        StartButtonControl.Visibility = Visibility.Collapsed;
        CancelButtonControl.Visibility = Visibility.Collapsed;
    }

    private void ShowFailure(string message, bool showDialog)
    {
        ProgressText.Text = "迁移没有完成。";
        ShowStatus(message, isError: true);

        if (showDialog)
        {
            AppDialog.Error(this, message, "迁移失败");
        }
    }

    private void ShowCancelled()
    {
        ProgressText.Text = "已取消。";
        ShowStatus("已取消，未做任何更改。", isError: false);
    }

    private void ShowStatus(string message, bool isError, bool isSuccess = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? FindBrush("DangerBackgroundBrush", Brushes.Firebrick)
            : isSuccess
                ? FindBrush("SuccessBrush", Brushes.Green)
                : FindBrush("WarningBrush", Brushes.DarkOrange);
    }

    private void UpdateBusyUi()
    {
        TargetBoxControl.IsEnabled = !_busy;
        BrowseButtonControl.IsEnabled = !_busy;
        CheckButtonControl.IsEnabled = !_busy;
        IncludeCachesCheck.IsEnabled = !_busy;
        SwitchButtonControl.IsEnabled = !_busy;
        CloseButtonControl.IsEnabled = !_busy;
        StartButtonControl.IsEnabled = !_busy && _lastPreflight?.CanMigrate == true;
        CancelButtonControl.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButtonControl.IsEnabled = _busy;
    }

    private void OnRestartNowClick(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(exe))
        {
            ShowFailure("找不到当前程序路径，请手动关闭后重新打开程序。", showDialog: true);
            return;
        }

        try
        {
            // 用当前 exe 路径重启即可，不传参数：数据目录由指针文件解析
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "迁移完成后的自动重启失败");
            ShowFailure("自动重启失败：" + ex.Message + "。请手动关闭后重新打开程序。", showDialog: true);
        }
    }

    private void OnRestartLaterClick(object sender, RoutedEventArgs e) => Close();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ---------- 小工具 ----------

    private Brush FindBrush(string resourceKey, Brush fallback)
        => TryFindResource(resourceKey) as Brush ?? fallback;

    private static (long Bytes, int Files) MeasureDirectory(string dir)
    {
        var bytes = 0L;
        var files = 0;

        if (!Directory.Exists(dir))
        {
            return (bytes, files);
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                bytes += new FileInfo(file).Length;
                files++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 统计不是关键路径，读不到就跳过
            }
        }

        return (bytes, files);
    }

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024d;

        if (bytes >= kb * kb * kb)
        {
            return (bytes / kb / kb / kb).ToString("F2") + " GB";
        }

        if (bytes >= kb * kb)
        {
            return (bytes / kb / kb).ToString("F1") + " MB";
        }

        if (bytes >= kb)
        {
            return (bytes / kb).ToString("F0") + " KB";
        }

        return bytes + " B";
    }
}

/// <summary>检查清单里的一行：✓/✗ + 标题 + 人话原因。</summary>
internal sealed record CheckRowUi(string Mark, string Title, string Message, Brush MarkBrush);
