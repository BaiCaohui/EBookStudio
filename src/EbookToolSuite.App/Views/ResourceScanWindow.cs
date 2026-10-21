using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.App.Views;

/// <summary>
/// T5-5 资源检查窗口：**悬空引用（红色）**与**孤儿资源（黄色）**两栏，
/// 悬空可「定位到源文件」/「移除该引用」，孤儿可「删除」（服务里带安全约束与删除前快照）。
/// </summary>
public sealed class ResourceScanWindow : Window
{
    private readonly IResourceScanService _scan;
    private readonly IChapterSourceService? _source;
    private readonly IWorkspaceSnapshotService? _snapshots;
    private readonly IEpubWorkspace _workspace;
    private readonly ListBox _danglingList = new() { Height = 150 };
    private readonly ListBox _orphanList = new() { Height = 120 };
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly List<ResourceIssue> _dangling = [];
    private readonly List<ResourceIssue> _orphans = [];

    public ResourceScanWindow(
        IResourceScanService scan,
        IEpubWorkspace workspace,
        IChapterSourceService? source = null,
        IWorkspaceSnapshotService? snapshots = null)
    {
        _scan = scan;
        _workspace = workspace;
        _source = source;
        _snapshots = snapshots;

        Title = "资源检查";
        Width = 760;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var rescan = new Button { Content = "重新扫描", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 10, 0) };
        rescan.Click += async (_, _) => await RefreshAsync().ConfigureAwait(true);

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(rescan);
        bar.Children.Add(_summary);
        Grid.SetRow(bar, 0);
        root.Children.Add(bar);

        /* 这一行必须显式放到第 1 行：不设 Grid.Row 会落回第 0 行，
           和顶部"重新扫描 + 摘要"叠在一起（用户实测：标题与摘要文字重叠）。 */
        var danglingTitle = new TextBlock { Text = "悬空引用（引用了不存在的文件）", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 4) };
        Grid.SetRow(danglingTitle, 1);
        root.Children.Add(danglingTitle);
        Grid.SetRow(_danglingList, 2);
        root.Children.Add(_danglingList);

        var orphanTitle = new TextBlock { Text = "孤儿资源（在 manifest 里但没人引用）", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 4) };
        Grid.SetRow(orphanTitle, 3);
        root.Children.Add(orphanTitle);
        Grid.SetRow(_orphanList, 4);
        root.Children.Add(_orphanList);

        var locate = new Button { Content = "定位到源文件", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 10, 8, 0) };
        locate.Click += (_, _) => LocateSelected();

        var remove = new Button { Content = "移除该引用", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 10, 8, 0) };
        remove.Click += async (_, _) => await RemoveSelectedAsync().ConfigureAwait(true);

        var delete = new Button { Content = "删除孤儿", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 10, 8, 0) };
        delete.Click += async (_, _) => await DeleteSelectedAsync().ConfigureAwait(true);

        var close = new Button { Content = "关闭", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 10, 0, 0) };
        close.Click += (_, _) => Close();

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(locate);
        actions.Children.Add(remove);
        actions.Children.Add(delete);
        actions.Children.Add(close);
        Grid.SetRow(actions, 5);
        root.Children.Add(actions);

        Content = root;
        Loaded += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>冒烟探针：两栏条数与摘要。</summary>
    internal int DanglingCount => _danglingList.Items.Count;

    internal int OrphanCount => _orphanList.Items.Count;

    internal string SummaryText => _summary.Text;

    /// <summary>冒烟用：两栏控件本体（取条目文案/颜色）与选中指定项。</summary>
    internal ListBox DanglingBox => _danglingList;

    internal ListBox OrphanBox => _orphanList;

    internal void SelectDangling(int index) => _danglingList.SelectedIndex = index;

    internal void SelectOrphan(int index) => _orphanList.SelectedIndex = index;

    internal ResourceIssue? SelectedDangling
        => _danglingList.SelectedIndex >= 0 && _danglingList.SelectedIndex < _dangling.Count
            ? _dangling[_danglingList.SelectedIndex]
            : null;

    internal ResourceIssue? SelectedOrphan
        => _orphanList.SelectedIndex >= 0 && _orphanList.SelectedIndex < _orphans.Count
            ? _orphans[_orphanList.SelectedIndex]
            : null;

    internal async Task<ResourceScanReport> RefreshAsync()
    {
        var report = await _scan.ScanAsync(_workspace).ConfigureAwait(true);

        _dangling.Clear();
        _orphans.Clear();
        _danglingList.Items.Clear();
        _orphanList.Items.Clear();

        foreach (var issue in report.Dangling)
        {
            _dangling.Add(issue);
            _danglingList.Items.Add(new ListBoxItem
            {
                Content = (issue.SourceEntry ?? "(未知来源)") + " 第 " + (issue.Line?.ToString(CultureInfo.InvariantCulture) ?? "?")
                    + " 行  " + issue.Entry + (string.IsNullOrEmpty(issue.Snippet) ? string.Empty : "（" + issue.Snippet + "）"),
                Foreground = Brushes.IndianRed,
            });
        }

        foreach (var issue in report.Orphans)
        {
            _orphans.Add(issue);
            _orphanList.Items.Add(new ListBoxItem
            {
                Content = issue.Entry
                    + (issue.SizeBytes is long size ? "（" + (size / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB）" : string.Empty),
                Foreground = Brushes.DarkGoldenrod,
            });
        }

        _summary.Text = report.Summary
            + (report.HasIssues ? string.Empty : "：没问题");
        return report;
    }

    /// <summary>在源码编辑窗口里打开出问题的那一章。</summary>
    internal bool LocateSelected()
    {
        var issue = SelectedDangling;
        var entry = issue?.SourceEntry ?? SelectedOrphan?.Entry;

        if (string.IsNullOrWhiteSpace(entry) || _source is null)
        {
            _summary.Text = "先在左边选一条悬空引用（孤儿资源没有来源文件）。";
            return false;
        }

        var chapters = new[] { new SourceChapter(0, entry!, entry!) };
        new ChapterSourceWindow(_source, _workspace, chapters, 0, _snapshots) { Owner = this }.Show();
        return true;
    }

    internal async Task<bool> RemoveSelectedAsync()
    {
        var issue = SelectedDangling;

        if (issue is null)
        {
            _summary.Text = "先在上面选一条悬空引用。";
            return false;
        }

        var result = await _scan.RemoveReferenceAsync(_workspace, issue).ConfigureAwait(true);
        _summary.Text = result.Message;

        if (result.Ok)
        {
            await RefreshAsync().ConfigureAwait(true);
        }

        return result.Ok;
    }

    internal async Task<bool> DeleteSelectedAsync()
    {
        var issue = SelectedOrphan;

        if (issue is null)
        {
            _summary.Text = "先在下面选一个孤儿资源。";
            return false;
        }

        var result = await _scan.DeleteOrphanAsync(_workspace, issue).ConfigureAwait(true);
        _summary.Text = result.Message;

        if (result.Ok)
        {
            await RefreshAsync().ConfigureAwait(true);
        }

        return result.Ok;
    }
}