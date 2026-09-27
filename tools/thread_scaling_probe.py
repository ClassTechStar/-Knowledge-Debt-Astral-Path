# -*- coding: utf-8 -*-
"""线程扩展探针：验证 ORT 会话线程数是否生效 + 1/2/4/8/12 worker 扩展曲线。"""
import json
import os
import sys
import time
from concurrent.futures import ProcessPoolExecutor
from io import BytesIO
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from bench_ocr_engines import BOOKS, PDF_DIR, _init_engine, _ocr_one  # noqa: E402

import pymupdf  # noqa: E402
from PIL import Image  # noqa: E402


def render_probe_pages(k=8):
    """取第一本书的 k 页做探针（同质页面，扩展曲线更干净）。"""
    doc = pymupdf.open(str(PDF_DIR / BOOKS[0]))
    n = doc.page_count
    idxs = [int(i * (n - 1) / (k - 1)) for i in range(k)]
    jobs = []
    for i in idxs:
        pix = doc[i].get_pixmap(matrix=pymupdf.Matrix(1.5, 1.5))
        img = Image.open(BytesIO(pix.tobytes("png"))).convert("RGB")
        buf = BytesIO()
        img.save(buf, format="PNG")
        jobs.append(("rapid3", BOOKS[0], i + 1, buf.getvalue(), ""))
    doc.close()
    return jobs


def _sess_report(_):
    import gc

    import numpy as np
    import onnxruntime as ort
    try:
        from rapidocr import RapidOCR
        eng = RapidOCR()
        img = (np.ones((64, 200, 3), dtype="uint8") * 255)
        eng(img)
        out = {}
        for o in gc.get_objects():
            if isinstance(o, ort.InferenceSession):
                so = o.get_session_options()
                out[f"sess{len(out)}"] = (so.intra_op_num_threads, so.inter_op_num_threads)
        return out
    except Exception as e:
        return {"error": str(e)[:120]}


def main():
    engine = sys.argv[1] if len(sys.argv) > 1 and "x" not in sys.argv[1] else "rapid3"
    combos = [a for a in sys.argv[1:] if "x" in a] or ["1x8", "2x8", "4x4", "8x2", "12x1"]
    jobs = render_probe_pages(8)
    for combo in combos:
        w, intra = (int(x) for x in combo.split("x"))
        os.environ["OCR_INTRA"] = str(intra)
        with ProcessPoolExecutor(max_workers=1, initializer=_init_engine, initargs=(engine, intra)) as ex:
            rep = ex.submit(_sess_report, None).result()
            t0 = time.time()
            rs = list(ex.map(_ocr_one, jobs))
            s1 = time.time() - t0
            print(f"w=1 intra={intra}: {s1:.2f}s avg={sum(r['ms'] for r in rs[1:])/7:.0f}ms sess={rep}", flush=True)
        t0 = time.time()
        with ProcessPoolExecutor(max_workers=w, initializer=_init_engine, initargs=(engine, intra)) as ex:
            rs = list(ex.map(_ocr_one, jobs))
        wall = time.time() - t0
        print(f"workers={w} intra={intra}: wall={wall:.2f}s pps={8/wall:.2f} avg_ms={sum(r['ms'] for r in rs)/8:.0f}", flush=True)


if __name__ == "__main__":
    main()
