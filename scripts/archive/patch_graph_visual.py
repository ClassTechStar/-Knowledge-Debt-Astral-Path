from pathlib import Path

path = Path(r"C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path\src\AstralPath.Api\wwwroot\index.html")
c = path.read_text(encoding="utf-8")

# --- CSS upgrade ---
old_css = '''.dag-toolbar { display:flex; justify-content:space-between; gap:10px; margin:12px 0 8px; color:var(--muted); font-size:12px; }
.dag-legend span { margin-right:10px; display:inline-flex; align-items:center; gap:5px; }
.dag-legend span::before { width:9px; height:9px; border-radius:50%; content:""; background:var(--node-ok); }
.dag-legend .chapter::before { border-radius:3px; background:var(--node-chapter); }
.dag-viewport { position:relative; height:min(560px,70vh); min-height:420px; overflow:hidden; border:1px solid var(--rule); border-radius:var(--radius-group); background:#f5f6f7; background-image:radial-gradient(circle, #c7cacf 1px, transparent 1px); background-size:20px 20px; cursor:grab; }
.dag-canvas { position:absolute; left:0; top:0; transform-origin:0 0; }
.dag-canvas svg { position:absolute; inset:0; overflow:visible; pointer-events:none; }
.dag-canvas svg path { fill:none; stroke:#9ba0a6; stroke-width:1.6; }
.dag-node { position:absolute; max-width:170px; padding:10px 12px; border:2px solid #b7bbc0; border-radius:16px; background:#fbfbfc; box-shadow:0 8px 18px rgba(45,47,50,.08); cursor:pointer; text-align:left; }
.dag-node span { display:block; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-size:13px; font-weight:700; }
.dag-node small { color:var(--muted); font-size:10px; }
.dag-node.chapter { border-color:var(--node-chapter); background:#e7f1f9; }
.dag-node.debt { border-color:var(--node-target); background:#ffe1e5; color:#792e38; }
.dag-node.ok { border-color:var(--node-ok); background:#dcf4e7; color:#1e5d43; }
.dag-node.selected { outline:2px solid var(--primary); }'''

new_css = '''.dag-toolbar { display:flex; justify-content:space-between; align-items:center; gap:10px; margin:12px 0 8px; color:var(--muted); font-size:12px; flex-wrap:wrap; }
.dag-legend span { margin-right:10px; display:inline-flex; align-items:center; gap:5px; }
.dag-legend span::before { width:9px; height:9px; border-radius:50%; content:""; background:var(--node-ok); }
.dag-legend .chapter::before { border-radius:3px; background:var(--node-chapter); }
.dag-legend .term::before { border-radius:50%; background:var(--node-ok); }
.dag-zoom { display:flex; gap:6px; align-items:center; }
.dag-zoom button { min-width:32px; min-height:32px; padding:4px 8px; border:1px solid var(--rule); border-radius:999px; background:var(--surface); cursor:pointer; }
.dag-viewport { position:relative; height:min(640px,72vh); min-height:460px; overflow:hidden; border:1px solid var(--rule); border-radius:var(--radius-group); background:radial-gradient(circle at 20% 20%, #eef3f8, #f4f6f8 45%, #e9eef3); background-image:radial-gradient(circle, rgba(150,160,175,.35) 1px, transparent 1px); background-size:22px 22px; cursor:grab; }
.dag-canvas { position:absolute; left:0; top:0; transform-origin:0 0; }
.dag-canvas svg { position:absolute; inset:0; overflow:visible; pointer-events:none; }
.dag-canvas svg path { fill:none; stroke:#9aa6b5; stroke-width:1.4; opacity:.85; }
.dag-canvas svg path.highlight { stroke:#0D72D9; stroke-width:2.4; opacity:1; }
.dag-canvas svg path.chapter-link { stroke:#6d9fc7; stroke-width:1.8; }
.dag-node { position:absolute; max-width:168px; min-width:108px; padding:10px 12px; border:2px solid #b7bbc0; border-radius:14px; background:#fbfbfc; box-shadow:0 6px 16px rgba(45,47,50,.08); cursor:pointer; text-align:left; transition:border-color .15s ease, box-shadow .15s ease, transform .15s ease; }
.dag-node:hover { transform:translateY(-1px); box-shadow:0 10px 22px rgba(45,47,50,.12); }
.dag-node span { display:block; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-size:12.5px; font-weight:700; }
.dag-node small { display:block; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; color:var(--muted); font-size:10px; margin-top:2px; }
.dag-node.chapter { border-color:#4f86c6; background:linear-gradient(180deg,#e8f2fb,#d9e9f8); color:#1d3d5f; }
.dag-node.section { border-color:#8aa4c4; background:#f0f5fa; }
.dag-node.term, .dag-node.ok { border-color:#55a77b; background:linear-gradient(180deg,#e5f7ee,#d4f0e2); color:#1e5d43; }
.dag-node.debt { border-color:#d95a67; background:#ffe1e5; color:#792e38; }
.dag-node.selected { border-color:#0D72D9; box-shadow:0 0 0 3px rgba(13,114,217,.2), 0 10px 22px rgba(45,47,50,.12); }
.dag-node .layer-tag { display:inline-block; margin-top:4px; padding:1px 6px; border-radius:999px; background:rgba(255,255,255,.7); color:var(--muted); font-size:10px; }
.book-active-banner { display:flex; align-items:center; gap:8px; flex-wrap:wrap; margin-bottom:8px; padding:8px 12px; border-radius:12px; background:#eef5ff; border:1px solid #c5daf5; color:#1d3d5f; font-size:13px; }'''

