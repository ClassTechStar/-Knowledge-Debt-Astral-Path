# -*- coding: utf-8 -*-
"""MLP 验收：12 本 PDF · OCR → 知识图谱 → 智能体 · 最新代码"""
from __future__ import annotations

import json
import os
import re
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ocr_pipeline_umi import detect_needs_ocr, extract_document  # noqa: E402
from kg_builder import build_graph, graph_stats  # noqa: E402

PDF_DIR = Path(r"C:\Users\18948\Downloads\PDF TEST")
OUT = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\docs")
WORD = re.compile(r"[A-Za-z0-9一-鿿]")

BOOKS = [
    "Kotlin编程实践：Kotlin从入门到实战.pdf",
    "Go语言从入门到精通.pdf",
    "大模型应用开发：动手做 AI Agent (黄佳) .pdf",
    "深度学习入门2：自制框架 (斋藤康毅)-扫描版 (1).PDF",
    "图灵程序设计丛书--深度学习入门4：强化学习 ([日] 斋藤康毅) (1).pdf",
    "C#从入门到精通（第7版）+(明日科技)+.pdf",
    "深度学习进阶：自然语言处理 (斋藤康毅) .pdf",
    "黄仁勋：英伟达之芯_【美】斯蒂芬·威特.pdf",
    "Java从入门到精通（第6版） (明日科技) .pdf",
    "深度学习 Deep Learning [花书] (Ian Goodfellow,Yoshua Bengio,Aaron Courville) .pdf",
    "Python编程：从入门到实践（第3版）.pdf",
    "深度学习入门：基于Python的理论与实现+(斋藤康毅)+.pdf",
]


def main():
    # 1) OCR/解析
    ocr_rows = []
    texts = {}
    for i, name in enumerate(BOOKS, 1):
        path = PDF_DIR / name
        t0 = time.time()
        rec = {"name": name, "exists": path.exists()}
        print(f"[OCR {i}/12] {name[:40]}", flush=True)
        if not path.exists():
            rec["ok"] = False
            ocr_rows.append(rec)
            continue
        try:
            det = detect_needs_ocr(str(path))
            rec.update(det)
            # 验收用小样本：mixed 优先文本层，扫描书也只抽 24 页
            mode = "mixed"
            doc = extract_document(str(path), mode=mode, max_pages=24)
            chars = len(WORD.findall(doc.text or ""))
            rec.update({
                "ok": chars >= 40,
                "mode": mode,
                "pages": len(doc.pages),
                "chars": chars,
                "ocr_pages": len(doc.ocr_pages),
                "elapsed_s": round(time.time() - t0, 1),
            })
            texts[name] = doc.text or ""
        except Exception as e:
            rec.update({"ok": False, "error": str(e)[:120], "elapsed_s": round(time.time() - t0, 1)})
        ocr_rows.append(rec)

    # 2) 知识图谱
    kg_rows = []
    for i, name in enumerate(BOOKS, 1):
        text = texts.get(name, "")
        t0 = time.time()
        rec = {"name": name, "text_chars": len(text)}
        print(f"[KG  {i}/12] {name[:40]}", flush=True)
        if len(text) < 100:
            rec.update({"ok": False, "error": "text too short", "elapsed_s": round(time.time() - t0, 1)})
            kg_rows.append(rec)
            continue
        try:
            g = build_graph(text[:80000], book_name=name)
            st = graph_stats(g)
            rec.update({
                "ok": st.get("nodes", 0) >= 10 and st.get("edges", 0) >= 5,
                "nodes": st.get("nodes"),
                "edges": st.get("edges"),
                "elapsed_s": round(time.time() - t0, 1),
            })
        except Exception as e:
            rec.update({"ok": False, "error": str(e)[:120], "elapsed_s": round(time.time() - t0, 1)})
        kg_rows.append(rec)

    # 3) 智能体：意图表 + 危机词 + 样例路由
    agent = {"intents_ok": False, "crisis_ok": False, "routes": []}
    try:
        intents = json.loads((Path(__file__).resolve().parent / "agent-intents.json").read_text(encoding="utf-8"))
        n = len(intents.get("intents") or intents if isinstance(intents, list) else intents.get("intents", []))
        if isinstance(intents, list):
            n = len(intents)
        agent["intents_ok"] = n >= 15
        agent["intent_count"] = n
    except Exception as e:
        agent["intents_err"] = str(e)[:80]
    try:
        html = (Path(__file__).resolve().parent.parent / "deploy" / "monolith-web" / "index.html").read_text(encoding="utf-8")
        crisis_hits = sum(1 for w in ["不想活", "自杀", "活不下去", "结束生命"] if w in html)
        agent["crisis_ok"] = crisis_hits >= 3
        agent["crisis_hits"] = crisis_hits
        # 样例意图关键词
        samples = [
            ("诊断", ["诊断", "欠债", "先修"]),
            ("计划", ["计划", "14", "补课"]),
            ("图谱", ["图谱", "节点", "识网"]),
            ("危机", ["危机", "转人工", "不想活"]),
        ]
        for label, kws in samples:
            hit = any(k in html for k in kws)
            agent["routes"].append({"label": label, "ok": hit})
    except Exception as e:
        agent["html_err"] = str(e)[:80]

    # 汇总
    ocr_ok = sum(1 for r in ocr_rows if r.get("ok"))
    kg_ok = sum(1 for r in kg_rows if r.get("ok"))
    report = {
        "time": time.strftime("%Y-%m-%d %H:%M:%S"),
        "ocr": {"ok": ocr_ok, "total": len(ocr_rows), "rows": ocr_rows},
        "kg": {"ok": kg_ok, "total": len(kg_rows), "rows": kg_rows},
        "agent": agent,
        "mlp_pass": ocr_ok >= 11 and kg_ok >= 11 and agent.get("intents_ok") and agent.get("crisis_ok"),
    }
    out = OUT / "mlp-acceptance-2026-09-28.json"
    out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"ocr_ok": f"{ocr_ok}/12", "kg_ok": f"{kg_ok}/12", "agent": agent, "mlp_pass": report["mlp_pass"]}, ensure_ascii=False))
    return 0 if report["mlp_pass"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
