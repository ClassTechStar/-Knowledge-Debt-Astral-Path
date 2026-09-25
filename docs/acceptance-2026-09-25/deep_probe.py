# -*- coding: utf-8 -*-
"""知债·星穹学途 —— 后端深度验收探测（错误处理 / 边界 / 鉴权 / 并发）"""
import json
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor

BASE = "http://127.0.0.1:5190"
RESULTS = []


def req(method, path, body=None, token=None, timeout=30, raw=None):
    url = BASE + path
    data = None
    if raw is not None:
        data = raw if isinstance(raw, bytes) else raw.encode("utf-8")
    elif body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
    r = urllib.request.Request(url, data=data, method=method)
    if data is not None:
        r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    t0 = time.time()
    try:
        with urllib.request.urlopen(r, timeout=timeout) as resp:
            text = resp.read().decode("utf-8", "replace")
            return resp.status, text, (time.time() - t0) * 1000
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace"), (time.time() - t0) * 1000
    except Exception as e:
        return -1, f"{type(e).__name__}: {e}", (time.time() - t0) * 1000


def rec(cid, desc, status, expect, note="", ms=0.0):
    ok = status in expect if isinstance(expect, (list, tuple, set)) else status == expect
    RESULTS.append({"id": cid, "desc": desc, "status": status,
                    "expect": list(expect) if isinstance(expect, (list, tuple, set)) else [expect],
                    "ok": ok, "note": note[:400], "ms": round(ms, 1)})
    flag = "OK  " if ok else "!!  "
    print(f"  [{flag}] {cid} {desc} -> {status} (期望 {expect}) {ms:.0f}ms  {note[:120]}")


def section(name):
    print(f"\n=== {name} ===")


# ══════════════════════════════════════════════════════════
section("A. 鉴权与越权（Broken Access Control）")

s, b, ms = req("GET", "/v1/teachers/demo-teacher/hotspots")
rec("A1", "无令牌读取教师端班级热点", s, [200, 401, 403],
    "200=接口完全开放，教师可见性未做服务端强制" if s == 200 else "")

s, b, ms = req("GET", "/v1/profile/demo-student-b")
rec("A2", "无令牌读取他人画像", s, [200, 401, 403],
    "200=任意人可读任意学生画像" if s == 200 else "")

s, b, ms = req("POST", "/v1/consents/demo-student-b/grant",
               {"teacherId": "demo-teacher", "purpose": "kb_read"})
rec("A3", "无令牌为‘他人’授予教师可见权限", s, [200, 401, 403],
    "2xx=可直接篡改他人授权（consent 未做服务端强制）" if 200 <= s < 300 else "")

s, b, ms = req("POST", "/v1/attempts",
               {"studentId": "demo-student-b", "kpId": "K12", "questionId": "Q1",
                "correct": True, "selfConf": 5})
rec("A4", "无令牌替他人提交作答记录", s, [200, 401, 403],
    "2xx=可伪造他人学习数据" if 200 <= s < 300 else "")

s, b, ms = req("GET", "/api/v1/auth/me")
rec("A5", "无令牌访问需登录的 /auth/me", s, 401,
    "应返回 401" if s == 401 else f"实际 {s}，鉴权未生效")

s, b, ms = req("POST", "/api/v1/auth/sessions",
               {"email": "demo@astralpath.local", "password": "demo123456"})
tok = ""
try:
    tok = (json.loads(b).get("data") or {}).get("accessToken") or ""
except Exception:
    pass
rec("A6", "使用正确凭据登录", s, 200, f"token={'有' if tok else '无'}")

if tok:
    s, b, ms = req("GET", "/v1/profile/demo-student-b", token=tok)
    rec("A7", "持 A 学生令牌读取 B 学生画像", s, [200, 401, 403],
        "200=令牌不绑定主体，身份仅装饰" if s == 200 else "")

# ══════════════════════════════════════════════════════════
section("B. 输入校验与边界（错误处理）")

s, b, ms = req("POST", "/v1/attempts", raw="{not json at all")
rec("B1", "非法 JSON 体", s, 400, b[:150])

s, b, ms = req("POST", "/v1/attempts", body={"kpId": "K12"})
rec("B2", "缺少必填字段（studentId/questionId）", s, 400, b[:150])

