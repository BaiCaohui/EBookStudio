using System.Text.RegularExpressions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;

namespace EbookToolSuite.Engines.Epub;

/// <summary>
/// 可视模式保存时的组装（T5-2W）：可视编辑器只回传 body 里的内容，
/// 这里把原文件的 head（样式、meta 一字不改）和 body 开标签拼回去。
/// </summary>
public static class ChapterVisualEdit
{
    private static readonly Regex BodyOpenPattern = new(
        @"<body\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>把 body 内容组装成完整的 XHTML 源码。</summary>
    public static string Assemble(string originalSource, string bodyInnerXml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalSource);

        var open = BodyOpenPattern.Match(originalSource);
        var close = originalSource.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);

        if (!open.Success || close < 0 || close < open.Index)
        {
            throw new AppException(
                ErrorCode.EpubInvalid, "这一章里找不到 <body>...</body>", null);
        }

        var head = originalSource[..open.Index];
        var tail = originalSource[(close + "</body>".Length)..];

        return head + open.Value + (bodyInnerXml ?? string.Empty) + "</body>" + tail;
    }
}
