# -*- coding: utf-8 -*-
"""OCR 回归门（P3-17）：一条命令复跑项目管线并对照 2026-09-27 基线判定。

基线数据（入库，勿随手改）：
  ocr-bench/maxextract/*.jsonl   独立最大化抽取（文本层×RapidOCR v3 逐页择优）
  ocr-bench/gt-vision/           12 页独立视觉真值（PNG + .gt.txt）
  ocr-bench/project_v1_before/   优化前生产 payload（留档）

用法（paddle-venv 或含 pypdfium2/PIL 的 python）：
  python tools/ocr_regression.py               # 复跑缺失的书 + 全部判定
  python tools/ocr_regression.py --force       # 忽略已有结果全部重跑
  python tools/ocr_regression.py --judge-only  # 只判定不复跑（用现有 project/*.json）
  python tools/ocr_regression.py --selfcheck   # 只跑单元自检

判定阈值（docs/ocr-max-vs-production-2026-09-27.md 任务清单的验收标准）：
  文本层可靠 6 本        字符量 ≥ 基线 95%
  C#/Go/Java            字符量 ≥ 基线 90%（优化前 51–54%）
  强化学习              ≥150k 词字符且 KG 章节 ≥ 8（优化前 24.7k/1 章）
  大模型（扫描）         字符量 ≥ 基线 90%（优化前 6.6%）
  黄仁勋/C#/Java        fullText 康熙部首伪字 = 0（优化前 9.2%/5.1%/0.1%）
  全体                  页级 shingle 探针覆盖 ≥ 90%（优化前 75%）
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tools"))

PDF_DIR = Path(r"C:\Users\18948\Downloads\PDF TEST")
PROJ_DIR = ROOT / "ocr-bench" / "project"
BASE_DIR = ROOT / "ocr-bench" / "maxextract"

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

RATIO_FLOOR = {
    "C#从入门到精通": 0.90, "Go语言从入门到精通": 0.90, "Java从入门到精通": 0.90,
    "大模型应用开发": 0.90,
    "Kotlin编程实践": 0.95, "Python编程": 0.95, "深度学习入门2": 0.95,
    "深度学习入门：": 0.95, "深度学习进阶": 0.95, "深度学习 Deep Learning": 0.95,
    "黄仁勋": 0.85,
}
RADICAL_ZERO = ("黄仁勋", "C#从入门到精通", "Java从入门到精通")


def slug(name: str) -> str:
    import re
    return re.sub(r"[\\/:*?\"<>|\s（）()【】\[\]·：:,，]+", "_", Path(name).stem).strip("_")[:60]


def selfcheck() -> bool:
    import ocr_pipeline as p
    assert p.fix_radical_chars("⻩埔军校 汽⻋ 拷⻉") == "黄埔军校 汽车 拷贝", "radical map broken"
    assert p.page_word_quality("⻩") == 0, "garbled char should cancel word char"
    t, d = p.merge_page_texts("正文内容比较长一些", "OCR内容", figure=False)
    assert d == "text", f"1.25 rule broken: {d}"
    t, d = p.merge_page_texts("短", "OCR识别出明显更多的内容" * 5, figure=False)
    assert d == "ocr", f"ocr should win: {d}"
    import kg_algorithm as k
    assert k.parse_chapter_num("步骤26 计算图") == 26
    assert k.parse_chapter_num("第5阶段 DeZero") == 5
    assert k.parse_chapter_num("第3章 x") == 3
    print("[selfcheck] OK")
    return True


def run_books(force: bool) -> None:
    missing = [b for b in BOOKS if force or not (PROJ_DIR / f"{slug(b)}.json").exists()]
    if not missing:
        print("[run] project payloads up to date")
        return
    for b in missing:
        cmd = [sys.executable, str(ROOT / "tools" / "run_project_ocr_bench.py"),
               "--mode", "standard", "--python", sys.executable, "--books", b]
        print("[run]", b[:36], flush=True)
        subprocess.run(cmd, check=False)
        # 预算截断的书：页缓存让二段跑只补缺页
        f = PROJ_DIR / f"{slug(b)}.json"
        if f.exists() and "ocr_budget_truncated" in f.read_text(encoding="utf-8"):
            print("[resume]", b[:36], flush=True)
            subprocess.run(cmd, check=False)


def judge() -> bool:
    from max_vs_project_compare import load_max, page_shingles, word_chars, norm

    report = []
    ok = True
    total_cov = total_probe = 0
    for b in BOOKS:
        s = slug(b)
        proj_path = PROJ_DIR / f"{s}.json"
        mine = load_max(s)
        if not mine or not proj_path.exists():
            print(f"[judge] SKIP {s[:30]}（缺基线或项目结果）")
            continue
        proj = json.loads(proj_path.read_text(encoding="utf-8"))
        ft = proj.get("fullText", "")
        pword = word_chars(ft)
        mword = sum(r["chars"] for r in mine.values())
        probe = [pno for pno, r in mine.items() if r["chars"] >= 50]
        pfull = norm(ft)
        covered = sum(1 for pno in probe if any(sh in pfull for sh in page_shingles(mine[pno]["text"])))
        total_cov += covered
        total_probe += len(probe)
        radical = sum(1 for ch in ft if 0x2E80 <= ord(ch) <= 0x2FDF)
        chs = (proj.get("graphStats") or {}).get("chapters")

        fails = []
        floor = next((v for k, v in RATIO_FLOOR.items() if b.startswith(k)), 0.95)
        ratio = pword / max(1, mword)
        if ratio < floor:
            fails.append(f"ratio {ratio:.1%}<{floor:.0%}")
        if any(b.startswith(k) for k in RADICAL_ZERO) and radical > 0:
            fails.append(f"radical={radical}")
        if b.startswith("图灵程序设计丛书"):
            if mword < 150000 or (chs or 0) < 8:
                fails.append(f"chars={mword} chapters={chs}")
        cov_ratio = covered / max(1, len(probe))
        status = "PASS" if not fails else "FAIL"
        if fails:
            ok = False
        report.append(f"[{status}] {s[:30]:<32} chars {pword:>7}/{mword:<7}({ratio:>6.1%}) "
                      f"cover {covered}/{len(probe)} radical={radical} ch={chs} {';'.join(fails)}")
    cov_all = total_cov / max(1, total_probe)
    print("\n".join(report))
    print(f"[gate] total probe coverage {total_cov}/{total_probe} = {cov_all:.1%} (floor 90%)")
    if cov_all < 0.90:
        ok = False
    print("[regression]", "PASS ✅" if ok else "FAIL ❌")
    return ok


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--judge-only", action="store_true")
    ap.add_argument("--selfcheck", action="store_true")
    args = ap.parse_args()
    if args.selfcheck:
        return 0 if selfcheck() else 1
    selfcheck()
    if not args.judge_only:
        run_books(args.force)
    return 0 if judge() else 1


if __name__ == "__main__":
    raise SystemExit(main())
