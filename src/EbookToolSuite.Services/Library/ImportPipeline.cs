using System.Diagnostics;
using System.Globalization;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Kindle;
using EbookToolSuite.Engines.Pdf;
using EbookToolSuite.Infrastructure.Files;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Library;

/// <summary>
/// 单文件导入流水线（执行计划 T1-2 / 设计文档 7.1.1 的阶段 1 精简版）。
/// </summary>
/// <remarks>
/// 【流程顺序】探测(只读) 到 指纹 到 去重 到 复制到暂存 到 移入正式区 到 封面 到 写库 到 补偿。
/// 写库放最后，任何一步失败都把前面产生的文件删干净，绝不留孤儿。
///
/// 【与设计文档 7.1.1 的差别】7.1.1 的完整批量管线是
/// "暂存 到 DB 事务 到 正式区"（靠 book_formats.missing=1 兜底移动失败）。
/// T1-2 只有单文件，用不到 batch，改成"先落盘再写库 + 显式补偿"，
/// 好处是 happy path 上不会产生 missing=1 的书。T2-1 做批量队列时
/// 再切回 7.1.1 的顺序并引入 import_batch。
///
/// 【不做的事】不生成 import_batch（T2-1）、不写 operation_log（T3-6）、
/// 不填 word_count / chapter_count / title_sort（T1-3 与 T3 的元数据编辑）。
/// </remarks>
public sealed class ImportPipeline : IBookImportService
{
    /// <summary>复制缓冲区。1MB 在机械盘与 SSD 上都是合理值。</summary>
    private const int CopyBufferSize = 1024 * 1024;

    private readonly IAppPaths _paths;
    private readonly IEpubProbe _probe;
    private readonly IImageService _images;
    private readonly IHashUtil _hasher;
    private readonly IBookRepository _repo;
    private readonly IDuplicateRepository _duplicates;
    private readonly IClock _clock;
    private readonly ILogger<ImportPipeline> _log;

        // T6-2：PDF 的首页渲染（封面＝首页，判据 6）。
    private readonly IPdfDocumentService _pdfPages;

    /// <summary>T6-3：CBZ/CBR 的探测与首页封面渲染。</summary>
    private readonly IComicBookService _comics;

    public ImportPipeline(
        IAppPaths paths,
        IEpubProbe probe,
        IImageService images,
        IHashUtil hasher,
        IBookRepository repo,
        IDuplicateRepository duplicates,
        IClock clock,
        ILogger<ImportPipeline> log,
        IPdfDocumentService pdfPages,
        IComicBookService comics)
    {
        _paths = paths;
        _probe = probe;
        _images = images;
        _hasher = hasher;
        _repo = repo;
        _duplicates = duplicates;
        _clock = clock;
        _log = log;
        _pdfPages = pdfPages;
        _comics = comics;
    }

    public async Task<ImportResult> ImportAsync(
        string path,
        ImportOptions options,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(path))
        {
            return ImportResult.Unreadable(path ?? "", "没有指定文件。");
        }

        string source;
        try
        {
            source = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            return ImportResult.Unreadable(path, "路径无效。", ex);
        }

        var fileName = Path.GetFileName(source);

        // ---------- 1. 源文件与扩展名 ----------
        FileInfo sourceInfo;
        try
        {
            sourceInfo = new FileInfo(source);
            if (!sourceInfo.Exists)
            {
                return ImportResult.Unreadable(source, "文件不存在或已被移动。");
            }
        }
        catch (Exception ex)
        {
            return ImportResult.Unreadable(source, "无法访问这个文件。", ex);
        }

        // T4-5：TXT 也能导入。T6-1：MOBI/AZW3 也能导入（否则内置阅读器里根本没有可打开的 MOBI）。
        // T6-2：PDF 也能导入（阶段一借 WebView2 渲染，见设计文档 §6.5）。
        // T6-3：CBZ/CBR（漫画）也能导入（自绘页位图引擎，见设计文档 §6.6）。
        // 其余格式（FB2）仍不收，提示用户先转换。
        var kind = BookFormatKinds.FromExtension(source);