if old_css in c:
    c = c.replace(old_css, new_css)
    print("css_ok")
else:
    print("css_missing")

# --- graph toolbar zoom controls ---
old_tb = '''        <div class="dag-toolbar">
          <div class="dag-legend"><span class="chapter">章节</span><span>知识点</span></div>
          <div id="graphMeta">-</div>
        </div>'''
new_tb = '''        <div class="dag-toolbar">
          <div class="dag-legend"><span class="chapter">章节</span><span class="term">知识点/关键词</span><span style="color:#0D72D9">当前教材已高亮</span></div>
          <div class="dag-zoom">
            <button type="button" id="dagZoomOut" title="缩小">−</button>
            <span id="dagZoomLabel">100%</span>
            <button type="button" id="dagZoomIn" title="放大">+</button>
            <button type="button" id="dagZoomReset" title="重置视图">重置</button>
          </div>
          <div id="graphMeta">-</div>
        </div>
        <div id="bookActiveBanner" class="book-active-banner"></div>'''
if old_tb in c:
    c = c.replace(old_tb, new_tb)
    print("toolbar_ok")
else:
    print("toolbar_missing")

# --- fix fillBookSelects ---
old_fill = '''function fillBookSelects() {
  const gsel = $("graphSelect");
  const tsel = $("todayBookSelect");
  const opts = graphs.map(function (g) {
    return '<option value="' + escapeHtml(g.graphId) + '">' + escapeHtml(shortBookName(g.materialName)) +
      " · " + g.nodeCount + "n/" + g.edgeCount + "e</option>";
  }).join("");
  if (gsel) {
    gsel.innerHTML = opts || '<option value="">（暂无图谱）</option>';
  }
  if (tsel) {
    tsel.innerHTML = '<option value="">（当前教材）</option>' + opts;
    if (currentGraph) tsel.value = currentGraph.graphId;
  }
}'''
new_fill = '''function fillBookSelects(preferGraphId) {
  const gsel = $("graphSelect");
  const tsel = $("todayBookSelect");
  const keep = preferGraphId || (currentGraph && currentGraph.graphId) || (gsel && gsel.value) || "";
  const opts = graphs.map(function (g) {
    return '<option value="' + escapeHtml(g.graphId) + '">' + escapeHtml(shortBookName(g.materialName)) +
      " · " + g.nodeCount + "n/" + g.edgeCount + "e</option>";
  }).join("");
  if (gsel) {
    gsel.innerHTML = opts || '<option value="">（暂无图谱）</option>';
    if (keep && graphs.some(function (g) { return g.graphId === keep; })) gsel.value = keep;
  }
  if (tsel) {
    tsel.innerHTML = '<option value="">（当前教材）</option>' + opts;
    if (keep && graphs.some(function (g) { return g.graphId === keep; })) tsel.value = keep;
    else if (currentGraph) tsel.value = currentGraph.graphId;
  }
  window.__zzActiveGraphId = keep;
}'''
if old_fill in c:
    c = c.replace(old_fill, new_fill)
    print("fill_ok")
else:
    print("fill_missing")

# --- fix loadSelectedGraph to sync select after fill ---
old_sync = '''    try { sessionStorage.setItem("astralpath_graph_id", g.graphId); } catch (e) {}
    fillBookSelects();
    renderBookChips();'''
new_sync = '''    try { sessionStorage.setItem("astralpath_graph_id", g.graphId); } catch (e) {}
    // 关键：重建下拉框后必须把选中项设回当前教材
    fillBookSelects(g.graphId);
    const gsel = $("graphSelect");
    if (gsel && gsel.value !== g.graphId) gsel.value = g.graphId;
    const tsel = $("todayBookSelect");
    if (tsel && tsel.value !== g.graphId) tsel.value = g.graphId;
    renderBookChips();'''
if old_sync in c:
    c = c.replace(old_sync, new_sync)
    print("sync_ok")
else:
    print("sync_missing")

