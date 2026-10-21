using System.Text;
using EbookToolSuite.Engines.Epub;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>
/// 章节结构测试用的工作区（T5-1）：直接铺一套"解包后"的 EPUB 文件，不经过 zip。
/// </summary>
/// <remarks>
/// T5-1 的操作对象就是工作区里的解包文件，所以单测直接铺文件最省事；
/// 要验证"保存出来的 EPUB 合规"再交给 EpubPackager + 真 EpubCheck（集成测试）。
/// </remarks>
internal static class StructureFixture
{
    public const string OpfEntry = "OEBPS/content.opf";
    public const string ContainerEntry = "META-INF/container.xml";

    public static string TocEntry(bool epub3) => epub3 ? "OEBPS/nav.xhtml" : "OEBPS/toc.ncx";

    public static string ChapterHref(int number) => $"OEBPS/Text/chapter-{number:D4}.xhtml";

    public static string ChapterTitle(int number) => $"第 {number} 章";

    /// <summary>
    /// 铺一个工作区。
    /// </summary>
    /// <param name="epub3">true = EPUB3（nav.xhtml 目录），false = EPUB2（toc.ncx 目录）。</param>
    /// <param name="chapters">正文与 spine 的章节数。</param>
    /// <param name="tocChapters">目录里列几章（-1 = 全部）。用来造"目录缺项"的真实现象。</param>
    /// <param name="nestedToc">目录是否嵌套（验多级缩进）。</param>
    /// <param name="withToc">是否放目录文件。</param>
    public static string Create(
        bool epub3, int chapters = 3, int tocChapters = -1, bool nestedToc = false, bool withToc = true)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "ets-structure-" + Guid.NewGuid().ToString("N"));
        var opfDir = Path.Combine(workDir, "OEBPS");
        Directory.CreateDirectory(Path.Combine(opfDir, "Text"));
        Directory.CreateDirectory(Path.Combine(workDir, "META-INF"));

        var tocCount = tocChapters < 0 ? chapters : tocChapters;

        Write(Path.Combine(workDir, "META-INF", "container.xml"), ContainerXml(), crlf: false);
        Write(Path.Combine(opfDir, "content.opf"), Opf(epub3, chapters, withToc), crlf: true);

        if (withToc)
        {
            Write(
                Path.Combine(opfDir, epub3 ? "nav.xhtml" : "toc.ncx"),
                epub3 ? NavXhtml(tocCount, nestedToc) : Ncx(tocCount, nestedToc),
                crlf: true);
        }

        for (var number = 1; number <= chapters; number++)
        {
            Write(Path.Combine(workDir, ChapterHref(number).Replace('/', Path.DirectorySeparatorChar)),
                ChapterXhtml(epub3, number), crlf: false);
        }

        return workDir;
    }

    public static string ChapterXhtml(bool epub3, int number, string? body = null)
    {
        var title = ChapterTitle(number);
        var paragraphs = body ?? $"<p>正文 {number}</p>";

        if (epub3)
        {
            return $"""
<?xml version="1.0" encoding="utf-8"?>
<!DOCTYPE html>
<html xmlns="http://www.w3.org/1999/xhtml" xml:lang="zh" lang="zh">
  <head>
    <meta charset="utf-8" />
    <title>{title}</title>
  </head>
  <body>
    <h1>{title}</h1>
    {paragraphs}
  </body>
</html>
""";
        }

        return $"""
<?xml version="1.0" encoding="utf-8"?>
<!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.1//EN" "http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd">
<html xmlns="http://www.w3.org/1999/xhtml" xml:lang="zh">
  <head>
    <title>{title}</title>
    <meta http-equiv="Content-Type" content="text/html; charset=utf-8" />
  </head>
  <body>
    <h1>{title}</h1>
    {paragraphs}
  </body>
</html>
""";
    }

    private static string ContainerXml() => """
<?xml version="1.0" encoding="utf-8"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles>
    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml" />
  </rootfiles>
</container>
""";

    private static string Opf(bool epub3, int chapters, bool withToc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine(epub3
            ? "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\">"
            : "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\" unique-identifier=\"bookid\">");
        sb.AppendLine("  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:opf=\"http://www.idpf.org/2007/opf\">");
        sb.AppendLine("    <dc:identifier id=\"bookid\">urn:uuid:11111111-2222-3333-4444-555555555555</dc:identifier>");
        sb.AppendLine("    <dc:title>结构测试书</dc:title>");
        sb.AppendLine("    <dc:language>zh</dc:language>");

        if (epub3)
        {
            sb.AppendLine("    <meta property=\"dcterms:modified\">2026-01-01T00:00:00Z</meta>");
        }

        sb.AppendLine("  </metadata>");
        sb.AppendLine("  <manifest>");

        if (withToc)
        {
            sb.AppendLine(epub3
                ? "    <item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>"
                : "    <item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>");
        }

        for (var number = 1; number <= chapters; number++)
        {
            sb.AppendLine($"    <item id=\"c{number}\" href=\"Text/chapter-{number:D4}.xhtml\" media-type=\"application/xhtml+xml\"/>");
        }

        sb.AppendLine("  </manifest>");
        sb.AppendLine(withToc && !epub3 ? "  <spine toc=\"ncx\">" : "  <spine>");

        for (var number = 1; number <= chapters; number++)
        {
            sb.AppendLine($"    <itemref idref=\"c{number}\"/>");
        }

        sb.AppendLine("  </spine>");
        sb.AppendLine("</package>");
        return sb.ToString();
    }

    private static string NavXhtml(int tocCount, bool nested)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" xml:lang=\"zh\" lang=\"zh\">");
        sb.AppendLine("  <head>");
        sb.AppendLine("    <title>目录</title>");
        sb.AppendLine("  </head>");
        sb.AppendLine("  <body>");
        sb.AppendLine("    <nav epub:type=\"toc\" id=\"toc\">");
        sb.AppendLine("      <h1>目录</h1>");
        sb.AppendLine("      <ol>");

        for (var number = 1; number <= tocCount; number++)
        {
            if (nested && number == 1 && tocCount >= 2)
            {
                sb.AppendLine($"        <li><a href=\"Text/chapter-{number:D4}.xhtml\">{ChapterTitle(number)}</a>");
                sb.AppendLine("          <ol>");
                sb.AppendLine($"            <li><a href=\"Text/chapter-{number + 1:D4}.xhtml\">{ChapterTitle(number + 1)}</a></li>");
                sb.AppendLine("          </ol>");
                sb.AppendLine("        </li>");
                number++;
                continue;
            }

            sb.AppendLine($"        <li><a href=\"Text/chapter-{number:D4}.xhtml\">{ChapterTitle(number)}</a></li>");
        }

        sb.AppendLine("      </ol>");
        sb.AppendLine("    </nav>");
        sb.AppendLine("  </body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static string Ncx(int tocCount, bool nested)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\">");
        sb.AppendLine("  <head>");
        sb.AppendLine("    <meta name=\"dtb:uid\" content=\"urn:uuid:11111111-2222-3333-4444-555555555555\"/>");
        sb.AppendLine("  </head>");
        sb.AppendLine("  <docTitle><text>结构测试书</text></docTitle>");
        sb.AppendLine("  <navMap>");

        for (var number = 1; number <= tocCount; number++)
        {
            if (nested && number == 1 && tocCount >= 2)
            {
                sb.AppendLine($"    <navPoint id=\"np-{number}\" playOrder=\"{number}\">");
                sb.AppendLine($"      <navLabel><text>{ChapterTitle(number)}</text></navLabel>");
                sb.AppendLine($"      <content src=\"Text/chapter-{number:D4}.xhtml\"/>");
                sb.AppendLine($"      <navPoint id=\"np-{number + 1}\" playOrder=\"{number + 1}\">");
                sb.AppendLine($"        <navLabel><text>{ChapterTitle(number + 1)}</text></navLabel>");
                sb.AppendLine($"        <content src=\"Text/chapter-{number + 1:D4}.xhtml\"/>");
                sb.AppendLine("      </navPoint>");
                sb.AppendLine("    </navPoint>");
                number++;
                continue;
            }

            sb.AppendLine($"    <navPoint id=\"np-{number}\" playOrder=\"{number}\">");
            sb.AppendLine($"      <navLabel><text>{ChapterTitle(number)}</text></navLabel>");
            sb.AppendLine($"      <content src=\"Text/chapter-{number:D4}.xhtml\"/>");
            sb.AppendLine("    </navPoint>");
        }

        sb.AppendLine("  </navMap>");
        sb.AppendLine("</ncx>");
        return sb.ToString();
    }

    private static void Write(string path, string content, bool crlf)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = crlf ? content.Replace("\r\n", "\n").Replace("\n", "\r\n") : content.Replace("\r\n", "\n");
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}

/// <summary>测试用的工作区包：用完自动删目录（测试里不掉磁盘垃圾）。</summary>
internal sealed class StructureSandbox : IDisposable
{
    public StructureSandbox(string workDir) => WorkDir = workDir;

    public string WorkDir { get; }

    public string PathOf(string entry)
        => Path.Combine(WorkDir, entry.Replace('/', Path.DirectorySeparatorChar));

    public string Read(string entry) => File.ReadAllText(PathOf(entry));

    public bool Exists(string entry) => File.Exists(PathOf(entry));

    public EpubStructureEditor Editor(bool epub3 = true)
        => new(WorkDir, StructureFixture.OpfEntry);

    public void Dispose()
    {
        try
        {
            Directory.Delete(WorkDir, recursive: true);
        }
        catch (IOException)
        {
            // 删不掉就算了：临时目录，不该让测试因此失败。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
