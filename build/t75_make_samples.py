#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""T7-5 全量回归「可构造测试样本」生成器（40 个样本）。

由 build/make-test-samples.ps1 调用，也可以直接运行：

    python build/t75_make_samples.py --output-dir Data-Test/t75-corpus
    python build/t75_make_samples.py --output-dir Data-Test/t75-corpus --only "A01,B01"
    python build/t75_make_samples.py --output-dir Data-Test/t75-corpus --skip-calibre

真源是 build/t75-samples.catalog.json：
  - 逐条按 catalog 的 file 字段落盘，产不出来就报错退出 1，绝不静默跳过；
  - manifest.json 保留 catalog 原字段，每条追加 bytes / sha256（跳过时追加 skipped）。

确定性约定：
  - ZIP 内每个条目的 date_time 固定为 (2026, 1, 1, 0, 0, 0)；
  - 图片 / PDF 的构造不使用随机数或当前时间；
  - 例外：C 组由真 Calibre 派生，字节会随 Calibre 版本与系统字体变化，manifest 记录本次实际 sha256。
"""

from __future__ import annotations

import argparse
import datetime
import hashlib
import io
import json
import os
import platform
import random
import re
import shutil
import subprocess
import sys
import time
import uuid
import zipfile
from pathlib import Path


# ---------------------------------------------------------------------------
# 控制台输出：包装脚本会设置 PYTHONIOENCODING=utf-8；直接运行时沿用控制台编码。
# ---------------------------------------------------------------------------

def _setup_console() -> None:
    enc = os.environ.get("PYTHONIOENCODING", "").lower()
    if "utf-8" in enc or "utf8" in enc:
        for stream in (sys.stdout, sys.stderr):
            try:
                stream.reconfigure(encoding="utf-8", errors="replace")
            except (AttributeError, ValueError):
                pass


_setup_console()

try:
    from PIL import Image, ImageDraw, ImageFont
except ImportError:  # pragma: no cover - 环境缺库时给出可操作的中文提示
    sys.stderr.write(
        "错误：当前 Python 缺少 Pillow，无法生成图片与 PDF 样本。\n"
        "请先安装：python -m pip install Pillow\n"
        "或把 $env:EBOOK_TOOLSUITE_PYTHON 指向已经装好 Pillow 的 python.exe。\n"
    )
    sys.exit(1)


# ---------------------------------------------------------------------------
# 常量
# ---------------------------------------------------------------------------

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent
CATALOG_PATH = SCRIPT_DIR / "t75-samples.catalog.json"

GENERATOR_NAME = "t75_make_samples.py"

FIXED_ZIP_TIME = (2026, 1, 1, 0, 0, 0)
MIMETYPE = b"application/epub+zip"
MODIFIED = "2026-01-01T00:00:00Z"
LANG = "zh-CN"

TITLE_A01 = "回归样本-最小 EPUB3"
AUTHOR_A = "回归作者 A"
AUTHOR_B = "回归作者 B"

# 图片 / 字体本身就是压缩格式：与 EpubPackager 一致，用 STORED 写入，避免无收益的二次压缩。
STORED_EXTENSIONS = {".jpg", ".jpeg", ".png", ".gif", ".webp", ".ttf", ".otf", ".woff", ".woff2"}

# C 组（真 Calibre 派生），-SkipCalibre 时整组标记 skipped。
CALIBRE_IDS = {"C01", "C02"}

PALETTE = [
    (203, 67, 53),
    (39, 105, 180),
    (46, 139, 87),
    (230, 176, 46),
    (128, 90, 160),
    (66, 165, 165),
    (190, 120, 80),
    (90, 90, 110),
]


class GenerationError(Exception):
    """可预期的生成失败（中文文案直接展示给用户）。"""


def human_bytes(n: int) -> str:
    value = float(n)
    units = ["B", "KB", "MB", "GB"]
    index = 0
    while value >= 1024.0 and index < len(units) - 1:
        value /= 1024.0
        index += 1
    if index == 0:
        return f"{n} B"
    return f"{value:.2f} {units[index]}"


def identifier_for(sample_id: str) -> str:
    """每个样本一个固定 UUID，避免 40 本书共用标识符而触发意外的去重。"""
    return "urn:uuid:" + str(uuid.uuid5(uuid.NAMESPACE_URL, "ebooktoolsuite-t75:" + sample_id))


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def ensure_parent(path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)


def write_bytes(path: Path, data: bytes) -> None:
    ensure_parent(path)
    with open(path, "wb") as stream:
        stream.write(data)


def read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8")


# ---------------------------------------------------------------------------
# ZIP 工具：固定时间戳，保证同一条目重复生成字节稳定
# ---------------------------------------------------------------------------

def entry(name: str, data, stored: bool | None = None):
    """构造一个待写入 ZIP 的条目：(名称, 字节, 是否 STORED)。"""
    if isinstance(data, str):
        data = data.encode("utf-8")
    if stored is None:
        stored = Path(name).suffix.lower() in STORED_EXTENSIONS
    return (name, bytes(data), bool(stored))


def _zipinfo(name: str, stored: bool) -> zipfile.ZipInfo:
    info = zipfile.ZipInfo(name, date_time=FIXED_ZIP_TIME)
    info.compress_type = zipfile.ZIP_STORED if stored else zipfile.ZIP_DEFLATED
    # 固定 create_system / external_attr，跨平台、跨次运行字节一致
    info.create_system = 0
    info.external_attr = 0
    return info


def zip_bytes(entries) -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        for name, data, stored in entries:
            archive.writestr(_zipinfo(name, stored), data)
    return buffer.getvalue()


def write_zip(path: Path, entries) -> None:
    ensure_parent(path)
    with zipfile.ZipFile(str(path), "w") as archive:
        for name, data, stored in entries:
            archive.writestr(_zipinfo(name, stored), data)


def write_epub(path: Path, body_entries, mimetype_position: str = "first",
               mimetype_deflated: bool = False) -> None:
    """按 OCF 规则组包。

    mimetype_position="first"：mimetype 首条（正常样本）；
    mimetype_position="last" ：A12 专用，mimetype 放到最后；
    mimetype_deflated=True   ：A13 专用，mimetype 用 DEFLATE 压缩。
    """
    mimetype_entry = ("mimetype", MIMETYPE, not mimetype_deflated)
    body = list(body_entries)
    if mimetype_position == "first":
        entries = [mimetype_entry] + body
    elif mimetype_position == "last":
        entries = body + [mimetype_entry]
    else:
        raise GenerationError(f"内部错误：未知的 mimetype 位置 {mimetype_position}")
    write_zip(path, entries)


def get_entry(entries, name: str) -> bytes:
    for entry_name, data, _stored in entries:
        if entry_name == name:
            return data
    raise GenerationError(f"内部错误：包内找不到条目 {name}")


def replace_entry(entries, name: str, data) -> list:
    if isinstance(data, str):
        data = data.encode("utf-8")
    return [(entry_name, data if entry_name == name else old, stored)
            for entry_name, old, stored in entries]


# ---------------------------------------------------------------------------
# EPUB 文档片段
# ---------------------------------------------------------------------------

CSS = """body { font-family: serif; line-height: 1.6; margin: 1.2em; }
h1 { font-size: 1.4em; margin: 0 0 0.8em 0; }
p { margin: 0.4em 0; text-indent: 2em; }
"""

def font_face_css(font_href: str) -> str:
    return (
        '@font-face {\n'
        '  font-family: "TestFont";\n'
        f'  src: url("{font_href}");\n'
        '}\n'
        'body { font-family: "TestFont", serif; }\n'
    )

CH1_BODY = """<h1>第一章 起点</h1>
<p>这是回归样本的第一章。正文里包含中文段落，用来确认阅读器能取到文字。</p>
<p>回归测试需要一份稳定的样本：同样的输入，每次都得到同样的输出。</p>"""

CH2_BODY = """<h1>第二章 归来</h1>
<p>这是回归样本的第二章。两章之间没有目录层级，只按 spine 顺序阅读。</p>
<p>最小 EPUB3 也必须带 dcterms:modified，否则 EpubCheck 会直接报 error。</p>"""


def container_xml(opf_path: str = "OEBPS/content.opf") -> str:
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">\n'
        '  <rootfiles>\n'
        f'    <rootfile full-path="{opf_path}" media-type="application/oebps-package+xml"/>\n'
        '  </rootfiles>\n'
        '</container>\n'
    )


def xhtml3(title: str, body: str, lang: str = LANG, css_href: str | None = "style.css",
           extra_head: str = "") -> str:
    link = (f'  <link rel="stylesheet" type="text/css" href="{css_href}"/>\n'
            if css_href else "")
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<!DOCTYPE html>\n'
        '<html xmlns="http://www.w3.org/1999/xhtml" '
        'xmlns:epub="http://www.idpf.org/2007/ops" '
        f'xml:lang="{lang}" lang="{lang}">\n'
        '<head>\n'
        '  <meta charset="utf-8"/>\n'
        f'  <title>{title}</title>\n'
        f'{link}{extra_head}'
        '</head>\n'
        '<body>\n'
        f'{body}\n'
        '</body>\n'
        '</html>\n'
    )


def xhtml2(title: str, body: str, lang: str = LANG, css_href: str | None = "style.css") -> str:
    link = (f'  <link rel="stylesheet" type="text/css" href="{css_href}"/>\n'
            if css_href else "")
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.1//EN" '
        '"http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd">\n'
        f'<html xmlns="http://www.w3.org/1999/xhtml" xml:lang="{lang}">\n'
        '<head>\n'
        f'  <title>{title}</title>\n'
        '  <meta http-equiv="Content-Type" content="text/html; charset=utf-8"/>\n'
        f'{link}'
        '</head>\n'
        '<body>\n'
        f'{body}\n'
        '</body>\n'
        '</html>\n'
    )


def opf3_document(title: str, author: str, identifier: str, manifest, spine,
                  lang: str = LANG) -> str:
    items = []
    for item in manifest:
        item_id, href, media_type = item[0], item[1], item[2]
        properties = item[3] if len(item) > 3 else ""
        prop_text = f' properties="{properties}"' if properties else ""
        items.append(f'    <item id="{item_id}" href="{href}" media-type="{media_type}"{prop_text}/>')
    refs = "\n".join(f'    <itemref idref="{item_id}"/>' for item_id in spine)
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<package xmlns="http://www.idpf.org/2007/opf" version="3.0" '
        'unique-identifier="bookid" xml:lang="zh-CN">\n'
        '  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">\n'
        f'    <dc:identifier id="bookid">{identifier}</dc:identifier>\n'
        f'    <dc:title>{title}</dc:title>\n'
        f'    <dc:creator>{author}</dc:creator>\n'
        f'    <dc:language>{lang}</dc:language>\n'
        f'    <meta property="dcterms:modified">{MODIFIED}</meta>\n'
        '  </metadata>\n'
        '  <manifest>\n'
        + "\n".join(items) + "\n"
        '  </manifest>\n'
        '  <spine>\n'
        f'{refs}\n'
        '  </spine>\n'
        '</package>\n'
    )


def opf2_document(title: str, author: str, identifier: str, manifest, spine,
                  lang: str = LANG) -> str:
    items = "\n".join(
        f'    <item id="{item_id}" href="{href}" media-type="{media_type}"/>'
        for item_id, href, media_type in manifest
    )
    refs = "\n".join(f'    <itemref idref="{item_id}"/>' for item_id in spine)
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<package xmlns="http://www.idpf.org/2007/opf" version="2.0" '
        'unique-identifier="bookid">\n'
        '  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/" '
        'xmlns:opf="http://www.idpf.org/2007/opf">\n'
        f'    <dc:identifier id="bookid">{identifier}</dc:identifier>\n'
        f'    <dc:title>{title}</dc:title>\n'
        f'    <dc:creator opf:role="aut">{author}</dc:creator>\n'
        f'    <dc:language>{lang}</dc:language>\n'
        '  </metadata>\n'
        '  <manifest>\n'
        f'{items}\n'
        '  </manifest>\n'
        '  <spine toc="ncx">\n'
        f'{refs}\n'
        '  </spine>\n'
        '</package>\n'
    )


def ncx_document(identifier: str, title: str, points) -> str:
    """points：按 NCX 顺序给出的 (label, href) 列表；playOrder 在该顺序上 1..N。"""
    nav_points = []
    for index, (label, href) in enumerate(points, start=1):
        nav_points.append(
            f'    <navPoint id="np{index}" playOrder="{index}">\n'
            f'      <navLabel><text>{label}</text></navLabel>\n'
            f'      <content src="{href}"/>\n'
            '    </navPoint>'
        )
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<!DOCTYPE ncx PUBLIC "-//NISO//DTD ncx 2005-1//EN" '
        '"http://www.daisy.org/z3986/2005/ncx-2005-1.dtd">\n'
        '<ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">\n'
        '  <head>\n'
        f'    <meta name="dtb:uid" content="{identifier}"/>\n'
        '    <meta name="dtb:depth" content="1"/>\n'
        '    <meta name="dtb:totalPageCount" content="0"/>\n'
        '    <meta name="dtb:maxPageNumber" content="0"/>\n'
        '  </head>\n'
        f'  <docTitle><text>{title}</text></docTitle>\n'
        '  <navMap>\n'
        + "\n".join(nav_points) + "\n"
        '  </navMap>\n'
        '</ncx>\n'
    )


def nav_document(items, nav_title: str = "目录") -> str:
    links = "\n".join(
        f'      <li><a href="{href}">{label}</a></li>' for label, href in items
    )
    body = (
        '<nav epub:type="toc" id="toc">\n'
        f'  <h1>{nav_title}</h1>\n'
        '  <ol>\n'
        f'{links}\n'
        '  </ol>\n'
        '</nav>'
    )
    return xhtml3(nav_title, body)


def nested_nav_document(volumes, nav_title: str = "目录") -> str:
    """volumes：[(卷名, [(章名, href), ...]), ...]，构造 卷 > 章 的嵌套 nav。"""
    parts = []
    for volume_title, chapters in volumes:
        links = "\n".join(
            f'        <li><a href="{href}">{label}</a></li>' for label, href in chapters
        )
        parts.append(
            f'    <li><span>{volume_title}</span>\n'
            '      <ol>\n'
            f'{links}\n'
            '      </ol>\n'
            '    </li>'
        )
    body = (
        '<nav epub:type="toc" id="toc">\n'
        f'  <h1>{nav_title}</h1>\n'
        '  <ol>\n'
        + "\n".join(parts) + "\n"
        '  </ol>\n'
        '</nav>'
    )
    return xhtml3(nav_title, body)


# ---------------------------------------------------------------------------
# 图片 / PDF 工具（全部无随机数、无当前时间）
# ---------------------------------------------------------------------------

def _random_bytes(rng: random.Random, size: int) -> bytes:
    try:
        return rng.randbytes(size)
    except AttributeError:  # Python 3.8 兜底
        return rng.getrandbits(size * 8).to_bytes(size, "little")


def load_font(size: int):
    try:
        return ImageFont.load_default(size=size)
    except TypeError:  # Pillow < 10.1 不支持 size 参数
        return ImageFont.load_default()


def draw_centered(draw: ImageDraw.ImageDraw, width: int, y: int, text: str,
                  font, fill) -> None:
    try:
        left, top, right, bottom = draw.textbbox((0, 0), text, font=font)
    except AttributeError:  # 很老的 Pillow
        right, bottom = draw.textsize(text, font=font)
        left, top = 0, 0
    draw.text(((width - (right - left)) / 2 - left, y - top), text, font=font, fill=fill)


def make_solid_jpeg(width: int, height: int, color, quality: int = 85) -> bytes:
    buffer = io.BytesIO()
    Image.new("RGB", (width, height), color).save(
        buffer, "JPEG", quality=quality, optimize=False, progressive=False
    )
    return buffer.getvalue()


def make_cmyk_jpeg(width: int, height: int, color, quality: int = 90) -> bytes:
    buffer = io.BytesIO()
    Image.new("CMYK", (width, height), color).save(
        buffer, "JPEG", quality=quality, optimize=False, progressive=False
    )
    return buffer.getvalue()


def make_small_image(index: int, size: int = 64) -> bytes:
    """A05 用的小图：纯色底 + 白色编号，PNG/JPEG 由调用方决定。"""
    color = PALETTE[(index - 1) % len(PALETTE)]
    image = Image.new("RGB", (size, size), color)
    draw = ImageDraw.Draw(image)
    draw.rectangle([0, 0, size - 1, size - 1], outline=(255, 255, 255), width=2)
    draw_centered(draw, size, size // 2 - 8, str(index), load_font(24), (255, 255, 255))
    buffer = io.BytesIO()
    if index % 2 == 1:
        image.save(buffer, "PNG", optimize=False)
    else:
        image.save(buffer, "JPEG", quality=88, optimize=False, progressive=False)
    return buffer.getvalue()


def make_page_png(number: int, total: int, width: int, height: int) -> bytes:
    """漫画页：纯色底 + 大号页码，确定性 PNG。"""
    color = PALETTE[(number - 1) % len(PALETTE)]
    image = Image.new("RGB", (width, height), color)
    draw = ImageDraw.Draw(image)
    draw.rectangle([12, 12, width - 13, height - 13], outline=(255, 255, 255), width=6)
    draw_centered(draw, width, height // 2 - 80, str(number), load_font(160), (255, 255, 255))
    draw_centered(draw, width, height - 120, f"Page {number} / {total}",
                  load_font(36), (255, 255, 255))
    buffer = io.BytesIO()
    image.save(buffer, "PNG", optimize=False)
    return buffer.getvalue()


def minimal_text_pdf() -> bytes:
    """D02 在 -SkipCalibre 下的兜底来源：一份最小的、带文本层的合法 PDF。

    只用于「取前 60% 字节做损坏 PDF」；正常路径（有 Calibre）仍然用 C02。
    """
    stream = b"BT /F1 18 Tf 72 720 Td (T7-5 fallback corrupt PDF sample) Tj ET\n"
    parts = []
    offsets = []

    body = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")

    def add_object(payload: bytes) -> None:
        offsets.append(len(body))
        body.extend(payload)

    add_object(b"1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n")
    add_object(b"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n")
    add_object(
        b"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        b"/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>\nendobj\n"
    )
    add_object(b"4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n")
    add_object(
        b"5 0 obj\n<< /Length " + str(len(stream)).encode("ascii") + b" >>\nstream\n"
        + stream + b"endstream\nendobj\n"
    )

    xref_offset = len(body)
    xref = b"xref\n0 6\n0000000000 65535 f \n"
    xref += b"".join(f"{offset:010d} 00000 n \n".encode("ascii") for offset in offsets)
    trailer = (
        b"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n"
        + str(xref_offset).encode("ascii")
        + b"\n%%EOF\n"
    )
    return bytes(body + xref + trailer)


# ---------------------------------------------------------------------------
# A 组：EPUB
# ---------------------------------------------------------------------------

def a01_entries() -> list:
    """A01 的包内条目（不含 mimetype）。G01/X01/A11-A18 都由它派生。"""
    identifier = identifier_for("A01")
    manifest = [
        ("nav", "nav.xhtml", "application/xhtml+xml", "nav"),
        ("ch1", "ch1.xhtml", "application/xhtml+xml", ""),
        ("ch2", "ch2.xhtml", "application/xhtml+xml", ""),
        ("css", "style.css", "text/css", ""),
    ]
    opf = opf3_document(TITLE_A01, AUTHOR_A, identifier, manifest, ["ch1", "ch2"])
    nav = nav_document([("第一章 起点", "ch1.xhtml"), ("第二章 归来", "ch2.xhtml")])
    return [
        entry("META-INF/container.xml", container_xml()),
        entry("OEBPS/content.opf", opf),
        entry("OEBPS/nav.xhtml", nav),
        entry("OEBPS/ch1.xhtml", xhtml3("第一章 起点", CH1_BODY)),
        entry("OEBPS/ch2.xhtml", xhtml3("第二章 归来", CH2_BODY)),
        entry("OEBPS/style.css", CSS),
    ]


def build_a01(ctx: "Context", path: Path) -> None:
    entries = a01_entries()
    # 注意：a01_entries() 只返回包内条目，mimetype 必须在这里补上（首条 + STORED）
    data = zip_bytes([("mimetype", MIMETYPE, True)] + entries)
    write_bytes(path, data)
    # 缓存下来：G01 必须是逐字节副本，A11-A18 也从同一份条目派生
    ctx.a01_entries_cache = entries
    ctx.a01_bytes_cache = data


def build_a02(ctx: "Context", path: Path) -> None:
    identifier = identifier_for("A02")
    title = "回归样本-最小 EPUB2"
    cover = make_solid_jpeg(300, 450, (70, 110, 160))
    manifest = [
        ("ncx", "toc.ncx", "application/x-dtbncx+xml"),
        ("ch1", "ch1.xhtml", "application/xhtml+xml"),
        ("ch2", "ch2.xhtml", "application/xhtml+xml"),
        ("cover-img", "Images/cover.jpg", "image/jpeg"),
        ("css", "style.css", "text/css"),
    ]
    opf = opf2_document(title, AUTHOR_A, identifier, manifest, ["ch1", "ch2"])
    opf = opf.replace(
        '    <dc:language>zh-CN</dc:language>\n',
        '    <dc:language>zh-CN</dc:language>\n'
        '    <meta name="cover" content="cover-img"/>\n',
    )
    ncx = ncx_document(identifier, title, [
        ("第一章 起点", "ch1.xhtml"),
        ("第二章 归来", "ch2.xhtml"),
    ])
    entries = [
        entry("META-INF/container.xml", container_xml()),
        entry("OEBPS/content.opf", opf),
        entry("OEBPS/toc.ncx", ncx),
        entry("OEBPS/ch1.xhtml", xhtml2("第一章 起点", CH1_BODY)),
        entry("OEBPS/ch2.xhtml", xhtml2("第二章 归来", CH2_BODY)),
        entry("OEBPS/style.css", CSS),
        entry("OEBPS/Images/cover.jpg", cover),
    ]
    write_epub(path, entries)


def build_a03(ctx: "Context", path: Path) -> None:
    font_path = find_system_font()
    entries = list(a01_entries())
    opf = get_entry(entries, "OEBPS/content.opf").decode("utf-8")
    extra_items = (
        '    <item id="cover-img" href="Images/cover.jpg" media-type="image/jpeg" '
        'properties="cover-image"/>\n'
        '    <item id="font-test" href="Fonts/test.ttf" media-type="application/font-sfnt"/>\n'
    )
    opf = opf.replace("  </manifest>", extra_items + "  </manifest>")
    css = font_face_css("Fonts/test.ttf") + "\n" + CSS
    entries = replace_entry(entries, "OEBPS/content.opf", opf)
    entries = replace_entry(entries, "OEBPS/style.css", css)
    entries.append(entry("OEBPS/Images/cover.jpg", make_solid_jpeg(600, 900, (186, 120, 62))))
    entries.append(entry("OEBPS/Fonts/test.ttf", font_path.read_bytes()))
    write_epub(path, entries)


def find_system_font() -> Path:
    candidates = [
        Path(r"C:\Windows\Fonts\arial.ttf"),
        Path(r"C:\Windows\Fonts\segoeui.ttf"),
    ]
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    raise GenerationError(
        "A03/A08 需要一份真实的系统 TTF 字体，但 C:\\Windows\\Fonts\\arial.ttf 与 "
        "segoeui.ttf 都不存在；请安装任一种系统字体后重试。"
    )


def build_a04(ctx: "Context", path: Path) -> None:
    total = 50
    chapters = []
    manifest = [("nav", "nav.xhtml", "application/xhtml+xml", "nav"),
                ("css", "style.css", "text/css", "")]
    spine = []
    volumes = [
        ("第一卷 起", []),
        ("第二卷 承", []),
    ]
    for number in range(1, total + 1):
        item_id = f"ch{number:02d}"
        file_name = f"{item_id}.xhtml"
        if number > 1:
            previous = (f'<a href="ch{number - 1:02d}.xhtml#sec1">上一章</a>')
        else:
            previous = '<span>这是第一卷的开头</span>'
        if number < total:
            following = (f'<a href="ch{number + 1:02d}.xhtml#sec1">下一章</a>')
        else:
            following = '<span>这是全书的结尾</span>'
        body = (
            f'<h1>第 {number} 章</h1>\n'
            f'<p id="sec1">本章锚点段落：第 {number} 章的正文从这里开始。</p>\n'
            f'<p>章节交叉引用：{previous}　{following}</p>\n'
            f'<p>第 {number} 章的正文继续。这里放一些中文，保证每一章都有实际内容，'
            '也保证 #sec1 锚点在目录与交叉引用里都能被解析。</p>'
        )
        chapters.append(entry(f"OEBPS/{file_name}", xhtml3(f"第 {number} 章", body)))
        manifest.append((item_id, file_name, "application/xhtml+xml", ""))
        spine.append(item_id)
        volume = volumes[0] if number <= 25 else volumes[1]
        volume[1].append((f"第 {number} 章", file_name))
    opf = opf3_document("回归样本-50 章", AUTHOR_A, identifier_for("A04"), manifest, spine)
    nav = nested_nav_document(volumes)
    entries = [
        entry("META-INF/container.xml", container_xml()),
        entry("OEBPS/content.opf", opf),
        entry("OEBPS/nav.xhtml", nav),
        entry("OEBPS/style.css", CSS),
    ] + chapters
    write_epub(path, entries)


def build_a05(ctx: "Context", path: Path) -> None:
    manifest = [
        ("nav", "nav.xhtml", "application/xhtml+xml", "nav"),
        ("ch1", "ch1.xhtml", "application/xhtml+xml", ""),
        ("ch2", "ch2.xhtml", "application/xhtml+xml", ""),
        ("css", "style.css", "text/css", ""),
        ("css-extra", "style-extra.css", "text/css", ""),
    ]
    image_entries = []
    for number in range(1, 26):
        extension = "png" if number % 2 == 1 else "jpg"
        media_type = "image/png" if extension == "png" else "image/jpeg"
        file_name = f"Images/img{number:02d}.{extension}"
        manifest.append((f"img{number:02d}", file_name, media_type, ""))
        image_entries.append(entry(f"OEBPS/{file_name}", make_small_image(number)))

    def image_tags(start: int, end: int) -> str:
        lines = []
        for number in range(start, end + 1):
            extension = "png" if number % 2 == 1 else "jpg"
            lines.append(
                f'<p><img src="Images/img{number:02d}.{extension}" '
                f'alt="图 {number}"/></p>'
            )
        return "\n".join(lines)

    ch1_body = "<h1>第一章 有引用的图</h1>\n" + image_tags(1, 10)
    ch2_body = "<h1>第二章 更多有引用的图</h1>\n" + image_tags(11, 20)
    opf = opf3_document("回归样本-未引用资源", AUTHOR_A, identifier_for("A05"),
                        manifest, ["ch1", "ch2"])
    nav = nav_document([("第一章 有引用的图", "ch1.xhtml"), ("第二章 更多有引用的图", "ch2.xhtml")])
    # style-extra.css 在 manifest 里、但没有任何章节引用它；img21-img25 同理。
    extra_css = "p.note { color: #666; }\n"
    entries = [
        entry("META-INF/container.xml", container_xml()),
        entry("OEBPS/content.opf", opf),
        entry("OEBPS/nav.xhtml", nav),
        entry("OEBPS/ch1.xhtml", xhtml3("第一章 有引用的图", ch1_body)),
        entry("OEBPS/ch2.xhtml", xhtml3("第二章 更多有引用的图", ch2_body)),
        entry("OEBPS/style.css", CSS),
        entry("OEBPS/style-extra.css", extra_css),
    ] + image_entries
    write_epub(path, entries)


def build_a06(ctx: "Context", path: Path) -> None:
    ch1 = """<h1>第一章 标点与换行</h1>
