// C7.8 storefront quality gate: search -> brand filter -> product -> variant -> back, plus axe, en + bn, 360 and 1280 px.
// Needs the seeded catalogue (DB `mechware`, indexed in Meilisearch) behind a running API, and the web app built with
// NEXT_PUBLIC_API_BASE_URL pointing at it. Skipped (exit 0) unless E2E_STOREFRONT=1, because the CI e2e job has no
// Meilisearch and no catalogue seed. See docs/perf/C7-storefront-quality.md.
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { WEB, SHOTS, launch } from './lib.mjs';

if (process.env.E2E_STOREFRONT !== '1') {
  console.log('SKIP  storefront E2E (set E2E_STOREFRONT=1 with API + Meilisearch + seeded catalogue running)');
  process.exit(0);
}

const here = dirname(fileURLToPath(import.meta.url));
function findAxe() {
  const ui = resolve(here, '../../ui/node_modules/axe-core/axe.min.js');
  if (existsSync(ui)) return ui;
  const pnpm = resolve(here, '../../../node_modules/.pnpm');
  const d = readdirSync(pnpm).find((n) => n.startsWith('axe-core@'));
  if (d) return join(pnpm, d, 'node_modules/axe-core/axe.min.js');
  throw new Error('axe-core not found (it is a devDependency of @mechware/ui)');
}
const AXE = findAxe();

// Seed-dependent values; override when the catalogue changes.
const MULTI_SLUG = process.env.E2E_MULTI_VARIANT_SLUG ?? 'hager-mbn110-mcb-10-a-c-curve-6-ka-bbcde7';
const BRAND = process.env.E2E_BRAND_SLUG ?? 'yaskawa';
const QUERY = process.env.E2E_QUERY ?? 'inverter';

const results = [];
const check = (name, ok, extra = '') => {
  results.push(ok);
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${extra ? '  -> ' + extra : ''}`);
};

const LOCALES = {
  en: { prefix: '', filters: /^(Filters|ফিল্টার)( \(.+\))?$/, partNumber: 'Part number' },
  bn: { prefix: '/bn', filters: /^(Filters|ফিল্টার)( \(.+\))?$/, partNumber: 'পার্ট নম্বর' }
};
const VIEWPORTS = { m360: { width: 360, height: 740 }, d1280: { width: 1280, height: 800 } };

async function axeCheck(page, label) {
  await page.addScriptTag({ path: AXE });
  const r = await page.evaluate(async () => {
    const out = await window.axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice'] } });
    return out.violations.map((v) => ({ id: v.id, impact: v.impact, nodes: v.nodes.slice(0, 3).map((n) => n.target.join(' ')) }));
  });
  check(`axe 0 violations: ${label}`, r.length === 0, r.length ? JSON.stringify(r) : '');
}

async function openFilters(page, loc, vp) {
  if (vp.width < 768) {
    await page.getByRole('button', { name: loc.filters }).click();
    await page.getByRole('dialog').waitFor();
  }
}

(async () => {
  const browser = await launch();
  for (const [lname, loc] of Object.entries(LOCALES)) {
    for (const [vname, vp] of Object.entries(VIEWPORTS)) {
      const tag = `${lname} ${vp.width}px`;
      const ctx = await browser.newContext({ viewport: vp, locale: lname === 'bn' ? 'bn-BD' : 'en-US' });
      const page = await ctx.newPage();
      const errors = [];
      page.on('pageerror', (e) => errors.push(e.message));

      await page.goto(`${WEB}${loc.prefix}/search?q=${QUERY}`, { waitUntil: 'networkidle' });
      check(`[${tag}] html lang`, (await page.getAttribute('html', 'lang')) === lname);
      const rows = page.locator('a[href*="/products/"]:visible');
      check(`[${tag}] search shows results`, (await rows.count()) > 0);
      check(`[${tag}] no horizontal overflow`, await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth));
      if (lname === 'bn') {
        check(`[${tag}] Bangla heading and digits in page`, /খুঁজুন/.test(await page.locator('h1').innerText()) && /[০-৯]/.test(await page.locator('main').innerText()));
      }
      await axeCheck(page, `${tag} /search?q=${QUERY}`);

      // brand filter -> URL + results change
      const before = await rows.count();
      await openFilters(page, loc, vp);
      await page.locator(`[id$="-brand-${BRAND}"]:visible`).click();
      if (vp.width < 768) await page.getByRole('dialog').getByRole('button').last().click();
      await page.waitForURL(new RegExp(`brand=${BRAND}`));
      await page.waitForLoadState('networkidle');
      const after = await rows.count();
      check(`[${tag}] brand filter updates URL and results`, after > 0 && after <= before, `${before} -> ${after}`);
      const filteredUrl = page.url();

      // open a product, then back restores filters
      await rows.first().click();
      await page.waitForURL(/\/products\//);
      await page.getByRole('heading', { level: 1 }).waitFor();
      await page.goBack();
      await page.waitForURL(/\/search/);
      await page.waitForLoadState('networkidle');
      check(`[${tag}] back restores filtered URL`, page.url() === filteredUrl, page.url());
      await openFilters(page, loc, vp);
      const checked = await page.locator(`[id$="-brand-${BRAND}"]:visible`).getAttribute('aria-checked');
      check(`[${tag}] back restores brand checkbox`, checked === 'true');
      if (vp.width < 768) await page.keyboard.press('Escape');

      // empty state
      await page.goto(`${WEB}${loc.prefix}/search?q=zzzzqq`, { waitUntil: 'networkidle' });
      await axeCheck(page, `${tag} /search empty`);

      // PDP with variants
      await page.goto(`${WEB}${loc.prefix}/products/${MULTI_SLUG}`, { waitUntil: 'networkidle' });
      const pn = page.locator('dt', { hasText: loc.partNumber }).locator('xpath=following-sibling::dd[1]');
      const first = (await pn.innerText()).trim();
      const radios = page.locator('input[name="variant"]');
      check(`[${tag}] PDP has >1 variants`, (await radios.count()) > 1);
      await page.locator('label:has(input[name="variant"]:not(:checked))').first().click();
      await page.waitForURL(/[?&]v=/);
      const second = (await pn.innerText()).trim();
      check(`[${tag}] variant switch changes part number and ?v=`, second !== first, `${first} -> ${second}`);
      const price = (await page.locator('span.text-3xl').first().innerText()).trim();
      if (lname === 'bn') check(`[${tag}] Bangla digits in price`, /[০-৯]/.test(price) && !/[0-9]/.test(price), price);
      else check(`[${tag}] price shown`, /[0-9]/.test(price), price);
      check(`[${tag}] no horizontal overflow (PDP)`, await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth));
      await axeCheck(page, `${tag} /products/<slug>`);
      await page.screenshot({ path: `${SHOTS}/storefront-${lname}-${vname}.png` });
      check(`[${tag}] no page errors`, errors.length === 0, errors.join(' | '));
      await ctx.close();
    }
  }
  await browser.close();
  const failed = results.filter((r) => !r).length;
  console.log(`\n${results.length - failed}/${results.length} passed`);
  process.exit(failed ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(1); });
