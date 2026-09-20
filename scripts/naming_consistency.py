#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
naming_consistency.py —— 项目命名一致性检查与修复工具

依据《技术方案》首部「项目名称规范」与《四人团队分工方案》§8.2：

| 形态     | 取值                                          | 使用场景 |
|----------|-----------------------------------------------|----------|
| 全称     | 知债：星穹学途（Knowledge Debt: Astral Path）  | 文档标题、封面、申报书、答辩材料、对外介绍 |
| 中文简称 | 知债：星穹学途                                 | 正文叙述、章节标题、图表标签 |
| 英文标识 | AstralPath                                     | 代码命名空间、解决方案名、包名、镜像名、数据库名、域名 |

禁止再出现旧名：知债图 / ZhiZhaiTu / zhizhaitu，以及由旧名派生的
ZhiZhai* 类型名、ZZ_* 环境变量、zz_* 临时前缀。

用法：
    python scripts/naming_consistency.py --check            # 只扫描（CI 门禁；残留 0 才返回 0）
    python scripts/naming_consistency.py --apply            # 就地修复（文本 + 文件改名）
    python scripts/naming_consistency.py --report out.md    # 输出 Markdown 残留扫描报告

注意：本文件与生成的报告位于 SELF_EXEMPT_NAMES，**不参与** --apply，
      因为它们需要原样保留旧名字面量作为检测依据（开发期已复现自指误报缺陷）。
