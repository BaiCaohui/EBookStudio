using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Services.Editing;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Epub;
using Serilog;

namespace EbookToolSuite.App.ViewModels;

/// <summary>
/// 「书籍编辑器」窗口（执行计划阶段 3 的 UI 入口）的视图模型。
/// 分工：这个类只管「工作区读进表单 / 表单写回工作区 / 打包落盘 / 清理工作区」，
/// 窗口（BookEditorWindow）只管控件、弹窗与关闭流程。
/// </summary>
/// <remarks>
/// 【本次边界】只做元数据 + 封面（T3-2 / T3-3 的引擎 + T3-4 的打包器）。
/// 保存链路上还缺两件，留给后面的任务，代码里都留了锚点：
///   1. T3-5：EpubCheck 分级门禁（打包成 .part 之后、原子替换之前插一道校验）；
///   2. T3-6：还原点/备份、operation_log、把新元数据与哈希写回 books / book_formats。
/// 【为什么保存后就清工作区】还没有撤销栈（T3-6 才有），留着工作区只会跟原文件分叉；
/// 关窗即 Discard，语义干净：要么已经写进原文件，要么一个字节都没动。
/// </remarks>
public sealed class BookEditorViewModel : INotifyPropertyChanged
{
    /// <summary>封面预览的解码宽度。太大只是浪费内存，封面上看不出区别。</summary>
    private const int PreviewDecodeWidth = 420;

    private readonly Book _book;
    private readonly BookFormat _format;
    private readonly IEpubWorkspaceFactory _workspaces;
    private readonly CoverService _covers;
    private readonly IEpubSaveService _save;
    private readonly ILibraryCoverService _coverCache;
    private readonly ILibraryScanService _scan;
    private readonly IBookRepository _repo;
    private readonly IOperationLogRepository _operations;

    /// <summary>文件级快照（T5-4）：每次保存前备份"将被写回书里的文件"，历史里可回退。</summary>
    private readonly IWorkspaceSnapshotService? _snapshots;
    private readonly IResourceScanService? _resourceScan;

    /// <summary>Serilog 的 ILogger：App 工程只引用了 Logging.Abstractions，没有 Microsoft 的日志扩展方法。</summary>
    private readonly ILogger _log = Log.ForContext<BookEditorViewModel>();

    /// <summary>刚读出来的作者，带 role / file-as。表单只显示名字，保存时按名字对回去，别把这两项弄丢。</summary>
    private readonly List<EpubContributor> _originalAuthors = new();

    private IEpubWorkspace? _workspace;

    /// <summary>左侧章节树（T5-1）。结构操作都在它自己身上，这里只订阅它的通知。</summary>
    public ChapterTreeViewModel Chapters { get; }
    private bool _loading;
    private bool _isDirty;
    private bool _isBusy;
    private bool _isEpub3 = true;

    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _authors = string.Empty;
    private string _series = string.Empty;
    private string _seriesIndex = string.Empty;
    private string _language = string.Empty;
    private string _isbn = string.Empty;
    private string _publisher = string.Empty;
    private string _publicationDate = string.Empty;
    private string _description = string.Empty;
    private string _subjects = string.Empty;
    private int _rating;
    private ImageSource? _coverPreview;
    private string _coverNote = "（封面还没载入）";
    private string _statusText = "还没打开工作区";
    private EpubSaveStage? _pendingStage;
    private bool _showValidationPanel;
    private string _validationHeadline = string.Empty;
    private string _validationDetails = string.Empty;
    private string _confirmActionText = "仍然保存（不推荐）";
    private bool _canAutoFix;

