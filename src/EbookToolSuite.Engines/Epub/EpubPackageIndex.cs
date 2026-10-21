using System.IO.Compression;
using System.Xml.Linq;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Engines.Epub;

/// <summary>manifest 里的一条资源。</summary>
public sealed record EpubManifestEntry(string Id, string Href, string MediaType, string Properties);

/// <summary>spine 里的一章（已解析到 manifest 的 href）。</summary>
public sealed record EpubSpineEntry(
    int Index,
    string IdRef,
    string SourceHref,
    string? Title,
    bool Linear);

/// <summary>
/// 只读的 OPF 索引：给出 spine 顺序、章节标题、样式表清单。
/// 规格见设计文档 8 章与执行计划 T1-5。
/// </summary>
/// <remarks>
/// 【与 LenientEpubReader 的关系】那个是导入路径的兜底读取器，只回计数，
/// 不吐明细。这里的形状不同（要 spine 顺序、要目录标题、要样式清单），
/// 所以单独一个类；两者的 XML 导航有少量重复，但导入路径已测试通过，
/// T1-5 不动它以免回归。将来若合并，以本类为基。
///
/// 【一律用 System.Xml.Linq】不用 HTML 解析器（AngleSharp 会小写化标签、
/// 改写空元素、丢命名空间），见 T-1 的决定 S8。
/// </remarks>
public sealed class EpubPackageIndex
{
    private const int MaxEntries = 20000;
    private const long MaxEntryBytes = 200L * 1024 * 1024;
    private const int MaxXmlBytes = 8 * 1024 * 1024;

    public required string OpfPath { get; init; }

    /// <summary>OPF 所在目录（包内路径，无前导斜杠）。空串表示在包根。</summary>
    public required string OpfDirectory { get; init; }

    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Language { get; init; }
    public string? Version { get; init; }

    public required IReadOnlyList<EpubManifestEntry> Manifest { get; init; }

    /// <summary>按 spine 顺序排列，Index 从 0 连续编号（跳过 linear="no" 的也保留，只在 UI 里可选处理）。</summary>
    public required IReadOnlyList<EpubSpineEntry> Spine { get; init; }

    /// <summary>全部 text/css 的包内路径。</summary>
    public required IReadOnlyList<string> Styles { get; init; }

    /// <summary>包内路径（已去 fragment）到章节标题的映射。取不到标题的章不在里面。</summary>
    public required IReadOnlyDictionary<string, string> Titles { get; init; }

    /// <summary>
    /// 目录树（T4-4）。原书的 NCX / nav 都解析不出条目时，
    /// 降级成"按 spine 顺序平铺"，此时 <see cref="TocIsFallback"/> 为 true。
    /// </summary>
    public IReadOnlyList<TocEntry> Toc { get; init; } = [];

    /// <summary>true = 目录是从 spine 平铺出来的（原书没有可用的目录）。</summary>
    public bool TocIsFallback { get; init; }

    /// <summary>把 OPF 里的相对 href 解析成包内路径。</summary>
    public string ResolveHref(string href)
    {
        var clean = href.Split('#')[0].Split('?')[0];
        if (clean.Length == 0)
        {
            return OpfDirectory;
        }

        var combined = OpfDirectory.Length == 0 ? clean : OpfDirectory + "/" + clean;
        return NormalizeEntryPath(Uri.UnescapeDataString(combined));
    }

    // ---------- 入口 ----------

