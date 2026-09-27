# -*- coding: utf-8 -*-
"""
知识图谱构建 —— 对齐 KnowledgeGraph(kg_opt) + mind-map(simple-mind-map)
  ① AC 自动机实体抽取     ← kg_opt.AhoCorasick
  ② 实体链接/并查集融合   ← kg_opt.UnionFind / bounded_levenshtein
  ③ TextRank + PMI 构图   ← AstralPath GraphInference
  ④ 思维导图树 / Markdown ← simple-mind-map LogicalStructure + parse/markdown
"""
from __future__ import annotations

import json
import math
import re
import sys
from collections import defaultdict, deque
from pathlib import Path
from typing import Dict, Iterable, List, Sequence, Tuple

# ───────────────────────── kg_opt: AhoCorasick ─────────────────────────
class AhoCorasick:
    __slots__ = ("goto", "fail", "output", "max_len", "first_chars", "size", "lens")

    def __init__(self, patterns: Iterable):
        self.goto = [{}]
        self.fail = [0]
        self.output = [[]]
        self.max_len = 0
        self.first_chars = set()
        self.size = 0
        self.lens = []
        self._build(patterns)

    def _build(self, patterns):
        goto, fail, output = self.goto, self.fail, self.output
        lens = []
        for pat in patterns:
            pid = len(lens)
            lens.append(len(pat) if pat else 0)
            if not pat:
                continue
            node = 0
            for ch in pat:
                nxt = goto[node].get(ch)
                if nxt is None:
                    goto.append({})
                    fail.append(0)
                    output.append([])
                    nxt = len(goto) - 1
                    goto[node][ch] = nxt
                node = nxt
            output[node].append(pid)
            self.max_len = max(self.max_len, len(pat))
            self.first_chars.add(pat[0])
        self.lens = lens
        self.size = len(goto)
        q = deque()
        for ch, nxt in goto[0].items():
            fail[nxt] = 0
            q.append(nxt)
        while q:
            node = q.popleft()
            for ch, nxt in goto[node].items():
                f = fail[node]
                while f and ch not in goto[f]:
                    f = fail[f]
                target = goto[f].get(ch, 0)
                fail[nxt] = target if target != nxt else 0
                if output[fail[nxt]]:
                    output[nxt].extend(output[fail[nxt]])
                q.append(nxt)

    def find_longest(self, text: Sequence) -> List[Tuple[int, int, int]]:
        hits = []
        goto, fail, output = self.goto, self.fail, self.output
        node = 0
        for i, ch in enumerate(text):
            while node and ch not in goto[node]:
                node = fail[node]
            node = goto[node].get(ch, 0)
            if output[node]:
                for pid in output[node]:
                    ln = self.lens[pid]
                    hits.append((i - ln + 1, i + 1, pid))
        if not hits:
            return []
        hits.sort(key=lambda h: (h[0], -(h[1] - h[0])))
        chosen, last_end = [], -1
        for s, e, pid in hits:
            if s >= last_end:
                chosen.append((s, e, pid))
                last_end = e
        return chosen


class UnionFind:
    def __init__(self, n: int):
        self.p = list(range(n))

    def find(self, x: int) -> int:
        while self.p[x] != x:
            self.p[x] = self.p[self.p[x]]
            x = self.p[x]
        return x

    def union(self, a: int, b: int):
        ra, rb = self.find(a), self.find(b)
        if ra != rb:
            self.p[rb] = ra


def bounded_levenshtein(a: str, b: str, tau: int) -> int:
    if a == b:
        return 0
    la, lb = len(a), len(b)
    if abs(la - lb) > tau:
        return tau + 1
    p = 0
    while p < min(la, lb) and a[p] == b[p]:
        p += 1
    a, b, la, lb = a[p:], b[p:], len(a) - p, len(b) - p
    s = 0
    while s < min(la, lb) and a[la - 1 - s] == b[lb - 1 - s]:
        s += 1
    if s:
        a, b, la, lb = a[: la - s], b[: lb - s], la - s, lb - s
    if a == b:
        return 0
    if abs(la - lb) > tau:
        return tau + 1
    INF = tau + 1
    n = lb
    prev = [INF] * (n + 1)
    for j in range(min(n, tau) + 1):
        prev[j] = j
    for i in range(1, la + 1):
        cur = [INF] * (n + 1)
        lo, hi = max(1, i - tau), min(n, i + tau)
        if i <= tau:
            cur[0] = i
        for j in range(lo, hi + 1):
            cost = 0 if a[i - 1] == b[j - 1] else 1
            cur[j] = min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + cost)
        if min(cur) > tau:
            return tau + 1
        prev = cur
    return min(prev[n], tau + 1)


