using System.Diagnostics;
using System.IO.Compression;
using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Engines.Comics;

/// <summary>
/// 把图片文件夹（或用户排好序的图片列表）打成 CBZ（就是一个扩展名不同的 zip）。
/// </summary>
/// <remarks>
/// <para>
/// 两条刻意决定：
/// 1. 「按文件夹打包」的条目名原样保留，不做 0001.jpg 这种补零重命名。安卓漫画阅读器
///    （静读天下 / Perfect Viewer / Comic Screen）都按文件名自然序排，
///    补零反而会让用户在阅读器里看到一串陌生文件名。
///    「按指定条目打包」的顺序由调用方决定（条目名本身已带有序前缀，见 <see cref="CbzEntryNames"/>），
///    这里只负责按列表顺序落盘。
/// 2. 图片本身已是压缩格式，所以图片条目用 NoCompression：再压一遍几乎不省空间，
///    只是白白花时间。ComicInfo.xml 是纯文本，单独用 Optimal 压。
/// </para>
/// <para>
/// 两个重载共用同一条核心写路径（临时文件 → 同目录改名、坏文件跳过并计数、进度回调），
/// 区别只有两处：顺序由谁决定、要不要写 ComicInfo.xml。
/// </para>
/// </remarks>
public sealed class CbzPacker : ICbzPacker
{
    /// <summary>扩展名白名单。刻意不含 tiff / heic：安卓阅读器支持度太差。</summary>
    public static readonly string[] SupportedExtensions =
        { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".avif" };

    /// <summary>跳过的文件最多记这么多条，避免界面被上千条刷屏（计数不截断）。</summary>
    private const int MaxSkippedRecorded = 50;

