# -*- coding: utf-8 -*-
"""XSS 收口门禁（方案 P4）
============================================================
两部分：
  ① 黄金用例：contracts/golden/xss-cases.json → shared/safe-dom.js 的 safeRich/esc
     必须阻断事件注入、协议注入、mutation XSS，同时不误伤白名单富文本。
  ② 源码哨兵：主 HTML 与 shared/integrations 的 innerHTML 直插点，若 RHS 出现
     「模型输出 / 用户内容」变量而未包裹 esc()/safeRich()/setHTML() 则判红。

用法：python scripts/verify_xss.py
退出码：0 = ALL GREEN；1 = 存在未收口注入点
"""
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CASES = ROOT / "contracts" / "golden" / "xss-cases.json"
RUNNER = ROOT / "scripts" / "xss_runner.js"

SCAN_TARGETS = [
    ROOT / "deploy" / "monolith-web" / "index.html",
    ROOT / "shared" / "integrations" / "p0-ai-integration.js",
    ROOT / "shared" / "integrations" / "p1-ai-integration.js",
    ROOT / "shared" / "integrations" / "p2-ai-integration.js",
]

# 已知承载「模型输出 / 用户内容」的属性/变量（只取属性引用，避免 acc/logs 之类
# 数值变量造成的假红；流式 acc 的写入点在下面 GUARD 覆盖）
SINK = re.compile(
    r"(\.stem|\.explain|\.text|\.title|\.name|\.label|\.fromName|\.toName"
    r"|reply|aiText|_aiExplain)"
)
# 已收口的转义出口
GUARD = re.compile(r"(esc\(|safeRich\(|setHTML\(|setText\(|sanitizeText\(|APSafe\.)")

# C3 回归哨兵（与 sync-monolith-html.ps1 同款）
C3_SENTINEL = re.compile(r"\.map\(title\)")

# 拼接标记字符串的证据（'<div' / "<span" 之类字面量）
MARKUP = re.compile(r"['\"]<[a-zA-Z/!]")

failed = []


def check_cases():
    if not CASES.exists():
        failed.append(f"缺少黄金用例 {CASES}")
        return 0
    if not RUNNER.exists():
        failed.append(f"缺少驱动 {RUNNER}")
        return 0
    proc = subprocess.run(
        ["node", str(RUNNER), str(CASES)],
        capture_output=True, text=True, encoding="utf-8", cwd=str(ROOT),
    )
    if proc.returncode != 0:
        failed.append(f"xss_runner 执行失败：{proc.stderr.strip()[:200]}")
        return 0
    results = json.loads(proc.stdout)
    doc = json.loads(CASES.read_text(encoding="utf-8"))
    by_id = {c["id"]: c for c in doc["cases"]}
    for r in results:
        c = by_id[r["id"]]
        out = r["safeRich"]
        low = out.lower()
        for bad in c.get("must_not_contain", []):
            if bad.lower() in low:
                failed.append(f"{r['id']} {r['name']}：safeRich 输出仍含 {bad!r} → {out!r}")
        for good in c.get("must_contain", []):
            if good not in out:
                failed.append(f"{r['id']} {r['name']}：safeRich 输出缺少 {good!r} → {out!r}")
        # 协议级：显式声明的用例必须被 sanitizeUrl 拦下
        bad_proto = c.get("url_must_not_start_with")
        if bad_proto and r["url"].lower().startswith(bad_proto.lower()):
            failed.append(f"{r['id']} {r['name']}：sanitizeUrl 未拦截 {bad_proto} → {r['url']!r}")
    return len(results)


JS_KEYWORDS = {
    "if", "for", "while", "function", "return", "var", "let", "const", "new",
    "String", "Number", "Math", "Object", "Array", "JSON", "Date", "true",
    "false", "null", "undefined", "document", "window", "this", "type", "of",
    "in", "do", "else", "try", "catch", "throw", "typeof", "instanceof", "class",
}


