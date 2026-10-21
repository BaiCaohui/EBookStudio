using System.Text;
using System.Xml;
using System.Xml.Linq;
using EbookToolSuite.Core.IO;

namespace EbookToolSuite.Engines.Epub;

/// <summary>
/// 目录文档（EPUB3 的 nav.xhtml / EPUB2 的 toc.ncx）的标题与条目改写（执行计划 T5-1，设计文档 6.1.4）。
/// </summary>
/// <remarks>
/// <para>
/// <b>只改标题文字与条目增减，绝不改章节文件名</b>：计划 T5-1 的关键实现点 3 说得很清楚
/// 改名会破坏目录、内部链接、书签等一切引用。所以"重命名章节"在文件层面是零改动的，
/// 只有目录里的 &lt;text&gt; / &lt;a&gt; 文本变。
/// </para>
/// <para>
/// 写回同样遵守 T3-2 的约束：无 BOM 的 UTF-8、不动原有换行风格、保留空白与 XML 声明原文。
/// 目录文件很小，改写面越窄，真实电子书的 diff 就越干净。
/// </para>
/// <para>
/// 匹配用"解析成包内路径再比"：目录里的 href 是相对目录文件自身的（如 <c>Text/chap01.xhtml</c>），
/// 章节坐标却是包内的（<c>OEBPS/Text/chap01.xhtml</c>），还要处理 <c>%20</c> 转义与 <c>#fragment</c>。
/// </para>
/// </remarks>
public static class NavDocumentBuilder
{
    /// <summary>XHTML 命名空间。</summary>
    public static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";

    /// <summary>NCX 命名空间（EPUB2 目录）。</summary>
    public static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";

    /// <summary>EPUB 出版命名空间（nav 的 epub:type）。</summary>
    public static readonly XNamespace Ops = "http://www.idpf.org/2007/ops";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>改目录里指向 <paramref name="chapterHref"/> 的那条标题。返回是否真改到了。</summary>
    public static bool RenameTitle(string workDir, string documentEntry, string chapterHref, string newTitle)
        => Edit(workDir, documentEntry, (doc, entry) => SetTitle(doc, entry, chapterHref, newTitle) ? 1 : 0) > 0;

    /// <summary>删掉指向这些章节的目录项（连带清掉因此变空的列表）。返回删掉几条。</summary>
    public static int RemoveEntries(string workDir, string documentEntry, IReadOnlyCollection<string> chapterHrefs)
    {
        var wanted = new HashSet<string>(chapterHrefs.Select(Collapse), StringComparer.OrdinalIgnoreCase);
        return Edit(workDir, documentEntry, (doc, entry) =>
            IsNcx(doc) ? RemoveNcxEntries(doc, entry, wanted) : RemoveNavEntries(doc, entry, wanted));
    }

    /// <summary>
    /// 插一条目录项（顶层）。<paramref name="afterChapterHref"/> 给了就插在它后面，找不到或为 null 就追加到末尾。
    /// </summary>
    public static bool InsertEntry(
        string workDir, string documentEntry, string? afterChapterHref, string chapterHref, string title)
        => Edit(workDir, documentEntry, (doc, entry) =>
            (IsNcx(doc)
                ? InsertNcxEntry(doc, entry, afterChapterHref, chapterHref, title)
                : InsertNavEntry(doc, entry, afterChapterHref, chapterHref, title)) ? 1 : 0) > 0;

    /// <summary>按目录顺序读出所有条目的包内路径（诊断/测试用）。</summary>
    public static IReadOnlyList<string> ReadHrefs(string workDir, string documentEntry)
    {
        var path = FullPath(workDir, documentEntry);
        var doc = XDocument.Parse(File.ReadAllText(path), LoadOptions.PreserveWhitespace);
        var result = new List<string>();

        foreach (var element in doc.Descendants())
        {
            var href = element.Name.LocalName switch
            {
                "a" => (string?)element.Attribute("href"),
                "content" => (string?)element.Attribute("src"),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(href))
            {
                result.Add(Resolve(documentEntry, href));
            }
        }

        return result;
    }

