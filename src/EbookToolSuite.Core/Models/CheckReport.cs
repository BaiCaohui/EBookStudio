using System.Globalization;

namespace EbookToolSuite.Core.Models;

/// <summary>
/// EpubCheck 的严重级别。报告里是大写（FATAL/ERROR/WARNING/USAGE），这里统一成小写常量，
/// 免得比较时到处写 ToLower 又忘了 culture。
/// </summary>
public static class CheckSeverity
{
    public const string Fatal = "fatal";
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Usage = "usage";
    public const string Info = "info";
    public const string Unknown = "unknown";

    /// <summary>归一成小写常量；认不出来算 unknown（不抛异常，报告是外部数据）。</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Unknown;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "fatal" => Fatal,
            "error" => Error,
            "warning" => Warning,
            "usage" => Usage,
            "info" or "informational" => Info,
            _ => Unknown,
        };
    }
}

/// <summary>报告里一条 message 的位置。</summary>
public sealed record CheckLocation(string Path, int? Line, int? Column)
{
    /// <summary>"OEBPS/content.opf:12:3"；没有行列时只给路径。</summary>
    public string DisplayText
    {
        get
        {
            var text = string.IsNullOrEmpty(Path) ? "(未给位置)" : Path;
            if (Line is { } line)
            {
                text += ":" + line.ToString(CultureInfo.InvariantCulture);
                if (Column is { } column)
                {
                    text += ":" + column.ToString(CultureInfo.InvariantCulture);
                }
            }

            return text;
        }
    }
}

/// <summary>EpubCheck 报告里的一条 message。</summary>
public sealed record CheckMessage(
    string Severity,
    string Id,
    string Message,
    IReadOnlyList<CheckLocation> Locations)
{
    public bool IsFatal => Severity == CheckSeverity.Fatal;

    public bool IsError => Severity == CheckSeverity.Error;

    public bool IsWarning => Severity == CheckSeverity.Warning;

    public CheckLocation? FirstLocation => Locations.Count > 0 ? Locations[0] : null;

    /// <summary>给人看的一行，界面里直接显示。</summary>
    public string DisplayText
    {
        get
        {
            var text = "[" + Severity + "] " + Id + " " + Message;
            if (FirstLocation is { } location)
            {
                text += "（" + location.DisplayText + "）";
            }

            return text;
        }
    }
}

/// <summary>
/// 一次 EpubCheck 校验的结果。计数直接取报告的 checker 段（权威），
/// messages 保留全部条目（含 usage/info），界面只显示前若干条。
/// </summary>
public sealed record CheckReport(
    int FatalCount,
    int ErrorCount,
    int WarningCount,
    int UsageCount,
    IReadOnlyList<CheckMessage> Messages,
    TimeSpan Duration = default,
    bool ParseFailed = false,
    string? ParseFailureReason = null)
{
    /// <summary>
    /// 报告读不出来时的兜底。注意这里**不抛异常**：让门禁去决定怎么办
    /// （抛异常会让「报告坏了」变成「保存整个失败」，而计划要求的是能兜底）。
    /// </summary>
    public static CheckReport Unparseable(string reason)
        => new(0, 0, 0, 0, Array.Empty<CheckMessage>(), default, true, reason);

    /// <summary>fatal 或 error 有任意一条 -> 保存默认要拦下来。</summary>
    public bool HasBlockingErrors => FatalCount > 0 || ErrorCount > 0;

    public bool HasWarnings => WarningCount > 0;

    public int IssueCount => FatalCount + ErrorCount + WarningCount;

    public string CountsText => "fatal " + FatalCount + " / error " + ErrorCount + " / warning " + WarningCount;

    /// <summary>前 max 条给人看的行（默认 10 条，免得把界面撑爆）。</summary>
    public IReadOnlyList<string> TopMessages(int max = 10)
    {
        if (max <= 0 || Messages.Count == 0)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>(Math.Min(max, Messages.Count));
        foreach (var message in Messages)
        {
            if (lines.Count >= max)
            {
                break;
            }

            lines.Add(message.DisplayText);
        }

        return lines;
    }
}
