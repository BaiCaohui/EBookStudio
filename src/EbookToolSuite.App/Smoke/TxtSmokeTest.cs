using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EbookToolSuite.App.Commands;
using EbookToolSuite.App.Themes;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Engines.Text;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Reader.WebView;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T4-5：TXT 的端到端冒烟导入 -&gt; 打开 -&gt; 编码识别 -&gt; 界面手选编码并立即重排。
/// 引擎侧的编码判定与切章细节由单测覆盖，这里只验"用户能做的那件事"能不能做成。
/// </summary>
internal static class TxtSmokeTest
{
    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<SmokeReport> RunAsync(
        IBookImportService import,
        IReaderService reader,
        IWebViewEnvironmentProvider environments,
        IReadingProgressService progress,
        ILoggerFactory loggerFactory,
        IAppPaths paths,
        AppPaths appPaths,
        string sourceFile,
        IConfigStore? config,
        IBookRepository? repository = null,
        EditCommand? edit = null,
        ILibraryScanService? scan = null)
    {
        var startedAt = DateTimeOffset.Now;
        var checks = new List<SmokeCheck>();
        var total = Stopwatch.StartNew();

        void Add(string name, bool ok, string detail) => checks.Add(new SmokeCheck(name, ok, detail));

        // ---------- A. 导入（判据：TXT 也能导入） ----------
        var importResult = await import
            .ImportAsync(sourceFile, ImportOptions.Default, null, CancellationToken.None)
            .ConfigureAwait(true);

        var bookId = importResult.BookId ?? Guid.Empty;

        Add(
            "A-import",
            bookId != Guid.Empty,
            "结果=" + importResult.Outcome + " 书=" + bookId.ToString("N")[..8]
                + " 用时=" + importResult.Duration.TotalMilliseconds.ToString("F0") + "ms");

        if (bookId == Guid.Empty)
        {
            return Build(appPaths, startedAt, total, checks);
        }

        // ---------- B. 打开缓存（判据 6：首屏时间） ----------
        var watch = Stopwatch.StartNew();
        var session = await reader.OpenAsync(bookId, CancellationToken.None).ConfigureAwait(true);
        var openMs = watch.ElapsedMilliseconds;

        Add(
            "B-txt-open",
            session.Manifest.Format == "TXT" && session.Manifest.Spine.Count > 0,
            "格式=" + session.Manifest.Format + " 章数=" + session.Manifest.Spine.Count
                + " 编码=" + session.Manifest.TextEncoding + " 硬切=" + session.Manifest.TextFallbackSplit
                + " 打开=" + openMs + "ms");

        // ---------- C. 编码识别（判据 1） ----------
        Add(
            "C-encoding-detected",
            !string.IsNullOrEmpty(session.Manifest.TextEncoding)
                && TextEncodingSupport.SupportedNames.Contains(session.Manifest.TextEncoding!),
            "识别为 " + session.Manifest.TextEncoding + " 有把握=" + session.Manifest.TextEncodingConfident
                + " BOM=" + session.Manifest.TextBomLength + " 字节");

        // ---------- D. 第一章物化出来是能读的中文 ----------
        var href = await reader.MaterializeChapterAsync(session, 0, CancellationToken.None).ConfigureAwait(true);
        var chapterPath = Path.Combine(session.CacheRoot, href.Replace('/', Path.DirectorySeparatorChar));
        var html = File.ReadAllText(chapterPath, Encoding.UTF8);
        var firstScreenMs = watch.ElapsedMilliseconds;
        var cjk = html.Count(ch => ch >= 0x4E00 && ch <= 0x9FFF);

        Add(
            "D-chapter-readable",
            File.Exists(chapterPath) && html.Contains("<p>", StringComparison.Ordinal) && cjk > 20,
            "首章=" + href + " 长度=" + html.Length + " 汉字数=" + cjk
                + " 首屏(含打开)=" + firstScreenMs + "ms");

        // ---------- E. 目录直接用切出来的章节（T4-4 复用） ----------
        Add(
            "E-toc-from-chapters",
            session.Manifest.Toc.Count == session.Manifest.Spine.Count && session.Manifest.Toc.Count > 0,
            "目录项=" + session.Manifest.Toc.Count + " 章节数=" + session.Manifest.Spine.Count);

        // ---------- F. 界面：编码选择器（判据 2） ----------
        var window = new ReaderWindow(
            reader, environments, progress, loggerFactory.CreateLogger<ReaderWindow>(), bookId,
            config: config, libraryScan: scan);

        window.Show();

        try
        {
            await WaitUntilAsync(() => window.LastChapterIndex >= 0, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            await Task.Delay(500).ConfigureAwait(true);

            var probe = await window.ReadTextEncodingAsync().ConfigureAwait(true);
            var optionsMatch = probe is not null
                && probe.Options.SequenceEqual(TextEncodingSupport.SupportedNames);

            Add(
                "F-encoding-selector-visible",
                probe is not null && probe.Visible
                    && probe.Options.Count == TextEncodingSupport.SupportedNames.Count
                    && optionsMatch
                    && probe.Value == session.Manifest.TextEncoding,
                "可见=" + (probe?.Visible ?? false) + " 当前=" + (probe?.Value ?? "?")
                    + " 选项=" + (probe?.Options.Count ?? 0) + "（与编码表顺序一致=" + optionsMatch + "）"
                    + " 首项=" + (probe?.Options.Count > 0 ? probe.Options[0] : "?")
                    + " 末项=" + (probe?.Options.Count > 0 ? probe.Options[^1] : "?")
                    + " 提示=" + (probe?.Hint ?? string.Empty));

            // ---------- G. 手选一个不同的编码 -> 必须立即重排（判据 2 的核心） ----------
            var wrong = session.Manifest.TextEncoding == "UTF-8" ? "GBK" : "UTF-8";

            await window.TextEncodingPickAsync(wrong).ConfigureAwait(true);

            var switched = await WaitUntilAsync(
                () => string.Equals(window.LastTextEncoding, wrong, StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);

            await Task.Delay(800).ConfigureAwait(true);

            var wrongCjk = await CountCjkAfterOpenAsync(reader, bookId).ConfigureAwait(true);

            Add(
                "G-encoding-switch-reflows",
                switched && wrongCjk != cjk && wrongCjk >= 0,
                "换成 " + wrong + "：重排完成=" + switched + " 汉字数 " + cjk + " -> " + wrongCjk);

            // ---------- H. 选回正确编码 -> 内容恢复 ----------
            await window.TextEncodingPickAsync(session.Manifest.TextEncoding!).ConfigureAwait(true);

            var restored = await WaitUntilAsync(
                () => string.Equals(window.LastTextEncoding, session.Manifest.TextEncoding, StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);

            await Task.Delay(800).ConfigureAwait(true);

            var backCjk = await CountCjkAfterOpenAsync(reader, bookId).ConfigureAwait(true);

            Add(
                "H-encoding-restored",
                restored && backCjk == cjk,
                "选回 " + session.Manifest.TextEncoding + "：内容恢复=" + (backCjk == cjk) + " 汉字数=" + backCjk);

            // ---------- I. 选择要记住（重开这本书还是它） ----------
            var stored = config?.GetString(
                ReaderWindow.TextEncodingKeyPrefix + bookId.ToString("D"), null);

            Add(
                "I-encoding-persisted",
                !string.IsNullOrEmpty(stored)
                    && string.Equals(stored, session.Manifest.TextEncoding, StringComparison.OrdinalIgnoreCase),
                "Config.json 里记的是 " + (stored ?? "(空)"));

            // ---------- J. 「编辑」按钮入口：TXT 进的是全文编辑（用户 2026-10-20 要求） ----------
            /* 口径（用户 2026-10-20 两次纠正后定死）：
               · 双击卡片 = 打开详情（与其它格式一致，由书库冒烟的双击断言钉住，这里不重复）；
               · 详情/工具栏的「编辑」按钮 = 与其它格式同一个入口；EPUB 进 EPUB 那套编辑器，
                 TXT 进本轮的**全文纯文本编辑器**（两套不是一个东西），且不能误开 EPUB 元数据编辑器；
               · 详情点「阅读」→ 阅读界面 → 「编辑本章」= 同一个全文编辑并定位到本章标题（见 K）。 */
            var book = repository is null ? null : await repository.GetByIdAsync(bookId).ConfigureAwait(true);
            var editorsBefore = Application.Current.Windows.OfType<TxtEditorWindow>().Count();
            var metaEditorsBefore = Application.Current.Windows.OfType<BookEditorWindow>().Count();

            if (book is not null && edit is not null)
            {
                edit.Run(window, book);
            }

            var libraryEntryOpened = await WaitUntilAsync(
                () => Application.Current.Windows.OfType<TxtEditorWindow>().Count() > editorsBefore,
                TimeSpan.FromSeconds(20)).ConfigureAwait(true);

            var libraryEditor = Application.Current.Windows.OfType<TxtEditorWindow>().LastOrDefault();
            var libraryEditorReady = libraryEditor is not null
                && await WaitUntilAsync(() => libraryEditor.IsReady, TimeSpan.FromSeconds(60)).ConfigureAwait(true);

            var editorOptionsMatch = libraryEditor is not null
                && libraryEditor.EncodingOptions.SequenceEqual(TextEncodingSupport.SupportedNames);

            var noMetadataEditor = Application.Current.Windows.OfType<BookEditorWindow>().Count() == metaEditorsBefore;

            Add(
                "J-txt-full-edit-entry",
                libraryEntryOpened && libraryEditorReady && noMetadataEditor
                    && string.Equals(libraryEditor?.DocumentEncoding, session.Manifest.TextEncoding, StringComparison.OrdinalIgnoreCase)
                    && editorOptionsMatch,
                "「编辑」按钮打开 TXT 全文编辑=" + libraryEntryOpened
                    + "；全文载入=" + libraryEditorReady
                    + "；没有误开 EPUB 元数据编辑器（两套编辑器不是一个东西）=" + noMetadataEditor
                    + "；编码=" + (libraryEditor?.DocumentEncoding ?? "?")
                    + "（阅读器用的是 " + session.Manifest.TextEncoding + "）"
                    + "；编辑器编码下拉与阅读器同一清单/顺序=" + editorOptionsMatch
                    + "（" + (libraryEditor?.EncodingOptions.Count ?? 0) + " 项）");

            // ---------- J2. 编辑区跟随书库「设置 → 外观 → 主题」（用户 2026-10-21） ----------
            // 口径：书库主题（ui.theme）≠ 阅读器主题（reader.theme），这里只验前者。
            // 报的问题：深色主题下工具栏/状态栏都变暗了，中间 AvalonEdit 还是一整块白底。
            var txtThemeOk = false;
            var txtThemeDetail = "(窗口没打开，跳过)";

            if (libraryEditor is not null)
            {
                var lightLayer = ThemeResourceColor("LayerBackgroundBrush");
                var lightText = ThemeResourceColor("TextPrimaryBrush");
                var lightOk = SameThemeColor(ThemeColorOf(libraryEditor.Editor.Background), lightLayer)
                    && SameThemeColor(ThemeColorOf(libraryEditor.Editor.Foreground), lightText);

                await ThemeManager.SetModeAsync(AppThemeMode.Dark).ConfigureAwait(true);
                await Task.Delay(300).ConfigureAwait(true);
                var darkLayer = ThemeResourceColor("LayerBackgroundBrush");
                var darkEditorBg = ThemeColorOf(libraryEditor.Editor.Background);
                var darkOk = SameThemeColor(darkEditorBg, darkLayer) && !SameThemeColor(darkLayer, lightLayer);
                CaptureWindow(libraryEditor, Path.Combine(paths.TempDir, "txt-editor-theme-dark.png"));

                // 自定义档：深底浅字 + 橙色强调，验编辑区用用户选的四色（不是被映射成 dark/light）
                await ThemeManager.SetCustomColorsAsync(
                    Color.FromRgb(0x1E, 0x2A, 0x3A),
                    Color.FromRgb(0x26, 0x34, 0x46),
                    Color.FromRgb(0xEC, 0xF1, 0xF7),
                    Color.FromRgb(0xF0, 0x8A, 0x24)).ConfigureAwait(true);
                await ThemeManager.SetModeAsync(AppThemeMode.Custom).ConfigureAwait(true);
                await Task.Delay(300).ConfigureAwait(true);
                var customEditorBg = ThemeColorOf(libraryEditor.Editor.Background);
                var customLayer = ThemeResourceColor("LayerBackgroundBrush");
                // 深底自定义档的层底色 = 底色与卡片色的中间值（#1E2A3A / #263446 -> #222F40）
                var customOk = SameThemeColor(customEditorBg, customLayer)
                    && !SameThemeColor(customLayer, darkLayer);
                var borderContrast = ThemeContrast(
                    ThemeResourceColor("BorderSubtleBrush"), ThemeResourceColor("WindowBackgroundBrush"));
                var borderOk = borderContrast >= 1.5;
                CaptureWindow(libraryEditor, Path.Combine(paths.TempDir, "txt-editor-theme-custom.png"));

                await ThemeManager.SetModeAsync(AppThemeMode.System).ConfigureAwait(true);
                await Task.Delay(200).ConfigureAwait(true);

                txtThemeOk = lightOk && darkOk && customOk && borderOk;
                txtThemeDetail = "亮色跟随=" + lightOk
                    + "；暗色跟随=" + darkOk + "（编辑区底色 " + ThemeHex(darkEditorBg) + "，期望 " + ThemeHex(darkLayer) + "）"
                    + "；自定义跟随=" + customOk + "（编辑区底色 " + ThemeHex(customEditorBg)
                    + "，期望层底色 " + ThemeHex(customLayer) + "，不是内置暗色 " + ThemeHex(darkLayer) + "）"
                    + "；自定义档按钮/输入框描边对底色 "
                    + borderContrast.ToString("0.00", CultureInfo.InvariantCulture) + ":1（≥1.5 才算凸显）";
            }

            Add("J2-txt-editor-follows-app-theme", txtThemeOk, txtThemeDetail);

            if (libraryEditor is not null && libraryEditor.IsVisible)
            {
                libraryEditor.Close();
                await Task.Delay(300).ConfigureAwait(true);
            }

            // ---------- K. 阅读器「编辑本章」：同样是全文编辑，但光标落在本章标题行 ----------
            /* K 前置（复现用户 2026-10-20 报的 bug）：把**阅读缓存清单里的 sourcePath** 改成不存在的路径。
               缓存命中时清单是直接复用的，里面的绝对路径可能是建缓存那一刻的（书重新导入/迁移后就失效）；
               「编辑本章」如果拿清单路径去开文件，就会"载入失败：Could not find file …"。
               正确做法是用本次从书库取到的最新路径（session.SourcePath）。 */
            var manifestPath = Path.Combine(session.CacheRoot, ReaderEndpoints.ManifestFileName);
            var staleSourcePath = Path.Combine(session.CacheRoot, "这份旧路径已经失效.txt");
            var manifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);
            var rewritten = Regex.Replace(
                manifestJson,
                "\"sourcePath\"\\s*:\\s*\"[^\"]*\"",
                "\"sourcePath\": \"" + staleSourcePath.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"");

            File.WriteAllText(manifestPath, rewritten, new UTF8Encoding(false));

            window.Close();
            await Task.Delay(400).ConfigureAwait(true);

            window = new ReaderWindow(
                reader, environments, progress, loggerFactory.CreateLogger<ReaderWindow>(), bookId,
                config: config, libraryScan: scan);

            window.Show();
            await WaitUntilAsync(() => window.LastChapterIndex >= 0, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            await Task.Delay(500).ConfigureAwait(true);

            var cachedPathIsStale = !string.IsNullOrEmpty(window.CachedSourcePath)
                && !string.Equals(window.CachedSourcePath, window.LiveSourcePath, StringComparison.OrdinalIgnoreCase)
                && !File.Exists(window.CachedSourcePath);

            var targetChapter = Math.Min(2, session.Manifest.Spine.Count - 1);
            await window.GoToChapterAsync(targetChapter).ConfigureAwait(true);
            await Task.Delay(800).ConfigureAwait(true);

            var readerEditorsBefore = Application.Current.Windows.OfType<TxtEditorWindow>().Count();
            await window.OpenEditorForChapterAsync(targetChapter).ConfigureAwait(true);

            var readerEntryOpened = await WaitUntilAsync(
                () => Application.Current.Windows.OfType<TxtEditorWindow>().Count() > readerEditorsBefore,
                TimeSpan.FromSeconds(20)).ConfigureAwait(true);

            var chapterEditor = Application.Current.Windows.OfType<TxtEditorWindow>().LastOrDefault();
            var chapterEditorReady = chapterEditor is not null
                && await WaitUntilAsync(() => chapterEditor.IsReady, TimeSpan.FromSeconds(60)).ConfigureAwait(true);

            var expectedStart = session.Manifest.Spine.Count > targetChapter
                ? (int)session.Manifest.Spine[targetChapter].TextStart
                : -1;

            Add(
                "K-txt-editor-jumps-to-chapter",
                readerEntryOpened && chapterEditorReady && cachedPathIsStale
                    && chapterEditor?.InitialOffset == expectedStart
                    && chapterEditor.InitialLine > 1,
                "缓存清单里的路径已失效=" + cachedPathIsStale
                    + "（清单=" + (window.CachedSourcePath ?? "?") + "，实际=" + (window.LiveSourcePath ?? "?") + "）"
                    + "；第 " + (targetChapter + 1) + " 章「" + session.Manifest.Spine[targetChapter].Title + "」"
                    + " 期望字符下标=" + expectedStart
                    + " 实际=" + (chapterEditor?.InitialOffset ?? -1)
                    + " 落第 " + (chapterEditor?.InitialLine ?? -1) + " 行（必须 > 1）");

            /* 截图存证：TXT 全文编辑器真的开出来了、光标停在章节标题行、工具栏上有编码下拉。 */
            Directory.CreateDirectory(paths.TempDir);
            if (chapterEditor is not null)
            {
                CaptureWindow(chapterEditor, Path.Combine(paths.TempDir, "txt-editor-chapter.png"));
            }

            // ---------- L. 全文编辑保存：整本写回原文件、编码不变、阅读器重建缓存后能看到新文字 ----------
            if (chapterEditor is not null && chapterEditorReady)
            {
                var sourcePath = session.SourcePath;
                var beforeBytes = File.ReadAllBytes(sourcePath);
                var marker = "【TXT全本编辑" + DateTime.Now.ToString("HHmmss") + "】";
                var text = chapterEditor.Editor.Text;

                /* 标记插在**本章正文里**（章标题之后 20 个字符处），不插在章标题前面：
                   插在章标题前会变成上一章的结尾，本章内容一个字都不变，L2 就没法用
                   "当前章正文长度变了"来证明阅读器真的刷到新内容。 */
                var insertAt = Math.Min(Math.Max(0, chapterEditor.InitialOffset) + 20, text.Length);
                var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

                chapterEditor.Editor.Text = text[..insertAt] + marker + newline + text[insertAt..];

                // 保存前先记下阅读器 iframe 里的正文长度：保存后阅读器自动刷新会让它变长（多出 marker）
                await window.RefreshRenderedTextAsync().ConfigureAwait(true);
                var renderedBeforeSave = window.RenderedTextLength;

                var saved = await chapterEditor.SaveAsync().ConfigureAwait(true);
                var afterBytes = File.ReadAllBytes(sourcePath);
                var reloaded = TxtTextFile.Load(sourcePath, session.Manifest.TextEncoding);
                var readerShows = await ReaderShowsTextAsync(reader, bookId, marker, 5).ConfigureAwait(true);

                /* 用户 2026-10-20 要求：用 TXT 编辑器改书属于**内部修改**，
                   书库不能把它报成「文件被外部修改」。保存后基线应已同步成磁盘现状。 */
                var scanReport = scan?.LastReport;
                var scanHasNoProblem = scan is not null && scanReport is not null
                    && scanReport.Problems.All(problem => problem.BookId != bookId)
                    && scan.GetBookState(bookId) == BookFileState.Normal;
                var dbBook = repository is null ? null : await repository.GetByIdAsync(bookId).ConfigureAwait(true);
                var dbSizeSynced = dbBook?.Formats
                    .FirstOrDefault(f => f.Format == BookFormatKind.Txt)?.FileSize == afterBytes.Length;

                Add(
                    "L-txt-full-edit-saves-whole-file",
                    saved
                        && afterBytes.Length > beforeBytes.Length
                        && reloaded.Text.Contains(marker, StringComparison.Ordinal)
                        && string.Equals(reloaded.EncodingName, session.Manifest.TextEncoding, StringComparison.OrdinalIgnoreCase)
                        && reloaded.HasBom == (session.Manifest.TextBomLength > 0)
                        && readerShows
                        && Directory.GetFiles(Path.GetDirectoryName(sourcePath)!, "*.part").Length == 0
                        && scanHasNoProblem
                        && dbSizeSynced,
                    "保存=" + saved + "；文件 " + beforeBytes.Length + " -> " + afterBytes.Length + " 字节"
                        + "；写回后编码=" + reloaded.EncodingName + "（原 " + session.Manifest.TextEncoding + "）"
                        + " BOM=" + reloaded.HasBom + "（原 " + (session.Manifest.TextBomLength > 0) + "）"
                        + "；阅读器重建后能看到新文字=" + readerShows + "；残留 .part=无"
                        + "；书库状态正常（内部修改不算外部修改）=" + scanHasNoProblem
                        + "、库里体积跟上磁盘=" + dbSizeSynced);

                // ---------- L2. 保存后阅读器自动刷新 + 翻下一章不是 Chromium 错误页（用户 2026-10-21 报的问题） ----------
                /* 用户实测（TXT 编辑器）：从阅读器「编辑本章」进去改文字 → 保存 → 退出编辑器 → 翻下一章，
                   正文区域冒出 Chromium 的「它可能已被移动、编辑或删除」错误页。EPUB 编辑器没有这个问题。
                   根因（本次验证）：TXT 保存会把 book_formats.quick_fingerprint 换成新值（内部修改同步基线），
                   而阅读缓存根目录就是按这个指纹命名的 → 目录换名了；WebView 的 http://ebook.local/ 映射
                   还指着初始化时那个老目录 → 重建后导航 reader.html 直接 404（日志 navigation failed），
                   屏幕上留的还是旧页面；再翻下一章时章节文件物化在新目录，旧页面却按老目录取 → 404 错误页。 */
                var reloadedSession = await reader.OpenAsync(bookId, CancellationToken.None).ConfigureAwait(true);
                var cacheRootChanged = !string.Equals(
                    reloadedSession.CacheRoot, session.CacheRoot, StringComparison.OrdinalIgnoreCase);

                // 阅读器自己刷新：iframe 正文长度因为多了 marker 而变长，且不是错误页。
                var reloadDeadline = DateTimeOffset.Now + TimeSpan.FromSeconds(40);
                var readerReloadedNewContent = false;

                while (DateTimeOffset.Now < reloadDeadline)
                {
                    await window.RefreshRenderedTextAsync().ConfigureAwait(true);

                    if (!window.RenderedIsErrorPage && window.RenderedTextLength > renderedBeforeSave)
                    {
                        readerReloadedNewContent = true;
                        break;
                    }

                    await Task.Delay(300).ConfigureAwait(true);
                }

                var renderedAfterSave = window.RenderedTextLength;
                var reloadChapterShown = window.LastChapterIndex;
                var stayedOnChapter = reloadChapterShown == targetChapter;

                // 像用户一样点「下一章」：走页面上那颗按钮（JS → 宿主物化 → gotoChapter）。
                var nextIndex = Math.Min(targetChapter + 1, reloadedSession.Manifest.Spine.Count - 1);
                var nextHref = reloadedSession.Manifest.Spine[nextIndex].Href;
                var oldRootNextFile = Path.Combine(
                    session.CacheRoot, nextHref.Replace('/', Path.DirectorySeparatorChar));
                var newRootNextFile = Path.Combine(
                    reloadedSession.CacheRoot, nextHref.Replace('/', Path.DirectorySeparatorChar));

                await window.ClickNextButtonAsync().ConfigureAwait(true);

                var nextChapterReached = await WaitUntilAsync(
                    () => window.LastChapterIndex == nextIndex, TimeSpan.FromSeconds(20)).ConfigureAwait(true);

                await Task.Delay(1200).ConfigureAwait(true);
                await window.RefreshRenderedTextAsync().ConfigureAwait(true);

                var nextChapterRendered = nextChapterReached
                    && !window.RenderedIsErrorPage
                    && window.RenderedTextLength > 0
                    && window.RenderedLocation.EndsWith("/" + nextHref, StringComparison.OrdinalIgnoreCase);

                Add(
                    "L2-reader-refreshes-after-txt-save",
                    readerReloadedNewContent && stayedOnChapter && nextChapterRendered,
                    "保存后阅读器自动换上新正文=" + readerReloadedNewContent
                        + "（iframe 正文长度 " + renderedBeforeSave + " -> " + renderedAfterSave + "）"
                        + "、还停在第 " + (reloadChapterShown + 1) + " 章=" + stayedOnChapter
                        + "；阅读缓存根因指纹变化而改名=" + cacheRootChanged
                        + "（" + Path.GetFileName(session.CacheRoot) + " -> " + Path.GetFileName(reloadedSession.CacheRoot) + "）"
                        + "；点「下一章」到第 " + (nextIndex + 1) + " 章=" + nextChapterReached
                        + "、正文位置=" + (string.IsNullOrEmpty(window.RenderedLocation) ? "(空)" : window.RenderedLocation)
                        + "、正文长度=" + window.RenderedTextLength
                        + "、Chromium 错误页=" + window.RenderedIsErrorPage
                        + "；下一章文件老缓存根有=" + File.Exists(oldRootNextFile)
                        + "、新缓存根有=" + File.Exists(newRootNextFile));

                // ---------- M. 编码选择器两边共用：编辑器里切 -> 阅读器跟着切 + 落到这本书的配置 ----------
                const string sharedEncoding = "GB18030";   // GBK 字节的父集：切过去正文仍然可读，便于比较
                var switchedInEditor = await chapterEditor.ApplyEncodingAsync(sharedEncoding).ConfigureAwait(true);

                var readerFollowed = await WaitUntilAsync(
                    () => string.Equals(window.LastTextEncoding, sharedEncoding, StringComparison.OrdinalIgnoreCase),
                    TimeSpan.FromSeconds(40)).ConfigureAwait(true);

                var bookKey = ReaderWindow.TextEncodingKeyPrefix + bookId.ToString("D");
                var storedEncoding = config?.GetString(bookKey, null);

                Add(
                    "M-encoding-shared-with-editor",
                    switchedInEditor && readerFollowed
                        && string.Equals(chapterEditor.DocumentEncoding, sharedEncoding, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(storedEncoding, sharedEncoding, StringComparison.OrdinalIgnoreCase),
                    "编辑器切到 " + sharedEncoding + "=" + switchedInEditor
                        + "；编辑器当前编码=" + (chapterEditor.DocumentEncoding ?? "?")
                        + "；阅读器跟随=" + readerFollowed + "（现在 " + (window.LastTextEncoding ?? "?") + "）"
                        + "；Config[" + bookKey + "]=" + (storedEncoding ?? "(空)"));

                CaptureWindow(chapterEditor, Path.Combine(paths.TempDir, "txt-editor-encoding.png"));

                // ---------- N. 每本书各自记忆：另一本 TXT 仍走默认（自动识别），不继承这本书的选择 ----------
                Directory.CreateDirectory(paths.TempDir);
                var otherPath = Path.Combine(paths.TempDir, "txt-smoke-other-utf8.txt");
                var otherBody = "这是另一本书的正文，用来验证每本书各自记住自己的文本编码，不会沿用上一本的选择。";
                File.WriteAllText(
                    otherPath,
                    "第一章 甲\n" + otherBody + "\n\n第二章 乙\n" + otherBody + "\n",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                var otherImport = await import
                    .ImportAsync(otherPath, ImportOptions.Default, null, CancellationToken.None)
                    .ConfigureAwait(true);

                var otherBookId = otherImport.BookId ?? Guid.Empty;
                var otherSession = await reader.OpenAsync(otherBookId, CancellationToken.None).ConfigureAwait(true);
                var otherCjk = await CountCjkAfterOpenAsync(reader, otherBookId).ConfigureAwait(true);
                var otherKey = ReaderWindow.TextEncodingKeyPrefix + otherBookId.ToString("D");

                Add(
                    "N-encoding-per-book",
                    string.Equals(otherSession.Manifest.TextEncoding, "UTF-8", StringComparison.OrdinalIgnoreCase)
                        && otherCjk > 20
                        && config?.GetString(otherKey, null) is null,
                    "另一本 TXT（UTF-8）：识别/默认编码=" + otherSession.Manifest.TextEncoding + "（期望 UTF-8）"
                        + "；首章汉字数=" + otherCjk + "（> 20 说明没被按 GB18030 解成乱码）"
                        + "；这本书没有继承上一本的编码设置=" + (config?.GetString(otherKey, null) is null));

                // ---------- O. 重开这本书：延续上次选的编码 ----------
                var reopened = new ReaderWindow(
                    reader, environments, progress, loggerFactory.CreateLogger<ReaderWindow>(), bookId,
                    config: config, libraryScan: scan);

                reopened.Show();

                try
                {
                    var remembered = await WaitUntilAsync(
                        () => string.Equals(reopened.LastTextEncoding, sharedEncoding, StringComparison.OrdinalIgnoreCase),
                        TimeSpan.FromSeconds(40)).ConfigureAwait(true);

                    Add(
                        "O-encoding-remembered-on-reopen",
                        remembered
                            && string.Equals(reopened.LastTextEncoding, sharedEncoding, StringComparison.OrdinalIgnoreCase),
                        "重新打开这本书用的编码=" + (reopened.LastTextEncoding ?? "?")
                            + "（上次在编辑器里选的 " + sharedEncoding + "，期望沿用）");
                }
                finally
                {
                    reopened.Close();
                    await Task.Delay(300).ConfigureAwait(true);
                }

                chapterEditor.Close();
                await Task.Delay(300).ConfigureAwait(true);

                // ---------- P. 缩放控件：[-] 100% [+]、Ctrl+滚轮、百分比可键盘输入（用户 2026-10-20 补充要求） ----------
                /* 用户口径：
                   · 工具栏显示放大比例（默认 100%），百分比左右各一个按钮，−/+ 按 10% 步进，范围 1%–500%；
                   · Ctrl+鼠标滚轮 = 放大/缩小，每格 10%（和浏览器一样）；
                   · 百分比本身可键盘输入：输 1 就是精确 1%，越界夹到区间内，非数字回到合法值且不崩；
                   · 字号 = 16.8 × 百分比 / 100（100% = 原来 120% 的大小，用户 2026-10-20 要求），字号变了自动换行要实时重排。
                   用"整本 TXT 编辑"窗口直接测（不是另测一条路）；测完丢弃修改、绝不保存。 */
                var findEditor = new TxtEditorWindow(
                    bookId,
                    session.Manifest.Title,
                    session.SourcePath,
                    sharedEncoding,
                    0,
                    config,
                    scan);

                findEditor.Show();

                try
                {
                    var findEditorReady = await WaitUntilAsync(
                        () => findEditor.IsReady, TimeSpan.FromSeconds(60)).ConfigureAwait(true);

                    await Task.Delay(300).ConfigureAwait(true);

                    // 还没打开查找面板：工具栏上的查找计数不该出现（用户 2026-10-20 要求）。
                    var findCountHiddenAtStart = findEditor.FindCountText.Visibility == Visibility.Collapsed;

                    // -- 默认 100% / 字号 16.8（= 原来的 120%） --
                    var zoomInitial = findEditor.ZoomPercent == 100
                        && Math.Abs(findEditor.EditorFontSize - 16.8) < 0.0001
                        && string.Equals(findEditor.ZoomText, "100%", StringComparison.Ordinal);

                    // -- −/+ 各点一次（真实点按钮）：100% -> 90% -> 100% --
                    findEditor.ZoomOutButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    var minusPercent = findEditor.ZoomPercent;
                    var minusFont = findEditor.EditorFontSize;

                    findEditor.ZoomInButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    var plusPercent = findEditor.ZoomPercent;

                    // -- Ctrl+滚轮（内部等价逻辑，等于按住 Ctrl 滚一格）：+10 / -10 --
                    findEditor.ApplyZoomWheel(120);
                    var wheelUpPercent = findEditor.ZoomPercent;
                    var wheelUpFont = findEditor.EditorFontSize;

                    findEditor.ApplyZoomWheel(-120);
                    var wheelDownPercent = findEditor.ZoomPercent;

                    // -- 上限夹取：一直放大停在 500%，字号 70 --
                    findEditor.SetZoomPercent(100);
                    for (var i = 0; i < 60; i++)
                    {
                        findEditor.ZoomIn();
                    }

                    var maxPercent = findEditor.ZoomPercent;
                    var maxFont = findEditor.EditorFontSize;

                    // -- 自动换行随字号实时重排：150% 的行高必须明显高于 100%（WordWrap 仍开着）--
                    findEditor.SetZoomPercent(100);
                    await Task.Delay(300).ConfigureAwait(true);
                    findEditor.UpdateLayout();
                    var baseLineHeight = findEditor.Editor.TextArea.TextView.DefaultLineHeight;

                    findEditor.SetZoomPercent(150);
                    await Task.Delay(300).ConfigureAwait(true);
                    findEditor.UpdateLayout();
                    var zoomedLineHeight = findEditor.Editor.TextArea.TextView.DefaultLineHeight;

                    var reflowed = zoomedLineHeight > baseLineHeight * 1.2;
                    var wordWrapKept = findEditor.Editor.WordWrap;

                    findEditor.SetZoomPercent(100);

                    Add(
                        "P-zoom-controls",
                        findEditorReady && zoomInitial
                            && minusPercent == 90 && Math.Abs(minusFont - 15.12) < 0.0001
                            && plusPercent == 100
                            && wheelUpPercent == 110 && Math.Abs(wheelUpFont - 18.48) < 0.0001
                            && wheelDownPercent == 100
                            && maxPercent == 500 && Math.Abs(maxFont - 84) < 0.0001
                            && reflowed && wordWrapKept,
                        "默认=" + (zoomInitial ? "100%（字号 16.8，文本 \"100%\"）" : "不对")
                            + "；−按钮 100%->" + minusPercent + "%（字号 " + minusFont.ToString("0.0", CultureInfo.InvariantCulture) + "）"
                            + "；+按钮 ->" + plusPercent + "%"
                            + "；Ctrl+滚轮 ↑->" + wheelUpPercent + "%（字号 " + wheelUpFont.ToString("0.0", CultureInfo.InvariantCulture) + "）"
                            + " ↓->" + wheelDownPercent + "%"
                            + "；连按放大到上限 " + maxPercent + "%（字号 " + maxFont.ToString("0", CultureInfo.InvariantCulture) + "，范围 1–500）"
                            + "；150% 行高 " + zoomedLineHeight.ToString("0.0", CultureInfo.InvariantCulture)
                            + " > 100% 行高 " + baseLineHeight.ToString("0.0", CultureInfo.InvariantCulture)
                            + "（自动换行实时重排=" + reflowed + "，WordWrap=" + wordWrapKept + "）");

                    // ---------- Q. 百分比可键盘输入：1% 精确生效、越界夹取、非数字不崩 ----------
                    var acceptedOne = findEditor.ApplyZoomInput("1");
                    var onePercent = findEditor.ZoomPercent;
                    var oneFont = findEditor.EditorFontSize;
                    var oneText = findEditor.ZoomText;

                    var acceptedHuge = findEditor.ApplyZoomInput("9999");
                    var hugePercent = findEditor.ZoomPercent;

                    var acceptedBad = findEditor.ApplyZoomInput("不是数字");
                    var badPercent = findEditor.ZoomPercent;
                    var badText = findEditor.ZoomText;

                    findEditor.ApplyZoomInput("100");

                    Add(
                        "Q-zoom-input",
                        findEditorReady
                            && acceptedOne && onePercent == 1 && Math.Abs(oneFont - 0.168) < 1e-9
                            && string.Equals(oneText, "1%", StringComparison.Ordinal)
                            && acceptedHuge && hugePercent == 500
                            && !acceptedBad && badPercent == 500
                            && string.Equals(badText, "500%", StringComparison.Ordinal)
                            && findEditor.ZoomPercent == 100,
                        "百分比框输入 1 回车 -> " + onePercent + "%（字号 " + oneFont.ToString("0.###", CultureInfo.InvariantCulture)
                            + " = 16.8×1/100，显示 \"" + oneText + "\"）"
                            + "；输入 9999 -> " + hugePercent + "%（夹到上限，不崩）"
                            + "；输入非数字 -> " + (acceptedBad ? "被接受(不对)" : "回到合法值 " + badPercent + "%（显示 \"" + badText + "\"）")
                            + "；再输 100 -> " + findEditor.ZoomPercent + "%");

                    // ---------- R1. 先选中正文再点「查找」：自动填入查找框，红色当前命中落在选中处 ----------
                    /* 用户 2026-10-20 要求：
                       · 正文里先选定文字，再点「查找」，选中的文字自动填进查找框并立即查找；
                       · 查找的初始定位就在"选中的那个位置"（红色高亮 = 选中的那一处），
                         「下一个」从这一处往后走，而不是从文首开始。 */
                    const string findTerm = "主角说";
                    var selectText = findEditor.Editor.Text;
                    var selectOffsets = FindAllOffsets(selectText, findTerm);
                    const int selectTargetIndex = 2;                 // 第 3 处
                    var selectTargetOffset = selectOffsets[selectTargetIndex];

                    findEditor.Editor.Select(selectTargetOffset, findTerm.Length);
                    findEditor.FindButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                    var filledQuery = findEditor.FindBox.Text;
                    var selectCurrentOffset = findEditor.CurrentMatchOffset;
                    var selectCurrentNumber = findEditor.CurrentMatchNumber;
                    var selectCountValue = findEditor.FindCountValue;
                    var selectRedAtTarget = ReferenceEquals(
                        findEditor.HighlightBrushAt(selectTargetOffset), TxtSearchColorizer.CurrentBackground);
                    var selectYellowAtFirst = ReferenceEquals(
                        findEditor.HighlightBrushAt(selectOffsets[0]), TxtSearchColorizer.MatchBackground);
                    var selectCollapsed = findEditor.Editor.SelectionLength == 0;
                    var selectCountVisible = findEditor.FindCountText.Visibility == Visibility.Visible;
                    var selectCountInToolbar = findEditor.FindCountText.Parent is DockPanel
                        && !findEditor.FindPanel.IsAncestorOf(findEditor.FindCountText);
                    var countLabelBrush = findEditor.FindResource("DangerTextBrush") as SolidColorBrush;
                    var selectCountRed = countLabelBrush is not null
                        && findEditor.FindCountText.Foreground is SolidColorBrush countLabelForeground
                        && countLabelForeground.Color == countLabelBrush.Color;
                    var expectedSelectCountText = "第 " + (selectTargetIndex + 1).ToString(CultureInfo.InvariantCulture)
                        + " / " + selectOffsets.Count.ToString(CultureInfo.InvariantCulture) + " 处";

                    // 截图存证：查找框里自动填入了"主角说"，红色当前命中在选中的第 3 处。
                    Directory.CreateDirectory(paths.TempDir);
                    await Task.Delay(250).ConfigureAwait(true);
                    findEditor.UpdateLayout();
                    CaptureWindow(findEditor, Path.Combine(paths.TempDir, "txt-editor-find-from-selection.png"));

                    // 「下一个」从选中处往后走（不是从头开始），「上一个」再回到选中处。
                    findEditor.FindNextButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    var selectNextNumber = findEditor.CurrentMatchNumber;
                    var selectNextOffset = findEditor.CurrentMatchOffset;
                    findEditor.FindPreviousButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    var selectBackOffset = findEditor.CurrentMatchOffset;

                    Add(
                        "R1-find-from-selection",
                        findEditorReady
                            && findEditor.SearchPanelVisible
                            && string.Equals(filledQuery, findTerm, StringComparison.Ordinal)
                            && findEditor.MatchCount == selectOffsets.Count
                            && findEditor.MatchOffsets.SequenceEqual(selectOffsets)
                            && selectCurrentOffset == selectTargetOffset
                            && selectCurrentNumber == selectTargetIndex + 1
                            && string.Equals(selectCountValue, expectedSelectCountText, StringComparison.Ordinal)
                            && selectRedAtTarget && selectYellowAtFirst && selectCollapsed
                            && selectNextNumber == selectTargetIndex + 2
                            && selectNextOffset == selectOffsets[selectTargetIndex + 1]
                            && selectBackOffset == selectTargetOffset,
                        "先选中第 " + (selectTargetIndex + 1) + " 处「" + findTerm + "」再点「查找」：查找框自动填成 \""
                            + filledQuery + "\"（应为 \"" + findTerm + "\"）"
                            + "；红色当前命中 = 选中的位置（下标 " + selectCurrentOffset + "，应为 " + selectTargetOffset + "）"
                            + "；计数=\"" + selectCountValue + "\"（应为 \"" + expectedSelectCountText + "\"）"
                            + "；选中已收起、红色不被选择底色压住=" + selectCollapsed
                            + "；「下一个」-> 第 " + selectNextNumber + " 处（下标 " + selectNextOffset + "，= 选中处往后一处）"
                            + "；「上一个」回到下标 " + selectBackOffset);

                    // ---------- R2. 选中文字跨多行：只看第一行（查找框是单行输入），仍定位在选中处 ----------
                    var multilineEnd = selectOffsets[selectTargetIndex + 1] + findTerm.Length;
                    findEditor.Editor.Select(selectTargetOffset, multilineEnd - selectTargetOffset);
                    var multilineSelection = findEditor.Editor.SelectedText;
                    var multilineBreak = multilineSelection.IndexOfAny(new[] { '\r', '\n' });
                    var expectedFirstLine = multilineBreak > 0
                        ? multilineSelection[..multilineBreak]
                        : multilineSelection;
                    var firstLineOffsets = FindAllOffsets(selectText, expectedFirstLine);

                    findEditor.FindButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                    var multilineQuery = findEditor.FindBox.Text;
                    var multilineCount = findEditor.MatchCount;
                    var multilineCurrentOffset = findEditor.CurrentMatchOffset;
                    var multilineCollapsed = findEditor.Editor.SelectionLength == 0;

                    // 收尾：关掉面板，把状态还给下面 R 的"从文首开始"口径。
                    findEditor.FindCloseButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    var panelClosedBack = !findEditor.SearchPanelVisible && findEditor.MatchCount == 0;
                    var countHiddenAfterClose = findEditor.FindCountText.Visibility == Visibility.Collapsed;

                    Add(
                        "R2-find-from-multiline-selection",
                        findEditorReady
                            && multilineBreak > 0
                            && string.Equals(multilineQuery, expectedFirstLine, StringComparison.Ordinal)
                            && !multilineQuery.Contains('\n') && !multilineQuery.Contains('\r')
                            && multilineCount == firstLineOffsets.Count
                            && multilineCurrentOffset == selectTargetOffset
                            && multilineCollapsed
                            && panelClosedBack,
                        "跨行选中 " + multilineSelection.Length + " 个字符（含换行）：查找框只填第一行 \""
                            + multilineQuery + "\"（" + multilineQuery.Length + " 字）"
                            + "；命中 " + multilineCount + " 处、红色在下标 " + multilineCurrentOffset
                            + "（= 选中处 " + selectTargetOffset + "）"
                            + "；选中已收起=" + multilineCollapsed
                            + "；随后关面板清掉高亮=" + panelClosedBack);

                    // ---------- R3. 查找计数显示在工具栏右侧（红字，面板开才出现） ----------
                    /* 用户 2026-10-20 要求：点「查找」后，在工具栏右侧（截图里圈出来的位置，紧挨「共 N 字」）
                       用红字写出"当前定位第几处 / 一共多少处"；查找替换这一行关掉时不显示；
                       替换之后数字要实时跟着变（U/V 两项已经在断言计数随替换变化）。 */
                    Add(
                        "R3-find-count-in-toolbar",
                        findEditorReady
                            && findCountHiddenAtStart
                            && selectCountVisible
                            && selectCountInToolbar
                            && selectCountRed
                            && countHiddenAfterClose,
                        "打开查找前隐藏=" + findCountHiddenAtStart
                            + "；打开后可见=" + selectCountVisible
                            + "、内容=\"" + selectCountValue + "\""
                            + "；显示在工具栏右侧（不在查找面板里）=" + selectCountInToolbar
                            + "；红字（DangerTextBrush）=" + selectCountRed
                            + "；关闭面板后隐藏=" + countHiddenAfterClose);

                    // ---------- R. 查找：面板打开、所有命中黄底、当前那一处红底、计数"第 n / m 个" ----------
                    findEditor.FindButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    var panelOpened = findEditor.SearchPanelVisible;

                    findEditor.FindBox.Text = findTerm;

                    var searchText = findEditor.Editor.Text;
                    var expectedOffsets = FindAllOffsets(searchText, findTerm);
                    var (redCount, yellowCount, noneCount) = CountHighlightedOffsets(findEditor, expectedOffsets);
                    var nonMatchOffset = FindNonMatchOffset(searchText, expectedOffsets, findTerm.Length);
                    var expectedCountText = "第 1 / " + expectedOffsets.Count.ToString(CultureInfo.InvariantCulture) + " 处";

                    var highlightOk = panelOpened
                        && expectedOffsets.Count > 2
                        && findEditor.MatchCount == expectedOffsets.Count
                        && findEditor.MatchOffsets.SequenceEqual(expectedOffsets)
                        && findEditor.CurrentMatchNumber == 1
                        && findEditor.CurrentMatchOffset == expectedOffsets[0]
                        && string.Equals(findEditor.FindCountValue, expectedCountText, StringComparison.Ordinal)
                        && ReferenceEquals(findEditor.HighlightBrushAt(expectedOffsets[0]), TxtSearchColorizer.CurrentBackground)
                        && ReferenceEquals(findEditor.HighlightBrushAt(expectedOffsets[1]), TxtSearchColorizer.MatchBackground)
                        && (nonMatchOffset < 0 || findEditor.HighlightBrushAt(nonMatchOffset) is null)
                        && findEditor.IsSearchColorizerAttached
                        && redCount == 1 && yellowCount == expectedOffsets.Count - 1 && noneCount == 0;

                    Add(
                        "R-find-highlight",
                        findEditorReady && highlightOk,
                        "查找「" + findTerm + "」命中 " + findEditor.MatchCount + " 处（独立扫描 " + expectedOffsets.Count + "）"
                            + "；当前第 " + findEditor.CurrentMatchNumber + " 处=红底、其余 " + yellowCount + " 处=黄底、无高亮 " + noneCount + " 处"
                            + "；非命中处无高亮=" + (nonMatchOffset < 0 || findEditor.HighlightBrushAt(nonMatchOffset) is null)
                            + "；计数=\"" + findEditor.FindCountValue + "\""
                            + "；高亮已挂到 AvalonEdit 渲染链上=" + findEditor.IsSearchColorizerAttached);

                    // 截图存证：内联查找面板 + 黄色全部命中 + 红色当前命中。
                    Directory.CreateDirectory(paths.TempDir);
                    await Task.Delay(250).ConfigureAwait(true);
                    findEditor.UpdateLayout();
                    CaptureWindow(findEditor, Path.Combine(paths.TempDir, "txt-editor-find.png"));

                    // ---------- S. 上一个/下一个只移动红色高亮（到头循环），不改正文 ----------
                    var textBeforeNavigate = findEditor.Editor.Text;

                    findEditor.FindNextButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    var nextNumber = findEditor.CurrentMatchNumber;
                    var nextOffset = findEditor.CurrentMatchOffset;
                    var nextRedAtSecond = ReferenceEquals(
                            findEditor.HighlightBrushAt(expectedOffsets[1]), TxtSearchColorizer.CurrentBackground)
                        && ReferenceEquals(
                            findEditor.HighlightBrushAt(expectedOffsets[0]), TxtSearchColorizer.MatchBackground);

                    findEditor.FindPreviousButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    var prevNumber = findEditor.CurrentMatchNumber;

                    findEditor.FindPrevious();          // 从第 1 个再往前 -> 绕到最后一个
                    var wrapPrevNumber = findEditor.CurrentMatchNumber;
                    var wrapPrevOffset = findEditor.CurrentMatchOffset;

                    findEditor.FindNext();              // 从最后一个再往后 -> 绕回第 1 个
                    var wrapNextNumber = findEditor.CurrentMatchNumber;

                    Add(
                        "S-find-navigation",
                        nextNumber == 2 && nextOffset == expectedOffsets[1] && nextRedAtSecond
                            && prevNumber == 1
                            && wrapPrevNumber == expectedOffsets.Count
                            && wrapPrevOffset == expectedOffsets[^1]
                            && wrapNextNumber == 1
                            && string.Equals(findEditor.Editor.Text, textBeforeNavigate, StringComparison.Ordinal)
                            && !findEditor.IsDirty,
                        "「下一个」-> 第 " + nextNumber + " 处（红色移到第 2 个=" + nextRedAtSecond + "）"
                            + "；「上一个」-> 第 " + prevNumber + " 处"
                            + "；第 1 个再往前 -> 第 " + wrapPrevNumber + " 处（循环到最后一个）"
                            + "；最后一个再往后 -> 第 " + wrapNextNumber + " 处（循环回第一个）"
                            + "；只挪红色高亮、正文未改=" + string.Equals(findEditor.Editor.Text, textBeforeNavigate, StringComparison.Ordinal)
                            + "、未变脏=" + (!findEditor.IsDirty));

                    // ---------- X. 自动换行的续行顶头显示（首行缩进只缩第一行；用户 2026-10-20 要求） ----------
                    /* 用户实测：段首有两个全角空格（TXT 的首行缩进），折行之后每一行也跟着缩进，
                       看起来像"整段向右缩进"。原因是 AvalonEdit 的 InheritWordWrapIndentation 默认 true——
                       它把首行前面的缩进复制给这一段的每个折行续行。关掉之后续行从最左边开始；
                       文字本身一个字都不改，只影响显示。
                       量法：换成一整段长文字 → 截窗口 → **从截图里量每行最左边的像素**（这就是用户看到的效果），
                       首行应比续行往右偏，续行之间左边缘对齐；量完立刻换回原文。 */
                    var wrapInheritOff = !findEditor.Editor.Options.InheritWordWrapIndentation
                        && Math.Abs(findEditor.Editor.Options.WordWrapIndentation) < 0.0001
                        && findEditor.Editor.WordWrap;

                    var textBeforeLongParagraph = findEditor.Editor.Text;
                    var longParagraph = "　　"
                        + string.Concat(Enumerable.Repeat("这一行故意写得很长，让自动换行把它折成好几行。", 8));

                    findEditor.Editor.Text = longParagraph;

                    // 光标挪到文末：光标竖线画在段首的话会被当成"首行最左边的字"，量出来就不准了。
                    findEditor.Editor.CaretOffset = longParagraph.Length;
                    findEditor.CloseFindPanel();
                    await Task.Delay(300).ConfigureAwait(true);
                    findEditor.UpdateLayout();

                    var wrapShotPath = Path.Combine(paths.TempDir, "txt-editor-wrap-indent.png");
                    CaptureWindow(findEditor, wrapShotPath);

                    var wrapDpi = VisualTreeHelper.GetDpi(findEditor);
                    var editorTopInWindow = findEditor.Editor.TranslatePoint(new Point(0, 0), findEditor).Y;
                    var textRows = MeasureTextRowLeftEdges(
                        wrapShotPath,
                        (int)((editorTopInWindow + 2) * wrapDpi.DpiScaleY));

                    var firstLineLeft = textRows.Count > 0 ? textRows[0].Left : -1;
                    var continuationLeft = textRows.Count > 1 ? textRows[1].Left : -1;
                    var secondContinuationLeft = textRows.Count > 2 ? textRows[2].Left : -1;
                    var charWidthPx = 16.8 * wrapDpi.DpiScaleX;
                    var firstLineIndented = firstLineLeft - continuationLeft > charWidthPx;
                    var continuationFlush = textRows.Count >= 3 && continuationLeft == secondContinuationLeft;

                    findEditor.Editor.Text = textBeforeLongParagraph;
                    findEditor.OpenFindPanel();
                    await Task.Delay(250).ConfigureAwait(true);

                    Add(
                        "X-wordwrap-continuation-flush",
                        findEditorReady && wrapInheritOff && textRows.Count >= 3
                            && firstLineIndented && continuationFlush
                            && findEditor.MatchCount == expectedOffsets.Count,
                        "自动换行续行不继承首行缩进=" + wrapInheritOff
                            + "；长段落折成 " + textRows.Count + " 行"
                            + "；首行最左像素 x=" + firstLineLeft
                            + "、续行 x=" + continuationLeft + "、" + secondContinuationLeft
                            + "（一个整字宽 " + charWidthPx.ToString("0.0", CultureInfo.InvariantCulture)
                            + "px：首行缩进=" + firstLineIndented + "，续行顶头对齐=" + continuationFlush + "）"
                            + "；换回原文后命中数=" + findEditor.MatchCount + "（应 " + expectedOffsets.Count + "）"
                            + "；截图 txt-editor-wrap-indent.png");

                    // ---------- T. 面板开着时手工编辑正文：命中/高亮/计数实时刷新 ----------
                    var insertedPrefix = findTerm + "\n";
                    findEditor.Editor.Text = insertedPrefix + searchText;
                    var textAfterInsert = findEditor.Editor.Text;
                    var expectedAfterInsert = FindAllOffsets(textAfterInsert, findTerm);
                    var (redAfterInsert, yellowAfterInsert, noneAfterInsert) =
                        CountHighlightedOffsets(findEditor, expectedAfterInsert);
                    var expectedCountAfterInsert = "第 " + findEditor.CurrentMatchNumber.ToString(CultureInfo.InvariantCulture)
                        + " / " + expectedAfterInsert.Count.ToString(CultureInfo.InvariantCulture) + " 处";

                    var liveRefreshOk = findEditor.MatchCount == expectedAfterInsert.Count
                        && findEditor.MatchOffsets.SequenceEqual(expectedAfterInsert)
                        && findEditor.CurrentMatchNumber >= 1
                        && findEditor.CurrentMatchNumber <= expectedAfterInsert.Count
                        && string.Equals(findEditor.FindCountValue, expectedCountAfterInsert, StringComparison.Ordinal)
                        && redAfterInsert == 1
                        && yellowAfterInsert == expectedAfterInsert.Count - 1
                        && noneAfterInsert == 0;

                    findEditor.Editor.Text = textAfterInsert.Substring(insertedPrefix.Length);
                    var backToOriginal = findEditor.MatchCount == expectedOffsets.Count
                        && findEditor.MatchOffsets.SequenceEqual(expectedOffsets);

                    Add(
                        "T-find-live-refresh",
                        findEditorReady && liveRefreshOk && backToOriginal,
                        "面板开着时在正文开头插入一处命中：命中数 " + expectedOffsets.Count + " -> "
                            + (expectedAfterInsert.Count) + "（红色/黄色实时重算=" + liveRefreshOk + "）"
                            + "；删掉后回到 " + findEditor.MatchCount + " 处（与原来一致=" + backToOriginal + "）"
                            + "；正文编辑后变脏=" + findEditor.IsDirty);

                    // ---------- U. 「替换」只替换红色那一处，高亮/计数实时刷新且**不写盘** ----------
                    var guard = 0;

                    while (findEditor.CurrentMatchNumber != 2 && guard++ < expectedOffsets.Count + 2)
                    {
                        findEditor.FindNext();
                    }

                    var redOffset = findEditor.CurrentMatchOffset;
                    var diskBeforeReplace = File.ReadAllBytes(session.SourcePath);

                    const string replacementText = "【统一替换】";
                    findEditor.ReplaceBox.Text = replacementText;
                    var replacedCurrent = findEditor.ReplaceCurrent();

                    var textAfterReplace = findEditor.Editor.Text;
                    var expectedAfterReplace = FindAllOffsets(textAfterReplace, findTerm);
                    var expectedIndexAfterReplace = expectedAfterReplace.FindIndex(
                        offset => offset >= redOffset + replacementText.Length);

                    if (expectedIndexAfterReplace < 0)
                    {
                        expectedIndexAfterReplace = 0;
                    }

                    var (redAfterReplace, yellowAfterReplace, noneAfterReplace) =
                        CountHighlightedOffsets(findEditor, expectedAfterReplace);
                    var expectedNumberAfterReplace = expectedAfterReplace.Count == 0 ? 0 : expectedIndexAfterReplace + 1;
                    var textAtRedOffsetReplaced = string.CompareOrdinal(
                        textAfterReplace, redOffset, replacementText, 0, replacementText.Length) == 0;

                    Add(
                        "U-find-replace-current",
                        replacedCurrent
                            && textAtRedOffsetReplaced
                            && findEditor.MatchCount == expectedAfterReplace.Count
                            && findEditor.MatchCount == expectedOffsets.Count - 1
                            && findEditor.MatchOffsets.SequenceEqual(expectedAfterReplace)
                            && findEditor.CurrentMatchNumber == expectedNumberAfterReplace
                            && redAfterReplace == 1
                            && yellowAfterReplace == Math.Max(0, expectedAfterReplace.Count - 1)
                            && noneAfterReplace == 0
                            && findEditor.IsDirty
                            && File.ReadAllBytes(session.SourcePath).SequenceEqual(diskBeforeReplace),
                        "只替换当前红色那一处=" + replacedCurrent + "（原下标 " + redOffset + " 处变成「" + replacementText + "」="
                            + textAtRedOffsetReplaced + "）"
                            + "；命中数 " + expectedOffsets.Count + " -> " + findEditor.MatchCount + "（高亮实时刷新：红 " + redAfterReplace
                            + "、黄 " + yellowAfterReplace + "、无 " + noneAfterReplace + "）"
                            + "；当前计数=\"" + findEditor.FindCountValue + "\""
                            + "；未保存未写盘=" + File.ReadAllBytes(session.SourcePath).SequenceEqual(diskBeforeReplace));

                    // ---------- V. 「全部替换」替换全部命中，计数归零且**不写盘** ----------
                    var remainingMatches = findEditor.MatchCount;
                    var diskBeforeReplaceAll = File.ReadAllBytes(session.SourcePath);
                    var replacedAll = findEditor.ReplaceAll();
                    var textAfterReplaceAll = findEditor.Editor.Text;
                    var replacementOccurrences = FindAllOffsets(textAfterReplaceAll, replacementText).Count;

                    Add(
                        "V-find-replace-all",
                        replacedAll == remainingMatches
                            && !textAfterReplaceAll.Contains(findTerm, StringComparison.OrdinalIgnoreCase)
                            && findEditor.MatchCount == 0
                            && findEditor.CurrentMatchNumber == 0
                            && findEditor.CurrentMatchOffset == -1
                            && findEditor.MatchOffsets.Count == 0
                            && string.Equals(findEditor.FindCountValue, "第 0 / 0 处", StringComparison.Ordinal)
                            && (textAfterReplaceAll.Length == 0 || findEditor.HighlightBrushAt(0) is null)
                            && replacementOccurrences == expectedOffsets.Count
                            && File.ReadAllBytes(session.SourcePath).SequenceEqual(diskBeforeReplaceAll),
                        "全部替换 " + replacedAll + " 处（替换前剩余 " + remainingMatches + " 处）"
                            + "；正文里已无「" + findTerm + "」=" + !textAfterReplaceAll.Contains(findTerm, StringComparison.OrdinalIgnoreCase)
                            + "；计数归零=" + (findEditor.MatchCount == 0) + "（\"" + findEditor.FindCountValue + "\"）"
                            + "；替换文本出现 " + replacementOccurrences + " 次（应 " + expectedOffsets.Count + " 次）"
                            + "；磁盘文件未被写=" + File.ReadAllBytes(session.SourcePath).SequenceEqual(diskBeforeReplaceAll));

                    // ---------- W. 工具栏说明文字已删干净 + 有未保存修改时关窗走内联面板（不弹模态框） ----------
                    var hintTextGone = !FindTextBlocks(findEditor)
                        .Any(textBlock => textBlock.Text.Contains("像记事本一样", StringComparison.Ordinal));

                    var diskBeforeClose = File.ReadAllBytes(session.SourcePath);
                    findEditor.Close();
                    await Task.Delay(300).ConfigureAwait(true);

                    var inlinePanelShown = findEditor.IsVisible
                        && findEditor.DirtyPanel.Visibility == Visibility.Visible;

                    findEditor.DirtyDropButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    var discardedClosed = await WaitUntilAsync(
                        () => !findEditor.IsVisible, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

                    Add(
                        "W-ui-hint-removed-and-discard",
                        hintTextGone && inlinePanelShown && discardedClosed
                            && File.ReadAllBytes(session.SourcePath).SequenceEqual(diskBeforeClose),
                        "工具栏「像记事本一样直接改文字，改完 Ctrl+S 保存」已删除=" + hintTextGone
                            + "；有未保存修改时关窗显示内联面板（非模态）=" + inlinePanelShown
                            + "；点「放弃修改」后关闭=" + discardedClosed
                            + "；全过程没有写盘=" + File.ReadAllBytes(session.SourcePath).SequenceEqual(diskBeforeClose));
                }
                finally
                {
                    if (findEditor.IsVisible)
                    {
                        findEditor.Close();
                        await Task.Delay(200).ConfigureAwait(true);

                        if (findEditor.IsVisible && findEditor.DirtyPanel.Visibility == Visibility.Visible)
                        {
                            findEditor.DirtyDropButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            await Task.Delay(200).ConfigureAwait(true);
                        }
                    }
                }
            }
        }
        finally
        {
            window.Close();
            await Task.Delay(300).ConfigureAwait(true);
        }

        return Build(appPaths, startedAt, total, checks);
    }

    /// <summary>重新打开并数一数第一章里的汉字数（换编码后内容会变）。</summary>
    private static async Task<int> CountCjkAfterOpenAsync(IReaderService reader, Guid bookId)
    {
        var session = await reader.OpenAsync(bookId, CancellationToken.None).ConfigureAwait(true);
        var href = await reader.MaterializeChapterAsync(session, 0, CancellationToken.None).ConfigureAwait(true);
        var path = Path.Combine(session.CacheRoot, href.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path))
        {
            return -1;
        }

        var html = File.ReadAllText(path, Encoding.UTF8);

        return html.Count(ch => ch >= 0x4E00 && ch <= 0x9FFF);
    }

    /// <summary>
    /// 阅读器重建缓存后，前几章里能不能找到刚写进去的文字。
    /// 这是"保存后屏幕上也变了"的判据：只看文件变了不够，阅读缓存可能还是旧的。
    /// </summary>
    private static async Task<bool> ReaderShowsTextAsync(
        IReaderService reader, Guid bookId, string marker, int maxChapters)
    {
        var deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(40);

        while (DateTimeOffset.Now < deadline)
        {
            try
            {
                var session = await reader.OpenAsync(bookId, CancellationToken.None).ConfigureAwait(true);
                var limit = Math.Min(maxChapters, session.Manifest.Spine.Count);

                for (var i = 0; i < limit; i++)
                {
                    var href = await reader
                        .MaterializeChapterAsync(session, i, CancellationToken.None)
                        .ConfigureAwait(true);

                    var path = Path.Combine(session.CacheRoot, href.Replace('/', Path.DirectorySeparatorChar));

                    if (File.Exists(path)
                        && File.ReadAllText(path, Encoding.UTF8).Contains(marker, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch (IOException)
            {
                // 阅读器正在重建缓存（删目录 / 重写章节）：下一轮再看
            }
            catch (UnauthorizedAccessException)
            {
                // 同上
            }

            await Task.Delay(300).ConfigureAwait(true);
        }

        return false;
    }

    /// <summary>在一段文本里扫出某个词的全部命中下标（独立实现，用来交叉验证编辑器的查找结果）。</summary>
    private static List<int> FindAllOffsets(string text, string term)
    {
        var offsets = new List<int>();

        if (term.Length == 0)
        {
            return offsets;
        }

        var start = 0;

        while (start <= text.Length - term.Length)
        {
            var index = text.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);

            if (index < 0)
            {
                break;
            }

            offsets.Add(index);
            start = index + term.Length;
        }

        return offsets;
    }

    /// <summary>找一个不属于任何命中的字符下标（断言"非命中处没有高亮"用）。</summary>
    private static int FindNonMatchOffset(string text, IReadOnlyList<int> offsets, int matchLength)
    {
        for (var offset = 0; offset < text.Length; offset++)
        {
            var inside = false;

            foreach (var start in offsets)
            {
                if (offset >= start && offset < start + matchLength)
                {
                    inside = true;
                    break;
                }
            }

            if (!inside)
            {
                return offset;
            }
        }

        return -1;
    }

    /// <summary>数一数命中处画的是红底（当前）、黄底（普通）、还是没高亮（冒烟断言用）。</summary>
    private static (int Red, int Yellow, int None) CountHighlightedOffsets(
        TxtEditorWindow window, IReadOnlyList<int> offsets)
    {
        var red = 0;
        var yellow = 0;
        var none = 0;

        foreach (var offset in offsets)
        {
            var brush = window.HighlightBrushAt(offset);

            if (ReferenceEquals(brush, TxtSearchColorizer.CurrentBackground))
            {
                red++;
            }
            else if (ReferenceEquals(brush, TxtSearchColorizer.MatchBackground))
            {
                yellow++;
            }
            else
            {
                none++;
            }
        }

        return (red, yellow, none);
    }

    /// <summary>收集窗口可视树里的全部 TextBlock（断言"说明文字 HintText 已删干净"用）。</summary>
    private static List<TextBlock> FindTextBlocks(Window window)
    {
        var result = new List<TextBlock>();
        CollectTextBlocks(window, result);
        return result;
    }

    private static void CollectTextBlocks(DependencyObject root, List<TextBlock> result)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is TextBlock textBlock)
            {
                result.Add(textBlock);
            }

            CollectTextBlocks(child, result);
        }
    }

    // ---------- 书库主题（ui.theme）验收用的小工具 ----------

    private static Color ThemeResourceColor(string key)
        => Application.Current?.TryFindResource(key) is SolidColorBrush brush ? brush.Color : Colors.Transparent;

    private static Color ThemeColorOf(Brush? brush)
        => brush is SolidColorBrush solid ? solid.Color : Colors.Transparent;

    private static bool SameThemeColor(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B;

    private static string ThemeHex(Color color) => "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");

    /// <summary>WCAG 对比度（1..21）：自定义配色的描边要"凸显"就得跟底色拉开距离。</summary>
    private static double ThemeContrast(Color a, Color b)
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

    /// <summary>把窗口画成 PNG（WPF 窗口用 RenderTargetBitmap 才能把 WebView2 以外的控件拍下来）。</summary>
    private static void CaptureWindow(Window window, string path)    {
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

    /// <summary>
    /// 从窗口截图里量"每一行文字最左边的 x"（像素；按行分组、从上到下）。
    /// 用来验证"首行缩进只缩第一行、折行续行顶头"——量的是用户真正看到的渲染结果，
    /// 而不是 AvalonEdit 内部的视觉行列表（那是异步重建的，读早了会拿到旧版式）。
    /// <paramref name="minY"/> 以下的像素才看（避开工具栏那几行）。
    /// </summary>
    private static List<(int Top, int Left)> MeasureTextRowLeftEdges(string pngPath, int minY)
    {
        var frame = BitmapFrame.Create(new Uri(pngPath, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];

        frame.CopyPixels(pixels, stride, 0);

        var rows = new List<(int Top, int Left)>();
        var rowTop = -1;
        var rowLeft = int.MaxValue;

        for (var y = Math.Max(0, minY); y < height; y++)
        {
            var left = -1;

            // 从 x=2 起：窗口最外圈的那一两个像素是透明/抗锯齿边，不能被当成"字的左边"。
            for (var x = 2; x < width; x++)
            {
                var i = (y * stride) + (x * 4);

                /* Pbgra32：B G R A。正文是深灰/黑字，背景是白/浅灰；
                   透明像素的 RGB 也是 0，必须先按 alpha 排除掉（窗口边缘那一圈）。 */
                if (pixels[i + 3] > 200 && pixels[i] < 120 && pixels[i + 1] < 120 && pixels[i + 2] < 120)
                {
                    left = x;
                    break;
                }
            }

            if (left < 0)
            {
                if (rowTop >= 0)
                {
                    rows.Add((rowTop, rowLeft));
                    rowTop = -1;
                    rowLeft = int.MaxValue;
                }

                continue;
            }

            if (rowTop < 0)
            {
                rowTop = y;
                rowLeft = left;
            }
            else
            {
                rowLeft = Math.Min(rowLeft, left);
            }
        }

        if (rowTop >= 0)
        {
            rows.Add((rowTop, rowLeft));
        }

        return rows;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;

        while (DateTimeOffset.Now < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(true);
        }

        return condition();
    }

    private static SmokeReport Build(
        AppPaths paths,
        DateTimeOffset startedAt,
        Stopwatch total,
        IReadOnlyList<SmokeCheck> checks)
    {
        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            total.ElapsedMilliseconds,
            paths.DataRoot,
            paths.WebView2DataDir,
            null,
            checks);

        try
        {
            Directory.CreateDirectory(paths.TempDir);

            File.WriteAllText(
                Path.Combine(paths.TempDir, "txt-smoke.json"),
                JsonSerializer.Serialize(report, ReportOptions),
                new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // 写不出报告不影响退出码，脚本会按"没报告"报错
        }

        return report;
    }
}