    /// <summary>目录里的一条：标题 + 包内路径 + 层级（1 = 顶层）。</summary>
    public sealed record NavEntry(string Title, string Href, int Level);

    /// <summary>把 href 解析成包内路径（相对工作区根），供调用方比较用。</summary>
    public static string ResolveHref(string documentEntry, string href) => Resolve(documentEntry, href);

    /// <summary>
    /// 读出目录里的全部条目（按文档顺序，带层级）。没有条目时返回空表。
    /// </summary>
    /// <remarks>
    /// 章节树要"多级缩进"就靠这里的 Level：nav 走 ol/li 嵌套，ncx 走 navPoint 嵌套。
    /// 只认带 href 的条目（分组的 &lt;span&gt; 没有 href，跳不过去，不往树上放）。
    /// </remarks>
    public static IReadOnlyList<NavEntry> ReadEntries(string workDir, string documentEntry)
    {
        var document = XDocument.Parse(File.ReadAllText(FullPath(workDir, documentEntry)), LoadOptions.PreserveWhitespace);
        var result = new List<NavEntry>();

        if (IsNcx(document))
        {
            var map = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "navMap");
            if (map is not null)
            {
                WalkNavPoints(map, documentEntry, 1, result);
            }
        }
        else
        {
            var nav = document.Descendants().FirstOrDefault(e =>
                          e.Name.LocalName == "nav"
                          && string.Equals((string?)e.Attribute(Ops + "type"), "toc", StringComparison.OrdinalIgnoreCase))
                      ?? document.Descendants().FirstOrDefault(e => e.Name.LocalName == "nav");

            var list = nav?.Elements().FirstOrDefault(e => e.Name.LocalName == "ol");
            if (list is not null)
            {
                WalkNavList(list, documentEntry, 1, result);
            }
        }

