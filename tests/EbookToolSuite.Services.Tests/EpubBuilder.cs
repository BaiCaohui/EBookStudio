using System.IO.Compression;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace EbookToolSuite.Services.Tests;

/// <summary>
/// 现造一个结构合法的最小 EPUB（设计文档 12 章：样本可构造，不依赖二进制样本）。
/// 用真实的 ZipArchive 写盘，所以导入管线走的是和线上完全一样的路径。
/// </summary>
internal sealed class EpubBuilder
{
    /// <summary>固定 zip 条目时间戳，让构造出来的 EPUB 逐字节可复现。</summary>
    private static readonly DateTimeOffset FixedStamp =
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string ContainerXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
        "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" " +
        "media-type=\"application/oebps-package+xml\"/></rootfiles></container>";

    public string Title { get; set; } = "测试书";

    public List<string> Authors { get; set; } = new() { "测试作者" };

    public string? Publisher { get; set; }

    public string? Description { get; set; }

    public string Language { get; set; } = "zh-CN";

    public string? Series { get; set; }

    public double? SeriesIndex { get; set; }

    public string? Isbn { get; set; }

    public bool IsEpub3 { get; set; } = true;

    public int ChapterCount { get; set; } = 2;

    /// <summary>
    /// 每章正文（T6-4 全文搜索测试要能塞进去可搜的关键词）。
    /// 默认值与原行为一致，不影响既有用例。
    /// </summary>
    public string ChapterText { get; set; } = "正文内容。";

    /// <summary>封面原始字节。null 表示这本书没有封面。</summary>
    public byte[]? CoverBytes { get; set; }

    /// <summary>是否在 OPF 里声明封面。false 但有图片资源，用来测兜底取图。</summary>
    public bool DeclareCover { get; set; } = true;

    /// <summary>额外塞一张正文插图（体积比封面大时用来验证兜底策略）。</summary>
    public byte[]? ExtraImageBytes { get; set; }

    /// <summary>EPUB2 时省略 spine 的 toc 属性（真实书里很常见，但规范不允许）。</summary>
    public bool OmitSpineToc { get; set; }

    /// <summary>manifest 里重复声明同一个 href（真实书里很常见，但规范不允许）。</summary>
    public bool DuplicateManifestHref { get; set; }

    /// <summary>正文条目的 media-type 写成 text/html 而不是 xhtml+xml（EPUB2 常见违规）。</summary>
    public bool HtmlMediaTypeForChapters { get; set; }

    /// <summary>自定义 encryption.xml 内容：用来构造字体混淆与真加密两种情形。</summary>
    public string? EncryptionXmlOverride { get; set; }

    /// <summary>true 时写入 META-INF/encryption.xml，模拟 DRM。</summary>
    public bool MarkEncrypted { get; set; }

    /// <summary>造一张能解码的真 JPEG。</summary>
    public static byte[] MakeJpeg(int width = 400, int height = 600)
    {
        using var image = new Image<Rgba32>(width, height);
        using var buffer = new MemoryStream();
        image.SaveAsJpeg(buffer);
        return buffer.ToArray();
    }

    public string WriteTo(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        using var stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        // mimetype 必须是第一条目且不压缩，否则 EpubCheck 直接报错（设计文档 6.1.5）
        var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var entry = mime.Open())
        {
            entry.Write(Encoding.ASCII.GetBytes("application/epub+zip"));
        }

        WriteText(zip, "META-INF/container.xml", ContainerXml);

        if (MarkEncrypted || EncryptionXmlOverride is not null)
        {
            var encryptionXml = EncryptionXmlOverride
                ?? "<?xml version=\"1.0\"?><encryption xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">"
                    + "<EncryptedData><CipherData/></EncryptedData></encryption>";
            WriteText(zip, "META-INF/encryption.xml", encryptionXml);
        }

        var manifest = new StringBuilder();
        var spine = new StringBuilder();

        if (IsEpub3)
        {
            manifest.Append("<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" " +
                            "properties=\"nav\"/>");
        }
        else
        {
            manifest.Append("<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>");
        }

        for (var i = 1; i <= ChapterCount; i++)
        {
            manifest.Append("<item id=\"ch").Append(i).Append("\" href=\"ch").Append(i)
                .Append(".xhtml\" media-type=\"")
                .Append(HtmlMediaTypeForChapters ? "text/html" : "application/xhtml+xml")
                .Append("\"/>");
            spine.Append("<itemref idref=\"ch").Append(i).Append("\"/>");
        }

        if (DuplicateManifestHref)
        {
            // 同一个 href 在 manifest 里声明两次：规范不允许，但真实书里常见
            manifest.Append("<item id=\"ch1-dup\" href=\"ch1.xhtml\" media-type=\"application/xhtml+xml\"/>");
        }


