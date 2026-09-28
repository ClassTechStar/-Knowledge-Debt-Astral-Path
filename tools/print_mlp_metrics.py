# -*- coding: utf-8 -*-
import json
from pathlib import Path
p = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\docs\mlp-acceptance-2026-09-28.json")
r = json.loads(p.read_text(encoding="utf-8"))
print("OCR rows:")
for row in r["ocr"]["rows"]:
    print(f"  {row['name'][:32]:32} ok={row.get('ok')} chars={row.get('chars')} pages={row.get('pages')} ocr_pages={row.get('ocr_pages')} t={row.get('elapsed_s')}")
print("KG rows:")
for row in r["kg"]["rows"]:
    print(f"  {row['name'][:32]:32} ok={row.get('ok')} nodes={row.get('nodes')} edges={row.get('edges')} t={row.get('elapsed_s')}")
print("agent", r["agent"])
print("mlp_pass", r["mlp_pass"])
