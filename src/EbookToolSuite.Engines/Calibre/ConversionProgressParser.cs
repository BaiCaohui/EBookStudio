using System.Globalization;
using System.Text.RegularExpressions;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Engines.Calibre;

/// <summary>
/// Calibre 进度解析（设计文档 6.3.3 的诚实策略）。
///
/// 实测事实（docs/calibre-args-baseline.txt 第 11 节）：
///   - 一本 418 KB 的书全程 73 行输出，其中【只有 3 行】带百分比（1% / 34% / 67%）；
///   - 注入 CALIBRE_OVERRIDE_LANG=en 走英文，但中文兜底也要留（注入可能失败）；
///   - 匹配不到时返回 null，由 UI 退化成"不确定进度 + 已耗时"。
/// 绝不伪造成 50%：那会让用户以为转换卡住或者正常，两种判断都是错的。
/// </summary>
public sealed partial class ConversionProgressParser
{
    [GeneratedRegex(@"^(\d{1,3})%\s*(.*)$")]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"^Detected chapter:\s*(.+)$")]
    private static partial Regex ChapterRegex();

    [GeneratedRegex(@"^Output saved to\s+(.+)$")]
    private static partial Regex OutputSavedRegex();

    [GeneratedRegex(@"(\d+)\s*/\s*(\d+)\s*(?:images|pages|files)", RegexOptions.IgnoreCase)]
    private static partial Regex CountRegex();

    [GeneratedRegex(@"Processing page (\d+) of (\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PageRegex();

    private double? _percent;

    private string? _stage;

    /// <summary>最近一次成功解析出的百分比（0..1）。null = 至今没有可用信息。</summary>
    public double? LastPercent => _percent;

    /// <summary>最近一次解析出的阶段文本。</summary>
    public string? LastStage => _stage;

    /// <summary>
    /// 喂一行原始输出。
    /// 返回 null = 这一行没有带来任何新的进度信息（UI 只需更新"最近输出"）。
    /// </summary>
    public ConversionProgress? Feed(string line, TimeSpan elapsed)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var text = line.Trim();

        // 1) 完成标志：Calibre 收尾时会打这行，是唯一可信的 100%。
        if (OutputSavedRegex().IsMatch(text))
        {
            _percent = 1d;
            _stage = "转换完成";
            return Build(elapsed);
        }

        // 2) 百分比（中英文同样格式："1% Converting input to HTML..." / "34% 正在...")
        var percentMatch = PercentRegex().Match(text);
        if (percentMatch.Success
            && int.TryParse(percentMatch.Groups[1].Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var percent)
            && percent is >= 0 and <= 100)
        {
            _percent = percent / 100d;
            var rest = percentMatch.Groups[2].Value.Trim();
            _stage = rest.Length > 0 ? rest : text;
            return Build(elapsed);
        }

        // 3) 章节标题：只能更新阶段文本，百分比保持不变（不能凭空进位）。
        var chapterMatch = ChapterRegex().Match(text);
        if (chapterMatch.Success)
        {
            _stage = "正在处理：" + chapterMatch.Groups[1].Value.Trim();
            return Build(elapsed);
        }

        // 4) 页码 / 计数类（PDF 输出与资源处理阶段会打）。
        var fraction = TryParseFraction(text);
        if (fraction is not null)
        {
            _percent = fraction.Value;
            return Build(elapsed);
        }

        return null;
    }

    private double? TryParseFraction(string text)
    {
        var page = PageRegex().Match(text);
        if (page.Success)
        {
            return Fraction(page.Groups[1].Value, page.Groups[2].Value);
        }

        var count = CountRegex().Match(text);
        if (count.Success)
        {
            return Fraction(count.Groups[1].Value, count.Groups[2].Value);
        }

        return null;
    }

    private static double? Fraction(string currentText, string totalText)
    {
        if (!int.TryParse(currentText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var current)
            || !int.TryParse(totalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
            || total <= 0)
        {
            return null;
        }

        return Math.Clamp((double)current / total, 0d, 1d);
    }

    private ConversionProgress Build(TimeSpan elapsed) => new()
    {
        Percent = _percent,
        StageText = _stage,
        Elapsed = elapsed,
        LastLine = _stage,
    };
}
