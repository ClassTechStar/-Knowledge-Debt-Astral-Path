from pathlib import Path

path = Path(r"C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path\src\AstralPath.Api\wwwroot\index.html")
c = path.read_text(encoding="utf-8")

old_debt = '''  <main id="page-debt" class="page hidden">
    <header class="page-header">
      <div>
        <h1>知债诊断</h1>
        <p>会计课程包 BASELINE：impact = freq × (50 − score_c) × recency × weight</p>
      </div>
      <div class="page-header__actions">
        <button type="button" class="button" id="btnRefreshDebt">刷新诊断</button>
        <button type="button" class="button" id="btnCreatePlan">生成 14 天计划</button>
        <button type="button" class="button" id="btnTeacher">教师端</button>
      </div>
    </header>
    <div class="home-summary">
      <article><span>红边数</span><strong id="debtCount">-</strong></article>
      <article><span>最高 impact</span><strong id="debtTopImpact">-</strong></article>
      <article><span>图版本</span><strong>1</strong></article>
      <article><span>公式</span><strong>score-v1 / impact-v1</strong></article>
    </div>
    <div class="workspace-grid">
      <section class="panel">
        <header><h2>边列表视图</h2></header>
        <div id="graphView">-</div>
      </section>
      <section class="panel">
        <header><h2>诊断 Top 债边</h2></header>
        <div id="debts">-</div>
        <div id="planBox" style="margin-top:14px"></div>
      </section>
    </div>
    <section id="teacherCard" class="panel hidden" style="margin-top:16px">
      <header><h2>教师热点</h2><p>consent fail-closed</p></header>
      <div class="page-header__actions">
        <button type="button" class="button" id="btnGrantA">授权 A</button>
        <button type="button" class="button danger" id="btnRevokeA">撤销 A</button>
        <button type="button" class="button" id="btnHotspots">刷新热点</button>
      </div>
      <div id="hotspots">-</div>
    </section>
  </main>'''

new_debt = '''  <main id="page-debt" class="page hidden">
    <header class="page-header">
      <div>
        <h1>知债诊断</h1>
        <p>会计课程包 BASELINE：impact = freq × (50 − score_c) × recency × weight</p>
      </div>
      <div class="page-header__actions">
        <button type="button" class="button" id="btnRefreshDebt">刷新诊断</button>
        <button type="button" class="button primary" id="btnCreatePlan">生成/刷新 14 天计划</button>
        <button type="button" class="button" id="btnPlanToToday">计划任务→今日</button>
        <button type="button" class="button" id="btnHotspots">刷新教师热点</button>
      </div>
    </header>
    <div class="home-summary">
      <article><span>红边数</span><strong id="debtCount">-</strong></article>
      <article><span>最高 impact</span><strong id="debtTopImpact">-</strong></article>
      <article><span>计划天数</span><strong id="planDayCount">-</strong></article>
      <article><span>教师可见学生</span><strong id="teacherAuthCount">-</strong></article>
    </div>
    <div class="workspace-grid">
      <section class="panel">
        <header><h2>边列表视图</h2><p>可访问性优先</p></header>
        <div id="graphView">-</div>
      </section>
      <section class="panel">
        <header><h2>诊断 Top 债边</h2><p>BASELINE impact</p></header>
        <div id="debts">-</div>
      </section>
    </div>

    <section class="panel" style="margin-top:16px">
      <header>
        <h2>14 天修复计划</h2>
        <p id="planMeta">点击「生成/刷新 14 天计划」；任一天 ≤35 分钟，K1–K5 约束检查</p>
      </header>
      <div id="planSummary" class="status-line">尚未生成计划</div>
      <div id="planGantt" style="margin:12px 0"></div>
      <div id="planDays"></div>
    </section>

    <section class="panel" style="margin-top:16px" id="teacherCard">
      <header>
        <h2>教师端 · 班级热点</h2>
        <p>consent fail-closed：仅已授权学生进入热点；撤销后立即 purge</p>
      </header>
      <div class="page-header__actions" style="margin-bottom:12px">
        <button type="button" class="button" id="btnGrantA">授权 Student A</button>
        <button type="button" class="button" id="btnGrantB">授权 Student B</button>
        <button type="button" class="button danger" id="btnRevokeA">撤销 A</button>
        <button type="button" class="button danger" id="btnRevokeB">撤销 B</button>
      </div>
      <div class="workspace-grid">
        <div>
          <h3 style="margin:0 0 8px;font-size:16px">学生授权状态</h3>
          <div id="teacherStudents">-</div>
        </div>
        <div>
          <h3 style="margin:0 0 8px;font-size:16px">班级热点</h3>
          <div id="hotspots">-</div>
        </div>
      </div>
      <div id="teacherBrief" style="margin-top:12px"></div>
    </section>
  </main>'''

