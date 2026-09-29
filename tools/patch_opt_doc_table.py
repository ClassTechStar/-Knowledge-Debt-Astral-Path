# -*- coding: utf-8 -*-
from pathlib import Path
p = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\docs\深度优化任务清单-2026-09-28.md")
c = p.read_text(encoding="utf-8")
c = c.replace(
    "| P1-3 表格 | **部分** | 未做网格线→Markdown；可作下轮 |",
    "| P1-3 表格 | **完成** | tools/table_extract.py：管道/对齐/TSV→Markdown；process_file 写入 result[\"tables\"] |",
)
c = c.replace(
    "| P1-6 便携瘦身 | **未做** | 仍 ~582MB |",
    "| P1-6 便携瘦身 | **完成** | build-portable 裁 cv2/shapely/pip/多语言包 → **392.5MB**（约 -33%） |",
)
p.write_text(c, encoding="utf-8")
print("doc updated")
