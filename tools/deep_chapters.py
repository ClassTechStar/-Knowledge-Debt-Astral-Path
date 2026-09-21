#!/usr/bin/env python3
"""知债：星穹学途 — 深度章节解析：完整目录 + 章节正文 + 按章出题。

供 ocr_pipeline 调用。目标：
1. 从全文提取尽可能完整的目录（章/节/小节）
2. 按目录切分章节正文
3. 为每一章自动出 2–4 道题
"""
from __future__ import annotations

import hashlib
import re
from typing import Any

CN_NUM = {
    "一": 1, "二": 2, "三": 3, "四": 4, "五": 5,
    "六": 6, "七": 7, "八": 8, "九": 9, "十": 10,
    "十一": 11, "十二": 12, "十三": 13, "十四": 14, "十五": 15,
    "十六": 16, "十七": 17, "十八": 18, "十九": 19, "二十": 20,
    "二十一": 21, "二十二": 22, "二十三": 23, "二十四": 24, "二十五": 25,
    "三十": 30, "四十": 40,
}

CHAPTER_RES = [
    re.compile(r"(第\s*([0-9]+|[一二三四五六七八九十百零]+)\s*章\s*[^\n]{0,60})"),
    re.compile(r"(Chapter\s+(\d+)\s+[^\n]{0,50})", re.I),
    re.compile(r"(第\s*([0-9]+|[一二三四五六七八九十百零]+)\s*部分\s*[^\n]{0,40})"),
]

SECTION_RES = [
    re.compile(r"(?m)^\s*(\d{1,2}(?:\.\d{1,2}){0,2})\s+([^\n]{2,48})$"),
    re.compile(r"(\d{1,2}\.\d{1,2}(?:\.\d{1,2})?)\s+([^\n]{2,48})"),
]

NOISE = re.compile(
    r"(版权|出版社|印刷|开本|书号|ISBN|CIP|定价|字数|版次|印次|www\.|http|"
    r"责任编辑|封面设计|校对|经销|发行|版权所有|邮电|新华书店|图书在版)",
    re.I,
)

STOP_Q = {"的", "了", "和", "与", "在", "是", "有", "对", "中", "为", "等", "及", "或", "被"}


def _clean_title(t: str) -> str:
    t = re.sub(r"\s+", " ", (t or "").strip())
    t = re.sub(r"[.·…\s]+\d{1,4}$", "", t)
    t = re.split(r"[。；;：:，,]|另外|包括|其中|介绍|讲解了|重点介绍", t)[0]
    t = re.sub(r"\s+", " ", t).strip()
    return t[:48]


def _is_noise(t: str) -> bool:
    s = (t or "").strip()
    if len(s) < 2:
        return True
    if NOISE.search(s):
        return True
    if re.search(r"[\.\…·]{3,}", s):
        return True
    if re.match(r"^[\d\s\.、，,。①②③ⅠⅡⅢ]+$", s):
        return True
    han = len(re.findall(r"[一-鿿]", s))
    latin = len(re.findall(r"[A-Za-z]", s))
    if han == 0 and latin + len(re.findall(r"\d", s)) >= 1:
        if not re.search(r"(Go|Java|Kotlin|Python|RNN|LSTM|Agent|SQL|API|Chapter|Part)\b", s, re.I):
            return True
    m = re.match(r"^(\d{1,2}(?:\.\d{1,2}){0,2})\s*(.*)$", s)
    if m:
        rest = (m.group(2) or "").strip()
        if len(rest) < 2:
            return True
        rest_han = len(re.findall(r"[一-鿿]", rest))
        if rest_han == 0 and not re.search(r"[A-Za-z]{3,}", rest):
            return True
    return False


def _parse_ch_num(title: str) -> int | None:
    m = re.search(r"第\s*([0-9]+|[一二三四五六七八九十百零]+)\s*[章部]", title)
    if m:
        tok = m.group(1)
        return int(tok) if tok.isdigit() else CN_NUM.get(tok)
    m = re.search(r"Chapter\s+(\d+)", title, re.I)
    return int(m.group(1)) if m else None


def _sec_parent(code: str) -> int | None:
    try:
        return int(code.split(".")[0])
    except Exception:
        return None


