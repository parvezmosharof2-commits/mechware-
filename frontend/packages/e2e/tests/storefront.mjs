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
  en: { prefix: '', filters: /^(Filters|ফিল্টার)( \(.+\))?$/, partNumber: 'Part number', draft: 'Draft — this text is pending legal review and is not yet in force.', contact: 'Contact details coming soon.' },
  bn: { prefix: '/bn', filters: /^(Filters|ফিল্টার)( \(.+\))?$/, partNumber: 'পার্ট নম্বর', draft: 'খসড়া — এই লেখাটি আইনি পর্যালোচনার অপেক্ষায় আছে এবং এখনো কার্যকর নয়।', contact: 'যোগাযোগের তথ্য শীঘ্রই আসছে।' }
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

// H1.4 SEO tags must be in <head> for every user agent (Next streams metadata into <body> for browser UAs unless
// htmlLimitedBots matches them). Parsed from the raw HTML with a normal browser UA.
const BROWSER_UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36';
async function headChecks() {
  const pages = [
    { path: '/', canonical: true },
    { path: `/search?q=${QUERY}`, robots: /noindex/ },
    { path: `/products/${MULTI_SLUG}`, canonical: true },
    { path: '/help', canonical: true },
    { path: '/legal/terms', canonical: true, robots: /noindex/ }
  ];
  for (const pfx of ['', '/bn']) {
    for (const p of pages) {
      const url = `${WEB}${pfx}${p.path === '/' && pfx ? '' : p.path}`;
      const html = await (await fetch(url, { headers: { 'user-agent': BROWSER_UA } })).text();
      const end = html.indexOf('</head>');
      const head = html.slice(0, end);
      const body = html.slice(end);
      const tag = /<meta name="(?:description|robots)"|<link rel="(?:canonical|alternate)"/;
      const ok =
        end > 0 &&
        /<meta name="description" content="[^"]+"/.test(head) &&
        !tag.test(body) &&
        (!p.canonical || /<link rel="canonical" href="[^"]+"/.test(head)) &&
        (!p.robots || p.robots.test((head.match(/<meta name="robots" content="([^"]*)"/) ?? [])[1] ?? ''));
      check(`head tags (description${p.canonical ? ', canonical' : ''}${p.robots ? ', robots' : ''}) are in <head>: ${pfx}${p.path}`, ok);
    }
  }
}

(async () => {
  await headChecks();
  const browser = await launch();
  for (const [lname, loc] of Object.entries(LOCALES)) {
    for (const [vname, vp] of Object.entries(VIEWPORTS)) {
      const tag = `${lname} ${vp.width}px`;
      const ctx = await browser.newContext({ viewport: vp, locale: lname === 'bn' ? 'bn-BD' : 'en-US' });
      const page = await ctx.newPage();
      const errors = [];
      page.on('pageerror', (e) => errors.push(e.message));

      // H1.1 home: hero search (works as a plain GET form), real example chips, categories, header + footer
      await page.goto(`${WEB}${loc.prefix}/`, { waitUntil: 'networkidle' });
      check(`[${tag}] home h1, header, footer`, (await page.locator('h1').count()) === 1 && (await page.locator('header').count()) === 1 && (await page.locator('footer a[href*="/legal/terms"]').count()) === 1);
      check(`[${tag}] home example chips link to searches`, (await page.locator('main a[href*="/search?q="]').count()) > 0);
      check(`[${tag}] home categories and best sellers`, (await page.locator('#home-categories').count()) === 1 && (await page.locator('section[aria-labelledby="home-best"] a[href*="/products/"]').count()) > 0);
      check(`[${tag}] home no horizontal overflow`, await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth));
      await axeCheck(page, `${tag} /`);

      // H1.2 legal and help pages: every footer link reaches a 200 page, draft notice, axe 0
      const footerHrefs = await page.locator('footer a[href]').evaluateAll((as) => as.map((a) => a.getAttribute('href')).filter((h) => h && h.startsWith('/') && !h.endsWith('/search')));
      check(`[${tag}] footer has 8 legal/help links`, footerHrefs.length === 8, footerHrefs.join(' '));
      for (const href of footerHrefs) {
        const res = await page.request.get(WEB + href);
        check(`[${tag}] footer link ${href} is 200`, res.status() === 200, String(res.status()));
      }
      await page.goto(`${WEB}${loc.prefix}/legal/terms`, { waitUntil: 'networkidle' });
      const notice = (await page.locator('[data-testid="draft-notice"]').innerText()).trim();
      check(`[${tag}] legal draft notice visible`, notice === loc.draft, notice);
      check(`[${tag}] legal page noindex, toc and sections`, (await page.locator('meta[name="robots"][content*="noindex"]').count()) === 1 && (await page.locator('nav[aria-label] ol a').count()) >= 4 && (await page.locator('main section h2').count()) >= 4);
      check(`[${tag}] legal no horizontal overflow`, await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth));
      await axeCheck(page, `${tag} /legal/terms`);
      await page.goto(`${WEB}${loc.prefix}/help`, { waitUntil: 'networkidle' });
      check(`[${tag}] help FAQ groups and contact placeholder`, (await page.locator('main details').count()) >= 10 && (await page.locator('main h2').count()) === 6 && (await page.locator('main', { hasText: loc.contact }).count()) === 1);
      await page.locator('main details summary').first().click();
      check(`[${tag}] help details opens`, (await page.locator('main details[open]').count()) === 1);
      check(`[${tag}] help no horizontal overflow`, await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth));
      await axeCheck(page, `${tag} /help`);
      await page.goto(`${WEB}${loc.prefix}/`, { waitUntil: 'networkidle' });
      await page.locator('#hero-search').fill(QUERY);
      await page.locator('#hero-search').press('Enter');
      await page.waitForURL(new RegExp(`${loc.prefix}/search\\?q=${QUERY}`));
      check(`[${tag}] hero search goes to ${loc.prefix}/search`, page.url().includes(`/search?q=${QUERY}`));
      // language switch keeps the path
      await page.goto(`${WEB}${loc.prefix}/search?q=${QUERY}`, { waitUntil: 'networkidle' });
      await page.locator(lname === 'bn' ? 'header a[hreflang="en"]' : 'header a[hreflang="bn"]').click();
      await page.waitForURL(lname === 'bn' ? new RegExp(`${WEB}/search\\?q=${QUERY}`) : new RegExp(`${WEB}/bn/search\\?q=${QUERY}`));
      check(`[${tag}] language switch keeps path and query`, true);
      await ctx.clearCookies(); // next-intl remembers the chosen language in NEXT_LOCALE

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
