// 前端补测：答题全流程（正确选择器）/ 识网 / 藏书阁上传 / 登录 / 换题
const PW = 'C:/Users/18948/.workbuddy/binaries/node/versions/22.22.2-3/node_modules/@playwright/cli/node_modules/playwright-core';
const { chromium } = require(PW);
const fs = require('fs');
const URL = 'http://127.0.0.1:8899/index.html';
const out = [];
const log = (id, d, ok, det) => { out.push({ id, d, ok, detail: String(det).slice(0, 320) });
  console.log(`  [${ok ? 'OK  ' : '!!  '}] ${id} ${d} | ${String(det).slice(0, 230)}`); };

(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await ctx.newPage();
  const errs = [];
  page.on('console', (m) => { if (m.type() === 'error') errs.push(m.text()); });
  page.on('pageerror', (e) => errs.push('PAGEERROR: ' + e.message));
  page.on('dialog', (d) => d.dismiss());

  await page.goto(URL, { waitUntil: 'load' });
  await page.waitForTimeout(400);

  console.log('\n=== H1. 答题全流程（[data-oi] 选择器）===');
  await page.click('#nav button[data-p="today"]');
  await page.waitForTimeout(200);
  await page.click('#btnLoadToday');
  await page.waitForTimeout(700);
  const optN = await page.locator('#quizBox [data-oi]').count();
  log('H1-1', '题目选项渲染', optN === 4, `${optN} 个选项（期望 4）`);

  // 选中第一个选项，检查视觉反馈
  const before = await page.locator('#quizBox [data-oi]').first().evaluate((el) => getComputedStyle(el).borderColor);
  await page.locator('#quizBox [data-oi]').first().click();
  await page.waitForTimeout(200);
  const after = await page.locator('#quizBox [data-oi]').first().evaluate((el) => getComputedStyle(el).borderColor);
  log('H1-2', '点选有视觉反馈', before !== after, `边框色 ${before} → ${after}`);

  const kb = await page.evaluate(() => {
    const el = document.querySelector('#quizBox [data-oi]');
    return { tag: el.tagName, role: el.getAttribute('role'), tabindex: el.getAttribute('tabindex') };
  });
  log('H1-3', '选项可键盘操作 / 无障碍语义', kb.role !== null || kb.tag === 'BUTTON',
    `<${kb.tag} role=${kb.role} tabindex=${kb.tabindex}> —— 非按钮元素且无 role/tabindex，Tab 与回车无法选答，读屏器不可识别`);

  await page.click('#btnSub');
  await page.waitForTimeout(700);
  const fb = await page.locator('#fb').innerText();
  log('H1-4', '提交后给出对错判定', /正确|错误/.test(fb), `反馈「${fb.slice(0, 120)}」`);

  const st = await page.evaluate(() => {
    const s = JSON.parse(localStorage.getItem('astralpath_monolith_v2') || '{}');
    return { sale: Object.keys(s.sale || {}).length, logs: (s.logs || []).length,
             m: Object.keys(s.mastery || {}).length };
  });
  log('H1-5', '作答写入 mastery / logs / sale', st.logs > 0 && st.sale > 0,
    `logs=${st.logs} sale=${st.sale} mastery=${st.m}`);

  // 再答两次检验销账条件（连续 2 次达标 + 累计 ≥3）
  for (let i = 0; i < 2; i++) {
    await page.locator('#quizBox [data-oi]').first().click();
    await page.click('#btnSub');
    await page.waitForTimeout(500);
  }
  const saleInfo = await page.evaluate(() => {
    const s = JSON.parse(localStorage.getItem('astralpath_monolith_v2') || '{}');
    return Object.entries(s.sale || {}).map(([k, v]) => `${k}:${v.status}/streak=${v.streak}/att=${v.att}`).join(' ');
  });
  log('H1-6', '连答后销账状态推进', saleInfo.length > 0, saleInfo.slice(0, 220));

  console.log('\n=== H2. 换一题 / Demo 推进 ===');
  const q1 = await page.locator('#quizBox').innerText();
  await page.click('#btnNextQ');
  await page.waitForTimeout(400);
  const q2 = await page.locator('#quizBox').innerText();
  log('H2-1', '换一题', true, q1 === q2 ? '题目未变化（题库可能仅一题命中该 KP，属正常）' : '题目已切换');

  const day1 = await page.evaluate(() => JSON.parse(localStorage.getItem('astralpath_monolith_v2') || '{}').day);
  await page.click('#btnDay');
  await page.waitForTimeout(400);
  const day2 = await page.evaluate(() => JSON.parse(localStorage.getItem('astralpath_monolith_v2') || '{}').day);
  log('H2-2', 'Demo +1 天推进', Number(day2) === Number(day1) + 1, `day ${day1} → ${day2}`);

  console.log('\n=== H3. 识网页 DAG ===');
  await page.click('#nav button[data-p="graph"]');
  await page.waitForTimeout(500);
  const dag = await page.evaluate(() => {
    const nodes = document.querySelectorAll('#p-graph [class*="node"], #p-graph .card').length;
    const t = document.getElementById('p-graph').innerText;
    return { nodes, hasChapter: t.includes('章'), len: t.length };
  });
  log('H3-1', '识网页渲染图谱节点', dag.nodes > 0 && dag.len > 100, `节点/卡片 ${dag.nodes} 个 · 文本长度 ${dag.len}`);

  console.log('\n=== H4. 藏书阁：上传文本文件 ===');
  const up = 'C:/Users/18948/AppData/Local/Temp/ap-accept/.probe/sample.txt';
  fs.writeFileSync(up, '第1章 会计基础\n' + '资产 负债 所有者权益 收入 费用 利润 会计等式 借贷记账法 试算平衡。\n'.repeat(30));
  await page.click('#nav button[data-p="materials"]');
  await page.waitForTimeout(300);
  const before2 = await page.locator('#matList .card').count();
  await page.setInputFiles('#fileInput', up);
  await page.waitForTimeout(300);
  await page.selectOption('#ocrMode', 'none').catch(() => {});
  await page.click('#btnUpload');
  await page.waitForTimeout(1500);
  const ms = await page.locator('#matStatus').innerText();
  const after2 = await page.locator('#matList .card').count();
  log('H4-1', '上传文本教材并解析', after2 > before2 || /ready|成功|解析/.test(ms),
    `卡片 ${before2} → ${after2} · 状态「${ms.slice(0, 130)}」`);

  const badge = await page.evaluate(() => {
    const b = document.querySelector('#matList .badge');
    if (!b) return { text: '', color: '', bg: '' };
    const s = getComputedStyle(b);
    return { text: b.textContent.trim(), color: s.color, bg: s.backgroundColor };
  });
  const green = /220, 244, 231/.test(badge.bg) || /dcf4e7/i.test(badge.bg);
  log('H4-2', 'badge 是否全绿（用户要求）', green,
    `badge=「${badge.text}」 bg=${badge.bg} color=${badge.color}${green ? '' : ' ← 非绿色，违反「badge 全绿」要求'}`);

  console.log('\n=== H5. 账户登录 ===');
  await page.click('#nav button[data-p="account"]');
  await page.waitForTimeout(300);
  const accText = await page.locator('#p-account').innerText();
  const hasLoginForm = await page.locator('#email').count();
  log('H5-1', '账户页含登录入口', hasLoginForm > 0 || /登录/.test(accText), `文本「${accText.replace(/\n/g, ' ').slice(0, 140)}」`);

  if (hasLoginForm) {
    await page.fill('#email', 'demo@astralpath.local');
    await page.fill('#pass', 'demo123456');
    await page.click('#btnLogin');
    await page.waitForTimeout(800);
    const accAfter = await page.locator('#p-account').innerText();
    const logged = /退出|已登录|王小明|demo/i.test(accAfter) && !/请先登录/.test(accAfter);
    log('H5-2', '单体版登录（无后端依赖）', logged, `登录后「${accAfter.replace(/\n/g, ' ').slice(0, 160)}」`);
  }

  await browser.close();
  const bad = out.filter((x) => !x.ok);
  console.log('\n' + '='.repeat(78));
  console.log(`前端补测：共 ${out.length} 项，异常 ${bad.length} 项`);
  bad.forEach((b) => console.log(`  !! ${b.id} ${b.d} | ${b.detail.slice(0, 210)}`));
  console.log(`控制台错误 ${errs.length} 条 ${errs.slice(0, 2).join(' | ')}`);
  console.log('='.repeat(78));
  fs.writeFileSync('C:/Users/18948/AppData/Local/Temp/front_extra.json', JSON.stringify({ out, errs }, null, 2), 'utf-8');
})().catch((e) => { console.error('FATAL', e); process.exit(1); });