def extract_full_toc(text: str, max_chapters: int = 80, max_sections: int = 240) -> dict[str, list[dict]]:
    """尽可能完整地抽出章/节目录。"""
    chapters: list[dict] = []
    sections: list[dict] = []
    seen_c: set[str] = set()
    seen_s: set[str] = set()

    for rx in CHAPTER_RES:
        for m in rx.finditer(text):
            raw = _clean_title(m.group(1))
            if _is_noise(raw):
                continue
            num = _parse_ch_num(raw)
            key = f"c:{num if num is not None else raw[:24]}"
            # 同一章可能在目录和正文各出现一次：全部记录 offset，正文优先
            if key not in seen_c:
                seen_c.add(key)
                chapters.append({
                    "id": "",
                    "title": raw[:48],
                    "kind": "chapter",
                    "level": 0,
                    "chapterNum": num,
                    "offset": m.start(),
                    "offsets": [m.start()],
                    "parentId": None,
                })
            else:
                for c in chapters:
                    ck = f"c:{c['chapterNum'] if c.get('chapterNum') is not None else c['title'][:24]}"
                    if ck == key:
                        c.setdefault("offsets", [c["offset"]]).append(m.start())
                        break

    for rx in SECTION_RES:
        for m in rx.finditer(text):
            code = m.group(1)
            name = _clean_title(f"{code} {m.group(2)}")
            if _is_noise(name) or name in seen_s:
                continue
            key = f"s:{code}"
            if key in seen_s:
                continue
            seen_s.add(key)
            depth = min(code.count(".") + 1, 3)
            sections.append({
                "id": "",
                "title": name[:48],
                "kind": "section" if depth == 1 else "subsection",
                "level": depth,
                "chapterNum": _sec_parent(code),
                "sectionCode": code,
                "offset": m.start(),
                "parentId": None,
            })

    chapters.sort(key=lambda x: (x["chapterNum"] if x["chapterNum"] is not None else 999, x["offset"]))
    sections.sort(key=lambda x: (x.get("chapterNum") or 999, x["offset"], x.get("sectionCode") or ""))
    chapters = chapters[:max_chapters]
    sections = sections[:max_sections]

    # 分配稳定 id
    for i, ch in enumerate(chapters, start=1):
        ch["id"] = f"CH{i:03d}"
    ch_by_num = {c["chapterNum"]: c["id"] for c in chapters if c["chapterNum"] is not None}
    for i, sec in enumerate(sections, start=1):
        sec["id"] = f"SEC{i:03d}"
        pid = ch_by_num.get(sec.get("chapterNum"))
        if pid is None and chapters:
            # 就近挂靠
            best = None
            best_off = -1
            for c in chapters:
                if c["offset"] <= sec["offset"] and c["offset"] >= best_off:
                    best_off = c["offset"]
                    best = c["id"]
            pid = best or chapters[0]["id"]
        sec["parentId"] = pid
    return {"chapters": chapters, "sections": sections}


def _pick_body_offset(text: str, offsets: list[int], title: str) -> int:
    """目录里也会出现章标题；正文区通常在更后面且后续文字更长、更像散文。"""
    if not offsets:
        return 0
    if len(offsets) == 1:
        return offsets[0]
    best = offsets[-1]
    best_score = -1.0
    for off in offsets:
        window = text[off: off + 800]
        # 目录特征：短行、多个「第N章/数字.」
        toc_like = len(re.findall(r"第\s*[0-9一二三四五六七八九十]+\s*章|\d+\.\d+", window[:400]))
        prose = len(re.findall(r"[一-鿿]", window))
        score = prose - toc_like * 40
        if off == offsets[0]:
            score -= 80  # 首次出现多为目录
        if score > best_score:
            best_score = score
            best = off
    return best


