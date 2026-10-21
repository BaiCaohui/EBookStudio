using System.IO;
using System.Windows;
using System.Windows.Controls;
using EbookToolSuite.App.Themes;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.App.Views;

/// <summary>下拉框里的一项：第几章、哪个文件、标题是什么。</summary>
public sealed record SourceChapter(int Index, string Href, string Title)
{
    public string Display => (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "、" + Title;
}

/// <summary>
/// 章节源码编辑器（执行计划 T5-2）：AvalonEdit 编辑该章 XHTML 源码，保存前做良构检查。
/// </summary>
/// <remarks>
/// 【为什么单独一个窗口】主编辑窗口没有"中间编辑区"（那是后续任务的事），
/// 先把源码视图做成独立窗口，功能与判据都能落地；将来搬进主窗口只动入口。
/// 【为什么不弹模态框】未保存提示做成内联面板：模态框在自动化冒烟里会把进程卡死（本仓库踩过）。
/// </remarks>
public partial class ChapterSourceWindow : Window
{
    private readonly IChapterSourceService _source;
    private readonly List<SourceChapter> _chapters;
    private readonly IWorkspaceSnapshotService? _snapshots;
    private int _current;
    private bool _dirty;
    private bool _switching;
    private Action? _pending;

    public ChapterSourceWindow(
        IChapterSourceService source,
        IEpubWorkspace workspace,
        IEnumerable<SourceChapter> chapters,
        int initialIndex,
        IWorkspaceSnapshotService? snapshots = null)
    {
        _source = source;
        _snapshots = snapshots;
        Workspace = workspace;
        _chapters = chapters.ToList();

        InitializeComponent();

        // 源码编辑区跟着书库主题走（底色/正文/行号/选区），否则深色主题下中间还是白底。
        AvalonTheme.Apply(Editor);

        ChapterBox.ItemsSource = _chapters;

        _current = Math.Clamp(initialIndex, 0, Math.Max(_chapters.Count - 1, 0));

        _switching = true;
        ChapterBox.SelectedIndex = _current;
        _switching = false;

        Editor.TextChanged += (_, _) =>
        {
            if (!_switching)
            {
                _dirty = true;
                StatusText.Text = "有未保存的修改（点【保存到工作区】写入工作区；写进书里还要在主窗口点【保存】）";
            }
        };

        LoadCurrent();
    }

    /// <summary>当前章节（给冒烟与测试看的）。</summary>
    internal SourceChapter CurrentChapter => _chapters[_current];

    internal bool IsDirty => _dirty;

    internal bool DirtyPromptVisible => DirtyPanel.Visibility == Visibility.Visible;

    internal int ChapterCount => _chapters.Count;

    private string CurrentHref => _chapters[_current].Href;

    private void LoadCurrent()
    {
        _switching = true;

        try
        {
            Editor.Text = _source.ReadSource(Workspace, CurrentHref);
        }
        catch (Exception ex)
        {
            Editor.Text = string.Empty;
            StatusText.Text = "读源码失败：" + ex.SafeMessage();
            _switching = false;
            return;
        }

        _switching = false;
        _dirty = false;
        DirtyPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "已载入 " + CurrentChapter.Display + " 的源码（"
            + Editor.Text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 个字符）";
    }

    /// <summary>保存前记一份"这一章"的快照（T5-4 判据 2）。false = 建不出来，调用方应中止保存。</summary>
    private bool TryTakeSaveSnapshot()
    {
        if (_snapshots is null)
        {
            return true;
        }

        try
        {
            var entry = CurrentHref;

            _snapshots
                .TakeIncrementalAsync(
                    Workspace,
                    new[] { entry },
                    WorkspaceSnapshotKinds.Save,
                    "保存前：" + entry)
                .GetAwaiter()
                .GetResult();
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "保存前建快照失败：" + ex.SafeMessage();
            return false;
        }
    }

    /// <summary>保存：校验不过不写盘，并把光标定位到出错行（判据 3 / 4）。</summary>
    internal bool Save()
    {
        ChapterSourceCheck check;

        /* 【T5-4 判据 2】保存前先记一份这一章的快照（历史里能回退到"这次保存前"）。
           建不出来就不写盘：与主窗口/可视编辑器同一口径。 */
        if (_dirty && !TryTakeSaveSnapshot())
        {
            StatusText.Text = "快照建不出来，这次保存已中止（怕你改完退不回去）。";
            return false;
        }

        try
        {
            check = _source.Save(Workspace, CurrentHref, Editor.Text);
        }
        catch (Exception ex)
        {
            StatusText.Text = "保存失败：" + ex.SafeMessage();
            return false;
        }

        if (!check.Ok)
        {
            StatusText.Text = "保存被拦（没有写入）：" + check.Summary;
            MoveCaretTo(check.Line, check.Column);
            return false;
        }

        _dirty = false;
        DirtyPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "已写入工作区（" + Editor.Text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " 个字符）。写进书里还要在主窗口点【保存】。";
        return true;
    }

    /// <summary>把光标移到第 line 行第 column 列并高亮那一行。</summary>
    private void MoveCaretTo(int line, int column)
    {
        var document = Editor.Document;

        if (document.LineCount == 0)
        {
            return;
        }

        var number = Math.Clamp(line, 1, document.LineCount);
        var documentLine = document.GetLineByNumber(number);
        var offset = document.GetOffset(number, Math.Clamp(column, 1, documentLine.Length + 1));

        Editor.CaretOffset = offset;
        Editor.ScrollToLine(number);
        Editor.Select(documentLine.Offset, documentLine.Length);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => Save();

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        if (_dirty)
        {
            Ask(DirtyText.Text = "这一章有未保存的修改，要放弃吗？", LoadCurrent);
            return;
        }

        LoadCurrent();
    }

    private void OnChapterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switching || ChapterBox.SelectedIndex < 0 || ChapterBox.SelectedIndex == _current)
        {
            return;
        }

        var target = ChapterBox.SelectedIndex;

        if (!_dirty)
        {
            _current = target;
            LoadCurrent();
            return;
        }

        // 有未保存修改：先把下拉框拨回去，等用户在内联面板里选
        _switching = true;
        ChapterBox.SelectedIndex = _current;
        _switching = false;

        Ask("这一章有未保存的修改，切换前要保存吗？", () =>
        {
            _current = target;
            LoadCurrent();
        });
    }

    private void Ask(string message, Action pending)
    {
        DirtyText.Text = message;
        _pending = pending;
        DirtyPanel.Visibility = Visibility.Visible;
    }

    private void OnDirtySaveClick(object sender, RoutedEventArgs e)
    {
        if (!Save())
        {
            return;
        }

        var pending = _pending;
        _pending = null;
        DirtyPanel.Visibility = Visibility.Collapsed;
        pending?.Invoke();
    }

    private void OnDirtyDropClick(object sender, RoutedEventArgs e)
    {
        var pending = _pending;
        _pending = null;
        DirtyPanel.Visibility = Visibility.Collapsed;
        pending?.Invoke();
    }

    private void OnDirtyCancelClick(object sender, RoutedEventArgs e)
    {
        _pending = null;
        DirtyPanel.Visibility = Visibility.Collapsed;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_dirty)
        {
            e.Cancel = true;
            Ask("有未保存的修改，关闭前要保存吗？", Close);
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>工作区（构造函数里给，源码窗口只认工作区里的文件）。</summary>
    internal IEpubWorkspace Workspace { get; }
}