if old_debt in c:
    c = c.replace(old_debt, new_debt)
    print("debt_html_ok")
else:
    print("debt_html_missing")

old_plan = '''async function createPlan() {
  try {
    const plan = await j("/v1/students/" + studentId() + "/plans", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ graphVersion: 1, dayBudgetMin: 35, horizonDays: 14 })
    });
    let html = '<h2 style="font-size:17px;margin:0 0 8px">14 天计划</h2>' +
      '<div class="status-line ok">constraintsChecked=' + plan.constraintsChecked + " · budget=" + plan.dayBudgetMin + "</div>";
    (plan.days || []).forEach(function (d) {
      const pct = Math.min(100, (d.minutes / plan.dayBudgetMin) * 100);
      html += '<div class="gantt-day"><div style="width:40px;color:var(--muted);font-size:12px">D' + d.day +
        '</div><div class="gantt-wrap"><div class="gantt-bar" style="width:' + pct + '%"></div></div>' +
        '<div style="width:64px;color:var(--muted);font-size:12px">' + d.minutes + " min</div></div>";
    });
    $("planBox").innerHTML = html;
  } catch (e) {
    $("planBox").innerHTML = '<div class="status-line error">' + escapeHtml(e.message) + "</div>";
  }
}'''

new_plan = r'''let currentPlan = null;

function renderPlan(plan) {
  currentPlan = plan;
  const budget = plan.dayBudgetMin || 35;
  const days = plan.days || [];
  const totalMin = days.reduce(function (s, d) { return s + (d.minutes || 0); }, 0);
  const totalItems = days.reduce(function (s, d) { return s + ((d.items || []).length); }, 0);
  if ($("planDayCount")) $("planDayCount").textContent = days.length;
  if ($("planMeta")) {
    $("planMeta").textContent = "计划ID " + String(plan.id || "").slice(0, 12) + "… · budget=" + budget +
      " · planner=" + (plan.plannerVersion || "planner-v1");
  }
  if ($("planSummary")) {
    $("planSummary").innerHTML = plan.constraintsChecked
      ? '<span class="status-line ok" style="margin:0;display:block">K1–K5 约束检查通过 · ' + days.length +
        " 天 · 合计 " + totalMin + " 分钟 · 任务 " + totalItems + " 项 · 任一天 ≤" + budget + " 分钟</span>"
      : '<span class="status-line error" style="margin:0;display:block">约束未通过：' +
        escapeHtml(JSON.stringify(plan.constraintViolations || [])) + "</span>";
  }

  // gantt
  let gantt = "";
  days.forEach(function (d) {
    const pct = Math.min(100, ((d.minutes || 0) / budget) * 100);
    const ok = (d.minutes || 0) <= budget;
    gantt += '<div class="gantt-day"><div style="width:42px;color:var(--muted);font-size:12px">D' + d.day +
      '</div><div class="gantt-wrap"><div class="gantt-bar" style="width:' + pct + '%;background:' +
      (ok ? "linear-gradient(90deg,var(--primary),#6aa8f0)" : "var(--danger)") + '"></div></div>' +
      '<div style="width:72px;color:var(--muted);font-size:12px">' + (d.minutes || 0) + " / " + budget + " min</div></div>";
  });
  if ($("planGantt")) $("planGantt").innerHTML = gantt || '<div class="inspector-empty">无甘特数据</div>';

  // day cards
  let html = "";
  if (!days.length) {
    html = '<div class="inspector-empty">计划为空，请点击生成</div>';
  } else {
    days.forEach(function (d) {
      html += '<div class="task-card"><div style="display:flex;justify-content:space-between;align-items:center">' +
        "<strong>第 " + d.day + " 天</strong>" +
        '<span class="badge ' + ((d.minutes || 0) <= budget ? "ready" : "failed") + '">' +
        (d.minutes || 0) + " min · " + ((d.items || []).length) + " 项</span></div>";
      const items = d.items || [];
      if (!items.length) {
        html += '<div class="why">本日无安排（缓冲日）</div>';
      } else {
        items.forEach(function (it) {
          html += '<div style="margin-top:8px;padding:8px;border:1px solid var(--rule);border-radius:12px">' +
            "<div><span class=\"badge\">" + escapeHtml(it.type) + " · d" + it.difficulty + " · " + it.estMin + "min</span> " +
            "<strong style=\"font-size:13px\">" + escapeHtml(it.kpName || it.kpId) + "</strong></div>" +
            '<div class="why">' + escapeHtml(it.why) + "</div></div>";
        });
      }
      html += "</div>";
    });
  }
  if ($("planDays")) $("planDays").innerHTML = html;
}

async function createPlan() {
  const box = $("planDays");
  if (box) box.innerHTML = '<div class="inspector-empty">正在生成 14 天计划…</div>';
  if ($("planSummary")) {
    $("planSummary").textContent = "生成中…";
    $("planSummary").className = "status-line";
  }
  try {
    const plan = await j("/v1/students/" + studentId() + "/plans", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ graphVersion: 1, dayBudgetMin: 35, horizonDays: 14 })
    });
    renderPlan(plan);
  } catch (e) {
    if ($("planSummary")) {
      $("planSummary").textContent = "计划生成失败：" + e.message;
      $("planSummary").className = "status-line error";
    }
    if (box) box.innerHTML = '<div class="status-line error">' + escapeHtml(e.message) + "</div>";
  }
}

async function planToToday() {
  if (!currentPlan) {
    await createPlan();
    if (!currentPlan) return;
  }
  const day = (currentPlan.days || [])[0];
  const items = (day && day.items) || [];
  const tasks = items.map(function (it) {
    return {
      kpId: it.kpId,
      kpName: it.kpName,
      type: it.type,
      difficulty: it.difficulty,
      estMin: it.estMin,
      why: it.why,
      questionId: "q-plan-" + String(it.id || Math.random()).slice(0, 10),
      stem: "完成：" + (it.kpName || it.kpId) + " — " + (it.why || ""),
      options: ["开始本任务", "稍后处理", "需要辅导", "跳过今日"],
      correctIndex: 0,
      planItemId: it.id
    };
  });
  window._materialToday = {
    materialName: "14天修复计划 · Day1",
    brief: { coachMessage: "今日来自 14 天修复计划第 1 天，任一天预算 " + (currentPlan.dayBudgetMin || 35) + " 分钟。" },
    tasks: tasks
  };
  go("today");
  renderTasks(window._materialToday);
}

async function loadTeacherDashboard() {
  const studentsBox = $("teacherStudents");
  const hotBox = $("hotspots");
  const briefBox = $("teacherBrief");
  try {
    const ids = ["demo-student-a", "demo-student-b"];
    const rows = [];
    let auth = 0;
    for (let i = 0; i < ids.length; i++) {
      const sid = ids[i];
      let state = "none", allow = false;
      try {
        const cons = await j("/v1/consents/" + sid);
        const list = cons || [];
        const hit = list.filter(function (x) { return x.purpose === "teacher_hotspots" && x.teacherId === "demo-teacher"; })[0];
        if (hit) { state = hit.state; allow = !!hit.allowTeacher; }
      } catch (e) { state = "none"; }
      if (state === "granted" && allow) auth++;
      const label = sid.indexOf("-b") >= 0 ? "李华（对照）" : "王小明（有债）";
      rows.push("<tr><td>" + escapeHtml(label) + "</td><td>" + escapeHtml(sid) + "</td><td>" +
        '<span class="badge ' + (allow ? "ready" : "") + '">' + escapeHtml(state) + "</span></td><td>" +
        (allow ? "可见" : "不可见（fail-closed）") + "</td></tr>");
    }
    if ($("teacherAuthCount")) $("teacherAuthCount").textContent = auth;
    if (studentsBox) {
      studentsBox.innerHTML = '<table class="debt-table"><tr><th>学生</th><th>ID</th><th>consent</th><th>教师可见性</th></tr>' +
        rows.join("") + "</table>" +
        '<div class="status-line">已授权 ' + auth + " / " + ids.length + " 人。无授权时热点列表为空。</div>";
    }

    const data = await j("/v1/teachers/demo-teacher/hotspots?only_consent=true");
    const list = data.hotspots || [];
    if (!list.length) {
      if (hotBox) {
        hotBox.innerHTML = '<div class="status-line">' + escapeHtml(data.emptyReason || "无有效授权或无开放债边") +
          "</div>";
      }
      if (briefBox) briefBox.innerHTML = '<div class="status-line">教师简报：当前无可见数据（默认不可见）。</div>';
      return;
    }
    let html = '<div class="status-line ok">authorizedCount=' + data.authorizedCount + " · hotspots=" + list.length + "</div>";
    html += '<table class="debt-table"><tr><th>#</th><th>热点债边</th><th>人数</th><th>均 impact</th><th>建议</th></tr>';
    list.forEach(function (h, i) {
      html += "<tr><td>" + (i + 1) + "</td><td class=\"edge-red\">" + escapeHtml(h.fromKpName) + " → " +
        escapeHtml(h.toKpName) + "</td><td>" + h.studentCount + '</td><td class="edge-red">' + fmt(h.avgImpact) +
        "</td><td>课堂补充：" + escapeHtml(h.fromKpName) + "→" + escapeHtml(h.toKpName) + " 桥接练习</td></tr>";
    });
    html += "</table>";
    if (hotBox) hotBox.innerHTML = html;
    if (briefBox) {
      const top = list[0];
      briefBox.innerHTML = '<div class="status-line ok">教师简报：本班最集中债边为「' +
        escapeHtml(top.fromKpName) + " → " + escapeHtml(top.toKpName) + "」，涉及 " + top.studentCount +
        " 名已授权学生，均 impact " + fmt(top.avgImpact) + "。建议先在课上巩固先修概念，再布置桥接练习（非处分依据）。</div>";
    }
  } catch (e) {
    if (studentsBox) studentsBox.innerHTML = '<div class="status-line error">' + escapeHtml(e.message) + "</div>";
    if (hotBox) hotBox.innerHTML = '<div class="status-line error">' + escapeHtml(e.message) + "</div>";
  }
}

async function grantConsent(sid) {
  await j("/v1/consents/" + sid + "/grant", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ teacherId: "demo-teacher", purpose: "teacher_hotspots", actorId: sid })
  });
  await loadTeacherDashboard();
}

async function revokeConsent(sid) {
  await j("/v1/consents/" + sid + "/revoke", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ teacherId: "demo-teacher", purpose: "teacher_hotspots", actorId: sid })
  });
  await loadTeacherDashboard();
}'''

