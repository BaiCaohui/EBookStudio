using EbookToolSuite.Engines.Epub;
using EbookToolSuite.Engines.Images;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Infrastructure.Data;
using EbookToolSuite.Infrastructure.Data.Repositories;
using EbookToolSuite.Infrastructure.Files;
using EbookToolSuite.Services.Comics;
using EbookToolSuite.Services.Library;
using EbookToolSuite.Services.Pdf;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests;

/// <summary>
/// 用完即弃的数据目录 + 真实 SQLite + 真实引擎。
/// 导入这条链路的价值就在于「真的复制了文件、真的写了库」，
/// mock 掉任何一段都会让测试失去意义。
/// </summary>
internal sealed class ImportSandbox : IAsyncDisposable
{
    public ImportSandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "ets-import-" + Guid.NewGuid().ToString("N"));
        SourceDir = Path.Combine(Root, "source");

        var appRoot = Path.Combine(Root, "app");
        var userLocal = Path.Combine(Root, "localappdata", "EbookToolSuite");
        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(userLocal);
        Directory.CreateDirectory(SourceDir);

        Paths = AppPaths.Create(new AppPathsOptions
        {
            AppRoot = appRoot,
            UserLocalRoot = userLocal,
            Fallback = DataDirFallback.Throw,
        });
        Paths.EnsureCreated();

        Factory = new SqliteConnectionFactory(Paths);
        Database = new Database(
            Factory,
            new SchemaMigrator(NullLogger<SchemaMigrator>.Instance),
            NullLogger<Database>.Instance);

        Repository = new BookRepository(Factory);
        DuplicateRepository = new DuplicateRepository(Factory);
        Probe = new EpubProbe();

        Pipeline = new ImportPipeline(
            Paths,
            Probe,
            new ImageProcessor(),
            new HashUtil(),
            Repository,
            DuplicateRepository,
            SystemClock.Instance,
            NullLogger<ImportPipeline>.Instance,
            // T6-2：PDF 封面＝pdfium 渲的首页。这里用真实现（原生库随包复制，测试机上可用）。
            new PdfDocumentService(new PdfiumLibrary()),
            // T6-3：CBZ/CBR 的探测与首页封面。真实现（SharpCompress 纯托管，无原生依赖）。
            new ComicBookService());
    }

    public string Root { get; }

    public string SourceDir { get; }

    public AppPaths Paths { get; }

    public SqliteConnectionFactory Factory { get; }

    public Database Database { get; }

    public BookRepository Repository { get; }
    public DuplicateRepository DuplicateRepository { get; }

    public EpubProbe Probe { get; }

    public ImportPipeline Pipeline { get; }

    public async Task InitializeAsync() => await Database.InitializeAsync();

    /// <summary>在源目录里造一本 EPUB，返回它的绝对路径。</summary>
    public string WriteEpub(EpubBuilder builder, string fileName)
        => builder.WriteTo(Path.Combine(SourceDir, fileName));

    public string LibraryFileCount()
        => Directory.Exists(Paths.LibraryDir)
            ? Directory.GetFiles(Paths.LibraryDir, "*", SearchOption.AllDirectories).Length.ToString()
            : "0";

    public string[] LibraryFiles()
        => Directory.Exists(Paths.LibraryDir)
            ? Directory.GetFiles(Paths.LibraryDir, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();

    public string[] CoverFiles()
        => Directory.Exists(Paths.CoversCacheDir)
            ? Directory.GetFiles(Paths.CoversCacheDir, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();

    public string[] StagingLeftovers()
        => Directory.Exists(Paths.TempDir)
            ? Directory.GetFiles(Paths.TempDir, "import-*", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();

    public async Task<int> BookCountAsync() => await Repository.CountAsync();

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}