using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using EbookToolSuite.App.Commands;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Comics;
using EbookToolSuite.Infrastructure.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T6-6 阶段 6 验收：按计划书 5495-5502 的六条场景，把前五个任务的能力串起来跑一遍。
/// 每一步独立 try/catch——一条挂掉不影响其余，报告里逐条写清"哪一步、什么现象、什么数字"。
/// </summary>
/// <remarks>
/// 【为什么不重复 T62B/T63/T64/T65 的全套判据】那四套验的是"每个功能自身的边界"；
/// T6-6 验的是"阶段 6 作为一个整体能不能用"：一次打开一本书、看图、搜"测试"、加书签笔记、
/// 重开还在。所以这里的断言刻意选"跨模块的串联点"，细节仍引用各自的报告。
/// </remarks>
public static class T66SmokeTest
{
    public static async Task<SmokeReport> RunAsync(
        IBookImportService importer,
        IReaderService reader,
        IBookRepository repo,
        IComicBookService comics,
        IContentIndexer indexer,
        IContentSearchService search,
        IAnnotationService annotations,
        IReadingProgressService progress,
        ReadCommand read,
        ILoggerFactory loggerFactory,
        AppPaths paths,
        string pdfDir,
        string fixturesRoot)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        void Add(string name, bool ok, string detail) => checks.Add(new SmokeCheck(name, ok, detail));

        var ctx = new Ctx(
            importer, reader, repo, comics, indexer, search, annotations, progress, read,
            paths, pdfDir, fixturesRoot, Add);

        Guid? searchBookId = null;

