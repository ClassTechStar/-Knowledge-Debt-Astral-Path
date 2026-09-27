# -*- coding: utf-8 -*-
"""
P0 评估口径：以 B（项目 OCR）为底 + 内容分区 CER
- recall_B：B 有多少字符能在 A 找到（内容完整度）
- precision_B：A 有多少字符来自 B（噪声）
- cer_section：正文/图注/公式 三区差异
"""
from __future__ import annotations

import re
from collections import Counter

WORD = re.compile(r"[A-Za-z0-9一-鿿]")
MATH = re.compile(r"[←-⇿∀-⋿⌀-⏿⁰-₟½-×÷≠≤≥±∞∑∏√∫∂∆∇∈∉⊂⊃∪∩∧∨¬⇒⇔α-ωΑ-Ω]")


def chars(s):
    return Counter(WORD.findall(s or ""))


def cer_b_based(ta: str, tb: str):
    """以 B 为真值近似：miss = B 有 A 无；extra = A 有 B 无。"""
    ca, cb = chars(ta), chars(tb)
    b_total = sum(cb.values()) or 1
    miss = sum((cb - ca).values())
    extra = sum((ca - cb).values())
    recall = 1 - miss / b_total
    a_total = sum(ca.values()) or 1
    precision = 1 - extra / a_total
    return {
        "miss": miss,
        "extra": extra,
        "recall_b": round(recall, 4),
        "precision_b": round(precision, 4),
        "f1": round(2 * recall * precision / max(recall + precision, 1e-9), 4),
    }


def split_sections(text: str):
    """粗分区：公式行 / 短图注 / 正文。"""
    math_lines, caption, body = [], [], []
    for ln in (text or "").split("\n"):
        s = ln.strip()
        if not s:
            continue
        math_score = len(MATH.findall(s)) / max(len(s), 1)
        if math_score > 0.18:
            math_lines.append(s)
        elif len(s) <= 40 and re.search(r"图|表|Figure|Table|来源|注[:：]", s):
            caption.append(s)
        else:
            body.append(s)
    return {"body": "\n".join(body), "caption": "\n".join(caption), "math": "\n".join(math_lines)}


def section_cer(ta: str, tb: str):
    sa, sb = split_sections(ta), split_sections(tb)
    out = {}
    for k in ("body", "caption", "math"):
        out[k] = cer_b_based(sa[k], sb[k])
    return out


def summarize_page(pairs):
    """pairs: [(text_a, text_b), ...]"""
    agg = {"miss": 0, "extra": 0, "a": 0, "b": 0}
    secs = {"body": [0, 0], "caption": [0, 0], "math": [0, 0]}
    for ta, tb in pairs:
        m = cer_b_based(ta, tb)
        agg["miss"] += m["miss"]
        agg["extra"] += m["extra"]
        agg["a"] += sum(chars(ta).values())
        agg["b"] += sum(chars(tb).values())
        sc = section_cer(ta, tb)
        for k in secs:
            secs[k][0] += sc[k]["miss"]
            secs[k][1] += sc[k]["extra"]
    recall = 1 - agg["miss"] / max(agg["b"], 1)
    precision = 1 - agg["extra"] / max(agg["a"], 1)
    return {
        "chars_a": agg["a"],
        "chars_b": agg["b"],
        "miss": agg["miss"],
        "extra": agg["extra"],
        "recall_b": round(recall, 4),
        "precision_b": round(precision, 4),
        "f1": round(2 * recall * precision / max(recall + precision, 1e-9), 4),
        "sections": {k: {"miss": v[0], "extra": v[1]} for k, v in secs.items()},
    }