<p>他说：“今天的回归测试通过了。”她回答：‘那就好——不过，别高兴得太早……’</p>
<p>全角标点：，。！？：；（）【】、；半角标点: , . ! ? : ; ( ) [ ].</p>
<p>T7-5 回归（Round 1）：EPUB、MOBI、PDF 与 CBZ 共 40 个样本，通过率 100%。</p>
<p>下面这段模拟 OCR 的硬换行：它本来是一整句话，却被
切成了好几行，每一行都没有标点结尾
读起来是断开的，
需要在批量清理时重新拼回去。</p>
<p>英文单词与中文混排：EbookToolSuite 是 Windows 桌面应用，使用 .NET 8 与 WebView2；数字 12345 与书名《回归样本》也都保留。</p>"""
    ch2 = """<h1>第二章 更多混排</h1>
<p>半角逗号,半角句号.全角逗号，全角句号。两者混在一起：A，B, C。D</p>
<p>引号测试：“双引号”与‘单引号’，以及英文的 "double" 与 'single'。</p>
<p>这一行同样在句子中间
被硬换行切断，
用来验证正文保真与清理规则不会误删内容。</p>"""
    manifest = [
        ("nav", "nav.xhtml", "application/xhtml+xml", "nav"),
        ("ch1", "ch1.xhtml", "application/xhtml+xml", ""),
        ("ch2", "ch2.xhtml", "application/xhtml+xml", ""),
        ("css", "style.css", "text/css", ""),
    ]
    opf = opf3_document("回归样本-中文标点", AUTHOR_A, identifier_for("A06"),
                        manifest, ["ch1", "ch2"])
    nav = nav_document([("第一章 标点与换行", "ch1.xhtml"), ("第二章 更多混排", "ch2.xhtml")])
    entries = [
        entry("META-INF/container.xml", container_xml()),
        entry("OEBPS/content.opf", opf),
        entry("OEBPS/nav.xhtml", nav),
        entry("OEBPS/ch1.xhtml", xhtml3("第一章 标点与换行", ch1)),
        entry("OEBPS/ch2.xhtml", xhtml3("第二章 更多混排", ch2)),
        entry("OEBPS/style.css", CSS),
    ]
    write_epub(path, entries)


def build_a07(ctx: "Context", path: Path) -> None:
    """无 nav / 无 NCX，只有 spine。原始样本不符合 EPUB3 的 nav 要求（validate 由保存后的产物负责）。"""
    manifest = []
    spine = []
    for number in range(1, 4):
        item_id = f"ch{number}"
        file_name = f"{item_id}.xhtml"
        manifest.append((item_id, file_name, "application/xhtml+xml", ""))
        spine.append(item_id)
    manifest.append(("css", "style.css", "text/css", ""))
    opf = opf3_document("回归样本-无目录", AUTHOR_A, identifier_for("A07"), manifest, spine)
    chapters = [
        entry(f"OEBPS/ch{number}.xhtml",
              xhtml3(f"第 {number} 章", f"<h1>第 {number} 章</h1>\n<p>没有目录，只有 spine 的第 {number} 章正文。</p>"))
        for number in range(1, 4)
    ]
    entries = [
        entry("META-INF/container.xml", container_xml()),
        entry("OEBPS/content.opf", opf),
    ] + chapters + [entry("OEBPS/style.css", CSS)]
    write_epub(path, entries)


def idpf_obfuscate(data: bytes, identifier: str) -> bytes:
    """IDPF 字体混淆：key = SHA-1(uid).digest()（20 字节），对前 1040 字节循环 XOR。

    任务书写的「SHA-1 的前 104 字节」与 SHA-1 的 20 字节长度不符；
    这里按 IDPF 规范（OCF 3.0 / epubfontobfuscation）实现，才能和真实电子书一致。
    """
    key = hashlib.sha1(identifier.strip().encode("utf-8")).digest()
    payload = bytearray(data)
    limit = min(1040, len(payload))
    for index in range(limit):
        payload[index] ^= key[index % len(key)]
    return bytes(payload)


def build_a08(ctx: "Context", path: Path) -> None:
    identifier = identifier_for("A08")
    font_path = find_system_font()
    obfuscated = idpf_obfuscate(font_path.read_bytes(), identifier)
    ch1 = """<h1>第一章 矢量图与语言标记</h1>
