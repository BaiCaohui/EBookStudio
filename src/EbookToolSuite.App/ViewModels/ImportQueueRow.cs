using System.ComponentModel;
using System.Runtime.CompilerServices;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.App.ViewModels;

/// <summary>
/// 导入队列窗口里的一行。包住 ImportJob 而不直接暴露它：
/// ImportJob 是可变 POCO、没有变更通知，200 本时会整表重建（很卡）。
/// 这里只对"界面真正显示的三个字段"发通知，队列表格才能只刷那一行。
/// </summary>
public sealed class ImportQueueRow : INotifyPropertyChanged
{
    public ImportQueueRow(ImportJob job)
    {
        Job = job;
    }

    public ImportJob Job { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FileName => Job.FileName;

    public string StateText => Job.State switch
    {
        ImportJobState.Queued => "等待",
        ImportJobState.Running => "导入中",
        ImportJobState.Canceled => "已取消",
        ImportJobState.Finished => OutcomeText(Job.Outcome),
        _ => "未知",
    };

    public string MessageText => Job.Message ?? string.Empty;

    /// <summary>队列改了 Job 之后由窗口调用（事件在后台线程触发，调用方先切回 UI 线程）。</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(MessageText));
    }

    private static string OutcomeText(ImportOutcome? outcome) => outcome switch
    {
        ImportOutcome.Imported => "完成",
        ImportOutcome.Duplicate => "跳过（重复）",
        ImportOutcome.InvalidEpub => "不是有效电子书",
        ImportOutcome.DrmProtected => "有 DRM，拒绝导入",
        ImportOutcome.UnsupportedFormat => "格式暂不支持",
        ImportOutcome.SourceUnreadable => "源文件读不到",
        ImportOutcome.Canceled => "已取消",
        ImportOutcome.Failed => "失败",
        _ => "完成",
    };

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}