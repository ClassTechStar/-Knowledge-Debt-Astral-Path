#!/usr/bin/env python3
"""知识图谱算法优化模块（供 ocr_pipeline 调用）。

算法要点：
1. 章节层级解析：第N章 / N.x.x 小节 → parent-child
2. 术语 TF × 位置加权，过滤停用词与噪声
3. 边生成：
   - 章节时序链（顺序先修）
   - 小节 → 所属章节（前缀匹配）
   - 术语 → 首次出现的章节/小节（就近锚定，仅单向，防环）
   - 同章术语共现（同窗口共现，边权=共现强度，仅早期→后期节点，防环）
4. DAG 收尾：拓扑排序，删除会造成环的边
5. 输出 level/layer 元数据，便于前端分层布局
"""
from __future__ import annotations

import re
from collections import defaultdict
from typing import Any

CHAPTER_NUM_RE = re.compile(r"第\s*([0-9]+|[一二三四五六七八九十百零]+)\s*章")
SECTION_NUM_RE = re.compile(r"^(\d{1,2}(?:\.\d{1,2}){0,2})")
CN_NUM = {
    "一": 1, "二": 2, "三": 3, "四": 4, "五": 5,
    "六": 6, "七": 7, "八": 8, "九": 9, "十": 10,
    "十一": 11, "十二": 12, "十三": 13, "十四": 14, "十五": 15,
    "十六": 16, "十七": 17, "十八": 18, "十九": 19, "二十": 20,
}

STOP_TERMS = {
    "the", "and", "for", "you", "with", "this", "that", "from", "are", "was",
    "www", "http", "https", "com", "pdf", "isbn", "copyright", "page", "chapter",
    "function", "class", "public", "static", "void", "import", "return",
    "ptpress", "com.cn", "net", "org",
    # OCR / 版权噪声
    "posts", "telecom", "press", "rights", "reserved",
    "isbn", "cip", "books", "edition", "preface", "contents",
    "figure", "table", "section", "example", "note", "see", "also",
    "post", "tele", "mail", "phone",
}


def parse_chapter_num(title: str) -> int | None:
    m = CHAPTER_NUM_RE.search(title)
    if not m:
        return None
    token = m.group(1)
    if token.isdigit():
        return int(token)
    return CN_NUM.get(token)


def parse_section_code(title: str) -> str | None:
    m = SECTION_NUM_RE.match(title.strip())
    return m.group(1) if m else None


def section_parent_chapter(code: str) -> int | None:
    """1.2.3 → 1"""
    try:
        return int(code.split(".")[0])
    except Exception:
        return None


def strip_noise_title(t: str) -> str:
    t = re.sub(r"\s+", " ", t).strip()
    t = re.sub(r"[.·…\s]+\d{1,4}$", "", t)
    return t[:40]


NOISE_TITLE = re.compile(
    r"(版权|出版社|印刷|开本|书号|ISBN|CIP|备案|定价|字数|版次|印次|www\.|http|@|邮电|新华书店|责任编辑|封面设计|校对|经销|发行|版权所有)",
    re.I,
)


def clean_chapter_title(t: str) -> str:
    t = re.sub(r"\s+", " ", (t or "").strip())
    t = re.sub(r"^[第]*\s*([0-9一二三四五六七八九十百零]+)\s*章\s*", r"第\1章 ", t)
    # 目录后接说明文字：截到句号/逗号/空白说明
    t = re.split(r"[。；;：:，,—\-—]|另外|包括|其中|介绍|讲解了|重点介绍|本书", t)[0]
    t = re.sub(r"\s+\d{1,4}$", "", t)  # 去掉页码
    t = re.sub(r"\s+", " ", t).strip()
    return t[:36]


