using System.Diagnostics;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.IO;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Calibre;
using EbookToolSuite.Infrastructure.Files;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Conversion;

/// <summary>
/// 格式转换服务（T1-4 最小实现）。
///
/// 一次转换的完整流程：
///   找到源文件 -> 查支持矩阵 -> 查重 -> 建参数（过白名单）-> 磁盘预检
///   -> 在独立临时目录里转换（保留正确扩展名）-> 原子改名到正式区 -> 登记 book_formats。
///
/// 为什么不用 T1-2 导入的常规路径：转换产物【不复制文件】，而是直接写进该书的 Library 目录，
/// 因为它本来就是这本书的另一个格式，属于同一本书。
/// </summary>
public sealed class ConversionService : IConversionService
{
    /// <summary>进度上报的最小间隔：Calibre 输出很密，不节流会把 UI 线程打满。</summary>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>自动挑源文件时的优先顺序（能保留排版信息的排前面）。</summary>
    private static readonly BookFormatKind[] SourcePreference =
    {
        BookFormatKind.Epub,
        BookFormatKind.Txt,
        BookFormatKind.Fb2,
        BookFormatKind.Mobi,
        BookFormatKind.Azw3,
        BookFormatKind.Pdf,
        BookFormatKind.Cbz,
    };

    /// <summary>支持的转换矩阵。没列出来的链路一律拒绝，不做"试试看"。</summary>
    private static readonly Dictionary<BookFormatKind, BookFormatKind[]> Targets = new()
    {
        [BookFormatKind.Epub] = new[]
        {
            BookFormatKind.Mobi, BookFormatKind.Pdf, BookFormatKind.Txt, BookFormatKind.Epub,
        },
        // T4-5：TXT 能转 EPUB / MOBI / AZW3（都走 Calibre；
        // 已知缺口：Calibre 的 TXT 输入插件生成不了章节目录，见执行计划 T4-5 的落地补充）
        [BookFormatKind.Txt] = new[] { BookFormatKind.Epub, BookFormatKind.Mobi, BookFormatKind.Azw3 },
        [BookFormatKind.Mobi] = new[] { BookFormatKind.Epub },
        [BookFormatKind.Azw3] = new[] { BookFormatKind.Epub },
        [BookFormatKind.Fb2] = new[] { BookFormatKind.Epub },
        [BookFormatKind.Pdf] = new[] { BookFormatKind.Epub },
        [BookFormatKind.Cbz] = new[] { BookFormatKind.Epub },
    };

    private readonly IAppPaths _paths;
    private readonly IToolLocator _tools;
    private readonly IBookRepository _repository;
    private readonly IConfigStore _config;
    private readonly IClock _clock;
    private readonly ILogger<ConversionService>? _log;
    private readonly CalibreArgsCatalog _catalog;
    private readonly CalibrePolishArgsCatalog _polishCatalog;
    private readonly CalibreConverter _converter;
    private readonly IHashUtil? _hasher;
    private readonly ConversionTemplateStore _templates;

    public ConversionService(
        IAppPaths paths,
        IToolLocator tools,
        IBookRepository repository,
        IConfigStore config,
        IProcessHost processHost,
        IClock clock,
        ILogger<ConversionService>? log = null,
        ILogger<CalibreConverter>? converterLog = null,
        IHashUtil? hasher = null,
        ConversionTemplateStore? templates = null)
    {
        _paths = paths;
        _tools = tools;
        _repository = repository;
        _config = config;
        _clock = clock;
        _log = log;
        _converter = new CalibreConverter(processHost, converterLog);
        _catalog = new CalibreArgsCatalog(
            processHost,
            tools.CalibreConvert,
            Path.Combine(paths.CacheDir, "calibre-options-v2.json"),   // v2：修掉了漏 -- 前缀的旧缓存
            tools.BuildEnvironment("calibre"),
            paths.ToolsRoot);

        _polishCatalog = new CalibrePolishArgsCatalog(
            processHost,
            tools.CalibrePolish,
            // 独立缓存：polish 的选项集与 ebook-convert 完全不同，不能共用一份。
            Path.Combine(paths.CacheDir, "calibre-polish-options-v1.json"),
            tools.BuildEnvironment("calibre"),
            paths.ToolsRoot);

        _hasher = hasher;
        _templates = templates ?? new ConversionTemplateStore();
    }

