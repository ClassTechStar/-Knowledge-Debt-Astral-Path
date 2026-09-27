# -*- coding: utf-8 -*-
"""
全量 OCR 对比：独立(PyMuPDF+RapidOCR) vs 项目(ocr_pipeline_umi)
- 覆盖 12 本 PDF TEST 每一页
- 字符级差异（CJK 不分词）
- 断点续跑：每书完成后写 state
用法：python ocr_full_compare.py [书名关键字]
"""
from __future__ import annotations

import json
import os
import re
import sys
import time
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

PDF_DIR = Path(r"C:\Users\18948\Downloads\PDF TEST")
OUT = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\ocr-full-compare")
OUT.mkdir(parents=True, exist_ok=True)
STATE = OUT / "state.json"
WORD = re.compile(r"[A-Za-z0-9一-鿿]")
CJK = re.compile(r"[一-鿿]")


def nchars(s):
    return len(WORD.findall(s or ""))


def load_state():
    if STATE.exists():
        return json.loads(STATE.read_text(encoding="utf-8"))
    return {"done": {}}


def save_state(st):
    STATE.write_text(json.dumps(st, ensure_ascii=False), encoding="utf-8")


# ── 独立抽取：文本层全覆盖；低文本页 RapidOCR（1.5x）──
def independent_full(pdf: Path, ocr_low: bool = True, ocr_cap: int = 25):
    import numpy as np
    import pymupdf
    from PIL import Image
    from io import BytesIO

    doc = pymupdf.open(str(pdf))
    eng = None
    pages = []
    ocr_budget = ocr_cap
    for i in range(doc.page_count):
        page = doc[i]
        t0 = time.time()
        raw = page.get_text("text") or ""
        method = "text"
        ocr = False
        if ocr_low and ocr_budget > 0 and nchars(raw) < 15:
            try:
                if eng is None:
                    from rapidocr_onnxruntime import RapidOCR
                    eng = RapidOCR()
                pix = page.get_pixmap(matrix=pymupdf.Matrix(1.5, 1.5))
                arr = np.array(Image.open(BytesIO(pix.tobytes("png"))).convert("RGB"))
                result, _ = eng(arr)
                ocr_txt = "\n".join(str(t[1]) for t in (result or []))
                if nchars(ocr_txt) > nchars(raw):
                    raw, method, ocr = ocr_txt, "rapidocr", True
                    ocr_budget -= 1
            except Exception:
                method = "text+ocr_err"
        pages.append({
            "p": i + 1,
            "chars": nchars(raw),
            "cjk": len(CJK.findall(raw)),
            "method": method,
            "ocr": ocr,
            "ms": int((time.time() - t0) * 1000),
            "text": raw,
        })
        if (i + 1) % 100 == 0:
            print(f"    indep p{i+1}/{doc.page_count}", flush=True)
    n = doc.page_count
    doc.close()
    return pages, n


# ── 项目抽取：分块 mixed（每块 150 页）──
def project_full(pdf: Path, chunk=120, save_path=None):
    from ocr_pipeline_umi import extract_document
    import pymupdf
    t0 = time.time()
    doc = pymupdf.open(str(pdf))
    n = doc.page_count
    doc.close()
    pages = []
    if save_path and Path(save_path).exists():
        pages = json.loads(Path(save_path).read_text(encoding="utf-8"))
        print(f"    proj resume {len(pages)}", flush=True)
    have = {p["p"] for p in pages}
    for start in range(1, n + 1, chunk):
        end = min(n, start + chunk - 1)
        todo = [p for p in range(start, end + 1) if p not in have]
        if not todo:
            continue
        print(f"    proj p{start}-{end}", flush=True)
        part = extract_document(str(pdf), mode="mixed", page_list=todo)
        for p in part.pages:
            pages.append({
                "p": p.page_no,
                "chars": p.chars,
                "text": p.text or "",
                "ocr": p.ocr_used,
                "ocr_chars": p.ocr_chars,
                "text_chars": p.text_chars,
                "blocks": p.blocks,
            })
        if save_path:
            Path(save_path).write_text(json.dumps(pages, ensure_ascii=False), encoding="utf-8")
    total = sum(p["chars"] for p in pages)
    return pages, total, time.time() - t0


# ── 字符级对比 ──
def char_diff(ta: str, tb: str):
    ca, cb = Counter(WORD.findall(ta or "")), Counter(WORD.findall(tb or ""))
    only_a = sum((ca - cb).values())
    only_b = sum((cb - ca).values())
    total = max(sum(ca.values()), sum(cb.values()), 1)
    cer = (only_a + only_b) / (2 * total)  # 对称字符差异率
    return only_a, only_b, round(cer, 4)