def is_noise_title(title: str) -> bool:
    t = (title or "").strip()
    if len(t) < 2:
        return True
    # OCR 垃圾：大量符号、拉丁字母与数字混杂且无中文
    import re as _re
    if "出版社" in t or "印刷" in t or "开本" in t or "字数" in t or "版次" in t:
        return True
    if "www." in t or ".com" in t or ".cn" in t:
        return True
    if "ISBN" in t or "CIP" in t or "责任编辑" in t or "封面设计" in t:
        return True
    # 连续点号 / 省略号垃圾（强化学习扫描版目录）
    if _re.search(r"[\.\…·]{3,}", t):
        return True
    han = len(_re.findall(r"[一-鿿]", t))
    latin = len(_re.findall(r"[A-Za-z]", t))
    digits = len(_re.findall(r"\d", t))
    if han == 0 and (latin + digits) >= 1:
        # 允许 Go / Java / C# / RNN 等有意义英文术语
        if not _re.search(r"(Go|Java|Kotlin|Python|RNN|LSTM|GRU|Transformer|Agent|SQL|API|NLP|CNN|RL)\b", t):
            return True
    # 「4 时预约」「9 著 黄 佳」「9 著」OCR 残片
    if _re.match(r"^\d+\s+\S{0,6}(著|时|页|印|版|次|预约)", t):
        return True
    if _re.search(r"\b(著|校对|经销|发行|印次|版次)\b", t) and han < 8:
        return True
    if t.count("…") >= 2:
        return True
    if _re.match(r"^\d+\s*[，,。]", t):
        return True
    if NOISE_TITLE.search(t):
        return True
    # 目录/CIP 噪声：如「1 . ①深」「1 数据核字」
    if re.match(r"^\d+\s*[\.\s]*[①②③④⑤ⅠⅡⅢ]", t):
        return True
    if re.match(r"^[\d\s\.、，,。①②③ⅠⅡⅢ]+$", t):
        return True
    if re.match(r"^[\W_]{1,8}$", t):
        return True
    # 小节残片：数字编号 + 极短无意义中文（OCR 目录）
    m = _re.match(r"^(\d{1,2}(?:\.\d{1,2}){0,2})\s*(.*)$", t)
    if m:
        rest = (m.group(2) or "").strip()
        rest_han = len(_re.findall(r"[一-鿿]", rest))
        if len(rest) < 2 or rest_han == 0:
            return True
        # 「须程」「老虎」「数据核字」「尔曼方」等碎片
        if rest_han <= 2 and len(rest) <= 4 and not _re.search(r"(变量|函数|类|数组|循环|线程|对象|接口|继承|网络|梯度|损失)", rest):
            return True
        if _re.search(r"(数据核字|须程|尔曼|间的随|示的|法平均|掌握|以及)", rest) and rest_han <= 4:
            return True
    return False


def extract_outline(text: str, max_chapters: int = 80, max_sections: int = 200) -> tuple[list[dict], list[dict]]:
    """返回 (chapters, sections)，带层级与序号；过滤版权/CIP 噪声。目录尽量全量。"""
    chapters: list[dict[str, Any]] = []
    sections: list[dict[str, Any]] = []
    seen_c: set[str] = set()
    seen_s: set[str] = set()

    for m in re.finditer(r"(第\s*[0-9一二三四五六七八九十百零]+\s*章[^\n]{0,40})", text):
        raw = clean_chapter_title(strip_noise_title(m.group(1)))
        if is_noise_title(raw):
            continue
        num = parse_chapter_num(raw)
        key = f"ch:{num if num is not None else raw}"
        if key in seen_c or len(raw) < 3:
            continue
        seen_c.add(key)
        chapters.append({
            "title": raw[:36],
            "kind": "chapter",
            "level": 0,
            "chapter_num": num,
            "offset": m.start(),
        })

    for m in re.finditer(r"(?m)^\s*(\d{1,2}(?:\.\d{1,2}){0,2})\s+([^\n]{2,40})$", text):
        code = m.group(1)
        name = strip_noise_title(m.group(2))
        if is_noise_title(name) or is_noise_title(f"{code} {name}"):
            continue
        title = f"{code} {name}"[:36]
        key = f"sec:{code}"
        if key in seen_s or len(name) < 2:
            continue
        seen_s.add(key)
        pnum = section_parent_chapter(code)
        depth = code.count(".") + 1
        sections.append({
            "title": title,
            "kind": "section",
            "level": min(depth, 3),
            "section_code": code,
            "chapter_num": pnum,
            "offset": m.start(),
        })

    chapters.sort(key=lambda x: (x["chapter_num"] if x["chapter_num"] is not None else 999, x["offset"]))
    sections.sort(key=lambda x: (x.get("chapter_num") or 999, x["offset"]))
    return chapters[:max_chapters], sections[:max_sections]