        if (kind is not (BookFormatKind.Epub or BookFormatKind.Txt
            or BookFormatKind.Mobi or BookFormatKind.Azw3 or BookFormatKind.Pdf
            or BookFormatKind.Cbz))
        {
            return ImportResult.Unsupported(source);
        }

        // ---------- 2. 只读探测（不产生任何文件） ----------
        Report(progress, fileName, ImportPhase.Probing);

        EpubProbeResult sensed;
        try
        {
            if (kind == BookFormatKind.Txt)
            {
                var length = new FileInfo(source).Length;

                if (length == 0)
                {
                    return ImportResult.Invalid(source, "这个 TXT 文件是空的。");
                }

                /* TXT 没有 OPF 可探测：元数据先用文件名顶上；
                   编码识别与切章留到阅读器打开时做（ReaderService，T4-5）。 */
                sensed = new EpubProbeResult
                {
                    SourcePath = source,
                    FileSize = length,
                    Title = Path.GetFileNameWithoutExtension(source),
                };
            }
            else if (kind is BookFormatKind.Mobi or BookFormatKind.Azw3)
            {
                /* T6-1：MOBI/AZW3 没有 OPF 可探测。这里只做结构 + DRM 体检；
                   元数据先用文件名，正文转换与封面交给 Calibre（打开时惰性转换，见 EpubCacheService）。 */
                var length = new FileInfo(source).Length;

                if (length == 0)
                {
                    return ImportResult.Invalid(source, "这个文件是空的。");
                }

                MobiProbe.Validate(source);

                sensed = new EpubProbeResult
                {
                    SourcePath = source,
                    FileSize = length,
                    Title = Path.GetFileNameWithoutExtension(source),
                };
            }
            else if (kind == BookFormatKind.Pdf)
            {
                /* T6-2：PDF 不做深度解析——页数与首页封面交给 pdfium（§6.5.4 的封面阶段）。
                   这里只确认"真的是 PDF"，并如实记下加密与截断；**两者都不阻止导入**：
                   加密交给 Chromium 自己弹密码框，截断由打开失败面板 + 外部打开兜底。 */
                var pdf = PdfProbe.Inspect(source);

                if (!pdf.LooksLikePdf)
                {
                    // 与"无法识别的电子书格式"同类：扩展名是 .pdf，内容却不是 PDF
                    _log.LogInformation("扩展名是 PDF 但头 1 KB 里没有 %PDF-，按不支持格式跳过：{File}", source);
                    return ImportResult.Unsupported(source);
                }

                if (pdf.IsEncrypted)
                {
                    _log.LogWarning("PDF 带 /Encrypt（加密或权限受限），仍允许导入：{File}", source);
                }

                if (pdf.IsTruncated)
                {
                    _log.LogWarning("PDF 尾部没有 %%EOF，疑似被截断，仍允许导入：{File}", source);
                }

                sensed = new EpubProbeResult
                {
                    SourcePath = source,
                    FileSize = pdf.FileSize,
                    Title = Path.GetFileNameWithoutExtension(source),

                    /* T6-2 判据 6：书库封面必须是"首页渲染图"，不是占位图。
                       就在这里渲——导入是唯一一次我们确定要付这个代价的地方。 */
                    CoverBytes = options.ExtractCover
                        ? RenderPdfCoverOrNull(source, options.CoverFullSizePx)
                        : null,
                };
            }
            else if (kind == BookFormatKind.Cbz)
            {
                /* T6-3：CBZ/CBR 只做只读探测——不解包、不读图，只确认"能列出图片、多少页"。
                   ★ 计划 T6-3 的坑：不支持的要明确提示，**不要静默失败**。
                   所以"压缩包打不开（加密/固实/损坏）"走 Invalid 并带上原因；
                   只有"根本不是能列出图片的压缩包"才走 Unsupported（与格式无关的乱文件）。 */
                var comic = _comics.Probe(source);

                if (!comic.LooksLikeComic)
                {
                    _log.LogInformation(
                        "扩展名是 CBZ/CBR 但不是能列出图片的压缩包，按不支持格式跳过：{File}", source);
                    return ImportResult.Unsupported(source);
                }

                if (comic.Error is not null)
                {
                    _log.LogWarning("漫画包打不开：{File}：{Reason}", source, comic.Error);
                    return ImportResult.Invalid(source, comic.Error);
                }

                if (comic.IsEncrypted)
                {
                    _log.LogWarning("漫画包带密码保护，仍允许导入：{File}", source);
                }

                sensed = new EpubProbeResult
                {
                    SourcePath = source,
                    FileSize = comic.FileSize,

                    /* 包内 ComicInfo.xml 优先；没有（或解析失败）时兜底用文件名，
                       与 TXT / PDF 的「先拿文件名顶上」同一口径。
                       Authors 取 Writer ?? Penciller：多数漫画只有画师署名，
                       只认 Writer 会让作者一栏大面积为空。 */
                    Title = FirstNonEmpty(comic.Metadata?.Title, Path.GetFileNameWithoutExtension(source)),
                    Authors = comic.Metadata?.Writer ?? comic.Metadata?.Penciller,
                    Publisher = comic.Metadata?.Publisher,
                    Description = comic.Metadata?.Summary,
                    Language = comic.Metadata?.LanguageISO,
                    Series = comic.Metadata?.Series,
                    SeriesIndex = ParseSeriesIndex(comic.Metadata?.Number),
                    PublishedDate = BuildPublishedDate(comic.Metadata),
                    Isbn = comic.Metadata?.Isbn,

                    /* 与 PDF 判据同一口径：书库封面用第 1 页渲染图；失败返回 null 走占位图，
                       不阻塞导入。 */
                    CoverBytes = options.ExtractCover
                        ? _comics.RenderCover(source, options.CoverFullSizePx)
                        : null,
                };
            }
            else
            {
                sensed = await _probe.ProbeAsync(source, options.ExtractCover, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return ImportResult.Canceled(source);
        }
        catch (AppException ex)
        {
            _log.LogWarning("探测失败 {Code} {File}: {Message}", ex.Code, source, ex.Message);
            return MapProbeFailure(source, ex);
        }
        catch (Exception ex)
        {
            // 探测器本该把所有异常包成 AppException；这里兜底，防止漏网的实现缺陷拖垮队列
            _log.LogError(ex, "探测器抛出未包装异常 {File}", source);
            return ImportResult.Invalid(source, "读取这本书时发生意外错误。", ex);
        }

        if (sensed.Damaged)
        {
            // 宽松兜底读进来的：书能用，但没通过严格规范校验，日志里留一笔（设计文档 6.1.9）
            _log.LogWarning("这本书没通过严格规范校验，已用宽松兜底读入：{File}", source);
        }

        // ---------- 3. 快速指纹 ----------
        Report(progress, fileName, ImportPhase.Hashing);

        string fingerprint;
        if (options.KnownQuickFingerprint is { Length: > 0 } knownFingerprint)
        {
            // T2-1：队列预检阶段已经算过这一本的指纹，直接用，
            // 200 本书能省掉 200 次 128KB 读取（约 25MB 的重复 IO）。
            fingerprint = knownFingerprint;
        }
        else
        {
            try
            {
                fingerprint = await _hasher.QuickFingerprintAsync(source, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ImportResult.Canceled(source);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "算指纹失败 {File}", source);
                return ImportResult.Failed(source, "读取文件内容失败。", ex, stopwatch.Elapsed);
            }
        }

        // ---------- 4. 去重 ----------
        string? contentHash = null;
        if (!options.ImportDuplicates)
        {
            DuplicateCandidateRow? existing = null;
            try
            {
                existing = await _duplicates.FindByFingerprintAsync(fingerprint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ImportResult.Canceled(source);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "去重查询失败 {File}", source);
                return ImportResult.Failed(source, "查询书库失败。", ex, stopwatch.Elapsed);
            }

            if (existing is not null)
            {
                DuplicateVerdict verdict;
                try
                {
                    verdict = await ConfirmDuplicateAsync(existing, source, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return ImportResult.Canceled(source);
                }

                contentHash = verdict.Hash;

                if (verdict.IsDuplicate)
                {
                    _log.LogInformation("跳过重复导入 {File} 已在库中 {BookId}", source, existing.BookId);
                    return ImportResult.Duplicate(source, existing.BookId, "这本书已经在库中了。");
                }

                _log.LogWarning(
                    "快速指纹相同但全文件哈希不同（指纹碰撞），按新书导入 {File} 库里已有 {BookId}",
                    source, existing.BookId);
            }
        }

        // ---------- 5. 定路径 ----------
        _paths.EnsureCreated();

        var bookId = Guid.NewGuid();
        var libraryPath = PathUtil.BuildLibraryPath(
            _paths.LibraryDir, bookId, Path.GetFileName(source), kind.ToExtension());
        var stagingPath = Path.Combine(_paths.TempDir, "import-" + bookId.ToString("N") + ".part");
        var coverFullPath = Path.Combine(_paths.CoversCacheDir, bookId.ToString("D") + ".jpg");
        var coverThumbPath = Path.Combine(_paths.CoversCacheDir, bookId.ToString("D") + "_t.jpg");

        var movedToLibrary = false;
        var committed = false;

        void Compensate()
        {
            if (committed)
            {
                return;
            }

            TryDelete(stagingPath);

            if (movedToLibrary)
            {
                TryDelete(libraryPath);
                TryDeleteEmptyDirectory(Path.GetDirectoryName(libraryPath));
            }

            TryDelete(coverFullPath);
            TryDelete(coverThumbPath);
        }

        try
        {
            // ---------- 6. 复制到暂存区 ----------
            Report(progress, fileName, ImportPhase.Copying);

            /* T7-2 第 2 项：先做磁盘空间预检，空间不够就在动任何文件之前明确拒绝。
               经验系数 2.0 与转换一致（复制 + 封面 + 可能的临时文件）。 */
            DiskSpaceGuard.Ensure(_paths.TempDir, sourceInfo.Length, 2.0);
            DiskSpaceGuard.Ensure(_paths.LibraryDir, sourceInfo.Length, 2.0);

            await CopyFileAsync(source, stagingPath, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // ---------- 7. 移入正式区 ----------
            // 同卷改名，极快；放写库之前，这样写库失败时只需要删一个文件。
            var libraryFolder = Path.GetDirectoryName(libraryPath)!;
            /* 替换书籍：用户选了"替换"，先把库里那本旧的删掉（DB 行 + 旧文件目录），
         再让本次文件按新书导入（同一本书在库里只会剩一本）。 */
if (options.ReplaceBookId is Guid replacedId && replacedId != Guid.Empty)
        {
            await DeleteReplacedBookAsync(replacedId, ct).ConfigureAwait(false);

            /* 同一本书在一批里出现多次时：前一个 job 已经"替换"出了同指纹的新书，
               本 job 记录里的 DuplicateOfBookId 还是最旧那本（已不存在）-> 再删一次落空 -> 会多导入一份。
               所以按指纹再查一次，把同指纹的那本也删掉。 */
            /* 库里可能有多份同指纹的副本（反复导入造成的），全部清掉：
               否则"替换"只会替换掉其中一份，旧的其它副本仍然留着 -> 用户看到还是多本。
               每轮删一本，删到查不到为止（最多 20 次，防死循环）。 */
            for (var guard = 0; guard < 20; guard++)
            {
                DuplicateCandidateRow? more;

                try
                {
                    more = await _duplicates.FindByFingerprintAsync(fingerprint, ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    break;
                }

                if (more is null)
                {
                    break;
                }

                await DeleteReplacedBookAsync(more.BookId, ct).ConfigureAwait(false);
            }
        }

      Directory.CreateDirectory(libraryFolder);
            File.Move(stagingPath, libraryPath);
            movedToLibrary = true;

            // ---------- 8. 封面 ----------
            string? coverPathForDb = null;
            if (options.ExtractCover && sensed.CoverBytes is { Length: > 0 })
            {
                Report(progress, fileName, ImportPhase.Cover);

                var wrote = await _images.WriteCoverVariantsAsync(
                    sensed.CoverBytes,
                    coverFullPath,
                    coverThumbPath,
                    options.CoverFullSizePx,
                    options.CoverThumbWidthPx,
                    ct).ConfigureAwait(false);

                if (wrote)
                {
                    coverPathForDb = coverFullPath;
                }
                else
                {
                    // 封面解不开不是错误：库里留空，UI 显示占位图
                    _log.LogWarning("封面无法解码，改用占位图 {File}", source);
                }
            }

            // ---------- 9. 写库（单事务） ----------
            Report(progress, fileName, ImportPhase.Committing);

            // 写库时记的是**正式区这个文件**的真实体积与修改时间：
            // 它才是 T2-6 扫描时拿来比对的基准，用源文件的信息会一扫描就报"被外部修改"。
            var libraryInfo = new FileInfo(libraryPath);
            var copiedSize = libraryInfo.Length;

            var book = BuildBook(
                bookId, sensed, fingerprint, contentHash, libraryPath, copiedSize,
                libraryInfo.LastWriteTimeUtc, coverPathForDb, options.BatchId);

            await _repo.AddAsync(book, ct).ConfigureAwait(false);
            committed = true;

            Report(progress, fileName, ImportPhase.Done);
            _log.LogInformation(
                "导入成功 {BookId} {Title} {Bytes} 字节 耗时 {Ms} ms {File}",
                bookId, sensed.Title, copiedSize, stopwatch.ElapsedMilliseconds, source);

            return ImportResult.Ok(source, bookId, libraryPath, stopwatch.Elapsed, replaced: options.ReplaceBookId is Guid);
        }
        catch (OperationCanceledException)
        {
            Compensate();
            _log.LogInformation("导入已取消并回滚 {File}", source);
            return ImportResult.Canceled(source);
        }
        catch (Exception ex)
        {
            Compensate();
            _log.LogError(ex, "导入失败 {File}", source);

            /* T7-2 第 1/2/3 项：导入阶段的 IO 失败按"发生了什么 + 为什么 + 怎么办"
               翻译成中文。补偿已经执行，暂存文件、正式区文件、封面都清掉了。 */
            var translated = FileErrors.Translate(
                ex, "导入《" + Path.GetFileNameWithoutExtension(fileName) + "》", source);

            return ImportResult.Failed(source, translated.UserMessage, ex, stopwatch.Elapsed);
        }
    }

    // ---------- 组装 ----------

private async Task DeleteReplacedBookAsync(Guid bookId, CancellationToken ct)
      {
          var existing = await _repo.GetByIdAsync(bookId, ct).ConfigureAwait(false);

          if (existing is null)
          {
              return;
          }

          foreach (var format in existing.Formats)
          {
              if (string.IsNullOrWhiteSpace(format.FilePath))
              {
                  continue;
              }

              TryDelete(format.FilePath);
              TryDeleteEmptyDirectory(Path.GetDirectoryName(format.FilePath));
          }

          await _repo.DeleteAsync(bookId, ct).ConfigureAwait(false);
      }

      private Book BuildBook(
        Guid bookId,
        EpubProbeResult sensed,
        string fingerprint,
        string? contentHash,
        string libraryPath,
        long fileSize,
        DateTime lastWriteUtc,
        string? coverPath,
        Guid? batchId)
    {
        var now = _clock.Now;

        return new Book
        {
            Id = bookId,
            Title = sensed.Title,
            Authors = sensed.Authors,
            Publisher = sensed.Publisher,
            Description = sensed.Description,
            Language = sensed.Language,
            Isbn = sensed.Isbn,
            Series = sensed.Series,
            SeriesIndex = sensed.SeriesIndex,
            PublishedDate = sensed.PublishedDate,
            Identifier = sensed.Identifier,
            // T2-1：批量导入要能事后查"这批书是哪来的"
            ImportBatchId = batchId,
            CoverPath = coverPath,
            // 宽松兜底读出来的书要标脏：UI 上显示"未通过严格校验"角标（设计文档 6.1.9）
            Damaged = sensed.Damaged,
            CreatedAt = now,
            UpdatedAt = now,
            Formats =
            {
                new BookFormat
                {
                    BookId = bookId,
                    // T4-5：TXT 入库要记 TXT，否则阅读器会按 EPUB 去解压
                    Format = BookFormatKinds.FromExtension(sensed.SourcePath),
                    FilePath = libraryPath,
                    FileSize = fileSize,
                    QuickFingerprint = fingerprint,
                    // T2-2：指纹命中时复核过，顺手写回，省得重复检测再算一遍
                    FullHash = contentHash,
                    FileModifiedAt = new DateTimeOffset(lastWriteUtc, TimeSpan.Zero),
                    Missing = false,
                    AddedAt = now,
                },
            },
        };
    }

    /// <summary>
    /// T6-2 判据 6：用 pdfium 把第 1 页渲成封面。
    ///
    /// 失败**不算导入失败**——设计文档 §6.5.4 明确"加密或渲染失败 → cover_source = placeholder，
    /// 书库仍可见、不阻塞导入"。所以这里把异常吃掉并记 WRN，返回 null 让上层走占位图。
    /// </summary>
    private byte[]? RenderPdfCoverOrNull(string source, int coverFullSizePx)
    {
        // 渲染长边跟着封面管线的旋钮走，但夹一次：太小看不清、太大白烧内存（§6.4 的上限是 1200）
        var longEdge = Math.Clamp(coverFullSizePx, 320, 1600);

        try
        {
            return _pdfPages.RenderPage(source, 0, longEdge);
        }
        catch (Exception ex) when (ex is AppException or IOException)
        {
            _log.LogWarning(ex, "PDF 首页渲染失败，这本改用占位图：{File}", source);
            return null;
        }
    }

    private static ImportResult MapProbeFailure(string source, AppException ex) => ex.Code switch
    {
        ErrorCode.DrmDetected => ImportResult.Drm(source),
        ErrorCode.FileNotFound => ImportResult.Unreadable(source, ex.UserMessage, ex),
        ErrorCode.InvalidPath => ImportResult.Unreadable(source, ex.UserMessage, ex),
        ErrorCode.FileLocked => ImportResult.Unreadable(source, ex.UserMessage, ex),
        ErrorCode.AccessDenied => ImportResult.Unreadable(source, "文件被占用或没有读取权限。" + ex.UserMessage, ex),
        _ => ImportResult.Invalid(source, ex.UserMessage, ex),
    };

    // ---------- 漫画元数据转换（T6-3 / ComicInfo.xml） ----------

    /// <summary>空字符串一律视为「没填」：ComicInfo.xml 里空元素没有意义，落库也用 null 更干净。</summary>
    private static string FirstNonEmpty(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>
    /// ComicInfo 的 Number 是字符串（"1"、"1.5" 都合法），书库的 series_index 是 double。
    /// 解析不了（"SP/2" 这类）就放弃序号，不拿 0 冒充。
    /// </summary>
    private static double? ParseSeriesIndex(string? number)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            return null;
        }

        return double.TryParse(
            number.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value)
                ? value
                : null;
    }

    /// <summary>
    /// ComicInfo 把出版日期拆成 Year / Month / Day 三个可选整数，书库只存一个 DateOnly。
    /// 只有年时补 1 月 1 日：书库按年显示，编造具体月份反而会误导。
    /// </summary>
    private static DateOnly? BuildPublishedDate(ComicMetadata? metadata)
    {
        if (metadata?.Year is not int year || year is < 1 or > 9999)
        {
            return null;
        }

        var month = metadata.Month is int m and >= 1 and <= 12 ? m : 1;
        var day = metadata.Day is int d && d >= 1 && d <= DateTime.DaysInMonth(year, month) ? d : 1;

        return new DateOnly(year, month, day);
    }

    // ---------- IO 小工具 ----------

    private static async Task CopyFileAsync(string source, string destination, CancellationToken ct)
    {
        var sourceModified = File.GetLastWriteTimeUtc(source);

        // 用一层显式作用域把两个流关掉：下面 SetLastWriteTimeUtc 要独占打开文件，
        // 而 dst 是以 FileShare.None 打开的，流没关会直接抛共享冲突。
        {
            await using var src = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.Read,
                CopyBufferSize, useAsync: true);

            // CreateNew：万一暂存名撞了就报错，绝不覆盖（比 Create 安全）
            // destination 是 Temp 下的 import-*.part 暂存文件，复制完成后才改名进正式区。
            await using var dst = new FileStream(
                destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                CopyBufferSize, useAsync: true);

            await src.CopyToAsync(dst, CopyBufferSize, ct).ConfigureAwait(false);
            await dst.FlushAsync(ct).ConfigureAwait(false);
        }

        TrySetLastWriteTimeUtc(destination, sourceModified);
    }

    /// <summary>
    /// 把修改时间搬过去。流式复制不会带修改时间：不补这一步，正式区文件的日期就是
    /// "导入那一刻"，而书库记的是原文件的时间，T2-6 一扫描就会把刚导入的书判成"被外部修改"。
    /// </summary>
    private static void TrySetLastWriteTimeUtc(string path, DateTime lastWriteUtc)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
        }
        catch (Exception)
        {
            // 设不上不影响导入：写库时记的是正式区文件的真实时间（见 BuildBook 的调用点）
        }
    }

