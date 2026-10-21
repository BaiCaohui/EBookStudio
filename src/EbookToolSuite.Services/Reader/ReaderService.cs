using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using EbookToolSuite.Core;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.IO;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Epub;
using EbookToolSuite.Engines.Text;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Reader;

/// <summary>
/// 阅读器服务：把库里的书变成一堆"WebView2 能直接吃"的文件。
/// 规格见执行计划 T1-5、设计文档 8 章。
/// </summary>
/// <remarks>
/// 【准备与渲染分离】OpenAsync 只解压 + 解析 OPF + 写 reader.json，
/// 一章都不规范化；章节走 MaterializeChapterAsync 按需做。
/// 500 章的书若在打开时全量规范化要好几秒，而判据要求首屏不超过 1.5 秒。
/// </remarks>
public sealed class ReaderService : IReaderService
{
    private const int ManifestSchemaVersion = 3;

    /// <summary>PDF 清单里的 <see cref="ReaderManifest.Format"/> 取值。</summary>
    private const string PdfFormatName = "PDF";

    /// <summary>T6-3：漫画包在清单里的格式名（存储串与 BookFormatKinds 一致）。</summary>
    private const string ComicFormatName = "CBZ";
    private const int MaxEntries = 20000;
    private const long MaxEntryBytes = 200L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,

        // 中文书名不要被转成 \uXXXX，否则维护时看 reader.json 完全没法读
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IBookRepository _repo;
    private readonly IAppPaths _paths;
    private readonly IHashUtil _hash;
    private readonly IReaderAssets _assets;
    private readonly IEpubCacheService _epubCache;
    private readonly ILogger<ReaderService> _log;