def extract_terms_weighted(text: str, chapter_offsets: list[tuple[int, str]], limit: int = 28) -> list[dict]:
    """术语抽取：频率 + 靠前章节加权 + 领域词典加成。"""
    from re import finditer

    domain = {
        "变量", "类型", "函数", "类", "对象", "接口", "继承", "多态", "泛型", "协程",
        "数组", "字符串", "循环", "异常", "并发", "线程", "进程", "面向对象",
        "神经网络", "反向传播", "卷积", "注意力", "Transformer", "强化学习",
        "监督学习", "梯度下降", "损失函数", "激活函数", "智能体", "大模型",
        "Word", "Excel", "Python", "Java", "Go", "Kotlin", "C#", "Agent", "RAG",
        "RNN", "LSTM", "GRU", "seq2seq", "word2vec", "Attention",
    }
    pats = [
        r"([A-Za-z_][A-Za-z0-9_+#\.]{2,28})",
        r"((?:" + "|".join(sorted(domain, key=len, reverse=True)) + r"))",
    ]
    # 位置：章节区间
    bounds = sorted(chapter_offsets)  # (offset, chapter_id/title)
    counts: dict[str, int] = defaultdict(int)
    first_pos: dict[str, int] = {}
    for pat in pats:
        for m in finditer(pat, text):
            term = (m.group(1) or "").strip()
            if len(term) < 2 or term.isdigit():
                continue
            low = term.lower()
            if low in STOP_TERMS:
                continue
            # 过滤页码式、全小写虚词
            if re.fullmatch(r"[a-z]{1,3}", term):
                continue
            # OCR 噪声词：全大写新闻页脚 / 连续点号 / 过长垃圾
            if re.search(r"[\.\…·]{3,}", term):
                continue
            if term.isupper() and len(term) >= 4 and term not in {"API", "SQL", "NLP", "CNN", "RNN", "LSTM", "GRU", "RL", "HTTP"}:
                if term not in domain and term.lower() not in {d.lower() for d in domain}:
                    continue
            if low in {"posts", "telecom", "press", "copyright", "isbn", "preface", "contents", "reilly", "o'reilly"}:
                continue
            if term in {"Reilly", "O'Reilly", "Media", "Inc", "Ltd", "图灵", "社区", "投稿", "邮箱"}:
                continue
            # 中英混杂且中文 < 2 的碎片
            han_n = len(re.findall(r"[一-鿿]", term))
            if han_n == 0 and len(term) >= 10 and term.lower() not in {d.lower() for d in domain}:
                continue
            counts[term] += 1
            if term not in first_pos:
                first_pos[term] = m.start()

    def score(term: str) -> float:
        freq = counts[term]
        pos = first_pos.get(term, 10**9)
        # 越靠前略有加权
        pos_w = 1.0 + max(0.0, (1.0 - pos / max(1, len(text))) * 0.25)
        dom_w = 1.35 if (term in domain or term.lower() in {d.lower() for d in domain}) else 1.0
        len_w = 1.05 if 2 <= len(term) <= 12 else 0.9
        return freq * pos_w * dom_w * len_w

    ranked = sorted(counts.keys(), key=lambda t: (-score(t), t))
    out = []
    for t in ranked[:limit]:
        # 归属章节：首次出现所在章节
        host = None
        pos = first_pos.get(t, 0)
        for off, name in bounds:
            if off <= pos:
                host = name
            else:
                break
        out.append({
            "term": t,
            "freq": counts[t],
            "score": round(score(t), 4),
            "first_offset": pos,
            "host_chapter": host,
        })
    return out


