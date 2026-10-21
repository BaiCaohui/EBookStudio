using System.Globalization;
using System.IO;

using System.Text.Json;

using System.Text.Json.Nodes;

using System.Windows;

using System.Windows.Controls;

using EbookToolSuite.App.Commands;

using EbookToolSuite.App.Settings;

using EbookToolSuite.Core;

using EbookToolSuite.Core.Exceptions;

using EbookToolSuite.Core.Enums;

using EbookToolSuite.Core.Interfaces;

using EbookToolSuite.Core.Models;

using EbookToolSuite.Engines.Text;

using EbookToolSuite.Reader;

using EbookToolSuite.Reader.WebView;

using EbookToolSuite.Services.Pdf;
using EbookToolSuite.App.ViewModels;

using Microsoft.Extensions.Logging;

using Microsoft.Web.WebView2.Core;



namespace EbookToolSuite.App.Views;



/// <summary>

/// 阅读窗口。规格见执行计划 T1-5。

/// </summary>

/// <remarks>

/// 三层结构，别混：

/// 1. 本类（宿主）：准备缓存、映射虚拟主机、收发协议消息、把链接跳转翻译成章节跳转。

/// 2. reader.html/js（外壳）：章节列表、翻章、把位置变化回报给宿主。

/// 3. iframe 里的章节：书的原始内容，只被注入了主题样式。

///

/// 章节是"翻到哪章物化哪章"：打开时只物化第一章，其余等用户真翻过去再做。

/// </remarks>

public partial class ReaderWindow : Window

{

    /// <summary>等 WebView2 起来 + 页面就绪的上限。冷启动（首次用常青版）可能较慢。</summary>

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);



    private readonly IReaderService _reader;

    private readonly IWebViewEnvironmentProvider _environments;

    private readonly IReadingProgressService _progress;

    private readonly ILogger _log;

    private readonly Guid _bookId;

    /* T7-3B 阅读会话采集。可空：单测 / 冒烟直接 new 窗口时不必传。
       采集全程 null-safe，服务内部也不抛，绝不挡住打开书或阅读。 */
    private readonly IReadingSessionService? _stats;

    /* T7-3B：会话采集的本地游标 —— 当前章字数、章内进度、页号。
       chapterChanged 会重置它们；页号 -1 表示"还没建立基线"（首次上报不计增量）。 */
    private int _statsChapterIndex = -1;
    private int _statsChapterChars;
    private double _statsChapterProgress;
    private int _statsPage = -1;

    private readonly int _startIndex;

    /* T6-4：从全文搜索结果跳进来时带的两样东西——
       initialFind 非空时打开后自动高亮全部命中（顺带让 ClampStartIndex 忽略保存的进度）；
       startPage 是 PDF/漫画要直接落到的那一页（null = 按保存的进度/第一页）。 */
    private readonly string? _initialFind;

    private readonly int? _startPage;



    /// <summary>T4-5：换编码重建缓存后要回到的章号（null = 按正常流程定位）。</summary>

    private int? _restartChapter;



    private Task? _openTask;

    private CoreWebView2? _core;

    /// <summary>WebView 主机（持有 http://ebook.local/ 的目录映射；缓存根换目录时要重指）。</summary>
    private ReaderWebHost? _webHost;

    private ReaderSession? _session;



    /* T4-6 全屏（F11）：页面负责藏 UI，窗口这边切成无边最大化；退出时恢复原样 */

    private WindowStyle _savedWindowStyle = WindowStyle.SingleBorderWindow;

    private WindowState _savedWindowState = WindowState.Normal;

    private ReaderChannel? _channel;

    /// <summary>本阅读器窗口开出来的可视编辑窗口；用来保证"同一章不叠第二个"（T5-2W 第三步）。</summary>
    private ChapterVisualEditorWindow? _chapterEditorWindow;

    /// <summary>TXT 全文编辑窗口（同一本书只开一个；保存后重建缓存回原章）。</summary>
    private TxtEditorWindow? _txtEditorWindow;

    private int _txtEditorIndex = -1;

    /// <summary>那个可视编辑窗口正在编辑的章节序号（-1 = 当前没有）。</summary>
    private int _chapterEditorIndex = -1;



    /// <summary>Config.json 的读写入口（T4-1：分页模式要记住）。冒烟里可能不传。</summary>

    private readonly IConfigStore? _config;
    private readonly IEpubWorkspaceFactory? _workspaces;
    private readonly IChapterSourceService? _source;
    private readonly IVisualEditAutoFixService? _autofix;
    private readonly IReaderAssets? _assets;

    /// <summary>T6-1 判据 4：转换失败时用系统默认程序打开源文件。</summary>
    private readonly IShellLauncher? _shell;

    /// <summary>失败提示里「用系统默认程序打开」要打开的文件；null = 这本书没有可用源文件。</summary>
    private string? _fatalSourcePath;
    private readonly IBookRepository? _repository;

    /// <summary>T5-3 跨章查找替换（只写工作区 + 逐章校验才写盘）。冒烟里可能不传。</summary>
    private readonly IChapterFindReplaceService? _findReplace;

    /// <summary>文件级快照（T5-4）："全部替换前"自动建、可以一键撤销上一次替换。</summary>
    private readonly IWorkspaceSnapshotService? _snapshots;

    /// <summary>T6-2B：PDF 自绘引擎要用 pdfium 渲染每一页、取文本层与目录。非 PDF 链路不碰它。</summary>
    private readonly IPdfDocumentService? _pdfPages;

    /// <summary>T6-3：漫画（CBZ/CBR）按页读图。非漫画链路不碰它。</summary>
    private readonly IComicBookService? _comics;

    /// <summary>T6-5：书签与笔记的读写与导出。null 时阅读器里的书签/笔记按钮不落库。</summary>
    private readonly IAnnotationService? _annotations;

    /// <summary>
    /// 书籍编辑器入口（阅读器目录抽屉的「编辑目录」按钮走它）。
    /// 编辑保存后由 <see cref="Saved"/> 通知回来刷新目录与书籍信息。
    /// </summary>
    private readonly EditCommand? _edit;

    /// <summary>
    /// 书库扫描服务（用户 2026-10-20 要求）：TXT 全文编辑器保存后同步书库基线用，
    /// 免得书库把我们的内部编辑报成「文件被外部修改」。可空（冒烟构造窗口时可以不传）。
    /// </summary>
    private readonly ILibraryScanService? _libraryScan;

    /// <summary>Saved 事件只订阅一次（窗口可能重新初始化：TXT 换编码会重开）。</summary>
    private bool _editSubscribed;

    /// <summary>T6-5：最近一次从库里读到的书签与笔记（写操作后刷新，随消息回推给页面）。</summary>
    private IReadOnlyList<Bookmark> _bookmarks = Array.Empty<Bookmark>();

    private IReadOnlyList<Note> _notes = Array.Empty<Note>();

    /// <summary>
    /// T6-3：打开着的漫画归档。关窗时释放——RAR（尤其固实压缩）每次重开都要重新解压，
    /// 所以一个会话只开一次，按页读图复用同一个句柄。
    /// </summary>
    private IComicBook? _comic;

    /// <summary>
    /// T6-2B：阅读器内搜索的文本索引（懒建，只有 PDF 才用到）。
    /// 打开时不建：它要把整本书每页文本读一遍，等用户真的按了 Ctrl+F 再付这个代价。
    /// </summary>
    private PdfSearchIndex? _pdfSearch;

    /// <summary>
    /// T6-2B：PDF 当前页与总页数。自绘之后页码是**精确**的（滚动、跳目录都算）。
    /// T6-3 的漫画复用这两个字段：两者都是固定版式，进度就是"页码 + 总页数"。
    /// </summary>
    private int _pdfPage;
    private int _pdfTotalPages;

    /// <summary>PDF 目录（outline）：随 reader.load 下发，进的是 EPUB 用的同一个目录抽屉。</summary>
    private IReadOnlyList<PdfOutlineEntry> _pdfOutline = Array.Empty<PdfOutlineEntry>();

    /// <summary>
    /// 预载窗口（页）：外壳连续滚动时提前请求多少页。来自 Config.json 的
    /// <c>reader.pdfPreloadPages</c>（T7-4 起只在主程序「设置 → 阅读」组里改），范围 1–1000。
    /// 0 不再表示"不预载"：设置页最低就是 1 页。
    /// </summary>
    private int _pdfPreloadPages = DefaultPdfPreloadPages;

    /// <summary>PDF 打不开时的原因（加密没给密码 / 损坏 / 缺 pdfium），下发给外壳显示。null = 正常。</summary>
    private string? _pdfUnavailable;

    /// <summary>加密 PDF 的密码。只在内存里，不写配置、不落盘。</summary>
    private string? _pdfPassword;

    /// <summary>问密码最多几次（输错还能再试；试满就走"用系统默认程序打开"，不无限弹）。</summary>
    private const int MaxPdfPasswordAttempts = 3;

    /// <summary>
    /// T6-2B：已经渲染好、等着被 <c>WebResourceRequested</c> 取走的页位图（key = 地址里的 token）。
    ///
    /// **只在内存**：判据⑤要求 Data\Cache\Pages 不存在——我们不自建落盘的页缓存，
    /// 页的复用是 pdfium 服务内部那 20 页内存 LRU（PdfPageCache），这里只负责把位图
    /// 送到页面手里。容量比它略大，给正在解码的几张留位置；被挤掉的 token 再被请求就回 404，
    /// 外壳的 img.onerror 会重新要一次这一页（自愈，不卡死）。
    /// </summary>
    private readonly Dictionary<string, byte[]> _pdfPageImages = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _pdfPageImageOrder = new();

    /// <summary>
    /// 把 pdfium 渲染串起来：FPDF_* 不是线程安全的，同时渲染多页有崩溃风险。
    /// 静态＝进程级：两个阅读窗口（同一个 WebView2 环境）也不能并排渲染。
    /// </summary>
    private static readonly SemaphoreSlim PdfRenderGate = new(1, 1);

    /// <summary>页位图主机的响应器只挂一次。</summary>
    private bool _pdfPageHostReady;

    /// <summary>
    /// "首次成功服务页位图"这条日志只写一次。
    ///
    /// 【为什么要这条日志】用户实测报过"PDF 里的图片显示不出来"（破图 + alt 文字）。
    /// 现象有三种完全不同的成因，而它们留下的痕迹不同，只靠猜会查错方向：
    /// ① 响应器根本没挂上 → 一条日志都没有；
    /// ② 请求到了但 token 取不到（被 LRU 挤掉）→ 有 404 那条 WRN；
    /// ③ 请求正常服务了 → 有这条"首次服务"。
    /// 所以这条日志是"到底是哪一类"的判据，不是随手加的调试输出。
    /// </summary>
    private bool _pdfPageServedLogged;

    /// <summary>
    /// 内存里最多留几张页位图：跟着预载窗口走（预载 20 页就得留得住 20 页，
    /// 否则刚预载完就被自己挤掉）。最少 24 张，最多 64 张。
    /// </summary>
    private int PdfPageImageLimit => Math.Clamp(_pdfPreloadPages + 8, 24, 64);



    /// <summary>阅读模式的配置键。</summary>

    internal const string PaginationModeKey = "reader.paginationMode";



    /// <summary>Config.json 里记住的阅读主题（T4-2 判据 4：重开还是上次的主题）。</summary>

    internal const string ThemeKey = "reader.theme";



    /// <summary>PDF/漫画的预载窗口（页）配置键；T7-4 起由设置页写入，阅读器只读。</summary>

    internal const string PdfPreloadKey = AppSettingsKeys.PdfPreloadPages;



    /// <summary>没设置过时的默认预载页数（用户实测：往下滑时下面的图要等很久才出来）。</summary>

    internal const int DefaultPdfPreloadPages = AppSettingsKeys.DefaultPreloadPages;



    /// <summary>用户自定义主题的三个颜色（T4-2：用户可自己定一套主题）。</summary>

    internal const string CustomThemeBgKey = "reader.customTheme.bg";



    internal const string CustomThemeFgKey = "reader.customTheme.fg";



    internal const string CustomThemeLinkKey = "reader.customTheme.link";



    /* T4-3 排版设置：8 个键（IConfigStore 只有 GetString/GetBool，数字一律存成字符串） */

    internal const string TypographyFontSizeKey = "reader.typography.fontSize";



    internal const string TypographyLineHeightKey = "reader.typography.lineHeight";



    internal const string TypographyFontFamilyKey = "reader.typography.fontFamily";



    internal const string TypographyMarginTopKey = "reader.typography.marginTop";



    internal const string TypographyMarginBottomKey = "reader.typography.marginBottom";



    internal const string TypographyMarginLeftKey = "reader.typography.marginLeft";



    internal const string TypographyMarginRightKey = "reader.typography.marginRight";



    internal const string TypographyParaSpacingKey = "reader.typography.paraSpacing";



    internal const string TypographyTextIndentKey = "reader.typography.textIndent";



