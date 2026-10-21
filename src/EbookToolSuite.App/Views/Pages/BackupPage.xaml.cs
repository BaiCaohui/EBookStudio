using System.IO;
using System.Windows;
using System.Windows.Controls;
using EbookToolSuite.App.Navigation;
using EbookToolSuite.App.Settings;
using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.App.Views.Pages;

/// <summary>
/// 备份与还原（T3-7 补）：列出所有还原点、能还原、能手动清理超限备份。
///
/// 还原点就是 Data/Backup/RestorePoints/{id}/ 下的一个目录（见 RestorePointService），
/// 每次保存原文件前自动建一个；还原时**先自动建一个反向还原点**，所以退回去之后还能再退回来。
/// </summary>
public partial class BackupPage : UserControl, INavigationPage
{
    private readonly IRestorePointService _restorePoints;
    private readonly IBookRepository _books;
    private readonly IConfigStore _config;

    public BackupPage(IRestorePointService restorePoints, IBookRepository books, IConfigStore config)
    {
        // 构造函数只做无副作用的事：页面是懒加载的，这里查盘会卡住第一次点击。
        InitializeComponent();

        _restorePoints = restorePoints;
        _books = books;
        _config = config;

        ErrorView.Retry += (_, _) => _ = ReloadAsync(CancellationToken.None);
    }

    /// <summary>
    /// T7-4：备份总上限改为从设置页的 backup.limitMb 读（默认 1024 MB），
    /// 不再写死 <see cref="IRestorePointService.DefaultMaxTotalBytes"/>。
    /// </summary>
    private long BackupLimitBytes => AppSettingsKeys.GetInt(
        _config,
        AppSettingsKeys.BackupLimitMb,
        AppSettingsKeys.DefaultBackupLimitMb) * 1024L * 1024L;

    public async Task OnNavigatedToAsync(object? parameter, CancellationToken ct)
        => await ReloadAsync(ct).ConfigureAwait(true);

    public Task<bool> CanNavigateAwayAsync() => Task.FromResult(true);

    private async Task ReloadAsync(CancellationToken ct)
    {
        IReadOnlyList<RestorePoint> points;
        long total;

        // T7-3：以前这里只有一行静态"正在读取还原点"，错误靠系统消息框。现在三态齐全。
        ShowOnly(LoadingView);

        try
        {
            points = await _restorePoints.ListAsync(ct).ConfigureAwait(true);
            total = await _restorePoints.TotalBytesAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            LoadingView.Visibility = Visibility.Collapsed;
            return;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "读取还原点失败");
            ErrorView.Message = ex.SafeMessage();
            ShowOnly(ErrorView);
            return;
        }

        var titles = new Dictionary<Guid, string>();
        var rows = new List<BackupRow>(points.Count);

        foreach (var point in points)
        {
            string book = "（多本）";
            var file = point.ItemCount + " 个文件";

            if (point.Items.Count == 1)
            {
                var item = point.Items[0];
                file = Path.GetFileName(item.OriginalPath);

                if (titles.TryGetValue(item.BookId, out var cached))
                {
                    book = cached;
                }
                else
                {
                    // 书可能已经被删了：查不到就显示 Id 前 8 位，不编造书名。
                    var stored = await _books.GetByIdAsync(item.BookId, ct).ConfigureAwait(true);
                    book = stored?.Title ?? "（已删除的书 " + item.BookId.ToString("N")[..8] + "）";
                    titles[item.BookId] = book;
                }
            }

            rows.Add(new BackupRow(
                point.Id,
                point.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm:ss"),
                point.Type == RestorePointTypes.Save ? "保存" : "还原",
                book,
                file,
                FormatBytes(point.TotalBytes)));
        }

        PointList.ItemsSource = rows;
        RestoreButton.IsEnabled = false;

        SubtitleText.Text = rows.Count == 0
            ? "还没有还原点。"
            : "共 " + rows.Count + " 个还原点，占用 " + FormatBytes(total) + " / 上限 "
              + FormatBytes(BackupLimitBytes) + "（每本书最多留 "
              + IRestorePointService.DefaultMaxPerBook + " 份）";

        ShowOnly(rows.Count == 0 ? EmptyView : PointList);
    }

    /// <summary>同一时刻只显示一个状态（列表 / 空 / 加载 / 错误）。</summary>
    private void ShowOnly(UIElement target)
    {
        PointList.Visibility = ReferenceEquals(target, PointList) ? Visibility.Visible : Visibility.Collapsed;
        EmptyView.Visibility = ReferenceEquals(target, EmptyView) ? Visibility.Visible : Visibility.Collapsed;
        LoadingView.Visibility = ReferenceEquals(target, LoadingView) ? Visibility.Visible : Visibility.Collapsed;
        ErrorView.Visibility = ReferenceEquals(target, ErrorView) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        => RestoreButton.IsEnabled = PointList.SelectedItem is BackupRow;

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (PointList.SelectedItem is not BackupRow row)
        {
            return;
        }

        var confirm = AppDialog.Show(
            "把《" + row.Book + "》退回到 " + row.When + " 那一刻？\n\n"
            + "当前的版本会先被自动备份成一个新的还原点（反向还原点），所以退回去之后还能再退回来。",
            "还原",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        RestoreButton.IsEnabled = false;

        try
        {
            var result = await _restorePoints.RestoreAsync(row.Id).ConfigureAwait(true);
            await ReloadAsync(CancellationToken.None).ConfigureAwait(true);

            AppDialog.Show(
                result.Success
                    ? "已还原 " + result.RestoredCount + " 个文件。\n\n（反向还原点 "
                      + (result.SafetyRestorePointId ?? "(无)") + " 也建好了，后悔了可以再退回来）"
                    : "没能完全还原：" + string.Join("；", result.Failures),
                result.Success ? "还原完成" : "还原失败",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "还原失败 {RestorePointId}", row.Id);
            AppDialog.Show(ex.SafeMessage(), "还原失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RestoreButton.IsEnabled = PointList.SelectedItem is BackupRow;
        }
    }

    private async void OnPruneClick(object sender, RoutedEventArgs e)
    {
        PruneButton.IsEnabled = false;

        try
        {
            var report = await _restorePoints.EnforceLimitsAsync(maxTotalBytes: BackupLimitBytes)
                .ConfigureAwait(true);
            await ReloadAsync(CancellationToken.None).ConfigureAwait(true);

            AppDialog.Show(
                report.AnythingRemoved
                    ? "清理了 " + report.RemovedRestorePoints.Count + " 个最旧的还原点（释放 "
                      + FormatBytes(report.FreedBytes) + "）。"
                    : "没有需要清理的：总量和每本书的份数都还在上限以内。",
                "清理备份",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "清理备份失败");
            AppDialog.Show(ex.SafeMessage(), "清理备份失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            PruneButton.IsEnabled = true;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
    }

    /// <summary>列表行。列名与 XAML 里的 DisplayMemberBinding 一一对应。</summary>
    public sealed record BackupRow(
        string Id,
        string When,
        string Kind,
        string Book,
        string File,
        string Size);
}