def cooccurrence_edges(
    text: str,
    term_positions: dict[str, int],
    node_id_by_term: dict[str, str],
    window: int = 180,
    max_edges: int = 24,
) -> list[dict]:
    """术语共现：只连 first_offset 较早 → 较晚，保证无环。"""
    items = sorted(((term_positions[t], t) for t in node_id_by_term if t in term_positions), key=lambda x: x[0])
    scores: list[tuple[float, str, str]] = []
    for i, (pos_i, ti) in enumerate(items):
        for pos_j, tj in items[i + 1:]:
            if pos_j - pos_i > window:
                break
            if ti == tj:
                continue
            # 共现强度
            gap = pos_j - pos_i
            w = 1.0 + max(0.0, (window - gap) / window)
            scores.append((w, ti, tj))
    scores.sort(key=lambda x: (-x[0], x[1], x[2]))
    edges = []
    seen = set()
    for w, ti, tj in scores:
        key = (ti, tj)
        if key in seen:
            continue
        seen.add(key)
        edges.append({
            "from": node_id_by_term[ti],
            "to": node_id_by_term[tj],
            "edgeType": "related",
            "weight": round(min(1.6, w), 3),
            "source": "auto:cooccurrence",
        })
        if len(edges) >= max_edges:
            break
    return edges


def remove_cycles(nodes: list[dict], edges: list[dict]) -> tuple[list[dict], list[str]]:
    """拓扑排序：无法入队的边（成环边）删除，返回净化后的边与被删描述。"""
    ids = {n["id"] for n in nodes}
    clean = [e for e in edges if e["from"] in ids and e["to"] in ids and e["from"] != e["to"]]
    # 去重（from,to）
    uniq: dict[tuple[str, str], dict] = {}
    for e in clean:
        key = (e["from"], e["to"])
        if key not in uniq:
            uniq[key] = e
    clean = list(uniq.values())

    indeg = {i: 0 for i in ids}
    out: dict[str, list[str]] = defaultdict(list)
    for e in clean:
        out[e["from"]].append(e["to"])
        indeg[e["to"]] = indeg.get(e["to"], 0) + 1
    # Kahn
    q = [i for i in ids if indeg[i] == 0]
    order: list[str] = []
    remaining = {i: indeg[i] for i in ids}
    adj = {i: list(out[i]) for i in ids}
    qq = list(q)
    while qq:
        u = qq.pop(0)
        order.append(u)
        for v in adj[u]:
            remaining[v] -= 1
            if remaining[v] == 0:
                qq.append(v)

    if len(order) == len(ids):
        # 无环；给节点写拓扑 rank
        rank = {n: i for i, n in enumerate(order)}
        for n in nodes:
            n["topo_rank"] = rank.get(n["id"], 0)
        return edges, []

    # 有环：逐步删边
    removed: list[str] = []
    edges_cur = list(clean)
    while True:
        indeg = {i: 0 for i in ids}
        adj = {i: [] for i in ids}
        for e in edges_cur:
            adj[e["from"]].append(e["to"])
            indeg[e["to"]] += 1
        qq = [i for i in ids if indeg[i] == 0]
        order = []
        rem = dict(indeg)
        while qq:
            u = qq.pop(0)
            order.append(u)
            for v in adj[u]:
                rem[v] -= 1
                if rem[v] == 0:
                    qq.append(v)
        if len(order) == len(ids):
            break
        # 找一条环上边删除：从入度>0 且不在 order 中的节点，删一条入边
        cycle_nodes = [i for i in ids if i not in set(order)]
        target = cycle_nodes[0] if cycle_nodes else ids.pop()
        victim = None
        for e in edges_cur:
            if e["to"] == target:
                victim = e
                break
        if victim is None:
            # 删任意
            if not edges_cur:
                break
            victim = edges_cur[-1]
        edges_cur.remove(victim)
        removed.append(f"{victim['from']}->{victim['to']} ({victim.get('source','')})")

    rank = {n: i for i, n in enumerate(order)}
    for n in nodes:
        n["topo_rank"] = rank.get(n["id"], 0)
    return edges_cur, removed


