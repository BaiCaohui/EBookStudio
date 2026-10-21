using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Epub;
using EbookToolSuite.Engines.Text;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Search;

/// <summary>
/// 把一本书的正文抽成切片（T6-4）。索引来源与设计文档 7.6 一致：
/// EPUB 解包后的每章纯文本、TXT 切分后的每章、PDF 的文本层（pdfium）；
/// 漫画与 MOBI/AZW3 不索引正文（前者本来就没文字，后者要等转成 EPUB 才有可读正文）。
/// </summary>
/// <remarks>
/// <para><b>章序必须与阅读器一致</b>：搜索命中里记的 spine_index 会被拿去当
/// <c>ReaderWindow</c> 的 startIndex，章序错一位就跳到相邻章。所以这里严格复用
/// 阅读侧的两个口径：EPUB 用 <see cref="EpubPackageIndex.Spine"/> 的顺序，
/// TXT 用 <see cref="TextChapterSplitter"/> 的切章结果。</para>
/// <para><b>为什么自己解包而不是读阅读缓存</b>：缓存是"第一次打开才建"的，
/// 导入后立刻索引时缓存还不存在；直接读源文件才能保证导入完就能搜。</para>
/// </remarks>
public sealed class ContentTextExtractor
{
    /// <summary>切片长度上限。太小会把一句话切断，太大则 snippet 的定位会偏。</summary>
    internal const int ChunkChars = 800;

    private static readonly Regex TagRegex = new("<[^>]*>", RegexOptions.Compiled);

    private readonly IPdfDocumentService? _pdf;
    private readonly ILogger<ContentTextExtractor> _log;

    public ContentTextExtractor(IPdfDocumentService? pdf, ILogger<ContentTextExtractor> log)
    {
        _pdf = pdf;
        _log = log;
    }

    /// <summary>
    /// 抽取一本书的切片。格式不支持时抛 <c>UnsupportedFormat</c>（索引器据此标 Unsupported），
    /// 文件坏了等其它错误照抛（索引器标 Failed）。
    /// </summary>
    public IReadOnlyList<ContentChunk> Extract(Book book, CancellationToken ct)
    {
        var format = PickFormat(book)
            ?? throw new AppException(
                ErrorCode.UnsupportedFormat,
                "这本书没有可索引正文的格式。",
                technical: $"book={book.Id} formats={string.Join(',', book.Formats.Select(f => f.Format))}");

        return format.Format switch
        {
            BookFormatKind.Epub => ExtractEpub(book.Id, format.FilePath, ct),
            BookFormatKind.Txt => ExtractTxt(book.Id, format.FilePath, ct),
            BookFormatKind.Pdf => ExtractPdf(book.Id, format.FilePath, ct),
            _ => throw new AppException(
                ErrorCode.UnsupportedFormat,
                "这个格式暂不索引正文。",
                technical: format.Format.ToString()),
        };
    }

    /// <summary>挑一个能读出正文的格式：EPUB &gt; TXT &gt; PDF。MOBI/AZW3/CBZ 不索引。</summary>
    private static BookFormat? PickFormat(Book book)
    {
        static int Rank(BookFormatKind kind) => kind switch
        {
            BookFormatKind.Epub => 0,
            BookFormatKind.Txt => 1,
            BookFormatKind.Pdf => 2,
            _ => -1,
        };

        return book.Formats
            .Where(f => !f.Missing && Rank(f.Format) >= 0 && File.Exists(f.FilePath))
            .OrderBy(f => Rank(f.Format))
            .FirstOrDefault();
    }

    // ---------- EPUB ----------

