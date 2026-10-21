using EbookToolSuite.Core.Models;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using EbookToolSuite.App.Smoke;
using EbookToolSuite.App.Themes;
using EbookToolSuite.Core;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.IO;
using EbookToolSuite.Reader.WebView;
using EbookToolSuite.Engines.Epub;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Serilog;

namespace EbookToolSuite.App.Views;

/// <summary>
/// 可视编辑窗口（T5-2W）：用 WebView2 + 书的样式渲染这一章（流式），直接在上面改文字/图片/表格。
/// </summary>
/// <remarks>
/// 保存链路：前端序列化 DOM -> <see cref="IChapterSourceService.SaveVisual"/>（良构校验）
/// -> 不通过就弹【自动修正确认】真弹窗 -> 确认后走"先建还原点 -> 应用修正 -> 再校验 -> 写工作区 -> 记日志"。
/// 写进书里仍由主编辑窗口的【保存】负责（打包 + EpubCheck 门禁 + 还原点）。
/// </remarks>
public partial class ChapterVisualEditorWindow : Window
{
    private const string EditorHostName = "ets-editor.local";
    private static readonly JsonSerializerOptions PostJson = new(JsonSerializerDefaults.Web);

    private readonly IChapterSourceService _source;
    private readonly IVisualEditAutoFixService _autofix;
    private readonly IWebViewEnvironmentProvider _environments;
    private readonly IReaderAssets _assets;
    private readonly IEpubWorkspace _workspace;
    private readonly IReadOnlyList<SourceChapter> _chapters;

    /// <summary>文件级快照（T5-4）：底部「历史」下拉与【还原】。</summary>
    private readonly IWorkspaceSnapshotService? _snapshots;

    private IReadOnlyList<WorkspaceSnapshot> _history = Array.Empty<WorkspaceSnapshot>();
    private readonly ILogger _log = Log.ForContext<ChapterVisualEditorWindow>();

    private CoreWebView2? _core;
    private bool _dirty;
    private string? _pendingAfterSave;
    private int _index;

    public ChapterVisualEditorWindow(
        IChapterSourceService source,
        IVisualEditAutoFixService autofix,
        IWebViewEnvironmentProvider environments,
        IReaderAssets assets,
        IEpubWorkspace workspace,
        IEnumerable<SourceChapter> chapters,
        int initialIndex,
        IWorkspaceSnapshotService? snapshots = null)
    {
        _source = source;
        _autofix = autofix;
        _environments = environments;
        _assets = assets;
        _workspace = workspace;
        _chapters = chapters.ToList();
        _snapshots = snapshots;
        _index = Math.Clamp(initialIndex, 0, Math.Max(_chapters.Count - 1, 0));

        InitializeComponent();

        Loaded += OnLoadedAsync;
    }

    /// <summary>当前章节（冒烟用）。</summary>
    internal SourceChapter CurrentChapter => _chapters[_index];

    internal int ChapterCount => _chapters.Count;

    internal CoreWebView2? Core => _core;

    /// <summary>有没有未保存的修改（自动化验证用；它是关窗/切模式确认弹窗的判断依据）。</summary>
    internal bool HasUnsavedChanges => _dirty;

    /// <summary>
    /// T5-2W 第三步：保存成功（已经写进工作区）时触发，参数是这一章的**包内路径**。
    /// 阅读器靠它实现"保存后重新加载当前章并保持阅读位置"（ReaderWindow.ReloadChapterAfterEditAsync）。
    /// </summary>
    internal event EventHandler<string>? ChapterSaved;

    /// <summary>测试钩子：模拟"选完图片之后宿主回推"的那条消息（文件选择框本身没法自动化，其余链路都能验）。</summary>
    internal void PostImagePickedForTest(string href, bool replace)
        => Post(new { type = "editor.imagePicked", href, replace });