    private static void Report(IProgress<ImportProgress>? progress, string fileName, ImportPhase phase)
        => progress?.Report(new ImportProgress(1, 1, fileName, phase));

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
            // 补偿失败只影响临时空间，不改变用户看到的结果
        }
    }

    private static void TryDeleteEmptyDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path) &&
                !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception)
        {
            // 同上
        }
    }

    /// <summary>复核「快速指纹命中」到底是不是同一本书。指纹会碰撞，所以必须用全文件哈希确证。</summary>
    private async Task<DuplicateVerdict> ConfirmDuplicateAsync(
        DuplicateCandidateRow existing,
        string source,
        CancellationToken ct)
    {
        string sourceHash;
        try
        {
            sourceHash = await _hasher.ComputeContentHashAsync(source, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "复核重复时算源文件哈希失败，按重复处理 {File}", source);
            return new DuplicateVerdict(true, null);
        }

        var existingHash = existing.ContentHash;

        if (string.IsNullOrEmpty(existingHash))
        {
            try
            {
                existingHash = await _hasher.ComputeContentHashAsync(existing.FilePath, ct).ConfigureAwait(false);
                await _duplicates.SaveContentHashAsync(existing.FormatId, existingHash, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "算库里已有文件的全文件哈希失败，按重复处理 {File}", existing.FilePath);
                return new DuplicateVerdict(true, sourceHash);
            }
        }

        var same = string.Equals(existingHash, sourceHash, StringComparison.OrdinalIgnoreCase);
        return new DuplicateVerdict(same, sourceHash);
    }

    /// <summary>复核结论。IsDuplicate=true 时 Hash 是源文件的全文件哈希，可留着写库。</summary>
    private readonly record struct DuplicateVerdict(bool IsDuplicate, string? Hash);
}