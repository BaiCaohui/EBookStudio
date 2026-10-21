using System.Collections.ObjectModel;
using System.ComponentModel;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Serilog;

namespace EbookToolSuite.App.ViewModels;

/// <summary>
/// 左侧章节树的 VM（执行计划 T5-1）：读章节树 + 重命名 / 移动 / 删除 / 新增 / 导入 / 合并 + 回到会话开始。
/// </summary>
/// <remarks>
/// 界面里不写业务：所有结构操作都转给 <see cref="IChapterTreeService"/>；
/// 这一层只负责"选中项 / 忙碌状态 / 状态条文案 / 通知编辑器标脏"。
/// 操作都是会话内的（只改工作区），真正落盘由编辑器窗口的【保存】负责。
/// </remarks>
public sealed class ChapterTreeViewModel : INotifyPropertyChanged
{
    private readonly IChapterTreeService _structure;
    private readonly ILogger _log = Log.ForContext<ChapterTreeViewModel>();

    private IEpubWorkspace? _workspace;
    private ChapterNodeViewModel? _selected;
    private string _newTitle = string.Empty;
    private ChapterSplitPoint? _selectedSplitPoint;

    private string _status = "打开书籍后可以重命名、上下移动、删除、新增、导入、合并章节。";
    private bool _isBusy;

    public ChapterTreeViewModel(IChapterTreeService structure)
        => _structure = structure ?? throw new ArgumentNullException(nameof(structure));

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>结构改过了（工作区文件已变）：编辑器据此标脏并刷新标题栏。</summary>
    public event EventHandler<string>? Changed;

    /// <summary>回到会话开始后换了工作区：编辑器要把手上的引用换掉，否则会往被删掉的目录里写。</summary>
    public event EventHandler<IEpubWorkspace>? WorkspaceReplaced;

    public ObservableCollection<ChapterNodeViewModel> Items { get; } = new();

    /// <summary>当前选中章的候选拆分点（换章时重新算）。</summary>
    public ObservableCollection<ChapterSplitPoint> SplitPoints { get; } = new();

    /// <summary>拆分点下拉框选中的那一项。</summary>
    public ChapterSplitPoint? SelectedSplitPoint
    {
        get => _selectedSplitPoint;
        set
        {
            if (ReferenceEquals(_selectedSplitPoint, value))
            {
                return;
            }

            _selectedSplitPoint = value;
            Raise();
            Raise(nameof(CanSplit));
        }
    }

    /// <summary>当前工作区（源码窗口要用）。</summary>
    internal IEpubWorkspace? Workspace => _workspace;

    /// <summary>选中项的序号（-1 = 没选）。</summary>
    internal int SelectedIndex => _selected?.Index ?? -1;

    /// <summary>能不能拆分（选中了章，也选了拆分点）。</summary>
    public bool CanSplit => !_isBusy && _workspace is not null && _selected is not null && _selectedSplitPoint is not null;

