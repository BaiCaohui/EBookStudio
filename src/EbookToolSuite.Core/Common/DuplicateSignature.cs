using System.Text;

namespace EbookToolSuite.Core.Common;

/// <summary>
/// 重复检测的归一化键：书名 + 第一作者 + 格式。
/// 刻意把格式算进键：同一本书的 EPUB 与 MOBI <b>不算</b>重复（执行计划 T2-2 判据 2）。
/// </summary>
public static class DuplicateSignature
{
    private static readonly string[] NoiseSuffixes =
    {
        "全集", "完结", "全本", "精校版", "完美排版", "多看版", "校对版", "典藏版",
        "扫描版", "文字版", "epub", "mobi", "azw3", "txt",
    };

    public static string For(string? title, string? authors, string? format)
        => NormalizeTitle(title) + "|" + NormalizeAuthor(authors) + "|" +
           (format ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>书名归一化：取《》里的正题名、去掉后缀噪声词、去空白与标点、转小写。</summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var s = title.Normalize(NormalizationForm.FormKC);

        // 有《》就只取里面的正题名："《Sample Book B》多看精校版" -> "Sample Book B"
        var open = s.IndexOf('《');
        var close = s.IndexOf('》', open + 1);
        if (open >= 0 && close > open + 1)
        {
            s = s.Substring(open + 1, close - open - 1);
        }

        s = Strip(s);
        foreach (var noise in NoiseSuffixes)
        {
            if (s.Length > noise.Length && s.EndsWith(noise, StringComparison.OrdinalIgnoreCase))
            {
                s = Strip(s.Substring(0, s.Length - noise.Length));
            }
        }

        return s;
    }


    /// <summary>作者归一化：只取第一位作者（多人合著的次要作者写法差异太大）。</summary>
    public static string NormalizeAuthor(string? authors)
    {
        if (string.IsNullOrWhiteSpace(authors))
        {
            return string.Empty;
        }

        var parts = authors.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var first = parts.Length == 0 ? string.Empty : parts[0];
        return Strip(first.Normalize(NormalizationForm.FormKC));
    }

    /// <summary>只留字母、数字与汉字，其余（空格 / 标点 / 括号 / 破折号）一律去掉并转小写。</summary>
    private static string Strip(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }
}

