using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.IO;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Engines.Epub;

/// <summary>
/// 封面替换（执行计划 T3-3，设计文档 6.1.8）。只动**工作区**，原 EPUB 一个字节都不碰。
/// </summary>
/// <remarks>
/// 流程：找 OPF -> 找原封面条目 -> 把用户选的图规范化成 JPEG（判据 3）-> 决定目标路径 ->
/// 原子替换图片文件 -> 重写引用（只在换扩展名时）-> 更新 manifest 与封面声明 -> 清掉没人引用的旧图。
///
/// 【为什么"不改文件名"很重要】EPUB 里封面可能被多处引用（&lt;img src&gt;、CSS background、
/// &lt;meta name="cover"&gt;），原封面已经是 JPEG 时改名要全部跟着改，风险大且 diff 大，
/// 所以原样覆盖。只有**格式必须变**（PNG/GIF/WebP -> JPEG）时才换扩展名，并同步重写引用。
///
/// 【为什么必须换扩展名】实测 EpubCheck 5.4.0：JPEG 字节放进 .png 且 media-type 改成 image/jpeg
/// 时零 error 但留一条 PKG-022 警告；若 media-type 保持 image/png 则直接 error（OPF-029 文件与
/// media-type 不符）。为了给 T3-5 的门禁留"零 warning"的余地，这里选择换成 .jpg 并重写引用。
/// </remarks>
public sealed class CoverService
{
    /// <summary>写进 EPUB 的封面长边上限（执行计划 T3-3 判据 3）。</summary>
    public const int CoverMaxEdgePx = 1600;

    /// <summary>写进 EPUB 的 JPEG 质量（执行计划 T3-3 流程第 2 步）。</summary>
    public const int CoverJpegQuality = 88;

    /// <summary>写进 EPUB 的封面体积上限（判据 3）。超了就按 <see cref="FallbackQualities"/> 降质量重编。</summary>
    public const int CoverMaxBytes = 500 * 1024;

    /// <summary>超体积时依次尝试的质量档。</summary>
    private static readonly int[] FallbackQualities = { 82, 76, 70, 62, 55 };

    private const string ContainerRelativePath = "META-INF/container.xml";
    private const string ContainerNamespace = "urn:oasis:names:tc:opendocument:xmlns:container";
    private const string CoverItemIdBase = "cover-img";
    private const string NewCoverFolder = "Images";
    private const string NewCoverFileName = "cover.jpg";
    private const string CoverMediaType = "image/jpeg";
    private const string CoverProperty = "cover-image";

    /// <summary>重写引用时要扫的文件类型。EPUB 里的引用只会出现在这些文本文件里。</summary>
    private static readonly string[] TextExtensions =
    [
        ".xhtml", ".html", ".htm", ".css", ".ncx", ".svg", ".js", ".xml", ".txt",
    ];

    private readonly IImageService _images;

    public CoverService(IImageService images)
        => _images = images ?? throw new ArgumentNullException(nameof(images));

    /// <summary>
    /// 把 <paramref name="newCoverBytes"/> 换成工作区里这本书的封面。
    /// </summary>
    /// <param name="workDir">T3-1 解出来的工作区根目录（里面是 mimetype / META-INF / OEBPS）。</param>
    /// <param name="newCoverBytes">用户选的图片原图（JPG / PNG / WebP 都行）。</param>
    /// <exception cref="AppException">
    /// 图片读不出来（UnsupportedFormat）、工作区或 OPF 不完整（EpubInvalid）、
    /// 封面 href 指到工作区外面（UnsafeArchiveEntry）。
    /// </exception>
    public async Task<CoverReplaceResult> ReplaceCoverAsync(
        string workDir,
        byte[] newCoverBytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workDir);

        if (newCoverBytes is null || newCoverBytes.Length == 0)
        {
            throw new AppException(
                ErrorCode.UnsupportedFormat,
                "这张图片是空的，请换一张。",
                "空图片字节");
        }

        var root = Path.GetFullPath(workDir);
        if (!Directory.Exists(root))
        {
            throw new AppException(
                ErrorCode.FileNotFound,
                "工作区不存在，无法换封面。",
                "工作区目录不存在：" + root);
        }

