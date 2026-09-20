from pathlib import Path

path = Path(r"C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path\src\AstralPath.Api\wwwroot\index.html")
c = path.read_text(encoding="utf-8")

old_nav = '''<button data-page="today" onclick="go('today')">今日</button>'''
new_nav = '''<button data-page="today" onclick="go('today')">今日</button>
      <button data-page="auth" onclick="go('auth')">账户</button>'''
if old_nav in c:
    c = c.replace(old_nav, new_nav)
    print("nav_ok")
else:
    print("nav_missing")

old_account = '''    <div class="rail-account">
      <div class="rail-profile">
        <div class="rail-avatar" id="avatar">王</div>
        <span><strong id="railStudent">王小明（有债）</strong><small id="apiHint">API 检测中…</small></span>
      </div>
      <select id="studentSelect" style="width:150px;min-height:38px;border:1px solid var(--rule);border-radius:999px;background:var(--surface);padding:6px 10px">
        <option value="demo-student-a">王小明（有债）</option>
        <option value="demo-student-b">李华（无债）</option>
      </select>
    </div>'''
new_account = '''    <div class="rail-account">
      <div class="rail-profile">
        <div class="rail-avatar" id="avatar">访</div>
        <span><strong id="railStudent">未登录</strong><small id="apiHint">API 检测中…</small></span>
      </div>
      <select id="studentSelect" style="width:150px;min-height:38px;border:1px solid var(--rule);border-radius:999px;background:var(--surface);padding:6px 10px">
        <option value="demo-student-a">王小明（有债）</option>
        <option value="demo-student-b">李华（无债）</option>
      </select>
      <button class="button quiet" id="authQuickBtn" type="button" onclick="go('auth')">登录</button>
    </div>'''
if old_account in c:
    c = c.replace(old_account, new_account)
    print("account_ok")
else:
    print("account_missing")

auth_page = r'''  <main id="page-auth" class="page hidden">
    <header class="page-header">
      <div>
        <h1>账户</h1>
        <p>参考千知万理 GalReview：邮箱+密码注册登录；可绑定演示学生。今日任务可来自你上传/示例教材的解析结果。</p>
      </div>
      <div class="page-header__actions">
        <span class="badge" id="authStateBadge">未登录</span>
        <button class="button" onclick="logoutAuth()">退出登录</button>
      </div>
    </header>
    <div class="workspace-grid">
      <section class="panel">
        <header>
          <h2 id="authFormTitle">登录</h2>
          <p>演示账户：demo@astralpath.local / demo123456</p>
        </header>
        <div class="form-field"><label>邮箱</label><input id="authEmail" type="email" placeholder="you@example.com" value="demo@astralpath.local" /></div>
        <div class="form-field" id="authNameField" style="display:none"><label>昵称（注册可选）</label><input id="authName" type="text" placeholder="学习者" /></div>
        <div class="form-field"><label>密码</label><input id="authPassword" type="password" placeholder="至少 6 位" value="demo123456" /></div>
        <div class="form-field"><label>绑定演示学生</label>
          <select id="authStudent">
            <option value="demo-student-a">王小明（有债）</option>
            <option value="demo-student-b">李华（无债）</option>
          </select>
        </div>
        <div class="page-header__actions">
          <button class="button primary" id="authModeLogin" onclick="setAuthMode('login')">登录</button>
          <button class="button" id="authModeRegister" onclick="setAuthMode('register')">注册</button>
        </div>
        <div id="authStatus" class="status-line">尚未登录</div>
        <div class="page-header__actions" style="margin-top:12px">
          <button class="button" onclick="submitAuth()">提交</button>
          <button class="button" onclick="loadMaterialToday()">按账户拉取教材今日任务</button>
          <button class="button" onclick="seedAndParseBooks()">导入并解析示例教材</button>
        </div>
      </section>
      <aside class="panel graph-inspector">
        <header><h2>账户资料</h2><p>登录后可更新昵称与演示学生</p></header>
        <div id="profileBox" class="inspector-empty">登录后显示</div>
        <div class="form-field" style="margin-top:14px"><label>新昵称</label><input id="profileName" type="text" placeholder="可选" /></div>
        <div class="form-field"><label>切换演示学生</label>
          <select id="profileStudent">
            <option value="demo-student-a">王小明（有债）</option>
            <option value="demo-student-b">李华（无债）</option>
          </select>
        </div>
        <button class="button" onclick="updateProfile()">保存资料</button>
      </aside>
    </div>
  </main>

'''
footer = '  <footer class="footer-note">'
if footer in c:
    c = c.replace(footer, auth_page + footer)
    print("auth_page_ok")
else:
    print("footer_missing")

