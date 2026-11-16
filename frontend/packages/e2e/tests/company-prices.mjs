// D2.13: the storefront shows a signed-in company buyer their contract price (PDP + cart), EN + BN, and nobody else.
// Needs: the API (Development, SCRATCH DB with the demo catalogue and cost prices on the variants), the web app built against it, and a second
// staff account admin2.e2e@mechware.com.bd cloned from the admin row (same password, TOTP secret stored in clear, then `Mechware.Worker migrate`
// encrypts it). The API allows CORS only from localhost:3000/3001. Skipped (exit 0) unless E2E_COMPANY_PRICES=1.
//   E2E_COMPANY_PRICES=1 E2E_API_URL=http://localhost:6300 E2E_WEB_URL=http://localhost:3000 node tests/company-prices.mjs
// Real: price list created by admin2, approved by admin with a real step-up code, an own company, sign-ins, the PDP and cart in a browser,
// withdraw, an order placed and cancelled. Mocked: nothing.
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { WEB, API, USERS, apiCall, apiLogin, launch, reporter, totp, uniqueBin } from './lib.mjs';

if (process.env.E2E_COMPANY_PRICES !== '1') {
  console.log('SKIP  company prices E2E (set E2E_COMPANY_PRICES=1 with the API, a seeded scratch DB and the web app running)');
  process.exit(0);
}

const r = reporter();
const here = dirname(fileURLToPath(import.meta.url));
function findAxe() {
  const ui = resolve(here, '../../ui/node_modules/axe-core/axe.min.js');
  if (existsSync(ui)) return ui;
  const pnpm = resolve(here, '../../../node_modules/.pnpm');
  const d = readdirSync(pnpm).find((n) => n.startsWith('axe-core@'));
  if (d) return join(pnpm, d, 'node_modules/axe-core/axe.min.js');
  throw new Error('axe-core not found');
}
async function axeCheck(page, label) {
  await page.addScriptTag({ path: findAxe() });
  const v = await page.evaluate(async () => {
    const out = await window.axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice'] } });
    return out.violations.map((x) => ({ id: x.id, impact: x.impact, nodes: x.nodes.slice(0, 3).map((n) => n.target.join(' ')) }));
  });
  r.check(`axe 0 violations: ${label}`, v.length === 0, v.length ? JSON.stringify(v) : '');
}
const ALL = {
  en: JSON.parse(readFileSync(resolve(here, '../../../apps/web/src/messages/en.json'), 'utf8')),
  bn: JSON.parse(readFileSync(resolve(here, '../../../apps/web/src/messages/bn.json'), 'utf8'))
};
const sleep = (ms) => new Promise((res) => setTimeout(res, ms));
const tag = String(Date.now() % 100000);
const REF = `CT-E2E-${tag}`;
const SLUG_A = process.env.E2E_SLUG_A ?? 'fluke-17b-digital-multimeter-handheld-with-temperature-958548';
const SLUG_B = process.env.E2E_SLUG_B ?? 'airtac-gfr200-08-air-preparation-unit-1-4-filter-regulator-a69ee8';
const money = (minor, lang) => `৳${new Intl.NumberFormat(lang === 'bn' ? 'bn-BD' : 'en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(minor / 100)}`;
const CONTRACT_A1 = 1_200_000; // net, per unit
const CONTRACT_A10 = 1_000_000; // net, from 10 units
const CONTRACT_B1 = 150_000;