def paras(s):
    return [p for p in re.split(r"\n{2,}|===== PAGE \d+ =====", s or "") if p.strip()]


def main():
    only = sys.argv[1] if len(sys.argv) > 1 else None
    pdfs = sorted(PDF_DIR.glob("*.pdf")) + sorted(PDF_DIR.glob("*.PDF"))
    seen, uniq = set(), []
    for p in pdfs:
        if p.name not in seen:
            seen.add(p.name)
            uniq.append(p)
    if only:
        uniq = [p for p in uniq if only in p.name]
    st = load_state()
    report = []
    for pdf in uniq:
        key = pdf.name
        if key in st["done"]:
            print(f"[skip] {key[:40]}", flush=True)
            continue
        print(f"[full] {key[:40]} …", flush=True)
        t0 = time.time()
        safe = re.sub(r'[\\/:*?"<>|]', "_", Path(key).stem)[:50]
        a_path = OUT / f"{safe}.A.json"
        b_path = OUT / f"{safe}.B.json"
        if a_path.exists():
            a_pages = json.loads(a_path.read_text(encoding="utf-8"))
            print(f"    reuse A pages={len(a_pages)}", flush=True)
        else:
            a_pages, n = independent_full(pdf, ocr_low=True)
            a_path.write_text(json.dumps(a_pages, ensure_ascii=False), encoding="utf-8")
            print(f"    saved A pages={len(a_pages)}", flush=True)
        if b_path.exists():
            b_pages = json.loads(b_path.read_text(encoding="utf-8"))
            print(f"    reuse B pages={len(b_pages)}", flush=True)
        else:
            b_pages, b_total, b_sec = project_full(pdf, chunk=120, save_path=b_path)
            b_path.write_text(json.dumps(b_pages, ensure_ascii=False), encoding="utf-8")
            print(f"    saved B pages={len(b_pages)}", flush=True)
        n = max(n if 'n' in dir() else 0, len(a_pages), len(b_pages))
        bmap = {p["p"]: p for p in b_pages}
        page_rows = []
        sum_a = sum_b = 0
        sum_oa = sum_ob = 0
        worst = []
        for pa in a_pages:
            pb = bmap.get(pa["p"], {"p": pa["p"], "text": "", "chars": 0})
            oa, ob, cer = char_diff(pa["text"], pb.get("text") or "")
            sum_a += pa["chars"]
            sum_b += pb.get("chars") or 0
            sum_oa += oa
            sum_ob += ob
            row = {"p": pa["p"], "a": pa["chars"], "b": pb.get("chars") or 0,
                   "oa": oa, "ob": ob, "cer": cer,
                   "ma": pa["method"], "ocr_b": bool(pb.get("ocr"))}
            page_rows.append(row)
            worst.append(row)
        worst.sort(key=lambda r: -r["cer"])
        # 段落数
        a_paras = sum(len(paras(p["text"])) for p in a_pages)
        b_paras = sum(len(paras((bmap.get(p["p"]) or {}).get("text") or "")) for p in a_pages)
        rec = {
            "pdf": key,
            "pages": n,
            "chars_a": sum_a,
            "chars_b": sum_b,
            "delta": sum_b - sum_a,
            "only_a": sum_oa,
            "only_b": sum_ob,
            "avg_cer": round(sum(r["cer"] for r in page_rows) / max(1, len(page_rows)), 4),
            "paras_a": a_paras,
            "paras_b": b_paras,
            "ms_a": sum(p["ms"] for p in a_pages),
            "ms_b": int(b_sec * 1000),
            "ocr_pages_a": sum(1 for p in a_pages if p["ocr"]),
            "ocr_pages_b": sum(1 for p in b_pages if p.get("ocr")),
            "worst10": worst[:10],
            "elapsed_s": round(time.time() - t0, 1),
        }
        report.append(rec)
        st["done"][key] = rec
        save_state(st)
        safe = re.sub(r'[\\/:*?"<>|]', "_", Path(key).stem)[:50]
        (OUT / f"{safe}.pages.json").write_text(
            json.dumps({"meta": rec, "pages": page_rows}, ensure_ascii=False, indent=1),
            encoding="utf-8")
        print(f"  A={sum_a} B={sum_b} cer={rec['avg_cer']} paras={a_paras}/{b_paras} t={rec['elapsed_s']}s", flush=True)
        (OUT / "full_report.json").write_text(
            json.dumps(list(st["done"].values()), ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"DONE books={len(st['done'])}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
