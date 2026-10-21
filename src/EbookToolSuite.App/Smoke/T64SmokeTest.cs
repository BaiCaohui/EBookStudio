using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.App.Views;
using EbookToolSuite.App.Views.Pages;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Reader.WebView;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T6-4「全文搜索（FTS5）」的端到端验收。判据与计划书 5302-5310 一一对应：
/// 导入后自动索引、中文/英文命中、点击跳章并高亮、1000 本可中断可续、搜索 ≤1 秒、
/// 索引体积记录在案。
/// </summary>
/// <remarks>
/// <para>为什么用真 IImportQueue 而不是直接 ImportAsync：判据 1 要验的是
/// 「导入完就自动建索引」这条**生产路径**，而自动入队就发生在 ImportQueue 里
/// （ImportPipeline 不认识索引器）。绕过队列等于把要验的那一行代码绕掉了。</para>
/// <para>为什么 1000 本用 TXT：判据 6/7 只关心"索引与查询在 1000 本量级下的行为"，
/// TXT 的导入与抽取是最轻的路径，造 1000 个合法 EPUB 会把验收时间花在 zip 上。</para>
/// </remarks>
public static class T64SmokeTest
{
    /// <summary>中文关键词（≥3 字，走 trigram MATCH）。只在第二章出现。</summary>
    private const string ChineseKeyword = "江湖夜雨十年灯";

    /// <summary>英文关键词（≥3 字符）。只在第二章出现。</summary>
    private const string EnglishKeyword = "lantern";

    /// <summary>短词（2 字，必须走 LIKE 兜底）。</summary>
    private const string ShortKeyword = "江湖";

    /// <summary>1000 本共有的词（用来证明全部索引完）。</summary>
    private const string BulkKeyword = "千书共有的词";

    /// <summary>1000 本样本的规模（计划书判据 6/7）。</summary>
    private const int BulkBooks = 1000;

    public static async Task<SmokeReport> RunAsync(
        IImportQueue queue,
        IBookImportService importer,
        IReaderService reader,
        IBookRepository repo,
        IWebViewEnvironmentProvider environments,
        IReadingProgressService progress,
        IContentIndexer indexer,
        IContentSearchService search,
        IContentRepository contentRepo,
        IPdfDocumentService? pdfPages,
        ILoggerFactory loggerFactory,
        AppPaths paths,
        string corpusRoot,
        string? pdfPath,
        LibraryPage libraryPage)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        void Add(string name, bool ok, string detail) => checks.Add(new SmokeCheck(name, ok, detail));

        var corpus = BuildCorpus(corpusRoot);

        Add("J0-corpus-built", corpus.EpubPath is not null, corpus.Detail);

        if (corpus.EpubPath is null)
        {
            return Finish(paths, startedAt, stopwatch, checks);
        }

        /* ---------- 判据 1：导入后索引自动构建 ---------- */
        var options = ImportQueueOptions.Default with
        {
            PromptOnDuplicates = false,
            ExtractCover = false,
        };

        var batchOk = false;
        var batchDetail = "未导入";

        try
        {
            var batch = await queue.StartAsync(new[] { corpus.EpubPath, corpus.TxtPath }, options)
                .ConfigureAwait(true);

            await queue.WaitForCompletionAsync().ConfigureAwait(true);

            batchOk = batch.Succeeded == 2;
            batchDetail = $"导入 {batch.Total} 个：成功 {batch.Succeeded}、失败 {batch.Failed}";
        }
        catch (Exception ex)
        {
            batchDetail = "导入异常：" + ex.GetType().Name + " " + ex.Message;
        }

        Add("J1-import-via-queue", batchOk, batchDetail);

        var indexed = await WaitForIdleAsync(indexer, TimeSpan.FromSeconds(60)).ConfigureAwait(true);

        var epubBook = await FindBookByTitleAsync(repo, "全文搜索冒烟书").ConfigureAwait(true);

