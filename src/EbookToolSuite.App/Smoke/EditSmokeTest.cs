using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EbookToolSuite.App.Commands;
using EbookToolSuite.App.Themes;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.App.Views;
using EbookToolSuite.App.Views.Pages;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Common;
using EbookToolSuite.Core;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Reader.WebView;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Epub;
using EbookToolSuite.Infrastructure.Config;
using Microsoft.Web.WebView2.Core;
using EbookToolSuite.Services.Library;
using EbookToolSuite.Services.Editing;
using EbookToolSuite.Services.Validation;
using EbookToolSuite.Services.Scanning;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// 书籍编辑 UI 入口冒烟（阶段 3 的 UI 入口）：
/// 三个入口存在 -> 点工具栏「编辑」真的开窗 -> 工作区与表单灌上真实元数据 ->
/// 改书名 + 换封面 -> 保存 -> 磁盘上的 EPUB 变了且打包自检通过 -> 库里的文件信息与封面缓存跟上 ->
/// 关窗清理工作区 -> 源文件一个字节都没动。
/// 全程真实界面对象、真实文件系统、真实 SQLite。
/// </summary>
public static class EditSmokeTest
{
    private const string MarkerTitle = "冒烟改名（编辑入口）";

    /// <summary>T3-7 验收脚本第 2 步要求"加一个作者 + 改系列"，这两个标记值用来独立核对。</summary>
    private const string MarkerAuthor = "验收作者（冒烟）";

    private const string MarkerSeries = "验收系列";

    public static async Task<SmokeReport> RunAsync(
        AppPaths paths,
        MainWindow window,
        LibraryPage page,
        EditCommand edit,
        IBookImportService importer,
        IBookRepository repo,
        ILibraryCoverService coverCache,
        IEpubWorkspaceFactory workspaces,
        IRestorePointService restorePoints,
        IOperationLogRepository operations,
        IChapterTreeService chapters,
        IChapterSourceService source,
        IWebViewEnvironmentProvider environments,
        IReaderAssets assets,
        IVisualEditAutoFixService autofix,
        string sourceFile)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(coverCache);
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(restorePoints);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(environments);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(autofix);

        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        var sourceLength = new FileInfo(sourceFile).Length;
        var sourceStamp = File.GetLastWriteTimeUtc(sourceFile);

        window.Show();
        window.UpdateLayout();

        // ---------- 准备：导入一本书 ----------
        var imported = await importer
            .ImportAsync(sourceFile, ImportOptions.Default, null)
            .ConfigureAwait(true);

        if (imported.Outcome != ImportOutcome.Imported
            || imported.BookId is not Guid bookId
            || string.IsNullOrWhiteSpace(imported.LibraryPath))
        {
            checks.Add(new SmokeCheck(
                "setup-import",
                false,
                "导入没成功：" + imported.Outcome + " " + (imported.Message ?? string.Empty)));
            return Finish(paths, startedAt, stopwatch, checks);
        }

        var libraryPath = imported.LibraryPath;
        checks.Add(new SmokeCheck("setup-import", true, "已导入 -> " + libraryPath));

        var vm = page.ViewModel;
        await vm.LoadAsync().ConfigureAwait(true);
        window.UpdateLayout();

        var card = vm.Books.FirstOrDefault(b => b.Id == bookId);
        var epubFormat = card?.Book.Formats.FirstOrDefault(f => f.Format == BookFormatKind.Epub);

        if (card is null || epubFormat is null)
        {
            checks.Add(new SmokeCheck(
                "setup-card",
                false,
                card is null ? "网格里找不到这本书" : "书里没有 EPUB 格式记录"));
            return Finish(paths, startedAt, stopwatch, checks);
        }

        // 原书（保存前）的元数据，用来证明「表单是从 OPF 灌进来的」而不是凭空来的
        var before = await ReadHeadAsync(workspaces, libraryPath, bookId, epubFormat.Id).ConfigureAwait(true);

        // ---------- A：三个入口都在 ----------
        var toolbarButton = page.FindName("ToolbarEditButton") as Button;
        var detailButton = page.FindName("DetailEditButton") as Button;
        var cardMenu = page.TryFindResource("CardContextMenu") as ContextMenu;
        var menuItem = cardMenu?.Items
            .OfType<MenuItem>()
            .FirstOrDefault(m => string.Equals(m.Header as string, "编辑书籍", StringComparison.Ordinal));

        checks.Add(new SmokeCheck(
            "A-three-entries-exist",
            toolbarButton is not null && detailButton is not null && menuItem is not null,
            "工具栏按钮=" + (toolbarButton is not null)
                + "，详情浮层按钮=" + (detailButton is not null)
                + "，卡片右键菜单项=" + (menuItem is not null)));

        var disabledBeforeSelect = toolbarButton?.IsEnabled == false;
        vm.Select(card);
        window.UpdateLayout();
        var enabledAfterSelect = toolbarButton?.IsEnabled == true;

        checks.Add(new SmokeCheck(
            "A-toolbar-enabled-after-select",
            disabledBeforeSelect && enabledAfterSelect,
            "未选中时禁用=" + disabledBeforeSelect + "，选中后可用=" + enabledAfterSelect));

        // ---------- B：点入口真的开窗（走真实 Click 处理器） ----------
        var thumbBefore = ReadBytes(coverCache.ThumbPath(bookId));

        toolbarButton!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await PumpAsync().ConfigureAwait(true);