# ───────────────────────── GraphInference: TextRank / PMI ─────────────────────────
_JIEBA = None
_JIEBA_TRIED = False

# K-04：正则 8 字窗口机械切片会把连续汉字切成无语义伪词；有 jieba 用 jieba，
# 没有则回退原口径（不引入硬依赖）。停用表滤掉分词后高频虚词与章节噪声。
_CN_STOPWORDS = {
    "我们", "你们", "他们", "自己", "什么", "这个", "那个", "一个", "没有", "不是",
    "可以", "进行", "使用", "通过", "如果", "那么", "因此", "但是", "然后", "以及",
    "或者", "并且", "对于", "关于", "以上", "下面", "如下", "其中", "之间", "开始",
    "结束", "时候", "现在", "可能", "需要", "应该", "如何", "这些", "那些", "这样",
    "小结", "习题", "本章", "参考", "文献", "目录", "前言", "后记", "附录", "版本",
    "出版", "印刷", "字数", "定价", "编著",
    # 英文虚词（jieba 会把 in/for/the 等当词吐出来）
    "in", "on", "of", "to", "is", "are", "an", "as", "at", "by", "or", "if", "it",
    "we", "be", "do", "no", "so", "up", "an", "the", "and", "for", "not", "with",
    "from", "this", "that", "will", "can", "its", "one", "two", "use", "using",
}


def _get_jieba():
    global _JIEBA, _JIEBA_TRIED
    if not _JIEBA_TRIED:
        _JIEBA_TRIED = True
        try:
            import jieba

            jieba.setLogLevel(60)
            _JIEBA = jieba
        except Exception:
            _JIEBA = None
    return _JIEBA


def tokenize(text: str) -> List[str]:
    jieba = _get_jieba()
    if jieba is not None:
        return [t for t in jieba.lcut(text)
                if 2 <= len(t) <= 20 and t.lower() not in _CN_STOPWORDS and not t.isspace()]
    return [t for t in re.findall(r"[一-鿿]{2,8}|[A-Za-z][A-Za-z0-9_\.]{1,20}", text) if len(t) >= 2]


def text_rank(tokens: List[str], window=5, iterations=8, damping=0.85) -> Dict[str, float]:
    # 限制词表规模，避免 O(V^2) 矩阵爆炸
    from collections import Counter

    freq = Counter(tokens)
    vocab = [t for t, _ in freq.most_common(400)]
    if not vocab:
        return {}
    keep = set(vocab)
    tokens = [t for t in tokens if t in keep][:20000]
    idx = {t: i for i, t in enumerate(vocab)}
    n = len(vocab)
    w = [[0.0] * n for _ in range(n)]
    for i, t in enumerate(tokens):
        a = idx[t]
        for j in range(i + 1, min(len(tokens), i + window)):
            b = idx[tokens[j]]
            v = 1.0 / (j - i)
            w[a][b] += v
            w[b][a] = w[a][b]
    row = [sum(r) for r in w]
    rank = [1.0 / n] * n
    for _ in range(iterations):
        nxt = [0.0] * n
        for i in range(n):
            s = 0.0
            for j in range(n):
                if w[j][i] > 0 and row[j] > 0:
                    s += (w[j][i] / row[j]) * rank[j]
            nxt[i] = (1 - damping) / n + damping * s
        delta = max((abs(a - b) for a, b in zip(rank, nxt)), default=0.0)
        rank = nxt
        if delta < 1e-6:  # K-17：收敛即停，固定 8 轮只是上界
            break
    return {vocab[i]: rank[i] for i in range(n)}