    /// <summary>跨格式找位用：去标签与去脚本/样式，尽量贴近 reader.js 拿到的 textContent。</summary>
    private static readonly Regex ScriptStyleRegex = new(
        "<(script|style)[^>]*>.*?</\\1>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);

    /// <summary>与 reader.js 的 normalizeText 同口径：去掉空白与零宽字符。</summary>
    private static readonly Regex WhitespaceRegex = new(
        "[\\s\\u200B-\\u200F\\uFEFF]+", RegexOptions.Compiled);

    public ReaderService(
        IBookRepository repo,
        IAppPaths paths,
        IHashUtil hash,
        IReaderAssets assets,
        IEpubCacheService epubCache,
        ILogger<ReaderService> log)
    {
        _repo = repo;
        _paths = paths;
        _hash = hash;
        _assets = assets;
        _epubCache = epubCache;
        _log = log;
    }

    /// <summary>Converted=true 表示 Format 是 MOBI/AZW3，正文要先转成 EPUB 缓存才能读。</summary>
    private sealed record Source(Book Book, BookFormat Format, string Hash, bool Converted = false);

    public Task<ReaderSession> OpenAsync(Guid bookId, CancellationToken ct = default)
        => OpenCoreAsync(bookId, textEncodingOverride: null, ct);

    public Task<ReaderSession> OpenWithTextEncodingAsync(
        Guid bookId, string? encodingName, CancellationToken ct = default)
    {
        // 不认识的名字当"没指定"，退回自动识别（用户在下拉里选的一定认识）
        var normalized = TextEncodingSupport.Normalize(encodingName);

        return OpenCoreAsync(bookId, normalized.Length > 0 ? normalized : null, ct);
    }

    private async Task<ReaderSession> OpenCoreAsync(
        Guid bookId, string? textEncodingOverride, CancellationToken ct)
    {
        var source = await ResolveSourceAsync(bookId, ct).ConfigureAwait(false);
        var cacheRoot = Path.Combine(_paths.ReaderCacheDir, source.Hash);

        /* T6-2/T6-3：PDF 与漫画（CBZ/CBR）都不解包，正文由自绘引擎按页向宿主取，
           所以它们需要两条额外信息：第二个虚拟主机名映射哪个目录、原始文件在哪个地址。
           这里一次算好，缓存命中与新建两条路都用它——只在新建那条路上算的话，
           第二次打开就会拿不到地址。 */
        var isFixedLayout = source.Format.Format is BookFormatKind.Pdf or BookFormatKind.Cbz;
        var pdfFileRoot = isFixedLayout ? _paths.LibraryDir : null;
        var pdfUrl = isFixedLayout ? BuildFileUrl(source.Format.FilePath) : null;

        // T6-1：MOBI/AZW3 先惰性转成 EPUB（缓存命中就直接返回，判据 2）。
        var archivePath = source.Converted
            ? await _epubCache
                .GetOrConvertAsync(
                    bookId, source.Format.Format, source.Format.FilePath, source.Hash, ct)
                .ConfigureAwait(false)
            : null;

        if (TryLoadManifest(cacheRoot, out var cached) && cached is not null)
        {
            /* TXT 的缓存要连编码一起看：用户在界面上换了编码就必须重新切章，
               不能拿旧清单接着用（T4-5 判据 2：改完立即重排）。 */
            var encodingMatches = source.Format.Format != BookFormatKind.Txt
                || textEncodingOverride is null
                || string.Equals(cached.TextEncoding, textEncodingOverride, StringComparison.OrdinalIgnoreCase);

            if (encodingMatches)
            {
                // 命中已有缓存：只补一次静态资源（几个小文件，很快）
                CopyAssets(cacheRoot);
                _log.LogInformation(
                    "阅读缓存命中：{BookId} 共 {Count} 章，目录 {Root}",
                    bookId, cached.Spine.Count, cacheRoot);

                BeginReadAndTrim(bookId, cacheRoot);

                return new ReaderSession
                {
                    BookId = bookId,
                    CacheRoot = cacheRoot,
                    Manifest = cached,
                    SourceFormat = source.Format.Format,
                    SourcePath = source.Format.FilePath,
                    FromCache = true,
                    FileRoot = pdfFileRoot,
                    PdfUrl = pdfUrl,
                };
            }

            _log.LogInformation("TXT 编码改为 {Encoding}，重建阅读缓存：{Root}", textEncodingOverride, cacheRoot);
        }

        var session = source.Format.Format switch
        {
            BookFormatKind.Txt => BuildText(source, cacheRoot, textEncodingOverride),
            BookFormatKind.Pdf => BuildFixedLayout(source, cacheRoot, pdfFileRoot!, pdfUrl!, PdfFormatName),
            BookFormatKind.Cbz => BuildFixedLayout(source, cacheRoot, pdfFileRoot!, pdfUrl!, ComicFormatName),
            _ => Build(source, cacheRoot, archivePath),
        };

        BeginReadAndTrim(bookId, cacheRoot);
        return session;
    }

    /// <summary>
    /// 固定版式原文件在第二个虚拟主机上的地址（设计文档 §7.4）：
    /// <c>http://ebook-file.local/{bookId}/{文件名}</c>。PDF 与漫画共用。
    /// 路径的每一段单独转义——整串一起转义会把分隔符也吃掉，那样映射就找不到了。
    /// </summary>
    private string BuildFileUrl(string sourcePath)
    {
        var relative = Path.GetRelativePath(_paths.LibraryDir, sourcePath)
            .Replace('\\', '/')
            .TrimStart('/');

        var escaped = string.Join(
            "/",
            relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

        return ReaderEndpoints.FileHostRootUrl + escaped;
    }

    /// <summary>
    /// 把这本书的缓存登记为「正在读」，再按上限清理一次（T6-1 判据 6）。
    /// 清理跳过正在读的，所以不会把刚打开的这本删掉。
    /// </summary>
    private void BeginReadAndTrim(Guid bookId, string cacheRoot)
    {
        _epubCache.BeginRead(bookId, cacheRoot);

        try
        {
            _epubCache.EnforceLimitAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // 缓存清理失败不该让用户打不开书
            _log.LogWarning(ex, "清理阅读缓存失败（不影响打开）：{Root}", cacheRoot);
        }
    }

    /// <summary>阅读窗口关闭：释放这本书占用的缓存，让上限清理可以回收它。</summary>
    public void Release(Guid bookId) => _epubCache.EndRead(bookId);

    /// <summary>
    /// 找到可读的文件并算出缓存键。
    /// </summary>
    /// <remarks>
    /// 优先级（用户 2026-10-21 允许在详情页改）：用户在详情页选的「当前阅读格式」且文件还能用
    /// → 默认链 EPUB → TXT → MOBI → AZW3 → PDF → CBZ。
    /// 选过但文件没了（被删 / 被移走）时**静默退回默认链**：设置不能把这本书变成打不开。
    /// </remarks>
    private async Task<Source> ResolveSourceAsync(Guid bookId, CancellationToken ct)
    {
        var book = await _repo.GetByIdAsync(bookId, ct).ConfigureAwait(false)
            ?? throw new AppException(
                ErrorCode.ReaderOpenFailed,
                "找不到这本书，它可能已经从书库里移除了。",
                "book " + bookId + " not found");

        var preferred = await _repo.GetReadingFormatAsync(bookId, ct).ConfigureAwait(false);
        var readable = ReaderFormatSupport.Resolve(book.Formats, preferred);

        if (preferred is { } wanted && readable is not null && readable.Format != wanted)
        {
            _log.LogInformation(
                "当前阅读格式 {Format} 已不可用，退回默认优先级：这次用 {Fallback} 打开",
                wanted, readable.Format);
        }

        if (readable is null)
        {
            var declared = book.Formats.Any(f =>
                ReaderFormatSupport.CanRead(f.Format) && !f.Missing);

            throw new AppException(
                ErrorCode.ReaderOpenFailed,
                declared
                    ? "这本书的文件不在了，可能被移动或删除了。可以重新导入一次。"
                    : "这本书没有可阅读的格式（EPUB / TXT / MOBI / AZW3 / PDF / CBZ·CBR）。",
                declared ? "readable file missing" : "no readable format",
                context: new Dictionary<string, object?>
                {
                    ["bookId"] = bookId,
                    ["formats"] = string.Join(',', book.Formats.Select(f => f.Format.ToString())),
                });
        }

        // MOBI / AZW3 不是直接读：打开时惰性转成 EPUB 缓存（T6-1）。
        var converted = ReaderFormatSupport.RequiresEpubConversion(readable.Format);

        var hash = readable.QuickFingerprint;
        if (string.IsNullOrWhiteSpace(hash))
        {
            hash = await _hash.QuickFingerprintAsync(readable.FilePath, ct).ConfigureAwait(false);
        }

        if (converted)
        {
            // T6-1 判据 3：转换缓存必须随「源文件被替换」失效。
            // quick_fingerprint 只看头尾 64KB，同尺寸替换可能撞车，所以再带上 mtime+size。
            // 键的算法只有 ReaderEndpoints.ConvertedCacheKey 一份：
            // 书库详情页判断"转换产物在不在"走的也是它，两处必须一致。
            var info = new FileInfo(readable.FilePath);
            hash = ReaderEndpoints.ConvertedCacheKey(hash, info.LastWriteTimeUtc, info.Length);
        }

        return new Source(book, readable, ReaderEndpoints.CacheFolderName(hash), converted);
    }

    /// <summary>读缓存里的清单。结构版本不符或文件坏了都当没缓存。</summary>
    private bool TryLoadManifest(string cacheRoot, out ReaderManifest? manifest)
    {
        manifest = null;
        var path = Path.Combine(cacheRoot, ReaderEndpoints.ManifestFileName);

        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<ReaderManifest>(json, JsonOptions);

            if (loaded is null || loaded.SchemaVersion != ManifestSchemaVersion)
            {
                _log.LogInformation("阅读缓存版本不符或结构异常，将重建：{Path}", path);
                return false;
            }

            /* 固定版式的清单天然是"空"的：不解包所以没有 content/ 目录，也没有章节所以 Spine 为空。
               对它只认清单本身；其余格式两者都必须有，缺了就当缓存损坏重建。
               （T6-2 的 PDF 与 T6-3 的漫画同属这一类。） */
            var isFixedLayout = string.Equals(loaded.Format, PdfFormatName, StringComparison.Ordinal)
                || string.Equals(loaded.Format, ComicFormatName, StringComparison.Ordinal);

            if (!isFixedLayout
                && (loaded.Spine.Count == 0
                    || !Directory.Exists(Path.Combine(cacheRoot, ReaderEndpoints.ContentDirectoryName))))
            {
                _log.LogInformation("阅读缓存版本不符或结构异常，将重建：{Path}", path);
                return false;
            }

            manifest = loaded;
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "阅读缓存清单读不出来，将重建：{Path}", path);
            return false;
        }
    }
    /// <summary>
    /// 章节懒物化要读的那份「内容归档」。
    /// EPUB / TXT 就是源文件自己；MOBI / AZW3 是转换出来的缓存 EPUB
    /// （缓存键就是缓存目录名，取法见 EpubCacheService.GetArtifactPath）。
    /// </summary>
    private string ResolveContentArchive(ReaderSession session)
    {
        if (session.SourceFormat is not (BookFormatKind.Mobi or BookFormatKind.Azw3))
        {
            return session.SourcePath;
        }

        var cacheKey = Path.GetFileName(session.CacheRoot.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var artifact = _epubCache.GetArtifactPath(cacheKey);

        return File.Exists(artifact) ? artifact : session.SourcePath;
    }

    // ---------- 建缓存 ----------

    private ReaderSession Build(Source source, string cacheRoot, string? archivePath = null)
    {
        var tempRoot = cacheRoot + ".part-" + Guid.NewGuid().ToString("N")[..8];
        var contentRoot = Path.Combine(tempRoot, ReaderEndpoints.ContentDirectoryName);

        try
        {
            Directory.CreateDirectory(contentRoot);

            EpubPackageIndex index;
            int extracted;

            // MOBI/AZW3 走 archivePath（转换出来的 EPUB）；EPUB 直接用源文件。
            var readPath = archivePath ?? source.Format.FilePath;

            using (var fs = new FileStream(
                readPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                if (zip.Entries.Count > MaxEntries)
                {
                    throw new AppException(
                        ErrorCode.ReaderOpenFailed,
                        "这本书内部文件数量异常，读不了。可能不是正常的 EPUB。",
                        "too many entries: " + zip.Entries.Count);
                }

                index = EpubPackageIndex.TryRead(zip, out var reason)
                    ?? throw new AppException(
                        ErrorCode.ReaderOpenFailed,
                        "这本书的结构读不出来，可能已经损坏。可以试试重新导入。",
                        reason);

                extracted = ExtractAll(zip, contentRoot, CancellationToken.None);
            }

            var manifest = BuildManifest(source, index);
            WriteManifest(tempRoot, manifest);
            ReplaceDirectory(tempRoot, cacheRoot);
            CopyAssets(cacheRoot);

            _log.LogInformation(
                "阅读缓存已建立：{BookId} 共 {Count} 章，解压 {Files} 个文件，目录 {Root}",
                source.Book.Id, manifest.Spine.Count, extracted, cacheRoot);

            return new ReaderSession
            {
                BookId = source.Book.Id,
                CacheRoot = cacheRoot,
                Manifest = manifest,
                SourceFormat = source.Format.Format,
                SourcePath = source.Format.FilePath,
                FromCache = false,
            };
        }
        catch
        {
            TryDeleteDirectory(tempRoot);
            throw;
        }
    }

    /// <summary>
    /// 固定版式（PDF / 漫画 CBZ-CBR）的会话：**不解包、不拷贝正文**。
    ///
    /// 它们都是固定版式，正文由自绘引擎按页向宿主拿（PDF 走 pdfium、漫画走 SharpCompress），
    /// 这里只做两件事：把外壳资源（reader.html/js/css）放进缓存目录，写一份清单说明
    /// "这本书是什么格式、原始文件在哪"。
    /// </summary>
    private ReaderSession BuildFixedLayout(
        Source source, string cacheRoot, string fileRoot, string pdfUrl, string formatName)
    {
        var tempRoot = cacheRoot + ".part-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            Directory.CreateDirectory(tempRoot);

            var info = new FileInfo(source.Format.FilePath);

            var manifest = new ReaderManifest
            {
                SchemaVersion = ManifestSchemaVersion,
                BookId = source.Book.Id.ToString("D"),
                Format = formatName,
                Title = string.IsNullOrWhiteSpace(source.Book.Title)
                    ? Path.GetFileNameWithoutExtension(source.Format.FilePath)
                    : source.Book.Title,
                Author = source.Book.Authors,
                Language = source.Book.Language,
                SourcePath = source.Format.FilePath,
                SourceSize = info.Exists ? info.Length : 0,

                /* Spine 与 Toc 都留空：PDF 没有"章"这个概念，目录要等 T6-2B 解析 outline。
                   阶段一的目录按钮本来就置灰，空着不会少功能。 */
            };

            WriteManifest(tempRoot, manifest);
            ReplaceDirectory(tempRoot, cacheRoot);
            CopyAssets(cacheRoot);

            _log.LogInformation(
                "{Format} 阅读缓存已建立（不解包）：{BookId} -> {Root}",
                formatName, source.Book.Id, cacheRoot);

            return new ReaderSession
            {
                BookId = source.Book.Id,
                CacheRoot = cacheRoot,
                Manifest = manifest,
                SourceFormat = source.Format.Format,
                SourcePath = source.Format.FilePath,
                FromCache = false,
                FileRoot = fileRoot,
                PdfUrl = pdfUrl,
            };
        }
        catch
        {
            TryDeleteDirectory(tempRoot);
            throw;
        }
    }

    /// <summary>把包里的文件全部解到 content/。返回解出的文件数。</summary>
    private static int ExtractAll(ZipArchive zip, string contentRoot, CancellationToken ct)
    {
        var count = 0;
        var root = Path.GetFullPath(contentRoot) + Path.DirectorySeparatorChar;

        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            // 目录项（以斜杠结尾）没有内容，跳过
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue;
            }

            // 索引阶段已对整包做过一次 Zip Slip 检查，这里再逐个挡一次
            if (EpubPackageIndex.IsUnsafeEntryName(entry.FullName) || entry.Length > MaxEntryBytes)
            {
                continue;
            }

            var relative = EpubPackageIndex.NormalizeEntryPath(entry.FullName)
                .Replace('/', Path.DirectorySeparatorChar);

            var target = Path.GetFullPath(Path.Combine(contentRoot, relative));

            // 双保险：算出来的绝对路径必须仍在 content/ 之内
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            using var input = entry.Open();

            // target 在 tempRoot 暂存目录里（目录级 staging），整棵目录就绪后才原子改名发布。
            using var output = new FileStream(
                target, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
            input.CopyTo(output, 81920);
            count++;
        }

        return count;
    }