// ---- staff helpers (a TOTP code is single-use per account: wait for a fresh 30 s step)
const lastStep = new Map();
async function freshCode(person) {
  for (;;) {
    const step = Math.floor(Date.now() / 30000);
    if (step > (lastStep.get(person.email) ?? -1)) { lastStep.set(person.email, step); return totp(person.totpSecret); }
    await sleep(1000);
  }
}
async function staffToken(person) {
  for (let attempt = 0; attempt < 8; attempt++) {
    const res = await fetch(`${API}/api/v1/auth/login`, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ identifier: person.email, password: person.password, totpCode: await freshCode(person) })
    });
    const body = await res.json();
    if (res.ok && body.data?.accessToken) return body.data.accessToken;
    await sleep(5000);
  }
  throw new Error(`staff login failed for ${person.email}`);
}
async function stepUp(token, person, method, path, body) {
  const res = await fetch(`${API}${path}`, {
    method, headers: { 'content-type': 'application/json', authorization: `Bearer ${token}`, 'x-step-up-code': await freshCode(person) },
    body: body ? JSON.stringify(body) : undefined
  });
  return { status: res.status, json: await res.json().catch(() => ({})) };
}
const ADMIN = USERS.admin;
const ADMIN2 = { email: 'admin2.e2e@mechware.com.bd', password: ADMIN.password, totpSecret: ADMIN.totpSecret };

async function signIn(page, prefix, who, path) {
  const auth = ALL[prefix ? 'bn' : 'en'].Auth;
  await page.goto(`${WEB}${prefix}/auth/login?returnTo=${encodeURIComponent(prefix + path)}`, { waitUntil: 'networkidle' });
  await page.getByLabel(auth.emailOrPhoneLabel).fill(who.email);
  await page.locator('#auth-pw').fill(who.password);
  await page.getByRole('button', { name: auth.signInButton }).click();
  await page.waitForURL(new RegExp(`${prefix}${path.replace(/[/?.]/g, '\\$&')}`), { timeout: 60000 });
}
const overflow = (p) => p.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);