        var opfPath = LocateOpf(root);
        var opfDir = Path.GetDirectoryName(opfPath)!;

        // ---------- 1. 规范化（没有任何副作用，读不出来就到此为止）----------
        var normalized = await EncodeJpegAsync(newCoverBytes, ct).ConfigureAwait(false);
        if (normalized is null)
        {
            throw new AppException(
                ErrorCode.UnsupportedFormat,
                "这张图片读不出来，请换一张（支持 JPG / PNG / WebP）。",
                "ImageSharp 解码失败，或像素数超过上限");
        }

        ct.ThrowIfCancellationRequested();

        // ---------- 2. 现在才读 OPF、决定目标 ----------
        var editor = new OpfMetadataEditor(opfPath);
        var items = editor.ReadManifest();
        var existing = PickCoverItem(items, editor.Read().CoverItemId);

        var plan = PlanTarget(root, opfDir, items, existing);

        // 写图 + 重写引用 + 更新 OPF 的顺序是刻意的：
        // 中途任何一步失败，旧图都还在、OPF 也还指向旧图，包仍然是自洽的（判据 6）。
        WriteAtomic(plan.AbsolutePath, normalized.Bytes);

        var rewritten = plan.Renamed
            ? RewriteReferences(root, opfPath, plan.PreviousHref!, plan.Href)
            : Array.Empty<string>();

        editor.UpsertManifestItem(
            new OpfManifestItem(plan.ItemId, plan.Href, CoverMediaType),
            editor.IsEpub3 ? CoverProperty : null);
        editor.SetCoverItemId(plan.ItemId);
        editor.Save();

        // ---------- 3. 最后才清理旧图：确认工作区里再没人提它 ----------
        if (plan.Renamed && plan.PreviousHref is not null)
        {
            var oldPath = ResolveInside(root, opfDir, plan.PreviousHref);
            if (File.Exists(oldPath) && !IsStillReferenced(root, opfPath, oldPath))
            {
                TryDelete(oldPath);
            }
        }

        _ = rewritten;

