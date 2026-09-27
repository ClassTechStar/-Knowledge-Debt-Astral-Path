# -*- coding: utf-8 -*-
"""
OCR 引擎评估对比：GT 文本层 vs 引擎全页 OCR vs 项目自带 OCR（生产 CLI 输出）。

输入：
  ocr-bench/textlayer/{slug}.jsonl      GT（每页 text/chars）
  ocr-bench/full/{engine}/{slug}.jsonl  引擎全页 OCR
  ocr-bench/project/{slug}.json         项目生产管线 payload（fullText/extractedChars/elapsed）
  ocr-bench/bench_{engine}.json         120 页抽样基准（速度/质量）

输出：
  ocr-bench/eval_report.json            全部指标
  控制台 markdown 汇总表

指标（每书 × 每引擎）：
  覆盖：OCR 页数/总页数、空页数（成功率）
  完整度：抽取字符量 vs GT 字符量（比值）、OCR 增益页（引擎字数>GT）
  质量：有文本层页的页级 CER（GT 为底）均值/中位/P90；纯扫描页 OCR 字符产出
  字符级错误：归一化对齐后的替换混淆对 TOP-N（抽样页）
  项目管线：extractedChars、与 GT 全书 CER、耗时
"""
from __future__ import annotations

import difflib
import json
import re
import statistics
from collections import Counter
from pathlib import Path

BENCH = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\ocr-bench")
WORD = re.compile(r"[A-Za-z0-9一-鿿]")


def norm(s: str) -> str:
    """对齐用归一：去空白 + 全角转半角常用符。"""
    s = (s or "").replace("　", " ")
    table = str.maketrans("，。：；！？（）【】《》“”‘’", ",.:;!?()[]<>\"\"''")
    return re.sub(r"\s+", "", s).translate(table)


def cer(ref: str, hyp: str) -> float:
    ref, hyp = norm(ref), norm(hyp)
    if not ref:
        return float("nan")
    if not hyp:
        return 1.0
    n, m = len(ref), len(hyp)
    if n * m > 4_000_000:
        ref, hyp = ref[:3000], hyp[:3000]
        n, m = len(ref), len(hyp)
    dp = list(range(m + 1))
    for i in range(1, n + 1):
        prev = dp[0]
        dp[0] = i
        for j in range(1, m + 1):
            cur = dp[j]
            dp[j] = prev if ref[i - 1] == hyp[j - 1] else 1 + min(prev, dp[j], dp[j - 1])
            prev = cur
    return dp[m] / n


def confusion_pairs(ref: str, hyp: str, topn: int = 12):
    """difflib 对齐后的替换混淆统计（仅 CJK/字母数字）。"""
    ref, hyp = norm(ref), norm(hyp)
    sm = difflib.SequenceMatcher(a=ref, b=hyp, autojunk=False)
    pairs: Counter = Counter()
    for tag, i1, i2, j1, j2 in sm.get_opcodes():
        if tag == "replace" and (i2 - i1) == (j2 - j1) and (i2 - i1) <= 2:
            for k in range(i2 - i1):
                a, b = ref[i1 + k], hyp[j1 + k]
                if a != b and (WORD.match(a) or WORD.match(b)):
                    pairs[(a, b)] += 1
    return pairs.most_common(topn)


def load_jsonl(path: Path) -> dict:
    pages = {}
    if path.exists():
        with path.open(encoding="utf-8") as f:
            for line in f:
                try:
                    r = json.loads(line)
                    pages[r["page"]] = r
                except Exception:
                    continue
    return pages


def load_gt_slugs() -> dict:
    gt = {}
    for p in sorted((BENCH / "textlayer").glob("*.jsonl")):
        gt[p.stem] = load_jsonl(p)
    return gt


def eval_engine(slug_: str, gt_pages: dict, engine: str) -> dict:
    eng_pages = load_jsonl(BENCH / "full" / engine / f"{slug_}.jsonl")
    if not eng_pages:
        return {"skipped": True, "reason": "no full extraction"}
    cers, ocr_only_pages, gains, empty = [], [], 0, 0
    conf = Counter()
    sample_done = 0
    total_eng_chars = 0
    total_gt_chars = 0
    for pno, g in gt_pages.items():
        e = eng_pages.get(pno)
        if e is None:
            continue
        total_eng_chars += e.get("chars", 0)
        total_gt_chars += g.get("chars", 0)
        gt_chars = g.get("chars", 0)
        if e.get("error") or (not (e.get("text") or "").strip()):
            empty += 1
            if gt_chars >= 50:
                cers.append(1.0)
            continue
        if gt_chars >= 100 and sample_done < 30:
            c = cer(g["text"], e["text"])
            if c == c:
                cers.append(c)
            if 200 <= gt_chars <= 4000 and sample_done < 12:
                for a, b in confusion_pairs(g["text"], e["text"]):
                    conf[f"{a}->{b}"] += 1
                sample_done += 1
        if gt_chars < 20 and e.get("chars", 0) > 20:
            ocr_only_pages.append({"page": pno, "chars": e["chars"]})
        if e.get("chars", 0) > gt_chars * 1.15 and gt_chars > 30:
            gains += 1
    cers_sorted = sorted(cers)
    return {
        "pages_ocr": len(eng_pages),
        "pages_gt": len(gt_pages),
        "empty_pages": empty,
        "success_rate": round(1 - empty / max(len(eng_pages), 1), 4),
        "eng_chars": total_eng_chars,
        "gt_chars": total_gt_chars,
        "chars_ratio": round(total_eng_chars / max(total_gt_chars, 1), 3),
        "cer_mean": round(statistics.mean(cers), 4) if cers else None,
        "cer_median": round(statistics.median(cers), 4) if cers else None,
        "cer_p90": round(cers_sorted[int(len(cers_sorted) * 0.9) - 1], 4) if cers_sorted else None,
        "cer_n": len(cers),
        "ocr_beyond_textlayer_pages": len(ocr_only_pages),
        "ocr_gain_pages": gains,
        "top_confusions": dict(conf.most_common(10)),
        "sum_ms": sum(e.get("ms", 0) for e in eng_pages.values()),
        "errors": sum(1 for e in eng_pages.values() if e.get("error")),
    }


