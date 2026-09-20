from pathlib import Path

path = Path(r"C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path\src\AstralPath.Api\wwwroot\index.html")
c = path.read_text(encoding="utf-8")

old_graph = '''  <main id="page-graph" class="page hidden">
    <header class="page-header">
      <div>
        <h1>识网</h1>
        <p>由教材自动生成的知识图谱：章节先修链 + 关键词节点。</p>
      </div>
      <div class="page-header__actions">
        <button type="button" class="button" id="btnRefreshGraphs">刷新图谱</button>
        <button type="button" class="button" id="btnPreviewDebts">预览债边</button>
      </div>
    </header>
    <div class="graph-layout">
      <section class="panel">
        <header style="display:flex;justify-content:space-between;gap:12px;align-items:center">
          <div><h2>知识图谱</h2><p id="graphSubtitle">选择图谱</p></div>
          <select id="graphSelect" style="max-width:260px"></select>
        </header>
        <div class="dag-toolbar">
          <div class="dag-legend"><span class="chapter">章节</span><span>知识点</span></div>
          <div id="graphMeta">-</div>
        </div>
        <div id="dagViewport" class="dag-viewport">
          <div id="dagCanvas" class="dag-canvas">
            <svg id="dagEdges" width="2200" height="1400" aria-hidden="true">
              <defs>
                <marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
                  <path d="M 0 0 L 10 5 L 0 10 z"></path>
                </marker>
              </defs>
              <g id="dagEdgeGroup"></g>
            </svg>
            <div id="dagNodes"></div>
          </div>
        </div>
      </section>
      <aside class="panel graph-inspector">
        <header><h2>节点检视</h2></header>
        <div id="graphInspector" class="inspector-empty">点击 DAG 节点。</div>
        <div id="debtPreview" style="margin-top:16px"></div>
      </aside>
    </div>
  </main>'''

new_graph = '''  <main id="page-graph" class="page hidden">
    <header class="page-header">
      <div>
        <h1>识网</h1>
        <p>切换下方教材卡片，图谱、知识点与该书真题会一起切换（不是空壳下拉框）。</p>
      </div>
      <div class="page-header__actions">
        <button type="button" class="button" id="btnRefreshGraphs">刷新图谱</button>
        <button type="button" class="button" id="btnPreviewDebts">预览债边</button>
        <button type="button" class="button primary" id="btnUseBookToday">用本书出今日题</button>
      </div>
    </header>
    <div class="panel" style="margin-bottom:16px">
      <header><h2>教材切换</h2><p id="bookSwitchHint">点击卡片切换当前教材</p></header>
      <div id="bookChips" style="display:flex;flex-wrap:wrap;gap:8px"></div>
      <div id="graphSwitchStatus" class="status-line">尚未选择教材</div>
    </div>
    <div class="graph-layout">
      <section class="panel">
        <header style="display:flex;justify-content:space-between;gap:12px;align-items:center;flex-wrap:wrap">
          <div>
            <h2>知识图谱 <span id="activeBookBadge" class="badge">-</span></h2>
            <p id="graphSubtitle">选择教材</p>
          </div>
          <select id="graphSelect" style="max-width:320px"></select>
        </header>
        <div class="dag-toolbar">
          <div class="dag-legend"><span class="chapter">章节</span><span>知识点</span></div>
          <div id="graphMeta">-</div>
        </div>
        <div id="dagViewport" class="dag-viewport">
          <div id="dagCanvas" class="dag-canvas">
            <svg id="dagEdges" width="2200" height="1400" aria-hidden="true">
              <defs>
                <marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
                  <path d="M 0 0 L 10 5 L 0 10 z"></path>
                </marker>
              </defs>
              <g id="dagEdgeGroup"></g>
            </svg>
            <div id="dagNodes"></div>
          </div>
        </div>
      </section>
      <aside class="panel graph-inspector">
        <header><h2>节点检视</h2></header>
        <div id="graphInspector" class="inspector-empty">点击 DAG 节点。</div>
        <div id="debtPreview" style="margin-top:16px"></div>
        <div id="bookQuestions" style="margin-top:16px"></div>
      </aside>
    </div>
  </main>'''

