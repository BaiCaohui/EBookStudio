using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using EbookToolSuite.App.Commands;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.App.ViewModels;

/// <summary>
/// 转换队列页的 ViewModel（执行计划 T2-4 第 5 条）。单例。
/// 队列的事件都在后台线程触发，所以这里一律 Dispatcher 回 UI 线程再动集合。
/// 这里只做展示与转调，真正的排队/重试/取消逻辑全在 ConversionQueue 里。
/// </summary>
public sealed class ConversionQueueViewModel : INotifyPropertyChanged
{
    private readonly IConversionQueue _queue;
    private readonly ILogger<ConversionQueueViewModel>? _log;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<Guid, ConversionQueueRow> _rows = new();
    private readonly DispatcherTimer _ticker;
    private bool _started;

    public ConversionQueueViewModel(
        IConversionQueue queue, ILogger<ConversionQueueViewModel>? log = null)
    {
        _queue = queue;
        _log = log;
        _dispatcher = Dispatcher.CurrentDispatcher;

        CancelCommand = new RelayCommand(p => _ = CancelAsync(p as ConversionQueueRow));
        RetryCommand = new RelayCommand(p => _ = RetryAsync(p as ConversionQueueRow));
        RemoveCommand = new RelayCommand(p => _ = RemoveAsync(p as ConversionQueueRow));
        ViewLogCommand = new RelayCommand(p => ViewLog?.Invoke(p as ConversionQueueRow));
        OpenLocationCommand = new RelayCommand(p => OpenLocation?.Invoke(p as ConversionQueueRow));
        ExportCommand = new RelayCommand(p => ExportOutput?.Invoke(p as ConversionQueueRow));
        CancelAllCommand = new RelayCommand(_ => _ = CancelAllAsync());
        ClearFinishedCommand = new RelayCommand(_ => _ = ClearFinishedAsync());

        // 已耗时每秒走一格。只对跑着的那些发通知。
        _ticker = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _ticker.Tick += (_, _) =>
        {
            foreach (var row in _rows.Values)
            {
                if (row.Task.Status == ConversionTaskStatus.Running)
                {
                    row.TickElapsed();
                }
            }
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ConversionQueueRow> Rows { get; } = new();

    public ICommand CancelCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ViewLogCommand { get; }
    public ICommand OpenLocationCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand CancelAllCommand { get; }
    public ICommand ClearFinishedCommand { get; }

    /// <summary>页面把「查看日志」的实现挂上来（VM 不认识窗口）。</summary>
    public Action<ConversionQueueRow?>? ViewLog { get; set; }

    /// <summary>页面把「跳到文件位置」的实现挂上来（VM 不认识资源管理器）。</summary>
    public Action<ConversionQueueRow?>? OpenLocation { get; set; }

    /// <summary>页面把「导出文件」的实现挂上来（要弹另存为对话框，只能界面层做）。</summary>
    public Action<ConversionQueueRow?>? ExportOutput { get; set; }

    /// <summary>最近一次操作的错误提示，显示在页面顶部。</summary>
    public string? ErrorMessage { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool IsEmpty => _rows.Count == 0;

    public bool HasActive => _rows.Values.Any(r => !r.Task.IsTerminal);

    public bool HasFinished => _rows.Values.Any(r => r.Task.IsTerminal);

    public string SummaryText
    {
        get
        {
            int Count(ConversionTaskStatus status)
                => _rows.Values.Count(r => r.Task.Status == status);

            var parts = new List<string> { "共 " + _rows.Count + " 条" };
            var waiting = Count(ConversionTaskStatus.Queued)
                + Count(ConversionTaskStatus.WaitingRetry);
            if (waiting > 0)
            {
                parts.Add("等待 " + waiting);
            }

            var running = Count(ConversionTaskStatus.Running);
            if (running > 0)
            {
                parts.Add("转换中 " + running);
            }

            var completed = Count(ConversionTaskStatus.Completed);
            if (completed > 0)
            {
                parts.Add("已完成 " + completed);
            }

            var failed = Count(ConversionTaskStatus.Failed);
            if (failed > 0)
            {
                parts.Add("失败 " + failed);
            }

            var canceled = Count(ConversionTaskStatus.Canceled);
            if (canceled > 0)
            {
                parts.Add("已取消 " + canceled);
            }

            return string.Join("    ", parts);
        }
    }

    /// <summary>进页面时调一次（幂等）：订阅队列事件 + 拉一次全量。</summary>
    public async Task StartAsync()
    {
        if (!_started)
        {
            _started = true;
            _queue.TaskChanged += OnTaskChanged;
            _queue.TasksChanged += OnTasksChanged;
            _ticker.Start();
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    public Task RefreshAsync()
    {
        _rows.Clear();
        Rows.Clear();

        // 队列的快照本来就是按入队时间升序的
        foreach (var task in _queue.Tasks)
        {
            var row = new ConversionQueueRow(task);
            _rows[task.Id] = row;
            Rows.Add(row);
        }

        RaiseAll();
        return Task.CompletedTask;
    }

    private void OnTaskChanged(ConversionTask task)
        => _dispatcher.InvokeAsync(() => ApplyTask(task));

    private void OnTasksChanged()
        => _dispatcher.InvokeAsync(() => _ = RefreshAsync());

    private void ApplyTask(ConversionTask task)
    {
        if (_rows.TryGetValue(task.Id, out var row))
        {
            row.Refresh();
        }
        else
        {
            var added = new ConversionQueueRow(task);
            _rows[task.Id] = added;
            Rows.Add(added);
        }

        RaiseAll();
    }

    private async Task CancelAsync(ConversionQueueRow? row)
    {
        if (row is null)
        {
            return;
        }

        await GuardAsync(() => _queue.CancelAsync(row.Id)).ConfigureAwait(true);
    }

    private async Task RetryAsync(ConversionQueueRow? row)
    {
        if (row is null)
        {
            return;
        }

        await GuardAsync(() => _queue.RetryAsync(row.Id)).ConfigureAwait(true);
    }

    private async Task RemoveAsync(ConversionQueueRow? row)
    {
        if (row is null)
        {
            return;
        }

        await GuardAsync(() => _queue.RemoveAsync(row.Id)).ConfigureAwait(true);
    }

    private Task CancelAllAsync() => GuardAsync(() => _queue.CancelAllAsync());

    private Task ClearFinishedAsync() => GuardAsync(() => _queue.ClearFinishedAsync());

    /// <summary>把业务异常翻成页面上的一句话，别让整个页面炸掉。</summary>
    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            ErrorMessage = null;
            await action().ConfigureAwait(true);
        }
        catch (AppException ex)
        {
            ErrorMessage = ex.UserMessage;
            _log?.LogWarning(ex, "转换队列操作失败 {Code}", ex.Code);
        }
        catch (Exception ex)
        {
            ErrorMessage = "操作失败：" + ex.SafeMessage();
            _log?.LogWarning(ex, "转换队列操作发生未预期的异常");
        }

        RaiseAll();
    }

    /// <summary>传空字符串表示「所有绑定属性都变了」，省得漏掉一个。</summary>
    /// <summary>这条任务本次运行的完整外部输出，界面「查看日志」用。</summary>
    public string? GetTaskOutput(Guid taskId) => _queue.GetTaskOutput(taskId);

    private void RaiseAll()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