        var editorWindow = await WaitForWindowAsync(bookId, TimeSpan.FromSeconds(15)).ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "B-entry-opens-editor-window",
            editorWindow is not null && edit.OpenWindowCount == 1,
            editorWindow is null ? "点「编辑」没有出现编辑器窗口" : "窗口已开，OpenWindowCount=" + edit.OpenWindowCount));

        if (editorWindow is null)
        {
            return Finish(paths, startedAt, stopwatch, checks);
        }

        // 同一本书再点一次：应该前置旧窗口，不能再开一个工作区
        edit.Run(Window.GetWindow(page)!, card.Book);
        await PumpAsync().ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "B-same-book-single-window",
            edit.OpenWindowCount == 1
                && Application.Current.Windows.OfType<BookEditorWindow>().Count() == 1,
            "OpenWindowCount=" + edit.OpenWindowCount));

        if (editorWindow.DataContext is not BookEditorViewModel editorVm)
        {
            checks.Add(new SmokeCheck("B-window-viewmodel", false, "窗口的 DataContext 不是 BookEditorViewModel"));
            return Finish(paths, startedAt, stopwatch, checks);
        }

        // ---------- C：工作区与表单 ----------
        await editorVm.LoadAsync().ConfigureAwait(true);

        var loaded = await WaitUntilAsync(() => editorVm.IsLoaded, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
        var workDir = editorVm.WorkDir;

        checks.Add(new SmokeCheck(
            "C-workspace-loaded",
            loaded && editorVm.IsLoaded && !string.IsNullOrEmpty(workDir) && Directory.Exists(workDir),
            "工作区=" + (workDir ?? "(null)")));

        checks.Add(new SmokeCheck(
            "C-form-filled-from-opf",
            string.Equals(editorVm.Title.Trim(), before.Title?.Trim(), StringComparison.Ordinal),
            "表单书名=" + editorVm.Title + "，原书 OPF 书名=" + (before.Title ?? "(null)")));

        checks.Add(new SmokeCheck(
            "C-version-detected",
            editorVm.IsEpub3 == before.IsEpub3,
            "表单判定 EPUB3=" + editorVm.IsEpub3 + "，原书包 version=" + before.Version));

        // ---------- 评分下拉：必须是 0..5 六个条目，且每条都得有文字 ----------
        // （曾经这 5 条写成 Content=""，界面上只有"未评分"一行有字，其余全是空白行用户报的 bug）
        var ratingLabels = editorWindow.RatingBox.Items
            .OfType<ComboBoxItem>()
            .Select(item => (item.Content as string ?? string.Empty).Trim())
            .ToList();

        checks.Add(new SmokeCheck(
            "C-rating-dropdown-labels",
            ratingLabels.Count == 6 && ratingLabels.All(label => label.Length > 0),
            "条目=" + ratingLabels.Count + " 个："
                + string.Join(" | ", ratingLabels.Select(label => label.Length == 0 ? "(空)" : label))));


        // ---------- D：改书名 -> 脏状态 ----------
        editorVm.Title = MarkerTitle;
        editorVm.Authors = MarkerAuthor;
        editorVm.Series = MarkerSeries;
        editorVm.Rating = 4;
        await PumpAsync().ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "D-dirty-title-has-star",
            editorVm.IsDirty
                && editorVm.WindowTitle.EndsWith(" *", StringComparison.Ordinal)
                && string.Equals(editorWindow.Title, editorVm.WindowTitle, StringComparison.Ordinal),
            "IsDirty=" + editorVm.IsDirty + "，窗口标题=" + editorWindow.Title));

        // ---------- E：换封面（640x1600 的 PNG，应该被规范化成 <=1600 的 JPEG） ----------
        var coverResult = await editorVm.ReplaceCoverAsync(MakePng(640, 1600)).ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "E-cover-replaced-in-workspace",
            File.Exists(coverResult.AbsolutePath)
                && coverResult.Width <= CoverService.CoverMaxEdgePx
                && coverResult.Height <= CoverService.CoverMaxEdgePx
                && coverResult.ByteCount <= CoverService.CoverMaxBytes
                && editorVm.HasCoverPreview,
            coverResult.Href + "（" + coverResult.Width + "x" + coverResult.Height + "，"
                + coverResult.ByteCount + " 字节，预览=" + editorVm.HasCoverPreview + "）"));

        // ---------- F：保存 ----------
        var summary = await editorVm.SaveAsync().ConfigureAwait(true);

        var after = await ReadHeadAsync(workspaces, libraryPath, bookId, epubFormat.Id).ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "F-title-written-to-epub",
            string.Equals(after.Title?.Trim(), MarkerTitle, StringComparison.Ordinal),
            "磁盘上的书名=" + (after.Title ?? "(null)") + "；" + summary));

        checks.Add(new SmokeCheck(
            "F-rating-written-to-epub",
            after.Rating == 4,
            "磁盘上的评分=" + (after.Rating?.ToString() ?? "(null)") + "，期望=4"));

        var verified = true;
        var verifyDetail = string.Empty;
        try
        {
            var verification = EpubPackageVerifier.Verify(libraryPath);
            verifyDetail = "条目=" + verification.EntryCount + "，mimetype=" + verification.MimetypeLength + " 字节";
        }
        catch (Exception ex)
        {
            verified = false;
            verifyDetail = ex.GetType().Name + "：" + ex.Message;
        }

        checks.Add(new SmokeCheck("F-package-verifier-passed", verified, verifyDetail));

        checks.Add(new SmokeCheck(
            "F-editor-clean-after-save",
            !editorVm.IsDirty && !editorVm.WindowTitle.EndsWith(" *", StringComparison.Ordinal),
            "IsDirty=" + editorVm.IsDirty + "，窗口标题=" + editorVm.WindowTitle));

        // ---------- G：库里的文件信息与封面缓存要跟上 ----------
        var stored = await repo.GetByIdAsync(bookId).ConfigureAwait(true);
        var storedFormat = stored?.Formats.FirstOrDefault(f => f.Format == BookFormatKind.Epub);
        var diskSize = new FileInfo(libraryPath).Length;

        checks.Add(new SmokeCheck(
            "G-db-file-size-synced",
            storedFormat is not null && storedFormat.FileSize == diskSize,
            "库里记的=" + (storedFormat is null ? "(null)" : storedFormat.FileSize.ToString())
                + "，磁盘上=" + diskSize));

        var thumbAfter = ReadBytes(coverCache.ThumbPath(bookId));
        var thumbAfterLength = thumbAfter?.Length ?? 0;

        checks.Add(new SmokeCheck(
            "G-cover-cache-refreshed",
            thumbAfterLength > 0 && !SameBytes(thumbBefore, thumbAfter),
            "网格缩略图 " + (thumbBefore?.Length ?? 0) + " -> " + thumbAfterLength + " 字节"));

        // ---------- N：保存闭环（T3-6） 还原点 / operation_log / 元数据回写 ----------
        var points = await restorePoints.ListAsync().ConfigureAwait(true);
        var savePoint = points.FirstOrDefault(point =>
            point.Type == RestorePointTypes.Save
            && point.Items.Any(item => string.Equals(item.OriginalPath, libraryPath, StringComparison.OrdinalIgnoreCase)));
        var storedBook = await repo.GetByIdAsync(bookId).ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "N-save-created-restore-point",
            savePoint is not null && Directory.Exists(restorePoints.PointDirectory(savePoint!.Id)),
            "还原点=" + (savePoint?.Id ?? "(没有)") + "；当前共 " + points.Count + " 个"));

        var logs = await operations.ListForBookAsync(bookId).ConfigureAwait(true);
        var saveLog = logs.FirstOrDefault(entry =>
            entry.Operation is OperationNames.Save or OperationNames.SaveForced);

        checks.Add(new SmokeCheck(
            "N-operation-log-written",
            saveLog is not null && saveLog.RestorePointId == savePoint?.Id,
            saveLog is null
                ? "operation_log 里没有这本书的保存记录"
                : saveLog.Operation + "：" + saveLog.Summary));

        // 这一条正是 T3-UI 以来一直挂着的缺口：网格里的书名读的是数据库，不是 OPF
        checks.Add(new SmokeCheck(
            "N-db-title-synced",
            storedBook is not null && string.Equals(storedBook!.Title, MarkerTitle, StringComparison.Ordinal),
            "数据库里的书名=" + (storedBook?.Title ?? "(null)") + "，期望=" + MarkerTitle));

        /* ---------- N2：T5-4 保存前快照（判据 2）----------
           历史里要有"打开时的全量基线"和"保存前"两类快照；保存前那份必须**只含变更文件**
           （条目数明显少于全量基线，不是整本书的副本）。 */
        var snapshotRoot = Path.Combine(editorVm.WorkDir ?? string.Empty, ".snapshots");
        var manifests = Directory.Exists(snapshotRoot)
            ? Directory.GetDirectories(snapshotRoot)
                .Select(directory => Path.Combine(directory, "manifest.json"))
                .Where(File.Exists)
                .Select(File.ReadAllText)
                .ToList()
            : new List<string>();
        var initialManifests = manifests
            .Where(text => text.Contains("\"kind\": \"initial\"", StringComparison.Ordinal))
            .ToList();
        var saveManifests = manifests
            .Where(text => text.Contains("\"kind\": \"save\"", StringComparison.Ordinal))
            .ToList();
        var initialEntryCount = initialManifests
            .Select(text => text.Split("\"relativePath\"").Length - 1)
            .DefaultIfEmpty(0)
            .Max();
        var saveEntryCount = saveManifests
            .Select(text => text.Split("\"relativePath\"").Length - 1)
            .DefaultIfEmpty(0)
            .Max();

        checks.Add(new SmokeCheck(
            "T54-save-snapshot-in-history",
            initialManifests.Count >= 1 && saveManifests.Count >= 1
                && saveEntryCount > 0 && saveEntryCount < initialEntryCount,
            "快照 " + manifests.Count + " 份（全量基线 " + initialManifests.Count + "、保存前 " + saveManifests.Count
                + "）；全量条目 " + initialEntryCount + "、保存前条目 " + saveEntryCount));

        // ---------- O：从还原点还原（判据 2 + 3）----------
        var bytesBeforeRestore = File.ReadAllBytes(libraryPath);
        var manualPoint = await restorePoints
            .CreateAsync(
                RestorePointTypes.Save,
                new[] { new RestorePointRequest(bookId, epubFormat.Id, libraryPath) })
            .ConfigureAwait(true);

        await File.WriteAllTextAsync(libraryPath, "故意改坏的内容", new UTF8Encoding(false)).ConfigureAwait(true);
        var restoreResult = await restorePoints.RestoreAsync(manualPoint.Id).ConfigureAwait(true);
        var restoredBytes = File.ReadAllBytes(libraryPath);

        checks.Add(new SmokeCheck(
            "O-restore-recovers-content",
            restoreResult.Success
                && restoreResult.RestoredCount == 1
                && SameBytes(bytesBeforeRestore, restoredBytes),
            "还原 " + restoreResult.RestoredCount + " 个；失败 " + restoreResult.Failures.Count
                + "；内容一致=" + SameBytes(bytesBeforeRestore, restoredBytes)));

        var safety = string.IsNullOrWhiteSpace(restoreResult.SafetyRestorePointId)
            ? null
            : await restorePoints.GetAsync(restoreResult.SafetyRestorePointId).ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "O-reverse-restore-point-created",
            safety is not null && safety!.Type == RestorePointTypes.Restore,
            "反向还原点=" + (safety?.Id ?? "(没有)") + "，类型=" + (safety?.Type ?? "(null)")));

        // ---------- P：单本上限到了要清理最旧的，而且要留痕（判据 4）----------
        var pruneBook = Guid.NewGuid();
        for (var i = 0; i < 6; i++)
        {
            await restorePoints
                .CreateAsync(
                    RestorePointTypes.Save,
                    new[] { new RestorePointRequest(pruneBook, Guid.NewGuid(), libraryPath) })
                .ConfigureAwait(true);
        }

        var beforePrune = (await restorePoints.ListAsync().ConfigureAwait(true)).Count;
        var pruneReport = await restorePoints.EnforceLimitsAsync(maxPerBook: 5).ConfigureAwait(true);
        var afterPrune = (await restorePoints.ListAsync().ConfigureAwait(true)).Count;

        checks.Add(new SmokeCheck(
            "P-per-book-limit-prunes-oldest",
            pruneReport.RemovedByPerBookLimit == 1
                && pruneReport.RemovedRestorePoints.Count == 1
                && afterPrune == beforePrune - 1,
            "清理 " + pruneReport.RemovedRestorePoints.Count + " 个（单本超限 "
                + pruneReport.RemovedByPerBookLimit + "）；" + beforePrune + " -> " + afterPrune));

        var pruneLogged = (await operations.ListRecentAsync(50).ConfigureAwait(true))
            .Any(entry => entry.Operation == OperationNames.Prune);

        checks.Add(new SmokeCheck(
            "P-prune-logged",
            pruneLogged,
            "operation_log 里有 prune 记录=" + pruneLogged + "；释放 " + pruneReport.FreedBytes + " 字节"));

        // ---------- M：一键修复（被引用但未声明的图片 -> RSC-008 -> 补 manifest 后通过） ----------
        // 手法：复制一张**真实存在且合法**的图到章节旁边，再让章节引用它，但 manifest 不声明。
        // 实测（真 EpubCheck 5.4.0）这种包会报 `RSC-008 Referenced resource ... is not declared`（error）。
        var undeclaredDetail = AddUndeclaredReferencedImage(workDir!);
        var beforeFixSummary = await editorVm.SaveAsync().ConfigureAwait(true);
        var blockedAndFixable = editorVm.HasPendingConfirmation && editorVm.CanAutoFix;

        checks.Add(new SmokeCheck(
            "M-undeclared-blocked-and-fixable",
            !string.IsNullOrEmpty(undeclaredDetail)
                && blockedAndFixable
                && editorVm.ShowValidationPanel,
            "手法=" + undeclaredDetail
                + "；被拦=" + editorVm.HasPendingConfirmation
                + "；修复按钮出现=" + editorVm.CanAutoFix
                + "；提示=" + editorVm.ValidationHeadline));

        var fixSummary = await editorVm.AutoFixAndRetryAsync().ConfigureAwait(true);
        var afterFixHead = await ReadHeadAsync(workspaces, libraryPath, bookId, epubFormat.Id).ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "M-autofix-then-save-passes",
            !editorVm.HasPendingConfirmation
                && !editorVm.ShowValidationPanel
                && !editorVm.IsDirty
                && string.Equals(afterFixHead.Title?.Trim(), MarkerTitle, StringComparison.Ordinal),
            fixSummary));

        // M 那一步保存时自动建了一个还原点，它存的是"干净保存之后、M 乱改之前"的正式区内容。
        // 下面 R 就退回到它T3-7 的独立验收（diff / EpubCheck / Calibre）要的就是这份干净文件。
        var cleanRestorePointId = (await restorePoints.ListAsync().ConfigureAwait(true))
            .FirstOrDefault()?.Id;


        // ---------- J/K/L：EpubCheck 门禁（T3-5） ----------
        // 先把工作区**故意弄坏**：删掉一个 manifest 里声明过的 XHTML，再把另一个写成非法 XML。
        // 两种坏法都是 EpubCheck 必然报错（文件没了 / XML 解析失败），不会退化成 warning，
        // 所以"保存必须被拦下"这件事是确定的，不依赖它对某个错误号的分类。
        var bytesBeforeBlocked = File.ReadAllBytes(libraryPath);
        var stagedPath = libraryPath + EpubSaveService.StagedSuffix;
        var brokeDetail = BreakWorkspace(workDir!);

        var blockedSummary = await editorVm.SaveAsync().ConfigureAwait(true);
        var bytesAfterBlocked = File.ReadAllBytes(libraryPath);

        checks.Add(new SmokeCheck(
            "J-error-epub-blocked",
            !string.IsNullOrEmpty(brokeDetail)
                && editorVm.HasPendingConfirmation
                && editorVm.ShowValidationPanel
                && !string.IsNullOrWhiteSpace(editorVm.ValidationHeadline)
                && File.Exists(stagedPath)
                && SameBytes(bytesBeforeBlocked, bytesAfterBlocked),
            "破坏方式=" + brokeDetail
                + "；被拦=" + editorVm.HasPendingConfirmation
                + "；面板=" + editorVm.ShowValidationPanel
                + "；提示=" + editorVm.ValidationHeadline
                + "；原文件未变=" + SameBytes(bytesBeforeBlocked, bytesAfterBlocked)
                + "；暂存文件在=" + File.Exists(stagedPath)));

        // K：点「取消（不保存）」原文件不动，暂存产物也不许留
        var cancelStatus = editorVm.CancelPendingSave();

        checks.Add(new SmokeCheck(
            "K-cancel-save-keeps-original",
            !editorVm.HasPendingConfirmation
                && !editorVm.ShowValidationPanel
                && !File.Exists(stagedPath)
                && SameBytes(bytesBeforeBlocked, File.ReadAllBytes(libraryPath)),
            cancelStatus + "；暂存文件在=" + File.Exists(stagedPath)));

        // L：再保存一次，然后点「仍然保存（不推荐）」计划的哲学是永远给用户最终决定权
        await editorVm.SaveAsync().ConfigureAwait(true);
        var blockedAgain = editorVm.HasPendingConfirmation;
        var confirmStatus = await editorVm.ConfirmSaveAnywayAsync().ConfigureAwait(true);
        var bytesAfterForce = File.ReadAllBytes(libraryPath);
        var afterForceHead = await ReadHeadAsync(workspaces, libraryPath, bookId, epubFormat.Id).ConfigureAwait(true);

        var forceVerified = true;
        try
        {
            EpubPackageVerifier.Verify(libraryPath);
        }
        catch (Exception ex)
        {
            forceVerified = false;
            confirmStatus += "；自检失败：" + ex.Message;
        }

        checks.Add(new SmokeCheck(
            "L-force-save-writes-through",
            blockedAgain
                && !editorVm.HasPendingConfirmation
                && !editorVm.ShowValidationPanel
                && !SameBytes(bytesBeforeBlocked, bytesAfterForce)
                && forceVerified
                && string.Equals(afterForceHead.Title?.Trim(), MarkerTitle, StringComparison.Ordinal),
            "再次被拦=" + blockedAgain
                + "；磁盘书名=" + (afterForceHead.Title ?? "(null)")
                + "；打包自检通过=" + forceVerified
                + "；面板已收起=" + !editorVm.ShowValidationPanel));

        // ---------- T：书库主题覆盖编辑窗口（用户 2026-10-21） ----------
        // 口径：这是书库「设置 → 外观 → 主题」那套（ui.theme），不是阅读器主题（reader.theme）。
        // 报的问题：深色主题下左栏「章节结构」还是白底，白色文字直接看不见（白底白字）。
        // 这里把三件事钉住：跟主题令牌同色、和右侧文本框同一个底、深色下文字对比度够读。
        var themeLightLayer = ResourceColor("LayerBackgroundBrush");
        var chapterList = editorWindow.ChapterList;
        var chapterListFollowsLight = SameColor(SolidColorOf(chapterList.Background), themeLightLayer)
            && SameColor(SolidColorOf(editorWindow.TitleBox.Background), themeLightLayer);

        await ThemeManager.SetModeAsync(AppThemeMode.Dark).ConfigureAwait(true);
        // DynamicResource 是走失效通知重取的，读得太早会拿到上一套颜色：等到真的换成暗色再断言
        var chapterListDarkOk = await WaitUntilAsync(
            () => SameColor(SolidColorOf(chapterList.Background), ResourceColor("LayerBackgroundBrush")),
            TimeSpan.FromSeconds(5)).ConfigureAwait(true);

        var darkLayer = ResourceColor("LayerBackgroundBrush");
        var darkText = ResourceColor("TextPrimaryBrush");
        var chapterListDarkBg = SolidColorOf(chapterList.Background);
        var chapterListFollowsDark = chapterListDarkOk && !SameColor(darkLayer, themeLightLayer);
        var darkContrast = ContrastRatio(darkLayer, darkText);

        CaptureWindow(editorWindow, Path.Combine(paths.TempDir, "edit-window-theme-dark.png"));

        // 自定义档：用一组"深底浅字 + 橙色强调"的四色，顺便验按钮/输入框边框在自定义档要凸显
        await ThemeManager.SetCustomColorsAsync(
            Color.FromRgb(0x1E, 0x2A, 0x3A),
            Color.FromRgb(0x26, 0x34, 0x46),
            Color.FromRgb(0xEC, 0xF1, 0xF7),
            Color.FromRgb(0xF0, 0x8A, 0x24)).ConfigureAwait(true);
        await ThemeManager.SetModeAsync(AppThemeMode.Custom).ConfigureAwait(true);
        var chapterListCustomOk = await WaitUntilAsync(
            () => SameColor(SolidColorOf(chapterList.Background), ResourceColor("LayerBackgroundBrush")),
            TimeSpan.FromSeconds(5)).ConfigureAwait(true);

        var customLayer = ResourceColor("LayerBackgroundBrush");
        var customBorder = ResourceColor("BorderSubtleBrush");
        var customWindow = ResourceColor("WindowBackgroundBrush");
        var chapterListCustomBg = SolidColorOf(chapterList.Background);
        var customBorderContrast = ContrastRatio(customBorder, customWindow);
        // 深底自定义档的层底色 = 底色与卡片色的中间值（#1E2A3A 与 #263446 -> #222F40），
        // 只要它既不是内置暗色的 #2B2B2B、也不是亮色的 #FFFFFF，就证明用的是用户四色
        var chapterListFollowsCustom = chapterListCustomOk
            && !SameColor(customLayer, darkLayer)
            && !SameColor(customLayer, themeLightLayer);

        CaptureWindow(editorWindow, Path.Combine(paths.TempDir, "edit-window-theme-custom.png"));

        await ThemeManager.SetModeAsync(AppThemeMode.System).ConfigureAwait(true);
        await PumpAsync();

        checks.Add(new SmokeCheck(
            "T-chapter-list-follows-app-theme",
            chapterListFollowsLight
                && chapterListFollowsDark
                && chapterListFollowsCustom
                && darkContrast >= 4.5,
            "亮色时列表底=右栏输入框底=" + Hex(themeLightLayer)
                + "；暗色时=" + Hex(chapterListDarkBg)
                + "（期望 " + Hex(darkLayer) + "，底色/文字对比度 "
                + darkContrast.ToString("0.0", CultureInfo.InvariantCulture) + ":1）"
                + "；自定义时=" + Hex(chapterListCustomBg)
                + "（层底色 " + Hex(customLayer) + "，既非内置暗色 " + Hex(darkLayer)
                + " 也非亮色 " + Hex(themeLightLayer) + " = 用的是用户四色）"));

        checks.Add(new SmokeCheck(
            "T-custom-border-stands-out",
            customBorderContrast >= 1.5,
            "自定义档描边=" + Hex(customBorder) + "，窗口底=" + Hex(customWindow)
                + "，对比度 " + customBorderContrast.ToString("0.00", CultureInfo.InvariantCulture) + ":1（≥1.5 = 按钮/输入框有轮廓）"));

        // ---------- H：关窗清理工作区 ----------
        editorWindow.Close();
        // 两个条件都要等：工作区目录没了，且窗口的 Closed 已经跑完（EditCommand 把它从列表里摘掉）。
        // 只等前者会有竞态：目录先删掉，Closed 还在消息队列里排着。
        var cleaned = await WaitUntilAsync(
            () => (workDir is null || !Directory.Exists(workDir)) && edit.OpenWindowCount == 0,
            TimeSpan.FromSeconds(15)).ConfigureAwait(true);

        var stillOpen = Application.Current.Windows
            .OfType<BookEditorWindow>()
            .Count(w => w.BookId == bookId);

        // ---------- R：把"强制保存"那份破坏用还原点退回去（T3-7 验收要用干净的书）----------
        // 强制保存是**用户明确选择**的（L 场景），它会把坏内容写进正式区；
        // 这一场景证明"选错了还能退回来"：拿最新的还原点还原，正式区就回到强制保存之前那份干净版本。
        var damagedHash = await RestorePointService.HashAsync(libraryPath).ConfigureAwait(true);
        var rolledBack = cleanRestorePointId is null
            ? null
            : await restorePoints.RestoreAsync(cleanRestorePointId).ConfigureAwait(true);
        var cleanHash = await RestorePointService.HashAsync(libraryPath).ConfigureAwait(true);
        var changedBack = !string.Equals(damagedHash, cleanHash, StringComparison.OrdinalIgnoreCase);

        checks.Add(new SmokeCheck(
            "R-restore-undoes-forced-save",
            rolledBack is not null && rolledBack.Success && changedBack,
            "退回到 " + (cleanRestorePointId ?? "(没有)")
                + "；还原前 " + damagedHash[..Math.Min(19, damagedHash.Length)]
                + " -> 还原后 " + cleanHash[..Math.Min(19, cleanHash.Length)]
                + "；内容已换回干净版本=" + changedBack));


        checks.Add(new SmokeCheck(
            "H-close-clears-workspace",
            cleaned && !Directory.Exists(workDir ?? string.Empty) && edit.OpenWindowCount == 0,
            "工作区还在=" + Directory.Exists(workDir ?? string.Empty)
                + "，OpenWindowCount=" + edit.OpenWindowCount
                + "，应用窗口里的编辑器=" + stillOpen
                + "，窗口 IsVisible=" + editorWindow.IsVisible
                + "，DispatcherShutdown=" + editorWindow.Dispatcher.HasShutdownStarted));

        // ---------- I：源文件一个字节都没动 ----------
        checks.Add(new SmokeCheck(
            "I-source-file-untouched",
            new FileInfo(sourceFile).Length == sourceLength
                && File.GetLastWriteTimeUtc(sourceFile) == sourceStamp,
            "源文件=" + Path.GetFileName(sourceFile)));

        // ---------- Q：章节结构（T5-1 的 7 条判据）----------
        // 这一段换一个**干净的新工作区**来做：前面的 M/J/K/L 是故意把工作区改坏的。
        // 结构操作只写工作区；"保存出来的 EPUB 过不过 EpubCheck"由真工具集成测试
        // （EpubStructureIntegrationTests）覆盖，这里验会话内行为与撤销。
        var qWorkspace = await workspaces
            .OpenAsync(
                new WorkspaceOpenRequest
                {
                    EpubPath = libraryPath,
                    BookId = bookId,
                    FormatId = epubFormat.Id,
                })
            .ConfigureAwait(true);

        var tree = new ChapterTreeViewModel(chapters);
        tree.Attach(qWorkspace);

        var originalCount = tree.Count;

        checks.Add(new SmokeCheck(
            "Q1-chapter-tree-loaded",
            originalCount >= 2
                && tree.Items.All(item => !string.IsNullOrWhiteSpace(item.Title))
                && tree.Items.All(item => item.Exists),
            "章节数=" + originalCount + "；第一章=" + (tree.Items.FirstOrDefault()?.Title ?? "(无)")
                + "；标题退回文件名的章数=" + tree.Items.Count(item => item.TitleFromFileName)));

        // 判据 2：重命名改的是目录文字，不动文件名
        const string chapterMarker = "冒烟改过的第一章";
        var firstHref = tree.Items[0].Href;
        tree.Selected = tree.Items[0];
        tree.NewTitle = chapterMarker;
        await tree.RenameAsync().ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "Q2-rename-updates-toc",
            TocContains(qWorkspace.WorkDir, chapterMarker) && File.Exists(PathOf(qWorkspace.WorkDir, firstHref)),
            "目录里出现新标题=" + TocContains(qWorkspace.WorkDir, chapterMarker)
                + "；原文件仍在=" + File.Exists(PathOf(qWorkspace.WorkDir, firstHref))));

        // 判据 1 续：目录层级可编辑（升一级 / 降一级）只改目录结构，不动正文与文件名
        var tocItems = tree.Items.Where(item => item.TitleFromToc).ToList();
        var levelTarget = tocItems.Count >= 2 ? tocItems[^1] : tocItems.FirstOrDefault();

        if (levelTarget is null)
        {
            checks.Add(new SmokeCheck("Q8-demote-chapter-level", false, "这本书的目录里一个条目都没有"));
            checks.Add(new SmokeCheck("Q9-promote-chapter-level", false, "这本书的目录里一个条目都没有"));
        }
        else
        {
            var levelHref = levelTarget.Href;
            var levelBefore = levelTarget.Level;

            tree.Selected = levelTarget;
            await tree.DemoteAsync().ConfigureAwait(true);
            var afterDemote = tree.Items.First(item =>
                string.Equals(item.Href, levelHref, StringComparison.OrdinalIgnoreCase)).Level;

            checks.Add(new SmokeCheck(
                "Q8-demote-chapter-level",
                afterDemote == levelBefore + 1,
                "目录层级 " + levelBefore + " -> " + afterDemote + "（" + levelHref + "）"));

            await tree.PromoteAsync().ConfigureAwait(true);
            var afterPromote = tree.Items.First(item =>
                string.Equals(item.Href, levelHref, StringComparison.OrdinalIgnoreCase)).Level;

            checks.Add(new SmokeCheck(
                "Q9-promote-chapter-level",
                afterPromote == levelBefore,
                "目录层级 " + afterDemote + " -> " + afterPromote + "（期望回到 " + levelBefore + "）"));
        }

        // 判据 4：上下移动改 spine 顺序
        var spineBefore = ReadSpineIds(qWorkspace.WorkDir);
        tree.Selected = tree.Items[0];
        await tree.MoveDownAsync().ConfigureAwait(true);
        var spineAfter = ReadSpineIds(qWorkspace.WorkDir);

        checks.Add(new SmokeCheck(
            "Q3-move-changes-spine-order",
            spineAfter.Count == spineBefore.Count
                && spineAfter[0] == spineBefore[1]
                && spineAfter[1] == spineBefore[0],
            "spine " + string.Join(",", spineBefore.Take(3)) + " -> " + string.Join(",", spineAfter.Take(3))));

        // 判据 3：删除 = spine + manifest + 目录 + 文件（文件先进暂存区）
        tree.Selected = tree.Items[1];
        var removedHref = tree.Items[1].Href;
        await tree.DeleteAsync().ConfigureAwait(true);
        var stagingPath = Path.Combine(
            qWorkspace.WorkDir, EpubStructureEditor.DeletedDirectoryName,
            removedHref.Replace('/', Path.DirectorySeparatorChar));

        checks.Add(new SmokeCheck(
            "Q4-delete-syncs-spine-manifest-toc",
            !File.Exists(PathOf(qWorkspace.WorkDir, removedHref))
                && File.Exists(stagingPath)
                && ReadSpineIds(qWorkspace.WorkDir).Count == spineAfter.Count - 1
                && !TocContains(qWorkspace.WorkDir, Path.GetFileName(removedHref)),
            "原路径已消失=" + !File.Exists(PathOf(qWorkspace.WorkDir, removedHref))
                + "；文件名=" + Path.GetFileName(removedHref)
                + "；目录仍含该文件名=" + TocContains(qWorkspace.WorkDir, Path.GetFileName(removedHref))
                + "；命中文件=" + FilesContaining(qWorkspace.WorkDir, Path.GetFileName(removedHref))
                + "；残留行=" + MatchingLines(qWorkspace.WorkDir, Path.GetFileName(removedHref))
                + "；章节标题=" + (tree.Items.Count > 1 ? tree.Items[1].Title : "(无)")
                + "；暂存区里有=" + File.Exists(stagingPath)
                + "；spine " + spineAfter.Count + " -> " + ReadSpineIds(qWorkspace.WorkDir).Count));

        // 判据 5：新增空章节
        var beforeAdd = tree.Count;
        tree.Selected = tree.Items[0];
        tree.NewTitle = "冒烟新增的一章";
        await tree.AddAsync().ConfigureAwait(true);
        var added = tree.Items.FirstOrDefault(item => item.Title == "冒烟新增的一章");

        checks.Add(new SmokeCheck(
            "Q5-add-empty-chapter",
            tree.Count == beforeAdd + 1
                && added is not null
                && File.Exists(PathOf(qWorkspace.WorkDir, added!.Href))
                && ReadSpineIds(qWorkspace.WorkDir).Count == tree.Count,
            "章节数 " + beforeAdd + " -> " + tree.Count + "；新章=" + (added?.Href ?? "(没有)")));

        // 判据 6：合并两章（内容拼接、不重复 html/body）
        tree.Selected = tree.Items[0];
        var mergeTarget = tree.Items[0].Href;
        var mergeSource = tree.Items[1].Href;
        await tree.MergeAsync().ConfigureAwait(true);

        var mergedText = File.Exists(PathOf(qWorkspace.WorkDir, mergeTarget))
            ? File.ReadAllText(PathOf(qWorkspace.WorkDir, mergeTarget))
            : string.Empty;

        checks.Add(new SmokeCheck(
            "Q6-merge-chapters",
            CountOf(mergedText, "<body") == 1
                && CountOf(mergedText, "<html") == 1
                && !File.Exists(PathOf(qWorkspace.WorkDir, mergeSource))
                && ReadSpineIds(qWorkspace.WorkDir).Count == tree.Count,
            "合并后 body 数=" + CountOf(mergedText, "<body") + "；被合并章已移走="
                + !File.Exists(PathOf(qWorkspace.WorkDir, mergeSource))));

        // 拆分章节：按拆分点把一章切成两章（前半留原文件，后半写成新章）
        var splitIndex = -1;

        for (var i = 0; i < tree.Items.Count && splitIndex < 0; i++)
        {
            tree.Selected = tree.Items[i];

            if (tree.SplitPoints.Count > 0)
            {
                splitIndex = i;
            }
        }

        if (splitIndex < 0)
        {
            checks.Add(new SmokeCheck("Q10-split-chapter", false, "这本书里找不到可拆分的章节"));
        }
        else
        {
            var beforeSplit = tree.Count;
            var targetHref = tree.Items[splitIndex].Href;

            tree.Selected = tree.Items[splitIndex];
            tree.SelectedSplitPoint = tree.SplitPoints[^1];
            await tree.SplitAsync().ConfigureAwait(true);

            var target = tree.Items.First(item =>
                string.Equals(item.Href, targetHref, StringComparison.OrdinalIgnoreCase));
            var addedHref = tree.Items[target.Index + 1].Href;
            var addedPath = PathOf(tree.WorkDir!, addedHref);
            var tail = File.Exists(addedPath) ? File.ReadAllText(addedPath) : string.Empty;

            checks.Add(new SmokeCheck(
                "Q10-split-chapter",
                tree.Count == beforeSplit + 1
                    && tail.Length > 0
                    && CountOf(tail, "<body") == 1
                    && ReadSpineIds(tree.WorkDir!).Count == tree.Count,
                "章节数 " + beforeSplit + " -> " + tree.Count + "；新章=" + addedHref));
        }

        // ---- S 段：章节源码编辑器（T5-2）----
        // S1：点「编辑源码」按钮 -> 源码窗口打开、源码载入、语法高亮是 XML（判据 1）
        tree.Selected = tree.Items[0];

        // 注意：跑到这里时编辑器窗口已经关了（H 段关的），所以按钮点不到，
        // 这里直接构造源码窗口；入口按钮「编辑源码」的接线由静态核对 + 人工验收覆盖。
        ChapterSourceWindow? sourceWindow = new ChapterSourceWindow(
            source,
            qWorkspace,
            tree.Items.Select(item => new SourceChapter(item.Index, item.Href, item.Title)).ToList(),
            0)
        {
            Owner = window,
        };

        sourceWindow!.Show();
        await PumpAsync();

        var sourceText = sourceWindow?.Editor.Text ?? string.Empty;
        var highlightName = sourceWindow?.Editor.SyntaxHighlighting?.Name ?? "(无)";

        checks.Add(new SmokeCheck(
            "S1-source-window-loads-with-highlight",
            sourceWindow is not null
                && sourceText.Length > 0
                && sourceText.Contains("<html", StringComparison.OrdinalIgnoreCase)
                && highlightName == "XML",
            "源码长度=" + sourceText.Length
                + "；语法高亮=" + highlightName));

        if (sourceWindow is null)
        {
            checks.Add(new SmokeCheck("S2-source-save-writes-workspace", false, "源码窗口没打开，后面的检查没法做"));
            checks.Add(new SmokeCheck("S3-unclosed-br-blocked", false, "源码窗口没打开"));
            checks.Add(new SmokeCheck("S4-bare-amp-blocked", false, "源码窗口没打开"));
            checks.Add(new SmokeCheck("S5-large-source-loads-fast", false, "源码窗口没打开"));
            checks.Add(new SmokeCheck("S6-unsaved-switch-prompts", false, "源码窗口没打开"));
        }
        else
        {
            var sourceHref = sourceWindow.CurrentChapter.Href;
            var workspace = qWorkspace;
            var marker = "源码视图冒烟标记";
            var original = sourceWindow.Editor.Text;

            // S2：编辑后保存 -> 写进工作区（判据 2 的前半；EpubCheck 那一半在集成测试里）
            var edited = original.Replace(
                "</body>", "<p>" + marker + "</p></body>", StringComparison.OrdinalIgnoreCase);
            sourceWindow.Editor.Text = edited;
            var saved = sourceWindow.Save();
            var onDisk = File.Exists(PathOf(workspace.WorkDir, sourceHref))
                ? File.ReadAllText(PathOf(workspace.WorkDir, sourceHref))
                : string.Empty;

            checks.Add(new SmokeCheck(
                "S2-source-save-writes-workspace",
                edited != original && saved && onDisk.Contains(marker, StringComparison.Ordinal),
                "保存返回=" + saved + "；工作区文件里有标记=" + onDisk.Contains(marker, StringComparison.Ordinal)));

            var diskBefore = onDisk;

            // S3：故意写不闭合的 <br> -> 被拦、光标定位到错行、提示人话（判据 3）
            sourceWindow.Editor.Text = edited.Replace(
                "<p>" + marker + "</p>", "<p>第一行<br>第二行</p>", StringComparison.Ordinal);
            var blocked = !sourceWindow.Save();
            var caretLine = sourceWindow.Editor.TextArea.Caret.Line;
            var statusText = (sourceWindow.FindName("StatusText") as TextBlock)?.Text ?? string.Empty;
            var diskAfterBlock = File.ReadAllText(PathOf(workspace.WorkDir, sourceHref));

            checks.Add(new SmokeCheck(
                "S3-unclosed-br-blocked",
                blocked
                    && caretLine > 0
                    && statusText.Contains("<br/>", StringComparison.Ordinal)
                    && diskAfterBlock == diskBefore,
                "被拦=" + blocked + "；光标行=" + caretLine + "；提示=" + statusText));

            // S4：裸 & -> 被拦 + 提示转义（判据 4）
            sourceWindow.Editor.Text = edited.Replace(
                "<p>" + marker + "</p>", "<p>甲 & 乙</p>", StringComparison.Ordinal);
            var blockedAmp = !sourceWindow.Save();
            var ampStatus = (sourceWindow.FindName("StatusText") as TextBlock)?.Text ?? string.Empty;

            checks.Add(new SmokeCheck(
                "S4-bare-amp-blocked",
                blockedAmp && ampStatus.Contains("&amp;", StringComparison.Ordinal),
                "被拦=" + blockedAmp + "；提示=" + ampStatus));

            // S5：1.2MB 的章节载入不卡（判据 5）
            // 真实的大章节是很多段落，不是一整行 1.2MB（一整行会让排版退化成极端情况）
            var hugeBody = new System.Text.StringBuilder();

            for (var i = 0; i < 12_000; i++)
            {
                hugeBody.Append("<p>第 ").Append(i).Append(" 段：")
                    .Append(new string('字', 40)).Append("</p>\n");
            }

            var huge = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
                + "<html xmlns=\"http://www.w3.org/1999/xhtml\">\n<head><title>大章</title></head>\n<body>\n"
                + hugeBody
                + "</body>\n</html>";
            var watch = System.Diagnostics.Stopwatch.StartNew();
            sourceWindow.Editor.Text = huge;
            sourceWindow.Editor.UpdateLayout();
            watch.Stop();

            checks.Add(new SmokeCheck(
                "S5-large-source-loads-fast",
                sourceWindow.Editor.Text.Length == huge.Length && watch.ElapsedMilliseconds < 3000,
                "1.2MB 载入 " + watch.ElapsedMilliseconds + " 毫秒（阈值 3000）"));

            // S6：有未保存修改时切章 -> 先出内联确认面板，点「放弃修改」才切过去（判据 6）
            sourceWindow.Editor.Text = huge + " ";
            var hrefBefore = sourceWindow.CurrentChapter.Href;
            var combo = sourceWindow.FindName("ChapterBox") as ComboBox;

            if (combo is not null)
            {
                combo.SelectedIndex = combo.SelectedIndex == 0 ? 1 : 0;
            }

            await PumpAsync();

            var prompted = sourceWindow.DirtyPromptVisible
                && string.Equals(sourceWindow.CurrentChapter.Href, hrefBefore, StringComparison.OrdinalIgnoreCase);

            var dropButton = sourceWindow.FindName("DirtyDropButton") as Button;
            dropButton?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await PumpAsync();

            var switched = !string.Equals(sourceWindow.CurrentChapter.Href, hrefBefore, StringComparison.OrdinalIgnoreCase);

            checks.Add(new SmokeCheck(
                "S6-unsaved-switch-prompts",
                prompted && switched,
                "先弹确认=" + prompted + "；点放弃后切过去了=" + switched));

            sourceWindow.Close();
            await PumpAsync();
        }

        // ---- V 段：可视编辑（T5-2W）----
        // 前端交互（工具条 / 表格 / 图片 / 自动修正真弹窗）需要人工看，这里验 C# 侧的保存与自动修正链路。
        // 用一份**独立工作区**，免得这里的改动影响后面的检查。
        var visualWorkspace = await workspaces
            .OpenAsync(new WorkspaceOpenRequest { EpubPath = libraryPath, BookId = bookId, FormatId = epubFormat.Id })
            .ConfigureAwait(true);

        try
        {
            var visualHref = tree.Items[0].Href;
            var visualPath = PathOf(visualWorkspace.WorkDir, visualHref);
            var visualBefore = File.ReadAllText(visualPath);

            // 前端序列化出来的 HTML 通常不闭合（<br>、&nbsp;），应当先"给修正建议、不写盘"
            var askFix = source.SaveVisual(
                visualWorkspace,
                visualHref,
                "<div class=\"keeptext\"><p>可视冒烟<br>正文&nbsp;甲</p>",
                applyFixes: false);

            var afterAsk = File.ReadAllText(visualPath);

            checks.Add(new SmokeCheck(
                "V1-visual-save-asks-for-fix",
                !askFix.Ok && askFix.NeedsFix && askFix.Fixes.Count >= 3 && afterAsk == visualBefore,
                "需要修正=" + askFix.NeedsFix + "；建议条数=" + askFix.Fixes.Count
                    + "；原文件未变=" + (afterAsk == visualBefore)));

            var applied = source.SaveVisual(visualWorkspace, visualHref, "<p>可视模式冒烟标记</p>", applyFixes: true);
            var afterApply = File.ReadAllText(visualPath);

            checks.Add(new SmokeCheck(
                "V2-visual-save-applies-fixes-and-writes",
                applied.Ok && afterApply.Contains("可视模式冒烟标记", StringComparison.Ordinal)
                    && afterApply.Contains("<html", StringComparison.OrdinalIgnoreCase)
                    && afterApply.Contains("template.css", StringComparison.OrdinalIgnoreCase) == visualBefore.Contains("template.css", StringComparison.OrdinalIgnoreCase),
                "保存=" + applied.Ok + "；消息=" + applied.Message));

            // ---- V6：自动修正链路（判据 12）：先建还原点（备份修正前的原文）-> 应用 -> 写工作区 -> 记 operation_log ----
            // 用"能修但还没修"的一段正文：<br> 没自闭合、&nbsp; 是命名实体、div/p 没闭合
            var fixBody = "<div><p>自动修正冒烟<br>正文&nbsp;乙</p>";
            var scanForFix = source.SaveVisual(visualWorkspace, visualHref, fixBody, applyFixes: false);
            var beforeFix = File.ReadAllText(visualPath);
            var logsBefore = await operations.ListForBookAsync(bookId).ConfigureAwait(true);
            var logsBeforeCount = logsBefore.Count(e => e.Operation == OperationNames.AutoFix);

            var outcome = await autofix
                .ApplyAsync(visualWorkspace, visualHref, fixBody)
                .ConfigureAwait(true);

            var afterFix = File.ReadAllText(visualPath);
            var point = outcome.RestorePointId is null
                ? null
                : await restorePoints.GetAsync(outcome.RestorePointId).ConfigureAwait(true);
            var backupText = point is null
                ? string.Empty
                : File.ReadAllText(Path.Combine(restorePoints.PointDirectory(point.Id), point.Items[0].BackupFile));
            var logsAfter = await operations.ListForBookAsync(bookId).ConfigureAwait(true);
            var autofixLog = logsAfter.FirstOrDefault(e => e.Operation == OperationNames.AutoFix);

            checks.Add(new SmokeCheck(
                "V6-autofix-backup-then-write-and-log",
                scanForFix.NeedsFix
                    && scanForFix.Fixes.Count > 0
                    && outcome.Ok
                    && outcome.Applied
                    && afterFix != beforeFix
                    && afterFix.Contains("自动修正冒烟", StringComparison.Ordinal)
                    && point is not null
                    && point.Type == RestorePointTypes.AutoFix
                    && point.Items.Count == 1
                    && string.Equals(backupText, beforeFix, StringComparison.Ordinal)
                    && autofixLog is not null
                    && autofixLog.RestorePointId == point.Id
                    && logsAfter.Count(e => e.Operation == OperationNames.AutoFix) == logsBeforeCount + 1,
                "需要修正=" + scanForFix.NeedsFix + "；建议=" + scanForFix.Fixes.Count
                    + "；修正并落盘=" + outcome.Ok
                    + "；还原点=" + (point?.Id ?? "(无)")
                    + "；还原点里备份的正是修正前原文=" + string.Equals(backupText, beforeFix, StringComparison.Ordinal)
                    + "；operation_log(autofix)=" + (autofixLog is null ? "没写" : autofixLog.Summary)
                    + "；原文变了=" + (afterFix != beforeFix)));

            // ---- V7：自动修正确认弹窗（真弹窗本身是人工验收项，这里只核"逐条列出来 + 按钮文案"）----
            var dialogFixes = new[]
            {
                new SourceFix("void-tag", 12, "<br>", "<br/>", "空标签没自闭合"),
                new SourceFix("named-entity", 12, "&nbsp;", "&#160;", "命名实体改成数字实体"),
            };

            var confirmDialog = new ConfirmAutoFixDialog(dialogFixes);
            var dialogItems = confirmDialog.FixList.Items.Count;
            var dialogText = string.Join(" | ", confirmDialog.FixList.Items.Cast<string>());
            var confirmText = confirmDialog.ConfirmButton.Content as string ?? string.Empty;
            var dialogMessage = confirmDialog.MessageText.Text;
            confirmDialog.Close();

            checks.Add(new SmokeCheck(
                "V7-autofix-dialog-lists-every-fix",
                dialogItems == dialogFixes.Length
                    && confirmText == "确认修改"
                    && dialogMessage.Contains("2", StringComparison.Ordinal)
                    && dialogText.Contains("第 12 行", StringComparison.Ordinal)
                    && dialogText.Contains("空标签没自闭合", StringComparison.Ordinal)
                    && dialogText.Contains("命名实体改成数字实体", StringComparison.Ordinal),
                "条目数=" + dialogItems + "；按钮=" + confirmText
                    + "；正文=" + dialogMessage + "；条目=" + dialogText));
        }
        finally
        {
            await visualWorkspace.DiscardAsync().ConfigureAwait(true);
        }

        // ---- V3：真的打开可视窗口，用脚本点按钮、读回数据（自己验证，不靠人工点击）----
        // 只有"选择图片文件"那种系统弹窗无法自动点，编辑器本身完全可以脚本驱动。
        var probeWorkspace = await workspaces
            .OpenAsync(new WorkspaceOpenRequest { EpubPath = libraryPath, BookId = bookId, FormatId = epubFormat.Id })
            .ConfigureAwait(true);

        try
        {
            var probeChapters = tree.Items
                .Select(item => new SourceChapter(item.Index, item.Href, item.Title))
                .ToList();

            var probeWindow = new ChapterVisualEditorWindow(
                source, autofix, environments, assets, probeWorkspace, probeChapters, 0)
            {
                Owner = window,
            };

            probeWindow.Show();
            await WaitUntilAsync(() => probeWindow.Core is not null, TimeSpan.FromSeconds(25));
            await Task.Delay(4000);

            // V4：干净状态下点「源码视图」应当打开源码窗口（模式切换链路）
            var sourceWindowsBefore = Application.Current.Windows.OfType<ChapterSourceWindow>().Count();

            await probeWindow.RunScriptAsync("document.getElementById('btnSourceMode').click()");

            var sourceAppeared = await WaitUntilAsync(
                () => Application.Current.Windows.OfType<ChapterSourceWindow>().Count() > sourceWindowsBefore,
                TimeSpan.FromSeconds(15));

            var openedSource = Application.Current.Windows.OfType<ChapterSourceWindow>().FirstOrDefault();

            checks.Add(new SmokeCheck(
                "V4-source-view-opens",
                sourceAppeared && openedSource is not null && openedSource.ChapterCount > 0,
                "源码窗口出现=" + sourceAppeared + "；章节数=" + (openedSource?.ChapterCount ?? 0)));

            openedSource?.Close();
            await PumpAsync();

            var probe = await probeWindow.RunScriptAsync(PROBE_SCRIPT);
            var probeFocus = await probeWindow.RunScriptAsync(PROBE_FOCUS_SCRIPT);
            probe = probe + " || FOCUS=" + probeFocus;
            var probe2 = await probeWindow.RunScriptAsync(PROBE2_SCRIPT);
            var imagesBefore = await probeWindow.RunScriptAsync("document.images.length");

            // 端到端：拿工作区里一张真实图片，走"宿主复制进工作区 + 回推"整条链路（只有文件选择框是人工项）。
            // 扩展名要放宽：真实书里 .jpeg/.png 很常见，以前只找 *.jpg/*.gif，碰到"只有 .jpeg 的书"
            // 会直接抛 Sequence contains no elements，把整轮冒烟中断、报告都写不出来（2026-10-09 实测）。
            var realImage = new[] { "*.jpg", "*.jpeg", "*.gif", "*.png" }
                .SelectMany(pattern => Directory.EnumerateFiles(
                    probeWorkspace.WorkDir, pattern, SearchOption.AllDirectories))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            var imagesAfter = "n/a";
            var lastWidth = "(没有可用图片)";

            if (realImage is not null)
            {
                probeWindow.InsertImageFile(realImage, false);
                await Task.Delay(1500);

                imagesAfter = await probeWindow.RunScriptAsync("document.images.length");
                lastWidth = await probeWindow.RunScriptAsync(
                    "JSON.stringify({ w: document.images[document.images.length - 1].naturalWidth,"
                    + " src: String(document.images[document.images.length - 1].src),"
                    + " ok: document.images[document.images.length - 1].complete })");
            }

            probe = probe + " || P2=" + probe2 + " | imgCount " + imagesBefore + "->" + imagesAfter
                + " lastNaturalWidth=" + lastWidth;

            checks.Add(new SmokeCheck(
                "V9-insert-real-image",
                realImage is not null
                    && !lastWidth.Contains("\"w\":0,", StringComparison.Ordinal)
                    && imagesAfter != imagesBefore,
                "工作区里选的图=" + (realImage is null ? "(没有 *.jpg/.jpeg/.gif/.png)" : Path.GetFileName(realImage))
                    + "；图片个数 " + imagesBefore + "->" + imagesAfter
                    + "；最后一张=" + lastWidth));

            checks.Add(new SmokeCheck(
                "V5-visual-dirty-tracked",
                probeWindow.HasUnsavedChanges,
                "刚改过内容后 HasUnsavedChanges=" + probeWindow.HasUnsavedChanges
                    + "（关窗/切模式的确认弹窗就靠它）"));
            // ---- V10：判据 2 / 3 逐项验证：段落样式 / 列表 / 对齐 / 缩进 / 相对字号 / 合并 / 拆分 ----
            var probe3 = await probeWindow.RunScriptAsync(PROBE3_SCRIPT);
            var probe3Fields = new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                using var outerDoc = JsonDocument.Parse(probe3);
                using var innerDoc = JsonDocument.Parse(outerDoc.RootElement.GetString() ?? "{}");

                foreach (var property in innerDoc.RootElement.EnumerateObject())
                {
                    probe3Fields[property.Name] = property.Value.ToString();
                }
            }
            catch (JsonException ex)
            {
                probe3Fields["解析失败"] = ex.Message;
            }

            string Field(string name) => probe3Fields.TryGetValue(name, out var value) ? value : "(缺)";

            int Number(string name) => int.TryParse(Field(name), out var value) ? value : -1;

            checks.Add(new SmokeCheck(
                "V10-visual-editor-commands",
                (Field("err") == string.Empty || Field("err") == "(缺)")
                    && Field("h2") == "True"
                    && Field("h2Text") == "段落样式冒烟"
                    && Field("list") == "True"
                    && Number("alignAfter") > Number("alignBefore")
                    && (Number("blockquotes") > 0 || Field("indentAfter") != Field("indentBefore"))
                    && Field("sizeStyle") == "150%"
                    && Field("sizeRelative") == "True"
                    && Number("mergeBefore") == 3
                    && Number("mergeAfter") == 2
                    && Field("mergeColspan") == "2"
                    && Number("splitAfter") == 3
                    && Field("splitColspan") == "1",
                probe3));

            // ---- V11：有未保存修改时**关窗**必须先弹真确认框（判据 8）----
            // 模态弹窗会卡住自动化（本仓库踩过），所以用 DispatcherTimer 在弹窗上点【取消】：
            // 既验证了"真的弹了"，又让冒烟继续往下跑。
            var closeDialogSeen = false;
            var closeDialogCancelled = false;
            var dialogPump = new DispatcherTimer(
                TimeSpan.FromMilliseconds(200),
                DispatcherPriority.Normal,
                (_, _) =>
                {
                    var dialog = Application.Current.Windows.OfType<ConfirmSaveDialog>().FirstOrDefault();

                    if (dialog is null)
                    {
                        return;
                    }

                    closeDialogSeen = true;
                    dialog.Close();          // 取消：DialogResult 保持 null -> Choice=Cancel -> 关窗被拦下
                    closeDialogCancelled = true;
                },
                Dispatcher.CurrentDispatcher);

            dialogPump.Start();
            probeWindow.Close();
            dialogPump.Stop();

            checks.Add(new SmokeCheck(
                "V11-close-asks-when-dirty",
                closeDialogSeen && closeDialogCancelled && probeWindow.IsVisible,
                "弹了确认框=" + closeDialogSeen + "；点了取消=" + closeDialogCancelled
                    + "；窗口还开着=" + probeWindow.IsVisible));
            // ---- V8：落盘成功后未保存标记必须归零（否则"是 = 保存后退出"那条分支永远走不到）----
            // 用一段**先扫过确认不需要修正**的正文，避免弹出真弹窗把自动化卡住（弹窗本身是人工验收项）。
            var cleanBody = "<p>可视保存冒烟</p>";
            var cleanScan = source.SaveVisual(
                probeWorkspace, probeWindow.CurrentChapter.Href, cleanBody, applyFixes: false);
            var dirtyBeforeSave = probeWindow.HasUnsavedChanges;
            var savedOk = false;

            if (cleanScan.Ok)
            {
                savedOk = await probeWindow.OnSaveAsync(cleanBody).ConfigureAwait(true);
            }

            var savedChapterFile = PathOf(probeWorkspace.WorkDir, probeWindow.CurrentChapter.Href);
            var markerOnDisk = File.Exists(savedChapterFile)
                && File.ReadAllText(savedChapterFile).Contains("可视保存冒烟", StringComparison.Ordinal);

            checks.Add(new SmokeCheck(
                "V8-visual-save-clears-unsaved-flag",
                cleanScan.Ok
                    && dirtyBeforeSave
                    && savedOk
                    && !probeWindow.HasUnsavedChanges
                    && markerOnDisk,
                "保存前有未保存修改=" + dirtyBeforeSave
                    + "；这段正文需要修正=" + !cleanScan.Ok
                    + "；保存成功=" + savedOk
                    + "；保存后未保存标记=" + probeWindow.HasUnsavedChanges
                    + "；工作区里有标记=" + markerOnDisk));

            // ---- V12：1 MB 章节在**可视模式**下"重新载入 / 切模式 / 保存"都不卡（T5-2W 判据 13 后半段）----
            /* S5 只验了源码视图（WPF TextBox）；这条补可视模式（WebView2 + editor.js）：
               1) 把工作区里这一章换成 1.2MB 正文并重新载入，量到 WebView2 真排版出 12000 段；
               2) 切到源码模式（开源码窗口）并等它把 1.2MB 装进 TextBox；
               3) 把 1.2MB 正文保存回工作区。
               三段都用与 S5 相同的 3000ms 口径，并把原始毫秒数写进读数。 */
            var bigBody = new System.Text.StringBuilder();

            for (var bi = 0; bi < 12_000; bi++)
            {
                bigBody.Append("<p>第 ").Append(bi).Append(" 段：")
                    .Append(new string('字', 40)).Append("</p>\n");
            }

            var bigBodyText = bigBody.ToString();
            var bigChapter = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
                + "<html xmlns=\"http://www.w3.org/1999/xhtml\">\n<head><title>大章</title></head>\n<body>\n"
                + bigBodyText + "</body>\n</html>";
            var bigChapterFile = PathOf(probeWorkspace.WorkDir, probeWindow.CurrentChapter.Href);
            var bigOriginal = File.Exists(bigChapterFile) ? File.ReadAllText(bigChapterFile) : string.Empty;

            File.WriteAllText(bigChapterFile, bigChapter);

            var bigRenderWatch = System.Diagnostics.Stopwatch.StartNew();
            probeWindow.ReloadChapterForTest();

            var bigRenderedCount = 0;
            var bigRenderWait = System.Diagnostics.Stopwatch.StartNew();

            while (bigRenderWait.Elapsed < TimeSpan.FromSeconds(30))
            {
                var countRaw = await probeWindow.RunScriptAsync("document.querySelectorAll('p').length")
                    .ConfigureAwait(true);
                bigRenderedCount = int.TryParse(
                    (countRaw ?? "0").Trim().Trim('"'), out var parsedCount) ? parsedCount : 0;

                if (bigRenderedCount >= 12_000)
                {
                    break;
                }

                await Task.Delay(100).ConfigureAwait(true);
            }

            var bigRenderMs = bigRenderWatch.ElapsedMilliseconds;

            var bigSwitchWatch = System.Diagnostics.Stopwatch.StartNew();
            probeWindow.OpenSourceView();

            ChapterSourceWindow? bigSourceWindow = null;
            var bigSourceWait = System.Diagnostics.Stopwatch.StartNew();

            while (bigSourceWait.Elapsed < TimeSpan.FromSeconds(30))
            {
                bigSourceWindow = Application.Current.Windows.OfType<ChapterSourceWindow>()
                    .FirstOrDefault(candidate => candidate.Editor.Text.Length >= bigChapter.Length);

                if (bigSourceWindow is not null)
                {
                    break;
                }

                await PumpAsync();
                await Task.Delay(100).ConfigureAwait(true);
            }

            var bigSwitchMs = bigSwitchWatch.ElapsedMilliseconds;

            var bigSaveDialogSeen = false;
            var bigSavePump = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(150),
                System.Windows.Threading.DispatcherPriority.Normal,
                (_, _) =>
                {
                    var dialog = Application.Current.Windows.OfType<ConfirmAutoFixDialog>().FirstOrDefault();

                    if (dialog is null)
                    {
                        return;
                    }

                    bigSaveDialogSeen = true;
                    dialog.Close();
                },
                System.Windows.Threading.Dispatcher.CurrentDispatcher);

            bigSavePump.Start();

            var bigSaveWatch = System.Diagnostics.Stopwatch.StartNew();
            bool bigSaved;

            try
            {
                bigSaved = await probeWindow.OnSaveAsync(bigBodyText).ConfigureAwait(true);
            }
            finally
            {
                bigSavePump.Stop();
            }

            var bigSaveMs = bigSaveWatch.ElapsedMilliseconds;
            var bigOnDisk = File.Exists(bigChapterFile) && new FileInfo(bigChapterFile).Length > 1_000_000;

            checks.Add(new SmokeCheck(
                "V12-large-chapter-visual-is-fast",
                bigRenderedCount >= 12_000 && bigRenderMs < 3000
                    && bigSourceWindow is not null && bigSwitchMs < 3000
                    && bigSaved && !bigSaveDialogSeen && bigSaveMs < 3000
                    && bigOnDisk,
                "1.2MB 可视重载=" + bigRenderMs + "ms（段落 " + bigRenderedCount + "）"
                    + "；切源码模式=" + bigSwitchMs + "ms"
                    + "；保存=" + bigSaveMs + "ms（成功=" + bigSaved + "，修正弹窗=" + bigSaveDialogSeen + "）"
                    + "；落盘 >1MB=" + bigOnDisk + "；阈值都 3000ms"));

            // 收尾：把这一章恢复成原文，别把冒烟造的大章留给后面的检查/清理
            if (!string.IsNullOrEmpty(bigOriginal))
            {
                File.WriteAllText(bigChapterFile, bigOriginal);
                probeWindow.ReloadChapterForTest();
                await PumpAsync();
            }

            // ---- V13：中文输入法（IME）合成期间不打断，提交后文字进入正文并标脏（出口条件：中文输入法正常工作）----
            var imeProbe = await probeWindow.RunScriptAsync(PROBE_IME_SCRIPT).ConfigureAwait(true);
            var imeDirty = probeWindow.HasUnsavedChanges;
            checks.Add(new SmokeCheck(
                "V13-ime-composition-commits-chinese",
                imeProbe.Contains("\\\"after\\\":true", StringComparison.Ordinal)
                    && imeProbe.Contains("\\\"htmlHasMarker\\\":true", StringComparison.Ordinal)
                    && imeProbe.Contains("\\\"err\\\":\\\"\\\"", StringComparison.Ordinal)
                    && imeDirty,
                imeProbe + "；HasUnsavedChanges=" + imeDirty));
            probeWindow.ReloadChapterForTest();
            await PumpAsync();

            // ---------- W：可视编辑窗口 / 源码窗口跟随书库主题（用户 2026-10-21） ----------
            // 书库「设置 → 外观 → 主题」（ui.theme），不是阅读器主题（reader.theme）。
            // 可视编辑页在 WebView2 里：用 getComputedStyle 读回真实渲染色，逐个和主题令牌比。
            var wLight = await ReadEditorThemeStateAsync(probeWindow).ConfigureAwait(true);
            var wLightOk = EditorThemeMatches(wLight);

            await ThemeManager.SetModeAsync(AppThemeMode.Dark).ConfigureAwait(true);
            var wDark = await WaitForEditorThemeAsync(probeWindow, "host", ResourceColor("WindowBackgroundBrush"))
                .ConfigureAwait(true);
            var wDarkOk = EditorThemeMatches(wDark);

            await CaptureWebViewAsync(probeWindow.Core!, Path.Combine(paths.TempDir, "visual-editor-theme-dark.png"))
                .ConfigureAwait(true);

            // 源码窗口（AvalonEdit）也归书库主题管：深色下不能还是白底黑字
            var wSourceWindow = new ChapterSourceWindow(source, probeWorkspace, probeChapters, 0)
            {
                Owner = window,
            };

            wSourceWindow.Show();
            var wSourceReady = await WaitUntilAsync(() => wSourceWindow.Editor.Text.Length > 0, TimeSpan.FromSeconds(15))
                .ConfigureAwait(true);
            var wSourceLayer = ResourceColor("LayerBackgroundBrush");
            var wSourceText = ResourceColor("TextPrimaryBrush");
            var wSourceBg = SolidColorOf(wSourceWindow.Editor.Background);
            var wSourceFg = SolidColorOf(wSourceWindow.Editor.Foreground);
            var wSourceOk = wSourceReady
                && SameColor(wSourceBg, wSourceLayer)
                && SameColor(wSourceFg, wSourceText);
            CaptureWindow(wSourceWindow, Path.Combine(paths.TempDir, "source-window-theme-dark.png"));
            wSourceWindow.Close();
            await PumpAsync();

            // 自定义档：深底浅字 + 橙色强调，验"页面用用户四色而不是映射成 dark/light"
            await ThemeManager.SetCustomColorsAsync(
                Color.FromRgb(0x1E, 0x2A, 0x3A),
                Color.FromRgb(0x26, 0x34, 0x46),
                Color.FromRgb(0xEC, 0xF1, 0xF7),
                Color.FromRgb(0xF0, 0x8A, 0x24)).ConfigureAwait(true);
            await ThemeManager.SetModeAsync(AppThemeMode.Custom).ConfigureAwait(true);
            var wCustom = await WaitForEditorThemeAsync(probeWindow, "host", Color.FromRgb(0x1E, 0x2A, 0x3A))
                .ConfigureAwait(true);
            var wCustomOk = SameHex(wCustom, "host", Color.FromRgb(0x1E, 0x2A, 0x3A))
                && SameHex(wCustom, "surface", Color.FromRgb(0x26, 0x34, 0x46))
                && SameHex(wCustom, "text", Color.FromRgb(0xEC, 0xF1, 0xF7))
                && SameHex(wCustom, "primary", Color.FromRgb(0xF0, 0x8A, 0x24))
                && SameHex(wCustom, "border", ResourceColor("BorderSubtleBrush"));

            await CaptureWebViewAsync(probeWindow.Core!, Path.Combine(paths.TempDir, "visual-editor-theme-custom.png"))
                .ConfigureAwait(true);

            await ThemeManager.SetModeAsync(AppThemeMode.System).ConfigureAwait(true);
            await PumpAsync();

            checks.Add(new SmokeCheck(
                "W-visual-editor-follows-app-theme",
                wLightOk && wDarkOk && wCustomOk,
                "亮色：四周/纸张/正文/按钮边框/主按钮=" + EditorThemeSummary(wLight)
                    + "；暗色：" + EditorThemeSummary(wDark)
                    + "；自定义：" + EditorThemeSummary(wCustom)
                    + "（期望 四周 #1E2A3A / 纸张 #263446 / 正文 #ECF1F7 / 主按钮 #F08A24）"));

            checks.Add(new SmokeCheck(
                "W-source-editor-follows-app-theme",
                wSourceOk,
                "源码窗口就绪=" + wSourceReady
                    + "；编辑区底色=" + Hex(wSourceBg)
                    + "（期望 " + Hex(wSourceLayer) + "）"
                    + "；正文色=" + Hex(wSourceFg)
                    + "（期望 " + Hex(wSourceText) + "）"));

            bigSourceWindow?.Close();
            await PumpAsync();
            probeWindow.Close();
            await PumpAsync();

            checks.Add(new SmokeCheck(
                "V3-visual-page-scripted-probe",
                probe.Contains("\\\"bold\\\":true", StringComparison.Ordinal)
                    && probe.Contains("\\\"undo\\\":true", StringComparison.Ordinal)
                    && probe.Contains("\\\"colWidth\\\":\\\"200px", StringComparison.Ordinal),
                probe));
        }
        finally
        {
            await probeWorkspace.DiscardAsync().ConfigureAwait(true);
        }

        // 判据 7：回到会话开始（撤销全部结构改动）
        await tree.RevertAsync().ConfigureAwait(true);
        var restoredSpine = ReadSpineIds(tree.WorkDir!);

        checks.Add(new SmokeCheck(
            "Q7-revert-goes-back-to-session-start",
            tree.Count == originalCount
                && restoredSpine.Count == originalCount
                && !TocContains(tree.WorkDir!, chapterMarker)
                && File.Exists(PathOf(tree.WorkDir!, firstHref)),
            "章节数=" + tree.Count + "（原 " + originalCount + "）；目录里还有改过的标题="
                + TocContains(tree.WorkDir!, chapterMarker)));

        await qWorkspace.DiscardAsync().ConfigureAwait(true);

                /* ---------- T5-5：资源检查窗口（悬空红 / 孤儿黄 + 定位能开源码窗口） ---------- */
        {
            var t55ScanWindow = (ResourceScanWindow?)null;
            var t55ScanWorkspace = editorVm?.Workspace;
            var t55Owned = false;

            if (t55ScanWorkspace is null && editorVm is not null)
            {
                var t55Book = await repo.GetByIdAsync(editorVm.Book.Id).ConfigureAwait(true);
                var t55Format = t55Book?.Formats.FirstOrDefault(
                    candidate => !candidate.Missing && File.Exists(candidate.FilePath));

                if (t55Book is not null && t55Format is not null)
                {
                    t55ScanWorkspace = await workspaces.OpenAsync(new WorkspaceOpenRequest
                    {
                        EpubPath = t55Format.FilePath,
                        BookId = t55Book.Id,
                        FormatId = t55Format.Id,
                    }).ConfigureAwait(true);
                    t55Owned = true;
                }
            }
            string? t55ChapterPath = null;
            string? t55ChapterBackup = null;
            string? t55OpfPath = null;
            string? t55OpfBackup = null;
            string? t55OrphanFull = null;
            var t55OpenedWindows = new List<Window>();

            try
            {
                if (t55ScanWorkspace is null)
                {
                    checks.Add(new SmokeCheck("T55-resource-scan-window", false, "编辑器还没打开工作区"));
                }
                else
                {
                    const string t55Marker = "missing-t55-smoke.jpg";

                    t55ChapterPath = Directory
                        .EnumerateFiles(t55ScanWorkspace.WorkDir, "*.xhtml", SearchOption.AllDirectories)
                        .FirstOrDefault(path => !path.Contains(".deleted") && !path.Contains(".snapshots"));

                    if (t55ChapterPath is not null)
                    {
                        t55ChapterBackup = File.ReadAllText(t55ChapterPath);
                        File.WriteAllText(
                            t55ChapterPath,
                            t55ChapterBackup.Replace(
                                "</body>",
                                "<p><img src=\"Images/" + t55Marker + "\" alt=\"t55\" /></p></body>",
                                StringComparison.OrdinalIgnoreCase));
                    }

                    t55OpfPath = Path.Combine(
                        t55ScanWorkspace.WorkDir, t55ScanWorkspace.Session.OpfPath!.Replace('/', Path.DirectorySeparatorChar));

                    t55OpfBackup = File.ReadAllText(t55OpfPath);
                    t55OrphanFull = Path.Combine(
                        Path.GetDirectoryName(t55OpfPath)!, "Images", "orphan-t55-smoke.jpg");
                    Directory.CreateDirectory(Path.GetDirectoryName(t55OrphanFull)!);
                    File.WriteAllBytes(t55OrphanFull, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
                    File.WriteAllText(
                        t55OpfPath,
                        t55OpfBackup.Replace(
                            "</manifest>",
                            "  <item id=\"orphan-t55-smoke\" href=\"Images/orphan-t55-smoke.jpg\" media-type=\"image/jpeg\" />" + Environment.NewLine + "  </manifest>",
                            StringComparison.OrdinalIgnoreCase));

                    t55ScanWindow = new ResourceScanWindow(new ResourceScanService(source), t55ScanWorkspace, source);
                    t55ScanWindow.Show();
                    await t55ScanWindow.RefreshAsync().ConfigureAwait(true);
                    await Task.Delay(300).ConfigureAwait(true);

                    var t55DanglingItem = t55ScanWindow.DanglingBox.Items.Count > 0
                        ? t55ScanWindow.DanglingBox.Items[0] as ListBoxItem
                        : null;
                    var t55OrphanItem = t55ScanWindow.OrphanBox.Items.Count > 0
                        ? t55ScanWindow.OrphanBox.Items[0] as ListBoxItem
                        : null;

                    var t55DanglingOk = t55DanglingItem?.Content?.ToString()?.Contains(t55Marker, StringComparison.OrdinalIgnoreCase) == true;
                    var t55OrphanOk = t55OrphanItem?.Content?.ToString()?.Contains("orphan-t55-smoke", StringComparison.OrdinalIgnoreCase) == true;
                    var t55DanglingBrush = t55DanglingItem?.Foreground as SolidColorBrush;
                    var t55OrphanBrush = t55OrphanItem?.Foreground as SolidColorBrush;
                    var t55ColorsOk = t55DanglingBrush?.Color == Brushes.IndianRed.Color
                        && t55OrphanBrush?.Color == Brushes.DarkGoldenrod.Color;

                    var t55Before = Application.Current.Windows.OfType<ChapterSourceWindow>().Count();

                    t55ScanWindow.SelectDangling(0);
                    var t55Located = t55ScanWindow.DanglingBox.SelectedIndex == 0 && t55ScanWindow.LocateSelected();

                    var t55Appeared = false;
                    var t55Watch = Stopwatch.StartNew();

                    while (!t55Appeared && t55Watch.Elapsed < TimeSpan.FromSeconds(15))
                    {
                        t55Appeared = Application.Current.Windows.OfType<ChapterSourceWindow>().Count() > t55Before;

                        if (!t55Appeared)
                        {
                            await Task.Delay(200).ConfigureAwait(true);
                        }
                    }

                    t55OpenedWindows.AddRange(Application.Current.Windows.OfType<ChapterSourceWindow>());

                    checks.Add(new SmokeCheck(
                        "T55-resource-scan-window",
                        t55DanglingOk && t55OrphanOk && t55ColorsOk && t55Located && t55Appeared,
                        "悬空栏=" + t55ScanWindow.DanglingCount + "（含造的悬空：" + t55DanglingOk + "）、孤儿栏="
                            + t55ScanWindow.OrphanCount + "（含造的孤儿：" + t55OrphanOk + "）；悬空红/孤儿黄=" + t55ColorsOk
                            + "；定位开出源码窗口=" + t55Appeared + "；摘要=" + t55ScanWindow.SummaryText));
                }
            }
            finally
            {
                t55ScanWindow?.Close();

                foreach (var t55SourceWindow in t55OpenedWindows)
                {
                    t55SourceWindow.Close();
                }

                if (t55ChapterPath is not null && t55ChapterBackup is not null)
                {
                    File.WriteAllText(t55ChapterPath, t55ChapterBackup);
                }

                if (t55OpfPath is not null && t55OpfBackup is not null)
                {
                    File.WriteAllText(t55OpfPath, t55OpfBackup);
                }

                if (t55OrphanFull is not null && File.Exists(t55OrphanFull))
                {
                    File.Delete(t55OrphanFull);
                }
                if (t55Owned && t55ScanWorkspace is not null)
                {
                    await t55ScanWorkspace.DiscardAsync().ConfigureAwait(true);
                }
            }
        }
        /* ---------- T5-6：保存闭环的"保存后工作区仍在 / changedFiles 清空" ---------- */
        {
            var t56SavedOk = checks.Any(check => check.Name == "F-title-written-to-epub" && check.Ok);
            var t56SessionDirs = Directory.Exists(paths.WorkspacesDir)
                ? Directory.GetDirectories(paths.WorkspacesDir).OrderByDescending(Directory.GetLastWriteTimeUtc).ToList()
                : new List<string>();
            var t56SessionFile = t56SessionDirs
                .Select(dir => Path.Combine(dir, ".session.json"))
                .FirstOrDefault(File.Exists);
            var t56Changed = -1;

            if (t56SessionFile is not null)
            {
                var t56Json = JsonNode.Parse(File.ReadAllText(t56SessionFile));
                t56Changed = t56Json?["changedFiles"]?.AsArray().Count ?? -1;
            }

            checks.Add(new SmokeCheck(
                "T56-save-closed-loop",
                t56SavedOk && t56SessionFile is not null && t56Changed == 0,
                "保存成功=" + t56SavedOk + "；工作区仍在=" + (t56SessionFile is not null)
                    + "（" + (t56SessionDirs.Count > 0 ? Path.GetFileName(t56SessionDirs[0]) : "(无)") + "）"
                    + "；.session.json 里 changedFiles=" + t56Changed + "（判据：保存后应为 0）"));
        }
return Finish(paths, startedAt, stopwatch, checks);
    }

    /// <summary>
    /// 塞一张"被章节引用、但 manifest 没声明"的图片（内容复制自工作区里已有的合法图，保证图片本身没问题）。
    /// 返回给冒烟展示的说明；返回空串表示没找到可复制的图（调用方据此判失败）。
    /// </summary>
    private static string AddUndeclaredReferencedImage(string workDir)
    {
        var opf = Directory.EnumerateFiles(workDir, "*.opf", SearchOption.AllDirectories).Where(path => !path.Contains(".snapshots", StringComparison.OrdinalIgnoreCase) && !path.Contains(".deleted", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (opf is null)
        {
            return string.Empty;
        }

        var editor = new OpfMetadataEditor(opf);
        editor.Read();
        var opfDirectory = Path.GetDirectoryName(opf)!;
        var items = editor.ReadManifest();

        var image = items.FirstOrDefault(item =>
            item.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(opfDirectory, item.Href.Replace('/', Path.DirectorySeparatorChar))));
        var chapter = items.FirstOrDefault(item =>
            item.MediaType.Contains("xhtml", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(opfDirectory, item.Href.Replace('/', Path.DirectorySeparatorChar))));

        if (image is null || chapter is null)
        {
            return string.Empty;
        }

        var sourceFile = Path.Combine(opfDirectory, image.Href.Replace('/', Path.DirectorySeparatorChar));
        var chapterFile = Path.Combine(opfDirectory, chapter.Href.Replace('/', Path.DirectorySeparatorChar));
        var chapterDirectory = Path.GetDirectoryName(chapterFile)!;
        var fileName = "ref-smoke" + Path.GetExtension(sourceFile);

        File.Copy(sourceFile, Path.Combine(chapterDirectory, fileName), overwrite: true);

        var text = File.ReadAllText(chapterFile, Encoding.UTF8);
        File.WriteAllText(
            chapterFile,
            text.Replace(
                "</body>",
                "<p><img src=\"" + fileName + "\" alt=\"x\"/></p></body>",
                StringComparison.Ordinal),
            new UTF8Encoding(false));

        return "在 " + chapter.Href + " 里引用未声明的 " + fileName;
    }

    /// <summary>
    /// 把工作区弄成"EpubCheck 一定会报错"的状态：删一个 XHTML、再写坏另一个。
    /// 返回一句给人看的说明（说明为空 = 没找到可破坏的章节，调用方据此判失败）。
    /// </summary>
    private static string BreakWorkspace(string workDir)
    {
        var opf = Directory.EnumerateFiles(workDir, "*.opf", SearchOption.AllDirectories).Where(path => !path.Contains(".snapshots", StringComparison.OrdinalIgnoreCase) && !path.Contains(".deleted", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (opf is null)
        {
            return string.Empty;
        }

        var editor = new OpfMetadataEditor(opf);
        var opfDirectory = Path.GetDirectoryName(opf)!;
        var chapters = editor.ReadManifest()
            .Where(item => item.MediaType.Contains("xhtml", StringComparison.OrdinalIgnoreCase)
                           || item.Href.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                           || item.Href.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase))
            .Select(item => Path.Combine(opfDirectory, item.Href.Replace('/', Path.DirectorySeparatorChar)))
            .Where(File.Exists)
            .ToList();

        if (chapters.Count == 0)
        {
            return string.Empty;
        }

        // 1) 删掉一个：OPF 里还声明着它，文件却不在
        File.Delete(chapters[0]);
        var detail = "删掉 " + Path.GetFileName(chapters[0]);

        // 2) 再写坏一个（如果还有第二个）：非法 XML -> 必然报错
        if (chapters.Count > 1)
        {
            File.WriteAllText(chapters[1], "<html><body><p>故意没闭合的标签", new UTF8Encoding(false));
            detail += "；写坏 " + Path.GetFileName(chapters[1]);
        }

        return detail;
    }

    /// <summary>开一次一次性工作区，读 OPF 的书名与版本（用完立刻清理）。</summary>
    private static async Task<EpubHead> ReadHeadAsync(
        IEpubWorkspaceFactory workspaces, string epubPath, Guid bookId, Guid formatId)
    {
        IEpubWorkspace? workspace = null;
        try
        {
            workspace = await workspaces
                .OpenAsync(new WorkspaceOpenRequest
                {
                    EpubPath = epubPath,
                    BookId = bookId,
                    FormatId = formatId,
                })
                .ConfigureAwait(true);

            var entry = workspace.Session.OpfPath;
            if (string.IsNullOrWhiteSpace(entry))
            {
                return new EpubHead(null, false, workspace.Session.EpubVersion ?? string.Empty);
            }

            var opf = Path.Combine(workspace.WorkDir, entry.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(opf))
            {
                return new EpubHead(null, false, workspace.Session.EpubVersion ?? string.Empty);
            }

            var editor = new OpfMetadataEditor(opf);
            var meta = editor.Read();

            return new EpubHead(meta.Title, editor.IsEpub3, workspace.Session.EpubVersion ?? string.Empty, meta.Rating);
        }
        finally
        {
            if (workspace is not null)
            {
                try
                {
                    await workspace.DiscardAsync().ConfigureAwait(true);
                }
                catch (Exception)
                {
                    // 冒烟用的一次性工作区，清不掉不挡判定
                }
            }
        }
    }

    /// <summary>造一张渐变 PNG 当新封面（640x1600 竖图，故意比 1600 长边大一圈才好验规范化）。</summary>
    private static byte[] MakePng(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * stride) + (x * 4);
                pixels[i] = (byte)(x % 256);
                pixels[i + 1] = (byte)(y % 256);
                pixels[i + 2] = 200;
                pixels[i + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[]? ReadBytes(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool SameBytes(byte[]? left, byte[]? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.AsSpan().SequenceEqual(right);
    }

    private static async Task<BookEditorWindow?> WaitForWindowAsync(Guid bookId, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            var found = Application.Current.Windows
                .OfType<BookEditorWindow>()
                .FirstOrDefault(w => w.BookId == bookId);

            if (found is not null)
            {
                return found;
            }

            await PumpAsync().ConfigureAwait(true);
        }

        return null;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await PumpAsync().ConfigureAwait(true);
        }

        return condition();
    }

    /// <summary>让 WPF 把排队的布局 / Loaded / 绑定都跑一轮。</summary>
    /// <summary>工作区里的包内路径 -> 磁盘路径。</summary>
    private static string PathOf(string workDir, string entry)
        => Path.Combine(workDir, entry.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>读 OPF 的 spine 顺序（idref 列表）。</summary>
    private static IReadOnlyList<string> ReadSpineIds(string workDir)
    {
        var opfPath = FindOpfPath(workDir);
        if (opfPath is null || !File.Exists(opfPath))
        {
            return Array.Empty<string>();
        }

        return System.Xml.Linq.XDocument.Load(opfPath).Descendants()
            .Where(e => e.Name.LocalName == "itemref")
            .Select(e => (string?)e.Attribute("idref") ?? string.Empty)
            .ToList();
    }

    /// <summary>目录文件（nav/NCX）或 OPF 里有没有这段文字。</summary>
    /// <summary>列出工作区里仍然包含某段文字的文件（便于定位"到底哪个文件没清干净"）。</summary>
    private static string FilesContaining(string workDir, string text)
    {
        var hits = new List<string>();

        foreach (var file in Directory.EnumerateFiles(workDir, "*.*", SearchOption.AllDirectories).Where(path => !path.Contains(".snapshots", StringComparison.OrdinalIgnoreCase) && !path.Contains(".deleted", StringComparison.OrdinalIgnoreCase)))
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();

            if (extension is not (".ncx" or ".xhtml" or ".html" or ".opf"))
            {
                continue;
            }

            try
            {
                if (File.ReadAllText(file).Contains(text, StringComparison.Ordinal))
                {
                    hits.Add(Path.GetRelativePath(workDir, file));
                }
            }
            catch (IOException)
            {
            }
        }

        return hits.Count == 0 ? "(无)" : string.Join(" | ", hits);
    }

    /// <summary>把"还含某段文字"的具体行号与内容打出来（定位残留到底是哪个元素）。</summary>
    private static string MatchingLines(string workDir, string text)
    {
        var hits = new List<string>();

        foreach (var file in Directory.EnumerateFiles(workDir, "*.*", SearchOption.AllDirectories).Where(path => !path.Contains(".snapshots", StringComparison.OrdinalIgnoreCase) && !path.Contains(".deleted", StringComparison.OrdinalIgnoreCase)))
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();

            if (extension is not (".ncx" or ".xhtml" or ".html" or ".opf"))
            {
                continue;
            }

            try
            {
                var lines = File.ReadAllLines(file);

                for (var i = 0; i < lines.Length && hits.Count < 4; i++)
                {
                    if (lines[i].Contains(text, StringComparison.Ordinal))
                    {
                        hits.Add(Path.GetFileName(file) + ":" + (i + 1) + "  " + lines[i].Trim());
                    }
                }
            }
            catch (IOException)
            {
            }
        }

        return hits.Count == 0 ? "(无)" : string.Join("  ||  ", hits);
    }

    private static bool TocContains(string workDir, string text)
    {
        foreach (var file in Directory.EnumerateFiles(workDir, "*.*", SearchOption.AllDirectories).Where(path => !path.Contains(".snapshots", StringComparison.OrdinalIgnoreCase) && !path.Contains(".deleted", StringComparison.OrdinalIgnoreCase)))
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();

            if (extension is not (".ncx" or ".xhtml" or ".html" or ".opf"))
            {
                continue;
            }

            try
            {
                if (File.ReadAllText(file).Contains(text, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (IOException)
            {
            }
        }

        return false;
    }

    /// <summary>从 container.xml 找到 OPF 的磁盘路径。</summary>
    private static string? FindOpfPath(string workDir)
    {
        var container = Path.Combine(workDir, "META-INF", "container.xml");

        if (!File.Exists(container))
        {
            return null;
        }

        var entry = System.Xml.Linq.XDocument.Load(container).Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "rootfile")
            ?.Attribute("full-path")?.Value;

        return entry is null ? null : PathOf(workDir, entry);
    }

    /// <summary>数一段文字里出现多少次（判"只有一个 html/body"用）。</summary>
    private static int CountOf(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>探针脚本：直接点工具条按钮，把真实结果（是否加粗、撤销是否回退、图片加载情况）回报。</summary>
    private const string PROBE_SCRIPT = @"
(function () {
  var out = { err: null, bold: false, undo: false, imgs: [], tables: 0 };
  try {
    var s = document.getElementById('surface');
    var p = s.querySelector('p') || s.firstElementChild;
    var r = document.createRange();
    r.selectNodeContents(p);
    var sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(r);
    var b = document.querySelector('button[data-cmd=bold]');
    b.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
    b.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    out.bold = /<(b|strong)[\s>]/i.test(s.innerHTML);
    var u = document.getElementById('undo');
    u.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    out.undo = !/<(b|strong)[\s>]/i.test(s.innerHTML);
    var im = document.images;
    for (var i = 0; i < im.length && i < 6; i++) {
      out.imgs.push((im[i].getAttribute('src') || '').slice(-24) + '|w' + im[i].naturalWidth);
    }
    out.base = (document.querySelector('base[data-book]') || {}).href || '(none)';
    var im0 = document.images;
    for (var k = 0; k < im0.length && k < 4; k++) {
      out.imgs.push('w' + im0[k].naturalWidth + '|' + String(im0[k].src).slice(-58));
    }
    var bookTables = [];
    var all = s.querySelectorAll('table');
    for (var q = 0; q < all.length; q++) {
      var firstCell = all[q].rows.length > 0 ? all[q].rows[0].cells[0] : null;
      if (!firstCell) { continue; }
      var bc = firstCell.getBoundingClientRect().width;
      var br = document.createRange();
      br.selectNodeContents(firstCell);
      var bs = window.getSelection();
      bs.removeAllRanges();
      bs.addRange(br);
      document.getElementById('selectCol').dispatchEvent(new MouseEvent('click', { bubbles: true }));
      document.getElementById('colWidth').value = String(Math.round(bc) + 120);
      document.getElementById('colWidthApply').dispatchEvent(new MouseEvent('click', { bubbles: true }));
      bookTables.push(Math.round(bc) + '->' + Math.round(firstCell.getBoundingClientRect().width)
        + '(tableStyle=' + (all[q].style.width || 'none') + ',css=' + getComputedStyle(all[q]).width + ')');
    }
    out.bookTables = bookTables.join(' ; ') || '(这本书没有表格)';
    document.getElementById('tableInsert').dispatchEvent(new MouseEvent('click', { bubbles: true }));
    var tb = s.querySelector('table');
    out.tables = s.querySelectorAll('table').length;
    if (tb && tb.rows.length > 1 && tb.rows[0].cells.length > 0) {
      var c0 = tb.rows[0].cells[0];
      var cr = document.createRange();
      cr.selectNodeContents(c0);
      var cs = window.getSelection();
      cs.removeAllRanges();
      cs.addRange(cr);
      document.getElementById('selectCol').dispatchEvent(new MouseEvent('click', { bubbles: true }));
      document.getElementById('colWidth').value = '200';
      document.getElementById('colWidthApply').dispatchEvent(new MouseEvent('click', { bubbles: true }));
      out.colWidth = tb.rows[1].cells[0].style.width || '(empty)';
      out.cellPicked = c0.classList.contains('ets-sel');
    }
    out.tables = s.querySelectorAll('table').length;
  } catch (e) { out.err = String((e && e.message) || e); }
  return JSON.stringify(out);
})()";

    /// <summary>第二轮探针：量表格尺寸的真实渲染宽度 + 图片回推链路。</summary>
    private const string PROBE2_SCRIPT = @"
(function () {
  var out = { err: null, colReadback: '(none)', colBefore: 0, colAfter: 0, rowReadback: '(none)',
              imgBefore: 0, imgAfter: 0, tableCss: '' };
  try {
    var s = document.getElementById('surface');
    document.getElementById('tableInsert').dispatchEvent(new MouseEvent('click', { bubbles: true }));
    var t = s.querySelector('table');
    var cell = t.rows[0].cells[0];
    out.tableCss = getComputedStyle(t).width + ' | tableLayout=' + getComputedStyle(t).tableLayout;
    out.colBefore = Math.round(cell.getBoundingClientRect().width);
    var r = document.createRange();
    r.selectNodeContents(cell);
    var sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(r);
    document.getElementById('selectCol').dispatchEvent(new MouseEvent('click', { bubbles: true }));
    out.colReadback = document.getElementById('colWidth').value || '(empty)';
    document.getElementById('colWidth').value = '200';
    document.getElementById('colWidthApply').dispatchEvent(new MouseEvent('click', { bubbles: true }));
    out.colAfter = Math.round(t.rows[0].cells[0].getBoundingClientRect().width);
    var r2 = document.createRange();
    r2.selectNodeContents(t.rows[0].cells[0]);
    var sel2 = window.getSelection();
    sel2.removeAllRanges();
    sel2.addRange(r2);
    document.getElementById('selectRow').dispatchEvent(new MouseEvent('click', { bubbles: true }));
    out.rowReadback = document.getElementById('rowHeight').value || '(empty)';
  } catch (e) { out.err = String((e && e.message) || e); }
  return JSON.stringify(out);
})()";

    /// <summary>焦点/选区探针：专门量"点工具条按钮后，焦点与选中是否被抢走"。</summary>
    private const string PROBE_FOCUS_SCRIPT = @"
(function () {
  var out = { err: null };
  try {
    var s = document.getElementById('surface');
    var p = s.querySelector('p') || s.firstElementChild;
    var r = document.createRange();
    r.selectNodeContents(p);
    var sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(r);
    out.selBefore = sel.isCollapsed ? 'collapsed' : 'text';
    var boldBtn = document.querySelector('#ribbon button[data-cmd=bold]');
    boldBtn.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
    out.focusKeptAfterMouseDown = document.activeElement === s;
    out.selKeptAfterMouseDown = window.getSelection().isCollapsed ? 'collapsed' : 'text';
    boldBtn.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    out.boldApplied = /<(b|strong)[\s>]/i.test(s.innerHTML);
    document.getElementById('tableInsert').dispatchEvent(new MouseEvent('click', { bubbles: true }));
    var t = s.querySelector('table');
    if (t) {
      var c = t.rows[0].cells[0];
      var rc = document.createRange();
      rc.selectNodeContents(c);
      var sc = window.getSelection();
      sc.removeAllRanges();
      sc.addRange(rc);
      out.readBeforePick = document.getElementById('colWidth').value || '(empty)';
      document.getElementById('selectCol').dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
      document.getElementById('selectCol').dispatchEvent(new MouseEvent('click', { bubbles: true }));
      out.cellPicked = c.classList.contains('ets-sel');
      out.readAfterPick = document.getElementById('colWidth').value || '(empty)';
      var widthBefore = Math.round(c.getBoundingClientRect().width);
      document.getElementById('colWidth').focus();
      document.getElementById('colWidth').value = '170';
      document.getElementById('colWidthApply').dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
      document.getElementById('colWidthApply').dispatchEvent(new MouseEvent('click', { bubbles: true }));
      out.colChange = widthBefore + '->' + Math.round(t.rows[0].cells[0].getBoundingClientRect().width);
      out.statusTail = document.getElementById('status').textContent.slice(0, 36);
    }
    var colInput = document.getElementById('colWidth');
    colInput.value = '150';
    colInput.dispatchEvent(new Event('input', { bubbles: true }));
    colInput.dispatchEvent(new Event('keyup', { bubbles: true }));
    document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));
    out.typedValueSurvives = colInput.value;
    if (t) {
      var wBefore = Math.round(t.getBoundingClientRect().width);
      document.getElementById('tableWidth').value = '600';
      document.getElementById('tableWidth').dispatchEvent(new Event('input', { bubbles: true }));
      document.getElementById('tableWidthApply').dispatchEvent(new MouseEvent('click', { bubbles: true }));
      out.tableWidthChange = wBefore + '->' + Math.round(t.getBoundingClientRect().width);
      // 表宽输 600 却渲染出别的数的成因取证：把每条线索原样读回来
      var tcs = getComputedStyle(t);
      var row0 = t.rows[0];
      var sumCells = 0;
      var cols = [];

      for (var ci = 0; ci < row0.cells.length; ci++) {
        var cc = row0.cells[ci];
        var ccs = getComputedStyle(cc);
        var cw = Math.round(cc.getBoundingClientRect().width);
        sumCells += cw;
        cols.push({
          w: cw,
          style: cc.style.width || '(none)',
          padH: ccs.paddingLeft + '/' + ccs.paddingRight,
          bordH: ccs.borderLeftWidth + '/' + ccs.borderRightWidth,
          box: ccs.boxSizing
        });
      }

      out.tblDiag = {
        cssWidth: tcs.width,
        minWidth: tcs.minWidth,
        maxWidth: tcs.maxWidth,
        layout: tcs.tableLayout,
        box: tcs.boxSizing,
        collapse: tcs.borderCollapse,
        spacing: tcs.borderSpacing,
        inlineWidth: t.style.width || '(none)',
        rect: Math.round(t.getBoundingClientRect().width),
        cellCount: row0.cells.length,
        sumCells: sumCells,
        cols: cols,
        colEls: t.querySelectorAll('colgroup,col').length,
        parentRect: Math.round(t.parentNode.getBoundingClientRect().width),
        tablePadH: tcs.paddingLeft + '/' + tcs.paddingRight,
        tableBordH: tcs.borderLeftWidth + '/' + tcs.borderRightWidth,
        inlineBox: t.style.boxSizing || '(none)'
      };
    }

    if (t && t.rows.length > 1) {
      var r1 = t.rows[1];
      var rc1 = document.createRange();
      rc1.selectNodeContents(r1.cells[0]);
      var sc1 = window.getSelection();
      sc1.removeAllRanges();
      sc1.addRange(rc1);
      document.getElementById('selectRow').dispatchEvent(new MouseEvent('click', { bubbles: true }));
      var hBefore = Math.round(r1.getBoundingClientRect().height);
      document.getElementById('rowHeight').value = '90';
      document.getElementById('rowHeight').dispatchEvent(new Event('input', { bubbles: true }));
      document.getElementById('rowHeightApply').dispatchEvent(new MouseEvent('click', { bubbles: true }));
      out.rowHeightChange = hBefore + '->' + Math.round(t.rows[1].getBoundingClientRect().height);
      out.rowStatus = document.getElementById('status').textContent.slice(0, 40);
    }

    if (t && t.rows.length > 0 && t.rows[0].cells.length > 0) {
      var c3 = t.rows[0].cells[0];
      var b3 = c3.getBoundingClientRect();
      var w3 = Math.round(b3.width);
      var edgeX = Math.round(b3.right);
      var midY = Math.round(b3.top + 5);
      c3.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, clientX: edgeX, clientY: midY }));
      document.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: edgeX + 60, clientY: midY }));
      document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));
      out.dragColChange = w3 + '->' + Math.round(c3.getBoundingClientRect().width);
    }

    out.ribbonButtons = document.querySelectorAll('#ribbon button').length;
  } catch (e) { out.err = String((e && e.message) || e); }
  return JSON.stringify(out);
})()";

    /// <summary>
    /// 判据 2/3 的逐项探针：段落样式 / 列表 / 对齐 / 缩进 / 相对字号 / 合并单元格 / 拆分单元格。
    /// 每步都新造一个顶层 &lt;p&gt; 再操作，互不干扰；结果全读回原始值（不好好看返回值就断言 = 猜）。
    /// </summary>
    private const string PROBE_IME_SCRIPT = @"
