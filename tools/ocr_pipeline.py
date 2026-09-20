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
import traceback
from pathlib import Path
from typing import Any

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
    r"C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path\tools\tessdata",
    r"C:\Program Files\Tesseract-OCR\tessdata",
    r"C:\Users\18948\Documents\GitHub\tesseract\tessdata",
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


def tesseract_pdf_pages(path: Path, page_indexes: list[int], scale: float = 2.0, max_pages: int = 12) -> tuple[str, str]:
    """PDF → PNG → tesseract（官方 CLI）逐页识别。"""
    exe = find_tesseract()
    if not exe:
        return "", "tesseract_unavailable"
    tessdata = find_tessdata()
    lang = resolve_lang(tessdata)
    chunks: list[str] = []
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
        return "", f"pdf_open_error:{e}"

    for idx in sorted({i for i in page_indexes if 0 <= i < total})[:max_pages]:
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
            chunks.append(f"[page {idx + 1}]\n{result['text']}")
            used += 1

    text = "\n".join(chunks)
    note = f"tesseract:{used}/{min(max_pages, total)} lang={lang} oem={TESS_OEM} psm={TESS_PSM}"
    return text, note


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
        s = line.strip()
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

    chapter_nodes: list[str] = []
    for ch in chapters:
        if len(chapter_nodes) >= 24:
            break
        node_id = next_id()
        nodes.append({
            "id": node_id,
            "name": ch["title"][:40],
            "course": material_title[:24] or "MATERIAL",
            "description": ch.get("kind", "chapter"),
            "source": f"tesseract:{ch.get('kind')}",
        })
        chapter_nodes.append(node_id)

    for a, b in zip(chapter_nodes, chapter_nodes[1:]):
        edges.append({"from": a, "to": b, "edgeType": "prerequisite", "weight": 1.2,
                      "source": "auto:chapter-sequence"})

    for term in terms[:20]:
        node_id = next_id()
        nodes.append({
            "id": node_id,
            "name": term["term"][:28],
            "course": material_title[:24] or "MATERIAL",
            "description": f"关键词 freq={term['freq']}",
            "source": "auto:term-frequency",
        })
        if chapter_nodes:
            edges.append({"from": node_id, "to": chapter_nodes[0], "edgeType": "prerequisite",
                          "weight": 1.0, "source": "auto:term-anchor"})

    if not chapter_nodes:
        term_ids = [n["id"] for n in nodes]
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


def process_file(path: Path, ocr_mode: str = "standard") -> dict[str, Any]:
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
        "ocrEngine": "tesseract",
        "tesseract": info,
        "notes": [],
        "textSample": "",
        "chapters": [],
        "terms": [],
        "sentences": [],
        "nodes": [],
        "edges": [],
        "suggestedTasks": [],
    }
    if not path.exists():
        result["notes"].append("file_missing")
        return result

    max_text_pages = 45 if ocr_mode == "standard" else 25
    text, pages, mode = try_extract_text_pypdf(path, max_pages=max_text_pages)
    if len(text) < 80:
        text2, pages2, mode2 = try_extract_text_pdfium(path, max_pages=max_text_pages)
        if len(text2) > len(text):
            text, pages, mode = text2, pages2, mode2

    result["pageCount"] = pages
    result["mode"] = mode
    result["extractedChars"] = len(text)

    if len(text) < 80:
        result["needsOcr"] = True
        result["notes"].append("text_layer_missing_or_sparse")
        if ocr_mode in ("standard", "quick") and info.get("available"):
            total = pages or 30
            if ocr_mode == "quick":
                indexes = list(range(0, min(total, 8)))
                max_ocr = 6
            else:
                indexes = list(range(0, min(total, 12)))
                indexes += [min(total - 1, i) for i in (20, 40, 60, 80, 100, 120, 150, 200, 260)]
                max_ocr = 12
            indexes = sorted({i for i in indexes if 0 <= i < max(total, 1)})
            ocr_text, ocr_note = tesseract_pdf_pages(path, indexes, scale=2.0, max_pages=max_ocr)
            result["notes"].append(ocr_note)
            ocr_text = clean_ocr_text(ocr_text)
            if len(ocr_text) > len(text):
                text = ocr_text
                result["ocrUsed"] = True
                result["mode"] = f"{mode}+{ocr_note}"
        elif ocr_mode in ("standard", "quick"):
            result["notes"].append("tesseract_unavailable_or_no_traineddata")
    else:
        result["notes"].append("text_layer_ok")
        text = clean_ocr_text(text)

    result["textSample"] = text[:4000]
    result["extractedChars"] = len(text)
    chapters = extract_chapters(text)
    terms = extract_terms(text)
    sentences = extract_sentences(text)

    if len(chapters) < 2:
        stem = path.stem
        title = re.sub(r"[_\-]+", " ", stem)
        chapters = [{"title": title[:40], "kind": "title", "offset": 0}]
        result["notes"].append("chapter_fallback_from_filename")
        if not terms:
            tokens = re.findall(r"[一-鿿]{2,10}|[A-Za-z]{3,20}", title)
            terms = [{"term": t, "freq": 1} for t in tokens[:12]] or [{"term": title[:16], "freq": 1}]

    try:
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        import kg_algorithm
        # 直接用优化算法构图（完整文本）
        kg = kg_algorithm.build_knowledge_graph(text, Path(path).stem)
        chapters = [{"title": c, "kind": "chapter", "offset": 0} for c in kg.get("chapters", [])]
        terms = kg.get("terms", [])
        result["nodes"] = kg.get("nodes", [])
        result["edges"] = kg.get("edges", [])
        result["graphAlgorithm"] = kg.get("stats", {}).get("algorithm", "kg-v2")
        result["graphStats"] = kg.get("stats", {})
        result["chapters"] = chapters[:40]
        result["terms"] = [{"term": t.get("term"), "freq": t.get("freq"), "score": t.get("score")} for t in terms[:30]]
        result["sentences"] = sentences
        result["suggestedTasks"] = build_suggested_tasks(result["nodes"], path.stem)
        return result
    except Exception as e:
        result["notes"].append(f"kg_algorithm_fallback:{e}")
        nodes, edges = build_nodes_edges(chapters, terms, path.stem)
        result["chapters"] = chapters[:40]
        result["terms"] = terms
        result["sentences"] = sentences
        result["nodes"] = nodes
        result["edges"] = edges
        result["suggestedTasks"] = build_suggested_tasks(nodes, path.stem)
        return result


def main() -> int:
    parser = argparse.ArgumentParser(
        description="AstralPath OCR pipeline — tesseract CLI aligned",
        epilog="tesseract imagename outputbase [-l lang] [--oem oem] [--psm psm] [configfiles...]",
    )
    parser.add_argument("path", nargs="?", default=".", help="PDF/image/text path")
    parser.add_argument("--ocr", default="standard", choices=["none", "quick", "standard", "tesseract-only"])
    parser.add_argument("--out", default="-")
    parser.add_argument("--info", action="store_true", help="print tesseract info and exit")
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
            payload = process_file(Path(args.path), ocr_mode=mode)
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


if __name__ == "__main__":
    sys.exit(main())