        return result;
    }

    /// <summary>
    /// 把目录顶层条目按给定章节顺序重排（spine 改顺序后调用，让目录顺序跟着走）。
    /// 顺序表里没有的条目排到最后并保持原有相对顺序；嵌套子项跟着父项一起搬。
    /// </summary>
    public static bool AlignTopLevelOrder(string workDir, string documentEntry, IReadOnlyList<string> orderedHrefs)
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < orderedHrefs.Count; i++)
        {
            rank[Collapse(orderedHrefs[i])] = i;
        }

        return Edit(workDir, documentEntry, (document, entry) =>
        {
            var isNcx = IsNcx(document);
            var container = isNcx
                ? document.Descendants().FirstOrDefault(e => e.Name.LocalName == "navMap")
                : document.Descendants()
                    .FirstOrDefault(e =>
                        e.Name.LocalName == "nav"
                        && string.Equals((string?)e.Attribute(Ops + "type"), "toc", StringComparison.OrdinalIgnoreCase))
                    ?.Elements().FirstOrDefault(e => e.Name.LocalName == "ol");

            if (container is null)
            {
                return 0;
            }

            var name = isNcx ? "navPoint" : "li";
            var items = container.Elements().Where(e => e.Name.LocalName == name).ToList();
            if (items.Count < 2)
            {
                return 0;
            }

            var sorted = items
                .Select((element, position) => (Element: element, Position: position, Key: RankOf(entry, element, isNcx, rank)))
                .OrderBy(x => x.Key)
                .ThenBy(x => x.Position)
                .ToList();

            if (sorted.Select(x => x.Position).SequenceEqual(Enumerable.Range(0, items.Count)))
            {
                return 0;
            }

            foreach (var item in sorted)
            {
                RemoveWithIndent(item.Element);
            }

            foreach (var item in sorted)
            {
                AppendChild(container, item.Element);
            }

            if (isNcx)
            {
                ResequencePlayOrder(document);
            }

            return 1;
        }) > 0;
    }

    /// <summary>条目在给定顺序表里的排位；不在表里排到最后。</summary>
    private static int RankOf(string entry, XElement element, bool isNcx, Dictionary<string, int> rank)
    {
        var href = isNcx
            ? (string?)element.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Attribute("src")
            : (string?)element.Descendants().FirstOrDefault(e => e.Name.LocalName == "a")?.Attribute("href");

        if (href is null)
        {
            return int.MaxValue;
        }

        var resolved = Resolve(entry, href);
        return rank.TryGetValue(resolved, out var value) ? value : int.MaxValue;
    }

    private static void WalkNavList(XElement list, string entry, int level, List<NavEntry> result)
    {
        foreach (var li in list.Elements().Where(e => e.Name.LocalName == "li"))
        {
            var anchor = li.Elements().FirstOrDefault(e => e.Name.LocalName == "a");
            var label = anchor ?? li.Elements().FirstOrDefault(e => e.Name.LocalName == "span");
            var href = (string?)anchor?.Attribute("href");
            var title = label?.Value?.Trim();

            if (!string.IsNullOrWhiteSpace(href) && !string.IsNullOrWhiteSpace(title))
            {
                result.Add(new NavEntry(title!, Resolve(entry, href!), level));
            }

            var child = li.Elements().FirstOrDefault(e => e.Name.LocalName == "ol");
            if (child is not null)
            {
                WalkNavList(child, entry, level + 1, result);
            }
        }
    }

    private static void WalkNavPoints(XElement parent, string entry, int level, List<NavEntry> result)
    {
        foreach (var point in parent.Elements().Where(e => e.Name.LocalName == "navPoint"))
        {
            var title = point.Elements().FirstOrDefault(e => e.Name.LocalName == "navLabel")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "text")?.Value?.Trim();
            var source = (string?)point.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Attribute("src");

            if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(title))
            {
                result.Add(new NavEntry(title!, Resolve(entry, source!), level));
            }

            WalkNavPoints(point, entry, level + 1, result);
        }
    }

    /// <summary>追加子元素，并沿用原有缩进（插在收尾空白之前）。</summary>
    private static void AppendChild(XElement parent, XElement child)
    {
        var indent = IndentOf(parent);
        var last = parent.Elements().LastOrDefault();

        if (last?.NextNode is XText trailing && trailing.Value.Trim().Length == 0)
        {
            if (indent.Length > 0)
            {
                trailing.AddBeforeSelf(new XText(indent));
            }

            trailing.AddBeforeSelf(child);
            return;
        }

        if (indent.Length > 0)
        {
            parent.Add(new XText(indent));
        }

        parent.Add(child);
    }

    /// <summary>
    /// 把某条目录项降一级：挂到它前一个同级条目的底下（变成子项）。没有前一个条目时返回 false。
    /// </summary>
    public static bool DemoteEntry(string workDir, string documentEntry, string chapterHref)
        => Edit(workDir, documentEntry, (document, entry) =>
            (IsNcx(document)
                ? DemoteNcx(document, entry, chapterHref)
                : DemoteNav(document, entry, chapterHref)) ? 1 : 0) > 0;

    /// <summary>
    /// 把某条目录项升一级：从父项里提出来，排在父项后面。本来就是顶层时返回 false。
    /// </summary>
    public static bool PromoteEntry(string workDir, string documentEntry, string chapterHref)
        => Edit(workDir, documentEntry, (document, entry) =>
            (IsNcx(document)
                ? PromoteNcx(document, entry, chapterHref)
                : PromoteNav(document, entry, chapterHref)) ? 1 : 0) > 0;

    // ================= 层级调整（nav.xhtml） =================
    // 目录的"层级"就是 DOM 的嵌套：降级 = 挪进前一个兄弟的 <ol>；升级 = 从父 <li> 里提出来。
    // 章节文件、spine、manifest 都不动目录层级纯粹是显示结构。

    private static bool DemoteNav(XDocument doc, string entry, string chapterHref)
    {
        var item = FindNavItem(doc, entry, chapterHref);
        var previous = item?.ElementsBeforeSelf().Where(e => e.Name.LocalName == "li").LastOrDefault();

        if (item is null || previous is null)
        {
            return false;
        }

        var childList = previous.Elements().FirstOrDefault(e => e.Name.LocalName == "ol");
        var indent = IndentOf(previous);

        if (childList is null)
        {
            childList = new XElement(Xhtml + "ol");

            if (indent.Length > 0)
            {
                previous.Add(new XText(indent + "  "));
            }

            previous.Add(childList);
        }

        RemoveWithIndent(item);
        AppendChild(childList, item);
        return true;
    }

    private static bool PromoteNav(XDocument doc, string entry, string chapterHref)
    {
        var item = FindNavItem(doc, entry, chapterHref);
        var list = item?.Parent;
        var parentItem = list?.Parent;

        if (item is null || list is null || parentItem is null || parentItem.Name.LocalName != "li")
        {
            return false;   // 本来就是顶层
        }

        var outerList = parentItem.Parent;
        if (outerList is null)
        {
            return false;
        }

        var indent = IndentOf(outerList);
        RemoveWithIndent(item);

        if (indent.Length > 0)
        {
            parentItem.AddAfterSelf(new XText(indent));
        }

        parentItem.AddAfterSelf(item);

        // 父项底下空了就把那个空列表收掉，别留下 <ol></ol>
        if (!list.Elements().Any(e => e.Name.LocalName == "li"))
        {
            RemoveWithIndent(list);
        }

        return true;
    }

    // ================= 层级调整（toc.ncx） =================

    private static bool DemoteNcx(XDocument doc, string entry, string chapterHref)
    {
        var point = FindNcxPoint(doc, entry, chapterHref);
        var previous = point?.ElementsBeforeSelf().Where(e => e.Name.LocalName == "navPoint").LastOrDefault();

        if (point is null || previous is null)
        {
            return false;
        }

        var indent = IndentOf(previous);
        RemoveWithIndent(point);

        // NCX 的 navPoint 子元素必须排在 <content> 之后，所以直接追加到末尾
        if (indent.Length > 0)
        {
            previous.Add(new XText(indent));
        }

        previous.Add(point);
        ResequencePlayOrder(doc);
        return true;
    }

    private static bool PromoteNcx(XDocument doc, string entry, string chapterHref)
    {
        var point = FindNcxPoint(doc, entry, chapterHref);
        var parent = point?.Parent;

        if (point is null || parent is null || parent.Name.LocalName != "navPoint")
        {
            return false;   // 本来就是顶层
        }

        var indent = IndentOf(parent.Parent ?? doc.Root!);
        RemoveWithIndent(point);

        if (indent.Length > 0)
        {
            parent.AddAfterSelf(new XText(indent));
        }

        parent.AddAfterSelf(point);
        ResequencePlayOrder(doc);
        return true;
    }

    private static XElement? FindNavItem(XDocument doc, string entry, string chapterHref)
        => doc.Descendants()
            .Where(e => e.Name.LocalName == "li")
            .FirstOrDefault(li => li.Elements().Any(a =>
                a.Name.LocalName == "a" && Matches(entry, (string?)a.Attribute("href"), chapterHref)));

    private static XElement? FindNcxPoint(XDocument doc, string entry, string chapterHref)
        => doc.Descendants()
            .Where(e => e.Name.LocalName == "navPoint")
            .FirstOrDefault(point => Matches(
                entry,
                (string?)point.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Attribute("src"),
                chapterHref));

    // ================= nav.xhtml（EPUB3） =================

    private static bool SetTitle( XDocument doc, string entry, string chapterHref, string newTitle)
        => IsNcx(doc) ? SetNcxTitle(doc, entry, chapterHref, newTitle) : SetNavTitle(doc, entry, chapterHref, newTitle);

    private static bool SetNavTitle(XDocument doc, string entry, string chapterHref, string newTitle)
    {
        foreach (var anchor in doc.Descendants().Where(e => e.Name.LocalName == "a"))
        {
            if (!Matches(entry, (string?)anchor.Attribute("href"), chapterHref))
            {
                continue;
            }

            anchor.ReplaceNodes(new XText(newTitle));
            return true;
        }

        return false;
    }

    private static int RemoveNavEntries(XDocument doc, string entry, HashSet<string> wanted)
    {
        var removed = 0;

        foreach (var li in doc.Descendants().Where(e => e.Name.LocalName == "li").ToList())
        {
            var anchor = li.Descendants().FirstOrDefault(e => e.Name.LocalName == "a");
            if (anchor is null || !Matches(entry, (string?)anchor.Attribute("href"), wanted))
            {
                continue;
            }

            var list = li.Parent;
            RemoveWithIndent(li);
            removed++;

            // 列表空了就把列表也收掉（留下 <ol></ol> 会让目录结构看起来很怪，EpubCheck 也会挑刺）。
            if (list is not null
                && !list.Elements().Any(e => e.Name.LocalName == "li")
                && list.Parent is not null
                && list.Parent.Elements().Any(e => e != list))
            {
                RemoveWithIndent(list);
            }
        }

        return removed;
    }

    private static bool InsertNavEntry(
        XDocument doc, string entry, string? afterChapterHref, string chapterHref, string title)
    {
        var list = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ol");
        if (list is null)
        {
            return false;
        }

        var item = new XElement(Xhtml + "li",
            new XElement(Xhtml + "a", new XAttribute("href", RelativeHref(entry, chapterHref)), new XText(title)));

        var indent = IndentOf(list);
        var anchorItem = afterChapterHref is null
            ? null
            : list.Elements().FirstOrDefault(li =>
                li.Descendants().Any(a =>
                    a.Name.LocalName == "a" && Matches(entry, (string?)a.Attribute("href"), afterChapterHref)));

        if (anchorItem is not null)
        {
            if (indent.Length > 0)
            {
                anchorItem.AddAfterSelf(new XText(indent));
            }

            anchorItem.AddAfterSelf(item);
            return true;
        }

        var last = list.Elements().LastOrDefault();
        if (last?.NextNode is XText trailing && trailing.Value.Trim().Length == 0)
        {
            if (indent.Length > 0)
            {
                trailing.AddBeforeSelf(new XText(indent));
            }

            trailing.AddBeforeSelf(item);
            return true;
        }

        if (indent.Length > 0)
        {
            list.Add(new XText(indent));
        }

        list.Add(item);
        return true;
    }

    // ================= toc.ncx（EPUB2） =================

    private static bool SetNcxTitle(XDocument doc, string entry, string chapterHref, string newTitle)
    {
        foreach (var text in NavPointsWithHref(doc, entry, chapterHref))
        {
            text.ReplaceNodes(new XText(newTitle));
            return true;
        }

        return false;
    }

    private static int RemoveNcxEntries(XDocument doc, string entry, HashSet<string> wanted)
    {
        var removed = 0;

        foreach (var point in doc.Descendants().Where(e => e.Name.LocalName == "navPoint").ToList())
        {
            var source = (string?)point.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Attribute("src");
            if (!Matches(entry, source, wanted))
            {
                continue;
            }

            RemoveWithIndent(point);
            removed++;
        }

        if (removed > 0)
        {
            ResequencePlayOrder(doc);
        }

        return removed;
    }

    private static bool InsertNcxEntry(
        XDocument doc, string entry, string? afterChapterHref, string chapterHref, string title)
    {
        var map = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "navMap");
        if (map is null)
        {
            return false;
        }

        // NCX 规范里 navPoint 的 id 是必填的：不写真 EpubCheck 直接 RSC-005
        // （T5-1 集成测试实测抓到过一次）。
        var point = new XElement(Ncx + "navPoint",
            new XAttribute("id", NextNavPointId(doc)),
            new XElement(Ncx + "navLabel", new XElement(Ncx + "text", new XText(title))),
            new XElement(Ncx + "content", new XAttribute("src", RelativeHref(entry, chapterHref))));

        var indent = IndentOf(map);
        var reference = afterChapterHref is null
            ? null
            : map.Elements()
                .FirstOrDefault(p => Matches(
                    entry,
                    (string?)p.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Attribute("src"),
                    afterChapterHref));

        if (reference is not null)
        {
            if (indent.Length > 0)
            {
                reference.AddAfterSelf(new XText(indent));
            }

            reference.AddAfterSelf(point);
        }
        else
        {
            var last = map.Elements().LastOrDefault();
            if (last?.NextNode is XText trailing && trailing.Value.Trim().Length == 0)
            {
                if (indent.Length > 0)
                {
                    trailing.AddBeforeSelf(new XText(indent));
                }

                trailing.AddBeforeSelf(point);
            }
            else
            {
                if (indent.Length > 0)
                {
                    map.Add(new XText(indent));
                }

                map.Add(point);
            }
        }

        ResequencePlayOrder(doc);
        return true;
    }

    /// <summary>给新 navPoint 起一个没被占用的 id（NCX 规范要求必填）。</summary>
    private static string NextNavPointId(XDocument doc)
    {
        var used = new HashSet<string>(
            doc.Descendants()
                .Where(e => e.Name.LocalName == "navPoint")
                .Select(e => (string?)e.Attribute("id") ?? string.Empty),
            StringComparer.Ordinal);

        for (var n = 1; n < 100000; n++)
        {
            var id = "ets-navPoint-" + n.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

            if (!used.Contains(id))
            {
                return id;
            }
        }

        return "ets-navPoint";
    }

    private static IEnumerable<XElement> NavPointsWithHref(XDocument doc, string entry, string chapterHref)
    {
        foreach (var point in doc.Descendants().Where(e => e.Name.LocalName == "navPoint"))
        {
            var source = (string?)point.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Attribute("src");
            var text = point.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "navLabel")?
                .Elements()
                .FirstOrDefault(e => e.Name.LocalName == "text");

            if (text is not null && Matches(entry, source, chapterHref))
            {
                yield return text;
            }
        }
    }

    /// <summary>删/增条目后重排 playOrder，否则 EPUB2 目录的顺序号会错乱（EpubCheck 会挑）。</summary>
    private static void ResequencePlayOrder(XDocument doc)
    {
        var order = 1;

        foreach (var point in doc.Descendants().Where(e => e.Name.LocalName == "navPoint"))
        {
            point.SetAttributeValue("playOrder", order.ToString(System.Globalization.CultureInfo.InvariantCulture));
            order++;
        }
    }

    // ================= 读写与路径 =================

    private static bool IsNcx(XDocument doc)
        => string.Equals(doc.Root?.Name.LocalName, "ncx", StringComparison.OrdinalIgnoreCase);

    private static int Edit(string workDir, string documentEntry, Func<XDocument, string, int> action)
    {
        var path = FullPath(workDir, documentEntry);
        var raw = File.ReadAllText(path);
        var doc = XDocument.Parse(raw, LoadOptions.PreserveWhitespace);
        var count = action(doc, documentEntry);

        if (count > 0)
        {
            WriteBackPreservingStyle(doc, path, raw);
        }

        return count;
    }

    /// <summary>把改过的 XML 写回去，并尽量逐字节保住原来的声明与换行风格（结构编辑器也用）。</summary>
    public static void WriteBackPreservingStyle(XDocument doc, string path, string raw)
    {
        var newLine = raw.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var declaration = ReadDeclaration(raw);

        var settings = new XmlWriterSettings
        {
            Encoding = Utf8NoBom,
            OmitXmlDeclaration = true,
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = newLine,
            Indent = false,
            CloseOutput = false,
        };

        AtomicFile.Write(path, stream =>
        {
            if (declaration is not null)
            {
                var header = Utf8NoBom.GetBytes(declaration + newLine);
                stream.Write(header, 0, header.Length);
            }

            using var writer = XmlWriter.Create(stream, settings);
            doc.Save(writer);
        });
    }

    private static string? ReadDeclaration(string raw)
    {
        var text = raw.TrimStart('\uFEFF');
        if (!text.StartsWith("<?xml", StringComparison.Ordinal))
        {
            return null;
        }

        var end = text.IndexOf("?>", StringComparison.Ordinal);
        return end < 0 ? null : text[..(end + 2)];
    }

    private static string FullPath(string workDir, string entry)
        => Path.Combine(workDir, entry.Replace('/', Path.DirectorySeparatorChar));

    private static bool Matches(string documentEntry, string? href, string chapterHref)
        => href is not null
           && string.Equals(Resolve(documentEntry, href), Collapse(chapterHref), StringComparison.OrdinalIgnoreCase);

    private static bool Matches(string documentEntry, string? href, HashSet<string> wanted)
        => href is not null && wanted.Contains(Resolve(documentEntry, href));

    /// <summary>把目录项里的相对 href 解析成包内路径（相对工作区根）。</summary>
    private static string Resolve(string documentEntry, string href)
    {
        var value = href.Trim();
        var cut = value.IndexOfAny(new[] { '#', '?' });
        if (cut >= 0)
        {
            value = value[..cut];
        }

        value = Uri.UnescapeDataString(value).Replace('\\', '/');

        if (value.StartsWith('/'))
        {
            return Collapse(value);
        }

        var separator = documentEntry.LastIndexOf('/');
        var baseDirectory = separator < 0 ? string.Empty : documentEntry[..separator];
        return Collapse(baseDirectory.Length == 0 ? value : baseDirectory + "/" + value);
    }

    /// <summary>算从 documentEntry 所在目录看过去的相对路径（写新条目、写 manifest href 都用）。</summary>
    public static string RelativeHref(string documentEntry, string chapterHref)
    {
        var separator = documentEntry.LastIndexOf('/');
        var baseDirectory = separator < 0 ? string.Empty : documentEntry[..separator];
        var target = Collapse(chapterHref);

        if (baseDirectory.Length == 0)
        {
            return target;
        }

        var baseParts = baseDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var targetParts = target.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var common = 0;

        while (common < baseParts.Length
               && common < targetParts.Length - 1
               && string.Equals(baseParts[common], targetParts[common], StringComparison.Ordinal))
        {
            common++;
        }

        var parts = new List<string>();

        for (var i = common; i < baseParts.Length; i++)
        {
            parts.Add("..");
        }

        parts.AddRange(targetParts.Skip(common));
        return string.Join('/', parts);
    }

    /// <summary>折叠 <c>.</c> / <c>..</c> / 重复斜杠，得到规范包内路径。</summary>
    private static string Collapse(string path)
    {
        var value = (path ?? string.Empty).Replace('\\', '/').Trim();
        var parts = new List<string>();

        foreach (var part in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        return string.Join('/', parts);
    }

    private static string IndentOf(XElement parent)
    {
        foreach (var node in parent.Nodes())
        {
            if (node is XText text && text.Value.Trim().Length == 0 && text.Value.Contains('\n'))
            {
                return text.Value;
            }
        }

        return string.Empty;
    }

    /// <summary>删元素时连它前面的缩进空白一起删，否则原地留一行空白。</summary>
    private static void RemoveWithIndent(XElement element)
    {
        if (element.PreviousNode is XText indent && indent.Value.Trim().Length == 0)
        {
            indent.Remove();
        }

        element.Remove();
    }
}
