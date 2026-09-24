# -*- coding: utf-8 -*-
"""13 本 PDF 深度测试：逐文件抽取全文并校验可读字符。"""
from __future__ import annotations

import json
import os
import re
import sys
import time
import traceback
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ocr_pipeline_umi import (  # noqa: E402
    detect_needs_ocr,
    extract_document,
    extract_page,
)

PDF_DIR = Path(r"C:\Users\18948\Downloads")
OUT_DIR = Path(r"C:\Users\18948\XiaomiMiMoProjects\.mimo-sessions\2026-09-19\按照项目方案要求，对整个项目进行完整开发。开发过程中需持续推进，不得中途停顿，直\ocr-deep-test")
OUT_DIR.mkdir(parents=True, exist_ok=True)

PDFS = [
    "Kotlin编程实践：Kotlin从入门到实战.pdf",
    "Go语言从入门到精通.pdf",
    "大模型应用开发：动手做 AI Agent (黄佳) .pdf",
    "深度学习入门2：自制框架 (斋藤康毅)-扫描版 (1).PDF",
    "图灵程序设计丛书--深度学习入门4：强化学习 ([日] 斋藤康毅) (1).pdf",
    "C#从入门到精通（第7版）+(明日科技)+.pdf",
    "深度学习进阶：自然语言处理 (斋藤康毅) .pdf",
    "黄仁勋：英伟达之芯_【美】斯蒂芬·威特.pdf",
    "Java从入门到精通（第6版） (明日科技) .pdf",
    "DeepLearning-Goodfellow-花书.pdf",
    "深度学习 Deep Learning [花书] (Ian Goodfellow,Yoshua Bengio,Aaron Courville) .pdf",
    "Python编程：从入门到实践（第3版）.pdf",
    "深度学习入门：基于Python的理论与实现+(斋藤康毅)+.pdf",
]

CJK = re.compile(r"[一-鿿]")
WORD = re.compile(r"[A-Za-z0-9一-鿿]")


def readable_chars(s: str) -> int:
    return len(WORD.findall(s or ""))


def run_one(name: str, mode: str, max_pages=None) -> dict:
    path = PDF_DIR / name
    t0 = time.time()
    rec = {
        "name": name,
        "path": str(path),
        "exists": path.exists(),
        "size_mb": round(path.stat().st_size / 1024 / 1024, 2) if path.exists() else 0,
    }
    if not path.exists():
        rec["error"] = "missing"
        return rec
    try:
        det = detect_needs_ocr(str(path))
        rec.update(det)
        rec["mode"] = mode
        doc = extract_document(str(path), mode=mode, max_pages=max_pages)
        rec["page_count"] = doc.page_count
        rec["extracted_pages"] = len(doc.pages)
        rec["total_chars"] = doc.total_chars
        rec["empty_pages"] = doc.empty_pages[:50]
        rec["empty_page_count"] = len(doc.empty_pages)
        rec["ocr_pages"] = len(doc.ocr_pages)
        rec["avg_chars_per_page"] = round(doc.total_chars / max(len(doc.pages), 1), 1)
        rec["has_cjk"] = bool(CJK.search(doc.text))
        rec["elapsed_s"] = round(time.time() - t0, 1)
        # 保存全文
        safe = re.sub(r'[\\/:*?"<>|]', "_", name)[:80]
        out_txt = OUT_DIR / f"{safe}.txt"
        out_txt.write_text(doc.text, encoding="utf-8")
        rec["txt"] = str(out_txt)
        rec["txt_bytes"] = out_txt.stat().st_size
        # 抽样页明细
        rec["sample_pages"] = [
            {
                "page": p.page_no,
                "chars": p.chars,
                "blocks": p.blocks,
                "ocr": p.ocr_used,
                "ocr_chars": p.ocr_chars,
                "text_chars": p.text_chars,
                "head": (p.text or "")[:80].replace("\n", " "),
            }
            for p in doc.pages[:3] + doc.pages[len(doc.pages) // 2 : len(doc.pages) // 2 + 2]
        ]
        rec["ok"] = doc.total_chars > 50
    except Exception as e:
        rec["error"] = f"{type(e).__name__}: {e}"
        rec["trace"] = traceback.format_exc()[-500:]
        rec["ok"] = False
    return rec


def main():
    which = sys.argv[1] if len(sys.argv) > 1 else "all"
    mode = sys.argv[2] if len(sys.argv) > 2 else "mixed"
    max_pages = int(sys.argv[3]) if len(sys.argv) > 3 else None
    names = PDFS if which == "all" else [which]
    report = []
    for i, name in enumerate(names, 1):
        print(f"[{i}/{len(names)}] {name} mode={mode} max_pages={max_pages}", flush=True)
        rec = run_one(name, mode=mode, max_pages=max_pages)
        report.append(rec)
        print(
            f"  -> pages={rec.get('page_count')} chars={rec.get('total_chars')} "
            f"avg={rec.get('avg_chars_per_page')} scanned={rec.get('likely_scanned')} "
            f"ok={rec.get('ok')} err={rec.get('error')}",
            flush=True,
        )
        # 增量落盘
        (OUT_DIR / "report.json").write_text(
            json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
        )
    ok_n = sum(1 for r in report if r.get("ok"))
    print(f"DONE ok={ok_n}/{len(report)}")
    return 0 if ok_n == len(report) else 1


if __name__ == "__main__":
    raise SystemExit(main())
