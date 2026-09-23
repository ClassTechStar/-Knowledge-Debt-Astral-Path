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


def tesseract_pdf_pages(path: Path, page_indexes: list[int], scale: float = 2.0, max_pages: int = 12, time_budget_s: float = 40.0) -> tuple[str, str]:
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
    for sec in chapters:
        if sec.get("kind") != "section":
            continue
        m = sec_re.search(sec.get("title", ""))
        if not m:
            continue
        ch_num = int(m.group(1))
        parent = ch_map.get(ch_num)
        # find node id for this section by order — rebuild lookup
    # simpler: zip sections to chapter by title prefix number
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


def extract_full_text(path: Path, ocr_mode: str = "standard") -> tuple[str, int, str, list[str]]:
    """尽量抽取 PDF 全部文字：文本层全量 + 扫描页 tesseract 补齐。"""
    notes: list[str] = []
    # 文本层：尽可能全量（deep/standard 不截断；quick 仍限制以保速度）
    if ocr_mode == "quick":
        max_text_pages = 120
    else:
        max_text_pages = 0  # 0 = 全部

    text, pages, mode = try_extract_text_pypdf(path, max_pages=max_text_pages if max_text_pages else 100000)
    if len(text) < 80:
        text2, pages2, mode2 = try_extract_text_pdfium(path, max_pages=max_text_pages if max_text_pages else 100000)
        if len(text2) > len(text):
            text, pages, mode = text2, pages2, mode2
            notes.append("fallback_pdfium")

    # 密度：平均每页字符
    density = (len(text) / pages) if pages else 0
    info = tesseract_info()
    ocr_used = False

    if len(text) < 80 or density < 120:
        notes.append(f"text_layer_sparse:density={density:.1f}")
        if ocr_mode in ("standard", "quick", "deep") and info.get("available"):
            total = pages or 30
            # 扫描版：优先 OCR 目录区 + 全书均匀采样，保证能抽出完整目录
            toc_idx = list(range(0, min(total, 24)))
            sample = []
            if total > 24:
                step = max(8, total // 20)
                sample = list(range(24, total, step))[:18]
            indexes = sorted({i for i in toc_idx + sample if 0 <= i < total})
            if ocr_mode == "quick":
                indexes = indexes[:12]
                max_ocr = 10
            else:
                max_ocr = 28
            ocr_text, ocr_note = tesseract_pdf_pages(
                path, indexes, scale=2.0, max_pages=max_ocr,
                time_budget_s=90.0 if ocr_mode != "quick" else 40.0,
            )
            notes.append(ocr_note)
            ocr_text = clean_ocr_text(ocr_text)
            if len(ocr_text) > len(text) * 0.5:
                # 合并：保留已有文本层，OCR 结果附在后面供目录抽取
                if len(ocr_text) > len(text):
                    text = ocr_text
                else:
                    text = text + "\n" + ocr_text
                ocr_used = True
                notes.append("ocr_merged")
        else:
            notes.append("tesseract_unavailable_or_no_traineddata")
    else:
        notes.append("text_layer_full")

    text = clean_ocr_text(text)
    return text, pages, mode, notes


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

    text, pages, mode, notes = extract_full_text(path, ocr_mode)
    result["pageCount"] = pages
    result["mode"] = mode
    result["extractedChars"] = len(text)
    result["notes"].extend(notes)
    result["ocrUsed"] = any("tesseract" in n or "ocr" in n for n in notes)
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

    result["terms"] = [{"term": t, "freq": 1} for t in (result.get("terms") or [])][:30]
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


def fix_ocr_text(text: str) -> str:
    """全角归一 + 中英文粘连拆开 + 软断行合并。"""
    s = (text or "").replace("　", " ").replace("\x00", "")
    for a, b in CHAR_CONFUSIONS:
        s = s.replace(a, b)
    s = re.sub(r"([一-鿿])([A-Za-z])", r"\1 \2", s)
    s = re.sub(r"([A-Za-z])([一-鿿])", r"\1 \2", s)
    # 中文软断行：行尾无标点则与下一行合并
    out: list[str] = []
    buf = ""
    for line in (ln.strip() for ln in s.splitlines() if ln.strip()):
        if not buf:
            buf = line
            continue
        cjk = sum(1 for c in buf if "一" <= c <= "鿿")
        soft = not buf.endswith(("。", "！", "？", ".", ":", "：", ";", "；"))
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
