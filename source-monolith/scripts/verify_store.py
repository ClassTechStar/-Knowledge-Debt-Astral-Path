# -*- coding: utf-8 -*-
"""shared/store.js 分片存储门禁（方案 P6）
============================================
两层断言：
  1) 行为层：scripts/store_runner.js 的 9 条黄金用例（ST-01…ST-09）全过；
  2) 接入层：index.html 必须真的调用 APStore
     （read/write/clear 至少各出现一次）——防止「模块写好了但页面没接上」。

用法：python scripts/verify_store.py
退出码：0 = 全绿；1 = 用例失败或未接入
"""
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RUNNER = ROOT / "scripts" / "store_runner.js"
STORE = ROOT / "shared" / "store.js"
INDEX = ROOT / "index.html"

REQUIRED_CALLS = ["APStore.read", "APStore.write", "APStore.clear"]


def run_harness():
    proc = subprocess.run(["node", str(RUNNER)], capture_output=True, text=True, encoding="utf-8")
    if proc.returncode != 0:
        print("[STORE] 黄金用例脚本执行失败：", (proc.stderr or "")[:400])
        return None
    try:
        return json.loads(proc.stdout)
    except json.JSONDecodeError as e:
        print("[STORE] 输出不是合法 JSON：", e)
        return None


def main():
    if not STORE.exists():
        print("[STORE] shared/store.js 缺失")
        sys.exit(1)

    data = run_harness()
    if data is None:
        sys.exit(1)

    failed = []
    for r in data.get("results", []):
        mark = "PASS" if r["ok"] else "FAIL"
        print(f"  [{mark}] {r['id']} {r['name']}" + (f" — {r['detail']}" if r["ok"] else f" — {r['detail']}"))
        if not r["ok"]:
            failed.append(r["id"])
    if failed:
        print(f"FAILED: 分片存储黄金用例未过（{', '.join(failed)}）")
        sys.exit(1)
    print(f"ALL GREEN · store 黄金用例 {data['passed']}/{data['passed'] + data['failed']}")

    # 接入层：index.html 必须真的用上 APStore
    src = INDEX.read_text(encoding="utf-8") if INDEX.exists() else ""
    missing = [c for c in REQUIRED_CALLS if c not in src]
    if missing:
        print(f"[STORE-WIRE] index.html 缺少调用：{', '.join(missing)}")
        print("FAILED: shared/store.js 未接入页面（load/save/wipe 仍走旧单键路径）")
        sys.exit(1)
    print("ALL GREEN · index.html 已接入 APStore（" + ", ".join(REQUIRED_CALLS) + "）")


if __name__ == "__main__":
    main()