    /// <summary>从文件读。失败返回 null 并给出原因。</summary>
    public static EpubPackageIndex? TryRead(string fullPath, out string? reason)
    {
        reason = null;

        try
        {
            using var fs = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            return TryRead(zip, out reason);
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>从已打开的包读。调用方负责 zip 的生命周期。</summary>
    public static EpubPackageIndex? TryRead(ZipArchive zip, out string? reason)
    {
        reason = null;

        try
        {
            if (zip.Entries.Count > MaxEntries)
            {
                reason = "包内条目数 " + zip.Entries.Count + " 超过上限 " + MaxEntries;
                return null;
            }

            var unsafeEntry = zip.Entries.FirstOrDefault(e => IsUnsafeEntryName(e.FullName));
            if (unsafeEntry is not null)
            {
                reason = "包内含不安全路径：" + unsafeEntry.FullName;
                return null;
            }

            var opfPath = ReadRootFilePath(zip);
            if (opfPath is null)
            {
                reason = "META-INF/container.xml 里找不到 rootfile";
                return null;
            }

            var opfEntry = FindEntry(zip, opfPath);
            if (opfEntry is null)
            {
                reason = "container.xml 指向的 OPF 不在包里：" + opfPath;
                return null;
            }

            var opf = TryLoadXml(opfEntry);
            if (opf is null)
            {
                reason = "OPF 不是合法 XML 或超过 " + MaxXmlBytes + " 字节：" + opfPath;
                return null;
            }

            var package = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "package");
            if (package is null)
            {
                reason = "OPF 里没有 package 元素";
                return null;
            }

            var opfDir = GetDirectory(NormalizeEntryPath(opfPath));
            var metadata = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");

            var manifest = opf.Descendants()
                .Where(e => e.Name.LocalName == "item")
                .Select(e => new EpubManifestEntry(
                    e.Attribute("id")?.Value ?? string.Empty,
                    e.Attribute("href")?.Value ?? string.Empty,
                    e.Attribute("media-type")?.Value ?? string.Empty,
                    e.Attribute("properties")?.Value ?? string.Empty))
                .Where(i => i.Href.Length > 0)
                .ToList();

            var byId = manifest
                .GroupBy(m => m.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var titles = BuildTitles(zip, opf, opfDir, manifest, byId);

            var spine = ApplyTitles(BuildSpine(opf, opfDir, byId), titles);
            var toc = BuildToc(zip, opf, opfDir, manifest, byId, spine);

            var index = new EpubPackageIndex
            {
                OpfPath = NormalizeEntryPath(opfPath),
                OpfDirectory = opfDir,
                Title = FirstText(metadata, "title"),
                Author = AllTexts(metadata, "creator") is { Count: > 0 } creators
                    ? string.Join(EpubProbe.AuthorSeparator, creators)
                    : null,
                Language = FirstText(metadata, "language"),
                Version = package.Attribute("version")?.Value,
                Manifest = manifest,
                Spine = spine,
                Styles = BuildStyles(manifest, opfDir),
                Titles = titles,
                Toc = toc.Entries,
                TocIsFallback = toc.IsFallback,
            };

            return index;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }
    // ---------- 构建 ----------

    private static List<EpubSpineEntry> BuildSpine(
        XDocument opf, string opfDirectory, Dictionary<string, EpubManifestEntry> byId)
    {
        var result = new List<EpubSpineEntry>();

        var spine = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "spine");
        if (spine is null)
        {
            return result;
        }

        var ordinal = 0;
        foreach (var itemref in spine.Elements().Where(e => e.Name.LocalName == "itemref"))
        {
            var idref = itemref.Attribute("idref")?.Value ?? string.Empty;
            if (!byId.TryGetValue(idref, out var item))
            {
                // spine 指向一个 manifest 里不存在的 id：跳过，不让整本读不出来
                continue;
            }

            var linear = !string.Equals(
                itemref.Attribute("linear")?.Value, "no", StringComparison.OrdinalIgnoreCase);

            result.Add(new EpubSpineEntry(
                ordinal,
                idref,
                ResolveHrefStatic(opfDirectory, item.Href),
                null,
                linear));

            ordinal++;
        }

        return result;
    }

    private static List<string> BuildStyles(
        List<EpubManifestEntry> manifest, string opfDirectory)
    {
        return manifest
            .Where(m => string.Equals(m.MediaType, "text/css", StringComparison.OrdinalIgnoreCase))
            .Select(m => ResolveHrefStatic(opfDirectory, m.Href))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>把章节标题贴回 spine。</summary>
    private static List<EpubSpineEntry> ApplyTitles(
        List<EpubSpineEntry> spine, Dictionary<string, string> titles)
    {
        if (titles.Count == 0)
        {
            return spine;
        }

        return spine
            .Select(s => titles.TryGetValue(s.SourceHref, out var title)
                ? s with { Title = title }
                : s)
            .ToList();
    }

    /// <summary>
    /// 目录标题。EPUB3 优先读 nav 文档，EPUB2 回退读 NCX。
    /// 两个都读不到就返回空表，UI 回落成"第 N 章"。
    /// </summary>
    private static Dictionary<string, string> BuildTitles(
        ZipArchive zip,
        XDocument opf,
        string opfDirectory,
        List<EpubManifestEntry> manifest,
        Dictionary<string, EpubManifestEntry> byId)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var nav = manifest.FirstOrDefault(m => m.Properties
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("nav", StringComparer.OrdinalIgnoreCase));

            if (nav is not null)
            {
                ReadNavTitles(zip, opfDirectory, nav.Href, titles);
            }

            if (titles.Count == 0)
            {
                ReadNcxTitles(zip, opf, opfDirectory, byId, titles);
            }
        }
        catch (Exception)
        {
            // 目录坏了不该让整本书打不开，静默降级成没标题
        }

        return titles;
    }

    /// <summary>EPUB3 nav：取第一个 epub:type="toc" 的 nav（没有就取第一个 nav）里的全部 a。</summary>
    private static void ReadNavTitles(
        ZipArchive zip, string opfDirectory, string navHref, Dictionary<string, string> titles)
    {
        var entry = FindEntry(zip, ResolveHrefStatic(opfDirectory, navHref));
        if (entry is null)
        {
            return;
        }

        var doc = TryLoadXml(entry);
        if (doc is null)
        {
            return;
        }

        var navs = doc.Descendants()
            .Where(e => e.Name.LocalName == "nav")
            .ToList();

        var toc = navs.FirstOrDefault(n => n.Attributes()
            .Any(a => a.Name.LocalName == "type"
                && a.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("toc", StringComparer.OrdinalIgnoreCase)));

        toc ??= navs.FirstOrDefault();
        if (toc is null)
        {
            return;
        }

        var navDirectory = GetDirectory(ResolveHrefStatic(opfDirectory, navHref));

        foreach (var anchor in toc.Descendants().Where(e => e.Name.LocalName == "a"))
        {
            var href = anchor.Attribute("href")?.Value;
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var text = Collapse(anchor.Value);
            if (text.Length == 0)
            {
                continue;
            }

            var key = ResolveHrefStatic(navDirectory, href);
            if (key.Length > 0)
            {
                titles.TryAdd(key, text);
            }
        }
    }

    /// <summary>EPUB2 NCX：spine@toc 指向的 navMap 里的 navPoint，递归。</summary>
    private static void ReadNcxTitles(
        ZipArchive zip,
        XDocument opf,
        string opfDirectory,
        Dictionary<string, EpubManifestEntry> byId,
        Dictionary<string, string> titles)
    {
        var tocId = opf.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "spine")
            ?.Attribute("toc")?.Value;

        if (string.IsNullOrWhiteSpace(tocId) || !byId.TryGetValue(tocId, out var ncx))
        {
            return;
        }

        var entry = FindEntry(zip, ResolveHrefStatic(opfDirectory, ncx.Href));
        if (entry is null)
        {
            return;
        }

        var doc = TryLoadXml(entry);
        if (doc is null)
        {
            return;
        }

        var ncxDirectory = GetDirectory(ResolveHrefStatic(opfDirectory, ncx.Href));

        foreach (var navPoint in doc.Descendants().Where(e => e.Name.LocalName == "navPoint"))
        {
            var label = navPoint.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "navLabel")?
                .Elements()
                .FirstOrDefault(e => e.Name.LocalName == "text")?
                .Value;

            var src = navPoint.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "content")?
                .Attribute("src")?.Value;

            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(src))
            {
                continue;
            }

            var key = ResolveHrefStatic(ncxDirectory, src);
            if (key.Length > 0)
            {
                titles.TryAdd(key, Collapse(label));
            }
        }
    }


    // ---------- 目录（T4-4）----------

    /// <summary>
    /// 目录树：EPUB3 读 nav（nav[epub:type=toc] 里的嵌套 ol），EPUB2 读 NCX 的 navMap（嵌套 navPoint）。
    /// 解析不出任何"能跳的"条目时，降级成按 spine 顺序平铺。
    /// </summary>
    /// <remarks>
    /// 【与 BuildTitles 的分工】那个只求"每章标题"（平铺、给章节起名用），
    /// 这个要层级、要 fragment、要能跳。两者都解析 NCX/nav 但用途不同，所以各自独立：
    /// BuildTitles 的行为已被 T1-5 的测试与 30 本真实样本锁住，不动它以免回归。
    /// </remarks>
    private static (List<TocEntry> Entries, bool IsFallback) BuildToc(
        ZipArchive zip,
        XDocument opf,
        string opfDirectory,
        List<EpubManifestEntry> manifest,
        Dictionary<string, EpubManifestEntry> byId,
        IReadOnlyList<EpubSpineEntry> spine)
    {
        var byHref = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var chapter in spine)
        {
            byHref.TryAdd(NormalizeEntryPath(chapter.SourceHref), chapter.Index);
        }

        List<TocEntry> entries = [];

        try
        {
            var nav = manifest.FirstOrDefault(m => m.Properties
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("nav", StringComparer.OrdinalIgnoreCase));

            if (nav is not null)
            {
                entries = ReadNavToc(zip, opfDirectory, nav.Href, byHref);
            }

            if (entries.Count == 0)
            {
                entries = ReadNcxToc(zip, opf, opfDirectory, byId, byHref);
            }
        }
        catch (Exception)
        {
            // 目录坏了不该让整本书打不开（与 BuildTitles 同一口径）
            entries = [];
        }

        return entries.Count > 0
            ? (entries, false)
            : (BuildTocFromSpine(spine), true);
    }

    /// <summary>EPUB3 nav：取第一个 epub:type="toc" 的 nav（没有就取第一个 nav），按 ol/li 递归。</summary>
    private static List<TocEntry> ReadNavToc(
        ZipArchive zip, string opfDirectory, string navHref, Dictionary<string, int> byHref)
    {
        var entry = FindEntry(zip, ResolveHrefStatic(opfDirectory, navHref));
        var doc = entry is null ? null : TryLoadXml(entry);

        if (doc is null)
        {
            return [];
        }

        var navs = doc.Descendants()
            .Where(e => e.Name.LocalName == "nav")
            .ToList();

        var toc = navs.FirstOrDefault(n => n.Attributes()
            .Any(a => a.Name.LocalName == "type"
                && a.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("toc", StringComparer.OrdinalIgnoreCase)));

        toc ??= navs.FirstOrDefault();

        if (toc is null)
        {
            return [];
        }

        // 标准写法是 nav > ol；也有书把 ol 包在 div 里，所以退一步找后代
        var list = toc.Elements().FirstOrDefault(e => e.Name.LocalName == "ol")
            ?? toc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ol");

        if (list is null)
        {
            return [];
        }

        var navDirectory = GetDirectory(ResolveHrefStatic(opfDirectory, navHref));

        return WalkNavList(list, navDirectory, 1, byHref);
    }

    private static List<TocEntry> WalkNavList(
        XElement list, string directory, int level, Dictionary<string, int> byHref)
    {
        var result = new List<TocEntry>();

        foreach (var item in list.Elements().Where(e => e.Name.LocalName == "li"))
        {
            var childList = item.Elements().FirstOrDefault(e => e.Name.LocalName == "ol");

            var children = childList is null
                ? []
                : WalkNavList(childList, directory, NextLevel(level), byHref);

            var anchor = item.Elements().FirstOrDefault(e => e.Name.LocalName == "a");

            // span 是"分组标题"（部/卷之类），本身没有 href
            var group = anchor is null
                ? item.Elements().FirstOrDefault(e => e.Name.LocalName == "span")
                : null;

            var label = Collapse(anchor?.Value ?? group?.Value ?? string.Empty);
            var href = anchor?.Attribute("href")?.Value;

            if (href is null || label.Length == 0)
            {
                // 分组标题保留成不可跳转的节点（子目录要留着），纯空节点则拍掉
                if (label.Length > 0 && children.Count > 0)
                {
                    result.Add(new TocEntry
                    {
                        Title = label,
                        Level = level,
                        SpineIndex = -1,
                        Children = children,
                    });
                }
                else
                {
                    result.AddRange(Promote(children, level));
                }

                continue;
            }

            var key = ResolveHrefStatic(directory, href);

            if (key.Length == 0 || !byHref.TryGetValue(key, out var spineIndex))
            {
                // 指向不在 spine 里的文档（封面页、版权页、注释页很常见）：
                // 这一条跳不了，但子目录可能跳得了，提上来别丢。
                result.AddRange(Promote(children, level));
                continue;
            }

            result.Add(new TocEntry
            {
                Title = label,
                Level = level,
                SpineIndex = spineIndex,
                Fragment = FragmentOf(href),
                SourceHref = key,
                Children = children,
            });
        }

        return result;
    }

    /// <summary>EPUB2 NCX：spine@toc 指向的 ncx 里的 navMap，按 navPoint 递归。</summary>
    private static List<TocEntry> ReadNcxToc(
        ZipArchive zip,
        XDocument opf,
        string opfDirectory,
        Dictionary<string, EpubManifestEntry> byId,
        Dictionary<string, int> byHref)
    {
        var tocId = opf.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "spine")
            ?.Attribute("toc")?.Value;

        EpubManifestEntry? ncx = null;

        if (!string.IsNullOrWhiteSpace(tocId) && byId.TryGetValue(tocId, out var declared))
        {
            ncx = declared;
        }

        // spine@toc 缺失或指向不存在的 id 时，退一步按 media-type 找（真实电子书里很常见）
        ncx ??= byId.Values.FirstOrDefault(m => string.Equals(
            m.MediaType, "application/x-dtbncx+xml", StringComparison.OrdinalIgnoreCase));

        if (ncx is null)
        {
            return [];
        }

        var entry = FindEntry(zip, ResolveHrefStatic(opfDirectory, ncx.Href));
        var doc = entry is null ? null : TryLoadXml(entry);

        if (doc is null)
        {
            return [];
        }

        var navMap = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "navMap");

        if (navMap is null)
        {
            return [];
        }

        var ncxDirectory = GetDirectory(ResolveHrefStatic(opfDirectory, ncx.Href));

        return WalkNavPoints(navMap, ncxDirectory, 1, byHref);
    }

    private static List<TocEntry> WalkNavPoints(
        XElement parent, string directory, int level, Dictionary<string, int> byHref)
    {
        var result = new List<TocEntry>();

        foreach (var navPoint in parent.Elements().Where(e => e.Name.LocalName == "navPoint"))
        {
            var children = WalkNavPoints(navPoint, directory, NextLevel(level), byHref);

            var label = Collapse(navPoint.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "navLabel")?
                .Elements()
                .FirstOrDefault(e => e.Name.LocalName == "text")?
                .Value ?? string.Empty);

            var src = navPoint.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "content")?
                .Attribute("src")?.Value;

            if (src is null || label.Length == 0)
            {
                result.AddRange(Promote(children, level));
                continue;
            }

            var key = ResolveHrefStatic(directory, src);

            if (key.Length == 0 || !byHref.TryGetValue(key, out var spineIndex))
            {
                result.AddRange(Promote(children, level));
                continue;
            }

            result.Add(new TocEntry
            {
                Title = label,
                Level = level,
                SpineIndex = spineIndex,
                Fragment = FragmentOf(src),
                SourceHref = key,
                Children = children,
            });
        }

        return result;
    }

    /// <summary>目录不可用时的降级：按 spine 顺序平铺，标题取章节标题，取不到就用文件名。</summary>
    private static List<TocEntry> BuildTocFromSpine(IReadOnlyList<EpubSpineEntry> spine)
        => spine
            .Select(chapter => new TocEntry
            {
                Title = string.IsNullOrWhiteSpace(chapter.Title)
                    ? FileNameOf(chapter.SourceHref)
                    : chapter.Title!,
                Level = 1,
                SpineIndex = chapter.Index,
                SourceHref = chapter.SourceHref,
            })
            .ToList();

    private static int NextLevel(int level) => level >= 3 ? 3 : level + 1;

    /// <summary>
    /// 父项被丢掉（跳不了）时，把它的子目录提上来接替它的层级，
    /// 否则顶层会出现一级"第 2 层"的缩进错误。
    /// </summary>
    private static List<TocEntry> Promote(List<TocEntry> children, int level)
    {
        foreach (var child in children)
        {
            child.Level = level;
            Promote(child.Children, NextLevel(level));
        }

        return children;
    }

    /// <summary>取 href 里 # 之后那截并做 URL 解码；没有或为空返回 null。</summary>
    private static string? FragmentOf(string href)
    {
        var hash = href.IndexOf('#');

        if (hash < 0)
        {
            return null;
        }

        var fragment = Uri.UnescapeDataString(href[(hash + 1)..]);

        return fragment.Length == 0 ? null : fragment;
    }

    private static string FileNameOf(string path)
    {
        var slash = path.LastIndexOf('/');
        var name = slash < 0 ? path : path[(slash + 1)..];

        return name.Length == 0 ? path : name;
    }

    // ---------- zip / xml 工具 ----------

    private static string? ReadRootFilePath(ZipArchive zip)
    {
        var container = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, "META-INF/container.xml", StringComparison.OrdinalIgnoreCase));

        if (container is null)
        {
            return null;
        }

        var rootFile = TryLoadXml(container)?
            .Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile");

        var path = rootFile?.Attribute("full-path")?.Value;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static XDocument? TryLoadXml(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxXmlBytes)
        {
            return null;
        }

        try
        {
            using var stream = entry.Open();
            return XDocument.Load(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string path)
    {
        var wanted = NormalizeEntryPath(path);
        return zip.Entries.FirstOrDefault(e =>
            string.Equals(NormalizeEntryPath(e.FullName), wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FirstText(XElement? parent, string localName)
    {
        var text = parent?
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == localName)?
            .Value?.Trim();

        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static List<string> AllTexts(XElement? parent, string localName)
    {
        if (parent is null)
        {
            return [];
        }

        return parent.Descendants()
            .Where(e => e.Name.LocalName == localName)
            .Select(e => e.Value.Trim())
            .Where(v => v.Length > 0)
            .ToList();
    }

    private static string ResolveHrefStatic(string baseDirectory, string href)
    {
        var clean = href.Split('#')[0].Split('?')[0];
        if (clean.Length == 0)
        {
            return baseDirectory;
        }

        var combined = baseDirectory.Length == 0 ? clean : baseDirectory + "/" + clean;
        return NormalizeEntryPath(Uri.UnescapeDataString(combined));
    }

    public static string GetDirectory(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    public static string NormalizeEntryPath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    /// <summary>Zip Slip 与盘符路径一律拒绝。</summary>
    /// <remarks>
    /// 按"路径段"判断越界，而不是简单查 <c>..</c> 子串：
    /// <c>chapter..1.xhtml</c> 是正常文件名，<c>../evil</c> 才是越界。
    /// </remarks>
    public static bool IsUnsafeEntryName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name.StartsWith('/') || name.StartsWith('\\'))
        {
            return true;
        }

        var normalized = NormalizeEntryPath(name);

        if (normalized.Contains(':', StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment == "..")
            {
                return true;
            }
        }

        return false;
    }

    private static string Collapse(string text)
        => string.Join(' ', text.Split(
            [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
}