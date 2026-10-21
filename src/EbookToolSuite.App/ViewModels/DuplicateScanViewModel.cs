using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using EbookToolSuite.App.Commands;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.App.ViewModels;

/// <summary>
/// 重复检测窗口的视图模型。规格见执行计划 T2-2。
/// 合并 = 保留选中那本，其余标为副本并从书库默认隐藏；不删任何文件，可恢复。
/// </summary>
public sealed class DuplicateScanViewModel : INotifyPropertyChanged
{
    private readonly IDuplicateDetectionService _service;
    private readonly ILogger<DuplicateScanViewModel> _log;

    private bool _isScanning;
    private bool _hasScanned;
    private string _status = "点「开始扫描」，找出疑似重复的书。";
    private double _progress;

    public DuplicateScanViewModel(IDuplicateDetectionService service, ILogger<DuplicateScanViewModel> log)
    {
        _service = service;
        _log = log;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>合并或忽略之后触发，书库页据此重新加载网格。</summary>
    public event EventHandler? Changed;

    public ObservableCollection<DuplicateGroupRow> Groups { get; } = new();

    public bool IsScanning
    {
        get => _isScanning;
        private set => Set(ref _isScanning, value);
    }

    public bool HasScanned
    {
        get => _hasScanned;
        private set => Set(ref _hasScanned, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public async Task<int> ScanAsync(CancellationToken ct = default)
    {
        IsScanning = true;
        Progress = 0;
        Status = "正在扫描...";

        try
        {
            // Progress<T> 会把回调派回 UI 线程，界面状态才不会被后台线程改
            var reporter = new Progress<DuplicateScanProgress>(p =>
            {
                Progress = p.Percent;
                Status = p.Phase + " " + p.Done + " / " + p.Total;
            });

            var report = await _service.ScanAsync(DuplicateScanOptions.Default, reporter, ct).ConfigureAwait(true);

            Groups.Clear();

            foreach (var group in report.Groups)
            {
                Groups.Add(new DuplicateGroupRow(group, MergeAsync, IgnoreAsync));
            }

            HasScanned = true;

            Status = report.IsEmpty
                ? "扫描了 " + report.BooksScanned + " 本，没有发现疑似重复。"
                : "扫描了 " + report.BooksScanned + " 本，发现 " + report.GroupCount + " 组疑似重复（算了 "
                  + report.HashesComputed + " 个全文件哈希，" + (long)report.Duration.TotalMilliseconds + " ms）。";

            return Groups.Count;
        }
        catch (OperationCanceledException)
        {
            Status = "扫描已取消。";
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "重复检测失败");
            Status = "扫描失败：" + ex.SafeMessage();
            return -1;
        }
        finally
        {
            IsScanning = false;
            RaiseListChanged();
        }
    }

    /// <summary>保留 keepBookId，其余成员标为副本。返回被标记的本数。</summary>
    public async Task<int> MergeAsync(DuplicateGroupRow row, Guid keepBookId, CancellationToken ct = default)
    {
        var copyIds = row.Group.Members
            .Where(m => m.BookId != keepBookId)
            .Select(m => m.BookId)
            .ToList();

        var marked = await _service.HideCopiesAsync(keepBookId, copyIds, ct).ConfigureAwait(true);

        Groups.Remove(row);
        Status = "已保留 1 本，另 " + marked + " 本标为副本（文件都还在，没有删）。";
        RaiseListChanged();
        Changed?.Invoke(this, EventArgs.Empty);

        return marked;
    }

    /// <summary>这组不再提醒。</summary>
    public async Task<bool> IgnoreAsync(DuplicateGroupRow row, CancellationToken ct = default)
    {
        var memberIds = row.Group.Members.Select(m => m.BookId).ToList();

        await _service.IgnoreGroupAsync(memberIds, ct).ConfigureAwait(true);

        Groups.Remove(row);
        Status = "这组不再提醒。";
        RaiseListChanged();

        return true;
    }

    public bool CanAct => !_isScanning && Groups.Count > 0;

    private void RaiseListChanged()
    {
        OnPropertyChanged(nameof(CanAct));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>一组疑似重复的书。每个成员行都带一个「保留这本」按钮。</summary>
public sealed class DuplicateGroupRow
{
    public DuplicateGroupRow(
        DuplicateGroup group,
        Func<DuplicateGroupRow, Guid, CancellationToken, Task<int>> merge,
        Func<DuplicateGroupRow, CancellationToken, Task<bool>> ignore)
    {
        Group = group;

        Members = group.Members
            .Select(m => new DuplicateMemberRow(m))
            .ToList();

        KeepCommand = new RelayCommand(async o =>
        {
            if (o is DuplicateMemberRow row)
            {
                await merge(this, row.BookId, CancellationToken.None);
            }
        });

        IgnoreCommand = new RelayCommand(async _ => await ignore(this, CancellationToken.None));
    }

    public DuplicateGroup Group { get; }

    public IReadOnlyList<DuplicateMemberRow> Members { get; }

    public ICommand KeepCommand { get; }

    public ICommand IgnoreCommand { get; }

    public string Suggestion => Group.Suggestion;

    public string CountText => Group.Count + " 本";

    public string SizeText => DuplicateMemberRow.FormatSize(Group.TotalSize);

    public string HashVerdict => Group.AllHashesEqual
        ? "全文件哈希一致，基本可以确定是同一本"
        : "全文件哈希不一致，可能是同一本书的不同版本";
}

/// <summary>组里的一本。</summary>
public sealed class DuplicateMemberRow
{
    public DuplicateMemberRow(DuplicateMember member)
    {
        Member = member;
    }

    public DuplicateMember Member { get; }

    public Guid BookId => Member.BookId;

    public string Title => Member.Title;

    public string FormatText => Member.Format.ToUpperInvariant();

    public string SizeText => FormatSize(Member.FileSize);

    public string AddedText => Member.AddedAt.ToLocalTime().ToString("yyyy-MM-dd");

    public string ModifiedText => Member.FileModifiedAt is null
        ? "未知"
        : Member.FileModifiedAt.Value.ToLocalTime().ToString("yyyy-MM-dd");

    public string HashText => string.IsNullOrEmpty(Member.ContentHash)
        ? "未算全文件哈希"
        : Member.ContentHash[..12];

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return (bytes / 1024.0 / 1024 / 1024).ToString("0.##") + " GB";
        }

        if (bytes >= 1024 * 1024)
        {
            return (bytes / 1024.0 / 1024).ToString("0.#") + " MB";
        }

        if (bytes >= 1024)
        {
            return (bytes / 1024.0).ToString("0.#") + " KB";
        }

        return bytes + " B";
    }
}
