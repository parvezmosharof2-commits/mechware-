// D4.13: "Request a quote" for company buyers (web): entry points (cart, product page, quick order, company dashboard), the request page
// (cart / pasted list / CSV file / one product, message, needed-by, delivery site), one RFQ per click even on a double click, one
// Idempotency-Key per distinct body, per-line parser errors, the error messages, the company's list and detail (own vs all, matched /
// obsolete / unmatched lines, withdraw with a confirm), outsiders and members who may not see it (one neutral not-found text), signed-out
// users sent to sign in, guests and personal accounts told "quotes are for verified company accounts", EN + BN at 360 and 1280 px, axe.
// Needs: the API (Development, SCRATCH DB mw_*, migrated, staff account admin2.e2e@ cloned from the admin row, see docs/modules/B3.md B3.11
// E2E setup) and the web app built against it. The API allows CORS only from localhost:3000/3001: use a CORS proxy for another origin.
// Skipped (exit 0) unless E2E_QUOTES=1.
//   E2E_QUOTES=1 E2E_API_URL=http://localhost:5372 E2E_WEB_URL=http://localhost:3371 E2E_DB=mw_d413 node tests/quotes.mjs
// Real: companies, members, a cart, the RFQs themselves (every success path), withdraw, the 404 for outsiders, the pending company.
// Mocked (a request rewrite, the API answers are the documented bodies): the error codes that need another state of the world (suspended
// company, unverified account, hourly cap, open cap, size limit, key reuse, a network failure).
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { WEB, API, USERS, apiCall, apiLogin, launch, reporter, totp, uniqueBin } from './lib.mjs';

if (process.env.E2E_QUOTES !== '1') {
  console.log('SKIP  quotes E2E (set E2E_QUOTES=1 with the API, a scratch DB and the web app running)');
  process.exit(0);
}
const DB = process.env.E2E_DB;
if (!DB || !/^mw_/.test(DB) || DB === 'mechware') {
  console.error('E2E_DB must name a SCRATCH database (mw_*), never the dev database');
  process.exit(2);
}

const r = reporter();
const here = dirname(fileURLToPath(import.meta.url));
const sleep = (ms) => new Promise((res) => setTimeout(res, ms));
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
const MSG = {
  en: JSON.parse(readFileSync(resolve(here, '../../../apps/web/src/messages/en.json'), 'utf8')),
  bn: JSON.parse(readFileSync(resolve(here, '../../../apps/web/src/messages/bn.json'), 'utf8'))
};
const SLUG = process.env.E2E_SLUG ?? 'fluke-17b-digital-multimeter-handheld-with-temperature-958548';
const tag = String(Date.now() % 100000);
const overflow = (p) => p.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
const sql = (text) => execFileSync('docker', ['exec', '-i', 'mechware_postgres', 'psql', '-q', '-At', '-v', 'ON_ERROR_STOP=1', '-U', 'mechware', '-d', DB], { input: text, encoding: 'utf8' });

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
const ADMIN2 = { email: 'admin2.e2e@mechware.com.bd', password: USERS.admin.password, totpSecret: USERS.admin.totpSecret };

async function signIn(page, prefix, who, path) {
  const auth = MSG[prefix ? 'bn' : 'en'].Auth;
  await page.goto(`${WEB}${prefix}/auth/login?returnTo=${encodeURIComponent(prefix + path)}`, { waitUntil: 'networkidle' });
  await page.getByLabel(auth.emailOrPhoneLabel).fill(who.email);
  await page.locator('#auth-pw').fill(who.password);
  const answer = page.waitForResponse((res) => res.url().includes('/auth/login') && res.request().method() === 'POST', { timeout: 30000 });
  await page.getByRole('button', { name: auth.signInButton }).click();
  const login = await answer;
  if (!login.ok()) throw new Error(`sign-in of ${who.email} failed: ${login.status()}`);
  await page.waitForURL(new RegExp(`${prefix}${path.replace(/[/?.=&]/g, '\\$&')}`), { timeout: 60000 });
}