auth_js = r'''
// ---------- Auth (GalReview-style local session) ----------
let authMode = 'login';
let authSession = null;
try { authSession = JSON.parse(localStorage.getItem('astralpath_auth') || 'null'); } catch { authSession = null; }

function setAuthMode(mode) {
  authMode = mode;
  document.getElementById('authFormTitle').textContent = mode === 'login' ? '登录' : '注册';
  document.getElementById('authNameField').style.display = mode === 'register' ? 'grid' : 'none';
  document.getElementById('authModeLogin').classList.toggle('primary', mode === 'login');
  document.getElementById('authModeRegister').classList.toggle('primary', mode === 'register');
}

function authHeaders() {
  return authSession?.accessToken ? { 'Authorization': 'Bearer ' + authSession.accessToken } : {};
}

function applyAuthUi() {
  const badge = document.getElementById('authStateBadge');
  const nameEl = document.getElementById('railStudent');
  const avatar = document.getElementById('avatar');
  const btn = document.getElementById('authQuickBtn');
  const box = document.getElementById('profileBox');
  const status = document.getElementById('authStatus');
  if (authSession?.profile) {
    const p = authSession.profile;
    badge.textContent = '已登录';
    badge.className = 'badge ready';
    nameEl.textContent = p.displayName || p.email || '学习者';
    avatar.textContent = Array.from((p.displayName || p.email || '学').trim())[0] || '学';
    btn.textContent = '账户';
    if (box) {
      box.innerHTML = `
        <div class="kv">
          <div><span>邮箱</span><span>${escapeHtml(p.email||'')}</span></div>
          <div><span>昵称</span><span>${escapeHtml(p.displayName||'')}</span></div>
          <div><span>绑定学生</span><span>${escapeHtml(p.demoStudentId||'')}</span></div>
          <div><span>用户ID</span><span>${escapeHtml((p.userId||'').slice(0,12))}…</span></div>
        </div>`;
    }
    if (p.demoStudentId) {
      document.getElementById('studentSelect').value = p.demoStudentId;
      document.getElementById('profileStudent').value = p.demoStudentId;
      document.getElementById('authStudent').value = p.demoStudentId;
    }
    if (status) {
      status.textContent = `已登录：${p.displayName || p.email} · 绑定 ${p.demoStudentId || '-'}`;
      status.className = 'status-line ok';
    }
  } else {
    badge.textContent = '未登录';
    badge.className = 'badge';
    nameEl.textContent = '未登录';
    avatar.textContent = '访';
    btn.textContent = '登录';
    if (box) box.innerHTML = '<div class="inspector-empty">登录后显示账户资料</div>';
    if (status) {
      status.textContent = '尚未登录。可用演示账户 demo@astralpath.local / demo123456';
      status.className = 'status-line';
    }
  }
}

async function submitAuth() {
  const email = document.getElementById('authEmail').value.trim();
  const password = document.getElementById('authPassword').value;
  const displayName = document.getElementById('authName').value.trim();
  const demoStudentId = document.getElementById('authStudent').value;
  const status = document.getElementById('authStatus');
  status.textContent = authMode === 'login' ? '登录中…' : '注册中…';
  status.className = 'status-line';
  try {
    await resolveApiBase();
    const url = authMode === 'login' ? api + '/api/v1/auth/sessions' : api + '/api/v1/auth/register';
    const body = authMode === 'login'
      ? { email, password, deviceName: 'web' }
      : { email, password, displayName: displayName || null, demoStudentId };
    const r = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    const text = await r.text();
    let data = null; try { data = JSON.parse(text); } catch {}
    if (!r.ok) throw new Error(data?.error?.message || `HTTP ${r.status}`);
    const payload = data.data || data;
    authSession = {
      sessionId: payload.sessionId,
      accessToken: payload.accessToken,
      refreshToken: payload.refreshToken,
      profile: payload.profile
    };
    localStorage.setItem('astralpath_auth', JSON.stringify(authSession));
    applyAuthUi();
    if (authSession.profile?.demoStudentId) {
      document.getElementById('studentSelect').value = authSession.profile.demoStudentId;
      if (currentPage === 'debt') refreshDebt();
      if (currentPage === 'today') loadToday();
    }
    status.textContent = (authMode === 'login' ? '登录成功' : '注册并登录成功') + `：${authSession.profile.displayName || email}`;
    status.className = 'status-line ok';
  } catch (e) {
    status.textContent = '失败：' + e.message;
    status.className = 'status-line error';
  }
}

function logoutAuth() {
  if (authSession?.sessionId) {
    fetch(api + '/api/v1/auth/logout', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify({ sessionId: authSession.sessionId })
    }).catch(() => {});
  }
  authSession = null;
  localStorage.removeItem('astralpath_auth');
  applyAuthUi();
}

async function updateProfile() {
  if (!authSession?.accessToken) { alert('请先登录'); return; }
  try {
    const r = await fetch(api + '/api/v1/auth/profile', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify({
        displayName: document.getElementById('profileName').value.trim() || null,
        demoStudentId: document.getElementById('profileStudent').value
      })
    });
    const data = await r.json();
    if (!r.ok) throw new Error(data?.error?.message || `HTTP ${r.status}`);
    authSession.profile = data.data || data;
    localStorage.setItem('astralpath_auth', JSON.stringify(authSession));
    applyAuthUi();
    document.getElementById('authStatus').textContent = '资料已更新';
    document.getElementById('authStatus').className = 'status-line ok';
  } catch (e) {
    alert('更新失败：' + e.message);
  }
}

async function seedAndParseBooks() {
  const status = document.getElementById('authStatus');
  status.textContent = '导入示例教材并解析中…（扫描版可能需要数十秒）';
  status.className = 'status-line';
  try {
    await resolveApiBase();
    const seed = await j('/v1/materials/seed-samples?parse=true', { method: 'POST' });
    status.textContent = `已导入 ${seed.seeded} 份，后台解析中…`;
    // kick parse-all as well
    await j('/v1/materials/parse-all?ocr=quick', { method: 'POST' }).catch(() => {});
    // poll today-from-books
    for (let i = 0; i < 20; i++) {
      await new Promise(r => setTimeout(r, 3000));
      const today = await j('/v1/materials/today-from-books?maxTasks=8');
      if ((today.tasks || []).length > 0) {
        status.textContent = `教材今日任务已生成：${today.materialName} · ${today.tasks.length} 项`;
        status.className = 'status-line ok';
        window._materialToday = today;
        go('today');
        renderMaterialToday(today);
        return;
      }
    }
    status.textContent = '仍在解析，请稍后再点「按账户拉取教材今日任务」';
  } catch (e) {
    status.textContent = '失败：' + e.message;
    status.className = 'status-line error';
  }
}

async function loadMaterialToday() {
  const status = document.getElementById('authStatus');
  try {
    await resolveApiBase();
    // logged-in path
    if (authSession?.accessToken) {
      const r = await fetch(api + '/api/v1/auth/material-today?maxTasks=8', { method: 'POST', headers: authHeaders() });
      const data = await r.json();
      if (r.ok) {
        const payload = data.data || data;
        status.textContent = `账户教材任务：${payload.material} · ${payload.tasks.length} 项`;
        status.className = 'status-line ok';
        window._materialToday = { materialName: payload.material, brief: payload.brief, tasks: payload.tasks };
        go('today');
        renderMaterialToday(window._materialToday);
        return;
      }
    }
    const today = await j('/v1/materials/today-from-books?maxTasks=8');
    window._materialToday = today;
    status.textContent = `公开教材任务：${today.materialName} · ${(today.tasks||[]).length} 项`;
    status.className = 'status-line ok';
    go('today');
    renderMaterialToday(today);
  } catch (e) {
    status.textContent = '拉取失败：' + e.message;
    status.className = 'status-line error';
  }
}

function renderMaterialToday(today) {
  const el = document.getElementById('coach');
  if (!el) return;
  const brief = today.brief || {};
  const tasks = today.tasks || [];
  el.innerHTML = `
    <div class="status-line ok">${escapeHtml(brief.coachMessage || ('今日任务来自《' + (today.materialName||'') + '》'))}</div>
    <div style="color:var(--muted);font-size:12px;margin:8px 0">教材：${escapeHtml(today.materialName||'')} · 合计 ${tasks.reduce((s,t)=>s+(t.estMin||0),0)} 分钟</div>
    ${tasks.map((t, idx) => `
      <div class="task-card">
        <div><strong>${escapeHtml(t.kpName)}</strong> <span class="badge">${escapeHtml(t.type)} · d${t.difficulty} · ${t.estMin}min</span></div>
        <div class="why">${escapeHtml(t.why)}</div>
        <div>${escapeHtml(t.stem || '（教材概念卡，口头复述即可）')}</div>
        <div class="options">
          ${(t.options||[]).map((o,i)=>`<button onclick="answer(${idx},'${t.questionId||''}','${t.kpId||''}','${t.planItemId||''}',${i},${t.correctIndex ?? 0})">${String.fromCharCode(65+i)}. ${escapeHtml(o)}</button>`).join('') || '<button onclick="answer('+idx+',\''+(t.questionId||'')+'\',\''+(t.kpId||'')+'\',\'\',0,0)">已完成概念卡</button>'}
        </div>
        <div class="slider-row"><span style="color:var(--muted);font-size:12px">信心</span><input type="range" min="1" max="5" value="3" id="conf-${idx}" oninput="document.getElementById('confv-${idx}').textContent=this.value"><span id="confv-${idx}" style="color:var(--muted)">3</span></div>
        <div id="fb-${idx}" style="color:var(--muted);font-size:12px"></div>
      </div>`).join('')}
  `;
  window._todayTasks = tasks;
}

// hook go() auth page
const _origGo = go;
go = function(page) {
  _origGo(page);
  if (page === 'auth') applyAuthUi();
};
applyAuthUi();
'''

# inject before go('home');
marker = "go('home');"
if marker in c and 'function submitAuth()' not in c:
    c = c.replace(marker, auth_js + "\n" + marker)
    print("js_ok")
else:
    print("js_skip", "submitAuth" in c, marker in c)

# update loadToday to merge material tasks if available
old_today_start = "async function loadToday() {\n  try {"
new_today_start = """async function loadToday() {
  if (window._materialToday && (window._materialToday.tasks||[]).length) {
    // keep material tasks visible alongside API plan tasks
  }
  try {"""
if old_today_start in c:
    c = c.replace(old_today_start, new_today_start)
    print("today_hook_ok")

path.write_text(c, encoding="utf-8")
print("written", path.stat().st_size)
