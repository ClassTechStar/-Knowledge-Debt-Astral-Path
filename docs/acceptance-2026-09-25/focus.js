// 前端定点复测：今日流程 / 销账 alert / opt-out 生效 / 配额耗尽静默丢失
const PW = 'C:/Users/18948/.workbuddy/binaries/node/versions/22.22.2-3/node_modules/@playwright/cli/node_modules/playwright-core';
const { chromium } = require(PW);
const URL = 'http://127.0.0.1:8899/index.html';
const out = [];
const log = (id, d, ok, det) => { out.push({ id, d, ok, detail: String(det).slice(0, 300) });
  console.log(`  [${ok ? 'OK  ' : '!!  '}] ${id} ${d} | ${String(det).slice(0, 220)}`); };

(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await ctx.newPage();
  const dialogs = [];
  page.on('dialog', async (d) => { dialogs.push({ type: d.type(), msg: d.message() }); await d.dismiss(); });
  const errs = [];
  page.on('console', (m) => { if (m.type() === 'error') errs.push(m.text()); });
  page.on('pageerror', (e) => errs.push('PAGEERROR: ' + e.message));

  await page.goto(URL, { waitUntil: 'load' });
  await page.waitForTimeout(500);

  console.log('\n=== G1. 今日任务 → 练习 → 提交（正确容器） ===');
  await page.click('#nav button[data-p="today"]');
  await page.waitForTimeout(200);
  await page.click('#btnLoadToday');
  await page.waitForTimeout(600);
  const tlist = await page.locator('#todayList').innerText();
  const cards = await page.locator('#todayList .card').count();
  log('G1-1', '加载今日任务渲染任务卡', cards > 0, `卡片 ${cards} 张 · 文本「${tlist.slice(0, 90)}」`);

  const quizText = await page.locator('#quizBox').innerText();
  log('G1-2', '练习题目自动加载', quizText.trim().length > 5, `题目区「${quizText.slice(0, 90)}」`);

  const optCount = await page.locator('#optLine button, #quizBox button').count();
  const firstOpt = page.locator('#quizBox button').first();
  await firstOpt.click().catch(() => {});
  await page.waitForTimeout(200);
  await page.click('#btnSub');
  await page.waitForTimeout(500);
  const fb = await page.locator('#fb').innerText();
  const hasVerdict = /正确|错误|达标|未达标|再完成/.test(fb);
  log('G1-3', '选择选项后提交并给出判定', hasVerdict, `选项按钮 ${optCount} 个 · 反馈「${fb.slice(0, 140)}」`);

  const saleCount = await page.evaluate(() => {
    const s = JSON.parse(localStorage.getItem('astralpath_monolith_v2') || '{}');
    return Object.values(s.sale || {}).filter((x) => x && x.status).length;
  });
  log('G1-4', '作答后销账状态被记录', saleCount > 0, `sale 条目 ${saleCount} 个`);

  console.log('\n=== G2. 销账检查交互方式 ===');
  await page.click('#nav button[data-p="debt"]');
  await page.waitForTimeout(200);
  const dlgBefore = dialogs.length;
  await page.click('#btnSale');
  await page.waitForTimeout(600);
  const usedAlert = dialogs.length > dlgBefore;
  log('G2-1', '销账检查使用原生 alert 弹窗', false,
    usedAlert ? `确认为 alert()：「${dialogs[dialogs.length - 1].msg.slice(0, 90)}」——与整站面板式 UI 不一致，且会阻塞页面`
              : '未弹出 alert（已改为内联反馈）');

  console.log('\n=== G3. opt-out 是否真正生效 ===');
  await page.click('#nav button[data-p="profile"]');
  await page.waitForTimeout(250);
  const before = await page.evaluate(() => JSON.parse(localStorage.getItem('astralpath_monolith_v2') || '{}').optOut);
  await page.click('#btnOpt');
  await page.waitForTimeout(400);
  const after = await page.evaluate(() => JSON.parse(localStorage.getItem('astralpath_monolith_v2') || '{}').optOut);
  await page.click('#btnOpt');
  await page.waitForTimeout(400);
  const after2 = await page.evaluate(() => JSON.parse(localStorage.getItem('astralpath_monolith_v2') || '{}').optOut);
  log('G3-1', 'opt-out 切换并持久化', before !== after && after !== after2,
    `切换序列 ${before} → ${after} → ${after2}`);

  // 推荐是否真的受 opt-out 影响
  await page.click('#nav button[data-p="today"]');
  await page.waitForTimeout(200);
  const recWith = await page.locator('#todayList').innerText();
  await page.click('#nav button[data-p="profile"]');
  await page.click('#btnOpt'); // 打开 optOut
  await page.waitForTimeout(300);
  await page.click('#nav button[data-p="today"]');
  await page.waitForTimeout(200);
  const recWithout = await page.locator('#todayList').innerText();
  log('G3-2', 'opt-out 后个性化推荐应停用', recWith !== recWithout,
    recWith === recWithout ? '推荐内容完全一致 —— opt-out 只改了开关，未影响推荐与画像展示' : '推荐内容发生变化');

  console.log('\n=== G4. localStorage 配额耗尽 → 数据是否静默丢失 ===');
  const fill = await page.evaluate(() => {
    // 先把大块数据塞进去逼近配额
    try {
      let n = 0;
      for (; n < 60; n++) localStorage.setItem('__pad' + n, 'x'.repeat(100 * 1024));
      return '占位写入成功 ' + n + ' 块';
    } catch (e) { return '占位写入中断: ' + e.name; }
  });
  log('G4-1', '占用 localStorage 配额', true, fill);

  // 触发一次会调用 save() 的状态变更：同意授权
  await page.click('#nav button[data-p="account"]');
  await page.waitForTimeout(200);
  await page.click('#btnGrant');
  await page.waitForTimeout(600);
  const uiState = await page.locator('#consentLine').innerText();
  const stored = await page.evaluate(() => {
    try { return (JSON.parse(localStorage.getItem('astralpath_monolith_v2') || '{}').consent) + ''; }
    catch (e) { return 'PARSE_FAIL'; }
  });
  const saveFailed = uiState.includes('已同意') && stored !== 'true';
  log('G4-2', '配额不足时 save() 失败是否有用户可见提示', !saveFailed,
    saveFailed ? `界面显示「${uiState}」但 localStorage.consent=${stored} —— save() 返回 false 被忽略，界面无任何提示，用户会以为已保存`
               : `界面=「${uiState}」 存储 consent=${stored}（未复现静默丢失）`);

  console.log('\n=== G5. 其它交互一致性 ===');
  // 重置按钮是否二次确认
  const dlgB = dialogs.length;
  await page.click('#btnWipe');
  await page.waitForTimeout(500);
  const confirmUsed = dialogs.length > dlgB;
  const wiped = await page.evaluate(() => {
    const s = JSON.parse(localStorage.getItem('astralpath_monolith_v2') || '{}');
    return { students: (s.books || []).length, day: s.day, consent: s.consent };
  });
  log('G5-1', '「重置」是否有二次确认', confirmUsed,
    confirmUsed ? `已确认弹窗：「${dialogs[dialogs.length - 1].msg.slice(0, 80)}」`
                : `无任何确认即直接重置，数据已清空：books=${wiped.students} day=${wiped.day}`);

  await browser.close();
  const bad = out.filter((x) => !x.ok);
  console.log('\n' + '='.repeat(78));
  console.log(`定点复测：共 ${out.length} 项，异常 ${bad.length} 项`);
  bad.forEach((b) => console.log(`  !! ${b.id} ${b.d} | ${b.detail.slice(0, 200)}`));
  console.log(`控制台错误：${errs.length} 条 ${errs.slice(0, 3).join(' | ')}`);
  console.log('='.repeat(78));
  require('fs').writeFileSync('C:/Users/18948/AppData/Local/Temp/front_focus.json',
    JSON.stringify({ out, dialogs, errs }, null, 2), 'utf-8');
})().catch((e) => { console.error('FATAL', e); process.exit(1); });