    // ---------- 静态能力查询 ----------

    public bool IsSupported(BookFormatKind source, BookFormatKind target)
        => Targets.TryGetValue(source, out var targets) && Array.IndexOf(targets, target) >= 0;

    public IReadOnlyList<BookFormatKind> GetSupportedTargets(BookFormatKind source)
        => Targets.TryGetValue(source, out var targets)
            ? targets
            : Array.Empty<BookFormatKind>();

    public IReadOnlyList<ConversionTemplate> GetTemplates(BookFormatKind targetFormat)
        => _templates.ForTarget(targetFormat);

    public IReadOnlyList<ConversionTemplate> GetFormatKeepingTemplates(BookFormatKind sourceFormat)
        => _templates.FormatKeeping(sourceFormat);

    // ---------- 转换 ----------

    public async Task<ConversionResult> ConvertAsync(
        ConversionRequest request,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken ct = default,
        IProgress<ProcessOutputLine>? rawOutput = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var book = await _repository.GetByIdAsync(request.BookId, ct).ConfigureAwait(false)
            ?? throw new AppException(
                ErrorCode.FileNotFound,
                "找不到这本书的记录，可能已被移除。请在书库里刷新后重试。",
                technical: $"book not found: {request.BookId}");

        await _templates.EnsureLoadedAsync(ct).ConfigureAwait(false);
        var target = request.TargetFormat;
        var source = ResolveSource(book, request.SourceFormat);

        // 解析模板要看源格式：「不换格式，只优化」（polish）没有目标格式，
        // 能不能用取决于**源**文件是 EPUB 还是 AZW3。
        var template = ResolveTemplate(request, target, source.Format);

        // polish 的产物就是源格式本身，这里把目标格式纠正过来。
        var keepsFormat = template?.KeepsFormat == true;
        if (keepsFormat)
        {
            target = source.Format;
        }


        // 「不换格式，只优化」（polish）：产物就是源格式，所以不走「源 -> 目标」支持矩阵，
        // 也不做「已有该格式」查重：那两道闸对这个场景必然为真，会误报。
        if (keepsFormat)
        {
            return await PolishAsync(book, source, template!, request, progress, rawOutput, ct)
                .ConfigureAwait(false);
        }

        if (!IsSupported(source.Format, target))
        {
            throw new AppException(
                ErrorCode.ConversionUnsupported,
                $"{source.Format.ToStorageString()} 不能转成 {target.ToStorageString()}。",
                technical: $"unsupported link: {source.Format}->{target}");
        }

        // T6-1：阅读缓存转换（RegisterFormat=false）不是"给书库加格式"，
        // 所以既不能因为库里已有该格式（哪怕是 Missing 的旧行）就被拒，
        // 也不能在成功后被登记进 book_formats。
        if (request.RegisterFormat
            && await _repository.HasFormatAsync(book.Id, target, ct).ConfigureAwait(false))
        {
            throw new AppException(
                ErrorCode.ConversionOutputExists,
                $"这本书已经有 {target.ToStorageString()} 文件了。"
                    + "要重新转换请先移除该格式，或换一个输出格式。",
                technical: $"format exists: book={book.Id} format={target}");
        }

        var options = await BuildOptionsAsync(source, target, template, request, ct)
            .ConfigureAwait(false);

        var outputDirectory = request.OutputDirectory
            ?? Path.GetDirectoryName(source.FilePath)
            ?? _paths.LibraryDir;
        Directory.CreateDirectory(outputDirectory);

        var finalOutput = MakeOutputPath(outputDirectory, source.FilePath, target);

        // 源文件大小以磁盘为准：库里记的 file_size 可能是 0（老数据）。
        long sourceBytes;
        try
        {
            sourceBytes = new FileInfo(source.FilePath).Length;
        }
        catch (Exception)
        {
            sourceBytes = source.FileSize;
        }

        // 预检：输入 x 3 + 200MB 余量（执行计划 T2-4 第 4 条）。实测产物约 1.6 倍，
        // 但还要算上临时目录里的中间产物与同卷改名，留 3 倍加 200MB 才稳。
        DiskSpaceGuard.Ensure(
            outputDirectory,
            sourceBytes,
            _config.GetDouble("conversion.diskSpaceFactor", 3.0),
            200L * 1024 * 1024);

        var runDirectory = Path.Combine(
            _paths.TempDir, "calibre", "conv-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(runDirectory);

        // 临时产物必须带正确扩展名：Calibre 靠扩展名推断目标格式（写成 .part 必失败）。
        var tempOutput = Path.Combine(runDirectory, "out" + target.ToExtension());
        var environment = PrepareEnvironment();

        var timeoutMinutes = _config.GetDouble("conversion.timeoutMinutes", 60d);
        if (timeoutMinutes <= 0)
        {
            timeoutMinutes = 60d;
        }

        var spec = new CalibreConversionSpec
        {
            ExePath = _tools.CalibreConvert,
            InputPath = source.FilePath,
            TempOutputPath = tempOutput,
            RunDirectory = runDirectory,
            Timeout = request.Timeout ?? TimeSpan.FromMinutes(timeoutMinutes),
            Options = options,
            Environment = environment,
        };

        var parser = new ConversionProgressParser();
        var stopwatch = Stopwatch.StartNew();

        // 立刻报一次"已开始"：界面从第一秒就有"已耗时"可看（不确定进度 + 已耗时）。
        progress?.Report(new ConversionProgress
        {
            Percent = null,
            StageText = "正在启动转换",
            Elapsed = TimeSpan.Zero,
        });

        var reporter = MakeReporter(parser, stopwatch, progress, rawOutput);

        _log?.LogInformation(
            "开始转换《{Title}》{Source}->{Target}，模板={Template}",
            book.Title, source.Format, target, template?.Id ?? "(无)");

        try
        {
            var outcome = await _converter.ConvertAsync(spec, reporter, ct).ConfigureAwait(false);

            // 同卷改名，原子：不会出现"半个文件"。
            File.Move(outcome.TempOutputPath, finalOutput, overwrite: false);

            if (request.RegisterFormat)
            {
                try
                {
                    await RegisterFormatAsync(book.Id, target, finalOutput, outcome.SizeBytes, ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // 登记失败就把刚改好名的产物删掉，避免"文件在、库里没有"的孤儿。
                    TryDeleteFile(finalOutput);
                    throw;
                }
            }

            stopwatch.Stop();
            progress?.Report(new ConversionProgress
            {
                Percent = 1d,
                StageText = "转换完成",
                Elapsed = stopwatch.Elapsed,
                LastLine = finalOutput,
            });

            _log?.LogInformation(
                "转换完成《{Title}》-> {Output}（{Size} 字节，{Seconds:F1} 秒）",
                book.Title, Path.GetFileName(finalOutput), outcome.SizeBytes,
                outcome.Duration.TotalSeconds);

            return new ConversionResult(
                finalOutput,
                outcome.SizeBytes,
                outcome.Duration,
                target,
                template?.Id,
                BuildCommandLineText(source.FilePath, tempOutput, options));
        }
        catch (OperationCanceledException)
        {
            _log?.LogInformation("转换已取消：《{Title}》", book.Title);
            throw;
        }
        finally
        {
            TryDeleteDirectory(runDirectory);
        }
    }

    // ---------- 内部：选源与模板 ----------

    private static BookFormat ResolveSource(Book book, BookFormatKind? requested)
    {
        if (requested is { } kind)
        {
            var exact = book.Formats.FirstOrDefault(f => f.Format == kind);
            if (exact is null || !File.Exists(exact.FilePath))
            {
                throw new AppException(
                    ErrorCode.FileNotFound,
                    $"这本书没有可用的 {kind.ToStorageString()} 文件。",
                    technical: $"source {kind} missing or file gone: book={book.Id}");
            }

            return exact;
        }

        foreach (var candidate in SourcePreference)
        {
            var match = book.Formats.FirstOrDefault(
                f => f.Format == candidate && File.Exists(f.FilePath));
            if (match is not null)
            {
                return match;
            }
        }

        throw new AppException(
            ErrorCode.FileNotFound,
            "这本书没有任何可用的文件，可能在磁盘上被移动或删除了。",
            technical: $"no usable source: book={book.Id} formats={book.Formats.Count}");
    }

    private ConversionTemplate? ResolveTemplate(
        ConversionRequest request,
        BookFormatKind target,
        BookFormatKind sourceFormat)
    {
        if (!string.IsNullOrWhiteSpace(request.TemplateId))
        {
            var explicitTemplate = _templates.Find(request.TemplateId)
                ?? throw new AppException(
                    ErrorCode.InternalError,
                    $"找不到转换模板 {request.TemplateId}。",
                    technical: $"unknown template id: {request.TemplateId}");

            // 「不换格式，只优化」没有目标格式，能不能用取决于**源**文件。
            if (explicitTemplate.KeepsFormat)
            {
                if (!explicitTemplate.AppliesTo(sourceFormat))
                {
                    throw new AppException(
                        ErrorCode.ConversionArgsRejected,
                        $"模板 {explicitTemplate.DisplayName} 只能用于 EPUB 或 AZW3，不能用于 {sourceFormat.ToStorageString()}。",
                        technical: $"template not applicable: {explicitTemplate.Id} source={sourceFormat}");
                }

                return explicitTemplate;
            }

            var templateTarget = explicitTemplate.TargetFormat;
            if (templateTarget is null || templateTarget != target)
            {
                throw new AppException(
                    ErrorCode.ConversionArgsRejected,
                    $"模板 {explicitTemplate.Id} 是给 "
                        + $"{templateTarget?.ToStorageString() ?? "不换格式"} 用的，不能用于 "
                        + $"{target.ToStorageString()}。",
                    technical: $"template target mismatch: {templateTarget} != {target}");
            }

            return explicitTemplate;
        }

        var defaultId = _config.GetString(
            "conversion.defaultTemplates." + target.ToStorageString());

        return _templates.Find(defaultId)
            ?? _templates.ForTarget(target).FirstOrDefault();
    }

    private async Task<IReadOnlyList<ConversionArgument>> BuildOptionsAsync(
        BookFormat source,
        BookFormatKind target,
        ConversionTemplate? template,
        ConversionRequest request,
        CancellationToken ct)
    {
        var catalog = await _catalog
            .GetAsync(source.Format.ToExtension(), target.ToExtension(), ct)
            .ConfigureAwait(false);

        var baseArguments = new List<ConversionArgument>();

        // 设备档位来自配置；模板自己指定了就以模板为准（模板在后面，合并时后写的赢）。
        var profile = _config.GetString("conversion.defaultOutputProfile", "default");
        var templateHasProfile = template is not null
            && template.Arguments.Any(a => string.Equals(a.Key, "--output-profile", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(profile)
            && !templateHasProfile
            && catalog.IsKnown("--output-profile"))
        {
            baseArguments.Add(new ConversionArgument("--output-profile", profile));
        }

        if (template is not null)
        {
            /* 【T7-5 实测】内置模板的参数是按「同名格式转换」实测出来的。
               例如 EPUB 主用模板里的 --change-justification 是 EPUB **输入**插件的选项，
               拿到 CBZ -> EPUB（漫画输入）就不存在——照原样传会被白名单拦下，整次转换直接失败。
               这也是 T7-5 回归发现的真问题：转换矩阵里有 CBZ -> EPUB，但默认模板跑不通。

               处理口径（区分来源，不是放宽校验）：
                 - 模板自带的参数：只保留当前 输入 x 输出 组合认识的那些，被跳过的记一条日志；
                 - 用户在界面里临时填的参数（request.Overrides）：仍然严格校验、不认识就抛，
                   见下面的 CalibreArgumentBuilder.Build。 */
            foreach (var argument in template.Arguments)
            {
                if (catalog.IsKnown(argument.Key))
                {
                    baseArguments.Add(argument);
                }
                else
                {
                    _log?.LogInformation(
                        "转换模板 {Template} 的参数 {Key} 不适用于 {Source}->{Target} 组合，本次跳过",
                        template.Id, argument.Key, source.Format, target);
                }
            }
        }

        // --extra-css 要在运行时换成 eye-care.css 的绝对路径（模板里只能放占位符，
        // 相对路径 Calibre 会找不到文件）。
        var expanded = baseArguments
            .Select(a => a.Value is null
                ? a
                : new ConversionArgument(
                    a.Key,
                    a.Value.Replace(ConversionTemplates.ExtraCssPlaceholder, EyeCareCssPath, StringComparison.Ordinal),
                    a.Note))
            .ToList();

        // 合并 + 白名单校验：任何一个参数不合法都在这里就抛，绝不"跳过继续跑"。
        var merged = CalibreArgumentBuilder.Build(catalog, expanded, request.Overrides);

        ValidateTargetSpecificOptions(target, merged);
        return merged;
    }

    /// <summary>
    /// 随程序分发的护眼样式绝对路径。模板里的 {EyeCareCss} 占位符在 BuildOptionsAsync 里
    /// 被替换成它（Calibre 不接受相对路径，会直接找不到文件）。
    /// </summary>
    /// <summary>
    /// 判据 5：--mobi-file-type 只在 MOBI 输出时才有意义。放进 EPUB / PDF / TXT 模板
    /// 属于配置错误，必须在执行前拦下。
    /// </summary>
    /// <remarks>
    /// 不能只靠参数白名单：白名单是「按 输入->输出 组合探测」出来的，而探测失败时会退化成
    /// 一份 20 项的兜底清单，那里面也有 --mobi-file-type。所以这条规则要单独写死。
    /// </remarks>
    public static void ValidateTargetSpecificOptions(
        BookFormatKind target, IReadOnlyList<ConversionArgument> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (target == BookFormatKind.Mobi)
        {
            return;
        }

        if (options.Any(a => string.Equals(a.Key, "--mobi-file-type", StringComparison.OrdinalIgnoreCase)))
        {
            throw new AppException(
                ErrorCode.ConversionArgsRejected,
                "--mobi-file-type 只能用于 MOBI 输出，当前目标是 " + target.ToStorageString() + "。",
                technical: $"target-specific option rejected: --mobi-file-type target={target}");
        }
    }

    private static string EyeCareCssPath => Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "Conversion",
        ConversionTemplates.EyeCareCssFileName);

    private Dictionary<string, string?> PrepareEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _tools.BuildEnvironment("calibre"))
        {
            environment[pair.Key] = pair.Value;
        }

        environment.TryAdd("CALIBRE_CONFIG_DIRECTORY", _paths.CalibreConfigDir);
        environment.TryAdd("CALIBRE_CACHE_DIRECTORY", _paths.CalibreCacheDir);
        environment.TryAdd("CALIBRE_TEMP_DIR", Path.Combine(_paths.TempDir, "Calibre"));
        environment.TryAdd("CALIBRE_OVERRIDE_LANG", "en");
        environment.TryAdd("PYTHONIOENCODING", "utf-8");

        // CALIBRE_* 指向的目录必须已经存在：不存在时 Calibre 直接崩溃（实测 exit=1），它不会自建。
        foreach (var key in CalibreDirectoryKeys)
        {
            if (environment.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                Directory.CreateDirectory(value);
            }
        }

        return environment;
    }

