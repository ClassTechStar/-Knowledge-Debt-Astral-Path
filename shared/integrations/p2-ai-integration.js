/* ═══════════════════════════════════════════════════════════════════
 * P2 迁移：试卷作业错题识别 + 智能体三级路由（源：Astralpath index.html）
 * 注入位置：.cloudflare/pages/index.html 尾部 </body> 前（p1-ai-integration.js 之后）
 * 对应开发方案 P2-1（模块 D4-D8）/ P2-3（模块 F1-F6）。
 * 依赖：p0-ai-integration.js 的 window.__AP_MIMO__（可选，流式对话用）
 *     基准页面已有 esc / activeBook / activeNodes / title / save / $ / S（可选）
 * ═══════════════════════════════════════════════════════════════════ */
(function () {
  "use strict";

  /* ═══ 1. 试卷 / 作业 / 错题本地结构化（P2-1，源 L3501-3634）═══════════ */
  var EXAM_MAX_ITEMS = 300;

  function paperTypeLabel(t) { return t === "hw" ? "作业" : (t === "wrong" ? "错题" : "试卷"); }
  function qTypeLabel(t) { return t === "choice" ? "选择题" : (t === "blank" ? "填空题" : (t === "judge" ? "判断题" : (t === "solve" ? "解答题" : "题目"))); }

  // 行预处理：统一换行 + 剥离 [page N]/[slide N]/[image N]/[media N]/[备注 N] 标记（源 t10ExamLines）
  function examLines(text) {
    return String(text || "").replace(/\r\n?/g, "\n").split("\n")
      .map(function (l) { return l.replace(/^\s*\[(?:page|slide|image|media|备注)\s*\d+\]\s*/, "").replace(/\s+$/g, ""); })
      .filter(function (l) { return l.trim().length > 0; });
  }

  // 板块标题（源 t10SectionOf）
  function sectionOf(line) {
    var s = String(line || "").trim();
    if (!s || s.length > 26) return null;
    if (/[?？。；，,]$/.test(s)) return null;
    if (/^第\s*[一二三四五六七八九十]+\s*[大部分大题篇]/.test(s)) return s;
    if (/^[（(][一二三四五六七八九十]+[）)]\s*\S/.test(s)) return s;
    var m = /^[一二三四五六七八九十]+、\s*(.{1,22})$/.exec(s);
    if (m) {
      var body = m[1];
      if (body.length <= 10 && !/[?？]/.test(body)) return s;
      if (/(选择|填空|判断|解答|计算|应用|证明|简答|论述|综述|分析|改错|写作|翻译|名词解释|案例|听力|阅读|综合|基础|拓展|题目|试题|部分)/.test(body)) return s;
      return null;
    }
    if (/^(选择|填空|判断|解答|计算|应用|证明|简答|论述|综述|分析|改错|写作|翻译|名词解释|案例)题?$/.test(s)) return s;
    return null;
  }

  // 题头（源 t10QuestionHead）：1. / 1、/ 1）/ (1) / 第1题
  function questionHead(line) {
    var s = String(line || "").trim();
    var m = /^(\d{1,3})\s*[.、．)]\s*(\S.*)$/.exec(s);
    if (m) return { no: parseInt(m[1], 10), rest: m[2].trim() };
    m = /^[（(](\d{1,3})[）)]\s*(\S.*)$/.exec(s);
    if (m) return { no: parseInt(m[1], 10), rest: m[2].trim() };
    m = /^第\s*(\d{1,3})\s*题\s*[:：]?\s*(.*)$/.exec(s);
    if (m) return { no: parseInt(m[1], 10), rest: (m[2] || "").trim() };
    return null;
  }

  // 答案行（源 t10AnswerOf）：支持 "1.A 2.C" 批量分发
  function answerOf(line) {
    var s = String(line || "").trim();
    var m = /^(?:【\s*答案\s*】|参考答案|答案|答)\s*[:：]?\s*(\S.*)$/.exec(s);
    if (!m) return [];
    var body = m[1].trim();
    var multi = body.match(/\d{1,3}\s*[.、．)]\s*\S{1,24}/g);
    if (multi && multi.length >= 2) {
      return multi.map(function (x) {
        var mm = /^(\d{1,3})\s*[.、．)]\s*(.+)$/.exec(x.trim());
        return mm ? { no: parseInt(mm[1], 10), text: mm[2].trim() } : null;
      }).filter(Boolean);
    }
    var one = /^(\d{1,3})\s*[.、．)]\s*(.+)$/.exec(body);
    if (one) return [{ no: parseInt(one[1], 10), text: one[2].trim() }];
    return [{ no: null, text: body }];
  }

  // 同行内联选项（源 t10InlineOptions）
  function inlineOptions(stem) {
    var s = String(stem || "");
    var marks = s.match(/(?:^|\s)[A-Da-d]\s*[.、．)]/g) || [];
    if (marks.length < 2) return null;
    var parts = s.split(/(?:^|\s)[A-Da-d]\s*[.、．)]/).map(function (x) { return x.trim(); }).filter(Boolean);
    if (parts.length < 3) return null;
    return { head: parts[0], options: parts.slice(1).map(function (t, i) { return String.fromCharCode(65 + i) + "." + t; }) };
  }

  // 题型推断（源 t10QType）
  function qTypeOf(item, section) {
    var s = String(section || "") + " " + String((item && item.stem) || "").slice(0, 40);
    if ((item && item.options || []).length >= 2) return "choice";
    if (/填空|填写/.test(s)) return "blank";
    if (/判断/.test(s)) return "judge";
    if (/解答|计算|应用|证明|简答|论述|综述|分析|改错|案例|翻译|写作/.test(s)) return "solve";
    return "unknown";
  }

  // 结构化主流程（源 structureExamText）
  function structureExamText(text) {
    var lines = examLines(text);
    var items = [], cur = null, section = null, secOrder = [], ansPool = [];
    function flush() { if (cur && (cur.stem || cur.raw)) items.push(cur); cur = null; }
    for (var i = 0; i < lines.length && items.length < EXAM_MAX_ITEMS; i++) {
      var ln = lines[i];
      var sec = sectionOf(ln);
      if (sec) { section = sec; if (secOrder.indexOf(sec) < 0) secOrder.push(sec); continue; }
      var qh = questionHead(ln);
      if (qh) { flush(); cur = { no: items.length + 1, rawNo: qh.no, section: section, stem: qh.rest, options: [], answer: "", qtype: "", raw: ln, kps: [] }; continue; }
      var om = /^\s*([A-Da-d])\s*[.、．)]\s*(.+)$/.exec(ln);
      if (om && cur) { cur.options.push(om[1].toUpperCase() + "." + om[2].trim()); cur.raw += "\n" + ln; continue; }
      var an = answerOf(ln);
      if (an && an.length) {
        if (cur && an.length === 1 && an[0].no == null) { cur.answer = an[0].text; cur.raw += "\n" + ln; }
        else { an.forEach(function (a) { ansPool.push(a); }); }
        continue;
      }
      if (cur) { cur.stem += (cur.stem ? "\n" : "") + ln; cur.raw += "\n" + ln; }
    }
    flush();
    if (items.length > EXAM_MAX_ITEMS) items = items.slice(0, EXAM_MAX_ITEMS); // 硬上限（业务约束 ≤300）
    items.forEach(function (it) {
      if (it.options.length) return;
      var io = inlineOptions(it.stem);
      if (io) { it.stem = io.head; it.options = io.options; }
    });
    items.forEach(function (it) {
      it.qtype = qTypeOf(it, it.section);
      it.stem = String(it.stem || "").replace(/\n{2,}/g, "\n").trim().slice(0, 600);
      it.raw = String(it.raw || "").slice(0, 900);
    });
    ansPool.forEach(function (a) {
      if (a.no == null) return;
      var it = null;
      for (var j = 0; j < items.length; j++) if (items[j].rawNo === a.no && !items[j].answer) { it = items[j]; break; }
      if (it) it.answer = a.text;
    });
    var un = items.filter(function (it) { return !it.answer; });
    if (un.length && ansPool.length && ansPool.every(function (a) { return a.no == null; }) && ansPool.length === un.length) {
      un.forEach(function (it, i) { it.answer = ansPool[i].text; });
    }
    var sections = secOrder.map(function (t) {
      return { title: t, items: items.filter(function (it) { return it.section === t; }).length };
    });
    if (!sections.length && items.length) sections.push({ title: "全部题目", items: items.length });
    return { items: items, sections: sections };
  }

  /* ── 知识点词库 / 匹配 / 层级图谱（源 examKpLexicon / matchExamKps / buildExamKG）── */
  function examKpLexicon(book) {
    var lex = [], seen = {};
    function add(t, w, chTitle, chId, nodeId) {
      var k = String(t || "").trim();
      if (k.length < 2 || k.length > 24 || seen[k]) return;
      seen[k] = 1;
      lex.push({ title: k, w: w || 1, chTitle: chTitle || null, chId: chId || null, nodeId: nodeId || null });
    }
    var nodes = (book && book.nodes) || [];
    nodes.forEach(function (n) {
      if (n.kind === "chapter") add(n.title, 2.2, n.title, n.id, n.id);
      else if (n.kind === "term") add(n.title, 1.6, null, null, n.id);
    });
    return lex;
  }

  // 长词优先 + 位置不重叠贪心匹配（源 matchExamKps；T10.1 同题不重复命中）
  function matchExamKps(text, lex, limit) {
    var s = String(text || "");
    if (!s.trim() || !lex || !lex.length) return [];
    var hits = [];
    for (var i = 0; i < lex.length && hits.length < 600; i++) {
      var t = lex[i], idx = s.indexOf(t.title);
      while (idx >= 0) { hits.push({ t: t, pos: idx, end: idx + t.title.length }); idx = s.indexOf(t.title, idx + 1); }
    }
    hits.sort(function (a, b) { return (b.t.title.length - a.t.title.length) || (b.t.w - a.t.w); });
    var taken = [], out = [], seenT = {};
    for (var k = 0; k < hits.length && out.length < (limit || 4); k++) {
      var h = hits[k], overlap = false;
      for (var j = 0; j < taken.length; j++) if (h.pos < taken[j].end && h.end > taken[j].pos) { overlap = true; break; }
      if (overlap) continue;
      if (seenT[h.t.title]) continue;
      seenT[h.t.title] = 1;
      taken.push(h); out.push(h.t);
    }
    return out;
  }

  // Kahn 拓扑清环（源 removeCyclesTopo，与 P1 C# 泛型实现语义一致）
  function removeCyclesTopo(nodes, edges) {
    var deg = {}, adj = {};
    nodes.forEach(function (n) { deg[n.id] = 0; adj[n.id] = []; });
    edges.forEach(function (e) {
      if (!e.from || !e.to || e.from === e.to) return;
      if (!(e.from in deg) || !(e.to in deg)) return;
      adj[e.from].push(e.to); deg[e.to] = (deg[e.to] || 0) + 1;
    });
    var order = [], used = {}, q = [];
    nodes.forEach(function (n) { if (!deg[n.id]) { q.push(n.id); used[n.id] = 1; } });
    for (var h = 0; h < q.length; h++) {
      var x = q[h]; order.push(x);
      (adj[x] || []).forEach(function (y) { if (--deg[y] === 0) { q.push(y); used[y] = 1; } });
    }
    if (order.length >= nodes.length) return { edges: edges.filter(function (e) { return e.from && e.to && e.from !== e.to; }), cut: 0 };
    var pos = {}; order.forEach(function (id, ix) { pos[id] = ix; });
    var rest = nodes.filter(function (n) { return !used[n.id]; }).map(function (n) { return n.id; });
    var kept = [], cut = 0;
    edges.forEach(function (e) {
      if (!e.from || !e.to || e.from === e.to) return;
      var pa = (e.from in pos) ? pos[e.from] : rest.indexOf(e.from) + nodes.length;
      var pb = (e.to in pos) ? pos[e.to] : rest.indexOf(e.to) + nodes.length;
      if (pa < pb) kept.push(e); else cut++;
    });
    return { edges: kept, cut: cut };
  }

  function examTriples(nodes, edges) {
    var titles = {};
    nodes.forEach(function (n) { titles[n.id] = n.title; });
    var label = { prerequisite: "先修于", related: "同题共现", attach: "归属", tested: "考查" };
    return edges.map(function (e) {
      return {
        subject: titles[e.from] || e.from, relation: label[e.etype] || e.etype || "关联",
        object: titles[e.to] || e.to, weight: +(e.w || 0).toFixed(3), confidence: e.conf != null ? +e.conf.toFixed(3) : null
      };
    });
  }

  // 三层节点图谱（源 buildExamKG：主题/知识点/题目 + attach/tested/related/prerequisite）
  function buildExamKG(paper, book, fallbackKps) {
    var lex = examKpLexicon(book);
    var nodes = [], edges = [], seenEk = {}, topics = [], kpMap = {};
    function ek(a, b) { return [a, b].sort().join(">"); }
    function topicNode(title, source) {
      var id = "TP" + String(topics.length + 1).padStart(2, "0");
      topics.push({ id: id, title: title, source: source });
      nodes.push({ id: id, title: title, diff: 2, course: paper.name, kind: "topic", source: source, body: source });
      return id;
    }
    // ① 题目 → 知识点
    (paper.items || []).forEach(function (it) {
      it.kps = matchExamKps((it.stem || "") + "\n" + (it.options || []).join("\n") + "\n" + (it.answer || ""), lex, 4)
        .map(function (t) { return t.title; });
    });
    var kpTitles = [], kpSeen = {};
    (paper.items || []).forEach(function (it) {
      (it.kps || []).forEach(function (k) { if (!kpSeen[k]) { kpSeen[k] = 1; kpTitles.push(k); } });
    });
    // ② 兜底：词库未命中时用试卷自身抽取的知识点
    if (!kpTitles.length && fallbackKps) {
      fallbackKps.forEach(function (t) { if (t && !kpSeen[t]) { kpSeen[t] = 1; kpTitles.push(t); } });
    }
    // ③ 知识点节点
    kpTitles.forEach(function (k, i) {
      var id = "KP" + String(i + 1).padStart(2, "0");
      var l = null;
      for (var j = 0; j < lex.length; j++) if (lex[j].title === k) { l = lex[j]; break; }
      kpMap[k] = { id: id, lex: l };
      nodes.push({ id: id, title: k, diff: 2, course: paper.name, kind: "kp", body: "" });
    });
    // ④ 主题节点：教材章节归属优先，否则试卷板块
    var topicByKey = {};
    function topicFor(k, lexEntry) {
      var key = null, src = null, title = null;
      if (lexEntry && lexEntry.chTitle) { key = "B:" + lexEntry.chTitle; src = "教材章节"; title = lexEntry.chTitle; }
      if (!key) {
        var hit = (paper.items || []).find(function (it) { return (it.kps || []).indexOf(k) >= 0 && it.section; });
        if (hit) { key = "S:" + hit.section; src = "试卷板块"; title = hit.section; }
      }
      if (!key) { key = "S:全部题目"; src = "试卷板块"; title = "全部题目"; }
      if (!topicByKey[key]) topicByKey[key] = topicNode(title, src);
      return topicByKey[key];
    }
    kpTitles.forEach(function (k) {
      var tid = topicFor(k, kpMap[k] && kpMap[k].lex);
      edges.push({ from: tid, to: kpMap[k].id, tg: false, w: 0.6, etype: "attach", conf: 0.8 });
    });
    // ⑤ 题目节点（上限 120）
    var shown = Math.min((paper.items || []).length, 120);
    for (var i = 0; i < shown; i++) {
      var it = paper.items[i];
      var id = "Q" + String(i + 1).padStart(3, "0");
      nodes.push({
        id: id, title: "题" + (i + 1) + " " + String(it.stem || "").replace(/\s+/g, " ").slice(0, 14),
        diff: 2, course: paper.name, kind: "item", qtype: it.qtype, no: i + 1, body: String(it.stem || "").slice(0, 120)
      });
      (it.kps || []).forEach(function (k) {
        if (kpMap[k]) edges.push({ from: kpMap[k].id, to: id, tg: false, w: 0.5, etype: "tested", conf: 0.7 });
      });
    }
    // ⑥ 同题共现
    var co = {};
    (paper.items || []).forEach(function (it) {
      var ks = it.kps || [];
      for (var a = 0; a < ks.length; a++) for (var b = a + 1; b < ks.length; b++) {
        var key = [ks[a], ks[b]].sort().join("\t");
        co[key] = (co[key] || 0) + 1;
      }
    });
    Object.keys(co).forEach(function (key) {
      var p = key.split("\t");
      if (!kpMap[p[0]] || !kpMap[p[1]]) return;
      var kk = ek(kpMap[p[0]].id, kpMap[p[1]].id);
      if (seenEk[kk]) return; seenEk[kk] = 1;
      edges.push({ from: kpMap[p[0]].id, to: kpMap[p[1]].id, tg: false, w: Math.min(0.4 + co[key] * 0.2, 1.2), etype: "related", conf: 0.6 });
    });
    // 继承教材先修边
    var be = (book && book.edges) || [];
    be.forEach(function (e) {
      var ft = null, tt = null;
      for (var j = 0; j < lex.length; j++) {
        if (lex[j].nodeId === e.from && !ft) ft = lex[j].title;
        if (lex[j].nodeId === e.to && !tt) tt = lex[j].title;
      }
      if (!ft || !tt || !kpMap[ft] || !kpMap[tt]) return;
      var kk = ek(kpMap[ft].id, kpMap[tt].id);
      if (seenEk[kk]) return; seenEk[kk] = 1;
      edges.push({ from: kpMap[ft].id, to: kpMap[tt].id, tg: !!e.tg, w: 1.1, etype: "prerequisite", conf: 0.85 });
    });
    var cleaned = removeCyclesTopo(nodes, edges);
    paper.nodes = nodes;
    paper.edges = cleaned.edges;
    paper.kgTriples = examTriples(nodes, cleaned.edges);
    paper.stats = {
      items: (paper.items || []).length, kps: kpTitles.length, topics: topics.length,
      edges: cleaned.edges.length, shownItems: shown, cut: cleaned.cut || 0
    };
    return paper;
  }

  // 统一入口（源 addExamPaper）：文本 → 结构化 → 图谱
  function addExamPaper(o, book, fallbackKps) {
    var st = structureExamText(o.text);
    var paper = {
      id: "p" + Date.now().toString(36) + Math.random().toString(36).slice(2, 6),
      type: o.type || "exam", name: o.name || "", source: o.source || "file", ocr: !!o.ocr,
      engine: o.engine || "", createdAt: Date.now(), items: st.items, sections: st.sections,
      nodes: [], edges: [], kgTriples: [],
      stats: { items: st.items.length, kps: 0, topics: 0, edges: 0, shownItems: 0 },
      preview: String(o.text || "").slice(0, 4000)
    };
    buildExamKG(paper, book, fallbackKps);
    return paper;
  }

  window.__AP_EXAM__ = {
    structure: structureExamText,
    buildKg: buildExamKG,
    addPaper: addExamPaper,
    lexicon: examKpLexicon,
    matchKps: matchExamKps,
    qTypeLabel: qTypeLabel,
    paperTypeLabel: paperTypeLabel,
    maxItems: EXAM_MAX_ITEMS
  };

  /* ═══ 2. 智能体三级路由（P2-3，源 L6635-7023）══════════════════════════
     ① 规则锚点（26 意图，与 tools/agent-intents.json 逐字一致）
     ② 轻量 ML：本地朴素贝叶斯分类器（源为 ONNX 模型；此处以字符 n-gram 朴素贝叶斯等价实现，
        能力对齐「口语化表达补强」，不引入外部模型文件依赖）
     ③ 模糊锚点二次确认（子序列覆盖率 ≥0.7）
     ④ 与 C# AgentRouter 语义对齐：TauExec=0.75 TauClarify=0.60 MaxClarify=2 */

  var CRISIS = [
    "不想活",
    "自杀",
    "轻生",
    "结束生命",
    "自残",
    "伤害自己",
    "跳楼",
    "上吊",
    "suicide",
    "kill myself",
    "活不下去"
  ];
  var BANNED = [
    "身份证",
    "银行卡密码",
    "详细住址"
  ];
  var NEGATIVE = [
    "哈哈",
    "呵呵",
    "随便",
    "没事了",
    "thanks",
    "thank you",
    "ok",
    "好的"
  ];
  var INTENTS = [
    ["crisis.handoff", []],
    ["debt.diagnose", [ "知识债", "债边", "诊断", "红边", "薄弱", "搞不清", "漏洞", "哪里不会", "哪里薄弱", "帮我分析" ]],
    ["debt.explain", [ "解释", "为什么", "怎么来的", "公式", "讲解", "怎么回事", "什么意思", "怎么理解", "什么原理" ]],
    ["plan.create", [ "计划", "14天", "安排", "复习计划", "做个计划", "怎么安排", "复习规划" ]],
    ["plan.rebalance", [ "减负", "重排", "调整计划", "太重", "换个计划", "改一下计划" ]],
    ["today.tasks", [ "今日", "今天做", "今日任务", "今天学什么", "现在学什么", "该学什么", "今天干什么" ]],
    ["practice.start", [ "练习", "做题", "小测", "测验", "来几道", "出几道", "出个题", "刷几道", "做几道", "练一下" ]],
    ["progress.check", [ "进度", "销账", "进展", "streak", "学得怎么样", "看看进度", "掌握怎么样" ]],
    ["graph.view", [ "图谱", "识网", "知识图谱", "思维导图", "关系图", "知识网", "看看图" ]],
    ["material.parse", [ "解析资料", "上传", "OCR", "教材", "藏书", "解析文件", "导入教材", "上传课本" ]],
    ["material.list", [ "列出资料", "教材列表", "藏书", "有哪些书", "书单", "几本书", "已解析教材", "有什么教材" ]],
    ["material.switch", [ "切换教材", "换书", "切书", "切换到", "换一本", "切到", "切过去", "换个教材", "换本书" ]],
    ["kb.search", [ "知识库", "检索", "什么是", "查资料", "文档", "搜索", "搜一下", "查一下", "找找", "章节", "帮我找", "找找看", "查一查", "讲一下", "知识点", "介绍", "概要", "概述", "概括" ]],
    ["kb.ingest", [ "入库", "上传文档", "发布知识" ]],
    ["profile.view", [ "画像", "我的标签", "学习画像", "我的情况" ]],
    ["profile.optout", [ "退出画像", "关闭画像", "opt-out" ]],
    ["teacher.hotspots", [ "热点", "班级", "共性问题", "大家的情况" ]],
    ["consent.grant", [ "授权", "同意可见", "consent", "允许查看" ]],
    ["consent.revoke", [ "撤销", "取消授权", "关闭可见" ]],
    ["whatif.simulate", [ "what-if", "如果", "模拟", "假设", "假如", "要是", "试想" ]],
    ["narrative.read", [ "故事", "叙事", "讲解", "讲个故事", "举个例子", "给我讲讲", "讲一讲" ]],
    ["sale.check", [ "销账检查", "能否销账", "销账条件", "能销账吗", "销账了没" ]],
    ["chapter.read", [ "目录", "读一下", "讲了什么", "讲了啥", "章节", "这一章", "内容是什么", "翻到", "第几章", "前几章", "本章", "这章", "有什么内容", "不明白", "不懂", "不理解", "不清楚" ]],
    ["graph.path", [ "什么关系", "怎么联系", "有没有关", "路径", "谁先", "先后", "前置", "怎么到", "怎么学", "学习顺序", "什么联系", "有关吗", "先后顺序" ]],
    ["meta.help", [ "帮助", "怎么用", "说明", "你能干什么", "你会什么", "功能有哪些" ]],
    ["meta.feedback", [ "反馈", "建议", "不好用" ]]
  ];
  var TauExec = 0.75, TauClarify = 0.60, MaxClarify = 2;

  function normalize(s) {
    return String(s || "").replace(/[！-～]/g, function (c) { return String.fromCharCode(c.charCodeAt(0) - 0xFEE0); })
      .replace(/　/g, " ").replace(/\s+/g, " ").toLowerCase().trim();
  }
  // 口语归一（源 normSpoken，L6640-6642）：啥→什么、咋→怎么、去空格
  function normSpoken(s) {
    return String(s).replace(/啥/g, "什么").replace(/咋/g, "怎么").replace(/嘞/g, "").replace(/咯/g, "").replace(/ /g, "");
  }
  function containsAny(raw, list) {
    var n = normalize(raw); if (!n) return false;
    for (var i = 0; i < list.length; i++) if (n.indexOf(normalize(list[i])) >= 0) return true;
    return false;
  }
  // 模糊锚点二次确认（源 fuzzyAnchorConfirm，L6644-6657）：子序列覆盖率 ≥0.7
  function fuzzyAnchorConfirm(intentId, t) {
    var pair = INTENTS.filter(function (p) { return p[0] === intentId; })[0];
    if (!pair) return "";
    var best = "", bestCov = 0;
    for (var i = 0; i < pair[1].length; i++) {
      var a = pair[1][i];
      if (a.length < 3) continue;
      var idx = -1, cnt = 0;
      for (var k = 0; k < a.length; k++) {
        var j = t.indexOf(a[k], idx + 1);
        if (j < 0) break;
        idx = j; cnt++;
      }
      var cov = cnt / a.length;
      if (cov >= 0.7 && cov > bestCov) { bestCov = cov; best = a; }
    }
    return bestCov > 0 ? best + "[" + Math.round(bestCov * 100) + "%]" : "";
  }

  // 规则打分（与 C# AgentRouter.Score 同构：主锚点 + 其余饱和累加 + 覆盖率奖励）
  function scoreIntents(norm) {
    var scores = [];
    for (var i = 0; i < INTENTS.length; i++) {
      var id = INTENTS[i][0], anchors = INTENTS[i][1];
      if (id === "crisis.handoff") continue;
      var maxS = 0, sumOthers = 0, matchedLen = 0, bestAnchor = "";
      for (var j = 0; j < anchors.length; j++) {
        var a = normalize(anchors[j]);
        if (!a || norm.indexOf(a) < 0) continue;
        var sc = 0.5 + 0.5 * Math.min(1, a.length / 8);
        matchedLen += a.length;
        if (sc > maxS) { sumOthers += maxS; maxS = sc; bestAnchor = anchors[j]; }
        else sumOthers += sc;
      }
      if (maxS <= 0) continue;
      var score = maxS + 0.5 * Math.min(sumOthers, 1);
      score += 0.15 * Math.min(1, matchedLen / Math.max(6, norm.length));
      score = Math.max(0, Math.min(1, score));
      scores.push({ id: id, score: +score.toFixed(6), anchor: bestAnchor });
    }
    scores.sort(function (a, b) { return (b.score - a.score) || (a.id < b.id ? -1 : 1); });
    return scores;
  }

  /* 轻量 ML 回退（源为 ONNX 分类器；此处以字符 bigram 朴素贝叶斯等价实现，
     仅在规则未命中时给出低置信候选，再由 fuzzyAnchorConfirm 二次确认）。 */
  var _nbModel = null;
  function buildNbModel() {
    if (_nbModel) return _nbModel;
    var docs = {}, vocab = 0;
    INTENTS.forEach(function (pair) {
      var id = pair[0];
      if (id === "crisis.handoff" || id === "meta.help") return;
      docs[id] = {};
      pair[1].forEach(function (a) {
        var an = normSpoken(normalize(a));
        for (var i = 0; i < an.length; i++) {
          var g = an.slice(i, i + 2);
          if (g.length < 2) continue;
          docs[id][g] = (docs[id][g] || 0) + 1;
          vocab++;
        }
      });
    });
    _nbModel = { docs: docs, vocab: Math.max(1, vocab), labels: Object.keys(docs) };
    return _nbModel;
  }
  function mlGuess(text) {
    var t = normSpoken(normalize(text));
    if (t.length < 2) return null;
    var m = buildNbModel(), total = m.labels.length, best = null, bestScore = -1e9;
    var grams = [];
    for (var i = 0; i < t.length - 1; i++) grams.push(t.slice(i, i + 2));
    m.labels.forEach(function (id) {
      var count = 0;
      for (var g = 0; g < grams.length; g++) count += (m.docs[id][grams[g]] || 0);
      var sum = Math.log(1 / total) + Math.log((count + 1) / (m.vocab + 2));
      // 覆盖率归一：命中 gram 数 / 总 gram 数
      var hitGrams = grams.filter(function (g2) { return (m.docs[id][g2] || 0) > 0; }).length;
      var cov = grams.length ? hitGrams / grams.length : 0;
      var score = sum + 2.0 * cov;
      if (cov < 0.30) score -= 10; // 覆盖不足视为不可信
      if (score > bestScore) { bestScore = score; best = id; }
    });
    return best ? { id: best, score: bestScore } : null;
  }

  // 三级路由主流程（源 intentRoute + routeWithML）
  function routeWithML(text) {
    var raw = String(text || "");
    // ① 危机词最高优先级
    if (containsAny(raw, CRISIS)) return { id: "crisis.handoff", decision: "crisis.handoff", trace: "R0:crisis" };
    var norm = normSpoken(normalize(raw));
    // ② 负例快落（≥60% 占比）
    if (norm && containsAny(norm, NEGATIVE)) {
      var longest = 0;
      NEGATIVE.forEach(function (w) { var nw = normalize(w); if (norm.indexOf(nw) >= 0) longest = Math.max(longest, nw.length); });
      var ratio = norm.length ? longest / norm.length : 0;
      if (ratio >= 0.6) return { id: "meta.help", decision: "fallback", trace: "R2.5:negative;ratio=" + ratio.toFixed(2) };
    }
    // ③ 规则锚点
    var ranked = scoreIntents(norm);
    if (ranked.length) {
      var best = ranked[0], second = ranked[1] || null;
      var ambiguous = second && (best.score - second.score) < 0.08;
      if (best.score >= TauExec && !ambiguous)
        return { id: best.id, score: best.score, decision: "execute", trace: "R3:anchor:" + best.anchor };
      if (best.score >= TauClarify)
        return { id: best.id, score: best.score, decision: ambiguous ? "clarify" : "execute", trace: "R3:mid;score=" + best.score.toFixed(2) };
    }
    // ④ 模糊锚点二次确认
    for (var i = 0; i < ranked.length; i++) {
      var conf = fuzzyAnchorConfirm(ranked[i].id, norm);
      if (conf) return { id: ranked[i].id, score: ranked[i].score, decision: "execute", trace: "R4:fuzzy:" + conf };
    }
    // ⑤ 轻量 ML 回退
    var guess = mlGuess(raw);
    if (guess) {
      var confirm = fuzzyAnchorConfirm(guess.id, norm);
      if (confirm) return { id: guess.id, score: 0.6, decision: "execute", trace: "R5:ml:" + guess.id + ";fuzzy:" + confirm };
    }
    // ⑥ 兜底
    return { id: "meta.help", score: 0, decision: "fallback", trace: "R6:fallback" };
  }

  // 输出侧脱敏（源 sanitize，L6717）
  function sanitizeOutput(text) {
    var t = String(text || "");
    for (var i = 0; i < BANNED.length; i++) if (t.indexOf(BANNED[i]) >= 0) return "这段内容暂时无法展示，请查看教材原文或联系老师。";
    return t;
  }

  window.__AP_ROUTER__ = {
    route: routeWithML,
    intents: INTENTS,
    crisis: CRISIS,
    banned: BANNED,
    negative: NEGATIVE,
    normalize: normalize,
    normSpoken: normSpoken,
    fuzzyConfirm: fuzzyAnchorConfirm,
    score: scoreIntents,
    sanitize: sanitizeOutput,
    thresholds: { exec: TauExec, clarify: TauClarify, maxClarify: MaxClarify }
  };

  /* ═══ 3. 智能体流式对话（P2-3，源 reply L7053-7114 的精简适配）═════════ */
  function replyAgent(text, onChunk) {
    var r = routeWithML(text);
    var M = window.__AP_MIMO__;
    var cloudOK = M && M.ready && M.ready() && navigator.onLine !== false && !containsAny(text, CRISIS) && !containsAny(text, BANNED);
    var cloudIntent = r.decision === "fallback" || r.id === "meta.help" ||
      ["kb.search", "chapter.read", "debt.explain", "narrative.read", "graph.path", "today.tasks", "material.list", "material.switch"].indexOf(r.id) >= 0;
    if (cloudOK && cloudIntent) {
      return M.complete([
        { role: "system", content: "你是「知债：星穹学途」的学习助手。规则：1) 聚焦学习/教材/计划话题；遇心理危机信号立即建议联系信任的人或当地心理援助热线；2) 数字与诊断结论只引用系统上下文，不要编造；3) 教材上下文不足时可用通用知识补充，教材之外内容以【拓展】开头标注；4) 输出纯文本，不用 Markdown 标题与列表符号。" },
        { role: "user", content: String(text) }
      ], { maxTokens: 1200, timeoutMs: 60000, onDelta: function (acc, info) { if (onChunk) try { onChunk(acc, info); } catch (e) {} } })
        .then(function (res) {
          return { text: sanitizeOutput(String(res.text || "").trim()), channel: "cloud", route: r };
        })
        .catch(function () {
          return { text: localReply(r, text), channel: "local", route: r };
        });
    }
    return Promise.resolve({ text: sanitizeOutput(localReply(r, text)), channel: "local", route: r });
  }

  // 本地话术兜底（按意图给出可执行指引；数字不编造）
  function localReply(r, text) {
    switch (r.id) {
      case "crisis.handoff":
        return "听到你这样说我很担心。请马上联系你信任的人（家人、朋友、老师），或拨打当地心理援助热线 12356。你并不孤单，专业的人可以帮到你。";
      case "debt.diagnose": return "到「知债」页点「重新诊断」可查看红边与影响分排序；所有数字由本地公式计算。";
      case "plan.create": return "到「知债」页点「生成 14 天计划」即可；计划会过 K1–K5 合约校验。";
      case "practice.start": return "到「今日」页点「开始练习」，默认由 AI 基于教材全文命题。";
      case "graph.view": return "到「识网」页查看知识图谱，可点「AI 增强构图」补充概念与先修关系。";
      case "material.list": return "到「藏书阁」可查看已解析教材列表，或直接上传新的资料。";
      case "material.switch": return "到「藏书阁」点击教材卡片即可切换当前教材。";
      case "chapter.read": return "到「藏书阁」或「识网」可选择章节查看正文与知识点，也可直接把章节名说给我。";
      case "graph.path": return "到「识网」点节点可看先修与下游路径；先修顺序由图谱拓扑决定。";
      case "sale.check": return "到「知债」页点「销账检查」查看销账进度（连续达标 2 次且累计作答 ≥3 次）。";
      case "progress.check": return "到「今日」页查看今日战绩与薄弱项，或问「我能不能销账了」。";
      case "material.parse": return "到「藏书阁」上传 PDF / 图片 / Word / PPT / EPUB 等资料，扫描版会自动走离线 OCR。";
      case "profile.view": return "到「画像」页查看掌握度与学习风格标签。";
      default: return "我可以帮你：导入并解析教材、查看知识图谱与先修路径、按教材内容出题练习、诊断知识债并生成修复计划、识别试卷作业错题。直接说你想要什么即可。";
    }
  }

  window.__AP_AGENT__ = { reply: replyAgent };

  /* ═══ 4. UI 接线（试卷导入入口 + 智能体输入，存在即挂载）═══════════════ */
  document.addEventListener("DOMContentLoaded", function () {
    // 试卷/作业/错题文本导入：若页面有 #examText 与 #btnExamImport 则接线
    var btn = document.getElementById("btnExamImport");
    var ta = document.getElementById("examText");
    if (btn && ta) {
      btn.addEventListener("click", function () {
        var text = String(ta.value || "").trim();
        var status = document.getElementById("examStatus");
        if (!text) { if (status) { status.className = "status warn"; status.textContent = "请先粘贴试卷 / 作业 / 错题文本"; } return; }
        var book = (typeof activeBook === "function") ? activeBook() : null;
        var paper = addExamPaper({ text: text, name: "手动粘贴试卷", type: "exam", source: "paste" }, book);
        try { if (typeof S === "object" && S) { S.papers = S.papers || []; S.papers.unshift(paper); if (typeof save === "function") save(S); } } catch (e) {}
        if (status) {
          status.className = "status ok";
          status.textContent = "结构化完成：" + paper.stats.items + " 题 / " + paper.stats.kps + " 知识点 / " +
            paper.stats.topics + " 主题 / " + paper.stats.edges + " 边（题目节点渲染 " + paper.stats.shownItems + " 个）";
        }
      });
    }
  });

})();