"""
from __future__ import annotations

import argparse
import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# ── 规范取值（单一事实来源）───────────────────────────────────────────────
FULL_NAME = "知债：星穹学途（Knowledge Debt: Astral Path）"
SHORT_NAME = "知债：星穹学途"
ENGLISH_ID = "AstralPath"

SKIP_DIRS = {
    ".git", "bin", "obj", "node_modules", ".vs", ".idea", "__pycache__",
    "ocr-venv", "tessdata", ".mimo-sessions", "TestResults", "packages",
}
SELF_EXEMPT_NAMES = {"naming_consistency.py", "naming-residual-scan.md"}

TEXT_EXTS = {
    ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets",
    ".html", ".htm", ".js", ".css", ".json", ".md", ".py", ".sh", ".ps1", ".bat",
    ".yaml", ".yml", ".xml", ".http", ".txt", ".csv", ".env", ".toml", ".ini",
}
TEXT_NAMES = {"README", "LICENSE", "Dockerfile", "Makefile", "NuGet.Config", ".gitignore"}

LEGACY_ZH = "知债图"          # 旧中文名
LEGACY_PASCAL = "ZhiZhai"     # 旧 Pascal 前缀（ZhiZhaiTu / ZhiZhaiStore …）
LEGACY_LOWER = "zhizhaitu"    # 旧小写名

# ── 修复规则（顺序重要：先具体后通用）──────────────────────────────────────
RULES: list[tuple[str, str]] = [
    # 1) 环境变量：旧名 ZZ_* → 规范前缀 ASTRALPATH_*（C# 侧保留旧名兼容读取）
    ("ZZ_", "ASTRALPATH_"),
    # 2) 临时文件前缀 zz_tess* → astralpath_tess*
    ("zz_tess_p", "astralpath_tess_p"),
    ("zz_tess_", "astralpath_tess_"),
    # 3) 旧类型名 ZhiZhai* → AstralPath*
    ("ZhiZhaiStore", "AstralPathStore"),
    ("ZhiZhaiTu", ENGLISH_ID),
    (LEGACY_PASCAL, ENGLISH_ID),
    # 4) 界面/文档中的临时英文名（不在规范内）→ 规范表述
    ("AstralPath · GalReview UI", f"{ENGLISH_ID} · 演示台"),
    (f"{SHORT_NAME} · 藏书阁与识网", f"{FULL_NAME} · 演示台"),
    # 5) 过期项目路径引用 → 当前真实路径（正则处理，兼容正反斜杠）
    (r"C:\Users\18948\XiaomiMiMoProjects\astralpath", str(ROOT)),
    (r"C:\Users\18948\XiaomiMiMoProjects\AstralPath", str(ROOT)),
]

# ── 残留检测模式（--check 判定用；本文件需保留这些旧名字面量）───────────────
RESIDUAL_PATTERNS: list[tuple[str, str]] = [
    (f"旧中文名：{LEGACY_ZH}", LEGACY_ZH),
    (f"旧帕斯卡名：{LEGACY_PASCAL}Tu", f"{LEGACY_PASCAL}Tu"),
    (f"旧小写名：{LEGACY_LOWER}", LEGACY_LOWER),
    (f"旧派生类型名：{LEGACY_PASCAL}*", LEGACY_PASCAL),
    ("旧派生环境变量：ZZ_*", r"\bZZ_[A-Z_]+"),
    ("旧派生临时前缀：zz_tess*", r"\bzz_tess\w*"),
    ("过期路径：...\\astralpath", r"XiaomiMiMoProjects[\\/]astralpath\b"),
    ("非规范英文名：GalReview UI", r"GalReview UI"),
]


def iter_files():
    for dirpath, dirnames, filenames in os.walk(ROOT):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for fn in filenames:
            if fn in SELF_EXEMPT_NAMES:
                continue
            p = Path(dirpath) / fn
            if p.suffix.lower() in TEXT_EXTS or p.name in TEXT_NAMES:
                yield p


def read_text(p: Path) -> str | None:
    try:
        return p.read_text(encoding="utf-8")
    except (UnicodeDecodeError, PermissionError, OSError):
        return None


def scan() -> dict[str, list[tuple[str, int, str]]]:
    found: dict[str, list[tuple[str, int, str]]] = {name: [] for name, _ in RESIDUAL_PATTERNS}
    for p in iter_files():
        text = read_text(p)
        if text is None:
            continue
        lines = text.splitlines()
        for name, pat in RESIDUAL_PATTERNS:
            for m in re.finditer(pat, text):
                ln = text.count("\n", 0, m.start()) + 1
                content = lines[ln - 1].strip() if ln - 1 < len(lines) else ""
                found[name].append((str(p.relative_to(ROOT)), ln, content[:160]))
    return found


def apply_fixes() -> tuple[int, list[str]]:
    changed, renamed = 0, []
    for p in iter_files():
        text = read_text(p)
        if text is None:
            continue
        orig = text
        for a, b in RULES:
            if a.startswith("C:\\"):
                text = re.sub(a.replace("\\", r"\\"), lambda _m, rep=b: rep, text)
            elif a == "ZZ_":
                text = re.sub(r"\bZZ_", b, text)
            elif a in text:
                text = text.replace(a, b)
        if text != orig:
            p.write_text(text, encoding="utf-8", newline="")
            changed += 1
    for p in list(iter_files()):
        if LEGACY_PASCAL in p.name:
            new = p.with_name(p.name.replace(LEGACY_PASCAL, ENGLISH_ID))
            if not new.exists():
                p.rename(new)
                renamed.append(f"{p.relative_to(ROOT)} -> {new.name}")
    return changed, renamed


def write_report(found: dict[str, list[tuple[str, int, str]]], out: Path) -> int:
    total = sum(len(v) for v in found.values())
    lines = [
        "# 命名一致性残留扫描报告（P3 交付物 · 依据《分工方案》§8.2）",
        "",
        f"> 规范全称：**{FULL_NAME}**",
        f"> 规范中文简称：**{SHORT_NAME}**",
        f"> 规范英文标识：**{ENGLISH_ID}**",
        f"> 扫描根目录：`{ROOT}`",
        "> 扫描范围：文本类文件（跳过 bin/obj/.git/ocr-venv/tessdata/node_modules）",
        f"> **结论：残留总数 = {total}**（验收标准：0）",
        "",
        "| 检测项 | 残留数 | 状态 |",
        "|---|---:|---|",
    ]
    for name, _ in RESIDUAL_PATTERNS:
        n = len(found[name])
        lines.append(f"| {name} | {n} | {'✅ 通过' if n == 0 else '❌ 需修复'} |")
    lines += ["", "## 明细", ""]
    for name, _ in RESIDUAL_PATTERNS:
        items = found[name]
        if not items:
            continue
        lines += [f"### {name}（{len(items)}）", "", "| 文件 | 行 | 内容 |", "|---|---:|---|"]
        for rel, ln, content in items[:50]:
            lines.append(f"| `{rel}` | {ln} | `{content.replace('|', chr(92) + '|')}` |")
        if len(items) > 50:
            lines.append(f"| … | | 其余 {len(items) - 50} 条略 |")
        lines.append("")
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return total


def main() -> int:
    ap = argparse.ArgumentParser(description="项目命名一致性检查与修复")
    ap.add_argument("--check", action="store_true", help="只扫描并输出残留数")
    ap.add_argument("--apply", action="store_true", help="就地修复文本与文件名")
    ap.add_argument("--report", metavar="PATH", help="输出 Markdown 残留扫描报告")
    args = ap.parse_args()
    if not (args.check or args.apply or args.report):
        ap.print_help()
        return 0

    if args.apply:
        changed, renamed = apply_fixes()
        print(f"[apply] 修改文本文件 {changed} 个，改名文件 {len(renamed)} 个")
        for r in renamed:
            print(f"         {r}")

    found = scan()
    total = sum(len(v) for v in found.values())
    out = Path(args.report) if args.report else ROOT / "eval" / "naming-residual-scan.md"
    write_report(found, out)
    print(f"[scan] 残留总数 = {total} → 报告：{out.relative_to(ROOT)}")
    for name, _ in RESIDUAL_PATTERNS:
        if found[name]:
            print(f"       ❌ {name}: {len(found[name])}")
    return 0 if total == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
