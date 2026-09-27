# -*- coding: utf-8 -*-
"""用新 CER 口径（以 B 为底 + 分区）重算 3 本样本页。"""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ocr_eval import summarize_page, section_cer  # noqa: E402
from ocr_pipeline_umi import extract_document  # noqa: E402

PDF_DIR = Path(r"C:\Users\18948\Downloads\PDF TEST")
OUT = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\ocr-full-compare")

BOOKS = [
    ("Kotlin编程实践：Kotlin从入门到实战.pdf", [20, 80, 150]),
    ("大模型应用开发：动手做 AI Agent (黄佳) .pdf", [10, 50, 100]),
    ("Java从入门到精通（第6版） (明日科技) .pdf", [40, 200, 400]),
]


def load_pages(stem_prefix, which):
    f = next(OUT.glob(stem_prefix + f"*.{which}.json"), None)
    if not f:
        f = next(OUT.glob(stem_prefix + "*.A.json" if which == "A" else stem_prefix + "*.B.json"), None)
    if not f:
        return {}
    data = json.loads(f.read_text(encoding="utf-8"))
    return {p["p"]: p.get("text") or "" for p in data}


def main():
    report = []
    for name, pages in BOOKS:
        stem = Path(name).stem[:20]
        A = load_pages(stem, "A")
        B = load_pages(stem, "B")
        pairs = []
        for p in pages:
            ta = A.get(p) or ""
            tb = B.get(p) or ""
            if not ta and not tb:
                # 重抽
                import pymupdf
                doc = pymupdf.open(str(PDF_DIR / name))
                ta = doc[p - 1].get_text("text") or ""
                doc.close()
                part = extract_document(str(PDF_DIR / name), mode="mixed", page_list=[p])
                tb = part.pages[0].text if part.pages else ""
            pairs.append((ta, tb))
        s = summarize_page(pairs)
        s["book"] = name
        s["pages"] = pages
        report.append(s)
        print(f"{name[:24]:24s} recall={s['recall_b']:.3f} prec={s['precision_b']:.3f} f1={s['f1']:.3f} miss={s['miss']} extra={s['extra']}")
        print(f"   sections body/cap/math miss={s['sections']['body']['miss']}/{s['sections']['caption']['miss']}/{s['sections']['math']['miss']}")
    out = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\ocr-full-compare\eval_b_based.json")
    out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print("saved", out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
