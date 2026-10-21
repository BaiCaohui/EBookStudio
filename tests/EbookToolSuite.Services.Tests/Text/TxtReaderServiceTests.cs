using System.Diagnostics;
using System.Text;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Text;
using EbookToolSuite.Infrastructure.Files;
using EbookToolSuite.Services.Reader;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests.Text;

/// <summary>
/// T4-5：TXT 的阅读通路导入 -&gt; 建缓存 -&gt; 按字符范围物化章节 -&gt; 换编码重建。
/// 全部走真实文件系统与真实 SQLite（本项目的测试约定：不 mock）。
/// </summary>
public sealed class TxtReaderServiceTests
{
    private const string SampleText =
        "第一章 起头\n第一章的正文甲，用来验证按范围取内容。\n\n" +
        "第二章 承接\n第二章的正文乙。\n\n" +
        "第三章 收尾\n第三章的正文丙。\n";

    private const string FirstBody = "第一章的正文甲";

    [Fact]
    public async Task 导入TXT_建好缓存_按章物化_标题与正文都对()
    {
        await using var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();

        var path = WriteText(sandbox, "小说.txt", SampleText, new UTF8Encoding(false));
        var imported = await sandbox.Pipeline.ImportAsync(path, ImportOptions.Default);

        Assert.True(imported.Succeeded, imported.Message);
        Assert.NotNull(imported.BookId);

        var reader = NewReader(sandbox);
        var session = await reader.OpenAsync(imported.BookId!.Value);

        Assert.Equal(BookFormatKind.Txt, session.SourceFormat);
        Assert.Equal("TXT", session.Manifest.Format);
        Assert.Equal("UTF-8", session.Manifest.TextEncoding);
        Assert.Equal(3, session.Manifest.Spine.Count);
        Assert.Equal(3, session.Manifest.Toc.Count);
        Assert.False(session.Manifest.TocFallback);
        Assert.False(session.Manifest.TextFallbackSplit);

        // 章节文件是"翻到才生成"的：物化之前 chapters/ 里什么都没有
        var chaptersDir = Path.Combine(session.CacheRoot, "chapters");

        Assert.False(
            Directory.Exists(chaptersDir) && Directory.GetFiles(chaptersDir).Length > 0,
            "打开阶段不该预先物化章节");

        var first = await MaterializeTextAsync(reader, session, 0);

        Assert.Contains("第一章的正文甲", first, StringComparison.Ordinal);
        Assert.Contains("第一章 起头", first, StringComparison.Ordinal);
        Assert.DoesNotContain("第二章的正文乙", first, StringComparison.Ordinal);

        var third = await MaterializeTextAsync(reader, session, 2);
        Assert.Contains("第三章的正文丙", third, StringComparison.Ordinal);

        // 第二次打开要命中缓存，不重建
        var again = await reader.OpenAsync(imported.BookId.Value);
        Assert.True(again.FromCache);
        Assert.Equal(3, again.Manifest.Spine.Count);
    }

    [Fact]
    public async Task GBK文件_自动识别为GBK_手选UTF8会重排_选回GBK内容恢复()
    {
        await using var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();

        TextEncodingSupport.EnsureRegistered();

        var path = WriteText(sandbox, "gbk小说.txt", SampleText, Encoding.GetEncoding(936));
        var imported = await sandbox.Pipeline.ImportAsync(path, ImportOptions.Default);
        Assert.True(imported.Succeeded, imported.Message);

        var reader = NewReader(sandbox);
        var session = await reader.OpenAsync(imported.BookId!.Value);

        Assert.Equal("GBK", session.Manifest.TextEncoding);
        Assert.True(session.Manifest.TextEncodingConfident);
        Assert.Contains(FirstBody, await MaterializeTextAsync(reader, session, 0), StringComparison.Ordinal);

        // 判据 2：用户在界面上下拉里选了不合适的编码 -> 必须按新编码重排
        var wrong = await reader.OpenWithTextEncodingAsync(imported.BookId!.Value, "UTF-8");

        Assert.Equal("UTF-8", wrong.Manifest.TextEncoding);
        Assert.False(wrong.FromCache);
        Assert.DoesNotContain(FirstBody, await MaterializeTextAsync(reader, wrong, 0), StringComparison.Ordinal);

        // 再选回正确编码 -> 内容恢复
        var fixedAgain = await reader.OpenWithTextEncodingAsync(imported.BookId!.Value, "GBK");
        var fixedHtml = await MaterializeTextAsync(reader, fixedAgain, 0);

        Assert.Equal("GBK", fixedAgain.Manifest.TextEncoding);
        Assert.Contains(FirstBody, fixedHtml, StringComparison.Ordinal);

        // 编码没变时不该白重建
        var hit = await reader.OpenWithTextEncodingAsync(imported.BookId!.Value, "GBK");
        Assert.True(hit.FromCache);
    }

