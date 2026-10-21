using System.Text.Json.Nodes;

namespace EbookToolSuite.Core.Models;

/// <summary>
/// 阅读定位（设计文档 8.4 的 Locator）。三级精度同时存下来，读取时按序回退：
/// 滚动像素（只有同排版才可信）-> 章内比例 -> 文本指纹 -> 章首。
/// </summary>
/// <remarks>
/// 落在库里的形态是 reading_progress.location 这一列的 JSON 文本。
/// 设计文档 5.2 的 DDL 只有 location / fingerprint 两列（执行计划里那张
/// locator_type / locator_value 的表格是早期写法，已作废），所以这里
/// 把"类型"隐含在字段是否存在上：有 scrollTop 就是精确级，只有
/// percentInChapter 就是章节级。
/// </remarks>
public sealed class ReaderLocator
{
    /// <summary>章节在包内的路径，如 OEBPS/ch03.xhtml。用于校验书是否换过。</summary>
    public string? ChapterHref { get; set; }

    /// <summary>spine 序号。也是"读到第几章"这个说法的权威值。</summary>
    public int ChapterIndex { get; set; }

    /// <summary>章内比例 0..1。换排版后仍然大致有效，是最实用的一级。</summary>
    public double? PercentInChapter { get; set; }

    /// <summary>滚动像素。换字号 / 换字体 / 换窗口宽度后就不可信，用前必须校验。</summary>
    public double? ScrollTop { get; set; }

    /// <summary>记录当时的滚动高度，用于校验 ScrollTop 是否还有效。</summary>
    public double? ScrollHeight { get; set; }

    /// <summary>是否为"精确级"定位（有像素值）。</summary>
    public bool HasScroll => ScrollTop is not null;

    public string ToJson()
    {
        var node = new JsonObject { ["chapterIndex"] = ChapterIndex };

        if (!string.IsNullOrEmpty(ChapterHref))
        {
            node["chapterHref"] = ChapterHref;
        }

        if (PercentInChapter is not null)
        {
            node["percentInChapter"] = PercentInChapter.Value;
        }

        if (ScrollTop is not null)
        {
            node["scrollTop"] = ScrollTop.Value;
        }

        if (ScrollHeight is not null)
        {
            node["scrollHeight"] = ScrollHeight.Value;
        }

        return node.ToJsonString();
    }
    /// <summary>
    /// 从库里存的 JSON 解析回来。任何异常都返回 null：
    /// 进度是"锦上添花"的数据，坏掉只该回到章首，绝不能让打开书失败。
    /// </summary>
    public static ReaderLocator? TryParse(string? json)
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

            return new ReaderLocator
            {
                ChapterHref = node["chapterHref"]?.GetValue<string>(),
                ChapterIndex = node["chapterIndex"] is JsonValue ci && ci.TryGetValue<int>(out var i) ? i : 0,
                PercentInChapter = TryDouble(node, "percentInChapter"),
                ScrollTop = TryDouble(node, "scrollTop"),
                ScrollHeight = TryDouble(node, "scrollHeight"),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double? TryDouble(JsonObject node, string name)
    {
        try
        {
            return node[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// 文本指纹（设计文档 8.4：章节内首个可见块的前 30 字、中 20 字、后 30 字）。
/// 换字号、换主题、换设备后仍然有效，是最后一道防线。
/// </summary>
public sealed class TextFingerprint
{
    public string? Pre { get; set; }
    public string? Mid { get; set; }
    public string? Post { get; set; }

    public bool IsUsable => !string.IsNullOrWhiteSpace(Pre);

    public string ToJson()
    {
        var node = new JsonObject();

        if (!string.IsNullOrEmpty(Pre))
        {
            node["pre"] = Pre;
        }

        if (!string.IsNullOrEmpty(Mid))
        {
            node["mid"] = Mid;
        }

        if (!string.IsNullOrEmpty(Post))
        {
            node["post"] = Post;
        }

        return node.ToJsonString();
    }

    public static TextFingerprint? TryParse(string? json)
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

            var fp = new TextFingerprint
            {
                Pre = node["pre"]?.GetValue<string>(),
                Mid = node["mid"]?.GetValue<string>(),
                Post = node["post"]?.GetValue<string>(),
            };

            return fp.IsUsable ? fp : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}