    private static ReaderManifest BuildManifest(Source source, EpubPackageIndex index)
    {
        var basePath = ReaderEndpoints.ContentDirectoryName + "/" +
            (index.OpfDirectory.Length == 0 ? string.Empty : index.OpfDirectory + "/");

        var contentPrefix = ReaderEndpoints.ContentDirectoryName + "/";

        return new ReaderManifest
        {
            SchemaVersion = ManifestSchemaVersion,
            BookId = source.Book.Id.ToString("D"),
            Format = "EPUB",
            Title = string.IsNullOrWhiteSpace(index.Title) ? source.Book.Title : index.Title!,
            Author = index.Author ?? source.Book.Authors,
            Language = index.Language ?? source.Book.Language,
            BasePath = basePath,
            SourcePath = source.Format.FilePath,
            SourceSize = source.Format.FileSize,

            // 清单里的样式是"给宿主看的汇总"，章节自己也带着 link，
            // 所以 reader.html 不再重复加载它们（否则样式会叠两遍）。
            Styles = index.Styles.Select(s => contentPrefix + s).ToList(),

            Spine = index.Spine
                .Select((chapter, position) => new ReaderChapter
                {
                    Index = position,
                    Href = ChapterHref(position),
                    SourceHref = chapter.SourceHref,
                    Title = chapter.Title,
                })
                .ToList(),

            // T4-4：目录也一起物化进清单，页面直接拿来画，不用自己解 NCX/nav。
            Toc = index.Toc.ToList(),
            TocFallback = index.TocIsFallback,
        };
    }

