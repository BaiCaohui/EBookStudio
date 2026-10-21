using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Editing;

/// <summary>
/// 跨章节查找替换（执行计划 T5-3）。只改 XML 文本节点里的字，标签名/属性名/注释/PI 一个字节都不碰。
/// </summary>
/// <remarks>
/// <para><b>为什么不用 XDocument 序列化</b>：<c>XDocument</c> 重新序列化会重排引号/自闭合/实体，
/// 破坏"标签与属性逐字节不变"（验收标准 1）；而且它会把替换串里的 <c>&amp;</c> 转义成 <c>&amp;amp;</c>，
/// 于是"替换引入非法字符"永远触发不了（验收标准 4）。</para>
/// <para>所以这里只做两件事：用 XML 词法扫描出"文本区"（标签/注释/PI/DOCTYPE 之外），
/// 绝不把查找串当成源码字符串去正则；在文本区上做实体解码后的匹配，再把替换串**原样**拼回原位。
/// 替换后是否良构由 <see cref="IChapterSourceService.Check"/> 逐章兜底 全通过才写盘，失败一个字都不写。</para>
/// <para>2026-10-12 口径：只写工作区（<see cref="IChapterSourceService.Save"/> 会记 changedFiles）；
/// 预览与执行是同一份计划（预览报几处就替换几处）；非法正则不执行不崩；
/// 单章 2 秒 / 整轮 20 秒超时、整轮 5 万处上限，触发即中止。快照与"撤销上一次替换"归 T5-4。</para>
/// </remarks>
public sealed class ChapterFindReplaceService : IChapterFindReplaceService
{
    /// <summary>
    /// 不参与替换的文本节点所在元素：书的 CSS / 脚本，以及 &lt;title&gt;
    /// （2026-10-12 口径：替换只改正文，不改章节标题）。
    /// </summary>
    private static readonly HashSet<string> SkippedElements =
        new(StringComparer.OrdinalIgnoreCase) { "style", "script", "title" };

    private const int PreviewContext = 20;

    private readonly IChapterSourceService _source;
    private readonly ILogger<ChapterFindReplaceService>? _log;

    /// <summary>文件级快照（T5-4：「全部替换前」自动建，写盘失败按它逐章回滚）。为 null 时按 T5-3 旧口径。</summary>
    private readonly IWorkspaceSnapshotService? _snapshots;

    public ChapterFindReplaceService(
        IChapterSourceService source,
        ILogger<ChapterFindReplaceService>? log = null,
        IWorkspaceSnapshotService? snapshots = null)
    {
        _source = source;
        _log = log;
        _snapshots = snapshots;
    }

    public FindReplacePlan Preview(
        IEpubWorkspace workspace,
        IReadOnlyList<FindReplaceChapter> chapters,
        FindReplaceOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrEmpty(options.Find))
        {
            return Bad(options, "查找内容不能为空（空串会命中所有位置）。");
        }

        Regex? regex = null;

        if (options.UseRegex)
        {
            try
            {
                regex = new Regex(
                    options.Find,
                    RegexOptions.Multiline | (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase),
                    options.PerChapterTimeout);
            }
            catch (ArgumentException)
            {
                // .NET 的 Regex 异常消息是英文（如 "Invalid pattern ..."），不能直接给用户看。
                return Bad(
                    options,
                    "正则表达式不合法：请检查括号、方括号、转义符和量词是否配对。"
                    + "不用正则时请取消「使用正则」再试。");
            }
        }

        var watch = Stopwatch.StartNew();
        var changes = new List<FindReplaceChapterChange>();
        var preview = new List<FindReplacePreviewItem>();
        var total = 0;

