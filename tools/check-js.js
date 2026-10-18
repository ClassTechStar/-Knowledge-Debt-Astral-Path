/**
 * check-js.js —— 前端 JS 语法门禁（方案 P1）
 * ===========================================================
 * 覆盖两类目标：
 *   ① 独立 .js 文件（shared/、tools/、scripts/、deploy/monolith-web/、deploy/mobile-app/）
 *   ② HTML 内联 <script>…</script> 块（不含 <script src=…>）
 *      —— deploy/monolith-web/index.html（单一事实源）、.cloudflare/pages/index.html、
 *         src/AstralPath.Api/wwwroot/index.html、materials/**\/index.html
 * 只做**解析**不执行：用 vm.Script 编译，等价于 node --check，无副作用。
 *
 * 用法：node tools/check-js.js             # 全量
 *       node tools/check-js.js --json      # 输出机器可读结果
 * 退出码：0 = 全部通过；1 = 存在语法错误
 */
"use strict";

const fs = require("fs");
const path = require("path");
const vm = require("vm");

const ROOT = path.resolve(__dirname, "..");
const JSON_OUT = process.argv.includes("--json");

// 目录级黑名单：构建产物 / 第三方 / 虚拟环境 / 巨型引擎
const SKIP_DIR = new Set([
  "node_modules", ".git", "bin", "obj", "build", "dist", "publish",
  "ocr-venv", "tessdata", "__pycache__", "ocr-engine", ".wrangler",
  "release", "TestResults", "app", "intermediates",
]);

// 单文件黑名单：第三方/压缩体积过大（解析成本高且非本仓产物）
const SKIP_FILE = /(\.min\.js$|\.worker\.js$|\.wasm\.js$|pdf\.min\.js$)/i;

function walk(dir, out) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch (e) {
    return out;
  }
  for (const e of entries) {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (SKIP_DIR.has(e.name)) continue;
      walk(full, out);
    } else if (e.isFile()) {
      out.push(full);
    }
  }
  return out;
}

function rel(p) {
  return path.relative(ROOT, p).replace(/\\/g, "/");
}

function collectJsFiles() {
  const roots = ["shared", "tools", "scripts", "deploy/monolith-web", "deploy/mobile-app", "src/AstralPath.Api/wwwroot"]
    .map((r) => path.join(ROOT, r))
    .filter((p) => fs.existsSync(p));
  const files = [];
  for (const r of roots) {
    if (fs.statSync(r).isFile()) { files.push(r); continue; }
    walk(r, files);
  }
  return files
    .filter((f) => f.endsWith(".js"))
    .filter((f) => !SKIP_FILE.test(path.basename(f)))
    // 跳过任何路径中仍含黑名单目录的（如 src/**/app/build/…）
    .filter((f) => !rel(f).split("/").some((seg) => SKIP_DIR.has(seg)));
}

const HTML_TARGETS = [
  "deploy/monolith-web/index.html",
  ".cloudflare/pages/index.html",
  "src/AstralPath.Api/wwwroot/index.html",
  "src/AstralPath.Monolith/Resources/index.html",
  "src/AstralPath.Native/app/src/main/assets/www/index.html",
];

function collectInlineScripts(htmlPath) {
  const src = fs.readFileSync(htmlPath, "utf8");
  const re = /<script\b([^>]*)>([\s\S]*?)<\/script>/gi;
  const blocks = [];
  let m;
  while ((m = re.exec(src)) !== null) {
    const attrs = m[1] || "";
    if (/\bsrc\s*=/i.test(attrs)) continue; // 外链脚本不校验
    const before = src.slice(0, m.index);
    const line = before.split(/\r\n|\r|\n/).length;
    blocks.push({ line, code: m[2] });
  }
  return blocks;
}

function compile(name, code, lineOffset) {
  try {
    new vm.Script(code, { filename: name, lineOffset: lineOffset || 0 });
    return null;
  } catch (e) {
    return `${e.name}: ${e.message}`;
  }
}

const results = [];
let failed = 0;

// ① 独立 .js 文件
for (const f of collectJsFiles()) {
  const code = fs.readFileSync(f, "utf8");
  const err = compile(rel(f), code, 0);
  if (err) { failed++; results.push({ target: rel(f), kind: "file", ok: false, error: err }); }
  else results.push({ target: rel(f), kind: "file", ok: true });
}

// ② HTML 内联块
for (const h of HTML_TARGETS) {
  const p = path.join(ROOT, h);
  if (!fs.existsSync(p)) continue;
  const blocks = collectInlineScripts(p);
  blocks.forEach((b, i) => {
    const label = `${h}#inline[${i + 1}]@L${b.line}`;
    const err = compile(label, b.code, 0);
    if (err) { failed++; results.push({ target: label, kind: "inline", ok: false, error: err }); }
    else results.push({ target: label, kind: "inline", ok: true });
  });
}

const okCount = results.length - failed;
if (JSON_OUT) {
  console.log(JSON.stringify({ total: results.length, passed: okCount, failed, items: results.filter((r) => !r.ok) }, null, 2));
} else {
  for (const r of results) {
    if (!r.ok) console.log(`[FAIL] ${r.target}\n       ${r.error}`);
  }
  console.log(`check-js: ${okCount}/${results.length} 通过${failed ? `，${failed} 处语法错误` : " · ALL GREEN"}`);
}
process.exit(failed ? 1 : 0);
