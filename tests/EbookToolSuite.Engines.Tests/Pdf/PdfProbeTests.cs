using System.Text;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Engines.Pdf;

namespace EbookToolSuite.Engines.Tests.Pdf;

/// <summary>
/// PDF 导入体检（T6-2 前置）：真的是 PDF 的放行，改了扩展名的别的东西拦住；
/// 加密与截断**只记账、不拦人**（设计文档 §6.5.4：不因"可能损坏"拒收）。
/// </summary>
public sealed class PdfProbeTests : IDisposable
{
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var file in _files)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void 合法PDF_认出来_不加密_不截断()
    {
        var result = PdfProbe.Inspect(NewFile(BuildPdf()));

        Assert.True(result.LooksLikePdf);
        Assert.False(result.IsEncrypted);
        Assert.False(result.IsTruncated);
        Assert.True(result.FileSize > 0);
    }

    [Fact]
    public void 没有PDF签名_判为不是PDF()
    {
        var result = PdfProbe.Inspect(NewFile(Encoding.ASCII.GetBytes("这不是 PDF，只是改了扩展名。")));

        Assert.False(result.LooksLikePdf);
    }

    [Fact]
    public void 签名正好落在头部窗口内_认出来()
    {
        // 规范要求 %PDF- 在前 1024 字节内：这里让签名从第 1019 字节开始（最后一刻挤进去）
        var bytes = Merge(new byte[1019], BuildPdf());

        Assert.True(PdfProbe.Inspect(NewFile(bytes)).LooksLikePdf);
    }

    [Fact]
    public void 签名出现在第1024字节之后_判为不是PDF()
    {
        // 从第 1024 字节才开始，已经出了窗口（这正是"扩展名对齐但内容不是"的常见形态）
        var bytes = Merge(new byte[1024], BuildPdf());

        Assert.False(PdfProbe.Inspect(NewFile(bytes)).LooksLikePdf);
    }

    [Fact]
    public void 空文件_判为不是PDF()
    {
        var result = PdfProbe.Inspect(NewFile(Array.Empty<byte>()));

        Assert.False(result.LooksLikePdf);
        Assert.True(result.IsTruncated);
    }

    [Fact]
    public void 不存在的文件_抛FileNotFound()
    {
        var ex = Assert.Throws<AppException>(
            () => PdfProbe.Inspect(Path.Combine(Path.GetTempPath(), "ets-没有这个文件.pdf")));

        Assert.Equal(ErrorCode.FileNotFound, ex.Code);
    }

    [Fact]
    public void trailer带Encrypt间接引用_判为加密()
    {
        // /Encrypt 12 0 R —— 间接引用形式
        Assert.True(PdfProbe.Inspect(NewFile(BuildPdf(trailerExtra: "/Encrypt 12 0 R "))).IsEncrypted);
    }

    [Fact]
    public void trailer带Encrypt直接字典_判为加密()
    {
        // /Encrypt << /Filter /Standard >> —— 直接字典形式
        Assert.True(PdfProbe.Inspect(
            NewFile(BuildPdf(trailerExtra: "/Encrypt << /Filter /Standard >> "))).IsEncrypted);
    }

    [Fact]
    public void 内容里偶然出现Encrypt字样_不误判成加密()
    {
        // 压缩流里撞上同名字节串是常事：后面既不跟 << 也不跟数字，就不该算加密
        Assert.False(PdfProbe.Inspect(
            NewFile(BuildPdf(body: "stream\n.../EncryptFooBar...\nendstream"))).IsEncrypted);
    }

    [Fact]
    public void 缺少EOF标记_判为截断_但仍然认它是PDF()
    {
        var result = PdfProbe.Inspect(NewFile(BuildPdf(eof: false)));

        Assert.True(result.LooksLikePdf);
        Assert.True(result.IsTruncated);
    }

    /// <summary>
    /// 拿仓库里真实样本跑一遍（`TestCorpus/`，设计文档 §12.2 D：由用户提供）。
    /// 样本不在就静默跳过——xunit 2.x 没有动态跳过，这里用"直接返回"表达"没样本可测"。
    /// </summary>
    [Fact]
    public void 真实样本_都该认成PDF且不算截断()
    {
        var corpus = FindCorpus();

        if (corpus is null)
        {
            return;
        }

        var files = Directory.GetFiles(corpus, "*.pdf");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var result = PdfProbe.Inspect(file);

            Assert.True(result.LooksLikePdf, Path.GetFileName(file) + " 应该被认成 PDF");
            Assert.False(result.IsTruncated, Path.GetFileName(file) + " 不该被判成截断");
        }

        // 权限受限的那本（打开时 Chromium 会画"此文件具有受限权限"提示条，见 build/t62-probe-shots/p1-01）
        // 必须被 /Encrypt 认出来。按名字只在样本仍在时生效，样本换了也不会误伤。
        var restricted = files.FirstOrDefault(f => Path.GetFileName(f).Contains("萌版", StringComparison.Ordinal));

        if (restricted is not null)
        {
            Assert.True(PdfProbe.Inspect(restricted).IsEncrypted, "权限受限的 PDF 应判为加密");
        }
    }

    /// <summary>造一个最小但结构完整的 PDF 文本（不是合法交叉引用表，探测只看签名与关键字节）。</summary>
    private static byte[] BuildPdf(string body = "", bool eof = true, string trailerExtra = "")
    {
        var text = "%PDF-1.7\n" +
            body + "\n" +
            "trailer\n<< /Size 1 /Root 1 0 R " + trailerExtra + ">>\n" +
            "startxref\n9\n" +
            (eof ? "%%EOF\n" : string.Empty);

        return Encoding.ASCII.GetBytes(text);
    }

    private static byte[] Merge(byte[] head, byte[] tail)
    {
        var merged = new byte[head.Length + tail.Length];
        head.CopyTo(merged, 0);
        tail.CopyTo(merged, head.Length);
        return merged;
    }

    /// <summary>从测试程序集目录往上找带 `TestCorpus/` 的仓库根。</summary>
    private static string? FindCorpus()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "TestCorpus");

            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "*.pdf").Length > 0)
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private string NewFile(byte[] bytes)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "ets-pdfprobe-" + Guid.NewGuid().ToString("N") + ".pdf");

        File.WriteAllBytes(path, bytes);
        _files.Add(path);
        return path;
    }
}