def pmi(co: int, a: int, b: int, total: int) -> float:
    if co <= 0 or a <= 0 or b <= 0 or total <= 0:
        return 0.0
    return max(0.0, math.log2((co * total) / (a * b)))


# ───────────────────────── 章节 / 实体 / 构图 ─────────────────────────
def extract_chapters(text: str, max_ch=40):
    lines = text.split("\n")
    marks = []
    for i, ln in enumerate(lines):
        s = ln.strip()
        if not s or len(s) > 60:
            continue
        title = None
        md = re.match(r"^#{1,3}\s+(.+)$", s)
        if md:
            title = md.group(1).strip()
        if not title:
            ch = re.match(r"^第\s*[0-9一二三四五六七八九十百千]+\s*[章节回讲]\s*[:：·.、-]?\s*(.*)$", s)
            if ch:
                title = (ch.group(1) or s).strip()
        if not title:
            en = re.match(r"^Chapter\s+(\d+)\s*[:：.]?\s*(.*)$", s, re.I)
            if en:
                title = (en.group(2) or ("Chapter " + en.group(1))).strip()
        if not title:
            num = re.match(r"^(\d+(?:\.\d+)+)\s+([^\s].{1,40})$", s)
            if num:
                title = num.group(2).strip()
        if not title:
            cn = re.match(r"^([一二三四五六七八九十]+)、\s*(.+)$", s)
            if cn:
                title = cn.group(2).strip()
        if title and 2 <= len(title) <= 40 and not re.match(r"^[=\-–—_*]{2,}$", title):
            marks.append({"i": i, "title": re.sub(r"\s+", " ", title)[:40], "level": 1})
    uniq = []
    for m in marks:
        if not uniq or uniq[-1]["title"] != m["title"]:
            uniq.append(m)
        else:
            uniq[-1] = m
    list_ = uniq[:max_ch]
    if len(list_) < 2:
        paras = re.split(r"\n{2,}", text)
        out = []
        for idx, body in enumerate(paras):
            first = (body.split("\n")[0] or "").strip()
            title = first if first and len(first) <= 30 else f"段落 {idx+1}"
            out.append({"id": f"C{idx+1:02d}", "title": title, "body": body, "order": idx, "level": 1})
        return out[:max_ch]
    chs = []
    for k, m in enumerate(list_):
        a = m["i"]
        b = list_[k + 1]["i"] if k + 1 < len(list_) else len(lines)
        chs.append({"id": f"C{k+1:02d}", "title": m["title"], "body": "\n".join(lines[a:b]).strip(), "order": k, "level": m["level"]})
    return chs


def extract_entities(text: str, chapters, max_terms=24):
    """AC 字典匹配 + TextRank 候选 + 章节标题，再实体融合。"""
    sample = text[:120000]
    ranks = text_rank(tokenize(sample))
    cands = list(ranks.keys())
    titles = [c["title"] for c in chapters]
    # AC 最长匹配去重叠（限制模式数与扫描长度）
    patterns = list(dict.fromkeys(titles + cands[:40]))[:80]
    found = []
    if patterns:
        ac = AhoCorasick(patterns)
        hits = ac.find_longest(sample)
        found = [patterns[pid] for _s, _e, pid in hits]
    # 融合近义实体（编辑距离 ≤1），限制候选规模
    found = list(dict.fromkeys(found))[:60]
    merged = []
    if found:
        uf = UnionFind(len(found))
        for i in range(len(found)):
            for j in range(i + 1, min(len(found), i + 12)):
                if bounded_levenshtein(found[i], found[j], 1) <= 1:
                    uf.union(i, j)
        groups = defaultdict(list)
        for i, name in enumerate(found):
            groups[uf.find(i)].append(name)
        for names in groups.values():
            merged.append(max(names, key=len))
    for t, _ in sorted(ranks.items(), key=lambda kv: -kv[1]):
        if t not in merged:
            merged.append(t)
        if len(merged) >= max_terms + len(titles):
            break
    for t in titles:
        if t not in merged:
            merged.insert(0, t)
    return list(dict.fromkeys(merged))[: max_terms + len(titles)], ranks


