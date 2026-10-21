using System.Windows;
using System.Windows.Controls;
using EbookToolSuite.App.Navigation;
using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.App.Views.Pages;

/// <summary>
/// 活动中心（T2-1 起承载导入批次；T3-7 把编辑保存 / 还原 / 备份清理也接进来）。
///
/// 两边的数据源不一样：
///   导入批次在 import_batch 表（IImportBatchRepository）；
///   编辑保存 / 强制保存 / 还原 / 清理在 operation_log 表（IOperationLogRepository，T3-6 建的）。
/// 这里按时间倒序合成一条时间线，用户只需要看"最近发生了什么"。
/// </summary>
public partial class ActivityPage : UserControl, INavigationPage
{
    private readonly IImportBatchRepository _batches;
    private readonly IOperationLogRepository _operations;

    public ActivityPage(IImportBatchRepository batches, IOperationLogRepository operations)
    {
        // 构造函数里只允许 InitializeComponent 这种无副作用的事：页面是懒加载的，
        // 在这里查库会卡住第一次点击。真正的加载在 OnNavigatedToAsync 里。
        InitializeComponent();

        _batches = batches;
        _operations = operations;

        ErrorView.Retry += (_, _) => _ = OnNavigatedToAsync(null, CancellationToken.None);
    }

    public async Task OnNavigatedToAsync(object? parameter, CancellationToken ct)
    {
        // T7-3：三态齐了——先给加载态，出错落在页内错误态（带重试），不再只靠外壳兜底
        ShowOnly(LoadingView);

        try
        {
            var batches = await _batches.ListRecentAsync(100, ct).ConfigureAwait(true);
            var logs = await _operations.ListRecentAsync(200, ct).ConfigureAwait(true);

            var rows = batches.Select(FromBatch)
                .Concat(logs.Select(FromLog))
                .OrderByDescending(row => row.SortKey)
                .ToList();

            BatchList.ItemsSource = rows;
            SubtitleText.Text = rows.Count == 0
                ? "导入、编辑保存、还原与备份清理的历史记录。"
                : "最近 " + rows.Count + " 条记录。";

            ShowOnly(rows.Count == 0 ? EmptyView : BatchList);
        }
        catch (OperationCanceledException)
        {
            LoadingView.Visibility = System.Windows.Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "读取活动记录失败");
            ErrorView.Message = ex.SafeMessage();
            ShowOnly(ErrorView);
        }
    }

    /// <summary>同一时刻只显示一个状态（列表 / 空 / 加载 / 错误）。</summary>
    private void ShowOnly(UIElement target)
    {
        BatchList.Visibility = ReferenceEquals(target, BatchList)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        EmptyView.Visibility = ReferenceEquals(target, EmptyView)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        LoadingView.Visibility = ReferenceEquals(target, LoadingView)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        ErrorView.Visibility = ReferenceEquals(target, ErrorView)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    public Task<bool> CanNavigateAwayAsync() => Task.FromResult(true);

    private static ActivityRow FromBatch(ImportBatch batch) => new(
        batch.StartedAt,
        batch.StartedAt.LocalDateTime.ToString("MM-dd HH:mm"),
        "导入",
        batch.IsFinished ? batch.Summary : "进行中 " + batch.Total + " 本",
        batch.Source ?? batch.Note ?? string.Empty);

    private static ActivityRow FromLog(OperationLogEntry entry)
    {
        // At 是可空的（Id = 0 表示还没入库的那种半成品对象）；真从库里读出来的都有值。
        var at = entry.At ?? DateTimeOffset.MinValue;

        return new ActivityRow(
            at,
            at.LocalDateTime.ToString("MM-dd HH:mm"),
            SourceOf(entry.Operation),
            entry.Summary,
            entry.RestorePointId is null ? string.Empty : "还原点 " + entry.RestorePointId);
    }

    private static string SourceOf(string operation) => operation switch
    {
        OperationNames.Save => "编辑保存",
        OperationNames.SaveForced => "强制保存",
        OperationNames.Restore => "还原",
        OperationNames.Prune => "备份清理",
        OperationNames.Import => "导入",
        OperationNames.ReplaceCover => "换封面",
        _ => operation,
    };

    /// <summary>时间线的一行。列名与 XAML 的 DisplayMemberBinding 一一对应。</summary>
    private sealed record ActivityRow(DateTimeOffset SortKey, string When, string Source, string Result, string Note);
}