# --- rewrite renderDag with layered layout ---
old_dag = c[c.find("function renderDag(nodes, edges, debtPairs) {"):c.find("function selectDagNode(id)")]
new_dag = r'''function classifyNode(n) {
  const d = String(n.description || "").toLowerCase();
  const name = String(n.name || "");
  if (d.indexOf("ocr:chapter") >= 0 || d === "chapter" || /^第\s*[0-9一二三四五六七八九十]+章/.test(name)) return "chapter";
  if (d.indexOf("ocr:section") >= 0 || d === "section" || /^\d+(\.\d+)+/.test(name)) return "section";
  if (d === "title") return "chapter";
  if (d.indexOf("freq=") >= 0 || d.indexOf("关键词") >= 0) return "term";
  return "term";
}

function renderDag(nodes, edges, debtPairs) {
  const debtSet = new Set();
  (debtPairs || []).forEach(function (p) { debtSet.add(p.fromKp + "->" + p.toKp); });
  const nodeBox = $("dagNodes");
  const edgeGroup = $("dagEdgeGroup");
  const list = (nodes || []).slice();

  const chapters = [];
  const sections = [];
  const terms = [];
  list.forEach(function (n) {
    const kind = classifyNode(n);
    if (kind === "chapter") chapters.push(n);
    else if (kind === "section") sections.push(n);
    else terms.push(n);
  });

  // 若章节过少，按顺序把前排节点当章节
  if (chapters.length < 2 && list.length > 2) {
    list.slice(0, Math.min(8, list.length)).forEach(function (n) {
      if (chapters.indexOf(n) < 0) chapters.push(n);
    });
  }

  const positions = {};
  const NODE_W = 150;
  const NODE_H = 56;
  const GAP_X = 170;
  const GAP_Y = 90;
  const START_X = 36;
  const START_Y = 36;

  // 第 1 层：章节横向链
  chapters.forEach(function (n, i) {
    const col = i % 6;
    const row = Math.floor(i / 6);
    positions[n.id] = {
      x: START_X + col * GAP_X,
      y: START_Y + row * (NODE_H + 48),
      node: n,
      layer: "chapter"
    };
  });

  const chapterRows = Math.max(1, Math.ceil(chapters.length / 6));
  const sectionTop = START_Y + chapterRows * (NODE_H + 48) + 20;

  // 第 2 层：小节
  sections.forEach(function (n, i) {
    const col = i % 7;
    const row = Math.floor(i / 7);
    positions[n.id] = {
      x: START_X + col * (GAP_X - 8),
      y: sectionTop + row * (NODE_H + 16),
      node: n,
      layer: "section"
    };
  });

  const sectionRows = Math.max(0, Math.ceil(sections.length / 7));
  const termTop = sectionTop + (sectionRows > 0 ? sectionRows * (NODE_H + 16) + 24 : 40);

  // 第 3 层：关键词网格
  terms.forEach(function (n, i) {
    const col = i % 8;
    const row = Math.floor(i / 8);
    positions[n.id] = {
      x: START_X + col * (GAP_X - 20),
      y: termTop + row * (NODE_H - 6),
      node: n,
      layer: "term"
    };
  });

  // 画节点
  let nodeHtml = "";
  Object.keys(positions).forEach(function (id) {
    const p = positions[id];
    const n = p.node;
    const kind = classifyNode(n);
    const isDebt = (edges || []).some(function (e) {
      return debtSet.has(e.from + "->" + e.to) && (e.from === n.id || e.to === n.id);
    });
    const cls = isDebt ? "debt" : (kind === "chapter" ? "chapter" : (kind === "section" ? "section" : "term"));
    const layerLabel = kind === "chapter" ? "章节" : (kind === "section" ? "小节" : "知识点");
    const course = String(n.course || "");
    const sub = (course && course.length <= 18 && !/^[0-9a-f-]{16,}$/i.test(course))
      ? course
      : (n.description || "");
    nodeHtml += '<button type="button" class="dag-node ' + cls + '" data-id="' + escapeHtml(n.id) +
      '" data-layer="' + kind + '" style="left:' + p.x + "px;top:" + p.y + "px;width:" + (NODE_W + 8) + 'px">' +
      '<span title="' + escapeHtml(n.name) + '">' + escapeHtml(n.name) + "</span>" +
      "<small>" + escapeHtml(String(sub).slice(0, 22)) + "</small>" +
      '<span class="layer-tag">' + layerLabel + "</span></button>";
  });
  if (!nodeHtml) {
    nodeHtml = '<div class="inspector-empty" style="position:absolute;inset:0;display:grid;place-items:center">该教材暂无图谱节点</div>';
  }
  nodeBox.innerHTML = nodeHtml;

  // 画边：章节链用更粗的蓝线，其它细线
  const paths = [];
  (edges || []).forEach(function (e) {
    const a = positions[e.from], b = positions[e.to];
    if (!a || !b) return;
    const x1 = a.x + (NODE_W + 8) / 2;
    const y1 = a.y + NODE_H;
    const x2 = b.x + (NODE_W + 8) / 2;
    const y2 = b.y;
    // 同层横向
    if (Math.abs(a.y - b.y) < 8) {
      const xa = a.x + NODE_W + 8, xb = b.x;
      const midY = a.y + NODE_H / 2;
      const cls = (a.layer === "chapter" && b.layer === "chapter") ? "chapter-link" : "";
      paths.push('<path class="' + cls + '" data-from="' + escapeHtml(e.from) + '" data-to="' + escapeHtml(e.to) +
        '" d="M ' + xa + " " + midY + " C " + (xa + 30) + " " + midY + ", " + (xb - 30) + " " + midY + ", " + xb + " " + midY +
        '" marker-end="url(#arrow)"></path>');
    } else {
      paths.push('<path data-from="' + escapeHtml(e.from) + '" data-to="' + escapeHtml(e.to) +
        '" d="M ' + x1 + " " + y1 + " C " + x1 + " " + ((y1 + y2) / 2) + ", " + x2 + " " + ((y1 + y2) / 2) + ", " + x2 + " " + y2 +
        '" marker-end="url(#arrow)"></path>');
    }
  });
  edgeGroup.innerHTML = paths.join("");
  applyDagTransform();
  window.__dagPositions = positions;
  window.__dagEdges = edges || [];
  nodeBox.querySelectorAll(".dag-node").forEach(function (el) {
    el.addEventListener("click", function () { selectDagNode(el.getAttribute("data-id")); });
  });

  // banner
  const banner = $("bookActiveBanner");
  if (banner && currentGraph) {
    const ch = chapters.length, sec = sections.length, tm = terms.length;
    banner.innerHTML = "当前图谱：<strong>" + escapeHtml(currentGraph.materialName) + "</strong>" +
      " · 章节 " + ch + " · 小节 " + sec + " · 知识点 " + tm +
      " · 下拉框已同步：" + escapeHtml(shortBookName(currentGraph.materialName));
  }
}

'''
if old_dag:
    c = c.replace(old_dag, new_dag)
    print("dag_ok")
