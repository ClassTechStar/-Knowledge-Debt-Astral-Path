const pwPath = "C:/Users/18948/AppData/Roaming/qianfan-desktop-app/lightsandbox/node/node_modules/@playwright/cli/node_modules/playwright";
const { chromium } = require(pwPath);
(async () => {
  const base = "http://127.0.0.1:5190";
  const outDir = process.argv[2];
  const browser = await chromium.launch({ headless: true, channel: "msedge" }).catch(() => chromium.launch({ headless: true }));
  const page = await browser.newPage({ viewport: { width: 1440, height: 950 } });
  await page.goto(base + "/", { waitUntil: "domcontentloaded", timeout: 30000 });
  await page.waitForTimeout(2000);
  await page.click('button[data-page="materials"]');
  await page.waitForTimeout(2500);
  await page.screenshot({ path: outDir + "/deep-01-materials.png", fullPage: true });
  await page.click('button[data-page="graph"]');
  await page.waitForTimeout(4500);
  await page.screenshot({ path: outDir + "/deep-02-graph-chapters.png", fullPage: true });
  // click first chapter in sidebar if any
  const chBtn = await page.$('#chapterTree [data-ch-id]');
  if (chBtn) {
    await chBtn.click();
    await page.waitForTimeout(2000);
    await page.screenshot({ path: outDir + "/deep-03-chapter-content.png", fullPage: true });
  }
  await page.click('button[data-page="today"]');
  await page.waitForTimeout(2000);
  await page.screenshot({ path: outDir + "/deep-04-today.png", fullPage: true });
  await browser.close();
  console.log("done");
})().catch(e => { console.error(e); process.exit(1); });
