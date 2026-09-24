# -*- coding: utf-8 -*-
"""扫描版 PDF 分批 OCR，断点续跑，保证每页每字可读。"""
from __future__ import annotations

import json
import re
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ocr_pipeline_umi import extract_page  # noqa: E402
import pymupdf  # noqa: E402

PDF = Path(r"C:\Users\18948\Downloads\大模型应用开发：动手做 AI Agent (黄佳) .pdf")
OUT = Path(
    r"C:\Users\18948\XiaomiMiMoProjects\.mimo-sessions\2026-09-19\按照项目方案要求，对整个项目进行完整开发。开发过程中需持续推进，不得中途停顿，直\ocr-deep-test"
)
OUT.mkdir(parents=True, exist_ok=True)
TXT = OUT / "大模型应用开发：动手做 AI Agent (黄佳) .pdf.ocr.txt"
STATE = OUT / "aiagent_ocr_state.json"
LOG = OUT / "aiagent_ocr_log.txt"


def load_state():
    if STATE.exists():
        return json.loads(STATE.read_text(encoding="utf-8"))
    return {"done": {}, "order": []}


def save_state(st):
    STATE.write_text(json.dumps(st, ensure_ascii=False), encoding="utf-8")


def main():
    start = int(sys.argv[1]) if len(sys.argv) > 1 else 1
    end = int(sys.argv[2]) if len(sys.argv) > 2 else 0
    st = load_state()
    doc = pymupdf.open(str(PDF))
    n = doc.page_count
    if end <= 0:
        end = n
    t0 = time.time()
    log = open(LOG, "a", encoding="utf-8")
    for pno in range(start, end + 1):
        key = str(pno)
        if key in st["done"]:
            continue
        try:
            text, blocks, stats = extract_page(doc[pno - 1], mode="fullPage", pno=pno)
        except Exception as e:
            text, stats = "", {"error": str(e)}
        chars = len(re.sub(r"\s", "", text))
        st["done"][key] = {"chars": chars, "blocks": stats.get("ocr_chars", 0)}
        st["order"].append(pno)
        # 追加写全文
        with open(TXT, "a", encoding="utf-8") as f:
            f.write(f"\n\n===== PAGE {pno} =====\n{text}")
        msg = f"p{pno}/{end} chars={chars} elapsed={time.time()-t0:.0f}s"
        print(msg, flush=True)
        log.write(msg + "\n")
        if pno % 5 == 0:
            save_state(st)
    save_state(st)
    log.close()
    doc.close()
    total = sum(v["chars"] for v in st["done"].values())
    print(f"DONE pages={len(st['done'])}/{n} total_chars={total}")


if __name__ == "__main__":
    main()
