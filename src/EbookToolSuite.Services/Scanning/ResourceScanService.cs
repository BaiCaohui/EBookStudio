using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Epub;

namespace EbookToolSuite.Services.Scanning;

/// <summary>
/// 资源引用扫描（执行计划 T5-5，设计文档 6.1.7）。
/// <para>
/// 扫两类问题：**悬空引用**（引用了不存在的文件，能定位到来源文件与行）与
/// **孤儿资源**（在 OPF manifest 里但没有任何内容引用它）。
/// </para>
/// <para>
/// 引用来源：XHTML 用 XML 解析（img/link/内部 a，另含 image/source/audio/video 这些常见标签），
/// CSS 用正则（url(...) 与 @import）。相对路径一律**相对引用者所在目录**解析（判据 3）。
/// </para>
/// </summary>
public sealed class ResourceScanService : IResourceScanService
{
    private static readonly Regex CssUrl = new(
        @"url\(\s*['""]?([^'"")]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CssImport = new(
        @"@import\s+(?:url\(\s*['""]?([^'"")]+)|['""]([^'""]+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>外部协议 / 不是"包内相对路径"的都跳过（判据 5：data: URI 不误报）。</summary>
    private static readonly string[] ExternalPrefixes =
    [
        "data:", "http:", "https:", "//", "mailto:", "tel:", "javascript:", "blob:", "about:", "file:", "ftp:"
    ];

    private readonly IChapterSourceService? _source;
    private readonly IWorkspaceSnapshotService? _snapshots;

    public ResourceScanService(IChapterSourceService? source = null, IWorkspaceSnapshotService? snapshots = null)
    {
        _source = source;
        _snapshots = snapshots;
    }

    public Task<ResourceScanReport> ScanAsync(IEpubWorkspace workspace, CancellationToken ct = default)
        => Task.Run(() => Scan(workspace, ct), ct);

    private static ResourceScanReport Scan(IEpubWorkspace workspace, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var workDir = Path.GetFullPath(workspace.WorkDir);
        var opfEntry = EpubPackageIndex.NormalizeEntryPath(workspace.Session.OpfPath ?? string.Empty);

        if (string.IsNullOrWhiteSpace(opfEntry))
        {
            throw new AppException(ErrorCode.ValidationFailed, "这个工作区没有 OPF 路径，资源扫描中止。");
        }
        var opfFull = Path.Combine(workDir, opfEntry.Replace('/', Path.DirectorySeparatorChar));
        var opf = ReadOpf(opfFull, workDir);

        /* manifest 里能落到盘上的条目：后面判断"存在/孤儿"都以它为准。 */
        var items = new List<(string Id, string Entry, string MediaType, string Properties)>();

        foreach (var item in opf.Items)
        {
            var entry = Resolve(workDir, opf.Directory + "/x", item.Href);

            if (entry is not null && !entry.StartsWith("..", StringComparison.Ordinal))
            {
                items.Add((item.Id, entry, item.MediaType, item.Properties));
            }
        }

        var targets = items
            .Where(item => IsXhtml(item.MediaType, item.Entry) || IsCss(item.MediaType, item.Entry))
            .ToList();

        var references = new ConcurrentBag<ResourceReference>();
        var scanned = 0;

        Parallel.ForEach(
            targets,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
                CancellationToken = ct,
            },
            item =>
            {
                var full = Path.Combine(workDir, item.Entry.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(full))
                {
                    return;
                }

                Interlocked.Increment(ref scanned);

                if (IsCss(item.MediaType, item.Entry))
                {
                    ScanCss(workDir, item.Entry, full, references);
                }
                else
                {
                    ScanXhtml(workDir, item.Entry, full, references);
                }
            });

        /* @import url(...) 会被 @import 与 url(...) 两个正则各命中一次：按（来源+目标+行）去重，
           免得同一个问题报两遍（测试实测过）。 */
        var list = references
            .DistinctBy(reference => (reference.SourceEntry, reference.ResolvedEntry, reference.Line))
            .ToList();
        var dangling = new List<ResourceIssue>();

        foreach (var reference in list)
        {
            var target = Path.Combine(workDir, reference.ResolvedEntry.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(target))
            {
                dangling.Add(new ResourceIssue(
                    ResourceIssueKinds.Dangling,
                    reference.ResolvedEntry,
                    reference.SourceEntry,
                    reference.Line,
                    reference.Snippet));
            }
        }

        var orphans = FindOrphans(workDir, opf, items, list);
        var comparer = StringComparer.OrdinalIgnoreCase;

        return new ResourceScanReport(
            dangling.OrderBy(issue => issue.SourceEntry, comparer).ThenBy(issue => issue.Line ?? 0).ToList(),
            orphans.OrderBy(issue => issue.Entry, comparer).ToList(),
            scanned,
            list.Count,
            Math.Round(watch.Elapsed.TotalMilliseconds, 1));
    }
    /// <summary>包内相对路径解析：一律相对"引用者所在目录"（判据 3）；外部协议/锚点返回 null。</summary>
    private static string? Resolve(string workDir, string sourceEntry, string raw)
    {
        var target = (raw ?? string.Empty).Trim();

        if (target.Length == 0)
        {
            return null;
        }

        var hash = target.IndexOf('#');

        if (hash >= 0)
        {
            target = target[..hash];
        }

        var query = target.IndexOf('?');

        if (query >= 0)
        {
            target = target[..query];
        }

        target = target.Trim();

        if (target.Length == 0)
        {
            return null;
        }

        foreach (var prefix in ExternalPrefixes)
        {
            if (target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        try
        {
            target = Uri.UnescapeDataString(target);
        }
        catch (UriFormatException)
        {
            // 保持原样
        }

        var sourceDir = Path.GetDirectoryName(sourceEntry.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        string full;

        try
        {
            full = Path.GetFullPath(Path.Combine(workDir, sourceDir, target.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception)
        {
            return null;
        }

        var root = Path.GetFullPath(workDir);

        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(root, full).Replace('\\', '/')
            : "<outside>";
    }

    private static bool IsXhtml(string mediaType, string entry)
        => mediaType.Contains("xhtml", StringComparison.OrdinalIgnoreCase)
            || entry.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase)
            || entry.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || entry.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

    private static bool IsCss(string mediaType, string entry)
        => mediaType.Contains("css", StringComparison.OrdinalIgnoreCase)
            || entry.EndsWith(".css", StringComparison.OrdinalIgnoreCase);

    /// <summary>规格要求 img src / link href / 内部 a href；另外把常见多媒体与 SVG 图片也认上。</summary>
    private static string[] AttributesOf(string localName) => localName switch
    {
        "img" => ["src"],
        "link" => ["href"],
        "a" => ["href"],
        "image" => ["xlink:href", "href"],
        "source" => ["src"],
        "audio" => ["src"],
        "video" => ["src", "poster"],
        "iframe" => ["src"],
        _ => [],
    };

    private sealed record ResourceReference(string SourceEntry, string Raw, string ResolvedEntry, int? Line, string? Snippet);

    private static void ScanXhtml(string workDir, string entry, string full, ConcurrentBag<ResourceReference> sink)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreComments = true,
            };

            using var stream = File.OpenRead(full);
            using var reader = XmlReader.Create(stream, settings);
            var document = XDocument.Load(reader, LoadOptions.SetLineInfo);

            foreach (var element in document.Descendants())
            {
                var name = element.Name.LocalName.ToLowerInvariant();
                var wanted = AttributesOf(name);

                if (wanted.Length == 0)
                {
                    continue;
                }

                foreach (var attribute in element.Attributes())
                {
                    var attributeName = attribute.Name.LocalName.ToLowerInvariant();
                    var match = Array.IndexOf(wanted, attributeName) >= 0
                        || (attributeName == "href" && Array.IndexOf(wanted, "xlink:href") >= 0);

                    if (!match)
                    {
                        continue;
                    }

                    var resolved = Resolve(workDir, entry, attribute.Value);

                    if (resolved is null)
                    {
                        continue;
                    }

                    var line = element is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : (int?)null;
                    sink.Add(new ResourceReference(entry, attribute.Value, resolved, line, name + "@" + attributeName));
                }
                /* 设计文档 6.1.7 第 1 步：行内 style 里的 url(...) 也要算引用。 */
                var styleValue = element.Attribute("style")?.Value;

                if (!string.IsNullOrEmpty(styleValue))
                {
                    foreach (Match styleMatch in CssUrl.Matches(styleValue))
                    {
                        var rawStyle = styleMatch.Groups[1].Success ? styleMatch.Groups[1].Value : string.Empty;
                        var resolvedStyle = Resolve(workDir, entry, rawStyle);

                        if (resolvedStyle is null)
                        {
                            continue;
                        }

                        var styleLine = element is IXmlLineInfo styleInfo && styleInfo.HasLineInfo()
                            ? styleInfo.LineNumber
                            : (int?)null;
                        sink.Add(new ResourceReference(entry, rawStyle, resolvedStyle, styleLine, "style:url"));
                    }
                }
            }
        }
        catch (XmlException)
        {
            // 不是良构 XML：这章的引用扫不了（保存链路的良构校验会拦，这里不误报）
        }
        catch (IOException)
        {
            // 读不了就算了
        }
    }

    private static void ScanCss(string workDir, string entry, string full, ConcurrentBag<ResourceReference> sink)
    {
        string text;

        try
        {
            text = File.ReadAllText(full);
        }
        catch (IOException)
        {
            return;
        }

        Collect(CssUrl, text);
        Collect(CssImport, text);

        void Collect(Regex regex, string body)
        {
            foreach (Match match in regex.Matches(body))
            {
                var raw = match.Groups.Count > 1 && match.Groups[1].Success
                    ? match.Groups[1].Value
                    : (match.Groups.Count > 2 ? match.Groups[2].Value : string.Empty);
                var resolved = Resolve(workDir, entry, raw);

                if (resolved is null)
                {
                    continue;
                }

                var line = 1 + body.Take(match.Index).Count(character => character == '\n');
                sink.Add(new ResourceReference(entry, raw, resolved, line, match.Value.Trim()));
            }
        }
    }

    /// <summary>
    /// 孤儿资源 = manifest 里"没有任何内容引用"的文件。
    /// 结构性引用要排除掉，否则会误报：spine 的章节、nav / NCX、封面（cover-image 属性或 OPF 的 cover meta）。
    /// </summary>
    private static List<ResourceIssue> FindOrphans(
        string workDir,
        OpfIndex opf,
        List<(string Id, string Entry, string MediaType, string Properties)> items,
        List<ResourceReference> references)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var referenced = new HashSet<string>(comparer)
        {
            opf.Entry,
        };

        foreach (var reference in references)
        {
            referenced.Add(reference.ResolvedEntry);
        }

        var spineSource = opf.Directory + "/x";

        foreach (var spineHref in opf.Spine)
        {
            var entry = Resolve(workDir, spineSource, spineHref);

            if (entry is not null)
            {
                referenced.Add(entry);
            }
        }

        var coverId = opf.CoverId;

        foreach (var item in items)
        {
            if (item.Properties.Contains("nav", StringComparison.OrdinalIgnoreCase)
                || item.Properties.Contains("cover-image", StringComparison.OrdinalIgnoreCase)
                || item.MediaType.Contains("dtbncx", StringComparison.OrdinalIgnoreCase)
                || (coverId is not null && item.Id.Equals(coverId, StringComparison.OrdinalIgnoreCase)))
            {
                referenced.Add(item.Entry);
            }
        }

        var orphans = new List<ResourceIssue>();

        foreach (var item in items)
        {
            if (!referenced.Contains(item.Entry))
            {
                long? size = null;
                var orphanFull = Path.Combine(workDir, item.Entry.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(orphanFull))
                {
                    size = new FileInfo(orphanFull).Length;
                }

                orphans.Add(new ResourceIssue(ResourceIssueKinds.Orphan, item.Entry, SizeBytes: size));
            }
        }

        return orphans;
    }

