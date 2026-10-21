using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EbookToolSuite.Engines.Epub;
using EbookToolSuite.Engines.Images;
using SixLabors.ImageSharp;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>
/// T3-3 判据 1 与判据 2 的真工具验证：换完封面 -> 换回 EPUB -> 真 EpubCheck 判"零 error"、
/// 真 Calibre 能把新封面取出来。
///
/// 默认跳过（要 spawn JRE 与 Calibre，一次好几秒）。需要时这样跑：
///   $env:ETS_RUN_INTEGRATION='1'; dotnet test --filter CoverIntegration
///
/// 为什么要跟"基线"比而不是直接要求全零：EpubCheck 对最小样本本来就可能报 warning，
/// 判据要的是"换封面这件事**没有**引入新问题"。
/// </summary>
public sealed class CoverIntegrationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ets-cover-int-" + Guid.NewGuid().ToString("N"));

    private readonly CoverService _service = new(new ImageProcessor());

    public CoverIntegrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("ETS_RUN_INTEGRATION") == "1";

    // ================= 判据 2：EpubCheck 零 error =================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 判据2_换过封面之后EpubCheck不新增fatal与error(bool epub3)
    {
        if (!Enabled)
        {
            return;
        }

        var label = epub3 ? "EPUB3" : "EPUB2";
        var (source, workspace, _) = BuildWorkspaceWithCover(epub3);

        var baseline = Check(Repack(source, Path.Combine(_root, label + "-base.epub")), label + " 基线");
        Assert.True(baseline.Fatal == 0 && baseline.Error == 0,
            label + " 基线样本自己就不干净，先修样本：" + baseline.Details);

        // PNG -> JPEG：扩展名、media-type、引用三处都要跟着动，最容易违反 OPF-029
        var result = await _service.ReplaceCoverAsync(workspace, EpubFixture.MakeJpeg(1200, 1800));
        Assert.True(result.Renamed, label + " 应该走改名路径");

        var edited = Check(Repack(workspace, Path.Combine(_root, label + "-edited.epub")), label + " 换过封面");
        Assert.True(edited.Fatal == 0 && edited.Error == 0,
            label + " 换封面后没通过 EpubCheck：fatal=" + edited.Fatal + " error=" + edited.Error
            + Environment.NewLine + edited.Details);
        Assert.True(edited.Warning <= baseline.Warning,
            label + " 换封面引入了新警告：" + baseline.Warning + " -> " + edited.Warning
            + Environment.NewLine + edited.Details);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 判据2_本来没有封面的书新建封面后EpubCheck也零error(bool epub3)
    {
        if (!Enabled)
        {
            return;
        }

        var label = epub3 ? "EPUB3" : "EPUB2";
        var opf = epub3 ? Opf3(string.Empty) : Opf2(string.Empty);
        var source = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".epub");
        EpubFixture.Write(source, opf, epub3);

        var baseline = Check(source, label + " 无封面基线");
        Assert.True(baseline.Fatal == 0 && baseline.Error == 0,
            label + " 基线样本自己就不干净：" + baseline.Details);

        var workspace = Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(source, workspace);

        var result = await _service.ReplaceCoverAsync(workspace, EpubFixture.MakeJpeg(1200, 1800));
        Assert.True(result.Created, label + " 应该走新建路径");

        var edited = Check(Repack(workspace, Path.Combine(_root, label + "-created.epub")), label + " 新建封面");
        Assert.True(edited.Fatal == 0 && edited.Error == 0,
            label + " 新建封面后没通过 EpubCheck：fatal=" + edited.Fatal + " error=" + edited.Error
            + Environment.NewLine + edited.Details);
        Assert.True(edited.Warning <= baseline.Warning,
            label + " 新建封面引入了新警告：" + baseline.Warning + " -> " + edited.Warning
            + Environment.NewLine + edited.Details);
    }

    // ================= 判据 1：Calibre 里封面真的变了 =================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 判据1_Calibre能从换过封面的包里取出那张新JPEG(bool epub3)
    {
        if (!Enabled)
        {
            return;
        }

        var label = epub3 ? "EPUB3" : "EPUB2";
        var (_, workspace, _) = BuildWorkspaceWithCover(epub3);

        var result = await _service.ReplaceCoverAsync(workspace, EpubFixture.MakeJpeg(1200, 1800));
        var epubPath = Repack(workspace, Path.Combine(_root, label + "-calibre.epub"));

        var root = FindRepoRoot();
        Assert.NotNull(root);
        var calibre = Path.Combine(root!, "Tools", "Calibre Portable", "Calibre", "ebook-meta.exe");
        Assert.True(File.Exists(calibre), "找不到 Calibre：" + calibre);

        var coverOut = Path.Combine(_root, label + "-cover.jpg");
        var (exitCode, stdout, stderr) = RunProcess(
            calibre, "\"" + epubPath + "\" --get-cover \"" + coverOut + "\"");

        Assert.True(exitCode == 0, "ebook-meta --get-cover 退出码 " + exitCode
            + Environment.NewLine + stdout + Environment.NewLine + stderr);
        Assert.True(File.Exists(coverOut),
            "Calibre 没取到封面（说明包里的封面声明它不认）：" + stdout + stderr);

        // 取出来的必须就是我们写进去的那张：字节一致 + 尺寸一致
        var extracted = File.ReadAllBytes(coverOut);
        Assert.Equal(result.ByteCount, extracted.Length);
        Assert.True(extracted.SequenceEqual(File.ReadAllBytes(result.AbsolutePath)),
            label + " Calibre 取出来的封面与写进包的字节不同");

        using var image = Image.Load(coverOut);
        Assert.Equal(result.Width, image.Width);
        Assert.Equal(result.Height, image.Height);

        // 顺带确认 Calibre 也认得书名（包没被写坏）
        var (metaExit, metaOut, _) = RunProcess(calibre, "\"" + epubPath + "\"");
        Assert.True(metaExit == 0, "ebook-meta 退出码 " + metaExit);
        Assert.Contains("原始书名", metaOut);
    }

    // ================= 样本 =================

    private (string Source, string Workspace, string CoverHref) BuildWorkspaceWithCover(bool epub3)
    {
        var coverItem = "<item id=\"cover-img\" href=\"Images/cover.png\" media-type=\"image/png\""
            + (epub3 ? " properties=\"cover-image\"" : string.Empty) + "/>";

        var opf = epub3 ? Opf3(coverItem) : Opf2(coverItem);
        var source = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".epub");
        EpubFixture.Write(source, opf, epub3, new[] { ("OEBPS/Images/cover.png", EpubFixture.MakePng(600, 900)) });

        var workspace = Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(source, workspace);
        return (source, workspace, "Images/cover.png");
    }

    private static string Opf2(string coverItem)
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\" unique-identifier=\"bookid\">\n"
            + "  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:opf=\"http://www.idpf.org/2007/opf\">\n"
            + "    <dc:identifier id=\"bookid\">urn:uuid:11111111-2222-3333-4444-555555555555</dc:identifier>\n"
            + "    <dc:title>原始书名</dc:title>\n"
            + "    <dc:language>zh-CN</dc:language>\n"
            + "    <meta name=\"cover\" content=\"cover-img\"/>\n"
            + "  </metadata>\n"
            + "  <manifest>\n"
            + "    <item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>\n"
            + "    <item id=\"ch1\" href=\"ch1.xhtml\" media-type=\"application/xhtml+xml\"/>\n"
            + coverItem + "\n"
            + "  </manifest>\n"
            + "  <spine toc=\"ncx\">\n"
            + "    <itemref idref=\"ch1\"/>\n"
            + "  </spine>\n"
            + "</package>\n";

    private static string Opf3(string coverItem)
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\">\n"
            + "  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n"
            + "    <dc:identifier id=\"bookid\">urn:uuid:11111111-2222-3333-4444-555555555555</dc:identifier>\n"
            + "    <dc:title>原始书名</dc:title>\n"
            + "    <dc:language>zh-CN</dc:language>\n"
            + "    <meta property=\"dcterms:modified\">2026-03-04T05:06:07Z</meta>\n"
            + "    <meta name=\"cover\" content=\"cover-img\"/>\n"
            + "  </metadata>\n"
            + "  <manifest>\n"
            + "    <item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>\n"
            + "    <item id=\"ch1\" href=\"ch1.xhtml\" media-type=\"application/xhtml+xml\"/>\n"
            + coverItem + "\n"
            + "  </manifest>\n"
            + "  <spine>\n"
            + "    <itemref idref=\"ch1\"/>\n"
            + "  </spine>\n"
            + "</package>\n";

    /// <summary>
    /// 把工作区目录重新打成 EPUB（mimetype 必须是第一条且不压缩，EpubCheck 会查这条）。
    /// 真正的打包器是 T3-4，这里只要够 EpubCheck/Calibre 用就行。
    /// </summary>
    private static string Repack(string sourceOrDirectory, string outputPath)
    {
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        // 传进来是 .epub 就原样拷一份；是目录就打包
        if (File.Exists(sourceOrDirectory))
        {
            File.Copy(sourceOrDirectory, outputPath, overwrite: true);
            return outputPath;
        }

        using var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create);

        var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var stream = mime.Open())
        {
            var bytes = Encoding.ASCII.GetBytes("application/epub+zip");
            stream.Write(bytes, 0, bytes.Length);
        }

        foreach (var file in Directory.EnumerateFiles(sourceOrDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(x => x, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(sourceOrDirectory, file).Replace('\\', '/');
            if (relative == "mimetype")
            {
                continue;
            }

            zip.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
        }

        return outputPath;
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

        var jsonPath = Path.Combine(Path.GetTempPath(), "ets-covercheck-" + Guid.NewGuid().ToString("N") + ".json");
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
            {
                File.Delete(jsonPath);
            }
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
}