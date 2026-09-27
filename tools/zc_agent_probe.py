# -*- coding: utf-8 -*-
"""Round A（ZCode 智能体独立测试）机械探测工具。

与项目管线完全独立：阈值、乱码判定、阅读序拼装均为本轮自定义口径，不复用
ocr_pipeline 的 textGoodRatio / fix_ocr_text / MinSize=1080 逻辑。

子命令（inventory/ocr-pages/render 需 ocr-venv 或含 pypdfium2 的环境；
ocr-pages 额外要求 rapidocr）：
  python tools/zc_agent_probe.py inventory <pdf> --out <json>
  python tools/zc_agent_probe.py ocr-pages <pdf> --pages 1,5,9-12 [--zoom 1.5] --out <json>
  python tools/zc_agent_probe.py render    <pdf> --pages 1,5 [--zoom 1.5] --outdir <dir>
  python tools/zc_agent_probe.py cer <ref.txt> <hyp.txt>
"""
from __future__ import annotations

import argparse
import difflib
import json
import re
import sys
import time
from pathlib import Path

WORD = re.compile(r"[A-Za-z0-9一-鿿]")
CJK = re.compile(r"[一-鿿]")
TOC_LINE = re.compile(r"^\s*(第\s*[0-9一二三四五六七八九十百]+\s*[章篇部讲课回]|Chapter\s+\d+|Appendix\s+[A-Z]).{0,60}[.·…]{2,}\s*[0-9ivx]+\s*$", re.M)
CH_HEAD = re.compile(r"(第\s*[0-9一二三四五六七八九十百]+\s*[章篇部]|Chapter\s+\d+|Part\s+[IIVX]+)")
# P6-10：目录页三形态——①章号+点线+页码；②「标题 ……… 页码」（花书用空格分隔点线 ". . ."，
# 旧正则 [.·…]{2,} 只认连续点导致 26 页目录全漏）；③「标题 空格 页码」成片（无点线目录）。
# 页眉行（如「4.2 病态条件 73」）以页码结尾且单页只出现 1-2 条，不会过 tocDots≥3 门槛。
TOC_DOTS = re.compile(r"^\s*[^…·\s][^…·]{1,60}\s*(?:[.·…]\s*){3,}\s*[0-9]{0,3}\s*$")
TOC_NUMTAIL = re.compile(r"^\s*\S.{1,50}\s[0-9]{1,3}\s*$")
CH_HEAD = re.compile(r"(第\s*[0-9一二三四五六七八九十百]+\s*[章篇部]|Chapter\s+\d+|Part\s+[IIVX]+)")
_NUMTAIL = re.compile(r"[0-9]{1,3}\s*$")


def norm(s: str) -> str:
    return re.sub(r"\s+", "", s or "")


def cer(ref: str, hyp: str) -> float:
    r = norm(re.sub(r"[^\w一-鿿]+", "", ref or "", flags=re.UNICODE))
    h = norm(re.sub(r"[^\w一-鿿]+", "", hyp or "", flags=re.UNICODE))
    if not r:
        return 0.0 if not h else 1.0
    if not h:
        return 1.0
    return round(1.0 - difflib.SequenceMatcher(None, r, h).ratio(), 4)


def _open(path: Path):
    import pypdfium2 as pdfium

    doc = pdfium.PdfDocument(str(path))
    return doc


def _page_text(page) -> str:
    try:
        tp = page.get_textpage()
        try:
            return tp.get_text_bounded() or ""
        except Exception:
            return tp.get_text_range() or ""
    except Exception:
        return ""