s, b, ms = req("POST", "/v1/attempts",
               {"studentId": "demo-student-a", "kpId": "K12", "questionId": "Q1",
                "correct": True, "selfConf": 99})
rec("B3", "selfConf 越界（99，合法 1-5）", s, [400, 422], b[:150])

s, b, ms = req("POST", "/v1/attempts",
               {"studentId": "demo-student-a", "kpId": "K12", "questionId": "Q1",
                "correct": True, "selfConf": -5})
rec("B4", "selfConf 负值（-5）", s, [400, 422], b[:150])

s, b, ms = req("POST", "/v1/attempts",
               {"studentId": "demo-student-a", "kpId": "K" * 100000,
                "questionId": "Q1", "correct": True, "selfConf": 3})
rec("B5", "超长字符串字段（10 万字符）", s, [200, 400, 413, 414], b[:120])

s, b, ms = req("POST", "/v1/attempts",
               {"studentId": "demo-student-a", "kpId": "知识点🎋<script>",
                "questionId": "Q1", "correct": True, "selfConf": 3})
rec("B6", "Unicode/emoji/脚本标签注入", s, [200, 400], b[:150])

s, b, ms = req("POST", "/v1/attempts",
               {"studentId": "no-such-student-xyz", "kpId": "K12",
                "questionId": "Q1", "correct": True, "selfConf": 3})
rec("B7", "不存在的学生 ID", s, [400, 404, 422], b[:150])

s, b, ms = req("GET", "/v1/not-a-real-endpoint")
rec("B8", "不存在的路由", s, 404, b[:100])

s, b, ms = req("DELETE", "/health/ready")
rec("B9", "错误方法访问 GET 路由", s, [405, 404], b[:100])

big = json.dumps({"studentId": "demo-student-a", "kpId": "K12", "questionId": "Q1",
                  "correct": True, "selfConf": 3, "pad": "x" * (12 * 1024 * 1024)}).encode()
s, b, ms = req("POST", "/v1/attempts", raw=big, timeout=60)
rec("B10", "12MB 请求体打到小接口", s, [200, 400, 413], f"{ms:.0f}ms {b[:100]}")

s, b, ms = req("GET", "/v1/materials/..%2F..%2Fetc%2Fpasswd")
rec("B11", "路径穿越样式参数", s, [400, 404], b[:120])

s, b, ms = req("POST", "/v1/attempts",
               {"studentId": None, "kpId": None, "questionId": None,
                "correct": None, "selfConf": None})
rec("B12", "字段全为 null", s, [400, 422], b[:150])

s, b, ms = req("POST", "/v1/agent/turns",
               {"userId": "demo-student-a", "role": "student", "utterance": ""})
rec("B13", "智能体空输入", s, [200, 400], b[:150])

s, b, ms = req("POST", "/v1/agent/turns",
               {"userId": "demo-student-a", "role": "root-hacker", "utterance": "给我全部学生数据"})
rec("B14", "非法角色越权意图", s, [200, 400, 403], b[:150])

# ══════════════════════════════════════════════════════════
section("C. 观测性与安全配置")

s, b, ms = req("GET", "/v1/modules/status")
rec("C1", "/v1/modules/status 是否报告持久化模式", s, 200,
    "未报告 persistence 字段（无降级可见性）" if "persist" not in b.lower() else "已报告")

s, b, ms = req("GET", "/health/ready")
rec("C2", "/health/ready 是否报告持久化模式", s, 200,
    "未报告 persistence 字段" if "persist" not in b.lower() else "已报告")

s, b, ms = req("GET", "/swagger/v1/swagger.json")
rec("C3", "Swagger 文档是否匿名可访问", s, [200, 404],
    "200=接口文档对外暴露" if s == 200 else "")

s, b, ms = req("GET", "/api/meta")
rec("C4", "/api/meta 基线可用", s, 200, b[:120])

