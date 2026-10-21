using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Services.Conversion;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests.Conversion;

public sealed class ConversionServiceTests
{
    [Theory]
    [InlineData(BookFormatKind.Epub, BookFormatKind.Mobi, true)]
    [InlineData(BookFormatKind.Epub, BookFormatKind.Pdf, true)]
    [InlineData(BookFormatKind.Epub, BookFormatKind.Txt, true)]
    [InlineData(BookFormatKind.Txt, BookFormatKind.Epub, true)]
    [InlineData(BookFormatKind.Mobi, BookFormatKind.Epub, true)]
    [InlineData(BookFormatKind.Epub, BookFormatKind.Cbz, false)]
    [InlineData(BookFormatKind.Pdf, BookFormatKind.Mobi, false)]
    public void IsSupported_矩阵(BookFormatKind source, BookFormatKind target, bool expected)
    {
        var service = CreateService(new FakeProcessHost(), new ImportSandbox());

        Assert.Equal(expected, service.IsSupported(source, target));
    }

    [Fact]
    public async Task ConvertAsync_成功时产物落正式区且库里有记录()
    {
        await using var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();

        var bookId = await ImportOneAsync(sandbox);
        var host = new FakeProcessHost();
        var service = CreateService(host, sandbox);

        var result = await service.ConvertAsync(new ConversionRequest
        {
            BookId = bookId,
            TargetFormat = BookFormatKind.Mobi,
        });

        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(".mobi", Path.GetExtension(result.OutputPath));
        Assert.Contains("--output-profile", result.CommandLine);

        // 产物必须在正式区（这本书自己的目录），不能落在临时目录里
        Assert.StartsWith(sandbox.Paths.LibraryDir, result.OutputPath, StringComparison.Ordinal);

        var reloaded = await sandbox.Repository.GetByIdAsync(bookId);
        Assert.Contains(reloaded!.Formats, f => f.Format == BookFormatKind.Mobi);
    }

    [Fact]
    public async Task ConvertAsync_同一格式再转一次会被拦住()
    {
        await using var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();

        var bookId = await ImportOneAsync(sandbox);
        var service = CreateService(new FakeProcessHost(), sandbox);
        var request = new ConversionRequest { BookId = bookId, TargetFormat = BookFormatKind.Mobi };

        await service.ConvertAsync(request);
        var error = await Assert.ThrowsAsync<AppException>(() => service.ConvertAsync(request));

        Assert.Equal(ErrorCode.ConversionOutputExists, error.Code);
    }

    [Fact]
    public async Task ConvertAsync_不支持的链路直接拒绝()
    {
        await using var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();

        var bookId = await ImportOneAsync(sandbox);
        var service = CreateService(new FakeProcessHost(), sandbox);

        var error = await Assert.ThrowsAsync<AppException>(() => service.ConvertAsync(
            new ConversionRequest { BookId = bookId, TargetFormat = BookFormatKind.Cbz }));

        Assert.Equal(ErrorCode.ConversionUnsupported, error.Code);
    }

    [Fact]
    public async Task ConvertAsync_重名文件不会被覆盖_而是加序号()
    {
        await using var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();

        var bookId = await ImportOneAsync(sandbox);
        var service = CreateService(new FakeProcessHost(), sandbox);

        // 先手动放一个同名目标文件：转换成 TXT 时必须退让成 "书名 (2).txt"
        var book = await sandbox.Repository.GetByIdAsync(bookId);
        var epubPath = book!.Formats.First(f => f.Format == BookFormatKind.Epub).FilePath;
        var directory = Path.GetDirectoryName(epubPath)!;
        var stem = Path.GetFileNameWithoutExtension(epubPath);
        var occupied = Path.Combine(directory, stem + ".txt");
        await File.WriteAllTextAsync(occupied, "占位");

        var result = await service.ConvertAsync(new ConversionRequest
        {
            BookId = bookId,
            TargetFormat = BookFormatKind.Txt,
        });

        Assert.NotEqual(occupied, result.OutputPath);
        Assert.Contains("(2)", Path.GetFileName(result.OutputPath), StringComparison.Ordinal);
        Assert.Equal("占位", await File.ReadAllTextAsync(occupied));
    }

