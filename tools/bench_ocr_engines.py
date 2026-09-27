# -*- coding: utf-8 -*-
"""
PaddleOCR vs RapidOCR 并行基准 v2：12 本 PDF 抽样页
- 每本 10 页（首/中/尾 + 低文本页），共约 120 页（与 v1 抽样一致，可对比）
- 多进程 ProcessPoolExecutor：每 worker 独立引擎实例
- v2 修复：v1 的 ORT 每 worker 默认全核 intra-op 导致超订阅（35s/页假慢），
  这里统一 monkeypatch ORT 会话 intra_op=OCR_INTRA(默认2)；paddle 用 cpu_threads。
- 引擎：rapid3=本地 RapidOCR 仓库 python/ 包(v3)；rapid=rapidocr_onnxruntime 1.4.4（项目现用）；paddle=paddleocr 3.7
- 质量：字符量 + 与文本层的字符级 CER；串行基线：单 worker 10 页估加速比
用法（在 paddle-venv 中）：
  python bench_ocr_engines.py rapid3 [workers] [intra_threads]
  python bench_ocr_engines.py both [workers]   # rapid3 + paddle
"""
from __future__ import annotations

import json
import os
import re
import sys
import time
from concurrent.futures import ProcessPoolExecutor, as_completed
from io import BytesIO
from pathlib import Path

PDF_DIR = Path(r"C:\Users\18948\Downloads\PDF TEST")
OUT = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\ocr-bench")
OUT.mkdir(parents=True, exist_ok=True)
WORD = re.compile(r"[A-Za-z0-9一-鿿]")

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


