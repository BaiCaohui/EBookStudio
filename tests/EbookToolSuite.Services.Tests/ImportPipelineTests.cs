using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Files;

namespace EbookToolSuite.Services.Tests;

public sealed class ImportPipelineTests
{
    // ---------- 正向路径 ----------

    [Fact]
    public async Task Import_ValidEpubWithCover_CopiesFileWritesCoversAndDbRows()
    {
        await using var sandbox = await NewSandboxAsync();
        var source = sandbox.WriteEpub(
            new EpubBuilder { Title = "Sample Book I", CoverBytes = EpubBuilder.MakeJpeg(400, 600) },
            "book.epub");

        var result = await sandbox.Pipeline.ImportAsync(source, ImportOptions.Default);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        Assert.NotNull(result.BookId);
        Assert.NotNull(result.LibraryPath);
        Assert.True(File.Exists(result.LibraryPath));
        Assert.Equal(new FileInfo(source).Length, new FileInfo(result.LibraryPath!).Length);

        var book = await sandbox.Repository.GetByIdAsync(result.BookId!.Value);
        Assert.NotNull(book);
        Assert.Equal("Sample Book I", book!.Title);
        Assert.Single(book.Formats);
        Assert.Equal(BookFormatKind.Epub, book.Formats[0].Format);
        Assert.Equal(result.LibraryPath, book.Formats[0].FilePath);
        Assert.False(string.IsNullOrWhiteSpace(book.Formats[0].QuickFingerprint));
        Assert.NotNull(book.CoverPath);
        Assert.Equal(2, sandbox.CoverFiles().Length);
    }

    [Fact]
    public async Task Import_EpubWithoutCover_LeavesCoverPathNullAndWritesNoCoverFiles()
    {
        await using var sandbox = await NewSandboxAsync();
        var source = sandbox.WriteEpub(new EpubBuilder { Title = "无封面的书" }, "nocover.epub");

        var result = await sandbox.Pipeline.ImportAsync(source, ImportOptions.Default);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        var book = await sandbox.Repository.GetByIdAsync(result.BookId!.Value);
        Assert.Null(book!.CoverPath);
        Assert.Empty(sandbox.CoverFiles());
    }

    [Fact]
    public async Task Import_CorruptCoverBytes_UsesPlaceholderInsteadOfCover()
    {
        await using var sandbox = await NewSandboxAsync();
        var source = sandbox.WriteEpub(
            new EpubBuilder
            {
                Title = "封面是坏图",
                CoverBytes = "这不是一张图片"u8.ToArray(),
            },
            "badcover.epub");

        var result = await sandbox.Pipeline.ImportAsync(source, ImportOptions.Default);

        // 封面解不开不能让导入失败，只是没有封面
        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        var book = await sandbox.Repository.GetByIdAsync(result.BookId!.Value);
        Assert.Null(book!.CoverPath);
        Assert.Empty(sandbox.CoverFiles());
    }

    [Fact]
    public async Task Import_ChineseFileName_KeptInLibraryFolder()
    {
        await using var sandbox = await NewSandboxAsync();
        var source = sandbox.WriteEpub(
            new EpubBuilder { Title = "Sample Book A" },
            "Sample Book A.epub");

        var result = await sandbox.Pipeline.ImportAsync(source, ImportOptions.Default);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        Assert.Contains("Sample Book A", Path.GetFileName(result.LibraryPath!));
        Assert.True(File.Exists(result.LibraryPath!));
    }

        [Fact]
    public async Task Import_FileNameWithSpacesBracketsAndDots_IsPreserved()
    {
        await using var sandbox = await NewSandboxAsync();
        const string name = "sample-epub-2.epub";
        var source = sandbox.WriteEpub(new EpubBuilder { Title = "带空格与括号" }, name);

        var result = await sandbox.Pipeline.ImportAsync(source, ImportOptions.Default);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);

        var stored = Path.GetFileName(result.LibraryPath!);
        Assert.Contains("Sample Book A", stored);
        Assert.EndsWith(".epub", stored);

