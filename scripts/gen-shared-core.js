/**
 * gen-shared-core.js —— 由 tools/agent-intents.json 生成 shared/core.js 的常量块
 * ===========================================================
 * 方案 P3：意图表成为**真正单一事实源**。此前 p2-ai-integration.js 独立维护一份
 * INTENTS，22→26 扩容时漏同步（kb.search 少 13 个锚点等 17 处）。本生成器把共享
 * 内核里的常量改为构建期产物，并打上 DO NOT EDIT 标记，从根上消灭平行副本。
 *
 * 用法：node scripts/gen-shared-core.js [--check]
 *       --check  只校验生成块是否与 JSON 一致，不写文件（CI 用）
 * 退出码：0 = 一致/已生成；1 = 漂移（--check 且不一致时）
 */
"use strict";
const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(__dirname, "..");
const JSON_P = path.join(ROOT, "tools", "agent-intents.json");
const CORE = path.join(ROOT, "shared", "core.js");

const BEGIN = "  /* ==== GENERATED FROM tools/agent-intents.json — DO NOT EDIT ==== */";
const END = "  /* ==== /GENERATED ==== */";

const CHECK_ONLY = process.argv.includes("--check");

function js(s) {
  return JSON.stringify(s);
}

function renderBlock(doc) {
  const lines = [];
  lines.push(BEGIN);
  lines.push("  /* generator: scripts/gen-shared-core.js · source: tools/agent-intents.json */");
  lines.push("  var CRISIS = " + js(doc.crisis) + ";");
  lines.push("  var BANNED = " + js(doc.banned) + ";");
  lines.push("  var NEGATIVE = " + js(doc.negative) + ";");
  lines.push("  var INTENTS = [");
  doc.intents.forEach((it, i) => {
    const anchors = it.anchors.map((a) => js(a)).join(", ");
    const comma = i === doc.intents.length - 1 ? "" : ",";
    lines.push("    [" + js(it.id) + ", [" + anchors + "]]" + comma);
  });
  lines.push("  ];");
  lines.push(END);
  return lines.join("\n");
}

const doc = JSON.parse(fs.readFileSync(JSON_P, "utf8"));
const want = renderBlock(doc);
const src = fs.readFileSync(CORE, "utf8");

const bi = src.indexOf(BEGIN);
const ei = src.indexOf(END);
if (bi < 0 || ei < 0) {
  console.error("shared/core.js 缺少 GENERATED 标记块");
  process.exit(1);
}
const have = src.slice(bi, ei + END.length);

if (CHECK_ONLY) {
  if (have === want) {
    console.log("ALL GREEN · shared/core.js 生成块与 agent-intents.json 一致");
    process.exit(0);
  }
  console.error("[DRIFT] shared/core.js 生成块已漂移，请运行：node scripts/gen-shared-core.js");
  process.exit(1);
}

if (have === want) {
  console.log("shared/core.js 已与 JSON 一致，无需改动");
  process.exit(0);
}
fs.writeFileSync(CORE, src.slice(0, bi) + want + src.slice(ei + END.length), "utf8");
console.log("shared/core.js 生成块已更新（意图 " + doc.intents.length + " 条）");