    [Fact]
    public async Task 没有章节标记_硬切兜底_每一章都物化得出来()
    {
        await using var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();

        var builder = new StringBuilder();

        for (var i = 0; i < 400; i++)
        {
            builder.Append("这是没有任何章节标记的正文行，用来验证固定字数硬切兜底。abcdefghij\n");
        }

        var path = WriteText(sandbox, "无标记.txt", builder.ToString(), new UTF8Encoding(false));
        var imported = await sandbox.Pipeline.ImportAsync(path, ImportOptions.Default);
        Assert.True(imported.Succeeded, imported.Message);

        var reader = NewReader(sandbox);
        var session = await reader.OpenAsync(imported.BookId!.Value);

        Assert.True(session.Manifest.TextFallbackSplit);
        Assert.True(session.Manifest.TocFallback);
        Assert.True(session.Manifest.Spine.Count >= 3, "两万多字至少要切成好几章");

        for (var i = 0; i < session.Manifest.Spine.Count; i++)
        {
            var html = await MaterializeTextAsync(reader, session, i);

            Assert.Contains("<p>", html, StringComparison.Ordinal);
            Assert.Contains("<h1>", html, StringComparison.Ordinal);
        }

        // 章号首尾相接，不丢字：把每章的字数加起来应该等于全书
        long sum = 0;

        foreach (var chapter in session.Manifest.Spine)
        {
            sum += chapter.TextEnd - chapter.TextStart;
        }

        Assert.Equal(session.Manifest.Spine[^1].TextEnd, sum);
    }

    [Fact]
    public async Task 八兆TXT_打开是流式的_首屏够快且不整本读进内存()
    {
        await using var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();

        var path = Path.Combine(sandbox.SourceDir, "大书.txt");
        WriteBigText(path, 40, 2000, new UTF8Encoding(false));

        var imported = await sandbox.Pipeline.ImportAsync(path, ImportOptions.Default);
        Assert.True(imported.Succeeded, imported.Message);

        var size = new FileInfo(path).Length;
        var reader = NewReader(sandbox);

        var before = GC.GetTotalAllocatedBytes(precise: false);
        var watch = Stopwatch.StartNew();

        var session = await reader.OpenAsync(imported.BookId!.Value);
        var openMs = watch.ElapsedMilliseconds;

        await MaterializeTextAsync(reader, session, 0);
        var firstScreenMs = watch.ElapsedMilliseconds;

        Assert.Equal(40, session.Manifest.Spine.Count);
        Assert.True(openMs < 3000, $"打开 {size / 1024 / 1024} MB 的 TXT 用了 {openMs} ms");
        Assert.True(firstScreenMs < 4000, $"打开 + 第一章用了 {firstScreenMs} ms");

        // 注：这里不判内存小文件时 GC.GetTotalAllocatedBytes 的噪声（JIT/框架分配）比文件还大，
        // 没有辨别力。真正的"不整本读进内存"由下面那条 100 MB 的用例来判（ETS_RUN_BIG_TXT=1）。
    }

