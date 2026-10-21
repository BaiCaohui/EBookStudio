using System.Text;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Services.Pdf;

/// <summary>
/// 用 pdfium 的书签 API 读 PDF 目录，并把书签树拍平成带层级的列表。
/// </summary>
/// <remarks>
/// <para>
/// **遍历方式**：<c>GetFirstChild</c> 向下、<c>GetNextSibling</c> 向右，递归深度即层级（从 1 起）。
/// 目标页按 pdfium 的标准回退链解：<c>GetDest</c> → 没有就 <c>GetAction</c> →
/// <c>GetType == GOTO(1)</c> → <c>GetDest</c> → <c>FPDFDest_GetDestPageIndex</c>。
/// 实测三份样本里两类都存在：构造样本里纯 action 的书签两类调用都能解出页，
/// 而完全没有目标的（纯分组标题）如实回 <c>-1</c>。
/// </para>
/// <para>
/// **循环保护**：pdfium 自己的书签遍历不做环检测，损坏的 PDF（或恶意构造）
/// 可以让 <c>GetNextSibling</c> 指回祖先，递归就变成死循环 + 栈溢出——栈溢出在 .NET 里
/// 是**进程级崩溃**、catch 不住，比抛异常严重得多。所以用一个句柄集合挡重复访问，
/// 再加一个总条数上限兜底。
/// </para>
/// <para>
/// **返回 null 的语义**：只有"pdfium 打不开这份文档"才返回 null，
/// 由调用方去走 Docnet 那条既有的错误翻译通路（文件不存在/损坏/加密 → 既有错误码），
/// 这样错误码不会出现第二套定义。文档打得开但目录读不出来，一律返回空/部分列表。
/// </para>
/// </remarks>
internal static class PdfOutlineReader
{
    /// <summary>遍历条数上限：正常书最多几千条，超过这个数说明结构异常，直接停手但返回已读到的。</summary>
    private const int MaxEntries = 10_000;

    /// <summary>递归深度上限：真实 PDF 的目录极少超过 10 层，超了按异常结构处理。</summary>
    private const int MaxDepth = 32;

    /// <summary>
    /// 读目录。
    /// </summary>
    /// <param name="path">PDF 路径。</param>
    /// <param name="password">口令；null/空 = 没口令。</param>
    /// <returns>目录条目（可能为空）；**文档打不开时返回 null**。</returns>
    internal static IReadOnlyList<PdfOutlineEntry>? TryRead(string path, string? password)
    {
        try
        {
            PdfiumNative.EnsureInitialized();
        }
        // 原生 pdfium.dll 缺失/位数不对时抛的是这两个。返回 null 让调用方走 Open()
        // 那条通路，那里会把它们翻译成 MissingTool（"重装本软件"而不是"重新下载这本书"）。
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            return null;
        }

        IntPtr document;

        try
        {
            document = PdfiumNative.FPDF_LoadDocument(path, password ?? string.Empty);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            return null;
        }

        if (document == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var entries = new List<PdfOutlineEntry>();
            var visited = new HashSet<IntPtr>();

            try
            {
                Walk(
                    document,
                    PdfiumNative.FPDFBookmark_GetFirstChild(document, IntPtr.Zero),
                    level: 1,
                    entries,
                    visited);
            }
            // 目录只是导航辅助：遍历期间出任何岔子都返回"已经读到的那些"，
            // 绝不让它把"打开这本书"带崩。
            catch (Exception)
            {
            }

            return entries;
        }
        finally
        {
            PdfiumNative.FPDF_CloseDocument(document);
        }
    }

    /// <summary>深度优先遍历一级（同级链表），再对每个节点递归其子链表。</summary>
    private static void Walk(
        IntPtr document,
        IntPtr firstBookmark,
        int level,
        List<PdfOutlineEntry> entries,
        HashSet<IntPtr> visited)
    {
        var bookmark = firstBookmark;
        var emittedAtThisLevel = 0;

        while (bookmark != IntPtr.Zero
               && entries.Count < MaxEntries
               && level <= MaxDepth
               && !visited.Contains(bookmark))
        {
            visited.Add(bookmark);

            var title = ReadTitle(bookmark);

            if (string.IsNullOrWhiteSpace(title))
            {
                // 空标题的书签是"纯分组节点"（实测这类节点在目录里就是一行空白）。
                // 不产出它，但**它的子项照常遍历**，否则整棵子树的章节会凭空消失。
                Walk(
                    document,
                    PdfiumNative.FPDFBookmark_GetFirstChild(document, bookmark),
                    level,
                    entries,
                    visited);
            }
            else
            {
                entries.Add(new PdfOutlineEntry(title, level, ResolvePageIndex(document, bookmark)));

                Walk(
                    document,
                    PdfiumNative.FPDFBookmark_GetFirstChild(document, bookmark),
                    level + 1,
                    entries,
                    visited);
            }

            bookmark = PdfiumNative.FPDFBookmark_GetNextSibling(document, bookmark);

            if (++emittedAtThisLevel > MaxEntries)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 解出书签指向的页号，解不出返回 -1。
    /// </summary>
    /// <remarks>
    /// 先看 <c>GetDest</c>：多数书签直接带目标。
    /// 没有再看 <c>GetAction</c>，只认 GOTO（枚举值 1）——URI/Launch 这类动作不是"跳到某一页"，
    /// 当分组标题处理比乱跳好。
    /// </remarks>
    private static int ResolvePageIndex(IntPtr document, IntPtr bookmark)
    {
        var dest = PdfiumNative.FPDFBookmark_GetDest(document, bookmark);

        if (dest == IntPtr.Zero)
        {
            var action = PdfiumNative.FPDFBookmark_GetAction(bookmark);

            if (action != IntPtr.Zero && PdfiumNative.FPDFAction_GetType(action) == GoToActionType)
            {
                dest = PdfiumNative.FPDFAction_GetDest(document, action);
            }
        }

        return dest == IntPtr.Zero ? -1 : PdfiumNative.FPDFDest_GetDestPageIndex(document, dest);
    }

    /// <summary>pdfium 的 <c>PDFACTION_GOTO</c> 枚举值。</summary>
    private const uint GoToActionType = 1;

    /// <summary>
    /// 读标题：先问字节长度，再取缓冲，最后按 UTF-16LE 解码并剥掉结尾的 NUL。
    /// </summary>
    /// <remarks>
    /// 长度**含结尾 2 字节 NUL**（实测 'Chapter One' 11 字 → 22 字节 + NUL = 24）。
    /// pdfium 输出的已经是 UTF-16LE（PDF 里 UTF-16BE 的字面量会在内部转好），
    /// 实测 <c>/Title&lt;FEFF7B2C4E007AE0&gt;</c> 解出「第一章」，没有多余 BOM。
    /// </remarks>
    private static string ReadTitle(IntPtr bookmark)
    {
        var length = PdfiumNative.FPDFBookmark_GetTitle(bookmark, null, 0);

        if (length <= 2)
        {
            // 0 = 没有标题；2 = 只有一个 NUL（空串）。都当空标题处理。
            return string.Empty;
        }

        var buffer = new byte[length];
        var written = PdfiumNative.FPDFBookmark_GetTitle(bookmark, buffer, length);

        if (written <= 2)
        {
            return string.Empty;
        }

        // 去掉结尾的 NUL（2 字节），不信任 written：以实际写入长度为准，
        // 但绝不超过缓冲长度（pdfium 对坏数据也不保证写满）。
        var textBytes = (int)Math.Min(written, length) - 2;

        return textBytes <= 0 ? string.Empty : Encoding.Unicode.GetString(buffer, 0, textBytes);
    }
}