def js_identifiers(expr):
    return {m for m in re.findall(r"[A-Za-z_$][\w$]*", expr) if m not in JS_KEYWORDS}


def assignment_block(lines, start_idx, ident):
    """从 start_idx 行起，截取 `ident = …` 这条语句（按括号深度与分号结束）。"""
    depth = 0
    buf = []
    for j in range(start_idx, min(start_idx + 60, len(lines))):
        ln = lines[j]
        buf.append((j + 1, ln))
        depth += ln.count("(") + ln.count("[") + ln.count("{")
        depth -= ln.count(")") + ln.count("]") + ln.count("}")
        if depth <= 0 and ln.rstrip().endswith((";", "}", ",")):
            break
    return buf


def check_sources():
    """两层扫描：① 行内直插（单语句）；② 变量赋值回溯（跨行构造再插入）。"""
    hits = 0
    for f in SCAN_TARGETS:
        if not f.exists():
            continue
        src = f.read_text(encoding="utf-8")
        lines = src.split("\n")
        rel = f.relative_to(ROOT).as_posix()

        # 变量 → 赋值语句行号区间
        assigns = {}
        for i, ln in enumerate(lines):
            m = re.search(r"(?:var|let|const)?\s*([A-Za-z_$][\w$]*)\s*=\s*", ln)
            if m and not ln.strip().startswith("//"):
                assigns.setdefault(m.group(1), []).append(i)

        reported = set()
        for i, ln in enumerate(lines, 1):
            if C3_SENTINEL.search(ln):
                failed.append(f"{rel} L{i}: C3 回归哨兵命中（未转义的 .map(title)）")
                hits += 1
                continue
            m = re.search(r"\.(innerHTML|insertAdjacentHTML)\s*(\+?=|\()([^\n]*)", ln)
            if not m:
                continue
            rhs = m.group(3)
            if GUARD.search(ln):
                continue
            # ① 行内：RHS 直接出现未转义的动态内容
            if SINK.search(rhs):
                failed.append(f"{rel} L{i}: innerHTML 直插疑似未转义的动态内容 → {ln.strip()[:160]}")
                hits += 1
                continue
            # ② 回溯：RHS 引用的变量，其**邻近**赋值语句是否在拼接标记字符串时
            #    混入了未转义的动态内容。三重收敛条件，避免跨作用域跟随同名变量
            #    （i/b/n/data 之类通用名在全文件范围内会产生大量假红）：
            #      a) 赋值点必须在该 innerHTML 行上方 WINDOW 行内（同一局部作用域）
            #      b) 赋值块必须确实在拼接标记（出现 '<tag' 字面量）
            #      c) 块内出现承载模型/用户内容的属性且本行无转义出口
            WINDOW = 25
            for ident in js_identifiers(rhs):
                for st in assigns.get(ident, []):
                    if not (i - 1 - WINDOW) <= st < (i - 1):
                        continue
                    block = assignment_block(lines, st, ident)
                    if not any(MARKUP.search(bl) for _l, bl in block):
                        continue
                    for (lno, bl) in block:
                        if not SINK.search(bl) or GUARD.search(bl):
                            continue
                        key = (rel, lno)
                        if key in reported:
                            continue
                        reported.add(key)
                        failed.append(
                            f"{rel} L{lno}: 变量 {ident} 的标记拼接未转义动态内容（经 L{i} 的 innerHTML 插入）→ {bl.strip()[:150]}"
                        )
                        hits += 1
    return hits


def main():
    n_cases = check_cases()
    n_hits = check_sources()
    if failed:
        print("[XSS-DRIFT]")
        for m in failed:
            print("  - " + m)
        print(f"FAILED: xss 门禁未收口（{len(failed)} 项）")
        sys.exit(1)
    print(f"ALL GREEN · xss-cases {n_cases} 例全通过，直插点哨兵 {len(SCAN_TARGETS)} 文件无未转义注入")


if __name__ == "__main__":
    main()