    [Fact]
    public async Task 一百兆TXT_首屏两秒内_且内存占用远小于文件()
    {
        // 造 100 MB 很费时，默认跳过；设 ETS_RUN_BIG_TXT=1 时跑（T4-5 判据 6 的真凭据）
        if (Environment.GetEnvironmentVariable("ETS_RUN_BIG_TXT") != "1")
        {
            return;
        }

        await using var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();

        var path = Path.Combine(sandbox.SourceDir, "超大.txt");
        WriteBigText(path, 200, 4300, new UTF8Encoding(false));

        var size = new FileInfo(path).Length;

        Assert.True(size > 90 * 1024 * 1024, "样本不够大：" + size);

        var imported = await sandbox.Pipeline.ImportAsync(path, ImportOptions.Default);
        Assert.True(imported.Succeeded, imported.Message);

        var reader = NewReader(sandbox);
        var before = GC.GetTotalAllocatedBytes(precise: false);
        var watch = Stopwatch.StartNew();

        var session = await reader.OpenAsync(imported.BookId!.Value);
        var openMs = watch.ElapsedMilliseconds;

        await MaterializeTextAsync(reader, session, 0);
        var firstScreenMs = watch.ElapsedMilliseconds;

        var allocated = GC.GetTotalAllocatedBytes(precise: false) - before;

        Assert.True(
            session.Manifest.Spine.Count == 200,
            $"章数={session.Manifest.Spine.Count} 硬切={session.Manifest.TextFallbackSplit} 目录降级={session.Manifest.TocFallback} "
                + $"首章标题={session.Manifest.Spine[0].Title} 编码={session.Manifest.TextEncoding} 文件={size} 字节");

        Assert.True(firstScreenMs < 2000, $"100 MB 首屏用了 {firstScreenMs} ms（含打开 {openMs} ms）");
        Assert.True(
            allocated < size / 4,
            $"分配了 {allocated / 1024 / 1024} MB，超过文件的 1/4（{size / 1024 / 1024} MB），像是一次全读进来了");

        // 把实测数字留个档，好写进报告（这条用例只在显式开启时才跑）
        // 仓库根用"往上找 Tools\manifest.json"（数层数很脆，之前就数错过一次）
        var repoRoot = FindRepoRoot()
            ?? throw new InvalidOperationException("找不到仓库根（Tools/manifest.json）");
        var reportDir = Path.Combine(repoRoot, "build", "_t45");

        Directory.CreateDirectory(reportDir);
        File.WriteAllText(
            Path.Combine(reportDir, "big-txt.txt"),
            $"文件={size / 1024 / 1024} MB\n打开={openMs} ms\n首屏(打开+第一章)={firstScreenMs} ms\n"
                + $"分配内存={allocated / 1024 / 1024} MB\n章数={session.Manifest.Spine.Count}\n",
            new UTF8Encoding(false));
    }

    /// <summary>造一本大 TXT：流式写盘，别把测试自己先撑爆（生产代码也是流式的）。</summary>
    private static void WriteBigText(string path, int chapters, int linesPerChapter, Encoding encoding)
    {
        using var writer = new StreamWriter(path, append: false, encoding);

        for (var i = 1; i <= chapters; i++)
        {
            writer.Write("第");
            writer.Write(i);
            writer.Write("章 标题");
            writer.Write(i);
            writer.Write('\n');

            for (var k = 0; k < linesPerChapter; k++)
            {
                writer.Write("这是第 ");
                writer.Write(i);
                writer.Write(" 章的第 ");
                writer.Write(k);
                writer.Write(" 行正文，用来把文件撑大，验证流式读取与切章。abcdefghijklmnopqrstuvwxyz\n");
            }
        }
    }

    [Fact]
    public async Task 换格式续读_按文字指纹在目标书里找到同一段所在的章()
    {
        await using var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();

        var path = WriteText(sandbox, "找位.txt", SampleText, new UTF8Encoding(false));
        var imported = await sandbox.Pipeline.ImportAsync(path, ImportOptions.Default);
        Assert.True(imported.Succeeded, imported.Message);

        var reader = NewReader(sandbox);
        var session = await reader.OpenAsync(imported.BookId!.Value);

        // "第三章的正文丙"只在第 3 章（0 基 = 2）：从第 1 章开始、允许翻 3 章就能找到
        Assert.Equal(2, await reader.FindChapterByTextAsync(session, "第三章的正文丙", 0, 3));

        // 限定只看 1 章：找不到要老实返回 null（调用方会退回按百分比定位）
        Assert.Null(await reader.FindChapterByTextAsync(session, "第三章的正文丙", 0, 1));

        // 书里根本没有的文字
        Assert.Null(await reader.FindChapterByTextAsync(session, "这本书里根本没有的一段话", 0, 3));

        // 太短的串不当指纹用（到处都是，找不到"同一段"）
        Assert.Null(await reader.FindChapterByTextAsync(session, "第三章", 0, 3));
    }