def split_chapter_bodies(text: str, chapters: list[dict], max_chars_per_chapter: int = 8000) -> list[dict]:
    """按目录 offset 切分章节正文（优先正文区出现位置）。"""
    if not chapters:
        return [{
            "id": "CH001",
            "title": "全文",
            "kind": "chapter",
            "level": 0,
            "parentId": None,
            "offset": 0,
            "charCount": len(text),
            "content": text[:max_chars_per_chapter],
        }]
    work = []
    for ch in chapters:
        c = dict(ch)
        offs = c.get("offsets") or [c.get("offset", 0)]
        c["offset"] = _pick_body_offset(text, offs, c.get("title", ""))
        work.append(c)
    ordered = sorted(work, key=lambda c: c["offset"])
    bodies: list[dict] = []
    n = len(ordered)
    for i, ch in enumerate(ordered):
        start = ch["offset"]
        end = ordered[i + 1]["offset"] if i + 1 < n else min(len(text), start + max_chars_per_chapter)
        if end <= start:
            end = min(len(text), start + max_chars_per_chapter)
        body = text[start:end].strip()
        if len(body) < 80:
            body = text[start: min(len(text), start + max_chars_per_chapter)].strip()
        bodies.append({
            "id": ch["id"],
            "title": ch["title"],
            "kind": ch.get("kind", "chapter"),
            "level": ch.get("level", 0),
            "parentId": ch.get("parentId"),
            "chapterNum": ch.get("chapterNum"),
            "offset": start,
            "charCount": len(body),
            "content": body[:max_chars_per_chapter],
        })
    return bodies


def _key_sentences(content: str, limit: int = 6) -> list[str]:
    sents: list[str] = []
    for raw in re.split(r"[。！？!?\n]", content):
        s = raw.strip()
        s = re.sub(r"^\[page[^\]]*\]\s*", "", s)
        if 12 <= len(s) <= 90:
            if re.search(r"[一-鿿]{4,}|[A-Za-z]{3,}", s):
                sents.append(s[:90])
        if len(sents) >= limit:
            break
    return sents


def _key_terms(content: str, limit: int = 8) -> list[str]:
    counts: dict[str, int] = {}
    for m in re.finditer(r"([A-Za-z_][A-Za-z0-9_+#\.]{2,24}|[一-鿿]{2,8})", content):
        t = m.group(1)
        if t in STOP_Q or t.isdigit():
            continue
        if re.fullmatch(r"[a-z]{1,3}", t):
            continue
        counts[t] = counts.get(t, 0) + 1
    ranked = sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))
    return [t for t, c in ranked if c >= 1][:limit]


def generate_chapter_questions(
    chapter_title: str,
    content: str,
    material_title: str,
    n: int = 3,
) -> list[dict]:
    """根据章节标题+正文自动出题。"""
    sents = _key_sentences(content)
    terms = _key_terms(content)
    qs: list[dict] = []

    def qid(stem: str) -> str:
        h = hashlib.sha256(f"{material_title}|{chapter_title}|{stem}".encode("utf-8")).hexdigest()
        return f"cq-{h[:12]}"

    # 1) 概念定位题
    if sents:
        fact = sents[0]
        qs.append({
            "id": qid(f"concept|{chapter_title}|{fact}"),
            "type": "concept",
            "difficulty": 2,
            "estMin": 6,
            "kp": chapter_title[:24],
            "stem": f"《{material_title[:18]}》「{chapter_title[:20]}」中提到：{fact[:40]}… 下列理解更合理的是？",
            "options": [
                "这是本章要建立的核心概念/方法，应先理解定义再练题",
                "可以完全跳过，不影响后续章节",
                "与本章主题无关的附录信息",
                "只需要死记结论，不必理解过程",
            ],
            "correctIndex": 0,
            "why": f"本章「{chapter_title[:18]}」的主干内容需优先掌握",
        })
    else:
        qs.append({
            "id": qid(f"concept-empty|{chapter_title}"),
            "type": "concept",
            "difficulty": 2,
            "estMin": 6,
            "kp": chapter_title[:24],
            "stem": f"开始学习「{chapter_title[:22]}」时，更有效的顺序是？",
            "options": [
                "先看目录与定义，再对照例子复述一遍",
                "直接刷难题不看定义",
                "只背公式编号",
                "等考前再集中看",
            ],
            "correctIndex": 0,
            "why": f"建立「{chapter_title[:16]}」框架",
        })

    # 2) 术语关联题
    if len(terms) >= 2:
        t0, t1 = terms[0], terms[1]
        others = terms[2:5] + ["与本主题无关的概念", "完全独立的操作"]
        while len(others) < 3:
            others.append("无关选项")
        qs.append({
            "id": qid(f"term|{t0}|{t1}"),
            "type": "quiz",
            "difficulty": 3,
            "estMin": 7,
            "kp": t0[:20],
            "stem": f"在「{chapter_title[:18]}」中，概念「{t0}」最常与下列哪项一起出现？",
            "options": [t1, others[0], others[1], others[2]],
            "correctIndex": 0,
            "why": f"本章关键词：{t0} ↔ {t1}",
        })

    # 3) 应用/方法题
    focus = terms[0] if terms else chapter_title[:16]
    qs.append({
        "id": qid(f"drill|{focus}"),
        "type": "drill",
        "difficulty": 2 + (len(qs) % 2),
        "estMin": 6,
        "kp": focus[:20],
        "stem": f"复习「{focus}」所在章节时，更有效的做法是？",
        "options": [
            "先明确本章定义与例子，再完成 2–3 道小练习",
            "只看标题不读正文",
            "跳过例子直接背结论",
            "把整章抄一遍即可",
        ],
        "correctIndex": 0,
        "why": f"针对《{material_title[:14]}》「{chapter_title[:16]}」",
    })

    # 4) 若正文有明确句子，做判断/选择强化
    if len(sents) >= 2 and len(qs) < n + 1:
        s = sents[1]
        qs.append({
            "id": qid(f"detail|{s}"),
            "type": "quiz",
            "difficulty": 3,
            "estMin": 5,
            "kp": chapter_title[:20],
            "stem": f"关于「{chapter_title[:16]}」，下列哪项与教材表述更接近？",
            "options": [
                s[:60],
                "教材明确否定该说法，本章不涉及",
                "这是其他章节的内容，与本章无关",
                "教材要求完全忽略该主题",
            ],
            "correctIndex": 0,
            "why": "来自本章正文细节",
        })

    return qs[: max(2, n + 1)]


