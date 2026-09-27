# -*- coding: utf-8 -*-
"""项目自带 OCR（生产 CLI 路径）批量基准：12 本 PDF 逐本调用 ocr_pipeline.py。

与 App 生产路径一致：
  python tools/ocr_pipeline.py <pdf> --ocr standard --out <tmp>
记录每本耗时 + payload（fullText/extractedChars/notes/ocrPages），存 ocr-bench/project/{slug}.json。

用法（系统 python，需 pypdfium2 + PIL）：
  python tools/run_project_ocr_bench.py [--mode standard] [--books 子串]
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PDF_DIR = Path(r"C:\Users\18948\Downloads\PDF TEST")
OUT = ROOT / "ocr-bench" / "project"
OUT.mkdir(parents=True, exist_ok=True)

BOOKS = [
    "Kotlin编程实践：Kotlin从入门到实战.pdf",
    "Go语言从入门到精通.pdf",
    "大模型应用开发：动手做 AI Agent (黄佳) .pdf",
    "深度学习入门2：自制框架 (斋藤康毅)-扫描版 (1).PDF",
    "图灵程序设计丛书--深度学习入门4：强化学习 ([日] 斋藤康毅) (1).pdf",
    "C#从入门到精通（第7版）+(明日科技)+.pdf",
    "深度学习进阶：自然语言处理 (斋藤康毅) .pdf",
    "黄仁勋：英伟达之芯_【美】斯蒂芬·威特.pdf",
    "Java从入门到精通（第6版） (明日科技) .pdf",
    "深度学习 Deep Learning [花书] (Ian Goodfellow,Yoshua Bengio,Aaron Courville) .pdf",
    "Python编程：从入门到实践（第3版）.pdf",
    "深度学习入门：基于Python的理论与实现+(斋藤康毅)+.pdf",
]


def slug(name: str) -> str:
    import re

    stem = Path(name).stem
    return re.sub(r"[\\/:*?\"<>|\s（）()【】\[\]·：:,，]+", "_", stem).strip("_")[:60]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--mode", default="standard", choices=["none", "quick", "standard", "tesseract-only"])
    ap.add_argument("--books", default=None)
    ap.add_argument("--python", default=sys.executable)
    args = ap.parse_args()
    books = [b for b in BOOKS if not args.books or args.books in b]
    summary = {}
    for name in books:
        out_path = OUT / f"{slug(name)}.json"
        t0 = time.time()
        try:
            proc = subprocess.run(
                [args.python, str(ROOT / "tools" / "ocr_pipeline.py"),
                 str(PDF_DIR / name), "--ocr", args.mode, "--out", str(out_path) + ".tmp"],
                capture_output=True, timeout=3600,
            )
            elapsed = round(time.time() - t0, 1)
            if proc.returncode != 0:
                summary[name] = {"ok": False, "elapsed_s": elapsed,
                                 "stderr": proc.stderr.decode("utf-8", "ignore")[-400:]}
                print(f"[fail] {name[:30]} rc={proc.returncode} {elapsed}s", flush=True)
                continue
            payload = json.loads((Path(str(out_path) + ".tmp")).read_text(encoding="utf-8"))
            payload["elapsed_s"] = elapsed
            payload["ocr_mode"] = args.mode
            payload["python"] = args.python
            out_path.write_text(json.dumps(payload, ensure_ascii=False, indent=1), encoding="utf-8")
            Path(str(out_path) + ".tmp").unlink(missing_ok=True)
            summary[name] = {"ok": payload.get("ok"), "elapsed_s": elapsed,
                             "chars": payload.get("extractedChars"), "mode": payload.get("mode"),
                             "ocrPages": (payload.get("detail") or {}).get("ocrPages"),
                             "notes": (payload.get("notes") or [])[:6]}
            print(f"[done] {name[:30]} {elapsed}s chars={payload.get('extractedChars')} mode={payload.get('mode')}", flush=True)
        except Exception as e:
            summary[name] = {"ok": False, "error": f"{type(e).__name__}: {e}", "elapsed_s": round(time.time() - t0, 1)}
            print(f"[err] {name[:30]} {e}", flush=True)
    (OUT / "_summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=1), encoding="utf-8")
    print("ALL DONE", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
