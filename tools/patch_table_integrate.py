# -*- coding: utf-8 -*-
from pathlib import Path

p = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\tools\ocr_pipeline.py")
c = p.read_text(encoding="utf-8")

if "table_extract" not in c:
    if "import unicodedata" in c:
        c = c.replace(
            "import unicodedata",
            "import unicodedata\n"
            "sys.path.insert(0, str(Path(__file__).resolve().parent))\n"
            "try:\n"
            "    from table_extract import extract_markdown_tables\n"
            "except Exception:\n"
            "    extract_markdown_tables = None  # type: ignore",
            1,
        )
        print("import added")
    else:
        print("no unicodedata anchor")

old = '        result["fullText"] = text  # 供 API 存章节全文'
new = '''        # P1-3 表格抽取：识别区转 Markdown，元数据入 result["tables"]
        if extract_markdown_tables is not None and text:
            try:
                text, tables = extract_markdown_tables(text)
                if tables:
                    result["tables"] = tables
                    notes.append("tables:%d" % len(tables))
            except Exception as e:
                notes.append("tables_error:%s" % str(e)[:60])
        result["fullText"] = text  # 供 API 存章节全文'''
if old in c:
    c = c.replace(old, new, 1)
    print("process_file tables")
else:
    print("process_file anchor miss")
    i = c.find("fullText")
    print(repr(c[i:i + 80]) if i >= 0 else "no fullText")

p.write_text(c, encoding="utf-8")
print("len", len(c))