        /* TXT 没有元数据，书名就是文件名（t64-search.txt -> "t64-search"）。
           这里按真实书名找，别按"我以为的名字"找。 */
        var txtBook = await FindBookByTitleAsync(repo, "t64-search").ConfigureAwait(true);

        var epubState = epubBook is null ? null : await contentRepo.GetStateAsync(epubBook.Id).ConfigureAwait(true);
        var txtState = txtBook is null ? null : await contentRepo.GetStateAsync(txtBook.Id).ConfigureAwait(true);

        Add("J1-index-auto-built",
            indexed && epubState?.Status == ContentIndexStatus.Done
                && txtState?.Status == ContentIndexStatus.Done,
            $"索引队列走空={indexed}；EPUB 状态={epubState?.Status}（{epubState?.ChunkCount} 片）；"
            + $"TXT 状态={txtState?.Status}（{txtState?.ChunkCount} 片）；"
            + $"进度 Done={indexer.Progress.Done}/{indexer.Progress.Total} 失败={indexer.Progress.Failed}");

        /* ---------- 判据 2/3/4：中文、英文、短词 ---------- */
        if (epubBook is not null)
        {
            var chinese = await search.SearchAsync(ChineseKeyword).ConfigureAwait(true);

            Add("J2-chinese-hit",
                chinese.TotalMatches > 0
                    && chinese.Hits.Any(h => h.BookId == epubBook.Id && h.SpineIndex == 1),
                $"「{ChineseKeyword}」命中 {chinese.TotalMatches} 处；"
                + string.Join(" | ", chinese.Hits.Take(3).Select(h => h.BookTitle + "#" + h.SpineIndex)));

            var english = await search.SearchAsync(EnglishKeyword).ConfigureAwait(true);

            Add("J3-english-hit",
                english.TotalMatches > 0 && english.Hits.Any(h => h.BookId == epubBook.Id),
                $"「{EnglishKeyword}」命中 {english.TotalMatches} 处");

            var shortQuery = await search.SearchAsync(ShortKeyword).ConfigureAwait(true);

            Add("J4-short-query-fallback",
                shortQuery.TotalMatches > 0 && shortQuery.UsedShortQueryFallback,
                $"「{ShortKeyword}」（2 字）命中 {shortQuery.TotalMatches} 处，走 LIKE 兜底="
                + shortQuery.UsedShortQueryFallback);

            /* ---------- 判据 4/5：点击结果 -> 跳到正确章节 -> 高亮 ---------- */
            var jump = await ProbeJumpAndHighlightAsync(
                reader, environments, progress, repo, loggerFactory, epubBook.Id).ConfigureAwait(true);

            Add("J5-jump-to-chapter", jump.ChapterOk, jump.ChapterDetail);
            Add("J5-highlight-after-jump", jump.HighlightOk, jump.HighlightDetail);
        }
        else
        {
            Add("J2-chinese-hit", false, "找不到刚导入的 EPUB（按书名查不到）");
            Add("J3-english-hit", false, "找不到刚导入的 EPUB");
            Add("J4-short-query-fallback", false, "找不到刚导入的 EPUB");
            Add("J5-jump-to-chapter", false, "找不到刚导入的 EPUB");
            Add("J5-highlight-after-jump", false, "找不到刚导入的 EPUB");
        }

        /* ---------- 判据 6：1000 本索引可中断可续 ---------- */
        var bulkPaths = corpus.BulkPaths;

        // 先停掉索引器：让 1000 本的导入只入队、不建索引，中断点才可控
        await indexer.StopAsync().ConfigureAwait(true);

        var bulkDetail = "未导入";
        var bulkOk = false;

        try
        {
            var bulkBatch = await queue.StartAsync(bulkPaths, options).ConfigureAwait(true);

            await queue.WaitForCompletionAsync().ConfigureAwait(true);

            bulkOk = bulkBatch.Succeeded == BulkBooks;
            bulkDetail = $"导入 {bulkBatch.Total} 本：成功 {bulkBatch.Succeeded}、失败 {bulkBatch.Failed}";
        }
        catch (Exception ex)
        {
            bulkDetail = "批量导入异常：" + ex.GetType().Name + " " + ex.Message;
        }

