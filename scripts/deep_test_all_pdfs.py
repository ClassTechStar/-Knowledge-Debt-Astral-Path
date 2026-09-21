#!/usr/bin/env python3
"""藏书阁 / 识网 / 知债 全量 PDF 深度测试"""
from __future__ import annotations
import json, time, traceback
from pathlib import Path
import urllib.request
import urllib.error
import uuid

BASE = "http://127.0.0.1:5190"
PDFS = [
    r"C:\Users\18948\Downloads\C#从入门到精通（第7版）+(明日科技)+.pdf",
    r"C:\Users\18948\Downloads\Java从入门到精通（第6版） (明日科技) .pdf",
    r"C:\Users\18948\Downloads\Go语言从入门到精通.pdf",
    r"C:\Users\18948\Downloads\深度学习进阶：自然语言处理 (斋藤康毅) .pdf",
    r"C:\Users\18948\Downloads\深度学习入门：基于Python的理论与实现+(斋藤康毅)+.pdf",
    r"C:\Users\18948\Downloads\深度学习入门2：自制框架 (斋藤康毅)-扫描版 (1).PDF",
    r"C:\Users\18948\Downloads\图灵程序设计丛书--深度学习入门4：强化学习 ([日] 斋藤康毅) (1).pdf",
    r"C:\Users\18948\Downloads\深度学习 Deep Learning [花书] (Ian Goodfellow,Yoshua Bengio,Aaron Courville) .pdf",
    r"C:\Users\18948\Downloads\大模型应用开发：动手做 AI Agent (黄佳) .pdf",
    r"C:\Users\18948\Downloads\Python编程：从入门到实践（第3版）.pdf",
    r"C:\Users\18948\Downloads\Kotlin编程实践：Kotlin从入门到实战.pdf",
]

issues = []

def req(method, path, data=None, headers=None, timeout=180):
    url = BASE + path
    body = None
    hdrs = headers or {}
    if data is not None:
        body = json.dumps(data).encode("utf-8")
        hdrs.setdefault("Content-Type", "application/json")
    r = urllib.request.Request(url, data=body, method=method, headers=hdrs)
    with urllib.request.urlopen(r, timeout=timeout) as resp:
        raw = resp.read()
        return json.loads(raw.decode("utf-8")) if raw else {}

def upload_pdf(path: Path, ocr="quick"):
    boundary = "----astral" + uuid.uuid4().hex
    file_bytes = path.read_bytes()
    filename = path.name
    parts = []
    parts.append(f"--{boundary}\r\nContent-Disposition: form-data; name=\"files\"; filename=\"{filename}\"\r\nContent-Type: application/pdf\r\n\r\n".encode())
    parts.append(file_bytes)
    parts.append(f"\r\n--{boundary}--\r\n".encode())
    body = b"".join(parts)
    r = urllib.request.Request(
        BASE + f"/v1/materials/upload-batch?ocr={ocr}",
        data=body,
        method="POST",
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
    )
    with urllib.request.urlopen(r, timeout=300) as resp:
        return json.loads(resp.read().decode())

def wait_parse(mid, timeout=180):
    t0 = time.time()
    while time.time() - t0 < timeout:
        j = req("GET", f"/v1/materials/{mid}")
        m = j.get("data") or {}
        if m.get("status") in ("ready", "failed"):
            return m
        time.sleep(2)
    return req("GET", f"/v1/materials/{mid}").get("data") or {}

