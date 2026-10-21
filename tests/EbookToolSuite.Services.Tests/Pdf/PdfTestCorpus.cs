namespace EbookToolSuite.Services.Tests.Pdf;

/// <summary>
/// T6-2B 测试共用的样本定位与临时文件工具。
/// </summary>
/// <remarks>
/// <para>
/// 三份真实样本（<c>TestCorpus/</c>）的路径含空格、括号、日文与繁体中文——
/// 这本身就是 pdfium 路径封送的一个测试面，所以不做任何"复制成 ASCII 名"的规避。
/// </para>
/// <para>
/// **样本缺失时明确失败，不跳过**：与 <c>PdfDocumentServiceTests</c> 同一取舍
/// （那里的注释说得很清楚——"悄悄跳过等于把判据变成空转"）。
/// xunit 2.x 也没有动态 Skip，写 <c>return</c> 只会让人以为跑过了。
/// </para>
/// </remarks>
internal static class PdfTestCorpus
{
    /// <summary>有文本层的那本（57 页，含目录）。</summary>
    internal const string TextBook =
        "sample-pdf-1.pdf";

    /// <summary>纯扫描，38.6 MB / 320 页。</summary>
    internal const string ScannedBook38M =
        "sample-pdf-2.pdf";

    /// <summary>纯扫描，120 MB / 129 页。</summary>
    internal const string ScannedBook120M =
        "sample-pdf-3.pdf";

    /// <summary>
    /// T6-2B 判据②要的"200 页文字版"（pdfium 实测 196 页）。
    /// </summary>
    /// <remarks>
    /// 这是**构造出来的**样本，不是用户放进来的：Calibre 从一本几万字的 TXT 生成
    /// （`ebook-convert &lt;txt&gt; &lt;pdf&gt; --input-encoding=utf-8 --paper-size=a4`，
    /// 源文本按比例截断到目标页数），生成命令记在 `build\t62b-acceptance-report.md` 里，
    /// 需要时能一模一样地重造。它同时是"页面不自绘背景"那类 PDF 的代表——Calibre 输出的
    /// 页面不画自己的白底，正是"整页渲成黑页"那个 bug 的复现样本。
    /// </remarks>
    internal const string TextBook200Pages = "t62b-text-200p.pdf";

    internal static readonly string[] All = [TextBook, ScannedBook38M, ScannedBook120M, TextBook200Pages];

    /// <summary><c>TestCorpus/</c> 的绝对路径；找不到就抛，避免测试静默跑空。</summary>
    internal static string CorpusDirectory()
    {
        var root = FindRepoRoot()
            ?? throw new InvalidOperationException(
                "找不到仓库根（应从测试输出目录往上找到含 Tools/manifest.json 的目录）。");

        var corpus = Path.Combine(root, "TestCorpus");

        if (!Directory.Exists(corpus))
        {
            throw new DirectoryNotFoundException($"找不到样本目录：{corpus}");
        }

        return corpus;
    }

    internal static string CorpusPath(string fileName)
    {
        var path = Path.Combine(CorpusDirectory(), fileName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"T6-2B 的实测样本缺失，无法验证 pdfium 通路：{path}", path);
        }

        return path;
    }

    /// <summary>往上找带 Tools\manifest.json 的目录 = 仓库根（与仓库既有测试同一做法）。</summary>
    internal static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tools", "manifest.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>把构造出来的 PDF 落到临时目录，返回路径。</summary>
    internal static string WriteTempPdf(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "t62b-" + Guid.NewGuid().ToString("N") + ".pdf");

        File.WriteAllBytes(path, bytes);

        return path;
    }

    /// <summary>
    /// 删临时文件，失败就算了。
    /// </summary>
    /// <remarks>
    /// **不吞异常是刻意的**：实测 pdfium 打开过的文件在 <c>FPDF_CloseDocument</c> 之后
    /// 仍可能短暂占着句柄（探针里 File.Delete 抛过），删不掉不该让一个已经断言完的测试变红。
    /// </remarks>
    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
