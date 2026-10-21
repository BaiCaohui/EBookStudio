using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T7-5 全量回归的「40 个固定样本 × 导入 -> 阅读 -> 转换 -> 编辑 -> 保存 -> 校验」闭环。
///
/// 为什么要有它：T7-5 的验收判据是"40 个样本全部跑通"+"所有已知问题记录在案"。
/// 用 40 个手工点的窗口是点不完的，用 40 个独立进程跑冒烟又太慢（每个进程 5~10 秒启动，
/// 还要靠 UI 自动化）；这里在**一个进程**里直接驱动服务层（导入流水线、阅读服务、
/// 转换服务、编辑工作区、两段式保存、EpubCheck），每个样本每步都留下证据。
///
/// 契约在 <c>build/t75-samples.catalog.json</c>（我方定的 40 个样本 + 每条的期望），
/// 样本由 <c>build/make-test-samples.ps1</c> 按同一份 catalog 现造，manifest.json 带字节数与 SHA256。
/// 期望的语义见 catalog 顶部的 semantics 段；本类只负责执行与判定，不在代码里另立一套期望。
/// </summary>
public static class T75RegressionTest
{
    /// <summary>catalog 定死的样本数；少于这个数且没指定 -Only 时判样本不完整。</summary>
    public const int ExpectedSampleCount = 40;

    private const string ReportFileName = "t75-smoke.json";

    private const string DetailReportFileName = "t75-regression.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>改书名用的标记前缀。判定"保存后读回的是改后的书名"就靠它。</summary>
    private const string TitleMarkerPrefix = "回归改名-";

