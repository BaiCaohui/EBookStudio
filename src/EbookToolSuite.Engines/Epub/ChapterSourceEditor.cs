using System.Text;
using System.Xml;
using System.Xml.Linq;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.IO;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Engines.Epub;

/// <summary>
/// 章节源码编辑器（执行计划 T5-2）：原样读源码、保存前查 XML 良构性、原样写回。
/// </summary>
/// <remarks>
/// 【为什么不顺手格式化】这是源码视图：用户改的就是最终字节。任何自动重排
/// （缩进、换行、编码、自闭合写法）都会把 diff 搞得面目全非，也会破坏
/// 书里刻意写的东西（pre、空白敏感的行内元素、原作者的排版习惯）。
/// </remarks>
public sealed class ChapterSourceEditor
{
    /// <summary>读章节源码：原样返回文本（按 BOM 决定编码，没 BOM 按 UTF-8）。</summary>
    public string ReadSource(string workDir, string entry)
    {
        var path = ResolveInside(workDir, entry);
        var bytes = File.ReadAllBytes(path);
        var (encoding, bomLength) = DetectEncoding(bytes);

        return encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
    }

    /// <summary>
    /// 能不能存：必须是良构 XML，而且得看得见 html 与 body。
    /// 失败时给出第几行第几列和一句人话（判据 3 / 4）。
    /// </summary>
    public ChapterSourceCheck Check(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return new ChapterSourceCheck(false, 0, 0, "empty", "源码不能为空");
        }

        XDocument document;

        try
        {
            document = XDocument.Parse(source);
        }
        catch (XmlException ex)
        {
            var line = ex.LineNumber is > 0 ? ex.LineNumber : 1;
            return new ChapterSourceCheck(false, line, ex.LinePosition, ex.Message, Describe(ex, line, source));
        }

        if (document.Root is null || document.Root.Name.LocalName != "html")
        {
            return new ChapterSourceCheck(false, 1, 1, "root", "章节文件最外层必须是 <html></html>");
        }

        if (!document.Descendants().Any(e => e.Name.LocalName == "body"))
        {
            return new ChapterSourceCheck(false, 1, 1, "body", "章节文件必须有一个 <body>，正文要写在里面");
        }