def build_deep_chapter_payload(
    text: str,
    material_title: str,
    questions_per_chapter: int = 3,
) -> dict[str, Any]:
    """完整目录 + 章节正文 + 每章题目。"""
    toc = extract_full_toc(text)
    chapters = toc["chapters"]
    sections = toc["sections"]

    # 若目录几乎抽不到，用文件名兜底
    if len(chapters) == 0:
        chapters = [{
            "id": "CH001",
            "title": _clean_title(material_title)[:40] or "全文",
            "kind": "chapter",
            "level": 0,
            "chapterNum": 1,
            "offset": 0,
            "parentId": None,
        }]

    bodies = split_chapter_bodies(text, chapters)
    for b in bodies:
        b["questions"] = generate_chapter_questions(
            b["title"], b["content"], material_title, n=questions_per_chapter
        )

    # 小节也挂正文摘要 + 1–2 题（侧边栏可展开）
    sec_out: list[dict] = []
    for sec in sections:
        start = sec["offset"]
        # 小节正文：到下一小节/章节
        nxt = len(text)
        for other in sections:
            if other["offset"] > start:
                nxt = min(nxt, other["offset"])
        for ch in chapters:
            if ch["offset"] > start:
                nxt = min(nxt, ch["offset"])
        body = text[start:nxt].strip()[:2500]
        if len(body) < 20:
            body = f"小节「{sec['title']}」位于本教材中，建议结合所属章节复习。"
        q = generate_chapter_questions(sec["title"], body, material_title, n=1)
        sec_out.append({
            **sec,
            "charCount": len(body),
            "content": body[:2500],
            "questions": q[:1],
        })

    total_q = sum(len(b["questions"]) for b in bodies) + sum(len(s["questions"]) for s in sec_out)
    return {
        "toc": {
            "chapters": [{k: c[k] for k in ("id", "title", "kind", "level", "chapterNum", "parentId", "offset")} for c in chapters],
            "sections": [{k: s[k] for k in ("id", "title", "kind", "level", "chapterNum", "parentId", "sectionCode", "offset")} for s in sections],
        },
        "chapterBodies": bodies,
        "sectionBodies": sec_out,
        "stats": {
            "chapterCount": len(bodies),
            "sectionCount": len(sec_out),
            "questionCount": total_q,
            "textChars": len(text),
        },
    }
