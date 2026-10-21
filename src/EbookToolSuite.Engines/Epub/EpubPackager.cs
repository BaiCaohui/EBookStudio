using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Linq;
using System.Text;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.IO;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Engines.Epub;

/// <summary>
/// 把 EPUB 工作区重新打成 EPUB（执行计划 T3-4，设计文档 6.1.5）。
/// <para>
/// 每一条约束都对应一个 EpubCheck 报错，写错任何一条产出都不合规：
/// <list type="number">
/// <item>mimetype 必须是第一条，且必须 STORED（不压缩），内容精确、无 BOM、无换行；</item>
/// <item>输出流必须可 seek，否则所有条目都会带 data descriptor（GPF bit3），产出不合规；</item>
/// <item>工作区自带的 mimetype 必须排除，由打包器唯一生成；</item>
/// <item>图片与字体不压缩（本来就是压缩格式），其余用 Optimal；</item>
/// <item>写入顺序：mimetype -> META-INF/ -> 其余；</item>
/// <item>先写 .part，自检通过后备份 + 原子替换，原文件绝不半途被改；</item>
/// <item>entry 名用正斜杠。</item>
/// </list>
/// </para>
/// </summary>
public sealed class EpubPackager
{
    /// <summary>ZIP 第一条的名称（OCF 规定必须是小写 mimetype）。</summary>
    public const string MimetypeEntryName = "mimetype";

    /// <summary>OCF 规定的 mimetype 内容：无 BOM、无换行。</summary>
    public const string MimetypeContent = "application/epub+zip";

    /// <summary>EPUB 容器描述文件；复核器要确认它指向的 OPF 真的在包里。</summary>
    public const string ContainerEntryName = "META-INF/container.xml";

    /// <summary>会话文件（T3-1 写在每个工作区根目录），不能打进 EPUB。</summary>
    public const string SessionFileName = ".session.json";

    /// <summary>快照目录（T3-5），不能打进 EPUB。</summary>
    public const string SnapshotsDirectoryName = ".snapshots";

    /// <summary>
    /// 删除章节的暂存区（T5-1）：删章节先把文件挪到这里，保存时才真正丢弃。
    /// 它和 .snapshots 一样绝不能被打进 EPUB。
    /// </summary>
    public const string DeletedDirectoryName = ".deleted";

    private const int CopyBufferSize = 81_920;