    private static string ChapterHref(int index)
        => ReaderEndpoints.ChaptersDirectoryName + "/" + (index + 1).ToString("D4") + ".html";

    private static void WriteManifest(string cacheRoot, ReaderManifest manifest)
    {
        Directory.CreateDirectory(cacheRoot);
        var path = Path.Combine(cacheRoot, ReaderEndpoints.ManifestFileName);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        AtomicFile.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>把临时目录顶替成正式缓存目录。已有坏缓存时先删掉。</summary>
    private static void ReplaceDirectory(string source, string target)
    {
        if (Directory.Exists(target))
        {
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AppException(
                    ErrorCode.ReaderOpenFailed,
                    "这本书的阅读缓存正被占用，请关掉它的阅读窗口再试一次。",
                    "cannot delete stale cache: " + target,
                    ex);
            }
        }

        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        Directory.Move(source, target);
    }

    /// <summary>
    /// 把界面文件复制进缓存目录。每次打开都覆盖一次：
    /// 只有几 KB，代价可忽略，换来的是程序升级后界面立刻生效。
    /// </summary>
    private void CopyAssets(string cacheRoot)
    {
        var from = _assets.Directory;

        if (!Directory.Exists(from))
        {
            throw new AppException(
                ErrorCode.ReaderOpenFailed,
                "阅读器界面文件缺失，请重新解压一份完整程序后再试。",
                "reader assets not found: " + from);
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            var target = Path.Combine(cacheRoot, relative);
            var parent = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            // T7-2 判据 4：界面资源也是"发布后的文件"，原子替换，避免读到半截 reader.html。
            AtomicFile.Copy(file, target);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响主流程，留给下次覆盖
        }
    }
    // ---------- 章节物化 ----------

