const path = require("path");
const pwPath = "C:/Users/18948/AppData/Roaming/qianfan-desktop-app/lightsandbox/node/node_modules/@playwright/cli/node_modules/playwright";
const { chromium } = require(pwPath);
(async () => {
  const base = "http://127.0.0.1:5190";
  const outDir = process.argv[2];
  const browser = await chromium.launch({ headless: true, channel: "msedge" }).catch(async () => {
    return chromium.launch({ headless: true });
  });
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  await page.goto(base + "/", { waitUntil: "domcontentloaded", timeout: 30000 });
  await page.waitForTimeout(2000);
  await page.screenshot({ path: outDir + "/01-home.png", fullPage: true });
  await page.click('button[data-page="materials"]');
  await page.waitForTimeout(2500);
  await page.screenshot({ path: outDir + "/02-materials.png", fullPage: true });
  await page.click('button[data-page="graph"]');
  await page.waitForTimeout(3500);
  await page.screenshot({ path: outDir + "/03-graph.png", fullPage: true });
  await page.click('button[data-page="debt"]');
  await page.waitForTimeout(3500);
  await page.screenshot({ path: outDir + "/04-debt.png", fullPage: true });
  await page.click('button[data-page="today"]');
  await page.waitForTimeout(2000);
  // try load book questions
  const btns = await page.$$("button");
  for (const b of btns) {
    const t = (await b.textContent()) || "";
    if (t.includes("教材") || t.includes("真题") || t.includes("今日")) {
      try { await b.click(); await page.waitForTimeout(1500); break; } catch {}
    }
  }
  await page.screenshot({ path: outDir + "/05-today.png", fullPage: true });
  await browser.close();
  console.log("OK");
})().catch(e => { console.error("ERR", e.message); process.exit(1); });
