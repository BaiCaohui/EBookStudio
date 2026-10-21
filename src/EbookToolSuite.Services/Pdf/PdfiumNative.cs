using System.Runtime.InteropServices;

namespace EbookToolSuite.Services.Pdf;

/// <summary>
/// pdfium.dll 的书签（目录）API 直连声明。**只声明目录用得到的那几个**，
/// 其余能力一律走 Docnet.Core——多声明一个就多一份要跟着 pdfium 版本维护的签名。
/// </summary>
/// <remarks>
/// <para>
/// **为什么必须自己 P/Invoke**：Docnet.Core 2.6.0 的公开 API 里没有任何 outline/bookmark
/// 方法（已反射确认 22 个导出类型）。要去读 PDF 目录只有两条路：换包（还要多背一条原生依赖链）
/// 或者直接用仓库随包发布的那个 pdfium.dll——它确实导出了 <c>FPDFBookmark_*</c> 全套（已实测）。
/// </para>
/// <para>
/// **路径与口令必须按 UTF-8 封送**（<see cref="UnmanagedType.LPUTF8Str"/>），这不是风格问题：
/// 实测 <c>FPDF_LoadDocument</c> 对同一个中文路径，
/// ANSI/LPStr 封送返回 0（NULL）、UTF-16/LPWStr 也返回 0，**只有 UTF-8 能打开**。
/// 样本路径全是中文（<c>TestCorpus\sample-pdf-1.pdf</c>），封送方式写错就是"目录永远读不到"，
/// 而且表现为"没有目录"这种不报错的静默失败。
/// </para>
/// <para>
/// **不调用 <c>FPDF_DestroyLibrary</c>**：pdfium 的初始化是进程级引用计数，
/// Docnet.Core 的 <c>DocLib.Instance</c> 也持有一次；本类只在自己第一次用到时
/// <c>FPDF_InitLibrary</c>（重复调用安全，内部引用计数），退出时不做对称销毁——
/// 原生库的生命周期归进程，不归某个 C# 对象。
/// </para>
/// </remarks>
internal static class PdfiumNative
{
    private const string Library = "pdfium.dll";

    private static int _initialized;

    /// <summary>幂等地初始化 pdfium 库（引用计数，重复调用安全）。</summary>
    internal static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            FPDF_InitLibrary();
        }
    }

    [DllImport(Library)]
    internal static extern void FPDF_InitLibrary();

    [DllImport(Library, EntryPoint = "FPDF_LoadDocument")]
    internal static extern IntPtr FPDF_LoadDocument(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password);

    [DllImport(Library)]
    internal static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(Library)]
    internal static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);

    [DllImport(Library)]
    internal static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);

    /// <summary>
    /// 取标题。长度单位为字节，**含结尾的 2 字节 NUL**（实测 'Chapter One' → 24 = 22 + 2）。
    /// 传 <c>null</c>/0 询问长度，再取缓冲。
    /// </summary>
    [DllImport(Library)]
    internal static extern uint FPDFBookmark_GetTitle(IntPtr bookmark, byte[]? buffer, uint bufferLength);

    [DllImport(Library)]
    internal static extern IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);

    [DllImport(Library)]
    internal static extern IntPtr FPDFBookmark_GetAction(IntPtr bookmark);

    [DllImport(Library)]
    internal static extern uint FPDFAction_GetType(IntPtr action);

    [DllImport(Library)]
    internal static extern IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);

    [DllImport(Library)]
    internal static extern int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);
}