def build_knowledge_graph(
    text: str,
    material_title: str,
    max_chapters: int = 80,
    max_sections: int = 200,
    max_terms: int = 28,
) -> dict[str, Any]:
    """完整构图算法。目录尽量全量入图（章+节+小节）。"""
    chapters, sections = extract_outline(text, max_chapters=max_chapters, max_sections=max_sections)

    # 若章过少，从文件名/正则兜底
    if len(chapters) < 2:
        for m in re.finditer(r"(第\s*\d+\s*章[^\n]{0,24})", text):
            t = clean_chapter_title(strip_noise_title(m.group(1)))
            if is_noise_title(t):
                continue
            chapters.append({"title": t, "kind": "chapter", "level": 0,
                             "chapter_num": parse_chapter_num(t), "offset": m.start()})
        # 去重
        seen = set()
        uniq = []
        for ch in chapters:
            k = ch["title"][:20]
            if k in seen:
                continue
            seen.add(k)
            uniq.append(ch)
        chapters = uniq

    chapters = chapters[:max_chapters]
    sections = sections[:max_sections]
    offsets = [(c["offset"], c["title"]) for c in chapters]
    terms = extract_terms_weighted(text, offsets, limit=max_terms)

    course = material_title[:24] or "MATERIAL"
    nodes: list[dict] = []
    edges: list[dict] = []

    # 无章节时兜底：合成书名主干节点，保证小节/术语有挂点，图不至于 0 边
    if len(chapters) == 0:
        fallback_title = (material_title or "教材")[:24]
        chapters = [{
            "title": fallback_title,
            "kind": "chapter",
            "level": 0,
            "chapter_num": 1,
            "offset": 0,
        }]
        for si, sec in enumerate(sections[:12], start=1):
            if sec.get("chapter_num") is None:
                sec["chapter_num"] = 1
        if not terms:
            terms = [{"term": fallback_title[:12], "freq": 3, "score": 3.0, "first_offset": 0, "host_chapter": fallback_title}]

    # 分配 ID：章 → 小节 → 关键词（ID 顺序即层级顺序，利于布局）
    def alloc(prefix: str, i: int) -> str:
        return f"{prefix}{i:03d}"

    ch_id: dict[str, str] = {}
    ch_num_to_id: dict[int, str] = {}
    for i, ch in enumerate(chapters, start=1):
        nid = alloc("C", i)
        ch_id[ch["title"]] = nid
        if ch.get("chapter_num") is not None:
            ch_num_to_id[int(ch["chapter_num"])] = nid
        nodes.append({
            "id": nid,
            "name": ch["title"][:36],
            "course": course,
            "description": "chapter",
            "source": "kg:chapter",
            "level": 0,
            "layer": "chapter",
            "weight": 1.0,
        })

    # 章节时序链
    ch_ids = [ch_id[c["title"]] for c in chapters]
    for a, b in zip(ch_ids, ch_ids[1:]):
        edges.append({
            "from": a, "to": b, "edgeType": "prerequisite", "weight": 1.3,
            "source": "kg:chapter-sequence",
        })

    sec_id: dict[str, str] = {}
    for i, sec in enumerate(sections, start=1):
        nid = alloc("S", i)
        sec_id[sec["title"]] = nid
        nodes.append({
            "id": nid,
            "name": sec["title"][:36],
            "course": course,
            "description": "section",
            "source": "kg:section",
            "level": sec.get("level", 1),
            "layer": "section",
            "weight": 0.8,
        })
        # 小节 → 所属章
        pnum = sec.get("chapter_num")
        parent = ch_num_to_id.get(pnum) if pnum is not None else None
        if parent:
            edges.append({
                "from": parent, "to": nid, "edgeType": "prerequisite", "weight": 1.1,
                "source": "kg:section-parent",
            })
        elif ch_ids:
            # 兜底：挂到第一章，避免小节孤立导致 0 边
            edges.append({
                "from": ch_ids[0], "to": nid, "edgeType": "prerequisite", "weight": 0.9,
                "source": "kg:section-fallback",
            })

    term_id: dict[str, str] = {}
    term_pos: dict[str, int] = {}
    for i, term in enumerate(terms, start=1):
        nid = alloc("T", i)
        term_id[term["term"]] = nid
        term_pos[term["term"]] = term.get("first_offset", 0)
        nodes.append({
            "id": nid,
            "name": term["term"][:28],
            "course": course,
            "description": f"关键词 freq={term['freq']} score={term['score']}",
            "source": "kg:term",
            "level": 3,
            "layer": "term",
            "weight": round(min(1.0, term["freq"] / 30.0), 3),
        })
        # 术语 → 所属章节（单向，防环）
        host = term.get("host_chapter")
        host_id = ch_id.get(host) if host else None
        if host_id:
            edges.append({
                "from": host_id, "to": nid, "edgeType": "prerequisite",
                "weight": round(0.7 + min(0.6, term["freq"] / 40.0), 3),
                "source": "kg:term-anchor",
            })
        elif ch_ids:
            edges.append({
                "from": ch_ids[0], "to": nid, "edgeType": "prerequisite", "weight": 0.7,
                "source": "kg:term-fallback",
            })

    # 术语共现（仅 term→term，且先出现→后出现，天然无环）
    if len(term_id) >= 2:
        edges.extend(cooccurrence_edges(text, term_pos, term_id, window=220, max_edges=20))

    # DAG 收尾（remove_cycles 返回 (净化后的边, 被删边描述)）
    edges, removed_cycle_edges = remove_cycles(nodes, edges)

    # 度数与层级统计
    deg = defaultdict(int)
    for e in edges:
        deg[e.get("from")] += 1
        deg[e.get("to")] += 1
    for n in nodes:
        nid = n.get("id") if isinstance(n, dict) else None
        if nid:
            n["degree"] = deg.get(nid, 0)

    stats = {
        "chapters": len(ch_ids),
        "sections": len(sec_id),
        "terms": len(term_id),
        "nodes": len(nodes),
        "edges": len(edges),
        "algorithm": "kg-v2-hierarchy-cooc-dag",
        "refs": {
            "knowledgeGraph": "triple/RDF + 节点属性 + 关系谓词（参考 GitHub/KnowledgeGraph）",
            "mindMap": "simple-mind-map 逻辑结构树 + Markdown 大纲（参考 GitHub/mind-map）",
        },
    }
    triples = to_triples(nodes, edges)
    try:
        mindmap = to_mindmap_tree(nodes, edges, root_text=material_title)
        outline = tree_to_markdown(mindmap)
        layout = logical_structure_layout(mindmap)
    except Exception:
        mindmap = {"data": {"text": material_title[:24] or "知识图谱", "id": "__root__", "layer": "root", "expand": True}, "children": []}
        outline = f"# {material_title}"
        layout = None
    return {
        "nodes": nodes,
        "edges": edges,
        "chapters": [c["title"] for c in chapters],
        "terms": [{"term": t["term"], "freq": t["freq"], "score": t["score"]} for t in terms],
        "stats": stats,
        "removed_cycle_edges": removed_cycle_edges,
        # KnowledgeGraph 仓库：三元组/属性图
        "triples": triples,
        "propertyGraph": to_property_graph(nodes, edges),
        # mind-map 仓库：逻辑结构树 + 大纲
        "mindmap": mindmap,
        "markdownOutline": outline,
        "logicalLayout": layout,
    }


