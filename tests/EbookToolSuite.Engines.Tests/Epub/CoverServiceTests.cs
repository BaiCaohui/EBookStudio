using System.IO.Compression;
using System.Text;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Engines.Epub;
using EbookToolSuite.Engines.Images;
using SixLabors.ImageSharp;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>
/// T3-3 封面替换的单元测试。工作区是"把 EpubFixture 造的 EPUB 解压到临时目录"，
/// 全部真实文件系统、不 mock（笔者的测试约定）。
///
/// 判据 1（Calibre 里封面变了）与判据 2（EpubCheck 零 error）靠真工具，
/// 在 CoverIntegrationTests 里（ETS_RUN_INTEGRATION=1 才跑）。
/// </summary>
public sealed class CoverServiceTests : IDisposable
{
    private const string OpfPath = "OEBPS/content.opf";
    private const string CoverJpg = "OEBPS/Images/cover.jpg";
    private const string CoverPng = "OEBPS/Images/cover.png";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ets-cover-" + Guid.NewGuid().ToString("N"));

    private readonly CoverService _service = new(new ImageProcessor());

    public CoverServiceTests() => Directory.CreateDirectory(_root);

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

    // ================= 判据 1：换封面 + 保存 =================

    [Fact]
    public async Task 换封面_原封面是JPEG时原地覆盖_不改文件名也不改引用()
    {
        var opf = MakeOpf2(Item("cover-img", "Images/cover.jpg", "image/jpeg"), Meta("cover-img"));
        var workspace = Workspace(opf, epub3: false, (CoverJpg, EpubFixture.MakeJpeg(600, 900)));

        var coverPath = Path.Combine(workspace, "OEBPS", "Images", "cover.jpg");
        var before = File.ReadAllBytes(coverPath);
        var snapshot = Snapshot(workspace);

        var result = await _service.ReplaceCoverAsync(workspace, EpubFixture.MakeJpeg(1200, 800, 90));

        Assert.False(result.Created);
        Assert.False(result.Renamed);
        Assert.Equal("Images/cover.jpg", result.Href);
        Assert.Equal("cover-img", result.ItemId);
        Assert.Equal(coverPath, result.AbsolutePath);
        Assert.Equal(result.ByteCount, new FileInfo(coverPath).Length);

        // 封面确实换了
        Assert.False(File.ReadAllBytes(coverPath).SequenceEqual(before));

        var opfText = File.ReadAllText(Path.Combine(workspace, "OEBPS", "content.opf"));
        Assert.Equal(3, CountItems(opfText));                 // ncx + ch1 + cover-img，没有多出一条
        Assert.Contains("<item id=\"cover-img\"", opfText);   // id 没变
        Assert.Contains("Images/cover.jpg", opfText);
        Assert.Contains("image/jpeg", opfText);
        Assert.DoesNotContain("properties=", opfText);        // EPUB2 不许有 properties
        Assert.Contains("name=\"cover\"", opfText);

        // 只该动封面与 OPF 两个文件
        var changed = ChangedFiles(snapshot, Snapshot(workspace));
        Assert.Equal(2, changed.Count);
        Assert.Contains("OEBPS/Images/cover.jpg", changed);
        Assert.Contains("OEBPS/content.opf", changed);

        // 不留临时文件
        Assert.Empty(Directory.GetFiles(workspace, "*.ets-tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(workspace, "*.ets-bak", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task 换封面_原封面不是JPEG时改名为jpg_manifest与引用同步更新()
    {
        var opf = MakeOpf2(Item("cover-img", "Images/cover.png", "image/png"), Meta("cover-img"));
        var workspace = Workspace(opf, epub3: false, (CoverPng, EpubFixture.MakePng(600, 900)));

        // 两处引用：同目录 + 上一层目录（相对路径要分别算）
        Write(Path.Combine(workspace, "OEBPS", "ch1.xhtml"),
            "<html><body><img src=\"Images/cover.png\"/></body></html>");
        Directory.CreateDirectory(Path.Combine(workspace, "OEBPS", "Text"));
        Write(Path.Combine(workspace, "OEBPS", "Text", "deep.xhtml"),
            "<html><body><img src=\"../Images/cover.png\"/></body></html>");

        var result = await _service.ReplaceCoverAsync(workspace, EpubFixture.MakeJpeg(900, 1400));

        Assert.False(result.Created);
        Assert.True(result.Renamed);
        Assert.Equal("cover-img", result.ItemId);
        Assert.Equal("Images/cover.png", result.PreviousHref);
        Assert.Equal("Images/cover.jpg", result.Href);

        Assert.True(File.Exists(Path.Combine(workspace, "OEBPS", "Images", "cover.jpg")));
        Assert.False(File.Exists(Path.Combine(workspace, "OEBPS", "Images", "cover.png")));  // 没人引用了才删

        var ch1 = File.ReadAllText(Path.Combine(workspace, "OEBPS", "ch1.xhtml"));
        Assert.Contains("Images/cover.jpg", ch1);
        Assert.DoesNotContain("cover.png", ch1);

        var deep = File.ReadAllText(Path.Combine(workspace, "OEBPS", "Text", "deep.xhtml"));
        Assert.Contains("../Images/cover.jpg", deep);

        // media-type 必须跟着扩展名改：EpubCheck 的 OPF-029 就是查这条
        var opfText = File.ReadAllText(Path.Combine(workspace, "OEBPS", "content.opf"));
        Assert.Contains("Images/cover.jpg", opfText);
        Assert.Contains("image/jpeg", opfText);
        Assert.DoesNotContain("cover.png", opfText);
        Assert.DoesNotContain("properties=", opfText);
        Assert.Equal(3, CountItems(opfText));
    }

    [Fact]
    public async Task 换封面_相似的别的文件名不被误改_旧图保守保留()
    {
        var opf = MakeOpf2(Item("cover-img", "Images/cover.png", "image/png"), Meta("cover-img"));
        var workspace = Workspace(opf, epub3: false, (CoverPng, EpubFixture.MakePng(600, 900)));

        // xImages/cover.png 是**另一个文件**，不能被顺手改成 xImages/cover.jpg
        Write(Path.Combine(workspace, "OEBPS", "ch1.xhtml"),
            "<html><body><img src=\"xImages/cover.png\"/></body></html>");

        await _service.ReplaceCoverAsync(workspace, EpubFixture.MakeJpeg(900, 1400));

        var ch1 = File.ReadAllText(Path.Combine(workspace, "OEBPS", "ch1.xhtml"));
        Assert.Contains("xImages/cover.png", ch1);
        Assert.DoesNotContain("xImages/cover.jpg", ch1);

        // 旧图名字还出现在工作区别处时，宁可不删
        Assert.True(File.Exists(Path.Combine(workspace, "OEBPS", "Images", "cover.png")));
    }

    // ================= 判据 5：本来没有封面要能新建 =================

    [Fact]
    public async Task 换封面_EPUB3本来没有封面时新建Images_cover_jpg并写进manifest()
    {
        var workspace = Workspace(EpubFixture.Epub3Opf, epub3: true);

        var result = await _service.ReplaceCoverAsync(workspace, EpubFixture.MakeJpeg(1000, 1500));

        Assert.True(result.Created);
        Assert.False(result.Renamed);
        Assert.Equal("cover-img", result.ItemId);
        Assert.Equal("Images/cover.jpg", result.Href);
        Assert.Null(result.PreviousHref);
        Assert.True(File.Exists(Path.Combine(workspace, "OEBPS", "Images", "cover.jpg")));

        var opfText = File.ReadAllText(Path.Combine(workspace, "OEBPS", "content.opf"));
        Assert.Contains("id=\"cover-img\"", opfText);
        Assert.Contains("Images/cover.jpg", opfText);
        Assert.Contains("image/jpeg", opfText);
        Assert.Contains("cover-image", opfText);                       // EPUB3 的封面口径
        Assert.Matches("name=\"cover\"\\s+content=\"cover-img\"", opfText);
        Assert.Equal(3, CountItems(opfText));                          // nav + ch1 + 新封面
    }

    [Fact]
    public async Task 换封面_EPUB2新建封面时不许出现properties属性()
    {
        var workspace = Workspace(EpubFixture.Epub2Opf, epub3: false);

        var result = await _service.ReplaceCoverAsync(workspace, EpubFixture.MakeJpeg(1000, 1500));

        Assert.True(result.Created);
        Assert.Equal("Images/cover.jpg", result.Href);

        var opfText = File.ReadAllText(Path.Combine(workspace, "OEBPS", "content.opf"));
        Assert.Contains("id=\"cover-img\"", opfText);
        Assert.Contains("Images/cover.jpg", opfText);
        Assert.Contains("name=\"cover\"", opfText);
        Assert.DoesNotContain("properties", opfText);                  // OPF 2.0 里没有这个属性
    }

    [Fact]
    public async Task 换封面_EPUB3已有的cover_image属性在改名后仍然保留()
    {
        var opf = MakeOpf3(
            Item("cover-img", "Images/cover.png", "image/png", "cover-image"),
            Meta("cover-img"));
        var workspace = Workspace(opf, epub3: true, (CoverPng, EpubFixture.MakePng(600, 900)));

        var result = await _service.ReplaceCoverAsync(workspace, EpubFixture.MakeJpeg(900, 1400));

        Assert.True(result.Renamed);

        var opfText = File.ReadAllText(Path.Combine(workspace, "OEBPS", "content.opf"));
        Assert.Contains("id=\"cover-img\"", opfText);
        Assert.Contains("href=\"Images/cover.jpg\"", opfText);
        Assert.Contains("media-type=\"image/jpeg\"", opfText);
        Assert.Contains("properties=\"cover-image\"", opfText);
    }

    [Fact]
    public async Task 换封面_目标文件名被占用时依次加序号()
    {
        // Images/cover.jpg 是个没人声明的散文件 -> 不能覆盖它，改叫 cover-2.jpg
        var workspace = Workspace(EpubFixture.Epub3Opf, epub3: true, (CoverJpg, EpubFixture.MakeJpeg(10, 10)));
        var stray = File.ReadAllBytes(Path.Combine(workspace, "OEBPS", "Images", "cover.jpg"));

        var result = await _service.ReplaceCoverAsync(workspace, EpubFixture.MakeJpeg(1000, 1500));

        Assert.True(result.Created);
        Assert.Equal("Images/cover-2.jpg", result.Href);
        Assert.True(File.Exists(Path.Combine(workspace, "OEBPS", "Images", "cover-2.jpg")));
        Assert.True(File.ReadAllBytes(Path.Combine(workspace, "OEBPS", "Images", "cover.jpg")).SequenceEqual(stray));
    }

    // ================= 判据 3：大图规范化 =================

    [Fact]
    public async Task 换封面_八千乘六千的PNG被规范化成不超过1600px且不超过500KB的JPEG()
    {
        var workspace = Workspace(EpubFixture.Epub3Opf, epub3: true);

        var result = await _service.ReplaceCoverAsync(workspace, EpubFixture.MakePng(8000, 6000));

        Assert.Equal(1600, result.Width);        // 长边缩到 1600
        Assert.Equal(1200, result.Height);       // 短边等比
        Assert.True(result.ByteCount <= CoverService.CoverMaxBytes,
            "封面 " + result.ByteCount + " 字节，超过 500 KB 上限");
        Assert.EndsWith(".jpg", result.AbsolutePath, StringComparison.Ordinal);

        // 独立解码落盘的文件再确认一遍（不是信自己返回的数字）
        using var image = Image.Load(result.AbsolutePath);
        Assert.Equal(1600, image.Width);
        Assert.Equal(1200, image.Height);
        Assert.Equal("JPEG", Image.DetectFormat(result.AbsolutePath).Name);
        Assert.True(new FileInfo(result.AbsolutePath).Length <= CoverService.CoverMaxBytes);
    }

    [Fact]
    public async Task 换封面_小图不会被放大()
    {
        var workspace = Workspace(EpubFixture.Epub3Opf, epub3: true);

        var result = await _service.ReplaceCoverAsync(workspace, EpubFixture.MakeJpeg(400, 600));

        Assert.Equal(400, result.Width);
        Assert.Equal(600, result.Height);
    }

    // ================= 判据 6：失败不能破坏别的东西 =================

    [Fact]
    public async Task 换封面_图片读不出来时抛UnsupportedFormat且工作区一个字节不变()
    {
        var opf = MakeOpf2(Item("cover-img", "Images/cover.jpg", "image/jpeg"), Meta("cover-img"));
        var workspace = Workspace(opf, epub3: false, (CoverJpg, EpubFixture.MakeJpeg(600, 900)));

        var before = Snapshot(workspace);

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            _service.ReplaceCoverAsync(workspace, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 }));

        Assert.Equal(ErrorCode.UnsupportedFormat, exception.Code);

        var after = Snapshot(workspace);
        Assert.Equal(before.Count, after.Count);
        foreach (var (path, bytes) in before)
        {
            Assert.True(after.ContainsKey(path), path + " 不见了");
            Assert.True(bytes.SequenceEqual(after[path]), path + " 被改了");
        }
    }

    [Fact]
    public async Task 换封面_空图片字节被拒绝()
    {
        var workspace = Workspace(EpubFixture.Epub3Opf, epub3: true);

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            _service.ReplaceCoverAsync(workspace, Array.Empty<byte>()));

        Assert.Equal(ErrorCode.UnsupportedFormat, exception.Code);
    }

