using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace EbookToolSuite.Engines.Epub;

/// <summary>
/// 把 EPUB 里的 XHTML 章节规范化成"能直接丢给浏览器渲染"的 HTML。
/// 规格见执行计划 T1-5 的规范化规则。
/// </summary>
/// <remarks>
/// 【为什么必须规范化】
/// 1. 章节里的相对路径（../Images/a.jpg）在虚拟主机下解析不出来，必须改写成
///    http://ebook.local/content/... 形式的绝对 URL。
/// 2. XHTML 常常没有 charset 声明，中文会乱码，必须注入 meta。
/// 3. 要统一走同一个渲染入口，才能做主题注入与定位。
///
/// 【为什么用 System.Xml.Linq 而不是 HTML 解析器】
/// 见 T-1 的决定 S8：HTML5 解析器会把 &lt;br/&gt; 改成 &lt;br&gt;、小写化标签名、
/// 丢掉命名空间。这里处理的正是 XHTML，XML 语义必须原样保住。
/// </remarks>
public static class EpubChapterNormalizer
{
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";

    /// <summary>规范化结果。</summary>
    public sealed record Result(string Html, bool Rewritten, string? Note);

    /// <summary>
    /// 规范化一章。
    /// </summary>
    /// <param name="xml">章节原始文本。</param>
    /// <param name="sourcePackagePath">该章在包内的路径，如 "OEBPS/Text/chap01.xhtml"。</param>
    /// <param name="contentRootUrl">
    /// 解压出来的 content 目录对应的 URL 前缀，如 "http://ebook.local/content/"。
    /// </param>
    public static Result? Normalize(
        string xml, string sourcePackagePath, string contentRootUrl)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        var document = TryParse(xml);
        if (document is null)
        {
            // 不是合法 XML：按原文返回，让浏览器自己去容错。
            // 代价是相对路径没被改写（图片可能不显示），但总比整章打不开强。
            return new Result(xml, false, "章节不是合法 XML，已按原文返回（资源路径未改写）");
        }

        var html = document.Root;
        if (html is null || html.Name.LocalName != "html")
        {
            // 有些书写成裸 <body> 或直接一堆 <p>，这是坏包，不猜。
            return null;
        }

        var rewritten = 0;

        var head = html.Elements().FirstOrDefault(e => e.Name.LocalName == "head");
        if (head is null)
        {
            head = new XElement(Xhtml + "head");
            html.AddFirst(head);
        }

        rewritten += EnsureCharset(head);
        rewritten += RewriteReferences(html, sourcePackagePath, contentRootUrl);

        var builder = new StringBuilder();
        builder.Append("<!DOCTYPE html>\n");
        builder.Append(html.ToString(SaveOptions.DisableFormatting));

        return new Result(
            builder.ToString(),
            Rewritten: rewritten > 0,
            Note: null);
    }

    /// <summary>
    /// 注入 meta charset。已经有一个 charset 声明就改它，避免出现两个（浏览器只认第一个）。
    /// </summary>
    private static int EnsureCharset(XElement head)
    {
        var existing = head.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "meta"
                && e.Attribute("charset") is not null);

        if (existing is not null)
        {
            if (!string.Equals(existing.Attribute("charset")?.Value, "utf-8", StringComparison.OrdinalIgnoreCase))
            {
                existing.SetAttributeValue("charset", "utf-8");
                return 1;
            }

            return 0;
        }

        head.AddFirst(new XElement(Xhtml + "meta", new XAttribute("charset", "utf-8")));
        return 1;
    }

    /// <summary>改写全部指向包内资源的属性。返回改了几处。</summary>
    private static int RewriteReferences(
        XElement root, string sourcePackagePath, string contentRootUrl)
    {
        var count = 0;
        var baseDirectory = EpubPackageIndex.GetDirectory(sourcePackagePath);

        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes().ToList())
            {
                if (!ShouldRewrite(element.Name.LocalName, attribute))
                {
                    continue;
                }

                var value = attribute.Value;
                if (string.IsNullOrWhiteSpace(value) || IsAbsolute(value))
                {
                    continue;
                }

                var target = ResolveAndEncode(
                    baseDirectory, value, contentRootUrl, out var fragment);

                if (target is null)
                {
                    continue;
                }

                attribute.Value = fragment is null ? target : target + fragment;
                count++;
            }
        }

        return count;
    }

    /// <summary>哪些属性指向包内资源。</summary>
    private static bool ShouldRewrite(string elementName, XAttribute attribute)
    {
        var local = attribute.Name.LocalName;

        // SVG 里引用图片走 xlink:href
        if (attribute.Name.Namespace == Xlink && local == "href")
        {
            return true;
        }

        return local switch
        {
            // 图片、脚本、框架、音视频
            "src" => true,

            // 样式表
            "href" when string.Equals(elementName, "link", StringComparison.OrdinalIgnoreCase) => true,

            // 普通超链接：也改写，这样点击时前端能拿回"指向包内哪个文件"
            "href" when string.Equals(elementName, "a", StringComparison.OrdinalIgnoreCase) => true,

            // 视频封面图
            "poster" => true,

            _ => false,
        };
    }

    /// <summary>已经是绝对 URL / 锚点 / 特殊协议的，不动。</summary>
    private static bool IsAbsolute(string value)
    {
        if (value.StartsWith('#') || value.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        // data:、mailto:、javascript:、tel:、http:、https: 等统一按"冒号在第一个斜杠之前"判定
        var colon = value.IndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        var slash = value.IndexOf('/');
        return slash < 0 || colon < slash;
    }

    /// <summary>
    /// 相对路径 -&gt; content 目录下的绝对 URL。识别不了返回 null。
    /// </summary>
    private static string? ResolveAndEncode(
        string baseDirectory, string value, string contentRootUrl, out string? fragment)
    {
        fragment = null;

        var hash = value.IndexOf('#');
        var path = value;
        if (hash >= 0)
        {
            path = value[..hash];
            fragment = value[hash..];
        }

        if (path.Length == 0)
        {
            return null;
        }

        var query = path.IndexOf('?');
        if (query >= 0)
        {
            path = path[..query];
        }

        var combined = baseDirectory.Length == 0 ? path : baseDirectory + "/" + path;
        var normalized = CollapseDotSegments(combined);
        if (normalized is null)
        {
            // 跑到包外去了（../../..），拒绝改写，保持原样让浏览器报 404
            return null;
        }

        return contentRootUrl + EncodePath(normalized);
    }

    /// <summary>
    /// 处理 ./ 与 ../。越出根目录返回 null（不改写这种链接）。
    /// </summary>
    private static string? CollapseDotSegments(string path)
    {
        var stack = new List<string>();

        foreach (var segment in path.Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count == 0)
                {
                    return null;
                }

                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(segment);
        }

        return stack.Count == 0 ? null : string.Join('/', stack);
    }

    /// <summary>逐段百分号编码，保留斜杠。中文文件名在 EPUB 里很常见。</summary>
    private static string EncodePath(string packagePath)
        => string.Join('/', packagePath.Split('/').Select(Uri.EscapeDataString));

    private static XDocument? TryParse(string xml)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                // 章节自带 DOCTYPE 时不能炸：Ignore 直接跳过 DTD，不解析实体
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreComments = false,
                IgnoreWhitespace = false,
            };

            using var reader = XmlReader.Create(new StringReader(xml), settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception)
        {
            return null;
        }
    }
}