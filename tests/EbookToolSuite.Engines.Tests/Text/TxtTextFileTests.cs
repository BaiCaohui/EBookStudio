using System.Text;
using EbookToolSuite.Engines.Text;

namespace EbookToolSuite.Engines.Tests.Text;

/// <summary>
/// 用户 2026-10-20 要求：TXT 要能像记事本一样直接编辑**全文**。
/// 这里钉死编辑保存的两条底线：
///   1) 编码 / BOM / 换行符**原样写回**（改一段文字不该把 UTF-8 变 GBK、也不该弄丢 BOM）；
///   2) 走原子写（不留半截文件、不留 .part）。
/// </summary>
public sealed class TxtTextFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ets-txt-file-" + Guid.NewGuid().ToString("N"));

    public TxtTextFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void UTF8带BOM_读时跳过BOM_保存后BOM还在()
    {
        var text = "第一章 开端\r\n正文甲。\r\n";
        var path = Path.Combine(_dir, "bom.txt");
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var body = new UTF8Encoding(false).GetBytes(text);
        File.WriteAllBytes(path, bom.Concat(body).ToArray());

        var document = TxtTextFile.Load(path);

        Assert.True(document.HasBom);
        Assert.Equal("UTF-8", document.EncodingName);
        Assert.Equal(text, document.Text);                  // BOM 没混进正文

        TxtTextFile.Save(document, "第一章 开端\r\n正文甲（改过）。\r\n");

        var written = File.ReadAllBytes(path);

        Assert.True(written.Length >= 3);
        Assert.Equal(bom, written.Take(3).ToArray());
        Assert.Equal("第一章 开端\r\n正文甲（改过）。\r\n", new UTF8Encoding(false).GetString(written, 3, written.Length - 3));
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
    }

    [Fact]
    public void GBK无BOM_保存后仍是GBK且没有BOM()
    {
        var text = "第一章 开端\r\n正文甲。\r\n第二章 承接\r\n正文乙。\r\n";
        var gbk = TextEncodingSupport.Resolve(TextEncodingSupport.GbkName)!;
        var path = Path.Combine(_dir, "gbk.txt");
        File.WriteAllBytes(path, gbk.GetBytes(text));

        var document = TxtTextFile.Load(path, TextEncodingSupport.GbkName);

        Assert.False(document.HasBom);
        Assert.Equal("GBK", document.EncodingName);
        Assert.Equal(text, document.Text);

        TxtTextFile.Save(document, text.Replace("正文乙", "正文乙（改过）", StringComparison.Ordinal));

        var written = File.ReadAllBytes(path);

        Assert.False(written.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal(
            text.Replace("正文乙", "正文乙（改过）", StringComparison.Ordinal),
            gbk.GetString(written));
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
    }

    [Fact]
    public void 换行符原样保留_LF不会被改成CRLF()
    {
        var text = "第一章 开端\n正文甲。\n第二章 承接\n正文乙。\n";
        var path = Path.Combine(_dir, "lf.txt");
        File.WriteAllText(path, text, new UTF8Encoding(false));

        var document = TxtTextFile.Load(path);
        TxtTextFile.Save(document, document.Text + "尾部新增一行\n");

        var written = File.ReadAllText(path, new UTF8Encoding(false));

        Assert.Contains("正文乙。\n尾部新增一行\n", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n", written, StringComparison.Ordinal);
    }

    [Fact]
    public void 指定的编码优先于自动识别()
    {
        // 同一串字节：按 UTF-8 读是中文，按 GBK 读是乱码；显式传入就按 GBK 读
        var path = Path.Combine(_dir, "override.txt");
        File.WriteAllText(path, "第一章 开端\n正文。\n", new UTF8Encoding(false));

        var utf8 = TxtTextFile.Load(path);
        var gbk = TxtTextFile.Load(path, TextEncodingSupport.GbkName);

        Assert.Equal("UTF-8", utf8.EncodingName);
        Assert.Equal("GBK", gbk.EncodingName);
        Assert.NotEqual(utf8.Text, gbk.Text);
        Assert.StartsWith("第一章", utf8.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 空文件也能读_保存后变成有内容的文件()
    {
        var path = Path.Combine(_dir, "empty.txt");
        File.WriteAllBytes(path, Array.Empty<byte>());

        var document = TxtTextFile.Load(path);

        Assert.Equal(string.Empty, document.Text);

        TxtTextFile.Save(document, "新写的一句话");

        Assert.Equal("新写的一句话", File.ReadAllText(path, document.Encoding));
    }

    [Fact]
    public void 选UTF8BOM保存_文件开头会写BOM()
    {
        var path = Path.Combine(_dir, "utf8bom.txt");
        File.WriteAllText(path, "第一章 开端\n正文。\n", new UTF8Encoding(false));

        var document = TxtTextFile.Load(path, "UTF-8-BOM");

        Assert.Equal("UTF-8-BOM", document.EncodingName);

        TxtTextFile.Save(document, document.Text);

        var written = File.ReadAllBytes(path);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, written.Take(3).ToArray());
        Assert.Equal(document.Text, new UTF8Encoding(false).GetString(written, 3, written.Length - 3));
    }

    [Fact]
    public void 扩表后的编码_都能按名字读回并原样写回()
    {
        var cases = new (string Name, string Text)[]
        {
            ("Big5", "第一章 開端\n正文。\n"),
            ("ShiftJIS", "第一章 本文\n正文。\n"),
            ("Windows-1252", "Chapter one é\nBody.\n"),
            ("ISO-8859-15", "Chapter one €\nBody.\n"),
            ("KOI8-R", "Глава первая\nТекст.\n"),
        };

        foreach (var (name, text) in cases)
        {
            var encoding = TextEncodingSupport.Resolve(name);
            Assert.NotNull(encoding);
            Assert.Equal(name, TextEncodingSupport.Normalize(name), ignoreCase: true);

            var path = Path.Combine(_dir, "roundtrip-" + name + ".txt");
            File.WriteAllBytes(path, encoding!.GetBytes(text));

            var document = TxtTextFile.Load(path, name);

            Assert.Equal(name, document.EncodingName, ignoreCase: true);
            Assert.Equal(text, document.Text);

            TxtTextFile.Save(document, text + "END\n");

            Assert.Equal(text + "END\n", encoding.GetString(File.ReadAllBytes(path)));
        }
    }
}
