using System.Text;
using System.Text.RegularExpressions;

namespace EbookToolSuite.Engines.Text;

/// <summary>切出来的一章。<see cref="StartChar"/> / <see cref="EndChar"/> 是**字符**下标（不是字节）。</summary>
public sealed record TextChapter(string Title, long StartChar, long EndChar);

/// <summary>整本书的切章方案。</summary>
/// <param name="Chapters">章节列表，按顺序、首尾相接。</param>
/// <param name="UsedFallback">true = 没找到章节标记，按固定字数硬切的（判据 5）。</param>
/// <param name="Pattern">命中的格式名，写日志与详情用。</param>
public sealed record TextChapterPlan(IReadOnlyList<TextChapter> Chapters, bool UsedFallback, string Pattern);

/// <summary>
/// TXT 切章（T4-5，设计文档 6.2 的四组正则）：
/// 第X章/节/回、Chapter X、【第X章】、卷X 第X章、数字编号行；
/// 一组都切不出来就按固定字数硬切，保证一定能读（判据 5）。
/// <para>
/// 2026-10-20 追加（T7-5 回归时用户要求）：无编号的固定标题 <c>番外 / 楔子 / 尾声</c> 也单独成章。
/// </para>
/// </summary>
/// <remarks>
/// 【流式】100 MB 的 TXT 不许 File.ReadAllText（判据 6）：这里按块读字符、
/// 逐行过一遍，只在"候选行"上跑正则，内存占用与文件大小无关。
/// </remarks>
public static class TextChapterSplitter
{
    /// <summary>硬切时每章多少个字。</summary>
    public const int HardSplitChars = 3000;

    /// <summary>一章的标题最多留多少字。</summary>
    public const int MaxTitleChars = 60;

    /// <summary>一行最长多少字才可能是章节标题（比标题上限再宽 40 字，容错）。</summary>
    private const int MaxLineChars = MaxTitleChars + 40;

    private const int BlockChars = 128 * 1024;

    /* 标记后面必须跟"分隔符或行尾"：否则"第一章的正文甲。"这种正文行会被当成章节标题
       （2026-10-09 实测踩到：正文行以"第X章"开头，宽松的 .{0,40} 全吃掉）。
       代价是"第一章起头"（标记后直接接汉字、没空格）这种写法认不出来，属可接受的取舍。 */
    private const string SeparatorAfterMarker = @"(?=[\s:：、.。\-]|$)";

    /* 中文数字：小写与大写都要认（"卷壹"这种写法真实电子书里很常见，2026-10-09 由测试抓出来）。 */
    private const string CnNumbers = @"[0-9０-９一二三四五六七八九十百千万零两壹贰叁肆伍陆柒捌玖拾]";

    private static readonly Regex ChineseChapter = new(
        @"^第\s*" + CnNumbers + @"+\s*[章节回]" + SeparatorAfterMarker + @".{0,40}$",
        RegexOptions.Compiled);

    private static readonly Regex EnglishChapter = new(
        @"^(chapter|part|prologue|epilogue)\b.{0,60}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BracketedChapter = new(
        @"^[【\[]\s*第\s*" + CnNumbers + @"+\s*[章节回]\s*[】\]].{0,40}$",
        RegexOptions.Compiled);

    private static readonly Regex VolumeChapter = new(
        @"^卷\s*" + CnNumbers + @"+" + SeparatorAfterMarker + @".{0,30}$",
        RegexOptions.Compiled);

    /* 【2026-10-20 追加】无编号的固定标题：番外 / 楔子 / 尾声。
       这类标题本身就是独立的一章，却没有任何编号，上面几组正则一个都认不出来
       （T7-5 的 B06 样本实测：10 个标题只切出 7 章，番外/楔子/尾声被并进相邻章）。
       口径与其它组一致：标题后必须是分隔符或行尾，避免把"番外的正文……"这种正文行当成标题。 */
    private static readonly Regex UnnumberedChapter = new(
        @"^(番外|楔子|尾声)" + SeparatorAfterMarker + @".{0,40}$",
        RegexOptions.Compiled);

    private static readonly Regex NumberedLine = new(
        @"^[0-9]{1,4}\s*[\.、,，]\s*\S.{0,40}$",
        RegexOptions.Compiled);