def cmd_inventory(pdf: Path, out: Path) -> int:
    doc = _open(pdf)
    n = len(doc)
    pages = []
    ch_hits_by_page: list[list[str]] = []
    for i in range(n):
        page = doc[i]
        w, h = page.get_size()
        text = _page_text(page)
        chars = len(WORD.findall(text))
        cjk = len(CJK.findall(text))
        pua = sum(1 for ch in text if 0xE000 <= ord(ch) <= 0xF8FF)
        fffd = text.count("\ufffd")
        odd = sum(1 for ch in text if 0xFFF0 <= ord(ch) <= 0xFFFF)
        susp = pua + fffd + odd
        susp_ratio = round(susp / max(1, len(text)), 4)
        lines = [ln for ln in text.splitlines() if ln.strip()]
        entry = {
            "page": i + 1,
            "w": round(w), "h": round(h),
            "chars": chars, "cjk": cjk, "lines": len(lines),
            "pua": pua, "fffd": fffd, "suspRatio": susp_ratio,
            "tocLike": len(TOC_LINE.findall(text)),
            "tocDots": sum(1 for ln in lines if TOC_DOTS.match(ln)),
            "numTail": sum(1 for ln in lines if TOC_NUMTAIL.match(ln)),
            "chHeads": 0,  # 页眉去重后在下方回填
        }
        if "目录" in text[:60] or "Contents" in text[:200]:
            entry["tocHeader"] = True
        # 独立判定：字太少或疑似乱码 → 需要 OCR
        if chars < 40:
            entry["needsOcr"], entry["reason"] = True, "low_text"
        elif susp_ratio > 0.05:
            entry["needsOcr"], entry["reason"] = True, "susp_chars"
        else:
            entry["needsOcr"], entry["reason"] = False, ""
        pages.append(entry)
        # P6-10：章首判定（花书实测校准）——
        # ① 只看 ≤40 字的短行：正文长句（「第一部分介绍基本的…」）不算章头；
        # ② 花书页眉形态是「页码在行首」（'4 第一章 引言'）：短行内含独立页码 → 页眉，剔除；
        # ③ 目录行「第五章 机器学习基础 87」以页码结尾 → 同样剔除。
        ch_hits: list[str] = []
        for ln in lines:
            if len(ln) > 40 or _NUMTAIL.search(ln):
                continue
            m = CH_HEAD.search(ln)
            if not m:
                continue
            rest = (ln[:m.start()] + ln[m.end():]).strip()
            if len(ln) <= 24 and re.search(r"(?<!\S)\d{1,3}(?!\S)", rest):
                continue
            ch_hits.append(m.group(1))
        ch_hits_by_page.append(ch_hits)
    doc.close()

    for entry, hits in zip(pages, ch_hits_by_page):
        entry["chHeads"] = len(hits)

    need = sum(1 for p in pages if p["needsOcr"])
    payload = {
        "pdf": pdf.name,
        "pages": n,
        "fileMB": round(pdf.stat().st_size / 2**20, 1),
        "needsOcrPages": need,
        "textPages": n - need,
        "zeroTextPages": sum(1 for p in pages if p["chars"] == 0),
        "tocCandidatePages": [p["page"] for p in pages
                              if p["tocLike"] >= 2 or p["tocDots"] >= 3
                              or (p.get("tocHeader") and p["numTail"] >= 6)][:16],
        "chHeadPages": sum(1 for p in pages if p["chHeads"] > 0),
        "pagesDetail": pages,
    }
    out.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
    print(f"inventory ok pages={n} needsOcr={need} zeroText={payload['zeroTextPages']} "
          f"tocCand={len(payload['tocCandidatePages'])} chHeadPages={payload['chHeadPages']} -> {out}")
    return 0


def _order_lines(res) -> str:
    """独立阅读序：按框中心 y 聚行（阈值 0.012*页高），行内按 x。"""
    # 注意：txts/boxes 可能是 tuple 或 numpy 数组，不能用 `or []`（ndarray 真值二义）
    _t = getattr(res, "txts", None)
    txts = [] if _t is None else [str(t) for t in _t]
    _b = getattr(res, "boxes", None)
    boxes = [] if _b is None else list(_b)
    words = []
    for i, t in enumerate(txts):
        box = boxes[i] if i < len(boxes) else None
        if box is not None and len(box) >= 4:
            try:
                y = sum(float(p[1]) for p in box) / len(box)
                x = sum(float(p[0]) for p in box) / len(box)
            except Exception:
                x = y = 0.0
        else:
            x = y = 0.0
        words.append((str(t), x, y))
    if not words:
        return ""
    words.sort(key=lambda t: (t[2], t[1]))
    rows: list[list[tuple]] = []
    ythr = 14.0
    for w in words:
        row = next((r for r in rows if abs(r[0][2] - w[2]) <= ythr), None)
        if row is None:
            row = []
            rows.append(row)
        row.append(w)
    rows.sort(key=lambda r: sum(x[2] for x in r) / len(r))
    return "\n".join(" ".join(t[0] for t in sorted(r, key=lambda t: t[1])) for r in rows)


