# -*- coding: utf-8 -*-
"""最小 paddle 多进程可行性测试：2 worker × 2 页，打印错误。"""
import os
import sys
from concurrent.futures import ProcessPoolExecutor
from io import BytesIO
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from bench_ocr_engines import BOOKS, PDF_DIR, _init_engine, _ocr_one  # noqa: E402


def make_jobs(k=2):
    import pymupdf
    from PIL import Image

    doc = pymupdf.open(str(PDF_DIR / BOOKS[0]))
    jobs = []
    for i in range(k):
        pix = doc[i * 20].get_pixmap(matrix=pymupdf.Matrix(1.5, 1.5))
        img = Image.open(BytesIO(pix.tobytes("png"))).convert("RGB")
        buf = BytesIO()
        img.save(buf, format="PNG")
        jobs.append(("paddle", BOOKS[0], i * 20 + 1, buf.getvalue(), ""))
    doc.close()
    return jobs


def main():
    os.environ["OCR_INTRA"] = "4"
    jobs = make_jobs(2)
    with ProcessPoolExecutor(max_workers=2, initializer=_init_engine, initargs=("paddle", 4)) as ex:
        for r in ex.map(_ocr_one, jobs):
            print("ms=", r["ms"], "chars=", r["chars"], "err=", r["error"], flush=True)
    print("OK", flush=True)


if __name__ == "__main__":
    main()
