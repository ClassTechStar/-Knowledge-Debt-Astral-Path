# 公式跨端行为对拍（重构评估 2.2）：JS 侧
# ============================================================
# 从 deploy/monolith-web/index.html 提取内联的 K 常量与
# scoreV2 / impactV2 / saleStep（含其依赖 helper），在 node 中按
# eval/golden/formula-cross.json 的输入求值，与 Python 生成的期望值对拍。
# C# 侧由 tests/AstralPath.Core.Tests/FormulaCrossTests.cs 断言。
# 退出码：0 = ALL GREEN；非 0 = JS 与契约漂移。
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
INDEX_HTML = ROOT / "deploy" / "monolith-web" / "index.html"
FIXTURE = ROOT / "eval" / "golden" / "formula-cross.json"

FUNCS = ["empirical", "stability", "retention", "prereqSup", "prereqCeil",
         "scoreV2", "impactV2", "saleStep"]


def fail(msg):
    print(f"[DRIFT] {msg}")
    print("FAILED: formula-cross JS 对拍不一致")
    sys.exit(1)


def extract(source, name):
    """按花括号配平提取 `function NAME(...){...}` 或单行 `const NAME=...;`。"""
    m = re.search(r"(const " + name + r"\s*=[^;]*;)", source)
    if m:
        return m.group(1)
    m = re.search(r"function " + name + r"\s*\(", source)
    if not m:
        fail(f"index.html 中找不到 {name}")
    i = m.start()
    depth = 0
    started = False
    for j in range(i, len(source)):
        ch = source[j]
        if ch == "{":
            depth += 1
            started = True
        elif ch == "}":
            depth -= 1
            if started and depth == 0:
                return source[i:j + 1]
    fail(f"{name} 花括号不配平")


def main():
    if not INDEX_HTML.exists():
        fail(f"缺少 {INDEX_HTML}")
    fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))
    tol = fixture["tolerance"]
    src = INDEX_HTML.read_text(encoding="utf-8")

    parts = ["const fs=require('fs');"]
    parts.append(extract(src, "K"))
    for name in ["sig", "clamp01"] + FUNCS:
        parts.append(extract(src, name))
    parts.append("const fx=JSON.parse(fs.readFileSync(process.argv[2],'utf8'));")
    parts.append("""
const out = {score: [], impact: [], sale: []};
for (const c of fx.score) {
  out.score.push({id: c.id, actual: scoreV2(c.att, c.ok, c.conf, c.age, c.streak, c.prereq)});
}
for (const c of fx.impact) {
  out.impact.push({id: c.id, actual: impactV2(c.sf, c.st, c.w, c.tg === 1, c.freq, c.down)});
}
for (const c of fx.sale) {
  let s = {streak: 0, att: 0};
  for (const [acc, conf] of c.steps) s = saleStep(s, acc, conf);
  out.sale.push({id: c.id, actual: s});
}
process.stdout.write(JSON.stringify(out));
""")
    js = "\n".join(parts)

    with tempfile.TemporaryDirectory() as td:
        jsPath = Path(td) / "formula_probe.js"
        jsPath.write_text(js, encoding="utf-8")
        proc = subprocess.run(["node", str(jsPath), str(FIXTURE)],
                              capture_output=True, text=True, shell=True)
        if proc.returncode != 0:
            fail(f"node 求值失败：{proc.stderr[:400]}")
        try:
            out = json.loads(proc.stdout)
        except json.JSONDecodeError:
            fail(f"node 输出不是 JSON：{proc.stdout[:200]}")

    bad = []

    def cmp(section, actual_list):
        expect = {c["id"]: c["expect"] for c in fixture[section]}
        for item in actual_list:
            e = expect[item["id"]]
            a = item["actual"]
            if isinstance(a, dict):
                if not (a["status"] == e["status"]
                        and a["streak"] == e["streak"]
                        and a["att"] == e["attempts"]):
                    bad.append(f"{item['id']}: js={a} expect={e}")
            else:
                if abs(float(a) - e) > tol:
                    bad.append(f"{item['id']}: js={a} expect={e}")

    cmp("score", out["score"])
    cmp("impact", out["impact"])
    cmp("sale", out["sale"])
    if bad:
        for b in bad:
            print(f"[DRIFT] {b}")
        print("FAILED: formula-cross JS 对拍不一致")
        sys.exit(1)
    print(f"ALL GREEN · formula-cross JS 对拍一致（score={len(out['score'])} "
          f"impact={len(out['impact'])} sale={len(out['sale'])}，tol={tol}）")


if __name__ == "__main__":
    main()
