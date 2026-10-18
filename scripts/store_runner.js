/**
 * store_runner.js —— shared/store.js 黄金用例（供 scripts/verify_store.py 调用）
 * 用内存 FakeLS 模拟 localStorage，逐条断言分片/跳过未变更/迁移/备份/清空。
 * 输出 JSON：{ passed, failed, results:[{id,name,ok,detail}] }
 */
"use strict";
const path = require("path");
const APStore = require(path.resolve(__dirname, "..", "shared", "store.js"));

function FakeLS() {
  const m = {};
  return {
    getItem: (k) => (Object.prototype.hasOwnProperty.call(m, k) ? m[k] : null),
    setItem: (k, v) => { m[k] = String(v); },
    removeItem: (k) => { delete m[k]; },
    key: (i) => Object.keys(m)[i] ?? null,
    get length() { return Object.keys(m).length; },
    _dump: () => m,
  };
}

/** 造一个接近真实形状的状态（含未知字段，验证 meta 兜底） */
function makeState(scale) {
  const n = scale || 1;
  const chapters = [];
  for (let i = 0; i < 30 * n; i++) chapters.push({ id: "C" + i, title: "章节" + i, body: "", order: i, level: 1 });
  const logs = [];
  for (let i = 0; i < 50 * n; i++) logs.push({ kp: "K" + (i % 10), ok: i % 2 === 0, conf: 3, ts: 1700000000000 + i });
  return {
    user: { id: "u1" },
    consent: true,
    optOut: false,
    books: [{ id: "b1", name: "示例教材", chapters: chapters, qs: [], nodes: [] }],
    activeBookId: "b1",
    mastery: { K1: { n: 3, s: 2, c: 2, age: 0, st: 1 } },
    plan: { days: 14, items: [] },
    sale: { K1: 2 },
    day: 3,
    logs: logs,
    chat: null,
    planMeta: { createdAt: 1 },
    debtExplain: { K1: "解释" },
    papers: [{ id: "p1", name: "期中卷", text: "x".repeat(200) }],
    userName: "王小明",
    userTag: true,
    chatLog: [],
    kgMode: "slim",
    onboard: true,
    // 未知字段：必须被 meta 兜住，不得丢失
    __futureField: { hello: "world" },
  };
}

const results = [];
function check(id, name, fn) {
  try {
    const detail = fn();
    results.push({ id, name, ok: true, detail: detail || "" });
  } catch (e) {
    results.push({ id, name, ok: false, detail: String(e && e.message || e) });
  }
}
function assert(cond, msg) { if (!cond) throw new Error(msg); }
function eq(a, b, msg) { if (JSON.stringify(a) !== JSON.stringify(b)) throw new Error(msg + `（实际 ${JSON.stringify(a)} ≠ 期望 ${JSON.stringify(b)}）`); }

check("ST-01", "pick/merge 往返不丢字段（含未知键）", () => {
  const s = makeState(1);
  const parts = APStore.pick(s);
  const back = APStore.merge(parts);
  assert(back, "merge 返回 null");
  eq(Object.keys(back).sort(), Object.keys(s).sort(), "键集合应一致");
  eq(back.__futureField, s.__futureField, "未知字段应被 meta 兜住");
  eq(back.books.length, s.books.length, "books 数量应一致");
  return "键数 " + Object.keys(back).length;
});

check("ST-02", "写分片后读回可还原", () => {
  const ls = FakeLS();
  const s = makeState(1);
  const w = APStore.write(ls, s);
  eq(w.failed, [], "不应有写入失败");
  eq(w.written.sort(), ["answers", "books", "meta", "papers", "plan"], "首次应写全部分片");
  const r = APStore.read(ls);
  assert(r.hasShards, "应检测到分片");
  assert(r.missing.length === 0, "不应有缺失分片");
  const back = APStore.merge(r.parts);
  eq(back.logs.length, s.logs.length, "logs 应还原");
  eq(back.papers[0].name, "期中卷", "papers 应还原");
  return "bytes=" + w.bytes;
});

check("ST-03", "同状态二次写：0 个分片被重写", () => {
  const ls = FakeLS();
  const s = makeState(1);
  const w1 = APStore.write(ls, s);
  const w2 = APStore.write(ls, s, w1.cache);
  eq(w2.written, [], "未变更不应重写");
  assert(w2.changed === false, "changed 应为 false");
  return "跳过 " + APStore.PARTS.length + " 个分片";
});

