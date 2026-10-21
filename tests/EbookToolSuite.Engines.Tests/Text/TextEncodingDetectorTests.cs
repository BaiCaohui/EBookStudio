using System.Text;
using EbookToolSuite.Engines.Text;

namespace EbookToolSuite.Engines.Tests.Text;

/// <summary>
/// T4-5 判据 1：UTF-8 / UTF-8 BOM / UTF-16 / GBK / GB18030 五种编码都要识别对。
/// </summary>
public sealed class TextEncodingDetectorTests : IDisposable
{
    private const string Sample =
        "第一章 引子\n这是用来测编码识别的一段中文正文，里面要有汉字、标点、以及 ASCII 混排 abc123。\n";

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ets-txt-detect-" + Guid.NewGuid().ToString("N"));

    public TextEncodingDetectorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响测试结论
        }
    }

    [Fact]
    public void UTF8_无BOM_识别为UTF8且正文一致()
    {
        var path = WriteEncoded("utf8.txt", Sample, new UTF8Encoding(false), bom: false);

        var result = TextEncodingDetector.Detect(path);

        Assert.Equal("UTF-8", result.Name);
        Assert.Equal(0, result.BomLength);
        Assert.True(result.Confident);
        Assert.Equal(Sample.Trim(), TextEncodingDetector.ReadPreview(path, result, 200));
    }

    [Fact]
    public void UTF8_有BOM_识别为UTF8且BOM长度是3()
    {
        var path = WriteEncoded("utf8bom.txt", Sample, new UTF8Encoding(true), bom: true);

        var result = TextEncodingDetector.Detect(path);

        Assert.Equal("UTF-8", result.Name);
        Assert.Equal(3, result.BomLength);
        Assert.True(result.Confident);
        Assert.Equal(Sample, result.Text);
        Assert.Equal(Sample.Trim(), TextEncodingDetector.ReadPreview(path, result, 200));
    }

    [Fact]
    public void UTF16LE_有BOM_识别为UTF16LE()
    {
        var path = WriteEncoded("utf16le.txt", Sample, new UnicodeEncoding(false, true), bom: true);

        var result = TextEncodingDetector.Detect(path);

        Assert.Equal("UTF-16LE", result.Name);
        Assert.Equal(2, result.BomLength);
        Assert.Equal(Sample, result.Text);
        Assert.Equal(Sample.Trim(), TextEncodingDetector.ReadPreview(path, result, 200));
    }

    [Fact]
    public void UTF16BE_有BOM_识别为UTF16BE()
    {
        var path = WriteEncoded("utf16be.txt", Sample, new UnicodeEncoding(true, true), bom: true);

        var result = TextEncodingDetector.Detect(path);

        Assert.Equal("UTF-16BE", result.Name);
        Assert.Equal(Sample, result.Text);
    }

    [Fact]
    public void GBK_识别为GBK且正文一致()
    {
        TextEncodingSupport.EnsureRegistered();

        var gbk = Encoding.GetEncoding(936);
        var path = WriteEncoded("gbk.txt", Sample, gbk, bom: false);

        var result = TextEncodingDetector.Detect(path);

        Assert.Equal("GBK", result.Name);
        Assert.Equal(Sample, result.Text);
        Assert.Equal(Sample.Trim(), TextEncodingDetector.ReadPreview(path, result, 200));
    }

    [Fact]
    public void GB18030_含四字节字符_识别为GB18030()
    {
        TextEncodingSupport.EnsureRegistered();

        // U+20000 这类扩展 B 区汉字只有 GB18030 的四字节序列能表示
        var text = "第一章 生僻字\n扩展汉字：\u20000 \u20001，以及常见汉字混排。\n";
        var gb18030 = Encoding.GetEncoding(54936);
        var path = WriteEncoded("gb18030.txt", text, gb18030, bom: false);

        var result = TextEncodingDetector.Detect(path);

        Assert.Equal("GB18030", result.Name);
        Assert.Contains("\u20000", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 纯ASCII_识别为UTF8()
    {
        var path = Path.Combine(_dir, "ascii.txt");
        File.WriteAllText(path, "Chapter 1\nHello world, plain ascii only.\n", new UTF8Encoding(false));

        var result = TextEncodingDetector.Detect(path);

        Assert.Equal("UTF-8", result.Name);
        Assert.True(result.Confident);
    }

    [Fact]
    public void 空文件_给UTF8且不抛异常()
    {
        var path = Path.Combine(_dir, "empty.txt");
        File.WriteAllBytes(path, Array.Empty<byte>());

        var result = TextEncodingDetector.Detect(path);

        Assert.Equal("UTF-8", result.Name);
        Assert.Empty(result.Text);
    }

    [Fact]
    public void 手动指定编码_常用名字都能取到且不认识的名字返回null()
    {
        Assert.Equal(54936, TextEncodingSupport.Resolve("GB18030")!.CodePage);
        Assert.Equal(936, TextEncodingSupport.Resolve("gbk")!.CodePage);
        Assert.Equal("UTF-8", TextEncodingSupport.Resolve("utf8")!.WebName.ToUpperInvariant());
        Assert.Equal("UTF-16LE", TextEncodingSupport.Normalize("utf_16le"));
        Assert.Equal("UTF-16BE", TextEncodingSupport.Normalize("UTF-16BE"));

        // 2026-10-20 扩表：这些名字都要能解析（用户给的编码下拉清单）
        Assert.Equal(950, TextEncodingSupport.Resolve("big5")!.CodePage);
        Assert.Equal(932, TextEncodingSupport.Resolve("ShiftJIS")!.CodePage);
        Assert.Equal(1252, TextEncodingSupport.Resolve("cp1252")!.CodePage);
        Assert.Equal(28591, TextEncodingSupport.Resolve("ISO8859-1")!.CodePage);
        Assert.Equal(20866, TextEncodingSupport.Resolve("koi8-r")!.CodePage);
        Assert.Equal("UTF-8-BOM", TextEncodingSupport.Normalize("utf8bom"));
        Assert.Equal("ISO-8859-15", TextEncodingSupport.Normalize("iso_8859_15"));

        Assert.Null(TextEncodingSupport.Resolve("不存在的编码"));
    }

    [Fact]
    public void 大于采样长度_且采样边界正落在汉字中间_仍然识别为UTF8()
    {
        /* 回归（2026-10-09）：256 KB 采样边界落在三字节汉字中间时，
           第一版会把那个完整字符切残 -> 严格 UTF-8 判定失败 -> 整个文件被当成 GBK。
           这里造 300000 个汉字（约 900 KB），采样边界必然落在某个汉字中间。 */
        var text = string.Concat(Enumerable.Repeat("中", 300000));
        var path = Path.Combine(_dir, "boundary.txt");

        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));

        var result = TextEncodingDetector.Detect(path);

        Assert.Equal("UTF-8", result.Name);
        Assert.True(result.Confident);
    }

    [Fact]
    public void 编码下拉顺序_与用户给的清单一致()
    {
        var names = TextEncodingSupport.SupportedNames;

        Assert.Equal(
            new[]
            {
                "UTF-8", "UTF-8-BOM", "UTF-16BE", "UTF-16LE", "UTF-32BE", "UTF-32LE",
                "GB2312", "GB18030", "GBK", "Big5", "Big5-HKSCS", "CESU-8",
                "EUC-JP", "EUC-KR", "IBM866", "IBM850", "ISO-2022-JP", "ISO-2022-KR",
            },
            names.Take(18).ToArray());

        // 用户图里的 ISO-8859 只有 1…10 与 13/14/15；
        // 但 .NET 10 运行时的代码页表里没有 ISO-8859-10 / -14（实测 NotSupportedException），
        // 所以这两个会自动从下拉里剔除，其余都在。
        Assert.Contains("ISO-8859-9", names);
        Assert.Contains("ISO-8859-13", names);
        Assert.Contains("ISO-8859-15", names);
        Assert.DoesNotContain("ISO-8859-10", names);
        Assert.DoesNotContain("ISO-8859-14", names);
        Assert.DoesNotContain("ISO-8859-11", names);
        Assert.DoesNotContain("ISO-8859-12", names);
        Assert.DoesNotContain("ISO-8859-16", names);

        var koi = names.ToList().IndexOf("KOI8-R");
        Assert.True(koi > 0);
        Assert.Equal("KOI8-U", names[koi + 1]);
        Assert.Equal("ShiftJIS", names[koi + 2]);
        Assert.Equal("Windows-1250", names[koi + 3]);
        Assert.Equal("Windows-1258", names[^1]);
    }

    [Fact]
    public void UTF32带BOM_识别为UTF32而不是UTF16()
    {
        foreach (var (name, encode) in new (string, Func<Encoding>)[]
        {
            ("UTF-32LE", () => new UTF32Encoding(bigEndian: false, byteOrderMark: true)),
            ("UTF-32BE", () => new UTF32Encoding(bigEndian: true, byteOrderMark: true)),
        })
        {
            var encoding = encode();
            var path = Path.Combine(_dir, "utf32-" + name + ".txt");
            var body = encoding.GetBytes("第一章");
            var preamble = encoding.GetPreamble();
            var all = new byte[preamble.Length + body.Length];

            Array.Copy(preamble, all, preamble.Length);
            Array.Copy(body, 0, all, preamble.Length, body.Length);
            File.WriteAllBytes(path, all);

            var result = TextEncodingDetector.Detect(path);

            Assert.Equal(name, result.Name);
            Assert.Equal(4, result.BomLength);
            Assert.StartsWith("第一章", result.Text, StringComparison.Ordinal);
        }
    }

    private string WriteEncoded(string name, string text, Encoding encoding, bool bom)
    {
        var path = Path.Combine(_dir, name);
        var bytes = encoding.GetBytes(text);

        if (bom)
        {
            var preamble = encoding.GetPreamble();
            var all = new byte[preamble.Length + bytes.Length];

            Array.Copy(preamble, all, preamble.Length);
            Array.Copy(bytes, 0, all, preamble.Length, bytes.Length);
            File.WriteAllBytes(path, all);
        }
        else
        {
            File.WriteAllBytes(path, bytes);
        }

        return path;
    }
}