# ---------------------------------------------------------------------------
# 对齐 KnowledgeGraph（RDF/三元组 + 属性图）与 mind-map（逻辑结构树）
# ---------------------------------------------------------------------------

PREDICATE_MAP = {
    "kg:chapter-sequence": "后继章节",
    "kg:section-parent": "隶属于章节",
    "kg:section-near": "就近所属章节",
    "kg:term-anchor": "涉及关键词",
    "kg:term-fallback": "涉及关键词",
    "auto:cooccurrence": "共现相关",
    "auto:chapter-sequence": "后继章节",
    "auto:term-anchor": "涉及关键词",
}


def to_triples(nodes: list[dict], edges: list[dict]) -> list[dict]:
    """RDF 风格三元组：subject - predicate - object（对齐 KnowledgeGraph/RDF）。"""
    name_of = {n["id"]: n.get("name", n["id"]) for n in nodes}
    label_of = {n["id"]: n.get("layer", n.get("description", "concept")) for n in nodes}
    triples = []
    for e in edges:
        pred = e.get("edgeType") or "related"
        src = e.get("source", "")
        predicate = PREDICATE_MAP.get(src, pred)
        triples.append({
            "subject": name_of.get(e["from"], e["from"]),
            "subjectId": e["from"],
            "subjectLabel": label_of.get(e["from"], ""),
            "predicate": predicate,
            "predicateSource": src,
            "object": name_of.get(e["to"], e["to"]),
            "objectId": e["to"],
            "objectLabel": label_of.get(e["to"], ""),
            "weight": e.get("weight", 1.0),
        })
    return triples