    public static async Task<SmokeReport> RunAsync(
        IServiceProvider services,
        AppPaths paths,
        string corpusDirectory,
        string? onlySampleId,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();
        var rows = new List<SampleRow>();

        void Add(string name, bool ok, string detail) => checks.Add(new SmokeCheck(name, ok, detail));

        var importer = services.GetRequiredService<IBookImportService>();
        var reader = services.GetRequiredService<IReaderService>();
        var conversion = services.GetRequiredService<IConversionService>();
        var conversionQueue = services.GetRequiredService<IConversionQueue>();
        var workspaces = services.GetRequiredService<IEpubWorkspaceFactory>();
        var saveService = services.GetRequiredService<IEpubSaveService>();
        var checker = services.GetRequiredService<IEpubChecker>();
        var probe = services.GetRequiredService<IEpubProbe>();
        var chapterSource = services.GetRequiredService<IChapterSourceService>();
        var repository = services.GetRequiredService<IBookRepository>();
        var pdf = services.GetRequiredService<IPdfDocumentService>();
        var comics = services.GetRequiredService<IComicBookService>();

        // ---------- 读 manifest（样本生成器写的，字段与 catalog 一致） ----------
        var manifestPath = Path.Combine(corpusDirectory, "manifest.json");
        CorpusManifest? manifest = null;
        string? manifestError = null;

        try
        {
            manifest = JsonSerializer.Deserialize<CorpusManifest>(
                await File.ReadAllTextAsync(manifestPath, Encoding.UTF8).ConfigureAwait(true),
                JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            manifestError = ex.GetType().Name + ": " + ex.Message;
        }

        if (manifest is null || manifest.Samples.Count == 0)
        {
            Add("t75-manifest", false,
                "读不到样本清单：" + manifestPath + "（" + (manifestError ?? "样本为空")
                + "）；先跑 build\\make-test-samples.ps1");
            return Finish(paths, startedAt, stopwatch, checks, rows, null);
        }

        if (onlySampleId is not null)
        {
            manifest.Samples = manifest.Samples
                .Where(s => string.Equals(s.Id, onlySampleId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var countOk = onlySampleId is not null || manifest.Samples.Count == ExpectedSampleCount;
        Add("t75-manifest-count", countOk,
            $"清单里 {manifest.Samples.Count} 个样本（期望 {ExpectedSampleCount}）"
            + (onlySampleId is null ? string.Empty : "；本次只跑 " + onlySampleId));

        if (manifest.Samples.Count == 0)
        {
            Add("t75-sample-filter", false, "过滤后没有样本可跑：" + onlySampleId);
            return Finish(paths, startedAt, stopwatch, checks, rows, manifest);
        }

        // MOBI/AZW3 首次阅读要靠转换队列做惰性转换，把执行器叫起来。
        try
        {
            await conversionQueue.StartAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "T7-5：转换队列启动失败，MOBI/AZW3 惰性阅读可能不可用");
        }

        var importedBooks = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var corpusBytes = 0L;

        // ---------- 阶段 1：先把全部样本导入 ----------
        // 为什么不把导入和后面的步骤放在同一个循环：去重样本（G01 是 A01 的逐字节副本）
        // 要求"导入那一刻" A01 还在库里、且没被改过。若边导入边编辑，G01 面对的已经是
        // 改过的 A01，精确去重当然不命中——那是测试自己的顺序问题，不是产品缺陷。
        var states = new List<SampleState>();

        foreach (var spec in manifest.Samples.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            var state = new SampleState
            {
                Spec = spec,
                Watch = Stopwatch.StartNew(),
                SamplePath = Path.Combine(corpusDirectory, spec.File.Replace('/', Path.DirectorySeparatorChar)),
                Row = new SampleRow
                {
                    Id = spec.Id,
                    Group = spec.Group,
                    File = spec.File,
                    Format = spec.Format,
                    Bytes = spec.Bytes,
                },
            };

            if (!File.Exists(state.SamplePath))
            {
                state.Row.Failures.Add("样本文件不存在：" + spec.File);
                states.Add(state);
                continue;
            }

            corpusBytes += new FileInfo(state.SamplePath).Length;

            var row = state.Row;
            var sampleId = spec.Id;
            var samplePath = state.SamplePath;

            // ===== 1. 导入 =====
            try
            {
                var beforeCount = await repository.CountAsync().ConfigureAwait(true);
                var result = await importer
                    .ImportAsync(samplePath, ImportOptions.Default)
                    .ConfigureAwait(true);
                var afterCount = await repository.CountAsync().ConfigureAwait(true);

                row.ImportOutcome = result.Outcome.ToString();
                row.ImportMessage = result.Message;

                var allowed = spec.Expect.Import.Count > 0 ? spec.Expect.Import : new List<string> { "Imported" };
                var outcomeOk = allowed.Contains(result.Outcome.ToString(), StringComparer.OrdinalIgnoreCase);

                // 反例样本绝不能"静默成功"：失败/重复都不许凭空多出一行书。
                var countDelta = afterCount - beforeCount;
                var countOk2 = outcomeOk
                    && (result.Outcome == ImportOutcome.Imported ? countDelta == 1 : countDelta == 0);

                if (!countOk2)
                {
                    row.Failures.Add($"导入结果 {result.Outcome}（允许 {string.Join('/', allowed)}），"
                        + $"书数 {beforeCount}->{afterCount}；{result.Message}");
                }

                row.ImportOk = countOk2;
                if (result.Outcome == ImportOutcome.Imported && result.BookId is { } id)
                {
                    state.BookId = id;
                    importedBooks[sampleId] = id;
                }

                // 精确去重必须命中的是"同一本书"。
                if (spec.Expect.SameBookAs is { } sameAs
                    && importedBooks.TryGetValue(sameAs, out var expectedId))
                {
                    var sameOk = result.BookId == expectedId;
                    if (!sameOk)
                    {
                        row.Failures.Add($"去重命中的不是 {sameAs}（期望 {expectedId}，实际 {result.BookId}）");
                        row.ImportOk = false;
                    }
                }
            }
            catch (Exception ex)
            {
                row.ImportOk = false;
                row.ImportOutcome = "Exception";
                row.ImportMessage = ex.GetType().Name + ": " + ex.Message;
                row.Failures.Add("导入抛异常：" + ex.GetType().Name + " " + ex.Message);
            }

            state.ImportMs = state.Watch.ElapsedMilliseconds;
            states.Add(state);
        }

        // ---------- 阶段 2：逐个样本跑 阅读 -> 转换 -> 编辑 -> 保存 -> 校验 ----------
        foreach (var state in states)
        {
            var spec = state.Spec;
            var row = state.Row;
            var bookId = state.BookId;
            var sampleId = spec.Id;
            var phaseWatch = Stopwatch.StartNew();

            // ===== 2. 阅读 =====
            if (bookId is null || string.Equals(spec.Expect.Read, "skip", StringComparison.OrdinalIgnoreCase))
            {
                row.ReadDetail = bookId is null ? "未导入，跳过" : "按契约跳过";
            }
            else
            {
                try
                {
                    var read = await ReadAsync(reader, pdf, comics, bookId.Value, spec).ConfigureAwait(true);
                    row.ReadOk = read.Ok;
                    row.ReadDetail = read.Detail;
                    if (!read.Ok && string.Equals(spec.Expect.Read, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        row.Failures.Add("阅读：" + read.Detail);
                    }
                }
                catch (Exception ex)
                {
                    row.ReadOk = false;
                    row.ReadDetail = ex.GetType().Name + ": " + ex.Message;

                    // read=either 是"打不开也算预期"的样本（如截断 PDF），记录但不判失败。
                    if (string.Equals(spec.Expect.Read, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        row.Failures.Add("阅读抛异常：" + ex.GetType().Name + " " + ex.Message);
                    }
                }
            }

            // ===== 3. 转换 =====
            if (bookId is not null && spec.Expect.Convert.Count > 0)
            {
                foreach (var targetName in spec.Expect.Convert)
                {
                    var stepWatch = Stopwatch.StartNew();
                    var step = new StepRow { Step = "convert-" + targetName };
                    try
                    {
                        if (!Enum.TryParse<BookFormatKind>(targetName, ignoreCase: true, out var target)
                            || target == BookFormatKind.Unknown)
                        {
                            step.Ok = false;
                            step.Detail = "未知目标格式：" + targetName;
                        }
                        else
                        {
                            var request = new ConversionRequest { BookId = bookId.Value, TargetFormat = target };
                            var result = await conversion.ConvertAsync(request).ConfigureAwait(true);
                            var exists = File.Exists(result.OutputPath) && result.SizeBytes > 0;
                            var epubZero = true;
                            var checkText = string.Empty;

                            if (exists && target == BookFormatKind.Epub
                                && !string.Equals(spec.Expect.ConvertCheck, "open", StringComparison.OrdinalIgnoreCase))
                            {
                                var report = await checker.ValidateAsync(result.OutputPath).ConfigureAwait(true);
                                epubZero = !report.HasBlockingErrors;
                                checkText = "；EpubCheck " + report.CountsText;
                                step.ValidateOk = epubZero;
                            }

                            step.Ok = exists && epubZero;
                            step.Detail = $"产物 {Path.GetFileName(result.OutputPath)} {result.SizeBytes:N0} B，"
                                + $"{result.Duration.TotalSeconds:F1} s" + checkText;
                            step.OutputPath = result.OutputPath;
                            step.Bytes = result.SizeBytes;
                        }
                    }
                    catch (Exception ex) when (ex is AppException or IOException or UnauthorizedAccessException)
                    {
                        step.Ok = false;
                        step.Detail = "转换失败：" + ex.GetType().Name + " " + ex.Message;
                    }

                    step.Ms = stepWatch.ElapsedMilliseconds;
                    row.Convert.Add(step);
                    if (!step.Ok)
                    {
                        row.Failures.Add(step.Step + " " + step.Detail);
                    }
                }
            }

            // ===== 4. 编辑 -> 保存 -> 5. 校验（只对 EPUB 且已导入的样本） =====
            var isEpub = string.Equals(spec.Format, "EPUB", StringComparison.OrdinalIgnoreCase);
            if (bookId is not null && isEpub
                && !string.Equals(spec.Expect.EditSave, "skip", StringComparison.OrdinalIgnoreCase))
            {
                var stepWatch = Stopwatch.StartNew();
                var step = new StepRow { Step = "editSave" };
                try
                {
                    var edit = await EditAndSaveAsync(
                        repository, workspaces, saveService, checker, probe, chapterSource,
                        bookId.Value, spec).ConfigureAwait(true);
                    step.Ok = edit.Ok;
                    step.Detail = edit.Detail;
                    step.Saved = edit.Saved;
                    step.ValidateOk = edit.EpubCheckOk;
                    step.OutputPath = edit.Path;
                    if (!edit.Ok)
                    {
                        row.Failures.Add("编辑保存：" + edit.Detail);
                    }
                }
                catch (Exception ex)
                {
                    step.Ok = false;
                    step.Detail = ex.GetType().Name + ": " + ex.Message;
                    row.Failures.Add("编辑保存抛异常：" + ex.GetType().Name + " " + ex.Message);
                }

                step.Ms = stepWatch.ElapsedMilliseconds;
                row.EditSave = step;
            }

            if (bookId is not null
                && string.Equals(spec.Expect.Validate, "zero", StringComparison.OrdinalIgnoreCase))
            {
                var stepWatch = Stopwatch.StartNew();
                var step = new StepRow { Step = "validate" };
                try
                {
                    var epubPath = await FindEpubArtifactAsync(repository, bookId.Value).ConfigureAwait(true);
                    if (epubPath is null || !File.Exists(epubPath))
                    {
                        step.Ok = false;
                        step.Detail = "找不到该书库里的 EPUB 产物";
                    }
                    else
                    {
                        var report = await checker.ValidateAsync(epubPath).ConfigureAwait(true);
                        step.Ok = !report.HasBlockingErrors && !report.ParseFailed;
                        step.OutputPath = epubPath;
                        step.Detail = Path.GetFileName(epubPath) + " " + report.CountsText
                            + (report.ParseFailed ? "；报告解析失败：" + report.ParseFailureReason : string.Empty);
                        step.ValidateOk = step.Ok;
                    }
                }
                catch (Exception ex) when (ex is AppException or IOException)
                {
                    step.Ok = false;
                    step.Detail = ex.GetType().Name + " " + ex.Message;
                }

                step.Ms = stepWatch.ElapsedMilliseconds;
                row.Validate = step;
                if (!step.Ok)
                {
                    row.Failures.Add("校验：" + step.Detail);
                }
            }

            row.ElapsedMs = state.ImportMs + phaseWatch.ElapsedMilliseconds;
            rows.Add(row);

            var ok = row.Failures.Count == 0;
            Add("t75-" + sampleId, ok, row.Describe());
            log.LogInformation(
                "T7-5 回归 {Id} {State} 导入={Import} 阅读={Read} 转换={Convert} 编辑保存={Edit} {Ms} ms",
                sampleId,
                ok ? "OK  " : "FAIL",
                row.ImportOutcome ?? "-",
                row.ReadOk?.ToString() ?? "-",
                row.Convert.Count == 0 ? "-" : string.Join('+', row.Convert.Select(c => c.Ok ? "ok" : "fail")),
                row.EditSave?.Ok.ToString() ?? "-",
                row.ElapsedMs);

            if (!ok)
            {
                foreach (var failure in row.Failures)
                {
                    log.LogWarning("T7-5 回归 {Id} 失败项：{Failure}", sampleId, failure);
                }
            }
        }

        // ---------- 汇总 ----------
        var failed = rows.Where(r => r.Failures.Count > 0).ToList();
        Add("t75-all-samples", failed.Count == 0,
            failed.Count == 0
                ? $"{rows.Count} 个样本全部跑通"
                : $"{failed.Count}/{rows.Count} 个样本有失败项：" + string.Join(", ", failed.Select(r => r.Id)));

        var sum = new SummaryRow
        {
            SampleCount = rows.Count,
            PassedSamples = rows.Count - failed.Count,
            FailedSamples = failed.Count,
            CorpusBytes = corpusBytes,
            ConvertCount = rows.Sum(r => r.Convert.Count),
            ConvertPassed = rows.Sum(r => r.Convert.Count(c => c.Ok)),
            EditSaveCount = rows.Count(r => r.EditSave is not null),
            EditSavePassed = rows.Count(r => r.EditSave is { Ok: true }),
            ValidateCount = rows.Count(r => r.Validate is not null),
            ValidatePassed = rows.Count(r => r.Validate is { Ok: true }),
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };

        log.LogInformation(
            "T7-5 回归汇总：样本 {Passed}/{Total}；转换 {ConvPassed}/{ConvTotal}；编辑保存 {EditPassed}/{EditTotal}；"
            + "EpubCheck {ValPassed}/{ValTotal}；样本 {Bytes:N0} B；总耗时 {Ms} ms",
            sum.PassedSamples, sum.SampleCount, sum.ConvertPassed, sum.ConvertCount,
            sum.EditSavePassed, sum.EditSaveCount, sum.ValidatePassed, sum.ValidateCount,
            sum.CorpusBytes, sum.ElapsedMs);

        return Finish(paths, startedAt, stopwatch, checks, rows, manifest, sum);
    }

    // ---------- 阅读步骤 ----------

    private static async Task<(bool Ok, string Detail)> ReadAsync(
        IReaderService reader,
        IPdfDocumentService pdf,
        IComicBookService comics,
        Guid bookId,
        SampleSpec spec)
    {
        var format = ParseFormat(spec.Format);
        var session = await reader.OpenAsync(bookId).ConfigureAwait(true);
        var detail = new StringBuilder();

        switch (format)
        {
            case BookFormatKind.Epub:
            case BookFormatKind.Txt:
            case BookFormatKind.Mobi:
            case BookFormatKind.Azw3:
            {
                var href = await reader.MaterializeChapterAsync(session, 0).ConfigureAwait(true);
                var materialized = Path.Combine(session.CacheRoot, href.Replace('/', Path.DirectorySeparatorChar));
                var bytes = File.Exists(materialized) ? new FileInfo(materialized).Length : 0;
                detail.Append($"spine={session.Manifest.Spine.Count} 章；首章 {bytes:N0} B");

                var spineOk = session.Manifest.Spine.Count > 0 && bytes > 0;
                if (spec.Expect.MinSpine is { } minSpine)
                {
                    spineOk &= session.Manifest.Spine.Count >= minSpine;
                    detail.Append($"，要求 >= {minSpine} 章");
                }

                if (spec.Expect.FallbackSplit)
                {
                    spineOk &= session.Manifest.TextFallbackSplit;
                    detail.Append($"，兜底硬切={session.Manifest.TextFallbackSplit}");
                }

                if (format == BookFormatKind.Txt && !string.IsNullOrEmpty(session.Manifest.TextEncoding))
                {
                    detail.Append($"，编码 {session.Manifest.TextEncoding}");
                }

                return (spineOk, detail.ToString());
            }

            case BookFormatKind.Pdf:
            {
                var pages = pdf.GetPageCount(session.SourcePath);
                var rendered = pdf.RenderPage(session.SourcePath, 0, 800);
                var ok = pages > 0 && rendered.Length > 0 && session.PdfUrl is not null;
                detail.Append($"页数={pages}，首页 {rendered.Length:N0} B，PdfUrl={(session.PdfUrl is not null)}");
                return (ok, detail.ToString());
            }

            case BookFormatKind.Cbz:
            {
                var probeResult = comics.Probe(session.SourcePath);
                using var book = comics.Open(session.SourcePath);
                var page = book.RenderPage(0, 800, 0);
                var ok = probeResult.PageCount > 0 && book.PageCount > 0 && page.Jpeg.Length > 0;
                detail.Append($"页数={book.PageCount}，首页 {page.Jpeg.Length:N0} B");
                return (ok, detail.ToString());
            }

            default:
                return (false, "不认识的格式：" + spec.Format);
        }
    }

    // ---------- 编辑保存步骤 ----------

    private static async Task<EditResult> EditAndSaveAsync(
        IBookRepository repository,
        IEpubWorkspaceFactory workspaces,
        IEpubSaveService saveService,
        IEpubChecker checker,
        IEpubProbe probe,
        IChapterSourceService chapterSource,
        Guid bookId,
        SampleSpec spec)
    {
        var book = await repository.GetByIdAsync(bookId).ConfigureAwait(true);
        var format = book?.Formats
            .FirstOrDefault(f => f.Format == BookFormatKind.Epub && !f.Missing
                && File.Exists(f.FilePath));

        if (book is null || format is null)
        {
            return new EditResult(false, false, false, "库里找不到这本书的 EPUB 格式文件", null);
        }

        var originalHash = Sha256File(format.FilePath);
        var marker = TitleMarkerPrefix + spec.Id;
        var notes = new List<string>();

        var workspace = await workspaces.OpenAsync(new WorkspaceOpenRequest
        {
            EpubPath = format.FilePath,
            BookId = book.Id,
            FormatId = format.Id,
        }).ConfigureAwait(true);

        try
        {
            var opfEntry = workspace.Session.OpfPath;
            if (string.IsNullOrEmpty(opfEntry))
            {
                return new EditResult(false, false, false, "工作区没有 OPF 路径（.session.json 的 opfPath 为空）", format.FilePath);
            }

            // ① 改元数据：把 dc:title 改成回归标记。
            //    注意 dc:title 的命名空间是 Dublin Core，不是 package 的 opf 命名空间，所以按 LocalName 找。
            var opfFull = ToFullPath(workspace.WorkDir, opfEntry);
            var opfDoc = XDocument.Load(opfFull, LoadOptions.PreserveWhitespace);
            var titleElement = opfDoc.Root?.Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, "title", StringComparison.Ordinal));
            if (titleElement is null)
            {
                return new EditResult(false, false, false, "OPF 里没有 dc:title，无法验证元数据写回", format.FilePath);
            }

            titleElement.Value = marker;
            opfDoc.Save(opfFull);
            if (!workspace.Session.ChangedFiles.Contains(opfEntry))
            {
                workspace.Session.ChangedFiles.Add(opfEntry);
            }

            notes.Add("书名改为 " + marker);

            // ② 改正文：在第一章 body 末尾插一个标记段落（走真正的章节源码服务，良构性不过就不写）。
            var chapterEntry = FindFirstSpineXhtml(workspace.WorkDir, opfEntry);
            if (chapterEntry is not null)
            {
                var source = chapterSource.ReadSource(workspace, chapterEntry);
                var bodyClose = source.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (bodyClose >= 0)
                {
                    var edited = source.Insert(bodyClose, $"<p id=\"t75-regression\">{marker}</p>\n");
                    var check = chapterSource.Save(workspace, chapterEntry, edited);
                    notes.Add(check.Ok
                        ? "正文插入标记（" + chapterEntry + "）"
                        : "正文插入被良构性检查拒绝：" + check.Summary);
                }
                else
                {
                    notes.Add("首章没有 </body>，跳过正文改动（" + chapterEntry + "）");
                }
            }
            else
            {
                notes.Add("spine 里找不到 XHTML 章，跳过正文改动");
            }

            // ③ 两段式保存：先打包到暂存文件并跑 EpubCheck，再原子替换。
            var stage = await saveService
                .StageAsync(workspace.WorkDir, format.FilePath)
                .ConfigureAwait(true);

            var decision = stage.Decision.Outcome.ToString();

            if (stage.Decision.IsSilentPass)
            {
                var package = await saveService
                    .CommitAsync(stage, new RestorePointRequest(book.Id, format.Id, format.FilePath))
                    .ConfigureAwait(true);

                var report = await checker.ValidateAsync(format.FilePath).ConfigureAwait(true);
                var probed = await probe.ProbeAsync(format.FilePath, loadCover: false).ConfigureAwait(true);
                var titleOk = string.Equals(probed.Title?.Trim(), marker, StringComparison.Ordinal);
                var checkOk = !report.HasBlockingErrors && !report.ParseFailed;

                // A12/A13：保存必须把 mimetype 规范化成「首条 + STORED」。
                var mimetypeOk = !spec.Expect.MimetypeFixed || CheckMimetypeLayout(format.FilePath);

                var ok = checkOk && titleOk && mimetypeOk && package.ByteCount > 0;
                var detail = $"保存={decision}，{package.ByteCount:N0} B；书名读回={(titleOk ? "是" : "否")}"
                    + $"；EpubCheck {report.CountsText}"
                    + (spec.Expect.MimetypeFixed ? "；mimetype 布局=" + (mimetypeOk ? "规范" : "不规范") : string.Empty)
                    + "；" + string.Join("；", notes);

                return new EditResult(ok, true, checkOk, detail, format.FilePath);
            }

            // 门禁拦下（fatal/error、超时、工具挂了）：原文件必须一个字节都没变。
            saveService.Discard(stage);

            var unchanged = string.Equals(Sha256File(format.FilePath), originalHash, StringComparison.OrdinalIgnoreCase);
            var blockedAllowed = string.Equals(spec.Expect.EditSave, "blocked-or-zero", StringComparison.OrdinalIgnoreCase);
            var okBlocked = blockedAllowed && unchanged && stage.Decision.IsBlocked;

            var detailBlocked = $"保存被拦={decision}（{stage.Decision.Summary}）；原文件字节未变={unchanged}"
                + (blockedAllowed ? string.Empty : "；但契约要求这里的保存必须通过")
                + "；" + string.Join("；", notes);

            return new EditResult(okBlocked, false, false, detailBlocked, format.FilePath);
        }
        finally
        {
            try
            {
                await workspace.DiscardAsync().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 工作区清理失败不该改变判定结论。
            }
        }
    }

    /// <summary>找该书库里第一个可用的 EPUB 文件（保存后校验步骤用）。</summary>
    private static async Task<string?> FindEpubArtifactAsync(IBookRepository repository, Guid bookId)
    {
        var book = await repository.GetByIdAsync(bookId).ConfigureAwait(false);
        return book?.Formats
            .Where(f => f.Format == BookFormatKind.Epub && !f.Missing)
            .Select(f => f.FilePath)
            .FirstOrDefault(File.Exists);
    }

    // ---------- OPF / ZIP 小工具 ----------

    /// <summary>
    /// 取 spine 里第一个 XHTML 条目的包内相对路径（相对工作区根）。
    /// 只做 T7-5 需要的最小解析：manifest item 的 href + OPF 所在目录。
    /// </summary>
    private static string? FindFirstSpineXhtml(string workDir, string opfEntry)
    {
        try
        {
            var doc = XDocument.Load(ToFullPath(workDir, opfEntry));
            if (doc.Root is null)
            {
                return null;
            }

            var ns = doc.Root.Name.Namespace;
            var manifest = doc.Root.Element(ns + "manifest");
            var spine = doc.Root.Element(ns + "spine");
            if (manifest is null || spine is null)
            {
                return null;
            }

            var items = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var item in manifest.Elements(ns + "item"))
            {
                if (item.Attribute("id")?.Value is { Length: > 0 } id)
                {
                    items[id] = item;
                }
            }

            var opfDir = opfEntry.Contains('/') ? opfEntry[..opfEntry.LastIndexOf('/')] : string.Empty;

            foreach (var itemref in spine.Elements(ns + "itemref"))
            {
                if (itemref.Attribute("idref")?.Value is not { Length: > 0 } idref
                    || !items.TryGetValue(idref, out var item))
                {
                    continue;
                }

                var mediaType = item.Attribute("media-type")?.Value;
                if (mediaType is not ("application/xhtml+xml" or "text/html"))
                {
                    continue;
                }

                var href = item.Attribute("href")?.Value;
                if (string.IsNullOrEmpty(href))
                {
                    continue;
                }

                return NormalizeEntry(opfDir.Length > 0 ? opfDir + "/" + href : href);
            }
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            return null;
        }

        return null;
    }

    /// <summary>把 "OEBPS/Text/../Text/ch1.xhtml" 规范成 "OEBPS/Text/ch1.xhtml"。</summary>
    private static string NormalizeEntry(string entry)
    {
        var stack = new List<string>();
        foreach (var part in entry.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part)
            {
                case ".":
                    continue;
                case "..":
                    if (stack.Count > 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }

                    continue;
                default:
                    stack.Add(part);
                    break;
            }
        }

        return string.Join('/', stack);
    }

    private static string ToFullPath(string root, string entry)
        => Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>保存后的 EPUB 必须满足：首条是 mimetype、不压缩、内容精确。</summary>
    private static bool CheckMimetypeLayout(string epubPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(epubPath);
            var first = zip.Entries.FirstOrDefault();
            if (first is null || !string.Equals(first.FullName, "mimetype", StringComparison.Ordinal))
            {
                return false;
            }

            // STORED 时压缩后长度与原始长度一致；DEFLATE 时会不一样。
            if (first.CompressedLength != first.Length)
            {
                return false;
            }

            using var stream = first.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var text = Encoding.ASCII.GetString(buffer.ToArray());
            return text == "application/epub+zip";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static BookFormatKind ParseFormat(string format)
        => Enum.TryParse<BookFormatKind>(format, ignoreCase: true, out var kind)
            ? kind
            : BookFormatKind.Unknown;

    // ---------- 报告 ----------

    private static SmokeReport Finish(
        AppPaths paths,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        List<SmokeCheck> checks,
        List<SampleRow> rows,
        CorpusManifest? manifest,
        SummaryRow? summary = null)
    {
        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            stopwatch.ElapsedMilliseconds,
            paths.DataRoot,
            paths.TempDir,
            null,
            checks);

        Directory.CreateDirectory(paths.TempDir);
        File.WriteAllText(
            Path.Combine(paths.TempDir, ReportFileName),
            JsonSerializer.Serialize(report, JsonOptions),
            new UTF8Encoding(false));

        var detail = new RegressionDetail
        {
            StartedAt = report.StartedAt,
            FinishedAt = report.FinishedAt,
            DurationMs = report.DurationMs,
            DataRoot = report.DataRoot,
            Generator = manifest?.Generator,
            GeneratedAt = manifest?.GeneratedAt,
            Summary = summary,
            Samples = rows,
        };

        File.WriteAllText(
            Path.Combine(paths.TempDir, DetailReportFileName),
            JsonSerializer.Serialize(detail, JsonOptions),
            new UTF8Encoding(false));

        return report;
    }

    // ---------- JSON 模型（与 catalog / 生成器 manifest 同名同义） ----------

    private sealed class CorpusManifest
    {
        public int SchemaVersion { get; set; }

        public string? GeneratedAt { get; set; }

        public string? Generator { get; set; }

        public List<SampleSpec> Samples { get; set; } = new();
    }

    private sealed class SampleSpec
    {
        public string Id { get; set; } = string.Empty;

        public string Group { get; set; } = string.Empty;

        public string File { get; set; } = string.Empty;

        public string Format { get; set; } = string.Empty;

        public ExpectSpec Expect { get; set; } = new();

        public string? Note { get; set; }

        public long Bytes { get; set; }

        public string? Sha256 { get; set; }
    }

    private sealed class ExpectSpec
    {
        public List<string> Import { get; set; } = new();

        public string Read { get; set; } = "skip";

        public List<string> Convert { get; set; } = new();

        public string ConvertCheck { get; set; } = "zero";

        public string EditSave { get; set; } = "skip";

        public string Validate { get; set; } = "skip";

        public bool MimetypeFixed { get; set; }

        public int? MinSpine { get; set; }

        public bool FallbackSplit { get; set; }

        public string? SameBookAs { get; set; }
    }

    /// <summary>一个样本在"导入阶段"结束后的状态，供第二阶段继续跑。</summary>
    private sealed class SampleState
    {
        public required SampleSpec Spec { get; init; }

        public required SampleRow Row { get; init; }

        public required Stopwatch Watch { get; init; }

        public required string SamplePath { get; init; }

        public Guid? BookId { get; set; }

        /// <summary>阶段 1（导入）花了多少毫秒。总体耗时 = 它 + 阶段 2 的耗时。</summary>
        public long ImportMs { get; set; }
    }

    private sealed class SampleRow
    {
        public string Id { get; set; } = string.Empty;

        public string Group { get; set; } = string.Empty;

        public string File { get; set; } = string.Empty;

        public string Format { get; set; } = string.Empty;

        public long Bytes { get; set; }

        public string? ImportOutcome { get; set; }

        public bool? ImportOk { get; set; }

        public string? ImportMessage { get; set; }

        public bool? ReadOk { get; set; }

        public string? ReadDetail { get; set; }

        public List<StepRow> Convert { get; set; } = new();

        public StepRow? EditSave { get; set; }

        public StepRow? Validate { get; set; }

        public List<string> Failures { get; set; } = new();

        public long ElapsedMs { get; set; }

        public string Describe()
        {
            var text = $"导入={ImportOutcome}（{(ImportOk == true ? "ok" : "fail")}）"
                + $"；阅读={ReadOk?.ToString() ?? "-"}（{ReadDetail}）";
            if (Convert.Count > 0)
            {
                text += "；转换=" + string.Join(' ', Convert.Select(c => c.Step + ":" + (c.Ok ? "ok" : "fail")));
            }

            if (EditSave is not null)
            {
                text += "；编辑保存=" + (EditSave.Ok ? "ok" : "fail") + "（" + EditSave.Detail + "）";
            }

            if (Validate is not null)
            {
                text += "；校验=" + (Validate.Ok ? "ok" : "fail") + "（" + Validate.Detail + "）";
            }

            if (Failures.Count > 0)
            {
                text += "；失败项：" + string.Join(" | ", Failures);
            }

            return text + $"；{ElapsedMs} ms";
        }
    }

    private sealed class StepRow
    {
        public string Step { get; set; } = string.Empty;

        public bool Ok { get; set; }

        public string? Detail { get; set; }

        public long Ms { get; set; }

        public string? OutputPath { get; set; }

        public long? Bytes { get; set; }

        public bool? Saved { get; set; }

        public bool? ValidateOk { get; set; }
    }

    private sealed class SummaryRow
    {
        public int SampleCount { get; set; }

        public int PassedSamples { get; set; }

        public int FailedSamples { get; set; }

        public long CorpusBytes { get; set; }

        public int ConvertCount { get; set; }

        public int ConvertPassed { get; set; }

        public int EditSaveCount { get; set; }

        public int EditSavePassed { get; set; }

        public int ValidateCount { get; set; }

        public int ValidatePassed { get; set; }

        public long ElapsedMs { get; set; }
    }

    private sealed class RegressionDetail
    {
        public string StartedAt { get; set; } = string.Empty;

        public string FinishedAt { get; set; } = string.Empty;

        public long DurationMs { get; set; }

        public string DataRoot { get; set; } = string.Empty;

        public string? Generator { get; set; }

        public string? GeneratedAt { get; set; }

        public SummaryRow? Summary { get; set; }

        public List<SampleRow> Samples { get; set; } = new();
    }

    private sealed record EditResult(bool Ok, bool Saved, bool EpubCheckOk, string Detail, string? Path);
}