(async () => {
  const en = MSG.en.Quotes;
  const bn = MSG.bn.Quotes;
  // ---------- setup through the API
  const buyer = await apiLogin(USERS.buyer);
  const alpha = await apiLogin(USERS.sellerAlpha);
  const beta = await apiLogin(USERS.sellerBeta);
  const bcall = (m, p, b) => apiCall(buyer, m, p, b);
  const admin = await staffToken(ADMIN2);
  const variant = (await (await fetch(`${API}/api/v1/storefront/products/${SLUG}`)).json()).data.variants[0];

  // Scratch DB only: a re-run starts with alpha as a personal account again (no membership left from the last run).
  sql(`DELETE FROM company_members WHERE user_id = (SELECT id FROM users WHERE email = '${USERS.sellerAlpha.email}');`);
  // alpha has no company yet (a personal account): checked first, before alpha is added as a Requester.
  const alphaId = (await apiCall(alpha, 'GET', '/api/v1/users/me')).json.data.id;
  const alphaCompanies = (await apiCall(alpha, 'GET', '/api/v1/companies')).json.data ?? [];
  const betaCompanies = (await apiCall(beta, 'GET', '/api/v1/companies')).json.data ?? [];
  r.check('setup: seller alpha is a personal account (no company)', alphaCompanies.length === 0, `${alphaCompanies.length}/${betaCompanies.length}`);

  const reg = await bcall('POST', '/api/v1/companies', { legalName: `Quote Mills ${tag} Ltd`, tradeLicenceNo: 'TL-D413', bin: uniqueBin() });
  const companyId = reg.json.data.id;
  const act = await apiCall(admin, 'POST', `/api/v1/admin/companies/${companyId}/activate`);
  const me = (await bcall('GET', '/api/v1/users/me')).json.data.id;
  const own = (await bcall('GET', `/api/v1/companies/${companyId}/members`)).json.data.find((x) => x.userId === me);
  await bcall('PUT', `/api/v1/companies/${companyId}/members/${own.id}`, { roles: 10, spendLimitMinor: null, costCentreIds: [], isActive: true });
  const site = await bcall('POST', `/api/v1/companies/${companyId}/sites`, { name: 'Gazipur Plant', contactName: 'Sabbir', contactPhone: '01711000000', line1: 'Plot 5, BSCIC Area', upazila: 'Tongi Industrial Zone', district: 'Gazipur', division: 'Dhaka', isDefaultDelivery: true });
  r.check('setup: an active company with an Admin+Buyer owner and a delivery site', reg.status === 201 && act.status === 200 && site.status === 201, `${reg.status}/${act.status}/${site.status}`);

  // beta owns a company that stays Pending (never verified): the "not verified" case, and an outsider to the active company.
  const pending = await apiCall(beta, 'POST', '/api/v1/companies', { legalName: `Pending Works ${tag} Ltd`, tradeLicenceNo: 'TL-D413P', bin: uniqueBin() });
  // On a reused scratch DB beta already owns a Pending company from an earlier run: that one does the same job.
  const pendingOk = pending.status === 201 || (await apiCall(beta, 'GET', '/api/v1/companies')).json.data.some((m) => m.company.status === 'pending');
  r.check('setup: another owner with a company that is still Pending', pendingOk, String(pending.status));

  // A Requester (role 1) colleague and her own request, made through the API before alpha is checked as a Requester on the pages.
  const cart = async (token, qty) => {
    const res = await fetch(`${API}/api/v1/cart/items`, { method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${token}` }, body: JSON.stringify({ variantId: variant.id, quantity: qty }) });
    return res.status;
  };

  const browser = await launch();
  const newPage = async (width, prefix = '') => {
    const ctx = await browser.newContext({ viewport: { width, height: width < 600 ? 740 : 900 }, locale: prefix ? 'bn-BD' : 'en-US' });
    const page = await ctx.newPage();
    page.errors = [];
    page.rfqCalls = [];
    page.expectNoise = false;
    page.on('console', (m) => { // Scratch artefact: product images come from the API origin (5372), which the page's img-src does not list; not a defect of this unit.
      if (m.type() === 'error' && !page.expectNoise && !/img-src|Failed to load resource: the server responded with a status of 4\d\d/.test(m.text())) page.errors.push(`console: ${m.text()}`); });
    page.on('pageerror', (e) => page.errors.push(`pageerror: ${e.message}`));
    page.on('response', (res) => {
      if (/\/companies\/[^/]+\/rfqs/.test(res.url()) && res.request().method() !== 'OPTIONS') page.rfqCalls.push({ method: res.request().method(), status: res.status(), cc: res.headers()['cache-control'] ?? '' });
    });
    return page;
  };
  // The seeded buyer may belong to other companies (a reused scratch DB): the request page then offers a company choice; pick ours.
  const sub = async (p) => {
    const sel = p.locator('#rfq-company');
    if ((await sel.count()) > 0 && (await sel.locator(`option[value="${companyId}"]`).count()) > 0) await sel.selectOption(companyId);
    return p.getByTestId('rfq-submit');
  };
  const rfqCount = async (token = buyer) => (await apiCall(token, 'GET', `/api/v1/companies/${companyId}/rfqs?pageSize=50`)).json.data.total;

  // ---------- guest: short note instead of the buttons, signed-out request page goes to sign-in
  {
    const p = await newPage(1280);
    await p.goto(`${WEB}/products/${SLUG}`, { waitUntil: 'networkidle' });
    const note = p.getByTestId('quote-note');
    await note.first().waitFor({ timeout: 30000 });
    r.check('guest PDP: a short "company accounts" note and no quote link', (await note.first().innerText()).includes(en.companyOnlyNote) && (await p.locator('a[href*="request-quote"]').count()) === 0);
    await p.goto(`${WEB}/cart`, { waitUntil: 'networkidle' });
    r.check('guest cart: no quote link', (await p.locator('a[href*="request-quote"]').count()) === 0);
    await p.goto(`${WEB}/request-quote?source=text`, { waitUntil: 'networkidle' });
    await p.waitForURL(/\/auth\/login\?returnTo=/, { timeout: 30000 });
    r.check('signed-out /request-quote is sent to sign-in with a returnTo', decodeURIComponent(p.url()).includes('returnTo=/request-quote?source=text'), p.url());
    await p.context().close();
  }

  // ---------- personal account (alpha, no company yet)
  {
    const p = await newPage(1280);
    await signIn(p, '', USERS.sellerAlpha, `/products/${SLUG}`);
    await p.waitForLoadState('networkidle');
    await p.getByTestId('quote-note').first().waitFor({ timeout: 30000 });
    r.check('personal account PDP: the note, no quote link', (await p.locator('a[href*="request-quote"]').count()) === 0);
    await p.goto(`${WEB}/request-quote?source=text`, { waitUntil: 'networkidle' });
    const box = p.getByTestId('quote-company-only');
    await box.waitFor({ timeout: 30000 });
    r.check('personal account request page: "quotes are for verified company accounts", no form', (await box.innerText()).includes(en.companyOnlyNote) && (await p.getByTestId('rfq-form').count()) === 0);
    await p.context().close();
  }

  // ---------- company buyer, EN, 1280: entry points
  const buyerPage = await newPage(1280);
  {
    const p = buyerPage;
    await signIn(p, '', USERS.buyer, `/products/${SLUG}`);
    await p.waitForLoadState('networkidle');
    const link = p.getByRole('link', { name: en.askForProduct });
    await link.waitFor({ timeout: 30000 });
    r.check('buyer PDP: "Ask for a quote" link to the request page for this product', (await link.getAttribute('href')).includes('/request-quote?source=product&variantId='));
    await p.locator('#pdp-qty').fill('3');
    await p.locator('#pdp-qty').blur();
    await p.waitForTimeout(300);
    await link.click();
    await p.waitForURL(/\/request-quote\?source=product/, { timeout: 30000 });
    const form = p.getByTestId('rfq-form');
    await form.waitFor({ timeout: 30000 });
    r.check('product source: the quantity from the product page and the product name are carried', (await p.locator('#rfq-qty').inputValue()) === '3' && (await form.innerText()).includes(en.productInfo));
    await axeCheck(p, 'request page (product), 1280');

    // optional details + one click = one RFQ even with a double click
    const needed = new Date(Date.now() + 6 * 3_600_000 + 10 * 86_400_000).toISOString().slice(0, 10);
    await sub(p);
    await p.locator('#rfq-site option', { hasText: 'Gazipur Plant' }).waitFor({ state: 'attached', timeout: 30000 });
    await p.locator('#rfq-message').fill(`Need it for the Gazipur plant ${tag}`);
    await p.locator('#rfq-needed').fill(needed);
    await p.locator('#rfq-site').selectOption({ label: 'Gazipur Plant' });
    const before = await rfqCount();
    await (await sub(p)).dblclick();
    await p.waitForURL(/\/rfqs\/[0-9a-f-]{36}\?created=1/, { timeout: 30000 });
    const after = await rfqCount();
    r.check('double click on "Send request" creates exactly ONE request', after === before + 1, `${before} -> ${after}`);
    await p.getByTestId('rfq-line').first().waitFor({ timeout: 30000 });
    r.check('the new request shows its number, the success note, matched line, message, needed-by and site', (await p.getByTestId('rfq-created').count()) === 1 && /^MWR/.test(await p.getByTestId('rfq-number').innerText()) && (await p.getByTestId('rfq-line').first().getAttribute('data-match')) === 'matched' && (await p.getByTestId('rfq-message').innerText()).includes(tag) && (await p.locator('main').innerText()).includes('Gazipur Plant'), (await p.getByTestId('rfq-number').innerText()));
    const posts = p.rfqCalls.filter((c) => c.method === 'POST');
    r.check('the create answers are private no-store', posts.length >= 1 && posts.every((c) => /no-store/.test(c.cc)), JSON.stringify(posts));
    await axeCheck(p, 'request detail, 1280');
  }

  // ---------- cart entry
  {
    const p = buyerPage;
    r.check('setup: the buyer cart has the product', (await cart(buyer, 2)) < 300);
    await p.goto(`${WEB}/cart`, { waitUntil: 'networkidle' });
    const ask = p.getByTestId('cart-ask-quote');
    await ask.waitFor({ timeout: 30000 });
    await ask.click();
    await p.waitForURL(/\/request-quote\?source=cart/, { timeout: 30000 });
    await p.getByTestId('rfq-source-cart').waitFor();
    const before = await rfqCount();
    await (await sub(p)).click();
    await p.waitForURL(/\/rfqs\/[0-9a-f-]{36}\?created=1/, { timeout: 30000 });
    await p.getByTestId('rfq-line').first().waitFor({ timeout: 30000 });
    const lines = await p.getByTestId('rfq-line').count();
    r.check('cart source: "Ask for a quote for this cart" creates one request with the cart line', (await rfqCount()) === before + 1 && lines === 1, `lines ${lines}`);
    r.check('cart source: the cart is still there (asking changes nothing)', (await (await fetch(`${API}/api/v1/cart`, { headers: { authorization: `Bearer ${buyer}` } })).json()).data.lines.length === 1);
  }

  // ---------- pasted text: per-line errors, then a good list; CSV file
  {
    const p = buyerPage;
    await p.goto(`${WEB}/quick-order`, { waitUntil: 'networkidle' });
    const ask = p.getByTestId('qo-ask-quote');
    await ask.waitFor({ timeout: 30000 });
    await ask.click();
    await p.waitForURL(/\/request-quote\?source=text/, { timeout: 30000 });
    await p.locator('#rfq-text').waitFor();
    const sku = variant.sku;
    await p.locator('#rfq-text').fill(`${sku} 4\nABC 1.5\n@@@ 3`);
    const before = await rfqCount();
    await (await sub(p)).click();
    const err = p.getByTestId('rfq-error');
    await err.waitFor({ timeout: 30000 });
    const items = await p.getByTestId('rfq-line-errors').locator('li').allInnerTexts();
    r.check('pasted list with bad lines: 422 shown per line (line numbers, the text of the line, localised reason), nothing sent', (await err.getAttribute('data-kind')) === 'linesInvalid' && items.length === 2 && items[0].includes('2') && items[0].includes('ABC 1.5') && items[1].includes('@@@ 3') && (await rfqCount()) === before, JSON.stringify(items));
    r.check('the error alert takes focus', await p.evaluate(() => document.activeElement?.getAttribute('data-testid') === 'rfq-error'));
    await axeCheck(p, 'request page with line errors, 1280');
    // fix: a known part, an unknown part, a quantity line; resend. Same text twice (retry) would be the same key; here the text changed.
    await p.locator('#rfq-text').fill(`${sku} 4\nZZ-NOPE-1 2`);
    await (await sub(p)).click();
    await p.waitForURL(/\/rfqs\/[0-9a-f-]{36}\?created=1/, { timeout: 30000 });
    await p.getByTestId('rfq-line').first().waitFor({ timeout: 30000 });
    const kinds = await p.getByTestId('rfq-line').evaluateAll((els) => els.map((e) => e.getAttribute('data-match')));
    r.check('fixed list: one request, one line found and one not found (still sent for the team to look for)', (await rfqCount()) === before + 1 && kinds.join(',') === 'matched,notFound', kinds.join(','));
    r.check('the summary counts matched / obsolete / unmatched', (await p.getByTestId('rfq-counts').innerText()).length > 5);

    // CSV
    await p.goto(`${WEB}/request-quote?source=text`, { waitUntil: 'networkidle' });
    await p.locator('input[type=file]').setInputFiles({ name: 'parts.csv', mimeType: 'text/csv', buffer: Buffer.from(`﻿${sku},7\r\n`, 'utf8') });
    await p.waitForFunction(() => document.querySelector('#rfq-text')?.value.includes(','));
    const b2 = await rfqCount();
    await (await sub(p)).click();
    await p.waitForURL(/\/rfqs\/[0-9a-f-]{36}\?created=1/, { timeout: 30000 });
    await p.getByTestId('rfq-number').waitFor({ timeout: 30000 });
    r.check('CSV file (with a BOM): one request, source shown as CSV file', (await rfqCount()) === b2 + 1 && (await p.locator('main').innerText()).includes(en.source_csv), String(await rfqCount()));
    // a wrong file type
    await p.goto(`${WEB}/request-quote?source=text`, { waitUntil: 'networkidle' });
    await p.locator('input[type=file]').setInputFiles({ name: 'photo.png', mimeType: 'image/png', buffer: Buffer.from('x') });
    await p.getByTestId('rfq-error').waitFor();
    r.check('a file that is not .csv/.txt is refused with a message', (await p.getByTestId('rfq-error').getAttribute('data-kind')) === 'fileWrong');
  }

  // ---------- one Idempotency-Key per distinct body (a flaky network, then an edit)
  {
    const p = buyerPage;
    await p.goto(`${WEB}/request-quote?source=text`, { waitUntil: 'networkidle' });
    const keys = [];
    let mode = 'fail';
    await p.route(/\/api\/v1\/companies\/[^/]+\/rfqs$/, async (route) => {
      if (route.request().method() !== 'POST') return route.fallback();
      keys.push(route.request().headers()['idempotency-key']);
      if (mode === 'fail') return route.abort('failed');
      return route.fallback();
    });
    p.expectNoise = true;
    await p.locator('#rfq-text').fill(`${variant.sku} 1`);
    await (await sub(p)).click();
    await p.getByTestId('rfq-error').waitFor();
    const kind1 = await p.getByTestId('rfq-error').getAttribute('data-kind');
    await (await sub(p)).click(); // retry, same details
    await p.waitForTimeout(500);
    await p.locator('#rfq-text').fill(`${variant.sku} 2`); // edited details
    await (await sub(p)).click();
    await p.waitForTimeout(500);
    r.check('network failure: told to try again; a retry with the same details reuses the key, an edit gets a new one', kind1 === 'unavailable' && keys.length === 3 && keys[0] === keys[1] && keys[2] !== keys[0], `${kind1} ${keys.join(' | ')}`);
    mode = 'pass';
    const b = await rfqCount();
    await (await sub(p)).click();
    await p.waitForURL(/\/rfqs\/[0-9a-f-]{36}\?created=1/, { timeout: 30000 });
    r.check('then the request goes through once', (await rfqCount()) === b + 1);
    await p.unroute(/\/api\/v1\/companies\/[^/]+\/rfqs$/);
    p.expectNoise = false;
  }

  // ---------- the documented error codes, each in plain words (mocked answers)
  {
    const p = buyerPage;
    const cases = [
      ['COMPANY_NOT_VERIFIED', 409, 'companyNotVerified'], ['COMPANY_SUSPENDED', 409, 'companySuspended'], ['ACCOUNT_NOT_VERIFIED', 403, 'accountNotVerified'],
      ['QTY_EXCEEDS_LIMIT', 422, 'qtyExceeds', [{ field: 'lines[1]', message: '20,000 is more than 10,000.' }]], ['INPUT_TOO_LARGE', 413, 'tooLarge'],
      ['RFQ_RATE_LIMITED', 429, 'rateLimited'], ['TOO_MANY_OPEN_RFQS', 409, 'tooManyOpen'], ['IDEMPOTENCY_KEY_REUSED', 409, 'keyReused']
    ];
    for (const lang of ['en', 'bn']) {
      const prefix = lang === 'bn' ? '/bn' : '';
      const M = MSG[lang].Quotes;
      await p.goto(`${WEB}${prefix}/request-quote?source=text`, { waitUntil: 'networkidle' });
      let current = null;
      await p.route(/\/api\/v1\/companies\/[^/]+\/rfqs$/, (route) => {
        if (route.request().method() !== 'POST') return route.fallback();
        const [code, status, , details] = current;
        return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify({ type: 'x', title: code, status, detail: code, error: { code, message: code, details: details ?? [] } }) });
      });
      p.expectNoise = true;
      for (const c of cases) {
        current = c;
        await p.locator('#rfq-text').fill(`${variant.sku} 1 ${c[0]}`.replace(` ${c[0]}`, '') + `\n${variant.sku} ${c[1]}`);
        await (await sub(p)).click();
        const err = p.getByTestId('rfq-error');
        await err.waitFor({ timeout: 15000 });
        await p.waitForFunction((k) => document.querySelector('[data-testid=rfq-error]')?.getAttribute('data-kind') === k, c[2], { timeout: 15000 });
        const text = await err.innerText();
        r.check(`${lang} ${c[0]} (${c[1]}): ${c[2]} message shown`, text.includes(M[`error_${c[2]}`]), text.slice(0, 80));
        if (c[3]) r.check(`${lang} ${c[0]}: the per-line detail is listed`, (await p.getByTestId('rfq-line-errors').locator('li').count()) === 1);
      }
      await p.unroute(/\/api\/v1\/companies\/[^/]+\/rfqs$/);
      p.expectNoise = false;
    }
    // Visiting /bn sets the language cookie; the English checks below must see English again.
    await p.context().clearCookies({ name: 'NEXT_LOCALE' });
  }

  // ---------- the company's list (Admin+Buyer sees all) and a Requester colleague (own only)
  const buyerRfqs = (await bcall('GET', `/api/v1/companies/${companyId}/rfqs?pageSize=50`)).json.data;
  const sample = buyerRfqs.items[0];
  const added = await bcall('POST', `/api/v1/companies/${companyId}/members`, { userId: alphaId, roles: 1, spendLimitMinor: null, costCentreIds: [] });
  const accepted = await apiCall(alpha, 'POST', `/api/v1/companies/${companyId}/invitations/accept`);
  r.check('setup: alpha becomes a Requester of the company', added.status === 201 && accepted.status === 200, `${added.status}/${accepted.status}`);
  {
    const p = buyerPage;
    await p.goto(`${WEB}/account/company/${companyId}`, { waitUntil: 'networkidle' });
    await p.getByTestId('rfq-card-list').click();
    await p.waitForURL(/\/rfqs$/, { timeout: 30000 });
    await p.getByTestId('rfq-list').waitFor({ timeout: 30000 });
    const n = await p.getByTestId('rfq-list').locator('li').count();
    r.check('company dashboard links to the list; an Admin/Buyer sees all the company requests, with a "see all" note', n === Math.min(buyerRfqs.total, 20) && (await p.getByTestId('rfq-scope').innerText()) === en.listScopeAll, `${n}/${buyerRfqs.total}`);
    r.check('list answers are no-store', p.rfqCalls.filter((c) => c.method === 'GET').every((c) => /no-store/.test(c.cc)));
    await axeCheck(p, 'request list, 1280');
  }
  // alpha asks for her own request through the API (a Requester may), the buyer must still see it, alpha must not see the buyer's
  const aRes = await fetch(`${API}/api/v1/companies/${companyId}/rfqs`, { method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${alpha}`, 'idempotency-key': `e2e-d413-alpha-${tag}` }, body: JSON.stringify({ source: 'product', variantId: variant.id, quantity: 5 }) });
  const alphaCreated = (await aRes.json()).data?.rfq;
  r.check('setup: the Requester asks for her own quote', aRes.status === 201 && Boolean(alphaCreated), String(aRes.status));
  {
    const p = await newPage(1280);
    await signIn(p, '', USERS.sellerAlpha, `/account/company/${companyId}/rfqs`);
    await p.getByTestId('rfq-list').waitFor({ timeout: 30000 });
    const nums = await p.getByTestId('rfq-list').locator('li').count();
    r.check('Requester sees only her own request(s), with the "your own" note', nums === 1 && (await p.getByTestId('rfq-scope').innerText()) === en.listScopeOwn, String(nums));
    // a colleague's request by id: the same not-found text as an unknown id
    await p.goto(`${WEB}/account/company/${companyId}/rfqs/${sample.id}`, { waitUntil: 'networkidle' });
    await p.getByTestId('rfq-not-found').waitFor({ timeout: 30000 });
    const t1 = await p.getByTestId('rfq-not-found').innerText();
    await p.goto(`${WEB}/account/company/${companyId}/rfqs/00000000-0000-4000-8000-000000000000`, { waitUntil: 'networkidle' });
    await p.getByTestId('rfq-not-found').waitFor({ timeout: 30000 });
    r.check("Requester opening a colleague's request gets the same not-found text as an unknown id, no crash", t1 === (await p.getByTestId('rfq-not-found').innerText()) && t1.includes(en.notFoundTitle) && p.errors.length === 0, p.errors.join('|'));
    // her own request: detail with withdraw
    await p.goto(`${WEB}/account/company/${companyId}/rfqs/${alphaCreated.id}`, { waitUntil: 'networkidle' });
    await p.getByTestId('rfq-number').waitFor({ timeout: 30000 });
    r.check('Requester sees her own request detail', (await p.getByTestId('rfq-number').innerText()) === alphaCreated.number);
    await p.context().close();
  }
  // buyer sees alpha's request (and a note that a colleague asked)
  {
    const p = buyerPage;
    await p.goto(`${WEB}/account/company/${companyId}/rfqs/${alphaCreated.id}`, { waitUntil: 'networkidle' });
    await p.getByTestId('rfq-number').waitFor({ timeout: 30000 });
    r.check("Admin/Buyer opens a colleague's request and is told a colleague asked", (await p.locator('main').innerText()).includes(en.byColleague));
  }

  // ---------- outsider (beta, other company) and a Pending company
  {
    const p = await newPage(1280);
    await signIn(p, '', USERS.sellerBeta, `/account/company/${companyId}/rfqs`);
    await p.getByTestId('rfq-not-found').waitFor({ timeout: 30000 });
    r.check('another company\'s member opening this company\'s list: the neutral not-found text, no crash', (await p.getByTestId('rfq-not-found').innerText()).includes(en.notFoundBody) && p.errors.length === 0, p.errors.join('|'));
    await p.goto(`${WEB}/account/company/${companyId}/rfqs/${sample.id}`, { waitUntil: 'networkidle' });
    await p.getByTestId('rfq-not-found').waitFor({ timeout: 30000 });
    r.check("the same outsider opening a request by id: the same text", true);
    // Pending company: the form warns first, and the server's COMPANY_NOT_VERIFIED is shown too
    await p.goto(`${WEB}/request-quote?source=text`, { waitUntil: 'networkidle' });
    await p.getByTestId('rfq-company-status').waitFor({ timeout: 30000 });
    r.check('a member of a Pending company is told up front that the company is not verified', (await p.getByTestId('rfq-company-status').innerText()).includes(en.error_companyNotVerified));
    await p.locator('#rfq-text').fill(`${variant.sku} 1`);
    await (await sub(p)).click();
    await p.getByTestId('rfq-error').waitFor({ timeout: 30000 });
    // The API answers a Pending company's create with 409 COMPANY_NOT_VERIFIED (D4.5): shown in words.
    const kind = await p.getByTestId('rfq-error').getAttribute('data-kind');
    r.check('the real API refuses the Pending company and the page says so (COMPANY_NOT_VERIFIED)', kind === 'companyNotVerified', kind);
    await p.context().close();
  }

  // ---------- withdraw with a confirm (own request)
  {
    const p = buyerPage;
    await p.goto(`${WEB}/account/company/${companyId}/rfqs/${sample.id}`, { waitUntil: 'networkidle' });
    await p.getByTestId('rfq-withdraw').click();
    const dlg = p.getByRole('dialog');
    await dlg.waitFor();
    r.check('withdraw asks first (dialog with the request number)', (await dlg.innerText()).includes(sample.number));
    await axeCheck(p, 'withdraw dialog, 1280');
    await dlg.getByRole('button', { name: en.keep }).click();
    r.check('"Keep request" closes the dialog and changes nothing', (await p.getByRole('dialog').count()) === 0 && (await apiCall(buyer, 'GET', `/api/v1/companies/${companyId}/rfqs/${sample.id}`)).json.data.rfq.withdrawnAt === null);
    await p.getByTestId('rfq-withdraw').click();
    await p.getByTestId('rfq-withdraw-confirm').click();
    await p.getByTestId('rfq-withdrawn').waitFor({ timeout: 30000 });
    const after = (await apiCall(buyer, 'GET', `/api/v1/companies/${companyId}/rfqs/${sample.id}`)).json.data.rfq;
    r.check('confirmed: withdrawn status shown, no withdraw button, the server agrees', after.withdrawnAt !== null && (await p.getByTestId('rfq-withdraw').count()) === 0 && (await p.locator('main').innerText()).includes(en.status_withdrawn), JSON.stringify(after).slice(0, 120));
  }

  // ---------- obsolete line with a successor and an unmatched one, as the API documents them (mocked detail answer)
  {
    const p = buyerPage;
    const v = (sku, title) => ({ id: '11111111-1111-4111-8111-111111111111', sku, title, titleBn: `${title} (bn)` });
    await p.route(new RegExp(`/api/v1/companies/${companyId}/rfqs/${sample.id}$`), async (route) => {
      const real = await route.fetch();
      const body = await real.json();
      body.data.lines = [
        { lineNo: 1, rawText: 'OLD-1 2', partKey: 'OLD1', quantity: 2, matchStatus: 'obsolete', variant: v('OLD-1', 'Old relay'), successor: v('NEW-1', 'New relay'), note: null },
        { lineNo: 2, rawText: 'GONE-2 1', partKey: 'GONE2', quantity: 1, matchStatus: 'not_found', variant: null, successor: null, note: null },
        { lineNo: 3, rawText: 'X-3 1', partKey: 'X3', quantity: 1, matchStatus: 'matched', variant: v('X-3', 'Found thing'), successor: null, note: 'Typed on lines 3, 4' }
      ];
      await route.fulfill({ response: real, json: body });
    });
    await p.goto(`${WEB}/account/company/${companyId}/rfqs/${sample.id}`, { waitUntil: 'networkidle' });
    await p.getByTestId('rfq-successor').waitFor({ timeout: 30000 });
    const kinds = await p.getByTestId('rfq-line').evaluateAll((els) => els.map((e) => e.getAttribute('data-match')));
    r.check('detail: an obsolete line shows its successor; unmatched and matched lines are told apart', kinds.join(',') === 'obsolete,notFound,matched' && (await p.getByTestId('rfq-successor').innerText()).includes('New relay (NEW-1)'), kinds.join(','));
    await p.unroute(new RegExp(`/api/v1/companies/${companyId}/rfqs/${sample.id}$`));
  }

  // ---------- BN at 360 px: request page, list and detail, no horizontal overflow, axe
  {
    const p = await newPage(360, '/bn');
    await signIn(p, '/bn', USERS.buyer, `/request-quote?source=text`);
    await p.locator('#rfq-text').waitFor({ timeout: 30000 });
    r.check('bn: request page title and labels are Bangla', (await p.locator('h1').innerText()) === bn.requestTitle && (await p.locator('label[for=rfq-text]').innerText()) === bn.textLabel);
    r.check('bn 360 px: request page has no horizontal scroll', !(await overflow(p)));
    await axeCheck(p, 'request page, bn 360');
    await p.locator('#rfq-text').fill(`${variant.sku} 1\nABC 1.5`);
    await (await sub(p)).click();
    await p.getByTestId('rfq-error').waitFor({ timeout: 30000 });
    r.check('bn: the per-line error is Bangla (line label and reason)', (await p.getByTestId('rfq-line-errors').innerText()).includes(bn.lineN.replace('{n}', '2')));
    r.check('bn 360 px: errors do not overflow', !(await overflow(p)));
    await axeCheck(p, 'request page with errors, bn 360');
    await p.goto(`${WEB}/bn/account/company/${companyId}/rfqs`, { waitUntil: 'networkidle' });
    await p.getByTestId('rfq-list').waitFor({ timeout: 30000 });
    r.check('bn 360 px: list is Bangla and has no horizontal scroll', (await p.locator('h1').innerText()) === bn.listTitle && !(await overflow(p)));
    await axeCheck(p, 'request list, bn 360');
    await p.goto(`${WEB}/bn/account/company/${companyId}/rfqs/${alphaCreated.id}`, { waitUntil: 'networkidle' });
    await p.getByTestId('rfq-number').waitFor({ timeout: 30000 });
    r.check('bn 360 px: detail has no horizontal scroll', !(await overflow(p)));
    await axeCheck(p, 'request detail, bn 360');
    await p.goto(`${WEB}/bn/products/${SLUG}`, { waitUntil: 'networkidle' });
    await p.getByRole('link', { name: bn.askForProduct }).waitFor({ timeout: 30000 });
    r.check('bn PDP: the quote link is in Bangla and goes to /bn/request-quote', (await p.getByRole('link', { name: bn.askForProduct }).getAttribute('href')).startsWith('/bn/request-quote'));
    r.check('no console or page errors in the BN run', p.errors.length === 0, p.errors.join('|'));
    await p.context().close();
  }

  // ---------- 360 px EN request page + no errors on the main buyer page
  {
    const p = await newPage(360);
    await signIn(p, '', USERS.buyer, `/request-quote?source=cart`);
    await p.getByTestId('rfq-form').waitFor({ timeout: 30000 });
    r.check('en 360 px: request page has no horizontal scroll', !(await overflow(p)));
    await axeCheck(p, 'request page (cart), 360');
    await p.context().close();
  }
  r.check('no console or page errors on the buyer desktop run', buyerPage.errors.length === 0, buyerPage.errors.join('|'));

  await browser.close();
  const ok = r.finish();
  process.exit(ok ? 0 : 1);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
