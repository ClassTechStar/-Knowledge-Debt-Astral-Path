# -*- coding: utf-8 -*-
"""
12 本 PDF 全量抽取（文本层 GT / 引擎全页 OCR），多进程并行，断点续跑。

模式：
  --gt-only            只渲染 + 抽文本层（GT 基线），不跑 OCR
  --engine rapid3|rapid|paddle   全页 OCR（每页记录 ms/文本/错误）

输出：
  ocr-bench/textlayer/{slug}.jsonl        （GT：每页 page/text/chars）
  ocr-bench/full/{engine}/{slug}.jsonl    （OCR：每页 page/ms/chars/text/error）
  ocr-bench/full/{engine}/_summary.json   （按书 wall/chars/success 统计）

断点续跑：重跑时跳过 JSONL 中已有页；统计只对本次完整跑的书记 wall_s。
用法（paddle-venv）：
  python ocr_full_extract.py --gt-only
  python ocr_full_extract.py --engine rapid3 --workers 12 [--books 子串过滤]
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import threading
import time
from concurrent.futures import ProcessPoolExecutor, as_completed
from io import BytesIO
from pathlib import Path

PDF_DIR = Path(r"C:\Users\18948\Downloads\PDF TEST")
OUT = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\ocr-bench")
WORD = re.compile(r"[A-Za-z0-9一-鿿]")
ZOOM = 1.5

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
    stem = Path(name).stem
    return re.sub(r"[\\/:*?\"<>|\s（）()【】\[\]·：:,，]+", "_", stem).strip("_")[:60]


# ── OCR worker（与 bench_ocr_engines.py 同一套初始化） ──
_ENGINE = None
_ENGINE_NAME = None


def _pin_ort_threads(intra: int):
    import onnxruntime as ort

    if getattr(ort, "_astralpath_pinned", False):
        return
    orig = ort.InferenceSession

    def patched(path, *args, **kwargs):
        so = kwargs.get("sess_options")
        if so is None and args and isinstance(args[0], ort.SessionOptions):
            so = args[0]
        if so is None:
            so = ort.SessionOptions()
            kwargs["sess_options"] = so
        try:
            so.intra_op_num_threads = intra
            so.inter_op_num_threads = 1
        except Exception:
            pass
        return orig(path, *args, **kwargs)

    ort.InferenceSession = patched
    ort._astralpath_pinned = True


def _init_engine(name, intra=2):
    global _ENGINE, _ENGINE_NAME
    _ENGINE_NAME = name
    os.environ.setdefault("OMP_NUM_THREADS", str(intra))
    if name in ("rapid", "rapid3"):
        _pin_ort_threads(intra)
        if name == "rapid":
            from rapidocr_onnxruntime import RapidOCR
            _ENGINE = RapidOCR()
        else:
            from rapidocr import RapidOCR
            _ENGINE = RapidOCR()
    elif name == "paddle":
        from paddleocr import PaddleOCR
        _ENGINE = PaddleOCR(
            use_textline_orientation=False,
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
            lang="ch",
            enable_mkldnn=False,
            cpu_threads=intra,
        )
    else:
        raise ValueError(name)


def _ocr_job(args):
    engine, book, page, png_bytes = args
    global _ENGINE
    if _ENGINE is None:
        _init_engine(engine, int(os.environ.get("OCR_INTRA", "2")))
    import numpy as np
    from PIL import Image

    img = Image.open(BytesIO(png_bytes)).convert("RGB")
    arr = np.array(img)
    t0 = time.time()
    try:
        if engine in ("rapid", "rapid3"):
            out = _ENGINE(arr)
            if hasattr(out, "txts"):
                text = "\n".join(str(t) for t in (out.txts or []))
            elif isinstance(out, tuple) and out:
                res = out[0] or []
                text = "\n".join(str(it[1]) if isinstance(it, (list, tuple)) and len(it) >= 2 else str(it) for it in res)
            else:
                res = out or []
                text = "\n".join(str(it[1]) if isinstance(it, (list, tuple)) and len(it) >= 2 else str(it) for it in res)
            err = None
        else:
            result = list(_ENGINE.predict(arr))
            if result and hasattr(result[0], "get"):
                text = "\n".join(str(t) for t in (result[0].get("rec_texts") or []))
            else:
                text = str(result[0]) if result else ""
            err = None
    except Exception as e:
        text, err = "", f"{type(e).__name__}: {str(e)[:150]}"
    ms = int((time.time() - t0) * 1000)
    return {
        "book": book,
        "page": page,
        "ms": ms,
        "chars": len(WORD.findall(text)),
        "cjk": len(re.findall(r"[一-鿿]", text)),
        "error": err,
        "text": text,
    }


# ── 渲染（父进程内小线程池） ──
def _render_book_pages(pdf_name, engine, with_text):
    """生成器：逐页产出 (book, page, png_bytes, text_layer, text_layer_chars)。"""
    import pymupdf
    from PIL import Image

    path = PDF_DIR / pdf_name
    doc = pymupdf.open(str(path))
    n = doc.page_count
    try:
        for i in range(n):
            page = doc[i]
            pix = page.get_pixmap(matrix=pymupdf.Matrix(ZOOM, ZOOM))
            img = Image.open(BytesIO(pix.tobytes("png"))).convert("RGB")
            buf = BytesIO()
            img.save(buf, format="PNG")
            tl = ""
            if with_text:
                tl = page.get_text() or ""
            yield pdf_name, i + 1, buf.getvalue(), tl
    finally:
        doc.close()


def _existing_pages(path: Path) -> set:
    done = set()
    if path.exists():
        with path.open(encoding="utf-8") as f:
            for line in f:
                try:
                    done.add(json.loads(line).get("page"))
                except Exception:
                    continue
    return done


def _write_jsonl(path: Path, rec: dict, lock: threading.Lock):
    with lock:
        with path.open("a", encoding="utf-8") as f:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")


def run(gt_only: bool, engine: str | None, workers: int, books_filter: str | None):
    books = [b for b in BOOKS if not books_filter or books_filter in b]
    if gt_only:
        gt_dir = OUT / "textlayer"
        gt_dir.mkdir(parents=True, exist_ok=True)
        for name in books:
            t0 = time.time()
            out_path = gt_dir / f"{slug(name)}.jsonl"
            done = _existing_pages(out_path)
            n_chars = 0
            n_pages = 0
            with out_path.open("a", encoding="utf-8") as f:
                for book, page, _png, tl in _render_book_pages(name, None, True):
                    if page in done:
                        continue
                    f.write(json.dumps({
                        "book": name, "page": page,
                        "chars": len(WORD.findall(tl)), "cjk": len(re.findall(r"[一-鿿]", tl)),
                        "text": tl,
                    }, ensure_ascii=False) + "\n")
                    n_pages += 1
                    n_chars += len(WORD.findall(tl))
            print(f"[gt] {name[:26]} pages+{n_pages} chars+{n_chars} {time.time()-t0:.1f}s", flush=True)
        return 0

    # ── OCR 全量模式 ──
    assert engine
    out_dir = OUT / "full" / engine
    out_dir.mkdir(parents=True, exist_ok=True)
    os.environ["OCR_INTRA"] = os.environ.get("OCR_INTRA", "2")
    intra = int(os.environ["OCR_INTRA"])
    lock = threading.Lock()
    summary_books = {}
    t_all0 = time.time()
    for name in books:
        out_path = out_dir / f"{slug(name)}.jsonl"
        done = _existing_pages(out_path)
        print(f"[{engine}] {name[:30]} total_pages_start done={len(done)}", flush=True)
        t0 = time.time()
        stats = {"pages": 0, "chars": 0, "errors": 0, "sum_ms": 0}
        with ProcessPoolExecutor(max_workers=workers, initializer=_init_engine, initargs=(engine, intra)) as ex:
            pending = set()
            WINDOW = max(workers * 3, 12)
            gen = _render_book_pages(name, engine, False)
            first = True
            while True:
                # 补满窗口
                while gen is not None and len(pending) < WINDOW:
                    try:
                        book, page, png, _tl = next(gen)
                    except StopIteration:
                        gen = None
                        break
                    if page in done:
                        continue
                    if first:
                        t_first0 = time.time()
                        first = False
                    pending.add(ex.submit(_ocr_job, (engine, book, page, png)))
                if not pending:
                    break
                fut = next(as_completed(list(pending)))
                pending.discard(fut)
                r = fut.result()
                stats["pages"] += 1
                stats["chars"] += r["chars"]
                stats["sum_ms"] += r["ms"]
                if r["error"]:
                    stats["errors"] += 1
                _write_jsonl(out_path, r, lock)
                if stats["pages"] % 100 == 0:
                    rate = stats["pages"] / max(time.time() - t0, 1)
                    print(f"  {name[:24]} p{r['page']} {stats['pages']} done rate={rate:.2f}p/s", flush=True)
        wall = time.time() - t0
        total = len(done) + stats["pages"]
        summary_books[name] = {
            "pages_total": total,
            "pages_this_run": stats["pages"],
            "resumed_pages": len(done),
            "wall_s": round(wall, 2),
            "pages_per_s": round(stats["pages"] / wall, 3) if wall and stats["pages"] else None,
            "chars": stats["chars"],
            "errors": stats["errors"],
            "sum_ms": stats["sum_ms"],
        }
        print(f"[{engine}] DONE {name[:30]} pages={total} wall={wall:.0f}s chars={stats['chars']} err={stats['errors']}", flush=True)
    (out_dir / "_summary.json").write_text(
        json.dumps({
            "engine": engine, "workers": workers, "intra": intra,
            "total_wall_s": round(time.time() - t_all0, 2),
            "books": summary_books,
        }, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"[{engine}] ALL DONE wall={time.time()-t_all0:.0f}s", flush=True)
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--gt-only", action="store_true")
    ap.add_argument("--engine", choices=["rapid3", "rapid", "paddle"])
    ap.add_argument("--workers", type=int, default=12)
    ap.add_argument("--books", default=None, help="子串过滤书名")
    args = ap.parse_args()
    return run(args.gt_only, args.engine, args.workers, args.books)


if __name__ == "__main__":
    raise SystemExit(main())