def main():
    print("=== 1. HEALTH ===")
    h = req("GET", "/health/ready")
    print(h)
    if h.get("status") != "ready":
        raise SystemExit("api not ready")

    print("\n=== 2. UPLOAD ALL PDFS ===")
    uploaded = []
    for p in PDFS:
        path = Path(p)
        if not path.exists():
            print(f"MISS {path.name}")
            issues.append(f"pdf missing: {path.name}")
            continue
        try:
            print(f"UPLOAD {path.name} ({path.stat().st_size/1e6:.1f}MB) ...")
            j = upload_pdf(path, ocr="quick")
            items = (j.get("data") or {}).get("items") or []
            if not items:
                issues.append(f"upload empty: {path.name}")
                print("  FAIL empty")
                continue
            it = items[0]
            print(f"  id={it['id']} status={it.get('status')}")
            uploaded.append((path.name, it["id"]))
        except Exception as e:
            issues.append(f"upload error {path.name}: {e}")
            print(f"  ERR {e}")

    print(f"\nuploaded={len(uploaded)}")

    print("\n=== 3. PARSE EACH (quick OCR) ===")
    results = []
    for name, mid in uploaded:
        try:
            print(f"PARSE {name} ...")
            t0 = time.time()
            m = wait_parse(mid, timeout=240)
            dt = time.time() - t0
            status = m.get("status")
            nodes = m.get("nodeCount") or 0
            edges = m.get("edgeCount") or 0
            chars = m.get("extractedChars") or 0
            err = m.get("error") or ""
            notes = m.get("notes") or ""
            print(f"  status={status} nodes={nodes} edges={edges} chars={chars} t={dt:.1f}s notes={notes} err={err[:80]}")
            if status != "ready":
                issues.append(f"parse failed: {name} -> {err}")
            if status == "ready" and nodes < 3:
                issues.append(f"too few nodes: {name} nodes={nodes}")
            results.append((name, mid, m))
        except Exception as e:
            issues.append(f"parse error {name}: {e}")
            print(f"  ERR {e}")
            traceback.print_exc()

    print("\n=== 4. KNOWLEDGE GRAPHS ===")
    gs = req("GET", "/v1/knowledge-graphs")
    graphs = gs.get("data") or []
    print(f"graphs={len(graphs)}")
    seen_names = set()
    for g in graphs:
        gid = g.get("graphId")
        full = req("GET", f"/v1/knowledge-graphs/{gid}")
        d = full.get("data") or {}
        nodes = d.get("nodes") or []
        edges = d.get("edges") or []
        ok = d.get("ok")
        cycles = d.get("cycles") or []
        sample = [n.get("name") for n in nodes[:3]]
        trip = len(d.get("triples") or [])
        mm = d.get("mindmap") or {}
        mm_children = len(mm.get("children") or []) if isinstance(mm, dict) else 0
        outline = (d.get("markdownOutline") or "")[:80].replace("\n", " | ")
        print(f"  {g.get('materialName','')[:36]}: n={len(nodes)} e={len(edges)} ok={ok} cycles={len(cycles)} triples={trip} mmKids={mm_children}")
        print(f"      sample={sample}")
        print(f"      outline={outline}")
        name = g.get("materialName") or ""
        if name in seen_names:
            issues.append(f"duplicate graph material: {name}")
        seen_names.add(name)
        if not ok:
            issues.append(f"graph not acyclic: {name} cycles={len(cycles)}")
        if len(nodes) < 3:
            issues.append(f"graph too small: {name} nodes={len(nodes)}")
        # question banks
        mid = g.get("materialId")
        if mid:
            q = req("GET", f"/v1/materials/{mid}/textbook-questions?maxTasks=4")
            qd = q.get("data") or {}
            nq = qd.get("count") or 0
            bank = qd.get("bank")
            print(f"      bank={bank} questions={nq}")
            if nq == 0:
                issues.append(f"no textbook questions: {name}")

    print("\n=== 5. 知债 DIAGNOSE ===")
    for sid in ("demo-student-a", "demo-student-b"):
        d = req("POST", f"/v1/students/{sid}/diagnose?graph_ver=1&top_n=8")
        dd = d.get("data") or {}
        debts = dd.get("topDebts") or []
        print(f"  {sid}: debts={len(debts)}")
        for t in debts[:3]:
            print(f"    {t.get('fromKpName')}->{t.get('toKpName')} impact={t.get('impact')}")
        if sid == "demo-student-a" and len(debts) < 3:
            issues.append(f"student A should have >=3 debts, got {len(debts)}")
        if sid == "demo-student-b" and len(debts) > 0:
            issues.append(f"student B should have 0 debts, got {len(debts)}")
        gv = req("GET", f"/v1/students/{sid}/graph-view?graph_ver=1")
        gd = gv.get("data") or {}
        print(f"    graph-view nodes={len(gd.get('nodes') or [])} edges={len(gd.get('edges') or [])}")

    print("\n=== 6. PLAN / TODAY / TEACHER ===")
    plan = req("POST", "/v1/students/demo-student-a/plans", {"graphVersion": 1, "dayBudgetMin": 35, "horizonDays": 14})
    pd = plan.get("data") or {}
    days = pd.get("days") or []
    checked = pd.get("constraintsChecked")
    maxmin = max((d.get("minutes") or 0) for d in days) if days else -1
    print(f"  plan days={len(days)} checked={checked} maxMin={maxmin}")
    if not checked:
        issues.append("plan constraintsChecked=false")
    if days and maxmin > 35:
        issues.append(f"plan day exceeds 35min: {maxmin}")

    today = req("POST", "/v1/students/demo-student-a/today", {"day": 1})
    td = today.get("data") or {}
    tasks = td.get("tasks") or []
    print(f"  today tasks={len(tasks)} material={td.get('material')}")
    for t in tasks[:3]:
        print(f"    [{t.get('type')}] {t.get('kpName')} | {(t.get('stem') or '')[:50]}")
    if len(tasks) == 0:
        issues.append("today tasks empty")

    books = req("GET", "/v1/materials/today-from-books?maxTasks=6")
    bd = books.get("data") or {}
    print(f"  book-today material={bd.get('materialName')} tasks={len(bd.get('tasks') or [])} source={bd.get('source')}")

    hs = req("GET", "/v1/teachers/demo-teacher/hotspots?only_consent=true")
    hd = hs.get("data") or {}
    print(f"  teacher authorized={hd.get('authorizedCount')} hotspots={len(hd.get('hotspots') or [])}")
    if (hd.get("authorizedCount") or 0) == 0:
        issues.append("teacher authorized=0 after default seed")

    print("\n=== 7. UI HTML CHECK ===")
    import urllib.request as ur
    html = ur.urlopen(BASE + "/", timeout=10).read().decode("utf-8", "ignore")
    for marker in ["星穹学途", "藏书阁", "识网", "知债", "今日", "账户", "upload-batch", "graphSelect", "planDays"]:
        ok = marker in html
        print(f"  ui has {marker}: {ok}")
        if not ok:
            issues.append(f"UI missing marker: {marker}")

    print("\n========== ISSUES ==========")
    if not issues:
        print("NONE — all checks passed")
    else:
        for i, msg in enumerate(issues, 1):
            print(f"{i}. {msg}")
    Path(r"C:\Temp\astralpath_test_report.json").write_text(json.dumps({
        "uploaded": uploaded,
        "issues": issues,
        "graphs": len(graphs),
        "parsed": [(n, m.get("status"), m.get("nodeCount"), m.get("extractedChars")) for n, _, m in results],
    }, ensure_ascii=False, indent=2), encoding="utf-8")
    print("\nreport -> C:\\Temp\\astralpath_test_report.json")

if __name__ == "__main__":
    main()
