using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using EbookToolSuite.App.Views;
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
/// T6-5「书签与笔记」的端到端验收。判据与计划书 5374-5381 一一对应：
/// <list type="number">
/// <item>加书签 → 重启 → 书签还在，能跳回；</item>
/// <item>加笔记 → 重启 → 笔记还在，能跳回；</item>
/// <item>跨元素选中文字能正确记录（起止路径不同容器）；</item>
/// <item>选中文字浮出弹条，点空白处消失，弹条动作与右键菜单等价；</item>
/// <item>位置失效时降级并提示（坏锚点 → 章内比例 + 提示语）；</item>
/// <item>能导出全部书签/笔记为 Markdown（真读回文件断言内容）；</item>
/// <item>一本书 100 条笔记，列表渲染流畅。</item>
/// </list>
/// </summary>
/// <remarks>
/// 全部断言走真窗口 + 真 WebView2 + 真库：书签/笔记落库与回读是这条链路的价值所在，
/// 任何一段 mock 掉都会让"重启后还在"变成自说自话。
/// </remarks>
public static class T65SmokeTest
{
    /// <summary>判据 7 的规模：一本书 100 条笔记。</summary>
    private const int ManyNotes = 100;

    /// <summary>判据 7 的渲染门槛：把 100 条笔记画进面板不超过它（毫秒）。</summary>
    private const int ListRenderBudgetMs = 1000;

    /// <summary>判据 1 的书内滚动位置（像素）：加书签的位置，重开后要能回去。</summary>
    private const int BookmarkScrollTop = 300;

    private const string BookTitle = "书签笔记冒烟书";

    public static async Task<SmokeReport> RunAsync(
        IBookImportService importer,
        IReaderService reader,
        IBookRepository repo,
        IWebViewEnvironmentProvider environments,
        IReadingProgressService progress,
        IAnnotationService annotations,
        ILoggerFactory loggerFactory,
        AppPaths paths,
        string corpusDir)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        void Add(string name, bool ok, string detail) => checks.Add(new SmokeCheck(name, ok, detail));

        var ctx = new Ctx
        {
            Reader = reader,
            Repo = repo,
            Environments = environments,
            Progress = progress,
            Annotations = annotations,
            Loggers = loggerFactory,
            Paths = paths,
        };

        // ---------- 0. 样本与导入 ----------
        var epub = WriteEpub(corpusDir);
        var result = await importer.ImportAsync(epub, ImportOptions.Default).ConfigureAwait(true);
        var book = await FindBookByTitleAsync(repo, BookTitle).ConfigureAwait(true);

        ctx.BookId = book?.Id ?? Guid.Empty;

        Add("J0-corpus-imported", result.Succeeded && book is not null,
            result.Succeeded
                ? $"导入成功：{Path.GetFileName(epub)}，bookId={book!.Id}"
                : $"导入失败：{result.Outcome} {result.Message}");

        if (!result.Succeeded || book is null)
        {
            return Finish(paths, startedAt, stopwatch, checks);
        }

        var bookId = book.Id;

        // ---------- 第一次打开：J3 跨元素锚点 / J4 弹条 / J1 加书签 / J2 加笔记 ----------
        ReaderWindow? window = null;
        CoreWebView2? core = null;

