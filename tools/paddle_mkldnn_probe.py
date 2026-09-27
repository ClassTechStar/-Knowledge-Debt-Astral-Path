# -*- coding: utf-8 -*-
"""paddle + mkldnn 串行速度探针（真实文件，spawn 安全）。"""
import sys
import time
from concurrent.futures import ProcessPoolExecutor
from io import BytesIO
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from bench_ocr_engines import BOOKS, PDF_DIR  # noqa: E402

ENG = None


def worker_init():
    global ENG
    from paddleocr import PaddleOCR

    ENG = PaddleOCR(
        use_textline_orientation=False, use_doc_orientation_classify=False,
        use_doc_unwarping=False, lang="ch", enable_mkldnn=True, cpu_threads=4,
    )


def one(png):
    import numpy as np
    from PIL import Image

    arr = np.array(Image.open(BytesIO(png)).convert("RGB"))
    t0 = time.time()
    list(ENG.predict(arr))
    return int((time.time() - t0) * 1000)


def make_jobs(k=8):
    import pymupdf
    from PIL import Image

    doc = pymupdf.open(str(PDF_DIR / BOOKS[0]))
    n = doc.page_count
    jobs = []
    for i in range(k):
        pix = doc[int(i * (n - 1) / (k - 1))].get_pixmap(matrix=pymupdf.Matrix(1.5, 1.5))
        img = Image.open(BytesIO(pix.tobytes("png"))).convert("RGB")
        buf = BytesIO()
        img.save(buf, format="PNG")
        jobs.append(buf.getvalue())
    doc.close()
    return jobs


def main():
    jobs = make_jobs(8)
    with ProcessPoolExecutor(max_workers=1, initializer=worker_init) as ex:
        ms = list(ex.map(one, jobs))
    print("paddle mkldnn serial avg(excl first):", sum(ms[1:]) // len(ms[1:]), "ms  all:", ms)


if __name__ == "__main__":
    main()