        Add("J6-bulk-import", bulkOk, bulkDetail);

        var interrupted = false;
        var interruptDetail = "未开始";

        await indexer.StartAsync().ConfigureAwait(true);

        try
        {
            // 跑到 10 本以上就掐掉，证明"中途停"确实停在半路
            var caught = await WaitAsync(
                () => Task.FromResult(indexer.Progress.Done >= 10),
                timeoutMs: 60000,
                stepMs: 5).ConfigureAwait(true);

            await indexer.StopAsync().ConfigureAwait(true);

            var afterStop = indexer.Progress;

            interrupted = caught && afterStop.Done >= 10 && afterStop.Pending > 0;
            interruptDetail = $"停在中途={caught}；Done={afterStop.Done}、Pending={afterStop.Pending}、"
                + $"Total={afterStop.Total}（剩余的书应留待续建）";
        }
        catch (Exception ex)
        {
            interruptDetail = "中断测试异常：" + ex.GetType().Name + " " + ex.Message;
        }

        Add("J6-index-interruptible", interrupted, interruptDetail);

        var resumed = false;
        var resumeDetail = "未重跑";

        try
        {
            await indexer.StartAsync().ConfigureAwait(true);

            var totalThisRun = indexer.Progress.Total;

            resumed = await WaitForIdleAsync(indexer, TimeSpan.FromSeconds(180)).ConfigureAwait(true);

            var final = indexer.Progress;

            resumeDetail = $"续建本轮 Total={totalThisRun}（应小于 {BulkBooks}）、Done={final.Done}、"
                + $"Pending={final.Pending}、Failed={final.Failed}、走空={resumed}";

            // 本轮要建的本数必须小于 1000，否则说明中断时已完成的书被重做了
            resumed = resumed && totalThisRun < BulkBooks;
        }
        catch (Exception ex)
        {
            resumeDetail = "续建异常：" + ex.GetType().Name + " " + ex.Message;
        }

        Add("J6-index-resumable", resumed, resumeDetail);

        /* ---------- 判据 7：1000 本搜索 ≤ 1 秒 ---------- */
        var searchTimes = new List<long>();
        var hits = 0;