if old_plan in c:
    c = c.replace(old_plan, new_plan)
    print("plan_js_ok")
else:
    print("plan_js_missing")

# remove old grant/revoke if duplicated later - check for duplicate function names
# The old file may still have grantConsent/revokeConsent later - need to remove duplicates
# Find second occurrence
idx1 = c.find("async function grantConsent(sid)")
idx2 = c.find("async function grantConsent(sid)", idx1 + 10) if idx1 >= 0 else -1
if idx2 >= 0:
    # remove the later old versions - find loadHotspots old and grantConsent old
    old_teacher_js = '''function toggleTeacher() {
  const el = $("teacherCard");
  el.classList.toggle("hidden");
  if (!el.classList.contains("hidden")) loadHotspots();
}

async function loadHotspots() {
  try {
    const data = await j("/v1/teachers/demo-teacher/hotspots?only_consent=true");
    const list = data.hotspots || [];
    if (!list.length) {
      $("hotspots").innerHTML = '<div class="status-line">' + escapeHtml(data.emptyReason || "无热点") +
        " · authorized=" + data.authorizedCount + "</div>";
      return;
    }
    let rows = "";
    list.forEach(function (h) {
      rows += "<tr><td>" + escapeHtml(h.fromKpName) + " → " + escapeHtml(h.toKpName) +
        "</td><td>" + h.studentCount + '</td><td class="edge-red">' + fmt(h.avgImpact) + "</td></tr>";
    });
    $("hotspots").innerHTML = '<div style="color:var(--muted);font-size:12px">authorized=' + data.authorizedCount +
      '</div><table class="debt-table"><tr><th>热点</th><th>人数</th><th>均 impact</th></tr>' + rows + "</table>";
  } catch (e) {
    $("hotspots").innerHTML = '<div class="status-line error">' + escapeHtml(e.message) + "</div>";
  }
}

async function grantConsent(sid) {
  await j("/v1/consents/" + sid + "/grant", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ teacherId: "demo-teacher", purpose: "teacher_hotspots", actorId: sid })
  });
  loadHotspots();
}

async function revokeConsent(sid) {
  await j("/v1/consents/" + sid + "/revoke", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ teacherId: "demo-teacher", purpose: "teacher_hotspots", actorId: sid })
  });
  loadHotspots();
}'''
    if old_teacher_js in c:
        c = c.replace(old_teacher_js, '''function toggleTeacher() { loadTeacherDashboard(); }

async function loadHotspots() { await loadTeacherDashboard(); }''')
        print("dup_teacher_removed")

