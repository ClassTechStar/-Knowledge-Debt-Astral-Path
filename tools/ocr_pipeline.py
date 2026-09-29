#!/usr/bin/env python3
"""知债：星穹学途 OCR 管线 v3 —— 对齐 tesseract 官方用法。

用法（与 tesseract README 一致）：
    tesseract imagename outputbase [-l lang] [--oem ocrenginemode] [--psm pagesegmode] [configfiles...]

本仓库参照：C:\\Users\\18948\\Documents\\GitHub\\tesseract
CLI 示例：
    tesseract page.png out -l chi_sim+eng --oem 1 --psm 3
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import traceback
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))
try:
    from table_extract import extract_markdown_tables  # P1-3
except Exception:  # pragma: no cover
    extract_markdown_tables = None  # type: ignore

# ---------------------------------------------------------------------------
# tesseract 路径与 tessdata（完全对齐 tesseract 安装布局）
# ---------------------------------------------------------------------------
TESSERACT_EXE_CANDIDATES = [
    os.environ.get("ASTRALPATH_TESSERACT", ""),
    os.environ.get("TESSERACT_CMD", ""),
    r"C:\Program Files\Tesseract-OCR\tesseract.exe",
    r"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe",
    shutil.which("tesseract") or "",
    r"C:\Users\18948\Documents\GitHub\tesseract\tesseract",
]

TESSDATA_CANDIDATES = [
    os.environ.get("TESSDATA_PREFIX", ""),
    os.environ.get("ASTRALPATH_TESSDATA", ""),
    str(Path(__file__).resolve().parent / "tessdata"),
    r"C:\Program Files\Tesseract-OCR\tessdata",
]

# tesseract OEM：0=legacy, 1=LSTM, 2=both, 3=default
TESS_OEM = os.environ.get("ASTRALPATH_TESS_OEM", "1")
# tesseract PSM：3=Fully automatic page segmentation, 6=Assume uniform block
TESS_PSM = os.environ.get("ASTRALPATH_TESS_PSM", "3")
# language：chi_sim+eng（tesseract 多语言用 + 连接）
TESS_LANG = os.environ.get("ASTRALPATH_TESS_LANG", "chi_sim+eng")


def find_tesseract() -> str | None:
    for p in TESSERACT_EXE_CANDIDATES:
        if p and Path(p).exists():
            return str(Path(p))
    return shutil.which("tesseract")


def find_tessdata() -> str | None:
    for p in TESSDATA_CANDIDATES:
        if p and Path(p).is_dir():
            # 需要包含 traineddata
            if any(Path(p).glob("*.traineddata")):
                return str(Path(p))
    return None


def tesseract_info() -> dict[str, Any]:
    exe = find_tesseract()
    data = find_tessdata()
    info: dict[str, Any] = {
        "exe": exe,
        "tessdata": data,
        "available": bool(exe),
        "lang": TESS_LANG,
        "oem": TESS_OEM,
        "psm": TESS_PSM,
        "version": "",
        "langs": [],
    }
    if not exe:
        return info
    try:
        ver = subprocess.run([exe, "--version"], capture_output=True, text=True, timeout=20)
        info["version"] = (ver.stdout or ver.stderr or "").splitlines()[0] if (ver.stdout or ver.stderr) else ""
    except Exception as e:
        info["version_error"] = str(e)
    try:
        cmd = [exe, "--list-langs"]
        if data:
            cmd += ["--tessdata-dir", data]
        langs = subprocess.run(cmd, capture_output=True, text=True, timeout=20)
        lines = [ln.strip() for ln in (langs.stdout or "").splitlines() if ln.strip()]
        info["langs"] = [ln for ln in lines if ln and not ln.lower().startswith("list")]
    except Exception as e:
        info["langs_error"] = str(e)
    return info


def resolve_lang(tessdata: str | None) -> str:
    """按可用 traineddata 自动收敛语言串（对齐 tesseract -l）。"""
    want = [x.strip() for x in TESS_LANG.split("+") if x.strip()]
    if not tessdata:
        return "+".join(want) if want else "eng"
    have = {p.stem for p in Path(tessdata).glob("*.traineddata")}
    chosen = [x for x in want if x in have]
    if not chosen:
        for pref in ("chi_sim", "eng", "osd"):
            if pref in have:
                chosen.append(pref)
    return "+".join(chosen) if chosen else "eng"


CHAPTER_RE = re.compile(
    r"(第\s*\d+\s*章[^\n]{0,48}|第\s*[一二三四五六七八九十百零]+\s*章[^\n]{0,48}|Chapter\s+\d+[^\n]{0,48})",
    re.I,
)
TOC_CHAPTER_RE = re.compile(r"(第\s*\d+\s*章|第\s*[一二三四五六七八九十百零]+\s*章)\s*([^\n\d]{2,40})")
SECTION_RE = re.compile(r"(?m)^\s*(\d{1,2}(?:\.\d{1,2}){0,2})\s+([^\n]{2,48})$")
PAGE_MARK_RE = re.compile(r"\[page\s+(\d+)\]")

DOMAIN_TERMS = [
    "变量", "类型", "函数", "类", "对象", "接口", "继承", "多态", "泛型", "协程", "集合",
    "数组", "字符串", "循环", "条件", "异常", "文件", "网络", "并发", "线程", "进程",
    "面向对象", "设计模式", "单元测试", "依赖注入", "生命周期", "编译", "解释", "运行时",
    "神经网络", "反向传播", "卷积", "循环神经网络", "注意力机制", "Transformer", "强化学习",
    "监督学习", "无监督学习", "过拟合", "正则化", "梯度下降", "损失函数", "激活函数",
    "特征工程", "知识图谱", "嵌入", "提示词", "智能体", "大模型", "微调", "RAG", "Agent",
    "马尔可夫", "策略", "价值函数", "Q-learning", "词向量", "seq2seq",
    "会计要素", "借贷记账", "会计分录", "试算平衡", "资产负债表",
]
TERM_PATTERNS = [
    r"([A-Za-z_][A-Za-z0-9_\.]{2,32})",
    r"((?:" + "|".join(DOMAIN_TERMS) + r"))",
]


# ---------------------------------------------------------------------------
# tesseract OCR（照抄官方 CLI 调用）
# ---------------------------------------------------------------------------
def tesseract_ocr_image(
    image_path: Path,
    output_base: Path,
    lang: str | None = None,
    oem: str | None = None,
    psm: str | None = None,
    tessdata: str | None = None,
    config: str | None = None,
) -> dict[str, Any]:
    """tesseract <image> <outputbase> [-l lang] [--oem] [--psm] [config]"""
    exe = find_tesseract()
    if not exe:
        return {"ok": False, "error": "tesseract not found", "text": ""}
    tessdata = tessdata or find_tessdata()
    lang = lang or resolve_lang(tessdata)
    oem = oem or TESS_OEM
    psm = psm or TESS_PSM
    output_base.parent.mkdir(parents=True, exist_ok=True)

    cmd = [exe, str(image_path), str(output_base), "-l", lang, "--oem", str(oem), "--psm", str(psm)]
    if tessdata:
        cmd += ["--tessdata-dir", tessdata]
    if config:
        cmd.append(config)

    env = os.environ.copy()
    if tessdata:
        env["TESSDATA_PREFIX"] = tessdata

    try:
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=120, env=env)
    except Exception as e:
        return {"ok": False, "error": f"tesseract_run:{e}", "text": "", "cmd": cmd}

    txt_path = Path(str(output_base) + ".txt")
    text = txt_path.read_text(encoding="utf-8", errors="ignore") if txt_path.exists() else ""
    tsv_path = Path(str(output_base) + ".tsv")
    hocr_path = Path(str(output_base) + ".hocr")
    return {
        "ok": proc.returncode == 0 or bool(text.strip()),
        "error": (proc.stderr or "").strip()[:500] if proc.returncode != 0 else "",
        "text": text,
        "cmd": cmd,
        "stdout": (proc.stdout or "").strip()[:300],
        "has_tsv": tsv_path.exists(),
        "has_hocr": hocr_path.exists(),
        "lang": lang,
        "oem": oem,
        "psm": psm,
        "engine": "tesseract",
        "exe": exe,
    }


def render_pdf_page_png(path: Path, page_index: int, scale: float = 2.0, out_png: Path | None = None) -> Path | None:
    try:
        import pypdfium2 as pdfium
    except Exception:
        return None
    try:
        doc = pdfium.PdfDocument(str(path))
        if page_index < 0 or page_index >= len(doc):
            doc.close()
            return None
        page = doc[page_index]
        # tesseract 偏好高分辨率单页图
        bitmap = page.render(scale=scale)
        pil = bitmap.to_pil()
        page.close()
        doc.close()
        if out_png is None:
            out_png = Path(tempfile.gettempdir()) / f"astralpath_tess_p{page_index}_{os.getpid()}.png"
        # 转为 RGB PNG
        if pil.mode not in ("RGB", "L"):
            pil = pil.convert("RGB")
        pil.save(out_png, format="PNG")
        return out_png
    except Exception:
        return None


def tesseract_pdf_pages(path: Path, page_indexes: list[int], scale: float = 2.0, max_pages: int = 12, time_budget_s: float = 40.0) -> tuple[str, str]:
    """兼容旧签名的薄封装：只返回拼接文本与说明。"""
    text, note, _pages = tesseract_pdf_pages_detail(
        path, page_indexes, scale=scale, max_pages=max_pages, time_budget_s=time_budget_s)
    return text, note


def tesseract_pdf_pages_detail(path: Path, page_indexes: list[int], scale: float = 2.0, max_pages: int = 12, time_budget_s: float = 40.0) -> tuple[str, str, dict[int, str]]:
    """同 tesseract_pdf_pages，但额外返回 {0 基页号: 该页 OCR 文本}，供按页回填。"""
    """PDF → PNG → tesseract（官方 CLI）逐页识别。"""
    exe = find_tesseract()
    if not exe:
        return "", "tesseract_unavailable", {}
    tessdata = find_tessdata()
    lang = resolve_lang(tessdata)
    chunks: list[str] = []
    pages_out: dict[int, str] = {}
    used = 0
    workdir = Path(tempfile.gettempdir()) / f"astralpath_tess_{os.getpid()}"
    workdir.mkdir(parents=True, exist_ok=True)

    # 获取总页数
    try:
        import pypdfium2 as pdfium
        doc = pdfium.PdfDocument(str(path))
        total = len(doc)
        doc.close()
    except Exception as e:
        return "", f"pdf_open_error:{e}", {}

    import time as _tesseract_time
    _t0 = _tesseract_time.time()
    for idx in sorted({i for i in page_indexes if 0 <= i < total})[:max_pages]:
        if _tesseract_time.time() - _t0 > time_budget_s:
            chunks.append("[ocr-budget] stopped")
            break
        png = workdir / f"page_{idx:04d}.png"
        out_base = workdir / f"page_{idx:04d}"
        rendered = render_pdf_page_png(path, idx, scale=scale, out_png=png)
        if rendered is None:
            continue
        # 同时输出 txt 与 tsv（对齐 tesseract 输出格式）
        result = tesseract_ocr_image(
            rendered,
            out_base,
            lang=lang,
            oem=TESS_OEM,
            psm=TESS_PSM,
            tessdata=tessdata,
            config=None,
        )
        # 额外产出 tsv/hocr（可选，失败不影响）
        try:
            tsv_base = workdir / f"page_{idx:04d}_tsv"
            subprocess.run(
                [exe, str(rendered), str(tsv_base), "-l", lang, "--oem", TESS_OEM, "--psm", TESS_PSM,
                 "--tessdata-dir", tessdata] if tessdata else
                [exe, str(rendered), str(tsv_base), "-l", lang, "--oem", TESS_OEM, "--psm", TESS_PSM],
                capture_output=True, timeout=90,
            )
        except Exception:
            pass
        if result.get("text"):
            pages_out[idx] = result["text"]
            chunks.append(f"[page {idx + 1}]\n{result['text']}")
            used += 1

    text = "\n".join(chunks)
    note = f"tesseract:{used}/{min(max_pages, total)} lang={lang} oem={TESS_OEM} psm={TESS_PSM}"
    return text, note, pages_out


def try_extract_text_pypdf(path: Path, max_pages: int = 40) -> tuple[str, int, str]:
    try:
        from pypdf import PdfReader
    except Exception as e:
        return "", 0, f"pypdf_unavailable:{e}"
    try:
        reader = PdfReader(str(path))
        if getattr(reader, "is_encrypted", False):
            try:
                reader.decrypt("")
            except Exception:
                return "", 0, "encrypted"
        n = len(reader.pages)
        chunks: list[str] = []
        limit = min(n, max_pages)
        for i in range(limit):
            try:
                chunks.append(reader.pages[i].extract_text() or "")
            except Exception:
                chunks.append("")
        text = "\n".join(chunks).strip()
        return text, n, "pypdf" if text else "pypdf_empty"
    except Exception as e:
        return "", 0, f"pypdf_error:{type(e).__name__}:{e}"


def try_extract_text_pdfium(path: Path, max_pages: int = 40) -> tuple[str, int, str]:
    try:
        import pypdfium2 as pdfium
    except Exception as e:
        return "", 0, f"pdfium_unavailable:{e}"
    try:
        doc = pdfium.PdfDocument(str(path))
        n = len(doc)
        limit = min(n, max_pages)
        chunks: list[str] = []
        for i in range(limit):
            try:
                page = doc[i]
                tp = page.get_textpage()
                chunks.append(tp.get_text_range() or "")
                tp.close()
                page.close()
            except Exception:
                chunks.append("")
        doc.close()
        text = "\n".join(chunks).strip()
        return text, n, "pdfium" if text else "pdfium_empty"
    except Exception as e:
        return "", 0, f"pdfium_error:{type(e).__name__}:{e}"


def clean_ocr_text(text: str) -> str:
    text = (text or "").replace("　", " ").replace("\x00", "")
    lines = []
    for line in text.splitlines():
        s = line.rstrip()  # P6-2：保留行首缩进（代码页），仅去尾
        if not s:
            continue
        if len(s) <= 1 and not s.isdigit():
            continue
        if re.fullmatch(r"[\W_]{1,8}", s):
            continue
        # tesseract 常见噪声
        if s.lower() in {"page", "ocr", "tesseract"}:
            continue
        lines.append(s)
    return "\n".join(lines)


def shorten_chapter_title(title: str) -> str:
    t = re.sub(r"\s+", " ", title).strip()
    m = re.match(r"(第\s*\d+\s*章|第\s*[一二三四五六七八九十百零]+\s*章)\s*(.{0,24})", t)
    if m:
        head = m.group(1).replace(" ", "")
        tail = re.split(r"[，,。；;：:—⸺-]", m.group(2))[0]
        tail = re.sub(r"^(讲解了|介绍了|重点介绍|说明了|讨论了|阐述了)", "", tail)
        tail = tail.strip()[:16]
        return f"{head} {tail}".strip()[:24] if tail else head
    return t[:24]


def extract_chapters(text: str) -> list[dict[str, Any]]:
    found: list[dict[str, Any]] = []
    seen: set[str] = set()
    for regex in (CHAPTER_RE, TOC_CHAPTER_RE):
        for m in regex.finditer(text):
            raw = m.group(1) if regex is CHAPTER_RE else f"{m.group(1)} {m.group(2)}"
            title = shorten_chapter_title(raw)
            key = title[:40]
            if key in seen or len(title) < 3:
                continue
            seen.add(key)
            found.append({"title": title, "kind": "chapter", "offset": m.start()})
    for m in SECTION_RE.finditer(text):
        title = f"{m.group(1)} {m.group(2).strip()}"[:24]
        key = title[:40]
        if key in seen:
            continue
        seen.add(key)
        found.append({"title": title, "kind": "section", "offset": m.start()})
    return found[:60]


def extract_terms(text: str, limit: int = 40) -> list[dict[str, Any]]:
    counts: dict[str, int] = {}
    for pat in TERM_PATTERNS:
        for m in re.finditer(pat, text):
            term = (m.group(1) or "").strip()
            if len(term) < 2 or term.isdigit():
                continue
            if term.lower() in {"the", "and", "for", "you", "with", "this", "that", "www", "http", "https", "com"}:
                continue
            counts[term] = counts.get(term, 0) + 1
    ranked = sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))
    return [{"term": t, "freq": c} for t, c in ranked[:limit]]


def extract_sentences(text: str, limit: int = 12) -> list[str]:
    sents: list[str] = []
    for raw in re.split(r"[。！？!?\n]", text):
        s = raw.strip()
        if 12 <= len(s) <= 80 and not s.startswith("[page"):
            if any(t in s for t in DOMAIN_TERMS) or re.search(r"[A-Za-z]{3,}", s):
                sents.append(s)
        if len(sents) >= limit:
            break
    return sents


def build_nodes_edges(chapters: list[dict], terms: list[dict], material_title: str) -> tuple[list, list]:
    nodes: list[dict] = []
    edges: list[dict] = []
    nid = 1

    def next_id() -> str:
        nonlocal nid
        value = f"A{nid:03d}"
        nid += 1
        return value

    course = (material_title or "MATERIAL")[:24]
    chapter_nodes: list[str] = []
    section_nodes: list[str] = []
    for ch in chapters:
        if len(chapter_nodes) + len(section_nodes) >= 36:
            break
        node_id = next_id()
        kind = ch.get("kind", "chapter")
        nodes.append({
            "id": node_id,
            "name": ch["title"][:40],
            "course": course,
            "description": kind,
            "source": f"text:{kind}",
        })
        if kind == "chapter":
            chapter_nodes.append(node_id)
        else:
            section_nodes.append(node_id)

    # 章顺序先修
    for a, b in zip(chapter_nodes, chapter_nodes[1:]):
        edges.append({"from": a, "to": b, "edgeType": "prerequisite", "weight": 1.2,
                      "source": "auto:chapter-sequence"})

    # 小节挂到所属章（按标题里的章节号启发式）
    sec_re = re.compile(r"(\d{1,2})\.(\d{1,2})")
    ch_map = {}
    for idx, ch in enumerate(chapters):
        if ch.get("kind") == "chapter" and idx < len(chapter_nodes):
            m = re.search(r"第\s*(\d+)", ch.get("title", ""))
            if m:
                ch_map[int(m.group(1))] = chapter_nodes[idx]
    sec_node_by_title = {}
    s_i = 0
    for ch in chapters:
        if ch.get("kind") != "section":
            continue
        if s_i < len(section_nodes):
            sec_node_by_title[ch.get("title", "")] = section_nodes[s_i]
            s_i += 1
    for ch in chapters:
        if ch.get("kind") != "section":
            continue
        title = ch.get("title", "")
        m = sec_re.search(title) or re.search(r"第\s*(\d+)", title)
        parent = None
        if sec_re.search(title):
            parent = ch_map.get(int(sec_re.search(title).group(1)))
        elif m:
            parent = ch_map.get(int(m.group(1)))
        sec_id = sec_node_by_title.get(title)
        if parent and sec_id:
            edges.append({"from": parent, "to": sec_id, "edgeType": "prerequisite",
                          "weight": 1.0, "source": "auto:chapter-section"})

    # 关键词：挂到前两章 + 彼此弱边（共现近似）
    term_ids = []
    for term in terms[:20]:
        node_id = next_id()
        nodes.append({
            "id": node_id,
            "name": term["term"][:28],
            "course": course,
            "description": f"关键词 freq={term['freq']}",
            "source": "auto:term-frequency",
        })
        term_ids.append(node_id)
        for anchor in chapter_nodes[:2]:
            edges.append({"from": node_id, "to": anchor, "edgeType": "prerequisite",
                          "weight": 0.8, "source": "auto:term-anchor"})
    for a, b in zip(term_ids, term_ids[1:3] + term_ids[3:4]):
        if a != b:
            edges.append({"from": a, "to": b, "edgeType": "related", "weight": 0.7,
                          "source": "auto:term-cooccur"})

    if not chapter_nodes and not section_nodes:
        for a, b in zip(term_ids, term_ids[1:]):
            edges.append({"from": a, "to": b, "edgeType": "prerequisite", "weight": 0.9,
                          "source": "auto:term-sequence"})
    return nodes, edges


def build_suggested_tasks(nodes: list, material_title: str, max_tasks: int = 6) -> list[dict]:
    tasks: list[dict] = []
    chapters = [n for n in nodes if str(n.get("description", "")).startswith("chapter")
                or str(n.get("source", "")).endswith("chapter") or str(n.get("name", "")).startswith("第")]
    others = [n for n in nodes if n not in chapters]
    seq = 0
    for n in chapters[:3]:
        tasks.append({
            "kpId": n["id"], "kpName": n["name"], "type": "concept", "difficulty": 2, "estMin": 8,
            "why": f"来自《{material_title[:16]}》：先建立「{n['name'][:16]}」整体框架",
        })
        seq += 1
    for n in others[: max(0, max_tasks - len(tasks))]:
        tasks.append({
            "kpId": n["id"], "kpName": n["name"],
            "type": "quiz" if seq % 3 == 2 else "drill",
            "difficulty": 2 + (seq % 3), "estMin": 6,
            "why": f"为还《{material_title[:14]}》相关知识债：巩固「{n['name'][:12]}」",
        })
        seq += 1
    return tasks[:max_tasks]


# ── 行/页级乱码判定（与前端 deploy/monolith-web/index.html 的 garbledPages() 同一套规则与阈值）──
# 可疑字符 = 私有使用区（BMP / 平面 15 / 平面 16）、U+FFFD、CJK 部首区（康熙部首 + 部首补充，
# pypdf ToUnicode 缺陷会把"黄"抽成"⻩"这类伪汉字，P0-3 起计入可疑）、以及中文技术书里不可能出现的稀有文字。
# 正体 CJK、注音、假名、谚文、拉丁扩展、希腊、西里尔、数学符号、emoji、全角标点一律视为正常字符。
GARBLE_RANGES = (
    (0x0530, 0x058F),     # 亚美尼亚文
    (0x0590, 0x05FF),     # 希伯来文
    (0x0600, 0x06FF),     # 阿拉伯文
    (0x0700, 0x074F),     # 叙利亚文
    (0x0750, 0x077F),     # 阿拉伯文补充
    (0x0780, 0x07BF),     # 塔纳文
    (0x0900, 0x0DFF),     # 印度系文字（天城/孟加拉/古木基/古吉拉特/奥里亚/泰米尔/泰卢固/卡纳达/马拉雅拉姆/僧伽罗）
    (0x0E00, 0x0E7F),     # 泰文
    (0x0E80, 0x0EFF),     # 老挝文
    (0x0F00, 0x0FFF),     # 藏文
    (0x1000, 0x109F),     # 缅甸文
    (0x10A0, 0x10FF),     # 格鲁吉亚文
    (0x1200, 0x137F),     # 埃塞俄比亚文
    (0x13A0, 0x13FF),     # 切罗基文
    (0x1780, 0x17FF),     # 高棉文
    (0x1800, 0x18AF),     # 蒙古文
    (0x2E80, 0x2FDF),     # CJK 部首区（部首补充 U+2E80–U+2EFF + 康熙部首 U+2F00–U+2FDF）：正常文本不应出现，pypdf cmap 缺陷高发区
    (0xE000, 0xF8FF),     # 私有使用区（BMP，苹果 logo 等私有字形）
    (0xF0000, 0xFFFFD),   # 私有使用区（平面 15）
    (0x100000, 0x10FFFD), # 私有使用区（平面 16）
    (0xFFFD, 0xFFFD),     # 替换字符
)

GARBLE_LINE_MIN_CHARS = 3            # 行级：行内可疑字符绝对下限
GARBLE_LINE_MIN_DENSITY = 0.10       # 行级：可疑字符 / 行内非空白字符
GARBLE_PAGE_MIN_LINES = 2            # 页级：页内可疑行数下限
GARBLE_PAGE_MIN_LINE_DENSITY = 0.02  # 页级：可疑行 / 页内非空行
GARBLE_PAGE_MIN_CHARS = 6            # 页级：页内可疑字符绝对下限
GARBLE_BOOK_DENSE_RATIO = 0.30       # 可疑页占比 ≥30% → 视为整本文本层不可用，走整本 OCR
GARBLE_OCR_MAX_PAGES = 12            # 单本最多定向补 OCR 的可疑页数


def _is_garbled_char(cp: int) -> bool:
    """字体乱码字符判定：命中 GARBLE_RANGES 任一区间即视为可疑。"""
    for lo, hi in GARBLE_RANGES:
        if lo <= cp <= hi:
            return True
    return False


def garbled_pages(page_texts: list[str]) -> dict[str, Any]:
    """行/页级乱码密度判定（与前端 index.html garbledPages 同规则同阈值）。

    行级：susp >= GARBLE_LINE_MIN_CHARS 且 susp/signif >= GARBLE_LINE_MIN_DENSITY → 可疑行。
    页级：可疑行 >= GARBLE_PAGE_MIN_LINES、可疑行/非空行 >= GARBLE_PAGE_MIN_LINE_DENSITY、
          页内可疑字符 >= GARBLE_PAGE_MIN_CHARS → 可疑页（定向补 OCR 的触发单位）。
    """
    flagged: list[dict[str, Any]] = []
    for pi, page in enumerate(page_texts):
        total_lines = 0
        flag_lines = 0
        page_susp = 0
        peak = 0.0
        for raw in (page or "").split("\n"):
            line = raw.strip()
            if not line:
                continue
            total_lines += 1
            signif = 0
            susp = 0
            for ch in line:
                if ch.isspace():
                    continue
                signif += 1
                if _is_garbled_char(ord(ch)):
                    susp += 1
            if susp == 0:
                continue
            page_susp += susp
            density = (susp / signif) if signif else 0.0
            if susp >= GARBLE_LINE_MIN_CHARS and density >= GARBLE_LINE_MIN_DENSITY:
                flag_lines += 1
                if density > peak:
                    peak = density
        if (flag_lines >= GARBLE_PAGE_MIN_LINES and total_lines > 0
                and (flag_lines / total_lines) >= GARBLE_PAGE_MIN_LINE_DENSITY
                and page_susp >= GARBLE_PAGE_MIN_CHARS):
            flagged.append({"page": pi + 1, "flagLines": flag_lines, "totalLines": total_lines,
                            "suspChars": page_susp, "peakLineDensity": round(peak, 3)})
    return {"flaggedPages": flagged, "pageCount": len(page_texts)}


# ---------------------------------------------------------------------------
# P0/P1 抽取质量核心：部首修复 / 双抽取器 / 逐页择优合并 / 弱页检测 / 图示页 / 缓存
# ---------------------------------------------------------------------------
WORD_CHAR_RE = re.compile(r"[A-Za-z0-9一-鿿]")

# NFKC 修不了的 CJK 部首补充区（U+2E80–U+2EEF）映射：条目全部来自真实语料上下文验证
# （pypdf cmap 缺陷实测字形：⻩埔/红⻩绿灯、汽⻋、拷⻉、咸⻥直播、⻁爸⻁妈、吸⾎⻤…）。
# 只收录有语料实证的字形；未收录的会命中 GARBLE_RANGES 部首区，显性可见而非静默。
_RADICAL_EXTRA = {
    "⻩": "黄", "⻓": "长", "⻅": "见", "⻋": "车", "⻢": "马", "⻆": "角",
    "⻔": "门", "⻛": "风", "⻜": "飞", "⻚": "页", "⻄": "西", "⻉": "贝",
    "⺠": "民", "⻝": "食", "⻬": "齐", "⻦": "鸟", "⻘": "青", "⻣": "骨",
    "⻨": "麦", "⻮": "齿", "⻰": "龙", "⻥": "鱼", "⻙": "韦", "⻁": "虎",
    "⻤": "鬼", "⺎": "兀",
}


def _build_radical_map() -> dict[str, str]:
    """康熙部首区（U+2F00–U+2FD5）NFKC 可直接还原；部首补充区用实证表补齐。"""
    import unicodedata
    m: dict[str, str] = {}
    for cp in range(0x2F00, 0x2FE0):
        ch = chr(cp)
        n = unicodedata.normalize("NFKC", ch)
        if n != ch and len(n) == 1:
            m[ch] = n
    m.update(_RADICAL_EXTRA)
    return m


_RADICAL_MAP = _build_radical_map()


def fix_radical_chars(text: str) -> str:
    """把 pypdf cmap 缺陷产生的部首伪汉字还原为正体 CJK（黄仁勋实测污染 9.21%）。"""
    if not text:
        return text
    if not any(0x2E80 <= ord(c) <= 0x2FDF for c in text):
        return text
    return "".join(_RADICAL_MAP.get(c, c) for c in text)


def garbled_char_count(text: str) -> int:
    return sum(1 for ch in text or "" if _is_garbled_char(ord(ch)))


def page_word_quality(text: str) -> int:
    """页面质量分：词字符数 − 3×乱码字符（乱码不但无用还污染搜索/建图）。"""
    return max(0, len(WORD_CHAR_RE.findall(text or "")) - 3 * garbled_char_count(text))


MERGE_OCR_WIN_RATIO = 1.25  # OCR 质量须超出文本层 25% 才替补：识别错误不该冤胜可信文本层


def merge_page_texts(text_layer: str, ocr_text: str, figure: bool = False,
                     ocr_ratio: float = MERGE_OCR_WIN_RATIO) -> tuple[str, str]:
    """P1-7 逐页择优：返回 (合并文本, 决策)。决策 ∈ text/ocr/text_only/ocr_only/none。
    图示页（P1-8）反向偏好文本层，OCR 只保留长行/含 CJK 行，滤掉节点标签噪声。
    ocr_ratio：OCR 获胜阈值。出版方夹层可疑（overlay_suspect）的书传 1.0 ——
    视觉真值实测该类书 OCR CER 0.0000 vs 隐藏层 0.0115，字数相近时 OCR 反而更准。"""
    q_tl = page_word_quality(text_layer)
    q_oc = page_word_quality(ocr_text)
    if q_tl == 0 and q_oc == 0:
        return "", "none"
    if q_tl == 0:
        return (ocr_text, "ocr_only") if q_oc > 0 else ("", "none")
    if q_oc == 0:
        return text_layer, "text_only"
    if figure:
        if q_tl >= 60:
            return text_layer, "text"
        kept = "\n".join(ln for ln in ocr_text.splitlines()
                         if len(ln.strip()) >= 6 or sum(1 for c in ln if "一" <= c <= "鿿") >= 2)
        return (kept if page_word_quality(kept) > 20 else text_layer), "text"
    if q_oc > q_tl * ocr_ratio and q_oc >= 20:
        return ocr_text, "ocr"
    return text_layer, "text"


def weak_page_indexes(page_texts: list[str], ratio: float = 0.4, floor: int = 20) -> tuple[list[int], float]:
    """P0-4 弱页判定：词字符 < max(floor, 中位数×ratio)。
    以"有内容页"(≥60) 的中位数为基准——中等密度页缺一半（代码截图页）即命中。
    若有内容页太少（整本无文本层），返回空：整本稀疏走 sparse/full 分支。"""
    words = [page_word_quality(t) for t in page_texts]
    filled = [w for w in words if w >= 60]
    if len(filled) < max(4, int(len(words) * 0.2)):
        return [], 0.0
    filled.sort()
    n = len(filled)
    med = float(filled[n // 2]) if n % 2 else (filled[n // 2 - 1] + filled[n // 2]) / 2.0
    thr = max(float(floor), med * ratio)
    weak = [i for i, w in enumerate(words) if w < thr]
    return weak, med


def try_extract_text_pdfium_pages(path: Path, max_pages: int = 100000) -> tuple[list[str], int, str]:
    """pypdfium2 逐页文本层（P0-1 交叉验证的另一路）。失败返回 ([], 0, err)。"""
    try:
        import pypdfium2 as pdfium
    except Exception as e:
        return [], 0, f"pdfium_unavailable:{e}"
    try:
        doc = pdfium.PdfDocument(str(path))
        n = len(doc)
        out: list[str] = []
        for i in range(min(n, max_pages)):
            try:
                page = doc[i]
                tp = page.get_textpage()
                out.append(tp.get_text_range() or "")
                tp.close()
                page.close()
            except Exception:
                out.append("")
        doc.close()
        return out, n, "pdfium"
    except Exception as e:
        return [], 0, f"pdfium_error:{type(e).__name__}:{e}"


_FPDF_PAGEOBJ_IMAGE = 3


def image_coverage_pages(path: Path, page_indexes: list[int]) -> dict[int, float]:
    """批量计算页面图片对象面积占比（P0-4/P1-8 用）。每页必有返回值（异常按 0 处理）。"""
    out: dict[int, float] = {}
    try:
        import pypdfium2 as pdfium
        doc = pdfium.PdfDocument(str(path))
        targets = sorted({i for i in page_indexes if 0 <= i < len(doc)})
        for idx in targets:
            area = 0.0
            total = 1.0
            try:
                page = doc[idx]
                pw, ph = page.get_size()
                total = max(1.0, pw * ph)
                for obj in page.get_objects(max_depth=4):
                    if obj.type != _FPDF_PAGEOBJ_IMAGE:
                        continue
                    try:
                        l, b, r, t = obj.get_bounds()  # PdfImage：返回 (left, bottom, right, top)
                    except Exception:
                        l, b, r, t = obj.get_pos()
                    area += max(0.0, r - l) * max(0.0, t - b)
                page.close()
            except Exception:
                pass
            out[idx] = min(1.0, area / total)
        doc.close()
    except Exception:
        return {}
    return out


def is_figure_page(ocr_text: str, image_cov: float = 0.0) -> bool:
    """P1-8 图示页：大图 + OCR 行平均极短（思维导图/计算图节点标签会被串成流水）。"""
    lines = [ln.strip() for ln in (ocr_text or "").splitlines() if ln.strip()]
    if len(lines) < 6:
        return False
    avg = sum(len(ln) for ln in lines) / len(lines)
    return (image_cov >= 0.5 and avg < 12) or avg < 5.0


# ── P3-16 页级 OCR 缓存（生产 rapid 路径）：key 含引擎/倍率/预处理标志，环境变化自动失效 ──
# P5-5 补充：PIPELINE_VERSION 进 key——OCR 通道/纠错规则变化时 +1，旧缓存整体失效
# （否则行为改了还命中旧文本，实测大模型书 1.1s 全命中旧缓存的教训）。
PIPELINE_VERSION = "3.2"
_OCR_CACHE_DIR = os.path.join(os.path.expanduser("~"), ".astralpath", "ocr-cache", "prod")
_OCR_PAGE_CACHE: dict[str, str] = {}


def _ocr_cache_key(path: Path, idx: int, scale: float) -> str:
    import hashlib
    try:
        st = os.stat(path)
        raw = f"{path.name}|{st.st_size}|{int(st.st_mtime)}|{idx}|{scale}|{_ocr_engine_name()}|{os.environ.get('ASTRALPATH_OCR_PREPROCESS', '1')}|v{PIPELINE_VERSION}"
    except Exception:
        raw = f"{path.name}|?|{idx}|{scale}|v{PIPELINE_VERSION}"
    return hashlib.sha1(raw.encode("utf-8")).hexdigest()


def _ocr_cache_get(key: str) -> str | None:
    if key in _OCR_PAGE_CACHE:
        return _OCR_PAGE_CACHE[key]
    fp = os.path.join(_OCR_CACHE_DIR, key + ".txt")
    try:
        with open(fp, encoding="utf-8") as f:
            val = f.read()
        _OCR_PAGE_CACHE[key] = val
        return val
    except Exception:
        return None


def _ocr_cache_put(key: str, text: str) -> None:
    _OCR_PAGE_CACHE[key] = text
    try:
        os.makedirs(_OCR_CACHE_DIR, exist_ok=True)
        with open(os.path.join(_OCR_CACHE_DIR, key + ".txt"), "w", encoding="utf-8") as f:
            f.write(text)
    except Exception:
        pass


# ── P1-9 rapid 路径预处理（对齐 umi：灰度 + 对比度拉伸；ASTRALPATH_OCR_PREPROCESS=0 可关）──
def preprocess_pil_for_ocr(img):
    if os.environ.get("ASTRALPATH_OCR_PREPROCESS", "1") != "1":
        return img
    try:
        from PIL import Image, ImageOps
        g = img.convert("L")
        g = ImageOps.autocontrast(g, cutoff=2)
        return g.convert("RGB")
    except Exception:
        return img


# ── P1-11 出版方 OCR 夹层复核：文本层 vs 抽样 OCR 的相似度过低 → 文本层不可信 ──
def overlay_suspect_check(path: Path, page_texts: list[str], sample: int = 6,
                          time_budget_s: float = 60.0) -> dict[str, Any]:
    import difflib
    total = len(page_texts)
    if total < 20:
        return {"checked": 0, "suspect": False}
    cand = [i for i, t in enumerate(page_texts) if 150 <= page_word_quality(t) <= 1200]
    if not cand:
        return {"checked": 0, "suspect": False}
    step = max(1, len(cand) // sample)
    targets = cand[::step][:sample]
    _txt, _note, ocr_map = ocr_pdf_pages_detail(path, targets, scale=2.0, max_pages=sample,
                                                time_budget_s=time_budget_s)
    sims: list[float] = []
    for idx, ocr_t in ocr_map.items():
        if not ocr_t or page_word_quality(ocr_t) < 50:
            continue
        a = re.sub(r"[^\w一-鿿]+", "", page_texts[idx] or "", flags=re.UNICODE)
        b = re.sub(r"[^\w一-鿿]+", "", ocr_t or "", flags=re.UNICODE)
        if not a or not b:
            continue
        sims.append(difflib.SequenceMatcher(None, a, b).ratio())
    med = sorted(sims)[len(sims) // 2] if sims else 1.0
    return {"checked": len(sims), "median_sim": round(med, 4), "suspect": bool(sims) and med < 0.90}


def try_extract_text_pymupdf_pages(path: Path, max_pages: int = 100000) -> tuple[list[str], int, str]:
    """PyMuPDF 逐页文本层（抽取质量通常最好；环境无 pymupdf 时不参与）。"""
    try:
        import pymupdf
    except Exception as e:
        return [], 0, f"pymupdf_unavailable:{e}"
    try:
        doc = pymupdf.open(str(path))
        n = doc.page_count
        out: list[str] = []
        for i in range(min(n, max_pages)):
            try:
                out.append(doc[i].get_text("text") or "")
            except Exception:
                out.append("")
        doc.close()
        return out, n, "pymupdf"
    except Exception as e:
        return [], 0, f"pymupdf_error:{type(e).__name__}:{e}"


def _select_extractor(cands: list[tuple[str, list[str], int]]) -> tuple[list[str], int, str, list[str]]:
    """P0-1 抽取器交叉验证：优先排除部首污染者，其余取词字符最多者。
    cands: [(name, page_texts, pages)]；返回 (page_texts, pages, mode, notes)。"""
    notes: list[str] = []
    scored: list[tuple[str, int, float, list[str], int]] = []
    for name, texts, pages in cands:
        if not pages or not texts:
            continue
        w = sum(page_word_quality(t) for t in texts)
        r = garbled_char_count("".join(texts)) / max(1, w)
        scored.append((name, w, r, texts, pages))
    if not scored:
        if cands:
            return cands[0][1], cands[0][2], cands[0][0], ["extractor:all_failed"]
        return [], 0, "none", notes
    # 部首污染排除：某家污染率 >0.4% 且另一家不到其一半 → 弃用
    clean = [s for s in scored if s[2] <= 0.004]
    polluted = [s for s in scored if s[2] > 0.004]
    if polluted and clean and min(s[2] for s in polluted) > 2 * max(s[2] for s in clean):
        for name, _w, r, _t, _p in polluted:
            notes.append("extractor_rejected:%s(radical=%.3f)" % (name, r))
        scored = clean
    best = max(scored, key=lambda s: s[1])
    for name, w, r, _t, _p in scored:
        if name == best[0]:
            notes.append("extractor:%s(words=%d_radical=%.3f)" % (name, w, r))
        else:
            notes.append("extractor_alt:%s(words=%d)" % (name, w))
    return best[3], best[4], best[0], notes


def try_extract_text_pages(path: Path, max_pages: int = 100000) -> tuple[list[str], int, str]:
    """按页抽取文本层（pypdf 优先），返回 (每页文本, 总页数, 引擎名)；失败返回 ([], 0, err)。"""
    try:
        from pypdf import PdfReader
        reader = PdfReader(str(path))
        try:
            if getattr(reader, "is_encrypted", False):
                reader.decrypt("")
        except Exception:
            pass
        n = len(reader.pages)
        out: list[str] = []
        for i in range(min(n, max_pages)):
            try:
                out.append(reader.pages[i].extract_text() or "")
            except Exception:
                out.append("")
        return out, n, "pypdf"
    except Exception as exc:
        err = "pypdf_error:" + type(exc).__name__
    return [], 0, err


def _readable_ratio(text: str) -> float:
    """[已停用] v2.2.1 起不再参与乱码判定，仅留作参考：可读字符（CJK / ASCII 字母数字）占非空白字符比例。
    内嵌字体缺 ToUnicode 映射的 PDF，文本层会抽出大量"看似有字、实为乱码"的内容
    （实测《Python编程：从入门到实践（第3版）》文本层可读率仅约 28%），必须拦下走 OCR。"""
    s = re.sub(r"\s+", "", text or "")
    if not s:
        return 0.0
    good = sum(1 for ch in s if ("一" <= ch <= "鿿") or ("a" <= ch <= "z") or ("A" <= ch <= "Z") or ("0" <= ch <= "9"))
    return good / len(s)

def extract_full_text(path: Path, ocr_mode: str = "standard", force_pages: list[int] | None = None) -> tuple[str, int, str, list[str], dict[str, Any]]:
    """尽量抽取 PDF 全部文字：双抽取器交叉验证 + 逐页择优合并（文本层 × OCR）。

    相对旧实现的关键变化：
    - P0-1 pypdf 与 pdfium 逐页双抽，按词字符量 + 部首污染率择优（pypdf 大面积欠抽取/伪汉字不再静默）
    - P0-2 部首伪汉字还原（fix_radical_chars：⻩→黄 等）
    - P0-3 乱码判定纳入 CJK 部首区
    - P0-4 弱文本层页逐页补 OCR（词字符 < 有内容页中位数 40%，或图片对象占比 ≥55%）
    - P0-6 取消 wide-garble「整本文本层被 28 页 OCR 替换」的分支，一律逐页回填
    - P1-7 逐页 1.25 规则择优合并；P1-8 图示页降噪；P1-11 出版方 OCR 夹层复核
    - ocr_mode="full"：定向 OCR 无页数上限；整本无文本层时全书逐页 OCR
    force_pages：前端（pdf.js 文本层）算出的可疑页（1 基），与本地判定取并集。
    """
    notes: list[str] = []
    if ocr_mode == "quick":
        max_text_pages = 120
    else:
        max_text_pages = 0  # 0 = 全部
    page_limit = max_text_pages if max_text_pages else 100000

    # ── P0-1 多抽取器交叉验证（pymupdf / pypdf / pdfium，可用者参与，择优）──
    pm_texts, pm_pages, _pm_mode = try_extract_text_pymupdf_pages(path, max_pages=page_limit)
    pp_texts, pp_pages, _pp_mode = try_extract_text_pages(path, max_pages=page_limit)
    pd_texts, pd_pages, _pd_mode = try_extract_text_pdfium_pages(path, max_pages=page_limit)
    page_texts, pages, mode, sel_notes = _select_extractor([
        ("pymupdf", pm_texts, pm_pages),
        ("pypdf", pp_texts, pp_pages),
        ("pdfium", pd_texts, pd_pages),
    ])
    notes.extend(sel_notes)

    # ── P0-2 部首伪汉字还原 ──
    if page_texts:
        fixed_pages = sum(1 for t in page_texts if t and any(0x2E80 <= ord(c) <= 0x2FDF for c in t))
        if fixed_pages:
            page_texts = [fix_radical_chars(t) for t in page_texts]
            notes.append("radical_fix:pages=%d" % fixed_pages)

    text = "\n".join(page_texts).strip()
    density = (len(text) / pages) if pages else 0
    info = tesseract_info()
    ocr_used = False
    engine_ok = ocr_engine_ready(info) and ocr_mode != "none"

    scan = garbled_pages(page_texts) if page_texts else {"flaggedPages": [], "pageCount": 0}
    flagged = list(scan["flaggedPages"])
    forced: list[int] = []
    for p in (force_pages or []):
        if isinstance(p, int) and p >= 1:
            forced.append(p)
    forced = sorted(set(forced))
    has = {p["page"] for p in flagged}
    for p in forced:
        if p not in has:
            flagged.append({"page": p, "flagLines": 0, "totalLines": 0,
                            "suspChars": 0, "peakLineDensity": 0.0, "forced": True})

    detail: dict[str, Any] = {
        "garbledPages": sorted(p["page"] for p in flagged),
        "garbleScan": scan,
        "ocrPages": [],
        "forcePages": forced,
        "weakPages": [],
        "figurePages": [],
        "overlaySuspect": False,
    }
    figure_pages: set[int] = set()

    def _ocr_and_merge(targets: list[int], scale: float, max_pages: int, budget: float,
                       cov: dict[int, float], ocr_ratio: float = MERGE_OCR_WIN_RATIO) -> int:
        """OCR 一批页并逐页择优回填 page_texts；返回被 OCR 内容替换/降噪的页数。
        budget 是整批总预算（秒）：逐 chunk 递减，超时截断并记 note（页缓存可续跑）。"""
        nonlocal ocr_used
        if not targets or not engine_ok:
            return 0
        merged_n = 0
        chunk = 48
        import time as _t
        deadline = _t.time() + budget
        for s in range(0, len(targets), chunk):
            remaining = deadline - _t.time()
            if merged_n >= 0 and remaining <= 10 and s > 0:
                notes.append("ocr_budget_truncated:%d/%d_pages" % (s, len(targets)))
                break
            part = targets[s:s + chunk]
            _t2, _nt, ocr_map = ocr_pdf_pages_detail(path, part, scale=scale,
                                                     max_pages=len(part), time_budget_s=max(15.0, remaining))
            if not ocr_map:
                continue
            ocr_used = True
            for idx, ocr_t in ocr_map.items():
                if not (0 <= idx < len(page_texts)):
                    continue
                fig = is_figure_page(ocr_t, cov.get(idx, 0.0))
                if fig:
                    figure_pages.add(idx)
                body, decision = merge_page_texts(page_texts[idx], clean_ocr_text(ocr_t), figure=fig,
                                                  ocr_ratio=ocr_ratio)
                if decision in ("ocr", "ocr_only") and body.strip():
                    page_texts[idx] = body
                    merged_n += 1
            detail["ocrPages"] = sorted(set(detail["ocrPages"]) | {i + 1 for i in ocr_map})
        detail["figurePages"] = sorted(i + 1 for i in figure_pages)
        return merged_n

    sparse = len(text) < 80 or density < 120
    if ocr_mode == "quick" and pages and pages > max_text_pages:
        notes.append("quick_capped:%d/%d" % (max_text_pages, pages))  # P5-5：quick 静默截断显式化
    if sparse:
        notes.append("text_layer_sparse:density=%.1f" % density)
    if not engine_ok:
        if sparse:
            notes.append("ocr_engine_unavailable")
    else:
        cov = image_coverage_pages(path, list(range(len(page_texts)))) if page_texts else {}
        weak, med = ([], 0.0)
        if not sparse:
            weak, med = weak_page_indexes(page_texts)
            detail["medianPageWords"] = med
        detail["weakPages"] = sorted({i + 1 for i in weak})
        # 目标选择：整本稀疏，或书里存在成规模图片内容（median cov ≥0.03 或存在 ≥0.5 的整页图）
        # → 全书逐页 OCR（P0-4 的彻底解：图文混排书靠密度阈值必然漏半图页）；
        # 纯文本书 → 乱码页 ∪ 弱页 ∪ 前端可疑页。
        cov_vals = sorted(cov.values()) if cov else []
        has_images = bool(cov_vals) and (cov_vals[len(cov_vals) // 2] >= 0.03 or cov_vals[-1] >= 0.5)
        if sparse or has_images:
            targets = list(range(len(page_texts)))
            notes.append("full_page_ocr:reason=%s" % ("sparse" if sparse else "image_content"))
        else:
            targets = sorted({p["page"] - 1 for p in flagged} | set(weak) | {p - 1 for p in forced})

        if ocr_mode == "full":
            cap, budget = 1000000, 7200.0
        elif ocr_mode == "quick":
            cap, budget = 30, 40.0
        else:  # standard（含 deep）
            cap = _env_int("ASTRALPATH_OCR_MAX_PAGES", 1500)
            budget = float(os.environ.get("ASTRALPATH_OCR_BUDGET_S", "900"))

        if targets:
            # P1-11 夹层复核（仅 standard/full、非稀疏、页数充足时）：文本层疑似劣质出版方 OCR → full 下全页重抽
            if ocr_mode in ("standard", "full") and not sparse and len(page_texts) >= 20:
                overlay = overlay_suspect_check(path, page_texts)
                detail["overlay"] = overlay
                detail["overlaySuspect"] = bool(overlay.get("suspect"))
                if overlay.get("suspect"):
                    notes.append("overlay_suspect:sim=%.2f" % overlay.get("median_sim", 1.0))
                    if ocr_mode == "full":
                        targets = list(range(len(page_texts)))
            order = sorted(set(targets))[:cap]
            # 夹层可疑（出版方隐藏 OCR 层「合法但错误」）→ OCR 与文本层字数相近时以 OCR 为准
            merge_ratio = 1.0 if detail.get("overlaySuspect") else MERGE_OCR_WIN_RATIO
            merged = _ocr_and_merge(order, scale=2.0, max_pages=len(order), budget=budget, cov=cov,
                                    ocr_ratio=merge_ratio)
            if merged:
                text = "\n".join(page_texts).strip()
                ocr_used = True
            notes.append("targeted_ocr:targets=%d_merged=%d_cap=%d" % (len(order), merged, cap))
            if len(targets) > cap:
                notes.append("targeted_ocr_truncated:%d/%d_raise_ASTRALPATH_OCR_MAX_PAGES_or_use_full" % (cap, len(targets)))

    text = clean_ocr_text("\n".join(page_texts)).strip() if page_texts else ""
    if not sparse and not flagged:
        notes.append("text_layer_full")
    detail["ocrUsed"] = ocr_used  # P5-5：显式布尔，替代 notes 子串反推
    return text, pages, mode, notes, detail


def process_file(path: Path, ocr_mode: str = "standard", force_pages: list[int] | None = None) -> dict[str, Any]:
    info = tesseract_info()
    result: dict[str, Any] = {
        "file": str(path),
        "name": path.name,
        "size": path.stat().st_size if path.exists() else 0,
        "ok": path.exists(),
        "mode": None,
        "pageCount": 0,
        "extractedChars": 0,
        "needsOcr": False,
        "ocrUsed": False,
        "ocrEngine": _ocr_engine_name(),
        "tesseract": info,
        "notes": [],
        "textSample": "",
        "fullText": "",
        "chapters": [],
        "sections": [],
        "chapterBodies": [],
        "sectionBodies": [],
        "toc": {"chapters": [], "sections": []},
        "deepStats": {},
        "terms": [],
        "sentences": [],
        "nodes": [],
        "edges": [],
        "suggestedTasks": [],
        "chapterQuestions": [],
        "garblePages": [],
        "ocrPages": [],
    }
    if not path.exists():
        result["notes"].append("file_missing")
        return result

    try:
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        import deep_chapters
    except Exception as e:
        result["notes"].append(f"deep_chapters_import_error:{e}")
        deep_chapters = None

    text, pages, mode, notes, detail = extract_full_text(path, ocr_mode, force_pages=force_pages)
    result["pageCount"] = pages
    result["mode"] = mode
    result["extractedChars"] = len(text)
    result["notes"].extend(notes)
    result["garblePages"] = detail.get("garbledPages", [])
    result["ocrPages"] = detail.get("ocrPages", [])
    result["ocrUsed"] = bool(detail.get("ocrUsed"))  # P5-5：显式布尔（此前 notes 子串反推，「tesseract_unavailable」也误判 true）
        # P1-3 表格抽取：识别区转 Markdown
    if extract_markdown_tables is not None and text:
        try:
            text, tables = extract_markdown_tables(text)
            if tables:
                result["tables"] = tables
                result["notes"].append("tables:%d" % len(tables))
        except Exception as e:
            result["notes"].append("tables_error:%s" % str(e)[:60])
    result["fullText"] = text  # 供 API 存章节全文
    result["textSample"] = text[:4000]

    material_title = Path(path).stem
    # 上传落盘名形如 {32hex}_{书名}：去掉 id 前缀，避免题干出现 hash
    material_title = re.sub(r"^[0-9a-fA-F]{24,}_", "", material_title).strip() or Path(path).stem
    material_title = material_title[:48]

    # 深度章节解析：完整目录 + 章节正文 + 按章出题
    if deep_chapters is not None and text:
        try:
            deep = deep_chapters.build_deep_chapter_payload(text, material_title, questions_per_chapter=3)
            result["toc"] = deep.get("toc", {"chapters": [], "sections": []})
            result["chapterBodies"] = deep.get("chapterBodies", [])
            result["sectionBodies"] = deep.get("sectionBodies", [])
            result["deepStats"] = deep.get("stats", {})
            result["chapters"] = [
                {"title": c["title"], "kind": "chapter", "offset": c.get("offset", 0), "id": c.get("id")}
                for c in deep.get("chapterBodies", [])
            ]
            result["sections"] = deep.get("toc", {}).get("sections", [])
            # 今日任务候选：前几章的题
            cq = []
            for ch in deep.get("chapterBodies", [])[:8]:
                for q in ch.get("questions", [])[:2]:
                    cq.append({
                        "chapterId": ch.get("id"),
                        "chapterTitle": ch.get("title"),
                        **q,
                    })
            result["chapterQuestions"] = cq
            result["notes"].append(
                f"deep_parse:ch={result['deepStats'].get('chapterCount', 0)}"
                f",sec={result['deepStats'].get('sectionCount', 0)}"
                f",q={result['deepStats'].get('questionCount', 0)}"
            )
        except Exception as e:
            result["notes"].append(f"deep_parse_error:{e}")

    result["sentences"] = extract_sentences(text)

    # 知识图谱：完整目录入图
    try:
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        import kg_algorithm
        kg_text = text
        kg = kg_algorithm.build_knowledge_graph(kg_text, material_title, max_chapters=80, max_sections=200, max_terms=28)
        result["nodes"] = kg.get("nodes", [])
        result["edges"] = kg.get("edges", [])
        result["graphAlgorithm"] = kg.get("stats", {}).get("algorithm", "kg-v2")
        result["graphStats"] = kg.get("stats", {})
        # P2-14 输入质量闸：文本层欠抽取/夹层可疑/OCR 截断时，图谱可信度打折并显式标记
        if any(k in n for n in result["notes"]
               for k in ("sparse", "underextract", "overlay_suspect", "truncated", "radical")):
            result["graphStats"]["inputQuality"] = "low"
        else:
            result["graphStats"]["inputQuality"] = "ok"
        result["terms"] = [
            {"term": t.get("term"), "freq": t.get("freq"), "score": t.get("score")}
            for t in kg.get("terms", [])[:30]
        ]
        # 若 kg 章节数明显少于 deep TOC，用 deep TOC 补全图节点（目录完整性）
        deep_chs = result.get("toc", {}).get("chapters") or []
        deep_secs = result.get("toc", {}).get("sections") or []
        existing_names = {n.get("name") for n in result["nodes"]}
        if deep_chs and len(deep_chs) > len([n for n in result["nodes"] if n.get("description") == "chapter"]):
            course = material_title[:24] or "MATERIAL"
            added_ch = 0
            for c in deep_chs:
                if c["title"] in existing_names:
                    continue
                result["nodes"].append({
                    "id": f"TOC_{c['id']}",
                    "name": c["title"][:40],
                    "course": course,
                    "description": "chapter",
                    "source": "deep:toc-chapter",
                    "level": 0,
                    "layer": "chapter",
                    "weight": 1.0,
                })
                existing_names.add(c["title"])
                added_ch += 1
            # 时序边
            toc_ids = [n["id"] for n in result["nodes"] if str(n["id"]).startswith("TOC_CH")]
            for a, b in zip(toc_ids, toc_ids[1:]):
                result["edges"].append({
                    "from": a, "to": b, "edgeType": "prerequisite", "weight": 1.2,
                    "source": "deep:toc-sequence",
                })
            # 目录小节 → 章
            ch_id_by_title = {n["name"]: n["id"] for n in result["nodes"] if n.get("description") == "chapter"}
            ch_id_by_num = {}
            for c in deep_chs:
                if c.get("chapterNum") is not None:
                    ch_id_by_num[c["chapterNum"]] = f"TOC_{c['id']}" if f"TOC_{c['id']}" in {n["id"] for n in result["nodes"]} else None
            for s in deep_secs[:120]:
                if s["title"] in existing_names:
                    continue
                sid = f"TOC_{s['id']}"
                result["nodes"].append({
                    "id": sid,
                    "name": s["title"][:40],
                    "course": course,
                    "description": "section",
                    "source": "deep:toc-section",
                    "level": s.get("level", 1),
                    "layer": "section",
                    "weight": 0.8,
                })
                existing_names.add(s["title"])
                parent_id = None
                if s.get("parentId"):
                    parent_id = f"TOC_{s['parentId']}"
                    if parent_id not in {n["id"] for n in result["nodes"]}:
                        parent_id = None
                if parent_id is None and toc_ids:
                    parent_id = toc_ids[0]
                if parent_id:
                    result["edges"].append({
                        "from": parent_id, "to": sid, "edgeType": "prerequisite", "weight": 1.0,
                        "source": "deep:toc-section-parent",
                    })
            result["notes"].append(f"toc_graph_enrich:+{added_ch}ch")
    except Exception as e:
        result["notes"].append(f"kg_algorithm_fallback:{e}")
        chapters = result.get("chapters") or [{"title": material_title[:40], "kind": "chapter", "offset": 0}]
        nodes, edges = build_nodes_edges(chapters, [], material_title)
        result["nodes"] = nodes
        result["edges"] = edges

    if not result["suggestedTasks"]:
        result["suggestedTasks"] = build_suggested_tasks(result["nodes"], material_title)
    return result


def _parse_pages(raw) -> list[int] | None:
    """解析 --pages：逗号/分号/空格分隔的 1 基页码，去重排序；空则 None。"""
    if not raw:
        return None
    out: list[int] = []
    for tok in re.split(r"[,\s;]+", str(raw)):
        tok = tok.strip()
        if tok.isdigit():
            out.append(int(tok))
    return sorted(set(out)) or None


def main() -> int:
    parser = argparse.ArgumentParser(
        description="AstralPath OCR pipeline — tesseract CLI aligned",
        epilog="tesseract imagename outputbase [-l lang] [--oem oem] [--psm psm] [configfiles...]",
    )
    parser.add_argument("path", nargs="?", default=".", help="PDF/image/text path")
    parser.add_argument("--ocr", default="standard", choices=["none", "quick", "standard", "full", "tesseract-only"])
    parser.add_argument("--out", default="-")
    parser.add_argument("--info", action="store_true", help="print tesseract info and exit")
    parser.add_argument("--pages", default=None, help="comma separated 1-based page numbers to force OCR (from front-end pdf.js scan)")
    # 对齐 tesseract CLI 的可选参数
    parser.add_argument("-l", "--lang", default=None, help="tesseract language, e.g. chi_sim+eng")
    parser.add_argument("--oem", default=None, help="ocr engine mode 0-3")
    parser.add_argument("--psm", default=None, help="page segmentation mode 0-13")
    parser.add_argument("--tessdata", default=None, help="tessdata directory")
    args = parser.parse_args()

    if args.lang:
        global TESS_LANG
        TESS_LANG = args.lang
    if args.oem:
        global TESS_OEM
        TESS_OEM = str(args.oem)
    if args.psm:
        global TESS_PSM
        TESS_PSM = str(args.psm)
    if args.tessdata:
        os.environ["TESSDATA_PREFIX"] = args.tessdata

    if args.info:
        info = tesseract_info()
        print(json.dumps(info, ensure_ascii=False, indent=2))
        return 0 if info.get("available") else 1

    mode = args.ocr
    if mode == "tesseract-only":
        # 强制走 tesseract，跳过文本层
        try:
            pages = []
            try:
                import pypdfium2 as pdfium
                doc = pdfium.PdfDocument(str(Path(args.path)))
                pages = list(range(0, min(len(doc), 10)))
                doc.close()
            except Exception:
                pages = list(range(0, 10))
            text, note = tesseract_pdf_pages(Path(args.path), pages)
            payload = {
                "ok": True,
                "name": Path(args.path).name,
                "mode": note,
                "ocrUsed": True,
                "ocrEngine": "tesseract",
                "tesseract": tesseract_info(),
                "extractedChars": len(text),
                "textSample": clean_ocr_text(text)[:4000],
                "nodes": [],
                "edges": [],
                "notes": [note],
            }
        except Exception as e:
            payload = {"ok": False, "error": f"{type(e).__name__}: {e}", "trace": traceback.format_exc()}
    else:
        try:
            payload = process_file(Path(args.path), ocr_mode=mode, force_pages=_parse_pages(args.pages))
        except Exception as e:
            payload = {"ok": False, "error": f"{type(e).__name__}: {e}", "trace": traceback.format_exc()}

    text = json.dumps(payload, ensure_ascii=False)
    if args.out == "-":
        print(text)
    else:
        Path(args.out).write_text(text, encoding="utf-8")
        print(json.dumps({
            "ok": payload.get("ok", False),
            "out": args.out,
            "chars": payload.get("extractedChars"),
            "nodes": len(payload.get("nodes") or []),
            "ocrUsed": payload.get("ocrUsed"),
            "engine": payload.get("ocrEngine"),
            "tesseract": (payload.get("tesseract") or {}).get("exe"),
        }, ensure_ascii=False))
    return 0 if payload.get("ok", False) else 1


# ═══════════════════════════════════════════════════════════════
# OCR v2：预处理 / 置信度 / 多配置投票 / 中文断行 / 误识修复
# ═══════════════════════════════════════════════════════════════

CHAR_CONFUSIONS = (
    ("（", "("), ("）", ")"), ("［", "["), ("］", "]"),
    ("，", ","), ("；", ";"), ("：", ":"), ("？", "?"),
    ("！", "!"), ("＝", "="), ("＋", "+"), ("－", "-"), ("×", "*"),
)


def preprocess_image(path: Path) -> Path:
    """灰度 → 对比度拉伸 → 自适应二值 → 轻去噪。显著提升 tesseract 命中率。"""
    try:
        from PIL import Image, ImageOps, ImageFilter
    except Exception:
        return path
    try:
        im = Image.open(path).convert("L")
        im = ImageOps.autocontrast(im, cutoff=2)
        # 轻度锐化边缘，利于字形分割
        im = im.filter(ImageFilter.UnsharpMask(radius=1, percent=80, threshold=2))
        out = path.with_name(path.stem + "_pp.png")
        im.save(out, format="PNG")
        return out
    except Exception:
        return path


def parse_tsv_words(tsv_path: Path, min_conf: float = 40.0) -> list[tuple[str, float, float, float]]:
    """读 tesseract TSV → (text, conf, x, y) 词列表，过滤低置信与噪声。"""
    words: list[tuple[str, float, float, float]] = []
    if not tsv_path.exists():
        return words
    try:
        lines = tsv_path.read_text(encoding="utf-8", errors="ignore").splitlines()
    except Exception:
        return words
    for ln in lines[1:]:
        cols = ln.split("\t")
        if len(cols) < 12:
            continue
        try:
            conf = float(cols[10])
            text = cols[11].strip()
            x, y = float(cols[6]), float(cols[7])
        except Exception:
            continue
        if not text or conf < min_conf:
            continue
        if len(text) == 1 and not text.isdigit():
            continue
        words.append((text, conf, x, y))
    return words


def words_to_text(words: list[tuple[str, float, float, float]]) -> str:
    """按行带聚类 + 行内 x 排序（阅读顺序第一步）。"""
    if not words:
        return ""
    rows: list[list[tuple[str, float, float, float]]] = []
    for w in sorted(words, key=lambda t: (t[3], t[2])):
        row = next((r for r in rows if abs(r[0][3] - w[3]) <= 8), None)
        if row is None:
            row = []
            rows.append(row)
        row.append(w)
    rows.sort(key=lambda r: sum(x[3] for x in r) / len(r))
    return "\n".join(" ".join(t[0] for t in sorted(r, key=lambda t: t[2])) for r in rows)


def fix_ocr_text(text: str, aggressive: bool | None = None) -> str:
    """中英文粘连拆开 + 软断行合并（不改变字符内容）。
    P1-10：全角→半角标点、×→* 等会改写原文的归一化只在 aggressive=True 时做
    （env ASTRALPATH_OCR_AGGRESSIVE_NORMALIZE=1），默认保留原文标点。"""
    if aggressive is None:
        aggressive = os.environ.get("ASTRALPATH_OCR_AGGRESSIVE_NORMALIZE", "") == "1"
    s = (text or "").replace("　", " ").replace("\x00", "")
    if aggressive:
        for a, b in CHAR_CONFUSIONS:
            s = s.replace(a, b)
    s = re.sub(r"([一-鿿])([A-Za-z])", r"\1 \2", s)
    s = re.sub(r"([A-Za-z])([一-鿿])", r"\1 \2", s)
    # 中文软断行：行尾无标点则与下一行合并；P6-2：任一侧带行首缩进（代码行）不合并
    out: list[str] = []
    buf = ""
    for line in (ln.rstrip() for ln in s.splitlines() if ln.strip()):
        if not buf:
            buf = line
            continue
        cjk = sum(1 for c in buf if "一" <= c <= "鿿")
        soft = (not buf.endswith(("。", "！", "？", ".", ":", "：", ";", "；"))
                and not line[:1].isspace() and not buf[:1].isspace())
        if cjk * 2 >= len(buf) and soft and len(buf) < 80:
            buf += line
            continue
        out.append(buf)
        buf = line
    if buf:
        out.append(buf)
    return "\n".join(out)


def vote_passes(passes: list[str], keep_ratio: float = 0.5) -> str:
    """行级多数票：多 PSM 结果合成，去掉幻觉行。"""
    lists = [p.splitlines() for p in passes if p and p.strip()]
    if not lists:
        return ""
    if len(lists) == 1:
        return "\n".join(x.strip() for x in lists[0] if x.strip())
    counts: dict[str, int] = {}
    for lines in lists:
        for ln in {x.strip() for x in lines if len(x.strip()) > 1}:
            counts[ln] = counts.get(ln, 0) + 1
    need = int(len(lists) * keep_ratio + 0.999)  # ceil
    order: list[str] = []
    for ln in lists[0]:
        s = ln.strip()
        if len(s) > 1 and counts.get(s, 0) >= need:
            order.append(s)
    for s, c in counts.items():
        if c >= need and s not in order:
            order.append(s)
    return "\n".join(order)


def ocr_image_v2(image_path: Path, workdir: Path, tag: str) -> str:
    """
    一页三配置（psm 3/4/6）+ TSV 置信度重建 + 行投票 + 纠错。
    """
    exe = find_tesseract()
    if not exe:
        return ""
    tessdata = find_tessdata()
    lang = resolve_lang(tessdata)
    pre = preprocess_image(image_path)
    passes: list[str] = []
    for psm in ("3", "4", "6"):
        base = workdir / f"{tag}_p{psm}"
        cmd = [exe, str(pre), str(base), "-l", lang, "--oem", TESS_OEM, "--psm", psm, "tsv"]
        if tessdata:
            cmd += ["--tessdata-dir", tessdata]
        try:
            subprocess.run(cmd, capture_output=True, timeout=90)
            words = parse_tsv_words(Path(str(base) + ".tsv"), min_conf=45)
            if words:
                passes.append(words_to_text(words))
        except Exception:
            continue
    if not passes:
        return ""
    merged = vote_passes(passes, keep_ratio=0.5)
    return fix_ocr_text(merged)


# ---------------------------------------------------------------------------
# RapidOCR 引擎（rapidocr v3 包）+ 页级并行
# 引擎选择：ASTRALPATH_OCR_ENGINE = auto(默认) | rapid | tesseract
# 并行度：ASTRALPATH_OCR_WORKERS（默认 4 个线程，每线程独立引擎实例）
# 单实例线程：ASTRALPATH_OCR_INTRA（默认 2，EngineConfig.onnxruntime.intra_op_num_threads）
# ---------------------------------------------------------------------------

_RAPID_TL = threading.local()
_RAPID_INIT_LOCK = threading.Lock()
_PDFIUM_RENDER_LOCK = threading.Lock()  # pdfium 全局状态非线程安全：渲染串行，OCR 并行


def rapidocr_available() -> bool:
    try:
        import rapidocr  # noqa: F401
        return True
    except Exception:
        return False


def _env_int(name: str, default: int) -> int:
    try:
        return max(1, int(os.environ.get(name, str(default))))
    except Exception:
        return default


def _rapid_workers() -> int:
    return _env_int("ASTRALPATH_OCR_WORKERS", 4)


def _rapid_intra() -> int:
    return _env_int("ASTRALPATH_OCR_INTRA", 2)


def _rapid_engine():
    """线程本地 RapidOCR 实例：每线程独立加载，EngineConfig 限制单实例线程防超订阅。
    初始化（含模型下载/校验）用全局锁串行化，避免多线程同时落盘同名模型文件。"""
    eng = getattr(_RAPID_TL, "engine", None)
    if eng is not None:
        return eng
    from rapidocr import RapidOCR

    intra = _rapid_intra()
    with _RAPID_INIT_LOCK:
        eng = getattr(_RAPID_TL, "engine", None)
        if eng is not None:
            return eng
        eng = RapidOCR(params={
            "EngineConfig.onnxruntime.intra_op_num_threads": intra,
            "EngineConfig.onnxruntime.inter_op_num_threads": 1,
        })
        _RAPID_TL.engine = eng
    return eng


def _rapid_result_to_text(res, code: bool = False) -> str:
    """boxes+txts → 行序文本：按 y 聚行、行内按 x 排序（对齐 words_to_text 口径）。
    code=True（P6-2）：按行首 x 起点相对全页最小行首的偏移重建缩进，
    unit 取词高中位数×0.55——代码页缩进不再被拍平。"""
    txts = getattr(res, "txts", None)
    txts = list(txts) if txts is not None else []
    boxes = getattr(res, "boxes", None)
    boxes = list(boxes) if boxes is not None else []
    if not txts:
        return ""
    words = []
    for i, t in enumerate(txts):
        box = boxes[i] if i < len(boxes) else None
        x0 = y = h = 0.0
        if box is not None and len(box) >= 4:
            try:
                xs = [float(p[0]) for p in box]
                ys = [float(p[1]) for p in box]
                x0, y = min(xs), sum(ys) / len(ys)
                h = max(ys) - min(ys)
            except Exception:
                x0 = y = 0.0
        words.append((str(t), x0, y, h))
    rows: list[list[tuple]] = []
    for wd in sorted(words, key=lambda t: (t[2], t[1])):
        row = next((r for r in rows if abs(r[0][2] - wd[2]) <= 14), None)
        if row is None:
            row = []
            rows.append(row)
        row.append(wd)
    rows.sort(key=lambda r: sum(x[2] for x in r) / len(r))
    if not code:
        return "\n".join(" ".join(t[0] for t in sorted(r, key=lambda t: t[1])) for r in rows)
    hs = sorted(wd[3] for r in rows for wd in r if wd[3] > 4)
    unit = max(8.0, (hs[len(hs) // 2] if hs else 24.0) * 0.5)  # 拉丁字符宽 ≈ 0.5×字高
    base = min((wd[1] for r in rows for wd in r), default=0.0)
    out_lines = []
    for r in rows:
        left = min(wd[1] for wd in r)
        off = left - base
        pad = int(round(off / unit)) if off >= unit * 0.6 else 0  # 60% 容差滤 x 噪声
        out_lines.append(" " * pad + " ".join(t[0] for t in sorted(r, key=lambda t: t[1])))
    return "\n".join(out_lines)


def _render_scale_for(path: Path, page_index: int, base: float = 2.0) -> float:
    """P1-9 自适应渲染倍率：短边渲染不足 MinSize(1080px) 时提高倍率（封顶 4x）。"""
    if os.environ.get("ASTRALPATH_OCR_RENDER_SCALE"):
        try:
            return float(os.environ["ASTRALPATH_OCR_RENDER_SCALE"])
        except Exception:
            pass
    try:
        import pypdfium2 as pdfium
        doc = pdfium.PdfDocument(str(path))
        page = doc[page_index]
        w, h = page.get_size()
        page.close()
        doc.close()
        long_pt = max(float(w), float(h))
        if long_pt > 0 and long_pt * base < 1080.0:
            return min(4.0, round(1080.0 / long_pt, 2))
    except Exception:
        pass
    return base


# ---------------------------------------------------------------------------
# P6-1/2/3 内容专项通道：代码保真 / 公式密集重试 / 旋转文本
# ---------------------------------------------------------------------------
_CODE_TOKEN_RE = re.compile(
    r"\{\s*$|};|=>|</[a-zA-Z]|::|\)\s*;"
    r"|^\s*(def |class |import |from |func |public |private |static |var |let |const |return |if |for |while )",
    re.M,
)


def _looks_like_code(text: str) -> bool:
    """P6-2 代码页判定：代码标记行 ≥3（纯文本页/公式页不会误入）。"""
    hits = sum(1 for ln in (text or "").splitlines() if _CODE_TOKEN_RE.search(ln))
    return hits >= 3


_CIRCLED_FIX = str.maketrans({"©": "①", "➊": "①", "➋": "②", "➌": "③", "➍": "④", "➎": "⑤",
                              "❶": "①", "❷": "②", "❸": "③", "❹": "④", "❺": "⑤"})


def _fix_code_tokens(text: str) -> str:
    """P6-2 代码页保守 token 纠错（规则全部窄化，宁可漏纠不可误伤）：
    ① 圈码/© 还原 ①②③（OCR 高发误识：Kotlin p100/p196 圈码→©）；
    ② 前邻小写/数字的词尾大写 O → 0（'CartPole-vO'→'-v0'，强化学习 p233 实测）；
    ③ 数字夹的 l/I → 1。"""
    s = text.translate(_CIRCLED_FIX)
    s = re.sub(r"(?<=[a-z0-9_])O(?=[^A-Za-z]|$)", "0", s)
    s = re.sub(r"(?<=\d)l(?=\d)", "1", s)
    s = re.sub(r"(?<=\d)I(?=\d)", "1", s)
    return s


_MATH_CHARS = "∑∏∫∂√∞≠≈≤≥±×÷∈∉⊂⊃∪∩→←⇒⇐αβγδεθλµμπσφωΓΔΘΛΞΠΣΦΨΩ∇"


def _math_symbol_count(text: str) -> int:
    return sum(1 for ch in (text or "") if ch in _MATH_CHARS)


def _formula_dense(text: str) -> bool:
    """P6-1 公式密集页判定：数学符号 ≥12 且占词字符 ≥1.5%（纯符号矩阵页 words 可为 0）。"""
    n = _math_symbol_count(text)
    words = len(WORD_CHAR_RE.findall(text or ""))
    return n >= 12 and n / max(1, words) >= 0.015


def _vertical_box_count(res, img_h: float) -> int:
    """P6-3 竖排框计数：h > 2.5×w 且 >页高 8% 的检测框（旋转 y 轴标签特征，花书 p179）。"""
    boxes = getattr(res, "boxes", None)
    boxes = list(boxes) if boxes is not None else []
    n = 0
    for b in boxes:
        if b is None or len(b) < 4:
            continue
        try:
            xs = [float(p[0]) for p in b]
            ys = [float(p[1]) for p in b]
            w, h = max(xs) - min(xs), max(ys) - min(ys)
        except Exception:
            continue
        if h > 2.5 * max(w, 2.0) and img_h > 0 and h > 0.08 * img_h:
            n += 1
    return n


def ocr_image_rapid(image_path: Path) -> str:
    """单页 PNG → RapidOCR 行序文本（P6-1/2/3 智能通道版）。
    ① 基础识别；② 代码页：缩进重建 + 保守 token 纠错；③ 检出竖排框或近空页时
       ±90° 重试：整页更优则整体替换（旋转扫描页），否则最多附加 5 行增量
       （竖排 y 轴标签召回；增量行数封顶防垃圾注入）。"""
    from PIL import Image

    img = Image.open(image_path)
    img = preprocess_pil_for_ocr(img)
    eng = _rapid_engine()
    try:
        res = eng(img)
        text0 = _rapid_result_to_text(res)
        if _looks_like_code(text0):
            text0 = _fix_code_tokens(_rapid_result_to_text(res, code=True))
    except Exception:
        return ""
    try:
        vboxes = _vertical_box_count(res, float(img.height))
    except Exception:
        vboxes = 0
    chars0 = len(WORD_CHAR_RE.findall(text0))
    if vboxes == 0 and chars0 >= 20:
        return fix_ocr_text(text0)
    _TP = getattr(Image, "Transpose", Image)  # Pillow≥10 移除了模块级常量
    best_full = text0
    extras: list[str] = []
    for rot_name in ("ROTATE_90", "ROTATE_270"):
        try:
            t_r = _rapid_result_to_text(eng(img.transpose(getattr(_TP, rot_name))))
        except Exception:
            continue
        if len(WORD_CHAR_RE.findall(t_r)) > len(WORD_CHAR_RE.findall(best_full)) * 1.15:
            best_full = t_r
        elif vboxes >= 2:
            for ln in t_r.splitlines():
                s = ln.strip()
                if (len(s) >= 6 and s not in text0 and s not in extras
                        and len(WORD_CHAR_RE.findall(s)) >= 4):
                    extras.append(s)
    if best_full is not text0:
        return fix_ocr_text(best_full)
    if extras:
        return fix_ocr_text(text0 + "\n" + "\n".join(extras[:5]))
    return fix_ocr_text(text0)


def rapid_pdf_pages_detail(path: Path, page_indexes: list[int], scale: float = 2.0,
                           max_pages: int = 12, time_budget_s: float = 40.0) -> tuple[str, str, dict[int, str]]:
    """PDF → PNG（复用 pypdfium2 渲染）→ RapidOCR 页级并行识别，返回 {0基页号: 文本}。"""
    if not rapidocr_available():
        return "", "rapidocr_unavailable", {}
    try:
        import pypdfium2 as pdfium

        doc = pdfium.PdfDocument(str(path))
        total = len(doc)
        doc.close()
    except Exception as e:
        return "", f"pdf_open_error:{e}", {}
    import time as _t

    _t0 = _t.time()
    targets = sorted({i for i in page_indexes if 0 <= i < total})[:max_pages]
    workdir = Path(tempfile.gettempdir()) / f"astralpath_rapid_{os.getpid()}"
    workdir.mkdir(parents=True, exist_ok=True)

    _failed: list[int] = []

    def _one(idx: int) -> tuple[int, str]:
        ckey = _ocr_cache_key(path, idx, scale)
        cached = _ocr_cache_get(ckey)
        if cached is not None:
            return idx, cached
        png = workdir / f"page_{idx:04d}.png"
        with _PDFIUM_RENDER_LOCK:
            # pdfium 全局状态非线程安全：倍率计算也要开文档，必须与渲染一起串行
            eff_scale = _render_scale_for(path, idx, base=scale)
            rendered = render_pdf_page_png(path, idx, scale=eff_scale, out_png=png)
        if rendered is None:
            _failed.append(idx)
            return idx, ""
        try:
            text = ocr_image_rapid(rendered)
            # P6-1 公式重试：数学符号密集页升倍率（≤3.2）重识别一次，
            # 按（词字符 + 2×数学符号）取更优者；高倍率结果复用同一缓存键
            if _formula_dense(text) and float(eff_scale) < 3.0:
                hi_scale = min(3.2, eff_scale * 1.4)
                png2 = workdir / f"page_{idx:04d}_hi.png"
                with _PDFIUM_RENDER_LOCK:
                    rendered2 = render_pdf_page_png(path, idx, scale=hi_scale, out_png=png2)
                if rendered2 is not None:
                    try:
                        t2 = ocr_image_rapid(rendered2)
                        score = lambda t: len(WORD_CHAR_RE.findall(t)) + 2 * _math_symbol_count(t)
                        if score(t2) > score(text):
                            text = t2
                    except Exception:
                        pass
                    finally:
                        try:
                            png2.unlink(missing_ok=True)
                        except Exception:
                            pass
            _ocr_cache_put(ckey, text)
            return idx, text
        except Exception:
            _failed.append(idx)
            return idx, ""
        finally:
            try:
                png.unlink(missing_ok=True)
            except Exception:
                pass

    pages_out: dict[int, str] = {}
    workers = _rapid_workers()
    if workers <= 1 or len(targets) <= 1:
        for idx in targets:
            if pages_out and _t.time() - _t0 > time_budget_s:
                break
            pages_out[idx] = _one(idx)[1]
    else:
        from concurrent.futures import ThreadPoolExecutor

        with ThreadPoolExecutor(max_workers=workers) as pool:
            for start in range(0, len(targets), workers):
                if pages_out and _t.time() - _t0 > time_budget_s:
                    break
                for idx, tx in pool.map(_one, targets[start:start + workers]):
                    pages_out[idx] = tx

    used = sum(1 for v in pages_out.values() if v.strip())
    text = "\n".join(pages_out[i] for i in sorted(pages_out) if pages_out[i].strip())
    # P0-2 流式进度：宿主桥读 stderr 的 PROGRESS 行
    try:
        sys.stderr.write("PROGRESS ocr %d/%d\\n" % (used, min(max_pages, total)))
        sys.stderr.flush()
    except Exception:
        pass
    note = f"rapidocr:{used}/{min(max_pages, total)} workers={workers} intra={_rapid_intra()}"
    if _failed:
        note += f" fail={len(_failed)}"
    return text, note, pages_out


_RAPID_PROBE_OK: bool | None = None
_RAPID_PROBE_LOCK = threading.Lock()


def rapid_engine_usable() -> bool:
    """P5-5：rapid 不再「可 import 即可用」——惰性实例化探针（每进程一次）。
    模型加载失败 → False → 分发器回落 tesseract。此前失败时每页静默 fail=0。"""
    global _RAPID_PROBE_OK
    if not rapidocr_available():
        return False
    if _RAPID_PROBE_OK is None:
        with _RAPID_PROBE_LOCK:
            if _RAPID_PROBE_OK is None:
                try:
                    _rapid_engine()
                    _RAPID_PROBE_OK = True
                except Exception:
                    _RAPID_PROBE_OK = False
    return _RAPID_PROBE_OK


def _ocr_engine_name() -> str:
    env = (os.environ.get("ASTRALPATH_OCR_ENGINE") or "auto").strip().lower()
    want_rapid = env in ("", "auto", "rapid", "rapidocr")
    if want_rapid and rapidocr_available():
        return "rapid"
    return "tesseract"


def ocr_engine_ready(info: dict | None = None) -> bool:
    """当前引擎是否可用：rapid 需通过惰性实例化探针；tesseract 需 exe + tessdata。"""
    if _ocr_engine_name() == "rapid":
        return rapid_engine_usable()
    return bool((info if info is not None else tesseract_info()).get("available"))


def ocr_pdf_pages_detail(path: Path, page_indexes: list[int], scale: float = 2.0,
                         max_pages: int = 12, time_budget_s: float = 40.0) -> tuple[str, str, dict[int, str]]:
    """按 ASTRALPATH_OCR_ENGINE 分发到 RapidOCR / tesseract。
    P5-5：rapid 探针失败自动回落 tesseract（此前 import 成功即认定可用，
    模型坏掉时整本每页静默失败）。"""
    if _ocr_engine_name() == "rapid" and rapid_engine_usable():
        return rapid_pdf_pages_detail(path, page_indexes, scale=scale,
                                      max_pages=max_pages, time_budget_s=time_budget_s)
    return tesseract_pdf_pages_detail(path, page_indexes, scale=scale,
                                      max_pages=max_pages, time_budget_s=time_budget_s)


def ocr_pdf_pages(path: Path, page_indexes: list[int], scale: float = 2.0,
                  max_pages: int = 12, time_budget_s: float = 40.0) -> tuple[str, str]:
    text, note, _pages = ocr_pdf_pages_detail(
        path, page_indexes, scale=scale, max_pages=max_pages, time_budget_s=time_budget_s)
    return text, note


def ocr_quality_grade(text: str) -> dict[str, Any]:
    lines = [ln for ln in (text or "").splitlines() if ln.strip()]
    letters = sum(1 for c in text if c.isalpha())
    cjk = sum(1 for c in text if "一" <= c <= "鿿")
    cjk_ratio = (cjk / letters) if letters else 0.0
    noise = sum(1 for ln in lines if len(ln) <= 8 and re.fullmatch(r"[\W_]+", ln or " "))
    noise_ratio = (noise / len(lines)) if lines else 0.0
    score = 0.4 * min(cjk_ratio / 0.6, 1.0) + 0.4 * (1 - noise_ratio) + 0.2 * min(len(text) / 400.0, 1.0)
    grade = "A" if score >= 0.85 else "B" if score >= 0.7 else "C" if score >= 0.5 else "D"
    return {"score": round(score, 4), "grade": grade, "cjk_ratio": round(cjk_ratio, 4),
            "noise_ratio": round(noise_ratio, 4), "lines": len(lines)}


if __name__ == "__main__":
    sys.exit(main())