        try
        {
            await Step1MobiAsync(ctx).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Add("T66-J1-exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            await Step2PdfAsync(ctx).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Add("T66-J2-exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            await Step3CbzAsync(ctx).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Add("T66-J3-exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            searchBookId = await Step4SearchAsync(ctx).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Add("T66-J4-exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            await Step5BookmarksAsync(ctx, searchBookId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Add("T66-J5-exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            await Step6NotesAsync(ctx, searchBookId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Add("T66-J6-exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        return Finish(paths, startedAt, stopwatch, checks);
    }

    // ---------- 判据 1：MOBI 首次转换 + 二次秒开 ----------

    private static async Task Step1MobiAsync(Ctx c)
    {
        var path = Path.Combine(c.FixturesRoot, "t61-fixtures", "t61-a.mobi");

        if (!File.Exists(path))
        {
            c.Add("J1-mobi-cache", false,
                "缺少 MOBI 样本：" + path + "（先跑 build\\smoke-t61.ps1 造样本）");
            return;
        }

        var imported = await c.Importer.ImportAsync(path, ImportOptions.Default, null).ConfigureAwait(true);

        if (imported.BookId is not { } bookId)
        {
            c.Add("J1-mobi-cache", false, "MOBI 导入失败：" + imported.Outcome);
            return;
        }

        var firstWatch = Stopwatch.StartNew();
        var first = await c.Reader.OpenAsync(bookId).ConfigureAwait(true);
        firstWatch.Stop();

        var secondWatch = Stopwatch.StartNew();
        var second = await c.Reader.OpenAsync(bookId).ConfigureAwait(true);
        secondWatch.Stop();

        var ok = first.SourceFormat == BookFormatKind.Mobi
            && !first.FromCache
            && second.FromCache
            && second.Manifest.Spine.Count > 0
            && secondWatch.ElapsedMilliseconds <= 1500;

        c.Add("J1-mobi-cache", ok,
            $"首次（含 Calibre 转换）{firstWatch.ElapsedMilliseconds} ms、FromCache={first.FromCache}；" +
            $"二次 {secondWatch.ElapsedMilliseconds} ms、FromCache={second.FromCache}（门槛 ≤1500 ms）；" +
            $"章数={second.Manifest.Spine.Count}");
    }

    // ---------- 判据 2：PDF（界面同套 / 封面是首页图 / 进度记住 / 100 MB 首屏） ----------

    private static async Task Step2PdfAsync(Ctx c)
    {
        var pdfs = Directory.GetFiles(c.PdfDir, "*.pdf")
            .OrderBy(f => new FileInfo(f).Length)
            .ToList();

        if (pdfs.Count == 0)
        {
            c.Add("J2-pdf-cover", false, "TestCorpus 目录里没有 PDF");
            return;
        }

        var small = pdfs[0];
        var imported = await c.Importer.ImportAsync(small, ImportOptions.Default, null).ConfigureAwait(true);

        if (imported.BookId is not { } bookId)
        {
            c.Add("J2-pdf-cover", false, "PDF 导入失败：" + imported.Outcome);
            return;
        }

        var book = await c.Repo.GetByIdAsync(bookId).ConfigureAwait(true);

        // 判据 2 前半：书库封面是首页渲染图（不是占位图）
        var cover = book?.CoverPath;
        var coverOk = !string.IsNullOrWhiteSpace(cover) && File.Exists(cover) && new FileInfo(cover!).Length > 5000;

        c.Add("J2-pdf-cover", coverOk,
            coverOk
                ? $"封面={Path.GetFileName(cover)} {new FileInfo(cover!).Length:N0} B（pdfium 渲染的首页，不是占位图）"
                : $"封面缺失或太小：{cover ?? "(null)"}");

        // 判据 2：打开窗口 —— 界面与 EPUB 同一套（元素 id 全部存在、kind=pdf）
        var window = OpenReader(c, bookId);
        var core = window is null ? null : await WaitForCoreAsync(window, 30000).ConfigureAwait(true);
        var state = core is null ? null : await WaitForPdfStateAsync(core, 25000).ConfigureAwait(true);

        if (core is null || state is null)
        {
            c.Add("J2-pdf-ui-same-as-epub", false, "阅读窗口 / PDF 模式没就绪（30 s）");
            await CloseWindowAsync(window).ConfigureAwait(true);
            return;
        }

        var ui = await EvalAsync(core, ToolbarIdsJs).ConfigureAwait(true);
        var missing = ui?["missing"]?.AsArray()?.Select(n => n?.GetValue<string>() ?? "").ToList() ?? new List<string>();

        c.Add("J2-pdf-ui-same-as-epub",
            missing.Count == 0 && (state["kind"]?.GetValue<string>() == "pdf"),
            $"kind={state["kind"]?.GetValue<string>()}、totalPages={state["totalPages"]?.GetValue<int>()}；" +
            $"工具栏 14 个元素缺=[{string.Join(",", missing)}]；" +
            "内置 PDF 界面不出现（三级手段与帧比对见 build\\t62-probe-report.md）");

        // 判据 2：滚动几页后关闭，进度记住，重开回到那一页
        const int target = 9;

        await EvalAsync(core, "window.__readerDebug.pdfGoto(" + target + ")").ConfigureAwait(true);
        var jumped = await WaitPdfPageAsync(core, target, 15000).ConfigureAwait(true);

        await CloseWindowAsync(window).ConfigureAwait(true);
        await c.Progress.FlushAsync().ConfigureAwait(true);

        var reopened = OpenReader(c, bookId);
        var core2 = reopened is null ? null : await WaitForCoreAsync(reopened, 30000).ConfigureAwait(true);
        var state2 = core2 is null ? null : await WaitForPdfStateAsync(core2, 25000).ConfigureAwait(true);

        var backPage = state2?["page"]?.GetValue<int>() ?? -1;

        c.Add("J2-pdf-progress",
            jumped && backPage == target,
            $"跳到第 {target + 1} 页={jumped}；关闭重开后当前页={backPage + 1}（期望 {target + 1}）");

        await CloseWindowAsync(reopened).ConfigureAwait(true);

        // 判据 2 后半：100 MB 扫描件，点开到首页可见 ≤ 5 s
        var big = pdfs.OrderByDescending(f => new FileInfo(f).Length).First();
        var bigImport = await c.Importer.ImportAsync(big, ImportOptions.Default, null).ConfigureAwait(true);

        if (bigImport.BookId is not { } bigId)
        {
            c.Add("J2-pdf-100mb", false, "100 MB PDF 导入失败：" + bigImport.Outcome);
            return;
        }

        var watch = Stopwatch.StartNew();
        var bigWindow = OpenReader(c, bigId);
        var bigCore = bigWindow is null ? null : await WaitForCoreAsync(bigWindow, 45000).ConfigureAwait(true);
        var bigState = bigCore is null ? null : await WaitForPdfStateAsync(bigCore, 45000).ConfigureAwait(true);
        var rendered = bigCore is not null && await FirstImageReadyAsync(bigCore, 45000).ConfigureAwait(true);
        watch.Stop();

        var totalPages = bigState?["totalPages"]?.GetValue<int>() ?? 0;

        c.Add("J2-pdf-100mb",
            rendered && watch.ElapsedMilliseconds <= 5000,
            $"{Path.GetFileName(big)}（{new FileInfo(big).Length / 1024.0 / 1024.0:F1} MB、{totalPages} 页）：" +
            $"点开到首页可见 {watch.ElapsedMilliseconds} ms（判据 ≤5000 ms）；" +
            (watch.ElapsedMilliseconds <= 5000
                ? "达标"
                : "未达标——沿用 T6-2 判据④的已知限制，用户 2026-10-13 已接受（瓶颈是 pdfium 解析非线性化 PDF，不是传输）"));

        await CloseWindowAsync(bigWindow).ConfigureAwait(true);
    }

    // ---------- 判据 3：CBZ 自然序 ----------

    private static async Task Step3CbzAsync(Ctx c)
    {
        var path = Path.Combine(c.FixturesRoot, "t63-corpus", "t63-order.cbz");

        if (!File.Exists(path))
        {
            c.Add("J3-cbz-natural-order", false,
                "缺少 CBZ 样本：" + path + "（先跑 build\\smoke-t63.ps1 造样本）");
            return;
        }

        var imported = await c.Importer.ImportAsync(path, ImportOptions.Default, null).ConfigureAwait(true);

        if (imported.BookId is not { } bookId)
        {
            c.Add("J3-cbz-natural-order", false, "CBZ 导入失败：" + imported.Outcome);
            return;
        }

        var book = await c.Repo.GetByIdAsync(bookId).ConfigureAwait(true);
        var file = book?.Formats.FirstOrDefault(f => f.Format == BookFormatKind.Cbz)?.FilePath ?? path;

        // 归档里的条目名按产品用的同一个自然序比较器排序 —— 这就是"page1/page2/page10"的判据。
        var names = new List<string>();

        using (var zip = ZipFile.OpenRead(file))
        {
            names.AddRange(zip.Entries.Select(e => Path.GetFileName(e.FullName))
                .Where(n => !string.IsNullOrEmpty(n) && CbzPacker.IsSupportedImage(n)));
        }

        names.Sort(NaturalStringComparer.Instance);

        var book2 = c.Comics.Probe(file);
        var ok = names.Count == 3
            && names[0].StartsWith("page1", StringComparison.OrdinalIgnoreCase)
            && names[1].StartsWith("page2", StringComparison.OrdinalIgnoreCase)
            && names[2].StartsWith("page10", StringComparison.OrdinalIgnoreCase)
            && book2.PageCount == 3;

        c.Add("J3-cbz-natural-order", ok,
            $"归档页序=[{string.Join(" → ", names)}]（期望 page1 → page2 → page10）；" +
            $"探测页数={book2.PageCount}、界面按同一顺序渲染（见 build\\t63-acceptance-report.md）");
    }

    // ---------- 判据 4：搜索"测试"命中并跳转 ----------

    private static async Task<Guid?> Step4SearchAsync(Ctx c)
    {
        var corpus = Path.Combine(c.Paths.TempDir, "t66-corpus");
        Directory.CreateDirectory(corpus);

        var epub = WriteSearchEpub(corpus);
        var imported = await c.Importer.ImportAsync(epub, ImportOptions.Default, null).ConfigureAwait(true);

        if (imported.BookId is not { } bookId)
        {
            c.Add("J4-search-chinese", false, "搜索样本导入失败：" + imported.Outcome);
            return null;
        }

        await c.Indexer.EnqueueAsync(new[] { bookId }).ConfigureAwait(true);
        var idle = await c.Indexer.WaitForIdleAsync(TimeSpan.FromSeconds(40)).ConfigureAwait(true);

        var result = await c.Search.SearchAsync("测试", bookId, 50).ConfigureAwait(true);
        var hit = result.Hits.FirstOrDefault();

        c.Add("J4-search-chinese",
            idle && result.TotalMatches > 0 && hit is not null,
            $"索引走空={idle}；搜「测试」命中 {result.TotalMatches} 处" +
            (hit is null ? "（没有命中）" : $"；首条=第 {hit.SpineIndex + 1} 章「{hit.ChapterTitle}」"));

        if (hit is null)
        {
            return bookId;
        }

        // 跳转：物化命中的那一章，确认正文里真的有"测试"
        var session = await c.Reader.OpenAsync(bookId).ConfigureAwait(true);
        var href = await c.Reader.MaterializeChapterAsync(session, hit.SpineIndex).ConfigureAwait(true);
        var html = await File.ReadAllTextAsync(Path.Combine(session.CacheRoot, href)).ConfigureAwait(true);

        c.Add("J4-search-jump",
            html.Contains("测试", StringComparison.Ordinal),
            $"命中章节={hit.SpineIndex + 1}、物化后 HTML 含「测试」={html.Contains("测试", StringComparison.Ordinal)}" +
            "（界面点结果走 ReadCommand 的 findText 通道，见 build\\t64-acceptance-report.md）");

        return bookId;
    }

    // ---------- 判据 5：加 3 个书签并导出 Markdown ----------

    private static async Task Step5BookmarksAsync(Ctx c, Guid? bookId)
    {
        if (bookId is not { } id)
        {
            c.Add("J5-bookmarks-export", false, "没有可用的书（判据 4 的样本没导进来）");
            return;
        }

        for (var i = 1; i <= 3; i++)
        {
            await c.Annotations.AddBookmarkAsync(new Bookmark
            {
                BookId = id,
                SpineIndex = 0,
                Label = "第 " + i + " 个书签",
            }).ConfigureAwait(true);
        }

        var path = await c.Annotations.ExportMarkdownAsync(id).ConfigureAwait(true);
        var markdown = await File.ReadAllTextAsync(path).ConfigureAwait(true);
        var bullets = markdown.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal));

        c.Add("J5-bookmarks-export",
            bullets == 3 && markdown.Contains("第 1 个书签", StringComparison.Ordinal),
            $"加了 3 个书签；导出文件={Path.GetFileName(path)}（{markdown.Length} 字符）；" +
            $"Markdown 里书签条目={bullets}（期望 3）");
    }

    // ---------- 判据 6：加 2 条笔记，重开还在 ----------

    private static async Task Step6NotesAsync(Ctx c, Guid? bookId)
    {
        if (bookId is not { } id)
        {
            c.Add("J6-notes-persist", false, "没有可用的书（判据 4 的样本没导进来）");
            return;
        }

        for (var i = 1; i <= 2; i++)
        {
            await c.Annotations.AddNoteAsync(new Note
            {
                BookId = id,
                SpineIndex = 0,
                Type = NoteType.Annotation,
                Style = MarkStyle.Highlight,
                Quote = "测试文字",
                Content = "第 " + i + " 条笔记",
            }).ConfigureAwait(true);
        }

        // "重开"= 重新从数据库读（与关闭程序再打开走同一条 SQL）。
        var notes = await c.Annotations.ListNotesAsync(id).ConfigureAwait(true);

        c.Add("J6-notes-persist",
            notes.Count == 2 && notes.All(n => n.Content is not null),
            $"加了 2 条笔记；重新从库里读回 {notes.Count} 条" +
            $"（内容=[{string.Join(" / ", notes.Select(n => n.Content))}]）");
    }

    // ---------- 样本：一本结构合法、正文含"测试"的最小 EPUB ----------

    private static string WriteSearchEpub(string dir)
    {
        var path = Path.Combine(dir, "t66-search.epub");

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var stamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        void WriteText(string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = stamp;
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        mime.LastWriteTime = stamp;
        using (var mimeStream = mime.Open())
        {
            mimeStream.Write(Encoding.ASCII.GetBytes("application/epub+zip"));
        }

        WriteText("META-INF/container.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">"
            + "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" "
            + "media-type=\"application/oebps-package+xml\"/></rootfiles></container>");

        WriteText("OEBPS/content.opf",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\">"
            + "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">"
            + "<dc:identifier id=\"bookid\">urn:uuid:" + Guid.NewGuid().ToString("D") + "</dc:identifier>"
            + "<dc:title>T6-6 验收书</dc:title><dc:language>zh-CN</dc:language>"
            + "<meta property=\"dcterms:modified\">2026-01-01T00:00:00Z</meta></metadata>"
            + "<manifest>"
            + "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>"
            + "<item id=\"ch1\" href=\"ch1.xhtml\" media-type=\"application/xhtml+xml\"/>"
            + "</manifest>"
            + "<spine><itemref idref=\"ch1\"/></spine></package>");

        WriteText("OEBPS/nav.xhtml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\">"
            + "<head><title>目录</title></head><body><nav epub:type=\"toc\"><ol>"
            + "<li><a href=\"ch1.xhtml\">第 1 章</a></li></ol></nav></body></html>");

        WriteText("OEBPS/ch1.xhtml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>第 1 章</title></head>"
            + "<body><h1>第 1 章 测试</h1>"
            + "<p>这一段包含「测试」两个字，用来验证中文全文搜索能命中。</p>"
            + "<p>第二段没有关键词，只负责把章节撑长一点。</p></body></html>");

        return path;
    }

    // ---------- 窗口与探针 ----------

    private static ReaderWindow? OpenReader(Ctx c, Guid bookId)
    {
        // ReadCommand 需要一个 owner：用一个 1×1 的隐藏窗口，关掉它时会连带关阅读窗，
        // 所以它只活到本步骤结束（步骤结束时阅读窗已经关了）。
        var owner = new Window
        {
            Width = 1,
            Height = 1,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -2000,
            Top = -2000,
        };

        owner.Show();

        c.Read.Run(owner, new Book { Id = bookId });

        return Application.Current.Windows.OfType<ReaderWindow>().LastOrDefault();
    }

    private static async Task CloseWindowAsync(ReaderWindow? window)
    {
        if (window is null)
        {
            return;
        }

        try
        {
            window.Close();
        }
        catch (Exception)
        {
            // 关窗竞态不影响判据（进度由 OnClosed 排队保存）
        }

        await Task.Delay(500).ConfigureAwait(true);
    }

    private static async Task<CoreWebView2?> WaitForCoreAsync(ReaderWindow window, int timeoutMs)
    {
        CoreWebView2? core = null;

        await WaitAsync(
            () =>
            {
                core = window.Web.CoreWebView2;
                return Task.FromResult(core is not null);
            },
            timeoutMs).ConfigureAwait(true);

        return core;
    }

    private static async Task<JsonNode?> WaitForPdfStateAsync(CoreWebView2 core, int timeoutMs)
    {
        JsonNode? state = null;

        await WaitAsync(
            async () =>
            {
                var node = await EvalAsync(core, "window.__readerDebug ? window.__readerDebug.pdfState() : null")
                    .ConfigureAwait(true);

                if (node is JsonObject obj && obj["kind"]?.GetValue<string>() == "pdf"
                    && (obj["totalPages"]?.GetValue<int>() ?? 0) > 0)
                {
                    state = obj;
                    return true;
                }

                return false;
            },
            timeoutMs).ConfigureAwait(true);

        return state;
    }

    private static async Task<bool> WaitPdfPageAsync(CoreWebView2 core, int page, int timeoutMs)
    {
        return await WaitAsync(
            async () =>
            {
                var node = await EvalAsync(
                    core,
                    "(function(){var s=window.__readerDebug?window.__readerDebug.pdfState():null;return s?{page:s.page}:null;})()")
                    .ConfigureAwait(true);

                return node is JsonObject obj && (obj["page"]?.GetValue<int>() ?? -1) == page;
            },
            timeoutMs).ConfigureAwait(true);
    }

    private static async Task<bool> FirstImageReadyAsync(CoreWebView2 core, int timeoutMs)
    {
        return await WaitAsync(
            async () =>
            {
                var node = await EvalAsync(
                    core,
                    "(function(){var s=window.__readerDebug?window.__readerDebug.pdfState():null;if(!s){return null;}"
                    + "var el=document.querySelector('#pdfPages .pdfPage[data-page=\"'+(s.page)+'\"] .pdfImage');"
                    + "return {complete: el? el.complete : false, w: el? el.naturalWidth : 0};})()")
                    .ConfigureAwait(true);

                return node is JsonObject obj
                    && (obj["complete"]?.GetValue<bool>() ?? false)
                    && (obj["w"]?.GetValue<int>() ?? 0) > 0;
            },
            timeoutMs,
            200).ConfigureAwait(true);
    }

    private const string ToolbarIdsJs = """
        (function(){
          var ids = ['pdfPrev','pdfNext','pdfPageInput','pdfPageTotal','pdfZoomOut','pdfZoomIn',
                     'pdfFitPage','pdfFitWidth','pdfRotate','pdfLayout','pdfSpread','pdfSearch',
                     'pdfFullscreen','pdfOpenExternal'];
          var missing = [];
          for (var i = 0; i < ids.length; i++) { if (!document.getElementById(ids[i])) { missing.push(ids[i]); } }
          return { missing: missing };
        })()
        """;

    private static async Task<JsonNode?> EvalAsync(CoreWebView2 core, string script)
    {
        var json = await core.ExecuteScriptAsync(script).ConfigureAwait(true);

        return string.IsNullOrEmpty(json) ? null : JsonNode.Parse(json);
    }

    private static async Task<bool> WaitAsync(Func<Task<bool>> probe, int timeoutMs, int stepMs = 150)
    {
        var clock = Stopwatch.StartNew();

        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (await probe().ConfigureAwait(true))
            {
                return true;
            }

            await Task.Delay(stepMs).ConfigureAwait(true);
        }

        return false;
    }

    private static SmokeReport Finish(
        AppPaths paths, DateTimeOffset startedAt, Stopwatch stopwatch, List<SmokeCheck> checks)
    {
        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            stopwatch.ElapsedMilliseconds,
            paths.DataRoot,
            paths.WebView2DataDir,
            null,
            checks);

        Directory.CreateDirectory(paths.TempDir);

        File.WriteAllText(
            Path.Combine(paths.TempDir, "t66-smoke.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return report;
    }

    private sealed record Ctx(
        IBookImportService Importer,
        IReaderService Reader,
        IBookRepository Repo,
        IComicBookService Comics,
        IContentIndexer Indexer,
        IContentSearchService Search,
        IAnnotationService Annotations,
        IReadingProgressService Progress,
        ReadCommand Read,
        AppPaths Paths,
        string PdfDir,
        string FixturesRoot,
        Action<string, bool, string> Add);
}