(function () {
  var out = { err: '' };
  try {
    var s = document.getElementById('surface');
    if (!s) { out.err = 'no-surface'; return JSON.stringify(out); }
    s.focus();
    var marker = '中文输入法冒烟';
    out.before = s.textContent.indexOf(marker) >= 0;
    var sel = window.getSelection();
    var range = document.createRange();
    range.selectNodeContents(s);
    range.collapse(false);
    sel.removeAllRanges();
    sel.addRange(range);
    var fire = function (type, data, composing) {
      var ev;
      try {
        if (type === 'input') { ev = new InputEvent('input', { bubbles: true, isComposing: !!composing }); }
        else { ev = new CompositionEvent(type, { bubbles: true, data: data || '' }); }
      } catch (e) { ev = new Event(type, { bubbles: true }); }
      s.dispatchEvent(ev);
    };
    fire('compositionstart', '', true);
    var tn = document.createTextNode(marker);
    range.insertNode(tn);
    range.setStartAfter(tn);
    range.collapse(true);
    sel.removeAllRanges();
    sel.addRange(range);
    fire('compositionupdate', marker, true);
    fire('input', '', true);
    out.during = s.textContent.indexOf(marker) >= 0;
    fire('compositionend', marker, false);
    fire('input', '', false);
    out.after = s.textContent.indexOf(marker) >= 0;
    out.htmlHasMarker = s.innerHTML.indexOf(marker) >= 0;
  } catch (e) { out.err = String(e); }
  return JSON.stringify(out);
})()
";

    private const string PROBE3_SCRIPT = @"