def to_property_graph(nodes: list[dict], edges: list[dict]) -> dict:
    """Neo4j/属性图导出（对齐 KnowledgeGraph → Neo4j）。"""
    return {
        "nodes": [
            {
                "id": n["id"],
                "name": n.get("name", ""),
                "labels": [n.get("layer") or "Concept"],
                "properties": {
                    "course": n.get("course", ""),
                    "description": n.get("description", ""),
                    "source": n.get("source", ""),
                    "level": n.get("level", 0),
                    "weight": n.get("weight", 0),
                    "degree": n.get("degree", 0),
                    "topoRank": n.get("topo_rank", 0),
                },
            }
            for n in nodes
        ],
        "relationships": [
            {
                "from": e["from"],
                "to": e["to"],
                "type": (e.get("edgeType") or "RELATED").upper(),
                "properties": {
                    "weight": e.get("weight", 1.0),
                    "source": e.get("source", ""),
                    "predicate": PREDICATE_MAP.get(e.get("source", ""), e.get("edgeType", "related")),
                },
            }
            for e in edges
        ],
        "cypherHint": "// MATCH (a)-[r]->(b) RETURN a,r,b LIMIT 50",
    }


def to_mindmap_tree(nodes: list[dict], edges: list[dict], root_text: str = "知识图谱") -> dict:
    """simple-mind-map 数据结构：{data:{text}, children:[]}。"""
    by_id = {n["id"]: dict(n) for n in nodes}
    children: dict[str, list[str]] = {nid: [] for nid in by_id}
    parent: dict[str, str] = {}
    # 只保留树边：优先 parent/隶属/章节链，共现边不进树
    tree_sources = {
        "kg:chapter-sequence", "kg:section-parent", "kg:section-near",
        "kg:term-anchor", "kg:term-fallback",
        "auto:chapter-sequence", "auto:term-anchor",
    }
    tree_edges = [e for e in edges if e.get("source", "") in tree_sources or e.get("edgeType") == "prerequisite"]
    for e in tree_edges:
        a, b = e["from"], e["to"]
        if a not in by_id or b not in by_id or a == b:
            continue
        if b in parent:
            continue
        parent[b] = a
        children[a].append(b)

    # 无父节点 → 作为根的直接子节点
    roots = [nid for nid in by_id if nid not in parent]
    # 思维导图：章节并列挂在书根下（不要被 chapter-sequence 拉成单链）
    chapters = [
        nid for nid in by_id
        if by_id[nid].get("layer") == "chapter"
        or str(by_id[nid].get("name", "")).startswith("第")
        or str(by_id[nid].get("description", "")) in ("chapter", "title")
    ]
    chapter_set = set(chapters)
    # 章节彼此不作为父子（sequence 边只表示阅读顺序）
    for ch in chapters:
        parent.pop(ch, None)
    children = {nid: [c for c in lst if c not in chapter_set or lst is None] for nid, lst in children.items()}
    # rebuild children excluding chapter→chapter sequence edges
    children = {nid: [] for nid in by_id}
    for e in tree_edges:
        a, b = e["from"], e["to"]
        if a not in by_id or b not in by_id or a == b:
            continue
        if b in chapter_set and a in chapter_set:
            continue  # 章节并列，不互为父
        if b in parent:
            continue
        parent[b] = a
        children[a].append(b)

    roots = [nid for nid in by_id if nid not in parent]
    if chapters:
        chapter_ids = [c for c in chapters]
        others = [nid for nid in roots if nid not in chapter_ids]
        # 章节按 topo/序号并列
        chapter_ids = sorted(chapter_ids, key=lambda i: by_id[i].get("topo_rank", 999))
        root_children = chapter_ids + others
        # 章节下的 section/term 保持原父子
    else:
        root_children = sorted(roots, key=lambda i: by_id[i].get("topo_rank", 999))

    def build(nid: str) -> dict:
        n = by_id[nid]
        node = {
            "data": {
                "text": n.get("name", nid),
                "id": nid,
                "layer": n.get("layer") or "term",
                "level": n.get("level", 0),
                "description": n.get("description", ""),
                "expand": True,
                # mind-map 样式钩子
                "tag": [n.get("layer") or "node"],
            },
            "children": [build(c) for c in children.get(nid, [])],
        }
        return node

    return {
        "data": {
            "text": root_text[:24] or "知识图谱",
            "id": "__root__",
            "layer": "root",
            "expand": True,
        },
        "children": [build(c) for c in root_children[:30]],
        "layout": "logicalStructure",
        "meta": {"rootChildren": len(root_children), "treeEdges": len(tree_edges)},
    }


