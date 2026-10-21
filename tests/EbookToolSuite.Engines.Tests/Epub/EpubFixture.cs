using System.IO;
using System.IO.Compression;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>
/// 测试用最小 EPUB 样本 + "换掉 OPF 再打包"。
///
/// 刻意不引 Services.Tests 里的 EpubBuilder（跨测试项目不互相引用），
/// 也不做 T3-4 打包器该做的全套自检  这里只要能产出 EpubCheck/Calibre 愿意读的包即可。
/// mimetype 必须是第一条目且不压缩，这条会照做，否则 EpubCheck 直接报 PKG-006。
/// </summary>
internal static class EpubFixture
{
    private const string OpfEntry = "OEBPS/content.opf";
    private const string MimeType = "application/epub+zip";

    private static readonly string ContainerXml =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        + "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">\n"
        + "  <rootfiles>\n"
        + "    <rootfile full-path=\"" + OpfEntry + "\" media-type=\"application/oebps-package+xml\"/>\n"
        + "  </rootfiles>\n"
        + "</container>\n";

    internal const string Epub3Opf =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        + "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\">\n"
        + "  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n"
        + "    <dc:identifier id=\"bookid\">urn:uuid:11111111-2222-3333-4444-555555555555</dc:identifier>\n"
        + "    <dc:title>原始书名</dc:title>\n"
        + "    <dc:language>zh-CN</dc:language>\n"
        + "    <meta property=\"dcterms:modified\">2026-03-04T05:06:07Z</meta>\n"
        + "  </metadata>\n"
        + "  <manifest>\n"
        + "    <item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>\n"
        + "    <item id=\"ch1\" href=\"ch1.xhtml\" media-type=\"application/xhtml+xml\"/>\n"
        + "  </manifest>\n"
        + "  <spine>\n"
        + "    <itemref idref=\"ch1\"/>\n"
        + "  </spine>\n"
        + "</package>\n";

    internal const string Epub2Opf =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        + "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\" unique-identifier=\"bookid\">\n"
        + "  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:opf=\"http://www.idpf.org/2007/opf\">\n"
        + "    <dc:identifier id=\"bookid\">urn:uuid:11111111-2222-3333-4444-555555555555</dc:identifier>\n"
        + "    <dc:title>原始书名</dc:title>\n"
        + "    <dc:language>zh-CN</dc:language>\n"
        + "  </metadata>\n"
        + "  <manifest>\n"
        + "    <item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>\n"
        + "    <item id=\"ch1\" href=\"ch1.xhtml\" media-type=\"application/xhtml+xml\"/>\n"
        + "  </manifest>\n"
        + "  <spine toc=\"ncx\">\n"
        + "    <itemref idref=\"ch1\"/>\n"
        + "  </spine>\n"
        + "</package>\n";

    private const string NavXhtml =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        + "<!DOCTYPE html>\n"
        + "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\">\n"
        + "<head><title>目录</title></head>\n"
        + "<body>\n"
        + "<nav epub:type=\"toc\" id=\"toc\"><ol><li><a href=\"ch1.xhtml\">第 1 章</a></li></ol></nav>\n"
        + "</body>\n"
        + "</html>\n";

    private const string Chapter3 =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        + "<!DOCTYPE html>\n"
        + "<html xmlns=\"http://www.w3.org/1999/xhtml\">\n"
        + "<head><title>第 1 章</title></head>\n"
        + "<body><h1>第 1 章</h1><p>正文。</p></body>\n"
        + "</html>\n";

    private const string Chapter2 =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        + "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.1//EN\" \"http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd\">\n"
        + "<html xmlns=\"http://www.w3.org/1999/xhtml\">\n"
        + "<head><title>第 1 章</title></head>\n"
        + "<body><h1>第 1 章</h1><p>正文。</p></body>\n"
        + "</html>\n";

    private static readonly string Ncx =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        + "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\">\n"
        + "  <head><meta name=\"dtb:uid\" content=\"urn:uuid:11111111-2222-3333-4444-555555555555\"/></head>\n"
        + "  <docTitle><text>原始书名</text></docTitle>\n"
        + "  <navMap>\n"
        + "    <navPoint id=\"np1\" playOrder=\"1\"><navLabel><text>第 1 章</text></navLabel>\n"
        + "      <content src=\"ch1.xhtml\"/></navPoint>\n"
        + "  </navMap>\n"
        + "</ncx>\n";