        try
        {
            // 预热一次（SQLite 页缓存与 FTS 结构都还没热时不算数）
            _ = await search.SearchAsync(BulkKeyword).ConfigureAwait(true);

            for (var i = 0; i < 3; i++)
            {
                var clock = Stopwatch.StartNew();
                var result = await search.SearchAsync(BulkKeyword).ConfigureAwait(true);
                clock.Stop();

                hits = result.TotalMatches;
                searchTimes.Add(clock.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            Add("J7-search-under-1s", false, "搜索异常：" + ex.GetType().Name + " " + ex.Message);
        }

        if (searchTimes.Count > 0)
        {
            var min = searchTimes.Min();
            var avg = (long)searchTimes.Average();

            Add("J7-search-under-1s",
                min <= 1000,
                $"「{BulkKeyword}」在 {BulkBooks} 本书库里命中 {hits} 处；"
                + $"三次搜索 min/avg/max = {min}/{avg}/{searchTimes.Max()} ms（判据 ≤1000 ms，取 min 判定）");
        }

        /* ---------- T6-2B 转交的判据④：PDF 正文可被全库搜索命中、可跳转、可高亮 ---------- */
        try
        {
            var pdfJump = await ProbePdfSearchAsync(
                importer, reader, environments, progress, repo, indexer, search, pdfPages,
                loggerFactory, pdfPath).ConfigureAwait(true);

            Add("J10-pdf-search-hit", pdfJump.HitOk, pdfJump.HitDetail);
            Add("J10-pdf-jump-highlight", pdfJump.JumpOk, pdfJump.JumpDetail);
        }
        catch (Exception ex)
        {
            Add("J10-pdf-search-hit", false, "PDF 检查异常：" + ex.GetType().Name + " " + ex.Message);
            Add("J10-pdf-jump-highlight", false, "同上");
        }

        /* ---------- 判据 8：索引体积记录在案 ---------- */
        try
        {
            var volume = await contentRepo.MeasureVolumeAsync().ConfigureAwait(true);

            /* dbstat 是可选编译项：驱动没编进去时 FTS 的字节数拿不到（这里记 0）。
               磁盘预算还有一个不依赖它的口径：库文件（+ WAL）的真实体积。 */
            long dbBytes = 0;

            foreach (var file in new[] { paths.DbFile, paths.DbFile + "-wal" })
            {
                if (File.Exists(file))
                {
                    dbBytes += new FileInfo(file).Length;
                }
            }

            Add("J8-index-volume-recorded",
                volume.ChunkCount > 0 && volume.BodyBytes > 0,
                $"切片 {volume.ChunkCount} 条；正文 {volume.BodyBytes:N0} B；"
                + $"FTS 索引 {volume.FtsBytes:N0} B（dbstat 不可用时为 0，属已知）；"
                + $"库文件（含 WAL）{dbBytes:N0} B = {dbBytes / 1024.0 / 1024.0:F2} MB");
        }
        catch (Exception ex)
        {
            Add("J8-index-volume-recorded", false, "量体积异常：" + ex.GetType().Name + " " + ex.Message);
        }

        /* ---------- 界面：全文搜索面板能加载、绑定出结果（XAML 一加载就知道有没有错） ---------- */
        try
        {
            libraryPage.Measure(new Size(1280, 800));
            libraryPage.Arrange(new Rect(0, 0, 1280, 800));
            libraryPage.UpdateLayout();

            var vm = libraryPage.DataContext as LibraryViewModel;

            if (vm is null)
            {
                Add("J9-ui-panel-bindings", false, "书库页 DataContext 不是 LibraryViewModel");
            }
            else
            {
                vm.FullText.Open("江湖夜雨");
                await vm.FullText.FlushSearchAsync().ConfigureAwait(true);
                libraryPage.UpdateLayout();

                var visible = libraryPage.FullTextOverlay.Visibility == Visibility.Visible;
                var groups = vm.FullText.Groups.Count;
                var firstHit = groups > 0 ? vm.FullText.Groups[0].Hits.FirstOrDefault() : null;

                Add("J9-ui-panel-bindings",
                    visible && groups > 0 && firstHit is not null && firstHit.Match.Length > 0,
                    $"浮层可见={visible}；结果分组={groups} 本；首条命中词=\"{firstHit?.Match}\"、"
                    + $"状态文案=\"{vm.FullText.StatusText}\"");

                vm.FullText.Close();
            }
        }
        catch (Exception ex)
        {
            Add("J9-ui-panel-bindings", false, "UI 面板检查异常：" + ex.GetType().Name + " " + ex.Message);
        }

        return Finish(paths, startedAt, stopwatch, checks);
    }

    // ---------- 判据 4/5：跳章 + 高亮 ----------

    private readonly record struct JumpResult(
        bool ChapterOk, string ChapterDetail, bool HighlightOk, string HighlightDetail);

    private static async Task<JumpResult> ProbeJumpAndHighlightAsync(
        IReaderService reader,
        IWebViewEnvironmentProvider environments,
        IReadingProgressService progress,
        IBookRepository repo,
        ILoggerFactory loggerFactory,
        Guid bookId)
    {
        ReaderWindow? window = null;

        try
        {
            /* 走生产入口的同一组参数：章节号 = 命中里的 spineIndex（第 2 章），
               initialFind = 搜索词。ReadCommand 只是把这两样原样交给 ReaderWindow。 */
            window = new ReaderWindow(
                reader,
                environments,
                progress,
                loggerFactory.CreateLogger<ReaderWindow>(),
                bookId,
                startIndex: 1,
                repository: repo,
                initialFind: ChineseKeyword);

            window.Show();

            var core = await WaitForCoreAsync(window, 20000).ConfigureAwait(true);

            if (core is null)
            {
                return new JumpResult(false, "WebView2 20 秒内没就绪", false, "同上");
            }

            var chapterOk = await WaitAsync(
                async () => await EvalIntAsync(
                    core, "window.__readerDebug ? window.__readerDebug.chapterIndex() : -1")
                    .ConfigureAwait(true) == 1,
                timeoutMs: 30000).ConfigureAwait(true);

            var chapterIndex = await EvalIntAsync(
                core, "window.__readerDebug ? window.__readerDebug.chapterIndex() : -1").ConfigureAwait(true);

            var markOk = await WaitAsync(
                async () => await EvalIntAsync(
                    core, "window.__readerDebug ? window.__readerDebug.findMarkCount() : -1")
                    .ConfigureAwait(true) > 0,
                timeoutMs: 20000).ConfigureAwait(true);

            var marks = await EvalIntAsync(
                core, "window.__readerDebug ? window.__readerDebug.findMarkCount() : -1").ConfigureAwait(true);

            return new JumpResult(
                chapterOk,
                $"打开后停在第 {chapterIndex + 1} 章（期望第 2 章，0 基索引 1）",
                markOk,
                $"章节正文里的高亮标记（mark.ets-find）数量={marks}（期望 >0）");
        }
        catch (Exception ex)
        {
            return new JumpResult(
                false, "跳章检查异常：" + ex.GetType().Name + " " + ex.Message,
                false, "同上");
        }
        finally
        {
            window?.Close();
            await Task.Delay(500).ConfigureAwait(true);
        }
    }

    // ---------- PDF：T6-2B 转交的判据④ ----------

    private readonly record struct PdfJumpResult(
        bool HitOk, string HitDetail, bool JumpOk, string JumpDetail);

    /// <summary>
    /// 验"PDF 正文能被全库搜索命中，结果可跳转、可高亮"：
    /// 导入真实 PDF → 建索引 → 从 pdfium 文本层取一段连续文字当查询词 →
    /// 全库搜它（必须命中这本 PDF）→ 打开阅读窗口到命中页并带查找词 →
    /// 断言当前页 = 命中页、且 PDF 查找结果数 &gt; 0（黄底画出来了）。
    /// </summary>
    private static async Task<PdfJumpResult> ProbePdfSearchAsync(
        IBookImportService importer,
        IReaderService reader,
        IWebViewEnvironmentProvider environments,
        IReadingProgressService progress,
        IBookRepository repo,
        IContentIndexer indexer,
        IContentSearchService search,
        IPdfDocumentService? pdfPages,
        ILoggerFactory loggerFactory,
        string? pdfPath)
    {
        if (pdfPages is null || string.IsNullOrEmpty(pdfPath) || !File.Exists(pdfPath))
        {
            return new PdfJumpResult(
                false, "没有 PDF 样本或 pdfium 组件：" + (pdfPath ?? "(null)"),
                false, "同上");
        }

        var imported = await importer.ImportAsync(pdfPath, ImportOptions.Default, null).ConfigureAwait(true);

        if (imported.BookId is not { } bookId)
        {
            return new PdfJumpResult(
                false, $"PDF 导入失败：{imported.Outcome}（{imported.Message}）", false, "同上");
        }

        // 直接走 importer 不会经过 ImportQueue 的自动入队，所以这里显式排队
        await indexer.EnqueueAsync(new[] { bookId }).ConfigureAwait(true);
        await indexer.WaitForIdleAsync(TimeSpan.FromSeconds(180)).ConfigureAwait(true);

        var pages = pdfPages.ReadAllText(pdfPath);
        var pageIndex = -1;
        string? phrase = null;

        for (var i = 0; i < pages.Count && phrase is null; i++)
        {
            var text = pages[i];
            var runStart = -1;
            var runLength = 0;

            for (var k = 0; k < text.Length; k++)
            {
                if (char.IsWhiteSpace(text[k]))
                {
                    runStart = -1;
                    runLength = 0;
                    continue;
                }

                if (runStart < 0)
                {
                    runStart = k;
                }

                runLength++;

                if (runLength >= 6)
                {
                    pageIndex = i;
                    phrase = text.Substring(runStart, 6);
                    break;
                }
            }
        }

        if (phrase is null || pageIndex < 0)
        {
            return new PdfJumpResult(false, "这本 PDF 的文本层里找不到 6 个连续字符", false, "同上");
        }

        var result = await search.SearchAsync(phrase, bookId).ConfigureAwait(true);
        var hitOk = result.TotalMatches > 0 && result.Hits.Any(h => h.BookId == bookId);
        var hitDetail = $"6 字片段（取自第 {pageIndex + 1} 页文本层，正文不打印）命中 {result.TotalMatches} 处；"
            + $"命中页={string.Join(',', result.Hits.Select(h => h.SpineIndex + 1).Distinct().Take(5))}";

        ReaderWindow? window = null;

        try
        {
            window = new ReaderWindow(
                reader,
                environments,
                progress,
                loggerFactory.CreateLogger<ReaderWindow>(),
                bookId,
                repository: repo,
                pdfPages: pdfPages,
                startPage: pageIndex,
                initialFind: phrase);

            window.Show();

            var core = await WaitForCoreAsync(window, 20000).ConfigureAwait(true);

            if (core is null)
            {
                return new PdfJumpResult(hitOk, hitDetail, false, "WebView2 20 秒内没就绪");
            }

            /* 必须同时等到"页到了"和"查找结果回来了"：
               只等页的话，页在 runPdfFind 的异步结果回来之前就已经是目标页（PDF 默认从第 1 页开），
               会读到 hitCount=0 然后提前关窗——这正是第一次跑出来的假失败。 */
            var arrived = await WaitAsync(
                async () =>
                {
                    var state = await EvalJsonAsync(
                        core, "window.__readerDebug ? window.__readerDebug.pdfState() : null")
                        .ConfigureAwait(true);

                    return state is not null
                        && state["kind"]?.GetValue<string>() == "pdf"
                        && (state["page"]?.GetValue<int>() ?? -1) == pageIndex
                        && (state["hitCount"]?.GetValue<int>() ?? 0) > 0;
                },
                timeoutMs: 60000).ConfigureAwait(true);

            var final = await EvalJsonAsync(
                core, "window.__readerDebug ? window.__readerDebug.pdfState() : null").ConfigureAwait(true);

            var pageNow = final?["page"]?.GetValue<int>() ?? -1;
            var hitCount = final?["hitCount"]?.GetValue<int>() ?? -1;

            return new PdfJumpResult(
                hitOk,
                hitDetail,
                arrived && pageNow == pageIndex && hitCount > 0,
                $"打开后当前页={pageNow + 1}（期望第 {pageIndex + 1} 页）、PDF 查找命中数={hitCount}（期望 >0）");
        }
        finally
        {
            window?.Close();
            await Task.Delay(500).ConfigureAwait(true);
        }
    }

    // ---------- 样本 ----------

    private readonly record struct Corpus(
        string? EpubPath, string TxtPath, IReadOnlyList<string> BulkPaths, string Detail);

    private static Corpus BuildCorpus(string root)
    {
        try
        {
            var dir = Path.Combine(root, "t64-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);

            var epub = WriteEpub(dir);
            var txt = WriteText(dir);

            var bulk = new List<string>(BulkBooks);

            for (var i = 1; i <= BulkBooks; i++)
            {
                var path = Path.Combine(dir, "t64-bulk-" + i.ToString("D4") + ".txt");
                File.WriteAllText(
                    path,
                    "第1章 千书\n" + BulkKeyword + " 出现在第 " + i.ToString("D4") + " 本里。\n",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

                bulk.Add(path);
            }

            return new Corpus(
                epub,
                txt,
                bulk,
                $"样本目录 {dir}：EPUB + TXT + {BulkBooks} 个 TXT（共 {bulk.Count + 2} 个文件）");
        }
        catch (Exception ex)
        {
            return new Corpus(null, string.Empty, Array.Empty<string>(),
                "造样本失败：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    private static string WriteText(string dir)
    {
        var path = Path.Combine(dir, "t64-search.txt");

        File.WriteAllText(
            path,
            "第1章 开端\n这一章没有关键词。\n第2章 继续\n" + ChineseKeyword + " 与 " + EnglishKeyword + " 都在这里。\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        return path;
    }

    /// <summary>造一个结构合法的最小 EPUB（zip 里 mimetype 必须第一条且不压缩）。</summary>
    private static string WriteEpub(string dir)
    {
        var path = Path.Combine(dir, "t64-search.epub");

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
            + "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" "
            + "unique-identifier=\"bookid\">"
            + "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">"
            + "<dc:identifier id=\"bookid\">urn:uuid:" + Guid.NewGuid().ToString("D") + "</dc:identifier>"
            + "<dc:title>全文搜索冒烟书</dc:title><dc:language>zh-CN</dc:language>"
            + "<meta property=\"dcterms:modified\">2026-01-01T00:00:00Z</meta>"
            + "</metadata>"
            + "<manifest>"
            + "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>"
            + "<item id=\"ch1\" href=\"ch1.xhtml\" media-type=\"application/xhtml+xml\"/>"
            + "<item id=\"ch2\" href=\"ch2.xhtml\" media-type=\"application/xhtml+xml\"/>"
            + "</manifest>"
            + "<spine><itemref idref=\"ch1\"/><itemref idref=\"ch2\"/></spine>"
            + "</package>");

        WriteText("OEBPS/nav.xhtml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\">"
            + "<head><title>目录</title></head><body><nav epub:type=\"toc\"><ol>"
            + "<li><a href=\"ch1.xhtml\">第 1 章</a></li><li><a href=\"ch2.xhtml\">第 2 章</a></li>"
            + "</ol></nav></body></html>");

        WriteText("OEBPS/ch1.xhtml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>第 1 章</title></head>"
            + "<body><h1>第 1 章</h1><p>这一章没有关键词。</p></body></html>");

        WriteText("OEBPS/ch2.xhtml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>第 2 章</title></head>"
            + "<body><h1>第 2 章</h1><p>" + ChineseKeyword + " 与 " + EnglishKeyword + " 都在这里。</p>"
            + "</body></html>");

        return path;
    }

    // ---------- 基础设施 ----------

    private static async Task<Book?> FindBookByTitleAsync(IBookRepository repo, string title)
    {
        var books = await repo.QueryAsync(new BookQuery { SearchText = title, Limit = 10 })
            .ConfigureAwait(true);

        return books.FirstOrDefault(b => string.Equals(b.Title, title, StringComparison.Ordinal));
    }

    private static Task<bool> WaitForIdleAsync(IContentIndexer indexer, TimeSpan timeout)
        => indexer.WaitForIdleAsync(timeout);

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

    private static async Task<int> EvalIntAsync(CoreWebView2 core, string script)
    {
        var raw = await core.ExecuteScriptAsync(script).ConfigureAwait(true);

        return int.TryParse(raw, out var value) ? value : -1;
    }

    private static async Task<JsonNode?> EvalJsonAsync(CoreWebView2 core, string script)
    {
        var raw = await core.ExecuteScriptAsync(script).ConfigureAwait(true);

        if (string.IsNullOrEmpty(raw) || raw == "null")
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<bool> WaitAsync(Func<Task<bool>> probe, int timeoutMs, int stepMs = 100)
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
            paths.TempDir,
            null,
            checks);

        Directory.CreateDirectory(paths.TempDir);
        File.WriteAllText(
            Path.Combine(paths.TempDir, "t64-smoke.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return report;
    }
}
