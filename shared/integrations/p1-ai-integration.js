/* ═══════════════════════════════════════════════════════════════════
 * P1 迁移：AI 增强构图 + 多格式导入 + AI 计划官（源：Astralpath index.html）
 * 注入位置：.cloudflare/pages/index.html 尾部 </body> 前（p0-ai-integration.js 之后）
 * 对应开发方案 P1-1（模块 B2-B3）/ P1-2（模块 D1-D3）/ P1-3（模块 E2-E5）。
 * 依赖基准已有：esc / sanitizeText / activeBook / activeNodes / title / scoreOf /
 *              save / $ / K / buildPlan / scanDebts / generateQuestions /
 *              p0-ai-integration.js 的 window.__AP_MIMO__
 * ═══════════════════════════════════════════════════════════════════ */
(function () {
  "use strict";

  // ═══ 1. 多格式导入（P1-2，源 L3189-3385）═════════════════════════════

  // ── ZIP 容器读取（源 readZipEntries L3210-3249）──────────────────────
  function inflateRaw(bytes) {
    return new Promise(function (resolve, reject) {
      if (typeof DecompressionStream === "undefined") { reject(new Error("浏览器不支持 DecompressionStream")); return; }
      try {
        var st = new Blob([bytes]).stream().pipeThrough(new DecompressionStream("deflate-raw"));
        new Response(st).arrayBuffer().then(function (ab) { resolve(new Uint8Array(ab)); }, function () {
          // 个别容器用 zlib 包装（带 2 字节头），raw 失败再试 deflate
          try {
            var st2 = new Blob([bytes]).stream().pipeThrough(new DecompressionStream("deflate"));
            new Response(st2).arrayBuffer().then(function (ab) { resolve(new Uint8Array(ab)); }, reject);
          } catch (e) { reject(e); }
        });
      } catch (e) {
        try {
          var st3 = new Blob([bytes]).stream().pipeThrough(new DecompressionStream("deflate"));
          new Response(st3).arrayBuffer().then(function (ab) { resolve(new Uint8Array(ab)); }, reject);
        } catch (e2) { reject(e2); }
      }
    });
  }

  function readZipEntries(u8, opts) {
    opts = opts || {};
    var maxEntries = opts.maxEntries || 4000, maxSingle = opts.maxSingle || (32 * 1024 * 1024), maxTotal = opts.maxTotal || (96 * 1024 * 1024);
    function u16(o) { return u8[o] | (u8[o + 1] << 8); }
    function u32(o) { return (u8[o] | (u8[o + 1] << 8) | (u8[o + 2] << 16) | (u8[o + 3] << 24)) >>> 0; }
    var eocd = -1;
    for (var i = u8.length - 22; i >= Math.max(0, u8.length - 65557); i--) {
      if (u8[i] === 0x50 && u8[i + 1] === 0x4b && u8[i + 2] === 0x05 && u8[i + 3] === 0x06) { eocd = i; break; }
    }
    if (eocd < 0) throw new Error("不是有效的 ZIP 容器（未找到中央目录结尾标记）");
    var count = u16(eocd + 10), cdOff = u32(eocd + 16);
    if (count === 0xFFFF || cdOff === 0xFFFFFFFF) throw new Error("ZIP64 容器暂不支持");
    if (!count || count > maxEntries) throw new Error("ZIP 条目数异常（" + count + "）");
    var out = {}, p = cdOff, total = 0, pending = [];
    for (var k = 0; k < count && p + 46 <= u8.length; k++) {
      if (u32(p) !== 0x02014b50) break;
      var method = u16(p + 10), csize = u32(p + 20), usize = u32(p + 24), nlen = u16(p + 28), elen = u16(p + 30), clen = u16(p + 32), lho = u32(p + 42);
      var name = "";
      for (var j = 0; j < nlen; j++) name += String.fromCharCode(u8[p + 46 + j]);
      p += 46 + nlen + elen + clen;
      if (!name || /\/$/.test(name)) continue;
      if (usize > maxSingle) continue;
      if (lho + 30 > u8.length || u32(lho) !== 0x04034b50) continue;
      var lnlen = u16(lho + 26), lexlen = u16(lho + 28);
      var dstart = lho + 30 + lnlen + lexlen;
      if (dstart + csize > u8.length) continue;
      pending.push({ name: name, method: method, raw: u8.subarray(dstart, dstart + csize), usize: usize });
    }
    // 顺序解压（保持条目顺序与 total 累计语义）
    return pending.reduce(function (chain, ent) {
      return chain.then(function () {
        if (total > maxTotal) return;
        var dataP = ent.method === 0 ? Promise.resolve(ent.raw) : (ent.method === 8 ? inflateRaw(ent.raw) : Promise.reject(new Error("skip")));
        return dataP.then(function (data) {
          if (!data || !data.length) return;
          total += data.length;
          if (total > maxTotal) return;
          out[ent.name] = data;
        }).catch(function () {});
      });
    }, Promise.resolve()).then(function () {
      if (!Object.keys(out).length) throw new Error("ZIP 容器内没有可读取的条目");
      return out;
    });
  }

  function t10Decode(u8) {
    if (!u8) return "";
    if (u8.length >= 2 && u8[0] === 0xFF && u8[1] === 0xFE) { try { return new TextDecoder("utf-16le").decode(u8); } catch (e) {} }
    try { return new TextDecoder("utf-8", { fatal: false }).decode(u8); } catch (e) { return ""; }
  }
  function t10Ent(s) {
    return String(s == null ? "" : s)
      .replace(/&#x([0-9a-fA-F]+);/g, function (_, h) { try { return String.fromCodePoint(parseInt(h, 16)); } catch (e) { return ""; } })
      .replace(/&#(\d+);/g, function (_, d) { try { return String.fromCodePoint(parseInt(d, 10)); } catch (e) { return ""; } })
      .replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, "&");
  }
  function t10XmlText(xml) {
    var s = String(xml || "");
    s = s.replace(/<(?:w|a):p\b[^>]*\/>/g, "\n");
    s = s.replace(/<\/(?:w|a):p>/g, "\n");
    s = s.replace(/<(?:w|a):br\b[^>]*\/>/g, "\n");
    s = s.replace(/<w:tab\b[^>]*\/>/g, "\t");
    s = s.replace(/<[^>]+>/g, "");
    s = t10Ent(s);
    return s.replace(/[ \t ]+/g, " ").replace(/ ?\n ?/g, "\n").replace(/\n{3,}/g, "\n\n").trim();
  }
  function t10HtmlText(html) {
    var s = String(html || "");
    s = s.replace(/<!--[\s\S]*?-->/g, "");
    s = s.replace(/<script\b[\s\S]*?<\/script>/gi, "").replace(/<style\b[\s\S]*?<\/style>/gi, "");
    s = s.replace(/<\/(?:p|div|li|tr|h[1-6]|blockquote|section|article|figcaption)\s*>/gi, "\n");
    s = s.replace(/<br\s*\/?>/gi, "\n");
    s = s.replace(/<[^>]+>/g, "");
    s = t10Ent(s);
    return s.replace(/[ \t ]+/g, " ").replace(/ ?\n ?/g, "\n").replace(/\n{3,}/g, "\n\n").trim();
  }

  // Word(.docx)（源 extractDocxText L3283-3311）
  function extractDocxText(entries) {
    var main = entries["word/document.xml"];
    if (!main) return { text: "", engine: "docx", bestEffort: true };
    var xml = t10Decode(main), lines = [];
    var re = /<w:p\b[^>]*>([\s\S]*?)<\/w:p>/g, m;
    while ((m = re.exec(xml))) {
      var body = m[1], lv = 0;
      var st = /<w:pStyle\s+w:val="([^"]+)"/.exec(body);
      if (st) {
        var v = st[1], hm = /^(?:Heading|标题)\s*([1-3])$/i.exec(v);
        if (hm) lv = +hm[1];
        else if (/^[1-3]$/.test(v)) lv = +v;
        else if (/^(?:heading|标题)/i.test(v)) lv = 1;
      }
      var t = t10XmlText(body);
      if (!t) continue;
      lines.push(lv ? ("#".repeat(lv) + " " + t) : t);
    }
    var doc = lines.join("\n");
    if (window.countReadable && countReadable(doc) < 200) {
      var extra = [];
      Object.keys(entries).forEach(function (n) {
        if (/^word\/(?:header|footer|footnotes|endnotes)\d*\.xml$/.test(n)) extra.push(t10XmlText(entries[n]));
      });
      if (extra.length) doc += "\n" + extra.filter(Boolean).join("\n");
    }
    return { text: doc, engine: "docx" };
  }

  // PowerPoint(.pptx)（源 extractPptxText L3313-3322）
  function extractPptxText(entries) {
    var slides = Object.keys(entries).filter(function (n) { return /^ppt\/slides\/slide\d+\.xml$/.test(n); })
      .sort(function (a, b) { return (parseInt((a.match(/\d+/) || ["0"])[0], 10) || 0) - (parseInt((b.match(/\d+/) || ["0"])[0], 10) || 0); });
    var out = [];
    slides.forEach(function (n, i) { out.push("[slide " + (i + 1) + "]\n" + t10XmlText(t10Decode(entries[n]))); });
    var notes = Object.keys(entries).filter(function (n) { return /^ppt\/notesSlides\/notesSlide\d+\.xml$/.test(n); })
      .sort(function (a, b) { return (parseInt((a.match(/\d+/) || ["0"])[0], 10) || 0) - (parseInt((b.match(/\d+/) || ["0"])[0], 10) || 0); });
    notes.forEach(function (n, i) { var t = t10XmlText(t10Decode(entries[n])); if (t) out.push("[备注 " + (i + 1) + "]\n" + t); });
    return { text: out.join("\n"), engine: "pptx" };
  }

  // EPUB（源 extractEpubText L3324-3355）
  function extractEpubText(entries) {
    var cont = entries["META-INF/container.xml"];
    if (!cont) throw new Error("EPUB 缺少 META-INF/container.xml");
    var rootfile = /full-path\s*=\s*"([^"]+)"/.exec(t10Decode(cont));
    if (!rootfile) throw new Error("EPUB container.xml 未声明 rootfile");
    var opfPath = rootfile[1].replace(/^\//, "");
    var opf = entries[opfPath];
    if (!opf) throw new Error("EPUB 缺少 OPF 文件（" + opfPath + "）");
    var xml = t10Decode(opf);
    var dir = opfPath.replace(/[^/]*$/, "");
    var man = {}, reM = /<item\b[^>]*\/?>/g, m;
    while ((m = reM.exec(xml))) {
      var id = /id\s*=\s*"([^"]+)"/.exec(m[0]), href = /href\s*=\s*"([^"]+)"/.exec(m[0]);
      if (id && href) man[id[1]] = t10Ent(href[1]);
    }
    var order = [], reS = /<itemref\b[^>]*\/?>/g, ms;
    while ((ms = reS.exec(xml))) {
      var idr = /idref\s*=\s*"([^"]+)"/.exec(ms[0]);
      if (idr && man[idr[1]]) order.push(man[idr[1]]);
    }
    if (!order.length) Object.keys(man).forEach(function (k) { order.push(man[k]); });
    var out = [];
    for (var i = 0; i < order.length && out.length < 600; i++) {
      var h = String(order[i]).replace(/^\//, "");
      var f = entries[(dir + h).replace(/\/\.\//g, "/")] || entries[h];
      if (!f) continue;
      var t = t10HtmlText(t10Decode(f));
      if (t) out.push(t);
    }
    if (!out.length) throw new Error("EPUB 未解析出正文（spine 为空或资源缺失）");
    return { text: out.join("\n\n"), engine: "epub" };
  }

  // 旧版二进制 Office(.doc/.ppt)（源 extractLegacyOfficeText L3357-3385）
  function extractLegacyOfficeText(u8) {
    var runs = [], cur = [];
    function sequentialNoise(a) {
      if (a.length < 8) return false;
      var seq = 0;
      for (var i = 1; i < a.length; i++) { var d = a[i] - a[i - 1]; if (d === 1 || d === 2) seq++; }
      return seq / (a.length - 1) >= 0.6;
    }
    function flush() {
      if (cur.length >= 4) {
        var s = String.fromCharCode.apply(null, cur);
        if (/[一-鿿A-Za-z0-9]/.test(s) && !sequentialNoise(cur)) runs.push(s);
      }
      cur = [];
    }
    for (var i = 0; i + 1 < u8.length; i += 2) {
      var cp = u8[i] | (u8[i + 1] << 8);
      var ok = (cp >= 0x20 && cp <= 0x7E) || (cp >= 0x4E00 && cp <= 0x9FFF) || (cp >= 0x3000 && cp <= 0x303F)
        || (cp >= 0xFF00 && cp <= 0xFFEF) || (cp >= 0x2010 && cp <= 0x2027) || cp === 0x0A || cp === 0x0D || cp === 0x09;
      if (!ok) { flush(); continue; }
      if (cp === 0x0D) continue;
      cur.push(cp === 0x0A ? 0x0A : cp);
      if (cur.length > 20000) flush();
    }
    flush();
    return { text: runs.join("\n").replace(/\n{3,}/g, "\n\n").trim(), engine: "legacy-binary", bestEffort: true, note: "旧版二进制格式为尽力提取，建议核对结果" };
  }

  // 统一入口（源 resolveFileText 的非 OCR 分发 L1011-1037）
  function parseMultiFormat(u8, fileName) {
    var ext = (String(fileName).match(/\.[a-z]+$/i) || [""])[0].toLowerCase();
    if (ext === ".docx" || ext === ".pptx" || ext === ".epub") {
      return readZipEntries(u8).then(function (entries) {
        if (ext === ".docx") {
          if (!entries["word/document.xml"]) throw new Error("ZIP 容器不是受支持的 Office/EPUB 格式");
          return extractDocxText(entries);
        }
        if (ext === ".pptx") {
          var hasSlide = Object.keys(entries).some(function (n) { return /^ppt\/slides\/slide\d+\.xml$/.test(n); });
          if (!hasSlide) throw new Error("ZIP 容器不是受支持的 Office/EPUB 格式");
          return extractPptxText(entries);
        }
        return extractEpubText(entries);
      });
    }
    if (ext === ".doc" || ext === ".ppt") return Promise.resolve(extractLegacyOfficeText(u8));
    if (ext === ".html" || ext === ".htm") return Promise.resolve({ text: t10HtmlText(t10Decode(u8)), engine: "html" });
    if (ext === ".md" || ext === ".markdown") return Promise.resolve({ text: t10Decode(u8).replace(/\r\n/g, "\n"), engine: "markdown" });
    if (ext === ".txt") return Promise.resolve({ text: t10Decode(u8).replace(/\r\n/g, "\n"), engine: "text" });
    return Promise.reject(new Error("不支持的文档格式：" + ext));
  }

  window.__AP_MULTI_FORMAT__ = { parse: parseMultiFormat, readZip: readZipEntries, legacy: extractLegacyOfficeText };

  // ═══ 2. AI 增强构图（P1-1，源 mergeGraphAI L4361-4400 + demoGraphAI L4348）═══

  function normTitle(s) {
    return String(s || "").replace(/[！-～]/g, function (c) { return String.fromCharCode(c.charCodeAt(0) - 0xFEE0); })
      .replace(/　/g, " ").replace(/\s+/g, " ").toLowerCase().trim();
  }

  // Kahn 拓扑清环（源 removeCyclesTopo L1536-1564）
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
    if (order.length >= nodes.length)
      return { edges: edges.filter(function (e) { return e.from && e.to && e.from !== e.to; }), cut: 0 };
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

  // 题池重建约束（T15，源 L4394-4398）：保留 layer:"ai" 题置前，仅重建规则题
  function keepAiQuestions(pool, rebuilt, cap) {
    cap = cap || 300;
    var keptAi = (pool || []).filter(function (q) { return q && q.layer === "ai"; });
    var merged = keptAi.concat(rebuilt || []);
    return merged.length > cap ? merged.slice(0, cap) : merged;
  }

  // 合并管线（源 mergeGraphAI L4361-4396）
  function mergeGraphAI(book, ai) {
    if (!book || !ai) return null;
    var nodes = book.nodes || [], edges = book.edges || [];
    var byT = {};
    nodes.forEach(function (n) { var k = normTitle(n.title); if (!(k in byT)) byT[k] = n; });
    var chMax = Math.max(0, (book.chapters || []).length - 1);
    var seq = 0, addedN = 0;
    (Array.isArray(ai.concepts) ? ai.concepts : []).slice(0, 20).forEach(function (c) {
      var t = String(c && c.title || "").trim();
      if (!t) return;
      var key = normTitle(t);
      if (!key || key in byT) return;
      var n = {
        id: "A" + String(++seq).padStart(2, "0") + Date.now().toString(36).slice(-3),
        title: t.slice(0, 24), diff: 3, course: (book.name || "").slice(0, 24), kind: "term",
        body: String(c.def || "").slice(0, 120), rank: 0,
        ch: Math.min(Math.max(c.ch | 0, 0), chMax), src: "ai"
      };
      nodes.push(n); byT[key] = n; addedN++;
    });
    var seen = {};
    edges.forEach(function (e) { seen[e.from + ">" + e.to + ">" + (e.etype || "")] = 1; });
    var addedE = 0;
    (Array.isArray(ai.prereqs) ? ai.prereqs : []).slice(0, 20).forEach(function (p) {
      var a = byT[normTitle(p && p.from)], b = byT[normTitle(p && p.to)];
      if (!a || !b || a.id === b.id) return;
      var kk = a.id + ">" + b.id + ">prerequisite";
      if (seen[kk]) return; seen[kk] = 1;
      edges.push({ from: a.id, to: b.id, tg: true, w: Math.min(Math.max(+p.w || 1.2, 0.5), 2), etype: "prerequisite", conf: 0.75, src: "ai" });
      addedE++;
    });
    if (!addedN && !addedE) return null;
    var cleaned = removeCyclesTopo(nodes, edges);
    book.nodes = nodes;
    book.edges = cleaned.edges;
    // T15：题池保留 AI 题，仅重建规则题
    book.qs = keepAiQuestions(book.qs, generateQuestions(book), function (q) { return q.layer; }, 300);
    book.kgAiEnhanced = true;
    return { addedN: addedN, addedE: addedE, cut: (cleaned.cut || 0) };
  }

  // 演示模式构图素材（源 demoGraphAI L4348-4357）
  function demoGraphAI(book) {
    var chs = (book && book.chapters) || [];
    var concepts = chs.slice(0, 6).map(function (c, i) {
      var t = String(c.title || "").replace(/^\d+(\.\d+)*\s*/, "").trim();
      return t ? { title: (t.slice(0, 20) + "·核心"), ch: i, def: "（演示）「" + t.slice(0, 16) + "」相关概念，配置模型 Key 后由大模型生成 80 字定义。" } : null;
    }).filter(Boolean);
    var prereqs = [];
    for (var i = 0; i < concepts.length - 1; i++) prereqs.push({ from: concepts[i].title, to: concepts[i + 1].title, w: 1.2 });
    return { concepts: concepts, prereqs: prereqs };
  }

  // 构图 JSON 解析（源 btnGraphAI L7269-7272 的 parseJsonLoose 通道）
  function parseGraphExtract(text) {
    var M = window.__AP_MIMO__;
    if (!M || !M.parseJsonLoose) return null;
    var r = M.parseJsonLoose(text);
    if (!r) return null;
    var concepts = [], prereqs = [];
    (Array.isArray(r.concepts) ? r.concepts : []).slice(0, 20).forEach(function (c) {
      if (!c || typeof c.title !== "string" || !c.title.trim()) return;
      concepts.push({ title: c.title, ch: c.ch | 0, def: String(c.def || "") });
    });
    (Array.isArray(r.prereqs) ? r.prereqs : []).slice(0, 20).forEach(function (p) {
      if (!p || typeof p.from !== "string" || typeof p.to !== "string") return;
      prereqs.push({ from: p.from, to: p.to, w: +p.w || 1.2 });
    });
    if (!concepts.length && !prereqs.length) return null;
    return { concepts: concepts, prereqs: prereqs };
  }

  // btnGraphAI 处理器（源 L7253-7283）
  function graphAiEnhance() {
    var b = activeBook(), M = window.__AP_MIMO__, gs = $("gSwitch"), btn = $("btnGraphAI");
    if (!b) { if (gs) { gs.className = "status warn"; gs.textContent = "没有可用教材，请先到藏书阁解析资料"; } return; }
    if (btn) btn.disabled = true;
    var isDemo = !M || !M.ready();
    if (gs) {
      gs.className = "status";
      gs.textContent = isDemo ? "演示模式：正在用内置素材演示增强构图管线…" : "AI 增强构图中…（约 10-40 秒）";
    }
    var aiP;
    if (isDemo) {
      aiP = Promise.resolve(demoGraphAI(b));
    } else {
      if (navigator.onLine === false) {
        if (gs) { gs.className = "status warn"; gs.textContent = "当前离线，AI 增强不可用"; }
        if (btn) btn.disabled = false;
        return;
      }
      var chs = b.chapters || [];
      var toc = chs.slice(0, 24).map(function (c, i) { return (i + 1) + "." + c.title; }).join("；");
      var terms = (activeNodes() || []).filter(function (n) { return n.kind === "term"; }).slice(0, 30).map(function (n) { return n.title; }).join("、");
      aiP = M.complete([
        { role: "system", content: "你是知识图谱构建器。严格输出 JSON：{\"concepts\":[{\"title\":\"概念名\",\"ch\":0,\"def\":\"不超过80字定义\"}],\"prereqs\":[{\"from\":\"概念A\",\"to\":\"概念B\",\"w\":1.2}]}。concepts 给 8-15 个教材核心概念（不与已有术语重复），prereqs 给 5-12 条先修关系（from 为基础、to 建立在其上），from/to 必须使用 concepts 或已有术语中的准确名称。不要输出 JSON 以外的任何内容。" },
        { role: "user", content: "教材《" + (b.name || "") + "》章节目录：" + (toc || "无") + "\n已有术语：" + (terms || "无") }
      ], { maxTokens: 1400, temperature: 0.3, timeoutMs: 60000 }).then(function (r) {
        return parseGraphExtract(r && r.text);
      });
    }
    aiP.then(function (ai) {
      var res = ai ? mergeGraphAI(b, ai) : null;
      if (res) {
        if (typeof save === "function") save(S);
        if (typeof renderGraph === "function") try { renderGraph(); } catch (e) {}
        if (gs) {
          gs.className = "status ok";
          gs.textContent = (isDemo ? "【演示模式】" : "") + "AI 增强完成：新增 " + res.addedN + " 概念 / " + res.addedE + " 先修边（去重合并，标记 src=ai" + (res.cut ? ("，清环裁剪 " + res.cut + " 边") : "") + "）" + (isDemo ? "。配置模型 Key 后由大模型抽取真实概念。" : "");
        }
      } else if (gs) {
        gs.className = "status warn";
        gs.textContent = (isDemo ? "【演示模式】" : "") + "AI 未能抽取出新内容，原图谱已保留";
      }
    }).catch(function (e) {
      if (gs) { gs.className = "status warn"; gs.textContent = "AI 增强失败，已保留原图谱：" + (e.message || e); }
    }).then(function () {
      if (btn) btn.disabled = false;
    });
  }

  window.__AP_GRAPH_AI__ = { merge: mergeGraphAI, demo: demoGraphAI, parse: parseGraphExtract, cycles: removeCyclesTopo, keepAi: keepAiQuestions };

  // ═══ 3. AI 计划官（P1-3，源 generatePlan/startGuiPlan T9）═══════════════

  // K1-K5 合约校验（与 C# PlannerConstraintChecker 语义对齐：K1 日预算 / K2 连续难 / K3 why 含先修名 / K4 concept 先于 drill / K5 债边覆盖）
  function checkPlanK(plan, budget, topDebts) {
    var viol = [];
    var byDay = {};
    (plan || []).forEach(function (p) {
      if (p.st === "dropped") return;
      byDay[p.day] = (byDay[p.day] || 0) + (p.min || 0);
    });
    Object.keys(byDay).forEach(function (d) {
      if (byDay[d] > budget) viol.push("D" + d + " 合计 " + byDay[d] + "min 超出日预算 " + budget + "min（K1）");
    });
    // K3：why 含 from_kp 名
    (plan || []).forEach(function (p) {
      if (p.st === "dropped") return;
      if (!p.why) { viol.push("D" + p.day + " 条目 why 为空（K3）"); return; }
      if (p.fromKp && p.fromKp !== p.kp && p.why.indexOf(title(p.fromKp)) < 0 && p.why.indexOf(title(p.kp)) < 0)
        viol.push("D" + p.day + " 条目「" + title(p.kp) + "」why 未包含先修名（K3）");
    });
    // K4：同 kp 同天唯一（源 L1704）
    var seen = {};
    (plan || []).forEach(function (p) {
      if (p.st === "dropped") return;
      var k = p.day + "|" + p.kp;
      if (seen[k]) viol.push("D" + p.day + "「" + title(p.kp) + "」重复安排（K4）");
      seen[k] = 1;
    });
    // K5：top 债边覆盖或显式延期
    (topDebts || []).forEach(function (d) {
      var covered = (plan || []).some(function (p) { return p.st !== "dropped" && p.fromKp === d.fromKp && p.kp === d.toKp; });
      var dropped = (plan || []).some(function (p) { return p.st === "dropped" && p.fromKp === d.fromKp && p.kp === d.toKp; });
      if (!covered && !dropped) viol.push("债边「" + title(d.fromKp) + "→" + title(d.toKp) + "」未覆盖也未显式延期（K5）");
    });
    return { ok: viol.length === 0, violations: viol };
  }

  // 计划官草案解析（源 generatePlan 的草案通道）
  function parsePlanDraft(text) {
    var M = window.__AP_MIMO__;
    if (!M || !M.parseJsonLoose) return null;
    var r = M.parseJsonLoose(text);
    if (!r || !Array.isArray(r.items) || !r.items.length) return null;
    var items = r.items.slice(0, 200).map(function (it) {
      var type = ["concept", "drill", "quiz", "review"].indexOf(it.type) >= 0 ? it.type : "concept";
      return {
        day: Math.max(1, it.day | 0 || 1),
        kp: String(it.kp || "").trim(),
        type: type,
        difficulty: Math.min(5, Math.max(1, it.difficulty | 0 || 2)),
        est_min: Math.min(60, Math.max(5, it.est_min | 0 || 10)),
        why: String(it.why || ""),
        from_kp: it.from_kp ? String(it.from_kp) : ""
      };
    }).filter(function (it) { return it.kp; });
    if (!items.length) return null;
    return { strategy: String(r.strategy || ""), items: items, tips: String(r.tips || ""), deferred: String(r.deferred || "") };
  }

  // 四源上下文（源 startGuiPlan T9）
  function buildPlanUserPrompt(b, debts, kpWhitelist, note) {
    var parts = [];
    parts.push("教材《" + (b.name || "") + "》，修复周期 14 天，单日预算 40 分钟。");
    parts.push("章节目录：" + (b.chapters || []).slice(0, 24).map(function (c) { return c.title; }).join("；"));
    var weak = [];
    try { weak = (scanDebts() || []).filter(function (d) { return d.status === "open"; }).slice(0, 5).map(function (d) { return d.toName; }); } catch (e) {}
    parts.push("学习者画像：薄弱点 " + (weak.length ? weak.join("、") : "无") + "。");
    var snap = (typeof __todaySnapshot === "function") ? __todaySnapshot() : { answered: 0, planned: 0 };
    parts.push("今日快照：计划 " + (snap.planned || 0) + " 项，已答 " + (snap.answered || 0) + " 题。");
    parts.push("债边事实清单：");
    (debts || []).slice(0, 16).forEach(function (d) {
      parts.push("- " + d.fromName + " → " + d.toName + "（impact=" + (d.imp || 0).toFixed(2) + "，est=" + (d.estMin || 10) + "min）");
    });
    parts.push("知识点白名单：" + (kpWhitelist || []).slice(0, 60).join("、"));
    if (note) parts.push("用户补充要求：" + note);
    return parts.join("\n");
  }

  var PLAN_SYS = "你是「知债」修复计划编排官。根据教材、学习者画像、今日快照与债边清单，编排一份修复计划草案。"
    + "硬性要求：1) 严格输出 JSON：{\"strategy\":\"总体策略，不超过80字\",\"items\":[{\"day\":1,\"kp\":\"知识点名\",\"type\":\"concept|drill|quiz|review\",\"difficulty\":2,\"est_min\":10,\"why\":\"执行理由，须包含先修知识点名\",\"from_kp\":\"先修知识点名或空\"}],\"tips\":\"执行提示\",\"deferred\":\"未排入债边的延期说明\"}；"
    + "2) kp 与 from_kp 必须使用给定白名单中的准确名称，不得发明新名称；"
    + "3) 单日任务总时长不超过给定预算；"
    + "4) 先修任务的 day 不得晚于目标任务；"
    + "5) 不要输出 JSON 以外的任何内容。";

  // 编排主流程：LLM 草案 → 白名单校验 → K1-K5 终审 + 自动修复 → 回退
  function aiPlanOrchestrate(b, note) {
    var M = window.__AP_MIMO__;
    if (!M || !M.ready()) return Promise.resolve(null);
    var debts = [];
    try {
      debts = (scanDebts() || []).filter(function (d) { return d.status === "open"; }).map(function (d) {
        return { fromKp: d.from, fromName: d.fromName, toKp: d.to, toName: d.toName, imp: d.imp || 0, estMin: 10 };
      });
    } catch (e) {}
    var kpWhitelist = (activeNodes() || []).map(function (n) { return n.title; }).slice(0, 60);
    var topDebts = debts.slice(0, 5).map(function (d) { return { fromKp: d.fromKp, toKp: d.toKp }; });

    // 本地规则计划兜底
    function localPlan() {
      var plan = [];
      var id = 1, day = 1;
      debts.slice(0, 8).forEach(function (d, i) {
        plan.push({ id: id++, day: day, kp: d.fromKp, layer: "core", min: 12, st: "todo", fromKp: d.fromKp, why: "从「" + d.fromName + "」先垫一步，再检验「" + d.toName + "」" });
        plan.push({ id: id++, day: Math.min(14, day + 1), kp: d.toKp, layer: "challenge", min: 10, st: "todo", fromKp: d.fromKp, why: "巩固「" + d.fromName + "」后检验「" + d.toName + "」" });
        day = Math.min(14, day + 1);
      });
      return plan;
    }

    return M.complete([
      { role: "system", content: PLAN_SYS },
      { role: "user", content: buildPlanUserPrompt(b, debts, kpWhitelist, note) }
    ], { maxTokens: 3000, temperature: 0.4, timeoutMs: 45000 }).then(function (r) {
      var draft = parsePlanDraft(r && r.text);
      if (!draft) return { plan: localPlan(), fromAi: false, strategy: null };
      // 白名单校验：幻觉条目剔除
      var kept = draft.items.filter(function (it) {
        return !kpWhitelist.length || kpWhitelist.indexOf(it.kp) >= 0;
      });
      if (!kept.length) return { plan: localPlan(), fromAi: false, strategy: draft.strategy };
      // 草案 → plan 条目（K3 why 回填）
      var id = 1;
      var plan = kept.map(function (it) {
        var why = it.why;
        if (it.from_kp && why.indexOf(it.from_kp) < 0) why = "为巩固「" + it.from_kp + "」再推进本点；" + why;
        if (!why) why = it.from_kp ? ("从「" + it.from_kp + "」先垫一步，再检验「" + it.kp + "」") : ("巩固「" + it.kp + "」（按教材顺序）");
        return {
          id: id++, day: it.day, kp: it.kp,
          layer: it.type === "quiz" || it.type === "drill" ? "challenge" : "core",
          min: it.est_min, st: "todo",
          fromKp: it.from_kp || it.kp, why: why
        };
      });
      // K1 自动压缩：超额压 min
      var byDay = {};
      plan.forEach(function (p) { byDay[p.day] = (byDay[p.day] || 0) + p.min; });
      Object.keys(byDay).forEach(function (d) {
        if (byDay[d] > 40) {
          var over = byDay[d] - 40;
          plan.forEach(function (p) {
            if (over <= 0 || p.day != d || p.layer !== "challenge") return;
            var cut = Math.min(over, p.min - 5);
            if (cut > 0) { p.min -= cut; over -= cut; }
          });
        }
      });
      var check = checkPlanK(plan, 40, topDebts);
      if (!check.ok) return { plan: localPlan(), fromAi: false, strategy: draft.strategy, violations: check.violations };
      return { plan: plan, fromAi: true, strategy: draft.strategy };
    }).catch(function () {
      return { plan: localPlan(), fromAi: false, strategy: null };
    });
  }

  window.__AP_PLAN_AI__ = { orchestrate: aiPlanOrchestrate, checkK: checkPlanK, parse: parsePlanDraft, localPlan: null };

  // ═══ 4. UI 接线：构图按钮 + 计划按钮 + 多格式上传 ══════════════════════
  document.addEventListener("DOMContentLoaded", function () {
    // AI 增强构图按钮（基准旧版 UI 无此按钮时动态注入到识网页）
    var gbtn = document.getElementById("btnGraphAI");
    if (!gbtn) {
      var gHost = document.getElementById("gSwitch");
      if (gHost && gHost.parentNode) {
        gbtn = document.createElement("button");
        gbtn.id = "btnGraphAI";
        gbtn.className = "btn";
        gbtn.textContent = "🤖 AI 增强构图";
        gbtn.style.marginRight = "8px";
        gHost.parentNode.insertBefore(gbtn, gHost);
      }
    }
    if (gbtn) gbtn.addEventListener("click", graphAiEnhance);
    // AI 计划官按钮（若知债页有 btnPlan，替换为计划官编排）
    var pbtn = document.getElementById("btnPlan");
    if (pbtn) {
      pbtn.addEventListener("click", function () {
        var noteEl = document.getElementById("planAsk");
        var note = noteEl ? String(noteEl.value || "") : "";
        var msg = document.getElementById("debtMsg");
        if (msg) { msg.className = "status"; msg.textContent = "AI 计划官编排中…（四源输入 → 草案 → K1-K5 终审）"; }
        aiPlanOrchestrate(activeBook(), note).then(function (res) {
          if (!res || !res.plan || !res.plan.length) {
            if (msg) { msg.className = "status warn"; msg.textContent = "计划生成失败，可重试或使用本地计划"; }
            return;
          }
          S.plan = res.plan;
          save(S);
          if (typeof renderToday === "function") try { renderToday(); } catch (e) {}
          if (msg) {
            msg.className = "status ok";
            msg.textContent = (res.fromAi ? "✔ AI 计划官编排完成" : "✔ 本地规则计划（AI 不可用时回退）")
              + " · " + res.plan.length + " 项任务 · K1-K5 校验通过"
              + (res.strategy ? " · 策略：" + res.strategy : "");
          }
        });
      }, true); // capture 阶段拦截，替代原 buildPlan
    }
    // 多格式上传：拦截 parseFiles 的文件读取，为 docx/pptx/epub/doc/ppt/html/md 走多格式解析
    // （基准 parseFiles 已有 PDF/TXT 分发，这里补非 PDF 格式）
    window.__AP_MULTI_FORMAT_HOOK__ = parseMultiFormat;
  });

})();
