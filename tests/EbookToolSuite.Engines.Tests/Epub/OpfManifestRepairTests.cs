using System.IO.Compression;
using System.Text;
using EbookToolSuite.Engines.Epub;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>
/// 补 manifest（T3-5 的"一键修复"）。行为依据是真工具实测（见 OpfManifestRepair 的类注释）：
/// 被引用但未声明的资源会被 EpubCheck 报 <c>RSC-008</c>，补上 manifest 后必须变干净。
/// </summary>
public sealed class OpfManifestRepairTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ets-repair-" + Guid.NewGuid().ToString("N"));

    public OpfManifestRepairTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // 临时目录删不掉不影响结论
        }
    }

    [Fact]
    public void 被引用但未声明的图片_能找出来也能补进去()
    {
        var (workDir, opf) = MakeWorkspace(withReferencedImage: true);

        var missing = OpfManifestRepair.FindUnmanifested(opf, workDir);
        Assert.Contains("Images/ref.jpg", missing, StringComparer.OrdinalIgnoreCase);

        var result = OpfManifestRepair.Repair(opf, workDir);

        Assert.Equal(1, result.AddedCount);
        Assert.Equal("Images/ref.jpg", result.AddedHrefs[0]);

        // 补完之后 OPF 里真的能读回来，而且介质类型是对的
        var editor = new OpfMetadataEditor(opf);
        editor.Read();
        var item = editor.ReadManifest().Single(m => m.Href == "Images/ref.jpg");
        Assert.Equal("image/jpeg", item.MediaType);

        // 幂等：再补一次没有新增
        Assert.Equal(0, OpfManifestRepair.Repair(opf, workDir).AddedCount);
        Assert.DoesNotContain(
            OpfManifestRepair.FindUnmanifested(opf, workDir),
            h => h.Contains("ref.jpg", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 不该进manifest的东西一个都不碰()
    {
        var (workDir, opf) = MakeWorkspace(withReferencedImage: true);

        // 塞一堆"不该进 manifest"的东西：打包器生成物 + META-INF + OPF 自己
        File.WriteAllText(Path.Combine(workDir, ".session.json"), "{}", new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(workDir, ".snapshots"));
        File.WriteAllText(Path.Combine(workDir, ".snapshots", "s1.epub"), "x", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(workDir, "META-INF", "extra.xml"), "<x/>", new UTF8Encoding(false));

        var result = OpfManifestRepair.Repair(opf, workDir);

        Assert.DoesNotContain(result.AddedHrefs, h => h.Contains("mimetype", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.AddedHrefs, h => h.Contains(".session.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.AddedHrefs, h => h.Contains(".snapshots", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.AddedHrefs, h => h.Contains("META-INF", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.AddedHrefs, h => h.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 认不出介质类型的文件_跳过并报告()
    {
        var (workDir, opf) = MakeWorkspace(withReferencedImage: false);
        File.WriteAllText(Path.Combine(workDir, "OEBPS", "mystery.bin"), "?", new UTF8Encoding(false));

        var result = OpfManifestRepair.Repair(opf, workDir);

        Assert.Contains(result.SkippedFiles, s => s.Contains("mystery.bin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.AddedHrefs, h => h.Contains("mystery.bin", StringComparison.OrdinalIgnoreCase));
        // FindUnmanifested 也不该把它报成"该补的"
        Assert.DoesNotContain(OpfManifestRepair.FindUnmanifested(opf, workDir),
            h => h.Contains("mystery.bin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 干净的包_没有可补的东西()
    {
        var (workDir, opf) = MakeWorkspace(withReferencedImage: false);

        Assert.Empty(OpfManifestRepair.FindUnmanifested(opf, workDir));
        Assert.Equal(0, OpfManifestRepair.Repair(opf, workDir).AddedCount);
    }

    [Fact]
    public void 介质类型表_常见扩展名都认()
    {
        Assert.Equal("application/xhtml+xml", OpfManifestRepair.MediaTypeFor(".xhtml"));
        Assert.Equal("text/css", OpfManifestRepair.MediaTypeFor(".CSS"));
        Assert.Equal("image/png", OpfManifestRepair.MediaTypeFor(".png"));
        Assert.Equal("font/woff2", OpfManifestRepair.MediaTypeFor(".woff2"));
        Assert.Null(OpfManifestRepair.MediaTypeFor(".bin"));
        Assert.Null(OpfManifestRepair.MediaTypeFor(null));
    }

    /// <summary>
    /// 造一个真实工作区（把 EpubFixture 的最小 EPUB 解开）。
    /// withReferencedImage = true 时：包里有一张被 ch1 引用、但 manifest 没声明的图（= RSC-008 的场景）。
    /// </summary>
    private (string WorkDir, string Opf) MakeWorkspace(bool withReferencedImage)
    {
        var epub = Path.Combine(_root, (withReferencedImage ? "referenced" : "clean") + ".epub");
        var binaryEntries = withReferencedImage
            ? new[] { ("OEBPS/Images/ref.jpg", EpubFixture.MakeJpeg(8, 8)) }
            : Array.Empty<(string, byte[])>();

        EpubFixture.Write(epub, EpubFixture.Epub3Opf, true, binaryEntries);

        var workDir = Path.Combine(_root, (withReferencedImage ? "referenced-ws" : "clean-ws"));
        if (Directory.Exists(workDir))
        {
            Directory.Delete(workDir, recursive: true);
        }

        ZipFile.ExtractToDirectory(epub, workDir);

        if (withReferencedImage)
        {
            // 让 ch1 引用那张图（图在包里、manifest 没声明 -> EpubCheck 报 RSC-008）
            var chapter = Path.Combine(workDir, "OEBPS", "ch1.xhtml");
            var text = File.ReadAllText(chapter, Encoding.UTF8);
            File.WriteAllText(
                chapter,
                text.Replace("</body>", "<p><img src=\"Images/ref.jpg\" alt=\"x\"/></p></body>", StringComparison.Ordinal),
                new UTF8Encoding(false));
        }

        return (workDir, Path.Combine(workDir, "OEBPS", "content.opf"));
    }
    [Fact]
    public void 声明了但包里没有的文件_能找出来也能删掉()
    {
        var workDir = Path.Combine(_root, "dangling");
        var opf = MakeManualWorkspace(workDir, declaredMissing: true);

        var dangling = OpfManifestRepair.FindDangling(opf, workDir);
        Assert.Equal(new[] { "missing.xhtml" }, dangling.ToArray());

        var removed = OpfManifestRepair.RemoveDangling(opf, workDir);
        Assert.Equal(new[] { "missing.xhtml" }, removed.ToArray());

        var written = File.ReadAllText(opf, Encoding.UTF8);
        Assert.DoesNotContain("missing.xhtml", written, StringComparison.Ordinal);
        // spine 里指向它的 itemref 也要一起删：留着就是"指向不存在的 idref"的新错误
        Assert.DoesNotContain("\"gone\"", written, StringComparison.Ordinal);

        var editor = new OpfMetadataEditor(opf);
        editor.Read();
        Assert.DoesNotContain(
            editor.ReadManifest(),
            m => string.Equals(m.Href, "missing.xhtml", StringComparison.OrdinalIgnoreCase));
        // 没被删的那条还在
        Assert.Contains(editor.ReadManifest(), m => m.Href == "ch1.xhtml");

        // 幂等：再跑一次什么都没有
        Assert.Empty(OpfManifestRepair.RemoveDangling(opf, workDir));
    }

    [Fact]
    public void 文件都在的包_一个声明都不删()
    {
        var workDir = Path.Combine(_root, "complete");
        var opf = MakeManualWorkspace(workDir, declaredMissing: false);

        Assert.Empty(OpfManifestRepair.FindDangling(opf, workDir));
        Assert.Empty(OpfManifestRepair.RemoveDangling(opf, workDir));

        var editor = new OpfMetadataEditor(opf);
        editor.Read();
        Assert.Single(editor.ReadManifest());
        Assert.Contains(editor.ReadManifest(), m => m.Href == "ch1.xhtml");
    }

    /// <summary>
    /// 手搓一个最小工作区。<paramref name="declaredMissing"/> = true 时 manifest 里多一条
    /// "声明了但文件不存在"的 item（对应 EpubCheck 的 RSC-001），spine 里也有对应的 itemref。
    /// </summary>
    private static string MakeManualWorkspace(string workDir, bool declaredMissing)
    {
        var oebps = Path.Combine(workDir, "OEBPS");
        Directory.CreateDirectory(oebps);

        var opf = Path.Combine(oebps, "content.opf");
        var missingItem = declaredMissing
            ? "    <item id=\"gone\" href=\"missing.xhtml\" media-type=\"application/xhtml+xml\"/>\n"
            : string.Empty;
        var missingRef = declaredMissing ? "    <itemref idref=\"gone\"/>\n" : string.Empty;

        File.WriteAllText(
            opf,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\" unique-identifier=\"id\">\n"
            + "  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n"
            + "    <dc:title>测试</dc:title>\n"
            + "  </metadata>\n"
            + "  <manifest>\n"
            + "    <item id=\"c1\" href=\"ch1.xhtml\" media-type=\"application/xhtml+xml\"/>\n"
            + missingItem
            + "  </manifest>\n"
            + "  <spine>\n"
            + "    <itemref idref=\"c1\"/>\n"
            + missingRef
            + "  </spine>\n"
            + "</package>\n",
            new UTF8Encoding(false));

        File.WriteAllText(
            Path.Combine(oebps, "ch1.xhtml"),
            "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>x</p></body></html>",
            new UTF8Encoding(false));

        return opf;
    }

}
