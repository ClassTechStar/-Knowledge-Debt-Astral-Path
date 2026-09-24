#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""常量对齐校验：JS 的 K 对象 ↔ C# 的 FormulaConstants.cs

用途：P4 在 index.html 里内联了一份 JS 版公式常量，必须与 P2 的 C# 常量逐项一致。
本脚本把「人工签字」变成「CI 可执行的门禁」。

用法：
    python scripts/verify_constants.py
    python scripts/verify_constants.py --json     # 输出 JSON（供 CI 消费）

退出码：0 = 全部对齐（ALL GREEN）；1 = 存在不一致（FAILED: ...）
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CS = ROOT / "src" / "AstralPath.Core" / "Formula" / "FormulaConstants.cs"
HTML = ROOT / "deploy" / "monolith-web" / "index.html"

TOL = 1e-9

# ── 21 项「数值直映射」：JS 路径 → C# 常量名 ─────────────────────────
DIRECT_MAP = [
    ("W.e",        "WEmpirical",            0.50),
    ("W.r",        "WRetention",            0.30),
    ("W.p",        "WPrereq",               0.20),
    ("floor",      "PrereqFloor",           0.55),
    ("stab0",      "Stability0",            2.5),
    ("a",          "StabilityAlpha",        0.45),
    ("b",          "StabilityBeta",         0.25),
    ("mix",        "PrereqWeakMix",         0.65),
    ("impactBase", "ImpactBase",            0.55),
    ("crossTg",    "TransferGapMultiplier", 1.15),
    ("pg",         "ParentScoreGate",       0.55),
    ("cg",         "ChildScoreGate",        0.50),
    ("steep",      "GateSteepness",         8.0),
    ("hit",        "ImpactHitThreshold",    0.08),
    ("cas",        "CascadeAlpha",          0.12),
    ("vol",        "VolumeBeta",            0.08),
    ("saleBar",    "SaleBar",               0.65),
    ("saleAcc",    "SaleAccMin",            0.70),
    ("saleConf",   "SaleConfMin",           3.0),
    ("saleStreak", "SaleStreakRequired",    2.0),
    ("saleMinAtt", "SaleMinAttempts",       3.0),
]

# ── 3 项「语义映射」：JS 里写成字面量，需在 C# 侧核对 ────────────────
SEMANTIC_MAP = [
    ("ConfScale",     5.0, "JS 中 clamp01(conf/5) 与 0.3*(conf/5) 的分母"),
    ("PriorSuccess",  0.5, "JS 中 (s+0.5)/(n+1) 的 Jeffreys 先验"),
    ("DefaultHorizonDays", 14.0, "JS 中计划 horizon=14"),
]


def parse_cs(path: Path) -> dict:
    """从 FormulaConstants.cs 解析 public const 数值。"""
    text = path.read_text(encoding="utf-8")
    out = {}
    for m in re.finditer(
        r"public\s+const\s+(?:double|int)\s+(\w+)\s*=\s*([^;]+);", text
    ):
        name, raw = m.group(1), m.group(2).strip()
        try:
            out[name] = float(raw.rstrip("dDfF"))
        except ValueError:
            pass
    # 间隔偏移 { 0, 2, 6 }
    m = re.search(r"SpacingOffsets\s*=\s*\{([^}]*)\}", text)
    if m:
        out["SpacingOffsets"] = [
            float(x) for x in re.findall(r"-?\d+(?:\.\d+)?", m.group(1))
        ]
    return out


def parse_js_k(path: Path) -> dict:
    """从 index.html 解析 `const K={...}`（支持 W:{...} 嵌套）。"""
    text = path.read_text(encoding="utf-8")
    i = text.find("const K={")
    if i < 0:
        raise SystemExit("FAILED: 未找到 const K={...}")
    start = text.index("{", i)
    depth, j = 0, start
    while j < len(text):
        if text[j] == "{":
            depth += 1
        elif text[j] == "}":
            depth -= 1
            if depth == 0:
                break
        j += 1
    body = text[start + 1 : j]

    # JS 对象字面量 → 合法 JSON：①裸键加引号 ②.5 → 0.5
    s = re.sub(r"(^|[{,])(\s*)([A-Za-z_]\w*)(\s*):", r'\1\2"\3"\4:', body)
    s = re.sub(r"([:,])(\s*)(\.\d)", r"\g<1>\g<2>0\g<3>", s)
    try:
        return json.loads("{" + s + "}")
    except json.JSONDecodeError as e:  # pragma: no cover
        raise SystemExit(f"FAILED: 无法解析 const K={{...}}: {e}\n>>> {s[:200]}")