(async () => {
  // ---- setup through the API
  const buyer = await apiLogin(USERS.buyer);
  const bcall = (m, p, b) => apiCall(buyer, m, p, b);
  const admin = await staffToken(ADMIN);
  const admin2 = await staffToken(ADMIN2);

  const variantOf = async (slug) => (await (await fetch(`${API}/api/v1/storefront/products/${slug}`)).json()).data.variants[0];
  const vA = await variantOf(SLUG_A);
  const vB = await variantOf(SLUG_B);

  const reg = await bcall('POST', '/api/v1/companies', { legalName: `Contract Mills ${tag} Ltd`, tradeLicenceNo: 'TL-D213', bin: uniqueBin() });
  const companyId = reg.json.data.id;
  await apiCall(admin, 'POST', `/api/v1/admin/companies/${companyId}/activate`);
  const me = (await bcall('GET', '/api/v1/users/me')).json.data.id;
  const own = (await bcall('GET', `/api/v1/companies/${companyId}/members`)).json.data.find((x) => x.userId === me);
  await bcall('PUT', `/api/v1/companies/${companyId}/members/${own.id}`, { roles: 10, spendLimitMinor: null, costCentreIds: [], isActive: true });
  const site = await bcall('POST', `/api/v1/companies/${companyId}/sites`, { name: 'Gazipur Plant', contactName: 'Sabbir', contactPhone: '01711000000', line1: 'Plot 5, BSCIC Area', upazila: 'Tongi Industrial Zone', district: 'Gazipur', division: 'Dhaka', isDefaultDelivery: true });
  r.check('setup: an active company with a Buyer', reg.status === 201 && site.status === 201, `${reg.status}/${site.status}`);

  const created = await apiCall(admin2, 'POST', '/api/v1/admin/price-lists', {
    scope: 'Company', companyId, contractRef: REF, name: `E2E ${tag}`,
    validFromUtc: new Date(Date.now() - 3600_000).toISOString(), validToUtc: new Date(Date.now() + 30 * 86400_000).toISOString()
  });
  const listId = created.json.data?.id;
  const entries = await apiCall(admin2, 'PUT', `/api/v1/admin/price-lists/${listId}/entries`, {
    entries: [
      { variantId: vA.id, minQty: 1, unitPriceMinor: CONTRACT_A1 },
      { variantId: vA.id, minQty: 10, unitPriceMinor: CONTRACT_A10 },
      { variantId: vB.id, minQty: 1, unitPriceMinor: CONTRACT_B1 }
    ]
  });
  const submitted = await apiCall(admin2, 'POST', `/api/v1/admin/price-lists/${listId}/submit`);
  const hash = submitted.json.data?.contentHash;
  const approved = await stepUp(admin, ADMIN, 'POST', `/api/v1/admin/price-lists/${listId}/approve`, { expectedContentHash: hash, acknowledgedBelowFloorEntryIds: [] });
  r.check('setup: list created, submitted by one admin and approved by another with a real step-up code',
    created.status === 201 && entries.status === 200 && submitted.status === 200 && approved.status === 200, `${created.status}/${entries.status}/${submitted.status}/${approved.status} ${approved.status === 200 ? '' : JSON.stringify(approved.json).slice(0, 200)}`);
  const effective = Date.parse(approved.json.data?.effectiveFromUtc ?? '') || Date.now() + 61_000;
  while (Date.now() < effective + 1500) await sleep(1000);
  const direct = await bcall('GET', `/api/v1/companies/${companyId}/prices?items=${vA.id}:1`);
  r.check('the API prices the contract for the buyer', direct.json.data?.items?.[0]?.source === 'contract' && direct.json.data.items[0].unitPriceMinorNet === CONTRACT_A1, JSON.stringify(direct.json.data?.items?.[0] ?? direct.json).slice(0, 200));
  const vatRaw = direct.json.data?.items?.[0]?.vatRate;
  console.log(`INFO  vatRate on the wire: ${vatRaw}`);

  const browser = await launch();
  const newPage = async (width, ctxOpts = {}) => {
    const ctx = await browser.newContext({ viewport: { width, height: width < 600 ? 740 : 900 }, ...ctxOpts });
    const page = await ctx.newPage();
    page.pricesCalls = [];
    page.errors = [];
    page.on('request', (q) => q.url().includes('/prices?items=') && page.pricesCalls.push(q.url()));
    page.on('pageerror', (e) => page.errors.push(e.message));
    await page.addInitScript(() => {
      window.__cls = 0;
      new PerformanceObserver((l) => { for (const e of l.getEntries()) if (!e.hadRecentInput) window.__cls += e.value; }).observe({ type: 'layout-shift', buffered: true });
    });
    return page;
  };
  const pdp = (prefix, slug) => `${WEB}${prefix}/products/${slug}`;

  // ---- 1. the cached page HTML never holds a contract price (fetched without cookies, after the buyer's session used it below too)
  const rawBefore = await (await fetch(pdp('', SLUG_A))).text();
  const leaks = (html) => [REF, '12,000.00', '10,000.00', '1,500.00', 'company-price', String(CONTRACT_A1), `"${CONTRACT_A10}`].filter((x) => html.includes(x));
  r.check('ContractPrice_IsNotInTheCachedPageHtml (before a member visits)', leaks(rawBefore).length === 0, leaks(rawBefore).join(','));

  // ---- 2. a guest and a personal (non-company) signed-in user see only the public price
  const guest = await newPage(1280);
  await guest.goto(pdp('', SLUG_A), { waitUntil: 'networkidle', timeout: 120000 });
  await guest.waitForTimeout(1500);
  r.check('guest: public price shown, no company price, no /prices request', (await guest.getByText('16,150.00').count()) > 0 && (await guest.locator('[data-testid=company-price]').count()) === 0 && guest.pricesCalls.length === 0, `calls=${guest.pricesCalls.length}`);
  await guest.context().close();

  const personal = await newPage(1280);
  await signIn(personal, '', USERS.sellerAlpha, `/products/${SLUG_A}`);
  await personal.waitForLoadState('networkidle');
  await personal.waitForTimeout(2500);
  r.check('personal (no company) user: only the public price, no /prices request', (await personal.getByText('16,150.00').count()) > 0 && (await personal.locator('[data-testid=company-price]').count()) === 0 && personal.pricesCalls.length === 0, `calls=${personal.pricesCalls.length}`);
  await personal.context().close();

  // ---- 3. the company buyer: PDP in EN and BN at 1280 and 360
  for (const [prefix, lang, width] of [['', 'en', 1280], ['/bn', 'bn', 360], ['', 'en', 360], ['/bn', 'bn', 1280]]) {
    const L = ALL[lang].CompanyPrice;
    const page = await newPage(width);
    await signIn(page, prefix, USERS.buyer, `/products/${SLUG_A}`);
    const box = page.locator('[data-testid=company-price]');
    await box.waitFor({ timeout: 30000 });
    const text = await box.innerText();
    const tag2 = `${lang}@${width}`;
    r.check(`PDP ${tag2}: contract price net, excl. VAT label, ref, public price incl. VAT, company name`,
      text.includes(money(CONTRACT_A1, lang)) && text.includes(L.exclVat) && text.includes(REF) && text.includes(money(1_615_000, lang)) && text.includes(`Contract Mills ${tag}`.replace(/\d/g, (d) => d)), text.replace(/\n/g, ' | ').slice(0, 400));
    const vatLine = text.includes('15') || text.includes('১৫');
    r.check(`PDP ${tag2}: VAT line present`, vatLine);
    const rows = await box.locator('tbody tr').count();
    r.check(`PDP ${tag2}: break table shows both tiers`, rows === 2, `rows=${rows}`);
    // the quantity changes the price
    await page.locator('#pdp-qty').fill('12');
    await page.waitForFunction((want) => document.querySelector('[data-testid=company-price-unit]')?.textContent === want, money(CONTRACT_A10, lang), { timeout: 15000 }).catch(() => undefined);
    r.check(`PDP ${tag2}: quantity 12 switches to the 10+ tier`, (await box.locator('[data-testid=company-price-unit]').innerText()) === money(CONTRACT_A10, lang));
    await page.locator('#pdp-qty').fill('1');
    await page.waitForFunction((want) => document.querySelector('[data-testid=company-price-unit]')?.textContent === want, money(CONTRACT_A1, lang), { timeout: 15000 }).catch(() => undefined);
    r.check(`PDP ${tag2}: back to quantity 1`, (await box.locator('[data-testid=company-price-unit]').innerText()) === money(CONTRACT_A1, lang));
    r.check(`PDP ${tag2}: no horizontal overflow`, !(await overflow(page)));
    r.check(`PDP ${tag2}: no page errors`, page.errors.length === 0, page.errors.join(' | ').slice(0, 200));
    if (width === 1280 || lang === 'bn') await axeCheck(page, `PDP company price ${tag2}`);
    await page.context().close();
  }

  // layout shift measured on a fresh load of the PDP for the buyer (session already established in a context)
  {
    const ctx = await browser.newContext({ viewport: { width: 360, height: 740 } });
    const warm = await ctx.newPage();
    await signIn(warm, '', USERS.buyer, `/products/${SLUG_A}`);
    await warm.locator('[data-testid=company-price]').waitFor({ timeout: 30000 });
    await warm.close();
    const page = await ctx.newPage();
    await page.addInitScript(() => {
      window.__cls = 0;
      new PerformanceObserver((l) => { for (const e of l.getEntries()) if (!e.hadRecentInput) window.__cls += e.value; }).observe({ type: 'layout-shift', buffered: true });
    });
    await page.goto(pdp('', SLUG_A), { waitUntil: 'load' });
    await page.locator('[data-testid=company-price]').waitFor({ timeout: 30000 });
    await page.waitForTimeout(1500);
    const cls = await page.evaluate(() => window.__cls);
    console.log(`INFO  CLS on the PDP for a company buyer at 360 px (island appears after hydration): ${cls.toFixed(4)}`);
    r.check('PDP company buyer 360 px: CLS stays under 0.1', cls < 0.1, cls.toFixed(4));
    // the HTML the server hands out after a member visited still holds nothing private
    const rawAfter = await (await fetch(pdp('', SLUG_A))).text();
    r.check('ContractPrice_IsNotInTheCachedPageHtml (after the member visited)', leaks(rawAfter).length === 0, leaks(rawAfter).join(','));
    await ctx.close();
  }

  // ---- 4. the cart: second line per contract line, the note, and the server prices the order the same
  for (const [prefix, lang, width] of [['', 'en', 1280], ['/bn', 'bn', 360]]) {
    const L = ALL[lang].CompanyPrice;
    const page = await newPage(width);
    await signIn(page, prefix, USERS.buyer, `/products/${SLUG_A}`);
    // clean cart, then add A x2 and B x1 through the API (same token the page holds)
    await bcall('DELETE', '/api/v1/cart');
    await bcall('POST', '/api/v1/cart/items', { variantId: vA.id, quantity: 2 });
    await bcall('POST', '/api/v1/cart/items', { variantId: vB.id, quantity: 1 });
    await page.goto(`${WEB}${prefix}/cart`, { waitUntil: 'networkidle' });
    const lines = page.locator('[data-testid=cart-company-price]');
    await lines.first().waitFor({ timeout: 30000 });
    const texts = await lines.allInnerTexts();
    r.check(`cart ${lang}: a contract line for each of the two lines, with net prices`, texts.length === 2 && texts.some((x) => x.includes(money(CONTRACT_A1, lang))) && texts.some((x) => x.includes(money(CONTRACT_B1, lang))) && texts.every((x) => x.includes(L.exclVat)), texts.join(' | '));
    const note = await page.locator('[data-testid=cart-company-note]').innerText();
    r.check(`cart ${lang}: "final prices are confirmed at checkout" note`, note.includes(L.cartNote), note);
    r.check(`cart ${lang}: no horizontal overflow`, !(await overflow(page)));
    await axeCheck(page, `cart company prices ${lang}@${width}`);
    await page.context().close();
  }
  {
    // the order the server prices equals the contract line totals (before shipping)
    const asked = await bcall('GET', `/api/v1/companies/${companyId}/prices?items=${vA.id}:2,${vB.id}:1`);
    const sum = asked.json.data.items.reduce((a, i) => a + i.lineTotalMinor, 0);
    const addr = { companyId, shipToSiteId: site.json.data.id };
    const q = await bcall('POST', '/api/v1/orders/shipping-quote', { lines: [{ variantId: vA.id, quantity: 2 }, { variantId: vB.id, quantity: 1 }], ...addr, siteId: site.json.data.id });
    const placed = await fetch(`${API}/api/v1/orders`, {
      method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${buyer}`, 'idempotency-key': `e2e-d213-${Date.now()}` },
      body: JSON.stringify({ lines: [{ variantId: vA.id, quantity: 2 }, { variantId: vB.id, quantity: 1 }], ...addr })
    });
    const pj = await placed.json();
    const shipping = q.json.data?.totalMinor ?? 0;
    r.check('checkout is server-priced: order total = contract line totals + shipping quote', placed.status === 201 && pj.data.grandTotalMinor === sum + shipping, `order ${pj.data?.grandTotalMinor} vs ${sum} + ${shipping} (quote ${q.status})`);
    if (pj.data?.orderId) await bcall('POST', `/api/v1/orders/${pj.data.orderId}/cancel`, { reason: 'e2e cleanup' });
  }

  // ---- 5. a withdrawn list: the price is gone after a reload
  const withdrawn = await stepUp(admin, ADMIN, 'POST', `/api/v1/admin/price-lists/${listId}/withdraw`, { reason: 'D2.13 e2e cleanup' });
  r.check('the list is withdrawn with a real step-up code', withdrawn.status === 200, `${withdrawn.status} ${JSON.stringify(withdrawn.json).slice(0, 150)}`);
  {
    const page = await newPage(1280);
    await signIn(page, '', USERS.buyer, `/products/${SLUG_A}`);
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(2500);
    r.check('after withdrawal the PDP shows only the public price (no company box, no error banner)', (await page.locator('[data-testid=company-price]').count()) === 0 && (await page.getByText('16,150.00').count()) > 0 && (await page.locator('[role=alert]:not(#__next-route-announcer__)').count()) === 0);
    await page.context().close();
  }

  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