    public BookEditorViewModel(
        Book book,
        BookFormat format,
        IEpubWorkspaceFactory workspaces,
        CoverService covers,
        IEpubSaveService save,
        ILibraryCoverService coverCache,
        ILibraryScanService scan,
        IBookRepository repo,
        IOperationLogRepository operations,
        IChapterTreeService chapters,
        IWorkspaceSnapshotService? snapshots = null,
        IResourceScanService? resourceScan = null)
    {
        _book = book;
        _format = format;
        _workspaces = workspaces;
        _covers = covers;
        _save = save;
        _coverCache = coverCache;
        _scan = scan;
        _repo = repo;
        _operations = operations;
        _snapshots = snapshots;
        _resourceScan = resourceScan;

        Chapters = new ChapterTreeViewModel(chapters);

        // 章节结构改了 = 工作区文件变了，要标脏（保存才会把它打包进去）。
        Chapters.Changed += (_, message) =>
        {
            MarkDirty();
            StatusText = message;
        };

        // 【重要】回到会话开始会换掉工作区：不换引用的话，后面的保存会往已经删掉的目录里写。
        Chapters.WorkspaceReplaced += (_, workspace) =>
        {
            _workspace = workspace;
            StatusText = "已回到会话开始：章节结构恢复成刚打开时的样子（表单里未保存的修改还在，可以继续保存）";
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Book Book => _book;

    /// <summary>被编辑的 EPUB（书库正式区文件）。保存就是把它原地原子替换掉。</summary>
    public string SourcePath => _format.FilePath;

    /// <summary>工作区根目录；没打开时为 null。</summary>
    public string? WorkDir => _workspace?.WorkDir;

    /// <summary>当前会话工作区（T5-5 资源检查窗口要用）。</summary>
    internal IEpubWorkspace? Workspace => _workspace;

    /// <summary>工作区开好了（表单里的值才有意义）。</summary>
    public bool IsLoaded => _workspace is not null;

    public bool IsEpub3 => _isEpub3;

    public bool IsEpub2 => !_isEpub3;

    /// <summary>正在跑长任务（保存 / 换封面），期间按钮防重入。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetStatus(ref _isBusy, value);
    }

    /// <summary>有未保存的修改：窗口标题加 <c>*</c>，关闭时要提示（设计文档 9.4.4）。</summary>
    public bool IsDirty => _isDirty;

    public string WindowTitle
    {
        get
        {
            var name = _title.Trim();
            if (name.Length == 0)
            {
                name = _book.Title;
            }

            return "编辑书籍 《" + name + "》" + (_isDirty ? " *" : string.Empty);
        }
    }

    /// <summary>状态条文案（不参与脏判断）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetStatus(ref _statusText, value);
    }