        foreach (var chapter in chapters)
        {
            ct.ThrowIfCancellationRequested();

            if (watch.Elapsed > options.TotalTimeout)
            {
                return Bad(options, $"查找超时（整轮上限 {options.TotalTimeout.TotalSeconds:0} 秒），已中止，没有写盘。");
            }

            string source;

            try
            {
                source = _source.ReadSource(workspace, chapter.Entry);
            }
            catch (Exception ex)
            {
                return Bad(options, $"第 {chapter.Index + 1} 章读不出来，已中止：{ex.SafeMessage()}");
            }

            var result = ReplaceInChapter(
                source, options, regex, chapter, preview, options.MaxPreview, total, options.MaxMatches, watch);

            if (result.Error is not null)
            {
                return Bad(options, result.Error);
            }

            total += result.Count;

            if (total > options.MaxMatches)
            {
                return Bad(options, $"命中超过上限 {options.MaxMatches} 处，已中止，没有写盘。");
            }

            if (result.NewSource is not null)
            {
                changes.Add(new FindReplaceChapterChange(
                    chapter.Index, chapter.Entry, chapter.Title, result.Count, result.NewSource));
            }
        }

        _log?.LogInformation("查找替换预览：命中 {Total} 处、涉及 {Chapters} 章", total, changes.Count);
        return new FindReplacePlan(options, changes, preview, total, null);
    }

    public FindReplaceOutcome Apply(
        IEpubWorkspace workspace, FindReplacePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.Ok)
        {
            return new FindReplaceOutcome(false, 0, 0, plan.Error ?? "计划无效。");
        }

        if (plan.Changes.Count == 0)
        {
            return new FindReplaceOutcome(true, 0, 0, "没有命中，什么都没改。");
        }

        /* 第一道：逐章校验**全部**通过才写  这就是"失败一个字节都不写"的关键。
           例如替换串里写了裸的 & / < ，解析不过，就在这里整体放弃。 */
        foreach (var change in plan.Changes)
        {
            ct.ThrowIfCancellationRequested();
            var check = _source.Check(change.NewSource!);

            if (!check.Ok)
            {
                return new FindReplaceOutcome(
                    false,
                    0,
                    0,
                    $"第 {change.ChapterIndex + 1} 章替换后校验不过，已全部放弃（一个字都没写）：{check.Summary}");
            }
        }

        /* 第二道：先给"本次将被改动的这些章节"建替换前快照（T5-4 判据 7）。
           建不成就中止：宁可这次替换不做，也不要给用户一个"改坏了退不回去"的结果。 */
        WorkspaceSnapshot? snapshot = null;
        if (_snapshots is not null)
        {
            try
            {
                snapshot = _snapshots
                    .TakeIncrementalAsync(
                        workspace,
                        plan.Changes.Select(change => change.Entry).ToList(),
                        WorkspaceSnapshotKinds.ReplaceBefore,
                        $"全部替换前：{plan.TotalMatches} 处 / {plan.Changes.Count} 章",
                        ct)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "替换前建快照失败");
                return new FindReplaceOutcome(
                    false, 0, 0,
                    "替换前的快照没建成，已中止（工作区一个字节都没改）：" + ex.SafeMessage());
            }
        }

        /* 第三道：写工作区 + 记 changedFiles（落书仍走主窗口【保存】）。 */
        var written = 0;

        foreach (var change in plan.Changes)
        {
            ct.ThrowIfCancellationRequested();
            var saved = _source.Save(workspace, change.Entry, change.NewSource!);

            if (!saved.Ok)
            {
                /* 写盘半路失败：按刚建的替换前快照把涉及章节回滚（T5-4 判据 7）。
                   没注入快照服务时保持 T5-3 的旧口径：如实报告前几章已写。 */
                if (snapshot is not null && _snapshots is not null)
                {
                    string rollbackNote;
                    try
                    {
                        var rollback = _snapshots.RestoreAsync(workspace, snapshot.Id, ct).GetAwaiter().GetResult();
                        rollbackNote = rollback.Ok
                            ? "已按替换前快照回滚（这次替换一个字都没留下）。"
                            : "回滚没成功，请到「历史」里还原快照 " + snapshot.Id + "：" + rollback.Message;
                    }
                    catch (Exception ex)
                    {
                        rollbackNote = "回滚没成功，请到「历史」里还原快照 " + snapshot.Id + "：" + ex.SafeMessage();
                    }

                    return new FindReplaceOutcome(
                        false, 0, 0,
                        $"第 {change.ChapterIndex + 1} 章写盘被拦下：{saved.Summary}；{rollbackNote}");
                }

                return new FindReplaceOutcome(
                    false, written, written, $"第 {change.ChapterIndex + 1} 章写盘被拦下（前 {written} 章已写）：{saved.Summary}");
            }

            written++;
        }

        _log?.LogInformation("查找替换已写工作区：{Total} 处 / {Chapters} 章", plan.TotalMatches, written);
        return new FindReplaceOutcome(
            true,
            plan.TotalMatches,
            written,
            $"已替换 {plan.TotalMatches} 处、涉及 {written} 章（只写了工作区；要落进书里请回主窗口点【保存】）。")
        {
            SnapshotId = snapshot?.Id,
        };
    }

    private static FindReplacePlan Bad(FindReplaceOptions options, string error)
        => new(options, Array.Empty<FindReplaceChapterChange>(), Array.Empty<FindReplacePreviewItem>(), 0, error);

    /// <summary>
    /// 一章：扫描文本区 -> 在（实体解码后的）文本上找命中 -> 把替换串原样拼回原位。
    /// 返回 <c>NewSource == null</c> 表示这一章没有命中、不需要写。
    /// </summary>
    private static (int Count, string? NewSource, string? Error) ReplaceInChapter(
        string source,
        FindReplaceOptions options,
        Regex? regex,
        FindReplaceChapter chapter,
        List<FindReplacePreviewItem> preview,
        int maxPreview,
        int alreadyFound,
        int maxTotalMatches,
        Stopwatch totalWatch)
    {
        var regions = ScanTextRegions(source);
        var builder = new StringBuilder(source.Length + 64);
        var cursor = 0;
        var count = 0;
        var chapterOrdinal = 0;
        var changed = false;
        var chapterWatch = Stopwatch.StartNew();

        foreach (var region in regions)
        {
            if (totalWatch.Elapsed > options.TotalTimeout)
            {
                return (0, null, $"查找超时（整轮上限 {options.TotalTimeout.TotalSeconds:0} 秒），已中止，没有写盘。");
            }

            if (chapterWatch.Elapsed > options.PerChapterTimeout)
            {
                return (0, null, $"第 {chapter.Index + 1} 章匹配超时（单章上限 {options.PerChapterTimeout.TotalSeconds:0} 秒），已中止，没有写盘。");
            }

            if (region.Skip || region.End <= region.Start)
            {
                continue;
            }

            DecodeEntities(source, region.Start, region.End, out var text, out var starts, out var ends);

            if (text.Length == 0)
            {
                continue;
            }

            List<(int Index, int Length, string Replacement)> hits;

            try
            {
                hits = FindHits(text, options, regex);
            }
            catch (RegexMatchTimeoutException)
            {
                return (0, null, $"第 {chapter.Index + 1} 章正则匹配超时（单章上限 {options.PerChapterTimeout.TotalSeconds:0} 秒），已中止，没有写盘。");
            }

            if (hits.Count == 0)
            {
                continue;
            }

            if (alreadyFound + count + hits.Count > maxTotalMatches)
            {
                return (0, null, $"命中超过上限 {maxTotalMatches} 处，已中止，没有写盘。");
            }

            builder.Append(source, cursor, region.Start - cursor);
            var previousRaw = region.Start;

            foreach (var hit in hits)
            {
                var take = options.OnlyMatchOrdinal is null || options.OnlyMatchOrdinal == chapterOrdinal;
                chapterOrdinal++;

                if (!take)
                {
                    continue;
                }

                var rawStart = hit.Index >= text.Length ? region.End : starts[hit.Index];
                var rawEnd = hit.Length == 0
                    ? rawStart
                    : ends[Math.Min(hit.Index + hit.Length, text.Length) - 1];

                if (rawStart < previousRaw)
                {
                    continue;
                }

                builder.Append(source, previousRaw, rawStart - previousRaw);
                builder.Append(hit.Replacement);          /* 原样插入：非法字符交给逐章校验兜底 */
                previousRaw = rawEnd;
                AddPreview(preview, maxPreview, alreadyFound + count, chapter, text, hit.Index, hit.Length);
                count++;
                changed = true;
            }

            builder.Append(source, previousRaw, region.End - previousRaw);
            cursor = region.End;
        }

        if (!changed)
        {
            return (count, null, null);
        }

        builder.Append(source, cursor, source.Length - cursor);
        return (count, builder.ToString(), null);
    }

    private static List<(int Index, int Length, string Replacement)> FindHits(
        string text, FindReplaceOptions options, Regex? regex)
    {
        var hits = new List<(int, int, string)>();

        if (options.UseRegex)
        {
            foreach (Match match in regex!.Matches(text))
            {
                /* 零宽匹配不替换：否则会在每个字符位置插一串（数量也失真）。 */
                if (match.Length == 0)
                {
                    continue;
                }

                hits.Add((match.Index, match.Length, match.Result(options.Replace)));
            }
        }
        else
        {
            var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var index = 0;

            while (true)
            {
                var hit = text.IndexOf(options.Find, index, comparison);

                if (hit < 0)
                {
                    break;
                }

                hits.Add((hit, options.Find.Length, options.Replace));
                index = hit + options.Find.Length;
            }
        }

        return hits;
    }

    /// <summary>扫描出所有可参与替换的文本区；<see cref="TextRegion.Skip"/> 表示它落在 title/style/script 里。</summary>
    private static List<TextRegion> ScanTextRegions(string source)
    {
        var regions = new List<TextRegion>();
        var stack = new List<string>();
        var n = source.Length;
        var i = 0;

        while (i < n)
        {
            if (source[i] != '<')
            {
                var next = source.IndexOf('<', i);
                var end = next < 0 ? n : next;

                if (end > i && !IsInsideSkipped(stack))
                {
                    regions.Add(new TextRegion(i, end, Skip: false));
                }
                else if (end > i)
                {
                    regions.Add(new TextRegion(i, end, Skip: true));
                }

                i = end;
                continue;
            }

            if (StartsWith(source, i, "<!--"))
            {
                var close = source.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = close < 0 ? n : close + 3;
                continue;
            }

            if (StartsWith(source, i, "<![CDATA["))
            {
                var close = source.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                var end = close < 0 ? n : close;
                var skip = IsInsideSkipped(stack);

                if (end > i + 9)
                {
                    regions.Add(new TextRegion(i + 9, end, skip));
                }

                i = close < 0 ? n : close + 3;
                continue;
            }

            if (StartsWith(source, i, "<?"))
            {
                var close = source.IndexOf("?>", i + 2, StringComparison.Ordinal);
                i = close < 0 ? n : close + 2;
                continue;
            }

            if (StartsWith(source, i, "<!"))
            {
                var end = FindDeclarationEnd(source, i + 2);
                i = end < 0 ? n : end + 1;
                continue;
            }

            /* 元素标签：读到名字与右尖括号（属性里的引号要跳过）。 */
            var p = i + 1;
            var isEnd = p < n && source[p] == '/';

            if (isEnd)
            {
                p++;
            }

            var nameStart = p;

            while (p < n && IsNameChar(source[p]))
            {
                p++;
            }

            var name = LocalName(source[nameStart..p]);
            var gt = FindTagEnd(source, p);

            if (gt < 0)
            {
                i = n;
                continue;
            }

            var selfClosing = gt > i && source[gt - 1] == '/';

            if (isEnd)
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
            }
            else if (!selfClosing && name.Length > 0)
            {
                stack.Add(name);
            }

            i = gt + 1;
        }

        return regions;
    }

    private static bool IsInsideSkipped(List<string> stack)
    {
        foreach (var name in stack)
        {
            if (SkippedElements.Contains(name))
            {
                return true;
            }
        }

        return false;
    }

    private static int FindTagEnd(string source, int from)
    {
        var quote = '\0';

        for (var i = from; i < source.Length; i++)
        {
            var c = source[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>跳过 &lt;!DOCTYPE ...&gt;（可能带内部子集 [...]，里面的 &gt; 不算结束）。</summary>
    private static int FindDeclarationEnd(string source, int from)
    {
        var quote = '\0';
        var depth = 0;

        for (var i = from; i < source.Length; i++)
        {
            var c = source[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                if (depth > 0)
                {
                    depth--;
                }
            }
            else if (c == '>' && depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>把 [start,end) 的原始文本做 XML 实体解码；starts/ends 记录每个解码字符对应的原始区间。</summary>
    private static void DecodeEntities(
        string source, int start, int end, out string text, out int[] starts, out int[] ends)
    {
        var builder = new StringBuilder(end - start);
        var rawStarts = new List<int>(end - start);
        var rawEnds = new List<int>(end - start);
        var i = start;

        while (i < end)
        {
            var c = source[i];

            if (c == '&')
            {
                var semi = source.IndexOf(';', i + 1);

                if (semi > i && semi < end && semi - i <= 34)
                {
                    var decoded = DecodeEntity(source.Substring(i + 1, semi - i - 1));

                    if (decoded is not null)
                    {
                        foreach (var ch in decoded)
                        {
                            builder.Append(ch);
                            rawStarts.Add(i);
                            rawEnds.Add(semi + 1);
                        }

                        i = semi + 1;
                        continue;
                    }
                }
            }

            builder.Append(c);
            rawStarts.Add(i);
            rawEnds.Add(i + 1);
            i++;
        }

        text = builder.ToString();
        starts = rawStarts.ToArray();
        ends = rawEnds.ToArray();
    }

    private static string? DecodeEntity(string name)
    {
        switch (name)
        {
            case "amp": return "&";
            case "lt": return "<";
            case "gt": return ">";
            case "quot": return "\"";
            case "apos": return "'";
        }

        if (name.Length > 1 && name[0] == '#')
        {
            if (!int.TryParse(
                    name.AsSpan(1),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var codePoint))
            {
                return null;
            }

            if (codePoint <= 0 || codePoint > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
            {
                return null;
            }

            return char.ConvertFromUtf32(codePoint);
        }

        /* 未知实体（比如没带 DTD 的 &nbsp;）不当实体：原样按字符处理，避免凭空吞字。 */
        return null;
    }

    private static void AddPreview(
        List<FindReplacePreviewItem> preview,
        int maxPreview,
        int matchIndexInChapter,
        FindReplaceChapter chapter,
        string text,
        int index,
        int length)
    {
        if (preview.Count >= maxPreview)
        {
            return;
        }

        var matchLength = Math.Max(0, Math.Min(length, text.Length - index));
        var beforeStart = Math.Max(0, index - PreviewContext);
        var afterStart = Math.Min(text.Length, index + matchLength);
        var afterLength = Math.Min(PreviewContext, text.Length - afterStart);

        preview.Add(new FindReplacePreviewItem(
            chapter.Index,
            chapter.Entry,
            chapter.Title,
            matchIndexInChapter,
            text.Substring(beforeStart, index - beforeStart),
            text.Substring(index, matchLength),
            text.Substring(afterStart, afterLength)));
    }

    private static bool StartsWith(string source, int index, string value)
        => index + value.Length <= source.Length
            && string.CompareOrdinal(source, index, value, 0, value.Length) == 0;

    private static bool IsNameChar(char c)
        => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':';

    private static string LocalName(string name)
    {
        var colon = name.LastIndexOf(':');
        return colon >= 0 ? name[(colon + 1)..] : name;
    }

    private readonly record struct TextRegion(int Start, int End, bool Skip);
}
