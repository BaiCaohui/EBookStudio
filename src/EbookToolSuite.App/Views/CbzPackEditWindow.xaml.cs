using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.App.Views;

/// <summary>
/// CBZ 信息编辑窗：调图片顺序 + 填书籍信息（ComicInfo.xml），交给调用方去打包。
/// </summary>
/// <remarks>
/// <para>
/// 【为什么这个窗口只管界面】顺序与表单的全部规则都在
/// <see cref="CbzPackEditViewModel"/> 里（改名后重排、调序后重编号、保存前校验），
/// 窗口只做四件事：控件事件转给视图模型、弹提示、开预览窗、设置 <see cref="Result"/> 后关窗。
/// </para>
/// <para>
/// 【顺序为什么靠文件名】CBZ 没有页序字段，阅读器按包内文件名自然序排页；
/// 所以调序之后整个列表会重编号成 <c>0001_原名.jpg</c>，让文件名本身表达顺序。
/// 源文件夹里的图片一个字节都不会改：改的只是「包内条目名」。
/// </para>
/// <para>
/// 【对话框语义】点「保存」= <see cref="Result"/> 有值 + <c>DialogResult = true</c>；
/// 点「取消」/ 右上角 X / Esc = 什么都不返回，调用方直接中止（MainWindow 据此不写任何文件）。
/// </para>
/// </remarks>
public partial class CbzPackEditWindow : Window
{
    /// <summary>拖拽时放进 DataObject 的格式名（同一进程内自取自用，不需要注册全局格式）。</summary>
    private const string RowDataFormat = "EbookToolSuite.CbzPackEntry";

    /// <summary>改名 / 调序 / 表单数据全在它身上。</summary>
    private readonly CbzPackEditViewModel _vm;

    /// <summary>按下左键时的位置：超过系统拖拽阈值才算「拖」，否则还是普通点击 / 双击。</summary>
    private Point _dragStart;

    /// <summary>可能被拖动的行（按下时记下，真正拖动后清空，避免一次拖动重复触发）。</summary>
    private CbzPackEntryViewModel? _dragCandidate;

    /// <param name="folder">图片文件夹（只显示路径；源文件不会被改）。</param>
    /// <param name="images">扫描出来的图片绝对路径列表（本窗口会按文件名自然序重排）。</param>
    public CbzPackEditWindow(string folder, IReadOnlyList<string> images)
        : this(folder, images, null, null, overwriteOriginal: false)
    {
    }

    /// <summary>
    /// 编辑书库里已有的 CBZ：多传包内元数据（预填表单）、原文件路径（抬头显示）与覆盖模式。
    /// </summary>
    /// <param name="folder">解压出来的临时图片目录。</param>
    /// <param name="images">解压出来的图片列表。</param>
    /// <param name="initialMetadata">包内 ComicInfo.xml 解析结果；null = 新建打包。</param>
    /// <param name="displayPath">抬头显示的原 CBZ 路径；null = 显示文件夹。</param>
    /// <param name="overwriteOriginal">
    /// true = 保存后重新打包覆盖原 CBZ（调用方负责备份与替换），所以不再让用户选输出位置。
    /// </param>
    public CbzPackEditWindow(
        string folder,
        IReadOnlyList<string> images,
        ComicMetadata? initialMetadata,
        string? displayPath,
        bool overwriteOriginal)
    {
        _vm = new CbzPackEditViewModel(folder, images, displayPath, initialMetadata);

        InitializeComponent();
        DataContext = _vm;

        if (overwriteOriginal)
        {
            // 编辑书库里的书：保存 = 备份原件 → 重新打包 → 原子替换，不需要再选保存位置。
            SaveButton.ToolTip =
                "保存后重新打包并覆盖原 CBZ（原文件先备份到 Data\\Backup\\Files）；源图片一个字节都不会改";
            CancelButton.ToolTip = "什么都不保存，原 CBZ 一个字节都不会改";
        }
    }

    /// <summary>
    /// 用户点「保存」后的结果：按阅读顺序排好的条目 + 书籍信息。
    /// 取消 / 直接关窗时保持 null，调用方看到 null 就中止。
    /// </summary>
    public CbzPackEditResult? Result { get; private set; }

