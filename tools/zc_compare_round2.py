# -*- coding: utf-8 -*-
"""Round C：Round A（ZCode 智能体独立测试）vs Round B（项目管线）对拍汇总。

四方数据源：
  A  ocr-bench/agent-vs-project/A/{slug}/A-report.json   独立测试报告
  B  ocr-bench/project/{slug}.json                       项目管线 payload
  TL ocr-bench/textlayer/{slug}.jsonl                    文本层逐页（上轮 GT 基线）
  RF ocr-bench/full/rapid3/{slug}.jsonl                  rapid3 全页 OCR 参考（上轮）

口径：全部字符数用 WORD=[A-Za-z0-9一-鿿] 计数（B 的 extractedChars 是 len(raw)，含空白，不可直接比）。
B 页覆盖：内容页（TL/RF 任一 ≥50 字）取 3 个 18 字 shingle 探针在 B.fullText 归一化后命中 ≥1 即算覆盖。

用法：python tools/zc_compare_round2.py [--gate]
  --gate  回归门禁（P6-9）：任一书 B/参考% < 85 → 退出码 1（CI/发布前必跑）
输出：ocr-bench/agent-vs-project/compare_report.json + 控制台表
"""
from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
A_DIR = ROOT / "ocr-bench" / "agent-vs-project" / "A"
B_DIR = ROOT / "ocr-bench" / "project"
TL_DIR = ROOT / "ocr-bench" / "textlayer"
RF_DIR = ROOT / "ocr-bench" / "full" / "rapid3"
OUT = ROOT / "ocr-bench" / "agent-vs-project" / "compare_report.json"
WORD = re.compile(r"[A-Za-z0-9一-鿿]")


def word_chars(s: str) -> int:
    return len(WORD.findall(s or ""))


def norm(s: str) -> str:
    return re.sub(r"\s+", "", s or "")


def shingles(text: str, n: int = 18) -> list[str]:
    t = norm(text)
    if len(t) < n + 10:
        return [t] if len(t) >= 8 else []
    out = []
    for f in (0.2, 0.5, 0.8):
        p = int(len(t) * f)
        if p + n <= len(t):
            out.append(t[p:p + n])
    return out or ([t[:n]] if len(t) >= n else [])