def sample_pages(n, k=10):
    """分层抽样：首/中/尾 + 均匀间隔，保证覆盖扫描/低文本页。"""
    if n <= k:
        return list(range(n))
    idx = {0, n // 2, n - 1}
    step = max(1, (n - 1) // (k - 1))
    for i in range(k):
        idx.add(min(n - 1, i * step))
    return sorted(idx)[:k]


def render_jobs(pdf_name):
    """渲染抽样页为 PNG bytes，附带文本层与页码。"""
    import pymupdf
    from PIL import Image

    path = PDF_DIR / pdf_name
    doc = pymupdf.open(str(path))
    idxs = sample_pages(doc.page_count)
    out = []
    for i in idxs:
        page = doc[i]
        zoom = 1.5
        pix = page.get_pixmap(matrix=pymupdf.Matrix(zoom, zoom))
        img = Image.open(BytesIO(pix.tobytes("png"))).convert("RGB")
        buf = BytesIO()
        img.save(buf, format="PNG")
        text_layer = page.get_text() or ""
        out.append({
            "book": pdf_name,
            "page": i + 1,
            "png": buf.getvalue(),
            "text_layer": text_layer[:8000],
            "text_layer_chars": len(WORD.findall(text_layer)),
        })
    doc.close()
    return out


def _pin_ort_threads(intra: int):
    """让本进程内所有 ORT 会话固定 intra_op 线程数，避免多 worker 超订阅。"""
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


# ── worker：每进程初始化一次引擎 ──
_ENGINE = None


def _init_engine(name, intra=2):
    global _ENGINE
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


def _extract_text(name, arr):
    if name in ("rapid", "rapid3"):
        out = _ENGINE(arr)
        if hasattr(out, "txts"):
            return "\n".join(str(t) for t in (out.txts or []))
        if isinstance(out, tuple) and out:
            res = out[0]
        else:
            res = out
        if not res:
            return ""
        parts = []
        for item in res:
            if isinstance(item, (list, tuple)) and len(item) >= 2:
                parts.append(str(item[1]))
            else:
                parts.append(str(item))
        return "\n".join(parts)
    else:
        # PaddleOCR 3.x predict
        result = list(_ENGINE.predict(arr))
        if not result:
            return ""
        r0 = result[0]
        if hasattr(r0, "get"):
            texts = r0.get("rec_texts") or []
            return "\n".join(str(t) for t in texts)
        return str(r0)


def _ocr_one(args):
    name, book, page, png_bytes, text_layer = args
    global _ENGINE
    if _ENGINE is None:
        _init_engine(name, int(os.environ.get("OCR_INTRA", "2")))
    import numpy as np
    from PIL import Image

    img = Image.open(BytesIO(png_bytes)).convert("RGB")
    arr = np.array(img)
    t0 = time.time()
    try:
        text = _extract_text(name, arr)
        err = None
    except Exception as e:
        text, err = "", f"{type(e).__name__}: {str(e)[:120]}"
    ms = int((time.time() - t0) * 1000)
    chars = len(WORD.findall(text))
    cjk = len(re.findall(r"[一-鿿]", text))
    tl_chars = len(WORD.findall(text_layer or ""))
    page_cer = float("nan")
    if tl_chars > 20 and text:
        page_cer = cer(text_layer or "", text)
    return {
        "book": book,
        "page": page,
        "engine": name,
        "ms": ms,
        "chars": chars,
        "cjk": cjk,
        "text_layer_chars": tl_chars,
        "cer": round(page_cer, 4) if page_cer == page_cer else None,
        "error": err,
        "text": text[:2000],
    }


def cer(ref: str, hyp: str) -> float:
    """字符级编辑距离 / |ref|，ref 为空返回 nan。"""
    ref = re.sub(r"\s+", "", ref or "")
    hyp = re.sub(r"\s+", "", hyp or "")
    if not ref:
        return float("nan")
    if not hyp:
        return 1.0
    n, m = len(ref), len(hyp)
    if n * m > 4_000_000:
        ref, hyp = ref[:2000], hyp[:2000]
        n, m = len(ref), len(hyp)
    dp = list(range(m + 1))
    for i in range(1, n + 1):
        prev = dp[0]
        dp[0] = i
        for j in range(1, m + 1):
            cur = dp[j]
            if ref[i - 1] == hyp[j - 1]:
                dp[j] = prev
            else:
                dp[j] = 1 + min(prev, dp[j], dp[j - 1])
            prev = cur
    return dp[m] / n


def run_engine(engine: str, workers: int, intra: int) -> dict:
    print(f"=== engine={engine} workers={workers} intra={intra} ===", flush=True)
    jobs = []
    for name in BOOKS:
        try:
            pages = render_jobs(name)
        except Exception as e:
            print("render-fail", name[:30], e, flush=True)
            continue
        for p in pages:
            jobs.append((engine, p["book"], p["page"], p["png"], p["text_layer"]))
        print(f"prepared {name[:28]} pages={len(pages)}", flush=True)
    print(f"total jobs={len(jobs)}", flush=True)

    t_wall0 = time.time()
    # 预热：单页，计入模型加载
    t_warm0 = time.time()
    with ProcessPoolExecutor(max_workers=1, initializer=_init_engine, initargs=(engine, intra)) as ex:
        _ = list(ex.map(_ocr_one, jobs[:1]))
    warm_s = time.time() - t_warm0

    # 串行基线：单 worker 10 页（第 1 页吸收初始化，不计入均值）
    t_s0 = time.time()
    with ProcessPoolExecutor(max_workers=1, initializer=_init_engine, initargs=(engine, intra)) as ex:
        serial = list(ex.map(_ocr_one, jobs[1:11]))
    serial_s = time.time() - t_s0
    serial_avg_ms = sum(r["ms"] for r in serial[1:]) / max(len(serial) - 1, 1)

    t0 = time.time()
    results = []
    with ProcessPoolExecutor(max_workers=workers, initializer=_init_engine, initargs=(engine, intra)) as ex:
        futs = [ex.submit(_ocr_one, j) for j in jobs]
        for i, f in enumerate(as_completed(futs), 1):
            r = f.result()
            results.append(r)
            if i % 20 == 0 or i == len(jobs):
                print(f"[{i}/{len(jobs)}] done", flush=True)
    wall = time.time() - t0

    cers = [r["cer"] for r in results if r.get("cer") is not None]
    per_book = {}
    for r in results:
        b = per_book.setdefault(r["book"], {"pages": 0, "chars": 0, "ms": 0, "errors": 0, "cers": []})
        b["pages"] += 1
        b["chars"] += r["chars"]
        b["ms"] += r["ms"]
        b["errors"] += 1 if r.get("error") else 0
        if r.get("cer") is not None:
            b["cers"].append(r["cer"])
    for b in per_book.values():
        b["avg_ms"] = round(b["ms"] / b["pages"], 1)
        b["mean_cer"] = round(sum(b["cers"]) / len(b["cers"]), 4) if b["cers"] else None
        del b["cers"]
    summary = {
        "engine": engine,
        "workers": workers,
        "intra_threads": intra,
        "pages": len(results),
        "wall_s": round(wall, 2),
        "warm_s": round(warm_s, 2),
        "serial_avg_ms": round(serial_avg_ms, 1),
        "serial_s_per_page": round(serial_avg_ms / 1000, 2),
        "parallel_speedup": round(serial_avg_ms * len(results) / (wall * 1000), 2),
        "sum_ms": sum(r.get("ms", 0) for r in results),
        "avg_ms": round(sum(r.get("ms", 0) for r in results) / max(len(results), 1), 1),
        "pages_per_s": round(len(results) / wall, 3) if wall else 0,
        "total_chars": sum(r.get("chars", 0) for r in results),
        "total_cjk": sum(r.get("cjk", 0) for r in results),
        "errors": sum(1 for r in results if r.get("error")),
        "mean_cer_vs_textlayer": round(sum(cers) / len(cers), 4) if cers else None,
        "cer_n": len(cers),
        "total_wall_incl_warm_s": round(time.time() - t_wall0, 2),
        "per_book": per_book,
    }
    out_path = OUT / f"bench_{engine}.json"
    out_path.write_text(
        json.dumps({"summary": summary, "pages": results}, ensure_ascii=False, indent=1),
        encoding="utf-8",
    )
    print("SUMMARY", json.dumps(summary, ensure_ascii=False), flush=True)
    return summary


def main():
    import multiprocessing as mp

    mp.freeze_support()
    engine = sys.argv[1] if len(sys.argv) > 1 else "rapid3"
    workers = int(sys.argv[2]) if len(sys.argv) > 2 else 12
    intra = int(sys.argv[3]) if len(sys.argv) > 3 else 2
    os.environ["OCR_INTRA"] = str(intra)
    if engine == "both":
        s1 = run_engine("rapid3", workers, intra)
        s2 = run_engine("paddle", min(workers, 10), intra)
        cmp = {"rapid3": s1, "paddle": s2}
        (OUT / "bench_compare.json").write_text(json.dumps(cmp, ensure_ascii=False, indent=2), encoding="utf-8")
        print("COMPARE", json.dumps(cmp, ensure_ascii=False), flush=True)
    else:
        run_engine(engine, workers, intra)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