        var hasCover = CoverBytes is { Length: > 0 };
        if (hasCover && DeclareCover)
        {
            manifest.Append("<item id=\"cover-img\" href=\"cover.jpg\" media-type=\"image/jpeg\" " +
                            "properties=\"cover-image\"/>");
        }
        else if (hasCover)
        {
            // 有图但不声明封面：测兜底
            manifest.Append("<item id=\"img-a\" href=\"cover.jpg\" media-type=\"image/jpeg\"/>");
        }

        if (ExtraImageBytes is { Length: > 0 })
        {
            manifest.Append("<item id=\"img-b\" href=\"extra.jpg\" media-type=\"image/jpeg\"/>");
        }

        WriteText(zip, "OEBPS/content.opf", BuildOpf(manifest.ToString(), spine.ToString()));

        if (IsEpub3)
        {
            WriteText(zip, "OEBPS/nav.xhtml", BuildNav());
        }
        else
        {
            WriteText(zip, "OEBPS/toc.ncx", BuildNcx());
        }

        for (var i = 1; i <= ChapterCount; i++)
        {
            WriteText(zip, "OEBPS/ch" + i + ".xhtml", BuildChapter(i));
        }

        if (hasCover)
        {
            WriteBytes(zip, "OEBPS/cover.jpg", CoverBytes!);
        }

        if (ExtraImageBytes is { Length: > 0 })
        {
            WriteBytes(zip, "OEBPS/extra.jpg", ExtraImageBytes);
        }

        return full;
    }

    private string BuildOpf(string manifest, string spine)
    {
        var version = IsEpub3 ? "3.0" : "2.0";
        var metadata = new StringBuilder();

        metadata.Append("<dc:identifier id=\"bookid\">urn:uuid:")
            .Append(Guid.NewGuid().ToString("D")).Append("</dc:identifier>");
        metadata.Append("<dc:title>").Append(Escape(Title)).Append("</dc:title>");
        metadata.Append("<dc:language>").Append(Escape(Language)).Append("</dc:language>");

        foreach (var author in Authors)
        {
            metadata.Append("<dc:creator>").Append(Escape(author)).Append("</dc:creator>");
        }

        if (Publisher is not null)
        {
            metadata.Append("<dc:publisher>").Append(Escape(Publisher)).Append("</dc:publisher>");
        }

        if (Description is not null)
        {
            metadata.Append("<dc:description>").Append(Escape(Description)).Append("</dc:description>");
        }

        if (Isbn is not null)
        {
            metadata.Append("<dc:identifier>urn:isbn:").Append(Escape(Isbn)).Append("</dc:identifier>");
        }

        if (IsEpub3)
        {
            // EPUB3 必需，否则 EpubCheck 永远 error（审计 S2）
            metadata.Append("<meta property=\"dcterms:modified\">2026-01-01T00:00:00Z</meta>");
        }

        if (CoverBytes is { Length: > 0 } && DeclareCover)
        {
            metadata.Append("<meta name=\"cover\" content=\"cover-img\"/>");
        }

        if (Series is not null)
        {
            metadata.Append("<meta name=\"calibre:series\" content=\"").Append(Escape(Series)).Append("\"/>");
            if (SeriesIndex is double index)
            {
                metadata.Append("<meta name=\"calibre:series_index\" content=\"")
                    .Append(index.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append("\"/>");
            }
        }

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
               "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"" + version + "\" " +
               "unique-identifier=\"bookid\">" +
               "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" + metadata + "</metadata>" +
               "<manifest>" + manifest + "</manifest>" +
               "<spine" + (IsEpub3 || OmitSpineToc ? "" : " toc=\"ncx\"") + ">" + spine + "</spine>" +
               "</package>";
    }

    private string BuildNav()
        => "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
           "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\">" +
           "<head><title>目录</title></head><body>" +
           "<nav epub:type=\"toc\" id=\"toc\"><ol>" +
           string.Concat(Enumerable.Range(1, ChapterCount).Select(i =>
               "<li><a href=\"ch" + i + ".xhtml\">第 " + i + " 章</a></li>")) +
           "</ol></nav></body></html>";

    private string BuildNcx()
        => "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
           "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\">" +
           "<head><meta name=\"dtb:uid\" content=\"urn:uuid:test\"/></head>" +
           "<docTitle><text>" + Escape(Title) + "</text></docTitle>" +
           "<navMap>" +
           string.Concat(Enumerable.Range(1, ChapterCount).Select(i =>
               "<navPoint id=\"np" + i + "\" playOrder=\"" + i + "\"><navLabel><text>第 " + i +
               " 章</text></navLabel><content src=\"ch" + i + ".xhtml\"/></navPoint>")) +
           "</navMap></ncx>";

    private string BuildChapter(int number)
        => "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
           "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>第 " + number +
           " 章</title></head><body><h1>第 " + number + " 章</h1><p>" + Escape(ChapterText) + "</p></body></html>";

    private static string Escape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static void WriteText(ZipArchive zip, string name, string content)
        => WriteBytes(zip, name, Encoding.UTF8.GetBytes(content));

    private static void WriteBytes(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = FixedStamp;
        using var stream = entry.Open();
        stream.Write(content);
    }
}