    public static TextChapterPlan Split(string path, Encoding encoding, int bomLength = 0, CancellationToken ct = default)
    {
        var starts = new List<(string Title, long Start)>();
        var pattern = string.Empty;
        long total = 0;

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024))
        {
            if (bomLength > 0)
            {
                stream.Position = bomLength;
            }

            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, 64 * 1024);
            var buffer = new char[BlockChars];
            var carry = string.Empty;
            var carryStart = 0L;      // carry 这一行在源文件里的起始字符下标
            var carryDropped = false; // 这一行太长了（不可能是标题），别再当成标题去匹配
            long consumed = 0;        // 已经读进来的字符数

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var read = reader.ReadBlock(buffer, 0, buffer.Length);

                if (read <= 0)
                {
                    break;
                }

                var lineStart = 0;

                /* 上一块末尾剩下的半行：先补完它。
                   carry 最多几十个字（超长的会被丢掉），所以这里的拼接可以忽略不计。 */
                if (carry.Length > 0 || carryDropped)
                {
                    var newline = Array.IndexOf(buffer, '\n', 0, read);

                    if (newline < 0)
                    {
                        if (!carryDropped)
                        {
                            carry = Append(carry, buffer.AsSpan(0, read));

                            if (carry.Length == 0)
                            {
                                carryDropped = true;
                            }
                        }

                        consumed += read;
                        continue;
                    }

                    if (!carryDropped)
                    {
                        Consider(Merge(carry, buffer.AsSpan(0, newline)), carryStart);
                    }

                    carry = string.Empty;
                    carryDropped = false;
                    lineStart = newline + 1;
                }

                /* 块内的完整行：直接用 Span 扫，一个字符串都不分配。
                   100 MB 的书就靠这一段做到"内存占用与文件大小无关"（判据 6）。 */
                while (true)
                {
                    var newline = Array.IndexOf(buffer, '\n', lineStart, read - lineStart);

                    if (newline < 0)
                    {
                        break;
                    }

                    Consider(buffer.AsSpan(lineStart, newline - lineStart), consumed + lineStart);
                    lineStart = newline + 1;
                }

                // 尾巴留给下一块
                carryStart = consumed + lineStart;
                carry = Take(buffer.AsSpan(lineStart, read - lineStart));

                if (carry.Length == 0 && read > lineStart)
                {
                    // Take 返回空 = 这一行超长，不可能是标题
                    carryDropped = true;
                }

                consumed += read;
            }

            if (!carryDropped && carry.Length > 0)
            {
                Consider(carry.AsSpan(), carryStart);
            }

            total = consumed;

            void Consider(ReadOnlySpan<char> raw, long absoluteStart)
            {
                if (!raw.IsEmpty && raw[^1] == '\r')
                {
                    raw = raw[..^1];
                }

                if (raw.IsEmpty || raw.Length > MaxLineChars)
                {
                    return;
                }

                var line = raw.Trim();

                if (line.IsEmpty)
                {
                    return;
                }

                /* 便宜的预筛：所有模式都是"整行从标记开头"的（^ 锚定），
                   所以只看首字符就够，而且**必须**只看首字符
                   第一版写成"行内出现过 第/章 就放行"，结果每一行正文都要
                   ToString() + 跑 5 条正则：100 MB 的书白分配 342 MB 内存（2026-10-09 实测抓出）。 */
                var first = line[0];

                var plausible = first == '第'
                    || first == '卷'
                    || first == '【'
                    || first == '['
                    || char.IsAsciiDigit(first)
                    || first is 'C' or 'c' or 'P' or 'p' or 'E' or 'e'
                    || first is '番' or '楔' or '尾';

                if (!plausible)
                {
                    return;
                }

                var title = line.ToString();
                string? hit = null;

                if (ChineseChapter.IsMatch(title))
                {
                    hit = "第X章";
                }
                else if (BracketedChapter.IsMatch(title))
                {
                    hit = "【第X章】";
                }
                else if (VolumeChapter.IsMatch(title))
                {
                    hit = "卷X";
                }
                else if (UnnumberedChapter.IsMatch(title))
                {
                    hit = "番外/楔子/尾声";
                }
                else if (EnglishChapter.IsMatch(title))
                {
                    hit = "Chapter X";
                }
                else if (NumberedLine.IsMatch(title))
                {
                    hit = "数字标题行";
                }

                if (hit is null)
                {
                    return;
                }

                starts.Add((title, absoluteStart));

                if (pattern.Length == 0 || !pattern.Contains(hit, StringComparison.Ordinal))
                {
                    pattern = pattern.Length == 0 ? hit : pattern + "+" + hit;
                }
            }
        }

        if (starts.Count == 0)
        {
            return HardSplit(total);
        }

        var chapters = new List<TextChapter>(starts.Count + 1);

        // 第一个标记之前的内容（书名页 / 引子）单独成章
        if (starts[0].Start > 0)
        {
            chapters.Add(new TextChapter("前言", 0, starts[0].Start));
        }

        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Start : total;

            chapters.Add(new TextChapter(starts[i].Title, starts[i].Start, end));
        }

        return new TextChapterPlan(chapters, UsedFallback: false, pattern);
    }

    /* 半行拼接：超过 MaxLineChars 就直接丢掉（那种行长度过滤也会拒），
       免得一个没有换行的巨型文件把内存吃光。 */
    private static string Append(string carry, ReadOnlySpan<char> tail)
        => carry.Length + tail.Length > MaxLineChars + 1 ? string.Empty : carry + tail.ToString();

    private static string Merge(string carry, ReadOnlySpan<char> head)
        => carry.Length + head.Length > MaxLineChars + 1 ? string.Empty : carry + head.ToString();

    private static string Take(ReadOnlySpan<char> tail)
        => tail.Length > MaxLineChars + 1 ? string.Empty : tail.ToString();

    /// <summary>固定字数硬切（判据 5 的兜底）。</summary>
    public static TextChapterPlan HardSplit(long totalChars)
    {
        var chapters = new List<TextChapter>();

        if (totalChars <= 0)
        {
            chapters.Add(new TextChapter("第 1 章", 0, 0));
            return new TextChapterPlan(chapters, UsedFallback: true, "硬切（空文件）");
        }

        var index = 0;
        long start = 0;

        while (start < totalChars)
        {
            var end = Math.Min(totalChars, start + HardSplitChars);

            index++;
            chapters.Add(new TextChapter("第 " + index + " 章", start, end));
            start = end;
        }

        return new TextChapterPlan(chapters, UsedFallback: true, "硬切");
    }
}