        return new CoverReplaceResult(
            plan.Href,
            plan.AbsolutePath,
            CoverMediaType,
            plan.ItemId,
            plan.Created,
            plan.Renamed,
            plan.PreviousHref,
            normalized.Width,
            normalized.Height,
            normalized.Bytes.Length);
    }

    // ================= 找 OPF =================

    private static string LocateOpf(string root)
    {
        var containerPath = Path.Combine(root, "META-INF", "container.xml");
        if (!File.Exists(containerPath))
        {
            throw new AppException(
                ErrorCode.EpubInvalid,
                "工作区不完整，找不到 META-INF/container.xml。",
                "缺少 " + containerPath);
        }

        XDocument document;
        try
        {
            document = XDocument.Load(containerPath);
        }
        catch (Exception ex) when (ex is XmlException or IOException)
        {
            throw new AppException(ErrorCode.EpubInvalid, "工作区的 container.xml 读不出来。", ex.Message, ex);
        }

        var ns = XNamespace.Get(ContainerNamespace);
        var rootfile = document.Root?.Descendants(ns + "rootfile").FirstOrDefault()
            ?? document.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile");

        var fullPath = (string?)rootfile?.Attribute("full-path");
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            throw new AppException(
                ErrorCode.EpubInvalid,
                "工作区不完整，container.xml 里没有 rootfile@full-path。",
                containerPath);
        }

        var opfPath = ResolveInside(root, fullPath);
        if (!File.Exists(opfPath))
        {
            throw new AppException(
                ErrorCode.EpubInvalid,
                "工作区不完整，OPF 文件不在：" + fullPath,
                opfPath);
        }

        return opfPath;
    }

    // ================= 找原封面 =================

    /// <summary>
    /// 与 LenientEpubReader 同一口径：EPUB3 的 properties="cover-image" 优先，
    /// 其次 EPUB2/3 通用的 &lt;meta name="cover" content="id"/&gt;。
    /// **不猜"最大的图"**（设计文档 6.1.9 的分级口径）。
    /// </summary>
    internal static OpfManifestItem? PickCoverItem(IReadOnlyList<OpfManifestItem> items, string? metaCoverId)
    {
        var byProperty = items.FirstOrDefault(i => HasProperty(i.Properties, CoverProperty));
        if (byProperty is not null)
        {
            return byProperty;
        }

        if (string.IsNullOrWhiteSpace(metaCoverId))
        {
            return null;
        }

        return items.FirstOrDefault(i => string.Equals(i.Id, metaCoverId, StringComparison.Ordinal));
    }

    private static bool HasProperty(string? properties, string property)
        => !string.IsNullOrWhiteSpace(properties)
            && properties.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Any(p => string.Equals(p, property, StringComparison.OrdinalIgnoreCase));

    // ================= 决定目标路径 =================

    private sealed record TargetPlan(
        string ItemId,
        string Href,
        string AbsolutePath,
        bool Created,
        bool Renamed,
        string? PreviousHref);

    private static TargetPlan PlanTarget(
        string root,
        string opfDir,
        IReadOnlyList<OpfManifestItem> items,
        OpfManifestItem? existing)
    {
        if (existing is not null && !string.IsNullOrWhiteSpace(existing.Href) && !IsRemote(existing.Href))
        {
            if (HasJpegExtension(existing.Href))
            {
                var path = TryResolveInside(root, opfDir, existing.Href);
                if (path is not null)
                {
                    return new TargetPlan(existing.Id, existing.Href, path, false, false, existing.Href);
                }
            }
            else
            {
                var href = ReplaceExtension(existing.Href, ".jpg");
                var path = TryResolveInside(root, opfDir, href);
                if (path is not null && !IsHrefClaimed(items, href, existing.Id))
                {
                    return new TargetPlan(existing.Id, href, path, false, true, existing.Href);
                }
            }
        }

        // 新建：OPF 目录下 Images/cover.jpg（设计文档 6.1.8 步骤 2）
        var folder = Path.Combine(opfDir, NewCoverFolder);
        var stem = Path.GetFileNameWithoutExtension(NewCoverFileName);
        var extension = Path.GetExtension(NewCoverFileName);

        var index = 1;
        while (true)
        {
            var fileName = index == 1 ? stem + extension : stem + "-" + index + extension;
            var absolute = Path.Combine(folder, fileName);
            var href = ToHref(opfDir, absolute);

            if (!IsHrefClaimed(items, href, null) && !File.Exists(absolute))
            {
                return new TargetPlan(UniqueItemId(items), href, absolute, true, false, existing?.Href);
            }

            index++;
            if (index > 999)
            {
                throw new AppException(
                    ErrorCode.EpubInvalid,
                    "这本书里同名文件太多，换不了封面。",
                    "cover-N.jpg 全部撞名：" + folder);
            }
        }
    }

    private static bool IsHrefClaimed(IReadOnlyList<OpfManifestItem> items, string href, string? exceptId)
        => items.Any(i => !string.Equals(i.Id, exceptId, StringComparison.Ordinal)
            && string.Equals(i.Href, href, StringComparison.OrdinalIgnoreCase));

    private static string UniqueItemId(IReadOnlyList<OpfManifestItem> items)
    {
        if (items.All(i => !string.Equals(i.Id, CoverItemIdBase, StringComparison.Ordinal)))
        {
            return CoverItemIdBase;
        }

        var index = 2;
        while (items.Any(i => string.Equals(i.Id, CoverItemIdBase + "-" + index, StringComparison.Ordinal)))
        {
            index++;
        }

        return CoverItemIdBase + "-" + index;
    }

    private static bool IsRemote(string href)
        => href.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    private static bool HasJpegExtension(string href)
    {
        var path = href.Split('#', '?')[0];
        return path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpe", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReplaceExtension(string href, string newExtension)
    {
        var suffixStart = href.Length;
        foreach (var stop in new[] { '#', '?' })
        {
            var index = href.IndexOf(stop);
            if (index >= 0 && index < suffixStart)
            {
                suffixStart = index;
            }
        }

        var head = href.Substring(0, suffixStart);
        var suffix = href.Substring(suffixStart);
        var slash = head.LastIndexOf('/');
        var dot = head.LastIndexOf('.');

        return dot > slash ? head.Substring(0, dot) + newExtension + suffix : head + newExtension + suffix;
    }

    // ================= 路径与转义 =================

    /// <summary>按"相对工作区根"解 href（container.xml 的 rootfile@full-path 就是这个口径）。</summary>
    internal static string ResolveInside(string root, string href)
        => ResolveInside(root, root, href);

    /// <summary>把 href 解成绝对路径，并挡住"指到工作区外面"的路径（与 T3-1 同一口径）。</summary>
    /// <param name="baseDir">href 的基准目录。OPF 里的 href 相对 OPF 所在目录（OPF 在 OEBPS/ 时 href="Images/cover.jpg" 指 OEBPS/Images/cover.jpg），container.xml 的 rootfile@full-path 才是相对包根。</param>
    internal static string ResolveInside(string root, string baseDir, string href)
    {
        var decoded = Uri.UnescapeDataString(href.Replace('\\', '/'));
        if (decoded.Contains('\0'))
        {
            throw new AppException(ErrorCode.UnsafeArchiveEntry, "压缩包有非法路径", href);
        }

        var rootFull = Path.GetFullPath(root);
        var baseFull = Path.GetFullPath(baseDir);
        var combined = Path.GetFullPath(Path.Combine(baseFull, decoded.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(
                ErrorCode.UnsafeArchiveEntry,
                "压缩包有非法路径",
                "封面路径指到工作区外面：" + href);
        }

        return combined;
    }

    private static string? TryResolveInside(string root, string baseDir, string href)
    {
        try
        {
            return ResolveInside(root, baseDir, href);
        }
        catch (AppException)
        {
            return null;
        }
    }

    /// <summary>绝对路径 -> 以 <paramref name="baseDir"/> 为基准的 href（每段分别转义）。</summary>
    internal static string ToHref(string baseDir, string absolutePath)
    {
        var relative = Path.GetRelativePath(baseDir, absolutePath).Replace('\\', '/');
        var parts = relative.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = Uri.EscapeDataString(parts[i]);
        }

        return string.Join("/", parts);
    }

    // ================= 原子写文件 =================

    /// <summary>写 .ets-tmp 再 File.Replace（带 .ets-bak 备份），失败就还原：读者永远读不到半张图。</summary>
    private static void WriteAtomic(string targetPath, byte[] bytes)
    {
        var folder = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(folder);

        var temp = targetPath + ".ets-tmp";
        var backup = targetPath + ".ets-bak";
        var existed = File.Exists(targetPath);

        try
        {
            File.WriteAllBytes(temp, bytes);

            if (existed)
            {
                File.Replace(temp, targetPath, backup, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, targetPath);
            }
        }
        catch
        {
            TryDelete(temp);

            if (existed && File.Exists(backup))
            {
                try
                {
                    File.Move(backup, targetPath, overwrite: true);
                }
                catch (Exception)
                {
                    // 还原都失败就只能把异常抛上去，让调用方看到工作区处于异常状态
                }
            }

            throw;
        }

        TryDelete(backup);
    }

    // ================= 重写引用 =================

    /// <summary>
    /// 换扩展名之后，把所有文本文件里指向旧封面的引用改成新名字。
    /// 返回被改过的文件列表。**读写都保留原文件的编码与 BOM**，非 UTF-8 的文件跳过（EPUB 里不该有）。
    /// </summary>
    private static IReadOnlyList<string> RewriteReferences(
        string root,
        string opfPath,
        string oldHref,
        string newHref)
    {
        var opfDir = Path.GetDirectoryName(opfPath)!;
        var oldPath = ResolveInside(root, opfDir, oldHref);
        var newPath = ResolveInside(root, opfDir, newHref);
        var changed = new List<string>();

        foreach (var file in EnumerateTextFiles(root, opfPath))
        {
            var folder = Path.GetDirectoryName(file)!;
            var tokenOld = Path.GetRelativePath(folder, oldPath).Replace('\\', '/');
            var tokenNew = Path.GetRelativePath(folder, newPath).Replace('\\', '/');

            if (string.Equals(tokenOld, tokenNew, StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryReadText(file, out var text, out var encoding, out var bom))
            {
                continue;
            }

            var updated = ReplaceToken(text, tokenOld, tokenNew);
            var escapedOld = EscapeHref(tokenOld);
            if (escapedOld != tokenOld)
            {
                updated = ReplaceToken(updated, escapedOld, EscapeHref(tokenNew));
            }

            if (string.Equals(updated, text, StringComparison.Ordinal))
            {
                continue;
            }

            WriteText(file, updated, encoding, bom);
            changed.Add(file);
        }

        return changed;
    }

    /// <summary>旧图还有人提就不删（宁可留个孤儿文件，也不能留一句指向空气的引用）。</summary>
    private static bool IsStillReferenced(string root, string opfPath, string oldPath)
    {
        var name = Path.GetFileName(oldPath);
        var escapedName = EscapeHref(name);

        foreach (var file in EnumerateTextFiles(root, opfPath))
        {
            if (!TryReadText(file, out var text, out _, out _))
            {
                continue;
            }

            if (ContainsToken(text, name) || ContainsToken(text, escapedName))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateTextFiles(string root, string skipPath)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(file, skipPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    /// <summary>整词替换：前面不能紧挨着文件名字符，避免把 mycover.png 里的 cover.png 改掉。</summary>
    private static string ReplaceToken(string text, string oldValue, string newValue)
        => Regex.Replace(text, "(?<![A-Za-z0-9._\\-])" + Regex.Escape(oldValue), _ => newValue);

    private static bool ContainsToken(string text, string value)
        => Regex.IsMatch(text, "(?<![A-Za-z0-9._\\-])" + Regex.Escape(value));

    private static string EscapeHref(string value)
    {
        var parts = value.Replace('\\', '/').Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = Uri.EscapeDataString(parts[i]);
        }

        return string.Join("/", parts);
    }

    // ================= 文本读写（保留编码） =================

    private static bool TryReadText(string path, out string text, out Encoding encoding, out byte[] bom)
    {
        text = string.Empty;
        encoding = new UTF8Encoding(false, throwOnInvalidBytes: true);
        bom = Array.Empty<byte>();

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var offset = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            offset = 3;
            bom = bytes[..3];
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            offset = 2;
            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
            bom = bytes[..2];
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            offset = 2;
            encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true);
            bom = bytes[..2];
        }

        try
        {
            text = encoding.GetString(bytes, offset, bytes.Length - offset);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static void WriteText(string path, string text, Encoding encoding, byte[] bom)
    {
        var body = encoding.GetBytes(text);
        var payload = new byte[bom.Length + body.Length];
        bom.CopyTo(payload, 0);
        body.CopyTo(payload, bom.Length);
        AtomicFile.WriteAllBytes(path, payload);
    }

    // ================= 编码（含体积上限兜底） =================

    /// <summary>
    /// 规范化成 JPEG。超过 <see cref="CoverMaxBytes"/> 就依次降质量重编，
    /// 拿第一个达标的结果；都不达标时用最小的那个（判据 3 的目标是"500 KB"）。
    /// </summary>
    private async Task<NormalizedImage?> EncodeJpegAsync(byte[] imageBytes, CancellationToken ct)
    {
        var best = await _images
            .EncodeCoverJpegAsync(imageBytes, CoverMaxEdgePx, CoverJpegQuality, ct)
            .ConfigureAwait(false);

        if (best is null || best.Bytes.Length <= CoverMaxBytes)
        {
            return best;
        }

        foreach (var quality in FallbackQualities)
        {
            var attempt = await _images
                .EncodeCoverJpegAsync(imageBytes, CoverMaxEdgePx, quality, ct)
                .ConfigureAwait(false);

            if (attempt is null)
            {
                break;
            }

            best = attempt;
            if (attempt.Bytes.Length <= CoverMaxBytes)
            {
                break;
            }
        }

        return best;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响主流程（工作区的残留由 T3-1 的 DiscardAsync / T3-6 负责）
        }
    }
}