    [Fact]
    public async Task 换封面_工作区不存在时抛FileNotFound()
    {
        var missing = Path.Combine(_root, "没有这个目录");

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            _service.ReplaceCoverAsync(missing, EpubFixture.MakeJpeg(100, 100)));

        Assert.Equal(ErrorCode.FileNotFound, exception.Code);
    }

    // ================= T3-1 同口径：路径越界要挡住 =================

    [Fact]
    public async Task 换封面_container_xml指到工作区外面时抛UnsafeArchiveEntry()
    {
        var workspace = Workspace(EpubFixture.Epub3Opf, epub3: true);
        File.WriteAllText(
            Path.Combine(workspace, "META-INF", "container.xml"),
            "<?xml version=\"1.0\"?>\n"
            + "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">\n"
            + "  <rootfiles>\n"
            + "    <rootfile full-path=\"../../外面.opf\" media-type=\"application/oebps-package+xml\"/>\n"
            + "  </rootfiles>\n"
            + "</container>\n",
            new UTF8Encoding(false));

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            _service.ReplaceCoverAsync(workspace, EpubFixture.MakeJpeg(100, 100)));

        Assert.Equal(ErrorCode.UnsafeArchiveEntry, exception.Code);
    }

    [Fact]
    public async Task 换封面_工作区里没有container_xml时抛EpubInvalid()
    {
        var workspace = Workspace(EpubFixture.Epub3Opf, epub3: true);
        File.Delete(Path.Combine(workspace, "META-INF", "container.xml"));

        var exception = await Assert.ThrowsAsync<AppException>(() =>
            _service.ReplaceCoverAsync(workspace, EpubFixture.MakeJpeg(100, 100)));

        Assert.Equal(ErrorCode.EpubInvalid, exception.Code);
    }

    // ================= 找封面的口径 =================

    [Fact]
    public void 找封面_EPUB3的cover_image属性优先于meta声明()
    {
        var items = new[]
        {
            new EbookToolSuite.Core.Models.OpfManifestItem("pic-a", "Images/a.jpg", "image/jpeg"),
            new EbookToolSuite.Core.Models.OpfManifestItem("pic-b", "Images/b.jpg", "image/jpeg", "cover-image"),
        };

        var picked = CoverService.PickCoverItem(items, "pic-a");

        Assert.NotNull(picked);
        Assert.Equal("pic-b", picked!.Id);
    }

    [Fact]
    public void 找封面_没有cover_image属性时退回meta声明()
    {
        var items = new[]
        {
            new EbookToolSuite.Core.Models.OpfManifestItem("pic-a", "Images/a.jpg", "image/jpeg"),
            new EbookToolSuite.Core.Models.OpfManifestItem("pic-b", "Images/b.jpg", "image/jpeg"),
        };

        var picked = CoverService.PickCoverItem(items, "pic-a");

        Assert.NotNull(picked);
        Assert.Equal("pic-a", picked!.Id);
    }

    [Fact]
    public void 找封面_meta声明的id不存在时当作没有封面()
    {
        var items = new[]
        {
            new EbookToolSuite.Core.Models.OpfManifestItem("pic-a", "Images/a.jpg", "image/jpeg"),
        };

        Assert.Null(CoverService.PickCoverItem(items, "不存在的id"));
    }

    // ================= 样本与工具 =================

    private static string Item(string id, string href, string mediaType, string? properties = null)
        => "    <item id=\"" + id + "\" href=\"" + href + "\" media-type=\"" + mediaType + "\""
            + (properties is null ? string.Empty : " properties=\"" + properties + "\"")
            + "/>\n";

    private static string Meta(string coverId)
        => "    <meta name=\"cover\" content=\"" + coverId + "\"/>\n";

    private static string MakeOpf2(string coverItem, string metaCover)
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\" unique-identifier=\"bookid\">\n"
            + "  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:opf=\"http://www.idpf.org/2007/opf\">\n"
            + "    <dc:identifier id=\"bookid\">urn:uuid:11111111-2222-3333-4444-555555555555</dc:identifier>\n"
            + "    <dc:title>原始书名</dc:title>\n"
            + "    <dc:language>zh-CN</dc:language>\n"
            + metaCover
            + "  </metadata>\n"
            + "  <manifest>\n"
            + "    <item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>\n"
            + "    <item id=\"ch1\" href=\"ch1.xhtml\" media-type=\"application/xhtml+xml\"/>\n"
            + coverItem
            + "  </manifest>\n"
            + "  <spine toc=\"ncx\">\n"
            + "    <itemref idref=\"ch1\"/>\n"
            + "  </spine>\n"
            + "</package>\n";

    private static string MakeOpf3(string coverItem, string metaCover)
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\">\n"
            + "  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n"
            + "    <dc:identifier id=\"bookid\">urn:uuid:11111111-2222-3333-4444-555555555555</dc:identifier>\n"
            + "    <dc:title>原始书名</dc:title>\n"
            + "    <dc:language>zh-CN</dc:language>\n"
            + "    <meta property=\"dcterms:modified\">2026-03-04T05:06:07Z</meta>\n"
            + metaCover
            + "  </metadata>\n"
            + "  <manifest>\n"
            + "    <item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>\n"
            + "    <item id=\"ch1\" href=\"ch1.xhtml\" media-type=\"application/xhtml+xml\"/>\n"
            + coverItem
            + "  </manifest>\n"
            + "  <spine>\n"
            + "    <itemref idref=\"ch1\"/>\n"
            + "  </spine>\n"
            + "</package>\n";

    /// <summary>造一本最小 EPUB 再解压成工作区（解压本身走 ZipFile，不含任何引擎代码）。</summary>
    private string Workspace(
        string opfXml,
        bool epub3,
        params (string Name, byte[] Bytes)[] binaries)
    {
        var epubPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".epub");
        EpubFixture.Write(epubPath, opfXml, epub3, binaries);

        var workspace = Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(epubPath, workspace);
        return workspace;
    }

    private static void Write(string path, string content)
        => File.WriteAllText(path, content, new UTF8Encoding(false));

    private static int CountItems(string opfText)
        => opfText.Split("<item ").Length - 1;

    private static Dictionary<string, byte[]> Snapshot(string root)
        => Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                File.ReadAllBytes);

    private static List<string> ChangedFiles(
        IReadOnlyDictionary<string, byte[]> before,
        IReadOnlyDictionary<string, byte[]> after)
    {
        var changed = before
            .Where(pair => !after.TryGetValue(pair.Key, out var bytes) || !bytes.SequenceEqual(pair.Value))
            .Select(pair => pair.Key)
            .ToList();

        changed.AddRange(after.Keys.Where(key => !before.ContainsKey(key)));
        return changed;
    }
}