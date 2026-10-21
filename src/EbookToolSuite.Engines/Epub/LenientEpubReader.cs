using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Engines.Epub;

/// <summary>
/// 不依赖 VersOne 的最小 EPUB 读取器，只在 VersOne 严格解析失败时当兜底用。
/// 规格见设计文档 6.1.9。
/// </summary>
/// <remarks>
/// 【为什么需要它】真实样本里"违反规范但各家阅读器都能打开"的书写很常见：
/// spine 缺 toc、manifest 里 href 重复、正文写成 media-type="text/html"。
/// VersOne 一律拒收，正常书就进不了库（2026-10-02 实测 28 个文件被拒 3 个）。
///
/// 【兜底不等于不校验】Zip Slip、条目数上限、单条目大小上限在这里照样跑。
/// 一律用 System.Xml.Linq 解析，不用 HTML 解析器（AngleSharp 会小写化标签、
/// 改写空元素、丢命名空间，见 T-1 的决定 S8）。
/// </remarks>
public static class LenientEpubReader
{
    private const int MaxEntries = 20000;
    private const long MaxEntryBytes = 200L * 1024 * 1024;
    private const int MaxXmlBytes = 8 * 1024 * 1024;
    private const int MaxCoverBytes = 16 * 1024 * 1024;
    private const int MinFallbackCoverBytes = 4 * 1024;

    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"];

    private sealed record ManifestItem(string Id, string Href, string MediaType, string Properties);

    /// <summary>
    /// 尽力读一本 EPUB。成功返回结果（Damaged = true）；失败返回 null，并给出具体原因。
    /// </summary>
    public static EpubProbeResult? TryRead(
        string fullPath, long fileSize, bool loadCover, bool fontObfuscated, out string? reason)
    {
        reason = null;

        try
        {
            using var fs = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

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

            var oversized = zip.Entries.FirstOrDefault(e => e.Length > MaxEntryBytes);
            if (oversized is not null)
            {
                reason = "单个条目解压后过大：" + oversized.FullName;
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
                reason = "OPF 不是合法 XML：" + opfPath;
                return null;
            }

            var package = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "package");
            if (package is null)
            {
                reason = "OPF 里没有 package 元素";
                return null;
            }
            var version = package.Attribute("version")?.Value ?? "2.0";
            var metadata = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");

            var title = FirstText(metadata, "title");
            var creators = AllTexts(metadata, "creator");

            var items = opf.Descendants()
                .Where(e => e.Name.LocalName == "item")
                .Select(e => new ManifestItem(
                    e.Attribute("id")?.Value ?? string.Empty,
                    e.Attribute("href")?.Value ?? string.Empty,
                    e.Attribute("media-type")?.Value ?? string.Empty,
                    e.Attribute("properties")?.Value ?? string.Empty))
                .Where(i => i.Href.Length > 0)
                .ToList();

            var spineCount = opf.Descendants().Count(e => e.Name.LocalName == "itemref");

            byte[]? coverBytes = null;
            string? coverKey = null;

            if (loadCover)
            {
                coverKey = PickCoverHref(opf, items);
                if (coverKey is not null)
                {
                    coverBytes = ReadEntryBytes(zip, ResolveHref(opfPath, coverKey), MaxCoverBytes);
                    if (coverBytes is null)
                    {
                        coverKey = null;
                    }
                }
            }

            return new EpubProbeResult
            {
                SourcePath = fullPath,
                FileSize = fileSize,
                Title = string.IsNullOrWhiteSpace(title)
                    ? Path.GetFileNameWithoutExtension(fullPath)
                    : title.Trim(),
                Authors = creators.Count > 0
                    ? string.Join(EpubProbe.AuthorSeparator, creators)
                    : null,
                Publisher = FirstText(metadata, "publisher"),
                Description = FirstText(metadata, "description"),
                Language = FirstText(metadata, "language"),
                Identifier = FirstText(metadata, "identifier"),
                PublishedDate = ParseDate(FirstText(metadata, "date")),
                IsEpub3 = version.StartsWith('3'),
                SpineCount = spineCount,
                ManifestCount = items.Count,
                CoverBytes = coverBytes,
                CoverKey = coverKey,
                CoverFromFallback = false,
                FontObfuscated = fontObfuscated,
                Damaged = true,
            };
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    // ---------- 解析辅助 ----------

    /// <summary>container.xml 里的 rootfile@full-path。找不到返回 null。</summary>
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

    private static byte[]? ReadEntryBytes(ZipArchive zip, string path, int maxBytes)
    {
        var entry = FindEntry(zip, path);
        if (entry is null || entry.Length > maxBytes)
        {
            return null;
        }

        try
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer, 81920);
            return buffer.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
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

    /// <summary>
    /// 找封面 href。只认两种声明（EPUB3 的 properties="cover-image"、EPUB2 的 meta name="cover"），
    /// 不做"猜最大的图"那套：兜底路径宁可没有封面，也不要给个错的。
    /// </summary>
    private static string? PickCoverHref(XDocument opf, List<ManifestItem> items)
    {
        var byProperty = items.FirstOrDefault(i => i.Properties
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains("cover-image", StringComparer.OrdinalIgnoreCase));

        if (byProperty is not null)
        {
            return byProperty.Href;
        }

        var coverId = opf.Descendants()
            .Where(e => e.Name.LocalName == "meta")
            .FirstOrDefault(e => string.Equals(
                e.Attribute("name")?.Value, "cover", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("content")?.Value;

        if (!string.IsNullOrWhiteSpace(coverId))
        {
            var byId = items.FirstOrDefault(i => string.Equals(i.Id, coverId, StringComparison.Ordinal));
            if (byId is not null)
            {
                return byId.Href;
            }
        }

        return null;
    }

    /// <summary>把 OPF 里的相对 href 解析成包内路径。</summary>
    private static string ResolveHref(string opfPath, string href)
    {
        var dir = GetDirectory(opfPath);
        var clean = href.Split('#')[0].Split('?')[0];
        var combined = dir.Length == 0 ? clean : dir + "/" + clean;
        return NormalizeEntryPath(Uri.UnescapeDataString(combined));
    }

    private static string GetDirectory(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    private static string NormalizeEntryPath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    /// <summary>Zip Slip 与盘符路径一律拒绝。我们不落盘，但仍然拒绝。</summary>
    private static bool IsUnsafeEntryName(string name)
    {
        var normalized = NormalizeEntryPath(name);
        return normalized.Contains("..", StringComparison.Ordinal)
            || normalized.Contains(':', StringComparison.Ordinal)
            || name.StartsWith('/')
            || name.StartsWith('\\');
    }

    private static DateOnly? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();

        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        return DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp)
            ? DateOnly.FromDateTime(stamp.Date)
            : null;
    }
}