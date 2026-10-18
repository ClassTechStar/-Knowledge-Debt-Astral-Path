# -*- coding: utf-8 -*-
"""空 catch 门禁（方案 P13）
============================================================
不变量：任何 `catch` 块都不得为空——空吞异常会让线上故障只能靠猜。

判定口径（只认「完全空」与「仅注释」，不做语义分析，避免误伤）：
  FAIL  catch { }
  FAIL  catch (Exception) { /* 忽略 */ }
  PASS  catch (Exception ex) { Diag.Recover(ex); }
  PASS  catch { return ""; }            ← 有降级动作，不是空吞

用法：python scripts/verify_no_empty_catch.py [--verbose]
退出码：0 = 无空 catch；1 = 存在空 catch
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SKIP_DIRS = {"bin", "obj", "build", "publish", "TestResults", "node_modules", ".git", "Migrations"}
VERBOSE = "--verbose" in sys.argv

# 允许白名单：诊断器自身最后一道防线（Diag.Record 内的 catch 必须吞）
ALLOW = {
    ("src/AstralPath.Core/Diagnostics/Diag.cs", None),
}


def walk(dir_path, out):
    for e in dir_path.iterdir():
        if e.is_dir():
            if e.name in SKIP_DIRS:
                continue
            walk(e, out)
        elif e.suffix == ".cs":
            out.append(e)
    return out


def empty_catches(text):
    for m in re.finditer(r"\bcatch\b", text):
        p = m.end()
        dm = re.match(r"\s*(\([^)]*\))?\s*(\bwhen\b[^{]*)?\s*\{", text[p:])
        if not dm:
            continue
        brace = p + dm.end() - 1
        depth, j = 0, brace
        while j < len(text):
            if text[j] == "{":
                depth += 1
            elif text[j] == "}":
                depth -= 1
                if depth == 0:
                    break
            j += 1
        body = text[brace + 1:j]
        if re.sub(r"//[^\n]*|/\*[\s\S]*?\*/", "", body).strip():
            continue
        yield text[:m.start()].count("\n") + 1, (dm.group(1) or "catch").strip()


files = []
for base in ["src", "tests", "services"]:
    p = ROOT / base
    if p.exists():
        walk(p, files)

bad = []
counts = {}
for f in files:
    rel = f.relative_to(ROOT).as_posix()
    raw = f.read_bytes().decode("utf-8", errors="replace")
    hits = list(empty_catches(raw))
    if not hits:
        continue
    if (rel, None) in ALLOW:
        if VERBOSE:
            print(f"[ALLOW] {rel}: {len(hits)} 处（诊断器自身）")
        continue
    counts[rel] = len(hits)
    for (line, decl) in hits:
        bad.append(f"{rel}:{line}  {decl} {{ }}")

if bad:
    print("[EMPTY-CATCH]")
    for b in bad:
        print("  - " + b)
    print(f"FAILED: 存在 {len(bad)} 处空 catch（应改为记录 + 降级，或显式 return/throw）")
    sys.exit(1)

total_files = sum(1 for f in files)
print(f"ALL GREEN · {total_files} 个 .cs 文件中无空 catch（每个 catch 都有记录或降级动作）")