    /// <summary>在页面里执行一段脚本并取回结果（自动化验证用；文件选择器那种真弹窗才没法自动点）。</summary>
    internal async Task<string> RunScriptAsync(string script)
    {
        var core = _core ?? throw new InvalidOperationException("CoreWebView2 未就绪");

        return await core.ExecuteScriptAsync(script).ConfigureAwait(true);
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            var host = new ReaderWebHost(_environments);

            // 编辑器素材在 Assets\Editor，与 IReaderAssets.Directory（Assets\Reader）是**同级**目录，
            // 所以这里把它们的父目录（Assets）映射出去，URL 用 /Editor/editor.html（踩过：直接映射 Reader 目录 -> ERR_FILE_NOT_FOUND）
            var assetsRoot = Path.GetDirectoryName(_assets.Directory.TrimEnd(Path.DirectorySeparatorChar))
                ?? _assets.Directory;
            var editorPage = Path.Combine(assetsRoot, "Editor", "editor.html");

            if (!File.Exists(editorPage))
            {
                throw new FileNotFoundException("找不到可视编辑器素材：" + editorPage);
            }

            host.Ready += (_, core) =>
            {
                core.WebMessageReceived += OnWebMessageReceived;
                core.SetVirtualHostNameToFolderMapping(
                    EditorHostName, assetsRoot, CoreWebView2HostResourceAccessKind.DenyCors);
            };

            // 工作区（work/）映射成 http://ebook.local/，章节里的相对资源由此解析
            await host.InitializeAsync(Web, _workspace.WorkDir, null, CancellationToken.None);
            _core = host.Core ?? throw new InvalidOperationException("CoreWebView2 未就绪");

            // 书库主题：WebView2 兜底底色 + 主题热切换的重推，都挂在这里（_core 已就绪）
            HookTheme();

            await NavigateAsync(_core, BuildEditorUrl());
            LoadChapter();
            await RefreshHistoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "可视编辑器启动失败");
            AppDialog.Show(
                this, "打开可视编辑器失败：" + ex.SafeMessage(), "可视编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>切到源码视图（同一章）：源码窗口只改工作区，两边的改动都还要回主窗口点保存才落盘。</summary>
    internal void OpenSourceView()
    {
        if (!ConfirmDiscardIfDirty("切到源码视图"))
        {
            return;
        }

        new ChapterSourceWindow(_source, _workspace, _chapters, _index, _snapshots)
        {
            Owner = this,
        }.Show();
    }

    /// <summary>有未保存修改时用真弹窗确认（用户验收口径：真弹窗）。返回 true 表示可以继续。</summary>
    private bool ConfirmDiscardIfDirty(string action)
    {
        if (!_dirty)
        {
            return true;
        }

        // 自建弹窗（MessageBox 的字号改不了、也没法放大文字）：文案按用户原话，按钮 是/否/取消
        var dialog = new ConfirmSaveDialog("当前修改未保存，是否保存后退出。") { Owner = this };
        dialog.ShowDialog();
        var answer = dialog.Choice switch
        {
            ConfirmSaveChoice.SaveThenContinue => MessageBoxResult.Yes,
            ConfirmSaveChoice.DiscardAndContinue => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel,
        };

        if (answer == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (answer == MessageBoxResult.Yes)
        {
            // "是" = 保存后退出：保存是异步的（等 WebView 保存消息回来才算完），
            // 所以先记下待执行动作并触发保存，等保存成功后再关闭，不阻塞 UI 线程。
            _pendingAfterSave = action;
            _ = RunScriptAsync(
                "document.getElementById('status').textContent = '正在保存保存完成后将自动继续';"
                + "document.getElementById('save').click()");
            return false;
        }

        return true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscardIfDirty("关闭"))
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private static async Task NavigateAsync(CoreWebView2 core, string url)
    {
        var navigated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
            => navigated.TrySetResult(args.IsSuccess);

        core.NavigationCompleted += OnCompleted;
        core.Navigate(url);

        await Task.WhenAny(navigated.Task, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(true);
        core.NavigationCompleted -= OnCompleted;
    }

    // ---------- 主题：跟书库「设置 → 外观 → 主题」走 ----------
    //
    // 注意：这是**书库主题**（Config.ui.theme，六档含自定义），不是阅读器主题
    // （reader.theme，阅读器工具栏里那四档）。两者互不影响。
    //
    // 页面在 WebView2 里，WPF 的 DynamicResource 到不了，所以把主题字典里的颜色算成
    // JSON、随 URL 传给 editor.js（首帧就对，不会先闪一下白底），主题热切换时再用
    // ExecuteScriptAsync 调 window.etsApplyTheme 重推一次。

    /// <summary>书库主题色 → 页面配色。key 与 editor.js 里的 CSS 变量一一对应。</summary>
    private Dictionary<string, string> BuildThemePayload()
    {
        var host = BrushColor("WindowBackgroundBrush", Colors.White);
        var panel = BrushColor("CardBackgroundBrush", Colors.White);
        var text = BrushColor("TextPrimaryBrush", Colors.Black);
        var muted = BrushColor("TextTertiaryBrush", Colors.Gray);
        var border = BrushColor("BorderSubtleBrush", Colors.Gray);
        var accent = BrushColor("AccentColorBrush", Color.FromRgb(0x00, 0x67, 0xC0));

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // 页面四周用窗口底色；按钮/输入框/正文纸张用卡片色（亮色档两者都是 #FFFFFF，靠描边区分）
            ["host"] = ToHex(host),
            ["ribbon"] = ToHex(host),
            ["panel"] = ToHex(panel),
            ["text"] = ToHex(text),
            ["muted"] = ToHex(muted),
            ["border"] = ToHex(border),
            // 主题字典里的 SurfaceHoverBrush 是半透明叠色，CSS 里要实色：底色往文字色挪 8%
            ["hover"] = ToHex(MixColor(host, text, 0.08)),
            ["accent"] = ToHex(accent),
            ["accentText"] = ToHex(PickReadableText(accent)),
            // 表格选中单元格：强调色 12% 透明垫底（= 主题字典的 AccentSurfaceBrush 那个透明度）
            ["selection"] = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "rgba({0}, {1}, {2}, 0.12)",
                accent.R, accent.G, accent.B),
        };
    }

    /// <summary>带主题参数的编辑器地址；没有主题参数时退回默认浅色（单独打开 editor.html 的情况）。</summary>
    private string BuildEditorUrl()
    {
        var json = JsonSerializer.Serialize(BuildThemePayload(), PostJson);
        return "http://" + EditorHostName + "/Editor/editor.html?themeJson=" + Uri.EscapeDataString(json);
    }

    private void HookTheme()
    {
        ThemeManager.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
        ApplyThemeToChrome();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(new Action(() => _ = PushThemeAsync()));

    /// <summary>书库主题变了：把新配色推给已经打开的可视编辑器页面（不等下次开窗）。</summary>
    private async Task PushThemeAsync()
    {
        ApplyThemeToChrome();

        var core = _core;
        if (core is null)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(BuildThemePayload(), PostJson);
            await core.ExecuteScriptAsync("window.etsApplyTheme && window.etsApplyTheme(" + json + ")")
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "推送可视编辑器主题失败");
        }
    }

