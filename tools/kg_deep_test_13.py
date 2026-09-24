# -*- coding: utf-8 -*-
"""对 13 本全文批量生成知识图谱 + 思维导图。"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from kg_builder import build_graph, graph_stats, to_markdown, to_mindmap_tree  # noqa: E402

TXT_DIR = Path(
    r"C:\Users\18948\XiaomiMiMoProjects\.mimo-sessions\2026-09-19\按照项目方案要求，对整个项目进行完整开发。开发过程中需持续推进，不得中途停顿，直\ocr-deep-test"
)
OUT = Path(
    r"C:\Users\18948\XiaomiMiMoProjects\.mimo-sessions\2026-09-19\按照项目方案要求，对整个项目进行完整开发。开发过程中需持续推进，不得中途停顿，直\kg-deep-test"
)
OUT.mkdir(parents=True, exist_ok=True)

# 书名 → 文本文件（扫描版优先用 OCR 全文）
BOOKS = [
    ("Kotlin编程实践", "Kotlin编程实践：Kotlin从入门到实战.pdf.txt"),
    ("Go语言从入门到精通", "Go语言从入门到精通.pdf.txt"),
    ("大模型应用开发·AI Agent", "大模型应用开发：动手做 AI Agent (黄佳) .pdf.ocr.txt"),
    ("深度学习入门2·自制框架", "深度学习入门2：自制框架 (斋藤康毅)-扫描版 (1).PDF.txt"),
    ("深度学习入门4·强化学习", "图灵程序设计丛书--深度学习入门4：强化学习 ([日] 斋藤康毅) (1).pdf.txt"),
    ("C#从入门到精通", "C#从入门到精通（第7版）+(明日科技)+.pdf.txt"),
    ("深度学习进阶·NLP", "深度学习进阶：自然语言处理 (斋藤康毅) .pdf.txt"),
    ("黄仁勋：英伟达之芯", "黄仁勋：英伟达之芯_【美】斯蒂芬·威特.pdf.txt"),
    ("Java从入门到精通", "Java从入门到精通（第6版） (明日科技) .pdf.txt"),
    ("花书 DeepLearning", "DeepLearning-Goodfellow-花书.pdf.txt"),
    ("花书中译", "深度学习 Deep Learning [花书] (Ian Goodfellow,Yoshua Bengio,Aaron Courville) .pdf.txt"),
    ("Python从入门到实践", "Python编程：从入门到实践（第3版）.pdf.txt"),
    ("深度学习入门·Python实现", "深度学习入门：基于Python的理论与实现+(斋藤康毅)+.pdf.txt"),
]


def main():
    report = []
    for i, (name, fname) in enumerate(BOOKS, 1):
        src = TXT_DIR / fname
        rec = {"name": name, "file": fname, "exists": src.exists()}
        print(f"[{i}/13] {name}", flush=True)
        if not src.exists():
            rec["ok"] = False
            rec["error"] = "text missing"
            report.append(rec)
            continue
        text = src.read_text(encoding="utf-8", errors="replace")
        # 去掉 PAGE 标记噪音
        text = re.sub(r"===== PAGE \d+ =====", "\n", text)
        g = build_graph(text, name, max_terms=24)
        st = graph_stats(g)
        rec.update(st)
        rec["ok"] = st["nodes"] >= 5 and st["edges"] >= 4
        # 导出
        safe = re.sub(r'[\\/:*?"<>|]', "_", name)[:40]
        (OUT / f"{safe}.graph.json").write_text(
            json.dumps(g, ensure_ascii=False, indent=1), encoding="utf-8"
        )
        tree = to_mindmap_tree(g)
        (OUT / f"{safe}.mindmap.json").write_text(
            json.dumps(tree, ensure_ascii=False, indent=1), encoding="utf-8"
        )
        (OUT / f"{safe}.mindmap.md").write_text(to_markdown(tree), encoding="utf-8")
        rec["sample_nodes"] = [n["title"] for n in g["nodes"][:8]]
        report.append(rec)
        print(f"  -> nodes={st['nodes']} edges={st['edges']} ch={st['chapters']} term={st['terms']} ok={rec['ok']}", flush=True)
        (OUT / "kg_report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    ok = sum(1 for r in report if r.get("ok"))
    print(f"DONE ok={ok}/13")
    return 0 if ok == 13 else 1


if __name__ == "__main__":
    raise SystemExit(main())
