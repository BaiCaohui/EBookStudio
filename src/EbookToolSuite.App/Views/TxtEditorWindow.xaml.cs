using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.App.Themes;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Engines.Text;
using Serilog;

namespace EbookToolSuite.App.Views;

/// <summary>
/// TXT 全文编辑器（用户 2026-10-20 要求）：像 Windows 记事本一样，直接编辑整本 TXT 的**全部内容**。
///
/// 【两个入口】
/// · 书库双击 / 「编辑」：打开就从头开始编辑（<c>startChar = 0</c>）；
/// · 阅读器里的「编辑本章」：同样是全文编辑，但打开后光标落在**当前章标题行**（<c>startChar</c> 由阅读清单的
///   <c>ReaderChapter.TextStart</c> 给出，正好是解码后正文的字符下标）。
///
/// 【编码选择器】与阅读器里的那个是同一个设置（<see cref="TxtBookEncoding"/>，按书存）：
/// 任一边改编码，另一边立即跟着换；只影响这一本书。
///
/// 【保存口径】原编码 + 原 BOM 写回同一个文件（<see cref="TxtTextFile.Save"/> 内部是原子写）；
/// 保存成功发 <see cref="Saved"/>，阅读器据此重建缓存、回到原章，避免"改了但屏幕上还是旧的"。
/// 写回之后还要把书库基线（体积 / 修改时间 / 快速指纹）同步成磁盘现状——
/// 这是我们自己的编辑器写的文件，属于**内部修改**，不能让书库扫描把它报成「文件被外部修改」。
///
/// 【缩放】工具栏上是 <c>[−] 百分比 [+]</c>（用户 2026-10-20 要求）：
/// 默认 100%，范围 1%–500%，−/+ 与 Ctrl+鼠标滚轮各按 10% 步进，百分比本身也能点进去直接输数字回车；
/// 字号 = 16.8 × 百分比 / 100，字号一变就重生成视觉行，所以自动换行会实时重排。
///
/// 【查找替换】内联面板（不弹模态框）：所有命中黄底、当前那一处红底（<see cref="TxtSearchColorizer"/>）；
/// 「上一个/下一个」只挪红色高亮（到头会绕回去），「替换」只替换红色那一处、「全部替换」替换全部；
/// 查找框改字、正文被编辑、替换之后命中列表与计数都会实时刷新。
/// 「第 n / m 处」的计数显示在**工具栏右侧**（红字，紧挨「共 N 字」），面板关了就不显示（用户 2026-10-20 要求）。
/// 先在正文里选中文字再点「查找」/Ctrl+F：选中的文字自动填进查找框并立即查找，
/// 红色当前命中就落在**选中的那一处**（「下一个」也从这处往后走，不是从头开始）。
///
/// 【首行缩进 vs 折行续行】文本本身一个字不改；只是把 AvalonEdit 的"折行续行继承首行缩进"关掉，
/// 这样段落首行前面的两个全角空格只缩进第一行，续行顶头显示（用户 2026-10-20 实测后要求）。
///
/// 【为什么不弹模态框】未保存提示做成内联面板：模态框在自动化冒烟里会把进程卡死（本仓库踩过）。
/// </summary>
public partial class TxtEditorWindow : Window
{
    /// <summary>缩放范围与步进（用户 2026-10-20 补充要求：可输入精确百分比，所以下限放到 1%）。</summary>
    private const int MinZoomPercent = 1;
    private const int MaxZoomPercent = 500;
    private const int ZoomStepPercent = 10;

    /// <summary>
    /// 100% 时的正文字号；实际字号 = <see cref="BaseFontSize"/> × 百分比 / 100。
    /// 用户 2026-10-20 要求：100% 要比原来大——原来的 100% 是 14px，现在乘 1.2 = **16.8px**，
    /// 也就是"把 100% 对应的文字大小改成原来 120% 的大小"。
    /// </summary>
    private const double BaseFontSize = 16.8;

    private readonly ILogger _log = Log.ForContext<TxtEditorWindow>();
    private readonly string _displayName;
    private readonly string _path;
    private readonly string? _encodingName;
    private readonly int _startChar;
    private readonly IConfigStore? _config;

