using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using EbookToolSuite.Engines.Epub;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>
/// T3-4 打包器的真工具集成测试：真 EpubCheck 5.4.0 + 真 Calibre。
/// 默认跳过，要跑就设环境变量 ETS_RUN_INTEGRATION=1。
/// 覆盖计划判据 1（mimetype 首条/STORED）、判据 2（EpubCheck 零 error）、
/// 判据 3（Calibre 能打开读出书名）、判据 4（只改元数据时其余条目字节不变），
/// 外加真实样本（TestCorpus\）解压后重打包的逐条往返。
/// </summary>
public sealed class EpubPackagerIntegrationTests : IDisposable
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ETS_RUN_INTEGRATION") == "1";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ets-packager-int-" + Guid.NewGuid().ToString("N"));

    private readonly EpubPackager _packager = new();

    public EpubPackagerIntegrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ================= 判据 2：EpubCheck 零 error =================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 打包器产出的书_EpubCheck零错误(bool epub3)
    {
        if (!Enabled)
            return;

        var opf = epub3 ? EpubFixture.Epub3Opf : EpubFixture.Epub2Opf;
        var workspace = Workspace(opf, epub3);
        var output = Path.Combine(_root, (epub3 ? "check3" : "check2") + ".epub");

        await _packager.PackageAsync(workspace, output);

        // 判据 1 先自己过一遍
        EpubPackageVerifier.Verify(output);

        var check = Check(output, (epub3 ? "EPUB3" : "EPUB2") + " 打包结果");
        Assert.True(
            check.Fatal == 0 && check.Error == 0,
            "EpubCheck 不干净：" + check.Details + Environment.NewLine + "（warning=" + check.Warning + "）");
    }

    // ================= 判据 3：Calibre 能打开 =================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 打包器产出的书_Calibre能读出书名(bool epub3)
    {
        if (!Enabled)
            return;

        var opf = epub3 ? EpubFixture.Epub3Opf : EpubFixture.Epub2Opf;
        var workspace = Workspace(opf, epub3);
        var output = Path.Combine(_root, (epub3 ? "cal3" : "cal2") + ".epub");

        await _packager.PackageAsync(workspace, output);

        var title = ReadTitle(Path.Combine(workspace, "OEBPS", "content.opf"));
        Assert.False(string.IsNullOrWhiteSpace(title), "fixture 的 OPF 里应该有 dc:title");

        var ebookMeta = FindTool("ebook-meta.exe");
        var (exitCode, stdout, stderr) = RunProcess(ebookMeta, "\"" + output + "\"");

        Assert.True(exitCode == 0, "Calibre 打不开产出的 EPUB（退出码 " + exitCode + "）：" + stderr);
        Assert.True(
            stdout.Contains(title, StringComparison.Ordinal),
            "Calibre 读出的书名里没有「" + title + "」：" + stdout);
    }

    // ================= 判据 4：只改元数据，其余条目字节不变 =================

    [Fact]
    public async Task 只改元数据重新打包_其余条目字节完全不变()
    {
        if (!Enabled)
            return;

        var workspace = Workspace(EpubFixture.Epub3Opf, epub3: true);
        var first = Path.Combine(_root, "first.epub");
        await _packager.PackageAsync(workspace, first);

        // 模拟 T3-2 改元数据：只动 OPF 里的 dc:title
        var opfPath = Path.Combine(workspace, "OEBPS", "content.opf");
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        var document = XDocument.Load(opfPath);
        document.Descendants(dc + "title").First().Value = "改过书名的测试书";
        document.Save(opfPath);

        var second = Path.Combine(_root, "second.epub");
        await _packager.PackageAsync(workspace, second);

        var before = ReadEntries(first);
        var after = ReadEntries(second);

        Assert.Equal(
            before.Keys.OrderBy(k => k, StringComparer.Ordinal),
            after.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var name in before.Keys)
        {
            if (name == "OEBPS/content.opf")
                continue;

            Assert.Equal(before[name], after[name]);
        }

        // OPF 确实变了（否则这条测试没有意义）
        Assert.NotEqual(before["OEBPS/content.opf"], after["OEBPS/content.opf"]);
    }

    // ================= 真实样本：解压后重打包，逐条往返 =================

    [Fact]
    public async Task 真实样本解压后重打包_EpubCheck零错误且原有条目字节不变()
    {
        if (!Enabled)
            return;

        var root = FindRepoRoot();
        Assert.NotNull(root);

        var corpus = Path.Combine(root!, "TestCorpus");
        if (!Directory.Exists(corpus))
            return;   // 没带样本就跳过

        var books = Directory
            .EnumerateFiles(corpus, "*.epub", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(3)
            .ToList();

        Assert.NotEmpty(books);

        foreach (var book in books)
        {
            var workspace = Path.Combine(_root, "corpus-" + Guid.NewGuid().ToString("N"));
            ZipFile.ExtractToDirectory(book, workspace);

            var output = Path.Combine(_root, "corpus-out-" + Guid.NewGuid().ToString("N") + ".epub");
            await _packager.PackageAsync(workspace, output);

            EpubPackageVerifier.Verify(output);

            // 真实样本本身就可能不干净（比如这本就有 RSC-005），
            // 所以判据是「重打包不能引入新问题」，而不是「必须零 error」。
            var baseline = Check(book, "原始样本：" + Path.GetFileName(book));
            var check = Check(output, "重打包后：" + Path.GetFileName(book));
            Assert.True(
                check.Fatal <= baseline.Fatal && check.Error <= baseline.Error,
                "重打包引入了新问题（原始 "
                    + baseline.Fatal + " fatal / " + baseline.Error + " error ，重打包后 "
                    + check.Fatal + " fatal / " + check.Error + " error）：" + check.Details);

            using var source = ZipFile.OpenRead(book);
            using var packed = ZipFile.OpenRead(output);
            var packedByName = packed.Entries.ToDictionary(
                entry => Normalize(entry.FullName),
                entry => entry,
                StringComparer.Ordinal);

            foreach (var entry in source.Entries)
            {
                var name = Normalize(entry.FullName);
                if (name.Length == 0 || name.EndsWith('/'))
                    continue;   // 目录条目解压后不会存在
                if (name == "mimetype")
                    continue;   // 由打包器唯一重新生成（并会修掉源文件里的 BOM/换行）

                Assert.True(packedByName.ContainsKey(name), "重打包后少了条目：" + name);
                Assert.Equal(ReadEntry(entry), ReadEntry(packedByName[name]));
            }

            // mimetype 一定是精确内容
            using var mimetype = packed.GetEntry("mimetype")!.Open();
            using var buffer = new MemoryStream();
            mimetype.CopyTo(buffer);
            Assert.Equal("application/epub+zip", Encoding.UTF8.GetString(buffer.ToArray()));

            Directory.Delete(workspace, recursive: true);
        }
    }

    // ================= helpers =================

    private string Workspace(string opfXml, bool epub3)
    {
        var epubPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".epub");
        EpubFixture.Write(epubPath, opfXml, epub3);

        var workspace = Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(epubPath, workspace);
        return workspace;
    }

    private static Dictionary<string, byte[]> ReadEntries(string epubPath)
    {
        using var zip = ZipFile.OpenRead(epubPath);
        return zip.Entries.ToDictionary(
            entry => entry.FullName,
            ReadEntry,
            StringComparer.Ordinal);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string Normalize(string name)
    {
        var normalized = name.Replace('\\', '/').TrimStart('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized;
    }

    private static string ReadTitle(string opfPath)
    {
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        var document = XDocument.Load(opfPath);
        return document.Descendants(dc + "title").First().Value.Trim();
    }

    // ================= 真工具 =================

    private sealed record CheckResult(int Fatal, int Error, int Warning, string Details);

    private static CheckResult Check(string epubPath, string what)
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);

        var java = Path.Combine(root!, "Tools", "JRE", "bin", "java.exe");
        var jar = Path.Combine(root!, "Tools", "EpubCheck", "epubcheck.jar");
        Assert.True(File.Exists(java), "找不到 JRE：" + java);
        Assert.True(File.Exists(jar), "找不到 EpubCheck：" + jar);

        var jsonPath = Path.Combine(Path.GetTempPath(), "ets-packager-check-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var (exitCode, _, stderr) = RunProcess(
                java, "-jar \"" + jar + "\" \"" + epubPath + "\" --json \"" + jsonPath + "\" --quiet");

            Assert.True(exitCode is 0 or 1, "EpubCheck 进程本身出问题，退出码 " + exitCode + "：" + stderr);
            Assert.True(File.Exists(jsonPath), "EpubCheck 没有产出 JSON：" + stderr);

            using var document = JsonDocument.Parse(File.ReadAllText(jsonPath, Encoding.UTF8));
            var checker = document.RootElement.GetProperty("checker");
            var fatal = checker.GetProperty("nFatal").GetInt32();
            var error = checker.GetProperty("nError").GetInt32();
            var warning = checker.GetProperty("nWarning").GetInt32();

            var details = document.RootElement.GetProperty("messages").EnumerateArray()
                .Select(m => m.GetProperty("severity").GetString() + " " + m.GetProperty("ID").GetString()
                    + ": " + m.GetProperty("message").GetString())
                .Where(x => !x.StartsWith("INFO", StringComparison.Ordinal))
                .ToList();

            return new CheckResult(fatal, error, warning, what + Environment.NewLine + string.Join(Environment.NewLine, details));
        }
        finally
        {
            if (File.Exists(jsonPath))
                File.Delete(jsonPath);
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcess(string file, string arguments)
    {
        var startInfo = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.Environment["CALIBRE_OVERRIDE_LANG"] = "en";

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(180_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException(file + " 超时未返回");
        }

        return (process.ExitCode, stdout, stderr);
    }

    private static string FindTool(string fileName)
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);

        var direct = new[]
        {
            Path.Combine(root!, "Tools", "Calibre Portable", "Calibre", fileName),
            Path.Combine(root!, "Tools", "Calibre", fileName),
        }.FirstOrDefault(File.Exists);

        if (direct is not null)
            return direct;

        return Directory
            .EnumerateFiles(Path.Combine(root!, "Tools"), fileName, SearchOption.AllDirectories)
            .FirstOrDefault()
            ?? throw new FileNotFoundException("在 Tools 下找不到 " + fileName);
    }

    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tools", "manifest.json")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }
}
