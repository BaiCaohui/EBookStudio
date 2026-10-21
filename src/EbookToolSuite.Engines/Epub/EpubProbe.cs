using System.Globalization;
using System.IO.Compression;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using VersOne.Epub;
using VersOne.Epub.Schema;

namespace EbookToolSuite.Engines.Epub;

/// <summary>
/// VersOne.Epub 的只读适配器（设计文档 6.1：读取层）。
/// 只做一件事：把一个 epub 文件变成 EpubProbeResult，不写任何东西。
/// </summary>
/// <remarks>
/// 【铁律】这里抛出的异常只能是 AppException，且错误码只能是
/// EpubInvalid / DrmDetected / FileNotFound / InvalidPath 四种。
/// VersOne 在遇到损坏文件时抛出的类型不确定（EpubReaderException、
/// InvalidDataException、甚至 NullReferenceException），必须全部兜住，
/// 否则一个坏文件能让整个导入队列崩掉（执行计划 T1-2 的坑）。
/// </remarks>
public sealed class EpubProbe : IEpubProbe
{
    /// <summary>多作者在 books.authors 里的分隔符（见 docs/DECISIONS.md）。</summary>
    public const string AuthorSeparator = "; ";

    /// <summary>兜底封面时忽略小于这个字节数的图片：多半是分隔符、logo、占位图。</summary>
    private const int MinFallbackCoverBytes = 4 * 1024;

    public async Task<EpubProbeResult> ProbeAsync(
        string epubPath, bool loadCover, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(epubPath);
        long fileSize;

        try
        {
            var fi = new FileInfo(fullPath);
            if (!fi.Exists)
            {
                throw new AppException(
                    ErrorCode.FileNotFound,
                    "文件不存在：" + fullPath,
                    technical: "EpubProbe: FileInfo.Exists == false");
            }
            fileSize = fi.Length;
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AppException(
                ErrorCode.InvalidPath,
                "无法访问这个文件：" + fullPath,
                technical: ex.GetType().Name + ": " + ex.Message,
                inner: ex);
        }

        ct.ThrowIfCancellationRequested();

        /* T7-2 第 4/6 项：在交给 VersOne 之前先看 ZIP 本身。
           - 打不开（zip 头坏了 / 根本不是 zip）-> 明确提示"不是有效的 EPUB"，不写库；
           - 有越界路径（Zip Slip）-> 整包拒绝。 */
        EnsureZipIsSafe(fullPath);

        // DRM 判据：不是"有 encryption.xml 就判 DRM"，要按算法与覆盖对象分级（设计文档 6.1.9）。
        // 2026-10-02 实测踩到过：只有 IDPF 字体混淆的书被判成"带 DRM"，正常书被挡在门外。
        var encryption = EpubEncryptionInspector.Inspect(fullPath);
        if (encryption.IsDrm)
        {
            throw new AppException(
                ErrorCode.DrmDetected,
                "这本书是加密的（算法 " + encryption.DrmAlgorithm + "，覆盖 "
                    + encryption.EncryptedResourceCount + " 个资源），本工具不处理 DRM。",
                technical: "EpubProbe: encryption.xml, algorithms="
                    + string.Join(", ", encryption.Algorithms));
        }

        ct.ThrowIfCancellationRequested();

        EpubBook? book = null;
        string? strictFailure = null;

        try
        {
            // VersOne 3.x 的 ReadBookAsync 不接受 CancellationToken，取消只能在前后检查。
            book = await EpubReader.ReadBookAsync(fullPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            strictFailure = ex.GetType().Name + ": " + ex.Message;
        }

        if (book is null)
        {
            // 二级：宽松兜底。VersOne 做的是严格规范校验，而真实样本里
            // "违反规范但各家阅读器都能打开"的书很常见（spine 缺 toc、manifest 里 href 重复、
            // 正文写成 media-type="text/html"）。只要标题能读出来就收，标 Damaged 让 UI 提示。
            var lenient = LenientEpubReader.TryRead(
                fullPath, fileSize, loadCover, encryption.FontObfuscatedOnly, out var lenientReason);

            if (lenient is not null)
            {
                return lenient;
            }

            throw new AppException(
                ErrorCode.EpubInvalid,
                "不是有效的 EPUB 文件：" + (lenientReason ?? "结构不完整"),
                technical: "EpubProbe: 严格解析失败（" + (strictFailure ?? "ReadBookAsync 返回 null")
                    + "）；宽松兜底也失败（" + (lenientReason ?? "未给出原因") + "）");
        }

        ct.ThrowIfCancellationRequested();

        var package = book.Schema?.Package;
        var metadata = package?.Metadata;

        var title = FirstNonEmpty(metadata?.Titles?.Select(t => t.Title)) ?? book.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = Path.GetFileNameWithoutExtension(fullPath);
        }

        var authors = CleanAuthors(book.AuthorList);
        if (authors.Count == 0)
        {
            authors = CleanAuthors(metadata?.Creators?.Select(c => c.Creator));
        }

        var (coverBytes, coverKey, coverFromFallback) = loadCover
            ? PickCover(book)
            : (null, null, false);

        return new EpubProbeResult
        {
            SourcePath = fullPath,
            FileSize = fileSize,
            Title = title.Trim(),
            Authors = authors.Count > 0 ? string.Join(AuthorSeparator, authors) : null,
            Publisher = FirstNonEmpty(metadata?.Publishers?.Select(p => p.Publisher)),
            Description = CleanText(book.Description)
                ?? CleanText(FirstNonEmpty(metadata?.Descriptions?.Select(d => d.Description))),
            Language = FirstNonEmpty(metadata?.Languages?.Select(l => l.Language)),
            Isbn = PickIsbn(metadata),
            Series = MetaValue(metadata, "calibre:series")
                ?? MetaValue(metadata, "belongs-to-collection"),
            SeriesIndex = ParseSeriesIndex(MetaValue(metadata, "calibre:series_index")),
            PublishedDate = PickPublishedDate(metadata),
            Identifier = PickIdentifier(package, metadata),
            IsEpub3 = package?.EpubVersion is EpubVersion.EPUB_3 or EpubVersion.EPUB_3_1,
            SpineCount = package?.Spine?.Items?.Count ?? book.ReadingOrder?.Count ?? 0,
            ManifestCount = package?.Manifest?.Items?.Count ?? 0,
            CoverBytes = coverBytes,
            CoverKey = coverKey,
            CoverFromFallback = coverFromFallback,
            Damaged = false,
            FontObfuscated = encryption.FontObfuscatedOnly,
        };
    }

