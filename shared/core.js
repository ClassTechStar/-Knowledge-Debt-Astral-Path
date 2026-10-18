/**
 * core.js —— 前端共享内核（方案 P2）
 * ===========================================================
 * 收编此前四处平行维护的共享逻辑：
 *   normalize            ← index.html L6644
 *   parseJsonLoose       ← index.html L6385（p0 内叫 jsonLoose，同实现）
 *   removeCyclesTopo     ← p1 L253 / p2 L182（两份逐字相同）
 *   INTENTS/CRISIS/BANNED/NEGATIVE ← 构建期由 tools/agent-intents.json 生成
 *
 * 使用方式：
 *   浏览器：<script src="shared/core.js"></script> → window.APCore
 *   Node  ：require("./shared/core.js")
 *
 * ⚠ 下方 GENERATED 块由 scripts/gen-shared-core.js 生成，**禁止手工编辑**。
 *   改意图表请改 tools/agent-intents.json 后跑：
 *     node scripts/gen-shared-core.js && python scripts/verify_agent_intents.py
 *
 * 行为等价由 scripts/verify_core.py 守卫：把 index.html / p1 / p2 的旧实现与新内核
 * 在同一组图/文本用例上双跑对拍，输出不一致即判红（方案 P2 的「双跑对比」灰度策略）。
 */
