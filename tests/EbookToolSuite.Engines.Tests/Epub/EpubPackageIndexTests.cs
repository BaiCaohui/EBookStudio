using System.IO.Compression;
using System.Text;
using EbookToolSuite.Engines.Epub;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>OPF 索引器：spine 顺序、目录标题、样式清单、Zip Slip 拒收（T1-5）。</summary>
public sealed class EpubPackageIndexTests
{
    private const string Container = """
        <?xml version="1.0"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
        </container>
        """;

    private static ZipArchive BuildZip(params (string Path, string Content)[] files)
    {
        var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                var entry = zip.CreateEntry(path);
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        buffer.Position = 0;
        return new ZipArchive(buffer, ZipArchiveMode.Read);
    }

    private static string Opf(string spineToc, string extraManifest, string extraSpine)
        => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:title>测试书</dc:title>
            <dc:creator>作者甲</dc:creator>
            <dc:language>zh</dc:language>
          </metadata>
          <manifest>
            <item id="ch1" href="Text/ch1.xhtml" media-type="application/xhtml+xml"/>
            <item id="ch2" href="Text/ch2.xhtml" media-type="application/xhtml+xml"/>
            <item id="css" href="Styles/main.css" media-type="text/css"/>
            {extraManifest}
          </manifest>
          <spine{spineToc}>
            <itemref idref="ch1"/>
            <itemref idref="ch2"/>
            {extraSpine}
          </spine>
        </package>
        """;

    [Fact]
    public void 按_spine_顺序返回章节且路径已解析()
    {
        using var zip = BuildZip(
            ("META-INF/container.xml", Container),
            ("OEBPS/content.opf", Opf(string.Empty, string.Empty, string.Empty)));

        var index = EpubPackageIndex.TryRead(zip, out var reason);

        Assert.Null(reason);
        Assert.NotNull(index);
        Assert.Equal(2, index!.Spine.Count);
        Assert.Equal("OEBPS/Text/ch1.xhtml", index.Spine[0].SourceHref);
        Assert.Equal("OEBPS/Text/ch2.xhtml", index.Spine[1].SourceHref);
        Assert.Equal(0, index.Spine[0].Index);
        Assert.Equal(1, index.Spine[1].Index);
        Assert.Equal("测试书", index.Title);
        Assert.Equal("作者甲", index.Author);
    }

    [Fact]
    public void 收集样式表()
    {
        using var zip = BuildZip(
            ("META-INF/container.xml", Container),
            ("OEBPS/content.opf", Opf(string.Empty, string.Empty, string.Empty)));

        var index = EpubPackageIndex.TryRead(zip, out _);

        Assert.Equal(new[] { "OEBPS/Styles/main.css" }, index!.Styles);
    }

    [Fact]
    public void EPUB3_从_nav_取章节标题()
    {
        var nav = """
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
              <body><nav epub:type="toc"><ol>
                <li><a href="Text/ch1.xhtml">第一章 起</a></li>
                <li><a href="Text/ch2.xhtml#top">第二章 承</a></li>
              </ol></nav></body>
            </html>
            """;

        using var zip = BuildZip(
            ("META-INF/container.xml", Container),
            ("OEBPS/content.opf", Opf(
                string.Empty,
                """<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>""",
                string.Empty)),
            ("OEBPS/nav.xhtml", nav));

        var index = EpubPackageIndex.TryRead(zip, out _);

        Assert.Equal("第一章 起", index!.Spine[0].Title);
        // 标题里的 #fragment 必须被剥掉，否则对不上 spine 的路径
        Assert.Equal("第二章 承", index.Spine[1].Title);
    }

    [Fact]
    public void EPUB2_从_ncx_取章节标题()
    {
        var ncx = """
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/">
              <navMap>
                <navPoint id="n1"><navLabel><text>引子</text></navLabel><content src="Text/ch1.xhtml"/></navPoint>
                <navPoint id="n2"><navLabel><text>正文</text></navLabel><content src="Text/ch2.xhtml"/></navPoint>
              </navMap>
            </ncx>
            """;

        using var zip = BuildZip(
            ("META-INF/container.xml", Container),
            ("OEBPS/content.opf", Opf(
                " toc=\"ncx\"",
                """<item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>""",
                string.Empty)),
            ("OEBPS/toc.ncx", ncx));

        var index = EpubPackageIndex.TryRead(zip, out _);

        Assert.Equal("引子", index!.Spine[0].Title);
        Assert.Equal("正文", index.Spine[1].Title);
    }

    [Fact]
    public void spine_指向不存在的_id_时跳过且序号仍然连续()
    {
        using var zip = BuildZip(
            ("META-INF/container.xml", Container),
            ("OEBPS/content.opf", Opf(
                string.Empty,
                string.Empty,
                """<itemref idref="不存在的id"/>""")));

        var index = EpubPackageIndex.TryRead(zip, out _);

        Assert.Equal(2, index!.Spine.Count);
        Assert.Equal(0, index.Spine[0].Index);
        Assert.Equal(1, index.Spine[1].Index);
    }

    [Fact]
    public void 包内有不安全路径时整包拒收()
    {
        using var zip = BuildZip(
            ("META-INF/container.xml", Container),
            ("OEBPS/content.opf", Opf(string.Empty, string.Empty, string.Empty)),
            ("../跑出去.txt", "x"));

        Assert.Null(EpubPackageIndex.TryRead(zip, out var reason));
        Assert.Contains("不安全路径", reason);
    }

    [Fact]
    public void 缺_container_xml_时给出原因()
    {
        using var zip = BuildZip(
            ("OEBPS/content.opf", Opf(string.Empty, string.Empty, string.Empty)));

        Assert.Null(EpubPackageIndex.TryRead(zip, out var reason));
        Assert.Contains("container.xml", reason);
    }

    [Fact]
    public void 解析失败时返回_null_而不抛异常()
    {
        using var zip = BuildZip(
            ("META-INF/container.xml", "这不是 XML"),
            ("OEBPS/content.opf", Opf(string.Empty, string.Empty, string.Empty)));

        var index = EpubPackageIndex.TryRead(zip, out var reason);

        Assert.Null(index);
        Assert.NotNull(reason);
    }
}