    private static readonly string[] CalibreDirectoryKeys =
    {
        "CALIBRE_CONFIG_DIRECTORY",
        "CALIBRE_CACHE_DIRECTORY",
        "CALIBRE_TEMP_DIR",
    };

    /// <summary>目标文件路径。重名时加序号，绝不静默覆盖已有文件。</summary>
    private static string MakeOutputPath(
        string directory, string sourcePath, BookFormatKind target)
    {
        var stem = PathUtil.SanitizeFileName(
            Path.GetFileNameWithoutExtension(sourcePath), "book");
        var extension = target.ToExtension();

        var candidate = Path.Combine(directory, stem + extension);
        var index = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            index++;
        }

        return candidate;
    }

    private async Task RegisterFormatAsync(
        Guid bookId, BookFormatKind format, string path, long size, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var now = _clock.Now;

        var entity = new BookFormat
        {
            BookId = bookId,
            Format = format,
            FilePath = path,
            FileSize = size > 0 ? size : info.Length,
            FileModifiedAt = info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc) : now,
            AddedAt = now,
            Missing = false,
        };

        await _repository.AddFormatAsync(entity, ct).ConfigureAwait(false);
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // 删不掉不影响这次转换的结果，但要在活动中心留个记录。
            _log?.LogWarning(ex, "清理转换临时目录失败：{Directory}", directory);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "清理转换产物失败：{Path}", path);
        }
    }

    /// <summary>拼一条给人看的命令行。只用于日志与诊断，实际执行走 ArgumentList。</summary>
    private static string BuildCommandLineText(
        string inputPath,
        string outputPath,
        IReadOnlyList<ConversionArgument> options,
        string exeName = "ebook-convert")
    {
        var parts = new List<string> { Quote(inputPath), Quote(outputPath) };
        parts.AddRange(CalibreArgumentBuilder.ToCommandLineOptions(options).Select(Quote));
        return exeName + " " + string.Join(' ', parts);
    }

    private static string Quote(string value)
        => value.Contains(' ') ? "\"" + value + "\"" : value;

    /// <summary>把 Calibre 的输出行接成两条通路：原样透传（给「查看日志」）+ 节流后的进度上报。</summary>
    private static Progress<ProcessOutputLine> MakeReporter(
        ConversionProgressParser parser,
        Stopwatch stopwatch,
        IProgress<ConversionProgress>? progress,
        IProgress<ProcessOutputLine>? rawOutput)
    {
        var lastReported = TimeSpan.Zero;

        return new Progress<ProcessOutputLine>(line =>
        {
            // 原样透传给调用方：队列靠它攒出本次运行的完整输出（界面「查看日志」要看这个）。
            // 进度上报被 200ms 节流会丢行，所以完整输出必须走独立通道。
            rawOutput?.Report(line);

            var update = parser.Feed(line.Text, stopwatch.Elapsed);
            if (update is null)
            {
                return;
            }

            if (stopwatch.Elapsed - lastReported < ReportInterval)
            {
                return;
            }

            lastReported = stopwatch.Elapsed;
            progress?.Report(update);
        });
    }

    /// <summary>
    /// 「不换格式，只优化」：产物与源文件同路径同格式，用 ebook-polish 就地替换。
    /// 与 ConvertAsync 的三点不同：目标格式等于源格式；查重那两道闸不做；
    /// 产物要盖住库里那个文件，所以先备份再同卷原子替换。
    /// </summary>
    private async Task<ConversionResult> PolishAsync(
        Book book,
        BookFormat source,
        ConversionTemplate template,
        ConversionRequest request,
        IProgress<ConversionProgress>? progress,
        IProgress<ProcessOutputLine>? rawOutput,
        CancellationToken ct)
    {
        var catalog = await _polishCatalog.GetAsync(ct).ConfigureAwait(false);
        var options = CalibreArgumentBuilder.Build(catalog, template.Arguments, request.Overrides);

        long sourceBytes;
        try
        {
            sourceBytes = new FileInfo(source.FilePath).Length;
        }
        catch (Exception)
        {
            sourceBytes = source.FileSize;
        }

        var sourceDirectory = Path.GetDirectoryName(source.FilePath) ?? _paths.LibraryDir;
        DiskSpaceGuard.Ensure(
            sourceDirectory,
            sourceBytes,
            _config.GetDouble("conversion.diskSpaceFactor", 3.0),
            200L * 1024 * 1024);

        var runDirectory = Path.Combine(
            _paths.TempDir, "calibre", "polish-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(runDirectory);

        // polish 的输入与输出同格式，临时产物也用源格式的扩展名（执行器靠扩展名判断产物）。
        var tempOutput = Path.Combine(runDirectory, "out" + source.Format.ToExtension());
        var environment = PrepareEnvironment();

        var timeoutMinutes = _config.GetDouble("conversion.timeoutMinutes", 60d);
        if (timeoutMinutes <= 0)
        {
            timeoutMinutes = 60d;
        }

        var spec = new CalibreConversionSpec
        {
            ExePath = _tools.CalibrePolish,
            InputPath = source.FilePath,
            TempOutputPath = tempOutput,
            RunDirectory = runDirectory,
            Timeout = request.Timeout ?? TimeSpan.FromMinutes(timeoutMinutes),
            Options = options,
            Environment = environment,
        };

        var parser = new ConversionProgressParser();
        var stopwatch = Stopwatch.StartNew();

        progress?.Report(new ConversionProgress
        {
            Percent = null,
            StageText = "正在优化（不换格式）",
            Elapsed = TimeSpan.Zero,
        });

        var reporter = MakeReporter(parser, stopwatch, progress, rawOutput);

        _log?.LogInformation(
            "开始优化《{Title}》{Format}，模板={Template}，输出到 {Path}",
            book.Title, source.Format, template.Id, tempOutput);

        try
        {
            var outcome = await _converter.ConvertAsync(spec, reporter, ct).ConfigureAwait(false);

            // 先备份原件：polish 是就地替换，新文件万一有问题还能找回。
            Directory.CreateDirectory(_paths.BackupFilesDir);
            var backupPath = Path.Combine(
                _paths.BackupFilesDir,
                book.Id.ToString("D") + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")
                    + source.Format.ToExtension());
            // T7-2 判据 4：备份也要原子落盘，回滚时不能拿到半截文件。
            AtomicFile.Copy(source.FilePath, backupPath);

            ReplaceInPlace(outcome.TempOutputPath, source.FilePath);

            // 内容已经变了：体积与快速指纹都要重算，全量哈希作废（置 null，等精确判重时再算）。
            var newSize = new FileInfo(source.FilePath).Length;
            string? newFingerprint = null;
            if (_hasher is not null)
            {
                try
                {
                    newFingerprint = await _hasher
                        .QuickFingerprintAsync(source.FilePath, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log?.LogWarning(ex, "优化后重算快速指纹失败 {Path}", source.FilePath);
                }
            }

            await _repository
                .UpdateFormatMetadataAsync(book.Id, source.Format, newSize, newFingerprint, null, null, ct)
                .ConfigureAwait(false);

            progress?.Report(new ConversionProgress
            {
                Percent = 1,
                StageText = "优化完成",
                Elapsed = stopwatch.Elapsed,
            });

            _log?.LogInformation(
                "优化完成《{Title}》：{Old} 字节 -> {New} 字节", book.Title, sourceBytes, newSize);

            return new ConversionResult(
                source.FilePath,
                newSize,
                outcome.Duration,
                source.Format,
                template.Id,
                BuildCommandLineText(spec.InputPath, tempOutput, options, "ebook-polish"));
        }
        catch (OperationCanceledException)
        {
            _log?.LogInformation("优化已取消《{Title}》", book.Title);
            throw;
        }
        finally
        {
            TryDeleteDirectory(runDirectory);
        }
    }

    /// <summary>把新产物换到原地。同卷走覆盖式 Move，跨卷退化成「复制 + 删临时」。</summary>
    private static void ReplaceInPlace(string tempPath, string targetPath)
    {
        try
        {
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch (IOException)
        {
            // T7-2 判据 4：跨卷兜底也不能直接覆盖目标（半截 EPUB 会被书库当成好文件）。
            AtomicFile.Copy(tempPath, targetPath);
            File.Delete(tempPath);
        }
    }
}