        try
        {
            (window, core) = await OpenAsync(ctx, bookId).ConfigureAwait(true);

            if (core is null)
            {
                Add("J-open", false, "窗口起来了但 WebView2 没有就绪");
                return Finish(paths, startedAt, stopwatch, checks);
            }

            /* ---------- 判据 3：跨元素选中 ---------- */
            var across = await EvalAsync(core,
                "window.__readerDebug.selectAcrossParagraphs(0, 1)").ConfigureAwait(true);
            var quoteAcross = across?.GetValue<string>() ?? string.Empty;

            var anchorNode = await EvalAsync(core, "window.__readerDebug.selectionAnchor()")
                .ConfigureAwait(true);
            var anchor = anchorNode?["anchor"] as JsonObject;

            var startPath = anchor?["startPath"]?.GetValue<string>() ?? string.Empty;
            var endPath = anchor?["endPath"]?.GetValue<string>() ?? string.Empty;
            var anchorQuote = anchorNode?["quote"]?.GetValue<string>() ?? string.Empty;

            Add("J3-selection-across-nodes",
                startPath.StartsWith("body/", StringComparison.Ordinal)
                && endPath.StartsWith("body/", StringComparison.Ordinal)
                && startPath != endPath
                && anchorQuote.Length > 3
                && quoteAcross.Contains("第 2 段", StringComparison.Ordinal),
                $"选区文字=「{Trim(quoteAcross, 40)}」；startPath={startPath}；endPath={endPath}；" +
                $"起止容器不同={(startPath != endPath)}；引文=「{Trim(anchorQuote, 20)}」");

            /* ---------- 判据 4：弹条 / 点空白消失 / 右键等价 ---------- */
            await EvalAsync(core, "window.__readerDebug.showSelBar()").ConfigureAwait(true);

            var barShown = await EvalAsync(core, "window.__readerDebug.selBarState()").ConfigureAwait(true);
            var barHiddenAfterMouseDown = false;

            await EvalAsync(core,
                "(function(){ document.body.dispatchEvent(new MouseEvent('mousedown', { bubbles: true })); return 1; })()")
                .ConfigureAwait(true);

            var barAfter = await EvalAsync(core, "window.__readerDebug.selBarState()").ConfigureAwait(true);

            if (barAfter is JsonObject afterObj && afterObj["hidden"]?.GetValue<bool>() == true)
            {
                barHiddenAfterMouseDown = true;
            }

            // 右键入口：重新造选区再走 contextmenu 的同一个 handler
            await EvalAsync(core, "window.__readerDebug.selectAcrossParagraphs(0, 1)").ConfigureAwait(true);
            await EvalAsync(core, "window.__readerDebug.contextMenuNow()").ConfigureAwait(true);

            var barByContext = await EvalAsync(core, "window.__readerDebug.selBarState()").ConfigureAwait(true);
            var contextShowsBar = barByContext is JsonObject ctxBar
                && ctxBar["hidden"]?.GetValue<bool>() == false
                && (ctxBar["quote"]?.GetValue<string>() ?? string.Empty).Length > 3;

            Add("J4-selection-bar",
                barShown is JsonObject shown && shown["hidden"]?.GetValue<bool>() == false
                && (shown["quote"]?.GetValue<string>() ?? string.Empty).Length > 3
                && barHiddenAfterMouseDown
                && contextShowsBar,
                $"选中后弹条可见={(barShown is JsonObject s1 && s1["hidden"]?.GetValue<bool>() == false)}；" +
                $"点空白后消失={barHiddenAfterMouseDown}；右键等价={contextShowsBar}");

            /* ---------- 判据 1a：在滚动位置加书签 ---------- */
            var scrolled = await EvalIntAsync(core,
                "(function(){ var w=document.getElementById('chapter').contentWindow; " +
                "var max=Math.max(0, w.document.documentElement.scrollHeight - w.innerHeight); " +
                "w.scrollTo(0, Math.min(" + BookmarkScrollTop + ", max)); return Math.round(w.scrollY); })()")
                .ConfigureAwait(true);

            await EvalAsync(core, "window.__readerDebug.addBookmarkNow()").ConfigureAwait(true);

            var bookmarkSaved = await WaitAsync(
                async () =>
                {
                    var state = await EvalAsync(core, "window.__readerDebug.annotationState()")
                        .ConfigureAwait(true);

                    return state is JsonObject o && (o["bookmarkCount"]?.GetValue<int>() ?? 0) >= 1;
                },
                10000).ConfigureAwait(true);

            var bookmarksOnDisk = await annotations.ListBookmarksAsync(bookId).ConfigureAwait(true);

            Add("J1a-bookmark-added",
                bookmarkSaved && bookmarksOnDisk.Count >= 1
                && bookmarksOnDisk[0].SpineIndex is not null,
                $"页面内书签数={bookmarkSaved}；库里={bookmarksOnDisk.Count} 条；" +
                $"滚动位置={scrolled}px；标签=「{bookmarksOnDisk.FirstOrDefault()?.Label}」");

            // 滚回顶部，重开后才能证明"是书签把它带回去的"
            await EvalAsync(core,
                "(function(){ var w=document.getElementById('chapter').contentWindow; w.scrollTo(0,0); return 1; })()")
                .ConfigureAwait(true);

            /* ---------- 判据 2a：加笔记（批注） ---------- */
            await EvalAsync(core, "window.__readerDebug.selectAcrossParagraphs(0, 1)").ConfigureAwait(true);
            await EvalAsync(core, "window.__readerDebug.addNoteNow(1, '我的第一条批注')").ConfigureAwait(true);

            var noteSaved = await WaitAsync(
                async () =>
                {
                    var state = await EvalAsync(core, "window.__readerDebug.annotationState()")
                        .ConfigureAwait(true);

                    return state is JsonObject o && (o["noteCount"]?.GetValue<int>() ?? 0) >= 1;
                },
                10000).ConfigureAwait(true);

            var notesOnDisk = await annotations.ListNotesAsync(bookId).ConfigureAwait(true);

            Add("J2a-note-added",
                noteSaved && notesOnDisk.Count >= 1
                && !string.IsNullOrWhiteSpace(notesOnDisk[0].Quote)
                && notesOnDisk[0].Anchor?.StartPath is not null,
                $"页面内笔记数={noteSaved}；库里={notesOnDisk.Count} 条；" +
                $"引文=「{Trim(notesOnDisk.FirstOrDefault()?.Quote ?? "", 24)}」；" +
                $"批注=「{Trim(notesOnDisk.FirstOrDefault()?.Content ?? "", 20)}」");

            /* ---------- 判据 8（用户 2026-10-18 补短）：四种标记 + 再点取消 + 换样式 ---------- */
            async Task<int> MarkCountAsync(int style)
            {
                var key = style switch { 0 => "highlight", 1 => "underline", 2 => "wavy", _ => "strike" };
                var state = await EvalAsync(core!, "window.__readerDebug.annotationState()")
                    .ConfigureAwait(true);
                var counts = state?["markCounts"] as JsonObject;

                return counts?[key]?.GetValue<int>() ?? -1;
            }

            async Task<int> NoteCountAsync()
            {
                var state = await EvalAsync(core!, "window.__readerDebug.annotationState()")
                    .ConfigureAwait(true);

                return state?["noteCount"]?.GetValue<int>() ?? -1;
            }

            async Task SelectAsync()
                => await EvalAsync(core!, "window.__readerDebug.selectAcrossParagraphs(0, 1)")
                    .ConfigureAwait(true);

            async Task MarkAsync(int style)
                => await EvalAsync(core!, "window.__readerDebug.toggleMark(" + style + ")")
                    .ConfigureAwait(true);

            var trace = new List<string>();

            async Task SnapshotAsync(string tag)
            {
                var state = await EvalAsync(core!, "window.__readerDebug.annotationState()")
                    .ConfigureAwait(true);

                trace.Add(tag + "=notes:" + (state?["noteCount"]?.GetValue<int>() ?? -1) +
                    ",marks:" + (state?["noteMarks"]?.GetValue<int>() ?? -1) +
                    "," + (state?["markCounts"]?.ToJsonString() ?? "?"));
            }

            var baseNotes = await NoteCountAsync();
            var highlightBase = await MarkCountAsync(0);
            await SnapshotAsync("base").ConfigureAwait(true);

            /* 注意口径：库里本来就有一条批注（style 0），所以高亮的"加"要按
               "比基线多"判断、"取消"按"回到基线"判断，不能写死 1/0。 */
            async Task<bool> WaitMoreAsync(int style, int baseCount)
                => await WaitAsync(
                    async () => await MarkCountAsync(style).ConfigureAwait(true) > baseCount,
                    6000).ConfigureAwait(true);

            async Task<bool> WaitExactlyAsync(int style, int expected)
                => await WaitAsync(
                    async () => await MarkCountAsync(style).ConfigureAwait(true) == expected,
                    6000).ConfigureAwait(true);

            // 高亮：加 → 再点取消
            await SelectAsync().ConfigureAwait(true);
            await MarkAsync(0).ConfigureAwait(true);
            var highlightOn = await WaitMoreAsync(0, highlightBase).ConfigureAwait(true);
            await SnapshotAsync("hl+").ConfigureAwait(true);
            await SelectAsync().ConfigureAwait(true);
            await MarkAsync(0).ConfigureAwait(true);
            var highlightOff = await WaitExactlyAsync(0, highlightBase).ConfigureAwait(true);
            await SnapshotAsync("hl-").ConfigureAwait(true);

            // 下划线：加 → 同段改点波浪线（换样式）
            await SelectAsync().ConfigureAwait(true);
            await MarkAsync(1).ConfigureAwait(true);
            var underlineOn = await WaitMoreAsync(1, 0).ConfigureAwait(true);
            await SnapshotAsync("ul+").ConfigureAwait(true);
            await SelectAsync().ConfigureAwait(true);
            await MarkAsync(2).ConfigureAwait(true);
            var wavyOn = await WaitMoreAsync(2, 0).ConfigureAwait(true)
                && await WaitExactlyAsync(1, 0).ConfigureAwait(true);
            await SnapshotAsync("wavy+").ConfigureAwait(true);

            // 波浪线：再点取消
            await SelectAsync().ConfigureAwait(true);
            await MarkAsync(2).ConfigureAwait(true);
            var wavyOff = await WaitExactlyAsync(2, 0).ConfigureAwait(true);
            await SnapshotAsync("wavy-").ConfigureAwait(true);

            // 删除线：加 → 再点取消
            await SelectAsync().ConfigureAwait(true);
            await MarkAsync(3).ConfigureAwait(true);
            var strikeOn = await WaitMoreAsync(3, 0).ConfigureAwait(true);
            await SnapshotAsync("st+").ConfigureAwait(true);
            await SelectAsync().ConfigureAwait(true);
            await MarkAsync(3).ConfigureAwait(true);
            var strikeOff = await WaitExactlyAsync(3, 0).ConfigureAwait(true);
            await SnapshotAsync("st-").ConfigureAwait(true);

            var finalNotes = await NoteCountAsync();

            Add("J8-marks-and-toggle",
                highlightOn && highlightOff && underlineOn && wavyOn && wavyOff && strikeOn && strikeOff
                && finalNotes == baseNotes,
                $"高亮 加/取消={highlightOn}/{highlightOff}；下划线加={underlineOn}；换波浪线={wavyOn}；" +
                $"波浪线取消={wavyOff}；删除线 加/取消={strikeOn}/{strikeOff}；" +
                $"笔记数 起始={baseNotes} 末尾={finalNotes}（期望相等）；" +
                $"轨迹：{string.Join(" | ", trace)}");

            /* ---------- 判据 10（用户 2026-10-18 补短）：点已标记文字弹出笔记卡片 ---------- */
            await SelectAsync().ConfigureAwait(true);
            await MarkAsync(1).ConfigureAwait(true);
            var cardMarkReady = await WaitMoreAsync(1, 0).ConfigureAwait(true);

            var clicked = await EvalIntAsync(core!,
                "(function(){ var d=document.getElementById('chapter').contentDocument; " +
                // 同一段文字可能同时有批注与下划线（嵌套 mark），这里明确点下划线那一个
                "var m=d.querySelector('mark.ets-mark-underline'); if(!m) return 0; " +
                // 真实鼠标点击会先 collapse 选区；脚本 click() 不会，所以手动清掉
                "var s=d.defaultView.getSelection(); if(s) s.removeAllRanges(); m.click(); return 1; })()")
                .ConfigureAwait(true) == 1;

            var cardOpen = await WaitAsync(
                async () =>
                {
                    var card = await EvalAsync(core!, "window.__readerDebug.noteCardState()")
                        .ConfigureAwait(true);

                    return card?["open"]?.GetValue<bool>() == true;
                },
                6000).ConfigureAwait(true);

            var cardState = await EvalAsync(core!, "window.__readerDebug.noteCardState()")
                .ConfigureAwait(true);
            var cardStyle = cardState?["style"]?.GetValue<string>() ?? "";
            var cardQuote = cardState?["quote"]?.GetValue<string>() ?? "";

            await EvalAsync(core!, "window.__readerDebug.closeNoteCardNow()").ConfigureAwait(true);

            var cardClosed = await WaitAsync(
                async () =>
                {
                    var card = await EvalAsync(core!, "window.__readerDebug.noteCardState()")
                        .ConfigureAwait(true);

                    return card?["open"]?.GetValue<bool>() == false;
                },
                4000).ConfigureAwait(true);

            // 清理掉这条下划线，别影响后面书签页/笔记页的断言
            await SelectAsync().ConfigureAwait(true);
            await MarkAsync(1).ConfigureAwait(true);
            var cardCleanup = await WaitExactlyAsync(1, 0).ConfigureAwait(true);

            Add("J10-note-card-on-click",
                cardMarkReady && clicked && cardOpen && cardStyle == "下划线" && cardQuote.Length > 0
                && cardClosed && cardCleanup,
                $"点已标记文字：点击成功={clicked} 卡片弹出={cardOpen} 样式标签=「{cardStyle}」" +
                $" 引文=「{Trim(cardQuote, 20)}」 关闭后={cardClosed} 清理={cardCleanup}");

            /* ---------- 判据 11（用户 2026-10-18 第三轮反馈）：面板颜色/样式/备注/删除 ---------- */
            await SelectAsync().ConfigureAwait(true);
            await MarkAsync(1).ConfigureAwait(true);
            var panelMarkReady = await WaitMoreAsync(1, 0).ConfigureAwait(true);

            // 点开这条下划线
            await EvalIntAsync(core!,
                "(function(){ var d=document.getElementById('chapter').contentDocument; " +
                "var m=d.querySelector('mark.ets-mark-underline'); if(!m) return 0; " +
                "var s=d.defaultView.getSelection(); if(s) s.removeAllRanges(); m.click(); return 1; })()")
                .ConfigureAwait(true);

            await WaitAsync(
                async () => (await EvalAsync(core!, "window.__readerDebug.notePanelState()")
                    .ConfigureAwait(true))?["open"]?.GetValue<bool>() == true,
                6000).ConfigureAwait(true);

            var panelState = await EvalAsync(core!, "window.__readerDebug.notePanelState()")
                .ConfigureAwait(true);

            var colorDots = panelState?["colorDots"]?.GetValue<int>() ?? 0;
            var styleButtons = panelState?["styleButtons"]?.GetValue<int>() ?? 0;

            // 换样式：波浪线（2）
            await EvalAsync(core!, "window.__readerDebug.notePanelPickStyle(2)").ConfigureAwait(true);

            var styleSwitched = await WaitAsync(
                async () =>
                {
                    var state = await EvalAsync(core!, "window.__readerDebug.annotationState()")
                        .ConfigureAwait(true);

                    return state is JsonObject o
                        && (o["noteStyles"]?.AsArray().Any(v => v?.GetValue<int>() == 2) ?? false);
                },
                8000).ConfigureAwait(true);

            // 选颜色：蓝（6）
            await EvalAsync(core!, "window.__readerDebug.notePanelPickColor(11)").ConfigureAwait(true);

            var colorPicked = await WaitAsync(
                async () =>
                {
                    var state = await EvalAsync(core!, "window.__readerDebug.annotationState()")
                        .ConfigureAwait(true);

                    return state is JsonObject o
                        && (o["noteColors"]?.AsArray().Any(v => v?.GetValue<int>() == 11) ?? false);
                },
                8000).ConfigureAwait(true);

            // 备注 + 保存
            await EvalAsync(core!, "window.__readerDebug.notePanelSetText('这是面板里写下的备注')")
                .ConfigureAwait(true);
            await EvalAsync(core!, "window.__readerDebug.notePanelSaveNow()").ConfigureAwait(true);

            var contentSaved = await WaitAsync(
                async () =>
                {
                    var state = await EvalAsync(core!, "window.__readerDebug.annotationState()")
                        .ConfigureAwait(true);

                    return state is JsonObject o
                        && (o["noteContents"]?.AsArray()
                            .Any(v => v is not null && v.GetValue<string>().Contains("面板里写下", StringComparison.Ordinal))
                            ?? false);
                },
                8000).ConfigureAwait(true);

            var afterSave = await EvalAsync(core!, "window.__readerDebug.annotationState()")
                .ConfigureAwait(true);
            var notesAfterSave = afterSave?["noteCount"]?.GetValue<int>() ?? -1;

            // 再点开并删除
            await EvalIntAsync(core!,
                "(function(){ var d=document.getElementById('chapter').contentDocument; " +
                "var m=d.querySelector('mark.ets-mark-wavy'); if(!m) return 0; " +
                "var s=d.defaultView.getSelection(); if(s) s.removeAllRanges(); m.click(); return 1; })()")
                .ConfigureAwait(true);

            await WaitAsync(
                async () => (await EvalAsync(core!, "window.__readerDebug.notePanelState()")
                    .ConfigureAwait(true))?["open"]?.GetValue<bool>() == true,
                6000).ConfigureAwait(true);

            await EvalAsync(core!, "window.__readerDebug.notePanelDeleteNow()").ConfigureAwait(true);

            var deleted = await WaitAsync(
                async () =>
                {
                    var state = await EvalAsync(core!, "window.__readerDebug.annotationState()")
                        .ConfigureAwait(true);

                    return state is JsonObject o && (o["noteCount"]?.GetValue<int>() ?? -1) == baseNotes;
                },
                8000).ConfigureAwait(true);

            Add("J11-note-panel-colors-styles",
                panelMarkReady && colorDots == 14 && styleButtons == 7
                && styleSwitched && colorPicked && contentSaved && notesAfterSave == baseNotes + 1 && deleted,
                $"面板：颜色点={colorDots}（期望 14）样式按钮={styleButtons}（期望 7）；" +
                $"换波浪线={styleSwitched} 选蓝色={colorPicked} 备注保存={contentSaved}；" +
                $"笔记数 保存后={notesAfterSave}（期望 {baseNotes + 1}）删除后回到基线={deleted}");

            /* ---------- 判据 12（用户 2026-10-18 第四/五轮反馈）：
               荧光底色"降调"= 降低透明度（不是混白）；荧光下的文字色跟随主题，不写死。 ---------- */
            await SelectAsync().ConfigureAwait(true);
            await EvalAsync(core!, "window.__readerDebug.addMarkNow(0, 11)").ConfigureAwait(true);

            var highlightNoteId = string.Empty;

            var highlightBlueReady = await WaitAsync(
                async () =>
                {
                    var state = await EvalAsync(core!, "window.__readerDebug.annotationState()")
                        .ConfigureAwait(true);

                    highlightNoteId = FindNoteId(state, 0, 11) ?? string.Empty;

                    return highlightNoteId.Length > 0;
                },
                8000).ConfigureAwait(true);

            var lightProbe = highlightNoteId.Length > 0
                ? await EvalAsync(core!, MarkProbeJs(highlightNoteId)).ConfigureAwait(true)
                : null;

            // 切暗色主题：文字色应跟着变（继承正文），荧光底色仍是半透明
            await EvalAsync(core!, "window.__readerDebug.setThemeNow('dark')").ConfigureAwait(true);
            await Task.Delay(500).ConfigureAwait(true);

            var darkProbe = highlightNoteId.Length > 0
                ? await EvalAsync(core!, MarkProbeJs(highlightNoteId)).ConfigureAwait(true)
                : null;

            await EvalAsync(core!, "window.__readerDebug.setThemeNow('light')").ConfigureAwait(true);
            await Task.Delay(300).ConfigureAwait(true);

            var lightBg = RgbaOf(lightProbe?["bg"]?.GetValue<string>() ?? "");
            var lightMarkColor = lightProbe?["markColor"]?.GetValue<string>() ?? string.Empty;
            var lightBodyColor = lightProbe?["bodyColor"]?.GetValue<string>() ?? string.Empty;
            var darkBg = RgbaOf(darkProbe?["bg"]?.GetValue<string>() ?? "");
            var darkMarkColor = darkProbe?["markColor"]?.GetValue<string>() ?? string.Empty;
            var darkBodyColor = darkProbe?["bodyColor"]?.GetValue<string>() ?? string.Empty;

            // 半透明：alpha < 1 且 RGB 仍是原色（30,136,229）
            var translucent = lightBg is not null
                && lightBg.Value.A < 1
                && lightBg.Value.R == 30 && lightBg.Value.G == 136 && lightBg.Value.B == 229;

            // 文字色跟随主题：亮/暗两种主题下 mark 的字色都等于正文色，且两主题之间不同
            var followsTheme = lightMarkColor.Length > 0
                && lightMarkColor == lightBodyColor
                && darkMarkColor == darkBodyColor
                && !string.Equals(lightMarkColor, darkMarkColor, StringComparison.Ordinal);

            // 背景色样式用原色且不透明（同一种蓝）
            await SelectAsync().ConfigureAwait(true);
            await EvalAsync(core!, "window.__readerDebug.addMarkNow(5, 11)").ConfigureAwait(true);

            var solidNoteId = string.Empty;

            var solidBlueReady = await WaitAsync(
                async () =>
                {
                    var state = await EvalAsync(core!, "window.__readerDebug.annotationState()")
                        .ConfigureAwait(true);

                    solidNoteId = FindNoteId(state, 5, 11) ?? string.Empty;

                    return solidNoteId.Length > 0;
                },
                8000).ConfigureAwait(true);

            var solidBg = solidNoteId.Length > 0
                ? await EvalAsync(core!, MarkProbeJs(solidNoteId)).ConfigureAwait(true)
                : null;
            var solidRgba = RgbaOf(solidBg?["bg"]?.GetValue<string>() ?? "");
            var solidIsOriginal = solidRgba is not null
                && solidRgba.Value.A == 1
                && solidRgba.Value.R == 30 && solidRgba.Value.G == 136 && solidRgba.Value.B == 229;

            // 色点显示原色：打开草稿面板读一次（不保存）
            await SelectAsync().ConfigureAwait(true);
            await EvalAsync(core!, "window.__readerDebug.notePanelDraftNow()").ConfigureAwait(true);

            var dotColor = await EvalStrAsync(core!,
                "(function(){ var row=document.getElementById('noteColorRow'); if(!row) return ''; " +
                "var b=row.querySelector('[data-color=\"11\"]'); " +
                "return b ? getComputedStyle(b).backgroundColor : ''; })()").ConfigureAwait(true);

            await EvalAsync(core!, "window.__readerDebug.closeNotePanelNow()").ConfigureAwait(true);

            var dotRgba = RgbaOf(dotColor);
            var dotIsOriginal = dotRgba is not null
                && dotRgba.Value.A == 1
                && dotRgba.Value.R == 30 && dotRgba.Value.G == 136 && dotRgba.Value.B == 229;

            /* 清理：按 note id 精确点开再删除。
               注意不能按 class 找——批注本身也是 mark.ets-note，会删错（首轮实测踩到）。 */
            if (highlightNoteId.Length > 0)
            {
                await EvalIntAsync(core!, ClickMarkJs(highlightNoteId)).ConfigureAwait(true);
                await EvalAsync(core!, "window.__readerDebug.notePanelDeleteNow()").ConfigureAwait(true);
            }

            if (solidNoteId.Length > 0)
            {
                await EvalIntAsync(core!, ClickMarkJs(solidNoteId)).ConfigureAwait(true);
                await EvalAsync(core!, "window.__readerDebug.notePanelDeleteNow()").ConfigureAwait(true);
            }

            var j12Clean = await WaitAsync(
                async () =>
                {
                    var state = await EvalAsync(core!, "window.__readerDebug.annotationState()")
                        .ConfigureAwait(true);

                    return state is JsonObject o && (o["noteCount"]?.GetValue<int>() ?? -1) == baseNotes;
                },
                8000).ConfigureAwait(true);

            Add("J12-highlight-tint",
                highlightBlueReady && translucent && followsTheme
                && solidBlueReady && solidIsOriginal && dotIsOriginal && j12Clean,
                $"荧光+蓝的底色={lightProbe?["bg"]?.GetValue<string>()}" +
                $"（半透明降调：{translucent}，alpha<1 且 RGB 仍为 30/136/229）；" +
                $"文字色跟随主题={followsTheme}（亮：mark={lightMarkColor}/正文={lightBodyColor}；" +
                $"暗：mark={darkMarkColor}/正文={darkBodyColor}）；暗色下荧光底={darkProbe?["bg"]?.GetValue<string>()}；" +
                $"背景色+蓝={solidBg?["bg"]?.GetValue<string>()}（原色不透明：{solidIsOriginal}）；" +
                $"色点={dotColor}（原色不透明：{dotIsOriginal}）；清理={j12Clean}");

            await CloseAsync(window).ConfigureAwait(true);
            window = null;
        }
        catch (Exception ex)
        {
            Add("J1-J4-exception", false, ex.GetType().Name + ": " + ex.Message);

            if (window is not null)
            {
                await CloseAsync(window).ConfigureAwait(true);
            }
        }

