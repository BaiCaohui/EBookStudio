using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Services.Library;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.Engines.Images;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// 导入流水线的端到端冒烟（T1-2 验收）。
/// 一次运行覆盖四个场景，全部用真实文件系统与真实 SQLite：
///   A 真实 EPUB 导入成功；B 同一文件再导一次应识别为重复；
///   C 假 EPUB（随机字节改名）应被拒绝且不留下任何痕迹；
///   D 前面几步跑完后书库与暂存区里没有孤儿文件。
/// </summary>
public static class ImportSmokeTest
{
    public static async Task<SmokeReport> RunAsync(
        IBookImportService importer,
        IBookRepository repo,
        AppPaths paths,
        string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(paths);

        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        var libraryBefore = CountFiles(paths.LibraryDir);
        var foldersBefore = CountDirectories(paths.LibraryDir);
        var booksBefore = await repo.CountAsync().ConfigureAwait(true);

        // ---------- 场景 A：真实 EPUB ----------
        var first = await importer.ImportAsync(sourcePath, ImportOptions.Default, null)
            .ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "A-import-ok",
            first.Outcome == ImportOutcome.Imported,
            "结果=" + first.Outcome + "，用时 " + first.Duration.TotalMilliseconds.ToString("F0") + " ms"
                + (first.Message is null ? "" : "，" + first.Message)));

        if (first.Outcome != ImportOutcome.Imported || first.BookId is not Guid bookId)
        {
            // 后面全部依赖这一本，没成就没必要继续
            return Finish(paths, startedAt, stopwatch, checks);
        }

        checks.Add(new SmokeCheck(
            "A-library-file-copied",
            first.LibraryPath is not null
                && File.Exists(first.LibraryPath)
                && new FileInfo(first.LibraryPath).Length == new FileInfo(sourcePath).Length,
            first.LibraryPath ?? "(null)"));

        // 源文件必须原封不动（铁律：绝不原地修改用户文件）
        checks.Add(new SmokeCheck(
            "A-source-untouched",
            File.Exists(sourcePath) && new FileInfo(sourcePath).Length > 0,
            "源文件仍在：" + Path.GetFileName(sourcePath)));

        var stored = await repo.GetByIdAsync(bookId).ConfigureAwait(true);
        checks.Add(new SmokeCheck(
            "A-db-book-row",
            stored is not null && !string.IsNullOrWhiteSpace(stored.Title),
            stored is null ? "库里查不到这本书" : "标题=" + stored.Title));

        var format = stored?.Formats.FirstOrDefault();
        checks.Add(new SmokeCheck(
            "A-db-format-row",
            format is not null
                && format.Format == BookFormatKind.Epub
                && string.Equals(format.FilePath, first.LibraryPath, StringComparison.OrdinalIgnoreCase)
                && format.QuickFingerprint is { Length: > 0 },
            FormatDetail(format)));

        // 封面两档要么都在，要么都不在（不许只写一半）
        var coverFull = Path.Combine(paths.CoversCacheDir, bookId.ToString("D") + ".jpg");
        var coverThumb = Path.Combine(paths.CoversCacheDir, bookId.ToString("D") + "_t.jpg");
        var hasFull = File.Exists(coverFull);
        var hasThumb = File.Exists(coverThumb);
        checks.Add(new SmokeCheck(
            "A-cover-atomic",
            hasFull == hasThumb,
            "大图=" + (hasFull ? new FileInfo(coverFull).Length + "B" : "无")
                + "，缩略图=" + (hasThumb ? new FileInfo(coverThumb).Length + "B" : "无")));

        // ---------- 场景 B：重复导入 ----------
        // ---------- 场景 E：换封面之后书库网格的缩略图要跟着变（T3-3 判据 4）----------
        var coverService = new LibraryCoverService(paths, new ImageProcessor());
        var thumbBytesBefore = hasThumb ? File.ReadAllBytes(coverThumb) : null;

        var card = new BookCardViewModel(
            stored!, hasThumb ? coverThumb : null, new SemaphoreSlim(1, 1), NullLogger.Instance);
        await card.LoadCoverAsync().ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "E-card-old-cover-loaded",
            hasThumb ? card.Cover is not null : card.Cover is null,
            "卡片封面=" + (card.Cover is null ? "无（本来就没有封面）" : "已载入旧图")));

        var refreshed = await coverService.RefreshAsync(bookId, MakeTallPng()).ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "E-cover-cache-rewritten",
            refreshed
                && File.Exists(coverFull)
                && File.Exists(coverThumb)
                && (thumbBytesBefore is null || !File.ReadAllBytes(coverThumb).SequenceEqual(thumbBytesBefore)),
            "新大图=" + (File.Exists(coverFull) ? new FileInfo(coverFull).Length + "B" : "缺")
                + "，新缩略图=" + (File.Exists(coverThumb) ? new FileInfo(coverThumb).Length + "B" : "缺")));

        await card.ReloadCoverAsync().ConfigureAwait(true);
        var decoded = card.Cover as BitmapSource;

        checks.Add(new SmokeCheck(
            "E-card-reloaded-new-cover",
            decoded is not null
                && decoded.PixelWidth == BookCardViewModel.DecodePixelWidth
                && decoded.PixelHeight == 600,
            "卡片解码=" + (decoded is null ? "无" : decoded.PixelWidth + "x" + decoded.PixelHeight)
                + "（预期 " + BookCardViewModel.DecodePixelWidth + "x600）"));

        var second = await importer.ImportAsync(sourcePath, ImportOptions.Default, null)
            .ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "B-duplicate-detected",
            second.Outcome == ImportOutcome.Duplicate && second.BookId == bookId,
            "结果=" + second.Outcome + "，指向 " + (second.BookId?.ToString() ?? "(null)")));

        checks.Add(new SmokeCheck(
            "B-no-second-copy",
            CountFiles(paths.LibraryDir) == libraryBefore + 1,
            "Library 下文件数 " + CountFiles(paths.LibraryDir) + "（预期 " + (libraryBefore + 1) + "）"));

        // ---------- 场景 C：假 EPUB ----------
        var fakePath = Path.Combine(paths.TempDir, "smoke-invalid-" + Guid.NewGuid().ToString("N")[..8] + ".epub");
        var random = new byte[4096];
        Random.Shared.NextBytes(random);
        await File.WriteAllBytesAsync(fakePath, random).ConfigureAwait(true);

        var booksBeforeFake = await repo.CountAsync().ConfigureAwait(true);
        var fake = await importer.ImportAsync(fakePath, ImportOptions.Default, null)
            .ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "C-fake-epub-rejected",
            fake.Outcome == ImportOutcome.InvalidEpub,
            "结果=" + fake.Outcome + (fake.Message is null ? "" : "，" + fake.Message)));

        checks.Add(new SmokeCheck(
            "C-nothing-written",
            await repo.CountAsync().ConfigureAwait(true) == booksBeforeFake
                && CountFiles(paths.LibraryDir) == libraryBefore + 1,
            "书数未变，Library 文件数 " + CountFiles(paths.LibraryDir)));

        TryDelete(fakePath);

        // ---------- 场景 D：孤儿检查 ----------
        var leftovers = Directory.Exists(paths.TempDir)
            ? Directory.GetFiles(paths.TempDir, "import-*", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();
        checks.Add(new SmokeCheck(
            "D-no-leftover-staging",
            leftovers.Length == 0,
            leftovers.Length == 0 ? "暂存区干净" : "残留 " + leftovers.Length + " 个文件"));

        checks.Add(new SmokeCheck(
            "D-no-orphan-folder",
            CountDirectories(paths.LibraryDir) == foldersBefore + 1,
            "每本书一个目录：Library 下子目录 " + foldersBefore + " 到 " + CountDirectories(paths.LibraryDir) + " 个"));

        var total = await repo.CountAsync().ConfigureAwait(true);
        checks.Add(new SmokeCheck(
            "D-book-count",
            total == booksBefore + 1,
            "书库 " + booksBefore + " 到 " + total));

        return Finish(paths, startedAt, stopwatch, checks);
    }

    private static SmokeReport Finish(
        AppPaths paths, DateTimeOffset startedAt, Stopwatch stopwatch, List<SmokeCheck> checks)
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
            Path.Combine(paths.TempDir, "import-smoke.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return report;
    }

    private static string FormatDetail(EbookToolSuite.Core.Models.BookFormat? format)
    {
        if (format is null)
        {
            return "没有 book_formats 行";
        }

        var fp = format.QuickFingerprint;
        return "格式=" + format.Format
            + "，指纹=" + (fp is null ? "(空)" : fp[..Math.Min(12, fp.Length)])
            + "，大小=" + format.FileSize + "B";
    }

    private static int CountFiles(string dir)
        => Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length : 0;

    private static int CountDirectories(string dir)
        => Directory.Exists(dir) ? Directory.GetDirectories(dir, "*", SearchOption.TopDirectoryOnly).Length : 0;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 收尾清理失败不影响判定
        }
    }
    /// <summary>
    /// 造一张 640x1600 的竖版 PNG。缩略图会被写成 320x800，卡片按 DecodePixelWidth 解码后
    /// 是 240x600 用它能把"卡片确实重新读了新文件"钉死（旧图解出来的高度一定不是 600）。
    /// </summary>
    private static byte[] MakeTallPng()
    {
        const int width = 640;
        const int height = 1600;
        const int stride = width * 4;

        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x20;
            pixels[i + 1] = 0x90;
            pixels[i + 2] = 0x40;
            pixels[i + 3] = 0xFF;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

}