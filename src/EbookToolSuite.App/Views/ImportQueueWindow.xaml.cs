using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Serilog;

namespace EbookToolSuite.App.Views;

/// <summary>
/// 批量导入的队列窗口（执行计划 T2-1）。与旧的一次一本进度窗的区别：
/// 1. 一次显示整批的每一行，200 本时用户能看到"还剩多少、谁失败了"；
/// 2. 结束后**不自动关**，结果留在窗口里（同时已写进 import_batch，活动中心也能查）。
/// </summary>
public partial class ImportQueueWindow : Window
{
    private readonly IImportQueue _queue;
    private readonly ObservableCollection<ImportQueueRow> _rows = new();
    private readonly Dictionary<Guid, ImportQueueRow> _rowsById = new();
    private readonly CancellationTokenSource _cts = new();

    private ImportBatch? _batch;
    private bool _finished;
    private bool _closeWhenFinished;

    public ImportQueueWindow(IImportQueue queue)
    {
        _queue = queue;

        InitializeComponent();

        JobList.ItemsSource = _rows;

        _queue.StateChanged += OnQueueStateChanged;
        _queue.JobStateChanged += OnJobStateChanged;
        _queue.BatchFinished += OnBatchFinished;

        Closed += (_, _) => Detach();
    }

    /// <summary>取消令牌：窗口被关掉就等于请求取消（和旧的进度窗一样）。</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// StartAsync 返回后把批次灌进列表。预检发生在 StartAsync 内部，
    /// 所以调用方要先 Show 窗口、拿到批次再调这里（这段时间窗口显示"正在扫描"）。
    /// </summary>
    public void Attach(ImportBatch batch)
    {
        _batch = batch;

        _rows.Clear();
        _rowsById.Clear();

        foreach (var job in _queue.Jobs)
        {
            var row = new ImportQueueRow(job);
            _rowsById[job.Id] = row;
            _rows.Add(row);
        }

        HeadlineText.Text = "正在导入 " + _rows.Count + " 个文件";
        Title = "正在导入 - 共 " + _rows.Count + " 个文件";
        PauseButton.IsEnabled = true;
        CancelButton.IsEnabled = true;

        RefreshCounters();
    }

    /// <summary>StartAsync 直接抛了（例如一个文件都没扫到）：窗口留在屏幕上说明原因，让用户自己关。</summary>
    public void ShowStartupFailure(string message)
    {
        _finished = true;
        HeadlineText.Text = "没有开始导入";
        DetailText.Text = message;
        SummaryText.Text = string.Empty;
        PauseButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        Bar.Maximum = 1;
        Bar.Value = 0;
    }
    // ---------- 队列事件（都在后台线程触发，必须先切回 UI 线程） ----------

    private void OnJobStateChanged(object? sender, ImportJob job)
        => Dispatcher.InvokeAsync(() =>
        {
            if (IsVisible && _rowsById.TryGetValue(job.Id, out var row))
            {
                row.Refresh();
            }

            RefreshCounters();
        });

    private void OnQueueStateChanged(object? sender, ImportQueueState state)
        => Dispatcher.InvokeAsync(() => ApplyState(state));

    private void OnBatchFinished(object? sender, ImportBatch batch)
        => Dispatcher.InvokeAsync(() =>
        {
            _batch = batch;
            FinishUp();
        });

    private void ApplyState(ImportQueueState state)
    {
        switch (state)
        {
            case ImportQueueState.Preparing:
                DetailText.Text = "正在展开选择并预检重复...";
                break;

            case ImportQueueState.Running:
                PauseButton.Content = "暂停(_P)";
                PauseButton.IsEnabled = true;
                break;

            case ImportQueueState.Paused:
                PauseButton.Content = "继续(_P)";
                DetailText.Text = "已暂停（当前这本跑完才停）。";
                break;

            case ImportQueueState.Canceling:
                DetailText.Text = "正在取消（当前这本跑完才停）。";
                PauseButton.IsEnabled = false;
                CancelButton.IsEnabled = false;
                break;

            case ImportQueueState.Finished:
                FinishUp();
                break;
        }
    }

    private void FinishUp()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;

        PauseButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        Bar.Value = Bar.Maximum;

        foreach (var row in _rows)
        {
            row.Refresh();
        }

        var batch = _batch;
        HeadlineText.Text = batch is null ? "导入结束" : batch.Summary;
        SummaryText.Text = BuildSummary(batch);
        Title = "导入结果";

        Log.Information("导入窗口收尾：{Summary} {Note}", HeadlineText.Text, batch?.Note ?? "无");

        if (_closeWhenFinished)
        {
            Close();
        }
    }

    private void RefreshCounters()
    {
        var total = Math.Max(1, _rows.Count);
        var completed = _queue.CompletedCount;

        Bar.Maximum = total;
        Bar.Value = Math.Min(completed, total);

        if (!_finished)
        {
            DetailText.Text = "已完成 " + completed + " / " + _rows.Count
                + (_queue.State == ImportQueueState.Paused ? "（已暂停）" : string.Empty);
        }
    }

    private string BuildSummary(ImportBatch? batch)
    {
        if (batch is null)
        {
            return string.Empty;
        }

        var parts = new List<string>
        {
            "成功 " + batch.Succeeded,
            "失败 " + batch.Failed,
        };

        var duplicate = _rows.Count(r => r.Job.Outcome == ImportOutcome.Duplicate);
        if (duplicate > 0)
        {
            parts.Add("跳过重复 " + duplicate);
        }

        var canceled = _rows.Count(r => r.Job.State == ImportJobState.Canceled);
        if (canceled > 0)
        {
            parts.Add("已取消 " + canceled);
        }

        var text = string.Join("，", parts) + "。";

        if (!string.IsNullOrWhiteSpace(batch.Note))
        {
            text += " " + batch.Note + "。";
        }

        return text;
    }

    // ---------- 按钮与关窗 ----------

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (_queue.State == ImportQueueState.Paused)
        {
            _queue.Resume();
        }
        else
        {
            _queue.Pause();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        _queue.Cancel();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            // 关窗 = 请求取消，但当前这本要先跑完（不做中途打断，避免半成品进正式区）。
            _closeWhenFinished = true;
            _queue.Cancel();
            DetailText.Text = "正在取消（当前这本跑完才停）。";
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private void Detach()
    {
        _queue.StateChanged -= OnQueueStateChanged;
        _queue.JobStateChanged -= OnJobStateChanged;
        _queue.BatchFinished -= OnBatchFinished;
        _cts.Cancel();
    }
}