if old_graph in c:
    c = c.replace(old_graph, new_graph)
    print("graph_html_ok")
else:
    print("graph_html_missing")

# Add book selector on today page
old_today_header = '''      <div class="page-header__actions">
        <button type="button" class="button" id="btnLoadToday">加载今日</button>
        <button type="button" class="button" id="btnBookToday">教材任务</button>
        <button type="button" class="button" id="btnAdvance">Demo +1 天</button>
      </div>'''
new_today_header = '''      <div class="page-header__actions">
        <select id="todayBookSelect" style="min-width:220px;min-height:40px">
          <option value="">（当前教材）</option>
        </select>
        <button type="button" class="button" id="btnLoadToday">加载课程任务</button>
        <button type="button" class="button" id="btnBookToday">加载教材真题</button>
        <button type="button" class="button" id="btnAdvance">Demo +1 天</button>
      </div>'''
if old_today_header in c:
    c = c.replace(old_today_header, new_today_header)
    print("today_html_ok")
else:
    print("today_html_missing")

old_js_load = '''async function loadGraphs() {
  try { graphs = await j("/v1/knowledge-graphs") || []; } catch (e) { graphs = []; }
  const sel = $("graphSelect");
  if (!graphs.length) {
    sel.innerHTML = '<option value="">（暂无自动图谱）</option>';
    $("dagNodes").innerHTML = "";
    $("dagEdgeGroup").innerHTML = "";
    $("graphSubtitle").textContent = "请先在藏书阁解析资料";
    return;
  }
  let html = "";
  for (let i = 0; i < graphs.length; i++) {
    const g = graphs[i];
    html += '<option value="' + escapeHtml(g.graphId) + '">' + escapeHtml(g.materialName) +
      " · " + g.nodeCount + "n/" + g.edgeCount + "e</option>";
  }
  sel.innerHTML = html;
  if (!sel.value || !graphs.some(function (g) { return g.graphId === sel.value; })) sel.value = graphs[0].graphId;
  await loadSelectedGraph();
}

async function loadSelectedGraph() {
  const graphId = $("graphSelect").value;
  if (!graphId) return;
  try {
    const g = await j("/v1/knowledge-graphs/" + graphId);
    currentGraph = g;
    $("graphSubtitle").textContent = g.materialName + " · source=" + g.source;
    $("graphMeta").textContent = "nodes=" + g.nodes.length + " edges=" + g.edges.length + " cycles=" + ((g.cycles || []).length);
    renderDag(g.nodes || [], g.edges || [], []);
    $("graphInspector").innerHTML =
      '<div class="kv">' +
      "<div><span>图谱</span><span>" + escapeHtml(g.graphId) + "</span></div>" +
      "<div><span>资料</span><span>" + escapeHtml(g.materialName) + "</span></div>" +
      "<div><span>版本</span><span>" + g.graphVersion + "</span></div>" +
      "<div><span>无环</span><span>" + (g.ok ? "通过" : "未通过") + "</span></div>" +
      "</div>";
  } catch (e) {
    $("graphSubtitle").textContent = "图谱加载失败：" + e.message;
  }
}'''

