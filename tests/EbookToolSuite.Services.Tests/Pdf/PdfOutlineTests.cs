using System.Text;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Services.Pdf;
using Xunit.Abstractions;

namespace EbookToolSuite.Services.Tests.Pdf;

/// <summary>
/// T6-2B：PDF 目录（outline）的实测。
/// </summary>
/// <remarks>
/// <para>
/// 【为什么这个测试必须存在】Docnet.Core 没有任何 outline API，这条链路是仓库里唯一一处
/// <strong>直接 P/Invoke pdfium.dll</strong> 的代码（<c>PdfiumNative</c> / <c>PdfOutlineReader</c>）。
/// 它没有 NuGet 封装兜底：导出名、调用约定、UTF-16LE 标题、目标页解析，每一步都得靠实测撑着。
/// 而"目录抽屉可用"是 T6-2B 判据①的四项之一，不能靠"看起来没问题"交差。
/// </para>
/// <para>
/// 【56 这个期望值从哪来】不是从我们自己的实现里抄的：<c>build\t62b-pdfinfo.py</c>
/// 是一份独立的 ctypes 核验工具，按 pdfium 官方 C API 读同一份文件，数出来同样是 56 条、
/// 首条 page=0、含 L1/L2 两层。两边对不上就说明我们的封送有问题——这正是本测试守的边界。
/// </para>
/// </remarks>
public sealed class PdfOutlineTests
{
    private readonly ITestOutputHelper _output;

    public PdfOutlineTests(ITestOutputHelper output) => _output = output;

    private static IPdfDocumentService CreateService() => new PdfDocumentService(new PdfiumLibrary());

    [Fact]
    public void ReadOutline_带目录的真实样本_读到56条且层级与目标页可用()
    {
        var path = PdfTestCorpus.CorpusPath(PdfTestCorpus.TextBook);
        var service = CreateService();

        var pages = service.GetPageCount(path);
        var outline = service.ReadOutline(path);

        foreach (var entry in outline.Take(10))
        {
            _output.WriteLine($"L{entry.Level} page={entry.PageIndex} {entry.Title}");
        }

        Assert.Equal(56, outline.Count);

        // 首条是封面（page 0），标题非空：UTF-16LE 解错的话这里最先炸（标题会变成乱码甚至空串）
        Assert.Equal(1, outline[0].Level);
        Assert.Equal(0, outline[0].PageIndex);
        Assert.False(string.IsNullOrWhiteSpace(outline[0].Title));

        // 目录本身是嵌套的；全拍平成一层说明递归（GetFirstChild）根本没走到
        Assert.Contains(outline, e => e.Level >= 2);
        Assert.Equal(1, outline.Min(e => e.Level));

        // 每条的目标页要么落在书里，要么是"解不出目标页"的 -1（上层画成不可点的分组标题）
        Assert.All(outline, e => Assert.InRange(e.PageIndex, -1, pages - 1));
    }

    [Fact]
    public void ReadOutline_没有目录的真实样本_返回空列表而不是抛()
    {
        var service = CreateService();
        var scanned = PdfTestCorpus.CorpusPath(PdfTestCorpus.ScannedBook38M);

        // 扫描件没有书签树。"没有目录"是正常状态，不是错误——
        // 目录只是导航辅助，不该让"打开这本书"失败（接口注释里写死了这一条）。
        Assert.Empty(service.ReadOutline(scanned));
    }

    [Fact]
    public void ReadOutline_不是PDF的文件_抛AppException()
    {
        var path = PdfTestCorpus.WriteTempPdf(Encoding.UTF8.GetBytes("这不是 PDF，只是一段文字。"));

        try
        {
            var service = CreateService();

            var ex = Assert.Throws<AppException>(() => service.ReadOutline(path));

            _output.WriteLine($"错误码：{ex.Code}；消息：{ex.Message}");
        }
        finally
        {
            PdfTestCorpus.TryDelete(path);
        }
    }
}
