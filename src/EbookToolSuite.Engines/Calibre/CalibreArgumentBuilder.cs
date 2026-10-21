using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Engines.Calibre;

/// <summary>
/// 把模板参数与单次覆盖合并成最终参数表，并逐个过白名单。
/// 优先级（低到高）：内置模板 &lt; 用户对模板的覆盖 &lt; 单次临时参数。
///
/// 白名单校验的意义：Calibre 收到不认识的选项时会失败或静默忽略，
/// 静默忽略最危险（用户以为加了参数，其实没生效）。宁可执行前报错。
/// </summary>
public static class CalibreArgumentBuilder
{
    private const string OptionPrefix = "--";

    /// <summary>合并 + 校验，返回可直接拼进命令行的参数（不含位置参数 input/output）。</summary>
    public static IReadOnlyList<ConversionArgument> Build(
        CalibreOptions catalog,
        IReadOnlyList<ConversionArgument> template,
        IReadOnlyList<ConversionArgument> overrides)
    {
        var merged = Merge(template, overrides);
        Validate(catalog, merged);
        return merged;
    }

    /// <summary>合并两份参数表。同名键后者覆盖前者，顺序按首次出现。</summary>
    public static IReadOnlyList<ConversionArgument> Merge(
        IReadOnlyList<ConversionArgument> lower,
        IReadOnlyList<ConversionArgument> higher)
    {
        var order = new List<string>();
        var values = new Dictionary<string, ConversionArgument>(StringComparer.OrdinalIgnoreCase);

        void Add(ConversionArgument argument)
        {
            if (string.IsNullOrWhiteSpace(argument.Key))
            {
                return;
            }

            var key = NormalizeKey(argument.Key);
            if (!values.ContainsKey(key))
            {
                order.Add(key);
            }

            values[key] = new ConversionArgument(key, argument.Value);
        }

        foreach (var argument in lower)
        {
            Add(argument);
        }

        foreach (var argument in higher)
        {
            Add(argument);
        }

        return order.Select(key => values[key]).ToList();
    }

    /// <summary>统一成 --xxx 形式：用户手输时容易漏掉前导的两个减号。</summary>
    public static string NormalizeKey(string key)
    {
        var trimmed = key.Trim();
        return trimmed.StartsWith(OptionPrefix, StringComparison.Ordinal)
            ? trimmed
            : OptionPrefix + trimmed;
    }

    /// <summary>逐个过白名单。任何一个不合法就抛，绝不"跳过不合法的、继续跑"。</summary>
    public static void Validate(
        CalibreOptions catalog, IReadOnlyList<ConversionArgument> options)
    {
        foreach (var option in options)
        {
            var declared = catalog.Find(option.Key);
            if (declared is null)
            {
                var hint = catalog.IsFallback
                    ? "（当前拿不到 Calibre 的完整参数表，只放行内置模板用到的参数）"
                    : string.Empty;

                throw new AppException(
                    ErrorCode.ConversionArgsRejected,
                    $"参数 {option.Key} 不被当前 Calibre 版本支持，已阻止执行。{hint}",
                    technical: $"Unknown option {option.Key}; "
                        + $"catalog={catalog.InputExtension}->{catalog.OutputExtension}; "
                        + $"fallback={catalog.IsFallback}");
            }

            if (declared.TakesValue && option.Value is null)
            {
                throw new AppException(
                    ErrorCode.ConversionArgsRejected,
                    $"参数 {option.Key} 需要给一个值。",
                    technical: $"{option.Key} requires a value");
            }
        }
    }

    /// <summary>展开成命令行片段。开关型只出键名，带值的出「键 值」两个元素。</summary>
    public static IReadOnlyList<string> ToCommandLineOptions(
        IReadOnlyList<ConversionArgument> options)
    {
        var result = new List<string>(options.Count * 2);
        foreach (var option in options)
        {
            result.Add(option.Key);
            if (option.Value is not null)
            {
                result.Add(option.Value);
            }
        }

        return result;
    }
}