def load_jsonl(p: Path) -> list[dict]:
    if not p.exists():
        return []
    # 注意：不能用 splitlines()——文本层的 U+2028/U+2029 会被误当行分隔符切断 JSON
    return [json.loads(ln) for ln in p.read_text(encoding="utf-8").split("\n") if ln.strip()]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--gate", action="store_true", help="回归门禁：任一书 B/参考%%<85 退出码 1")
    args = ap.parse_args()
    rows = []
    for a_dir in sorted(A_DIR.iterdir()):
        if not a_dir.is_dir() or a_dir.name.startswith("_"):
            continue
        slug = a_dir.name
        rep_p = a_dir / "A-report.json"
        if not rep_p.exists():
            continue
        rep = json.loads(rep_p.read_text(encoding="utf-8"))

        b_p = B_DIR / f"{slug}.json"
        b_payload = json.loads(b_p.read_text(encoding="utf-8")) if b_p.exists() else {}
        b_text = b_payload.get("fullText", "") or ""
        b_chars = word_chars(b_text)
        b_norm = norm(b_text)

        tl = load_jsonl(TL_DIR / f"{slug}.jsonl")
        rf = load_jsonl(RF_DIR / f"{slug}.jsonl")
        tl_by_page = {r["page"]: r.get("text", "") for r in tl}
        rf_by_page = {r["page"]: r.get("text", "") for r in rf if not r.get("error")}

        tl_chars = sum(word_chars(t) for t in tl_by_page.values())
        rf_chars = sum(word_chars(t) for t in rf_by_page.values())
        pages = rep.get("pages") or len(tl) or len(rf)

        content_pages = [
            pg for pg in range(1, pages + 1)
            if word_chars(tl_by_page.get(pg, "")) >= 50 or word_chars(rf_by_page.get(pg, "")) >= 50
        ]
        covered = 0
        for pg in content_pages:
            probes = shingles(tl_by_page.get(pg, "") or rf_by_page.get(pg, ""))
            if probes and any(s in b_norm for s in probes):
                covered += 1
        cov = round(100.0 * covered / max(1, len(content_pages)), 1)

        defects = rep.get("defects", []) or []
        sev = {s: sum(1 for d in defects if d.get("severity") == s) for s in ("high", "medium", "low")}

        nodes = b_payload.get("nodes") or []
        edges = b_payload.get("edges") or []
        ch_nodes = sum(1 for n in nodes if n.get("description") == "chapter")
        notes = b_payload.get("notes") or []
        deep = next((n for n in notes if str(n).startswith("deep_parse")), "")
        m = re.search(r"ch=(\d+),sec=(\d+)", deep)
        rows.append({
            "slug": slug,
            "pages": pages,
            "A_needsOcr": rep.get("needsOcrPages"),
            "A_zeroText": rep.get("zeroTextPages"),
            "A_cerVsTL": (rep.get("cerVsTextlayer") or {}).get("mean"),
            "A_cerVsRef": (rep.get("cerVsRapid3Ref") or {}).get("mean"),
            "A_defects": sev,
            "A_chapters": len(rep.get("chapters") or []),
            "TL_chars": tl_chars,
            "RF_chars": rf_chars,
            "B_chars": b_chars,
            "B_covChars_pct": round(100.0 * b_chars / rf_chars, 1) if rf_chars else None,
            "B_covChars_vsTL_pct": round(100.0 * b_chars / tl_chars, 1) if tl_chars else None,
            "B_pageCov_pct": cov,
            "B_contentPages": len(content_pages),
            "B_ocrPages": len(b_payload.get("ocrPages") or []),
            "B_elapsed_s": b_payload.get("elapsed_s"),
            "B_engine": b_payload.get("ocrEngine"),
            "B_nodes": len(nodes),
            "B_edges": len(edges),
            "B_chapterNodes": ch_nodes,
            "B_deepParse": (f"ch={m.group(1)},sec={m.group(2)}" if m else deep or "-"),
        })

    OUT.write_text(json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8")
    hdr = ["slug[:22]", "pages", "TL字", "RF字", "B字", "B/RF%", "B页覆%", "ocrP", "A需ocr", "A_cerTL", "缺陷H/M/L", "节点/边", "耗时s"]
    print(" | ".join(hdr))
    for r in rows:
        print(" | ".join([
            r["slug"][:22], str(r["pages"]), str(r["TL_chars"]), str(r["RF_chars"]), str(r["B_chars"]),
            str(r["B_covChars_pct"]), str(r["B_pageCov_pct"]), str(r["B_ocrPages"]),
            str(r["A_needsOcr"]), str(r["A_cerVsTL"]),
            f"{r['A_defects']['high']}/{r['A_defects']['medium']}/{r['A_defects']['low']}",
            f"{r['B_nodes']}/{r['B_edges']}", str(r["B_elapsed_s"]),
        ]))
    tot_tl = sum(r["TL_chars"] for r in rows)
    tot_rf = sum(r["RF_chars"] for r in rows)
    tot_b = sum(r["B_chars"] for r in rows)
    print(f"\nTOTAL TL={tot_tl} RF={tot_rf} B={tot_b} B/RF={round(100*tot_b/tot_rf,1)}%")
    print(f"saved -> {OUT}")

    if args.gate:
        bad = [(r["slug"], r["B_covChars_pct"]) for r in rows
               if r["B_covChars_pct"] is not None and r["B_covChars_pct"] < 85.0]
        if bad:
            print(f"GATE FAIL · {len(bad)} 本书覆盖率 <85%：")
            for slug, pct in bad:
                print(f"  [RED] {slug[:40]} B/参考={pct}%")
            return 1
        print("GATE PASS · 12 本书覆盖率全部 ≥85%")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
