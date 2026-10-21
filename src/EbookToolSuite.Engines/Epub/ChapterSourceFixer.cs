using System.Text;
using System.Text.RegularExpressions;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Engines.Epub;

/// <summary>
/// 章节源码自动修正（T5-2W）。只做**白名单**里的确定性修正：
/// 空标签自闭合、裸露的 &amp;、未声明的命名实体、属性补引号、正文里的裸 &lt;、
/// 以及标签配对问题（补缺失的结束标签 / 删多余的结束标签）。
/// </summary>
/// <remarks>
/// 【边界】有歧义的一律不动：标签该在哪一层闭合、div 结构是否合理、段落合并拆分、
/// 样式与语义、大小写风格。修正完必须重新跑一次 <see cref="ChapterSourceEditor.Check"/>
/// 才算数（校验不过就整体回滚，由调用方负责）。
/// </remarks>
public sealed class ChapterSourceFixer
{
    private const int MaxRounds = 5;

    private static readonly Dictionary<string, int> NamedEntities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nbsp"] = 160, ["iexcl"] = 161, ["cent"] = 162, ["pound"] = 163, ["yen"] = 165,
        ["sect"] = 167, ["copy"] = 169, ["reg"] = 174, ["deg"] = 176, ["plusmn"] = 177,
        ["middot"] = 183, ["frac12"] = 189, ["iquest"] = 191, ["times"] = 215, ["divide"] = 247,
        ["ndash"] = 8211, ["mdash"] = 8212, ["lsquo"] = 8216, ["rsquo"] = 8217, ["ldquo"] = 8220,
        ["rdquo"] = 8221, ["bull"] = 8226, ["hellip"] = 8230, ["trade"] = 8482,
        ["larr"] = 8592, ["uarr"] = 8593, ["rarr"] = 8594, ["darr"] = 8595, ["euro"] = 8364,
    };

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "br", "img", "hr", "meta", "link", "input", "area", "base", "col", "embed", "source", "track", "wbr",
    };

    private static readonly Regex VoidTagPattern = new(
        @"<(?<tag>br|img|hr|meta|link|input|area|base|col|embed|source|track|wbr)(?<attrs>(?:\s[^<>]*?)?)(?<slash>\s*/)?\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareAmpPattern = new(
        @"&(?!#?[A-Za-z0-9]+;)", RegexOptions.Compiled);

    private static readonly Regex NamedEntityPattern = new(
        @"&(?<name>[A-Za-z][A-Za-z0-9]*);", RegexOptions.Compiled);

    private static readonly Regex BareLessThanPattern = new(
        @"<(?![A-Za-z!/?])", RegexOptions.Compiled);

    private static readonly Regex TagPattern = new(
        @"<(?<close>/)?(?<name>[A-Za-z][-A-Za-z0-9_:.]*)(?<rest>[^<>]*?)(?<self>/)?>",
        RegexOptions.Compiled);

    /// <summary>只看不改：列出会做哪些修正（确认弹窗用）。</summary>
    public IReadOnlyList<SourceFix> Scan(string source) => Fix(source).Fixes;

    /// <summary>扫描并应用，最多迭代 <see cref="MaxRounds"/> 轮（一轮修好了再扫一轮）。</summary>
    public (string Fixed, IReadOnlyList<SourceFix> Fixes) Fix(string source)
    {
        var text = source ?? string.Empty;
        var fixes = new List<SourceFix>();

        for (var round = 0; round < MaxRounds; round++)
        {
            var edits = ScanOnce(text);

            if (edits.Count == 0)
            {
                break;
            }

            foreach (var edit in edits)
            {
                fixes.Add(edit.Fix);
            }

            text = Apply(text, edits);
        }

        return (text, fixes);
    }

    private List<Edit> ScanOnce(string text)
    {
        var edits = new List<Edit>();

        foreach (Match match in VoidTagPattern.Matches(text))
        {
            var slash = match.Groups["slash"];

            if (slash.Success)
            {
                continue;
            }

            var tag = match.Groups["tag"].Value;
            var replacement = "<" + tag + match.Groups["attrs"].Value + "/>";
            edits.Add(Edit.At(text, match.Index, match.Length, replacement,
                "void-element",
                "空标签 <" + tag + "> 要写成自闭合 " + replacement));
        }

        foreach (Match match in BareAmpPattern.Matches(text))
        {
            edits.Add(Edit.At(text, match.Index, match.Length, "&amp;",
                "bare-amp",
                "正文里有裸露的 &，改成 &amp;"));
        }

        foreach (Match match in NamedEntityPattern.Matches(text))
        {
            var name = match.Groups["name"].Value;

            if (!NamedEntities.TryGetValue(name, out var code))
            {
                continue;
            }

            var replacement = "&#" + code.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";";
            edits.Add(Edit.At(text, match.Index, match.Length, replacement,
                "named-entity",
                "XHTML 里没有声明 &" + name + ";，改成数字实体 " + replacement));
        }

        foreach (Match match in TagPattern.Matches(text))
        {
            edits.AddRange(QuoteAttributes(text, match));
        }

        foreach (Match match in BareLessThanPattern.Matches(text))
        {
            edits.Add(Edit.At(text, match.Index, match.Length, "&lt;",
                "bare-lt",
                "正文里有裸露的 <，改成 &lt;"));
        }

        edits.AddRange(PairTags(text));

        return edits;
    }

    /// <summary>
    /// 给没加引号的属性值补双引号。**必须引号感知**：直接在整篇文本上跑正则会跑进已有引号内部
    /// （踩过：content="text/html; charset=utf-8" 被改成 charset="utf-8""，反而把文件弄坏）。
    /// </summary>
    private static List<Edit> QuoteAttributes(string text, Match tag)
    {
        var edits = new List<Edit>();
        var inner = tag.Value;
        var baseOffset = tag.Index;
        var limit = inner.Length - 1;
        var i = 1;

        while (i < limit)
        {
            var ch = inner[i];

            if (ch is '"' or '\'')
            {
                var quote = ch;
                i++;

                while (i < limit && inner[i] != quote)
                {
                    i++;
                }

                i++;
                continue;
            }

            if (!char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            var nameStart = i + 1;
            var j = nameStart;

            while (j < limit && (char.IsLetterOrDigit(inner[j]) || inner[j] is '_' or ':' or '-' or '.'))
            {
                j++;
            }

            if (j > nameStart && j < limit && inner[j] == '=')
            {
                var valueStart = j + 1;

                if (valueStart < limit && inner[valueStart] is not ('"' or '\''))
                {
                    var valueEnd = valueStart;

                    while (valueEnd < limit && !char.IsWhiteSpace(inner[valueEnd]) && inner[valueEnd] != '<')
                    {
                        valueEnd++;
                    }

                    if (valueEnd > valueStart)
                    {
                        var value = inner[valueStart..valueEnd];
                        edits.Add(Edit.At(
                            text,
                            baseOffset + valueStart,
                            value.Length,
                            "\"" + value + "\"",
                            "unquoted-attr",
                            "属性 " + inner[nameStart..j] + " 的值 " + value + " 没加引号，补上双引号"));
                    }

                    i = valueEnd;
                    continue;
                }
            }

            i = j > i ? j : i + 1;
        }

        return edits;
    }

    /// <summary>标签配对：补上缺失的结束标签、删掉多余的结束标签。</summary>
    private static List<Edit> PairTags(string text)
    {
        var edits = new List<Edit>();
        var stack = new List<(string Name, int Line)>();

        foreach (Match match in TagPattern.Matches(text))
        {
            var name = match.Groups["name"].Value;

            if (match.Groups["self"].Success || VoidTags.Contains(name))
            {
                continue;
            }

            var line = LineAt(text, match.Index);

            if (!match.Groups["close"].Success)
            {
                stack.Add((name, line));
                continue;
            }

            var at = stack.FindLastIndex(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

            if (at < 0)
            {
                edits.Add(Edit.At(text, match.Index, match.Length, string.Empty,
                    "stray-close",
                    "这个 </" + name + "> 没有对应的开始标签，删掉它"));
                continue;
            }

            for (var i = stack.Count - 1; i > at; i--)
            {
                var missing = "</" + stack[i].Name + ">";
                edits.Add(Edit.At(text, match.Index, 0, missing,
                    "unclosed",
                    "第 " + stack[i].Line + " 行的 <" + stack[i].Name + "> 没有闭合，在这里补上 " + missing));
            }

            stack.RemoveRange(at, stack.Count - at);
        }

        return edits;
    }

    private static string Apply(string text, List<Edit> edits)
    {
        var builder = new StringBuilder(text);
        var ordered = edits
            .OrderByDescending(edit => edit.Offset)
            .ThenByDescending(edit => edit.Length)
            .ToList();

        foreach (var edit in ordered)
        {
            builder.Remove(edit.Offset, edit.Length);
            builder.Insert(edit.Offset, edit.Replacement);
        }

        return builder.ToString();
    }

    private static int LineAt(string text, int offset)
    {
        var line = 1;

        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private sealed record Edit(int Offset, int Length, string Replacement, SourceFix Fix)
    {
        public static Edit At(string text, int offset, int length, string replacement, string kind, string reason)
        {
            var before = text.Substring(offset, length);
            var line = LineAt(text, offset);

            return new Edit(offset, length, replacement, new SourceFix(kind, line, before, replacement, reason));
        }
    }
}
