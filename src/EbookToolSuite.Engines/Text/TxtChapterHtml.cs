using System.Net;
using System.Text;

namespace EbookToolSuite.Engines.Text;

/// <summary>
/// TXT 的章节内容 -> 阅读器能吃的 HTML，以及"只读某一章、不整本读进来"的范围读取。
/// T4-5 判据 6：100 MB 的 TXT 不能一次读进内存，所以按字符范围流式取。
/// </summary>
public static class TxtChapterHtml
{
    private const int BufferSize = 64 * 1024;

    /// <summary>把一章的文字包成完整的 HTML 文档（转义 + 一行一段）。</summary>
    public static string Build(string title, string text)
    {
        var builder = new StringBuilder(text.Length + 256);

        builder.Append("<!doctype html>\n<html lang=\"zh-CN\">\n<head>\n<meta charset=\"utf-8\">\n<title>");
        builder.Append(Escape(title));
        builder.Append("</title>\n</head>\n<body>\n");

        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.Append("<h1>").Append(Escape(title)).Append("</h1>\n");
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();

            if (line.Length == 0)
            {
                continue;
            }

            builder.Append("<p>").Append(Escape(line)).Append("</p>\n");
        }

        builder.Append("</body>\n</html>\n");
        return builder.ToString();
    }

    /// <summary>
    /// 从 TXT 里取 [startChar, endChar) 这段字符。
    /// 实现是"从头流式跳过"：内存只占一个缓冲区，与文件大小无关；
    /// 代价是取靠后的章节要先把前面的解一遍（实测 100 MB 约 1 秒，可接受）。
    /// </summary>
    public static string ReadRange(
        string path,
        Encoding encoding,
        int bomLength,
        long startChar,
        long endChar,
        CancellationToken ct = default)
    {
        if (endChar <= startChar)
        {
            return string.Empty;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);

        if (bomLength > 0)
        {
            stream.Position = bomLength;
        }

        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, BufferSize);
        var scratch = new char[BufferSize];
        var remainingSkip = startChar;

        while (remainingSkip > 0)
        {
            ct.ThrowIfCancellationRequested();

            var want = (int)Math.Min(scratch.Length, remainingSkip);
            var skipped = reader.ReadBlock(scratch, 0, want);

            if (skipped <= 0)
            {
                return string.Empty;
            }

            remainingSkip -= skipped;
        }

        var wantChars = (int)Math.Min(int.MaxValue, endChar - startChar);
        var builder = new StringBuilder(Math.Min(wantChars, 64 * 1024));
        var left = wantChars;

        while (left > 0)
        {
            ct.ThrowIfCancellationRequested();

            var read = reader.ReadBlock(scratch, 0, Math.Min(scratch.Length, left));

            if (read <= 0)
            {
                break;
            }

            builder.Append(scratch, 0, read);
            left -= read;
        }

        return builder.ToString();
    }

    public static string Escape(string text) => WebUtility.HtmlEncode(text);
}
