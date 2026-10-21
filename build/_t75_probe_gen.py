"""临时探针生成器：只造 A01（EPUB3 最小书）并写 manifest.json。

用途：在正式生成器 build/make-test-samples.ps1 就绪前，先把 --smoke-t75 的
执行链路跑通（导入 -> 阅读 -> 转 MOBI -> 编辑保存 -> EpubCheck）。
正式生成器就绪后本文件删除。

用法：
  python build/_t75_probe_gen.py <输出目录>
"""

import hashlib
import json
import os
import sys
import zipfile
from datetime import datetime, timezone

DT = (2026, 1, 1, 0, 0, 0)


def write_zip(path: str, entries: list[tuple[str, bytes, int]]) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as zf:
        for name, data, mode in entries:
            info = zipfile.ZipInfo(name, date_time=DT)
            info.compress_type = mode
            info.external_attr = 0o600 << 16
            zf.writestr(info, data)


CONTAINER = """<?xml version="1.0" encoding="UTF-8"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles>
    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
  </rootfiles>
</container>
"""

OPF = """<?xml version="1.0" encoding="UTF-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="bookid">urn:uuid:t75-a01-minimal-epub3</dc:identifier>
    <dc:title>回归样本-最小 EPUB3</dc:title>
    <dc:creator>回归作者 A</dc:creator>
    <dc:language>zh-CN</dc:language>
    <meta property="dcterms:modified">2026-01-01T00:00:00Z</meta>
  </metadata>
  <manifest>
    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
    <item id="ch1" href="Text/ch1.xhtml" media-type="application/xhtml+xml"/>
    <item id="ch2" href="Text/ch2.xhtml" media-type="application/xhtml+xml"/>
    <item id="css" href="Styles/main.css" media-type="text/css"/>
  </manifest>
  <spine>
    <itemref idref="ch1"/>
    <itemref idref="ch2"/>
  </spine>
</package>
"""

NAV = """<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE html>
<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="zh-CN">
<head><meta charset="UTF-8"/><title>目录</title></head>
<body>
  <nav epub:type="toc" id="toc">
    <h1>目录</h1>
    <ol>
      <li><a href="Text/ch1.xhtml">第一章 回归起点</a></li>
      <li><a href="Text/ch2.xhtml">第二章 回归终点</a></li>
    </ol>
  </nav>
</body>
</html>
"""

CH = """<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE html>
<html xmlns="http://www.w3.org/1999/xhtml" xml:lang="zh-CN">
<head><meta charset="UTF-8"/><title>{title}</title><link rel="stylesheet" href="../Styles/main.css"/></head>
<body>
  <h1 id="{anchor}">{title}</h1>
  <p>这是 T7-5 全量回归用的最小 EPUB3 样本，第 {no} 章。中文标点：你好，世界！</p>
  <p class="quote">「回归不是走过场，每一步都要留下证据。」</p>
</body>
</html>
"""

CSS = """body { font-family: serif; line-height: 1.6; }
h1 { font-size: 1.4em; }
p { text-indent: 2em; }
"""


def build_a01(out_dir: str) -> str:
    path = os.path.join(out_dir, "A", "epub3-min.epub")
    entries = [
        ("mimetype", b"application/epub+zip", zipfile.ZIP_STORED),
        ("META-INF/container.xml", CONTAINER.encode("utf-8"), zipfile.ZIP_DEFLATED),
        ("OEBPS/content.opf", OPF.encode("utf-8"), zipfile.ZIP_DEFLATED),
        ("OEBPS/nav.xhtml", NAV.encode("utf-8"), zipfile.ZIP_DEFLATED),
        ("OEBPS/Text/ch1.xhtml", CH.format(title="第一章 回归起点", anchor="sec1", no=1).encode("utf-8"), zipfile.ZIP_DEFLATED),
        ("OEBPS/Text/ch2.xhtml", CH.format(title="第二章 回归终点", anchor="sec1", no=2).encode("utf-8"), zipfile.ZIP_DEFLATED),
        ("OEBPS/Styles/main.css", CSS.encode("utf-8"), zipfile.ZIP_DEFLATED),
    ]
    write_zip(path, entries)

    # 自检：mimetype 必须首条、STORED、内容精确
    with zipfile.ZipFile(path) as zf:
        infos = zf.infolist()
        assert infos[0].filename == "mimetype", "首条不是 mimetype"
        assert infos[0].compress_type == 0, "mimetype 被压缩了"
        assert zf.read("mimetype") == b"application/epub+zip"
    return path


def main() -> int:
    if len(sys.argv) < 2:
        print("用法：python _t75_probe_gen.py <输出目录>")
        return 1

    out_dir = os.path.abspath(sys.argv[1])
    catalog_path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                                "build", "t75-samples.catalog.json")
    # __file__ 在 build/ 下，仓库根是上一级
    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    catalog_path = os.path.join(repo_root, "build", "t75-samples.catalog.json")

    with open(catalog_path, "r", encoding="utf-8") as f:
        catalog = json.load(f)

    a01 = next(s for s in catalog["samples"] if s["id"] == "A01")
    built = build_a01(out_dir)

    with open(built, "rb") as f:
        data = f.read()

    a01 = dict(a01)
    a01["bytes"] = len(data)
    a01["sha256"] = hashlib.sha256(data).hexdigest()

    manifest = {
        "schemaVersion": catalog["schemaVersion"],
        "generatedAt": datetime.now(timezone.utc).astimezone().isoformat(timespec="seconds"),
        "generator": "build/_t75_probe_gen.py（临时探针）",
        "pythonVersion": sys.version.split()[0],
        "totalBytes": len(data),
        "samples": [a01],
    }

    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)

    print("生成 " + built + "（" + str(len(data)) + " 字节）")
    print("manifest 1 条，totalBytes=" + str(len(data)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
