# -*- coding: utf-8 -*-
"""最大化抽取合并：文本层 GT × RapidOCR v3 全页 OCR，逐页择优。

这是"独立评测方"的抽取口径：对每一页，从两个来源中选质量更高者，
目标是不漏字——文本层完整可信时用它（无识别错误），文本层稀疏/可疑时用全页 OCR。

质量分 = 词字符数(CJK+字母数字) − 3×乱码字符(私有使用区/替换字符)
选择规则：
  - 文本层质量 < 20：选 OCR（若 OCR 有内容）
  - OCR 质量 > 文本层质量 × 1.25：选 OCR（要求明显更多内容才值得承担识别错误；
    1.12 时强化学习 p150 这类页会让 OCR 以 12% 的量差冤胜质量更高的文本层）
  - 否则选文本层；两者皆空记 none

输入：
  ocr-bench/textlayer/{slug}.jsonl        {book,page,chars,cjk,text}
  ocr-bench/full/rapid3/{slug}.jsonl      {book,page,ms,chars,cjk,error,text}
输出：
  ocr-bench/maxextract/{slug}.jsonl       {book,page,method,text,chars,tl_chars,ocr_chars}
  ocr-bench/maxextract/_summary.json

用法（paddle-venv）：python tools/max_extract_merge.py [--books 子串]
"""
from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TL_DIR = ROOT / "ocr-bench" / "textlayer"
OCR_DIR = ROOT / "ocr-bench" / "full" / "rapid3"
OUT = ROOT / "ocr-bench" / "maxextract"
OUT.mkdir(parents=True, exist_ok=True)

WORD = re.compile(r"[A-Za-z0-9一-鿿]")


def garbled_count(t: str) -> int:
    return sum(1 for ch in t if 0xE000 <= ord(ch) <= 0xF8FF or ord(ch) == 0xFFFD)


def quality(t: str) -> int:
    return max(0, len(WORD.findall(t or "")) - 3 * garbled_count(t or ""))


def load_jsonl(path: Path) -> dict[int, dict]:
    rows: dict[int, dict] = {}
    if not path.exists():
        return rows
    for line in path.open(encoding="utf-8"):
        line = line.strip()
        if not line:
            continue
        try:
            r = json.loads(line)
        except Exception:
            continue
        p = r.get("page")
        if isinstance(p, int):
            rows[p] = r
    return rows


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--books", default=None, help="书名子串过滤")
    args = ap.parse_args()

    slugs = sorted({p.stem for p in TL_DIR.glob("*.jsonl")})
    if args.books:
        slugs = [s for s in slugs if args.books in s]

    summary: dict[str, dict] = {}
    for slug in slugs:
        tl = load_jsonl(TL_DIR / f"{slug}.jsonl")
        oc = load_jsonl(OCR_DIR / f"{slug}.jsonl")
        if not tl:
            continue
        pages = sorted(tl)
        out_path = OUT / f"{slug}.jsonl"
        stats = {"pages": len(pages), "method_text": 0, "method_ocr": 0, "method_none": 0,
                 "chars": 0, "tl_chars": 0, "ocr_only_pages": 0, "book": tl[pages[0]].get("book", slug)}
        with out_path.open("w", encoding="utf-8") as w:
            for p in pages:
                t_tl = tl[p].get("text") or ""
                r_oc = oc.get(p)
                t_oc = (r_oc.get("text") if r_oc else "") or ""
                q_tl, q_oc = quality(t_tl), quality(t_oc)
                if q_tl < 20 and q_oc > q_tl:
                    method, text = "ocr", t_oc
                elif q_oc > q_tl * 1.25 and q_oc >= 20:
                    method, text = "ocr", t_oc
                elif q_tl > 0:
                    method, text = "text", t_tl
                elif q_oc > 0:
                    method, text = "ocr", t_oc
                else:
                    method, text = "none", ""
                stats["chars"] += len(WORD.findall(text))
                stats["tl_chars"] += q_tl
                if method == "ocr":
                    stats["method_ocr"] += 1
                    if q_tl < 20:
                        stats["ocr_only_pages"] += 1
                elif method == "text":
                    stats["method_text"] += 1
                else:
                    stats["method_none"] += 1
                w.write(json.dumps({
                    "book": stats["book"], "page": p, "method": method,
                    "chars": len(WORD.findall(text)), "tl_chars": q_tl,
                    "ocr_chars": q_oc, "text": text,
                }, ensure_ascii=False) + "\n")
        summary[slug] = stats
        print(f"[max] {slug[:36]:<38} pages={stats['pages']:<4} chars={stats['chars']:<8} "
              f"text={stats['method_text']:<4} ocr={stats['method_ocr']:<4} none={stats['method_none']}",
              flush=True)

    (OUT / "_summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"DONE books={len(summary)} -> {OUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
