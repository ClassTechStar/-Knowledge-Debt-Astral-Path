// 知债·星穹学途 —— 无服务版（单体 HTML）前端端到端走查
// 运行：node walkthrough.js
const PW = 'C:/Users/18948/.workbuddy/binaries/node/versions/22.22.2-3/node_modules/@playwright/cli/node_modules/playwright-core';
const { chromium } = require(PW);
const fs = require('fs');

const URL = 'http://127.0.0.1:8899/index.html';
const OUT = 'C:/Users/18948/AppData/Local/Temp/front_walk.json';
const steps = [];
const consoleErrors = [];

function log(id, desc, ok, detail) {
  steps.push({ id, desc, ok, detail: String(detail).slice(0, 400) });
  console.log(`  [${ok ? 'OK  ' : '!!  '}] ${id} ${desc}${detail ? ' | ' + String(detail).slice(0, 200) : ''}`);
}
const sec = (s) => console.log(`\n=== ${s} ===`);

(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await ctx.newPage();
  page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(m.text()); });
  page.on('pageerror', (e) => consoleErrors.push('PAGEERROR: ' + e.message));

  sec('F1. 首屏加载');
  const t0 = Date.now();
  await page.goto(URL, { waitUntil: 'load', timeout: 60000 });
  const loadMs = Date.now() - t0;
  const title = await page.title();
  const mainCount = await page.locator('main').count();
  log('F1-1', '页面加载', loadMs < 5000, `${loadMs}ms title=「${title}」main容器=${mainCount}`);

  const navCount = await page.locator('#nav button').count();
  log('F1-2', '顶栏导航项数', navCount === 8, `${navCount} 个（期望 8）`);

  const fnCheck = await page.evaluate(() => {
    const need = ['go', 'renderHome', 'renderMats', 'renderGraph', 'renderDebt', 'renderToday',
      'renderProfile', 'renderAccount', 'scanDebts', 'scoreV2', 'impactV2', 'saleStep',
      'buildPlan', 'intentRoute', 'executeIntent', 'sampleBook', 'ensureMastery'];
    return need.filter((n) => typeof window[n] !== 'function');
  });
  log('F1-3', '核心 JS 函数存在性', fnCheck.length === 0, fnCheck.length ? `缺失: ${fnCheck.join(',')}` : '全部就绪');

  const lsKey = await page.evaluate(() => {
    const k = 'astralpath_monolith_v2';
    return { has: !!localStorage.getItem(k), size: (localStorage.getItem(k) || '').length };
  });
  log('F1-4', 'localStorage 状态键', lsKey.has, `键大小 ${lsKey.size} 字节`);

  sec('F2. 八个页面切换');
  const pages = [['home', '起点'], ['materials', '藏书阁'], ['graph', '识网'], ['debt', '知债'],
                 ['today', '今日'], ['agent', '智能体'], ['profile', '画像'], ['account', '账户']];
  for (const [p, label] of pages) {
    const before = consoleErrors.length;
    await page.click(`#nav button[data-p="${p}"]`, { timeout: 10000 }).catch(() => {});
    await page.waitForTimeout(180);
    const vis = await page.evaluate((pp) => {
      const el = document.getElementById('p-' + pp);
      const hidden = document.querySelectorAll('main[hidden]').length;
      return { visible: el && !el.hidden, hiddenCount: hidden, textLen: el ? el.innerText.length : 0 };
    }, p);
    log('F2-' + p, `切换到「${label}」`, vis.visible && vis.hiddenCount === 7,
      `可见=${vis.visible} 隐藏页数=${vis.hiddenCount}(期望7) 文本长度=${vis.textLen}` +
      (consoleErrors.length > before ? ` 新增控制台错误: ${consoleErrors.slice(before).join(';')}` : ''));
  }

  sec('F3. 核心流程：知债 → 诊断 → 计划 → 销账');
  await page.click('#nav button[data-p="debt"]');
  await page.waitForTimeout(150);
  await page.click('#btnDiag');
  await page.waitForTimeout(400);
  const debtHtml = await page.locator('#debtList').innerText();
  const debtRows = (debtHtml.match(/impact=/g) || []).length;
  log('F3-1', '重新诊断产出红边', debtRows > 0, `红边 ${debtRows} 条 · 首行「${debtHtml.split('\n')[0].slice(0, 60)}」`);

  await page.click('#btnPlan');
  await page.waitForTimeout(400);
  const planTxt = await page.locator('#planBox').innerText();
  const planDays = (planTxt.match(/^D\d+/gm) || []).length;
  log('F3-2', '生成 14 天计划', planDays > 0, `计划条目 ${planDays} 条（文本长度 ${planTxt.length}）`);

  await page.click('#btnSale');
  await page.waitForTimeout(300);
  const saleTxt = await page.locator('#fb').innerText().catch(() => '');
  log('F3-3', '销账检查有反馈', saleTxt.trim().length > 0, saleTxt.slice(0, 120));

  sec('F4. 核心流程：今日任务 → 练习 → 提交');
  await page.click('#nav button[data-p="today"]');
  await page.waitForTimeout(150);
  await page.click('#btnLoadToday');
  await page.waitForTimeout(400);
  const todayTxt = await page.locator('#todayBar').innerText().catch(() => '');
  await page.click('#btnSub').catch(() => {});
  await page.waitForTimeout(400);
  const fbTxt = await page.locator('#fb').innerText().catch(() => '');
  log('F4-1', '加载今日任务并提交作答', fbTxt.trim().length > 0 || todayTxt.includes('D'),
    `todayBar=「${todayTxt.slice(0, 60)}」 提交反馈=「${fbTxt.slice(0, 90)}」`);

  sec('F5. 核心流程：智能体（危机转介 + 常规意图）');
  await page.click('#nav button[data-p="agent"]');
  await page.waitForTimeout(250);
  await page.fill('#agentIn', '我不想活了');
  await page.click('#btnSend');
  await page.waitForTimeout(500);
  const chatCrisis = await page.locator('#chat').innerText();
  const crisisOk = chatCrisis.includes('热线') || chatCrisis.includes('联系') || chatCrisis.includes('孤单');
  log('F5-1', '危机词 → 转介话术', crisisOk, chatCrisis.slice(-140));

  await page.fill('#agentIn', '帮我诊断知识债');
  await page.click('#btnSend');
  await page.waitForTimeout(500);
  const chat2 = await page.locator('#chat').innerText();
  log('F5-2', '常规意图路由', chat2.length > chatCrisis.length, chat2.slice(-140));

  await page.fill('#agentIn', '好的，帮我生成计划');
  await page.click('#btnSend');
  await page.waitForTimeout(500);
  const chat3 = await page.locator('#chat').innerText();
  const negOk = !chat3.slice(chatCrisis.length).includes('我可以帮你：知识债诊断、14 天计划、今日任务、图谱查看');
  log('F5-3', '「好的，帮我生成计划」不被误判为闲聊', negOk, chat3.slice(-120));

  sec('F6. 画像与隐私');
  await page.click('#nav button[data-p="profile"]');
  await page.waitForTimeout(250);
  const profBefore = await page.locator('#p-profile').innerText();
  await page.click('#btnOpt');
  await page.waitForTimeout(400);
  const optTxt = await page.locator('#p-profile').innerText();
  log('F6-1', '一键 opt-out 个性化画像', optTxt !== profBefore, optTxt.slice(0, 160));

  sec('F7. 账户与授权');
  await page.click('#nav button[data-p="account"]');
  await page.waitForTimeout(250);
  await page.click('#btnGrant');
  await page.waitForTimeout(300);
  const consent = await page.locator('#consentLine').innerText();
  await page.click('#btnRevoke');
  await page.waitForTimeout(300);
  const consent2 = await page.locator('#consentLine').innerText();
  log('F7-1', '同意/撤销授权状态切换', consent !== consent2, `grant后=「${consent}」 revoke后=「${consent2}」`);

  const dl = page.waitForEvent('download', { timeout: 8000 }).catch(() => null);
  await page.click('#btnExport');
  const got = await dl;
  log('F7-2', '导出数据触发下载', !!got, got ? `文件名 ${got.suggestedFilename()}` : '8 秒内未触发下载事件');

  sec('F8. 边界与异常');
  // 8-1 空粘贴解析
  const before8 = consoleErrors.length;
  await page.click('#nav button[data-p="materials"]');
  await page.waitForTimeout(200);
  await page.fill('#pasteText', '');
  await page.click('#btnPasteParse');
  await page.waitForTimeout(400);
  const st = await page.locator('#matStatus').innerText();
  log('F8-1', '空文本解析给出提示', st.trim().length > 0, `提示=「${st}」 新增错误=${consoleErrors.length - before8}`);

  // 8-2 超短文本
  await page.fill('#pasteText', '短');
  await page.click('#btnPasteParse');
  await page.waitForTimeout(400);
  const st2 = await page.locator('#matStatus').innerText();
  log('F8-2', '过短文本解析给出提示', st2.trim().length > 0, `提示=「${st2}」`);

  // 8-3 大批量文本解析（性能）
  const bigText = ('第1章 会计要素 ' + '资产 负债 所有者权益 收入 费用 利润。'.repeat(40) + '\n').repeat(400);
  const t1 = Date.now();
  await page.fill('#pasteText', bigText);
  await page.click('#btnPasteParse');
  await page.waitForTimeout(1500);
  const parseMs = Date.now() - t1;
  const st3 = await page.locator('#matStatus').innerText();
  log('F8-3', `大文本解析（${(bigText.length / 1024).toFixed(0)}KB）`, parseMs < 60000,
    `${parseMs}ms 提示=「${st3.slice(0, 80)}」 新增错误=${consoleErrors.length - before8}`);

  // 8-4 损坏的 localStorage
  const before8b = consoleErrors.length;
  await page.evaluate(() => localStorage.setItem('astralpath_monolith_v2', '{{{不是JSON'));
  await page.reload({ waitUntil: 'load' });
  await page.waitForTimeout(600);
  const recovered = await page.evaluate(() => ({
    title: document.title, mainVisible: !!document.querySelector('main:not([hidden])'),
    len: (localStorage.getItem('astralpath_monolith_v2') || '').length,
  }));
  log('F8-4', 'localStorage 损坏后自愈', recovered.mainVisible,
    `标题=「${recovered.title}」 首屏可见=${recovered.mainVisible} 新键长=${recovered.len} 新增错误=${consoleErrors.length - before8b}`);

  // 8-5 超大 localStorage
  const before8c = consoleErrors.length;
  const flood = await page.evaluate(() => {
    try {
      const big = 'x'.repeat(5 * 1024 * 1024);
      localStorage.setItem('__flood', big);
      return '写入成功';
    } catch (e) { return 'QuotaExceeded: ' + e.name; }
  });
  log('F8-5', 'localStorage 配额耗尽行为', true, flood);

  // 8-6 断网（离线可用性）
  await ctx.setOffline(true);
  const before8d = consoleErrors.length;
  await page.click('#nav button[data-p="home"]').catch(() => {});
  await page.waitForTimeout(300);
  await page.click('#btnDiag').catch(() => {});
  await page.waitForTimeout(300);
  await page.click('#nav button[data-p="debt"]').catch(() => {});
  await page.waitForTimeout(300);
  const offlineOk = await page.evaluate(() => {
    const el = document.getElementById('debtList');
    return !!el && el.innerText.length > 10;
  });
  await ctx.setOffline(false);
  log('F8-6', '断网后核心功能仍可用', offlineOk,
    `诊断页仍有内容=${offlineOk} 新增错误=${consoleErrors.length - before8d}`);

  sec('F9. 控制台与资源');
  const favicon = consoleErrors.filter((e) => e.includes('favicon'));
  const others = consoleErrors.filter((e) => !e.includes('favicon'));
  log('F9-1', '控制台错误总数', others.length === 0,
    `总 ${consoleErrors.length} 条，其中 favicon 404 ${favicon.length} 条，其它 ${others.length} 条` +
    (others.length ? ` → ${others.slice(0, 3).join(' | ')}` : ''));

  const perf = await page.evaluate(() => {
    const n = performance.getEntriesByType('navigation')[0] || {};
    return { domInteractive: Math.round(n.domInteractive || 0), loadEnd: Math.round(n.loadEventEnd || 0) };
  });
  log('F9-2', '页面性能指标', perf.loadEnd < 3000, `DOMInteractive=${perf.domInteractive}ms loadEventEnd=${perf.loadEnd}ms`);

  await browser.close();

  const bad = steps.filter((s) => !s.ok);
  console.log('\n' + '='.repeat(78));
  console.log(`前端走查：共 ${steps.length} 项，异常 ${bad.length} 项`);
  bad.forEach((b) => console.log(`  !! ${b.id} ${b.desc} | ${b.detail.slice(0, 200)}`));
  console.log('='.repeat(78));
  fs.writeFileSync(OUT, JSON.stringify({ steps, consoleErrors }, null, 2), 'utf-8');
  console.log('明细已写入 ' + OUT);
})().catch((e) => { console.error('FATAL', e); process.exit(1); });