(function (root, factory) {
  "use strict";
  var api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  else root.APCore = api;
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  /** 全角归一 + 空白折叠 + 小写 + 去首尾空格（与 index.html normalize 逐字一致） */
  function normalize(s) {
    return String(s || "")
      .replace(/[！-～]/g, function (c) { return String.fromCharCode(c.charCodeAt(0) - 0xFEE0); })
      .replace(/　/g, " ")
      .replace(/\s+/g, " ")
      .toLowerCase()
      .trim();
  }

  /** 容错 JSON 解析：剥 ```json 围栏 → 整段 → 首个 {…} → 首个 […]（与 index.html parseJsonLoose 一致） */
  function parseJsonLoose(text) {
    var t = String(text || "").trim();
    t = t.replace(/^[\s\S]*?```(?:json)?\s*/i, "").replace(/```[\s\S]*$/, "").trim();
    try { return JSON.parse(t); } catch (e) {}
    var i = t.indexOf("{"), j = t.lastIndexOf("}");
    if (i >= 0 && j > i) { try { return JSON.parse(t.slice(i, j + 1)); } catch (e) {} }
    var i2 = t.indexOf("["), j2 = t.lastIndexOf("]");
    if (i2 >= 0 && j2 > i2) { try { return JSON.parse(t.slice(i2, j2 + 1)); } catch (e) {} }
    return null;
  }

  /** Kahn 拓扑清环：删去「回边」，保证知识图无环（p1/p2 同款，含悬空端点保护） */
  function removeCyclesTopo(nodes, edges) {
    var deg = {}, adj = {};
    nodes.forEach(function (n) { deg[n.id] = 0; adj[n.id] = []; });
    edges.forEach(function (e) {
      if (!e.from || !e.to || e.from === e.to) return;
      if (!(e.from in deg) || !(e.to in deg)) return;
      adj[e.from].push(e.to);
      deg[e.to] = (deg[e.to] || 0) + 1;
    });
    var order = [], used = {}, q = [];
    nodes.forEach(function (n) { if (!deg[n.id]) { q.push(n.id); used[n.id] = 1; } });
    for (var h = 0; h < q.length; h++) {
      var x = q[h];
      order.push(x);
      (adj[x] || []).forEach(function (y) { if (--deg[y] === 0) { q.push(y); used[y] = 1; } });
    }
    if (order.length >= nodes.length) {
      return { edges: edges.filter(function (e) { return e.from && e.to && e.from !== e.to; }), cut: 0 };
    }
    var pos = {};
    order.forEach(function (id, ix) { pos[id] = ix; });
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

  /* ==== GENERATED FROM tools/agent-intents.json — DO NOT EDIT ==== */
  /* generator: scripts/gen-shared-core.js · source: tools/agent-intents.json */
  var CRISIS = ["不想活","自杀","轻生","结束生命","自残","伤害自己","跳楼","上吊","suicide","kill myself","活不下去"];
  var BANNED = ["身份证","银行卡密码","详细住址"];
  var NEGATIVE = ["哈哈","呵呵","随便","没事了","thanks","thank you","ok","好的"];
  var INTENTS = [
    ["crisis.handoff", []],
    ["debt.diagnose", ["知识债", "债边", "诊断", "红边", "薄弱", "搞不清", "漏洞", "哪里不会", "哪里薄弱", "帮我分析"]],
    ["debt.explain", ["解释", "为什么", "怎么来的", "公式", "讲解", "怎么回事", "什么意思", "怎么理解", "什么原理"]],
    ["plan.create", ["计划", "14天", "安排", "复习计划", "做个计划", "怎么安排", "复习规划"]],
    ["plan.rebalance", ["减负", "重排", "调整计划", "太重", "换个计划", "改一下计划"]],
    ["today.tasks", ["今日", "今天做", "今日任务", "今天学什么", "现在学什么", "该学什么", "今天干什么"]],
    ["practice.start", ["练习", "做题", "小测", "测验", "来几道", "出几道", "出个题", "刷几道", "做几道", "练一下"]],
    ["progress.check", ["进度", "销账", "进展", "streak", "学得怎么样", "看看进度", "掌握怎么样"]],
    ["graph.view", ["图谱", "识网", "知识图谱", "思维导图", "关系图", "知识网", "看看图"]],
    ["material.parse", ["解析资料", "上传", "OCR", "教材", "藏书", "解析文件", "导入教材", "上传课本"]],
    ["material.list", ["列出资料", "教材列表", "藏书", "有哪些书", "书单", "几本书", "已解析教材", "有什么教材"]],
    ["material.switch", ["切换教材", "换书", "切书", "切换到", "换一本", "切到", "切过去", "换个教材", "换本书"]],
    ["kb.search", ["知识库", "检索", "什么是", "查资料", "文档", "搜索", "搜一下", "查一下", "找找", "章节", "帮我找", "找找看", "查一查", "讲一下", "知识点", "介绍", "概要", "概述", "概括"]],
    ["kb.ingest", ["入库", "上传文档", "发布知识"]],
    ["profile.view", ["画像", "我的标签", "学习画像", "我的情况"]],
    ["profile.optout", ["退出画像", "关闭画像", "opt-out"]],
    ["teacher.hotspots", ["热点", "班级", "共性问题", "大家的情况"]],
    ["consent.grant", ["授权", "同意可见", "consent", "允许查看"]],
    ["consent.revoke", ["撤销", "取消授权", "关闭可见"]],
    ["whatif.simulate", ["what-if", "如果", "模拟", "假设", "假如", "要是", "试想"]],
    ["narrative.read", ["故事", "叙事", "讲解", "讲个故事", "举个例子", "给我讲讲", "讲一讲"]],
    ["sale.check", ["销账检查", "能否销账", "销账条件", "能销账吗", "销账了没"]],
    ["chapter.read", ["目录", "读一下", "讲了什么", "讲了啥", "章节", "这一章", "内容是什么", "翻到", "第几章", "前几章", "本章", "这章", "有什么内容", "不明白", "不懂", "不理解", "不清楚"]],
    ["graph.path", ["什么关系", "怎么联系", "有没有关", "路径", "谁先", "先后", "前置", "怎么到", "怎么学", "学习顺序", "什么联系", "有关吗", "先后顺序"]],
    ["meta.help", ["帮助", "怎么用", "说明", "你能干什么", "你会什么", "功能有哪些"]],
    ["meta.feedback", ["反馈", "建议", "不好用"]]
  ];
  /* ==== /GENERATED ==== */

  return {
    normalize: normalize,
    parseJsonLoose: parseJsonLoose,
    jsonLoose: parseJsonLoose, // 兼容 p0 内的旧名
    removeCyclesTopo: removeCyclesTopo,
    CRISIS: CRISIS,
    BANNED: BANNED,
    NEGATIVE: NEGATIVE,
    INTENTS: INTENTS,
  };
});