def tree_to_markdown(tree: dict, max_depth: int = 4) -> str:
    """输出 Markdown 大纲（对齐 mind-map markdownTo 的逆过程）。"""
    lines: list[str] = []

    def walk(node: dict, depth: int) -> None:
        if depth > max_depth:
            return
        text = str((node.get("data") or {}).get("text", "")).strip()
        if text:
            prefix = "#" * max(1, depth)
            lines.append(f"{prefix} {text}")
        for child in node.get("children") or []:
            walk(child, depth + 1)

    walk(tree, 1)
    return "\n".join(lines)


def logical_structure_layout(tree: dict, margin_x: int = 210, margin_y: int = 18, node_w: int = 160, node_h: int = 48) -> dict:
    """对齐 simple-mind-map LogicalStructure：根在左，子节点纵向堆叠向右展开。"""
    positions: dict[str, dict] = {}

    def measure(node: dict) -> int:
        """返回子树总高度（像素）。"""
        children = node.get("children") or []
        nid = str((node.get("data") or {}).get("id", ""))
        if not children:
            h = node_h
            positions[nid] = {"x": 0, "y": 0, "w": node_w, "h": h, "layer": (node.get("data") or {}).get("layer")}
            return h
        child_heights = [measure(c) for c in children]
        total = sum(child_heights) + margin_y * (len(children) - 1)
        h = max(node_h, total)
        positions[nid] = {"x": 0, "y": 0, "w": node_w, "h": h, "layer": (node.get("data") or {}).get("layer")}
        # 子节点垂直居中
        y_cursor = -total / 2
        for c, ch in zip(children, child_heights):
            cid = str((c.get("data") or {}).get("id", ""))
            cy = y_cursor + ch / 2
            if cid in positions:
                positions[cid]["y"] = cy
                # 相对父的 x 在第二遍设置
            y_cursor += ch + margin_y
        node["_child_heights"] = child_heights
        node["_total"] = total
        return max(node_h, total)

    def place(node: dict, depth: int, y_center: float) -> None:
        nid = str((node.get("data") or {}).get("id", ""))
        x = depth * margin_x
        if nid in positions:
            positions[nid]["x"] = x
            positions[nid]["y"] = y_center
        children = node.get("children") or []
        if not children:
            return
        child_heights = node.get("_child_heights") or [node_h] * len(children)
        total = sum(child_heights) + margin_y * max(0, len(children) - 1)
        y = y_center - total / 2
        for c, ch in zip(children, child_heights):
            cid = str((c.get("data") or {}).get("id", ""))
            cy = y + ch / 2
            if cid not in positions:
                positions[cid] = {"x": (depth + 1) * margin_x, "y": cy, "w": node_w, "h": ch, "layer": (c.get("data") or {}).get("layer")}
            else:
                positions[cid]["x"] = (depth + 1) * margin_x
                positions[cid]["y"] = cy
            place(c, depth + 1, cy)
            y += ch + margin_y

    measure(tree)
    place(tree, 0, 0.0)
    return {
        "type": "logicalStructure",
        "marginX": margin_x,
        "marginY": margin_y,
        "nodeWidth": node_w,
        "nodeHeight": node_h,
        "positions": positions,
        "note": "布局参考 mind-map/simple-mind-map LogicalStructure",
    }
