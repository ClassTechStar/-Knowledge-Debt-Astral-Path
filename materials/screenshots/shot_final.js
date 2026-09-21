const pwPath = "C:/Users/18948/AppData/Roaming/qianfan-desktop-app/lightsandbox/node/node_modules/@playwright/cli/node_modules/playwright";
const { chromium } = require(pwPath);
(async () => {
  const base = "http://127.0.0.1:5190";
  const outDir = process.argv[2];
  const browser = await chromium.launch({ headless: true, channel: "msedge" }).catch(() => chromium.launch({ headless: true }));
  const page = await browser.newPage({ viewport: { width: 1440, height: 920 } });
  await page.goto(base + "/", { waitUntil: "domcontentloaded", timeout: 30000 });
  await page.waitForTimeout(1500);
  // agent page
  const agentBtn = await page.$('button[data-page="agent"]');
  if (agentBtn) { await agentBtn.click(); await page.waitForTimeout(2500); await page.screenshot({ path: outDir + "/final-agent.png", fullPage: true }); }
  // profile
  const profBtn = await page.$('button[data-page="profile"]');
  if (profBtn) { await profBtn.click(); await page.waitForTimeout(2500); await page.screenshot({ path: outDir + "/final-profile.png", fullPage: true }); }
  // graph chapter
  const gBtn = await page.$('button[data-page="graph"]');
  if (gBtn) { await gBtn.click(); await page.waitForTimeout(4000); await page.screenshot({ path: outDir + "/final-graph.png", fullPage: true }); }
  // home
  const hBtn = await page.$('button[data-page="home"]');
  if (hBtn) { await hBtn.click(); await page.waitForTimeout(2000); await page.screenshot({ path: outDir + "/final-home.png", fullPage: true }); }
  await browser.close();
  console.log("ok");
})().catch(e => { console.error(e); process.exit(1); });