new_js_load = r'''function shortBookName(name) {
  name = String(name || "");
  name = name.replace(/\.pdf$/i, "").replace(/\.PDF$/, "");
  if (name.length <= 22) return name;
  return name.slice(0, 22) + "…";
}

function resetDagViewport() {
  dagState.zoom = 1;
  dagState.x = 20;
  dagState.y = 20;
  applyDagTransform();
  const nodes = $("dagNodes");
  const edges = $("dagEdgeGroup");
  if (nodes) nodes.innerHTML = '<div class="inspector-empty" style="position:absolute;inset:0;display:grid;place-items:center">加载中…</div>';
  if (edges) edges.innerHTML = "";
}

function renderBookChips() {
  const box = $("bookChips");
  if (!box) return;
  if (!graphs.length) {
    box.innerHTML = '<div class="inspector-empty">暂无教材图谱，请先在藏书阁解析</div>';
    return;
  }
  box.innerHTML = graphs.map(function (g) {
    const active = currentGraph && currentGraph.graphId === g.graphId;
    return '<button type="button" class="button ' + (active ? "primary" : "") + '" data-graph-id="' +
      escapeHtml(g.graphId) + '" style="max-width:100%">' +
      escapeHtml(shortBookName(g.materialName)) + " · " + g.nodeCount + "n</button>";
  }).join("");
  box.querySelectorAll("[data-graph-id]").forEach(function (btn) {
    btn.addEventListener("click", function () {
      const id = btn.getAttribute("data-graph-id");
      $("graphSelect").value = id;
      loadSelectedGraph(id);
    });
  });
}

function fillBookSelects() {
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
}

async function loadGraphs(preferGraphId) {
  try { graphs = await j("/v1/knowledge-graphs") || []; } catch (e) { graphs = []; }
  fillBookSelects();
  renderBookChips();
  if (!graphs.length) {
    $("graphSubtitle").textContent = "请先在藏书阁解析资料";
    $("graphSwitchStatus").textContent = "暂无图谱";
    $("dagNodes").innerHTML = "";
    $("dagEdgeGroup").innerHTML = "";
    return;
  }
  let nextId = preferGraphId || $("graphSelect").value;
  if (!nextId || !graphs.some(function (g) { return g.graphId === nextId; })) {
    nextId = (currentGraph && currentGraph.graphId) || graphs[0].graphId;
  }
  $("graphSelect").value = nextId;
  await loadSelectedGraph(nextId);
}

async function loadSelectedGraph(graphId) {
  const id = graphId || $("graphSelect").value;
  if (!id) return;
  if ($("graphSelect").value !== id) $("graphSelect").value = id;
  const status = $("graphSwitchStatus");
  const subtitle = $("graphSubtitle");
  if (status) {
    status.textContent = "正在加载图谱：" + id + " …";
    status.className = "status-line";
  }
  if (subtitle) subtitle.textContent = "加载中…";
  resetDagViewport();
  try {
    const g = await j("/v1/knowledge-graphs/" + encodeURIComponent(id));
    currentGraph = g;
    // persist selection
    try { sessionStorage.setItem("astralpath_graph_id", g.graphId); } catch (e) {}
    fillBookSelects();
    renderBookChips();
    const badge = $("activeBookBadge");
    if (badge) {
      badge.textContent = shortBookName(g.materialName);
      badge.className = "badge ready";
    }
    if (subtitle) {
      subtitle.textContent = "当前教材：" + g.materialName + " · source=" + g.source +
        " · graphId=" + g.graphId;
    }
    $("graphMeta").textContent = "nodes=" + (g.nodes || []).length +
      " edges=" + (g.edges || []).length +
      " cycles=" + ((g.cycles || []).length) +
      " · 已切换";
    dagState.zoom = 1; dagState.x = 20; dagState.y = 20;
    renderDag(g.nodes || [], g.edges || [], []);
    const sample = (g.nodes || []).slice(0, 3).map(function (n) { return n.name; }).join(" / ");
    $("graphInspector").innerHTML =
      '<div class="kv">' +
      "<div><span>当前教材</span><span>" + escapeHtml(shortBookName(g.materialName)) + "</span></div>" +
      "<div><span>完整文件名</span><span>" + escapeHtml(g.materialName) + "</span></div>" +
      "<div><span>graphId</span><span>" + escapeHtml(String(g.graphId).slice(-16)) + "…</span></div>" +
      "<div><span>节点/边</span><span>" + (g.nodes || []).length + "/" + (g.edges || []).length + "</span></div>" +
      "<div><span>无环</span><span>" + (g.ok ? "通过" : "未通过") + "</span></div>" +
      "<div><span>示例节点</span><span>" + escapeHtml(sample) + "</span></div>" +
      "</div>";
    if (status) {
      status.textContent = "已切换到《" + shortBookName(g.materialName) + "》：" +
        (g.nodes || []).length + " 节点 / " + (g.edges || []).length + " 边";
      status.className = "status-line ok";
    }
    // load this book's textbook questions
    await loadBookQuestionsForGraph(g);
  } catch (e) {
    if (subtitle) subtitle.textContent = "图谱加载失败：" + e.message;
    if (status) {
      status.textContent = "切换失败：" + e.message;
      status.className = "status-line error";
    }
  }
}

async function loadBookQuestionsForGraph(graph) {
  const box = $("bookQuestions");
  if (!box) return;
  box.innerHTML = '<div class="inspector-empty">读取本书真题…</div>';
  try {
    const materialId = graph.materialId || (String(graph.graphId || "").replace(/^auto-/, ""));
    let data = null;
    try {
      data = await j("/v1/materials/" + materialId + "/textbook-questions?maxTasks=4");
    } catch (e1) {
      // fallback: match by material name via list
      const mats = await j("/v1/materials") || [];
      const m = mats.filter(function (x) { return x.name === graph.materialName || x.id === materialId; })[0];
      if (!m) throw e1;
      data = await j("/v1/materials/" + m.id + "/textbook-questions?maxTasks=4");
    }
    const tasks = data.tasks || [];
    window._currentBookQuestions = { materialName: data.materialName || graph.materialName, bank: data.bank, tasks: tasks };
    if (!tasks.length) {
      box.innerHTML = '<h2 style="font-size:17px;margin:0 0 6px">本书真题</h2><div class="status-line">' +
        escapeHtml(data.note || "暂无匹配题库") + "</div>";
      return;
    }
    let html = '<h2 style="font-size:17px;margin:0 0 6px">本书真题 <span class="badge">' +
      escapeHtml(data.bank || "bank") + " · " + tasks.length + "题</span></h2>";
    tasks.forEach(function (t) {
      html += '<div class="task-card" style="padding:10px"><strong style="font-size:13px">' +
        escapeHtml(t.kpName) + "</strong><div class=\"why\">" + escapeHtml(t.stem) +
        '</div><div class="why">正确：' + escapeHtml((t.options || [])[t.correctIndex || 0] || "-") + "</div></div>";
    });
    box.innerHTML = html;
  } catch (e) {
    box.innerHTML = '<h2 style="font-size:17px;margin:0 0 6px">本书真题</h2><div class="status-line error">' +
      escapeHtml(e.message) + "</div>";
  }
}

async function useCurrentBookForToday() {
  const graph = currentGraph;
  if (!graph) { alert("请先选择教材图谱"); return; }
  try {
    const materialId = graph.materialId || String(graph.graphId || "").replace(/^auto-/, "");
    let data = null;
    try {
      data = await j("/v1/materials/" + materialId + "/textbook-questions?maxTasks=8");
    } catch (e) {
      const mats = await j("/v1/materials") || [];
      const m = mats.filter(function (x) { return x.name === graph.materialName || x.graphId === graph.graphId; })[0];
      if (!m) throw e;
      data = await j("/v1/materials/" + m.id + "/textbook-questions?maxTasks=8");
    }
    window._materialToday = { materialName: data.materialName || graph.materialName, brief: data.brief, tasks: data.tasks || [] };
    if ($("todayBookSelect")) $("todayBookSelect").value = graph.graphId;
    go("today");
    renderTasks(window._materialToday);
  } catch (e) {
    alert("生成今日题失败：" + e.message);
  }
}

async function loadTodayByBook() {
  const gid = $("todayBookSelect") ? $("todayBookSelect").value : "";
  try {
    if (gid) {
      const g = await j("/v1/knowledge-graphs/" + encodeURIComponent(gid));
      currentGraph = g;
      const materialId = g.materialId || String(g.graphId || "").replace(/^auto-/, "");
      let data = null;
      try {
        data = await j("/v1/materials/" + materialId + "/textbook-questions?maxTasks=8");
      } catch (e) {
        const mats = await j("/v1/materials") || [];
        const m = mats.filter(function (x) { return x.name === g.materialName; })[0];
        if (!m) throw e;
        data = await j("/v1/materials/" + m.id + "/textbook-questions?maxTasks=8");
      }
      window._materialToday = { materialName: data.materialName || g.materialName, brief: data.brief, tasks: data.tasks || [] };
      renderTasks(window._materialToday);
      return;
    }
    const today = await j("/v1/materials/today-from-books?maxTasks=8");
    window._materialToday = today;
    renderTasks(today);
  } catch (e) {
    $("coach").innerHTML = '<div class="status-line error">' + escapeHtml(e.message) + "</div>";
  }
}'''

