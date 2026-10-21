using System.Text;
using System.Text.Json;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Engines.Epub;

/// <summary>
/// 把 EpubCheck 的 --json 报告解析成 <see cref="CheckReport"/>。
///
/// EpubCheck 5.4.0 的结构（实测）：
/// <code>
/// {
///   "checker":  { "nFatal": 0, "nError": 1, "nWarning": 2, "nUsage": 0 },
///   "messages": [ { "ID": "OPF-003", "severity": "ERROR", "message": "",
///                   "locations": [ { "path": "OEBPS/content.opf", "line": 12, "column": 3 } ] } ]
/// }
/// </code>
///
/// 两条纪律：
///   1. 外部数据，解析失败**绝不抛异常**返回 ParseFailed=true 的报告，交给门禁兜底；
///   2. 计数以 checker 段为准，但某个键缺失时从 messages 自己数（不同版本键名有过变化）。
/// </summary>
public static class EpubCheckReportParser
{
    public static CheckReport ParseFile(string jsonPath)
    {
        try
        {
            return Parse(File.ReadAllText(jsonPath, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CheckReport.Unparseable("读不了 EpubCheck 报告：" + ex.Message);
        }
    }

    public static CheckReport Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return CheckReport.Unparseable("EpubCheck 报告是空的。");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var messages = ReadMessages(root);
            var (fatal, error, warning, usage) = ReadCounts(root, messages);
            return new CheckReport(fatal, error, warning, usage, messages);
        }
        catch (JsonException ex)
        {
            return CheckReport.Unparseable("EpubCheck 报告不是合法 JSON：" + ex.Message);
        }
    }

    private static List<CheckMessage> ReadMessages(JsonElement root)
    {
        var messages = new List<CheckMessage>();
        if (!root.TryGetProperty("messages", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            messages.Add(new CheckMessage(
                CheckSeverity.Normalize(GetString(item, "severity")),
                GetString(item, "ID") ?? "(无 ID)",
                GetString(item, "message") ?? string.Empty,
                ReadLocations(item)));
        }

        return messages;
    }

    private static List<CheckLocation> ReadLocations(JsonElement message)
    {
        var locations = new List<CheckLocation>();
        if (!message.TryGetProperty("locations", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return locations;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            locations.Add(new CheckLocation(
                GetString(item, "path") ?? string.Empty,
                GetInt(item, "line"),
                GetInt(item, "column")));
        }

        return locations;
    }

    private static (int Fatal, int Error, int Warning, int Usage) ReadCounts(
        JsonElement root,
        List<CheckMessage> messages)
    {
        // 兜底用：从 messages 自己数一遍。
        var derivedFatal = messages.Count(m => m.IsFatal);
        var derivedError = messages.Count(m => m.IsError);
        var derivedWarning = messages.Count(m => m.IsWarning);
        var derivedUsage = messages.Count(m => m.Severity is CheckSeverity.Usage or CheckSeverity.Info);

        if (root.TryGetProperty("checker", out var checker) && checker.ValueKind == JsonValueKind.Object)
        {
            return (
                GetInt(checker, "nFatal") ?? derivedFatal,
                GetInt(checker, "nError") ?? derivedError,
                GetInt(checker, "nWarning") ?? derivedWarning,
                GetInt(checker, "nUsage") ?? GetInt(checker, "nInformational") ?? derivedUsage);
        }

        return (derivedFatal, derivedError, derivedWarning, derivedUsage);
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;
}