(function () {
  var out = {};
  var s = document.getElementById('surface');

  function selectNode(n) {
    var r = document.createRange();
    r.selectNodeContents(n);
    var sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(r);
  }

  function fresh(text) {
    var p = document.createElement('p');
    p.textContent = text;
    s.appendChild(p);
    return p;
  }

  function click(selector) {
    var b = document.querySelector(selector);
    if (!b) { return false; }
    b.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
    b.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    return true;
  }

  function countCentered() {
    var n = 0;
    for (var i = 0; i < s.children.length; i++) {
      if (getComputedStyle(s.children[i]).textAlign === 'center') { n++; }
    }
    return n;
  }

  try {
    // 1) 段落样式 -> 标题 2（工具条 #block）
    var a = fresh('段落样式冒烟');
    selectNode(a);
    var block = document.getElementById('block');
    block.value = 'h2';
    block.dispatchEvent(new Event('change', { bubbles: true }));
    var h2 = s.querySelector('h2');
    out.h2 = h2 !== null;
    out.h2Text = h2 ? h2.textContent : '';

    // 2) 无序列表
    var b = fresh('列表冒烟');
    selectNode(b);
    out.listClicked = click('#ribbon button[data-cmd=insertUnorderedList]');
    out.list = s.querySelectorAll('ul').length > 0;

    // 3) 居中
    var c = fresh('对齐冒烟');
    selectNode(c);
    out.alignBefore = countCentered();
    out.alignClicked = click('#ribbon button[data-cmd=justifyCenter]');
    out.alignAfter = countCentered();

    // 4) 增加缩进
    var d = fresh('缩进冒烟');
    selectNode(d);
    out.indentBefore = getComputedStyle(d).marginLeft;
    out.indentClicked = click('#ribbon button[data-cmd=indent]');
    out.indentAfter = s.contains(d) ? getComputedStyle(d).marginLeft : '(元素被换掉了)';
    out.blockquotes = s.querySelectorAll('blockquote').length;

    // 5) 字号：必须落成相对单位（em / %）
    var e = fresh('字号冒烟');
    selectNode(e);
    var size = document.getElementById('size');
    size.value = '150%';
    size.dispatchEvent(new Event('change', { bubbles: true }));
    var span = s.querySelector('span[style*=font-size]');
    out.sizeStyle = span ? span.style.fontSize : '(没有包 span)';
    out.sizeRelative = /^[0-9.]+(em|%)$/.test(out.sizeStyle);

    // 6) 合并单元格 / 拆分单元格（Word 式）
    var t = s.querySelector('table');
    var cell0 = t.rows[0].cells[0];
    selectNode(cell0);
    out.mergeBefore = t.rows[0].cells.length;
    out.mergeClicked = click('#tableOps button[data-tbl=mergeRight]');
    out.mergeAfter = t.rows[0].cells.length;
    out.mergeColspan = t.rows[0].cells[0].getAttribute('colspan') || '1';
    selectNode(t.rows[0].cells[0]);
    out.splitClicked = click('#tableOps button[data-tbl=split]');
    out.splitAfter = t.rows[0].cells.length;
    out.splitColspan = t.rows[0].cells[0].getAttribute('colspan') || '1';
  } catch (ex) {
    out.err = String((ex && ex.message) || ex);
  }

  return JSON.stringify(out);
})()";

    /* 读回可视编辑器页面真实渲染出来的主题色（WebView2 里的 getComputedStyle）。
       返回 JSON 字符串：host/ribbon/surface/text/border/primary/status。 */
    private const string EDITOR_THEME_STATE_SCRIPT = @"(function () {
  function hexOf(selector, prop) {
    var node = document.querySelector(selector);
    if (!node) { return ''; }
    var value = getComputedStyle(node)[prop] || '';
    var m = /rgba?\((\d+),\s*(\d+),\s*(\d+)/.exec(value);
    if (!m) { return value; }
    function two(v) { var s = (+v).toString(16).toUpperCase(); return s.length === 1 ? '0' + s : s; }
    return '#' + two(m[1]) + two(m[2]) + two(m[3]);
  }
  return JSON.stringify({
    host: hexOf('#host', 'backgroundColor'),
    ribbon: hexOf('#ribbon', 'backgroundColor'),
    surface: hexOf('#surface', 'backgroundColor'),
    text: hexOf('#surface', 'color'),
    border: hexOf('#ribbon button', 'borderTopColor'),
    primary: hexOf('#save', 'backgroundColor'),
    status: hexOf('#status', 'backgroundColor')
  });
})()";

    private static async Task<Dictionary<string, string>> ReadEditorThemeStateAsync(ChapterVisualEditorWindow window)
    {
        var raw = await window.RunScriptAsync(EDITOR_THEME_STATE_SCRIPT).ConfigureAwait(true);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            using var outer = JsonDocument.Parse(raw);
            var json = outer.RootElement.GetString() ?? "{}";

            using var inner = JsonDocument.Parse(json);
            foreach (var property in inner.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }
        catch (JsonException ex)
        {
            result["解析失败"] = ex.Message;
        }

        return result;
    }

    /// <summary>页面渲染色是否与当前书库主题令牌一致（四周=窗口底色、纸张=卡片色、正文/边框/主按钮=令牌）。</summary>
    private static bool EditorThemeMatches(Dictionary<string, string> state)
        => SameHex(state, "host", ResourceColor("WindowBackgroundBrush"))
            && SameHex(state, "ribbon", ResourceColor("WindowBackgroundBrush"))
            && SameHex(state, "surface", ResourceColor("CardBackgroundBrush"))
            && SameHex(state, "text", ResourceColor("TextPrimaryBrush"))
            && SameHex(state, "border", ResourceColor("BorderSubtleBrush"))
            && SameHex(state, "primary", ResourceColor("AccentColorBrush"));

    private static string EditorThemeSummary(Dictionary<string, string> state)
        => "四周 " + state.GetValueOrDefault("host", "(缺)")
            + " / 纸张 " + state.GetValueOrDefault("surface", "(缺)")
            + " / 正文 " + state.GetValueOrDefault("text", "(缺)")
            + " / 边框 " + state.GetValueOrDefault("border", "(缺)")
            + " / 主按钮 " + state.GetValueOrDefault("primary", "(缺)");

    /// <summary>主题热切换是异步推给页面的：等到指定颜色出现（最多 6 秒），等不到也返回最后一次读数。</summary>
    private static async Task<Dictionary<string, string>> WaitForEditorThemeAsync(
        ChapterVisualEditorWindow window,
        string key,
        Color expected)
    {
        var watch = Stopwatch.StartNew();
        var state = await ReadEditorThemeStateAsync(window).ConfigureAwait(true);

        while (watch.Elapsed < TimeSpan.FromSeconds(6) && !SameHex(state, key, expected))
        {
            await Task.Delay(100).ConfigureAwait(true);
            state = await ReadEditorThemeStateAsync(window).ConfigureAwait(true);
        }

        return state;
    }

    private static Color ResourceColor(string key)
        => Application.Current?.TryFindResource(key) is SolidColorBrush brush ? brush.Color : Colors.Transparent;

    private static Color SolidColorOf(Brush? brush)
        => brush is SolidColorBrush solid ? solid.Color : Colors.Transparent;

    private static bool SameColor(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B;

    private static bool SameHex(Dictionary<string, string> state, string key, Color expected)
        => string.Equals(state.GetValueOrDefault(key), Hex(expected), StringComparison.OrdinalIgnoreCase);

    private static string Hex(Color color) => "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");

    /// <summary>WCAG 对比度（1..21），用来判定"深色下文字看得见""自定义档边框有轮廓"。</summary>
    private static double ContrastRatio(Color a, Color b)
    {
        static double Linear(byte value)
        {
            var s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color color)
            => (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));

        var lighter = Math.Max(Luminance(a), Luminance(b)) + 0.05;
        var darker = Math.Min(Luminance(a), Luminance(b)) + 0.05;
        return lighter / darker;
    }

    /// <summary>把 WPF 窗口画成 PNG（截图为验收凭据，也用来肉眼复核）。</summary>
    private static void CaptureWindow(Window window, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        window.UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(window);
        var width = Math.Max(1, (int)Math.Round(window.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Round(window.ActualHeight * dpi.DpiScaleY));

        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>WebView2 里的内容 RenderTargetBitmap 拍不到，只能用它自己的截图接口。</summary>
    private static async Task CaptureWebViewAsync(CoreWebView2 core, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = File.Create(path);
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream).ConfigureAwait(true);
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.Background);
        await Task.Delay(20).ConfigureAwait(true);
    }

    private static SmokeReport Finish(
        AppPaths paths,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        List<SmokeCheck> checks)
    {
        stopwatch.Stop();

        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            stopwatch.ElapsedMilliseconds,
            paths.DataRoot,
            paths.TempDir,
            null,
            checks);

        var reportPath = Path.Combine(paths.TempDir, "edit-smoke.json");
        Directory.CreateDirectory(paths.TempDir);
        File.WriteAllText(
            reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return report;
    }

    private sealed record EpubHead(string? Title, bool IsEpub3, string Version, int? Rating = null);
}