    // ---------- 表单字段 ----------

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value)
            {
                return;
            }

            _title = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WindowTitle));
            MarkDirty();
        }
    }

    public string Subtitle
    {
        get => _subtitle;
        set => SetField(ref _subtitle, value);
    }

    public string Authors
    {
        get => _authors;
        set => SetField(ref _authors, value);
    }

    public string Series
    {
        get => _series;
        set => SetField(ref _series, value);
    }

    public string SeriesIndex
    {
        get => _seriesIndex;
        set => SetField(ref _seriesIndex, value);
    }

    public string Language
    {
        get => _language;
        set => SetField(ref _language, value);
    }

    public string Isbn
    {
        get => _isbn;
        set => SetField(ref _isbn, value);
    }

    public string Publisher
    {
        get => _publisher;
        set => SetField(ref _publisher, value);
    }

    public string PublicationDate
    {
        get => _publicationDate;
        set => SetField(ref _publicationDate, value);
    }

    public string Description
    {
        get => _description;
        set => SetField(ref _description, value);
    }

    public string Subjects
    {
        get => _subjects;
        set => SetField(ref _subjects, value);
    }

    /// <summary>0 = 未评分，1..5 = 星数。</summary>
    public int Rating
    {
        get => _rating;
        set => SetField(ref _rating, value);
    }

    public ImageSource? CoverPreview
    {
        get => _coverPreview;
        private set
        {
            if (!SetStatus(ref _coverPreview, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasCoverPreview));
        }
    }

    public bool HasCoverPreview => _coverPreview is not null;

    public string CoverNote => _coverNote;

    // ---------- 校验门禁（T3-5） ----------

    /// <summary>有等待用户决定的暂存产物（校验没过 / 超时 / 工具挂了）时为 true。</summary>
    public bool HasPendingConfirmation => _pendingStage is not null;

    /// <summary>校验面板要不要显示（非模态：冒烟测试能直接点上面的按钮）。</summary>
    public bool ShowValidationPanel
    {
        get => _showValidationPanel;
        private set => SetStatus(ref _showValidationPanel, value);
    }

    /// <summary>面板主提示：一句话说清为什么被拦下来。</summary>
    public string ValidationHeadline
    {
        get => _validationHeadline;
        private set => SetStatus(ref _validationHeadline, value);
    }

    /// <summary>面板里列出的前若干条 EpubCheck message（严重级别 + ID + 位置 + 说明）。</summary>
    public string ValidationDetails
    {
        get => _validationDetails;
        private set => SetStatus(ref _validationDetails, value);
    }

    /// <summary>确认按钮上的字：阻止时是「仍然保存（不推荐）」，超时/工具挂了是「跳过校验保存」。</summary>
    public string ConfirmActionText
    {
        get => _confirmActionText;
        private set => SetStatus(ref _confirmActionText, value);
    }

    /// <summary>
    /// 「一键修复」按钮要不要出现：工作区里确实有"在包里但 manifest 没声明"的文件时才出现。
    /// 注意判定依据是**事实**（有未声明的文件），不是 EpubCheck 的错误号
    /// 同一种毛病实测会报 RSC-008，计划文档里写的却是 OPF-003，绑死错误号会漏。
    /// </summary>
    public bool CanAutoFix
    {
        get => _canAutoFix;
        private set => SetStatus(ref _canAutoFix, value);
    }

    // ---------- 打开 ----------

    /// <summary>
    /// 打开工作区并把 OPF 里的元数据读进表单。重复调用是空操作。
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_workspace is not null)
        {
            return;
        }

        StatusText = "正在解包工作区";

        _workspace = await _workspaces
            .OpenAsync(
                new WorkspaceOpenRequest
                {
                    EpubPath = _format.FilePath,
                    BookId = _book.Id,
                    FormatId = _format.Id,
                },
                ct)
            .ConfigureAwait(true);

        Chapters.Attach(_workspace);

        var editor = CreateEditor();
        var meta = editor.Read();

        _isEpub3 = editor.IsEpub3;
        OnPropertyChanged(nameof(IsEpub3));
        OnPropertyChanged(nameof(IsEpub2));

        _loading = true;
        try
        {
            ApplyMetadata(meta);
        }
        finally
        {
            _loading = false;
        }

        LoadCoverPreview();

        ClearDirty();

        /* 【T5-6 第 8 步】保存成功＝工作区里的改动已经进书了：清空 changedFiles（重置为"已保存"状态），
           会话描述要落盘（下次打开才不会以为还没保存）；**不关工作区**，用户可以接着改。 */
        MarkWorkspaceSaved();
        StatusText = _isEpub3
            ? "工作区已就绪（EPUB3）"
            : "工作区已就绪（EPUB2）";
    }

    /// <summary>放弃编辑：清掉整个工作区目录。原文件全程不动，幂等。</summary>
    public async Task DiscardAsync(CancellationToken ct = default)
    {
        // 有等待确认的暂存产物就先丢掉：放弃/关窗时不能留下 .ets-stage 残渣。
        if (_pendingStage is { } pending)
        {
            ClearPending();
            _save.Discard(pending);
        }

        var workspace = _workspace;
        if (workspace is null)
        {
            return;
        }

        Chapters.Detach();
        _workspace = null;

        try
        {
            await workspace.DiscardAsync(ct).ConfigureAwait(true);
            StatusText = "工作区已清理（原文件未改动）";
        }
        catch (Exception ex)
        {
            // 清不掉不该挡用户关窗：认领目录已在 Data/Workspaces 下，启动时的 ListResumable 还能收拾
            _log.Warning(ex, "清理工作区失败 {Dir}", workspace.WorkDir);
            StatusText = "工作区没能清干净（不影响原文件）";
        }
    }

    // ---------- 封面 ----------

    /// <summary>换封面：交给 CoverService 规范化 + 写进工作区（原图不会被直接塞进书里）。</summary>
    public async Task<CoverReplaceResult> ReplaceCoverAsync(byte[] imageBytes, CancellationToken ct = default)
    {
        if (_workspace is null)
        {
            throw new AppException(ErrorCode.InternalError, "工作区还没打开，无法替换封面。");
        }

        IsBusy = true;
        try
        {
            StatusText = "正在处理封面";

            var result = await _covers
                .ReplaceCoverAsync(_workspace.WorkDir, imageBytes, ct)
                .ConfigureAwait(true);

            LoadCoverPreview();
            MarkDirty();

            StatusText = result.Renamed
                ? "封面已换（另存为 " + result.Href + "）"
                : "封面已换（" + result.Href + "）";

            _log.Information(
                "封面已替换 {BookId} -> {Href}（{Width}x{Height}，{Bytes} 字节）",
                _book.Id, result.Href, result.Width, result.Height, result.ByteCount);

            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------- 保存 ----------

    /// <summary>
    /// 保存：表单 -> OPF -> 打包成 .part -> 结构自检 -> 原子替换原文件 -> 同步库里记的文件信息。
    /// 返回一句给人看的总结（窗口状态条直接用）。
    /// </summary>
    public async Task<string> SaveAsync(CancellationToken ct = default)
    {
        if (_workspace is null)
        {
            throw new AppException(ErrorCode.InternalError, "工作区还没打开，无法保存。");
        }

        IsBusy = true;
        try
        {
            StatusText = "正在写回 OPF";
            WriteMetadataToOpf();

            /* 【T5-6 第 5 步】保存前扫一遍资源引用：有悬空引用就警告（只警告，不拦保存）。 */
            var resourceWarning = await ScanDanglingAsync(ct).ConfigureAwait(true);

            /* 【T5-4 判据 2】保存前给"这次要写回书里的那些文件"建一份增量快照（历史里能回退到保存前）。
               必须放在 WriteMetadataToOpf() 之后：元数据改的是 OPF，它到这一步才进 changedFiles；
               放在前面的话元数据保存会算成"没有变更"，快照就白建了。
               建不成就中止保存：宁可这次不保存，也不要给用户一个"存错了退不回去"的结果。 */
            if (!await TryTakeSaveSnapshotAsync(ct).ConfigureAwait(true))
            {
                return StatusText;
            }

            // 【T3-5】先打包到 {原文件}.ets-stage 再跑 EpubCheck：这一步全程不碰原文件。
            // 校验没过 / 超时 / 工具挂了时停下来问用户（见 ConfirmSaveAnywayAsync / CancelPendingSave）。
            StatusText = "正在打包到暂存文件并校验（EpubCheck 可能要几秒）";

            var stage = await _save
                .StageAsync(_workspace.WorkDir, _format.FilePath, progress: null, ct: ct)
                .ConfigureAwait(true);

            if (stage.Decision.NeedsConfirmation)
            {
                _pendingStage = stage;
                ShowValidation(stage.Decision);
                CanAutoFix = ComputeCanAutoFix();
                OnPropertyChanged(nameof(HasPendingConfirmation));
                StatusText = stage.Decision.Summary;
                return AppendResourceWarning(StatusText, resourceWarning);
            }

            return AppendResourceWarning(
                await CompleteSaveAsync(stage, stage.Decision, ct).ConfigureAwait(true), resourceWarning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 用户在「校验没过」/「校验超时」的面板上点了确认：不再拦，照旧替换原文件。
    /// 计划原文的哲学：个人自用工具，永远给用户最终决定权，但风险要说清楚（默认按钮是取消）。
    /// </summary>
    public async Task<string> ConfirmSaveAnywayAsync(CancellationToken ct = default)
    {
        if (_pendingStage is not { } stage)
        {
            StatusText = "没有等待确认的保存。";
            return StatusText;
        }

        IsBusy = true;
        try
        {
            ClearPending();
            _log.Information("用户选择仍然保存（EpubCheck 结论：{Outcome}）", stage.Decision.Outcome);
            return await CompleteSaveAsync(stage, stage.Decision, ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>用户点了「取消」：丢掉暂存产物。原文件从没被碰过，工作区里的改动还在。</summary>
    public string CancelPendingSave()
    {
        if (_pendingStage is not { } stage)
        {
            StatusText = "没有等待确认的保存。";
            return StatusText;
        }

        ClearPending();
        _save.Discard(stage);
        StatusText = "已取消保存（原文件没有被改动）";
        return StatusText;
    }

    /// <summary>
    /// 一键修复：把工作区里"在包里但 manifest 没声明"的文件补进 manifest，然后重新走一遍保存。
    /// 实测依据：被内容引用却没声明的资源，EpubCheck 会报 RSC-008 并阻止保存；
    /// 补全 manifest 之后同样的包是干净的（集成测试里用真工具验过）。
    /// </summary>
    public async Task<string> AutoFixAndRetryAsync(CancellationToken ct = default)
    {
        if (_pendingStage is not { } stage)
        {
            StatusText = "没有等待修复的保存。";
            return StatusText;
        }

        var workDir = _workspace?.WorkDir
            ?? throw new AppException(ErrorCode.InternalError, "工作区还没打开，无法修复。");
        var opf = OpfAbsolutePath()
            ?? throw new AppException(ErrorCode.EpubInvalid, "工作区里找不到 OPF，无法修复。");

        IsBusy = true;
        try
        {
            // 第 1 类：包里有、manifest 没声明的文件（EpubCheck 报 RSC-008）-> 补声明
            var repair = OpfManifestRepair.Repair(opf, workDir);

            // 第 2 类：manifest 声明了、包里却没有的文件（EpubCheck 报 RSC-001）-> 删掉声明与 spine 引用
            var removed = OpfManifestRepair.RemoveDangling(opf, workDir);

            _log.Information(
                "一键修复：补了 {Added} 个未声明文件（{Hrefs}）、移除 {Removed} 个指向不存在文件的声明（{RemovedHrefs}），跳过 {Skipped} 个",
                repair.AddedCount,
                string.Join("、", repair.AddedHrefs),
                removed.Count,
                string.Join("、", removed),
                repair.SkippedFiles.Count);

            // 旧的那份暂存产物（带着"未声明"的包）作废，重新打包 + 重新校验
            _save.Discard(stage);
            ClearPending();
            MarkDirty();
            var what = new List<string>();
            if (repair.AddedCount > 0)
            {
                what.Add("补全 " + repair.AddedCount + " 个文件的声明");
            }

            if (removed.Count > 0)
            {
                what.Add("移除 " + removed.Count + " 个指向不存在文件的声明");
            }

            StatusText = "已" + (what.Count == 0 ? "重新整理 manifest" : string.Join("、", what))
                + "，正在重新校验";

            return await SaveAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>清掉"等待用户决定"的状态（暂存产物由调用方决定是提交还是丢弃）。</summary>
    private void ClearPending()
    {
        _pendingStage = null;
        HideValidation();
        CanAutoFix = false;
        OnPropertyChanged(nameof(HasPendingConfirmation));
    }

    /// <summary>当前工作区里有没有可补的未声明文件（没有就不要显示「一键修复」）。</summary>
    private bool ComputeCanAutoFix()
    {
        try
        {
            var workDir = _workspace?.WorkDir;
            var opf = OpfAbsolutePath();
            if (string.IsNullOrEmpty(workDir) || opf is null)
            {
                return false;
            }

            // 两类可修错误都算"能修"：未声明的文件（RSC-008）、声明了但文件不在包里（RSC-001）
            return OpfManifestRepair.FindUnmanifested(opf, workDir).Count > 0
                || OpfManifestRepair.FindDangling(opf, workDir).Count > 0;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "判断能不能一键修复时出错，就不显示修复按钮");
            return false;
        }
    }

    /// <summary>真正落地：原子替换原文件 -> 同步书库 -> 刷封面缓存。</summary>
    /// <summary>真正落地：原子替换原文件 -> 同步书库 -> 刷封面缓存。</summary>
    /// <summary>
    /// 保存前建增量快照（只含将被写回书里的文件）。返回 false 表示"快照没建成，保存已中止"。
    /// <para>
    /// 为什么保存前要重新读一次 .session.json：会话文件是"谁改谁写"，主编辑窗口手里那份
    /// 可能是旧的（阅读器 / 可视编辑窗口也会往同一个工作区写），照内存里那份建快照会漏文件。
    /// </para>
    /// </summary>
    private async Task<bool> TryTakeSaveSnapshotAsync(CancellationToken ct)
    {
        if (_snapshots is null || _workspace is null)
        {
            return true;
        }

        IReadOnlyList<string> changed;
        try
        {
            var fresh = await _workspaces.ResumeAsync(_workspace.SessionId, ct).ConfigureAwait(true);
            changed = fresh?.Session.ChangedFiles?.ToList() ?? _workspace.Session.ChangedFiles.ToList();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "保存前重读会话失败，改用内存里的 changedFiles");
            changed = _workspace.Session.ChangedFiles.ToList();
        }

        var entries = new List<string>(changed);

        /* 保存一定会重写 OPF（书名/作者/评分这些元数据都在里面），而元数据那条链路没走
           ChapterSourceService.Save，也就没记进 changedFiles  所以这里显式把它算上，
           否则"只改了元数据"的保存会算成"没有变更"，快照白建。 */
        var opf = _workspace.Session.OpfPath;

        if (!string.IsNullOrWhiteSpace(opf)
            && !entries.Contains(opf, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(opf);
        }

        if (entries.Count == 0)
        {
            // 既没有变更清单、也没有 OPF：这次保存写回的内容与书里一样，没什么可保护的。
            return true;
        }

        try
        {
            var snapshot = await _snapshots
                .TakeIncrementalAsync(
                    _workspace,
                    entries,
                    WorkspaceSnapshotKinds.Save,
                    "保存前：将写回 " + entries.Count + " 个文件",
                    ct)
                .ConfigureAwait(true);

            _log.Information("保存前快照已建 {Id}（{Count} 个文件）", snapshot.Id, snapshot.EntryCount);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "保存前建快照失败");
            StatusText = "保存前的快照没建成，已中止（原文件没有被改动）：" + ex.SafeMessage();
            return false;
        }
    }

    private async Task<string> CompleteSaveAsync(EpubSaveStage stage, ValidationDecision decision, CancellationToken ct)
    {
        // 【T3-6 第 6/7/8 步】替换前先建还原点、替换、失败回滚，都在 CommitAsync 里按顺序做
        StatusText = "正在备份并替换原文件";
        var result = await _save
            .CommitAsync(stage, new RestorePointRequest(_book.Id, _format.Id, stage.TargetPath), ct)
            .ConfigureAwait(true);

        StatusText = "正在同步书库";

        // 文件被我们换过了：库里记的体积/修改时间/快速指纹要跟上，
        // 否则书库会把这本标成「已被外部修改」（T2-6 那套判断按大小与时间比）。
        await _scan
            .AcceptModificationAsync(_book.Id, BookFormatKind.Epub, ct)
            .ConfigureAwait(true);

        // 封面缓存（Data/Cache/Covers）只在导入时写过，换过封面就得重写两档，
        // 否则网格上还是旧封面（T3-3 判据 4 的引擎就在这里被复用）。
        await RefreshCachedCoverAsync(ct).ConfigureAwait(true);

        // 【T3-6 第 10 步】把改后的元数据回写 books：网格里的书名/作者读的是数据库，
        // 不回写就等于"改了看不见"（这是 T3-UI 以来一直挂着的那个缺口）。
        await WriteMetadataToLibraryAsync(ct).ConfigureAwait(true);

        // 【T3-6 第 11 步】写 operation_log
        await LogSaveAsync(result, decision, ct).ConfigureAwait(true);

        ClearDirty();

        /* 【T5-6 第 8 步】保存成功＝工作区里的改动已经进书了：清空 changedFiles（重置为"已保存"状态），
           会话描述要落盘（下次打开才不会以为还没保存）；**不关工作区**，用户可以接着改。 */
        MarkWorkspaceSaved();

        var note = decision.Outcome switch
        {
            ValidationOutcome.Pass => "，EpubCheck 校验通过",
            ValidationOutcome.PassWithWarnings => "，EpubCheck 有 " + WarningCount(decision) + " 条警告（已放行）",
            ValidationOutcome.ToolMissing => "，没装校验工具已跳过校验",
            ValidationOutcome.TimedOut => "，校验超时已跳过",
            ValidationOutcome.ToolFailed => "，校验没跑成功已跳过",
            _ => "，校验未通过但你选择了仍然保存",
        };

        StatusText = "已保存（" + result.EntryCount + " 个条目，" + FormatBytes(result.ByteCount) + "）" + note
            + "；书库信息已同步"
            + (result.RestorePointId is null
                ? string.Empty
                : "，已建还原点 " + result.RestorePointId);
        return StatusText;
    }

    /// <summary>把表单里的元数据回写 books 表（T3-6 保存闭环第 10 步）。</summary>
    private async Task WriteMetadataToLibraryAsync(CancellationToken ct)
    {
        var authors = string.Join("、", ParseAuthors().Select(static author => author.Name));

        var update = new BookMetadataUpdate(
            BookId: _book.Id,
            Title: Title.Trim(),
            TitleSort: Blank(Title),
            Authors: Blank(authors),
            AuthorSort: Blank(authors),
            Publisher: Blank(Publisher),
            PublishedDate: Blank(PublicationDate),
            Isbn: Blank(Isbn),
            Language: Blank(Language),
            Description: Blank(Description),
            Series: Blank(Series),
            SeriesIndex: ParseSeriesIndex(),
            Rating: Rating);

        await _repo.UpdateMetadataAsync(update, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 写 operation_log（T3-6 保存闭环第 11 步）。日志写失败**绝不能**影响保存本身，
    /// 所以这里自己兜住异常。强制保存（校验没过 / 超时 / 工具挂了）用单独的 operation 值区分。
    /// </summary>
    private async Task LogSaveAsync(EpubPackageResult result, ValidationDecision decision, CancellationToken ct)
    {
        try
        {
            var forced = decision.Outcome is ValidationOutcome.Blocked
                or ValidationOutcome.TimedOut
                or ValidationOutcome.ToolFailed;

            var detail = JsonSerializer.Serialize(new
            {
                outcome = decision.Outcome.ToString(),
                decision = decision.Summary,
                entries = result.EntryCount,
                bytes = result.ByteCount,
                warnings = WarningCount(decision),
            });

            await _operations
                .AddAsync(
                    new OperationLogEntry(
                        forced ? OperationNames.SaveForced : OperationNames.Save,
                        (forced ? "强制保存《" : "保存《") + Title.Trim() + "》",
                        _book.Id,
                        _format.Id,
                        result.RestorePointId,
                        detail),
                    ct)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "写 operation_log 失败 {BookId}", _book.Id);
        }
    }

    private static int WarningCount(ValidationDecision decision)
    {
        var count = 0;
        foreach (var message in decision.Messages)
        {
            if (message.IsWarning)
            {
                count++;
            }
        }

        return count;
    }

    // ---------- 内部 ----------

    private void WriteMetadataToOpf()
    {
        var editor = CreateEditor();
        editor.Read();

        var title = _title.Trim();
        if (title.Length == 0)
        {
            throw new AppException(ErrorCode.ValidationFailed, "书名不能为空（OPF 必须有 dc:title）。");
        }

        editor.SetTitle(title);

        // 副标题是 EPUB3 的 refines 写法，EPUB2 里写进去是非法的，直接跳过（表单也禁掉了）。
        if (_isEpub3)
        {
            editor.SetSubtitle(Blank(_subtitle));
        }

        editor.SetAuthors(ParseAuthors());
        editor.SetSeries(Blank(_series), ParseSeriesIndex());
        editor.SetLanguage(Blank(_language));
        editor.SetPublisher(Blank(_publisher));
        editor.SetDescription(Blank(_description));
        editor.SetPublicationDate(ParsePublicationDate());
        editor.SetSubjects(SplitList(_subjects));
        editor.SetRating(_rating <= 0 ? null : _rating);
        editor.SetIsbn(Blank(_isbn));

        editor.Save();
    }

    /// <summary>把表单里的替换分隔符切成若干项（中文顿号 / 逗号 / 分号 / 竖线都认）。</summary>
    private static IReadOnlyList<string> SplitList(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var parts = text.Split(new[] { '、', ',', '，', ';', '；', '|' }, StringSplitOptions.RemoveEmptyEntries);

        var result = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var item = part.Trim();
            if (item.Length > 0)
            {
                result.Add(item);
            }
        }

        return result;
    }

    private IReadOnlyList<EpubContributor> ParseAuthors()
    {
        var names = SplitList(_authors);
        var result = new List<EpubContributor>(names.Count);

        foreach (var name in names)
        {
            // 名字没变的作者保留原来的 role / file-as（表单里不回显这两项，丢了就再也回不来）
            var known = _originalAuthors.Find(a => string.Equals(a.Name, name, StringComparison.Ordinal));
            result.Add(known ?? new EpubContributor(name));
        }

        return result;
    }

    private double? ParseSeriesIndex()
    {
        var text = _seriesIndex.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new AppException(ErrorCode.ValidationFailed, "系列序号要填数字（例如 3 或 2.5）。");
        }

        return value;
    }

    private DateOnly? ParsePublicationDate()
    {
        var text = _publicationDate.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (!DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new AppException(ErrorCode.ValidationFailed, "出版日期要填成 2024-05-01 这样的格式。");
        }

        return date;
    }

    private OpfMetadataEditor CreateEditor()
    {
        var opf = OpfAbsolutePath()
            ?? throw new AppException(ErrorCode.EpubInvalid, "工作区里找不到 OPF 文件，无法编辑。");

        return new OpfMetadataEditor(opf);
    }

    private string? OpfAbsolutePath()
    {
        var workspace = _workspace;
        var entry = workspace?.Session.OpfPath;

        if (workspace is null || string.IsNullOrWhiteSpace(entry))
        {
            return null;
        }

        var relative = entry.Replace('/', Path.DirectorySeparatorChar);
        var absolute = Path.GetFullPath(Path.Combine(workspace.WorkDir, relative));

        return File.Exists(absolute) ? absolute : null;
    }

    private void ApplyMetadata(OpfMetadata meta)
    {
        _originalAuthors.Clear();
        _originalAuthors.AddRange(meta.Authors);

        _title = meta.Title ?? string.Empty;
        _subtitle = meta.Subtitle ?? string.Empty;
        _authors = string.Join("、", meta.Authors.Select(a => a.Name));
        _series = meta.Series ?? string.Empty;
        _seriesIndex = meta.SeriesIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _language = meta.Language ?? string.Empty;
        _isbn = meta.Isbn ?? string.Empty;
        _publisher = meta.Publisher ?? string.Empty;
        _publicationDate = meta.PublicationDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        _description = meta.Description ?? string.Empty;
        _subjects = string.Join("、", meta.Subjects);
        // 下拉框只有 0..5 六个条目：外部工具写的脏数据（比如 calibre:rating=12）夹一下，
        // 否则 SelectedIndex 越界，界面会显示成空白的评分。
        _rating = Math.Clamp(meta.Rating ?? 0, 0, 5);

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Authors));
        OnPropertyChanged(nameof(Series));
        OnPropertyChanged(nameof(SeriesIndex));
        OnPropertyChanged(nameof(Language));
        OnPropertyChanged(nameof(Isbn));
        OnPropertyChanged(nameof(Publisher));
        OnPropertyChanged(nameof(PublicationDate));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Subjects));
        OnPropertyChanged(nameof(Rating));
        OnPropertyChanged(nameof(WindowTitle));
    }

    /// <summary>封面预览用工作区里的那张图（没有就留空）。</summary>
    private void LoadCoverPreview()
    {
        var path = WorkspaceCoverPath();

        CoverPreview = path is null ? null : LoadImage(path);

        if (path is null)
        {
            _coverNote = "这本书没有可显示的封面";
        }
        else
        {
            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                size = 0;
            }

            _coverNote = Path.GetFileName(path) + "（" + FormatBytes(size) + "）";
        }

        OnPropertyChanged(nameof(CoverNote));
    }

    /// <summary>
    /// 在工作区里找封面文件。口径与 CoverService / 阅读器一致：
    /// 先认 OPF 的 cover item 引用，再认 EPUB3 的 properties="cover-image"。
    /// </summary>
    private string? WorkspaceCoverPath()
    {
        var workspace = _workspace;
        var opf = OpfAbsolutePath();

        if (workspace is null || opf is null)
        {
            return null;
        }

        OpfMetadata meta;
        IReadOnlyList<OpfManifestItem> manifest;
        try
        {
            var editor = new OpfMetadataEditor(opf);
            meta = editor.Read();
            manifest = editor.ReadManifest();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "读工作区封面信息失败 {Opf}", opf);
            return null;
        }

        var item = manifest.FirstOrDefault(
                i => meta.CoverItemId is { Length: > 0 }
                    && string.Equals(i.Id, meta.CoverItemId, StringComparison.Ordinal))
            ?? manifest.FirstOrDefault(
                i => i.Properties is { Length: > 0 }
                    && i.Properties.Contains("cover-image", StringComparison.Ordinal));

        if (item is null || string.IsNullOrWhiteSpace(item.Href))
        {
            return null;
        }

        if (item.Href.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
            || item.Href.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
            || item.Href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var baseDir = Path.GetDirectoryName(opf);
        if (string.IsNullOrEmpty(baseDir))
        {
            return null;
        }

        var href = Uri.UnescapeDataString(item.Href.Split('#')[0]).Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(baseDir, href));
        var root = Path.GetFullPath(workspace.WorkDir);

        // OPF 是外部数据，href 可以指向工作区外面（Zip Slip 的同款风险），越界就当没有封面
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _log.Warning("封面 href 指到工作区外，忽略：{Href}", item.Href);
            return null;
        }

        return File.Exists(full) ? full : null;
    }

    private ImageSource? LoadImage(string path)
    {
        try
        {
            // UriSource + OnLoad：OnLoad 会在 EndInit 时把图整个解进内存并把文件句柄关掉，
            // 所以放弃编辑删工作区时不会被预览锁住。
            // 【踩过的坑】这里不能用 StreamSource：IgnoreImageCache 需要一个 Uri 当缓存键，
            // 没有 Uri 时 WPF 会抛「键不能为 null。参数名: key」，预览永远是空的。
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = PreviewDecodeWidth;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception ex)
        {
            // WebP 之类 WPF 解不了的格式不是错误：没有预览照样能编辑
            _log.Warning(ex, "封面预览解码失败 {Path}", path);
            return null;
        }
    }

    /// <summary>保存成功后把封面重写进书库的两档缓存，网格才不会还显示旧封面。</summary>
    private async Task RefreshCachedCoverAsync(CancellationToken ct)
    {
        var path = WorkspaceCoverPath();
        if (path is null)
        {
            return;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(true);
            if (bytes.Length == 0)
            {
                return;
            }

            await _coverCache.RefreshAsync(_book.Id, bytes, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 封面缓存没刷上不该让「保存成功」变成失败：元数据已经写进文件了
            _log.Warning(ex, "刷新书库封面缓存失败 {BookId}", _book.Id);
        }
    }

    private static string? Blank(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + " MB",
    };

    /// <summary>把门禁结论铺到面板上。</summary>
    private void ShowValidation(ValidationDecision decision)
    {
        var lines = decision.TopMessages(12);
        ValidationHeadline = decision.Summary;
        ValidationDetails = lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
        ConfirmActionText = decision.ConfirmActionText;
        ShowValidationPanel = true;
    }

    private void HideValidation()
    {
        ShowValidationPanel = false;
        ValidationDetails = string.Empty;
    }

    private void MarkDirty()
    {
        if (_loading || _isDirty)
        {
            return;
        }

        _isDirty = true;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void ClearDirty()
    {
        if (!_isDirty)
        {
            return;
        }

        _isDirty = false;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(WindowTitle));
    }

    /// <summary>表单字段：变了就记脏。</summary>
    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
        MarkDirty();
    }

    /// <summary>状态类字段（不参与脏判断）。</summary>
    private bool SetStatus<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    /// <summary>【T5-6 第 5 步】保存前扫资源引用：有悬空引用就返回一句警告（只警告，不拦保存）。</summary>
    private async Task<string?> ScanDanglingAsync(CancellationToken ct)
    {
        if (_resourceScan is null || _workspace is null)
        {
            return null;
        }

        try
        {
            var report = await _resourceScan.ScanAsync(_workspace, ct).ConfigureAwait(true);

            if (report.Dangling.Count == 0)
            {
                return null;
            }

            _log.Warning("保存前资源扫描：悬空引用 {Count} 处", report.Dangling.Count);
            return "；注意：有 " + report.Dangling.Count + " 处悬空引用（引用了不存在的文件），可用工具栏「资源检查」定位";
        }
        catch (Exception error)
        {
            // 扫描失败不该拦住保存
            _log.Warning(error, "保存前资源扫描失败");
            return null;
        }
    }

    private static string AppendResourceWarning(string text, string? warning)
        => warning is null ? text : text + warning;

    /// <summary>【T5-6 第 8 步】把会话标记成"已保存"：清空 changedFiles 并落盘（工作区保持打开）。</summary>
    private void MarkWorkspaceSaved()
    {
        if (_workspace is null)
        {
            return;
        }

        _workspace.Session.ChangedFiles.Clear();
        EpubWorkspaceFactory.WriteSession(_workspace.WorkDir, _workspace.Session);
        _log.Information("保存成功：已清空 changedFiles（工作区保持打开）");
    }
}