    private static async Task<Guid> ImportOneAsync(ImportSandbox sandbox)
    {
        var path = sandbox.WriteEpub(new EpubBuilder { Title = "测试书" }, "测试书.epub");
        var imported = await sandbox.Pipeline.ImportAsync(path, ImportOptions.Default, null);
        return imported.BookId!.Value;
    }

    private static ConversionService CreateService(IProcessHost host, ImportSandbox sandbox)
        => new(
            sandbox.Paths,
            new FakeToolLocator(sandbox.Paths),
            sandbox.Repository,
            new FakeConfigStore(),
            host,
            SystemClock.Instance,
            NullLogger<ConversionService>.Instance);

    /// <summary>进程替身：把"Calibre 写出了产物"这件事伪造出来，其余逻辑全部走真实代码。</summary>
    private sealed class FakeProcessHost : IProcessHost
    {
        public Task<ProcessResult> RunAsync(
            ProcessSpec spec, IProgress<ProcessOutputLine>? output = null, CancellationToken ct = default)
        {
            // ArgumentList 的第二个位置是临时输出文件（第一个是输入文件）
            var tempOutput = spec.Arguments[1];
            Directory.CreateDirectory(Path.GetDirectoryName(tempOutput)!);
            File.WriteAllText(tempOutput, "fake-output");

            output?.Report(new ProcessOutputLine(DateTimeOffset.Now, false, "1% Converting input to HTML..."));
            output?.Report(new ProcessOutputLine(DateTimeOffset.Now, false, "Output saved to   " + tempOutput));

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, TimeSpan.FromSeconds(1)));
        }
    }

    private sealed class FakeToolLocator : IToolLocator
    {
        private readonly AppPaths _paths;

        public FakeToolLocator(AppPaths paths) => _paths = paths;

        public string AppRoot => _paths.AppRoot;

        public string ToolsRoot => _paths.ToolsRoot;

        public string DataRoot => _paths.DataRoot;

        public string CalibreConvert => Path.Combine(_paths.ToolsRoot, "calibre", "ebook-convert.exe");

        public string CalibreMeta => CalibreConvert;

        public string CalibrePolish => CalibreConvert;

        public string CalibreCustomize => CalibreConvert;

        public string? Java => null;

        public string? EpubCheckJar => null;

        public string? WebView2FixedFolder => null;

        public string? PdfiumDll => null;

        public Task<ToolHealthReport> VerifyAllAsync(bool runProbes, CancellationToken ct = default)
            => Task.FromResult(new ToolHealthReport(Array.Empty<ToolHealthItem>()));

        public IReadOnlyDictionary<string, string?> BuildEnvironment(string toolKey) => new Dictionary<string, string?>
        {
            ["CALIBRE_CONFIG_DIRECTORY"] = _paths.CalibreConfigDir,
            ["CALIBRE_CACHE_DIRECTORY"] = _paths.CalibreCacheDir,
            ["CALIBRE_TEMP_DIR"] = Path.Combine(_paths.TempDir, "Calibre"),
            ["CALIBRE_OVERRIDE_LANG"] = "en",
            ["PYTHONIOENCODING"] = "utf-8",
        };
    }

    private sealed class FakeConfigStore : IConfigStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public System.Text.Json.Nodes.JsonNode? Get(string dottedPath) => null;

        public string? GetString(string dottedPath, string? fallback = null)
            => dottedPath switch
            {
                "conversion.defaultOutputProfile" => "default",
                "conversion.defaultTemplates.MOBI" => "kindle-best",
                "conversion.defaultTemplates.PDF" => "a4-print",
                "conversion.defaultTemplates.TXT" => "plain-utf8",
                "conversion.defaultTemplates.EPUB" => "mobile-readable",
                _ => fallback,
            };

        public bool GetBool(string dottedPath, bool fallback) => fallback;

        public double GetDouble(string dottedPath, double fallback)
            => dottedPath == "conversion.timeoutMinutes" ? 5d : fallback;

        public Task SetAsync(string dottedPath, System.Text.Json.Nodes.JsonNode? value, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