        // 必须老老实实落在 Library 目录里，不能因为路径拼接跑出去
        Assert.StartsWith(sandbox.Paths.LibraryDir, result.LibraryPath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_MetadataIsPersisted()
    {
        await using var sandbox = await NewSandboxAsync();
        var source = sandbox.WriteEpub(
            new EpubBuilder
            {
                Title = "Sample Book K",
                Authors = new List<string> { "阿西莫夫", "另一个人" },
                Publisher = "江苏文艺出版社",
                Language = "zh-CN",
                Series = "Sample Book K",
                SeriesIndex = 3,
                Isbn = "9787539942742",
                Description = "简介文本",
                ChapterCount = 3,
            },
            "meta.epub");

        var result = await sandbox.Pipeline.ImportAsync(source, ImportOptions.Default);
        var book = await sandbox.Repository.GetByIdAsync(result.BookId!.Value);

        Assert.Equal("Sample Book K", book!.Title);
        Assert.Equal("阿西莫夫; 另一个人", book.Authors);
        Assert.Equal("江苏文艺出版社", book.Publisher);
        Assert.Equal("9787539942742", book.Isbn);
        Assert.Equal("Sample Book K", book.Series);
        Assert.Equal(3d, book.SeriesIndex);
    }

    [Fact]
    public async Task Import_Epub2_IsReadable()
    {
        await using var sandbox = await NewSandboxAsync();
        var source = sandbox.WriteEpub(
            new EpubBuilder { Title = "老格式的书", IsEpub3 = false, CoverBytes = EpubBuilder.MakeJpeg() },
            "epub2.epub");

        var result = await sandbox.Pipeline.ImportAsync(source, ImportOptions.Default);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        var book = await sandbox.Repository.GetByIdAsync(result.BookId!.Value);
        Assert.Equal("老格式的书", book!.Title);
    }

    // ---------- 拒绝路径 ----------

    [Fact]
    public async Task Import_FakeEpub_ReturnsInvalidEpubAndWritesNothing()
    {
        await using var sandbox = await NewSandboxAsync();
        var fake = Path.Combine(sandbox.SourceDir, "fake.epub");
        await File.WriteAllBytesAsync(fake, RandomBytes(4096));

        var result = await sandbox.Pipeline.ImportAsync(fake, ImportOptions.Default);

        Assert.Equal(ImportOutcome.InvalidEpub, result.Outcome);
        Assert.Empty(sandbox.LibraryFiles());
        Assert.Empty(sandbox.StagingLeftovers());
        Assert.Equal(0, await sandbox.BookCountAsync());
    }

    [Fact]
    public async Task Import_ZipThatIsNotAnEpub_ReturnsInvalidEpub()
    {
        await using var sandbox = await NewSandboxAsync();

        // 一个合法 zip，但没有 META-INF/container.xml
        var path = Path.Combine(sandbox.SourceDir, "plain.epub");
        using (var zip = System.IO.Compression.ZipFile.Open(
            path, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("readme.txt");
            using var stream = entry.Open();
            stream.Write("just a zip"u8);
        }

        var result = await sandbox.Pipeline.ImportAsync(path, ImportOptions.Default);

        Assert.Equal(ImportOutcome.InvalidEpub, result.Outcome);
        Assert.Equal(0, await sandbox.BookCountAsync());
    }

    [Fact]
    public async Task Import_DrmMarkedEpub_ReturnsDrmProtectedAndWritesNothing()
    {
        await using var sandbox = await NewSandboxAsync();
        var source = sandbox.WriteEpub(new EpubBuilder { MarkEncrypted = true }, "drm.epub");

        var result = await sandbox.Pipeline.ImportAsync(source, ImportOptions.Default);

        Assert.Equal(ImportOutcome.DrmProtected, result.Outcome);
        Assert.Empty(sandbox.LibraryFiles());
        Assert.Equal(0, await sandbox.BookCountAsync());
    }

    [Fact]
    public async Task Import_MissingFile_ReturnsUnreadable()
    {
        await using var sandbox = await NewSandboxAsync();
        var missing = Path.Combine(sandbox.SourceDir, "不存在.epub");

        var result = await sandbox.Pipeline.ImportAsync(missing, ImportOptions.Default);

        Assert.Equal(ImportOutcome.SourceUnreadable, result.Outcome);
    }

    [Fact]
    public async Task Import_NonEpubExtension_ReturnsUnsupported()
    {
        await using var sandbox = await NewSandboxAsync();
        var path = Path.Combine(sandbox.SourceDir, "book.pdf");
        await File.WriteAllBytesAsync(path, RandomBytes(64));

        var result = await sandbox.Pipeline.ImportAsync(path, ImportOptions.Default);

        Assert.Equal(ImportOutcome.UnsupportedFormat, result.Outcome);
    }

    // ---------- 去重 ----------

    [Fact]
    public async Task Import_SameFileTwice_SecondIsDuplicateAndDoesNotCopyAgain()
    {
        await using var sandbox = await NewSandboxAsync();
        var source = sandbox.WriteEpub(new EpubBuilder { Title = "重复的书" }, "dup.epub");

        var first = await sandbox.Pipeline.ImportAsync(source, ImportOptions.Default);
        var second = await sandbox.Pipeline.ImportAsync(source, ImportOptions.Default);

        Assert.Equal(ImportOutcome.Imported, first.Outcome);
        Assert.Equal(ImportOutcome.Duplicate, second.Outcome);
        Assert.Equal(first.BookId, second.BookId);
        Assert.Single(sandbox.LibraryFiles());
        Assert.Equal(1, await sandbox.BookCountAsync());
    }

    [Fact]
    public async Task Import_ImportDuplicatesOption_ForcesSecondCopy()
    {
        await using var sandbox = await NewSandboxAsync();
        var source = sandbox.WriteEpub(new EpubBuilder { Title = "故意重复" }, "dup2.epub");

        await sandbox.Pipeline.ImportAsync(source, ImportOptions.Default);
        var second = await sandbox.Pipeline.ImportAsync(
            source, new ImportOptions { ImportDuplicates = true });

        Assert.Equal(ImportOutcome.Imported, second.Outcome);
        Assert.Equal(2, await sandbox.BookCountAsync());
        Assert.Equal(2, sandbox.LibraryFiles().Length);
    }

    [Fact]
    public async Task Import_TwoDifferentFiles_WithSameContent_AreDuplicates()
    {
        await using var sandbox = await NewSandboxAsync();
        var builder = new EpubBuilder { Title = "同一本书" };

        var a = sandbox.WriteEpub(builder, "a.epub");

        // 复制一份：保证逐字节相同，只有路径不同
        var b = Path.Combine(sandbox.SourceDir, "b.epub");
        File.Copy(a, b);

        // 内容逐字节相同（builder 每次都写同样的东西）
        var first = await sandbox.Pipeline.ImportAsync(a, ImportOptions.Default);
        var second = await sandbox.Pipeline.ImportAsync(b, ImportOptions.Default);

        Assert.Equal(ImportOutcome.Imported, first.Outcome);
        Assert.Equal(ImportOutcome.Duplicate, second.Outcome);
    }

    // ---------- 取消 ----------

    [Fact]
    public async Task Import_CanceledDuringCopy_LeavesNoOrphanFiles()
    {
        await using var sandbox = await NewSandboxAsync();
        var source = sandbox.WriteEpub(new EpubBuilder { Title = "会被取消" }, "cancel.epub");

        using var cts = new CancellationTokenSource();
        var progress = new CancelOnPhaseProgress(ImportPhase.Copying, cts);

        var result = await sandbox.Pipeline.ImportAsync(
            source, ImportOptions.Default, progress, cts.Token);

        Assert.Equal(ImportOutcome.Canceled, result.Outcome);
        Assert.Empty(sandbox.LibraryFiles());
        Assert.Empty(sandbox.StagingLeftovers());
        Assert.Equal(0, await sandbox.BookCountAsync());
    }

    [Fact]
    public async Task Import_AlreadyCanceledToken_ReturnsCanceledWithoutWriting()
    {
        await using var sandbox = await NewSandboxAsync();
        var source = sandbox.WriteEpub(new EpubBuilder { Title = "一开始就取消" }, "cancel2.epub");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await sandbox.Pipeline.ImportAsync(
            source, ImportOptions.Default, null, cts.Token);

        Assert.Equal(ImportOutcome.Canceled, result.Outcome);
        Assert.Empty(sandbox.LibraryFiles());
        Assert.Equal(0, await sandbox.BookCountAsync());
    }

    [Fact]
    public async Task Import_FingerprintCollision_TreatedAsNewBookNotDuplicate()
    {
        await using var sandbox = await NewSandboxAsync();
        var source = sandbox.WriteEpub(new EpubBuilder { Title = "碰撞测试" }, "collide.epub");

        var hasher = new HashUtil();
        var fingerprint = await hasher.QuickFingerprintAsync(source);
        var contentHash = await hasher.ComputeContentHashAsync(source);

        // 植入一条 quick_fingerprint 相同、full_hash 明显不同的记录（模拟指纹碰撞）
        var plantedId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await sandbox.Repository.AddAsync(new Book
        {
            Id = plantedId,
            Title = "先来的那本",
            Authors = "别人",
            CreatedAt = now,
            UpdatedAt = now,
            Formats =
            {
                new BookFormat
                {
                    BookId = plantedId,
                    Format = BookFormatKind.Epub,
                    FilePath = Path.Combine(sandbox.Root, "planted.epub"),
                    FileSize = 123,
                    QuickFingerprint = fingerprint,
                    FullHash = new string((char)48, 64),
                    AddedAt = now,
                },
            },
        });

        var result = await sandbox.Pipeline.ImportAsync(source, ImportOptions.Default);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        Assert.NotEqual(plantedId, result.BookId);
        Assert.Equal(2, await sandbox.BookCountAsync());

        var imported = await sandbox.Repository.GetByIdAsync(result.BookId!.Value);
        Assert.Equal(contentHash, imported!.Formats[0].FullHash);
    }

    [Fact]
    public async Task Import_DuplicateWithUnknownContentHash_ComputesAndWritesItBack()
    {
        await using var sandbox = await NewSandboxAsync();
        var source = sandbox.WriteEpub(new EpubBuilder { Title = "写回测试" }, "writeback.epub");

        var first = await sandbox.Pipeline.ImportAsync(source, ImportOptions.Default);
        Assert.Equal(ImportOutcome.Imported, first.Outcome);

        // 导入路径不算全文件哈希，所以第一次入库后 full_hash 应该是空的
        var before = await sandbox.Repository.GetByIdAsync(first.BookId!.Value);
        Assert.Null(before!.Formats[0].FullHash);

        var second = await sandbox.Pipeline.ImportAsync(source, ImportOptions.Default);
        Assert.Equal(ImportOutcome.Duplicate, second.Outcome);

        // 复核时必须把算出来的哈希写回，否则下次还得再算一遍
        var expected = await new HashUtil().ComputeContentHashAsync(source);
        var after = await sandbox.Repository.GetByIdAsync(first.BookId!.Value);
        Assert.Equal(expected, after!.Formats[0].FullHash);
        Assert.Equal(1, await sandbox.BookCountAsync());
    }

    // ---------- T6-3 漫画（CBZ/CBR） ----------

    [Fact]
    public async Task Import_CbzWithImages_ImportsAndUsesFirstPageAsCover()
    {
        await using var sandbox = await NewSandboxAsync();
        var path = Path.Combine(sandbox.SourceDir, "comic.cbz");

        using (var stream = File.Create(path))
        using (var zip = new System.IO.Compression.ZipArchive(
                   stream, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "page10.jpg", "page2.jpg", "page1.jpg" })
            {
                var entry = zip.CreateEntry(name);

                using var entryStream = entry.Open();
                entryStream.Write(EpubBuilder.MakeJpeg(120, 180));
            }
        }

        var result = await sandbox.Pipeline.ImportAsync(path, ImportOptions.Default);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        Assert.Equal(1, await sandbox.BookCountAsync());

        var book = await sandbox.Repository.GetByIdAsync(result.BookId!.Value);

        Assert.NotNull(book);
        Assert.Equal(BookFormatKind.Cbz, book!.Formats[0].Format);

        // 封面＝第 1 页渲染图（不是占位图）：与 PDF 判据同一口径。
        Assert.NotNull(book.CoverPath);
        Assert.Equal(2, sandbox.CoverFiles().Length);
    }

    [Fact]
    public async Task Import_CbrWithUnsupportedAlgorithm_ReturnsInvalidWithReason()
    {
        await using var sandbox = await NewSandboxAsync();
        var path = Path.Combine(sandbox.SourceDir, "broken.cbr");

        /* 认得出是压缩包（ZIP 签名），但结构坏了读不出条目。
           计划 T6-3 的坑：遇到不支持的要**明确提示**，不能当"不支持的格式"静默跳过。 */
        var bytes = new byte[80];
        bytes[0] = 0x50;
        bytes[1] = 0x4B;
        bytes[2] = 0x03;
        bytes[3] = 0x04;
        Random.Shared.NextBytes(bytes.AsSpan(4));
        await File.WriteAllBytesAsync(path, bytes);

        var result = await sandbox.Pipeline.ImportAsync(path, ImportOptions.Default);

        Assert.Equal(ImportOutcome.InvalidEpub, result.Outcome);
        Assert.Contains("不支持", result.Message!);
        Assert.Equal(0, await sandbox.BookCountAsync());
    }

    // ---------- 辅助 ----------

    private static async Task<ImportSandbox> NewSandboxAsync()
    {
        var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();
        return sandbox;
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    /// <summary>同步的 IProgress，保证 Report 在流水线线程里立刻执行（用 Progress&lt;T&gt; 会派发到线程池，时序不可控）。</summary>
    private sealed class CancelOnPhaseProgress : IProgress<ImportProgress>
    {
        private readonly ImportPhase _phase;
        private readonly CancellationTokenSource _cts;

        public CancelOnPhaseProgress(ImportPhase phase, CancellationTokenSource cts)
        {
            _phase = phase;
            _cts = cts;
        }

        public void Report(ImportProgress value)
        {
            if (value.Phase == _phase)
            {
                _cts.Cancel();
            }
        }
    }
}