    public IReadOnlyList<string> ScanImages(string folder, bool recursive = false)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return Array.Empty<string>();
        }

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        try
        {
            return Directory
                .EnumerateFiles(folder, "*", option)
                .Where(IsSupportedImage)
                .OrderBy(f => Path.GetFileName(f) ?? string.Empty, NaturalStringComparer.Instance)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 扫描阶段不抛：交给 PackAsync 统一报「没有图片」。
            return Array.Empty<string>();
        }
    }

    /// <summary>扩展名是否在白名单里。不读文件内容，不抛异常。</summary>
    public static bool IsSupportedImage(string path)
    {
        var extension = Path.GetExtension(path);
        return SupportedExtensions.Any(
            e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<CbzPackResult> PackAsync(
        string sourceFolder,
        string outputPath,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (!Directory.Exists(sourceFolder))
        {
            throw new AppException(
                ErrorCode.CbzPackFailed,
                "找不到这个文件夹，可能已被移动或删除。",
                technical: $"folder not found: {sourceFolder}");
        }

        var images = ScanImages(sourceFolder);
        if (images.Count == 0)
        {
            throw new AppException(
                ErrorCode.CbzPackFailed,
                "这个文件夹里没有找到图片。打包 CBZ 支持 jpg / png / webp / bmp / gif。",
                technical: $"no images in {sourceFolder}");
        }

        // 文件夹模式：条目名保持原文件名，顺序就是扫描的自然序，不写 ComicInfo.xml。
        var entries = images
            .Select(image => new CbzPackEntry(image, Path.GetFileName(image)))
            .ToList();

        /* 交给同一条核心路径，避免第二份 zip 循环走样。错误文案用文件夹版：
           用户看到的是「文件夹里的图片」，不该出现「条目」这种内部词。 */
        return await PackCoreAsync(
            entries,
            comicInfo: null,
            outputPath,
            progress,
            allUnreadableMessage: "这个文件夹里的图片一张都读不出来，没有生成文件。",
            allUnreadableTechnical: $"all images unreadable in {sourceFolder}",
            ct).ConfigureAwait(false);
    }

    public async Task<CbzPackResult> PackAsync(
        IReadOnlyList<CbzPackEntry> entries,
        ComicMetadata? metadata,
        string outputPath,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (entries.Count == 0)
        {
            throw new AppException(
                ErrorCode.CbzPackFailed,
                "没有可以打包的图片。",
                technical: "empty entry list");
        }

        /* 重名/非法名在动文件之前就拦下：写到一半才发现重名，只能整包作废，
           而这时用户已经等了几分钟。大小写不敏感是因为 Windows 解压后两者会互相覆盖，
           而阅读器/解压工具对大小写的处理并不一致。 */
        ValidateEntries(entries);

        var comicInfo = metadata is { HasUserValues: true } ? metadata : null;

        return await PackCoreAsync(
            entries,
            comicInfo,
            outputPath,
            progress,
            allUnreadableMessage: "这些图片一张都读不出来，没有生成文件。",
            allUnreadableTechnical: "all entries unreadable",
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 两个重载共用的写路径。调用方负责前置校验（文件夹存在/列表非空/条目名合法），
    /// 这里只处理「怎么把条目写进 zip 并原子落盘」。
    /// </summary>
    /// <param name="comicInfo">null = 不写 ComicInfo.xml；否则它会被写成包内第一个条目。</param>
    private static async Task<CbzPackResult> PackCoreAsync(
        IReadOnlyList<CbzPackEntry> entries,
        ComicMetadata? comicInfo,
        string outputPath,
        IProgress<ConversionProgress>? progress,
        string allUnreadableMessage,
        string allUnreadableTechnical,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        var fullOutputPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutputPath);

        if (string.IsNullOrEmpty(directory))
        {
            // 正常绝对路径不会走到这里（根目录除外）；兜底用进程当前目录。
            directory = Directory.GetCurrentDirectory();
        }

        Directory.CreateDirectory(directory);

        var total = entries.Count;
        var skipped = new List<string>();
        var skippedCount = 0;

        /* 先探一遍源文件能不能打开，再动 zip：
           1. ComicInfo.xml 必须是包内第一个条目，而它的 PageCount 要写真实页数，
              不先数一遍就只能写「计划页数」，跳过坏文件后页数会对不上；
           2. 一张都读不出来时可以在创建临时文件之前就报错，不留半截产物。
           探测只 open/close，正式写条目时还会再打开一次；中间文件被删掉的极小窗口
           按「跳过」处理（此时 PageCount 会略大于实际写入数，可接受）。 */
        var readable = new List<CbzPackEntry>(total);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var probe = new FileStream(
                    entry.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
            }
            catch (Exception ex) when (IsSourceUnreadable(ex))
            {
                skippedCount++;
                if (skipped.Count < MaxSkippedRecorded)
                {
                    skipped.Add(entry.EntryName);
                }

                Report(progress, skippedCount, total, entry.EntryName, stopwatch);
                continue;
            }

            readable.Add(entry);
        }

        if (readable.Count == 0)
        {
            throw new AppException(
                ErrorCode.CbzPackFailed,
                allUnreadableMessage,
                technical: allUnreadableTechnical);
        }

        // 先写同目录的临时文件再改名：失败不留半截产物，也不会覆盖掉已有文件。
        var tempPath = Path.Combine(
            directory,
            Path.GetFileNameWithoutExtension(fullOutputPath)
                + ".part-" + Guid.NewGuid().ToString("N")[..8]);

        var written = 0;

        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                // ★ 必须是第一个条目：阅读器与元数据扫描工具都习惯只看第一个条目。
                if (comicInfo is not null)
                {
                    ct.ThrowIfCancellationRequested();

                    var infoEntry = zip.CreateEntry(ComicInfoXml.EntryName, CompressionLevel.Optimal);

                    await using (var infoStream = infoEntry.Open())
                    {
                        ComicInfoXml.Write(infoStream, comicInfo, readable.Count);
                    }
                }

                foreach (var entry in readable)
                {
                    ct.ThrowIfCancellationRequested();

                    FileStream source;
                    try
                    {
                        source = new FileStream(
                            entry.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                            useAsync: true);
                    }
                    catch (Exception ex) when (IsSourceUnreadable(ex))
                    {
                        skippedCount++;
                        if (skipped.Count < MaxSkippedRecorded)
                        {
                            skipped.Add(entry.EntryName);
                        }

                        Report(progress, written + skippedCount, total, entry.EntryName, stopwatch);
                        continue;
                    }

                    await using (source)
                    {
                        var zipEntry = zip.CreateEntry(entry.EntryName, CompressionLevel.NoCompression);
                        try
                        {
                            await using var entryStream = zipEntry.Open();
                            await source.CopyToAsync(entryStream, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex) when (ex is IOException or NotSupportedException)
                        {
                            throw new AppException(
                                ErrorCode.CbzPackFailed,
                                "打包「" + entry.EntryName + "」时写入出错，已中止，没有生成文件。",
                                technical: $"write failed: {entry.SourcePath}",
                                inner: ex);
                        }
                    }

                    written++;
                    Report(progress, written + skippedCount, total, entry.EntryName, stopwatch);
                }
            }

            if (written == 0)
            {
                /* 预检时还能打开、正式写时全部失败（文件在两步之间被删/被占用）。
                   与「文件夹一张都读不出来」同一语义：不生成文件。 */
                throw new AppException(
                    ErrorCode.CbzPackFailed,
                    allUnreadableMessage,
                    technical: allUnreadableTechnical);
            }

            // 同目录改名：跨卷问题不存在，也不会出现「半个 CBZ」。
            File.Move(tempPath, fullOutputPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }

        var sizeBytes = new FileInfo(fullOutputPath).Length;

        return new CbzPackResult(
            fullOutputPath,
            written,
            sizeBytes,
            stopwatch.Elapsed,
            skipped)
        {
            SkippedCount = skippedCount,
        };
    }

    /// <summary>
    /// 条目名/重名校验。合法就直接返回，不合法抛 <see cref="ErrorCode.CbzPackFailed"/>。
    /// </summary>
    private static void ValidateEntries(IReadOnlyList<CbzPackEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (entry is null || entry.EntryName is null)
            {
                throw new AppException(
                    ErrorCode.CbzPackFailed,
                    "条目列表里有空项，无法打包。",
                    technical: "null entry in pack list");
            }

            var reason = CbzEntryNames.InvalidEntryNameReason(entry.EntryName);

            if (reason is not null)
            {
                throw new AppException(
                    ErrorCode.CbzPackFailed,
                    "包内文件名「" + entry.EntryName + "」不能用：" + reason,
                    technical: $"invalid entry name: {entry.EntryName}");
            }

            if (!seen.Add(entry.EntryName))
            {
                throw new AppException(
                    ErrorCode.CbzPackFailed,
                    "包内有两个同名文件：「" + entry.EntryName + "」，请改掉其中一个再打包。",
                    technical: $"duplicate entry name: {entry.EntryName}");
            }
        }
    }

    /// <summary>读源文件时「这张读不出来，跳过」的异常集合（其余异常仍照常上抛）。</summary>
    private static bool IsSourceUnreadable(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static void Report(
        IProgress<ConversionProgress>? progress,
        int done,
        int total,
        string currentName,
        Stopwatch stopwatch)
    {
        progress?.Report(new ConversionProgress
        {
            Percent = total > 0 ? (double)done / total : null,
            StageText = "正在打包 " + done + "/" + total + "：" + currentName,
            Elapsed = stopwatch.Elapsed,
        });
    }

    private static void TryDeleteFile(string path)
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
            // 临时文件删不掉不影响正常路径（下次发布会带走 Data 之外的目录，
            // 而这里是输出目录）。真正的失败已经在上面抛过了。
        }
    }
}
