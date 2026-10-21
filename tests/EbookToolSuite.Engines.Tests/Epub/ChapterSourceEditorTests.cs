using System.Text;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Engines.Epub;
using Xunit;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>章节源码编辑器（T5-2）：读原样、校验给人话、写回少改字节。</summary>
public sealed class ChapterSourceEditorTests : IDisposable
{
    private const string Good = """
        <?xml version="1.0" encoding="utf-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml">
          <head><title>第一章</title></head>
          <body><p>正文</p></body>
        </html>
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ets-src-" + Guid.NewGuid().ToString("N"));
    private readonly ChapterSourceEditor _editor = new();

    public ChapterSourceEditorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响测试结论
        }
    }

    private string Write(string entry, string text, Encoding? encoding = null, bool bom = false)
    {
        var path = Path.Combine(_dir, entry.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = (encoding ?? new UTF8Encoding(false)).GetBytes(text);
        // 注意：new UTF8Encoding(false).GetPreamble() 返回空数组，要 BOM 必须自己写这三个字节
        var preamble = bom ? new byte[] { 0xEF, 0xBB, 0xBF } : Array.Empty<byte>();
        File.WriteAllBytes(path, preamble.Concat(bytes).ToArray());
        return path;
    }

    [Fact]
    public void 读源码_原样返回_不改任何格式()
    {
        const string weird = "<html>\n<body>\n\n   <p>a</p>\n\t<p>b</p>\n</body>\n</html>";
        Write("OEBPS/Text/c1.xhtml", weird);

        Assert.Equal(weird, _editor.ReadSource(_dir, "OEBPS/Text/c1.xhtml"));
    }

    [Fact]
    public void 读源码_条目越界直接拒绝()
    {
        var exception = Assert.Throws<AppException>(() => _editor.ReadSource(_dir, "../evil.xhtml"));

        Assert.Equal(ErrorCode.UnsafeArchiveEntry, exception.Code);
    }

    [Fact]
    public void 检查_良构的XHTML通过()
    {
        var check = _editor.Check(Good);

        Assert.True(check.Ok);
        Assert.Equal("语法检查通过", check.Summary);
    }

    [Fact]
    public void 检查_br没闭合_给出行号并提示写成自闭合()
    {
        var source = "<?xml version=\"1.0\"?>\n<html xmlns=\"http://www.w3.org/1999/xhtml\">\n<body><p>第一行<br>第二行</p></body>\n</html>";

        var check = _editor.Check(source);

        Assert.False(check.Ok);
        Assert.Equal(3, check.Line);
        Assert.Contains("<br/>", check.Hint, StringComparison.Ordinal);
        Assert.Contains("第 3 行", check.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 检查_裸露的与号_提示写成实体()
    {
        var source = "<?xml version=\"1.0\"?>\n<html xmlns=\"http://www.w3.org/1999/xhtml\">\n<body><p>甲 & 乙</p></body>\n</html>";

        var check = _editor.Check(source);

        Assert.False(check.Ok);
        Assert.Contains("&amp;", check.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void 检查_标签没闭合_提示检查结束标签()
    {
        var source = "<?xml version=\"1.0\"?>\n<html xmlns=\"http://www.w3.org/1999/xhtml\">\n<body><p>正文</p>";

        var check = _editor.Check(source);

        Assert.False(check.Ok);
        Assert.Contains("标签", check.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void 检查_少闭合的div_直接指出是哪个标签哪一行没关()
    {
        // 用户遇到过的真实形态：两个 div 没关，解析器只在 </body> 处报错（行号指到了 </body>）
        var source = "<html xmlns=\"http://www.w3.org/1999/xhtml\">\n"
            + "<head><title>t</title></head>\n"
            + "<body>\n"
            + "<div class=\"keeptext\">\n"
            + "<p>甲</p>\n"
            + "<div class=\"sidebar\">\n"
            + "<p>乙</p>\n"
            + "</body>\n"
            + "</html>";

        var check = _editor.Check(source);

        Assert.False(check.Ok);
        Assert.Contains("没有闭合", check.Hint, StringComparison.Ordinal);
        Assert.Contains("第 4 行", check.Hint, StringComparison.Ordinal);       // 外层 div
        Assert.Contains("第 6 行", check.Hint, StringComparison.Ordinal);       // 侧栏 div
        Assert.Contains("2 个标签", check.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void 检查_自闭合标签与注释里的小于号不算没闭合()
    {
        var source = "<html xmlns=\"http://www.w3.org/1999/xhtml\">\n"
            + "<head><title>t</title></head>\n"
            + "<body>\n"
            + "<!-- 注释里有 <div> 和一个裸的 < -->\n"
            + "<p>甲<br/>乙<img src=\"a.png\"/></p>\n"
            + "</body>\n"
            + "</html>";

        Assert.True(_editor.Check(source).Ok);
    }

    [Fact]
    public void 检查_缺body或者根不是html都被拒()
    {
        Assert.False(_editor.Check("<html><head><title>t</title></head></html>").Ok);
        Assert.False(_editor.Check("<div><body><p>x</p></body></div>").Ok);
        Assert.False(_editor.Check("   ").Ok);
    }

    [Fact]
    public void 写回_保留原文件的BOM与CRLF换行()
    {
        var path = Write("OEBPS/Text/c2.xhtml", Good.Replace("\n", "\r\n"), bom: true);

        _editor.WriteSource(_dir, "OEBPS/Text/c2.xhtml", Good);      // 传进去的是 LF 文本

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Contains("\r\n", text, StringComparison.Ordinal);
        Assert.Equal(text.Split("\r\n").Length - 1, text.Split('\n').Length - 1);   // 没有落单的 LF
    }
}