        return ChapterSourceCheck.Success;
    }

    /// <summary>
    /// 把源码写回工作区。保留原文件的编码、BOM 与换行风格（能少改一个字节就少改一个）。
    /// </summary>
    public void WriteSource(string workDir, string entry, string source)
    {
        var path = ResolveInside(workDir, entry);
        var original = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
        var (encoding, bomLength) = DetectEncoding(original);
        var preamble = bomLength > 0 ? encoding.GetPreamble() : Array.Empty<byte>();
        var text = NormalizeNewLines(source ?? string.Empty, original);
        var body = encoding.GetBytes(text);
        var output = new byte[preamble.Length + body.Length];

        preamble.CopyTo(output, 0);
        body.CopyTo(output, preamble.Length);
        AtomicFile.WriteAllBytes(path, output);
    }

    /// <summary>包内条目路径 -> 工作区内的磁盘路径；越界直接抛（压缩包有非法路径）。</summary>
    private static string ResolveInside(string workDir, string entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);

        var root = Path.GetFullPath(workDir);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar)));

        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(ErrorCode.UnsafeArchiveEntry, "压缩包有非法路径", entry);
        }

        return candidate;
    }

    private static (Encoding Encoding, int BomLength) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        // 注意：GetPreamble() 只有在构造时允许 BOM 才返回字节。
        // 这里必须按检出的 BOM 造编码，否则"带 BOM 的文件写回后 BOM 会丢"（单测抓到过）。
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), 2);
        }

        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0);
    }

    /// <summary>
    /// 换行风格按原文件多数决还原：编辑器里的换行是 LF，而原文件很可能是 CRLF，
    /// 直接写回去会把每一行都改掉（T3-2 踩过同一个坑）。
    /// </summary>
    private static string NormalizeNewLines(string source, byte[] original)
    {
        var text = source.Replace("\r\n", "\n").Replace('\r', '\n');

        if (original.Length == 0)
        {
            return text;
        }

        var raw = Encoding.UTF8.GetString(original);
        var crlf = CountOf(raw, "\r\n");
        var lf = CountOf(raw, "\n") - crlf;

        return crlf > lf ? text.Replace("\n", "\r\n") : text;
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>
    /// 扫一遍标签配对，把没闭合的标签（连同它开在哪一行）找出来。
    /// 只做"给人看的提示"，不参与判断合法性：真正的判断始终是 XDocument.Parse。
    /// </summary>
    private static List<(string Name, int Line)> FindUnclosedTags(string source)
    {
        var stack = new List<(string Name, int Line)>();
        var unclosed = new List<(string Name, int Line)>();
        var line = 1;
        var index = 0;

        while (index < source.Length)
        {
            if (source[index] == '\n')
            {
                line++;
                index++;
                continue;
            }

            if (source[index] != '<')
            {
                index++;
                continue;
            }

            var span = source.AsSpan(index);

            // 注释 / CDATA / DOCTYPE / 处理指令：整段跳过
            if (span.StartsWith("<!--"))
            {
                index = SkipTo(source, index, "-->", 3, ref line);
                continue;
            }

            if (span.StartsWith("<![CDATA["))
            {
                index = SkipTo(source, index, "]]>", 3, ref line);
                continue;
            }

            if (span.StartsWith("<!"))
            {
                index = SkipTo(source, index, ">", 1, ref line);
                continue;
            }

            if (span.StartsWith("<?"))
            {
                index = SkipTo(source, index, "?>", 2, ref line);
                continue;
            }

            var close = source.IndexOf('>', index);

            if (close < 0)
            {
                break;
            }

            var startLine = line;
            var raw = source[(index + 1)..close].Trim();

            for (var i = index; i <= close; i++)
            {
                if (source[i] == '\n')
                {
                    line++;
                }
            }

            index = close + 1;

            if (raw.Length == 0)
            {
                continue;
            }

            if (raw[0] == '/')
            {
                var closing = TagNameOf(raw[1..]);
                var at = stack.FindLastIndex(entry => string.Equals(entry.Name, closing, StringComparison.OrdinalIgnoreCase));

                if (at >= 0)
                {
                    // 夹在这个结束标签"下面"没关上的，正是问题所在（比如 </body> 上面挂着的两个 <div>）
                    for (var i = stack.Count - 1; i > at; i--)
                    {
                        Remember(unclosed, stack[i]);
                    }

                    stack.RemoveRange(at, stack.Count - at);
                }

                continue;
            }

            if (raw[^1] == '/')
            {
                continue;   // 自闭合
            }

            stack.Add((TagNameOf(raw), startLine));
        }

        // 一直到文件结束都没关上的，也算
        foreach (var entry in stack)
        {
            Remember(unclosed, entry);
        }

        return unclosed.OrderBy(entry => entry.Line).ToList();
    }

    private static void Remember(List<(string Name, int Line)> list, (string Name, int Line) entry)
    {
        if (!list.Any(item => item.Line == entry.Line
            && string.Equals(item.Name, entry.Name, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(entry);
        }
    }

    private static int SkipTo(string source, int from, string terminator, int terminatorLength, ref int line)
    {
        var end = source.IndexOf(terminator, from, StringComparison.Ordinal);
        var stop = end < 0 ? source.Length : end + terminatorLength;

        for (var i = from; i < stop; i++)
        {
            if (source[i] == '\n')
            {
                line++;
            }
        }

        return stop;
    }

    private static string TagNameOf(string raw)
    {
        var end = raw.IndexOfAny([' ', '\t', '\r', '\n', '/']);

        return end < 0 ? raw : raw[..end];
    }

    /// <summary>常见错误翻译成人话（计划 T5-2 的对照表）。</summary>
    private static string Describe(XmlException ex, int line, string source)
    {
        var message = ex.Message;

        if (message.Contains("does not match the end tag of", StringComparison.OrdinalIgnoreCase))
        {
            // XML 解析器只在"发现处"报错（常常已经跑到 </body>），
            // 所以这里自己扫一遍标签配对，直接告诉用户是哪几个标签没关、开在哪一行。
            var unclosed = FindUnclosedTags(source);

            if (unclosed.Count > 0)
            {
                var names = string.Join(
                    "、",
                    unclosed.Select(entry => "第 " + entry.Line + " 行的 <" + entry.Name + ">"));

                return "有 " + unclosed.Count + " 个标签没有闭合：" + names
                    + "。解析器要到后面才发现（它报的是「发现处」，不是「犯错处」）：把对应的结束标签补上再保存。"
                    + "如果是空标签（<br>、<img>、<hr>、<meta>），要写成 <br/>、<img/>、<hr/>、<meta/>";
            }

            return "第 " + line + " 行的标签没有正确闭合：HTML 里的空标签（<br>、<img>、<hr>、<meta>）要写成 <br/>、<img/>、<hr/>、<meta/>";
        }

        if (message.Contains("EntityName", StringComparison.OrdinalIgnoreCase)
            || message.Contains("undeclared entity", StringComparison.OrdinalIgnoreCase))
        {
            return "第 " + line + " 行有裸露的 & ：正文里的 & 要写成 &amp;（< 写 &lt;，> 写 &gt;）";
        }

        if (message.Contains("Unexpected end of file", StringComparison.OrdinalIgnoreCase))
        {
            return "文件在这里就结束了，标签没闭合：检查是不是漏了 </p>、</div>、</body> 之类的结束标签";
        }

        if (message.Contains("invalid name character", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Name cannot begin with", StringComparison.OrdinalIgnoreCase))
        {
            return "第 " + line + " 行有不合法的标签或属性名（多半是正文里的尖括号没转义）";
        }

        return "第 " + line + " 行附近有 XML 语法错误：" + message;
    }
}
