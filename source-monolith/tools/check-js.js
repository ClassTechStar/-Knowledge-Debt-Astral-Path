import fs from "fs";
import os from "os";
import { execFileSync } from "child_process";
const files = process.argv.slice(2);
let bad = 0;
for (const f of files) {
  const html = fs.readFileSync(f, "utf8");
  const re = /<script\b[^>]*>([\s\S]*?)<\/script>/g;
  let m, i = 0;
  while ((m = re.exec(html))) {
    i++;
    const code = m[1];
    if (!code.trim()) continue;
    const tmp = os.tmpdir() + "/ap_chk_tmp.js";
    fs.writeFileSync(tmp, code);
    try {
      execFileSync("node", ["--check", tmp], { stdio: "pipe" });
    } catch (e) {
      bad++;
      console.log(`FAIL ${f} block#${i} (${code.length} chars): ${String(e.stderr).slice(0, 300)}`);
    }
  }
  console.log(`checked ${f}: ${i} script blocks`);
}
process.exit(bad ? 1 : 0);
