const fs = require("fs");
const p = process.argv[2];
const h = fs.readFileSync(p, "utf8");
const i = h.indexOf("<script");
const j = h.lastIndexOf("</script>");
if (i < 0 || j < 0) {
  console.log("no script tag");
  process.exit(1);
}
const start = h.indexOf(">", i) + 1;
const js = h.slice(start, j);
try {
  new Function(js);
  console.log("JS parse OK", js.length);
} catch (e) {
  console.log("JS PARSE ERROR", e.message);
  // show nearby line
  const m = /line (\d+)/.exec(e.stack || "");
  process.exit(2);
}
