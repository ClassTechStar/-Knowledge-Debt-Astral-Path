# -*- coding: utf-8 -*-
"""P1-3 表格抽取：从 OCR/文本层识别表格块并转 Markdown。

识别三类：
1. 管道/竖线表（含 OCR 竖线）
2. 空白对齐多列（≥3 行、≥2 列，列起点对齐）
3. 制表符 TSV

输出 Markdown 表；无法可靠识别的行保持原文（不丢字）。
"""
from __future__ import annotations

import re
from typing import List, Tuple

# 连续 ≥2 空白或 tab 作为列分隔
_SPLIT = re.compile(r"\t|\s{2,}")
_PIPE = re.compile(r"\s*\|\s*")


def _is_border_line(s: str) -> bool:
    t = s.strip()
    return bool(t) and set(t) <= set("-=+|: ") and len(t) >= 4


def _cells(line: str) -> List[str]:
    """统一取单元格列表（去空首尾）。"""
    s = line.rstrip()
    if s.count("|") >= 2:
        body = s.strip()
        if body.startswith("|"):
            body = body[1:]
        if body.endswith("|"):
            body = body[:-1]
        return [c.strip() for c in body.split("|")]
    # 多空白 / tab
    parts = [c.strip() for c in re.split(r"\t|\s{2,}", s.strip())]
    return [p for p in parts if p != ""]


def _col_starts(line: str) -> List[int]:
    starts = []
    if "\t" in line:
        pos = 0
        for cell in line.split("\t"):
            starts.append(pos)
            pos += len(cell) + 1
        return starts
    for m in re.finditer(r"\S+", line):
        starts.append(m.start())
    return starts


def _aligned_block(lines: List[str], start: int) -> Tuple[int, List[List[str]]]:
    """从 start 起收集对齐多列行，返回 (结束下一行, 行单元格列表)。"""
    rows: List[List[str]] = []
    i = start
    while i < len(lines):
        ln = lines[i]
        if not ln.strip() or _is_border_line(ln):
            break
        cells = _cells(ln)
        if len(cells) < 2:
            break
        # 与已收集行列数一致或 ±1
        if rows and abs(len(cells) - len(rows[0])) > 1:
            break
        rows.append(cells)
        i += 1
        if len(rows) >= 40:  # 防超长误合并
            break
    return i, rows


def _to_markdown(rows: List[List[str]]) -> str:
    if not rows:
        return ""
    ncol = max(len(r) for r in rows)
    norm = [r + [""] * (ncol - len(r)) for r in rows]
    # 首行作表头
    head = norm[0]
    body = norm[1:]
    def esc(c: str) -> str:
        return c.replace("|", "\\|").replace("\n", " ").strip()
    lines = ["| " + " | ".join(esc(c) for c in head) + " |",
             "| " + " | ".join("---" for _ in head) + " |"]
    for r in body:
        lines.append("| " + " | ".join(esc(c) for c in r) + " |")
    return "\n".join(lines)


def extract_markdown_tables(text: str) -> Tuple[str, List[dict]]:
    """扫描全文，把表格区转为 Markdown，返回 (新文本, 表格元数据列表)。

    元数据：{index, startLine, endLine, rows, cols, kind}
    """
    if not text:
        return text, []
    lines = text.splitlines()
    out_lines: List[str] = []
    tables: List[dict] = []
    i = 0
    tidx = 0
    while i < len(lines):
        ln = lines[i]
        # ① 管道表：连续含 | 的行（允许 --- 分隔行）
        if ln.count("|") >= 2 and not _is_border_line(ln):
            j = i
            block: List[str] = []
            while j < len(lines):
                s = lines[j]
                if s.count("|") >= 2 or _is_border_line(s):
                    block.append(s)
                    j += 1
                    continue
                break
            rows = []
            for s in block:
                if _is_border_line(s):
                    continue
                rows.append(_cells(s))
            rows = [r for r in rows if any(c.strip() for c in r)]
            if len(rows) >= 2 and all(len(r) >= 2 for r in rows):
                md = _to_markdown(rows)
                tables.append({
                    "index": tidx, "startLine": i, "endLine": j,
                    "rows": len(rows), "cols": max(len(r) for r in rows),
                    "kind": "pipe",
                })
                tidx += 1
                out_lines.append(md)
                i = j
                continue
        # ② 空白对齐多列（≥3 行）
        if "\t" in ln or re.search(r"\S\s{2,}\S", ln):
            j, rows = _aligned_block(lines, i)
            if len(rows) >= 3 and all(len(r) >= 2 for r in rows):
                # 列数稳定
                ncol = [len(r) for r in rows]
                if max(ncol) - min(ncol) <= 1:
                    md = _to_markdown(rows)
                    tables.append({
                        "index": tidx, "startLine": i, "endLine": j,
                        "rows": len(rows), "cols": max(ncol),
                        "kind": "tab" if "\t" in ln else "aligned",
                    })
                    tidx += 1
                    out_lines.append(md)
                    i = j
                    continue
        out_lines.append(ln)
        i += 1

    if not tables:
        return text, []
    return "\n".join(out_lines), tables


def format_tables_note(tables: List[dict]) -> str:
    if not tables:
        return ""
    return "tables:%d" % len(tables)