    /// <summary>书库扫描服务：保存后同步基线用（可空，冒烟/单测可以不传）。</summary>
    private readonly ILibraryScanService? _libraryScan;

    /// <summary>查找高亮画在 AvalonEdit 的行渲染链上（所有命中黄、当前红）。</summary>
    private readonly TxtSearchColorizer _searchColorizer = new();

    /// <summary>当前文本里的全部命中（按起始下标升序、互不重叠）。</summary>
    private readonly List<TxtSearchMatch> _searchMatches = new();

    private TxtTextDocument? _document;
    private bool _dirty;
    private bool _busy;
    private bool _switchingEncoding;
    private Action? _pending;
    private int _zoomPercent = 100;
    private int _searchCurrentIndex = -1;
    private bool _suppressSearchRefresh;

    public TxtEditorWindow(
        Guid bookId,
        string displayName,
        string path,
        string? encodingName = null,
        int startChar = 0,
        IConfigStore? config = null,
        ILibraryScanService? libraryScan = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        BookId = bookId;
        _displayName = string.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileNameWithoutExtension(path)
            : displayName;
        _path = path;
        _config = config;
        _libraryScan = libraryScan;

        /* 编码优先级：调用方给的（阅读清单里的当前编码）> 这本书手选过并落盘的 > 自动识别。
           第三条等于"默认编码"，只在这本书没被选过时生效。 */
        _encodingName = encodingName ?? TxtBookEncoding.Get(config, bookId);
        _startChar = Math.Max(0, startChar);

        InitializeComponent();

        // 编辑区跟着书库主题走（底色/正文/行号/选区）；以前 AvalonEdit 默认白底黑字，
        // 深色主题下这一大块是刺眼的白纸（用户 2026-10-21 报的图三）。
        AvalonTheme.Apply(Editor);

        Title = _displayName + " · 全文编辑";

        EncodingBox.ItemsSource = TextEncodingSupport.SupportedNames;

        // 查找高亮挂到 AvalonEdit 的行渲染链上；字号/查找初始化放在控件都建好之后。
        Editor.TextArea.TextView.LineTransformers.Add(_searchColorizer);
        Editor.PreviewMouseWheel += OnEditorPreviewMouseWheel;

        /* 自动换行的续行不继承首行缩进（用户 2026-10-20 要求）。
           AvalonEdit 的 InheritWordWrapIndentation 默认是 true：一段里首行前面的缩进（TXT 里是
           两个全角空格）会被复制给这一段的每一行折行续行，屏幕上看起来就是「整段向右缩进」。
           用户要的是「首行缩进只在第一行，续行顶头」，所以关掉它（原文一个字都不改，只影响显示）。 */
        Editor.Options.InheritWordWrapIndentation = false;

        SetZoomPercent(100);

        Editor.TextChanged += OnEditorTextChanged;
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateStatus();

        Loaded += OnLoadedAsync;
        PreviewKeyDown += OnPreviewKeyDown;

        TxtBookEncoding.Changed += OnSharedEncodingChanged;
    }

    /// <summary>所属书 Id（窗口复用与"同一本书只开一个编辑器"用）。</summary>
    internal Guid BookId { get; }

    /// <summary>全文是否已载入（冒烟等待用）。</summary>
    internal bool IsReady => _document is not null;

    internal bool IsDirty => _dirty;

    /// <summary>当前编辑用的编码名（冒烟断言"编辑不改变编码"用）。</summary>
    internal string? DocumentEncoding => _document?.EncodingName;

    /// <summary>编码下拉里的选项（冒烟断言：与阅读器同一个清单、同一个顺序）。</summary>
    internal IReadOnlyList<string> EncodingOptions
        => EncodingBox.ItemsSource as IReadOnlyList<string> ?? Array.Empty<string>();

    internal bool DocumentHasBom => _document?.HasBom ?? false;

    /// <summary>打开后光标落在第几行（1 基；阅读器入口用它证明"跳到了本章标题"）。</summary>
    internal int InitialLine { get; private set; }