    /// <summary>
    /// 所有 entry 的时间戳统一钉在 ZIP 纪元（1980-01-01）。
    /// EPUB 规范不关心时间戳，钉死之后「同样的工作区两次打包字节完全相同」，
    /// 判据 4（逐条字节对比）与 T3-6 的快照回滚都靠这个（审计 I23）。
    /// </summary>
    private static readonly DateTimeOffset FixedEntryTime =
        new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] UncompressedExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".woff", ".woff2", ".otf", ".ttf"];

    /// <summary>
    /// 把 <paramref name="workDir"/> 打成 <paramref name="outputPath"/>。
    /// 内容先写到 <c>outputPath + ".part"</c>，自检通过后才动目标文件。
    /// </summary>
    /// <param name="backupDirectory">
    /// 非 null 时，替换前把原文件备份进这个目录并保留（T3-6 的撤销栈要用）；
    /// 为 null 时备份成同目录的 <c>.ets-bak</c>，成功或失败后都删掉，用户看不到残渣。
    /// </param>
    public async Task<EpubPackageResult> PackageAsync(
        string workDir,
        string outputPath,
        string? backupDirectory = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workDir))
            throw new AppException(ErrorCode.InvalidPath, "工作区路径为空，无法打包 EPUB。", nameof(workDir));
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new AppException(ErrorCode.InvalidPath, "输出路径为空，无法打包 EPUB。", nameof(outputPath));

        var root = Path.GetFullPath(workDir);
        if (!Directory.Exists(root))
            throw new AppException(ErrorCode.FileNotFound, "工作区不存在，无法打包 EPUB。", root);

        var finalPath = Path.GetFullPath(outputPath);
        var finalDirectory = Path.GetDirectoryName(finalPath);
        if (string.IsNullOrEmpty(finalDirectory))
            throw new AppException(ErrorCode.InvalidPath, "输出路径不合法，无法打包 EPUB。", outputPath);
        Directory.CreateDirectory(finalDirectory);

        var partPath = finalPath + ".part";
        var entries = CollectEntries(root);
        var keepBackup = !string.IsNullOrWhiteSpace(backupDirectory);
        string? backupPath = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await BuildAsync(partPath, entries, progress, ct).ConfigureAwait(false);

            // 最后一道防线：自检不过就绝不碰原文件
            EpubPackageVerifier.Verify(partPath);
            progress?.Report(0.98);

            if (File.Exists(finalPath))
            {
                backupPath = ResolveBackupPath(finalPath, backupDirectory);
                try
                {
                    // 备份必须先落盘：File.Replace 一执行，旧文件就没了。
                    // T7-2 判据 4：备份本身也要原子写，否则失败时回滚用的是半截文件。
                    AtomicFile.Copy(finalPath, backupPath);
                    File.Replace(partPath, finalPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new AppException(
                        ErrorCode.FileLocked,
                        "书籍文件正被占用（阅读器或杀毒软件），没能保存；原文件没有被改动，请关掉占用它的程序后重试。",
                        $"替换 {finalPath} 失败：{ex.Message}",
                        ex);
                }
            }
            else
            {
                File.Move(partPath, finalPath);
            }

            stopwatch.Stop();
            progress?.Report(1.0);

            var length = new FileInfo(finalPath).Length;
            if (!keepBackup && backupPath is not null)
            {
                TryDeleteFile(backupPath);
                backupPath = null;
            }

            return new EpubPackageResult(finalPath, backupPath, entries.Count, length, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            TryDeleteFile(partPath);
            if (!keepBackup && backupPath is not null)
                TryDeleteFile(backupPath);

            if (ex is AppException or OperationCanceledException)
                throw;

            throw new AppException(
                ErrorCode.InternalError,
                "打包 EPUB 时出错，原文件没有被改动。",
                ex.ToString(),
                ex);
        }
    }

    /// <summary>列出会被打进去的 entry 名（已排除打包器自己生成的文件，按写入顺序）。</summary>
    public static IReadOnlyList<string> ListEntryNames(string workDir)
    {
        var root = Path.GetFullPath(workDir);
        return CollectEntries(root).Select(entry => entry.EntryName).ToList();
    }

    /// <summary>
    /// 打包器自己生成的文件，工作区里即使有也不许打进包（计划第 3 条，审计 S3/I23）。
    /// 判据是「相对路径」，大小写不敏感。
    /// </summary>
    public static bool IsPackagerGenerated(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return false;

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Equals(MimetypeEntryName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (normalized.Equals(SessionFileName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (normalized.StartsWith(SnapshotsDirectoryName + "/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.StartsWith(DeletedDirectoryName + "/", StringComparison.OrdinalIgnoreCase))
            return true;

        // 打包/保存过程中的临时文件与备份，任何情况下都不许进包
        return normalized.EndsWith(".ets-tmp", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".ets-bak", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ZIP entry 名一律用正斜杠，且不许以斜杠开头（计划第 7 条）。</summary>
    public static string NormalizeEntryName(string relativePath)
        => relativePath.Replace('\\', '/').TrimStart('/');

    /// <summary>图片/字体本来就是压缩格式，再压没收益还可能变大（计划第 4 条）。</summary>
    public static CompressionLevel CompressionFor(string entryName)
    {
        var extension = Path.GetExtension(entryName).ToLowerInvariant();
        return UncompressedExtensions.Contains(extension)
            ? CompressionLevel.NoCompression
            : CompressionLevel.Optimal;
    }

    private static IReadOnlyList<PackEntry> CollectEntries(string root)
    {
        var list = new List<PackEntry>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path);
            if (IsPackagerGenerated(relative))
                continue;

            var entryName = NormalizeEntryName(relative);
            if (entryName.Length == 0)
                continue;

            list.Add(new PackEntry(entryName, path));
        }

        // 写入顺序：mimetype（单独先写）-> META-INF/ -> 其余；同组内按序号排序，保证确定性
        list.Sort((left, right) =>
        {
            var rank = EntryRank(left.EntryName).CompareTo(EntryRank(right.EntryName));
            return rank != 0 ? rank : string.CompareOrdinal(left.EntryName, right.EntryName);
        });
        return list;
    }

    private static int EntryRank(string entryName)
        => entryName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    private static async Task BuildAsync(
        string partPath,
        IReadOnlyList<PackEntry> entries,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        await using var fs = new FileStream(
            partPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.None);

        // 计划第 2 条：不可 seek 的流会让每个条目带上 data descriptor，产出不合规。
        if (!fs.CanSeek)
        {
            throw new AppException(
                ErrorCode.OutputStreamNotSeekable,
                "内部错误：EPUB 输出流不可用（不可 seek 的流会产出不合规的 EPUB）。",
                $"FileStream.CanSeek == false，path = {partPath}");
        }

        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

        // 第一条：mimetype，必须 STORED，内容精确、无 BOM、无换行
        var mimetype = zip.CreateEntry(MimetypeEntryName, CompressionLevel.NoCompression);
        mimetype.LastWriteTime = FixedEntryTime;
        await using (var mimetypeStream = mimetype.Open())
        {
            var content = Encoding.UTF8.GetBytes(MimetypeContent);
            await mimetypeStream.WriteAsync(content, ct).ConfigureAwait(false);
        }

        var done = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var zipEntry = zip.CreateEntry(entry.EntryName, CompressionFor(entry.EntryName));
            zipEntry.LastWriteTime = FixedEntryTime;

            await using var target = zipEntry.Open();
            await using var source = File.OpenRead(entry.FullPath);
            await source.CopyToAsync(target, CopyBufferSize, ct).ConfigureAwait(false);

            done++;
            progress?.Report(done / (double)entries.Count * 0.95);
        }

        await fs.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string ResolveBackupPath(string finalPath, string? backupDirectory)
    {
        var name = Path.GetFileNameWithoutExtension(finalPath);

        if (string.IsNullOrWhiteSpace(backupDirectory))
            return finalPath + ".ets-bak";

        var directory = Path.GetFullPath(backupDirectory);
        Directory.CreateDirectory(directory);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(directory, $"{name}-{stamp}.epub");
        var index = 1;
        while (File.Exists(candidate))
            candidate = Path.Combine(directory, $"{name}-{stamp}-{++index}.epub");
        return candidate;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // 清理失败不影响结果，留着残渣也不能把成功的打包判成失败
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PackEntry(string EntryName, string FullPath);
}