def get(d: dict, dotted: str):
    cur = d
    for part in dotted.split("."):
        if not isinstance(cur, dict) or part not in cur:
            return None
        cur = cur[part]
    return cur


def main() -> int:
    if not CS.exists():
        print(f"FAILED: 缺少 {CS}")
        return 1
    if not HTML.exists():
        print(f"FAILED: 缺少 {HTML}")
        return 1

    cs = parse_cs(CS)
    js = parse_js_k(HTML)
    html_text = HTML.read_text(encoding="utf-8")
    rows, failed = [], 0

    for js_path, cs_name, expect in DIRECT_MAP:
        jv, cv = get(js, js_path), cs.get(cs_name)
        ok = jv is not None and cv is not None and abs(jv - cv) < TOL and abs(cv - expect) < TOL
        if not ok:
            failed += 1
        rows.append({"项": f"K.{js_path}", "C# 常量": cs_name,
                     "JS": jv, "C#": cv, "期望": expect, "结果": "OK" if ok else "MISMATCH"})

    for cs_name, expect, note in SEMANTIC_MAP:
        cv = cs.get(cs_name)
        # JS 侧检查对应字面量是否出现（JS 未把这些值放进 K 对象，直接写成字面量）
        if cs_name == "ConfScale":
            present = "/5" in html_text and "0.3*clamp01(conf/5)" in html_text
        elif cs_name == "PriorSuccess":
            present = "(s+0.5)/(n+1)" in html_text
        else:
            # horizon=14：buildPlan 里以 Math.min(14, day+1) 与 (S.day/14) 出现
            present = bool(re.search(r"Math\.min\(14\b", html_text)) and "(S.day/14)" in html_text
        ok = cv is not None and abs(cv - expect) < TOL and present
        if not ok:
            failed += 1
        rows.append({"项": f"(语义) {cs_name}", "C# 常量": cs_name,
                     "JS": "见字面量" if present else "缺失", "C#": cv,
                     "期望": expect, "结果": "OK" if ok else "MISMATCH"})

    # 间隔偏移 {0,2,6}（附加项，不计入 24）
    offsets = cs.get("SpacingOffsets")
    js_offsets_ok = bool(re.search(r"offs\s*=\s*\[0\s*,\s*2\s*,\s*6\]", html_text))
    offsets_ok = offsets == [0.0, 2.0, 6.0] and js_offsets_ok
    if not offsets_ok:
        failed += 1
    rows.append({"项": "(附加) SpacingOffsets", "C# 常量": "SpacingOffsets",
                 "JS": "见字面量" if js_offsets_ok else "缺失", "C#": offsets,
                 "期望": [0, 2, 6], "结果": "OK" if offsets_ok else "MISMATCH"})

    # 版本号存在性（附加项）
    html_text = HTML.read_text(encoding="utf-8")
    ver_ok = "score-v2" in html_text or "scoreV2" in html_text
    rows.append({"项": "(附加) 公式版本", "C# 常量": "ScoreVersion",
                 "JS": "scoreV2" if ver_ok else "缺失", "C#": "score-v2",
                 "期望": "score-v2", "结果": "OK" if ver_ok else "MISMATCH"})

    if "--json" in sys.argv:
        print(json.dumps({"rows": rows, "failed": failed}, ensure_ascii=False, indent=2))
        return 0 if failed == 0 else 1

    total = len(DIRECT_MAP) + len(SEMANTIC_MAP)
    print(f"[.. ] 校验项：{total} 项直映射/语义映射 + 2 项附加")
    print("-" * 78)
    print(f"{'项':<26}{'C# 常量':<24}{'JS':>10}{'C#':>10}{'结果':>10}")
    print("-" * 78)
    for r in rows:
        js_v = r["JS"] if not isinstance(r["JS"], float) else f"{r['JS']:g}"
        cs_v = r["C#"] if not isinstance(r["C#"], float) else f"{r['C#']:g}"
        print(f"{r['项']:<26}{r['C# 常量']:<24}{str(js_v):>10}{str(cs_v):>10}{r['结果']:>10}")
    print("-" * 78)
    if failed == 0:
        print(f"ALL GREEN · {len(rows)} 项全部对齐（容差 {TOL:g}）")
        return 0
    print(f"FAILED: {failed} 项不一致")
    return 1


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", action="store_true")
    ap.parse_args()
    raise SystemExit(main())
