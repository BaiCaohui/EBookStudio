using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using EbookToolSuite.Engines.Epub;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>
/// 目录解析（T4-4）：EPUB2 NCX、EPUB3 nav、层级、fragment、坏目录降级。
/// </summary>
/// <remarks>
/// 造包一律在内存里（跟 EpubPackageIndexTests 一个路子）：
/// EpubPackageIndex 只读 OPF/nav/ncx 三个文件，不需要真的章节文件。
/// </remarks>
public sealed class EpubTocTests
{
    private const string Container = """
        <?xml version="1.0"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
        </container>
        """;

    /// <summary>spine 上的 toc 属性（指向 ncx）。</summary>
    private const string SpineToc = " toc=\"ncx\"";

    private const string NcxItem =
        """<item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>""";

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

    /// <summary>默认三章的书：Text/ch1.xhtml、Text/ch2.xhtml、Text/ch3.xhtml。</summary>
    private static string Opf(string spineAttributes, string extraManifest = "", string extraSpine = "")
        => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>目录测试书</dc:title></metadata>
          <manifest>
            <item id="ch1" href="Text/ch1.xhtml" media-type="application/xhtml+xml"/>
            <item id="ch2" href="Text/ch2.xhtml" media-type="application/xhtml+xml"/>
            <item id="ch3" href="Text/ch3.xhtml" media-type="application/xhtml+xml"/>
        {extraManifest}
          </manifest>
          <spine{spineAttributes}>
            <itemref idref="ch1"/>
            <itemref idref="ch2"/>
            <itemref idref="ch3"/>
        {extraSpine}
          </spine>
        </package>
        """;

    [Fact]
    public void EPUB2_NCX_平铺目录_标题与_spine_序号对得上()
    {
        var ncx = """
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/"><navMap>
              <navPoint id="n1"><navLabel><text>引子</text></navLabel><content src="Text/ch1.xhtml"/></navPoint>
              <navPoint id="n2"><navLabel><text>正文</text></navLabel><content src="Text/ch2.xhtml"/></navPoint>
            </navMap></ncx>
            """;

        using var zip = BuildZip(
            ("META-INF/container.xml", Container),
            ("OEBPS/content.opf", Opf(SpineToc, NcxItem)),
            ("OEBPS/toc.ncx", ncx));

        var index = EpubPackageIndex.TryRead(zip, out var reason);

        Assert.NotNull(index);
        Assert.False(index!.TocIsFallback, reason);
        Assert.Equal(2, index.Toc.Count);
        Assert.Equal("引子", index.Toc[0].Title);
        Assert.Equal(0, index.Toc[0].SpineIndex);
        Assert.Equal(1, index.Toc[0].Level);
        Assert.Equal("正文", index.Toc[1].Title);
        Assert.Equal(1, index.Toc[1].SpineIndex);
        Assert.Empty(index.Toc[0].Children);
    }

    [Fact]
    public void EPUB2_NCX_四级嵌套_层级到_3_级封顶()
    {
        var ncx = """
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/"><navMap>
              <navPoint id="a"><navLabel><text>第一部</text></navLabel><content src="Text/ch1.xhtml"/>
                <navPoint id="b"><navLabel><text>第一章</text></navLabel><content src="Text/ch2.xhtml"/>
                  <navPoint id="c"><navLabel><text>第一节</text></navLabel><content src="Text/ch3.xhtml"/>
                    <navPoint id="d"><navLabel><text>小段</text></navLabel><content src="Text/ch3.xhtml#p9"/></navPoint>
                  </navPoint>
                </navPoint>
              </navPoint>
            </navMap></ncx>
            """;

        using var zip = BuildZip(
            ("META-INF/container.xml", Container),
            ("OEBPS/content.opf", Opf(SpineToc, NcxItem)),
            ("OEBPS/toc.ncx", ncx));

        var index = EpubPackageIndex.TryRead(zip, out _)!;

        var first = Assert.Single(index.Toc);
        Assert.Equal("第一部", first.Title);
        Assert.Equal(1, first.Level);

        var second = Assert.Single(first.Children);
        Assert.Equal("第一章", second.Title);
        Assert.Equal(2, second.Level);

        var third = Assert.Single(second.Children);
        Assert.Equal("第一节", third.Title);
        Assert.Equal(3, third.Level);

        // 第四层被拍平到第 3 级（判据：只支持 3 级）
        var fourth = Assert.Single(third.Children);
        Assert.Equal("小段", fourth.Title);
        Assert.Equal(3, fourth.Level);
        Assert.Equal("p9", fourth.Fragment);
        Assert.Equal(2, fourth.SpineIndex);
    }

    [Fact]
    public void EPUB3_nav_嵌套目录_分组标题不可跳转但保留子项()
    {
        var nav = """
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><body>
              <nav epub:type="toc"><ol>
                <li><span>第一部 起</span><ol>
                  <li><a href="Text/ch1.xhtml">第一章</a></li>
                  <li><a href="Text/ch2.xhtml">第二章</a></li>
                </ol></li>
                <li><a href="Text/ch3.xhtml">第三章</a></li>
              </ol></nav>
            </body></html>
            """;

        using var zip = BuildZip(
            ("META-INF/container.xml", Container),
            ("OEBPS/content.opf", Opf(
                string.Empty,
                """<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>""")),
            ("OEBPS/nav.xhtml", nav));

        var index = EpubPackageIndex.TryRead(zip, out var reason)!;

        Assert.False(index.TocIsFallback, reason);
        Assert.Equal(2, index.Toc.Count);

        var group = index.Toc[0];
        Assert.Equal("第一部 起", group.Title);
        Assert.Equal(-1, group.SpineIndex);
        Assert.Equal(2, group.Children.Count);
        Assert.Equal("第一章", group.Children[0].Title);
        Assert.Equal(0, group.Children[0].SpineIndex);
        Assert.Equal(2, group.Children[0].Level);

        Assert.Equal("第三章", index.Toc[1].Title);
        Assert.Equal(2, index.Toc[1].SpineIndex);
    }

    [Fact]
    public void 目录项带_fragment_时解析出锚点_且_URL_编码的路径也能对上_spine()
    {
        // 第三章在 OPF 里就写成 %20 编码（真实 EPUB 常见写法）
        var opf = """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>目录测试书</dc:title></metadata>
              <manifest>
                <item id="ch1" href="Text/ch1.xhtml" media-type="application/xhtml+xml"/>
                <item id="ch2" href="Text/ch2.xhtml" media-type="application/xhtml+xml"/>
                <item id="ch3" href="Text/ch%203.xhtml" media-type="application/xhtml+xml"/>
                <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
              </manifest>
              <spine toc="ncx">
                <itemref idref="ch1"/>
                <itemref idref="ch2"/>
                <itemref idref="ch3"/>
              </spine>
            </package>
            """;

        var ncx = """
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/"><navMap>
              <navPoint id="n1"><navLabel><text>第二章第二节</text></navLabel><content src="Text/ch2.xhtml#sec-2"/></navPoint>
              <navPoint id="n2"><navLabel><text>带空格的章</text></navLabel><content src="Text/ch%203.xhtml#p%201"/></navPoint>
            </navMap></ncx>
            """;

        using var zip = BuildZip(
            ("META-INF/container.xml", Container),
            ("OEBPS/content.opf", opf),
            ("OEBPS/toc.ncx", ncx));

        var index = EpubPackageIndex.TryRead(zip, out _)!;

        Assert.False(index.TocIsFallback);
        Assert.Equal(2, index.Toc.Count);
        Assert.Equal("sec-2", index.Toc[0].Fragment);
        Assert.Equal(1, index.Toc[0].SpineIndex);

        // %20 解码后要能对上 spine（ch%203.xhtml 与 Text/ch%203.xhtml 是同一个文件）
        Assert.Equal("p 1", index.Toc[1].Fragment);
        Assert.Equal(2, index.Toc[1].SpineIndex);
        Assert.Equal("OEBPS/Text/ch 3.xhtml", index.Spine[2].SourceHref);
    }

    [Fact]
    public void 目录项指向不在_spine_的文档时_丢该项但把子项提上来()
    {
        var ncx = """
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/"><navMap>
              <navPoint id="cover"><navLabel><text>封面</text></navLabel><content src="Text/cover.xhtml"/>
                <navPoint id="c1"><navLabel><text>第一章</text></navLabel><content src="Text/ch1.xhtml"/></navPoint>
              </navPoint>
            </navMap></ncx>
            """;

        const string CoverItem =
            """<item id="cover" href="Text/cover.xhtml" media-type="application/xhtml+xml"/>""";

        using var zip = BuildZip(
            ("META-INF/container.xml", Container),
            ("OEBPS/content.opf", Opf(SpineToc, NcxItem + "\n" + CoverItem)),
            ("OEBPS/toc.ncx", ncx));

        var index = EpubPackageIndex.TryRead(zip, out _)!;

        // 封面页不在 spine 里：它跳不了，但子项必须还在，且层级提回第 1 层
        var only = Assert.Single(index.Toc);
        Assert.Equal("第一章", only.Title);
        Assert.Equal(1, only.Level);
        Assert.Equal(0, only.SpineIndex);
    }

    [Fact]
    public void NCX_损坏时_降级为按_spine_平铺且标题用文件名()
    {
        using var zip = BuildZip(
            ("META-INF/container.xml", Container),
            ("OEBPS/content.opf", Opf(SpineToc, NcxItem)),
            ("OEBPS/toc.ncx", "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\"><head/></ncx>"));

        var index = EpubPackageIndex.TryRead(zip, out _)!;

        Assert.True(index.TocIsFallback);
        Assert.Equal(3, index.Toc.Count);
        Assert.Equal("ch1.xhtml", index.Toc[0].Title);
        Assert.Equal(0, index.Toc[0].SpineIndex);
        Assert.Equal(1, index.Toc[0].Level);
        Assert.Equal("ch3.xhtml", index.Toc[2].Title);
    }

    [Fact]
    public void 完全没有目录时_降级为按_spine_平铺()
    {
        using var zip = BuildZip(
            ("META-INF/container.xml", Container),
            ("OEBPS/content.opf", Opf(string.Empty)));

        var index = EpubPackageIndex.TryRead(zip, out _)!;

        Assert.True(index.TocIsFallback);
        Assert.Equal(3, index.Toc.Count);
        Assert.Equal(2, index.Toc[2].SpineIndex);
    }

    [Fact]
    public void spine_没写_toc_属性时_按_media_type_也能找到_ncx()
    {
        var ncx = """
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/"><navMap>
              <navPoint id="n1"><navLabel><text>引子</text></navLabel><content src="Text/ch1.xhtml"/></navPoint>
            </navMap></ncx>
            """;

        using var zip = BuildZip(
            ("META-INF/container.xml", Container),
            ("OEBPS/content.opf", Opf(string.Empty, NcxItem)),
            ("OEBPS/toc.ncx", ncx));

        var index = EpubPackageIndex.TryRead(zip, out _)!;

        Assert.False(index.TocIsFallback);
        Assert.Equal("引子", Assert.Single(index.Toc).Title);
    }

    [Fact]
    public void 五百章的_NCX_全部解析出来_顺序不丢()
    {
        var opfItems = new StringBuilder();
        var spine = new StringBuilder();
        var navPoints = new StringBuilder();

        for (var i = 0; i < 500; i++)
        {
            opfItems.Append($"\n<item id=\"c{i}\" href=\"Text/c{i}.xhtml\" media-type=\"application/xhtml+xml\"/>");
            spine.Append($"\n<itemref idref=\"c{i}\"/>");
            navPoints.Append($"\n<navPoint id=\"n{i}\"><navLabel><text>第 {i + 1} 章</text></navLabel><content src=\"Text/c{i}.xhtml\"/></navPoint>");
        }

        var opf = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>大部头</dc:title></metadata>
              <manifest><item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>{opfItems}</manifest>
              <spine toc="ncx">{spine}</spine>
            </package>
            """;

        var ncx = "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\"><navMap>" + navPoints + "</navMap></ncx>";

        using var zip = BuildZip(
            ("META-INF/container.xml", Container),
            ("OEBPS/content.opf", opf),
            ("OEBPS/toc.ncx", ncx));

        var watch = Stopwatch.StartNew();
        var index = EpubPackageIndex.TryRead(zip, out var reason)!;
        watch.Stop();

        Assert.False(index.TocIsFallback, reason);
        Assert.Equal(500, index.Spine.Count);
        Assert.Equal(500, index.Toc.Count);
        Assert.Equal("第 1 章", index.Toc[0].Title);
        Assert.Equal("第 500 章", index.Toc[499].Title);
        Assert.Equal(499, index.Toc[499].SpineIndex);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"解析 500 章用了 {watch.ElapsedMilliseconds} ms");
    }
}