    /// <summary>读 OPF 里的 &lt;meta name="cover" content="ID"&gt;（EPUB2 的封面写法），拿不到返回 null。</summary>
    private static string? ReadCoverMetaId(string workDir, string opfPath)
    {
        try
        {
            var full = Path.Combine(workDir, opfPath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(full))
            {
                return null;
            }

            var document = XDocument.Load(full);

            foreach (var meta in document.Descendants().Where(element => element.Name.LocalName == "meta"))
            {
                var name = meta.Attribute("name")?.Value;

                if (string.Equals(name, "cover", StringComparison.OrdinalIgnoreCase))
                {
                    return meta.Attribute("content")?.Value;
                }
            }
        }
        catch (XmlException)
        {
            // 读不了就当没有
        }
        catch (IOException)
        {
            // 同上
        }

        return null;
    }
    private sealed record OpfIndex(
        string Entry,
        string Directory,
        IReadOnlyList<(string Id, string Href, string MediaType, string Properties)> Items,
        IReadOnlyList<string> Spine,
        string? CoverId);

    /// <summary>直接解析工作区里的 OPF（EpubPackageIndex 只吃 .epub 文件，工作区这条路要自己读）。</summary>
    private static OpfIndex ReadOpf(string opfFull, string workDir)
    {
        if (!File.Exists(opfFull))
        {
            throw new AppException(ErrorCode.ValidationFailed, "OPF 不存在，资源扫描中止：" + opfFull);
        }

        XDocument document;

        try
        {
            document = XDocument.Load(opfFull, LoadOptions.None);
        }
        catch (Exception error) when (error is XmlException or IOException)
        {
            throw new AppException(
                ErrorCode.ValidationFailed,
                "OPF 解析失败，资源扫描中止：" + error.SafeMessage(),
                technical: opfFull);
        }

        var items = new List<(string Id, string Href, string MediaType, string Properties)>();

        foreach (var element in document.Descendants().Where(node => node.Name.LocalName == "item"))
        {
            var href = element.Attribute("href")?.Value;

            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            items.Add((
                element.Attribute("id")?.Value ?? href,
                href,
                element.Attribute("media-type")?.Value ?? string.Empty,
                element.Attribute("properties")?.Value ?? string.Empty));
        }

        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            byId[item.Id] = item.Href;
        }