check("ST-04", "只改 logs：仅 answers 分片被写", () => {
  const ls = FakeLS();
  const s = makeState(1);
  const w1 = APStore.write(ls, s);
  s.logs.push({ kp: "K9", ok: true, conf: 2, ts: 1 });
  const w2 = APStore.write(ls, s, w1.cache);
  eq(w2.written, ["answers"], "只应写 answers");
  return "written=" + w2.written.join(",");
});

check("ST-05", "旧单键迁移：备份存在且分片可还原", () => {
  const ls = FakeLS();
  const s = makeState(1);
  const legacyKey = "astralpath_monolith_v2";
  ls.setItem(legacyKey, JSON.stringify(s));
  // 迁移前：无分片
  assert(APStore.read(ls).hasShards === false, "迁移前不应有分片");
  const bk = APStore.backup(ls, ls.getItem(legacyKey));
  assert(bk && bk.indexOf("ap:backup:") === 0, "应生成备份键");
  const w = APStore.write(ls, s);
  eq(w.failed, [], "迁移写入不应失败");
  assert(APStore.read(ls).hasShards, "迁移后应有分片");
  eq(APStore.merge(APStore.read(ls).parts).activeBookId, "b1", "迁移后数据应一致");
  return "backup=" + bk.replace(/^ap:backup:\d+$/, "ap:backup:<ts>");
});

check("ST-06", "备份只保留最新一份", () => {
  const ls = FakeLS();
  const raw = JSON.stringify(makeState(1));
  const a = APStore.backup(ls, raw);
  const b = APStore.backup(ls, raw);
  const keys = Object.keys(ls._dump()).filter((k) => k.indexOf("ap:backup:") === 0);
  assert(keys.length === 1, "应只剩 1 份备份，实际 " + keys.length);
  assert(a !== b, "两次备份键应不同");
  return "保留 " + keys[0].replace(/^ap:backup:\d+$/, "ap:backup:<ts>");
});

check("ST-07", "clear 删净全部分片", () => {
  const ls = FakeLS();
  APStore.write(ls, makeState(1));
  const removed = APStore.clear(ls);
  eq(removed.length, 5, "应删除 5 个分片键");
  assert(APStore.read(ls).hasShards === false, "清空后不应有分片");
  return removed.join(",");
});

check("ST-08", "4.5MB 级状态：迁移不炸配额、books 承载主体", () => {
  const ls = FakeLS();
  const s = makeState(1);
  // 造 ~4.5MB：塞进试卷（真实场景里是试卷识别结果）
  s.papers = [{ id: "big", name: "大试卷", text: "x".repeat(4.5 * 1024 * 1024) }];
  const w = APStore.write(ls, s);
  eq(w.failed, [], "4.5MB 写入不应失败");
  const parts = APStore.pick(s);
  const sizes = {};
  APStore.PARTS.forEach((p) => { sizes[p] = JSON.stringify(parts[p]).length; });
  assert(sizes.papers > 4 * 1024 * 1024, "papers 分片应承载主体体积");
  assert(sizes.meta < 4096, "meta 分片应保持轻量（<4KB），实际 " + sizes.meta);
  const back = APStore.merge(APStore.read(ls).parts);
  eq(back.papers[0].text.length, 4.5 * 1024 * 1024, "大文本应完整往返");
  return "meta=" + sizes.meta + "B papers=" + Math.round(sizes.papers / 1024 / 1024) + "MB";
});

check("ST-09", "空/损坏分片不抛异常", () => {
  const ls = FakeLS();
  ls.setItem("ap:meta", "{not json");
  ls.setItem("ap:books", "null");
  const r = APStore.read(ls);
  assert(r.missing.indexOf("books") >= 0, "books 应记为缺失");
  assert(APStore.merge(r.parts) === null, "无有效 books 时 merge 应为 null");
  return "missing=" + r.missing.join(",");
});

check("ST-10", "dropBackups 只删备份、不碰分片", () => {
  const ls = FakeLS();
  const raw = JSON.stringify(makeState(1));
  APStore.backup(ls, raw);
  APStore.write(ls, makeState(1));
  const removed = APStore.dropBackups(ls);
  assert(removed.length === 1, "应删 1 个备份键，实际 " + removed.length);
  assert(!Object.keys(ls._dump()).some((k) => k.indexOf("ap:backup:") === 0), "备份应清空");
  assert(APStore.read(ls).hasShards, "分片不应被误删");
  return "removed=" + removed.length;
});

const passed = results.filter((r) => r.ok).length;
process.stdout.write(JSON.stringify({ passed: passed, failed: results.length - passed, results: results }, null, 2));