    /// <summary>打开后光标的字符下标（= 当前章的 TextStart）。</summary>
    internal int InitialOffset { get; private set; }

    /// <summary>保存成功（已写回磁盘）。阅读器订阅它重建缓存。</summary>
    internal event EventHandler? Saved;

    /// <summary>当前放大比例（百分比；冒烟断言"默认 100%、按钮/滚轮步进"用）。</summary>
    internal int ZoomPercent => _zoomPercent;

    /// <summary>当前正文字号（= 16.8 × 比例 / 100；冒烟断言字号跟着比例走用）。</summary>
    internal double EditorFontSize => Editor.FontSize;

    /// <summary>百分比输入框里显示的文本（"100%" 这样；冒烟断言输入/夹取结果用）。</summary>
    internal string ZoomText => ZoomBox.Text;

    /// <summary>查找面板是否可见（冒烟断言"点查找/Ctrl+F 打开的是内联面板"用）。</summary>
    internal bool SearchPanelVisible => FindPanel.Visibility == Visibility.Visible;

    /// <summary>当前命中总数（冒烟断言"黄色命中数"用）。</summary>
    internal int MatchCount => _searchMatches.Count;

    /// <summary>当前红色高亮是第几个命中（1 基；没有命中时 0）。</summary>
    internal int CurrentMatchNumber => _searchCurrentIndex < 0 ? 0 : _searchCurrentIndex + 1;

    /// <summary>当前红色高亮的字符下标（没有命中时 -1）。</summary>
    internal int CurrentMatchOffset => _searchCurrentIndex < 0 ? -1 : _searchMatches[_searchCurrentIndex].Start;

    /// <summary>全部命中的字符下标（升序；冒烟拿它和独立扫描结果比对）。</summary>
    internal IReadOnlyList<int> MatchOffsets => _searchMatches.Select(match => match.Start).ToArray();

    /// <summary>计数文本（"第 n / m 个"）。</summary>
    internal string FindCountValue => FindCountText.Text;

    /// <summary>查找高亮是否真的挂到了 AvalonEdit 的渲染链上（不是只算了个列表）。</summary>
    internal bool IsSearchColorizerAttached => Editor.TextArea.TextView.LineTransformers.Contains(_searchColorizer);