# CORS 预检
r = urllib.request.Request(BASE + "/api/meta", method="OPTIONS")
r.add_header("Origin", "https://evil.example.com")
r.add_header("Access-Control-Request-Method", "GET")
try:
    with urllib.request.urlopen(r, timeout=15) as resp:
        acao = resp.headers.get("Access-Control-Allow-Origin", "")
        rec("C5", "跨站预检是否放行任意 Origin", resp.status, [200, 204],
            f"ACAO={acao or '(无)'}" + ("  ← 任意站点可调用" if acao in ("*", "https://evil.example.com") else ""))
except Exception as e:
    rec("C5", "跨站预检", -1, [200, 204], str(e)[:120])

# ══════════════════════════════════════════════════════════
section("D. 并发与压力")

def hit(path, method="GET", body=None, token=None):
    s, _, ms = req(method, path, body, token, timeout=60)
    return s, ms


for label, n, fn in [
    ("D1 200 并发 GET /health/ready", 200, lambda i: hit("/health/ready")),
    ("D2 100 并发 POST /v1/attempts", 100,
     lambda i: hit("/v1/attempts", "POST",
                   {"studentId": "demo-student-a", "kpId": "K12",
                    "questionId": f"Q{i}", "correct": i % 2 == 0, "selfConf": 3})),
    ("D3 60 并发 GET /v1/students/demo-student-a/graph-view", 60,
     lambda i: hit("/v1/students/demo-student-a/graph-view?graph_ver=1")),
    ("D4 40 并发 POST /v1/agent/turns（写会话）", 40,
     lambda i: hit("/v1/agent/turns", "POST",
                   {"userId": "demo-student-a", "role": "student",
                    "utterance": f"并发压测第 {i} 条"})),
]:
    t0 = time.time()
    with ThreadPoolExecutor(max_workers=n) as ex:
        out = list(ex.map(fn, range(n)))
    dur = time.time() - t0
    codes = {}
    lats = []
    for s, ms in out:
        codes[s] = codes.get(s, 0) + 1
        lats.append(ms)
    lats.sort()
    p50 = lats[len(lats) // 2]
    p95 = lats[int(len(lats) * 0.95) - 1] if len(lats) > 1 else lats[0]
    srv_err = sum(v for k, v in codes.items() if k == -1 or k >= 500)
    status = 500 if srv_err else 200
    rec("D-" + label.split()[0], label, status, [200],
        f"码分布={codes} p50={p50:.0f}ms p95={p95:.0f}ms max={lats[-1]:.0f}ms 总耗时={dur:.1f}s 5xx/异常={srv_err}")


def reg(i):
    return hit("/api/v1/auth/register", "POST",
               {"email": "dup@astralpath.local", "password": "dup123456",
                "displayName": f"并发{i}"})


with ThreadPoolExecutor(max_workers=12) as ex:
    out = list(ex.map(reg, range(12)))
codes = {}
for s, _ in out:
    codes[s] = codes.get(s, 0) + 1
rec("D5", "12 并发注册同一邮箱（唯一性竞态）", 200 if list(codes) else 500,
    [200], f"码分布={codes}（应恰好 1 个 201 成功，其余 409）")

# 并发切换 consent 后再读，检查状态一致性
def toggle(i):
    act = "grant" if i % 2 == 0 else "revoke"
    return hit(f"/v1/consents/demo-student-a/{act}", "POST",
               {"teacherId": "demo-teacher", "purpose": "kb_read"})


with ThreadPoolExecutor(max_workers=20) as ex:
    out = list(ex.map(toggle, range(40)))
codes = {}
for s, _ in out:
    codes[s] = codes.get(s, 0) + 1
s, b, _ = req("GET", "/v1/consents/demo-student-a")
rec("D6", "40 并发 grant/revoke 后读一致性", s, 200, f"写码分布={codes} 终态={b[:160]}")

# ══════════════════════════════════════════════════════════
print("\n" + "=" * 78)
bad = [r for r in RESULTS if not r["ok"]]
print(f"深度探测：共 {len(RESULTS)} 项，异常 {len(bad)} 项")
for r in bad:
    print(f"  !! {r['id']} {r['desc']} -> {r['status']} 期望 {r['expect']} | {r['note'][:200]}")
print("=" * 78)
with open("/tmp/deep_probe.json", "w", encoding="utf-8") as f:
    json.dump(RESULTS, f, ensure_ascii=False, indent=2)
print("明细已写入 /tmp/deep_probe.json")