if old_js_load in c:
    c = c.replace(old_js_load, new_js_load)
    print("js_load_ok")
else:
    print("js_load_missing")

# rebind events: graph select, chips, book today
old_bind = '''$("btnRefreshGraphs").addEventListener("click", loadGraphs);
$("btnPreviewDebts").addEventListener("click", previewDebts);
$("graphSelect").addEventListener("change", loadSelectedGraph);'''
new_bind = '''$("btnRefreshGraphs").addEventListener("click", function () { loadGraphs($("graphSelect").value); });
$("btnPreviewDebts").addEventListener("click", previewDebts);
$("btnUseBookToday").addEventListener("click", useCurrentBookForToday);
$("graphSelect").addEventListener("change", function () { loadSelectedGraph(this.value); });
$("todayBookSelect").addEventListener("change", loadTodayByBook);'''
if old_bind in c:
    c = c.replace(old_bind, new_bind)
    print("bind_ok")
else:
    print("bind_missing")

# btnBookToday uses loadTodayByBook
c = c.replace('$("btnBookToday").addEventListener("click", loadBookToday);',
              '$("btnBookToday").addEventListener("click", loadTodayByBook);')
if 'loadTodayByBook' in c and 'btnBookToday' in c:
    print("book_today_bind_ok")

# go('graph') keep selection
old_go_graph = 'if (page === "graph") loadGraphs();'
new_go_graph = '''if (page === "graph") {
      let keep = null;
      try { keep = sessionStorage.getItem("astralpath_graph_id"); } catch (e) {}
      loadGraphs(keep);
    }
    if (page === "today") {
      const tsel = $("todayBookSelect");
      if (tsel && currentGraph) tsel.value = currentGraph.graphId;
    }'''