def eval_project(slug_: str, gt_pages: dict) -> dict:
    p = BENCH / "project" / f"{slug_}.json"
    if not p.exists():
        return {"skipped": True}
    d = json.loads(p.read_text(encoding="utf-8"))
    full = d.get("fullText") or ""
    gt_all = "\n".join(g.get("text", "") for g in gt_pages.values())
    gt_chars = sum(g.get("chars", 0) for g in gt_pages.values())
    # 项目输出带 PAGE 标记则逐页比，否则全书比
    page_cers = []
    parts = re.split(r"===== PAGE (\d+) =====", full)
    page_map = {}
    if len(parts) >= 3:
        for i in range(1, len(parts) - 1, 2):
            page_map[int(parts[i])] = parts[i + 1]
    for pno, g in gt_pages.items():
        if g.get("chars", 0) < 100 or pno not in page_map:
            continue
        c = cer(g["text"], page_map[pno])
        if c == c:
            page_cers.append(c)
    whole = None
    if not page_cers:
        whole = cer(gt_all, full)
    cers_sorted = sorted(page_cers)
    return {
        "ok": d.get("ok"),
        "mode": d.get("mode"),
        "ocr_engine": d.get("ocrEngine"),
        "extracted_chars": d.get("extractedChars"),
        "gt_chars": gt_chars,
        "chars_ratio": round((d.get("extractedChars") or 0) / max(gt_chars, 1), 3),
        "page_cer_mean": round(statistics.mean(page_cers), 4) if page_cers else None,
        "page_cer_median": round(statistics.median(page_cers), 4) if page_cers else None,
        "page_cer_p90": round(cers_sorted[int(len(cers_sorted) * 0.9) - 1], 4) if cers_sorted else None,
        "page_cer_n": len(page_cers),
        "whole_cer": round(whole, 4) if whole == whole else None,
        "elapsed_s": d.get("elapsed_s"),
    }


def main():
    engines = [a for a in ("rapid3", "rapid", "paddle") if (BENCH / "full" / a).exists()]
    gt = load_gt_slugs()
    report = {"engines_full": {}, "project": {}, "bench": {}}
    for eng in engines:
        report["engines_full"][eng] = {}
        for slug_, pages in gt.items():
            r = eval_engine(slug_, pages, eng)
            if not r.get("skipped"):
                report["engines_full"][eng][slug_] = r
    pdir = BENCH / "project"
    if pdir.exists():
        for p in sorted(pdir.glob("*.json")):
            if p.stem in gt:
                report["project"][p.stem] = eval_project(p.stem, gt[p.stem])
    for b in sorted(BENCH.glob("bench_*.json")):
        if b.stem.startswith("bench_compare"):
            continue
        try:
            report["bench"][b.stem] = json.loads(b.read_text(encoding="utf-8"))["summary"]
        except Exception:
            pass
    (BENCH / "eval_report.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")

    # 控制台汇总
    print("## 引擎全量 vs GT\n")
    for eng, books in report["engines_full"].items():
        print(f"### {eng} ({len(books)} 书)\n")
        print("| 书 | 页 | 空页 | 成功率 | 引擎字 | GT字 | 比 | CER均值 | CER中位 | P90 | 增益页 |")
        print("|---|---|---|---|---|---|---|---|---|---|---|")
        for s, r in books.items():
            print(f"| {s[:22]} | {r['pages_ocr']} | {r['empty_pages']} | {r['success_rate']} "
                  f"| {r['eng_chars']} | {r['gt_chars']} | {r['chars_ratio']} "
                  f"| {r['cer_mean']} | {r['cer_median']} | {r['cer_p90']} | {r['ocr_gain_pages']} |")
        print()
    if report["project"]:
        print("## 项目自带 OCR（生产 CLI）vs GT\n")
        print("| 书 | 模式 | 引擎 | 抽取字 | GT字 | 比 | 页CER均值 | 耗时s |")
        print("|---|---|---|---|---|---|---|---|")
        for s, r in report["project"].items():
            if r.get("skipped"):
                continue
            print(f"| {s[:22]} | {r['mode']} | {r['ocr_engine']} | {r['extracted_chars']} "
                  f"| {r['gt_chars']} | {r['chars_ratio']} | {r['page_cer_mean']} | {r['elapsed_s']} |")
    print("\nDONE ->", BENCH / "eval_report.json")


if __name__ == "__main__":
    main()
