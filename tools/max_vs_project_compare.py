# -*- coding: utf-8 -*-
"""对比：我的最大化抽取（ocr-bench/maxextract）vs 项目生产管线（ocr-bench/project）。

指标：
  1. 体量：双方词字符数（CJK+字母数字）、比值
  2. 覆盖：项目 fullText 对我方逐页 18 字 shingle 的命中率 → 未覆盖页清单
  3. 质量：视觉真值页（ocr-bench/gt-vision/{slug}/pXXXX.gt.txt）上双方 CER
     - 我方：逐页直接算
     - 项目：fullText 中探针命中才算（扫描书中段页通常 0 命中 → 覆盖缺失）
  4. 结构：项目 payload 的 ocrPages/garblePages/notes/nodes/edges

用法（paddle-venv）：python tools/max_vs_project_compare.py
"""
from __future__ import annotations

import difflib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MAX_DIR = ROOT / "ocr-bench" / "maxextract"
PROJ_DIR = ROOT / "ocr-bench" / "project"
GT_DIR = ROOT / "ocr-bench" / "gt-vision"
OUT = ROOT / "ocr-bench" / "max_vs_project.json"

WORD = re.compile(r"[A-Za-z0-9一-鿿]")
# 与生产 ocr_pipeline.GARBLE_RANGES 的核心区间一致（PUA/替换字符）
SLUG_OF_GT = {d.name: d for d in GT_DIR.iterdir() if d.is_dir()}


def word_chars(s: str) -> int:
    return len(WORD.findall(s or ""))


def norm(s: str) -> str:
    """只保留 CJK/字母/数字：抽取器间标点/空白差异不影响探针。"""
    return re.sub(r"[^\w一-鿿]+", "", s or "", flags=re.UNICODE)


def cer(ref: str, hyp: str) -> float:
    """字符错误率：1 − SequenceMatcher 比值，基于归一化（只留 CJK/字母/数字）。"""
    r = norm(re.sub(r"[^\w一-鿿]+", "", ref or "", flags=re.UNICODE))
    h = norm(re.sub(r"[^\w一-鿿]+", "", hyp or "", flags=re.UNICODE))
    if not r:
        return 0.0 if not h else 1.0
    if not h:
        return 1.0
    return round(1.0 - difflib.SequenceMatcher(None, r, h).ratio(), 4)


def page_shingles(text: str, n: int = 18, k: int = 3) -> list[str]:
    t = norm(text)
    if len(t) < n + 10:
        return [t] if len(t) >= 8 else []
    pos = [int(len(t) * f) for f in (0.2, 0.5, 0.8)][:k]
    return [t[p:p + n] for p in pos if p + n <= len(t)]


def load_max(slug: str) -> dict[int, dict]:
    path = MAX_DIR / f"{slug}.jsonl"
    rows: dict[int, dict] = {}
    if not path.exists():
        return rows
    for line in path.open(encoding="utf-8"):
        try:
            r = json.loads(line)
        except Exception:
            continue
        rows[int(r["page"])] = r
    return rows


def main() -> int:
    report = []
    for max_path in sorted(MAX_DIR.glob("*.jsonl")):
        slug = max_path.stem
        mine = load_max(slug)
        if not mine:
            continue
        proj_path = PROJ_DIR / f"{slug}.json"
        proj = json.loads(proj_path.read_text(encoding="utf-8")) if proj_path.exists() else None
        pfull = norm(proj.get("fullText", "")) if proj else ""
        pchars_word = word_chars(proj.get("fullText", "")) if proj else 0

        my_chars = sum(r["chars"] for r in mine.values())
        pages = sorted(mine)
        probe_pages = [p for p in pages if mine[p]["chars"] >= 50]
        covered = 0
        missing_samples = []
        for p in probe_pages:
            sh = page_shingles(mine[p]["text"])
            if sh and any(s in pfull for s in sh):
                covered += 1
            elif len(missing_samples) < 10:
                missing_samples.append(p)

        # 视觉真值页
        gt_dir = GT_DIR / slug
        gt_rows = []
        if gt_dir.exists():
            for gtf in sorted(gt_dir.glob("*.gt.txt")):
                pno = int(gtf.stem.split(".")[0][1:])
                gt = gtf.read_text(encoding="utf-8")
                my_page = mine.get(pno, {}).get("text", "")
                row = {
                    "page": pno,
                    "gt_chars": word_chars(gt),
                    "my_chars": word_chars(my_page),
                    "my_cer": cer(gt, my_page),
                    "my_method": mine.get(pno, {}).get("method"),
                    "proj_probe_hit": any(s in pfull for s in page_shingles(gt)),
                }
                gt_rows.append(row)

        rec = {
            "slug": slug,
            "pages": len(pages),
            "my_chars": my_chars,
            "proj_exists": bool(proj),
            "proj_chars_raw": (proj or {}).get("extractedChars"),
            "proj_chars_word": pchars_word,
            "proj_ratio_vs_mine": round(pchars_word / max(1, my_chars), 3),
            "probe_pages": len(probe_pages),
            "proj_pages_covered": covered,
            "proj_pages_missing": len(probe_pages) - covered,
            "missing_samples": missing_samples,
            "proj_ocrPages": (proj or {}).get("ocrPages", []),
            "proj_garblePages_count": len((proj or {}).get("garblePages", [])),
            "proj_notes": (proj or {}).get("notes", []),
            "proj_nodes": len((proj or {}).get("nodes", [])),
            "proj_edges": len((proj or {}).get("edges", [])),
            "proj_chapters": len(((proj or {}).get("toc") or {}).get("chapters", [])),
            "proj_elapsed_s": (proj or {}).get("elapsed_s"),
            "gt_eval": gt_rows,
        }
        report.append(rec)
        c = rec["proj_pages_covered"]
        print(f"[cmp] {slug[:34]:<36} pages={rec['pages']:<4} mine={my_chars:<8} "
              f"proj={pchars_word:<8} cover={c}/{len(probe_pages)} gt_pages={len(gt_rows)}", flush=True)

    OUT.write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"DONE -> {OUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