    /// <summary>写一个最小但 EpubCheck 干净的单章 EPUB。</summary>
    internal static string Write(string path, string opfXml, bool epub3)
    {
        var entries = new List<(string Name, string Text)>
        {
            (OpfEntry, opfXml),
            ("OEBPS/ch1.xhtml", epub3 ? Chapter3 : Chapter2),
        };

        if (epub3)
        {
            entries.Add(("OEBPS/nav.xhtml", NavXhtml));
        }
        else
        {
            entries.Add(("OEBPS/toc.ncx", Ncx));
        }

        WriteZip(path, entries);
        return path;
    }

    /// <summary>把新的 OPF 文本换进现成的 EPUB 里，条目顺序与压缩方式保持不变。</summary>
    internal static string ReplaceOpf(string sourceEpub, string newOpfXml, string destination)
    {
        using var source = ZipFile.OpenRead(sourceEpub);
        using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write);
        using var destinationZip = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var entry in source.Entries)
        {
            if (entry.FullName == OpfEntry)
            {
                using var writer = new StreamWriter(
                    destinationZip.CreateEntry(OpfEntry, CompressionLevel.Optimal).Open(),
                    new UTF8Encoding(false));
                writer.Write(newOpfXml);
                continue;
            }

            var level = entry.FullName == "mimetype"
                ? CompressionLevel.NoCompression
                : CompressionLevel.Optimal;
            using var input = entry.Open();
            using var output = destinationZip.CreateEntry(entry.FullName, level).Open();
            input.CopyTo(output);
        }

        return destination;
    }

    private static void WriteZip(
        string path,
        IReadOnlyList<(string Name, string Text)> entries,
        IReadOnlyList<(string Name, byte[] Bytes)>? binaryEntries = null)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        // mimetype 必须是第一条且不压缩（设计文档 6.1.5）
        using (var writer = new StreamWriter(
                   zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open(),
                   new UTF8Encoding(false)))
        {
            writer.Write(MimeType);
        }

        using (var writer = new StreamWriter(
                   zip.CreateEntry("META-INF/container.xml", CompressionLevel.Optimal).Open(),
                   new UTF8Encoding(false)))
        {
            writer.Write(ContainerXml);
        }

        foreach (var (name, text) in entries)
        {
            using var writer = new StreamWriter(
                zip.CreateEntry(name, CompressionLevel.Optimal).Open(),
                new UTF8Encoding(false));
            writer.Write(text);
        }

        foreach (var (name, data) in binaryEntries ?? [])
        {
            using var output = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            output.Write(data, 0, data.Length);
        }
    }

    /// <summary>
    /// 写一个带二进制资源（封面图片）的最小 EPUB。binaryEntries 用包内相对路径做名字，
    /// 例如 "OEBPS/Images/cover.jpg"。mimetype 仍然排在第一条且不压缩（EpubCheck 会查）。
    /// </summary>
    internal static string Write(
        string path,
        string opfXml,
        bool epub3,
        IReadOnlyList<(string Name, byte[] Bytes)> binaryEntries)
    {
        var textEntries = new List<(string Name, string Text)>
        {
            (OpfEntry, opfXml),
            ("OEBPS/ch1.xhtml", epub3 ? Chapter3 : Chapter2),
        };

        if (epub3)
        {
            textEntries.Add(("OEBPS/nav.xhtml", NavXhtml));
        }
        else
        {
            textEntries.Add(("OEBPS/toc.ncx", Ncx));
        }

        WriteZip(path, textEntries, binaryEntries);
        return path;
    }

    /// <summary>
    /// 造一张确定性的测试图：RGB 渐变 + 左下角一块青色方块。
    /// 换封面前后用同一张图的像素值判断"到底换了没有"。
    /// </summary>
    internal static byte[] MakeJpeg(int width, int height, int quality = 88)
        => EncodeImage(width, height, quality, png: false);

    /// <summary>同上，PNG 编码（判据 3 的输入就是一张 8000x6000 的 PNG）。</summary>
    internal static byte[] MakePng(int width, int height)
        => EncodeImage(width, height, 0, png: true);

    private static byte[] EncodeImage(int width, int height, int quality, bool png)
    {
        using var image = new Image<Rgb24>(width, height);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var r = (byte)(x * 255 / Math.Max(1, accessor.Width - 1));
                    var g = (byte)(y * 255 / Math.Max(1, accessor.Height - 1));
                    row[x] = new Rgb24(r, g, 0x40);
                }
            }
        });

        using var buffer = new MemoryStream();
        IImageEncoder encoder = png ? new PngEncoder() : new JpegEncoder { Quality = quality };
        image.Save(buffer, encoder);
        return buffer.ToArray();
    }
}