        // ---------- 第二次打开：J1b 书签还在能跳回 / J2b 笔记还在能跳回 ----------
        try
        {
            (window, core) = await OpenAsync(ctx, bookId).ConfigureAwait(true);

            if (core is null)
            {
                Add("J-reopen", false, "重开后 WebView2 没有就绪");
                return Finish(paths, startedAt, stopwatch, checks);
            }

            var stateAfter = await WaitAsync(
                async () =>
                {
                    var state = await EvalAsync(core, "window.__readerDebug.annotationState()")
                        .ConfigureAwait(true);

                    return state is JsonObject o
                        && (o["bookmarkCount"]?.GetValue<int>() ?? 0) >= 1
                        && (o["noteCount"]?.GetValue<int>() ?? 0) >= 1;
                },
                15000).ConfigureAwait(true);

            var reopened = await EvalAsync(core, "window.__readerDebug.annotationState()").ConfigureAwait(true);

            var bookmarkCount = reopened?["bookmarkCount"]?.GetValue<int>() ?? 0;
            var noteCount = reopened?["noteCount"]?.GetValue<int>() ?? 0;
            var noteMarks = reopened?["noteMarks"]?.GetValue<int>() ?? 0;

            Add("J1b-bookmark-survives-restart", stateAfter && bookmarkCount >= 1,
                $"重开后书签数={bookmarkCount}（期望 ≥1）；标签=「{reopened?["bookmarkLabels"]?.ToJsonString()}」");

            await EvalAsync(core, "window.__readerDebug.jumpBookmark(0)").ConfigureAwait(true);

            var jumped = await WaitAsync(
                async () =>
                {
                    var y = await EvalIntAsync(core,
                        "Math.round(document.getElementById('chapter').contentWindow.scrollY)").ConfigureAwait(true);

                    return Math.Abs(y - BookmarkScrollTop) <= 60;
                },
                8000).ConfigureAwait(true);

            var jumpY = await EvalIntAsync(core,
                "Math.round(document.getElementById('chapter').contentWindow.scrollY)").ConfigureAwait(true);

            Add("J1b-bookmark-jumps-back", jumped,
                $"点书签后 scrollY={jumpY}px（加书签时约 {BookmarkScrollTop}px，容差 60）；命中={jumped}");

            Add("J2b-note-survives-restart", stateAfter && noteCount >= 1,
                $"重开后笔记数={noteCount}（期望 ≥1）；渲染出的 mark 数={noteMarks}；" +
                $"引文=「{Trim(reopened?["noteQuotes"]?.ToJsonString() ?? "", 40)}」");

            await EvalAsync(core, "window.__readerDebug.jumpNote(0)").ConfigureAwait(true);

            var noteJumpMarks = await EvalIntAsync(core,
                "window.__readerDebug.annotationState().noteMarks").ConfigureAwait(true);

            Add("J2b-note-jumps-back", noteMarks >= 1 && noteJumpMarks >= 1,
                $"跳转后本章 mark.ets-note={noteJumpMarks} 处（≥1 才算高亮真的画回来了）");

            /* ---------- 判据 9（用户 2026-10-18 补短）：书签页与笔记页分开 ---------- */
            await EvalAsync(core, "window.__readerDebug.openAnnotations('bookmarks')").ConfigureAwait(true);
            var bookmarkPanel = await EvalAsync(core, "window.__readerDebug.annotationState()")
                .ConfigureAwait(true);

            await EvalAsync(core, "window.__readerDebug.openAnnotations('notes')").ConfigureAwait(true);
            var notePanel = await EvalAsync(core, "window.__readerDebug.annotationState()")
                .ConfigureAwait(true);

            var bookmarkMode = bookmarkPanel?["panelMode"]?.GetValue<string>() ?? "";
            var bookmarkTitle = bookmarkPanel?["panelTitle"]?.GetValue<string>() ?? "";
            var bookmarkRows = bookmarkPanel?["rows"]?.GetValue<int>() ?? -1;
            var bookmarkAddVisible = bookmarkPanel?["addBookmarkVisible"]?.GetValue<bool>() ?? false;
            var bookmarkTotal = bookmarkPanel?["bookmarkCount"]?.GetValue<int>() ?? 0;

            var noteMode = notePanel?["panelMode"]?.GetValue<string>() ?? "";
            var noteTitle = notePanel?["panelTitle"]?.GetValue<string>() ?? "";
            var noteRows = notePanel?["rows"]?.GetValue<int>() ?? -1;
            var noteAddVisible = notePanel?["addBookmarkVisible"]?.GetValue<bool>() ?? true;
            var noteTotal = notePanel?["noteCount"]?.GetValue<int>() ?? 0;

            Add("J9-panels-separated",
                bookmarkMode == "bookmarks" && bookmarkTitle == "书签" && bookmarkAddVisible
                && bookmarkRows == bookmarkTotal && bookmarkTotal >= 1
                && noteMode == "notes" && noteTitle == "笔记" && !noteAddVisible
                && noteRows == noteTotal && noteTotal >= 1,
                $"书签页：模式={bookmarkMode} 标题=「{bookmarkTitle}」行={bookmarkRows} " +
                $"「添加书签」可见={bookmarkAddVisible} 书签数={bookmarkTotal}；" +
                $"笔记页：模式={noteMode} 标题=「{noteTitle}」行={noteRows} " +
                $"「添加书签」可见={noteAddVisible} 笔记数={noteTotal}");

            /* 用户 2026-10-19 要求：目录抽屉右上角、关闭按钮左侧要有「编辑目录」，
               点它跳到书籍信息与目录编辑页；保存后目录自动刷新（宿主 reader.toc）。
               这里只验界面位置——真正打开编辑器由 EditSmokeTest 覆盖，
               冒烟里不打开编辑器：窗口初始化是异步的，过早关闭会让 CoreWebView2 访问已释放的控件。 */
            await EvalAsync(core!, "document.getElementById('btnToc').click();1").ConfigureAwait(true);
            await Task.Delay(400).ConfigureAwait(true);

            const string tocEditJs = """
                (function(){
                  function r(id){ var e = document.getElementById(id); if (!e) return null;
                    var b = e.getBoundingClientRect();
                    return { l: Math.round(b.left), r: Math.round(b.right), w: Math.round(b.width) }; }
                  var panel = document.getElementById('tocPanel');
                  return { edit: r('tocEdit'), close: r('tocClose'), open: panel ? !panel.hidden : false };
                })()
                """;

            var tocEdit = await EvalAsync(core!, tocEditJs).ConfigureAwait(true);
            var editRect = tocEdit?["edit"] as JsonObject;
            var closeRect = tocEdit?["close"] as JsonObject;
            var editWidth = editRect?["w"]?.GetValue<double>() ?? 0;
            var editRight = editRect?["r"]?.GetValue<double>() ?? 0;
            var closeLeft = closeRect?["l"]?.GetValue<double>() ?? 0;

            await EvalAsync(core!, "document.getElementById('tocClose').click();1").ConfigureAwait(true);

            Add("J13-toc-edit-button",
                editWidth > 0 && editRight <= closeLeft + 1,
                $"目录抽屉里「编辑目录」宽={editWidth:F0}px、右边={editRight:F0}px，" +
                $"「关闭」左边={closeLeft:F0}px（编辑在关闭左侧={editRight <= closeLeft + 1}）；" +
                "点击后由宿主打开书籍编辑器（EditSmokeTest 覆盖该路径）");


            /* 保存后自动刷新（用户 2026-10-19 要求）：直接调宿主的 RefreshAfterEditAsync——
               它与编辑器 Saved 事件走同一条路径（重读源文件 → 推 reader.toc → 页面重画目录）。
               内容正确性由 ReadMetadataTests 钉住，这里验"推送真的到达页面并重画"。 */
            await EvalAsync(core!, "document.getElementById('btnToc').click();1").ConfigureAwait(true);
            await Task.Delay(300).ConfigureAwait(true);

            var noticeBefore = (await EvalAsync(core!, "window.__readerDebug.lastNotice()").ConfigureAwait(true))?.GetValue<string>() ?? "";
            var tocBefore = (await EvalAsync(core!, "document.querySelectorAll('#tocList .tocItem').length").ConfigureAwait(true))?.GetValue<int>() ?? -1;

            await window!.RefreshAfterEditAsync().ConfigureAwait(true);

            var noticeShown = await WaitAsync(
                async () =>
                {
                    var n = (await EvalAsync(core!, "window.__readerDebug.lastNotice()").ConfigureAwait(true))?.GetValue<string>() ?? "";
                    return n != noticeBefore && n.Contains("目录已保存");
                },
                8000).ConfigureAwait(true);

            var tocAfter = (await EvalAsync(core!, "document.querySelectorAll('#tocList .tocItem').length").ConfigureAwait(true))?.GetValue<int>() ?? -1;

            Add("J14-toc-auto-refresh",
                noticeShown && tocAfter == tocBefore && tocBefore > 0,
                $"保存通知后页面提示含「目录已保存」={noticeShown}；目录条目 {tocBefore} → {tocAfter}（应相等：源文件没改内容）");

            await EvalAsync(core!, "document.getElementById('tocClose').click();1").ConfigureAwait(true);

            await CloseAsync(window).ConfigureAwait(true);
            window = null;
        }
        catch (Exception ex)
        {
            Add("J1b-J2b-exception", false, ex.GetType().Name + ": " + ex.Message);

            if (window is not null)
            {
                await CloseAsync(window).ConfigureAwait(true);
            }
        }