else:
    print("dag_missing")

# selectDagNode highlight edges
old_sel = '''function selectDagNode(id) {
  document.querySelectorAll(".dag-node").forEach(function (el) {
    el.classList.toggle("selected", el.getAttribute("data-id") === id);
  });'''
new_sel = '''function selectDagNode(id) {
  document.querySelectorAll(".dag-node").forEach(function (el) {
    el.classList.toggle("selected", el.getAttribute("data-id") === id);
  });
  document.querySelectorAll("#dagEdgeGroup path").forEach(function (p) {
    const from = p.getAttribute("data-from");
    const to = p.getAttribute("data-to");
    p.classList.toggle("highlight", from === id || to === id);
  });'''
if old_sel in c:
    c = c.replace(old_sel, new_sel)
    print("select_ok")

# zoom controls bind + label
old_apply = '''function applyDagTransform() {
  const c = $("dagCanvas");
  if (c) c.style.transform = "translate(" + dagState.x + "px," + dagState.y + "px) scale(" + dagState.zoom + ")";
}'''
new_apply = '''function applyDagTransform() {
  const c = $("dagCanvas");
  if (c) c.style.transform = "translate(" + dagState.x + "px," + dagState.y + "px) scale(" + dagState.zoom + ")";
  const lab = $("dagZoomLabel");
  if (lab) lab.textContent = Math.round(dagState.zoom * 100) + "%";
}

function bindDagZoom() {
  const zin = $("dagZoomIn"), zout = $("dagZoomOut"), zres = $("dagZoomReset");
  if (zin) zin.onclick = function () {
    dagState.zoom = Math.min(1.8, dagState.zoom * 1.12);
    applyDagTransform();
  };
  if (zout) zout.onclick = function () {
    dagState.zoom = Math.max(0.5, dagState.zoom / 1.12);
    applyDagTransform();
  };
  if (zres) zres.onclick = function () {
    dagState.zoom = 1; dagState.x = 20; dagState.y = 20;
    applyDagTransform();
  };
}
bindDagZoom();'''
if old_apply in c:
    c = c.replace(old_apply, new_apply)
    print("zoom_ok")

# chip click also keeps select synced - already sets value before load
# bump ui version
c = c.replace("UI-rotate-v3", "UI-graph-v4")

path.write_text(c, encoding="utf-8")
print("written", path.stat().st_size)
print("has_fill_keep", "preferGraphId" in c)
print("has_layered", "classifyNode" in c)
print("has_banner", "bookActiveBanner" in c)
print("ui", "UI-graph-v4" in c)