# bind new buttons
old_bind = '''$("btnCreatePlan").addEventListener("click", createPlan);
$("btnTeacher").addEventListener("click", toggleTeacher);'''
new_bind = '''$("btnCreatePlan").addEventListener("click", createPlan);
$("btnPlanToToday").addEventListener("click", planToToday);
$("btnTeacher").addEventListener("click", loadTeacherDashboard);
$("btnGrantB").addEventListener("click", function () { grantConsent("demo-student-b"); });
$("btnRevokeB").addEventListener("click", function () { revokeConsent("demo-student-b"); });'''
if old_bind in c:
    c = c.replace(old_bind, new_bind)
    print("bind_ok")
else:
    print("bind_missing")
    # try partial
    if 'btnCreatePlan' in c:
        c = c.replace('$("btnCreatePlan").addEventListener("click", createPlan);',
                      '$("btnCreatePlan").addEventListener("click", createPlan);\n$("btnPlanToToday").addEventListener("click", planToToday);')

# go debt auto-load plan + teacher
old_go_debt = 'if (page === "debt") refreshDebt();'
new_go_debt = '''if (page === "debt") {
      refreshDebt();
      loadTeacherDashboard();
      // try load existing plan via create (server returns 201) or skip
      if (!currentPlan) {
        createPlan().catch(function () {});
      } else {
        renderPlan(currentPlan);
      }
    }'''
