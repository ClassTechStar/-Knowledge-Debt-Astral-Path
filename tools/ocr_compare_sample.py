# -*- coding: utf-8 -*-
"""独立 vs 项目 OCR 分层抽样对比（每本约 24 页，12 本可跑完）。"""
from __future__ import annotations

import json
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


def readable(s):
    return len(WORD.findall(s or ""))


def norm(s):
    s = re.sub(r"===== PAGE \d+ =====", "\n", s or "")
    return re.sub(r"\s+", " ", s).strip()


def sample_pages(n, k=8):
    if n <= k * 2:
        return list(range(n))
    idx = set()
    for i in range(k):
        idx.add(int(i * (n - 1) / max(1, k - 1)))
    idx.add(0)
    idx.add(n - 1)
    return sorted(idx)


def independent_page(page):
    import numpy as np
    import pymupdf
    from PIL import Image
    from io import BytesIO
    t0 = time.time()
    raw = page.get_text("text") or ""
    method = "pymupdf-text"
    if readable(raw) < 40:
        try:
            from rapidocr_onnxruntime import RapidOCR
            eng = getattr(independent_page, "_eng", None)
            if eng is None:
                eng = RapidOCR()
                independent_page._eng = eng
            pix = page.get_pixmap(matrix=pymupdf.Matrix(1.25, 1.25))
            arr = np.array(Image.open(BytesIO(pix.tobytes("png"))).convert("RGB"))
            result, _ = eng(arr)
            ocr = "\n".join(str(t[1]) for t in (result or []))
            if readable(ocr) > readable(raw):
                raw, method = ocr, "rapidocr"
        except Exception as e:
            method = "text+ocr-err:" + type(e).__name__
    return {"chars": readable(raw), "cjk": len(CJK.findall(raw)), "method": method, "ms": int((time.time()-t0)*1000), "text": raw}


def project_pages(pdf: Path, idxs):
    from ocr_pipeline_umi import extract_document
    doc = extract_document(str(pdf), mode="mixed", page_list=[i + 1 for i in idxs])
    return {p.page_no: p for p in doc.pages}


def jaccard(a, b):
    sa, sb = set(norm(a).split()), set(norm(b).split())
    if not sa and not sb:
        return 1.0
    if not sa or not sb:
        return 0.0
    return len(sa & sb) / max(1, len(sa | sb))


def main():
    pdfs = sorted(PDF_DIR.glob("*.pdf")) + sorted(PDF_DIR.glob("*.PDF"))
    seen, uniq = set(), []
    for p in pdfs:
        if p.name not in seen:
            seen.add(p.name)
            uniq.append(p)
    report = []
    for i, pdf in enumerate(uniq, 1):
        import pymupdf
        doc = pymupdf.open(str(pdf))
        n = doc.page_count
        idxs = sample_pages(n, 8)
        print(f"[{i}/{len(uniq)}] {pdf.name[:36]} pages={n} sample={len(idxs)}", flush=True)
        t0 = time.time()
        a_pages = {}
        for ix in idxs:
            a_pages[ix + 1] = independent_page(doc[ix])
        doc.close()
        bmap = project_pages(pdf, idxs)
        diffs = []
        for pno in sorted(a_pages):
            pa, pb = a_pages[pno], bmap.get(pno)
            ta = pa["text"]
            tb = (pb.text if pb else "") or ""
            ca, cb = readable(ta), readable(tb)
            diffs.append({
                "page": pno,
                "chars_a": ca, "chars_b": cb, "delta": cb - ca,
                "jaccard": round(jaccard(ta, tb), 3),
                "method_a": pa["method"],
                "ocr_b": bool(pb.ocr_used) if pb else False,
                "only_a": sum((Counter(norm(ta)) - Counter(norm(tb))).values()),
                "only_b": sum((Counter(norm(tb)) - Counter(norm(ta))).values()),
            })
        avg_j = sum(d["jaccard"] for d in diffs) / max(1, len(diffs))
        rec = {
            "pdf": pdf.name,
            "total_pages": n,
            "sample_pages": len(idxs),
            "chars_a": sum(d["chars_a"] for d in diffs),
            "chars_b": sum(d["chars_b"] for d in diffs),
            "delta": sum(d["delta"] for d in diffs),
            "avg_jaccard": round(avg_j, 3),
            "low_j_pages": [d for d in diffs if d["jaccard"] < 0.75][:6],
            "worst": sorted(diffs, key=lambda d: d["jaccard"])[:5],
            "elapsed_s": round(time.time() - t0, 1),
        }
        report.append(rec)
        safe = re.sub(r'[\\/:*?"<>|]', "_", pdf.stem)[:50]
        (OUT / f"{safe}.sample.json").write_text(json.dumps({"meta": rec, "pages": diffs}, ensure_ascii=False, indent=1), encoding="utf-8")
        print(f"  A={rec['chars_a']} B={rec['chars_b']} j={avg_j:.3f}", flush=True)
        (OUT / "compare_report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"DONE {len(report)}/{len(uniq)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