    public ChapterNodeViewModel? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value))
            {
                return;
            }

            _selected = value;
            Raise();
            Raise(nameof(CanEditSelected));
            Raise(nameof(CanSplit));
            RefreshSplitPoints();
        }
    }

    /// <summary>重命名 / 新增用的标题输入框。</summary>
    public string NewTitle
    {
        get => _newTitle;
        set
        {
            if (_newTitle == value)
            {
                return;
            }

            _newTitle = value;
            Raise();
        }
    }

    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            Raise();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            Raise();
            Raise(nameof(CanEditSelected));
        }
    }

    public bool HasWorkspace => _workspace is not null;

    public bool CanEditSelected => !_isBusy && _workspace is not null && _selected is not null;

    /// <summary>章节数（冒烟与状态条用）。</summary>
    public int Count => Items.Count;

    /// <summary>工作区根目录（冒烟测试核对文件用）。</summary>
    public string? WorkDir => _workspace?.WorkDir;

    public void Attach(IEpubWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Reload();
    }

    public void Detach()
    {
        _workspace = null;
        Items.Clear();
        Selected = null;
        Status = "工作区已关闭";
    }

    /// <summary>重新读树（工作区文件变了以后调）。</summary>
    public void Reload()
    {
        var keep = _selected?.Index;
        Items.Clear();

        if (_workspace is null)
        {
            return;
        }

        try
        {
            foreach (var node in _structure.Load(_workspace))
            {
                Items.Add(new ChapterNodeViewModel(node));
            }

            Selected = (keep is { } index && index >= 0 && index < Items.Count)
                ? Items[index]
                : Items.FirstOrDefault();

            Status = "共 " + Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 章";
            RefreshSplitPoints();
        }
        catch (AppException ex)
        {
            Status = ex.UserMessage;
        }
    }

    // ================= 六个结构操作 =================

    public async Task RenameAsync()
    {
        if (!CanEditSelected)
        {
            return;
        }

        var index = _selected!.Index;
        var title = string.IsNullOrWhiteSpace(NewTitle) ? _selected.Title : NewTitle;

        await GuardAsync(
            "重命名章节",
            () => Task.FromResult(_structure.Rename(_workspace!, index, title!)),
            index).ConfigureAwait(true);
    }

    public async Task MoveUpAsync()
    {
        if (!CanEditSelected || _selected!.Index <= 0)
        {
            return;
        }

        var index = _selected.Index;
        await GuardAsync("上移章节", () => Task.FromResult(_structure.Move(_workspace!, index, index - 1)), index - 1)
            .ConfigureAwait(true);
    }

    public async Task MoveDownAsync()
    {
        if (!CanEditSelected || _selected!.Index >= Items.Count - 1)
        {
            return;
        }

        var index = _selected.Index;
        await GuardAsync("下移章节", () => Task.FromResult(_structure.Move(_workspace!, index, index + 1)), index + 1)
            .ConfigureAwait(true);
    }

    public async Task DeleteAsync()
    {
        if (!CanEditSelected)
        {
            return;
        }

        var index = _selected!.Index;
        await GuardAsync("删除章节", () => Task.FromResult(_structure.Delete(_workspace!, index)), Math.Max(0, index - 1))
            .ConfigureAwait(true);
    }

    public async Task AddAsync()
    {
        if (_workspace is null || IsBusy)
        {
            return;
        }

        var index = _selected is null ? Items.Count : _selected.Index + 1;
        var title = string.IsNullOrWhiteSpace(NewTitle) ? "新章节" : NewTitle;

        await GuardAsync("新增章节", () => Task.FromResult(_structure.AddEmpty(_workspace, index, title)), index)
            .ConfigureAwait(true);
    }

    /// <summary>从文件导入一章（.xhtml / .html / .txt）。</summary>
    public async Task ImportAsync(string sourcePath)
    {
        if (_workspace is null || IsBusy || string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        var index = _selected is null ? Items.Count : _selected.Index + 1;
        var title = string.IsNullOrWhiteSpace(NewTitle) ? null : NewTitle;

        await GuardAsync(
            "导入章节",
            () => Task.FromResult(_structure.Import(_workspace, index, sourcePath, title)),
            index).ConfigureAwait(true);
    }

    /// <summary>在目录里升一级（减少缩进）。</summary>
    public async Task PromoteAsync()
    {
        if (!CanEditSelected)
        {
            return;
        }

        var index = _selected!.Index;
        await GuardAsync("升级章节", () => Task.FromResult(_structure.Promote(_workspace!, index)), index)
            .ConfigureAwait(true);
    }

    /// <summary>在目录里降一级（增加缩进，变成上一章的子项）。</summary>
    public async Task DemoteAsync()
    {
        if (!CanEditSelected)
        {
            return;
        }

        var index = _selected!.Index;
        await GuardAsync("降级章节", () => Task.FromResult(_structure.Demote(_workspace!, index)), index)
            .ConfigureAwait(true);
    }

    /// <summary>按选中的拆分点把当前章切成两章（前半留原文件，后半另存新章）。</summary>
    public async Task SplitAsync()
    {
        if (!CanEditSelected || _selectedSplitPoint is null)
        {
            return;
        }

        var index = _selected!.Index;
        var point = _selectedSplitPoint;

        await GuardAsync("拆分章节", () => Task.FromResult(_structure.Split(_workspace!, index, point.SplitAt)), index)
            .ConfigureAwait(true);
    }

    /// <summary>重算拆分点（选中章变了、结构变了都要重算）。</summary>
    private void RefreshSplitPoints()
    {
        SplitPoints.Clear();
        SelectedSplitPoint = null;

        if (_workspace is null || _selected is null)
        {
            return;
        }

        try
        {
            foreach (var point in _structure.ReadSplitPoints(_workspace, _selected.Index))
            {
                SplitPoints.Add(point);
            }

            SelectedSplitPoint = SplitPoints.FirstOrDefault();
        }
        catch (AppException)
        {
            // 这一章不是良构 XHTML 之类：读不出拆分点不影响别的操作，只是不能拆
        }
    }

    public async Task MergeAsync()
    {
        if (!CanEditSelected || _selected!.Index >= Items.Count - 1)
        {
            return;
        }

        var index = _selected.Index;
        await GuardAsync("合并章节", () => Task.FromResult(_structure.Merge(_workspace!, index)), index)
            .ConfigureAwait(true);
    }

    /// <summary>回到会话开始（判据 7）：丢掉当前工作区，用原书重开一个干净会话。</summary>
    public async Task RevertAsync()
    {
        if (_workspace is null || IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var fresh = await _structure.RestartAsync(_workspace).ConfigureAwait(true);

            _workspace = fresh;
            Reload();

            Status = "已回到会话开始：章节结构恢复成刚打开时的样子";
            WorkspaceReplaced?.Invoke(this, fresh);
        }
        catch (AppException ex)
        {
            Status = ex.UserMessage;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "回到会话开始失败");
            Status = "回到会话开始失败：" + ex.SafeMessage();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task GuardAsync(string what, Func<Task<Core.Models.ChapterOperationResult>> action, int selectIndex)
    {
        if (_workspace is null || IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var result = await action().ConfigureAwait(true);

            Reload();

            if (selectIndex >= 0 && selectIndex < Items.Count)
            {
                Selected = Items[selectIndex];
            }

            Status = result.Message;
            Changed?.Invoke(this, result.Message);
        }
        catch (AppException ex)
        {
            Status = ex.UserMessage;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "{What} 失败", what);
            Status = what + " 失败：" + ex.SafeMessage();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Raise([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