def remove_cycles(edges: List[dict]) -> List[dict]:
    """K-13：有向图环校验 + 破环。kg_builder 原先完全无环检测，C001→C002→C003→C001 可直接入库。
    破环删边优先级：非 prerequisite 边先删（related/attach 不该顶掉主干先修链），
    同类型按 weight 小者先删；环内全是 prerequisite 时删 weight 最小者。"""
    if not edges:
        return edges
    etype_rank = {"prerequisite": 0, "attach": 1, "related": 2}
    adj: Dict[str, List[int]] = defaultdict(list)
    for i, e in enumerate(edges):
        adj[e["from"]].append(i)
    color: Dict[str, int] = defaultdict(int)  # 0=white 1=gray 2=black
    removed: set = set()

    def dfs(u: str, stack: List[int]):
        color[u] = 1
        for i in adj.get(u, ()):
            if i in removed:
                continue
            v = edges[i]["to"]
            if color.get(v, 0) == 1:
                # stack 保存根→u 的边索引链；环 = 路径上从 v 展开的那段 + 闭合边 i
                pos = next(k for k, j in enumerate(stack) if edges[j]["from"] == v)
                cands = [j for j in stack[pos:] if j not in removed] + [i]
                non_prereq = [j for j in cands if edges[j]["etype"] != "prerequisite"]
                pool = non_prereq or cands
                victim = min(pool, key=lambda j: (etype_rank.get(edges[j]["etype"], 3), edges[j]["w"]))
                removed.add(victim)
            elif color.get(v, 0) == 0:
                dfs(v, stack + [i])
        color[u] = 2

    for e in edges:
        if color.get(e["from"], 0) == 0:
            dfs(e["from"], [])
    return [e for i, e in enumerate(edges) if i not in removed]