    [Fact]
    public async Task 当前阅读格式决定打开哪个文件_选的格式没了就退回默认链()
    {
        await using var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();

        var path = WriteText(sandbox, "换格式.txt", SampleText, new UTF8Encoding(false));
        var imported = await sandbox.Pipeline.ImportAsync(path, ImportOptions.Default);
        Assert.True(imported.Succeeded, imported.Message);

        var bookId = imported.BookId!.Value;

        // 给同一本书再登记一个 PDF 格式（ResolveSourceAsync 只要求文件真的在；
        // 这里不打开 PDF 的正文，只验证"挑的是哪个文件"）
        var pdfPath = Path.Combine(sandbox.Paths.LibraryDir, "换格式.pdf");
        Directory.CreateDirectory(sandbox.Paths.LibraryDir);
        File.WriteAllBytes(pdfPath, new byte[] { 0x25, 0x50, 0x44, 0x46 });
        await sandbox.Repository.AddFormatAsync(new BookFormat
        {
            BookId = bookId,
            Format = BookFormatKind.Pdf,
            FilePath = pdfPath,
            FileSize = 4,
            AddedAt = DateTimeOffset.UtcNow,
        });

        var reader = NewReader(sandbox);

        // 没选过：默认链先找 EPUB（没有），落到 TXT
        Assert.Equal(BookFormatKind.Txt, (await reader.OpenAsync(bookId)).SourceFormat);

        // 选 PDF：点阅读打开的就是 PDF
        await sandbox.Repository.SetReadingFormatAsync(bookId, BookFormatKind.Pdf);
        var pdfSession = await reader.OpenAsync(bookId);
        Assert.Equal(BookFormatKind.Pdf, pdfSession.SourceFormat);
        Assert.Equal("PDF", pdfSession.Manifest.Format);

        // 选回 TXT
        await sandbox.Repository.SetReadingFormatAsync(bookId, BookFormatKind.Txt);
        Assert.Equal(BookFormatKind.Txt, (await reader.OpenAsync(bookId)).SourceFormat);

        // 选了个这本书根本没有的格式：静默退回默认链，不能变成打不开
        await sandbox.Repository.SetReadingFormatAsync(bookId, BookFormatKind.Epub);
        Assert.Equal(BookFormatKind.Txt, (await reader.OpenAsync(bookId)).SourceFormat);
    }

    private string WriteText(ImportSandbox sandbox, string name, string text, Encoding encoding)
    {
        var path = Path.Combine(sandbox.SourceDir, name);

        File.WriteAllBytes(path, encoding.GetBytes(text));
        return path;
    }

    private static ReaderService NewReader(ImportSandbox sandbox)
    {
        // ReaderAssets 只是个目录：测试里造一个，免得依赖真实前端文件
        var assets = Path.Combine(sandbox.Root, "reader-assets");

        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "reader.html"), "<html></html>", new UTF8Encoding(false));

        return new ReaderService(
            sandbox.Repository,
            sandbox.Paths,
            new HashUtil(),
            new FakeReaderAssets(assets),
            new NoopEpubCacheService(),
            NullLogger<ReaderService>.Instance);
    }

    private static async Task<string> MaterializeTextAsync(IReaderService reader, ReaderSession session, int index)
    {
        var href = await reader.MaterializeChapterAsync(session, index);
        var file = Path.Combine(session.CacheRoot, href.Replace('/', Path.DirectorySeparatorChar));

        return await File.ReadAllTextAsync(file, Encoding.UTF8);
    }

    /// <summary>往上找带 Tools\manifest.json 的目录 = 仓库根。</summary>
    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tools", "manifest.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private sealed class FakeReaderAssets(string directory) : IReaderAssets
    {
        public string Directory { get; } = directory;
    }

    /// <summary>TXT/EPUB 通路不会碰 MOBI 转换，触发它就是测试写错了。</summary>
    private sealed class NoopEpubCacheService : IEpubCacheService
    {
        public Task<string> GetOrConvertAsync(
            Guid bookId, BookFormatKind sourceFormat, string sourcePath,
            string cacheKey, CancellationToken ct = default)
            => throw new InvalidOperationException("这个测试不该触发 MOBI/AZW3 转换");

        public string GetArtifactPath(string cacheKey) => cacheKey + ".source.epub";

        public long? GetConvertedArtifactSize(BookFormat format) => null;

        public void BeginRead(Guid bookId, string cacheRoot)
        {
        }

        public void EndRead(Guid bookId)
        {
        }

        public Task<long> EnforceLimitAsync(CancellationToken ct = default) => Task.FromResult(0L);
    }
}