    // ============================================================
    // 底部按钮
    // ============================================================

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!_vm.TryBuildResult(out var entries, out var metadata))
        {
            // 不能保存的中文原因视图模型已经写进 StatusText 了
            AppDialog.Show(this, _vm.StatusText, "还不能保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new CbzPackEditResult(entries, metadata);
        DialogResult = true;
    }

    // ============================================================
    // 调序：按钮 + 拖拽
    // ============================================================

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => _vm.MoveSelected(-1);

    private void OnMoveDownClick(object sender, RoutedEventArgs e) => _vm.MoveSelected(1);

    private void OnImageListKeyDown(object sender, KeyEventArgs e)
    {
        // F2 = 改选中那一张的名字（和资源管理器一致）。正在编辑时不重复触发，免得把刚打的字冲掉。
        if (e.Key != Key.F2 || _vm.Items.Any(row => row.IsEditing))
        {
            return;
        }

        BeginRenameSelected();
        e.Handled = true;
    }

    private void OnImageListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;

        // 双击是「打开预览」，不能被当成拖拽的起点；点在行内 ✎ / 改名框上也不该拖。
        if (e.ClickCount > 1 || IsFromRowControl(e.OriginalSource))
        {
            return;
        }

        var row = FindRow(e.OriginalSource);

        if (row is null)
        {
            return;
        }

        _dragStart = e.GetPosition(ImageList);
        _dragCandidate = row;
    }

    private void OnImageListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        // 只有移动超过系统阈值才算拖动：否则用户手抖一下就会把行拖走。
        var offset = e.GetPosition(ImageList) - _dragStart;

        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var row = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(ImageList, new DataObject(RowDataFormat, row), DragDropEffects.Move);
    }

    private void OnImageListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _dragCandidate = null;

    private void OnImageListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(RowDataFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnImageListDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(RowDataFormat)
            || e.Data.GetData(RowDataFormat) is not CbzPackEntryViewModel dragged)
        {
            return;
        }

        // 落在某一行上 = 放到那一行的位置；落在列表空白处 = 放到最后。
        _vm.MoveRowTo(dragged, FindRow(e.OriginalSource));
        e.Handled = true;
    }

    // ============================================================
    // 双击预览
    // ============================================================

    private void OnImageListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 双击 ✎ 或改名输入框时不算「预览」
        if (IsFromRowControl(e.OriginalSource))
        {
            return;
        }

        var row = FindRow(e.OriginalSource);

        if (row is null)
        {
            return;
        }

        _vm.SelectedRow = row;
        OpenPreview(row);
        e.Handled = true;
    }

    private void OpenPreview(CbzPackEntryViewModel row)
    {
        var caption = "第 " + row.Position + " / " + _vm.Items.Count + " 张 · " + row.EntryName;

        // 非模态（Show 而不是 ShowDialog）：预览窗开着也能回编辑窗改名 / 调序，不阻塞主流程。
        new CbzImagePreviewWindow(row.SourcePath, caption) { Owner = this }.Show();
    }

    // ============================================================
    // 改名：F2 / 「重命名」按钮 / 行内 ✎ / 回车 / 失焦 / Esc
    // ============================================================

    private void OnRenameClick(object sender, RoutedEventArgs e) => BeginRenameSelected();

    private void OnRowRenameClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CbzPackEntryViewModel row })
        {
            return;
        }

        // 正在改这一行时再点一次 ✎ = 提交（和回车等价）
        if (row.IsEditing)
        {
            if (!_vm.CommitRename(row))
            {
                FocusEntryNameBox(row);
            }

            return;
        }

        _vm.BeginRename(row);
    }

    private void BeginRenameSelected()
    {
        var row = _vm.SelectedRow ?? _vm.Items.FirstOrDefault();

        if (row is null)
        {
            return;
        }

        if (!_vm.BeginRename(row))
        {
            // 没进入编辑态 = 另一行正在改名且新名字不合法：把焦点放回那一行，
            // 否则用户点了按钮却看不出发生什么（原因在那一行下面红字里）。
            var editing = _vm.Items.FirstOrDefault(item => item.IsEditing);

            if (editing is not null)
            {
                FocusEntryNameBox(editing);
            }
        }
    }

    private void OnEntryNameBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CbzPackEntryViewModel row } || !row.IsEditing)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                // 提交成功才收起输入框；失败会保持编辑态并在行内显示中文原因。
                if (_vm.CommitRename(row))
                {
                    ImageList.Focus();
                }

                e.Handled = true;
                break;

            case Key.Escape:
                _vm.CancelRename(row);
                ImageList.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnEntryNameBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CbzPackEntryViewModel row } || !row.IsEditing)
        {
            return;
        }

        if (_vm.CommitRename(row))
        {
            return;
        }

        // 名字不合法：保持编辑态，并把焦点抢回输入框让用户接着改。
        FocusEntryNameBox(row);
    }

    private void OnEntryNameBoxIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsVisible)
        {
            return;
        }

        // 刚切到编辑态：把焦点放进输入框并全选主干名（用户一般是想整体改名）。
        // 用 Dispatcher 排队，是因为可见性刚变时布局还没算完，直接 Focus 会被随后的焦点转移覆盖。
        box.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (!box.IsVisible)
                {
                    return;
                }

                box.Focus();
                box.SelectAll();
            }),
            DispatcherPriority.Input);
    }

    private void FocusEntryNameBox(CbzPackEntryViewModel row)
    {
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (ImageList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem container)
                {
                    return;
                }

                FindDescendant<TextBox>(container)?.Focus();
            }),
            DispatcherPriority.Input);
    }

    // ============================================================
    // 视觉树辅助：找行 / 判交互控件 / 找输入框
    // ============================================================

    /// <summary>从事件源往上找它属于哪一行；找不到返回 null（点在列表空白处）。</summary>
    private static CbzPackEntryViewModel? FindRow(object? source)
    {
        var current = source as DependencyObject;

        while (current is not null)
        {
            if (current is ListBoxItem item)
            {
                return item.DataContext as CbzPackEntryViewModel;
            }

            current = ParentOf(current);
        }

        return null;
    }

    /// <summary>
    /// 事件源是不是落在行内可交互控件上（✎ 按钮 / 改名输入框）。
    /// 拖拽与双击都要避开它们，否则按钮点不动、输入框也没法选中文字。
    /// </summary>
    private static bool IsFromRowControl(object? source)
    {
        var current = source as DependencyObject;

        while (current is not null)
        {
            if (current is ButtonBase or TextBoxBase)
            {
                return true;
            }

            if (current is ListBoxItem)
            {
                return false;
            }

            current = ParentOf(current);
        }

        return false;
    }

    /// <summary>
    /// 统一的「往上走一层」：视觉树为主，内容元素（Run 之类）退回逻辑树。
    /// 直接用 VisualTreeHelper.GetParent 遇到非 Visual 会抛异常。
    /// </summary>
    private static DependencyObject? ParentOf(DependencyObject? node) => node switch
    {
        null => null,
        Visual or Visual3D => VisualTreeHelper.GetParent(node),
        FrameworkContentElement content => content.Parent,
        _ => LogicalTreeHelper.GetParent(node),
    };

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                return match;
            }

            var found = FindDescendant<T>(child);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}

/// <summary>
/// 「CBZ 信息编辑窗」交给调用方的结果：按阅读顺序排好的条目 + 书籍信息。
/// </summary>
/// <param name="Entries">按阅读顺序排好的包内条目（第 1 个就是第 1 页）。</param>
/// <param name="Metadata">要写进包内 ComicInfo.xml 的书籍信息。</param>
/// <remarks>
/// 放在窗口的 code-behind 里（任务允许两种放法，这里取少一个文件的那种）：
/// 它只被这个窗口产出、被 MainWindow 消费，和窗口的契约放在一起更好找。
/// </remarks>
public sealed record CbzPackEditResult(IReadOnlyList<CbzPackEntry> Entries, ComicMetadata Metadata);