internal const string TypographyColumnsKey = "reader.typography.columns";



    internal const string TypographyColumnGapKey = "reader.typography.columnGap";



    internal const string TypographyColumnRuleKey = "reader.typography.columnRule";



    /// <summary>排版字段：JS 里的名字 -> Config.json 里的键。</summary>

    private static readonly (string Json, string Key)[] TypographyFields =

    {

        ("fontSize", TypographyFontSizeKey),

        ("lineHeight", TypographyLineHeightKey),

        ("fontFamily", TypographyFontFamilyKey),

        ("marginTop", TypographyMarginTopKey),

        ("marginBottom", TypographyMarginBottomKey),

        ("marginLeft", TypographyMarginLeftKey),

        ("marginRight", TypographyMarginRightKey),

        ("paraSpacing", TypographyParaSpacingKey),

        ("textIndent", TypographyTextIndentKey),

        ("columns", TypographyColumnsKey),

        ("columnGap", TypographyColumnGapKey),

        ("columnRule", TypographyColumnRuleKey),

    };



    /// <summary>上次存下的位置。打开时用它恢复（T1-6）。</summary>

    private ReaderProgressSnapshot? _savedProgress;



    /// <summary>最近一次收到并已排进保存队列的位置。</summary>

    private ReaderProgressSnapshot? _latestProgress;



    /// <summary>
    /// 用户 2026-10-20：跨格式打开时的"已读百分比"（0..1）。null = 不需要换算（同版式）。
    /// 例如上次用 EPUB 读到 55%，这次 EPUB 文件被删了、用 PDF 打开，
    /// 就按 55% × 总页数落到那一页；反过来（PDF -> EPUB/TXT）按 55% × 总章数落章。
    /// 由 ResolveCrossFormatStart 在读出保存进度后定下，PDF/漫画与文本两条落点逻辑各自用它。
    /// </summary>

    private double? _crossFormatPercent;

    /// <summary>
    /// 用户 2026-10-21：跨文本格式（EPUB ↔ TXT ↔ MOBI/AZW3）时按文字指纹找到的章号（0 基）。
    /// null = 没找到或不需要，退回按百分比估章。
    /// </summary>
    private int? _crossFormatTextChapter;

    /// <summary>跨文本格式找位最多翻多少章：起点由已读百分比估出来，通常一两章就命中。</summary>
    private const int CrossFormatSearchChapters = 24;



    public ReaderWindow(

        IReaderService reader,

        IWebViewEnvironmentProvider environments,

        IReadingProgressService progress,

        ILogger<ReaderWindow> log,

        Guid bookId,

        int startIndex = 0,

        IConfigStore? config = null,
        IEpubWorkspaceFactory? workspaces = null,
        IChapterSourceService? source = null,
        IVisualEditAutoFixService? autofix = null,
        IReaderAssets? assets = null,
        IBookRepository? repository = null,
        IChapterFindReplaceService? findReplace = null,
        IWorkspaceSnapshotService? snapshots = null,
        IShellLauncher? shell = null,
        IPdfDocumentService? pdfPages = null,
        IComicBookService? comics = null,
        int? startPage = null,
        string? initialFind = null,
        IAnnotationService? annotations = null,
        EditCommand? edit = null,
        IReadingSessionService? stats = null,
        ILibraryScanService? libraryScan = null)

    {

        _reader = reader;

        _environments = environments;

        _progress = progress;

        _log = log;

        _bookId = bookId;

        _startIndex = startIndex;

        _config = config;
        _workspaces = workspaces;
        _source = source;
        _autofix = autofix;
        _assets = assets;
        _repository = repository;
        _findReplace = findReplace;
        _snapshots = snapshots;
        _shell = shell;
        _pdfPages = pdfPages;
        _comics = comics;
        _startPage = startPage;
        _initialFind = string.IsNullOrWhiteSpace(initialFind) ? null : initialFind.Trim();
        _annotations = annotations;
        _edit = edit;
        _stats = stats;
        _libraryScan = libraryScan;



        InitializeComponent();



        Title = "阅读";

        Loaded += OnLoaded;

        Closed += OnClosed;

        /* TEXT 编码是"每本书一个"的共享设置：全文编辑器里改了也要让这里跟着换。 */
        TxtBookEncoding.Changed += OnTxtBookEncodingChanged;

    }



    // ---------- 只给冒烟脚本用的只读入口 ----------



    internal ReaderSession? Session => _session;

    internal ReaderChannel? Channel => _channel;



    /// <summary>最近一次收到的章节序号。-1 表示还没收到过。</summary>

    internal int LastChapterIndex { get; private set; } = -1;



    /// <summary>只给冒烟看：宿主这边的全屏状态（F11 有没有生效）。</summary>

    internal bool LastFullscreen { get; private set; }



    internal string? LastChapterTitle { get; private set; }



    /// <summary>最近一次上报的章内进度（0..1）。</summary>

    internal double LastPercent { get; private set; }



    /// <summary>WebView2 里渲染出来的正文纯文本长度（冒烟用来证明"真的渲染出了内容"）。</summary>

    internal int RenderedTextLength { get; private set; }



    /// <summary>

    /// 渲染出来的图片元素个数（HTML img 加 SVG image）。

    /// 封面页一个文字都没有、只有一张图，只看纯文本长度会误判成"没渲染"。

    /// </summary>

    internal int RenderedImageCount { get; private set; }



    /// <summary>加载失败的 HTML img 个数（已 complete 但 naturalWidth 为 0）。</summary>

    internal int RenderedBrokenImageCount { get; private set; }



    /// <summary>iframe 里当前实际显示的路径，如 "/chapters/0002.html"。</summary>

    internal string RenderedLocation { get; private set; } = "";



    /// <summary>

    /// iframe 里是不是 Chromium 的错误页（"它可能已被移动、编辑或删除"）。

    /// 判据取 #main-frame-error：这是 Chromium 内部结构，用它是因为

    /// 错误页同样有文字，只看正文长度区分不出来。真出问题时它若失效，

    /// 还有"章节文件是否真的存在"这条不依赖浏览器内部的判据兜着。

    /// </summary>

    internal bool RenderedIsErrorPage { get; private set; }



    /// <summary>

    /// 打开一次。重复调用返回同一个任务（Loaded 与冒烟脚本都会调它）。

    /// </summary>

    internal Task EnsureOpenAsync(CancellationToken ct = default)

        => _openTask ??= OpenCoreAsync(ct);



    private async void OnLoaded(object sender, RoutedEventArgs e)

    {

        // T6-1：MOBI/AZW3 首次打开要在后台转 EPUB，可能好几秒；必须先有反馈。

        ShowBusy("正在准备这本书");



        try

        {

            await EnsureOpenAsync().ConfigureAwait(true);

            HideBusy();

        }

        catch (AppException ex)

        {

            HideBusy();

            _log.LogWarning(ex, "打开阅读器失败：{Code}", ex.Code);

            await ShowFatalAsync(ex).ConfigureAwait(true);

        }

        catch (Exception ex)

        {

            HideBusy();

            _log.LogError(ex, "打开阅读器时出现未预期的错误");

            await ShowFatalAsync(new AppException(

                ErrorCode.ReaderOpenFailed,

                "打开这本书时出错了，详情见日志。",

                ex.ToString())).ConfigureAwait(true);

        }

    }



    private void ShowBusy(string text)

    {

        BusyText.Text = text;

        Web.Visibility = Visibility.Collapsed;

        BusyPanel.Visibility = Visibility.Visible;

    }



    private void HideBusy()

    {

        BusyPanel.Visibility = Visibility.Collapsed;

        Web.Visibility = Visibility.Visible;

    }



    /// <summary>

    /// T6-1 判据 4：打开失败（含转换失败）不再一关了之，窗口里给明确提示；

    /// 只要这本书还有可用源文件，就提供「用系统默认程序打开」按钮。

    /// </summary>

    private async Task ShowFatalAsync(AppException ex)

    {

        _fatalSourcePath = await FindExternalSourceAsync().ConfigureAwait(true);



        OpenExternalButton.Visibility = _fatalSourcePath is not null

            ? Visibility.Visible

            : Visibility.Collapsed;

        ErrorText.Text = ex.UserMessage;

        Web.Visibility = Visibility.Collapsed;

        ErrorPanel.Visibility = Visibility.Visible;

    }



    private async Task<string?> FindExternalSourceAsync()

    {

        if (_repository is null || _shell is null)

        {

            return null;

        }



        try

        {

            var book = await _repository.GetByIdAsync(_bookId).ConfigureAwait(true);

            if (book is null)

            {

                return null;

            }



            // 优先把正在读的 MOBI/AZW3 交出去，再退回这本书里任意一个存在的文件。

            return book.Formats

                .Where(f => !f.Missing && File.Exists(f.FilePath))

                .OrderByDescending(f => f.Format is BookFormatKind.Mobi or BookFormatKind.Azw3)

                .Select(f => f.FilePath)

                .FirstOrDefault();

        }

        catch (Exception lookupEx)

        {

            _log.LogWarning(lookupEx, "查找可交给外部阅读器的源文件失败");

            return null;

        }

    }



    private void OnOpenExternalClick(object sender, RoutedEventArgs e)

    {

        if (_shell is null || string.IsNullOrWhiteSpace(_fatalSourcePath))

        {

            return;

        }



        try

        {

            _shell.OpenFile(_fatalSourcePath!);

        }

        catch (Exception ex)

        {

            _log.LogWarning(ex, "用系统默认程序打开失败：{Path}", _fatalSourcePath);

            AppDialog.Show(

                this, "用系统默认程序打开失败，详情见日志。", "打开",

                MessageBoxButton.OK, MessageBoxImage.Warning);

        }

    }




    private async Task OpenCoreAsync(CancellationToken ct)

    {

        // 分段计时：首屏慢的时候必须能指出是哪一段慢的，否则只能靠猜

        var watch = System.Diagnostics.Stopwatch.StartNew();

        long atOpen = 0, atInit = 0, atMaterialize = 0, atNavigate = 0, atReady = 0;



        _session = await OpenSessionAsync(ct).ConfigureAwait(true);

        atOpen = watch.ElapsedMilliseconds;



        LastTextEncoding = _session.Manifest.TextEncoding;



        /* 上次读到哪了（T1-6）。读不到就当新书从第一章开始，不打扰用户。

           注意这里只读出来放在内存里：真正的定位由 reader.js 做，

           因为"存的位置还算不算数"要拿章节当前的 scrollHeight 比才知道。 */

        _savedProgress = await _progress

            .LoadAsync(_bookId, _session.Manifest.Spine.Count, ct)

            .ConfigureAwait(true);

        /* 用户 2026-10-20：跨格式打开（EPUB 删了改用 PDF，或反过来）时，
           章号/页码的含义不同，不能直接拿来恢复。这里先定下"要不要按百分比换算"，
           下面 ResolvePdfPageState / ClampStartIndex 再各自取用。 */
        await ResolveCrossFormatStartAsync(ct).ConfigureAwait(true);

        ResolvePdfPageState();

        /* T6-3：漫画的页数与起始页。与 PDF 共用 _pdfPage/_pdfTotalPages，
           因为两者的进度都是"页码 + 总页数"，没必要养两套字段。 */
        ResolveComicState();

        var manifest = _session.Manifest;

        Title = manifest.Title;



        _log.LogInformation(

            "阅读器打开：{Title} 共 {Count} 章，缓存命中={Cached}",

            manifest.Title, manifest.Spine.Count, _session.FromCache);



        var host = new ReaderWebHost(_environments);

        _webHost = host;

        await host.InitializeAsync(Web, _session.CacheRoot, browserExecutableFolder: null, ct: ct, libraryRoot: _session.FileRoot)

            .ConfigureAwait(true);



        atInit = watch.ElapsedMilliseconds;



        var core = host.Core ?? throw new InvalidOperationException("CoreWebView2 未就绪");

        _core = core;



        _channel = new ReaderChannel(core, _log);

        _channel.MessageReceived += OnReaderMessage;

        /* 阅读器里点「编辑目录」→ 编辑器保存 → 这里收到通知，重读目录并推给页面自动刷新。 */
        if (_edit is not null && !_editSubscribed)
        {
            _edit.Saved += OnBookEdited;
            _editSubscribed = true;
        }

        /* T6-2B：自绘引擎的页位图走内存虚拟主机——每一个请求由 WebResourceRequested
           用刚渲染好的 JPEG 字节应答（探针实测这条路通，见 build\t62b-probe-report.md）。
           T6-3：漫画的页位图走同一条通道（宿主编码后按请求送进去），不用另开主机名。
           预载页数要在挂主机之前读：页位图 LRU 容量跟着它走。 */
        _pdfPreloadPages = AppSettingsKeys.ReadPreloadPages(_config);

        if (_session.SourceFormat is BookFormatKind.Pdf or BookFormatKind.Cbz)

        {

            EnsurePdfPageHost(core);

        }



        // 第一章必须在导航之前物化好：reader.js 一收到 load 就会把 iframe 指过去，

        // 那时文件还不存在就会 404。

        /* T6-2B：PDF 没有章节可物化——它的正文由自绘引擎按页渲染（reader.pdfPage →
           reader.pdfPageReady），硬要物化"第 1 章"会抛 ReaderRenderFailed，
           整个阅读窗口都进不去 PDF 模式。 */
        if (_session.Manifest.Spine.Count > 0)

        {

            await _reader.MaterializeChapterAsync(_session, ClampStartIndex(), ct)

                .ConfigureAwait(true);

        }



        atMaterialize = watch.ElapsedMilliseconds;



        await NavigateAsync(core, ReaderWebHost.ToVirtualUrl("reader.html")
            + "?theme=" + Uri.EscapeDataString(ResolveInitialThemeId()), ct)

            .ConfigureAwait(true);



        atNavigate = watch.ElapsedMilliseconds;



        // reader.js 挂好监听后才会发 ready。不等它就直接 load，消息会丢。

        await _channel.WaitForAsync("reader.ready", ReadyTimeout, ct).ConfigureAwait(true);



        /* T6-5：书签与笔记要在 reader.load 之前读出来——页面收到 load 后
           会立刻渲染面板与高亮，晚一步就会先闪一个空面板。 */
        await LoadAnnotationsAsync(ct).ConfigureAwait(true);

        SendLoad();



        /* T7-3B：书已经成功打开（reader.load 已下发）——从这里开始记阅读会话。
           采集失败只记 WRN：统计是锦上添花，绝不能挡住看书。 */
        if (_stats is not null)
        {
            try
            {
                await _stats.StartAsync(_bookId, ct).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "阅读会话采集启动失败 {BookId}", _bookId);
            }

            /* PDF / 漫画的页码是进度基线：记下来，第一次翻页只算增量，
               不把"恢复到第 N 页"算成这一次读了 N 页。 */
            if (_session.SourceFormat is BookFormatKind.Pdf or BookFormatKind.Cbz)
            {
                _statsPage = _pdfPage;
            }
        }

        atReady = watch.ElapsedMilliseconds;



        _log.LogInformation(

            "阅读器已就绪：{Title}；分段耗时 打开缓存={A}ms 初始化WebView2={B}ms" +

            " 物化章节={C}ms 导航={D}ms 等ready={E}ms 合计={F}ms",

            manifest.Title,

            atOpen, atInit - atOpen, atMaterialize - atInit,

            atNavigate - atMaterialize, atReady - atNavigate, atReady);

    }



    private int ClampStartIndex()

    {

        var count = _session?.Manifest.Spine.Count ?? 0;



        if (count == 0)

        {

            return 0;

        }



        /* 优先用存下来的章号（T1-6）。这一条不是可选的：

           "导航之前章节文件必须已存在"是阅读器的硬不变式，而页面收到的

           startIndex 就是它马上要打开的那一章。若这里仍返回第一章，

           页面就会去要一个还没物化的文件，直接渲染成 Chromium 错误页

           （T1-5 踩过同一个坑）。 */

        /* T6-4：从全文搜索结果跳进来时，用户要的是"这一章"，不是"上次读到哪"。
           所以带了查找词就强制用调用方给的章号，忽略保存的进度。 */
        var wanted = _initialFind is not null && _startIndex >= 0
            ? _startIndex
            : _restartChapter ?? _savedProgress?.ChapterIndex ?? _startIndex;

        /* 用户 2026-10-20：跨格式（上次读的是 PDF/漫画）时，存的"章号"其实是同一字段里的页码，
           直接当章号会落到错的地方。按已读百分比换算成章号（章内位置近似即可）。
           同格式（文本->文本）不走这里，继续用原来精确的章号恢复；
           _savedProgress 为 null 说明进度刚被作废（换编码 / 编辑后重建），也不该换算。 */
        /* 用户 2026-10-21：跨文本格式（EPUB ↔ TXT ↔ MOBI/AZW3）已按文字指纹找对了章，直接用。
           这条优先于百分比：文字对上了就是同一段内容，百分比只是"大概在哪一章"。 */
        if (_crossFormatTextChapter is { } textChapter && _savedProgress is not null && _initialFind is null)
        {
            wanted = textChapter;

            _log.LogInformation(
                "跨格式定位：文字指纹命中第 {Chapter} 章（共 {Count} 章）", wanted + 1, count);
        }
        else if (_crossFormatPercent is { } crossPercent && _savedProgress is not null && _initialFind is null)
        {
            wanted = CrossFormatLocator.ChapterFromPercent(crossPercent, count);

            _log.LogInformation(
                "跨格式定位：按已读 {Percent:P0} 换算出第 {Chapter} 章（共 {Count} 章）",
                crossPercent, wanted + 1, count);
        }

        return Math.Clamp(wanted, 0, count - 1);

    }



    private static async Task NavigateAsync(

        CoreWebView2 core, string url, CancellationToken ct)

    {

        var navigated = new TaskCompletionSource<bool>(

            TaskCreationOptions.RunContinuationsAsynchronously);



        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)

            => navigated.TrySetResult(args.IsSuccess);



        core.NavigationCompleted += OnCompleted;



        try

        {

            core.Navigate(url);



            var ok = await navigated.Task

                .WaitAsync(TimeSpan.FromSeconds(30), ct)

                .ConfigureAwait(true);



            if (!ok)

            {

                throw new AppException(

                    ErrorCode.ReaderRenderFailed,

                    "阅读器界面没能加载出来，试试重新打开。",

                    "navigation failed: " + url);

            }

        }

        finally

        {

            core.NavigationCompleted -= OnCompleted;

        }

    }



    /// <summary>用户手选过的 TXT 编码存在 Config.json 的这个前缀下（每本书一个键，与编辑器共用）。</summary>

    internal const string TextEncodingKeyPrefix = TxtBookEncoding.ConfigKeyPrefix;



    /// <summary>只给冒烟看：这次会话用的文本编码（EPUB 书为 null）。</summary>

    internal string? LastTextEncoding { get; private set; }

    /// <summary>
    /// 只给冒烟看：缓存清单里记的源路径（可能是建缓存那一刻的旧路径）。
    /// 与 <see cref="LiveSourcePath"/> 不同就说明"缓存里的路径已经过期"，编辑/读取都必须用后者。
    /// </summary>

    internal string? CachedSourcePath => _session?.Manifest.SourcePath;

    /// <summary>只给冒烟看：本次从书库记录取到的最新源路径（阅读正文用的就是它）。</summary>

    internal string? LiveSourcePath => _session?.SourcePath;



    private string TextEncodingKey(Guid bookId) => TxtBookEncoding.ConfigKey(bookId);

    /// <summary>
    /// TXT 编码在**另一个窗口**（全文编辑器）里被改了：阅读器也要跟着换（用户 2026-10-20 要求）。
    /// 编码切换是"重建缓存 + 重载页面"的既有路径，这里只负责触发。
    /// </summary>
    private void OnTxtBookEncodingChanged(object? sender, TxtBookEncodingChangedEventArgs e)
    {
        if (e.BookId != _bookId || _session is null || _core is null)
        {
            return;
        }

        if (!string.Equals(_session.Manifest.Format, "TXT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.EncodingName, LastTextEncoding, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = ChangeTextEncodingAsync(e.EncodingName);
    }



    private void SendLoad()

    {

        if (_session is null || _channel is null)

        {

            return;

        }



        var manifest = _session.Manifest;



        // 上次用的阅读模式（T4-1 判据：分页模式要保存到设置）。读不到就用滚动模式

        // 计划 T4-1 明确要求"默认滚动阅读"。

        _pagedMode = _config?.GetBool(PaginationModeKey, false) ?? false;



        // T4-2 判据 4：上次用的主题。读不到就用白色主题。

        _themeId = ResolveInitialThemeId();

        LastTheme = _themeId;



        // 预载窗口：在挂页主机前已经读过（见上面 _pdfPreloadPages 的赋值），这里不再重复读。



        _channel.Post("reader.load", new

        {

            bookId = manifest.BookId,

            title = manifest.Title,

            startIndex = ClampStartIndex(),

            mode = _pagedMode ? "paged" : "scroll",

            theme = _themeId,

            typography = new

            {

                fontSize = ParseInt(_config?.GetString(TypographyFontSizeKey, null), 17),

                lineHeight = ParseDouble(_config?.GetString(TypographyLineHeightKey, null), 1.75),

                fontFamily = _config?.GetString(TypographyFontFamilyKey, null) ?? "Microsoft YaHei UI",

                marginTop = ParseInt(_config?.GetString(TypographyMarginTopKey, null), 5),

                marginBottom = ParseInt(_config?.GetString(TypographyMarginBottomKey, null), 5),

                marginLeft = ParseInt(_config?.GetString(TypographyMarginLeftKey, null), 5),

                marginRight = ParseInt(_config?.GetString(TypographyMarginRightKey, null), 5),

                paraSpacing = ParseDouble(_config?.GetString(TypographyParaSpacingKey, null), 0.8),

                textIndent = ParseDouble(_config?.GetString(TypographyTextIndentKey, null), 2),

                columns = ParseInt(_config?.GetString(TypographyColumnsKey, null), 1),

                columnGap = ParseInt(_config?.GetString(TypographyColumnGapKey, null), 24),

                columnRule = string.Equals(_config?.GetString(TypographyColumnRuleKey, null), "true", StringComparison.OrdinalIgnoreCase),

            },

            fonts = FontOptions(),

            customTheme = new

            {

                bg = _config?.GetString(CustomThemeBgKey, "#20242B") ?? "#20242B",

                fg = _config?.GetString(CustomThemeFgKey, "#D8DEE9") ?? "#D8DEE9",

                link = _config?.GetString(CustomThemeLinkKey, "#88C0D0") ?? "#88C0D0",

            },

            /* T6-4：从全文搜索结果跳进来时不恢复进度——页面那边的 pendingLocator
               会把章节位置拉回"上次读到的地方"，用户就看不到自己点的那一章。
               查找词随 load 下发，页面打开章节后自动画出全部命中。 */
            progress = _initialFind is null ? BuildProgressPayload() : null,
            findText = _initialFind,

            /* T6-2B：kind 决定外壳取哪一套工具栏项目集。PDF 下 url 只是原始文件在文件主机上的
               地址（[用系统默认程序打开] 与兜底用），**正文不再由它渲染**——自绘引擎的每一页
               都由宿主用 pdfium 渲染后按请求送进来（reader.pdfPage → reader.pdfPageReady）。
               outline 是 PDF 目录（page 0 基，-1 = 不可跳转的分组标题），进 EPUB 那个同一个抽屉。
               unavailable 非空时正文区显示失败提示（加密不给密码 / 文件坏了 / 缺 pdfium）。 */
            kind = _session.SourceFormat == BookFormatKind.Pdf
                ? "pdf"
                : _session.SourceFormat == BookFormatKind.Cbz
                    ? "comic"
                    : _session.SourceFormat == BookFormatKind.Txt ? "txt" : "epub",

            pdf = new
            {
                url = _session.PdfUrl,
                startPage = _pdfPage,
                totalPages = _pdfTotalPages,
                outline = _pdfOutline.Select(o => new { title = o.Title, level = o.Level, page = o.PageIndex }),
                unavailable = _pdfUnavailable,
                /* 预载窗口：连续滚动时外壳提前请求多少页（工具栏「预载」下拉，落 Config.json）。 */
                preloadPages = _pdfPreloadPages,
            },



            /* T4-5：TXT 的编码信息。textEncodingOptions 非空时页面才会显示编码下拉

               （判据 2：识别不准要能手动指定，选完立即重排）。 */

            textEncoding = _session.Manifest.TextEncoding,

            textEncodingConfident = _session.Manifest.TextEncodingConfident,

            textEncodingOptions = _session.Manifest.TextEncoding is null

                ? Array.Empty<string>()

                : TextEncodingSupport.SupportedNames,

            textPreview = TextPreview(),



            // 换编码会重建缓存，章节路径却不变：给页面一个令牌绕开 WebView2 的 HTTP 缓存

            contentToken = Guid.NewGuid().ToString("N")[..8],

            spine = manifest.Spine

                .Select(c => new { index = c.Index, href = c.Href, title = c.Title })

                .ToList(),

            /* T4-4 目录：外层是 TocEntry，直接塞进去会被序列化成 PascalCase（页面读不到），

               所以照 spine 的做法手写成小驼峰。 */

            toc = manifest.Toc.Select(ProjectTocEntry).ToList(),

            tocFallback = manifest.TocFallback,

            /* T6-5：书签与笔记随 load 一起下发（页面据此画面板与正文高亮）。 */
            annotations = new
            {
                bookmarks = ProjectBookmarks(),
                notes = ProjectNotes(),
            },

        });

    }



    /// <summary>目录项投影成页面认识的小驼峰（含子项，递归）。</summary>

    private static object ProjectTocEntry(TocEntry entry) => new

    {

        title = entry.Title,

        level = entry.Level,

        spineIndex = entry.SpineIndex,

        fragment = entry.Fragment,

        href = entry.SourceHref,

        children = entry.Children.Select(ProjectTocEntry).ToList(),

    };



    /// <summary>

    /// 下发给页面的「上次读到哪」（T1-6）。

    /// 字段名必须手写成小驼峰：ReaderChannel 用 System.Text.Json 的默认设置

    /// 序列化匿名对象，直接塞 ReaderLocator 模型会变成 PascalCase，页面读不到。

    /// </summary>

    /// <summary>
    /// T6-2B：定下 PDF 的"当前页 / 总页数 / 目录"，需要时问用户要密码。
    ///
    /// 总页数优先用上次随进度一起存下来的——只有首次打开 / 没存过才真的去问 pdfium
    /// （设计文档 §6.5.4："导入时或首次打开时，缓存未命中时用 pdfium 打开一次"）。
    ///
    /// 【为什么现在必须管密码】阶段一的 PDF 是 Chromium 内置查看器，加密书由它自己弹密码框，
    /// 所以那时"读不出页数"不算失败（页数未知、百分比显示"—"）。T6-2B 换成自绘之后，
    /// 没有密码就**一页都渲染不出来**——再不问用户，加密书就从"能读"退化成"读不了"。
    /// 用户始终不配合（取消 / 试满三次）时仍然不拦着打开：把原因交给外壳显示，
    /// [用系统默认程序打开] 常驻可用。
    /// </summary>
    private void ResolvePdfPageState()
    {
        if (_session is null || _session.SourceFormat != BookFormatKind.Pdf || _pdfPages is null)
        {
            return;
        }

        _pdfPage = _startPage ?? Math.Max(0, _savedProgress?.PageIndex ?? 0);
        _pdfTotalPages = _savedProgress?.TotalPages ?? 0;

        var attempts = 0;

        while (true)
        {
            try
            {
                if (_pdfTotalPages <= 0)
                {
                    _pdfTotalPages = _pdfPages.GetPageCount(_session.SourcePath, _pdfPassword);
                }

                _pdfOutline = _pdfPages.ReadOutline(_session.SourcePath, _pdfPassword);

                /* 用户 2026-10-20：跨格式（上次读的是文本）时按百分比换算页码。
                   同格式（上次就是 PDF）不走这里，继续用保存的精确页码。
                   _startPage 是调用方明确指定的落点（全文搜索等），优先级最高。 */
                if (_startPage is null && _crossFormatPercent is { } crossPercent && _pdfTotalPages > 0)
                {
                    _pdfPage = CrossFormatLocator.PageFromPercent(crossPercent, _pdfTotalPages);
                }

                _log.LogInformation(
                    "PDF 元数据就绪：{Pages} 页，目录 {Count} 条（带密码={HasPassword}），起始页 {Page}",
                    _pdfTotalPages, _pdfOutline.Count, _pdfPassword is not null, _pdfPage);

                return;
            }
            catch (AppException ex) when (ex.Code == ErrorCode.DrmDetected && attempts < MaxPdfPasswordAttempts)
            {
                attempts++;

                var typed = PromptPdfPassword(attempts > 1);

                if (string.IsNullOrEmpty(typed))
                {
                    _log.LogInformation("用户没有提供 PDF 密码，正文按打不开处理");
                    _pdfUnavailable = "这本书的 PDF 有密码，没有密码读不出正文。";
                    return;
                }

                _pdfPassword = typed;
            }
            catch (Exception ex)
            {
                // 损坏 / 缺 pdfium / 其它：都不该拦着用户打开，交给外壳显示失败提示
                _log.LogWarning(ex, "读 PDF 元数据失败：{Path}", _session.SourcePath);
                _pdfUnavailable = "这本书的 PDF 打不开（文件可能损坏，或缺少 PDF 渲染组件）。";
                return;
            }
        }
    }

    /// <summary>
    /// 用户 2026-10-20/21：判断这次打开是不是"换了格式"，是就定下落点怎么换算。
    /// </summary>
    /// <remarks>
    /// 【为什么要换算】阅读进度只有一条记录（reading_progress 按书 Id 存），
    /// 里面可能是 EPUB 的"章号 + 章内比例"，也可能是 PDF 的"页码 + 总页数"。
    /// 换成另一个格式打开时这些字段的含义全变了：
    /// 1. 目标是固定版式（PDF / 漫画）：按已读百分比换算页码；
    /// 2. 目标文字类、来源固定版式：按百分比换算章号（用户允许"模糊"定位）；
    /// 3. 目标文字类、来源也是文字类（EPUB ↔ TXT ↔ MOBI/AZW3）：**按文字指纹精确找章**
    ///    （用户 2026-10-21 明确要求：能精确定位文字的书籍要落到同一段文字，不能只看百分比）。
    /// 同一格式（或旧数据判断不出版式差异）继续走原来的精确恢复：章号 / 页码本身是准的。
    /// 判断"进度来自哪个格式"优先用 book_reading_format.progress_format；
    /// 旧数据没有它，退回原来的线索：PageIndex 有没有值（文本快照的 PageIndex 恒为 null）。
    /// </remarks>
    private async Task ResolveCrossFormatStartAsync(CancellationToken ct)
    {
        _crossFormatPercent = null;
        _crossFormatTextChapter = null;

        if (_savedProgress is null || _session is null)
        {
            return;
        }

        var targetFormat = _session.SourceFormat;
        var targetFixed = CrossFormatLocator.IsFixedLayout(targetFormat);

        var sourceFormat = _savedProgress.SourceFormat;
        var fromFixed = sourceFormat is { } src
            ? CrossFormatLocator.IsFixedLayout(src)
            : CrossFormatLocator.ProgressFromFixedLayout(_savedProgress.PageIndex);

        // 同一个格式：章号 / 页码直接可用，换成百分比只会更粗
        if (sourceFormat == targetFormat)
        {
            return;
        }

        // 旧数据（不知道来源格式）且版式类别相同：沿用原来的精确恢复
        if (sourceFormat is null && targetFixed == fromFixed)
        {
            return;
        }

        /* 必须用库里存的原始百分比：跨格式时本次打开的"章数"可能是 0（PDF 没有章），
           快照自己的 Percent 会算成 0，那样"读到 55%"这条信息就丢了。 */
        var percent = _savedProgress.StoredPercent ?? _savedProgress.Percent;

        if (percent <= 0)
        {
            return;
        }

        _crossFormatPercent = Math.Clamp(percent, 0, 1);

        var chapterCount = _session.Manifest.Spine.Count;

        /* 文字 -> 文字：按上次保存的文本指纹精确找章（用户 2026-10-21）。
           找不到（指纹缺失 / 书被改过 / 限定章数内没命中）就退回百分比估章，绝不挡着打开。 */
        if (!targetFixed && !fromFixed && chapterCount > 0
            && _savedProgress.Fingerprint?.Pre is { Length: > 0 } needle)
        {
            var hint = CrossFormatLocator.ChapterFromPercent(_crossFormatPercent.Value, chapterCount);

            try
            {
                var found = await _reader
                    .FindChapterByTextAsync(_session, needle, hint, CrossFormatSearchChapters, ct)
                    .ConfigureAwait(true);

                if (found is { } index)
                {
                    _crossFormatTextChapter = index;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 找位失败只是"不够精确"，绝不能因此打不开书
                _log.LogWarning(ex, "跨格式文字找位失败，退回按百分比定位");
            }
        }

        _log.LogInformation(
            "跨格式定位：已读 {Percent:P0}，本次用 {Format} 打开，落点={Fallback}",
            _crossFormatPercent,
            targetFormat,
            _crossFormatTextChapter is { } chapter
                ? "文字命中第 " + (chapter + 1) + " 章"
                : "按百分比估算");
    }

    /// <summary>
    /// T6-3：打开漫画包，拿到页数与起始页。**不预先渲染任何页面**——
    /// 页位图等外壳来要（reader.pdfPage），这与 PDF 的按需渲染同一条纪律。
    /// 打不开时把原因交给外壳显示，不拦着用户打开窗口（[用系统默认程序打开] 常驻可用）。
    /// </summary>
    private void ResolveComicState()
    {
        if (_session is null || _session.SourceFormat != BookFormatKind.Cbz || _comics is null)
        {
            return;
        }

        _pdfPage = _startPage ?? Math.Max(0, _savedProgress?.PageIndex ?? 0);
        _pdfTotalPages = _savedProgress?.TotalPages ?? 0;

        try
        {
            _comic = _comics.Open(_session.SourcePath);
            _pdfTotalPages = _comic.PageCount;

            /* 用户 2026-10-20：跨格式（上次读的是文本）时按百分比换算页码；
               同格式继续用保存的精确页码。_startPage（全文搜索指定）优先。 */
            if (_startPage is null && _crossFormatPercent is { } crossPercent && _pdfTotalPages > 0)
            {
                _pdfPage = CrossFormatLocator.PageFromPercent(crossPercent, _pdfTotalPages);
            }
            else if (_pdfTotalPages > 0)
            {
                _pdfPage = Math.Min(_pdfPage, _pdfTotalPages - 1);
            }

            _log.LogInformation(
                "漫画包已就绪：{Pages} 页（从第 {Page} 页开始）", _pdfTotalPages, _pdfPage + 1);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "打开漫画包失败：{Path}", _session.SourcePath);

            _pdfUnavailable = ex is AppException app
                ? app.UserMessage
                : "这本漫画打不开（文件可能损坏，或压缩包用了不支持的算法）。";
        }
    }

    /// <summary>
    /// 问一次 PDF 密码。就地拼一个最小窗口（与 ConvertWindow 的输入框同一套路，
    /// 仓库里没有可复用的输入对话框类；用 PasswordBox 而不是 TextBox，别把密码显示成明文）。
    /// 返回 null / 空串 = 用户取消。
    /// </summary>
    private string? PromptPdfPassword(bool retry)
    {
        var box = new PasswordBox
        {
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 14,
            MinWidth = 260,
        };

        var tip = new TextBlock
        {
            Text = retry ? "密码不对，再试一次：" : "这本书的 PDF 有密码，请输入打开密码：",
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };

        var ok = new Button { Content = "确定", Width = 84, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "取消", Width = 84, IsCancel = true };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var stack = new StackPanel { Margin = new Thickness(18) };

        stack.Children.Add(tip);
        stack.Children.Add(box);
        stack.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "PDF 密码",
            Content = stack,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };

        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => box.Focus();

        return dialog.ShowDialog() == true ? box.Password : null;
    }

    private object? BuildProgressPayload()
    {

        if (_savedProgress is null)

        {

            return null;

        }

        /* 用户 2026-10-20：跨格式打开时，旧定位符里的"章号"含义已经不对了。
           目标是 PDF/漫画：落点由 pdf.startPage 按百分比算好，这里不再交定位符，
           否则页面会按旧的章号去打开章节；
           目标是文本：把定位符换成换算后的章号 + 章内比例（章内位置按百分比近似）。 */
        if (_crossFormatPercent is { } crossPercent && _session is not null)
        {
            if (CrossFormatLocator.IsFixedLayout(_session.SourceFormat))
            {
                return null;
            }

            /* 用户 2026-10-21：文字 -> 文字时已经用指纹找对了章，就把章号交出去，
               并把原指纹一起给页面——页面在章内滚到同一段文字（reader.js 的最后一级回退）。
               这里刻意不再传章内百分比：那会把章内位置带偏，白白浪费"精确"这两个字。 */
            if (_crossFormatTextChapter is { } precise)
            {
                var savedFingerprint = _savedProgress?.Fingerprint;

                return new
                {
                    locator = new
                    {
                        chapterHref = (string?)null,
                        chapterIndex = precise,
                        percentInChapter = (double?)null,
                        scrollTop = (double?)null,
                        scrollHeight = (double?)null,
                    },
                    fingerprint = savedFingerprint is null
                        ? null
                        : new
                        {
                            pre = savedFingerprint.Pre,
                            mid = savedFingerprint.Mid,
                            post = savedFingerprint.Post,
                        },
                };
            }

            var count = _session.Manifest.Spine.Count;
            var chapter = CrossFormatLocator.ChapterFromPercent(crossPercent, count);
            var span = count > 0 ? crossPercent * count : 0;
            var inChapter = Math.Clamp(span - Math.Floor(span), 0, 1);

            return new
            {
                locator = new
                {
                    chapterHref = (string?)null,
                    chapterIndex = chapter,
                    percentInChapter = (double?)inChapter,
                    scrollTop = (double?)null,
                    scrollHeight = (double?)null,
                },
                fingerprint = (object?)null,
            };
        }

        var locator = _savedProgress.Locator;

        var fp = _savedProgress.Fingerprint;



        return new

        {

            locator = new

            {

                chapterHref = locator.ChapterHref,

                chapterIndex = locator.ChapterIndex,

                percentInChapter = locator.PercentInChapter,

                scrollTop = locator.ScrollTop,

                scrollHeight = locator.ScrollHeight,

            },

            fingerprint = fp is null

                ? null

                : new { pre = fp.Pre, mid = fp.Mid, post = fp.Post },

        };

    }



    // ---------- 分页阅读（T4-1） ----------



    /// <summary>当前阅读模式："scroll" | "paged"。默认滚动（计划 T4-1 的要求）。</summary>

    internal string LastReaderMode { get; private set; } = "scroll";



    private bool _pagedMode;



    /// <summary>
    /// T7-3：阅读器的**默认**主题。用户显式选过 reader.theme 就尊重用户；
    /// 没选过则继承主程序主题（暗→dark、护眼→green、米黄→sepia、亮→light、
    /// 自定义按所选底色的深浅→dark/light、跟随系统按系统当前值），
    /// 否则深色主程序点开一本书会突然白底一闪。
    /// </summary>
    private string ResolveInitialThemeId()
    {
        var stored = _config?.GetString(ThemeKey, null);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            return stored;
        }

        return Themes.ThemeManager.Mode switch
        {
            Themes.AppThemeMode.Dark => "dark",
            Themes.AppThemeMode.Green => "green",
            Themes.AppThemeMode.Sepia => "sepia",
            Themes.AppThemeMode.Custom => Themes.ThemeManager.IsDark ? "dark" : "light",
            Themes.AppThemeMode.Light => "light",
            _ => Themes.ThemeManager.IsSystemLight() ? "light" : "dark",
        };
    }

    /// <summary>当前阅读主题 id（T4-2）。默认白色主题。</summary>

    internal string LastTheme { get; private set; } = "light";



    private string _themeId = "light";



    /// <summary>
    /// 系统字体（判据 4：字体下拉要能列出系统中文字体；`InstalledFontCollection` 列出的中文名可能是英文名）。
    /// 设置页「阅读 → 字体」复用同一份列表，保证两处选项一致。
    /// </summary>
    internal static string[] FontOptions()

    {

        try

        {

            return System.Windows.Media.Fonts.SystemFontFamilies

                .Select(f => f.Source)

                .Where(s => !string.IsNullOrWhiteSpace(s))

                .Distinct(StringComparer.OrdinalIgnoreCase)

                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)

                .Take(300)

                .ToArray();

        }

        catch (Exception)

        {

            return Array.Empty<string>();

        }

    }



    private static int ParseInt(string? text, int fallback) =>

        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;



    private static double ParseDouble(string? text, double fallback) =>

        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;



    /// <summary>排版设置落盘（阅读器上报的是字符串，失败只记日志）。</summary>

    private async Task PersistTypographyAsync(JsonObject? payload)

    {

        if (_config is null || payload is null)

        {

            return;

        }



        try

        {

            foreach (var (json, key) in TypographyFields)

            {

                var text = ReadString(payload, json);



                if (!string.IsNullOrWhiteSpace(text))

                {

                    await _config.SetAsync(key, JsonValue.Create(text)).ConfigureAwait(true);

                }

            }



            await _config.SaveAsync().ConfigureAwait(true);

        }

        catch (Exception ex)

        {

            _log.LogWarning(ex, "保存排版设置失败（不影响继续阅读）");

        }

    }



    /// <summary>只给冒烟用：在阅读器页面里跑一段脚本。</summary>

    internal Task<string?> ExecAsync(string script) => RunReaderScriptAsync(script);

    /// <summary>只给冒烟用：把 WebView2 视口截成 PNG（网页里的工具栏也在里面，用来看行布局）。</summary>
    internal async Task<bool> CapturePreviewAsync(string path)
    {
        if (_core is null)
        {
            return false;
        }

        try
        {
            await using var stream = File.Create(path);
            await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream)
                .ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "阅读器截图失败：{Path}", path);
            return false;
        }
    }



    /// <summary>只给冒烟用：读排版设置当前状态（含页号/页数/章内比例，用于验"改字号后位置误差 <= 1 屏"）。</summary>

    internal async Task<ReaderTypographyState?> ReadTypographyAsync()

    {

        var text = await RunReaderScriptAsync("JSON.stringify(window.__readerDebug.typographyProbe())").ConfigureAwait(true);



        if (string.IsNullOrWhiteSpace(text))

        {

            return null;

        }



        try

        {

            var json = text.Trim();



            if (json.StartsWith("\"", StringComparison.Ordinal))

            {

                json = JsonSerializer.Deserialize<string>(json) ?? "{}";

            }



            using var doc = JsonDocument.Parse(json);

            var r = doc.RootElement;



            string Text(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String

                ? v.GetString() ?? string.Empty

                : string.Empty;



            double Num(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number

                ? v.GetDouble()

                : 0;



            var fonts = new List<string>();



            if (r.TryGetProperty("fontOptions", out var options) && options.ValueKind == JsonValueKind.Array)

            {

                foreach (var item in options.EnumerateArray())

                {

                    if (item.ValueKind == JsonValueKind.String)

                    {

                        fonts.Add(item.GetString() ?? string.Empty);

                    }

                }

            }



            var computed = new List<string>();



            if (r.TryGetProperty("computed", out var computedNode) && computedNode.ValueKind == JsonValueKind.Array)

            {

                foreach (var item in computedNode.EnumerateArray())

                {

                    computed.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.ToString());

                }

            }



            return new ReaderTypographyState(

                Num("fontSize"),

                Num("lineHeight"),

                Text("fontFamily"),

                Num("marginTop"),

                Num("marginBottom"),

                Num("marginLeft"),

                Num("marginRight"),

                Num("columns"),

                Num("columnGap"),

                string.Equals(Text("columnRule"), "true", StringComparison.OrdinalIgnoreCase),

                Num("pageStride"),

                Num("columnWidth"),

                Num("pageWidth"),

                Num("marginPx"),

                string.Empty,

                computed,

                (int)Num("pageIndex"),

                (int)Num("pageCount"),

                Num("percentInChapter"),

                fonts);

        }

        catch (Exception)

        {

            /* 探针拿不到/结构不对（比如阅读器脚本报错）时不要让整轮冒烟崩掉 */

            return null;

        }

    }



    /// <summary>自定义主题颜色落盘（只记日志，失败不影响阅读）。</summary>

    private void PersistCustomTheme(string? bg, string? fg, string? link)

    {

        if (_config is null)

        {

            return;

        }



        _ = PersistCustomThemeAsync(bg, fg, link);

    }



    private async Task PersistCustomThemeAsync(string? bg, string? fg, string? link)

    {

        try

        {

            if (!string.IsNullOrWhiteSpace(bg))

            {

                await _config!.SetAsync(CustomThemeBgKey, JsonValue.Create(bg)).ConfigureAwait(true);

            }



            if (!string.IsNullOrWhiteSpace(fg))

            {

                await _config!.SetAsync(CustomThemeFgKey, JsonValue.Create(fg)).ConfigureAwait(true);

            }



            if (!string.IsNullOrWhiteSpace(link))

            {

                await _config!.SetAsync(CustomThemeLinkKey, JsonValue.Create(link)).ConfigureAwait(true);

            }



            await _config!.SaveAsync().ConfigureAwait(true);

        }

        catch (Exception ex)

        {

            _log.LogWarning(ex, "保存自定义主题颜色失败（不影响继续阅读）");

        }

    }



    /// <summary>打开会话：TXT 会带上用户上次手选的编码。</summary>

    private static int GetIndex(System.Text.Json.JsonElement root)
        => root.TryGetProperty("index", out var node) && node.TryGetInt32(out var value) ? value : 0;

    /// <summary>
    /// 阅读模式下「编辑本章」：为这本书打开（或复用）工作区，再打开可视编辑窗口并定位到这一章。
    /// 依赖没注入（workspaces/source/assets/repository 任一为空）时给一句人话提示，不静默失败。
    /// </summary>
    internal async Task OpenEditorForChapterAsync(int index)
    {
        try
        {
            if (_session is null)
            {
                _log.LogWarning("编辑本章不可用：还没有打开任何书");
                return;
            }

            /* 用户 2026-10-20：TXT 的「编辑本章」= 整本 txt 的全文编辑（像记事本），
               只是打开后光标落在当前章标题行。
               TXT 没有 EPUB 工作区，所以这条分支必须在"工作区/编辑服务"检查**之前**：
               它只用阅读清单（路径 + TextStart），与工作区无关。 */
            if (string.Equals(_session.Manifest.Format, "TXT", StringComparison.OrdinalIgnoreCase))
            {
                OpenTxtEditorForChapter(index);
                return;
            }

            if (_workspaces is null || _source is null || _autofix is null || _assets is null
                || _repository is null)
            {
                _log.LogWarning("编辑本章不可用：工作区/编辑服务未注入");
                return;
            }

            var book = await _repository.GetByIdAsync(_bookId).ConfigureAwait(true);
            var format = book?.Formats.FirstOrDefault(f => !f.Missing && File.Exists(f.FilePath));

            if (book is null || format is null)
            {
                _log.LogWarning("编辑本章不可用：书库记录或文件找不到");
                return;
            }

            // 章节用**包内路径**（SourceHref）：工作区根目录就是解压根，两者一致
            var chapters = _session.Manifest.Spine
                .Select(chapter => new SourceChapter(chapter.Index, chapter.SourceHref, chapter.Title ?? chapter.SourceHref))
                .ToList();

            var target = Math.Clamp(index, 0, Math.Max(chapters.Count - 1, 0));

            /* 同一章的可视编辑窗口已经开着就**前置**，不要再开一个：
               计划 T5-2W 第三步要求"同一本书已有窗口则前置"；按钮那侧以前会重复绑监听
               （reader.js 2026-10-11 已修），双击/重发消息也该扛住，别叠出一摞窗口。 */
            if (_chapterEditorWindow is { IsLoaded: true } existing
                && existing.IsVisible
                && _chapterEditorIndex == target)
            {
                if (existing.WindowState == WindowState.Minimized)
                {
                    existing.WindowState = WindowState.Normal;
                }

                existing.Activate();
                return;
            }

            var workspace = await OpenEditorWorkspaceAsync(
                book.Id, format.Id, format.FilePath, CancellationToken.None).ConfigureAwait(true);

            var editor = new ChapterVisualEditorWindow(
                _source, _autofix, _environments, _assets, workspace, chapters, target, _snapshots)
            {
                Owner = this,
            };

            /* T5-2W 第三步：可视编辑保存后，阅读器**重新加载当前章**并**保持阅读位置**
               （计划原文：章节缓存失效 + 回到原章与原进度）。落到书里仍走主窗口【保存】那条链路。 */
            editor.ChapterSaved += (_, _) => _ = ReloadChapterAfterEditAsync(workspace, target);

            _chapterEditorWindow = editor;
            _chapterEditorIndex = target;

            editor.Closed += (_, _) =>
            {
                if (ReferenceEquals(_chapterEditorWindow, editor))
                {
                    _chapterEditorWindow = null;
                    _chapterEditorIndex = -1;
                }
            };

            editor.Show();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "打开可视编辑失败");
        }
    }

    /// <summary>
    /// 阅读器里的「编辑本章」对 TXT 的处理（用户 2026-10-20 要求）：打开**同一个全文编辑窗口**
    /// （整本 txt、像记事本一样直接改），但光标落在**当前章标题行**。
    /// 字符下标直接用清单里的 <see cref="ReaderChapter.TextStart"/>（解码后正文的字符偏移），
    /// 与阅读器切章用的是同一套坐标，不会偏。
    /// </summary>
    /// <remarks>
    /// 【路径必须用 <see cref="_session"/> 的 SourcePath，不能用清单里的】
    /// 清单是**阅读缓存**，里面的 SourcePath 是建缓存那一刻的绝对路径；
    /// 书被重新导入 / 数据目录迁移过之后，书库记录里的路径已经变了，缓存里的那份就失效了。
    /// 用户 2026-10-20 实测：点「编辑本章」弹"载入失败：Could not find file …fa3b87a4…"，
    /// 而文件实际在另一个 hash 目录下 —— 就是这个原因。
    /// </remarks>
    private void OpenTxtEditorForChapter(int index)
    {
        var manifest = _session!.Manifest;
        var spine = manifest.Spine;

        if (spine.Count == 0)
        {
            return;
        }

        var sourcePath = ResolveTxtSourcePath();

        if (sourcePath is null)
        {
            _log.LogWarning("编辑本章不可用：TXT 源文件找不到（缓存路径 {Cached}）", manifest.SourcePath);
            AppDialog.Show(
                this,
                "找不到这本书的 TXT 源文件（可能被移动、改名或重新导入了）。"
                + "请先「接受改动」或重新导入，再点「编辑本章」。",
                "编辑本章",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var target = Math.Clamp(index, 0, spine.Count - 1);
        var startChar = (int)Math.Clamp(spine[target].TextStart, 0, int.MaxValue);

        /* 同一章的窗口已经开着就前置，别再叠一个（与 EPUB 那条入口同一口径）。 */
        if (_txtEditorWindow is { IsLoaded: true, IsVisible: true } existing && _txtEditorIndex == target)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var editor = new TxtEditorWindow(
            _bookId,
            manifest.Title,
            sourcePath,
            manifest.TextEncoding,
            startChar,
            _config,
            _libraryScan)
        {
            Owner = this,
        };

        editor.Saved += (_, _) => _ = ReloadTxtAfterEditAsync(target);

        _txtEditorWindow = editor;
        _txtEditorIndex = target;

        editor.Closed += (_, _) =>
        {
            if (ReferenceEquals(_txtEditorWindow, editor))
            {
                _txtEditorWindow = null;
                _txtEditorIndex = -1;
            }
        };

        editor.Show();
    }

    /// <summary>
    /// 找 TXT 源文件：**优先本次从书库取到的最新路径**（阅读正文用的就是它），
    /// 缓存清单里的路径只当兜底；都指不到文件返回 null。
    /// </summary>
    private string? ResolveTxtSourcePath()
    {
        foreach (var path in new[] { _session?.SourcePath, _session?.Manifest.SourcePath })
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// 把阅读虚拟主机（http://ebook.local/）重新指向当前会话的缓存根。
    /// 为什么需要：TXT 编辑器保存后书的 quick_fingerprint 变了（内部修改同步基线），
    /// 而阅读缓存根目录名取自指纹 → 重建后的目录换了名；WebView 初始化时映射的是老目录，
    /// 不重指的话导航 reader.html 直接 404（日志 "navigation failed"），旧页面留在屏幕上，
    /// 再翻下一章时章节文件物化在新目录、页面却按老目录取 → Chromium 报
    /// 「它可能已被移动、编辑或删除」（用户 2026-10-21 截图）。EPUB 编辑不重建缓存根，所以没这问题。
    /// </summary>
    private void RemapContentHost(string reason)
    {
        if (_webHost is null || _session is null)
        {
            return;
        }

        _webHost.RemapContentHost(_session.CacheRoot);

        _log.LogInformation(
            "阅读虚拟主机已重指到新缓存根：{Root}（{Reason}）",
            _session.CacheRoot, reason);
    }

    /// <summary>
    /// TXT 保存后：删掉阅读缓存、按当前编码重建，回到原章并让页面重新载入。
    /// 为什么整份重建而不是"只重读当前章"：用户可能改了章节标题、甚至删/加了一章，
    /// 清单（章节数 / 标题 / 字符偏移）必须跟着变，否则目录与翻章会错位。
    /// </summary>
    private async Task ReloadTxtAfterEditAsync(int keepChapter)
    {
        if (_reader is null || _core is null || _channel is null || _session is null)
        {
            return;
        }

        try
        {
            var keep = Math.Max(0, keepChapter);

            await _reader.InvalidateAsync(_bookId, CancellationToken.None).ConfigureAwait(true);

            _session = await _reader
                .OpenWithTextEncodingAsync(_bookId, _session.Manifest.TextEncoding)
                .ConfigureAwait(true);

            LastTextEncoding = _session.Manifest.TextEncoding;
            _savedProgress = null;
            _crossFormatPercent = null;
            _crossFormatTextChapter = null;
            _restartChapter = Math.Clamp(keep, 0, Math.Max(0, _session.Manifest.Spine.Count - 1));

            // 缓存根可能随指纹改名了：先把 ebook.local 重指过去，再物化/导航（否则 404）。
            RemapContentHost("TXT 编辑保存后重建");

            await _reader
                .MaterializeChapterAsync(_session, ClampStartIndex(), CancellationToken.None)
                .ConfigureAwait(true);

            _channel.ResetSticky();

            await NavigateAsync(_core, ReaderWebHost.ToVirtualUrl("reader.html")
                + "?theme=" + Uri.EscapeDataString(ResolveInitialThemeId()), CancellationToken.None)
                .ConfigureAwait(true);

            await _channel.WaitForAsync("reader.ready", ReadyTimeout, CancellationToken.None).ConfigureAwait(true);

            await LoadAnnotationsAsync(CancellationToken.None).ConfigureAwait(true);

            SendLoad();

            _log.LogInformation(
                "TXT 编辑保存后已重建缓存：从第 {Chapter} 章继续（编码 {Encoding}）",
                ClampStartIndex() + 1,
                _session.Manifest.TextEncoding);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "TXT 编辑保存后重建阅读缓存失败");
        }
    }

    /// <summary>
    /// 打开「可视编辑」用的工作区：**优先复用**同一本书（同格式、同源文件）还没过期的残留会话，
    /// 没有才新建。
    /// </summary>
    /// <remarks>
    /// 以前每次点「编辑本章」都 <c>OpenAsync</c> 新建，而新工作区是从**原书**重新解压的  于是
    /// "保存退出后再次进编辑本章"看到的是编辑前的内容，和阅读器里显示的（已重载的新内容）不一致
    /// （用户 2026-10-11 反馈）。主窗口那条链路靠"同一本书只开一个编辑窗口"天然复用了工作区，
    /// 阅读器这条必须自己按会话找回来。
    /// </remarks>
    private async Task<IEpubWorkspace> OpenEditorWorkspaceAsync(
        Guid bookId, Guid formatId, string epubPath, CancellationToken ct)
    {
        var factory = _workspaces!;
        DateTime sourceWrittenUtc;

        try
        {
            sourceWrittenUtc = File.GetLastWriteTimeUtc(epubPath);
        }
        catch (Exception)
        {
            sourceWrittenUtc = DateTime.MinValue;
        }

        var candidate = factory
            .ListResumable()
            /* ListResumable 已按 OpenedAt 倒序：取最新的那一份 */
            .Where(session => session.BookId == bookId
                && session.FormatId == formatId
                && string.Equals(session.SourcePath, epubPath, StringComparison.OrdinalIgnoreCase))
            /* 原书在工作区建好之后又被改过 -> 这份残留会话可能已经过期，宁可不复用 */
            .FirstOrDefault(session => session.OpenedAt.UtcDateTime >= sourceWrittenUtc);

        if (candidate is not null)
        {
            var resumed = await factory.ResumeAsync(candidate.SessionId, ct).ConfigureAwait(true);

            if (resumed is not null)
            {
                _log.LogInformation(
                    "复用已有工作区 {SessionId}（未落盘改动 {Count} 个）",
                    resumed.SessionId,
                    resumed.Session.ChangedFiles.Count);

                return resumed;
            }
        }

        return await factory
            .OpenAsync(
                new WorkspaceOpenRequest
                {
                    EpubPath = epubPath,
                    BookId = bookId,
                    FormatId = formatId,
                },
                ct)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// T5-2W 第三步：在阅读器里点了【编辑本章】-> 可视编辑保存后，
    /// 把工作区里这一章重新物化进阅读缓存，并让前端"重新加载当前章 + 回到原位置"。
    /// </summary>
    /// <remarks>
    /// 只动阅读缓存，绝不碰原书：落到书里仍然走主窗口【保存】那条链路（打包 + EpubCheck 门禁 + 还原点）。
    /// 位置用"章内比例"（<see cref="LastPercent"/>，reader.locatorChanged 一直在维护）：
    /// 分页模式换算成页号、滚动模式换算成 scrollTop，所以正文改长了也不会跑偏一屏。
    /// </remarks>
    internal Task ReloadChapterAfterEditAsync(IEpubWorkspace workspace, int index)
        => ReloadChapterCoreAsync(workspace, index, "可视编辑保存");

    /// <summary>把工作区里某一章重新物化进阅读缓存；正显示这一章就重载并保持阅读位置。</summary>
    private async Task ReloadChapterCoreAsync(IEpubWorkspace workspace, int index, string reason)
    {
        if (_session is null || _channel is null || _reader is null || _source is null)
        {
            return;
        }

        if (index < 0 || index >= _session.Manifest.Spine.Count)
        {
            return;
        }

        var entry = _session.Manifest.Spine[index].SourceHref;

        try
        {
            var xhtml = _source.ReadSource(workspace, entry);

            var copied = await _reader
                .SyncChapterAssetsAsync(_session, workspace.WorkDir, entry)
                .ConfigureAwait(true);

            await _reader
                .RematerializeChapterAsync(_session, index, xhtml)
                .ConfigureAwait(true);

            /* 用户已经翻到别的章去了就别硬拽回来：缓存更新好，等他翻回来自然看到新内容。 */
            if (LastChapterIndex != index)
            {
                _log.LogInformation(
                    "{Reason}：第 {Index} 章缓存已更新（素材 {Copied} 个），当前显示的是第 {Current} 章，不动阅读位置",
                    reason,
                    index + 1,
                    copied,
                    LastChapterIndex + 1);
                return;
            }

            var percent = LastPercent;

            _channel.Post("reader.reloadChapter", new
            {
                index,
                percentInChapter = percent,
                token = DateTime.UtcNow.Ticks.ToString("x", CultureInfo.InvariantCulture),
            });

            _log.LogInformation(
                "{Reason}：已重新加载第 {Index} 章（素材 {Copied} 个，回到章内 {Percent:P0}）",
                reason,
                index + 1,
                copied,
                percent);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Reason}后重新加载章节失败", reason);
        }
    }
    /// <summary>
    /// 阅读器点了【撤销上一次替换】：把工作区按"上一次全部替换前的快照"还原回去（T5-4 判据 8）。
    /// 只动工作区；落书仍走主窗口【保存】。还原前会自动再建一份反向快照，反悔了还能退回来。
    /// </summary>
    internal async Task UndoLastReplaceAsync(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (_snapshots is null || _session is null)
        {
            PostFindReplaceNotice("撤销替换不可用（宿主没有接上快照服务）。");
            return;
        }

        try
        {
            var workspace = await OpenFindReplaceWorkspaceAsync().ConfigureAwait(true);
            var target = await _snapshots
                .FindLatestAsync(workspace, WorkspaceSnapshotKinds.ReplaceBefore)
                .ConfigureAwait(true);

            if (target is null)
            {
                PostFindReplaceNotice("没有可撤销的替换（历史里没有「全部替换前」的快照）。");
                return;
            }

            var result = await _snapshots.RestoreAsync(workspace, target.Id).ConfigureAwait(true);
            PostFindReplaceNotice(result.Ok
                ? "已撤销上一次替换：" + result.Message
                : "撤销替换没成功：" + result.Message);

            if (!result.Ok)
            {
                return;
            }

            /* 快照里的条目就是当时涉及的章节；把它们重新物化，页面上立刻能看到还原后的正文。 */
            var indices = MapEntriesToSpineIndices(target.Entries.Select(entry => entry.RelativePath));

            if (indices.Count > 0)
            {
                await RefreshChaptersAfterWorkspaceWriteAsync(workspace, indices, "撤销替换").ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "撤销上一次替换失败");
            PostFindReplaceNotice("撤销替换失败，详情见日志。");
        }
    }

    /// <summary>把"包内相对路径"映射成 spine 下标（找不到的忽略）。</summary>
    private IReadOnlyList<int> MapEntriesToSpineIndices(IEnumerable<string> entries)
    {
        if (_session is null)
        {
            return Array.Empty<int>();
        }

        var wanted = new HashSet<string>(entries, StringComparer.OrdinalIgnoreCase);
        var indices = new List<int>();

        for (var index = 0; index < _session.Manifest.Spine.Count; index++)
        {
            if (wanted.Contains(_session.Manifest.Spine[index].SourceHref))
            {
                indices.Add(index);
            }
        }

        return indices;
    }

    // ---------- T5-3 查找替换：入口在阅读器工具栏；只写工作区，落书仍走主窗口【保存】 ----------

    /// <summary>
    /// 阅读器点了【全部替换】：先在**工作区**上算出完整计划（不写盘），弹"替换预览"让用户确认；
    /// 确认后逐章校验全通过才写工作区 + 记 changedFiles，最后把被改的当前章重新物化并保持阅读位置。
    /// 取消 / 非法正则 / 超时 / 命中超上限 / 校验不过：一个字节都不写。
    /// </summary>
    internal async Task ShowReplacePreviewAsync(JsonObject payload)
    {
        var prepared = await BeginFindReplaceAsync(payload).ConfigureAwait(true);

        if (prepared is null)
        {
            return;
        }

        var (workspace, chapters) = prepared.Value;
        var plan = _findReplace!.Preview(workspace, chapters, ReadFindReplaceOptions(payload)!);

        if (!plan.Ok)
        {
            PostFindReplaceNotice(plan.Error ?? "查找替换中止（没有写盘）。");
            return;
        }

        if (plan.TotalMatches == 0)
        {
            PostFindReplaceNotice("没有命中，什么都没改。");
            return;
        }

        var dialog = new ReplacePreviewDialog(plan.TotalMatches, plan.Preview) { Owner = this };
        var confirmed = dialog.ShowDialog() == true && dialog.Confirmed;

        if (!confirmed)
        {
            PostFindReplaceNotice("已取消，一个字节都没改。");
            return;
        }

        var outcome = _findReplace.Apply(workspace, plan);
        PostFindReplaceNotice(outcome.Message);

        if (outcome.Ok)
        {
            await RefreshChaptersAfterWorkspaceWriteAsync(
                workspace,
                plan.Changes.Select(change => change.ChapterIndex).ToList(),
                "查找替换").ConfigureAwait(true);
        }
    }

    /// <summary>阅读器点了【替换】：只替换本章第 N 处（N 由前端当前高亮的第几处给出）。</summary>
    internal async Task ReplaceOneAsync(JsonObject payload)
    {
        var prepared = await BeginFindReplaceAsync(payload).ConfigureAwait(true);

        if (prepared is null)
        {
            return;
        }

        var (workspace, chapters) = prepared.Value;
        var ordinal = ReadInt(payload, "ordinal", -1);

        if (ordinal < 0)
        {
            PostFindReplaceNotice("请先用【下一个】找到要替换的那一处。");
            return;
        }

        var options = ReadFindReplaceOptions(payload)! with { OnlyMatchOrdinal = ordinal };
        var plan = _findReplace!.Preview(workspace, chapters, options);

        if (!plan.Ok)
        {
            PostFindReplaceNotice(plan.Error ?? "替换中止（没有写盘）。");
            return;
        }

        if (plan.TotalMatches == 0)
        {
            PostFindReplaceNotice("没有命中，什么都没改。");
            return;
        }

        var outcome = _findReplace.Apply(workspace, plan);
        PostFindReplaceNotice(outcome.Message);

        if (outcome.Ok)
        {
            await RefreshChaptersAfterWorkspaceWriteAsync(
                workspace,
                plan.Changes.Select(change => change.ChapterIndex).ToList(),
                "替换").ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 跨章查找：当前章没有（或已经走完）命中时，从这一章往前/后绕一圈找第一个有命中的章，
    /// 让宿主物化它并跳过去，再让前端在新章里高亮第一个命中（计划 T5-3 "跨章时自动物化并跳到下一章第一个命中"）。
    /// </summary>
    internal async Task LocateChapterWithMatchAsync(JsonObject payload)
    {
        var prepared = await BeginFindReplaceAsync(payload).ConfigureAwait(true);

        if (prepared is null || _session is null || _channel is null)
        {
            return;
        }

        var (workspace, _) = prepared.Value;
        var options = ReadFindReplaceOptions(payload)!;
        var forward = ReadBool(payload, "forward");
        var spine = _session.Manifest.Spine;
        var from = Math.Clamp(ReadInt(payload, "fromIndex", LastChapterIndex), 0, Math.Max(spine.Count - 1, 0));

        for (var step = 1; step <= spine.Count; step++)
        {
            var raw = forward ? from + step : from - step;
            var index = ((raw % spine.Count) + spine.Count) % spine.Count;
            var chapter = spine[index];
            var one = new[] { new FindReplaceChapter(chapter.Index, chapter.SourceHref, chapter.Title ?? chapter.SourceHref) };
            var plan = _findReplace!.Preview(workspace, one, options);

            if (!plan.Ok)
            {
                PostFindReplaceNotice(plan.Error ?? "查找中止（没有写盘）。");
                return;
            }

            if (plan.TotalMatches == 0)
            {
                continue;
            }

            /* 页面自己改 iframe.src 会 404：必须由宿主先物化这一章再导航。 */
            await GoToChapterAsync(index).ConfigureAwait(true);
            _channel.Post("reader.findReplace.highlight", new
            {
                find = options.Find,
                useRegex = options.UseRegex,
                matchCase = options.MatchCase,
            });
            return;
        }

        PostFindReplaceNotice("全书都没有命中。");
    }

    /// <summary>查找替换的公共前置：依赖检查 + 选项 + 章节范围 + 工作区（都在工作区副本上做，不碰原书）。</summary>
    private async Task<(IEpubWorkspace Workspace, IReadOnlyList<FindReplaceChapter> Chapters)?> BeginFindReplaceAsync(
        JsonObject payload)
    {
        if (_findReplace is null || _workspaces is null || _source is null || _repository is null
            || _session is null || _channel is null)
        {
            _log.LogWarning("查找替换不可用：服务未注入");
            PostFindReplaceNotice("查找替换不可用（宿主没有接上服务）。");
            return null;
        }

        if (ReadFindReplaceOptions(payload) is null)
        {
            PostFindReplaceNotice("请输入查找内容。");
            return null;
        }

        var chapters = SelectFindReplaceChapters(payload);

        if (chapters is null || chapters.Count == 0)
        {
            PostFindReplaceNotice("这本书里没有可查找的章节。");
            return null;
        }

        try
        {
            var workspace = await OpenFindReplaceWorkspaceAsync().ConfigureAwait(true);
            return (workspace, chapters);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "打开查找替换工作区失败");
            PostFindReplaceNotice("打开工作区失败，详情见日志。");
            return null;
        }
    }

    /// <summary>
    /// 全书计数（用户 2026-10-12 口径）：**查找永远是"所有章节"**，只是高亮只画在当前章。
    /// 复用 Preview 的只读扫描（不写盘），把总数与每章命中数回推给页面，用于显示"本章 x / 全书 y"。
    /// </summary>
    private async Task CountFindMatchesAsync(JsonObject payload)
    {
        if (_findReplace is null || _session is null || _channel is null)
        {
            return;
        }

        var options = ReadFindReplaceOptions(payload);

        if (options is null)
        {
            return;
        }

        try
        {
            var workspace = await OpenFindReplaceWorkspaceAsync().ConfigureAwait(true);
            var chapters = _session.Manifest.Spine
                .Select(chapter => new FindReplaceChapter(
                    chapter.Index, chapter.SourceHref, chapter.Title ?? chapter.SourceHref))
                .ToList();
            var plan = _findReplace.Preview(workspace, chapters, options);

            _channel.Post("reader.findReplace.counted", new
            {
                ok = plan.Ok,
                total = plan.TotalMatches,
                error = plan.Error,
                chapters = plan.Changes
                    .Select(change => new { index = change.ChapterIndex, count = change.MatchCount })
                    .ToList(),
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "全书查找计数失败");
        }
    }

    private static FindReplaceOptions? ReadFindReplaceOptions(JsonObject payload)
    {
        var find = ReadString(payload, "find");

        if (string.IsNullOrEmpty(find))
        {
            return null;
        }

        return new FindReplaceOptions(
            find,
            ReadString(payload, "replace") ?? string.Empty,
            ReadBool(payload, "useRegex"),
            ReadBool(payload, "matchCase"));
    }

    private IReadOnlyList<FindReplaceChapter>? SelectFindReplaceChapters(JsonObject payload)
    {
        if (_session is null || _session.Manifest.Spine.Count == 0)
        {
            return null;
        }

        var spine = _session.Manifest.Spine;
        var scope = ReadString(payload, "scope");

        if (!string.Equals(scope, "book", StringComparison.OrdinalIgnoreCase))
        {
            var current = Math.Clamp(ReadInt(payload, "chapterIndex", LastChapterIndex), 0, spine.Count - 1);
            var chapter = spine[current];
            return new[] { new FindReplaceChapter(chapter.Index, chapter.SourceHref, chapter.Title ?? chapter.SourceHref) };
        }

        return spine
            .Select(chapter => new FindReplaceChapter(chapter.Index, chapter.SourceHref, chapter.Title ?? chapter.SourceHref))
            .ToList();
    }

    private async Task<IEpubWorkspace> OpenFindReplaceWorkspaceAsync()
    {
        var book = await _repository!.GetByIdAsync(_bookId).ConfigureAwait(true);
        var format = book?.Formats.FirstOrDefault(candidate => !candidate.Missing && File.Exists(candidate.FilePath));

        if (book is null || format is null)
        {
            throw new InvalidOperationException("书库记录或原书文件找不到");
        }

        return await OpenEditorWorkspaceAsync(book.Id, format.Id, format.FilePath, CancellationToken.None)
            .ConfigureAwait(true);
    }

    /// <summary>一次工作区写入涉及多章：逐章刷新缓存；正显示的那一章会重载并保持阅读位置。</summary>
    private async Task RefreshChaptersAfterWorkspaceWriteAsync(
        IEpubWorkspace workspace, IReadOnlyList<int> indices, string reason)
    {
        foreach (var index in indices.Distinct().OrderBy(value => value))
        {
            await ReloadChapterCoreAsync(workspace, index, reason).ConfigureAwait(true);
        }
    }
    private void PostFindReplaceNotice(string message)
    {
        try
        {
            _channel?.Post("reader.notice", new { message });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "发送查找替换提示失败");
        }
    }
    private Task<ReaderSession> OpenSessionAsync(CancellationToken ct)

    {

        var saved = _config?.GetString(TextEncodingKey(_bookId), null);



        return string.IsNullOrWhiteSpace(saved)

            ? _reader.OpenAsync(_bookId, ct)

            : _reader.OpenWithTextEncodingAsync(_bookId, saved, ct);

    }



    /// <summary>TXT 手选编码（T4-5 判据 2）：记住选择 -&gt; 按新编码重建缓存 -&gt; 重新加载并回到原章节。</summary>

    private async Task ChangeTextEncodingAsync(string? encoding)

    {

        if (_channel is null || _core is null || _session is null || string.IsNullOrWhiteSpace(encoding))

        {

            return;

        }



        if (string.Equals(_session.Manifest.TextEncoding, encoding, StringComparison.OrdinalIgnoreCase))

        {

            return;

        }



        var keepChapter = Math.Max(0, LastChapterIndex);



        try

        {

            if (_config is not null)

            {

                await _config.SetAsync(TextEncodingKey(_bookId), JsonValue.Create(encoding)).ConfigureAwait(true);

                await _config.SaveAsync().ConfigureAwait(true);

            }



            _session = await _reader.OpenWithTextEncodingAsync(_bookId, encoding).ConfigureAwait(true);

            LastTextEncoding = _session.Manifest.TextEncoding;

            // 换编码一般不会换目录（指纹没变），但重建时目录可能被重写过：保持映射与会话一致。
            RemapContentHost("TXT 换编码后重建");



            // 章节按新编码重切了：从原章号重新开始（章内位置会让给"换编码"这件事）

            _savedProgress = null;

            _crossFormatPercent = null;

            _crossFormatTextChapter = null;

            _restartChapter = keepChapter;



            await _reader.MaterializeChapterAsync(_session, ClampStartIndex(), CancellationToken.None)

                .ConfigureAwait(true);



            /* 清单变了（章节数/标题都可能变），必须让页面重新拿一次 load。

               页面重载后 reader.ready 会再发一次，所以先把握手缓存清掉，

               否则 WaitForAsync 会立刻拿到上次那条 ready。 */

            _channel.ResetSticky();

            await NavigateAsync(_core, ReaderWebHost.ToVirtualUrl("reader.html")
                + "?theme=" + Uri.EscapeDataString(ResolveInitialThemeId()), CancellationToken.None)

                .ConfigureAwait(true);

            await _channel.WaitForAsync("reader.ready", ReadyTimeout, CancellationToken.None).ConfigureAwait(true);

            await LoadAnnotationsAsync(CancellationToken.None).ConfigureAwait(true);

            SendLoad();



            _log.LogInformation(

                "TXT 编码已切换为 {Encoding}：{Title} 共 {Count} 章",

                encoding, _session.Manifest.Title, _session.Manifest.Spine.Count);

            /* 用户 2026-10-20：编码选择器要与 TXT 全文编辑器共用 —— 广播出去，开着的编辑器跟着换。
               这个键按书存（只影响这一本），下次打开这本书仍用这里选过的编码。 */

            TxtBookEncoding.Publish(_bookId, encoding);

        }

        catch (AppException ex)

        {

            _log.LogWarning(ex, "切换文本编码失败 {Encoding}", encoding);

            AppDialog.Show(

                this, ex.UserMessage, "切换编码失败", MessageBoxButton.OK, MessageBoxImage.Warning);

        }

        catch (Exception ex)

        {

            _log.LogError(ex, "切换文本编码时发生意外错误 {Encoding}", encoding);

            AppDialog.Show(

                this, "切换编码时发生意外错误，详情见日志。", "切换编码失败",

                MessageBoxButton.OK, MessageBoxImage.Warning);

        }

    }



    /// <summary>只给冒烟用：读页面上的编码选择器状态。</summary>

    internal sealed record ReaderTextEncodingState(

        bool Visible, string Value, IReadOnlyList<string> Options, string Hint, string Preview);



    internal async Task<ReaderTextEncodingState?> ReadTextEncodingAsync()

    {

        var json = await RunReaderScriptAsync(

            "(function(){var d=window.__readerDebug;if(!d||!d.textProbe){return null;}var p=d.textProbe();" +

            "return {visible:p.visible,value:p.value,options:p.options,hint:p.hint,preview:p.preview};})()")

            .ConfigureAwait(true);



        if (string.IsNullOrEmpty(json) || json == "null")

        {

            return null;

        }



        try

        {

            if (JsonNode.Parse(json) is not JsonObject o)

            {

                return null;

            }



            return new ReaderTextEncodingState(

                o["visible"]?.GetValue<bool>() ?? false,

                o["value"]?.GetValue<string>() ?? string.Empty,

                JsonStrings(o["options"]),

                o["hint"]?.GetValue<string>() ?? string.Empty,

                o["preview"]?.GetValue<string>() ?? string.Empty);

        }

        catch (Exception ex)

        {

            _log.LogDebug(ex, "读取编码选择器状态失败");

            return null;

        }

    }



    /// <summary>只给冒烟用：模拟用户在下拉里选编码（走真实 change 事件，会触发重建）。</summary>

    internal Task TextEncodingPickAsync(string name)

        => RunReaderScriptAsync("window.__readerDebug.textPick('" + name + "');true");



    /// <summary>前 500 字预览：编码识别没把握时给用户看，一眼就能判断是不是乱码。</summary>

    private string TextPreview()

    {

        if (_session?.Manifest.TextEncoding is not { Length: > 0 } name)

        {

            return string.Empty;

        }



        try

        {

            var encoding = TextEncodingSupport.Resolve(name);



            if (encoding is null)

            {

                return string.Empty;

            }



            var text = TxtChapterHtml.ReadRange(

                _session.SourcePath, encoding, _session.Manifest.TextBomLength, 0, 500);



            return text.Replace("\r\n", "\n").Replace("\n", " ").Trim();

        }

        catch (Exception ex)

        {

            _log.LogDebug(ex, "取 TXT 预览失败");

            return string.Empty;

        }

    }



    /// <summary>主题落盘。只改内存 + 落盘，失败只记日志（同分页模式）。</summary>

    private void PersistTheme(string themeId)

    {

        _themeId = string.IsNullOrWhiteSpace(themeId) ? "light" : themeId;

        LastTheme = _themeId;



        if (_config is null)

        {

            return;

        }



        _ = PersistThemeAsync(_themeId);

    }



    private async Task PersistThemeAsync(string themeId)

    {

        try

        {

            await _config!.SetAsync(ThemeKey, JsonValue.Create(themeId)).ConfigureAwait(true);

            await _config.SaveAsync().ConfigureAwait(true);

        }

        catch (Exception ex)

        {

            _log.LogWarning(ex, "保存阅读主题失败（不影响继续阅读）");

        }

    }



    /// <summary>
    /// 把配置里的预载页数收口到 1–1000：非法 / 空回退默认 10，越界夹到区间内。
    /// T7-4 起不再有"0 = 不预载"，也不再把数值归整到 10 的档位（设置页是自由输入框）。
    /// </summary>
    private static int NormalizePdfPreload(string? raw)
        => AppSettingsKeys.NormalizePreloadPages(raw);



    /// <summary>

    /// 分页模式落盘。只改内存 + 落盘，失败只记日志：

    /// 存不下偏好不是大事，绝不能因此打扰正在读书的人。

    /// </summary>

    private void PersistReaderMode(string mode)

    {

        _pagedMode = string.Equals(mode, "paged", StringComparison.Ordinal);



        if (_config is null)

        {

            return;

        }



        _ = PersistReaderModeAsync(_pagedMode);

    }



    private async Task PersistReaderModeAsync(bool paged)

    {

        try

        {

            await _config!.SetAsync(PaginationModeKey, JsonValue.Create(paged)).ConfigureAwait(true);

            await _config.SaveAsync().ConfigureAwait(true);

        }

        catch (Exception ex)

        {

            _log.LogWarning(ex, "保存阅读模式失败（不影响继续阅读）");

        }

    }



    /// <summary>页面里分页状态的快照（只给冒烟读）。</summary>

    internal sealed record ReaderPageState(

        string Mode,

        int PageIndex,

        int PageCount,

        int ChapterIndex,

        int PageWidth,

        int PageHeight,

        int MarginLeft,

        int MarginTop,

        int OffsetX,

        int StripWidth,

        int FrameColumns,

        int FrameWidth,

        int FrameHeight,

        int FrameLeft,

        int FrameTop,

        bool FrameExists,

        double PercentInChapter,

        double PaginateMs,

        bool HasPagedCss,

        string PagedCssText,

        int LinesOnPage,

        int DeepestLineBottom,

        int MaxLineWidth,

        string Diag);



    /// <summary>

    /// 只给冒烟用：读页面里的分页状态。

    /// PageWidth = 一页宽（列宽）；PageHeight = 一页高（列高）；

    /// OffsetX = 列带的横向位移（翻页就是把它平移一页宽，不是滚动）。

    /// </summary>

    internal async Task<ReaderPageState?> ReadPageStateAsync()

    {

        var json = await RunReaderScriptAsync(

            "(function(){var d=window.__readerDebug;if(!d){return null;}" +

            "var f=d.frameInfo();" +

            "return {mode:d.mode(),page:d.pageIndex(),count:d.pageCount(),chapter:d.chapterIndex()," +

            "stride:d.pageStride(),height:d.pageHeight(),marginLeft:d.marginLeft(),marginTop:d.marginTop()," +

            "offsetX:d.offsetXNow(),percent:d.percent(),paginateMs:d.paginateMs()," +

            "pagedCss:d.hasPagedCss(),pagedCssText:d.pagedCssText()," +

            "lines:d.linesOnPage(),deepest:d.deepestLineBottom(),maxLine:d.maxLineWidth()," +

            "frameExists:f.exists,frameWidth:f.width,frameHeight:f.height,frameLeft:f.left," +

            "frameTop:f.top,stripWidth:f.stripWidth,frameColumns:f.columns,diag:d.pageDiag()};})()")

            .ConfigureAwait(true);



        if (string.IsNullOrEmpty(json) || json == "null")

        {

            return null;

        }



        try

        {

            if (JsonNode.Parse(json) is not JsonObject o)

            {

                return null;

            }



            return new ReaderPageState(

                o["mode"]?.GetValue<string>() ?? "scroll",

                o["page"]?.GetValue<int>() ?? 0,

                o["count"]?.GetValue<int>() ?? 1,

                o["chapter"]?.GetValue<int>() ?? -1,

                o["stride"]?.GetValue<int>() ?? 0,

                o["height"]?.GetValue<int>() ?? 0,

                o["marginLeft"]?.GetValue<int>() ?? 0,

                o["marginTop"]?.GetValue<int>() ?? 0,

                o["offsetX"]?.GetValue<int>() ?? 0,

                o["stripWidth"]?.GetValue<int>() ?? 0,

                o["frameColumns"]?.GetValue<int>() ?? 0,

                o["frameWidth"]?.GetValue<int>() ?? 0,

                o["frameHeight"]?.GetValue<int>() ?? 0,

                o["frameLeft"]?.GetValue<int>() ?? 0,

                o["frameTop"]?.GetValue<int>() ?? 0,

                o["frameExists"]?.GetValue<bool>() ?? false,

                o["percent"]?.GetValue<double>() ?? 0,

                o["paginateMs"]?.GetValue<double>() ?? 0,

                o["pagedCss"]?.GetValue<bool>() ?? false,

                o["pagedCssText"]?.GetValue<string>() ?? string.Empty,

                o["lines"]?.GetValue<int>() ?? 0,

                o["deepest"]?.GetValue<int>() ?? 0,

                o["maxLine"]?.GetValue<int>() ?? 0,

                o["diag"]?.ToJsonString() ?? string.Empty);

        }

        catch (Exception ex)

        {

            _log.LogDebug(ex, "读取分页状态失败");

            return null;

        }

    }



    /// <summary>只给冒烟用：读外壳 + 章节 iframe 的主题配色（T4-2）。</summary>

    internal sealed record ReaderThemeState(

        string Id,

        string DataTheme,

        string SelectValue,

        string ShellBgHex,

        string ShellVarBg,

        string ChapterBgHex,

        string ChapterFgHex,

        string LinkHex,

        int ImageCount,

        string ImageFilter,

        string LastStyleId);



    internal async Task<ReaderThemeState?> ReadThemeAsync()

    {

        var json = await RunReaderScriptAsync(

            "(function(){var d=window.__readerDebug;if(!d||!d.themeProbe){return null;}" +

            "var p=d.themeProbe();" +

            "return {id:p.id,dataTheme:p.dataTheme,selectValue:p.selectValue,shellBg:p.shellBgHex," +

            "shellVar:p.shellVarBg,chapterBg:p.chapterBgHex,chapterFg:p.chapterFgHex,link:p.linkHex," +

            "images:p.imageCount,filter:p.imageFilter,lastStyle:p.lastStyleId};})()")

            .ConfigureAwait(true);



        if (string.IsNullOrEmpty(json) || json == "null")

        {

            return null;

        }



        try

        {

            if (JsonNode.Parse(json) is not JsonObject o)

            {

                return null;

            }



            return new ReaderThemeState(

                o["id"]?.GetValue<string>() ?? string.Empty,

                o["dataTheme"]?.GetValue<string>() ?? string.Empty,

                o["selectValue"]?.GetValue<string>() ?? string.Empty,

                o["shellBg"]?.GetValue<string>() ?? string.Empty,

                o["shellVar"]?.GetValue<string>() ?? string.Empty,

                o["chapterBg"]?.GetValue<string>() ?? string.Empty,

                o["chapterFg"]?.GetValue<string>() ?? string.Empty,

                o["link"]?.GetValue<string>() ?? string.Empty,

                o["images"]?.GetValue<int>() ?? 0,

                o["filter"]?.GetValue<string>() ?? string.Empty,

                o["lastStyle"]?.GetValue<string>() ?? string.Empty);

        }

        catch (Exception ex)

        {

            _log.LogDebug(ex, "读取主题状态失败");

            return null;

        }

    }



    /// <summary>只给冒烟用：按主题 id 切主题（走 reader.js 的 applyTheme 那条路）。</summary>

    internal Task SetThemeAsync(string themeId) =>

        RunReaderScriptAsync("window.__readerDebug.setTheme('" + themeId + "');true");



    /// <summary>只给冒烟用：模拟用户在下拉里选主题（会回报宿主  落盘，T4-2 判据 4）。</summary>

    /// <summary>只给冒烟用：改自定义主题的三色（走取色器 input 事件  会落盘）。</summary>

    internal sealed record ReaderTypographyState(

        double FontSize,

        double LineHeight,

        string FontFamily,

        double MarginTop,

        double MarginBottom,

        double MarginLeft,

        double MarginRight,

        double Columns,

        double ColumnGap,

        bool ColumnRule,

        double PageStride,

        double ColumnWidth,

        double PageWidth,

        double MarginPx,

        string TextAlign,

        IReadOnlyList<string> Computed,

        int PageIndex,

        int PageCount,

        double PercentInChapter,

        IReadOnlyList<string> FontOptions);



    internal Task CustomPickAsync(string bg, string fg, string link) =>

        RunReaderScriptAsync(

            "window.__readerDebug.customPick('" + bg + "','" + fg + "','" + link + "');true");



    internal Task ThemePickAsync(string themeId) =>

        RunReaderScriptAsync("window.__readerDebug.themePick('" + themeId + "');true");



    /// <summary>只给冒烟用：往章节里塞一份"书自带的白底黑字"，验主题压得住（T4-2 判据 2）。</summary>

    internal Task InjectBookWhiteBackgroundAsync() =>

        RunReaderScriptAsync(

            "(function(){var f=document.getElementById('chapter');" +

            "var d=f&&f.contentDocument;if(!d){return false;}" +

            "var s=d.getElementById('ets-book-white');" +

            "if(!s){s=d.createElement('style');s.id='ets-book-white';d.head.appendChild(s);}" +

            "s.textContent='html,body{background:#ffffff !important;color:#000000 !important;}';return true;})()");



    /// <summary>只给冒烟用：目录面板快照（T4-4）。</summary>

    internal sealed record ReaderTocState(

        bool Open,

        int Count,

        IReadOnlyList<string> Titles,

        IReadOnlyList<int> Levels,

        IReadOnlyList<int> Spines,

        IReadOnlyList<string> Fragments,

        IReadOnlyList<string> Indents,

        bool HasToc,

        bool Fallback,

        string Hint,

        int CurrentSpine,

        string CurrentTitle);



    /// <summary>只给冒烟用：某个锚点当前落在哪一页（判据 3）。</summary>

    internal sealed record ReaderAnchorState(bool Found, int PageIndex, bool OnCurrentPage, string Text, double Percent,

        int RectLeft = 0, int FrameLeft = 0, int FrameRight = 0, int Stride = 0);



    internal async Task<ReaderTocState?> ReadTocAsync()

    {

        var json = await RunReaderScriptAsync(

            "(function(){var d=window.__readerDebug;if(!d||!d.tocProbe){return null;}" +

            "var p=d.tocProbe();" +

            "return {open:p.open,count:p.count,titles:p.titles,levels:p.levels,spines:p.spines," +

            "fragments:p.fragments,indents:p.indents,hasToc:p.hasToc,fallback:p.fallback,hint:p.hint," +

            "currentSpine:p.currentSpine,currentTitle:p.currentTitle};})()")

            .ConfigureAwait(true);



        if (string.IsNullOrEmpty(json) || json == "null")

        {

            return null;

        }



        try

        {

            if (JsonNode.Parse(json) is not JsonObject o)

            {

                return null;

            }



            return new ReaderTocState(

                o["open"]?.GetValue<bool>() ?? false,

                o["count"]?.GetValue<int>() ?? 0,

                JsonStrings(o["titles"]),

                JsonInts(o["levels"]),

                JsonInts(o["spines"]),

                JsonStrings(o["fragments"]),

                JsonStrings(o["indents"]),

                o["hasToc"]?.GetValue<bool>() ?? false,

                o["fallback"]?.GetValue<bool>() ?? false,

                o["hint"]?.GetValue<string>() ?? string.Empty,

                o["currentSpine"]?.GetValue<int>() ?? -1,

                o["currentTitle"]?.GetValue<string>() ?? string.Empty);

        }

        catch (Exception ex)

        {

            _log.LogDebug(ex, "读取目录状态失败");

            return null;

        }

    }



    internal async Task<ReaderAnchorState?> ReadAnchorInfoAsync(string fragment)

    {

        var json = await RunReaderScriptAsync(

            "(function(){var d=window.__readerDebug;if(!d||!d.anchorInfo){return null;}" +

            "var p=d.anchorInfo('" + fragment + "');" +

            "return {found:p.found,page:p.pageIndex,onPage:p.onPage,text:p.text,percent:p.percent," +

            "rl:p.rectLeft,fl:p.frameLeft,fr:p.frameRight,st:p.stride};})()")

            .ConfigureAwait(true);



        if (string.IsNullOrEmpty(json) || json == "null")

        {

            return null;

        }



        try

        {

            if (JsonNode.Parse(json) is not JsonObject o)

            {

                return null;

            }



            return new ReaderAnchorState(

                o["found"]?.GetValue<bool>() ?? false,

                o["page"]?.GetValue<int>() ?? 0,

                o["onPage"]?.GetValue<bool>() ?? false,

                o["text"]?.GetValue<string>() ?? string.Empty,

                o["percent"]?.GetValue<double>() ?? 0,

                o["rl"]?.GetValue<int>() ?? 0,

                o["fl"]?.GetValue<int>() ?? 0,

                o["fr"]?.GetValue<int>() ?? 0,

                o["st"]?.GetValue<int>() ?? 0);

        }

        catch (Exception ex)

        {

            _log.LogDebug(ex, "读取锚点位置失败");

            return null;

        }

    }



    private static List<string> JsonStrings(JsonNode? node) =>

        node is JsonArray array

            ? array.Select(item => item?.GetValue<string>() ?? string.Empty).ToList()

            : new List<string>();



    private static List<int> JsonInts(JsonNode? node) =>

        node is JsonArray array

            ? array.Select(item => item?.GetValue<int>() ?? -1).ToList()

            : new List<int>();



    /// <summary>

    /// 只给冒烟用：在当前章节正文末尾插一个锚点（判据 3 的强验证用）。

    /// 真实电子书里的锚点常常是"包住整章的 div"，落在章首，跳不跳都看不出差别，所以这里自己造一个章末锚点。

    /// </summary>

    internal Task InjectChapterAnchorAsync(string id)

        => RunReaderScriptAsync(

            "(function(){var f=document.getElementById('chapter');var doc=f&&f.contentDocument;" +

            "if(!doc){return false;}var flow=doc.getElementById('ets-flow')||doc.body;if(!flow){return false;}" +

            "var old=doc.getElementById('" + id + "');if(old&&old.parentNode){old.parentNode.removeChild(old);}" +

            "var el=doc.createElement('p');el.id='" + id + "';el.textContent='锚点定位测试段落';" +

            "flow.appendChild(el);return true;})()");



    /// <summary>只给冒烟用：点工具栏的「目录」按钮（走真实 Click 处理器）。</summary>

    internal Task ClickTocButtonAsync()

        => RunReaderScriptAsync("document.getElementById('btnToc').click();true");



    /// <summary>只给冒烟用：开/关目录面板。</summary>

    internal Task TocToggleAsync(bool open)

        => RunReaderScriptAsync("window.__readerDebug && window.__readerDebug.tocToggle(" + (open ? "true" : "false") + ");true");



    /// <summary>只给冒烟用：点第 n 个目录项（派发真实 click，和用户点一样）。</summary>

    internal Task TocPickAsync(int index)

        => RunReaderScriptAsync("window.__readerDebug && window.__readerDebug.tocPick(" + index + ");true");



    /// <summary>只给冒烟用：跳到某章某锚点。</summary>

    internal Task TocJumpAsync(int spine, string fragment)

        => RunReaderScriptAsync("window.__readerDebug && window.__readerDebug.tocJump(" + spine + ",'" + fragment + "');true");



    /// <summary>只给冒烟用：切模式（走 reader.js 里那条路径）。</summary>

    internal Task SetReaderModeAsync(string mode)

        => RunReaderScriptAsync("window.__readerDebug && window.__readerDebug.setMode('" + mode + "')");



    /// <summary>只给冒烟用：点一下工具栏的「分页/滚动」按钮（走真实 Click 处理器）。</summary>

    internal Task ClickModeButtonAsync()

        => RunReaderScriptAsync("document.getElementById('btnMode').click()");



    /// <summary>只给冒烟用：按分页规则翻一次（+1 = 下一页，翻不动就翻下一章）。</summary>

    internal Task TurnPageAsync(int delta)

        => RunReaderScriptAsync("window.__readerDebug && window.__readerDebug.pageOrChapter(" + delta + ")");



    /// <summary>只给冒烟用：派发一个真实的键盘事件（验的是监听器接线，不是翻页函数本身）。</summary>

    /// <param name="where">"frame"（焦点在正文里，默认）或 "shell"（焦点在工具栏上）。</param>

    internal Task PressKeyAsync(string key, string where = "frame")

        => RunReaderScriptAsync(

            "window.__readerDebug && window.__readerDebug.pressKey('" + key + "', '" + where + "')");



    /// <summary>只给冒烟用：派发一个真实的滚轮事件。</summary>

    internal Task SpinWheelAsync(int deltaY)

        => RunReaderScriptAsync("window.__readerDebug && window.__readerDebug.spinWheel(" + deltaY + ")");



    private async Task<string?> RunReaderScriptAsync(string script)

    {

        var core = Web.CoreWebView2;



        if (core is null)

        {

            return null;

        }



        try

        {

            return await core.ExecuteScriptAsync(script).ConfigureAwait(true);

        }

        catch (Exception ex)

        {

            _log.LogDebug(ex, "执行阅读器脚本失败");

            return null;

        }

    }



    // ---------- 章节跳转（宿主侧的统一入口） ----------



    /// <summary>

    /// 跳到某一章。必须先物化再通知页面，否则 iframe 请求到不存在的文件会 404。

    /// </summary>

    internal async Task GoToChapterAsync(int index)

    {

        if (_session is null || _channel is null)

        {

            return;

        }



        if (index < 0 || index >= _session.Manifest.Spine.Count)

        {

            return;

        }



        await _reader.MaterializeChapterAsync(_session, index).ConfigureAwait(true);

        _channel.Post("reader.gotoChapter", new { index, scrollTop = 0 });

    }



    /// <summary>

    /// 只给冒烟用：在页面里点一下"下一章"，走的是 JS 那条路径。

    /// 这条路径曾经绕开宿主直接改 iframe.src，导致翻页后是错误页。

    /// </summary>

    internal async Task<bool> ClickNextButtonAsync()

    {

        var core = Web.CoreWebView2;

        if (core is null)

        {

            return false;

        }



        await core.ExecuteScriptAsync("document.getElementById('btnNext').click()")

            .ConfigureAwait(true);



        return true;

    }



    /// <summary>把 WebView2 里正文的纯文本长度读回来，冒烟用它证明"真的渲染出内容了"。</summary>

    internal async Task RefreshRenderedTextAsync()

    {

        var core = Web.CoreWebView2;

        if (core is null)

        {

            return;

        }



        const string script =

            "(function(){var f=document.getElementById('chapter');" +

            "if(!f||!f.contentDocument){return null;}" +

            "var d=f.contentDocument,b=d.body;" +

            "var imgs=d.querySelectorAll('img'),broken=0;" +

            "for(var i=0;i<imgs.length;i++){" +

            "if(imgs[i].complete&&imgs[i].naturalWidth===0){broken++;}}" +

            "return {text:b?(b.innerText||'').length:0," +

            "images:d.querySelectorAll('img,image').length," +

            "broken:broken," +

            "loc:(d.location&&d.location.pathname)?d.location.pathname:''," +

            "err:d.querySelector('#main-frame-error')?1:0};})()";



        var json = await core.ExecuteScriptAsync(script).ConfigureAwait(true);



        if (string.IsNullOrEmpty(json) || json == "null")

        {

            return;

        }



        try

        {

            if (JsonNode.Parse(json) is not JsonObject result)

            {

                return;

            }



            RenderedTextLength = result["text"]?.GetValue<int>() ?? 0;

            RenderedImageCount = result["images"]?.GetValue<int>() ?? 0;

            RenderedBrokenImageCount = result["broken"]?.GetValue<int>() ?? 0;

            RenderedLocation = result["loc"]?.GetValue<string>() ?? "";

            RenderedIsErrorPage = (result["err"]?.GetValue<int>() ?? 0) != 0;

        }

        catch (Exception ex)

        {

            _log.LogDebug(ex, "读取渲染结果失败");

        }

    }



    /// <summary>

    /// 只给冒烟用：在【同一个控件】里再导航一次 reader.html 并计时。

    /// 用来区分"每个控件都要新建渲染进程"与"渲染进程可以复用"。

    /// 注意这会重置阅读器状态，必须在其它检查都做完之后再调。

    /// </summary>

    internal async Task<long> MeasureNavigationAsync(string url, CancellationToken ct = default)

    {

        if (_core is null)

        {

            return -1;

        }



        var watch = System.Diagnostics.Stopwatch.StartNew();

        await NavigateAsync(_core, url, ct).ConfigureAwait(true);



        return watch.ElapsedMilliseconds;

    }



    // ---------- 来自阅读器的消息 ----------



    private async void OnReaderMessage(object? sender, ReaderInboundMessage message)

    {

        try

        {

            switch (message.Type)

            {

                case "reader.modeChanged":

                    LastReaderMode = ReadString(message.Payload, "mode") ?? "scroll";

                    PersistReaderMode(LastReaderMode);

                    break;



                case "reader.typographyChanged":

                    _ = PersistTypographyAsync(message.Payload);

                    break;



                case "reader.customThemeChanged":

                    PersistCustomTheme(

                        ReadString(message.Payload, "bg"),

                        ReadString(message.Payload, "fg"),

                        ReadString(message.Payload, "link"));

                    break;



                case "reader.themeChanged":

                    /* T4-2 判据 4：主题选择要持久化（重开还是上次的主题） */

                    PersistTheme(ReadString(message.Payload, "themeId") ?? "light");

                    break;



                case "reader.fullscreenChanged":

                    // T4-6：F11 全屏  窗口无边最大化，退出时恢复原来的样式与状态

                    ApplyFullscreen(ReadBool(message.Payload, "on"));

                    break;



                case "reader.textEncodingChanged":

                    // T4-5 判据 2：用户手选编码 -> 重建缓存 -> 立即重排

                    await ChangeTextEncodingAsync(ReadString(message.Payload, "encoding")).ConfigureAwait(true);

                    break;



                case "reader.editChapter":
                    _ = OpenEditorForChapterAsync(LastChapterIndex);
                    break;

                case "reader.chapterChanged":

                    LastChapterIndex = ReadInt(message.Payload, "index", -1);

                    LastChapterTitle = ReadString(message.Payload, "title");

                    /* T7-3B：换章了，采集游标全部重置（新章字数要等 reader.rendered 才知道），
                       并立刻打一次活动心跳，避免刚打开/翻章后长时间没有"有活动"的记录。 */
                    ResetReadingStatsCursor(LastChapterIndex);
                    ReportReadingActivity(0, 0);



                    // 切章是确定性节点：立刻落盘，不走 1.5 秒防抖（T1-6）

                    await SaveChapterStartAsync(LastChapterIndex).ConfigureAwait(true);

                    break;



                case "reader.locatorChanged":

                    /* T7-3B：按位置变化估算这次翻页 / 滚动读了多少（采集器为空直接跳过，
                       估算与服务调用都不抛，绝不影响阅读）。 */
                    TrackLocatorActivity(message.Payload);

                    LastPercent = ReadDouble(message.Payload, "percent");

                    _latestProgress = BuildSnapshot(message.Payload);



                    // 滚动期间只排队：1.5 秒内没有新上报才真的写库（T1-6 判据 3）

                    if (_latestProgress is not null)

                    {

                        _progress.ScheduleSave(_latestProgress);

                    }



                    break;



                /* T6-2B：PDF 的页码变化。自绘之后页码是**精确**的：翻页、跳页、目录跳转、
                   连续滚动（外壳算顶部可见页）都会报上来，进度因此才真的对得上书。 */
                case "reader.pdfPageChanged":

                    _pdfPage = ReadInt(message.Payload, "page", _pdfPage);

                    _pdfTotalPages = ReadInt(message.Payload, "totalPages", _pdfTotalPages);

                    // T7-3B：PDF / 漫画只记翻页数，没有字数
                    TrackPdfPageActivity(_pdfPage);

                    _latestProgress = BuildPdfSnapshot();

                    if (_latestProgress is not null)

                    {

                        _progress.ScheduleSave(_latestProgress);

                    }

                    break;

                // T6-2 判据 3：PDF 的外部打开**常驻可用**（EPUB 只在失败面板里出现）
                case "reader.openExternal":

                    OpenWithSystem();

                    break;


                /* 目录抽屉的「编辑目录」：打开书籍编辑器（章节树 + 书籍信息 + 封面）。 */
                case "reader.editToc":

                    await OpenBookEditorAsync().ConfigureAwait(true);

                    break;

                /* T6-5：书签与笔记。页面负责锚点与界面，宿主负责落库与回推最新列表——
                   这样"列表永远等于库里的事实"，不会出现界面上删了、库里还在的情况。 */
                case "reader.addBookmark":

                    await AddBookmarkAsync(message.Payload).ConfigureAwait(true);

                    break;

                case "reader.addNote":

                    await AddNoteAsync(message.Payload).ConfigureAwait(true);

                    break;

                case "reader.deleteAnnotation":

                    await DeleteAnnotationAsync(message.Payload).ConfigureAwait(true);

                    break;

                /* T6-5 补短：点已标记文字弹出的卡片里改内容（纯标记补写内容就变成批注）。 */
                case "reader.updateNote":

                    await UpdateNoteAsync(message.Payload).ConfigureAwait(true);

                    break;

                case "reader.exportAnnotations":

                    await ExportAnnotationsAsync().ConfigureAwait(true);

                    break;

                /* T6-2B：外壳要某一页的位图与文本层（只请求可见页 ±2）。渲染、内存 LRU、
                   送图都在 ServePdfPageAsync 里做——页位图只在内存，绝不落盘（判据⑤）。
                   T6-3：漫画走同一个消息、同一个页主机，只是渲染端从 pdfium 换成解包图片。 */
                case "reader.pdfPage":

                    if (_session?.SourceFormat == BookFormatKind.Cbz)
                    {
                        await ServeComicPageAsync(message.Payload).ConfigureAwait(true);
                    }
                    else
                    {
                        await ServePdfPageAsync(message.Payload).ConfigureAwait(true);
                    }

                    break;

                /* T6-2B：阅读器内搜索。文本索引在宿主这边（pdfium 的 GetText），外壳只负责画高亮；
                   探针 9 那套"注入真实 Ctrl+F 给内置查看器"的做法自绘后已经不需要了。 */
                case "reader.pdfFind":

                    await SearchPdfAsync(message.Payload).ConfigureAwait(true);

                    break;

                case "reader.rendered":

                    // 首屏完成，把正文长度取回来

                    await RefreshRenderedTextAsync().ConfigureAwait(true);

                    /* T7-3B：记下当前章的字数，后面 locatorChanged 按"字数 × 章内进度增量"
                       估算已读字数。PDF 没有 reader.rendered，字数就保持 0。 */
                    _statsChapterChars = RenderedTextLength;

                    break;



                case "reader.linkClicked":

                    await FollowLinkAsync(message.Payload).ConfigureAwait(true);

                    break;



                case "reader.error":

                    _log.LogWarning(

                        "阅读器脚本报错：{Message} 堆栈 {Stack}",

                        ReadString(message.Payload, "message"),

                        ReadString(message.Payload, "stack"));

                    break;



                case "reader.resourceError":

                    // 书里引用了不存在的资源。不影响阅读，记下来就够，

                    // 否则控制台一片红而宿主完全不知道（执行计划 T1-5 坑 2）。

                    _log.LogDebug(

                        "章节资源缺失：{Tag} {Url}",

                        ReadString(message.Payload, "tag"),

                        ReadString(message.Payload, "url"));

                    break;



                case "reader.findReplace.preview":
                    _ = ShowReplacePreviewAsync(message.Payload);
                    break;

                case "reader.findReplace.one":
                    _ = ReplaceOneAsync(message.Payload);
                    break;

                case "reader.findReplace.locate":
                    _ = LocateChapterWithMatchAsync(message.Payload);
                    break;

                case "reader.findReplace.count":
                    _ = CountFindMatchesAsync(message.Payload);
                    break;

                case "reader.findReplace.undo":
                    _ = UndoLastReplaceAsync(message.Payload);
                    break;
                case "reader.chapterRequest":

                    // 页面上点了上一章/下一章。必须由宿主来物化章节再导航：

                    // 章节文件是按需生成的，页面自己改 iframe.src 会 404。

                    await GoToChapterAsync(ReadInt(message.Payload, "index", -1))

                        .ConfigureAwait(true);

                    break;



                default:

                    break;

            }

        }

        catch (Exception ex)

        {

            // 这是 async void，异常漏出去会直接砸到 UI 线程

            _log.LogWarning(ex, "处理阅读器消息 {Type} 时出错", message.Type);

        }

    }



    /// <summary>

    /// 章节内的链接。链接已经被规范化成 http://ebook.local/content/... 的形式，

    /// 所以这里能反推出它指向包内哪个文件，再翻成"跳到第几章"。

    /// </summary>

    private async Task FollowLinkAsync(JsonObject payload)

    {

        if (_session is null || _channel is null)

        {

            return;

        }



        var resolved = ReadString(payload, "resolved") ?? ReadString(payload, "href");

        if (string.IsNullOrWhiteSpace(resolved))

        {

            return;

        }



        if (!resolved.StartsWith(ReaderEndpoints.ContentUrlPrefix, StringComparison.OrdinalIgnoreCase))

        {

            // 外链（http/https）阶段 1 不打开，只记日志

            _log.LogDebug("阅读器里的外链被忽略：{Url}", resolved);

            return;

        }



        var rest = resolved[ReaderEndpoints.ContentUrlPrefix.Length..];

        var hash = rest.IndexOf('#');

        var packagePath = Uri.UnescapeDataString(hash >= 0 ? rest[..hash] : rest);



        var index = _session.Manifest.Spine.FindIndex(c =>

            string.Equals(c.SourceHref, packagePath, StringComparison.OrdinalIgnoreCase));



        if (index < 0)

        {

            _log.LogDebug("链接指向的文件不在章节列表里：{Path}", packagePath);

            return;

        }



        // 阶段 1 不支持跳章内锚点（#fragment）：先落到章首，不假装能做到。

        await GoToChapterAsync(index).ConfigureAwait(true);

    }



    private void OnClosed(object? sender, EventArgs e)

    {

        /* 编码是静态广播：窗口关掉必须退订，否则下次换编码会打到已经关掉的窗口上。 */

        TxtBookEncoding.Changed -= OnTxtBookEncodingChanged;

        /* 关窗时把最后的位置排进保存队列（T1-6 判据：关闭阅读器时保存）。

           不在这里同步写库：Close 事件里阻塞 UI 线程会让人以为程序卡住；

           队列保证 1.5 秒内落盘，程序退出时还有 FlushAsync 兜底。 */

        if (_latestProgress is not null)

        {

            _progress.ScheduleSave(_latestProgress);

        }



        if (_edit is not null && _editSubscribed)

        {

            _edit.Saved -= OnBookEdited;

            _editSubscribed = false;

        }



        if (_channel is not null)

        {

            _channel.MessageReceived -= OnReaderMessage;

            _channel.Dispose();

            _channel = null;

        }



        /* T7-3B：关窗收尾阅读会话（服务内部吞异常并尽快落库）。
           故意不 await：Close 事件里阻塞会让人觉得程序卡住；进程退出还有 FlushAsync 兜底。 */
        _ = _stats?.EndAsync(_bookId);

        // T6-1 判据 6：关窗即释放缓存租约，上限清理才能回收这本书的缓存。

        _reader.Release(_bookId);

        /* T6-3：漫画归档句柄必须随窗口释放。RAR 固实压缩的逐页读取会让归档一直持有
           解压状态与文件句柄，不释放的话下次打开会重复解压、也删不掉原文件。 */

        _comic?.Dispose();

        _comic = null;



        _log.LogInformation(

            "阅读窗口已关闭：{Title}（停留第 {Index} 章）",

            _session?.Manifest.Title ?? "(未打开)",

            LastChapterIndex);

    }



    // ---------- 阅读进度（T1-6） ----------



    /// <summary>
    /// T6-2B：外壳要某一页的位图与文本层（只请求可见页 ±2）。
    ///
    /// 自绘之后没有"内核插件"了：翻页、缩放、旋转、搜索全是我们自己在 DOM 里做，
    /// 所以 T6-2 那套"注入真实 Ctrl+F / PageUp 给内置查看器"的代码（SendRealCtrlF、
    /// SendRealPageKeyAsync、FocusPdfPluginAsync 与 keybd_event 的 P/Invoke）已经整体删除。
    ///
    /// 【为什么先渲染再发消息，而不是在响应器里现渲染】core.Environment 与
    /// CreateWebResourceResponse **只能在 UI 线程访问**：探针里在后台线程建响应会抛
    /// InvalidOperationException，请求变成 net::ERR_ABORTED（见 build\t62b-probe-report.md）。
    /// 所以这里把渲染做完、把位图存进内存表，再发 ready；响应器里只剩"查表 + 建响应"两步。
    /// </summary>
    private async Task ServePdfPageAsync(JsonObject payload)
    {
        var requestId = ReadString(payload, "requestId") ?? string.Empty;
        var page = ReadInt(payload, "page", -1);
        var longEdge = Math.Clamp(ReadInt(payload, "longEdge", 1600), 200, 4000);
        var rotation = ReadInt(payload, "rotation", 0);
        var dark = string.Equals(ReadString(payload, "theme"), "dark", StringComparison.OrdinalIgnoreCase);

        if (_pdfPages is null || _session is null || page < 0)
        {
            PostPdfPageFailed(requestId, page, "PDF 渲染组件不可用。");
            return;
        }

        try
        {
            var options = new PdfPageRenderOptions(
                longEdge, rotation, dark ? PdfPageTheme.Dark : PdfPageTheme.Normal);

            PdfPageImage image;
            PdfPageText text;

            await PdfRenderGate.WaitAsync().ConfigureAwait(true);

            try
            {
                (image, text) = await Task.Run(() =>
                {
                    var rendered = _pdfPages.RenderPage(_session.SourcePath, page, options, _pdfPassword);
                    var layer = _pdfPages.ReadPageText(_session.SourcePath, page, _pdfPassword);

                    return (rendered, layer);
                }).ConfigureAwait(true);
            }
            finally
            {
                PdfRenderGate.Release();
            }

            var token = Guid.NewGuid().ToString("N");

            RememberPdfPageImage(token, image.Jpeg);

            _channel?.Post("reader.pdfPageReady", new
            {
                requestId,
                page,
                url = ReaderEndpoints.PdfPageUrl(token),
                width = image.Width,
                height = image.Height,
                isImagePage = image.IsImagePage,
                text = text.Text,

                /* 字符表与页文本同序，外壳靠它把文本层摆在图片上，
                   也是搜索高亮的落点（下标区间直接对应这几个字符）。 */
                chars = text.Chars.Select(c => new
                {
                    c = c.Value,
                    x = c.Left,
                    y = c.Top,
                    w = c.Width,
                    h = c.Height,
                }),
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "渲染 PDF 第 {Page} 页失败", page);
            PostPdfPageFailed(requestId, page, ex is AppException app ? app.Message : "这一页渲染失败。");
        }
    }

    /// <summary>
    /// T6-3：按外壳的请求读一页漫画，解码 → 缩放 → JPEG，再记进内存页表。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ServePdfPageAsync"/> 共用同一张内存页表（<c>_pdfPageImages</c>）与同一个
    /// <see cref="PdfRenderGate"/>：两者都是"重活 + 单线程渲染器"，串起来既安全也不会互抢内存。
    /// 漫画页不需要文本层，所以 text/chars 一律空——外壳据此不会画透明文字层。
    /// </remarks>
    private async Task ServeComicPageAsync(JsonObject payload)
    {
        var requestId = ReadString(payload, "requestId") ?? string.Empty;
        var page = ReadInt(payload, "page", -1);
        var longEdge = Math.Clamp(ReadInt(payload, "longEdge", 1600), 200, 4000);
        var rotation = ReadInt(payload, "rotation", 0);

        /* 字段可能在本方法等待期间被 OnClosed 清掉：先抓一份本地引用，
           否则后台渲染会 NRE（T6-3 首次验收实测：重开窗口时会抛）。 */
        var comic = _comic;

        if (comic is null || page < 0 || page >= comic.PageCount)
        {
            PostPdfPageFailed(requestId, page, "这本漫画打不开（文件可能损坏，或压缩包用了不支持的算法）。");
            return;
        }

        try
        {
            ComicPageImage image;

            await PdfRenderGate.WaitAsync().ConfigureAwait(true);

            try
            {
                image = await Task.Run(() => comic.RenderPage(page, longEdge, rotation))
                    .ConfigureAwait(true);
            }
            finally
            {
                PdfRenderGate.Release();
            }

            var token = Guid.NewGuid().ToString("N");

            RememberPdfPageImage(token, image.Jpeg);

            _channel?.Post("reader.pdfPageReady", new
            {
                requestId,
                page,
                url = ReaderEndpoints.PdfPageUrl(token),
                width = image.Width,
                height = image.Height,
                isImagePage = true,
                text = string.Empty,
                chars = Array.Empty<object>(),
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "渲染漫画第 {Page} 页失败", page);
            PostPdfPageFailed(requestId, page, ex is AppException app ? app.Message : "这一页读不出来。");
        }
    }

    /// <summary>
    /// T6-2B：阅读器内搜索。文本来自 pdfium 的 GetText（整本按页读进内存缓存），
    /// 命中位置 = 页号 + 页内字符下标，外壳据此在自己的文本层上画高亮。
    /// </summary>
    private async Task SearchPdfAsync(JsonObject payload)
    {
        var query = ReadString(payload, "query") ?? string.Empty;

        /* T6-3：漫画没有文本层，搜也搜不到东西。按钮在漫画模式下是隐藏的，
           这里再挡一道，免得将来别处误发消息时跑进 pdfium 打一本 CBZ。 */
        if (_session?.SourceFormat == BookFormatKind.Cbz)
        {
            PostPdfFindResults(query, Array.Empty<PdfSearchHit>());
            return;
        }

        if (_pdfPages is null || _session is null || string.IsNullOrEmpty(query))
        {
            PostPdfFindResults(query, Array.Empty<PdfSearchHit>());
            return;
        }

        try
        {
            /* 索引懒建：打开一本书不该付"把每页文本读一遍"的代价，
               等用户真的按了查找才建（建完就缓存在这个窗口里，同一本书不再重读）。 */
            _pdfSearch ??= new PdfSearchIndex(_pdfPages);

            var hits = await _pdfSearch
                .SearchAsync(_session.SourcePath, query, _pdfPassword)
                .ConfigureAwait(true);

            _log.LogInformation("PDF 搜索「{Query}」命中 {Count} 条", query, hits.Count);

            PostPdfFindResults(query, hits);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PDF 搜索失败：{Query}", query);
            PostPdfFindResults(query, Array.Empty<PdfSearchHit>());
        }
    }

    private void PostPdfFindResults(string query, IReadOnlyList<PdfSearchHit> hits)
    {
        _channel?.Post("reader.pdfFindResults", new
        {
            query,
            total = hits.Count,
            hits = hits.Select(h => new { page = h.PageIndex, start = h.Start, length = h.Length }),
        });
    }

    private void PostPdfPageFailed(string requestId, int page, string message)
    {
        _channel?.Post("reader.pdfPageFailed", new { requestId, page, message });
    }

    /// <summary>
    /// T6-2B：把"内存里的页位图"挂成一个虚拟主机。**不做 SetVirtualHostNameToFolderMapping**：
    /// 磁盘上没有这些页，每个请求都由 OnPdfPageRequested 用内存字节应答
    /// （探针实测这条通道通，见 build\t62b-probe-report.md）。
    /// </summary>
    private void EnsurePdfPageHost(CoreWebView2 core)
    {
        if (_pdfPageHostReady)
        {
            return;
        }

        _pdfPageHostReady = true;

        core.AddWebResourceRequestedFilter(
            ReaderEndpoints.PdfPageHostUrl + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnPdfPageRequested;

        _log.LogInformation(
            "PDF 页位图主机已挂上：{Filter}（内存 LRU 容量 {Capacity} 张）",
            ReaderEndpoints.PdfPageHostUrl + "*", PdfPageImageLimit);
    }

    /// <summary>
    /// 页位图请求的应答。这个事件在 **UI 线程**被调用，所以这里只做纯内存动作
    /// （渲染在发 reader.pdfPageReady 之前就已经做完了）。
    /// </summary>
    private void OnPdfPageRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var token = ReaderEndpoints.PdfPageTokenOf(e.Request.Uri);

        if (token is null)
        {
            /* 不是页位图的地址：交回 WebView2 的默认处理。
               **但要留一笔**：这说明有个我们没料到的请求打到了这个主机上
               （实测它就是"图片显示不出来"的第一现场，静默放过等于把线索扔掉）。 */
            _log.LogWarning("页位图主机收到非预期请求：{Uri}", e.Request.Uri);
            return;
        }

        var environment = _core?.Environment;

        if (environment is null)
        {
            return;
        }

        var jpeg = TakePdfPageImage(token);

        if (jpeg is null)
        {
            /* 位图被 LRU 挤掉了（或页面重载后要的是一张旧图）：明确回 404。
               外壳的 img.onerror 会重新请求这一页，自愈——不回 200 空图，
               否则浏览器会把它当成"加载成功但内容坏了"，用户看到的是破图而不是重试。

               这里必须记日志：这条路径以前是静默的，而"页位图取不到"正是
               "PDF 里的图片显示不出来"这个现象的现场之一，不留痕就没法定位。 */
            _log.LogWarning("页位图取不到（token 已过期或被 LRU 挤掉），回 404：{Uri}", e.Request.Uri);

            e.Response = environment.CreateWebResourceResponse(
                new MemoryStream(Array.Empty<byte>()), 404, "Not Found", "Content-Type: text/plain");
            return;
        }

        e.Response = environment.CreateWebResourceResponse(
            new MemoryStream(jpeg), 200, "OK", "Content-Type: image/jpeg");

        if (!_pdfPageServedLogged)
        {
            _pdfPageServedLogged = true;

            _log.LogInformation(
                "PDF 页位图开始服务：{Uri}（{Bytes} 字节，内存表 {Count} 张）",
                e.Request.Uri, jpeg.Length, _pdfPageImages.Count);
        }
    }

    /// <summary>把一张页位图记进内存表；超出容量就丢掉最老的那张（它再被要就回 404）。</summary>
    private void RememberPdfPageImage(string token, byte[] jpeg)
    {
        lock (_pdfPageImageOrder)
        {
            _pdfPageImages[token] = jpeg;
            _pdfPageImageOrder.AddLast(token);

            while (_pdfPageImageOrder.Count > PdfPageImageLimit)
            {
                var oldest = _pdfPageImageOrder.First!.Value;

                _pdfPageImageOrder.RemoveFirst();
                _pdfPageImages.Remove(oldest);
            }
        }
    }

    /// <summary>取一张页位图。**不删除**：同一个地址可能被渲染器再取一次（解码缓存被清时）。</summary>
    private byte[]? TakePdfPageImage(string token)
    {
        lock (_pdfPageImageOrder)
        {
            return _pdfPageImages.TryGetValue(token, out var jpeg) ? jpeg : null;
        }
    }

    /// <summary>PDF 的进度快照：只有页码与总页数，没有章节/定位符（T6-2）。</summary>
    private ReaderProgressSnapshot BuildPdfSnapshot() => new()
    {
        BookId = _bookId,
        ChapterIndex = 0,
        ChapterCount = 0,
        PageIndex = _pdfPage,
        TotalPages = _pdfTotalPages > 0 ? _pdfTotalPages : null,
        Locator = new ReaderLocator { ChapterIndex = 0 },
        UpdatedAt = DateTimeOffset.UtcNow,
        SourceFormat = _session?.SourceFormat,
    };

    /// <summary>
    /// T6-2 判据 3：把 PDF 交给系统默认程序打开。它是常驻入口（不像 EPUB 只在打开失败时出现），
    /// 所以这里不做"是否失败"的判断——用户想用外部程序读就一直给他。
    /// </summary>
    private void OpenWithSystem()
    {
        var path = _session?.SourcePath;

        if (_shell is null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _log.LogWarning("外部打开失败：没有可用的源文件或 shell 启动器");
            return;
        }

        _shell.OpenFile(path);
    }

    /// <summary>
    /// 目录抽屉的「编辑目录」：查回 Book 再交给 EditCommand（同一本书只会开一个编辑器窗口）。
    /// 编辑器保存成功后由 OnBookEdited 收到通知，重读目录与元数据刷给页面。
    /// </summary>
    private async Task OpenBookEditorAsync()
    {
        if (_channel is null)
        {
            return;
        }

        if (_edit is null || _repository is null)
        {
            _channel.Post("reader.notice", new { message = "编辑入口没有接上（版本不完整？）。" });
            return;
        }

        if (_session?.SourceFormat != BookFormatKind.Epub)
        {
            _channel.Post("reader.notice", new { message = "只有 EPUB 能编辑目录与书籍信息。" });
            return;
        }

        try
        {
            var book = await _repository.GetByIdAsync(_bookId).ConfigureAwait(true);

            if (book is null)
            {
                _channel.Post("reader.notice", new { message = "书库里已经找不到这本书了。" });
                return;
            }

            _edit.Run(this, book);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "从阅读器打开书籍编辑器失败");
            _channel.Post("reader.notice", new { message = "打开编辑器失败，详情见日志。" });
        }
    }

    /// <summary>编辑器保存成功（只关心自己这本）：触发重读与推送。</summary>
    private void OnBookEdited(Guid bookId)
    {
        if (bookId != _bookId)
        {
            return;
        }

        _ = RefreshAfterEditAsync();
    }

    /// <summary>
    /// 重读书的目录与元数据，把新目录 / 新书名作者推给页面——目录抽屉立刻重画；
    /// 书库页由 LibraryPage 订阅同一个 Saved 事件刷新卡片与详情。
    /// </summary>
    internal async Task RefreshAfterEditAsync()
    {
        if (_channel is null || _session is null || _session.SourceFormat != BookFormatKind.Epub)
        {
            return;
        }

        try
        {
            var meta = await _reader.ReadMetadataAsync(_session.SourcePath).ConfigureAwait(true);

            _session.Manifest.Toc = meta.Toc.ToList();
            _session.Manifest.TocFallback = meta.TocFallback;

            if (!string.IsNullOrWhiteSpace(meta.Title))
            {
                _session.Manifest.Title = meta.Title;
            }

            if (!string.IsNullOrWhiteSpace(meta.Author))
            {
                _session.Manifest.Author = meta.Author;
            }

            _channel.Post("reader.toc", new
            {
                toc = meta.Toc.Select(ProjectTocEntry).ToList(),
                tocFallback = meta.TocFallback,
                spineChanged = meta.SpineCount != _session.Manifest.Spine.Count,
            });

            _channel.Post("reader.bookInfo", new
            {
                title = _session.Manifest.Title,
                author = _session.Manifest.Author,
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "编辑保存后刷新目录失败");
            _channel.Post("reader.notice", new { message = "目录已保存，但自动刷新失败（详情见日志），重开这本书可看到新目录。" });
        }
    }

    /// <summary>把页面上报的位置变成落库快照。字段缺失按章首处理，绝不抛。</summary>
    private ReaderProgressSnapshot? BuildSnapshot(JsonObject payload)

    {

        if (_session is null)

        {

            return null;

        }



        var locator = new ReaderLocator

        {

            ChapterHref = ReadString(payload, "chapterHref"),

            ChapterIndex = ReadInt(payload, "chapterIndex", LastChapterIndex < 0 ? 0 : LastChapterIndex),

            PercentInChapter = ReadNullableDouble(payload, "percentInChapter"),

            ScrollTop = ReadNullableDouble(payload, "scrollTop"),

            ScrollHeight = ReadNullableDouble(payload, "scrollHeight"),

        };



        return new ReaderProgressSnapshot

        {

            BookId = _bookId,

            ChapterIndex = locator.ChapterIndex,

            ChapterCount = _session.Manifest.Spine.Count,

            Locator = locator,

            Fingerprint = ReadFingerprint(payload),

            // 用户 2026-10-21：记下这条进度是在哪个格式下存的，换格式继续读要靠它换算
            SourceFormat = _session.SourceFormat,

        };

    }



    // ---------- T6-5：书签与笔记 ----------

    /// <summary>从库里读出这本书的书签与笔记。失败只记 WRN：书签坏了不该挡住阅读。</summary>
    private async Task LoadAnnotationsAsync(CancellationToken ct)
    {
        if (_annotations is null)
        {
            return;
        }

        try
        {
            _bookmarks = await _annotations.ListBookmarksAsync(_bookId, ct).ConfigureAwait(true);
            _notes = await _annotations.ListNotesAsync(_bookId, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "读取书签与笔记失败：{BookId}", _bookId);
        }
    }

    /// <summary>写库之后重读并回推给页面（加/删都走这里，保证列表永远等于库里的事实）。</summary>
    private async Task ReloadAndPostAnnotationsAsync()
    {
        await LoadAnnotationsAsync(CancellationToken.None).ConfigureAwait(true);

        _channel?.Post("reader.annotations", new
        {
            bookmarks = ProjectBookmarks(),
            notes = ProjectNotes(),
        });
    }

    /// <summary>书签投影成页面认识的小驼峰（与 ProjectTocEntry 同一条纪律）。</summary>
    private object[] ProjectBookmarks() => _bookmarks.Select(b => new
    {
        id = b.Id.ToString(),
        label = b.Label,
        chapterIndex = b.SpineIndex,
        pageIndex = b.PageIndex,
        locator = b.Location is null ? null : new
        {
            chapterHref = b.Location.ChapterHref,
            chapterIndex = b.Location.ChapterIndex,
            percentInChapter = b.Location.PercentInChapter,
            scrollTop = b.Location.ScrollTop,
            scrollHeight = b.Location.ScrollHeight,
        },
        createdAt = b.CreatedAt.ToString("o"),
    }).ToArray();

    private object[] ProjectNotes() => _notes.Select(n => new
    {
        id = n.Id.ToString(),
        type = (int)n.Type,
        style = (int)n.Style,
        color = n.Color,
        quote = n.Quote,
        content = n.Content,
        spineIndex = n.SpineIndex,
        pageIndex = n.PageIndex,
        anchor = n.Anchor is null ? null : new
        {
            startPath = n.Anchor.StartPath,
            startOffset = n.Anchor.StartOffset,
            endPath = n.Anchor.EndPath,
            endOffset = n.Anchor.EndOffset,
            percentInChapter = n.Anchor.PercentInChapter,
            pageCharStart = n.Anchor.PageCharStart,
            pageCharEnd = n.Anchor.PageCharEnd,
        },
        createdAt = n.CreatedAt.ToString("o"),
    }).ToArray();

    private async Task AddBookmarkAsync(JsonObject payload)
    {
        if (_annotations is null)
        {
            return;
        }

        try
        {
            var bookmark = new Bookmark
            {
                BookId = _bookId,
                SpineIndex = ReadNullableInt(payload, "chapterIndex"),
                PageIndex = ReadNullableInt(payload, "pageIndex"),
                Location = ReadLocator(payload),
                Label = ReadString(payload, "label"),
            };

            await _annotations.AddBookmarkAsync(bookmark, CancellationToken.None).ConfigureAwait(true);
            await ReloadAndPostAnnotationsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "加书签失败：{BookId}", _bookId);
        }
    }

    private async Task AddNoteAsync(JsonObject payload)
    {
        if (_annotations is null)
        {
            return;
        }

        try
        {
            var note = new Note
            {
                BookId = _bookId,
                SpineIndex = ReadNullableInt(payload, "spineIndex"),
                PageIndex = ReadNullableInt(payload, "pageIndex"),
                Anchor = ReadAnchor(payload),
                Type = ReadInt(payload, "type", 0) == 1
                    ? Core.Enums.NoteType.Annotation
                    : Core.Enums.NoteType.Highlight,
                Style = (Core.Enums.MarkStyle)Math.Clamp(ReadInt(payload, "style", 0), 0, 6),
                Color = Math.Clamp(ReadInt(payload, "color", 0), 0, 14),
                Quote = ReadString(payload, "quote"),
                Content = ReadString(payload, "content"),
            };

            await _annotations.AddNoteAsync(note, CancellationToken.None).ConfigureAwait(true);
            await ReloadAndPostAnnotationsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "加笔记失败：{BookId}", _bookId);
        }
    }

    private async Task DeleteAnnotationAsync(JsonObject payload)
    {
        if (_annotations is null || !Guid.TryParse(ReadString(payload, "id"), out var id))
        {
            return;
        }

        try
        {
            if (string.Equals(ReadString(payload, "kind"), "bookmark", StringComparison.Ordinal))
            {
                await _annotations.DeleteBookmarkAsync(id, CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                await _annotations.DeleteNoteAsync(id, CancellationToken.None).ConfigureAwait(true);
            }

            await ReloadAndPostAnnotationsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "删除书签/笔记失败：{Id}", id);
        }
    }

    /// <summary>T6-5 补短：改笔记内容（点已标记文字弹出的卡片里保存）。</summary>
    private async Task UpdateNoteAsync(JsonObject payload)
    {
        if (_annotations is null || !Guid.TryParse(ReadString(payload, "id"), out var id))
        {
            return;
        }

        try
        {
            await _annotations
                .UpdateNoteAsync(
                    id,
                    ReadString(payload, "content"),
                    ReadNullableInt(payload, "style"),
                    ReadNullableInt(payload, "color"),
                    CancellationToken.None)
                .ConfigureAwait(true);

            await ReloadAndPostAnnotationsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "更新笔记失败：{Id}", id);
        }
    }

    /// <summary>导出这本书的全部书签与笔记为 Markdown，并把落盘路径回给页面提示。</summary>
    private async Task ExportAnnotationsAsync()
    {
        if (_annotations is null)
        {
            return;
        }

        try
        {
            var path = await _annotations.ExportMarkdownAsync(_bookId).ConfigureAwait(true);

            _channel?.Post("reader.exported", new { path });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "导出书签与笔记失败：{BookId}", _bookId);

            _channel?.Post("reader.notice", new { message = "导出失败，详情见日志。" });
        }
    }

    private static int? ReadNullableInt(JsonObject payload, string name)
    {
        try
        {
            return payload[name] is JsonValue value && value.TryGetValue<int>(out var number)
                ? number
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ReaderLocator? ReadLocator(JsonObject payload)
    {
        if (payload["locator"] is not JsonObject locator)
        {
            return null;
        }

        return new ReaderLocator
        {
            ChapterHref = ReadString(locator, "chapterHref"),
            ChapterIndex = ReadInt(locator, "chapterIndex", 0),
            PercentInChapter = ReadNullableDouble(locator, "percentInChapter"),
            ScrollTop = ReadNullableDouble(locator, "scrollTop"),
            ScrollHeight = ReadNullableDouble(locator, "scrollHeight"),
        };
    }

    private static NoteAnchor? ReadAnchor(JsonObject payload)
    {
        if (payload["anchor"] is not JsonObject anchor)
        {
            return null;
        }

        return new NoteAnchor
        {
            StartPath = ReadString(anchor, "startPath"),
            StartOffset = ReadInt(anchor, "startOffset", 0),
            EndPath = ReadString(anchor, "endPath"),
            EndOffset = ReadInt(anchor, "endOffset", 0),
            PercentInChapter = ReadNullableDouble(anchor, "percentInChapter"),
            PageCharStart = ReadNullableInt(anchor, "pageCharStart"),
            PageCharEnd = ReadNullableInt(anchor, "pageCharEnd"),
        };
    }

    /// <summary>切章时立即保存，位置记为章首。</summary>

    private Task SaveChapterStartAsync(int index)

    {

        if (_session is null || index < 0 || index >= _session.Manifest.Spine.Count)

        {

            return Task.CompletedTask;

        }



        var snapshot = new ReaderProgressSnapshot

        {

            BookId = _bookId,

            ChapterIndex = index,

            ChapterCount = _session.Manifest.Spine.Count,

            Locator = new ReaderLocator

            {

                ChapterIndex = index,

                ChapterHref = _session.Manifest.Spine[index].SourceHref,

                PercentInChapter = 0,

                ScrollTop = 0,

            },

            SourceFormat = _session.SourceFormat,

        };



        _latestProgress = snapshot;



        return _progress.SaveNowAsync(snapshot);

    }



    private static TextFingerprint? ReadFingerprint(JsonObject payload)

    {

        try

        {

            if (payload["fingerprint"] is not JsonObject fp)

            {

                return null;

            }



            var result = new TextFingerprint

            {

                Pre = fp["pre"]?.GetValue<string>(),

                Mid = fp["mid"]?.GetValue<string>(),

                Post = fp["post"]?.GetValue<string>(),

            };



            return result.IsUsable ? result : null;

        }

        catch (Exception)

        {

            return null;

        }

    }



    private static double? ReadNullableDouble(JsonObject payload, string name)

    {

        try

        {

            return payload[name] is JsonValue value && value.TryGetValue<double>(out var d) ? d : null;

        }

        catch (Exception)

        {

            return null;

        }

    }



    // ---------- 只给冒烟脚本用的入口（T1-6） ----------



    /// <summary>最近一次收到并已排进保存队列的位置快照。</summary>

    internal ReaderProgressSnapshot? LatestProgress => _latestProgress;



    /// <summary>

    /// 在页面里把正文滚到指定比例（0..1）。走的是真实滚动事件，

    /// 所以节流、上报、防抖整条链路都会被走到（不是去调内部函数）。

    /// </summary>

    internal async Task<bool> ScrollToFractionAsync(double fraction)

    {

        var core = Web.CoreWebView2;



        if (core is null)

        {

            return false;

        }



        var ratio = fraction.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);



        var script =

            "(function(){var f=document.getElementById('chapter');" +

            "if(!f||!f.contentWindow||!f.contentDocument){return false;}" +

            "var w=f.contentWindow,d=f.contentDocument;" +

            "var h=Math.max(1,d.documentElement.scrollHeight);" +

            "var max=Math.max(0,h-(w.innerHeight||0));" +

            "w.scrollTo(0,Math.round(max*" + ratio + "));return true;})()";



        var json = await core.ExecuteScriptAsync(script).ConfigureAwait(true);



        return json == "true";

    }



    /// <summary>读 iframe 当前滚动位置、内容高度与滚动比例。</summary>

    internal async Task<(double ScrollTop, double ScrollHeight, double Percent, double MaxScroll, double ViewportHeight)> ReadFrameScrollAsync()

    {

        var core = Web.CoreWebView2;



        if (core is null)

        {

            return (-1, -1, -1, -1, -1);

        }



        const string script =

            "(function(){var f=document.getElementById('chapter');" +

            "if(!f||!f.contentWindow||!f.contentDocument){return null;}" +

            "var w=f.contentWindow,d=f.contentDocument;" +

            "var h=Math.max(1,d.documentElement.scrollHeight);" +

            "var top=Math.round(w.scrollY||0);" +

            "var vp=w.innerHeight||0;" +

            "return {top:top,height:h,max:Math.max(0,h-vp),vp:vp};})()";



        var json = await core.ExecuteScriptAsync(script).ConfigureAwait(true);



        if (string.IsNullOrEmpty(json) || json == "null")

        {

            return (-1, -1, -1, -1, -1);

        }



        try

        {

            if (JsonNode.Parse(json) is not JsonObject obj)

            {

                return (-1, -1, -1, -1, -1);

            }



            var top = obj["top"]?.GetValue<double>() ?? -1;

            var height = obj["height"]?.GetValue<double>() ?? -1;

            var max = obj["max"]?.GetValue<double>() ?? 0;

            var viewport = obj["vp"]?.GetValue<double>() ?? 0;



            return (top, height, max > 0 ? top / max : 0, max, viewport);

        }

        catch (Exception)

        {

            return (-1, -1, -1, -1, -1);

        }

    }



    /// <summary>读提示条的文案。降级提示（"内容可能有变化"）是判据 2 的一部分。</summary>

    internal async Task<string> ReadNoticeAsync()

    {

        var core = Web.CoreWebView2;



        if (core is null)

        {

            return "";

        }



        var json = await core.ExecuteScriptAsync(

            "(function(){var n=document.getElementById('notice');" +

            "if(!n||n.hidden){return '';}return n.textContent||'';})()").ConfigureAwait(true);



        if (string.IsNullOrEmpty(json) || json == "null")

        {

            return "";

        }



        try

        {

            return JsonNode.Parse(json)?.GetValue<string>() ?? "";

        }

        catch (Exception)

        {

            return "";

        }

    }



    // ---------- JSON 取值（类型不对就回退，不抛） ----------



    /// <summary>T4-6：全屏开关。窗口样式与状态都记下来，退出全屏要一模一样地还原。</summary>

    private void ApplyFullscreen(bool on)

    {

        LastFullscreen = on;



        if (on)

        {

            _savedWindowStyle = WindowStyle;

            _savedWindowState = WindowState;



            WindowStyle = WindowStyle.None;

            ResizeMode = ResizeMode.NoResize;

            WindowState = WindowState.Maximized;

        }

        else

        {

            WindowStyle = _savedWindowStyle;

            ResizeMode = ResizeMode.CanResize;

            WindowState = _savedWindowState;

        }



        _log.LogInformation("阅读器全屏：{On}", on);

    }



    // ---------- T7-3B：阅读会话采集 ----------

    /// <summary>
    /// 上报一次阅读活动。采集器可空；上报本身也不能抛（服务已承诺不抛，这里再兜一层），
    /// 统计出问题最多是数字不准，绝不能让阅读窗口崩掉。
    /// </summary>
    private void ReportReadingActivity(int pagesDelta, long charsDelta)
    {
        if (_stats is null)
        {
            return;
        }

        try
        {
            _stats.ReportActivity(_bookId, Math.Max(0, pagesDelta), Math.Max(0L, charsDelta));
        }
        catch (Exception ex)
        {
            // ReportActivity 契约上不抛；真抛了也只是统计少记一次
            _log.LogDebug(ex, "上报阅读活动失败 {BookId}", _bookId);
        }
    }

    /// <summary>换章时重置采集游标：新章字数要等 reader.rendered 才知道，页号重新建基线。</summary>
    private void ResetReadingStatsCursor(int chapterIndex)
    {
        _statsChapterIndex = chapterIndex;
        _statsChapterChars = 0;
        _statsChapterProgress = 0;
        _statsPage = -1;
    }

    /// <summary>
    /// 按 reader.locatorChanged 估算这次位置变化读了多少：
    /// 字数 = 本章字数 × 章内进度正增量（回滚只把游标调回去、不计负值）；
    /// 分页模式下页数 = 页号变化的绝对值（单次最多算 30 页，防止目录跳章把页数刷爆）。
    /// </summary>
    private void TrackLocatorActivity(JsonObject payload)
    {
        if (_stats is null)
        {
            return;
        }

        var chapterIndex = ReadInt(payload, "chapterIndex", LastChapterIndex < 0 ? 0 : LastChapterIndex);

        if (chapterIndex != _statsChapterIndex)
        {
            // 正常由 chapterChanged 重置；万一漏了消息，这里发现换章也不会把上一章的进度算进来
            ResetReadingStatsCursor(chapterIndex);
        }

        var charsDelta = 0L;
        var percent = ReadNullableDouble(payload, "percentInChapter")
            ?? ReadNullableDouble(payload, "percent");

        if (percent is { } value)
        {
            var progress = Math.Clamp(value, 0, 1);

            if (_statsChapterChars > 0 && progress > _statsChapterProgress)
            {
                charsDelta = (long)Math.Round(_statsChapterChars * (progress - _statsChapterProgress));
            }

            // 回滚同样更新游标：之后往回读不会因为"比历史最大进度小"就一直不算
            _statsChapterProgress = progress;
        }

        var pagesDelta = 0;

        if (string.Equals(ReadString(payload, "mode"), "paged", StringComparison.OrdinalIgnoreCase))
        {
            var pageIndex = ReadNullableInt(payload, "pageIndex");

            if (pageIndex is { } page)
            {
                pagesDelta = PageDelta(page, _statsPage);
                _statsPage = page;
            }
        }

        ReportReadingActivity(pagesDelta, charsDelta);
    }

    /// <summary>PDF / 漫画的页码变化（reader.pdfPageChanged）；chars 恒 0。</summary>
    private void TrackPdfPageActivity(int page)
    {
        if (_stats is null || page < 0)
        {
            return;
        }

        var pagesDelta = PageDelta(page, _statsPage);
        _statsPage = page;

        ReportReadingActivity(pagesDelta, 0);
    }

    /// <summary>页数增量：上次页号是无基线（-1）时这次只建基线不计；单次上限 30 页。</summary>
    private static int PageDelta(int page, int lastPage)
        => lastPage < 0 ? 0 : Math.Min(30, Math.Abs(page - lastPage));

    private static int ReadInt(JsonObject payload, string name, int fallback)

    {

        try

        {

            return payload[name]?.GetValue<int>() ?? fallback;

        }

        catch (Exception)

        {

            return fallback;

        }

    }



    private static double ReadDouble(JsonObject payload, string name)

    {

        try

        {

            return payload[name]?.GetValue<double>() ?? 0;

        }

        catch (Exception)

        {

            return 0;

        }

    }



    private static string? ReadString(JsonObject payload, string name)

    {

        try

        {

            return payload[name]?.GetValue<string>();

        }

        catch (Exception)

        {

            return null;

        }

    }



    private static bool ReadBool(JsonObject payload, string name)

    {

        try

        {

            return payload[name]?.GetValue<bool>() ?? false;

        }

        catch (Exception)

        {

            return false;

        }

    }

}