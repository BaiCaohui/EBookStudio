using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Engines.Calibre;

/// <summary>
/// 界面「高级参数」框的解析器。支持 shell 风格写法：
///   --base-font-size 18 --change-justification left --extra-css "D:\我的 样式\a.css"
///   --base-font-size=18
/// 引号包裹的值可以含空格（中文路径是常态）。
/// 解析出来的结果【还必须】逐个过 CalibreArgumentBuilder.Validate。
/// </summary>
public static class CalibreAdvancedArgs
{
    public static IReadOnlyList<ConversionArgument> Parse(string? text)
    {
        var result = new List<ConversionArgument>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var tokens = Tokenize(text);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new AppException(
                    ErrorCode.ConversionArgsRejected,
                    $"高级参数里的 \"{token}\" 不是以 -- 开头的选项名。",
                    technical: $"Unexpected token at index {i}: {token}");
            }

            var equals = token.IndexOf('=');
            if (equals > 0)
            {
                result.Add(new ConversionArgument(
                    token[..equals], token[(equals + 1)..]));
                continue;
            }

            // 下一个 token 不是另一个选项时，它就是本选项的值
            string? value = null;
            if (i + 1 < tokens.Count
                && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = tokens[i + 1];
                i++;
            }

            result.Add(new ConversionArgument(token, value));
        }

        return result;
    }

    /// <summary>按空白分词，双引号或单引号包裹的片段整体算一个 token。</summary>
    internal static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        var inToken = false;

        foreach (var ch in text)
        {
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                inToken = true;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }

                continue;
            }

            current.Append(ch);
            inToken = true;
        }

        if (quote != '\0')
        {
            throw new AppException(
                ErrorCode.ConversionArgsRejected,
                "高级参数里的引号没有配对，请检查。",
                technical: "Unbalanced quote in advanced args");
        }

        if (inToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
