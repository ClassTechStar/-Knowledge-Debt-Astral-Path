# -*- coding: utf-8 -*-
from pathlib import Path

p = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\tools\ocr_pipeline.py")
c = p.read_text(encoding="utf-8")
needle = 'result["fullText"] = text  # 供 API 存章节全文'
print("found exact", needle in c)
i = c.find('result["fullText"] = text')
print("idx", i)
print(repr(c[i : i + 70]) if i >= 0 else "none")
print("has extract import", "extract_markdown_tables" in c)

if i >= 0 and "P1-3 表格抽取" not in c:
    insert = (
        "    # P1-3 表格抽取：识别区转 Markdown\n"
        "    if extract_markdown_tables is not None and text:\n"
        "        try:\n"
        "            text, tables = extract_markdown_tables(text)\n"
        "            if tables:\n"
        '                result["tables"] = tables\n'
        '                result["notes"].append("tables:%d" % len(tables))\n'
        "        except Exception as e:\n"
        '            result["notes"].append("tables_error:%s" % str(e)[:60])\n'
        "    "
    )
    c = c[:i] + insert + c[i:]
    p.write_text(c, encoding="utf-8")
    print("inserted")
else:
    print("skip insert")