<p xml:lang="zh-CN" lang="zh-CN">这一段显式标注了 xml:lang，用来测试语言属性保真。</p>
<p>下面是一张内联 SVG：</p>
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 100" width="200" height="100" role="img">
  <title>回归样本内联插图</title>
  <rect x="5" y="5" width="190" height="90" fill="#dbeafe" stroke="#1d4ed8" stroke-width="2"/>
</svg>"""
    ch2 = """<h1>第二章 普通章节</h1>
<p>这一章没有 SVG，用来对照第一章的矢量图解析。</p>
<p>字体在 CSS 里通过 @font-face 引用，并在 encryption.xml 里声明为 IDPF 混淆。</p>"""
    manifest = [
        ("nav", "nav.xhtml", "application/xhtml+xml", "nav"),
        # 第一章含内联 <svg>，EPUB3 要求在 manifest 的 item 上声明 properties="svg"
        ("ch1", "ch1.xhtml", "application/xhtml+xml", "svg"),
        ("ch2", "ch2.xhtml", "application/xhtml+xml", ""),
        ("css", "style.css", "text/css", ""),
        ("font", "Fonts/font.ttf", "application/font-sfnt", ""),
    ]
    opf = opf3_document("回归样本-SVG 与字体混淆", AUTHOR_A, identifier, manifest, ["ch1", "ch2"])
    nav = nav_document([("第一章 矢量图与语言标记", "ch1.xhtml"), ("第二章 普通章节", "ch2.xhtml")])
    encryption = (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container"\n'
        '            xmlns:enc="http://www.w3.org/2001/04/xmlenc#">\n'
        '  <enc:EncryptedData>\n'
        '    <enc:EncryptionMethod Algorithm="http://www.idpf.org/2008/embedding"/>\n'
        '    <enc:CipherData>\n'
        '      <enc:CipherReference URI="OEBPS/Fonts/font.ttf"/>\n'
        '    </enc:CipherData>\n'
        '  </enc:EncryptedData>\n'
        '</encryption>\n'
    )
    entries = [
        entry("META-INF/container.xml", container_xml()),
        entry("META-INF/encryption.xml", encryption),
        entry("OEBPS/content.opf", opf),
        entry("OEBPS/nav.xhtml", nav),
        entry("OEBPS/ch1.xhtml", xhtml3("第一章 矢量图与语言标记", ch1)),
        entry("OEBPS/ch2.xhtml", xhtml3("第二章 普通章节", ch2)),
        entry("OEBPS/style.css", font_face_css("Fonts/font.ttf") + "\n" + CSS),
        entry("OEBPS/Fonts/font.ttf", obfuscated),
    ]
    write_epub(path, entries)


A09_PARAGRAPH = (
    "这是回归样本第{n}章的正文。为了让大书样本足够真实，这一段文字会重复若干次，"
    "使单章正文接近一千字节。中文字符在 UTF-8 下通常占三个字节，因此三百多个字符"
    "正好约一千字节。这里再补一句用于凑足长度的话，确保章节大小稳定，也让分页与"
    "首屏测试拿到足够长的文本。"
)


def build_a09(ctx: "Context", path: Path) -> None:
    total = 2000
    chapters = []
    manifest = [("nav", "nav.xhtml", "application/xhtml+xml", "nav"),
                ("css", "style.css", "text/css", "")]
    spine = []
    nav_items = []
    for number in range(1, total + 1):
        item_id = f"ch{number:04d}"
        file_name = f"{item_id}.xhtml"
        paragraph = A09_PARAGRAPH.format(n=number)
        body = f"<h1>第 {number} 章</h1>\n" + "\n".join(
            f"<p>{paragraph}</p>" for _ in range(2)
        )
        chapters.append(entry(f"OEBPS/{file_name}", xhtml3(f"第 {number} 章", body)))
        manifest.append((item_id, file_name, "application/xhtml+xml", ""))
        spine.append(item_id)
        nav_items.append((f"第 {number} 章", file_name))
    opf = opf3_document("回归样本-2000 章", AUTHOR_A, identifier_for("A09"), manifest, spine)
    entries = [
        entry("META-INF/container.xml", container_xml()),
        entry("OEBPS/content.opf", opf),
        entry("OEBPS/nav.xhtml", nav_document(nav_items)),
        entry("OEBPS/style.css", CSS),
    ] + chapters
    write_epub(path, entries)


def build_a10(ctx: "Context", path: Path) -> None:
    rng = random.Random(20260101)  # 固定种子：同样的 10 张高熵图
    width, height = 2000, 3000
    image_entries = []
    image_tags = []
    manifest = [("nav", "nav.xhtml", "application/xhtml+xml", "nav"),
                ("ch1", "ch1.xhtml", "application/xhtml+xml", ""),
                ("css", "style.css", "text/css", "")]
    for number in range(1, 11):
        payload = _random_bytes(rng, width * height * 3)
        image = Image.frombytes("RGB", (width, height), payload)
        buffer = io.BytesIO()
        image.save(buffer, "JPEG", quality=90, optimize=False, progressive=False)
        file_name = f"Images/img{number:02d}.jpg"
        image_entries.append(entry(f"OEBPS/{file_name}", buffer.getvalue()))
        image_tags.append(f'<p><img src="{file_name}" alt="高熵插图 {number}"/></p>')
        manifest.append((f"img{number:02d}", file_name, "image/jpeg", ""))
    body = '<h1>插图集</h1>\n<p>这个样本约 50 MB，用来测试大文件导入、内存与取消。</p>\n' + "\n".join(image_tags)
    opf = opf3_document("回归样本-约 50MB 图片", AUTHOR_A, identifier_for("A10"),
                        manifest, ["ch1"])
    nav = nav_document([("插图集", "ch1.xhtml")])
    entries = [
        entry("META-INF/container.xml", container_xml()),
        entry("OEBPS/content.opf", opf),
        entry("OEBPS/nav.xhtml", nav),
        entry("OEBPS/ch1.xhtml", xhtml3("插图集", body)),
        entry("OEBPS/style.css", CSS),
    ] + image_entries
    write_epub(path, entries)


def build_a11(ctx: "Context", path: Path) -> None:
    data = ctx.ensure_a01_bytes()
    write_bytes(path, data[: int(len(data) * 0.6)])


def build_a12(ctx: "Context", path: Path) -> None:
    write_epub(path, list(ctx.ensure_a01_entries()), mimetype_position="last")


def build_a13(ctx: "Context", path: Path) -> None:
    write_epub(path, list(ctx.ensure_a01_entries()), mimetype_deflated=True)


def build_a14(ctx: "Context", path: Path) -> None:
    """文件在包里、manifest 没声明（孤儿资源）。

    做法：先给 A01 的 OPF 加一个封面 item，再把它删掉——净结果就是
    "A01 + 一个未声明的封面文件"；因为 item 被删掉，也不会留下悬空引用。
    """
    entries = list(ctx.ensure_a01_entries())
    opf = get_entry(entries, "OEBPS/content.opf").decode("utf-8")
    cover_item = (
        '    <item id="cover-img" href="Images/cover.jpg" media-type="image/jpeg" '
        'properties="cover-image"/>\n'
    )
    opf_with_item = opf.replace("  </manifest>", cover_item + "  </manifest>")
    opf_orphan = opf_with_item.replace(cover_item, "")
    if opf_orphan != opf:
        raise GenerationError("内部错误：A14 删除封面 item 后 OPF 与 A01 不一致。")
    entries.append(entry("OEBPS/Images/cover.jpg", make_solid_jpeg(600, 900, (90, 140, 90))))
    write_epub(path, entries)


def build_a15(ctx: "Context", path: Path) -> None:
    entries = list(ctx.ensure_a01_entries())
    opf = get_entry(entries, "OEBPS/content.opf").decode("utf-8")
    missing_item = (
        '    <item id="missing" href="Text/missing.xhtml" '
        'media-type="application/xhtml+xml"/>\n'
    )
    opf = opf.replace("  </manifest>", missing_item + "  </manifest>")
    opf = opf.replace("  </spine>", '    <itemref idref="missing"/>\n  </spine>')
    entries = replace_entry(entries, "OEBPS/content.opf", opf)
    write_epub(path, entries)


def build_a16(ctx: "Context", path: Path) -> None:
    identifier = identifier_for("A16")
    title = "回归样本-spine 与 NCX 顺序不一致"
    manifest = [
        ("ncx", "toc.ncx", "application/x-dtbncx+xml"),
        ("ch1", "ch1.xhtml", "application/xhtml+xml"),
        ("ch2", "ch2.xhtml", "application/xhtml+xml"),
        ("ch3", "ch3.xhtml", "application/xhtml+xml"),
        ("css", "style.css", "text/css"),
    ]
    opf = opf2_document(title, AUTHOR_A, identifier, manifest, ["ch1", "ch2", "ch3"])
    # spine 顺序 1,2,3；NCX 顺序故意写成 3,1,2
    ncx = ncx_document(identifier, title, [
        ("第三章 终局", "ch3.xhtml"),
        ("第一章 起点", "ch1.xhtml"),
        ("第二章 归来", "ch2.xhtml"),
    ])
    chapters = []
    for number in range(1, 4):
        body = f"<h1>第 {number} 章</h1>\n<p>这是第 {number} 章。spine 与 NCX 的顺序故意不一致。</p>"
        chapters.append(entry(f"OEBPS/ch{number}.xhtml", xhtml2(f"第 {number} 章", body)))
    entries = [
        entry("META-INF/container.xml", container_xml()),
        entry("OEBPS/content.opf", opf),
        entry("OEBPS/toc.ncx", ncx),
    ] + chapters + [entry("OEBPS/style.css", CSS)]
    write_epub(path, entries)


def build_a17(ctx: "Context", path: Path) -> None:
    entries = list(ctx.ensure_a01_entries())
    ch2 = get_entry(entries, "OEBPS/ch2.xhtml").decode("utf-8")
    # 故意留一个未闭合的 <p>，让第 2 章 XHTML 不是 well-formed XML
    broken = ch2.replace("</body>", "<p>没闭合\n</body>")
    if broken == ch2:
        raise GenerationError("内部错误：A17 未能注入未闭合标签。")
    entries = replace_entry(entries, "OEBPS/ch2.xhtml", broken)
    write_epub(path, entries)


def build_a18(ctx: "Context", path: Path) -> None:
    entries = list(ctx.ensure_a01_entries())
    entries = replace_entry(entries, "META-INF/container.xml", container_xml("OEBPS/nope.opf"))
    # 真正的 OEBPS/content.opf 仍然留在包里，只是 container 不再指向它
    write_epub(path, entries)


# ---------------------------------------------------------------------------
# B 组：TXT（编码是重点）
# ---------------------------------------------------------------------------

B_COMMON = (
    "第一章 回归测试\n"
    "\n"
    "这是 T7-5 全量回归使用的中文样例文本。它包含常见的中文标点，也混合 English words 和数字 12345。\n"
    "第二段用来确认编码检测能正确识别整篇文本，而不是只看第一行。\n"
    "第三段包含书名号《回归样本》、引号“测试”和省略号……\n"
    "最后一段写一句普通的话，方便肉眼核对解码结果是否正常。\n"
)


def build_b01(ctx: "Context", path: Path) -> None:
    write_bytes(path, B_COMMON.encode("utf-8"))


def build_b02(ctx: "Context", path: Path) -> None:
    write_bytes(path, B_COMMON.encode("gbk"))


def build_b03(ctx: "Context", path: Path) -> None:
    text = B_COMMON + "\n扩展区字符（仅 GB18030 可编码）：𠀀𠀁\n"
    write_bytes(path, text.encode("gb18030"))


def build_b04(ctx: "Context", path: Path) -> None:
    write_bytes(path, b"\xff\xfe" + B_COMMON.encode("utf-16-le"))


def build_b05(ctx: "Context", path: Path) -> None:
    write_bytes(path, b"\xef\xbb\xbf" + B_COMMON.encode("utf-8"))


B06_TEXT = """序言
这是序言的正文，用来确认「序言」这种不带编号的标题也能切章。