        var spine = new List<string>();

        foreach (var element in document.Descendants().Where(node => node.Name.LocalName == "itemref"))
        {
            var idref = element.Attribute("idref")?.Value;

            if (idref is not null && byId.TryGetValue(idref, out var href))
            {
                spine.Add(href);
            }
        }

        string? coverId = null;

        foreach (var meta in document.Descendants().Where(node => node.Name.LocalName == "meta"))
        {
            if (string.Equals(meta.Attribute("name")?.Value, "cover", StringComparison.OrdinalIgnoreCase))
            {
                coverId = meta.Attribute("content")?.Value;
                break;
            }
        }

        var entry = Path.GetRelativePath(Path.GetFullPath(workDir), Path.GetFullPath(opfFull)).Replace('\\', '/');

        return new OpfIndex(
            entry, Path.GetDirectoryName(entry)?.Replace('\\', '/') ?? string.Empty, items, spine, coverId);
    }

    /// <summary>移除一条悬空引用：只在命中的那一行里把引用属性摘掉，不重排整个文件。</summary>
    public Task<ResourceScanAction> RemoveReferenceAsync(
        IEpubWorkspace workspace, ResourceIssue issue, CancellationToken ct = default)
        => Task.Run(() => RemoveReference(workspace, issue), ct);

    private ResourceScanAction RemoveReference(IEpubWorkspace workspace, ResourceIssue issue)
    {
        if (!string.Equals(issue.Kind, ResourceIssueKinds.Dangling, StringComparison.Ordinal))
        {
            return new ResourceScanAction(false, "这条不是悬空引用，不能移除。");
        }

        var entry = issue.SourceEntry;

        if (string.IsNullOrWhiteSpace(entry) || issue.Line is not int line)
        {
            return new ResourceScanAction(false, "这条悬空引用没有来源文件或行号，无法安全移除。");
        }

        if (!IsXhtml(string.Empty, entry))
        {
            return new ResourceScanAction(false, "只支持从 XHTML 里移除引用；CSS 里的 url(...) 请手工改。");
        }

        if (_source is null)
        {
            return new ResourceScanAction(false, "没有注入 IChapterSourceService，不能写盘。");
        }

        var full = Path.Combine(workspace.WorkDir, entry.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(full))
        {
            return new ResourceScanAction(false, "源文件不存在：" + entry);
        }

        var text = File.ReadAllText(full);
        var lines = text.Replace("\r\n", "\n").Split('\n');

        if (line < 1 || line > lines.Length)
        {
            return new ResourceScanAction(false, "行号越界：" + line);
        }

        var replaced = false;

        var updated = Regex.Replace(
            lines[line - 1],
            "(?<attr>\\s(?:src|href)\\s*=\\s*(?:\"(?<dq>[^\"]*)\"|'(?<sq>[^']*)'))",
            match =>
            {
                var value = match.Groups["dq"].Success ? match.Groups["dq"].Value : match.Groups["sq"].Value;
                var resolved = Resolve(workspace.WorkDir, entry!, value);

                if (resolved is null || !string.Equals(resolved, issue.Entry, StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }

                replaced = true;
                return string.Empty;
            },
            RegexOptions.IgnoreCase);

        if (!replaced)
        {
            return new ResourceScanAction(
                false, "第 " + line + " 行没找到指向 " + issue.Entry + " 的引用；该行原文：[" + lines[line - 1] + "]"
                    + "（匹配结果：replaced=" + replaced + "）");
        }

        lines[line - 1] = updated;
        var newText = string.Join("\n", lines);

        if (text.Contains("\r\n", StringComparison.Ordinal))
        {
            newText = newText.Replace("\n", "\r\n");
        }

        var check = _source.Save(workspace, entry, newText);

        return check.Ok
            ? new ResourceScanAction(true, "已移除引用：" + issue.Entry + "（" + entry + " 第 " + line + " 行）")
            : new ResourceScanAction(false, "改完没通过良构校验，已放弃：" + check.Summary);
    }

    /// <summary>删除孤儿资源：白名单目录 + 先建快照 + 移到 .deleted/ + 摘掉 manifest 条目。</summary>
    public Task<ResourceScanAction> DeleteOrphanAsync(
        IEpubWorkspace workspace, ResourceIssue issue, CancellationToken ct = default)
        => Task.Run(() => DeleteOrphanAsyncCore(workspace, issue, ct), ct);

    private async Task<ResourceScanAction> DeleteOrphanAsyncCore(
        IEpubWorkspace workspace, ResourceIssue issue, CancellationToken ct)
    {
        if (!string.Equals(issue.Kind, ResourceIssueKinds.Orphan, StringComparison.Ordinal))
        {
            return new ResourceScanAction(false, "这条不是孤儿资源。");
        }

        var entry = issue.Entry;
        var workDir = Path.GetFullPath(workspace.WorkDir);
        var opfEntry = EpubPackageIndex.NormalizeEntryPath(workspace.Session.OpfPath ?? string.Empty);
        var opfDirectory = string.IsNullOrWhiteSpace(opfEntry) ? string.Empty : Path.GetDirectoryName(opfEntry)?.Replace('\\', '/') ?? string.Empty;
        var prefix = opfDirectory.Length == 0 ? string.Empty : opfDirectory + "/";
        var relative = entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? entry[prefix.Length..] : entry;

        /* 安全约束（设计文档 6.1.7）：只允许删这几类目录下的非结构性文件。 */
        var allowed = new[] { "Images/", "Fonts/", "Styles/", "Text/" };

        if (!allowed.Any(item => relative.StartsWith(item, StringComparison.OrdinalIgnoreCase)))
        {
            return new ResourceScanAction(
                false, "按安全约束只允许删除 Images/Fonts/Styles/Text 下的资源：" + entry);
        }

        var full = Path.Combine(workDir, entry.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(full))
        {
            return new ResourceScanAction(false, "文件已经不在：" + entry);
        }

        /* 删前先建快照（T5-4 的 delete-before），一键反悔用得上。 */
        if (_snapshots is not null)
        {
            try
            {
                await _snapshots
                    .TakeIncrementalAsync(
                        workspace,
                        new[] { entry },
                        WorkspaceSnapshotKinds.DeleteBefore,
                        "删除孤儿资源：" + entry,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception error)
            {
                return new ResourceScanAction(false, "建删除前快照失败，已放弃删除：" + error.SafeMessage());
            }
        }

        var deletedDir = Path.Combine(workDir, EpubStructureEditor.DeletedDirectoryName);
        Directory.CreateDirectory(deletedDir);
        var destination = Path.Combine(
            deletedDir,
            DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + Path.GetFileName(entry));
        File.Move(full, destination, overwrite: true);

        var removedFromManifest = false;

        if (!string.IsNullOrWhiteSpace(opfEntry))
        {
            var editor = new OpfMetadataEditor(Path.Combine(workDir, opfEntry.Replace('/', Path.DirectorySeparatorChar)));
            var item = editor.ReadManifest().FirstOrDefault(candidate =>
                string.Equals(
                    Resolve(workDir, opfDirectory + "/x", candidate.Href),
                    entry,
                    StringComparison.OrdinalIgnoreCase));

            if (item is not null && editor.RemoveManifestItem(item.Id))
            {
                /* 只改内存里的 XDocument，必须显式 Save() 才落盘（OpfManifestRepair 第 111/195 行就是这么做的）。 */
                editor.Save();
                removedFromManifest = true;
            }
        }

        return new ResourceScanAction(
            true,
            "已删除孤儿资源：" + entry
                + (removedFromManifest ? "（manifest 条目已摘掉，文件移到 .deleted/）" : "（文件移到 .deleted/；manifest 里没找到对应条目）"));
    }
}
