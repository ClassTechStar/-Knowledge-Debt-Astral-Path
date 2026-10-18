/**
 * xss_runner.js —— 黄金用例驱动（供 scripts/verify_xss.py 调用）
 * 用法：node scripts/xss_runner.js <cases.json>   # stdout 输出 JSON 结果
 * 只做渲染函数求值，不触碰 DOM，可在 CI 无浏览器环境下运行。
 */
"use strict";
const fs = require("fs");
const path = require("path");

const SAFE_DOM = path.resolve(__dirname, "..", "shared", "safe-dom.js");
const APSafe = require(SAFE_DOM);

const casesFile = process.argv[2];
const doc = JSON.parse(fs.readFileSync(casesFile, "utf8"));

const out = doc.cases.map((c) => ({
  id: c.id,
  name: c.name,
  input: c.input,
  safeRich: APSafe.safeRich(c.input),
  esc: APSafe.esc(c.input),
  url: APSafe.sanitizeUrl(c.input),
}));

process.stdout.write(JSON.stringify(out, null, 2));