    /// <summary>某个字符下标会画成什么背景色：普通命中=黄、当前命中=红、非命中=无（冒烟断言用）。</summary>
    internal Brush? HighlightBrushAt(int offset) => _searchColorizer.HighlightBrushAt(offset);

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadDocumentAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "载入 TXT 全文失败 {Path}", _path);
            Editor.IsReadOnly = true;
            SaveButton.IsEnabled = false;
            StatusText.Text = "载入失败：" + ex.Message;
        }
    }

    /// <summary>把整本 TXT 读进编辑器（冒烟也用入口，避免"测另一条路"）。</summary>
    internal async Task LoadDocumentAsync()
    {
        _busy = true;

        try
        {
            StatusText.Text = "正在载入全文（" + new FileInfo(_path).Length / 1024 + " KB）…";

            var document = await Task.Run(() => TxtTextFile.Load(_path, _encodingName)).ConfigureAwait(true);

            _document = document;
            Editor.Text = document.Text;
            _dirty = false;
            DirtyPanel.Visibility = Visibility.Collapsed;

            JumpToStartChar();
            SyncEncodingBox();
            UpdateStatus();

            // 换了一整份正文：查找面板开着的话，旧的命中下标已经失效，重算一遍。
            if (SearchPanelVisible)
            {
                RefreshSearchMatches();
            }

            Editor.Focus();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>下拉框显示"当前正文实际用的编码"（改它不触发切换逻辑）。</summary>
    private void SyncEncodingBox()
    {
        _switchingEncoding = true;

        try
        {
            EncodingBox.SelectedItem = _document?.EncodingName;
        }
        finally
        {
            _switchingEncoding = false;
        }
    }

    /// <summary>
    /// 换编码（用户 2026-10-20：编辑器里的选择器要和阅读器共用）：
    /// 按新编码重新读全文 → 落盘这本书的编码 → 广播给阅读器。
    /// 有未保存修改时**不切**（切了就等于把旧解码下的修改丢掉），状态栏里明说要先保存或放弃。
    /// </summary>
    internal Task<bool> ApplyEncodingAsync(string encodingName)
        => SwitchEncodingAsync(encodingName, publish: true);

    private async Task<bool> SwitchEncodingAsync(string encodingName, bool publish)
    {
        if (_document is null || _busy || string.IsNullOrWhiteSpace(encodingName))
        {
            return false;
        }

        if (string.Equals(encodingName, _document.EncodingName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var caretOffset = Editor.CaretOffset;

        try
        {
            _busy = true;

            var document = await Task.Run(() => TxtTextFile.Load(_path, encodingName)).ConfigureAwait(true);

            _document = document;
            _dirty = false;
            DirtyPanel.Visibility = Visibility.Collapsed;
            Editor.Text = document.Text;
            Editor.CaretOffset = Math.Clamp(caretOffset, 0, Editor.Document.TextLength);

            SyncEncodingBox();
            UpdateStatus();

            // 换编码等于换了一整份正文：查找面板开着的话，命中下标要按新正文重算。
            if (SearchPanelVisible)
            {
                RefreshSearchMatches();
            }

            if (publish)
            {
                await TxtBookEncoding.SetAsync(_config, BookId, document.EncodingName).ConfigureAwait(true);
                TxtBookEncoding.Publish(BookId, document.EncodingName);
                StatusText.Text = "已按 " + document.EncodingName + " 重新读取全文（这本书从此用这个编码）";
            }
            else
            {
                StatusText.Text = "阅读器把这本书的编码改成了 " + document.EncodingName + "，已按它重新读取全文";
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "切换 TXT 编码失败 {Path} {Encoding}", _path, encodingName);
            StatusText.Text = "切换编码失败：" + ex.Message;
            SyncEncodingBox();
            return false;
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnEncodingChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_switchingEncoding || _document is null || _busy)
        {
            return;
        }

        var name = EncodingBox.SelectedItem as string;

        if (string.IsNullOrWhiteSpace(name)
            || string.Equals(name, _document.EncodingName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_dirty)
        {
            StatusText.Text = "有未保存的修改：先保存（写回原编码）或放弃修改，再切到 " + name + "。";
            SyncEncodingBox();
            return;
        }

        await ApplyEncodingAsync(name).ConfigureAwait(true);
    }

    /// <summary>另一边（阅读器）改了这本书的编码：重新读全文跟上（不再广播，避免回声）。</summary>
    private void OnSharedEncodingChanged(object? sender, TxtBookEncodingChangedEventArgs e)
    {
        if (e.BookId != BookId || _document is null || _switchingEncoding)
        {
            return;
        }

        if (string.Equals(e.EncodingName, _document.EncodingName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_dirty)
        {
            StatusText.Text = "阅读器把这本书的编码改成了 " + e.EncodingName
                + "；本窗口有未保存的修改，保存或放弃后再切换。";
            SyncEncodingBox();
            return;
        }

        _ = SwitchEncodingAsync(e.EncodingName, publish: false);
    }

    protected override void OnClosed(EventArgs e)
    {
        TxtBookEncoding.Changed -= OnSharedEncodingChanged;
        base.OnClosed(e);
    }

    /// <summary>保存全文（冒烟直接调它，等价于点【保存】）。</summary>
    internal async Task<bool> SaveAsync()
    {
        if (_document is null || _busy)
        {
            return false;
        }

        try
        {
            _busy = true;

            var document = _document;
            var text = Editor.Text;

            await Task.Run(() => TxtTextFile.Save(document, text)).ConfigureAwait(true);

            _dirty = false;
            DirtyPanel.Visibility = Visibility.Collapsed;

            // 先同步书库基线，再报"已保存"：书库收到通知时状态已经是正常的。
            await SyncLibraryBaselineAsync().ConfigureAwait(true);

            StatusText.Text = "已保存 " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
                + "（编码 " + document.EncodingName + (document.HasBom ? "，带 BOM" : string.Empty) + "）";

            Saved?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "保存 TXT 失败 {Path}", _path);
            StatusText.Text = "保存失败：" + ex.Message;
            return false;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// 保存是我们自己写的文件（内部修改）：把书库记的体积 / 修改时间 / 快速指纹同步成磁盘现状，
    /// 书库扫描才不会把它报成「文件被外部修改」（用户 2026-10-20 实测的误报）。
    /// 这一步失败**不影响保存本身**（内容已经写盘），只记日志。
    /// </summary>
    private async Task SyncLibraryBaselineAsync()
    {
        if (_libraryScan is null)
        {
            return;
        }

        try
        {
            await _libraryScan
                .RecordInternalModificationAsync(BookId, BookFormatKind.Txt)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "保存成功后同步书库文件信息失败（保存本身已成功）{BookId}", BookId);
        }
    }

    /// <summary>光标落到本章标题行（字符下标来自阅读清单的 TextStart）。</summary>
    private void JumpToStartChar()
    {
        var document = Editor.Document;

        if (document is null || document.TextLength == 0)
        {
            return;
        }

        var offset = Math.Clamp(_startChar, 0, document.TextLength);
        var line = document.GetLineByOffset(offset);

        InitialOffset = line.Offset;
        InitialLine = line.LineNumber;

        Editor.CaretOffset = line.Offset;
        Editor.ScrollToHorizontalOffset(0);
        Editor.ScrollToLine(line.LineNumber);
        Editor.TextArea.Caret.BringCaretToView();
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _dirty = true;
        UpdateStatus();

        /* 手工改正文时查找面板若开着：命中与高亮要实时重算（用户 2026-10-20 要求）。
           替换流程自己会控制时机，用 _suppressSearchRefresh 挡掉替换中途的逐次刷新。 */
        if (!_suppressSearchRefresh && SearchPanelVisible)
        {
            RefreshSearchMatches(Editor.CaretOffset);
        }
    }

    private void UpdateStatus()
    {
        if (_document is null || Editor.Document is null)
        {
            return;
        }

        var caret = Editor.TextArea.Caret;
        var count = Editor.Document.TextLength;

        CountText.Text = "共 " + count.ToString("N0", CultureInfo.CurrentCulture) + " 字";
        StatusText.Text = "编码 " + _document.EncodingName
            + (_document.HasBom ? "（带 BOM）" : "（无 BOM）")
            + "　共 " + count.ToString("N0", CultureInfo.CurrentCulture) + " 字"
            + "　第 " + caret.Line + " 行 第 " + caret.Column + " 列"
            + (_dirty ? "　● 尚未保存" : "　磁盘上的内容与这里一致");
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = SaveAsync();
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            OpenFindPanel();
            return;
        }

        if (e.Key == Key.Escape && SearchPanelVisible)
        {
            e.Handled = true;
            CloseFindPanel();
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => _ = SaveAsync();

    private void OnWrapChanged(object sender, RoutedEventArgs e)
    {
        /* XAML 里 CheckBox 的 IsChecked 会先于 InitializeComponent 完成触发一次，
           那一刻 Editor 字段还是 null（实测空引用导致整个窗口开不起来）。 */
        if (Editor is null)
        {
            return;
        }

        Editor.WordWrap = WrapCheck.IsChecked == true;
    }

    // ===== 缩放（用户 2026-10-20：[-] 百分比 [+]，Ctrl+滚轮，百分比可键盘输入） =====

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomOut();

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomIn();

    /// <summary>按 10% 步进放大（+ 按钮；Ctrl+滚轮向上走同一条逻辑）。</summary>
    internal void ZoomIn() => SetZoomPercent(_zoomPercent + ZoomStepPercent);

    /// <summary>按 10% 步进缩小（− 按钮；Ctrl+滚轮向下走同一条逻辑）。</summary>
    internal void ZoomOut() => SetZoomPercent(_zoomPercent - ZoomStepPercent);

    /// <summary>
    /// Ctrl+鼠标滚轮的步进逻辑（模拟浏览器缩放）：滚轮向上放大、向下缩小，每格 10%。
    /// 事件处理里先判断有没有按 Ctrl；冒烟直接调它，等价于按住 Ctrl 滚一格。
    /// </summary>
    internal void ApplyZoomWheel(int delta)
    {
        if (delta == 0)
        {
            return;
        }

        if (delta > 0)
        {
            ZoomIn();
        }
        else
        {
            ZoomOut();
        }
    }

    private void OnEditorPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        // 标记已处理：不让正文跟着滚，Ctrl+滚轮只负责缩放。
        e.Handled = true;
        ApplyZoomWheel(e.Delta);
    }

    /// <summary>把比例设成指定值（夹到 1%–500%），并同步百分比框、字号与自动换行重排。</summary>
    internal void SetZoomPercent(int percent)
    {
        var clamped = Math.Clamp(percent, MinZoomPercent, MaxZoomPercent);

        _zoomPercent = clamped;
        ZoomBox.Text = FormatZoomText(clamped);
        ZoomOutButton.IsEnabled = clamped > MinZoomPercent;
        ZoomInButton.IsEnabled = clamped < MaxZoomPercent;

        /* 字号 = 16.8 × 比例 / 100；Redraw 让 AvalonEdit 重新生成视觉行，
           WordWrap 打开的自动换行于是跟着字号实时重排（用户要求）。 */
        Editor.FontSize = BaseFontSize * clamped / 100.0;
        Editor.TextArea.TextView.Redraw();
    }

    /// <summary>
    /// 百分比框输入后回车/失焦提交：数字照收（越界夹到 1%–500%），
    /// 非数字就回到当前合法值（不抛异常）。返回 false 表示输入不是数字。
    /// </summary>
    internal bool ApplyZoomInput(string? text)
    {
        if (TryParseZoomPercent(text, out var percent))
        {
            SetZoomPercent(percent);
            return true;
        }

        ZoomBox.Text = FormatZoomText(_zoomPercent);
        return false;
    }

    private void OnZoomBoxFocus(object sender, KeyboardFocusChangedEventArgs e) => ZoomBox.SelectAll();

    private void OnZoomBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyZoomInput(ZoomBox.Text);

    private void OnZoomBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ApplyZoomInput(ZoomBox.Text);
            ZoomBox.SelectAll();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ZoomBox.Text = FormatZoomText(_zoomPercent);
            ZoomBox.SelectAll();
        }
    }

    private static bool TryParseZoomPercent(string? text, out int percent)
    {
        percent = 0;

        var trimmed = (text ?? string.Empty).Trim().TrimEnd('%', '％').Trim();

        return trimmed.Length > 0
            && int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.CurrentCulture, out percent);
    }

    private static string FormatZoomText(int percent)
        => percent.ToString(CultureInfo.CurrentCulture) + "%";

    // ===== 查找替换（用户 2026-10-20：内联面板；所有命中黄底、当前那一处红底） =====

    private void OnFindClick(object sender, RoutedEventArgs e) => OpenFindPanel();

    /// <summary>
    /// 打开内联查找面板（工具栏「查找」/ Ctrl+F），焦点放到查找框。
    /// 用户 2026-10-20 补充要求：正文里**先选中一段文字**再点「查找」时，自动把选中的文字填进
    /// 查找框并立即查找；红色当前命中落在**选中的那一处**，「下一个」也从这处往后走。
    /// 选中内容跨多行时只取第一行（查找框是单行输入）。没选中就沿用原来的口径（不清空查找框）。
    /// </summary>
    internal void OpenFindPanel()
    {
        var wasVisible = FindPanel.Visibility == Visibility.Visible;
        var selectionStart = Editor.SelectionStart;
        var query = FirstLineOf(Editor.SelectedText);

        if (!wasVisible)
        {
            FindPanel.Visibility = Visibility.Visible;
            FindCountText.Visibility = Visibility.Visible;
        }

        if (query.Length > 0)
        {
            /* 填入选中文字 + 收起选择：红色高亮那一处就是"刚才选中的地方"，
               不会和选择底色叠在一起看不清。 */
            _suppressSearchRefresh = true;

            try
            {
                FindBox.Text = query;
                Editor.Select(selectionStart, 0);
            }
            finally
            {
                _suppressSearchRefresh = false;
            }

            // 定位口径：从选中处（而不是从文首）找第一个命中——选中的文字本身就是那一处的命中。
            RefreshSearchMatches(selectionStart);
        }
        else if (!wasVisible)
        {
            RefreshSearchMatches();
        }

        FindBox.Focus();
        FindBox.SelectAll();
    }

    /// <summary>选中文字里能直接当查找词的那部分：只取第一行（查找框是单行输入）。</summary>
    private static string FirstLineOf(string selected)
    {
        if (string.IsNullOrEmpty(selected))
        {
            return string.Empty;
        }

        var lineEnd = selected.IndexOfAny(new[] { '\r', '\n' });

        return lineEnd < 0 ? selected : selected[..lineEnd];
    }

    /// <summary>关闭查找面板并清掉所有高亮（面板上的「关闭」/ Esc）。</summary>
    internal void CloseFindPanel()
    {
        FindPanel.Visibility = Visibility.Collapsed;
        FindCountText.Visibility = Visibility.Collapsed;
        RefreshSearchMatches();
        Editor.Focus();
    }

    private void OnFindCloseClick(object sender, RoutedEventArgs e) => CloseFindPanel();

    /// <summary>查找框改字：命中列表实时重算（默认不区分大小写）。</summary>
    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        // 打开面板时"自动填入选中文字"由 OpenFindPanel 自己带定位锚点算一遍，这里别重复触发。
        if (_suppressSearchRefresh)
        {
            return;
        }

        RefreshSearchMatches();
    }

    /// <summary>下一个命中：只挪红色高亮；到最后一个再往后绕回第一个（用户允许循环）。</summary>
    internal void FindNext()
    {
        if (_searchMatches.Count == 0)
        {
            return;
        }

        _searchCurrentIndex = (_searchCurrentIndex + 1) % _searchMatches.Count;
        ApplyCurrentMatch();
    }

    /// <summary>上一个命中：只挪红色高亮；到第一个再往前绕到最后一个。</summary>
    internal void FindPrevious()
    {
        if (_searchMatches.Count == 0)
        {
            return;
        }

        _searchCurrentIndex = _searchCurrentIndex <= 0 ? _searchMatches.Count - 1 : _searchCurrentIndex - 1;
        ApplyCurrentMatch();
    }

    private void OnFindNextClick(object sender, RoutedEventArgs e) => FindNext();

    private void OnFindPreviousClick(object sender, RoutedEventArgs e) => FindPrevious();

    /// <summary>
    /// 「替换」：只替换当前红色高亮那一处。替换后立刻重算命中，
    /// 红色高亮移到替换位置之后的第一个命中，计数同步（替换后高亮实时刷新）。
    /// </summary>
    internal bool ReplaceCurrent()
    {
        if (_searchCurrentIndex < 0 || _searchCurrentIndex >= _searchMatches.Count)
        {
            return false;
        }

        var match = _searchMatches[_searchCurrentIndex];
        var replacement = ReplaceBox.Text ?? string.Empty;

        _suppressSearchRefresh = true;

        try
        {
            Editor.Document.Replace(match.Start, match.Length, replacement);
        }
        finally
        {
            _suppressSearchRefresh = false;
        }

        RefreshSearchMatches(match.Start + replacement.Length);
        return true;
    }

    private void OnReplaceClick(object sender, RoutedEventArgs e) => ReplaceCurrent();

    /// <summary>
    /// 「全部替换」：从后往前换（前面的命中下标不会失效）；整批算一次文档更新，
    /// 替换完命中列表与计数一起归位。返回实际替换了几处。
    /// </summary>
    internal int ReplaceAll()
    {
        if (_searchMatches.Count == 0)
        {
            return 0;
        }

        var replacement = ReplaceBox.Text ?? string.Empty;
        var matches = _searchMatches.ToArray();
        var firstOffset = matches[0].Start;
        var replaced = 0;

        _suppressSearchRefresh = true;

        try
        {
            Editor.Document.BeginUpdate();

            try
            {
                for (var i = matches.Length - 1; i >= 0; i--)
                {
                    Editor.Document.Replace(matches[i].Start, matches[i].Length, replacement);
                    replaced++;
                }
            }
            finally
            {
                Editor.Document.EndUpdate();
            }
        }
        finally
        {
            _suppressSearchRefresh = false;
        }

        RefreshSearchMatches(firstOffset);
        return replaced;
    }

    private void OnReplaceAllClick(object sender, RoutedEventArgs e) => ReplaceAll();

    /// <summary>
    /// 重算命中列表并刷新红色高亮与计数。
    /// <paramref name="preferredOffset"/>：优先把红色高亮放到这个下标之后的第一个命中
    /// （替换后指到"替换位置之后"，手工编辑后指到光标位置）。
    /// </summary>
    private void RefreshSearchMatches(int preferredOffset = -1)
    {
        var previousOffset = CurrentMatchOffset;

        _searchMatches.Clear();

        var query = FindBox.Text;

        if (SearchPanelVisible && !string.IsNullOrEmpty(query) && Editor.Document is not null)
        {
            var text = Editor.Text;
            var start = 0;

            while (start <= text.Length - query.Length)
            {
                var index = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);

                if (index < 0)
                {
                    break;
                }

                _searchMatches.Add(new TxtSearchMatch(index, query.Length));
                start = index + query.Length;
            }
        }

        if (_searchMatches.Count == 0)
        {
            _searchCurrentIndex = -1;
        }
        else
        {
            var anchor = preferredOffset >= 0 ? preferredOffset : previousOffset;
            var index = anchor < 0 ? 0 : _searchMatches.FindIndex(match => match.Start >= anchor);

            if (index < 0)
            {
                index = 0;
            }

            _searchCurrentIndex = index;
        }

        _searchColorizer.SetMatches(_searchMatches, _searchCurrentIndex);
        Editor.TextArea.TextView.Redraw();
        UpdateFindCount();
    }

    /// <summary>红色高亮换位置：只重画正文、同步计数，并把光标滚到那一处（不选中，红色才看得见）。</summary>
    private void ApplyCurrentMatch()
    {
        if (_searchCurrentIndex < 0 || _searchCurrentIndex >= _searchMatches.Count)
        {
            return;
        }

        _searchColorizer.SetCurrentIndex(_searchCurrentIndex);
        Editor.TextArea.TextView.Redraw();
        UpdateFindCount();

        var match = _searchMatches[_searchCurrentIndex];
        var line = Editor.Document.GetLineByOffset(match.Start);

        Editor.CaretOffset = match.Start;
        Editor.ScrollToLine(line.LineNumber);
        Editor.TextArea.Caret.BringCaretToView();
    }

    private void UpdateFindCount()
    {
        /* 工具栏右侧那句红字（用户 2026-10-20 要求）：第几处 / 共几处。
           查找框输入、正文编辑、替换、全部替换、上一个/下一个都会走到这里，所以数字实时跟手。 */
        FindCountText.Text = "第 " + CurrentMatchNumber.ToString(CultureInfo.CurrentCulture)
            + " / " + _searchMatches.Count.ToString(CultureInfo.CurrentCulture) + " 处";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_dirty)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Ask("有未保存的修改，关闭前要保存吗？", Close);
    }

    private void Ask(string message, Action pending)
    {
        DirtyText.Text = message;
        _pending = pending;
        DirtyPanel.Visibility = Visibility.Visible;
    }

    private async void OnDirtySaveClick(object sender, RoutedEventArgs e)
    {
        if (!await SaveAsync().ConfigureAwait(true))
        {
            return;
        }

        CompletePending();
    }

    private void OnDirtyDropClick(object sender, RoutedEventArgs e)
    {
        _dirty = false;
        CompletePending();
    }

    private void OnDirtyCancelClick(object sender, RoutedEventArgs e)
    {
        _pending = null;
        DirtyPanel.Visibility = Visibility.Collapsed;
    }

    private void CompletePending()
    {
        var pending = _pending;

        _pending = null;
        DirtyPanel.Visibility = Visibility.Collapsed;

        pending?.Invoke();
    }
}
