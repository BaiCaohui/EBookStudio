using System.Text;

namespace EbookToolSuite.Engines.Text;

/// <summary>编码识别的结论。</summary>
/// <param name="Encoding">判定的编码。</param>
/// <param name="Name">界面上的标准名（UTF-8 / UTF-16LE / UTF-16BE / GBK / GB18030）。</param>
/// <param name="BomLength">开头的 BOM 字节数（解码正文时要跳过）。</param>
/// <param name="Confident">false = 识别没把握，界面要提示"编码可能不准，请选择"并给预览（判据 2）。</param>
/// <param name="Reason">判定依据，写日志与详情用。</param>
public sealed record TextEncodingResult(
    Encoding Encoding,
    string Name,
    int BomLength,
    bool Confident,
    string Reason)
{
    /// <summary>采样解码出来的正文（判定时的副产品，用来做预览与自检）。</summary>
    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// TXT 编码识别（T4-5，设计文档 6.2 的顺序）：
/// BOM -&gt; 严格 UTF-8 -&gt; 无 BOM 的 UTF-16 启发式 -&gt; GB18030 兜底（并按 4 字节序列区分 GBK / GB18030）。
/// </summary>
/// <remarks>
/// 【与计划的一处偏离，已记入 DECISIONS】计划第 3 步写的是"UTF.Unknown 包"，
/// 但本机离线环境里没有这个 NuGet 包（缓存里没有、也没网），所以用一个等价的启发式代替：
/// 严格 UTF-8 失败后再看"是不是 UTF-16 的字节分布"，最后落到 GB18030，
/// 并给低置信度打标记（界面据此提示用户手选）。
/// </remarks>
public static class TextEncodingDetector
{
    /// <summary>一次读多少字节做判定。</summary>
    public const int SampleBytes = 256 * 1024;

    /// <summary>采样全是 ASCII 时往后多看一些，尽量撞到一个非 ASCII 字节。</summary>
    private const int AsciiExtendBytes = 4 * 1024 * 1024;

    static TextEncodingDetector()
    {
        TextEncodingSupport.EnsureRegistered();
    }

    public static IReadOnlyList<string> SupportedNames => TextEncodingSupport.SupportedNames;

    public static TextEncodingResult Detect(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        return Detect(stream);
    }

    public static TextEncodingResult Detect(Stream stream)
    {
        TextEncodingSupport.EnsureRegistered();

        var bom = ReadBom(stream);

        if (bom.Name.Length > 0)
        {
            // BOM 已经确定是哪种编码了，顺手把采样文字解出来（界面预览、日志自检都要用）
            stream.Position = bom.Length;
            var bomText = ReadText(stream, bom.Encoding!, SampleBytes);

            return new TextEncodingResult(bom.Encoding!, bom.Name, bom.Length, Confident: true, "有 BOM")
            {
                Text = bomText,
            };
        }

        var sample = ReadSample(stream, SampleBytes);

        if (sample.Length == 0)
        {
            return new TextEncodingResult(
                new UTF8Encoding(false), TextEncodingSupport.Utf8Name, 0, Confident: true, "空文件");
        }

        // 全是 ASCII 时再多读一点：ASCII 在 UTF-8 / GBK / GB18030 里完全一样，
        // 但"文件后半段是 GBK"这种情况只有再往后看一眼才敢下结论。
        if (!HasNonAscii(sample) && sample.Length == SampleBytes)
        {
            stream.Position = 0;
            sample = ReadSample(stream, AsciiExtendBytes);

            if (!HasNonAscii(sample))
            {
                return new TextEncodingResult(
                    new UTF8Encoding(false), TextEncodingSupport.Utf8Name, 0, Confident: true,
                    "纯 ASCII（三种编码解码结果一致）");
            }
        }

        // 1) 严格 UTF-8：乱一个字节就判定不是 UTF-8
        if (IsStrictUtf8(sample, out var utf8Text))
        {
            return new TextEncodingResult(
                new UTF8Encoding(false), TextEncodingSupport.Utf8Name, 0, Confident: true, "严格 UTF-8 校验通过")
            {
                Text = utf8Text,
            };
        }

        // 2) 无 BOM 的 UTF-16：看 0x00 的分布
        if (LooksLikeUtf16(sample, out var bigEndian))
        {
            var encoding = new UnicodeEncoding(bigEndian, byteOrderMark: false);

            return new TextEncodingResult(
                encoding,
                bigEndian ? TextEncodingSupport.Utf16BeName : TextEncodingSupport.Utf16LeName,
                0,
                Confident: true,
                "无 BOM，但 NUL 字节呈 UTF-16 分布")
            {
                Text = encoding.GetString(sample),
            };
        }

        // 3) 兜底：GB18030（GBK 的超集）。有 4 字节序列才敢叫 GB18030。
        var gb18030 = Encoding.GetEncoding(TextEncodingSupport.Gb18030CodePage);
        var text = gb18030.GetString(sample);
        var isGb18030 = HasFourByteGb18030(sample);
        var messy = LooksLikeMojibake(text);

        return new TextEncodingResult(
            gb18030,
            isGb18030 ? TextEncodingSupport.Gb18030Name : TextEncodingSupport.GbkName,
            0,
            Confident: !messy,
            isGb18030 ? "有 GB18030 四字节序列" : (messy ? "不认识，按 GBK 兜底（疑似乱码）" : "非 UTF-8，按 GBK 解"))
        {
            Text = text,
        };
    }

    /// <summary>读前 N 个字符做预览（判据 2 的"前 500 字预览"）。</summary>
    public static string ReadPreview(string path, TextEncodingResult result, int maxChars = 500)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);

        if (result.BomLength > 0)
        {
            stream.Position = result.BomLength;
        }

        using var reader = new StreamReader(stream, result.Encoding, detectEncodingFromByteOrderMarks: false, 64 * 1024);
        var buffer = new char[Math.Max(1, maxChars)];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);

        return new string(buffer, 0, read).Replace("\r\n", "\n").Trim();
    }

    /// <summary>从当前位置读最多 maxChars 个字符（BOM 分支填 Text 用）。</summary>
    private static string ReadText(Stream stream, Encoding encoding, int maxChars)
    {
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, 64 * 1024, leaveOpen: true);
        var buffer = new char[maxChars];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);

        return new string(buffer, 0, read);
    }

    /// <summary>
    /// 指定编码下，这个文件开头的 BOM 有多长（没有或与编码不符就是 0）。
    /// 用户手选编码后必须按"那个编码的 BOM"来跳，不能沿用自动识别的结论。
    /// </summary>
    public static int BomLengthFor(string path, Encoding encoding)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
        var bom = ReadBom(stream);

        if (bom.Length == 0 || bom.Encoding is null)
        {
            return 0;
        }

        return bom.Encoding.CodePage == encoding.CodePage ? bom.Length : 0;
    }

    private static (Encoding? Encoding, string Name, int Length) ReadBom(Stream stream)
    {
        var head = new byte[4];
        var read = stream.Read(head, 0, head.Length);
        stream.Position = 0;

        if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            return (new UTF8Encoding(true), TextEncodingSupport.Utf8Name, 3);
        }

        /* UTF-32 的 BOM 必须排在 UTF-16 之前判：FF FE 00 00 前两字节和 UTF-16LE 一样，
           先判 UTF-16 会把只剩 0x00 的 UTF-32 文件当成 UTF-16（用户 2026-10-20 要求编码下拉里
           有 UTF-32，选了它就得能正确跳过 BOM）。 */
        if (read >= 4 && head[0] == 0xFF && head[1] == 0xFE && head[2] == 0x00 && head[3] == 0x00)
        {
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true), TextEncodingSupport.Utf32LeName, 4);
        }

        if (read >= 4 && head[0] == 0x00 && head[1] == 0x00 && head[2] == 0xFE && head[3] == 0xFF)
        {
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), TextEncodingSupport.Utf32BeName, 4);
        }

        if (read >= 2 && head[0] == 0xFF && head[1] == 0xFE)
        {
            return (new UnicodeEncoding(false, true), TextEncodingSupport.Utf16LeName, 2);
        }

        if (read >= 2 && head[0] == 0xFE && head[1] == 0xFF)
        {
            return (new UnicodeEncoding(true, true), TextEncodingSupport.Utf16BeName, 2);
        }

        return (null, string.Empty, 0);
    }

    private static byte[] ReadSample(Stream stream, int maxBytes)
    {
        var buffer = new byte[maxBytes];
        var total = 0;

        while (total < maxBytes)
        {
            var read = stream.Read(buffer, total, maxBytes - total);

            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        if (total == buffer.Length)
        {
            return buffer;
        }

        var trimmed = new byte[total];
        Array.Copy(buffer, trimmed, total);
        return trimmed;
    }

    private static bool HasNonAscii(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            if (b >= 0x80)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>严格 UTF-8：不允许非法字节序列。</summary>
    private static bool IsStrictUtf8(byte[] bytes, out string text)
    {
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

            // 采样可能把一个多字节字符截成两半，末尾最多 3 个字节不参与校验。
            var usable = TrimIncompleteTail(bytes);
            text = strict.GetString(bytes, 0, usable);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    /* 采样可能把一个多字节字符切成两半，校验前要把那半个字符去掉。
     *
     * 【2026-10-09 踩过的坑】第一版是"从末尾往前数续字节（10xxxxxx）"，结果对
     * "... E4 B8 AD"（一个完整的三字节汉字）会数到首字节 E4 时算出 back=3 < need=3
     * 不成立却又多减了两位，把完整字符切残 -> 严格 UTF-8 校验误判失败 -> 整个文件被当成 GBK。
     * 症状很隐蔽：小文件（末尾是换行，ASCII）全都正常，只有大于采样长度、且采样边界
     * 正好落在汉字中间的文件才会中招（100 MB 的用例就是这么抓出来的）。
     * 现在改成"从末尾往前找首字节，再判断这个字符是否完整"。
     */
    private static int TrimIncompleteTail(byte[] bytes)
    {
        var end = bytes.Length;

        for (var back = 1; back <= 4 && back <= end; back++)
        {
            var b = bytes[end - back];

            if ((b & 0x80) == 0)
            {
                return end;             // 末尾是 ASCII，本来就是一个完整字符
            }

            if ((b & 0xC0) == 0xC0)
            {
                var need = (b & 0xE0) == 0xC0 ? 2
                    : (b & 0xF0) == 0xE0 ? 3
                    : (b & 0xF8) == 0xF0 ? 4
                    : 0;

                // 首字节非法，或者这个字符没凑齐 -> 把不完整的尾巴去掉
                return need == 0 || back < need ? end - back : end;
            }

            // 续字节（10xxxxxx）：继续往前找首字节
        }

        return end;
    }

    /// <summary>无 BOM 的 UTF-16 启发式：ASCII 字符会带一个 0x00（奇数位 LE / 偶数位 BE）。</summary>
    private static bool LooksLikeUtf16(byte[] bytes, out bool bigEndian)
    {
        bigEndian = false;

        if (bytes.Length < 16)
        {
            return false;
        }

        var even = 0;
        var odd = 0;

        for (var i = 0; i + 1 < bytes.Length; i += 2)
        {
            if (bytes[i] == 0)
            {
                even++;
            }

            if (bytes[i + 1] == 0)
            {
                odd++;
            }
        }

        var pairs = bytes.Length / 2;
        var threshold = pairs / 4;

        if (odd > threshold && even <= pairs / 16)
        {
            return true;    // 全是"字符 + 0x00" -> UTF-16LE（中文书里汉字占多数，NUL 会少一些，所以阈值取 1/4）
        }

        if (even > threshold && odd <= pairs / 16)
        {
            bigEndian = true;
            return true;    // 0x00 + 字符 -> UTF-16BE
        }

        return false;
    }

    /// <summary>GB18030 的四字节序列：81-FE / 30-39 / 81-FE / 30-39。</summary>
    private static bool HasFourByteGb18030(byte[] bytes)
    {
        for (var i = 0; i + 3 < bytes.Length; i++)
        {
            if (bytes[i] >= 0x81 && bytes[i] <= 0xFE
                && bytes[i + 1] >= 0x30 && bytes[i + 1] <= 0x39
                && bytes[i + 2] >= 0x81 && bytes[i + 2] <= 0xFE
                && bytes[i + 3] >= 0x30 && bytes[i + 3] <= 0x39)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>解码结果像不像乱码：几乎全是生僻/私用区字符就认为像。</summary>
    private static bool LooksLikeMojibake(string text)
    {
        if (text.Length < 32)
        {
            return false;
        }

        var suspicious = 0;
        var meaningful = 0;

        foreach (var ch in text)
        {
            if (ch < 0x80 || char.IsWhiteSpace(ch))
            {
                meaningful++;
                continue;
            }

            if (IsCjk(ch) || IsCjkPunctuation(ch) || (ch >= 0xFF00 && ch <= 0xFFEF))
            {
                meaningful++;
                continue;
            }

            if ((ch >= 0xE000 && ch <= 0xF8FF) || (ch >= 0x0100 && ch <= 0x02AF))
            {
                suspicious++;
                continue;
            }

            meaningful++;
        }

        var total = suspicious + meaningful;

        return total > 0 && suspicious * 100 / total >= 20;
    }

    private static bool IsCjk(char ch)
        => (ch >= 0x4E00 && ch <= 0x9FFF) || (ch >= 0x3400 && ch <= 0x4DBF);

    private static bool IsCjkPunctuation(char ch)
        => (ch >= 0x3000 && ch <= 0x303F) || (ch >= 0x2000 && ch <= 0x206F);
}