        // ---------- 判据 5：位置失效降级并提示 ----------
        try
        {
            // 直接写一条"路径根本不存在"的笔记：正文被改过 / 重排后 DOM 变了就是这个形态
            await annotations.AddNoteAsync(new Note
            {
                BookId = bookId,
                SpineIndex = 0,
                Anchor = new NoteAnchor
                {
                    StartPath = "body/99/p/9",
                    StartOffset = 0,
                    EndPath = "body/99/p/9",
                    EndOffset = 3,
                    PercentInChapter = 0.5,
                },
                Type = NoteType.Highlight,
                Quote = "这段引文在原章节里已经找不到了",
            }).ConfigureAwait(true);

            (window, core) = await OpenAsync(ctx, bookId).ConfigureAwait(true);

            if (core is null)
            {
                Add("J5-degraded-position", false, "重开后 WebView2 没有就绪");
                return Finish(paths, startedAt, stopwatch, checks);
            }

            await WaitAsync(
                async () =>
                {
                    var state = await EvalAsync(core, "window.__readerDebug.annotationState()")
                        .ConfigureAwait(true);

                    return state is JsonObject o && (o["noteCount"]?.GetValue<int>() ?? 0) >= 2;
                },
                15000).ConfigureAwait(true);

            var allNotes = await EvalAsync(core, "window.__readerDebug.annotationState()").ConfigureAwait(true);
            var totalNotes = allNotes?["noteCount"]?.GetValue<int>() ?? 0;

            // 坏笔记是最后加进库的（created_at 正序排最后）
            await EvalAsync(core, "window.__readerDebug.jumpNote(" + (totalNotes - 1) + ")")
                .ConfigureAwait(true);

            var notice = await EvalStrAsync(core, "window.__readerDebug.lastNotice()").ConfigureAwait(true);

            var degradedOk = notice.Contains("失效", StringComparison.Ordinal)
                || notice.Contains("章内比例", StringComparison.Ordinal);

            Add("J5-degraded-position", degradedOk,
                $"坏锚点笔记跳转后的提示=「{Trim(notice, 60)}」；" +
                (degradedOk
                    ? "已按降级路径给出提示（判据 5 要求'失效时降级并提示'）。"
                    : "没有出现降级提示——需要检查 restoreNoteMark / jumpToAnnotation 的降级分支。"));

            await CloseAsync(window).ConfigureAwait(true);
            window = null;
        }
        catch (Exception ex)
        {
            Add("J5-exception", false, ex.GetType().Name + ": " + ex.Message);

            if (window is not null)
            {
                await CloseAsync(window).ConfigureAwait(true);
            }
        }