    /// <summary>WebView2 自己的兜底底色（页面还没画出来时露出的那一层）也跟着主题走。</summary>
    private void ApplyThemeToChrome()
    {
        try
        {
            var host = BrushColor("WindowBackgroundBrush", Colors.White);
            Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, host.R, host.G, host.B);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "设置 WebView2 兜底底色失败（不影响编辑）");
        }
    }

    private static Color BrushColor(string key, Color fallback)
        => Application.Current?.TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;

    private static Color MixColor(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + ((to.R - from.R) * t)),
            (byte)Math.Round(from.G + ((to.G - from.G) * t)),
            (byte)Math.Round(from.B + ((to.B - from.B) * t)));
    }

    /// <summary>强调色实底上用白字还是黑字：取对比度更高的那一个（自定义强调色可能是亮黄）。</summary>
    private static Color PickReadableText(Color accent)
    {
        static double Luminance(Color color)
            => (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));

        static double Linear(byte value)
        {
            var s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        var background = Luminance(accent);

        double Ratio(double foreground)
        {
            var lighter = Math.Max(background, foreground) + 0.05;
            var darker = Math.Min(background, foreground) + 0.05;
            return lighter / darker;
        }

        return Ratio(1.0) >= Ratio(0.0) ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A);
    }

    private static string ToHex(Color color) => "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");

    /// <summary>把当前章喂给前端：正文用 EpubChapterNormalizer 归一化，图片/样式地址绝化成虚拟主机 URL。</summary>
    /// <summary>
    /// 只给冒烟用：把当前章**重新载入**一次（T5-2W 判据 13 的"1 MB 章节切模式/载入不卡"要量真实链路）。
    /// 生产代码里没有"重新载入本章"这个动作（编辑保存后是阅读器那边重载），所以单独开一个钩子。
    /// </summary>
    internal void ReloadChapterForTest() => LoadChapter();

    // ---------- T5-4 历史（快照） ----------

    /// <summary>冒烟探针：历史里现在有几份快照。</summary>
    internal int HistoryCount => _history.Count;

    /// <summary>冒烟探针：历史下拉里最上面那一条的文字。</summary>
    internal string HistoryTopText => _history.Count > 0 ? DescribeSnapshot(_history[0]) : string.Empty;

    /// <summary>冒烟探针：列表在 UI 上真的填好了没有。</summary>
    internal bool HistoryBoxFilled => HistoryBox.Items.Count == _history.Count && _history.Count > 0;

    /// <summary>重新读一遍快照列表，填进下拉（新的在最上面）。</summary>
    internal async Task RefreshHistoryAsync()
    {
        if (_snapshots is null)
        {
            HistoryHint.Text = "（没有接上快照服务）";
            return;
        }

        try
        {
            _history = await _snapshots.ListAsync(_workspace).ConfigureAwait(true);
            HistoryBox.ItemsSource = _history.Select(DescribeSnapshot).ToList();

            if (HistoryBox.Items.Count > 0)
            {
                HistoryBox.SelectedIndex = 0;
            }

            var total = _snapshots.IncrementalBytes(_workspace);
            HistoryHint.Text = _history.Count == 0
                ? "还没有快照"
                : "共 " + _history.Count + " 个；增量占用 " + FormatBytes(total);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "读快照列表失败");
            HistoryHint.Text = "读快照列表失败：" + ex.SafeMessage();
        }
    }

    private static string DescribeSnapshot(WorkspaceSnapshot snapshot)
        => snapshot.CreatedAt.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + snapshot.Describe();

    private static string FormatBytes(long bytes)
        => bytes < 1024
            ? bytes + " B"
            : bytes < 1024 * 1024
                ? (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB"
                : (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    private void OnRefreshHistoryClick(object sender, RoutedEventArgs e) => _ = RefreshHistoryAsync();

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (_snapshots is null)
        {
            HistoryHint.Text = "（没有接上快照服务）";
            return;
        }

        var index = HistoryBox.SelectedIndex;

        if (index < 0 || index >= _history.Count)
        {
            HistoryHint.Text = "请先在历史里选一个快照。";
            return;
        }

        var target = _history[index];
        var answer = AppDialog.Show(
            this,
            "把工作区还原到：" + Environment.NewLine + DescribeSnapshot(target) + Environment.NewLine
                + Environment.NewLine + "还原前会自动建一份「还原前」的快照，反悔了还能退回来。"
                + Environment.NewLine + "（只动工作区；要落进书里请回主窗口点【保存】）",
            "还原到快照",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            var result = await _snapshots.RestoreAsync(_workspace, target.Id).ConfigureAwait(true);
            HistoryHint.Text = result.Message;
            await RefreshHistoryAsync().ConfigureAwait(true);

            /* 工作区内容变了：把这一章重新读进编辑器，用户立刻看到还原后的正文。 */
            LoadChapter();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "还原快照失败 {Id}", target.Id);
            HistoryHint.Text = "还原失败：" + ex.SafeMessage();
        }
    }

    private void LoadChapter()
    {
        var chapter = CurrentChapter;
        var packageDirectory = EpubPackageIndex.GetDirectory(chapter.Href);
        // 归一化器自己会把"章节所在目录"算进去，这里只能给**虚拟主机根**，
        // 否则会拼成 http://ebook.local/OEBPSOEBPS/...（实测取证过）
        var baseUrl = ReaderWebHost.ToVirtualUrl(string.Empty);
        var normalized = EpubChapterNormalizer.Normalize(
            _source.ReadSource(_workspace, chapter.Href), chapter.Href, baseUrl);

        Post(new
        {
            type = "editor.load",
            html = normalized?.Html ?? string.Empty,
            title = chapter.Title,
            chapterHref = chapter.Href,
            basePath = packageDirectory,
            baseUrl,
        });
    }

    private void Post(object payload)
        => _core?.PostWebMessageAsJson(JsonSerializer.Serialize(payload, PostJson));

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string type;
        using var document = JsonDocument.Parse(e.WebMessageAsJson);
        var root = document.RootElement;

        if (!root.TryGetProperty("type", out var typeNode))
        {
            return;
        }

        type = typeNode.GetString() ?? string.Empty;

        switch (type)
        {
            case "editor.event.save":
                // 结果通过 editor.saved 回推；这里不 await（消息处理不该被保存阻塞）
                _ = OnSaveAsync(
                    root.TryGetProperty("bodyXml", out var body) ? body.GetString() ?? string.Empty : string.Empty);
                break;

            case "editor.event.changed":
                _dirty = true;
                break;

            case "editor.event.openSource":
                OpenSourceView();
                break;

            case "editor.event.pickImage":
                OnPickImage(root.TryGetProperty("replace", out var replace) && replace.ValueKind == JsonValueKind.True);
                break;
        }
    }

    /// <summary>保存（WebView 消息入口：不阻塞消息处理，结果通过 editor.saved 回推）。</summary>
    internal void OnSave(string bodyXml) => _ = OnSaveAsync(bodyXml);

    /// <summary>
    /// 保存：校验不过先弹【自动修正确认】真弹窗，用户点【确认修改】才动盘（T5-2W 口径）。
    /// 确认后交给 <see cref="IVisualEditAutoFixService"/>：先建还原点再改，改完写 operation_log。
    /// </summary>
    internal async Task<bool> OnSaveAsync(string bodyXml)
    {
        try
        {
            /* 【T5-4 判据 2】保存前先给这一章记一份快照：历史里就能回退到"这次保存前"。
               只在真的改过东西时记（点了保存但内容没变的话，记了也是空转）。
               建不出来就中止这次保存：与主窗口保存同一口径，宁可这次不存，也别让你退不回去。 */
            if (_dirty && !await TryTakeSaveSnapshotAsync().ConfigureAwait(true))
            {
                Reply(false, "快照建不出来，这次保存已中止（怕你改完退不回去）。");
                return false;
            }

            var result = _source.SaveVisual(_workspace, CurrentChapter.Href, bodyXml);

            if (!result.Ok && result.NeedsFix)
            {
                var dialog = new ConfirmAutoFixDialog(result.Fixes) { Owner = this };
                dialog.ShowDialog();

                if (!dialog.Confirmed)
                {
                    Reply(false, "已取消：没有写入（改动还在编辑器里）");
                    return false;
                }

                var outcome = await _autofix
                    .ApplyAsync(_workspace, CurrentChapter.Href, bodyXml)
                    .ConfigureAwait(true);

                Reply(
                    outcome.Ok,
                    outcome.RestorePointId is null
                        ? outcome.Message
                        : outcome.Message + "（修正前的原文已备份成还原点）");

                if (outcome.Ok)
                {
                    AfterSaveSucceeded();
                }

                return outcome.Ok;
            }

            Reply(result.Ok, result.Message);

            if (result.Ok)
            {
                AfterSaveSucceeded();
            }

            return result.Ok;
        }
        catch (Exception ex)
        {
            // 以前这里抛出去会被 WebView 的消息处理吞掉，界面上只看到"点保存没反应"
            _log.Error(ex, "可视编辑保存失败");
            Reply(false, "保存失败：" + ex.SafeMessage());
            return false;
        }
    }

    /// <summary>
    /// 落盘成功之后：未保存标记归零 + 执行"是 = 保存后退出"里记下的那个动作。
    /// 归零这一步以前漏了（<see cref="HasUnsavedChanges"/> 一直是 true），
    /// 所以 "保存后退出" 那条分支永远走不到。
    /// </summary>
    private void AfterSaveSucceeded()
    {
        _dirty = false;

        /* 历史下拉立刻刷新：新快照就在最上面，别让用户以为"保存了却没记上"。 */
        _ = RefreshHistoryAsync();

        /* 先通知宿主（阅读器）"工作区里这一章变了"，再处理"保存后退出"：
           那个分支会 Close()，窗口一关就没人听了。 */
        ChapterSaved?.Invoke(this, CurrentChapter.Href);

        if (_pendingAfterSave is null)
        {
            return;
        }

        var pending = _pendingAfterSave;
        _pendingAfterSave = null;
        _log.Information("保存完成，继续之前选择的动作：" + pending);
        Close();
    }

    private void Reply(bool ok, string message)
        => Post(new { type = "editor.saved", ok, message });

    /// <summary>保存前记一份"这一章"的快照（T5-4 判据 2）。返回 false = 建不出来，调用方应中止保存。</summary>
    private async Task<bool> TryTakeSaveSnapshotAsync()
    {
        if (_snapshots is null)
        {
            return true;
        }

        try
        {
            var entry = CurrentChapter.Href;

            await _snapshots
                .TakeIncrementalAsync(
                    _workspace,
                    new[] { entry },
                    WorkspaceSnapshotKinds.Save,
                    "保存前：" + (string.IsNullOrWhiteSpace(CurrentChapter.Title) ? entry : CurrentChapter.Title))
                .ConfigureAwait(true);
            return true;
        }
        catch (Exception error)
        {
            _log.Error(error, "可视编辑器：保存前建快照失败");
            return false;
        }
    }

    /// <summary>
    /// 把选中的图片复制进工作区（章节同目录）并回推给前端。抽出来是为了能被自动化验证：
    /// 只有系统文件选择框没法自动点，复制 + 回推 + 前端插入这一整条链路都可以自动测。
    /// </summary>
    internal void InsertImageFile(string sourcePath, bool replace)
    {
        try
        {
            var packageDirectory = EpubPackageIndex.GetDirectory(CurrentChapter.Href);
            var target = Path.GetDirectoryName(
                Path.Combine(_workspace.WorkDir, CurrentChapter.Href.Replace('/', '\\')))!;

            Directory.CreateDirectory(target);

            var name = "ets-img-" + Guid.NewGuid().ToString("N")[..8]
                + Path.GetExtension(sourcePath).ToLowerInvariant();
            var destination = Path.Combine(target, name);

            while (File.Exists(destination))
            {
                name = "ets-img-" + Guid.NewGuid().ToString("N")[..8]
                    + Path.GetExtension(sourcePath).ToLowerInvariant();
                destination = Path.Combine(target, name);
            }

            // T7-2 判据 4：图片进了工作区就会被打包进书里，必须原子落盘。
            AtomicFile.Copy(sourcePath, destination);

            Post(new
            {
                type = "editor.imagePicked",
                // GetDirectory 返回的目录**不带结尾斜杠**（实测拼出过 OEBPSets-img-xxx.jpg 这种 404 地址），
                // 这里自己补一个斜杠，别再依赖调用方
                href = ReaderWebHost.ToVirtualUrl(packageDirectory.TrimEnd('/') + "/" + name),
                replace,
            });
        }
        catch (Exception ex)
        {
            // 以前这里抛出去就"什么都不发生"，界面上看不出任何原因；现在把真实原因回报到状态栏
            _log.Error(ex, "插入图片失败");
            Reply(false, "插入图片失败：" + ex.SafeMessage());
        }
    }

    /// <summary>选一张图，复制进工作区（章节同目录），把显示用的绝对 URL 回给前端。</summary>
    private void OnPickImage(bool replace)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择图片",
            Filter = "图片|*.jpg;*.jpeg;*.png;*.gif;*.webp|所有文件|*.*",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        InsertImageFile(dialog.FileName, replace);
    }
}
