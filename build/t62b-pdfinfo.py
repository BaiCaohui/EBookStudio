"""T6-2B 的独立核验小工具：直接按 pdfium 官方 C API 读页数与书签树。

【为什么要这么一个东西】"目录抽屉可用"（判据①）取决于 pdfium 的书签树读得对不对。
如果只用我们自己那份 P/Invoke 实现来自证，层级/目标页解读错了也看不出来——
这里按 pdfium 官方语义独立读一遍，两边的数字必须一致。

用法：
    python build\\t62b-pdfinfo.py <某个.pdf> [pdfium.dll 路径]
"""

import ctypes
import os
import sys

DEFAULT_PDFIUM_CANDIDATES = (
    r"src\EbookToolSuite.App\bin\Debug\net10.0-windows\win-x64\pdfium.dll",
    r"src\EbookToolSuite.App\bin\Release\net10.0-windows\win-x64\pdfium.dll",
    r"publish\pdfium.dll",
)


def load_pdfium(explicit):
    candidates = [explicit] if explicit else list(DEFAULT_PDFIUM_CANDIDATES)
    for path in candidates:
        if path and os.path.exists(path):
            return ctypes.WinDLL(os.path.abspath(path)), path
    raise SystemExit("找不到 pdfium.dll，请把路径作为第二个参数传进来")


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)

    pdf_path = os.path.abspath(sys.argv[1])
    pdfium, dll_path = load_pdfium(sys.argv[2] if len(sys.argv) > 2 else None)

    pdfium.FPDF_InitLibrary()

    pdfium.FPDF_LoadDocument.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
    pdfium.FPDF_LoadDocument.restype = ctypes.c_void_p
    pdfium.FPDF_GetPageCount.argtypes = [ctypes.c_void_p]
    pdfium.FPDF_GetPageCount.restype = ctypes.c_int
    pdfium.FPDF_CloseDocument.argtypes = [ctypes.c_void_p]

    doc = pdfium.FPDF_LoadDocument(pdf_path.encode("utf-8"), None)

    if not doc:
        # Windows 上 pdfium 对路径的编码历史上不一致（UTF-8 与本地 ANSI 都见过），
        # 样本文件名带中文，所以两种都试一次。
        doc = pdfium.FPDF_LoadDocument(os.fsencode(pdf_path), None)

    if not doc:
        raise SystemExit("pdfium 打不开这个文件（加密或缺密码？）：" + pdf_path)

    pages = pdfium.FPDF_GetPageCount(doc)

    pdfium.FPDFBookmark_GetFirstChild.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    pdfium.FPDFBookmark_GetFirstChild.restype = ctypes.c_void_p
    pdfium.FPDFBookmark_GetNextSibling.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    pdfium.FPDFBookmark_GetNextSibling.restype = ctypes.c_void_p
    pdfium.FPDFBookmark_GetTitle.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ulong]
    pdfium.FPDFBookmark_GetTitle.restype = ctypes.c_ulong
    pdfium.FPDFBookmark_GetDest.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    pdfium.FPDFBookmark_GetDest.restype = ctypes.c_void_p
    pdfium.FPDFBookmark_GetAction.argtypes = [ctypes.c_void_p]
    pdfium.FPDFBookmark_GetAction.restype = ctypes.c_void_p
    pdfium.FPDFAction_GetType.argtypes = [ctypes.c_void_p]
    pdfium.FPDFAction_GetType.restype = ctypes.c_ulong
    pdfium.FPDFAction_GetDest.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    pdfium.FPDFAction_GetDest.restype = ctypes.c_void_p
    pdfium.FPDFDest_GetDestPageIndex.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    pdfium.FPDFDest_GetDestPageIndex.restype = ctypes.c_int

    def title_of(bookmark):
        need = pdfium.FPDFBookmark_GetTitle(bookmark, None, 0)
        if need <= 0:
            return ""
        buf = ctypes.create_string_buffer(need)
        pdfium.FPDFBookmark_GetTitle(bookmark, buf, need)
        return buf.raw[: need - 2].decode("utf-16-le", errors="replace")

    def dest_page(bookmark):
        dest = pdfium.FPDFBookmark_GetDest(doc, bookmark)
        if not dest:
            action = pdfium.FPDFBookmark_GetAction(bookmark)
            if action and pdfium.FPDFAction_GetType(action) == 1:  # PDFACTION_GOTO
                dest = pdfium.FPDFAction_GetDest(doc, action)
        if not dest:
            return -1
        return pdfium.FPDFDest_GetDestPageIndex(doc, dest)

    entries = []

    def walk(node, level):
        while node:
            entries.append((level, dest_page(node), title_of(node)))
            child = pdfium.FPDFBookmark_GetFirstChild(doc, node)
            if child:
                walk(child, level + 1)
            node = pdfium.FPDFBookmark_GetNextSibling(doc, node)

    walk(pdfium.FPDFBookmark_GetFirstChild(doc, None), 1)

    print("pdfium   : " + dll_path)
    print("file     : " + pdf_path)
    print("bytes    : %d" % os.path.getsize(pdf_path))
    print("pages    : %d" % pages)
    print("outline  : %d 条" % len(entries))
    for level, page, title in entries[:25]:
        print("  L%d page=%-5d %s" % (level, page, title))
    if len(entries) > 25:
        print("  ...（还有 %d 条）" % (len(entries) - 25))

    pdfium.FPDF_CloseDocument(doc)


if __name__ == "__main__":
    main()