    public Task<string> MaterializeChapterAsync(
        ReaderSession session, int spineIndex, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (spineIndex < 0 || spineIndex >= session.Manifest.Spine.Count)
        {
            throw new AppException(
                ErrorCode.ReaderRenderFailed,
                "章节序号超出范围，已跳回第一章。",
                "spine index " + spineIndex + " out of range",
                context: new Dictionary<string, object?> { ["count"] = session.Manifest.Spine.Count });
        }

        var chapter = session.Manifest.Spine[spineIndex];
        var target = Path.Combine(
            session.CacheRoot, chapter.Href.Replace('/', Path.DirectorySeparatorChar));

        if (File.Exists(target))
        {
            return Task.FromResult(chapter.Href);
        }

        // TXT：不打开 zip，直接按字符范围把这一章取出来（判据 6）
        if (!string.IsNullOrEmpty(session.Manifest.TextEncoding))
        {
            return MaterializeTextChapter(session, chapter, target, session.Manifest.TextEncoding);
        }

        ct.ThrowIfCancellationRequested();

        using (var fs = new FileStream(
            ResolveContentArchive(session), FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            var entry = FindEntry(zip, chapter.SourceHref);

            if (entry is null)
            {
                throw new AppException(
                    ErrorCode.ReaderRenderFailed,
                    "这一章的内容在书里找不到，这本书的结构可能有问题。",
                    "entry not found: " + chapter.SourceHref);
            }

            if (entry.Length > MaxEntryBytes)
            {
                throw new AppException(
                    ErrorCode.ReaderRenderFailed,
                    "这一章的文件异常大，读不了。",
                    "entry too large: " + entry.Length);
            }

            string xml;
            using (var stream = entry.Open())
            using (var reader = new StreamReader(
                stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                xml = reader.ReadToEnd();
            }

            var normalized = EpubChapterNormalizer.Normalize(
                xml, chapter.SourceHref, ReaderEndpoints.ContentUrlPrefix);

            if (normalized is null)
            {
                throw new AppException(
                    ErrorCode.ReaderRenderFailed,
                    "这一章的内容无法渲染，可能不是正常的网页文件。",
                    "normalizer returned null: " + chapter.SourceHref);
            }

            if (normalized.Note is not null)
            {
                _log.LogWarning(
                    "章节规范化降级：{Source} 原因 {Note}", chapter.SourceHref, normalized.Note);
            }

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            WriteAtomicFile(target, normalized.Html);
        }

        return Task.FromResult(chapter.Href);
    }

    /// <summary>
    /// T5-2W 第三步：把工作区里改好的这一章重新物化进阅读缓存（覆盖旧文件）。
    /// 与 MaterializeChapterAsync 的差别：内容不从原书 zip 取（原书还没落盘），也不认缓存。
    /// </summary>
    public Task<string> RematerializeChapterAsync(
        ReaderSession session, int spineIndex, string xhtml, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(xhtml);
        ct.ThrowIfCancellationRequested();

        var chapter = RequireChapter(session, spineIndex);
        var normalized = EpubChapterNormalizer.Normalize(
            xhtml, chapter.SourceHref, ReaderEndpoints.ContentUrlPrefix);

        if (normalized is null)
        {
            throw new AppException(
                ErrorCode.ReaderRenderFailed,
                "这一章的内容无法渲染，可能不是正常的网页文件。",
                "normalizer returned null: " + chapter.SourceHref);
        }

        var target = Path.Combine(
            session.CacheRoot, chapter.Href.Replace('/', Path.DirectorySeparatorChar));
        var parent = Path.GetDirectoryName(target);

        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        WriteAtomicFile(target, normalized.Html);
        _log.LogInformation("章节已按工作区内容重新物化：{Href}", chapter.Href);
        return Task.FromResult(chapter.Href);
    }

    /// <summary>
    /// T5-2W：把工作区里这一章所在目录的文件同步进缓存 content/。
    /// 可视编辑新插入的图片在工作区里、缓存里没有，不同步的话重新加载后会是一张裂图。
    /// 只同步这一个目录、整份覆盖；路径越界或目录不存在就什么都不做。返回复制文件数。
    /// </summary>
    public Task<int> SyncChapterAssetsAsync(
        ReaderSession session, string workspaceRoot, string chapterEntry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(workspaceRoot);
        ArgumentNullException.ThrowIfNull(chapterEntry);

        var relativeDir = Path.GetDirectoryName(chapterEntry.Replace('/', Path.DirectorySeparatorChar));

        if (string.IsNullOrEmpty(relativeDir))
        {
            return Task.FromResult(0);
        }

        var workspaceFull = Path.GetFullPath(workspaceRoot);
        var contentFull = Path.GetFullPath(Path.Combine(session.CacheRoot, ReaderEndpoints.ContentDirectoryName));
        var sourceDir = Path.GetFullPath(Path.Combine(workspaceFull, relativeDir));
        var targetDir = Path.GetFullPath(Path.Combine(contentFull, relativeDir));

        if (!IsUnder(sourceDir, workspaceFull) || !IsUnder(targetDir, contentFull)
            || !Directory.Exists(sourceDir))
        {
            return Task.FromResult(0);
        }

        Directory.CreateDirectory(targetDir);

        var copied = 0;

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                AtomicFile.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)));
                copied++;
            }
            catch (IOException ex)
            {
                _log.LogWarning(ex, "同步章节素材失败：{File}", file);
            }
        }

        if (copied > 0)
        {
            _log.LogInformation("章节素材已同步 {Count} 个：{Dir}", copied, relativeDir);
        }

        return Task.FromResult(copied);
    }

    private static ReaderChapter RequireChapter(ReaderSession session, int spineIndex)
    {
        if (spineIndex < 0 || spineIndex >= session.Manifest.Spine.Count)
        {
            throw new AppException(
                ErrorCode.ReaderRenderFailed,
                "章节序号超出范围，已跳回第一章。",
                "spine index " + spineIndex + " out of range",
                context: new Dictionary<string, object?> { ["count"] = session.Manifest.Spine.Count });
        }

        return session.Manifest.Spine[spineIndex];
    }

    /// <summary>路径必须真的在 root 里面（防止工作区里的相对路径把人带出缓存目录）。</summary>
    private static bool IsUnder(string path, string root)
        => path.StartsWith(
            root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// 编辑保存后重读目录与元数据（**不碰**阅读缓存）：直接重新解源文件的 OPF / NCX / nav。
    /// </summary>
    public Task<ReaderMetadataSnapshot> ReadMetadataAsync(string sourcePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new AppException(
                ErrorCode.ReaderOpenFailed,
                "这本书的源文件不在了，先接受改动或重新导入，再刷新目录。",
                "source file missing");
        }

        var index = EpubPackageIndex.TryRead(sourcePath, out var reason);

        if (index is null)
        {
            throw new AppException(
                ErrorCode.ReaderOpenFailed,
                "重新读取这本书的目录失败：" + (reason ?? "不是有效的 EPUB"),
                "metadata reload failed");
        }

        return Task.FromResult(new ReaderMetadataSnapshot(
            index.Title ?? string.Empty,
            index.Author,
            index.Toc.ToList(),
            index.TocIsFallback,
            index.Spine.Count));
    }

    public async Task<bool> InvalidateAsync(Guid bookId, CancellationToken ct = default)
    {
        var source = await ResolveSourceAsync(bookId, ct).ConfigureAwait(false);
        var cacheRoot = Path.Combine(_paths.ReaderCacheDir, source.Hash);

        var deleted = false;

        if (Directory.Exists(cacheRoot))
        {
            try
            {
                Directory.Delete(cacheRoot, recursive: true);
                _log.LogInformation("阅读缓存已删除：{Root}", cacheRoot);
                deleted = true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "删除阅读缓存失败：{Root}", cacheRoot);
            }
        }

        // T6-1：MOBI/AZW3 的转换产物与内容缓存同生共死，别留下孤儿 EPUB。
        var artifact = _epubCache.GetArtifactPath(source.Hash);
        if (File.Exists(artifact))
        {
            try
            {
                File.Delete(artifact);
                _log.LogInformation("转换缓存 EPUB 已删除：{Path}", artifact);
                deleted = true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "删除转换缓存 EPUB 失败：{Path}", artifact);
            }
        }

        return deleted;
    }

    /// <summary>
    /// 用户 2026-10-21：换格式继续读时的「文字精确找位」。
    /// 在目标书的章节里找出包含给定文字的那一章，返回章号（0 基）；找不到返回 null。
    /// </summary>
    /// <remarks>
    /// 为什么不整本扫：几百章的书全量物化要好几秒，而用户只是换了个格式。
    /// 起点章由"已读百分比"估出来，通常第一章就命中；命中即返回，最多看 maxChapters 章兜底。
    /// 章节物化本来就是读这本书的常规操作（翻到哪章物化哪章），这里只是替用户先把要找的几章翻出来。
    /// </remarks>
    public async Task<int?> FindChapterByTextAsync(
        ReaderSession session,
        string text,
        int hintIndex,
        int maxChapters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var needle = NormalizeForSearch(text);

        // 太短的字符串到处都是，拿它找位只会落到错处；宁可退回按百分比。
        if (needle.Length < 6)
        {
            return null;
        }

        var count = session.Manifest.Spine.Count;

        if (count == 0)
        {
            return null;
        }

        hintIndex = Math.Clamp(hintIndex, 0, count - 1);
        var budget = Math.Clamp(maxChapters, 1, count);

        foreach (var index in SearchOrder(hintIndex, count, budget))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var href = await MaterializeChapterAsync(session, index, ct).ConfigureAwait(false);
                var file = Path.Combine(
                    session.CacheRoot, href.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(file))
                {
                    continue;
                }

                var plain = NormalizeForSearch(HtmlToPlainText(File.ReadAllText(file, Encoding.UTF8)));

                if (plain.Contains(needle, StringComparison.Ordinal))
                {
                    _log.LogInformation(
                        "跨格式找位：文字在第 {Index} 章命中（共 {Count} 章）", index + 1, count);
                    return index;
                }
            }
            catch (AppException ex) when (ex.Code == ErrorCode.ReaderRenderFailed)
            {
                // 某一章坏了不该让整次定位失败：跳过它继续看下一章
                _log.LogWarning(ex, "跨格式找位：第 {Index} 章物化失败，跳过", index);
            }
        }

        _log.LogInformation("跨格式找位：{Budget} 章内没找到这段文字，退回按百分比定位", budget);
        return null;
    }

    /// <summary>按"离 hint 越近越先看"的顺序列出章号，最多 budget 个。</summary>
    private static List<int> SearchOrder(int hint, int count, int budget)
    {
        var order = new List<int>(Math.Min(budget, count));
        var seen = new bool[count];

        for (var distance = 0; order.Count < budget && distance < count; distance++)
        {
            var forward = hint + distance;
            var backward = hint - distance;

            if (forward < count && !seen[forward])
            {
                seen[forward] = true;
                order.Add(forward);
            }

            if (order.Count >= budget)
            {
                break;
            }

            if (backward >= 0 && !seen[backward])
            {
                seen[backward] = true;
                order.Add(backward);
            }
        }

        return order;
    }

    /// <summary>物化后的 HTML 去掉脚本/样式与标签，剩下的就是页面上的可见文字（近似）。</summary>
    private static string HtmlToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var stripped = ScriptStyleRegex.Replace(html, " ");
        stripped = TagRegex.Replace(stripped, " ");

        return WebUtility.HtmlDecode(stripped);
    }

    /// <summary>与 reader.js 的 normalizeText 同口径，保证指纹能对上。</summary>
    private static string NormalizeForSearch(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalized = WhitespaceRegex.Replace(text, string.Empty);

        try
        {
            normalized = normalized.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // 极少数组合字符没有 NFKC 形式，保持原样即可
        }

        return normalized;
    }

    /// <summary>
    /// TXT 建缓存（T4-5）：识别编码 -&gt; 流式切章 -&gt; 写清单。
    /// 正文不抄进 content/：章节内容在 MaterializeChapterAsync 里按字符范围现取（判据 6）。
    /// </summary>
    private ReaderSession BuildText(Source source, string cacheRoot, string? encodingOverride)
    {
        var tempRoot = cacheRoot + ".part-" + Guid.NewGuid().ToString("N")[..8];
        var contentRoot = Path.Combine(tempRoot, ReaderEndpoints.ContentDirectoryName);

        try
        {
            // content/ 是清单有效性的判据之一（TryLoadManifest 会检查），TXT 也建出来，只是空着
            Directory.CreateDirectory(contentRoot);

            var detected = TextEncodingDetector.Detect(source.Format.FilePath);
            var encoding = TextEncodingSupport.Resolve(encodingOverride) ?? detected.Encoding;
            var name = encodingOverride ?? detected.Name;
            var confident = encodingOverride is not null || detected.Confident;

            // BOM 长度必须按"最终选定的编码"算：用户手选编码后不能沿用自动识别的结论
            var bomLength = TextEncodingDetector.BomLengthFor(source.Format.FilePath, encoding);

            var plan = TextChapterSplitter.Split(source.Format.FilePath, encoding, bomLength);
            var manifest = BuildTextManifest(source, plan, name, confident, bomLength);

            WriteManifest(tempRoot, manifest);
            ReplaceDirectory(tempRoot, cacheRoot);
            CopyAssets(cacheRoot);

            _log.LogInformation(
                "TXT 阅读缓存已建立：{BookId} 共 {Count} 章（编码 {Encoding}，切法 {Split}），目录 {Root}",
                source.Book.Id, manifest.Spine.Count, name,
                plan.UsedFallback ? "硬切兜底" : plan.Pattern, cacheRoot);

            return new ReaderSession
            {
                BookId = source.Book.Id,
                CacheRoot = cacheRoot,
                Manifest = manifest,
                SourceFormat = source.Format.Format,
                SourcePath = source.Format.FilePath,
                FromCache = false,
            };
        }
        catch
        {
            TryDeleteDirectory(tempRoot);
            throw;
        }
    }

    private static ReaderManifest BuildTextManifest(
        Source source, TextChapterPlan plan, string encodingName, bool confident, int bomLength)
    {
        var chapters = plan.Chapters
            .Select((chapter, position) => new ReaderChapter
            {
                Index = position,
                Href = ChapterHref(position),
                SourceHref = "txt:" + chapter.StartChar + "-" + chapter.EndChar,
                Title = chapter.Title,
                TextStart = chapter.StartChar,
                TextEnd = chapter.EndChar,
            })
            .ToList();

        var title = string.IsNullOrWhiteSpace(source.Book.Title)
            ? Path.GetFileNameWithoutExtension(source.Format.FilePath)
            : source.Book.Title;

        return new ReaderManifest
        {
            SchemaVersion = ManifestSchemaVersion,
            BookId = source.Book.Id.ToString("D"),
            Format = "TXT",
            Title = title,
            Author = source.Book.Authors,
            Language = source.Book.Language,
            BasePath = ReaderEndpoints.ChaptersDirectoryName + "/",
            SourcePath = source.Format.FilePath,
            SourceSize = source.Format.FileSize,
            Styles = new List<string>(),

            // 目录直接用切出来的章节：TXT 没有 NCX/nav，硬切兜底时同样给一份（判据 5 也要能读）
            Toc = plan.Chapters
                .Select((chapter, position) => new TocEntry
                {
                    Title = chapter.Title,
                    Level = 1,
                    SpineIndex = position,
                    SourceHref = "txt:" + position,
                })
                .ToList(),
            TocFallback = plan.UsedFallback,
            Spine = chapters,

            TextEncoding = encodingName,
            TextEncodingConfident = confident,
            TextBomLength = bomLength,
            TextFallbackSplit = plan.UsedFallback,
        };
    }

    /// <summary>物化一章 TXT：只把这一章的字符范围读出来（判据 6：不整本读进内存）。</summary>
    private Task<string> MaterializeTextChapter(
        ReaderSession session, ReaderChapter chapter, string target, string encodingName)
    {
        var encoding = TextEncodingSupport.Resolve(encodingName)
            ?? throw new AppException(
                ErrorCode.ReaderRenderFailed,
                "这本书记录的文本编码不认识了，换个编码重新打开试试。",
                "unknown text encoding: " + encodingName);

        var text = TxtChapterHtml.ReadRange(
            session.SourcePath,
            encoding,
            session.Manifest.TextBomLength,
            chapter.TextStart,
            chapter.TextEnd);

        var html = TxtChapterHtml.Build(chapter.Title ?? string.Empty, text);

        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        WriteAtomicFile(target, html);
        return Task.FromResult(chapter.Href);
    }

    // ---------- 工具 ----------

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string packagePath)
    {
        var wanted = EpubPackageIndex.NormalizeEntryPath(packagePath);

        return zip.Entries.FirstOrDefault(e => string.Equals(
            EpubPackageIndex.NormalizeEntryPath(e.FullName),
            wanted,
            StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 先写 .tmp 再改名。半截文件不会被 WebView2 读到
    /// （iframe 请求的瞬间恰好撞上写一半的章节时，会渲染出残缺内容）。
    /// </summary>
    private static void WriteAtomicFile(string target, string content)
    {
        var temp = target + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, target, overwrite: true);
    }
}