        // ---------- 判据 7：100 条笔记的列表渲染 ----------
        try
        {
            for (var i = 0; i < ManyNotes; i++)
            {
                await annotations.AddNoteAsync(new Note
                {
                    BookId = bookId,
                    SpineIndex = 0,
                    Anchor = new NoteAnchor
                    {
                        StartPath = "body/0/p/0",
                        StartOffset = i % 5,
                        EndPath = "body/0/p/0",
                        EndOffset = (i % 5) + 2,
                    },
                    Type = NoteType.Highlight,
                    Quote = "批量笔记第 " + (i + 1) + " 条",
                }).ConfigureAwait(true);
            }

            var notesBefore = (await annotations.ListNotesAsync(bookId).ConfigureAwait(true)).Count;

            (window, core) = await OpenAsync(ctx, bookId).ConfigureAwait(true);

            if (core is null)
            {
                Add("J7-list-render", false, "重开后 WebView2 没有就绪");
                return Finish(paths, startedAt, stopwatch, checks);
            }

            var rendered = await WaitAsync(
                async () =>
                {
                    var state = await EvalAsync(core, "window.__readerDebug.annotationState()")
                        .ConfigureAwait(true);

                    return state is JsonObject o && (o["noteCount"]?.GetValue<int>() ?? 0) >= ManyNotes + 2;
                },
                20000).ConfigureAwait(true);

            // 打开面板并测量"从调用到 DOM 里已有 100+ 行"的耗时
            var listResult = await EvalAsync(core,
                "(function(){ var d=window.__readerDebug; var t=performance.now(); " +
                "d.openAnnotations('notes'); var ms=Math.round(performance.now()-t); " +
                "var st=d.annotationState(); return { rows: st.rows, ms: ms, open: st.panelOpen, mode: st.panelMode }; })()")
                .ConfigureAwait(true);

            var rows = listResult?["rows"]?.GetValue<int>() ?? 0;
            var ms = listResult?["ms"]?.GetValue<int>() ?? -1;
            var open = listResult?["open"]?.GetValue<bool>() ?? false;
            var listMode = listResult?["mode"]?.GetValue<string>() ?? string.Empty;

            Add("J7-list-render",
                rendered && open && listMode == "notes" && rows >= ManyNotes && ms >= 0
                && ms <= ListRenderBudgetMs,
                $"库里笔记={notesBefore} 条；面板行数={rows}；面板模式={listMode}；" +
                $"打开并渲染耗时={ms} ms（门槛 {ListRenderBudgetMs} ms）；面板可见={open}");

            /* ---------- 判据 6：导出 Markdown（在同一个窗口里走 UI 入口） ---------- */
            var exportDir = paths.ExportsDir;

            if (Directory.Exists(exportDir))
            {
                foreach (var old in Directory.GetFiles(exportDir, "*.md"))
                {
                    try { File.Delete(old); } catch (IOException) { }
                }
            }

            await EvalAsync(core, "window.__readerDebug.exportAnnotationsNow()").ConfigureAwait(true);

            var exported = await WaitAsync(
                () => Task.FromResult(Directory.Exists(exportDir) && Directory.GetFiles(exportDir, "*.md").Length > 0),
                15000).ConfigureAwait(true);

            string exportText = string.Empty;

            if (exported)
            {
                exportText = await File.ReadAllTextAsync(Directory.GetFiles(exportDir, "*.md")[0])
                    .ConfigureAwait(true);
            }

            Add("J6-export-markdown",
                exported
                && exportText.Contains(BookTitle, StringComparison.Ordinal)
                && exportText.Contains("我的第一条批注", StringComparison.Ordinal)
                && exportText.Contains("# 书签与笔记", StringComparison.Ordinal),
                exported
                    ? $"导出文件存在；含书名={exportText.Contains(BookTitle, StringComparison.Ordinal)}；" +
                      $"含批注={exportText.Contains("我的第一条批注", StringComparison.Ordinal)}；" +
                      $"长度={exportText.Length}；路径=「{Trim(Directory.GetFiles(exportDir, "*.md")[0], 80)}」"
                    : $"导出超时：目录={exportDir}，文件数={(Directory.Exists(exportDir) ? Directory.GetFiles(exportDir, "*.md").Length : 0)}");

            await CloseAsync(window).ConfigureAwait(true);
            window = null;
        }
        catch (Exception ex)
        {
            Add("J6-J7-exception", false, ex.GetType().Name + ": " + ex.Message);

            if (window is not null)
            {
                await CloseAsync(window).ConfigureAwait(true);
            }
        }