def cmd_ocr_pages(pdf: Path, pages: list[int], zoom: float, out: Path) -> int:
    try:
        from rapidocr import RapidOCR
    except Exception:
        print("rapidocr unavailable: run with tools/ocr-venv/Scripts/python.exe", file=sys.stderr)
        return 2
    import numpy as np
    import pypdfium2 as pdfium

    eng = RapidOCR(params={
        "EngineConfig.onnxruntime.intra_op_num_threads": 2,
        "EngineConfig.onnxruntime.inter_op_num_threads": 1,
    })
    doc = pdfium.PdfDocument(str(pdf))
    total = len(doc)
    results = []
    for idx in pages:
        if not (0 <= idx < total):
            results.append({"page": idx + 1, "error": "out_of_range", "ms": 0, "chars": 0, "text": ""})
            continue
        t0 = time.time()
        try:
            bmp = doc[idx].render(scale=zoom)
            img = bmp.to_pil().convert("RGB")
            res = eng(np.array(img))
            text = _order_lines(res)
            ms = int((time.time() - t0) * 1000)
            results.append({"page": idx + 1, "ms": ms, "chars": len(WORD.findall(text)), "text": text})
        except Exception as e:
            results.append({"page": idx + 1, "error": f"{type(e).__name__}: {e}", "ms": int((time.time() - t0) * 1000), "chars": 0, "text": ""})
    doc.close()
    out.write_text(json.dumps({"pdf": pdf.name, "zoom": zoom, "engine": "rapidocr-v3",
                               "pages": results}, ensure_ascii=False), encoding="utf-8")
    ok = [r for r in results if not r.get("error")]
    pps = round(len(ok) / max(0.001, sum(r["ms"] for r in ok) / 1000), 2)
    print(f"ocr-pages ok={len(ok)}/{len(results)} pages/s={pps} -> {out}")
    return 0


def cmd_render(pdf: Path, pages: list[int], zoom: float, outdir: Path) -> int:
    import pypdfium2 as pdfium

    outdir.mkdir(parents=True, exist_ok=True)
    doc = pdfium.PdfDocument(str(pdf))
    for idx in pages:
        if 0 <= idx < len(doc):
            png = outdir / f"p{idx + 1:04d}.png"
            doc[idx].render(scale=zoom).to_pil().save(png)
            print(png)
    doc.close()
    return 0


def cmd_cer(ref: Path, hyp: Path) -> int:
    print(cer(ref.read_text(encoding="utf-8", errors="ignore"), hyp.read_text(encoding="utf-8", errors="ignore")))
    return 0


def _pages_arg(s: str | None) -> list[int]:
    if not s:
        return []
    out: list[int] = []
    for part in s.split(","):
        part = part.strip()
        if "-" in part:
            a, b = part.split("-", 1)
            out.extend(range(int(a), int(b) + 1))
        elif part:
            out.append(int(part))
    return sorted({p - 1 for p in out})  # 1-based in, 0-based out


def main() -> int:
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    p1 = sub.add_parser("inventory")
    p1.add_argument("pdf")
    p1.add_argument("--out", required=True)
    p2 = sub.add_parser("ocr-pages")
    p2.add_argument("pdf")
    p2.add_argument("--pages", required=True)
    p2.add_argument("--zoom", type=float, default=1.5)
    p2.add_argument("--out", required=True)
    p3 = sub.add_parser("render")
    p3.add_argument("pdf")
    p3.add_argument("--pages", required=True)
    p3.add_argument("--zoom", type=float, default=1.5)
    p3.add_argument("--outdir", required=True)
    p4 = sub.add_parser("cer")
    p4.add_argument("ref")
    p4.add_argument("hyp")
    args = ap.parse_args()
    if args.cmd == "inventory":
        return cmd_inventory(Path(args.pdf), Path(args.out))
    if args.cmd == "ocr-pages":
        return cmd_ocr_pages(Path(args.pdf), _pages_arg(args.pages), args.zoom, Path(args.out))
    if args.cmd == "render":
        return cmd_render(Path(args.pdf), _pages_arg(args.pages), args.zoom, Path(args.outdir))
    if args.cmd == "cer":
        return cmd_cer(Path(args.ref), Path(args.hyp))
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