第一章 起点
第一章的正文。这里写几句普通的中文，让切章器有内容可切。

第二回 归来
第二回的正文。注意这里用的是「回」而不是「章」。

Chapter 3 The Middle
English heading body. 这一行仍然是中文正文，用来确认中英混排。

番外 一
番外的正文，通常出现在正文结束之后。

卷一 风起
卷一的正文。卷标题与章标题混在一起，用来测试正则的优先级。

楔子
楔子的正文，一般放在全书最前面。

尾声
尾声的正文，一般放在全书最后面。

第三章 终局
第三章的正文。这里再写几句，保证切出来的章节不是空的。

Chapter 10 End
The final chapter body. 这一章同样带中文，避免只剩英文。
"""


def build_b06(ctx: "Context", path: Path) -> None:
    write_bytes(path, B06_TEXT.encode("utf-8"))


_B07_SENTENCES = [
    "夜色落在旧城的屋檐上，风从巷口穿过，带来远处河面的潮气。",
    "他沿着青石路慢慢走，脚步声被两侧的高墙来回送远。",
    "街角那家旧书店还亮着灯，玻璃门上贴着一张褪色的海报。",
    "店主抬头看了他一眼，又低头继续整理手里的旧书。",
    "书页翻动的声音很轻，却在这间小店里显得格外清楚。",
    "他想起很多年前第一次来这里，那时架子上还没有这么多书。",
    "窗外的雨忽然大了起来，雨点敲在遮雨棚上，像一串急促的鼓点。",
    "他把手里的伞收好，靠在门边，随手抽出一本没有封面的书。",
]


def build_b07(ctx: "Context", path: Path) -> None:
    target = 50 * 1024
    lines = []
    total = 0
    index = 0
    while total < target:
        paragraph = (
            _B07_SENTENCES[index % len(_B07_SENTENCES)]
            + _B07_SENTENCES[(index + 1) % len(_B07_SENTENCES)]
            + _B07_SENTENCES[(index + 2) % len(_B07_SENTENCES)]
        )
        lines.append(paragraph)
        total += len(paragraph.encode("utf-8")) + 1
        index += 1
    write_bytes(path, ("\n".join(lines) + "\n").encode("utf-8"))


def build_b08(ctx: "Context", path: Path) -> None:
    line = "这是一行没有任何换行符的中文文本，用来测试单行文件的导入边界；" * 12
    write_bytes(path, line.encode("utf-8"))


def build_b09(ctx: "Context", path: Path) -> None:
    write_bytes(path, b"")


# ---------------------------------------------------------------------------
# C 组：真 Calibre 派生（字节可变，manifest 记录本次实际值）
# ---------------------------------------------------------------------------

def find_calibre() -> Path | None:
    candidates = [
        REPO_ROOT / "Tools" / "Calibre Portable" / "Calibre" / "ebook-convert.exe",
        REPO_ROOT / "Tools" / "Calibre" / "ebook-convert.exe",
    ]
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    return None


def decode_process_output(data: bytes) -> str:
    for encoding in ("utf-8", "cp936", "cp1252"):
        try:
            return data.decode(encoding)
        except UnicodeDecodeError:
            continue
    return data.decode("utf-8", errors="replace")


def run_calibre(calibre: Path, arguments, output_path: Path) -> None:
    # Calibre 不会替我们创建输出目录，先建好（例如 C\）。
    ensure_parent(output_path)
    environment = os.environ.copy()
    environment["CALIBRE_OVERRIDE_LANG"] = "en"  # 固定 Calibre 的诊断语言，便于解析失败原因
    command = [str(calibre)] + [str(item) for item in arguments]
    try:
        completed = subprocess.run(
            command,
            capture_output=True,
            env=environment,
            timeout=600,
        )
    except subprocess.TimeoutExpired as exc:
        raise GenerationError(
            "Calibre 超过 10 分钟没有结束，已中断：" + " ".join(command)
        ) from exc
    except OSError as exc:
        raise GenerationError(f"无法启动 Calibre（{calibre}）：{exc}") from exc

    if completed.returncode != 0:
        try:
            if output_path.is_file():
                output_path.unlink()
        except OSError:
            pass
        text = decode_process_output(completed.stderr or b"")
        if not text.strip():
            text = decode_process_output(completed.stdout or b"")
        tail = "\n".join(text.splitlines()[-30:])
        raise GenerationError(
            f"Calibre 生成失败（退出码 {completed.returncode}）：{' '.join(command)}\n"
            f"—— stderr 末 30 行 ——\n{tail}"
        )

    if not output_path.is_file() or output_path.stat().st_size == 0:
        raise GenerationError(
            f"Calibre 退出码为 0，但没有产出有效文件：{output_path}"
        )


def build_c01(ctx: "Context", path: Path) -> None:
    calibre = ctx.require_calibre()
    source = ctx.require_a01_file()
    # Calibre 强制要求「输入、输出」是前两个位置参数；选项必须排在输出文件之后。
    # 选项本身与设计文档一致：--output-profile kindle_pw3 --mobi-file-type new。
    run_calibre(calibre, [
        source,
        path,
        "--output-profile", "kindle_pw3",
        "--mobi-file-type", "new",
    ], path)


def build_c02(ctx: "Context", path: Path) -> None:
    calibre = ctx.require_calibre()
    source = ctx.require_a01_file()
    run_calibre(calibre, [
        source,
        path,
        "--paper-size", "a4",
        "--pdf-page-numbers",
    ], path)


# ---------------------------------------------------------------------------
# D 组：PDF
# ---------------------------------------------------------------------------

def build_d01(ctx: "Context", path: Path) -> None:
    pages = []
    font_big = load_font(180)
    font_small = load_font(40)
    for number in range(1, 21):
        image = Image.new("RGB", (1240, 1754), (255, 255, 255))
        draw = ImageDraw.Draw(image)
        draw.rectangle([40, 40, 1199, 1713], outline=(205, 205, 205), width=4)
        draw_centered(draw, 1240, 780, str(number), font_big, (25, 25, 25))
        draw_centered(draw, 1240, 1040, f"Page {number} / 20", font_small, (90, 90, 90))
        pages.append(image)

    fixed_time = time.struct_time((2026, 1, 1, 0, 0, 0, 0, 0, 0))
    ensure_parent(path)
    # 把 CreationDate / ModDate 也钉死，保证同一份代码重复生成字节一致
    pages[0].save(
        str(path),
        "PDF",
        save_all=True,
        append_images=pages[1:],
        resolution=150.0,
        title="T7-5 扫描件样本",
        creationDate=fixed_time,
        modDate=fixed_time,
    )


def build_d02(ctx: "Context", path: Path) -> None:
    c02_path = ctx.out_dir / "C" / "c-text.pdf"
    if c02_path.is_file():
        source = c02_path.read_bytes()
    else:
        # -SkipCalibre 时 C02 不存在；用内置的最小文本 PDF 保证 D02 仍然可构造。
        source = minimal_text_pdf()
        print("  提示：未启用 Calibre，D02 改用内置最小文本 PDF 作为截断源"
              "（正常路径应由 C02 派生）。")
    write_bytes(path, source[: int(len(source) * 0.6)])


# ---------------------------------------------------------------------------
# E 组：漫画
# ---------------------------------------------------------------------------

def e01_entries() -> list:
    entries = []
    for number in range(1, 21):
        entries.append(entry(
            f"{number:03d}.png",
            make_page_png(number, 20, 800, 1200),
            stored=True,
        ))
    return entries


def build_e01(ctx: "Context", path: Path) -> None:
    entries = e01_entries()
    data = zip_bytes(entries)
    write_bytes(path, data)
    ctx.e01_entries_cache = entries
    ctx.e01_bytes_cache = data


def build_e02(ctx: "Context", path: Path) -> None:
    """RTL 命名：页 i 存成 m{i:02d}.png，但条目写入顺序是 m20 -> m01。

    读取端按文件名自然排序得到 m01..m20，正好还原成第 1..20 页；
    「从右到左」是阅读器的视图状态，不是文件名本身倒序。
    """
    entries = []
    for number in range(20, 0, -1):
        entries.append(entry(
            f"m{number:02d}.png",
            make_page_png(number, 20, 800, 1200),
            stored=True,
        ))
    write_bytes(path, zip_bytes(entries))


def build_e03(ctx: "Context", path: Path) -> None:
    data = ctx.ensure_e01_bytes()
    write_bytes(path, data[: int(len(data) * 0.6)])


# ---------------------------------------------------------------------------
# G 组：去重
# ---------------------------------------------------------------------------

def build_g01(ctx: "Context", path: Path) -> None:
    # 逐字节副本：直接复用 A01 已经算好的 ZIP 字节
    write_bytes(path, ctx.ensure_a01_bytes())


def build_g02(ctx: "Context", path: Path) -> None:
    ch1 = """<h1>第一章 另一条时间线</h1>