    private List<ContentChunk> ExtractEpub(Guid bookId, string path, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var index = EpubPackageIndex.TryRead(zip, out var reason)
            ?? throw new AppException(
                ErrorCode.UnsupportedFormat,
                "这本 EPUB 的结构读不出来，索引不了正文。",
                technical: reason);

        var chunks = new List<ContentChunk>();

        foreach (var spine in index.Spine)
        {
            ct.ThrowIfCancellationRequested();

            var entry = FindEntry(zip, spine.SourceHref);

            if (entry is null)
            {
                continue;
            }

            var text = ReadEntryText(entry);

            if (text.Length == 0)
            {
                continue;
            }

            foreach (var piece in SplitIntoChunks(text))
            {
                chunks.Add(new ContentChunk
                {
                    BookId = bookId,
                    SpineIndex = spine.Index,
                    // 标题优先用目录里给的那份（与阅读器目录一致），没有就用文件名
                    ChapterTitle = spine.Title,
                    ChapterHref = spine.SourceHref,
                    Body = piece,
                });
            }
        }

        _log.LogInformation("EPUB 正文抽取完成：{BookId} 章 {Chapters} / 切片 {Chunks}",
            bookId, index.Spine.Count, chunks.Count);

        return chunks;
    }

    /// <summary>按包内路径找条目（与 EpubPackageIndex 的规范化口径一致，大小写不敏感）。</summary>
    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string path)
    {
        var wanted = EpubPackageIndex.NormalizeEntryPath(path);

        return zip.Entries.FirstOrDefault(
            e => string.Equals(EpubPackageIndex.NormalizeEntryPath(e.FullName), wanted,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var raw = reader.ReadToEnd();

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
            };

            using var xmlReader = XmlReader.Create(new StringReader(raw), settings);
            var doc = XDocument.Load(xmlReader, LoadOptions.None);
            var body = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "body");

            if (body is not null)
            {
                var builder = new StringBuilder(raw.Length);

                CollectText(body, builder);

                return Normalize(builder.ToString());
            }
        }
        catch (XmlException)
        {
            // 不是合法 XHTML：退回正则剥标签（EPUB 里确实有这种书，不能因此索引不了）
        }

        return Normalize(WebUtility.HtmlDecode(TagRegex.Replace(raw, "\n")));
    }

    /// <summary>递归收文本节点，块级元素前后补换行（保留段落边界，切片与 snippet 才读得通）。</summary>
    private static void CollectText(XElement element, StringBuilder builder)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    builder.Append(text.Value);
                    break;
                case XElement child:
                    var name = child.Name.LocalName;

                    if (name is "script" or "style" or "head" or "svg" or "math")
                    {
                        continue;
                    }

                    var block = name is "p" or "div" or "br" or "li" or "tr" or "blockquote"
                        or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "pre" or "section" or "article";

                    if (block)
                    {
                        builder.Append('\n');
                    }

                    CollectText(child, builder);

                    if (block)
                    {
                        builder.Append('\n');
                    }

                    break;
            }
        }
    }

    // ---------- TXT ----------

    private List<ContentChunk> ExtractTxt(Guid bookId, string path, CancellationToken ct)
    {
        var detected = TextEncodingDetector.Detect(path);
        var encoding = detected.Encoding;
        var bomLength = TextEncodingDetector.BomLengthFor(path, encoding);
        var plan = TextChapterSplitter.Split(path, encoding, bomLength, ct);

        var chunks = new List<ContentChunk>();

        /* 一次顺序流式读完整本：每章各开一次 ReadRange 会把 100 MB 的 TXT 解 N 遍。 */
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (bomLength > 0)
        {
            stream.Position = bomLength;
        }

        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false);

        var buffer = new char[64 * 1024];
        long position = 0;
        var ready = new StringBuilder();

        foreach (var (chapter, chapterIndex) in plan.Chapters.Select((c, i) => (c, i)))
        {
            ct.ThrowIfCancellationRequested();

            ready.Clear();

            while (position < chapter.StartChar)
            {
                var want = (int)Math.Min(buffer.Length, chapter.StartChar - position);
                var got = reader.Read(buffer, 0, want);

                if (got <= 0)
                {
                    break;
                }

                position += got;
            }

            while (position < chapter.EndChar)
            {
                var want = (int)Math.Min(buffer.Length, chapter.EndChar - position);
                var got = reader.Read(buffer, 0, want);

                if (got <= 0)
                {
                    break;
                }

                ready.Append(buffer, 0, got);
                position += got;
            }

            var text = Normalize(ready.ToString());

            if (text.Length == 0)
            {
                continue;
            }

            foreach (var piece in SplitIntoChunks(text))
            {
                chunks.Add(new ContentChunk
                {
                    BookId = bookId,
                    SpineIndex = chapterIndex,
                    ChapterTitle = chapter.Title,
                    ChapterHref = "txt:" + chapter.StartChar + "-" + chapter.EndChar,
                    Body = piece,
                });
            }
        }

        _log.LogInformation("TXT 正文抽取完成：{BookId} 章 {Chapters} / 切片 {Chunks}",
            bookId, plan.Chapters.Count, chunks.Count);

        return chunks;
    }

    // ---------- PDF ----------

    private List<ContentChunk> ExtractPdf(Guid bookId, string path, CancellationToken ct)
    {
        if (_pdf is null)
        {
            throw new AppException(
                ErrorCode.UnsupportedFormat,
                "缺少 PDF 组件，索引不了这本 PDF 的正文。",
                technical: "IPdfDocumentService not registered");
        }

        var pages = _pdf.ReadAllText(path);
        var chunks = new List<ContentChunk>();

        for (var page = 0; page < pages.Count; page++)
        {
            ct.ThrowIfCancellationRequested();

            var text = Normalize(pages[page]);

            if (text.Length == 0)
            {
                continue;
            }

            foreach (var piece in SplitIntoChunks(text))
            {
                chunks.Add(new ContentChunk
                {
                    BookId = bookId,
                    // PDF 的"章"就是页：跳转时 startIndex 当页号用
                    SpineIndex = page,
                    ChapterTitle = "第 " + (page + 1) + " 页",
                    ChapterHref = "pdf:" + page,
                    Body = piece,
                });
            }
        }

        _log.LogInformation("PDF 正文抽取完成：{BookId} 页 {Pages} / 切片 {Chunks}",
            bookId, pages.Count, chunks.Count);

        return chunks;
    }

    // ---------- 共用 ----------

    /// <summary>换行归一 + 空白折叠。保留段落换行，供切片与片段展示使用。</summary>
    internal static string Normalize(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        var pendingSpace = false;
        var newlines = 0;

        foreach (var ch in raw)
        {
            if (ch == '\r')
            {
                continue;
            }

            if (ch == '\n')
            {
                if (newlines < 2)
                {
                    builder.Append('\n');
                }

                newlines++;
                pendingSpace = false;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace && builder.Length > 0 && builder[^1] != '\n')
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            newlines = 0;
            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    /// <summary>把一章的正文按段落打包成不超过 <see cref="ChunkChars"/> 的切片。</summary>
    internal static IEnumerable<string> SplitIntoChunks(string text)
    {
        var start = 0;

        while (start < text.Length)
        {
            var end = Math.Min(start + ChunkChars, text.Length);

            if (end < text.Length)
            {
                /* 先退到最近的换行，退太远（超过半个切片）就退回最近的句号/逗号，都没有就硬切。 */
                var cut = text.LastIndexOf('\n', end - 1, end - start);

                if (cut < start + ChunkChars / 2)
                {
                    cut = Math.Max(
                        text.LastIndexOf('。', end - 1, end - start),
                        text.LastIndexOf('.', end - 1, end - start));
                }

                if (cut >= start + ChunkChars / 2)
                {
                    end = cut + 1;
                }
            }

            var piece = text[start..end].Trim();

            if (piece.Length > 0)
            {
                yield return piece;
            }

            start = end;
        }
    }
}
