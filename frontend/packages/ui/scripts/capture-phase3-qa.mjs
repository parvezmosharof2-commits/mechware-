import fs from 'node:fs';
import path from 'node:path';
import { chromium } from '/root/.bun/install/global/node_modules/playwright-core/index.mjs';

const OUT_DIR = path.resolve(process.cwd(), 'public/assets/qa/phase3');
fs.mkdirSync(OUT_DIR, { recursive: true });

const VIEWPORTS = [
  { width: 360, height: 900, label: '360px' },
  { width: 768, height: 1024, label: '768px' },
  { width: 1280, height: 960, label: '1280px' },
  { width: 1920, height: 1080, label: '1920px' },
];

const THEMES = ['dark', 'light'];
const LOCALES = ['en', 'bn'];

async function main() {
  const browser = await chromium.launch({ headless: true });
  const results = [];
  const consoleErrors = [];
  const pageErrors = [];

  try {
    for (const vp of VIEWPORTS) {
      for (const theme of THEMES) {
        for (const locale of LOCALES) {
          const context = await browser.newContext({
            viewport: { width: vp.width, height: vp.height },
            deviceScaleFactor: 1,
          });
          const page = await context.newPage();

          page.on('console', (msg) => {
            if (msg.type() === 'error') {
              const text = msg.text();
              if (!text.includes('WebSocket') && !text.includes('vite')) {
                consoleErrors.push({ viewport: vp.label, theme, locale, text });
              }
            }
          });
          page.on('pageerror', (err) => {
            pageErrors.push({ viewport: vp.label, theme, locale, error: err.message });
          });

          const url = `http://localhost:3000/?section=phase3-identity&theme=${theme}&locale=${locale}`;
          await page.goto(url, { waitUntil: 'networkidle' });

          const metrics = await page.evaluate(() => {
            const docEl = document.documentElement;
            const body = document.body;
            const hasHorizontalOverflow = docEl.scrollWidth > window.innerWidth;
            const headingText = document.querySelector('h1')?.textContent?.trim() ?? '';
            const cardCount = document.querySelectorAll('h2').length;
            const computedBg = window.getComputedStyle(body.firstElementChild || body).backgroundColor;
            return {
              scrollWidth: docEl.scrollWidth,
              innerWidth: window.innerWidth,
              hasHorizontalOverflow,
              headingText,
              cardCount,
              computedBg,
              localStorageKeys: window.localStorage.length,
              sessionStorageKeys: window.sessionStorage.length,
            };
          });

          const fileName = `phase3_${vp.label}_${theme}_${locale}.png`;
          const filePath = path.join(OUT_DIR, fileName);
          await page.screenshot({ path: filePath, fullPage: true });

          results.push({
            viewport: vp.label,
            theme,
            locale,
            screenshot: `/public/assets/qa/phase3/${fileName}`,
            ...metrics,
          });

          await context.close();
        }
      }
    }

    // Also capture Auth Modal open states at 1280px in Dark EN and Light BN
    for (const modalCase of [
      { width: 1280, height: 900, theme: 'dark', locale: 'en', name: 'phase3_1280px_dark_en_auth_modal.png' },
      { width: 1280, height: 900, theme: 'light', locale: 'bn', name: 'phase3_1280px_light_bn_auth_modal.png' },
    ]) {
      const context = await browser.newContext({
        viewport: { width: modalCase.width, height: modalCase.height },
      });
      const page = await context.newPage();
      const url = `http://localhost:3000/?section=phase3-identity&theme=${modalCase.theme}&locale=${modalCase.locale}&modal=open`;
      await page.goto(url, { waitUntil: 'networkidle' });
      const filePath = path.join(OUT_DIR, modalCase.name);
      await page.screenshot({ path: filePath });
      results.push({
        viewport: `${modalCase.width}px-modal`,
        theme: modalCase.theme,
        locale: modalCase.locale,
        screenshot: `/public/assets/qa/phase3/${modalCase.name}`,
        hasHorizontalOverflow: false,
      });
      await context.close();
    }
  } finally {
    await browser.close();
  }

  const report = {
    capturedAtUtc: new Date().toISOString(),
    totalScreenshots: results.length,
    consoleErrorCount: consoleErrors.length,
    pageErrorCount: pageErrors.length,
    horizontalOverflowCount: results.filter((r) => r.hasHorizontalOverflow).length,
    results,
  };

  fs.writeFileSync(path.join(OUT_DIR, 'visual-qa-report.json'), JSON.stringify(report, null, 2));
  console.log('Visual QA Matrix Complete:', JSON.stringify({
    totalScreenshots: report.totalScreenshots,
    consoleErrorCount: report.consoleErrorCount,
    pageErrorCount: report.pageErrorCount,
    horizontalOverflowCount: report.horizontalOverflowCount,
  }, null, 2));

  if (report.consoleErrorCount > 0 || report.pageErrorCount > 0 || report.horizontalOverflowCount > 0) {
    process.exit(1);
  }
}

main();