    // ---------- 封面 ----------

    /// <summary>
    /// 首选书里声明的封面（VersOne 已经处理了 EPUB2 的 meta name="cover"/guide
    /// 与 EPUB3 的 properties="cover-image"）。
    /// 兜底：取字节数最大的图片资源。比"正文第一张"稳，因为 EPUB 里
    /// 第一张图常常是出版社 logo 或分隔花纹。
    /// </summary>
    private static (byte[]? Bytes, string? Key, bool FromFallback) PickCover(EpubBook book)
    {
        if (book.CoverImage is { Length: > 0 })
        {
            return (book.CoverImage, null, false);
        }

        var images = book.Content?.Images?.Local;
        if (images is null || images.Count == 0)
        {
            return (null, null, false);
        }

        var candidate = images
            .Where(i => i.Content is { Length: >= MinFallbackCoverBytes })
            .OrderByDescending(i => i.Content!.Length)
            .FirstOrDefault();

        candidate ??= images
            .Where(i => i.Content is { Length: > 0 })
            .OrderByDescending(i => i.Content!.Length)
            .FirstOrDefault();

        return candidate?.Content is { Length: > 0 } bytes
            ? (bytes, candidate.Key, true)
            : (null, null, false);
    }

    /// <summary>
    /// T7-2 第 6 项：EPUB 里有 Zip Slip / 绝对路径 / 盘符条目时整包拒绝。
    /// 导入本身不解压，但"不落盘"不等于"可以放过"：这类包一定是被构造过的，
    /// 明确拒绝比让它进书库再在编辑/阅读路径上出事更安全。
    /// </summary>
    private static void EnsureZipIsSafe(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);