<p>这本书与 A01 同名，但作者和正文都不同，用来测试模糊去重不误判。</p>
<p>同名不同作者在书库里是两本书，不能被当成重复文件。</p>"""
    ch2 = """<h1>第二章 另一种结局</h1>
<p>这一章的正文与最小样本完全不一样，方便肉眼核对。</p>"""
    manifest = [
        ("nav", "nav.xhtml", "application/xhtml+xml", "nav"),
        ("ch1", "ch1.xhtml", "application/xhtml+xml", ""),
        ("ch2", "ch2.xhtml", "application/xhtml+xml", ""),
        ("css", "style.css", "text/css", ""),
    ]
    opf = opf3_document(TITLE_A01, AUTHOR_B, identifier_for("G02"), manifest, ["ch1", "ch2"])
    nav = nav_document([("第一章 另一条时间线", "ch1.xhtml"), ("第二章 另一种结局", "ch2.xhtml")])
    entries = [
        entry("META-INF/container.xml", container_xml()),
        entry("OEBPS/content.opf", opf),
        entry("OEBPS/nav.xhtml", nav),
        entry("OEBPS/ch1.xhtml", xhtml3("第一章 另一条时间线", ch1)),
        entry("OEBPS/ch2.xhtml", xhtml3("第二章 另一种结局", ch2)),
        entry("OEBPS/style.css", CSS),
    ]
    write_epub(path, entries)


# ---------------------------------------------------------------------------
# H 组：封面
# ---------------------------------------------------------------------------

def build_cover_sample(path: Path, sample_id: str, title: str, cover_bytes: bytes) -> None:
    manifest = [
        ("nav", "nav.xhtml", "application/xhtml+xml", "nav"),
        ("ch1", "ch1.xhtml", "application/xhtml+xml", ""),
        ("cover-img", "Images/cover.jpg", "image/jpeg", "cover-image"),
        ("css", "style.css", "text/css", ""),
    ]
    opf = opf3_document(title, AUTHOR_A, identifier_for(sample_id), manifest, ["ch1"])
    nav = nav_document([("第一章 封面之下", "ch1.xhtml")])
    body = ("<h1>第一章 封面之下</h1>\n<p>这本样本的重点是封面，不是正文。</p>")
    entries = [
        entry("META-INF/container.xml", container_xml()),
        entry("OEBPS/content.opf", opf),
        entry("OEBPS/nav.xhtml", nav),
        entry("OEBPS/ch1.xhtml", xhtml3("第一章 封面之下", body)),
        entry("OEBPS/style.css", CSS),
        entry("OEBPS/Images/cover.jpg", cover_bytes),
    ]
    write_epub(path, entries)


def build_h01(ctx: "Context", path: Path) -> None:
    build_cover_sample(path, "H01", "回归样本-超大封面",
                       make_solid_jpeg(4000, 6000, (48, 92, 160), quality=82))


def build_h02(ctx: "Context", path: Path) -> None:
    build_cover_sample(path, "H02", "回归样本-极小封面",
                       make_solid_jpeg(50, 50, (200, 80, 70), quality=90))


def build_h03(ctx: "Context", path: Path) -> None:
    build_cover_sample(path, "H03", "回归样本-CMYK 封面",
                       make_cmyk_jpeg(600, 900, (0, 120, 120, 10), quality=90))


# ---------------------------------------------------------------------------
# X 组：DRM 负例
# ---------------------------------------------------------------------------

def build_x01(ctx: "Context", path: Path) -> None:
    entries = list(ctx.ensure_a01_entries())
    encryption = (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container"\n'
        '            xmlns:enc="http://www.w3.org/2001/04/xmlenc#">\n'
        '  <enc:EncryptedData>\n'
        '    <enc:EncryptionMethod '
        'Algorithm="http://www.w3.org/2001/04/xmlenc#aes128-cbc"/>\n'
        '    <enc:CipherData>\n'
        '      <enc:CipherReference URI="OEBPS/ch1.xhtml"/>\n'
        '    </enc:CipherData>\n'
        '  </enc:EncryptedData>\n'
        '</encryption>\n'
    )
    # A01 的副本 + META-INF/encryption.xml（AES 指向正文 = 真 DRM）
    result = [entries[0], entry("META-INF/encryption.xml", encryption)] + entries[1:]
    write_epub(path, result)


# ---------------------------------------------------------------------------
# 生成上下文与注册表
# ---------------------------------------------------------------------------

BUILDERS = {
    "A01": build_a01,
    "A02": build_a02,
    "A03": build_a03,
    "A04": build_a04,
    "A05": build_a05,
    "A06": build_a06,
    "A07": build_a07,
    "A08": build_a08,
    "A09": build_a09,
    "A10": build_a10,
    "A11": build_a11,
    "A12": build_a12,
    "A13": build_a13,
    "A14": build_a14,
    "A15": build_a15,
    "A16": build_a16,
    "A17": build_a17,
    "A18": build_a18,
    "B01": build_b01,
    "B02": build_b02,
    "B03": build_b03,
    "B04": build_b04,
    "B05": build_b05,
    "B06": build_b06,
    "B07": build_b07,
    "B08": build_b08,
    "B09": build_b09,
    "C01": build_c01,
    "C02": build_c02,
    "D01": build_d01,
    "D02": build_d02,
    "E01": build_e01,
    "E02": build_e02,
    "E03": build_e03,
    "G01": build_g01,
    "G02": build_g02,
    "H01": build_h01,
    "H02": build_h02,
    "H03": build_h03,
    "X01": build_x01,
}


class Context:
    def __init__(self, out_dir: Path, skip_calibre: bool):
        self.out_dir = out_dir
        self.skip_calibre = skip_calibre
        self.calibre = find_calibre()
        self.a01_entries_cache = None
        self.a01_bytes_cache = None
        self.e01_entries_cache = None
        self.e01_bytes_cache = None

    # -- A01 派生缓存的懒构造（-Only 时不必先落盘 A01） -------------------

    def ensure_a01_entries(self) -> list:
        if self.a01_entries_cache is None:
            self.a01_entries_cache = a01_entries()
        return self.a01_entries_cache

    def ensure_a01_bytes(self) -> bytes:
        if self.a01_bytes_cache is None:
            # 与 A01 落盘时完全同一条构造路径：mimetype 首条 + STORED
            self.a01_bytes_cache = zip_bytes(
                [("mimetype", MIMETYPE, True)] + self.ensure_a01_entries()
            )
        return self.a01_bytes_cache

    def ensure_e01_bytes(self) -> bytes:
        if self.e01_bytes_cache is None:
            self.e01_entries_cache = e01_entries()
            self.e01_bytes_cache = zip_bytes(self.e01_entries_cache)
        return self.e01_bytes_cache

    # -- C 组依赖 ---------------------------------------------------------

    def require_calibre(self) -> Path:
        if self.calibre is None:
            raise GenerationError(
                "C 组需要真 Calibre，但没有找到 ebook-convert.exe。\n"
                "已尝试：Tools\\Calibre Portable\\Calibre\\ebook-convert.exe 与 "
                "Tools\\Calibre\\ebook-convert.exe。\n"
                "没有 Calibre 的环境请加 -SkipCalibre。"
            )
        return self.calibre

    def require_a01_file(self) -> Path:
        source = self.out_dir / "A" / "epub3-min.epub"
        if not source.is_file():
            raise GenerationError(
                f"C 组需要 A01 作为转换源，但 {source} 不存在。"
            )
        return source


# ---------------------------------------------------------------------------
# catalog / 选择 / manifest
# ---------------------------------------------------------------------------

def load_catalog() -> dict:
    if not CATALOG_PATH.is_file():
        raise GenerationError(f"找不到样本目录：{CATALOG_PATH}")
    try:
        catalog = json.loads(read_text(CATALOG_PATH))
    except (json.JSONDecodeError, UnicodeDecodeError) as exc:
        raise GenerationError(f"样本目录不是合法 JSON：{CATALOG_PATH}（{exc}）") from exc
    samples = catalog.get("samples")
    if not isinstance(samples, list) or not samples:
        raise GenerationError(f"样本目录缺少 samples 数组：{CATALOG_PATH}")
    return catalog


def resolve_selection(samples, only_text: str, skip_calibre: bool):
    """返回 (选中 id 集合, 自动补造的说明列表)。"""
    all_ids = [sample["id"] for sample in samples]
    if not only_text.strip():
        return set(all_ids), []

    requested = []
    for part in only_text.split(","):
        sample_id = part.strip().upper()
        if not sample_id:
            continue
        if sample_id not in all_ids:
            raise GenerationError(
                f"-Only 里的样本 id 不在 catalog 中：{sample_id}。"
                f"可用的 id：{', '.join(all_ids)}"
            )
        if sample_id not in requested:
            requested.append(sample_id)

    if not requested:
        return set(all_ids), []

    selected = set(requested)
    auto = []

    def add(sample_id: str, reason: str) -> None:
        if sample_id not in selected:
            selected.add(sample_id)
            auto.append(f"{sample_id}（{reason}）")

    # C 组需要一份真实的 A01 文件作为 Calibre 输入，必须补造并落盘。
    if (selected & {"C01", "C02"}) and not skip_calibre:
        add("A01", "C 组需要 A01 作转换源")

    # D02 需要 C02 的前 60%；C 组又需要 A01。
    if "D02" in selected and not skip_calibre and "C02" not in selected:
        add("C02", "D02 需要 C02 作截断源")
        add("A01", "C 组需要 A01 作转换源")

    return selected, auto


def manifest_covers(out_dir: Path, expected, require_exact_ids: bool, skip_calibre: bool) -> bool:
    """已有 manifest 是否覆盖本次选择，且文件字节/哈希都对得上。"""
    manifest_path = out_dir / "manifest.json"
    if not manifest_path.is_file():
        return False
    try:
        data = json.loads(read_text(manifest_path))
    except (json.JSONDecodeError, UnicodeDecodeError, OSError):
        return False
    entries = data.get("samples")
    if not isinstance(entries, list):
        return False
    expected_ids = [sample["id"] for sample in expected]
    actual_ids = [item.get("id") for item in entries]
    if require_exact_ids:
        if actual_ids != expected_ids:
            return False
    else:
        if any(sample_id not in actual_ids for sample_id in expected_ids):
            return False
    by_id = {item.get("id"): item for item in entries}
    for sample in expected:
        sample_id = sample["id"]
        item = by_id.get(sample_id)
        if item is None:
            return False
        should_skip = bool(skip_calibre and sample_id in CALIBRE_IDS)
        if bool(item.get("skipped")) != should_skip:
            return False
        path = out_dir / sample["file"]
        if should_skip:
            if path.exists():
                return False
            continue
        if not path.is_file():
            return False
        if item.get("bytes") != path.stat().st_size:
            return False
        if item.get("sha256") != sha256_file(path):
            return False
    return True


def build_manifest_entry(sample: dict, path: Path, skipped: bool) -> dict:
    item = dict(sample)  # 保留 catalog 原字段与顺序
    if skipped:
        item["bytes"] = 0
        item["sha256"] = None
        item["skipped"] = True
    else:
        data = path.read_bytes()
        item["bytes"] = len(data)
        item["sha256"] = sha256_bytes(data)
    return item


# ---------------------------------------------------------------------------
# 输出目录安全与重建
# ---------------------------------------------------------------------------

def resolve_output_dir(text: str) -> Path:
    path = Path(text).expanduser()
    if not path.is_absolute():
        path = Path.cwd() / path
    path = path.resolve()

    forbidden = {
        Path.cwd().resolve(),
        REPO_ROOT.resolve(),
        SCRIPT_DIR.resolve(),
        Path.home().resolve(),
    }
    if path in forbidden or path.parent == path:
        raise GenerationError(
            f"拒绝把输出目录设为 {path}：这会清空不该动的目录。"
            "请换一个专用子目录（默认是 Data-Test\\t75-corpus）。"
        )
    if path in REPO_ROOT.resolve().parents:
        raise GenerationError(
            f"拒绝把输出目录设为仓库根的上级目录：{path}"
        )
    return path


# ---------------------------------------------------------------------------
# 自检
# ---------------------------------------------------------------------------

def zip_opens_cleanly(path: Path) -> tuple[bool, str]:
    try:
        with zipfile.ZipFile(str(path)) as archive:
            bad = archive.testzip()
            if bad is None:
                return True, "可打开，testzip 通过"
            return False, f"testzip 报告坏条目：{bad}"
    except Exception as exc:  # 故意损坏样本会走到这里
        return False, f"{type(exc).__name__}: {exc}"


def run_self_checks(out_dir: Path, entries, selected_ids, only_used: bool,
                    skip_calibre: bool) -> bool:
    results = []  # (标题, 是否通过, 说明)

    # 1) A01/A02 的 ZIP 布局
    layout_notes = []
    layout_ok = True
    for sample_id, relative in (("A01", "A/epub3-min.epub"), ("A02", "A/epub2-min.epub")):
        if sample_id not in selected_ids:
            layout_notes.append(f"{sample_id} 未选中")
            continue
        path = out_dir / relative
        try:
            with zipfile.ZipFile(str(path)) as archive:
                infos = archive.infolist()
                ok = (
                    bool(infos)
                    and infos[0].filename == "mimetype"
                    and infos[0].compress_type == zipfile.ZIP_STORED
                    and archive.read("mimetype") == MIMETYPE
                )
            layout_notes.append(f"{sample_id} {'通过' if ok else '不符合'}")
            layout_ok = layout_ok and ok
        except Exception as exc:
            layout_notes.append(f"{sample_id} 打不开（{exc}）")
            layout_ok = False
    results.append(("A01/A02 的 mimetype 首条 + STORED + 内容精确", layout_ok,
                    "；".join(layout_notes)))

    # 2) catalog 条数、manifest 条数与实际文件数
    actual_files = sorted(
        str(path.relative_to(out_dir)).replace("\\", "/")
        for path in out_dir.rglob("*")
        if path.is_file() and path.name != "manifest.json"
    )
    declared_files = sorted(
        item["file"] for item in entries if not item.get("skipped")
    )
    skipped_count = sum(1 for item in entries if item.get("skipped"))
    no_skip_files = all(
        not (out_dir / item["file"]).exists() for item in entries if item.get("skipped")
    )
    count_detail = (
        f"manifest {len(entries)} 条（生成 {len(declared_files)}、跳过 {skipped_count}），"
        f"实际文件 {len(actual_files)} 个"
    )
    count_ok = (
        actual_files == declared_files
        and no_skip_files
        and len(entries) == len(declared_files) + skipped_count
    )
    if not only_used and not skip_calibre:
        count_ok = count_ok and len(entries) == 40
        count_detail += "；全量 40 条"
    elif only_used:
        count_detail += "；本次使用 -Only，未做 40 全量校验"
    else:
        count_detail += "；本次使用 -SkipCalibre，C 组按 skipped 计"
    results.append(("manifest 条数与文件数一致", count_ok, count_detail))

    # 3) 负例字节数
    negative_notes = []
    negative_ok = True
    for sample_id, relative, want_zero in (
        ("A11", "A/epub-broken-truncated.epub", False),
        ("A18", "A/epub-broken-container.epub", False),
        ("D02", "D/pdf-corrupt.pdf", False),
        ("E03", "E/cbz-corrupt.cbz", False),
        ("B09", "B/txt-empty.txt", True),
    ):
        if sample_id not in selected_ids:
            negative_notes.append(f"{sample_id} 未选中")
            continue
        path = out_dir / relative
        if not path.is_file():
            negative_notes.append(f"{sample_id} 缺文件")
            negative_ok = False
            continue
        size = path.stat().st_size
        ok = (size == 0) if want_zero else (size > 0)
        negative_notes.append(f"{sample_id}={size} B{'（预期 0）' if want_zero else ''}")
        negative_ok = negative_ok and ok
    results.append(("负例存在且字节数符合预期（B09 为 0）", negative_ok,
                    "；".join(negative_notes)))

    # 4) 所有 ZIP 类样本能打开；A11/E03 是故意截断，必须打不开
    zip_notes = []
    zip_ok = True
    for item in entries:
        if item.get("skipped"):
            continue
        sample_id = item["id"]
        suffix = Path(item["file"]).suffix.lower()
        if suffix not in (".epub", ".cbz"):
            continue
        path = out_dir / item["file"]
        good, detail = zip_opens_cleanly(path)
        if sample_id in ("A11", "E03"):
            ok = not good  # 截断样本必须坏
            zip_notes.append(f"{sample_id}={'预期损坏，确认打不开' if ok else '意外可读'}")
        else:
            ok = good
            zip_notes.append(f"{sample_id}={'通过' if ok else detail}")
        zip_ok = zip_ok and ok
    results.append(("ZIP 类样本可打开（截断样本必须打不开）", zip_ok, "；".join(zip_notes)))

    # 5) 总字节
    total = sum(item.get("bytes") or 0 for item in entries)
    results.append(("总字节数", True, f"{total} B（{human_bytes(total)}）"))

    print()
    print("[自检]")
    for index, (title, ok, detail) in enumerate(results, start=1):
        print(f"  {index}/5 {title}：{'通过' if ok else '失败'}")
        print(f"        {detail}")
    return all(ok for _title, ok, _detail in results)


# ---------------------------------------------------------------------------
# 汇总表
# ---------------------------------------------------------------------------

def print_summary(entries, catalog_total: int, only_used: bool, skip_calibre: bool) -> None:
    groups = {}
    for item in entries:
        group = item.get("group", "?")
        bucket = groups.setdefault(group, {"count": 0, "bytes": 0, "skipped": 0})
        bucket["count"] += 1
        bucket["bytes"] += item.get("bytes") or 0
        if item.get("skipped"):
            bucket["skipped"] += 1

    generated = sum(1 for item in entries if not item.get("skipped"))
    skipped = sum(1 for item in entries if item.get("skipped"))
    total_bytes = sum(item.get("bytes") or 0 for item in entries)

    print()
    print("=" * 64)
    print("T7-5 测试样本汇总")
    print("=" * 64)
    print(f"{'组别':<6}{'样本数':>8}{'生成字节':>16}{'跳过':>6}")
    for group in sorted(groups):
        bucket = groups[group]
        print(f"{group:<6}{bucket['count']:>8}{bucket['bytes']:>16,}{bucket['skipped']:>6}")
    print("-" * 64)
    print(f"{'合计':<6}{len(entries):>8}{total_bytes:>16,}{skipped:>6}")
    print(f"生成 {generated} 个文件，跳过 {skipped} 个；总字节 {total_bytes} B"
          f"（{human_bytes(total_bytes)}）")

    if only_used:
        print(f"与 catalog 数量是否一致：否——本次用 -Only 裁剪"
              f"（选中 {len(entries)}/{catalog_total}；自动依赖见上）。")
    elif skip_calibre:
        print(f"与 catalog 数量是否一致：按 -SkipCalibre 预期"
              f"（{catalog_total} 条中生成 {generated}、skipped {skipped}）。")
    elif len(entries) == catalog_total and skipped == 0:
        print(f"与 catalog 数量是否一致：是（{catalog_total}/{catalog_total}）。")
    else:
        print(f"与 catalog 数量是否一致：否（catalog {catalog_total} 条，"
              f"本次 manifest {len(entries)} 条，生成 {generated}、跳过 {skipped}）。")

    if any(item["id"] in CALIBRE_IDS for item in entries):
        print("说明：C 组由真 Calibre 派生，字节会随 Calibre 版本与系统字体变化；"
              "manifest 里的 sha256 是本次实际值。")


# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------

def main(argv) -> int:
    parser = argparse.ArgumentParser(
        description="T7-5 全量回归测试样本生成器（按 build/t75-samples.catalog.json 逐条构造）。"
    )
    parser.add_argument("--output-dir", default="Data-Test/t75-corpus",
                        help="输出目录，默认 Data-Test/t75-corpus")
    parser.add_argument("--only", default="",
                        help='只造这些样本，逗号分隔，例如 "A01,B01"')
    parser.add_argument("--skip-calibre", action="store_true",
                        help="跳过 C 组（没有 Calibre 的环境）；manifest 标 skipped，不伪造文件")
    parser.add_argument("--force", action="store_true",
                        help="输出目录已存在时先清空再重建")
    args = parser.parse_args(argv)

    try:
        catalog = load_catalog()
        samples = catalog["samples"]
        catalog_total = len(samples)
        selected_ids, auto_added = resolve_selection(
            samples, args.only, args.skip_calibre
        )
        expected = [sample for sample in samples if sample["id"] in selected_ids]

        out_dir = resolve_output_dir(args.output_dir)
        only_used = bool(args.only.strip())
        require_exact_ids = not only_used

        print("=" * 64)
        print("T7-5 全量回归：可构造测试样本生成器")
        print("=" * 64)
        print(f"样本目录：{CATALOG_PATH}")
        print(f"输出目录：{out_dir}")
        print(f"本次选择：{len(expected)}/{catalog_total} 条"
              + ("（-Only " + args.only.strip() + "）" if only_used else ""))
        if args.skip_calibre:
            print("Calibre：-SkipCalibre，C 组将标记 skipped，不生成文件")
        elif find_calibre() is None:
            print("Calibre：未找到（若本次需要 C 组会报错；可用 -SkipCalibre 跳过）")
        if auto_added:
            print("自动补造依赖：" + "、".join(auto_added))

        if out_dir.exists() and not out_dir.is_dir():
            raise GenerationError(f"输出路径已存在但不是目录：{out_dir}")

        if out_dir.is_dir() and not args.force:
            if manifest_covers(out_dir, expected, require_exact_ids, args.skip_calibre):
                print()
                print(f"输出目录已存在且 manifest 完整，已存在，跳过：{out_dir}")
                try:
                    previous = json.loads(read_text(out_dir / "manifest.json"))
                    total = previous.get("totalBytes", 0)
                    print(f"（manifest 共 {len(previous.get('samples', []))} 条，"
                          f"总字节 {total} B；如需强制重建请加 -Force）")
                except (OSError, json.JSONDecodeError):
                    pass
                return 0
            print()
            print("输出目录存在但内容与本次选择不一致，将清空重建。")

        if out_dir.exists():
            shutil.rmtree(out_dir)
        out_dir.mkdir(parents=True, exist_ok=True)

        ctx = Context(out_dir, args.skip_calibre)
        entries = []
        total = len(expected)

        for index, sample in enumerate(expected, start=1):
            sample_id = sample["id"]
            relative = sample["file"]
            path = out_dir / relative
            prefix = f"[{index:>2}/{total}] {sample_id}"

            if args.skip_calibre and sample_id in CALIBRE_IDS:
                entries.append(build_manifest_entry(sample, path, skipped=True))
                print(f"{prefix} 跳过（-SkipCalibre）")
                continue

            builder = BUILDERS.get(sample_id)
            if builder is None:
                raise GenerationError(f"内部错误：样本 {sample_id} 没有对应的构造器。")
            try:
                builder(ctx, path)
            except GenerationError:
                raise
            except Exception as exc:
                raise GenerationError(
                    f"样本 {sample_id} 生成失败：{type(exc).__name__}: {exc}"
                ) from exc

            if not path.is_file():
                raise GenerationError(f"样本 {sample_id} 的构造器没有产出文件：{path}")

            item = build_manifest_entry(sample, path, skipped=False)
            entries.append(item)
            print(f"{prefix} {item['bytes']:>12,} B  {relative}")

        total_bytes = sum(item.get("bytes") or 0 for item in entries)
        manifest = dict(catalog)  # 保留 catalog 顶层字段
        manifest["samples"] = entries
        manifest["generatedAt"] = datetime.datetime.now().astimezone().isoformat(
            timespec="seconds"
        )
        manifest["generator"] = GENERATOR_NAME
        manifest["pythonVersion"] = platform.python_version()
        manifest["totalBytes"] = total_bytes
        manifest_path = out_dir / "manifest.json"
        write_bytes(
            manifest_path,
            (json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode("utf-8"),
        )

        checks_ok = run_self_checks(
            out_dir, entries, selected_ids, only_used, args.skip_calibre
        )
        print_summary(entries, catalog_total, only_used, args.skip_calibre)

        if not checks_ok:
            print()
            print("生成完成，但自检未全部通过；请根据上面的失败项排查。", file=sys.stderr)
            return 1
        print()
        print("全部完成。")
        return 0

    except GenerationError as exc:
        print()
        print("错误：" + str(exc), file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("\n已中断。", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