        return Finish(paths, startedAt, stopwatch, checks);
    }

    // ==================== 基础设施 ====================

    /// <summary>造一本 20 段文字的最小 EPUB：够长才会出现真正的滚动位置（判据 1 要滚动后再加书签）。</summary>
    private static string WriteEpub(string dir)
    {
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "t65-annotations.epub");

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
            + "<dc:title>" + BookTitle + "</dc:title><dc:language>zh-CN</dc:language>"
            + "<meta property=\"dcterms:modified\">2026-01-01T00:00:00Z</meta>"
            + "</metadata>"
            + "<manifest><item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>"
            + "<item id=\"ch1\" href=\"ch1.xhtml\" media-type=\"application/xhtml+xml\"/></manifest>"
            + "<spine><itemref idref=\"ch1\"/></spine></package>");

        WriteText("OEBPS/nav.xhtml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\">"
            + "<head><title>目录</title></head><body><nav epub:type=\"toc\"><ol>"
            + "<li><a href=\"ch1.xhtml\">第 1 章</a></li></ol></nav></body></html>");

        var body = new StringBuilder();
        body.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>")
            .Append("<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>第 1 章</title></head><body>")
            .Append("<h1>第 1 章 · 书签与笔记</h1>");

        for (var i = 1; i <= 20; i++)
        {
            body.Append("<p>第 ").Append(i).Append(" 段：")
                .Append(i == 1 ? "跨段落选区会横跨两个段落，锚点必须记住起点与终点。" : "这是用来撑出滚动高度的正文内容，每段都有一定长度。")
                .Append("</p>");
        }

        body.Append("</body></html>");

        WriteText("OEBPS/ch1.xhtml", body.ToString());

        return path;
    }

    private static async Task<Book?> FindBookByTitleAsync(IBookRepository repo, string title)
    {
        var books = await repo.QueryAsync(new BookQuery { SearchText = title, Limit = 10 }).ConfigureAwait(true);

        return books.FirstOrDefault(b => string.Equals(b.Title, title, StringComparison.Ordinal));
    }

    private static async Task<(ReaderWindow Window, CoreWebView2? Core)> OpenAsync(Ctx c, Guid bookId)
    {
        var window = c.NewWindow(bookId);

        window.Show();

        var core = await WaitForCoreAsync(window, 30000).ConfigureAwait(true);

        if (core is null)
        {
            return (window, null);
        }

        // 等 reader.js 起来且第一章真的渲染出正文（长度 > 0）
        await WaitAsync(
            async () =>
            {
                var ready = await EvalIntAsync(core, "window.__readerDebug ? 1 : 0").ConfigureAwait(true);

                if (ready != 1)
                {
                    return false;
                }

                var length = await EvalIntAsync(core,
                    "(function(){ var f=document.getElementById('chapter'); " +
                    "var d=f && f.contentDocument; return d && d.body ? d.body.innerText.length : 0; })()")
                    .ConfigureAwait(true);

                return length > 0;
            },
            30000).ConfigureAwait(true);

        return (window, core);
    }

    private static async Task CloseAsync(ReaderWindow window)
    {
        try
        {
            window.Close();
        }
        catch (Exception)
        {
            // 窗口已经没了
        }

        await WaitAsync(() => Task.FromResult(!window.IsVisible), 15000).ConfigureAwait(true);

        /* 关窗后 WebView2 控制器释放有延迟（T6-3 实测：立刻重开要约 17 秒）。
           冒烟不是判据，等一小会儿让下一个窗口别撞在一起。 */
        await Task.Delay(600).ConfigureAwait(true);
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

    private static async Task<JsonNode?> EvalAsync(CoreWebView2 core, string script)
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

    private static async Task<int> EvalIntAsync(CoreWebView2 core, string script)
    {
        var raw = await core.ExecuteScriptAsync(script).ConfigureAwait(true);

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;
    }

    private static async Task<string> EvalStrAsync(CoreWebView2 core, string script)
    {
        var raw = await core.ExecuteScriptAsync(script).ConfigureAwait(true);

        try
        {
            return JsonNode.Parse(raw)?.GetValue<string>() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
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

    private static string Trim(string text, int max)
        => string.IsNullOrEmpty(text)
            ? string.Empty
            : (text.Length <= max ? text : text[..max] + "…");

    /// <summary>按 style + color 找笔记 id（annotationState 里的三个平行数组）。</summary>
    private static string? FindNoteId(JsonNode? state, int style, int color)
    {
        var ids = state?["noteIds"]?.AsArray();
        var styles = state?["noteStyles"]?.AsArray();
        var colors = state?["noteColors"]?.AsArray();

        if (ids is null || styles is null || colors is null)
        {
            return null;
        }

        for (var i = 0; i < ids.Count; i++)
        {
            if ((styles[i]?.GetValue<int>() ?? -1) == style
                && (colors[i]?.GetValue<int>() ?? -1) == color)
            {
                return ids[i]?.GetValue<string>();
            }
        }

        return null;
    }

    /// <summary>
    /// 按 note id 读那个 mark 的底色、字色与正文色（避开"批注也是 mark.ets-note"的歧义）。
    /// 字色与正文色用来验"荧光下的文字跟随主题，不写死深色"。
    /// </summary>
    private static string MarkProbeJs(string noteId)
        => "(function(){ var d=document.getElementById('chapter').contentDocument; "
           + "var m=d.querySelector('mark[data-note-id=\"" + noteId + "\"]'); if(!m) return null; "
           + "var cs=getComputedStyle(m); var bs=getComputedStyle(d.body); "
           + "return { bg: cs.backgroundColor, markColor: cs.color, bodyColor: bs.color }; })()";

    private static string ClickMarkJs(string noteId)
        => "(function(){ var d=document.getElementById('chapter').contentDocument; "
           + "var m=d.querySelector('mark[data-note-id=\"" + noteId + "\"]'); if(!m) return 0; "
           + "var s=d.defaultView.getSelection(); if(s) s.removeAllRanges(); m.click(); return 1; })()";

    /// <summary>"rgb(r, g, b)" / "rgba(r, g, b, a)" → 四元组（alpha 缺省按 1）。解析不了返回 null。</summary>
    private static (int R, int G, int B, double A)? RgbaOf(string css)
    {
        var matched = System.Text.RegularExpressions.Regex.Match(
            css ?? string.Empty, @"rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\)");

        if (!matched.Success)
        {
            return null;
        }

        var alpha = matched.Groups[4].Success
            ? double.Parse(matched.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 1.0;

        return (
            int.Parse(matched.Groups[1].Value),
            int.Parse(matched.Groups[2].Value),
            int.Parse(matched.Groups[3].Value),
            alpha);
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
            Path.Combine(paths.TempDir, "t65-smoke.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return report;
    }

    /// <summary>一次验收的公共上下文。</summary>
    private sealed class Ctx
    {
        public Guid BookId { get; set; }

        public required IReaderService Reader { get; init; }

        public required IBookRepository Repo { get; init; }

        public required IWebViewEnvironmentProvider Environments { get; init; }

        public required IReadingProgressService Progress { get; init; }

        public required IAnnotationService Annotations { get; init; }

        public required ILoggerFactory Loggers { get; init; }

        public required AppPaths Paths { get; init; }

        public ReaderWindow NewWindow(Guid bookId) => new(
            Reader,
            Environments,
            Progress,
            Loggers.CreateLogger<ReaderWindow>(),
            bookId,
            repository: Repo,
            annotations: Annotations);
    }
}