if old_go_debt in c:
    c = c.replace(old_go_debt, new_go_debt)
    print("go_debt_ok")

# graph switch: show material name immediately
old_status = 'if (subtitle) subtitle.textContent = "加载中…";'
new_status = '''if (subtitle) subtitle.textContent = "加载中… " + (graphId || "");
  {
    const opt = $("graphSelect") ? $("graphSelect").selectedOptions[0] : null;
    const label = opt ? opt.textContent : (graphId || "");
    if ($("graphSwitchStatus")) {
      $("graphSwitchStatus").textContent = "切换请求：" + label;
      $("graphSwitchStatus").className = "status-line";
    }
    if ($("activeBookBadge")) $("activeBookBadge").textContent = shortBookName(label.split(" · ")[0] || label);
  }'''
if old_status in c:
    c = c.replace(old_status, new_status)
    print("switch_status_ok")

# node short label: prefer course short name if not hash
old_small = '''const shortId = String(n.id || "").slice(-8);
    const kindLabel = n.description || "";
    nodeHtml += '<button type="button" class="dag-node ' + cls + '" data-id="' + escapeHtml(n.id) +
      '" style="left:' + p.x + "px;top:" + p.y + 'px"><span>' + escapeHtml(n.name) +
      "</span><small>" + escapeHtml(shortId) + " · " + escapeHtml(kindLabel) + "</small></button>";'''
new_small = '''const shortId = String(n.id || "");
    const course = String(n.course || "");
    const kindLabel = n.description || "";
    const sub = (course && course.length <= 20 && !/^[0-9a-f-]{16,}$/i.test(course))
      ? course
      : (shortId + " · " + kindLabel);
    nodeHtml += '<button type="button" class="dag-node ' + cls + '" data-id="' + escapeHtml(n.id) +
      '" style="left:' + p.x + "px;top:" + p.y + 'px"><span>' + escapeHtml(n.name) +
      "</span><small>" + escapeHtml(sub) + "</small></button>";'''
if old_small in c:
    c = c.replace(old_small, new_small)
    print("node_sub_ok")

path.write_text(c, encoding="utf-8")
print("written", path.stat().st_size)
# verify no duplicate grantConsent
print("grantConsent_count", c.count("async function grantConsent"))
print("has_renderPlan", "function renderPlan" in c)
print("has_loadTeacherDashboard", "function loadTeacherDashboard" in c)
print("scripts", c.count("<script"), c.count("</script>"))