def build_graph(text: str, book_name: str, max_terms=24):
    chapters = extract_chapters(text)
    entities, ranks = extract_entities(text, chapters, max_terms=max_terms)
    nodes = []
    id_of = {}
    for i, ch in enumerate(chapters):
        id_of[ch["title"]] = ch["id"]
        nodes.append({"id": ch["id"], "title": ch["title"], "kind": "chapter", "course": book_name, "diff": min(5, 1 + i // 4), "body": ch["body"][:400]})
    ti = 0
    for e in entities:
        if e in id_of:
            continue
        ti += 1
        nid = f"T{ti:02d}"
        id_of[e] = nid
        nodes.append({"id": nid, "title": e, "kind": "term", "course": book_name, "diff": min(5, 2 + int((ranks.get(e, 0) * 8))), "body": ""})
    edges = []
    edge_by_pair = {}
    # K-14：同一节点对的边按类型强度保留最强（prerequisite > attach > related），
    # 后到的强边替换先到的弱边（原先 sorted(a,b) 无向去重 + PMI 先插会静默顶掉先修边）。
    _ETYPE_RANK = {"prerequisite": 0, "attach": 1, "related": 2}

    def add(a, b, tg, w, et):
        if not a or not b or a == b:
            return
        ek = (a, b) if a < b else (b, a)
        old = edge_by_pair.get(ek)
        if old is not None:
            if (_ETYPE_RANK[et], w) < (_ETYPE_RANK[old["etype"]], old["w"]):
                old["from"], old["to"], old["tg"], old["w"], old["etype"] = a, b, tg, w, et
            return
        e = {"from": a, "to": b, "tg": tg, "w": w, "etype": et}
        edge_by_pair[ek] = e
        edges.append(e)

    for i in range(len(chapters) - 1):
        add(chapters[i]["id"], chapters[i + 1]["id"], False, 0.85, "prerequisite")
    # 依赖句式（强边先插，K-14）
    for c in top_entities(entities, id_of, max_terms)[:12]:
        try:
            rx = re.compile(
                r"(?:基于|先学|掌握|了解|学会)\s*" + re.escape(c) + r"\s*(?:后|之后|再|然后|才能|才能理解|的基础上)\s*([一-鿿A-Za-z0-9_·、]{2,12})"
            )
        except re.error:
            continue
        for m in rx.finditer(text[:100000]):
            tgt = m.group(1).strip()
            hit = next((x for x in entities if tgt in x or x in tgt), None)
            if hit and hit != c:
                add(id_of.get(c), id_of.get(hit), True, 1.3, "prerequisite")
    # PMI 共现
    top = top_entities(entities, id_of, max_terms)
    tokens = tokenize(text[:150000])
    co, cnt = defaultdict(int), defaultdict(int)
    window = 8
    top_set = set(top)
    for i, t in enumerate(tokens):
        if t not in top_set:
            continue
        cnt[t] += 1
        for j in range(i + 1, min(len(tokens), i + window)):
            u = tokens[j]
            if u not in top_set or u == t:
                continue
            key = (t, u) if t < u else (u, t)
            co[key] += 1
    total = max(sum(cnt.values()), 1)
    for (a, b), c in co.items():
        p = pmi(c, cnt[a], cnt[b], total)
        if p >= 1.0:
            add(id_of.get(a), id_of.get(b), False, min(p / 6, 2), "related")
    # 词挂章节：K-14 归属按「出现次数最多的章」（定义性出现），不再取第一个包含它的章
    # （正文含目录/前言时老逻辑会把全书术语错挂第 1 章）
    for e in top:
        best = None
        for c in chapters:
            n = c["body"].count(e)
            if n > 0 and (best is None or n > best[0]):
                best = (n, c)
        ch = best[1] if best else (chapters[0] if chapters else None)
        if ch:
            add(ch["id"], id_of.get(e), False, 0.55, "attach")
    before = len(edges)
    edges = remove_cycles(edges)
    return {"name": book_name, "chapters": chapters, "nodes": nodes, "edges": edges,
            "cyclesRemoved": before - len(edges)}


def top_entities(entities, id_of, max_terms):
    """构图用的术语子集（章节节点之外的实体，按原顺序截断）。"""
    return [e for e in entities if e not in id_of or id_of.get(e, "").startswith("T")][:max_terms]


# ───────────────────────── simple-mind-map 树 / Markdown ─────────────────────────
def to_mindmap_tree(graph: dict) -> dict:
    """simple-mind-map 数据结构：root + children 逻辑树。"""
    root = {"data": {"text": graph["name"], "expand": True}, "children": []}
    ch_nodes = [n for n in graph["nodes"] if n["kind"] == "chapter"]
    terms = [n for n in graph["nodes"] if n["kind"] == "term"]
    # 章节 → 挂术语（记录已挂术语，避免再进「核心概念」）
    attached_terms = set()
    for ch in ch_nodes:
        child = {"data": {"text": ch["title"], "expand": True}, "children": []}
        for e in graph["edges"]:
            if e["from"] == ch["id"] and e["to"].startswith("T"):
                t = next((x for x in terms if x["id"] == e["to"]), None)
                if t:
                    child["children"].append({"data": {"text": t["title"], "expand": True}, "children": []})
                    attached_terms.add(t["title"])
        root["children"].append(child)
    leftover = [t for t in terms if t["title"] not in attached_terms]
    if leftover:
        bucket = {"data": {"text": "核心概念", "expand": True}, "children": []}
        for t in leftover[:20]:
            bucket["children"].append({"data": {"text": t["title"], "expand": True}, "children": []})
        root["children"].append(bucket)
    return root


def to_markdown(tree: dict, depth=0) -> str:
    line = ("  " * depth) + ("- " if depth else "# ") + tree["data"]["text"] + "\n"
    for c in tree.get("children") or []:
        line += to_markdown(c, depth + 1)
    return line


def graph_stats(g):
    return {
        "nodes": len(g["nodes"]),
        "edges": len(g["edges"]),
        "chapters": sum(1 for n in g["nodes"] if n["kind"] == "chapter"),
        "terms": sum(1 for n in g["nodes"] if n["kind"] == "term"),
    }
