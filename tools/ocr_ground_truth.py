# -*- coding: utf-8 -*-
"""
P2 ground truth 脚手架：每书导出 20 页抽样文本，供人工校对成基线。
输出：ocr-ground-truth/<书>/<page>.txt  +  manifest.json
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ocr_pipeline_umi import extract_document  # noqa: E402

PDF_DIR = Path(r"C:\Users\18948\Downloads\PDF TEST")
OUT = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\ocr-ground-truth")


def sample_pages(n, k=20):
    if n <= k:
        return list(range(1, n + 1))
    idx = {1, n}
    for i in range(k - 2):
        idx.add(int(1 + i * (n - 1) / max(1, k - 3)))
    return sorted(idx)[:k]


def main():
    pdfs = sorted(PDF_DIR.glob("*.pdf")) + sorted(PDF_DIR.glob("*.PDF"))
    seen, uniq = set(), []
    for p in pdfs:
        if p.name not in seen:
            seen.add(p.name)
            uniq.append(p)
    manifest = []
    for pdf in uniq:
        import pymupdf
        doc = pymupdf.open(str(pdf))
        n = doc.page_count
        doc.close()
        pages = sample_pages(n)
        safe = re.sub(r'[\\/:*?"<>|\s]+', "_", pdf.stem)[:40].strip("_")
        book_dir = OUT / safe
        book_dir.mkdir(parents=True, exist_ok=True)
        part = extract_document(str(pdf), mode="mixed", page_list=pages)
        for p in part.pages:
            (book_dir / f"p{p.page_no:04d}.txt").write_text(p.text or "", encoding="utf-8")
        manifest.append({"pdf": pdf.name, "pages": pages, "dir": safe})
        (OUT / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"[gt] {pdf.name[:30]} pages={pages}", flush=True)
    print(f"DONE books={len(manifest)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
