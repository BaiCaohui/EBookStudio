using System.Text.Json.Nodes;

namespace EbookToolSuite.Core.Models;

/// <summary>
/// 笔记/高亮的选区锚点（T6-5）。落在 notes.anchor 这一列的 JSON 文本。
///
/// 【为什么要存"路径 + 偏移"而不是一个字符索引】
/// <c>window.getSelection()</c> 的 Range 可以跨多个元素（一段里加粗、链接、Ruby 都会切开
/// 文本节点），所以没有"第几个字符"这种全局编号可用。设计文档 8.4 / T6-5 的做法是：
/// 存**起止容器的路径**（从 body 往下走，每层记录 childIndex）与容器内的字符偏移，
/// 恢复时按路径重新找到 DOM 节点与偏移，再构造 Range。
///
/// 【路径会失效，所以还有两级兜底】
/// 用户改书、换排版、重载后 DOM 结构可能变，路径找不到就是"位置失效"：
///   1. 路径 + 偏移（最精确）
///   2. quote 引文在本章正文里搜索（NFKC + 去空白后比对，按偏移找最近的命中）
///   3. 章内比例 percentInChapter（最后手段）
/// 三级都失败时由前端提示"笔记位置已失效"，而不是让打开书失败。
/// </summary>
public sealed class NoteAnchor
{
    /// <summary>起点路径，如 <c>body/0/p/2</c>。null 表示没有路径级锚点。</summary>
    public string? StartPath { get; set; }

    /// <summary>起点在容器内的字符偏移。</summary>
    public int StartOffset { get; set; }

    /// <summary>终点路径（可与起点不同，跨元素选区）。</summary>
    public string? EndPath { get; set; }

    /// <summary>终点在容器内的字符偏移。</summary>
    public int EndOffset { get; set; }

    /// <summary>章内比例 0..1。路径失效后用来把用户送到大致位置。</summary>
    public double? PercentInChapter { get; set; }

    /// <summary>PDF：选区覆盖的第一个字符下标（state.pdf.chars 里的序号）。</summary>
    public int? PageCharStart { get; set; }

    /// <summary>PDF：选区覆盖的最后一个字符下标（含）。</summary>
    public int? PageCharEnd { get; set; }

    public bool HasPath => !string.IsNullOrWhiteSpace(StartPath);

    public string ToJson()
    {
        var node = new JsonObject();

        if (!string.IsNullOrEmpty(StartPath))
        {
            node["startPath"] = StartPath;
            node["startOffset"] = StartOffset;
        }

        if (!string.IsNullOrEmpty(EndPath))
        {
            node["endPath"] = EndPath;
            node["endOffset"] = EndOffset;
        }

        if (PercentInChapter is not null)
        {
            node["percentInChapter"] = PercentInChapter.Value;
        }

        if (PageCharStart is not null)
        {
            node["pageCharStart"] = PageCharStart.Value;
            node["pageCharEnd"] = PageCharEnd ?? PageCharStart.Value;
        }

        return node.ToJsonString();
    }

    /// <summary>解析坏掉的锚点一律返回 null：笔记位置是锦上添花的数据，绝不能让它弄坏阅读。</summary>
    public static NoteAnchor? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject node)
            {
                return null;
            }

            return new NoteAnchor
            {
                StartPath = Text(node, "startPath"),
                StartOffset = Int(node, "startOffset"),
                EndPath = Text(node, "endPath"),
                EndOffset = Int(node, "endOffset"),
                PercentInChapter = Double(node, "percentInChapter"),
                PageCharStart = IntOrNull(node, "pageCharStart"),
                PageCharEnd = IntOrNull(node, "pageCharEnd"),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Text(JsonObject node, string name)
        => node[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int Int(JsonObject node, string name)
        => node[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    private static int? IntOrNull(JsonObject node, string name)
        => node[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static double? Double(JsonObject node, string name)
        => node[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
