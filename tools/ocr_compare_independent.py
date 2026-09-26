# -*- coding: utf-8 -*-
"""
独立抽取 vs 项目 OCR 对比
- A 独立：PyMuPDF raw text / pypdf / RapidOCR 直调（不经 Umi-OCR TBPU）
- B 项目：ocr_pipeline_umi.extract_document（Umi 排版 + 同引擎）
输出：ocr-compare/ 逐页差异 + 汇总
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
OUT = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\ocr-compare")
OUT.mkdir(parents=True, exist_ok=True)

WORD = re.compile(r"[A-Za-z0-9一-鿿]")
CJK = re.compile(r"[一-鿿]")


def readable(s: str) -> int:
    return len(WORD.findall(s or ""))


def norm(s: str) -> str:
    s = re.sub(r"===== PAGE \d+ =====", "\n", s or "")
    s = re.sub(r"\s+", " ", s).strip()
    return s


# ───────────────────────── A. 独立抽取 ─────────────────────────
def independent_extract_pdf(path: Path, max_ocr_pages: int = 8):
    """PyMuPDF 文本层 + 低文本页 RapidOCR（独立调用，无 TBPU）。"""
    import pymupdf
    from PIL import Image
    from io import BytesIO

    doc = pymupdf.open(str(path))
    pages = []
    ocr_engine = None
    for i in range(doc.page_count):
        page = doc[i]
        t0 = time.time()
        raw = page.get_text("text") or ""
        method = "pymupdf-text"
        ocr_used = False
        if readable(raw) < 40 and max_ocr_pages > 0:
            # 独立 RapidOCR
            if ocr_engine is None:
                from rapidocr_onnxruntime import RapidOCR
                ocr_engine = RapidOCR()
            try:
                import numpy as np
                pix = page.get_pixmap(matrix=pymupdf.Matrix(1.5, 1.5))
                img = Image.open(BytesIO(pix.tobytes("png"))).convert("RGB")
                arr = np.array(img)
                result, _ = ocr_engine(arr)
                parts = []
                if result:
                    for box, text, score in result:
                        parts.append(str(text))
                ocr_txt = "\n".join(parts)
                if readable(ocr_txt) > readable(raw):
                    raw = ocr_txt
                    method = "rapidocr"
                    ocr_used = True
                    max_ocr_pages -= 1
            except Exception as e:
                method = "pymupdf-text+ocr-fail:" + type(e).__name__
        pages.append({
            "page": i + 1,
            "chars": readable(raw),
            "cjk": len(CJK.findall(raw)),
            "method": method,
            "ocr": ocr_used,
            "ms": int((time.time() - t0) * 1000),
            "text": raw[:4000],
        })
    doc.close()
    return pages


def independent_pypdf_chars(path: Path) -> dict:
    try:
        from pypdf import PdfReader
        r = PdfReader(str(path))
        n = 0
        for p in r.pages[:20]:
            try:
                n += readable(p.extract_text() or "")
            except Exception:
                pass
        return {"pages": len(r.pages), "chars_first20": n, "ok": True}
    except Exception as e:
        return {"ok": False, "err": str(e)[:80]}


# ───────────────────────── B. 项目 OCR ─────────────────────────
def project_extract(path: Path, max_pages=None):
    from ocr_pipeline_umi import extract_document
    doc = extract_document(str(path), mode="mixed", max_pages=max_pages)
    pages = [{"page": p.page_no, "chars": p.chars, "text": (p.text or "")[:4000],
              "ocr": p.ocr_used, "ocr_chars": p.ocr_chars, "text_chars": p.text_chars}
             for p in doc.pages]
    return pages, doc.total_chars


# ───────────────────────── 对比 ─────────────────────────
def jaccard(a: str, b: str) -> float:
    sa, sb = set(norm(a).split()), set(norm(b).split())
    if not sa and not sb:
        return 1.0
    if not sa or not sb:
        return 0.0
    return len(sa & sb) / max(1, len(sa | sb))


def page_diff(pa: dict, pb: dict) -> dict:
    ta, tb = pa.get("text") or "", pb.get("text") or ""
    ca, cb = readable(ta), readable(tb)
    jac = jaccard(ta, tb)
    # 字符多重集差
    ca_c, cb_c = Counter(norm(ta)), Counter(norm(tb))
    only_a = sum((ca_c - cb_c).values())
    only_b = sum((cb_c - ca_c).values())
    return {
        "page": pa["page"],
        "chars_a": ca, "chars_b": cb,
        "delta": cb - ca,
        "jaccard": round(jac, 3),
        "only_independent": only_a,
        "only_project": only_b,
        "method_a": pa.get("method"),
        "ocr_b": pb.get("ocr"),
    }


def main():
    pdfs = sorted(PDF_DIR.glob("*.pdf")) + sorted(PDF_DIR.glob("*.PDF"))
    seen = set()
    pdfs = [p for p in pdfs if p.name not in seen and not seen.add(p.name)]
    report = []
    for i, pdf in enumerate(pdfs, 1):
        print(f"[{i}/{len(pdfs)}] {pdf.name[:40]} …", flush=True)
        t0 = time.time()
        # A 独立：全页文本层 + 少量 OCR
        a_pages = independent_extract_pdf(pdf, max_ocr_pages=6)
        pypdf_meta = independent_pypdf_chars(pdf)
        # B 项目：全量 mixed
        b_pages, b_total = project_extract(pdf, max_pages=None)
        bmap = {p["page"]: p for p in b_pages}
        diffs = []
        for pa in a_pages:
            pb = bmap.get(pa["page"], {"page": pa["page"], "text": "", "chars": 0})
            diffs.append(page_diff(pa, pb))
        avg_j = sum(d["jaccard"] for d in diffs) / max(1, len(diffs))
        char_a = sum(p["chars"] for p in a_pages)
        char_b = sum(p["chars"] for p in b_pages)
        rec = {
            "pdf": pdf.name,
            "pages": len(a_pages),
            "chars_independent": char_a,
            "chars_project": char_b,
            "delta_chars": char_b - char_a,
            "avg_jaccard": round(avg_j, 3),
            "pypdf": pypdf_meta,
            "ocr_pages_independent": sum(1 for p in a_pages if p.get("ocr")),
            "ocr_pages_project": sum(1 for p in b_pages if p.get("ocr")),
            "worst_pages": sorted(diffs, key=lambda d: d["jaccard"])[:8],
            "elapsed_s": round(time.time() - t0, 1),
        }
        report.append(rec)
        safe = re.sub(r'[\\/:*?"<>|]', "_", pdf.stem)[:60]
        (OUT / f"{safe}.diff.json").write_text(json.dumps({"meta": rec, "pages": diffs}, ensure_ascii=False, indent=1), encoding="utf-8")
        print(f"  A={char_a} B={char_b} d={char_b-char_a} jaccard={avg_j:.3f} pages={len(a_pages)}", flush=True)
        (OUT / "compare_report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    ok = sum(1 for r in report if r["pages"] > 0)
    print(f"DONE {ok}/{len(pdfs)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
