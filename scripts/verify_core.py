# -*- coding: utf-8 -*-
"""shared/core.js 行为等价门禁（方案 P2 的「双跑对比」灰度策略）
============================================================
把收编前的旧实现（index.html 内联 / p0 / p1 / p2）与新内核 shared/core.js
在**同一组用例**上双跑对拍，输出不一致即判红。这样抽取期间任何行为漂移都会
在合并前暴露，而不是等浏览器冒烟才发现。

用法：python scripts/verify_core.py
退出码：0 = 全部等价；1 = 出现漂移
"""
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

SOURCES = {
    "index.html": (ROOT / "deploy" / "monolith-web" / "index.html",
                   {"normalize": "normalize", "parseJsonLoose": "parseJsonLoose", "removeCyclesTopo": "removeCyclesTopo"}),
    "p0": (ROOT / "shared" / "integrations" / "p0-ai-integration.js", {"jsonLoose": "parseJsonLoose"}),
    "p1": (ROOT / "shared" / "integrations" / "p1-ai-integration.js", {"removeCyclesTopo": "removeCyclesTopo"}),
    "p2": (ROOT / "shared" / "integrations" / "p2-ai-integration.js", {"removeCyclesTopo": "removeCyclesTopo"}),
}
CORE = ROOT / "shared" / "core.js"

# ── 用例（graph 用例只含**端点齐备**的边：graph-packs 门禁已保证无悬空边）──
FIXTURES = {
    "normalize": [
        "  ＡＢＣ　 Ｄ ",
        "知识债 ＫＰ ",
        "",
        "Hello   World",
        "ＱＵＩＺ－１　测试",
        None,
    ],
    "parseJsonLoose": [
        '```json\n{"a":1}\n```',
        '前缀{"b":2}后缀',
        "[1,2,3]",
        "not json at all",
        "",
        '```\n{"c":[1,{"d":true}]}\n```',
        'noise [ {"e":5} ] tail',
    ],
    # (nodes, edges)
    "removeCyclesTopo": [
        ([{"id": "a"}, {"id": "b"}, {"id": "c"}], [{"from": "a", "to": "b"}, {"from": "b", "to": "c"}]),
        ([{"id": "a"}, {"id": "b"}, {"id": "c"}], [{"from": "a", "to": "b"}, {"from": "b", "to": "c"}, {"from": "c", "to": "a"}]),
        ([{"id": "a"}, {"id": "b"}], [{"from": "a", "to": "a"}, {"from": "a", "to": "b"}]),
        ([{"id": "a"}], []),
        ([], []),
        ([{"id": "a"}, {"id": "b"}, {"id": "c"}, {"id": "d"}],
         [{"from": "a", "to": "b"}, {"from": "b", "to": "c"}, {"from": "c", "to": "b"}, {"from": "c", "to": "d"}]),
        ([{"id": "x"}, {"id": "y"}, {"id": "z"}],
         [{"from": "x", "to": "y", "w": 1.2}, {"from": "y", "to": "z", "w": 0.8}, {"from": "z", "to": "x", "w": 2.0}]),
    ],
}


def extract_function(src, name):
    """从源码中按括号配对抽出 `function name(...)  { ... }` 完整文本。"""
    m = re.search(r"function\s+" + re.escape(name) + r"\s*\(", src)
    if not m:
        return None
    i = src.index("{", m.end() - 1)
    depth, j = 0, i
    while j < len(src):
        if src[j] == "{":
            depth += 1
        elif src[j] == "}":
            depth -= 1
            if depth == 0:
                return src[m.start(): j + 1]
        j += 1
    return None


def build_harness():
    """拼一个 Node 脚本：把各方实现改名后同用例双跑，输出 JSON。"""
    parts = ["'use strict';", "const REG = {};"]
    for tag, (path, mapping) in SOURCES.items():
        src = path.read_text(encoding="utf-8")
        for local, canon in mapping.items():
            fn = extract_function(src, local)
            if fn is None:
                parts.append(f"// MISSING {tag}.{local}")
                continue
            alias = tag.replace(".", "_") + "__" + local
            parts.append(re.sub(r"function\s+" + re.escape(local) + r"\s*\(", f"function {alias}(", fn, count=1))
            parts.append(f'REG[{json.dumps(alias)}] = {alias};')
    core_src = CORE.read_text(encoding="utf-8")
    for canon in ["normalize", "parseJsonLoose", "removeCyclesTopo"]:
        fn = extract_function(core_src, canon)
        if fn is None:
            parts.append(f"// MISSING core.{canon}")
            continue
        parts.append(re.sub(r"function\s+" + re.escape(canon) + r"\s*\(", f"function core__{canon}(", fn, count=1))
        parts.append(f'REG["core__{canon}"] = core__{canon};')

    fixtures_json = json.dumps(FIXTURES, ensure_ascii=False)
    parts.append(f"const F = {fixtures_json};")
    parts.append("""
const out = {};
function run(fn, kind, arg) {
  try {
    if (kind === 'removeCyclesTopo') { const r = fn(arg[0], arg[1]); return { edges: r.edges, cut: r.cut }; }
    return fn(arg);
  } catch (e) { return { __error: String(e && e.message || e) }; }
}
for (const [kind, cases] of Object.entries(F)) {
  out[kind] = {};
  for (const [name, fn] of Object.entries(REG)) {
    const canon = name.split('__')[1];
    if (canon !== kind && !(kind === 'parseJsonLoose' && canon === 'jsonLoose')) continue;
    out[kind][name] = cases.map((c) => JSON.parse(JSON.stringify(run(fn, kind, c))));
  }
}
process.stdout.write(JSON.stringify(out));
""")
    return "\n".join(parts)


def main():
    harness = build_harness()
    tmp = ROOT / "scripts" / "_core_equiv.tmp.js"
    tmp.write_text(harness, encoding="utf-8")
    try:
        proc = subprocess.run(["node", str(tmp)], capture_output=True, text=True, encoding="utf-8")
        if proc.returncode != 0:
            print("[CORE-DRIFT] 双跑脚本执行失败：", proc.stderr.strip()[:400])
            sys.exit(1)
        data = json.loads(proc.stdout)
    finally:
        tmp.unlink(missing_ok=True)

    failed = []
    checked = 0
    for kind, impls in data.items():
        base_name = "core__" + ("parseJsonLoose" if kind == "parseJsonLoose" else kind)
        base = impls.get(base_name)
        if base is None:
            failed.append(f"shared/core.js 缺少 {kind} 实现，无法作为基准")
            continue
        for name, got in impls.items():
            if name == base_name:
                continue
            checked += 1
            if got != base:
                for i, (a, b) in enumerate(zip(got, base)):
                    if a != b:
                        failed.append(f"{name} 与 {base_name} 在 {kind} 用例 #{i} 不一致：{a} != {b}")
                        break
    if failed:
        print("[CORE-DRIFT]")
        for m in failed:
            print("  - " + m)
        print(f"FAILED: shared/core.js 与旧实现不等价（{len(failed)} 项）")
        sys.exit(1)
    print(f"ALL GREEN · shared/core.js 与 {checked} 份旧实现在 "
          f"{sum(len(v) for v in FIXTURES.values())} 个用例上逐一对拍等价")


if __name__ == "__main__":
    main()
