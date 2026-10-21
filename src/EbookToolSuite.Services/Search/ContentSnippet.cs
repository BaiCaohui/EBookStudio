using System.Text;

namespace EbookToolSuite.Services.Search;

/// <summary>
/// 从一段正文里切出「关键词前后文」（T6-4 搜索结果片段）。
/// 切点在服务侧做而不是在 SQL 里用 snippet()：trigram 的 snippet 会把 3 字符窗口
/// 当 token 处理，边界常常落在半个词上；自己找子串位置反而稳定，也好断言。
/// </summary>
public static class ContentSnippet
{
    /// <summary>命中前的上下文长度（字符）。</summary>
    internal const int BeforeChars = 40;

    /// <summary>命中后的上下文长度（字符）。</summary>
    internal const int AfterChars = 80;

    /// <summary>片段的三段：命中前文、命中词、命中后文。没命中时 Match 为空、Before 是开头。</summary>
    public sealed record Parts(string Before, string Match, string After)
    {
        public bool Found => Match.Length > 0;
    }

    public static Parts Build(string? body, IReadOnlyList<string> terms)
    {
        var text = Collapse(body ?? string.Empty);

        if (text.Length == 0)
        {
            return new Parts(string.Empty, string.Empty, string.Empty);
        }

        var hit = -1;
        var length = 0;

        foreach (var term in terms)
        {
            if (term.Length == 0)
            {
                continue;
            }

            var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);

            if (index >= 0 && (hit < 0 || index < hit))
            {
                hit = index;
                length = term.Length;
            }
        }

        if (hit < 0)
        {
            return new Parts(Clip(text, 0, BeforeChars + AfterChars), string.Empty, string.Empty);
        }

        var start = Math.Max(0, hit - BeforeChars);
        var end = Math.Min(text.Length, hit + length + AfterChars);

        return new Parts(
            (start > 0 ? "…" : string.Empty) + text[start..hit],
            text.Substring(hit, length),
            text[(hit + length)..end] + (end < text.Length ? "…" : string.Empty));
    }

    /// <summary>片段里不该出现换行（XAML 里会挤成一行，看不出断句）。</summary>
    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string Clip(string text, int start, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
