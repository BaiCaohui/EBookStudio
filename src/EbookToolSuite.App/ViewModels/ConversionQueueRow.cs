using System.ComponentModel;
using System.IO;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.App.ViewModels;

/// <summary>
/// 转换队列里的一行（转换队列页用）。
/// ConversionTask 是后台线程直接改的可变对象，这一层只做「读出来 + 通知界面」的翻译，
/// 不往任务对象里写任何东西。
/// </summary>
public sealed class ConversionQueueRow : INotifyPropertyChanged
{
    public ConversionQueueRow(ConversionTask task)
    {
        Task = task;
    }

    public ConversionTask Task { get; }

    public Guid Id => Task.Id;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title
        => string.IsNullOrWhiteSpace(Task.BookTitle) ? "(这本书已从书库移除)" : Task.BookTitle!;

    /// <summary>源格式 -> 目标格式。用 ASCII 箭头，与项目其它文案一致。</summary>
    public string FlowText
        => SourceText + "  ->  " + Task.OutFormat.ToStorageString();

    public string SourceText
        => Task.SourceFormat == BookFormatKind.Unknown
            ? "未知格式"
            : Task.SourceFormat.ToStorageString();

    public string StatusText => Task.StatusText;

    /// <summary>排队中/等待重试也说清楚，别让用户以为卡住了。</summary>
    public string StateText
        => Task.Status switch
        {
            ConversionTaskStatus.Queued => "排队中",
            ConversionTaskStatus.WaitingRetry
                => "等待第 " + (Task.RetryCount + 1) + " 次重试",
            ConversionTaskStatus.Running => Task.ProgressText,
            _ => Task.StatusText,
        };

    /// <summary>已耗时。跑着的按现在算，结束的按 finished_at - started_at 算。</summary>
    public string ElapsedText
    {
        get
        {
            var elapsed = Task.Elapsed(DateTimeOffset.Now);
            return elapsed is null ? string.Empty : "已耗时 " + Format(elapsed.Value);
        }
    }

    /// <summary>终态就不显示进度条了（完成是 100%，失败/取消留着进度没意义）。</summary>
    public bool ShowProgress => !Task.IsTerminal;

    /// <summary>没解析到百分比时必须用不确定态，绝不伪造成 50%。</summary>
    public bool IsIndeterminate
        => Task.ProgressKind != ConversionTaskProgressKind.Percent || Task.Progress is null;

    public double ProgressValue => Task.Progress ?? 0d;

    public string PercentText
        => Task.ProgressKind == ConversionTaskProgressKind.Percent && Task.Progress is { } p
            ? (p * 100).ToString("0") + "%"
            : string.Empty;

    /// <summary>失败原因 / 产物文件名，选一条对用户最有用的。</summary>
    public string DetailText
    {
        get
        {
            if (Task.Status == ConversionTaskStatus.Failed
                && !string.IsNullOrWhiteSpace(Task.ErrorMessage))
            {
                return Task.ErrorMessage!;
            }

            return Task.Status == ConversionTaskStatus.Completed
                ? Path.GetFileName(Task.OutputPath)
                : string.Empty;
        }
    }

    public string RetryText => Task.RetryCount > 0 ? "已重试 " + Task.RetryCount + " 次" : string.Empty;

    public bool CanCancel => !Task.IsTerminal;

    /// <summary>已经成功的没什么可重试的；失败和取消的可以。</summary>
    public bool CanRetry => Task.IsTerminal && Task.Status != ConversionTaskStatus.Completed;

    public bool CanRemove => Task.IsTerminal;

    /// <summary>产物落在磁盘上的完整路径。还没产出时是空串。</summary>
    public string OutputPath => Task.OutputPath ?? string.Empty;

    /// <summary>
    /// 能不能「跳到文件位置」/「导出文件」：必须真的转换成功，**而且文件还在**。
    /// 只判状态是不够的：用户可能已经把产物删掉或移走了，那时按钮点了只会报错。
    /// 调用点在刷新行（Refresh 发的是"所有属性都变了"），所以这里查磁盘不会漏更新。
    /// </summary>
    public bool CanOpenOutput => Task.Status == ConversionTaskStatus.Completed
        && OutputPath.Length > 0
        && File.Exists(OutputPath);

    /// <summary>状态/进度变了就整行刷新。传空字符串表示「所有绑定属性都变了」。</summary>
    public void Refresh()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    /// <summary>只为了让「已耗时」自己往前走，不碰别的属性。</summary>
    public void TickElapsed()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ElapsedText)));

    private static string Format(TimeSpan elapsed)
        => elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
}
