"""T6-2B 的独立渲染核验：直接按 pdfium 官方 C API 渲一页到 PNG，并报像素统计。

【为什么要独立渲一遍】"页面渲出来是黑的"可能是三种完全不同的原因：
  1. PDF 本身内容如此（几乎不可能）；
  2. pdfium 原生渲染就是这样（例如带透明组的页面在无 alpha 的位图上渲成黑底）；
  3. 我们的封装（Docnet.Core 的 GetImage() + ImageSharp 编码）把它弄黑了。
只有拿**不经我们封装**的原生渲染做对照，才能把 2 和 3 分开。

用法：python build\\t62b-render.py <pdf> <页码> [输出 png]
"""

import ctypes
import os
import sys

from PIL import Image

DEFAULT_PDFIUM_CANDIDATES = (
    r"src\EbookToolSuite.App\bin\Debug\net10.0-windows\win-x64\pdfium.dll",
    r"src\EbookToolSuite.App\bin\Release\net10.0-windows\win-x64\pdfium.dll",
    r"publish\pdfium.dll",
)


def load_pdfium():
    for path in DEFAULT_PDFIUM_CANDIDATES:
        if os.path.exists(path):
            return ctypes.WinDLL(os.path.abspath(path)), path
    raise SystemExit("找不到 pdfium.dll")


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)

    pdf_path = os.path.abspath(sys.argv[1])
    page_index = int(sys.argv[2])
    out_path = sys.argv[3] if len(sys.argv) > 3 else None

    pdfium, dll_path = load_pdfium()
    pdfium.FPDF_InitLibrary()

    pdfium.FPDF_LoadDocument.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
    pdfium.FPDF_LoadDocument.restype = ctypes.c_void_p
    pdfium.FPDF_GetPageCount.argtypes = [ctypes.c_void_p]
    pdfium.FPDF_GetPageCount.restype = ctypes.c_int
    pdfium.FPDF_LoadPage.argtypes = [ctypes.c_void_p, ctypes.c_int]
    pdfium.FPDF_LoadPage.restype = ctypes.c_void_p
    pdfium.FPDF_GetPageWidthF.argtypes = [ctypes.c_void_p]
    pdfium.FPDF_GetPageWidthF.restype = ctypes.c_double
    pdfium.FPDF_GetPageHeightF.argtypes = [ctypes.c_void_p]
    pdfium.FPDF_GetPageHeightF.restype = ctypes.c_double
    pdfium.FPDFBitmap_Create.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_int]
    pdfium.FPDFBitmap_Create.restype = ctypes.c_void_p
    pdfium.FPDFBitmap_FillRect.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int,
                                          ctypes.c_int, ctypes.c_int, ctypes.c_ulong]
    pdfium.FPDFBitmap_GetBuffer.argtypes = [ctypes.c_void_p]
    pdfium.FPDFBitmap_GetBuffer.restype = ctypes.c_void_p
    pdfium.FPDFBitmap_GetStride.argtypes = [ctypes.c_void_p]
    pdfium.FPDFBitmap_GetStride.restype = ctypes.c_int
    pdfium.FPDF_RenderPageBitmap.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int,
                                            ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                            ctypes.c_int, ctypes.c_int]

    doc = pdfium.FPDF_LoadDocument(pdf_path.encode("utf-8"), None) or \
        pdfium.FPDF_LoadDocument(os.fsencode(pdf_path), None)

    if not doc:
        raise SystemExit("打不开：" + pdf_path)

    pdfium.FPDF_CloseDocument.argtypes = [ctypes.c_void_p]

    pages = pdfium.FPDF_GetPageCount(doc)
    page = pdfium.FPDF_LoadPage(doc, page_index)

    if not page:
        raise SystemExit("加载不了第 %d 页" % page_index)

    # 尺寸直接用 A4@150dpi 的固定口径：本次只关心"颜色对不对"，
    # 不需要和 PDF 的真实点尺寸挂钩（那样还得跨一次 ctypes 结构体，徒增出错面）。
    pw = 595.0
    ph = 842.0
    w = 1240
    h = 1754

    bitmap = pdfium.FPDFBitmap_Create(w, h, 1)          # 1 = 带 alpha

    # 第 4 个参数给 "nofill" 就跳过铺白：用来判定"这份 PDF 渲成黑"到底是谁的责任——
    # pdfium 自己会不会给没有自绘背景的页面铺白。
    if not (len(sys.argv) > 4 and sys.argv[4] == "nofill"):
        pdfium.FPDFBitmap_FillRect(bitmap, 0, 0, w, h, 0xFFFFFFFF)

    pdfium.FPDF_RenderPageBitmap(bitmap, page, 0, 0, w, h, 0, 0)

    stride = pdfium.FPDFBitmap_GetStride(bitmap)
    buffer = pdfium.FPDFBitmap_GetBuffer(bitmap)

    raw = ctypes.string_at(buffer, stride * h)
    image = Image.frombuffer("RGBA", (w, h), raw, "raw", "BGRA", stride, 1)
    rgb = image.convert("RGB")

    pixels = list(rgb.get_flattened_data()) if hasattr(rgb, "get_flattened_data") else list(rgb.getdata())
    total = len(pixels)
    black = sum(1 for r, g, b in pixels if r < 60 and g < 60 and b < 60)
    white = sum(1 for r, g, b in pixels if r > 200 and g > 200 and b > 200)

    # alpha 分布：这是判断"白底合成会不会把图片刷没"的关键数据
    alphas = [image.getpixel((x, y))[3] for y in range(0, h, 7) for x in range(0, w, 7)]
    n = len(alphas)
    zero = sum(1 for a in alphas if a == 0)
    opaque = sum(1 for a in alphas if a == 255)
    partial = n - zero - opaque

    # a==0 但 RGB 不为 0 的像素 = "图画上去了却没写 alpha"的内容像素
    content_zero = 0
    for y in range(0, h, 7):
        for x in range(0, w, 7):
            r, g, b, a = image.getpixel((x, y))
            if a == 0 and (r or g or b):
                content_zero += 1

    print("pdfium  : " + dll_path)
    print("file    : " + os.path.basename(pdf_path))
    print("pages   : %d / 渲染第 %d 页" % (pages, page_index))
    print("点尺寸  : %.1f x %.1f pt" % (pw, ph))
    print("位图    : %d x %d  stride=%d" % (w, h, stride))
    print("黑像素  : %.2f%%" % (100.0 * black / total))
    print("白像素  : %.2f%%" % (100.0 * white / total))
    print("alpha=0 : %.2f%%（其中 RGB 不为 0 的占 %.2f%%）" % (
        100.0 * zero / n, 100.0 * content_zero / n))
    print("0<a<255 : %.2f%%" % (100.0 * partial / n))
    print("alpha=255: %.2f%%" % (100.0 * opaque / n))

    # 与 PdfPageEffects.ColoredPixelRatio 同一口径（max-min 通道 > 64 的抽样像素占比），
    # 用来给"图片页判定"的阈值找数据依据。
    colored = 0
    for y in range(0, h, 7):
        for x in range(0, w, 7):
            r, g, b, a = image.getpixel((x, y))
            if max(r, g, b) - min(r, g, b) > 64:
                colored += 1

    print("彩色像素占比: %.2f%%" % (100.0 * colored / n))

    if out_path:
        rgb.save(out_path)
        print("已保存  : " + out_path)

    pdfium.FPDF_CloseDocument(doc)


if __name__ == "__main__":
    main()