if old_go_graph in c:
    c = c.replace(old_go_graph, new_go_graph)
    print("go_graph_ok")
else:
    print("go_graph_missing")

# enhance renderDag node label: don't show long ids
old_node = '''nodeHtml += '<button type="button" class="dag-node ' + cls + '" data-id="' + escapeHtml(n.id) +
      '" style="left:' + p.x + "px;top:" + p.y + 'px"><span>' + escapeHtml(n.name) +
      "</span><small>" + escapeHtml(n.course || "") + " · " + escapeHtml(n.description || "") + "</small></button>";'''
new_node = '''const shortId = String(n.id || "").slice(-8);
    const kindLabel = n.description || "";
    nodeHtml += '<button type="button" class="dag-node ' + cls + '" data-id="' + escapeHtml(n.id) +
      '" style="left:' + p.x + "px;top:" + p.y + 'px"><span>' + escapeHtml(n.name) +
      "</span><small>" + escapeHtml(shortId) + " · " + escapeHtml(kindLabel) + "</small></button>";'''
if old_node in c:
    c = c.replace(old_node, new_node)
    print("node_label_ok")
else:
    print("node_label_missing")

# selectDagNode show cleaner id
old_sel = '''"<div><span>ID</span><span>" + escapeHtml(pos.node.id) + "</span></div>" +'''
new_sel = '''"<div><span>ID</span><span>" + escapeHtml(String(pos.node.id).slice(-12)) + "</span></div>" +'''
if old_sel in c:
    c = c.replace(old_sel, new_sel)
    print("sel_id_ok")

path.write_text(c, encoding="utf-8")
print("written", path.stat().st_size)
