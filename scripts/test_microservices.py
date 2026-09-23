#!/usr/bin/env python3
"""实机测试十个扩展服务：启动/核心功能/边界/性能，并生成报告。"""
import json
import subprocess
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path")
DOTNET = r"C:\Program Files\dotnet\dotnet.exe"
REPORT = ROOT / "deploy" / "微服务实机测试报告.md"

SVCS = [
    ("concept-diffusion-svc", 8081, "/v1/diffusion/simulate", '{"sourceKp":"K01","alpha":0.55,"maxDepth":6}'),
    ("exam-impact-svc", 8082, "/v1/exams/e1/impact", '{"studentId":"demo-student-a","daysToExam":7}'),
    ("peer-cohort-svc", 8083, "/v1/cohorts/stats", '{"courseCode":"ACCOUNTING","kpIds":["K01"]}'),
    ("study-group-svc", 8084, "/v1/study-groups/match", '{"members":[{"studentId":"demo-student-a"},{"studentId":"demo-student-b"}],"size":2}'),
    ("micro-lesson-svc", 8085, "/v1/micro-lessons/draft", '{"kpId":"K01","minutes":5}'),
    ("learning-velocity-svc", 8086, "/v1/velocity/fit", '{"studentId":"demo-student-a","series":[{"at":"2026-09-01T08:00:00Z","score":40},{"at":"2026-09-03T08:00:00Z","score":55},{"at":"2026-09-05T08:00:00Z","score":62},{"at":"2026-09-08T08:00:00Z","score":70},{"at":"2026-09-12T08:00:00Z","score":78}]}'),
    ("spaced-review-svc", 8087, "/v1/spaced-review/schedule", '{"kpIds":["K01"],"days":14}'),
    ("prerequisite-simulator-svc", 8088, "/v1/prereq-simulator/simulate", '{"sourceKp":"K01","targetKp":"K02","boostKp":"K03"}'),
    ("knowledge-forecast-svc", 8089, "/v1/forecast/student", '{"studentId":"demo-student-a","horizonDays":30}'),
    ("lab-bench-svc", 8090, "/internal/v1/lab/experiments", '{"name":"score-w","title":"score-w","owner":"demo"}'),
]


def req(method: str, url: str, body: str | None = None, timeout: float = 8.0):
    data = body.encode() if body is not None else None
    r = urllib.request.Request(url, data=data, method=method)
    r.add_header("Content-Type", "application/json")
    t0 = time.perf_counter()
    try:
        with urllib.request.urlopen(r, timeout=timeout) as resp:
            raw = resp.read()
            ms = int((time.perf_counter() - t0) * 1000)
            return resp.status, ms, raw
    except urllib.error.HTTPError as e:
        ms = int((time.perf_counter() - t0) * 1000)
        return e.code, ms, e.read() if e.fp else b""
    except Exception as e:
        ms = int((time.perf_counter() - t0) * 1000)
        return 0, ms, str(e).encode()


def start_svc(name: str, port: int):
    dll = ROOT / "services" / name / "bin" / "Release" / "net10.0" / f"{name}.dll"
    if not dll.exists():
        return None
    env = None
    import os
    env = os.environ.copy()
    env["ASPNETCORE_URLS"] = f"http://127.0.0.1:{port}"
    env["ASPNETCORE_ENVIRONMENT"] = "Production"
    return subprocess.Popen(
        [DOTNET, str(dll)],
        cwd=str(dll.parent),
        env=env,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )


rows = []
for name, port, path, body in SVCS:
    issues = []
    proc = start_svc(name, port)
    start = "OK" if proc else "FAIL"
    if not proc:
        rows.append(dict(name=name, port=port, start=start, health="-", core="-", empty="-", iso="-", mh=0, mc=0, issues=["dll missing or start fail"]))
        continue
    time.sleep(2.8)

    st, mh, raw = req("GET", f"http://127.0.0.1:{port}/health/ready")
    health = "OK" if st == 200 else f"FAIL{st}"
    if st != 200:
        issues.append(f"health {st}")

    st, mc, raw = req("POST", f"http://127.0.0.1:{port}{path}", body)
    core = f"OK{st}" if 200 <= st < 300 else f"FAIL{st}"
    if not (200 <= st < 300):
        issues.append(f"core {st} {raw[:80]!r}")
    if mc > 3000:
        issues.append(f"slow core {mc}ms")

    st, _, _ = req("POST", f"http://127.0.0.1:{port}{path}", "{}")
    empty = str(st)
    if st >= 500:
        issues.append(f"empty body 5xx {st}")

    st, _, _ = req("POST", f"http://127.0.0.1:{port}/v1/velocity/fit", "{}")
    iso = str(st)
    if st == 200 and name != "learning-velocity-svc":
        issues.append("route leak")

    try:
        proc.kill()
        proc.wait(timeout=3)
    except Exception:
        pass

    rows.append(dict(name=name, port=port, start=start, health=health, core=core, empty=empty, iso=iso, mh=mh, mc=mc, issues=issues))

ok = sum(1 for r in rows if not r["issues"] and r["core"].startswith("OK"))
lines = [
    "# 微服务实机测试报告",
    "",
    "产品：知债：星穹学途（Knowledge Debt: Astral Path）",
    "",
    f"日期：{time.strftime('%Y-%m-%d %H:%M')}",
    "",
    f"**总评：完全通过 {ok} / 10**",
    "",
    "测试项：1) 服务启动 2) 核心功能 3) 边界/异常 4) 响应时间(ms)",
    "",
    "| 服务 | 端口 | 启动 | 健康 | 核心 | 空body | 隔离 | 健康ms | 核心ms | 状态 |",
    "|---|---|---|---|---|---|---|---|---|---|",
]
for r in rows:
    status = "可用" if not r["issues"] and r["core"].startswith("OK") else ("部分可用" if r["health"].startswith("OK") else "不可用")
    lines.append(
        f"| {r['name']} | {r['port']} | {r['start']} | {r['health']} | {r['core']} | {r['empty']} | {r['iso']} | {r['mh']} | {r['mc']} | {status} |"
    )
    if r["issues"]:
        lines.append(f"| | | | | | | | | | 问题：{'; '.join(r['issues'])} |")

lines += ["", "## 逐服务说明"]
for r in rows:
    status = "可用" if not r["issues"] and r["core"].startswith("OK") else "需关注"
    lines += [
        f"### {r['name']}（:{r['port']}）— {status}",
        f"- 启动：{r['start']}；健康：{r['health']}（{r['mh']}ms）",
        f"- 核心：{r['core']}（{r['mc']}ms）；空 body：{r['empty']}；路由隔离：{r['iso']}",
        f"- 待修复：{('无' if not r['issues'] else '；'.join(r['issues']))}",
        "",
    ]

avg_h = sum(r["mh"] for r in rows) // max(1, len(rows))
avg_c = sum(r["mc"] for r in rows) // max(1, len(rows))
lines += ["## 性能", f"- 健康检查均值 {avg_h} ms", f"- 核心接口均值 {avg_c} ms"]

REPORT.write_text("\n".join(lines), encoding="utf-8")
print(REPORT.read_text(encoding="utf-8"))