            foreach (var entry in archive.Entries)
            {
                if (EpubPackageIndex.IsUnsafeEntryName(entry.FullName))
                {
                    throw new AppException(
                        ErrorCode.UnsafeArchiveEntry,
                        "这个 EPUB 里有非法路径（" + entry.FullName + "），可能被恶意构造，已拒绝导入。",
                        technical: "EpubProbe: unsafe zip entry=" + entry.FullName);
                }
            }
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException)
        {
            // T7-2 第 4 项：zip 头坏了 / 根本不是 zip。
            throw new AppException(
                ErrorCode.EpubInvalid,
                "不是有效的 EPUB 文件：压缩包结构损坏。",
                technical: "EpubProbe: ZipFile.OpenRead 失败 " + ex.GetType().Name + ": " + ex.Message,
                inner: ex);
        }
        catch (IOException ex)
        {
            // T7-2 第 3 项：文件被其他程序独占（共享冲突）时要说"被占用"，不是"结构损坏"。
            var info = FileErrors.Classify(ex);
            throw new AppException(
                info.Code,
                "读取这个 EPUB 失败：" + info.Message + info.Action,
                technical: "EpubProbe: ZipFile.OpenRead IO 失败 " + ex.GetType().Name + ": " + ex.Message,
                inner: ex);
        }
    }

    // ---------- 元数据小工具 ----------

    private static string? FirstNonEmpty(IEnumerable<string?>? values)
        => values?.Select(CleanText).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // 元数据里常见换行与首尾空白，统一压成单空格
        var collapsed = string.Join(' ', value.Split(
            new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return string.IsNullOrWhiteSpace(collapsed) ? null : collapsed;
    }

    private static List<string> CleanAuthors(IEnumerable<string?>? values)
    {
        var result = new List<string>();
        if (values is null)
        {
            return result;
        }

        foreach (var raw in values)
        {
            var name = CleanText(raw);
            if (name is null)
            {
                continue;
            }

            // 有些书把多个作者塞在一个 dc:creator 里，用 & 或 ; 分隔，拆开
            foreach (var part in name.Split(new[] { ';', '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var one = part.Trim();
                if (one.Length > 0 && !result.Contains(one, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(one);
                }
            }
        }

        return result;
    }

    /// <summary>按 Name 或 Property 取 meta 项的值（Calibre 自己写的书用 calibre:series 等）。</summary>
    private static string? MetaValue(EpubMetadata? metadata, string key)
    {
        var item = metadata?.MetaItems?.FirstOrDefault(m =>
            string.Equals(m.Name, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Property, key, StringComparison.OrdinalIgnoreCase));

        return CleanText(item?.Content);
    }

    private static double? ParseSeriesIndex(string? text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? PickIsbn(EpubMetadata? metadata)
    {
        var identifiers = metadata?.Identifiers;
        if (identifiers is null || identifiers.Count == 0)
        {
            return null;
        }

        var byScheme = identifiers.FirstOrDefault(i =>
            string.Equals(i.Scheme, "ISBN", StringComparison.OrdinalIgnoreCase));

        var value = CleanText(byScheme?.Identifier);

        if (value is null)
        {
            value = identifiers
                .Select(i => CleanText(i.Identifier))
                .FirstOrDefault(v => v is not null && v.StartsWith("urn:isbn:", StringComparison.OrdinalIgnoreCase));

            value = value?.Substring("urn:isbn:".Length);
        }

        if (value is null)
        {
            return null;
        }

        var digits = new string(value.Where(c => char.IsAsciiDigit(c) || c is 'X' or 'x').ToArray());
        return digits.Length is 10 or 13 ? digits.ToUpperInvariant() : null;
    }

    private static string? PickIdentifier(EpubPackage? package, EpubMetadata? metadata)
    {
        var identifiers = metadata?.Identifiers;
        if (identifiers is null || identifiers.Count == 0)
        {
            return null;
        }

        var refId = CleanText(package?.UniqueIdentifier);
        if (refId is not null)
        {
            var match = identifiers.FirstOrDefault(i =>
                string.Equals(i.Id, refId, StringComparison.Ordinal));
            var value = CleanText(match?.Identifier);
            if (value is not null)
            {
                return value;
            }
        }

        return CleanText(identifiers[0].Identifier);
    }

    /// <summary>出版日期。EPUB 里格式很杂（1998 / 1998-05 / 1998-05-01 / 带时区），逐个试。</summary>
    private static DateOnly? PickPublishedDate(EpubMetadata? metadata)
    {
        var dates = metadata?.Dates;
        if (dates is null || dates.Count == 0)
        {
            return null;
        }

        // 优先 event="publication" 的那条
        var ordered = dates
            .OrderByDescending(d => string.Equals(d.Event, "publication", StringComparison.OrdinalIgnoreCase))
            .Select(d => CleanText(d.Date));

        foreach (var text in ordered)
        {
            if (text is null)
            {
                continue;
            }

            if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            {
                return exact;
            }

            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var stamp))
            {
                return DateOnly.FromDateTime(stamp.UtcDateTime);
            }

            // 只有年份：补成 1 月 1 日（Calibre 也是这么做的）
            var yearText = new string(text.TakeWhile(char.IsAsciiDigit).ToArray());
            if (yearText.Length == 4 && int.TryParse(yearText, out var year)
                && year is >= 1000 and <= 2100)
            {
                return new DateOnly(year, 1, 1);
            }
        }

        return null;
    }

}