// B3.13: the buyer-side company credit panel (web): balance, open items, pending payments, statement with a date filter and paging, the
// credit status on an order, EN + BN, and who must NOT see it (a Requester/Approver sees no balance and no scare; another company's id is
// "not found"; the answers are private no-store).
// Needs: the API (Development, SCRATCH DB mw_*, a catalogue, staff accounts finance.e2e@ + admin2.e2e@ cloned from the admin row, see
// docs/modules/B3.md B3.11 E2E setup), the web app built against it. The API allows CORS only from localhost:3000/3001: use a CORS proxy
// for another origin. Skipped (exit 0) unless E2E_CREDIT=1.
//   E2E_CREDIT=1 E2E_API_URL=http://localhost:5314 E2E_WEB_URL=http://localhost:3313 E2E_DB=mw_b313 node tests/credit.mjs
// Real: companies, members, credit terms (admin step-up code), two credit orders placed through the API, a payment recorded by Finance and
// verified by a second person with a real step-up code, a second payment left waiting, an order made overdue directly in the SCRATCH DB (the
// ledger is append-only and due dates are fixed at the charge; a docker `psql` with replication role "replica" moves one due date back).
// Mocked: nothing (paging is exercised by asking the real API for a page size of 1 through a request rewrite).
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { WEB, API, USERS, apiCall, apiLogin, launch, reporter, totp, uniqueBin } from './lib.mjs';

if (process.env.E2E_CREDIT !== '1') {
  console.log('SKIP  credit panel E2E (set E2E_CREDIT=1 with the API, a scratch DB and the web app running)');
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
const money = (minor, lang = 'en') => `${minor < 0 ? '-' : ''}৳${new Intl.NumberFormat(lang === 'bn' ? 'bn-BD' : 'en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(Math.abs(minor) / 100)}`;
const dhakaToday = () => new Date(Date.now() + 6 * 3_600_000).toISOString().slice(0, 10);
const addDays = (day, n) => new Date(new Date(`${day}T00:00:00Z`).getTime() + n * 86_400_000).toISOString().slice(0, 10);

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
async function staffCall(token, person, method, path, body, extra = {}, withCode = true) {
  const res = await fetch(`${API}${path}`, {
    method,
    headers: { 'content-type': 'application/json', authorization: `Bearer ${token}`, ...(withCode ? { 'x-step-up-code': await freshCode(person) } : {}), ...extra },
    body: body ? JSON.stringify(body) : undefined
  });
  return { status: res.status, json: await res.json().catch(() => ({})) };
}
const ADMIN = USERS.admin;
const FINANCE = { email: 'finance.e2e@mechware.com.bd', password: ADMIN.password, totpSecret: ADMIN.totpSecret };
const ADMIN2 = { email: 'admin2.e2e@mechware.com.bd', password: ADMIN.password, totpSecret: ADMIN.totpSecret };

async function signIn(page, prefix, who, path) {
  const auth = MSG[prefix ? 'bn' : 'en'].Auth;
  await page.goto(`${WEB}${prefix}/auth/login?returnTo=${encodeURIComponent(prefix + path)}`, { waitUntil: 'networkidle' });
  await page.getByLabel(auth.emailOrPhoneLabel).fill(who.email);
  await page.locator('#auth-pw').fill(who.password);
  const answer = page.waitForResponse((res) => res.url().includes('/auth/login') && res.request().method() === 'POST', { timeout: 30000 });
  await page.getByRole('button', { name: auth.signInButton }).click();
  const login = await answer;
  if (!login.ok()) throw new Error(`sign-in of ${who.email} failed: ${login.status()}`);
  await page.waitForURL(new RegExp(`${prefix}${path.replace(/[/?.]/g, '\\$&')}`), { timeout: 60000 });
}
const STATEMENT_URL = /\/credit\/statement\?/;
const overflow = (p) => p.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
const sql = (text) => execFileSync('docker', ['exec', '-i', 'mechware_postgres', 'psql', '-q', '-v', 'ON_ERROR_STOP=1', '-U', 'mechware', '-d', DB], { input: text, encoding: 'utf8' });

(async () => {
  // ---------- setup through the API
  const buyer = await apiLogin(USERS.buyer);
  const alpha = await apiLogin(USERS.sellerAlpha);
  const beta = await apiLogin(USERS.sellerBeta);
  const bcall = (m, p, b, h) => apiCall(buyer, m, p, b, h);
  const admin = await staffToken(ADMIN);
  const finance = await staffToken(FINANCE);
  const admin2 = await staffToken(ADMIN2);

  const variant = (await (await fetch(`${API}/api/v1/storefront/products/${SLUG}`)).json()).data.variants[0];
  const reg = await bcall('POST', '/api/v1/companies', { legalName: `Credit Mills ${tag} Ltd`, tradeLicenceNo: 'TL-B313', bin: uniqueBin() });
  const companyId = reg.json.data.id;
  await apiCall(admin, 'POST', `/api/v1/admin/companies/${companyId}/activate`);
  const me = (await bcall('GET', '/api/v1/users/me')).json.data.id;
  const own = (await bcall('GET', `/api/v1/companies/${companyId}/members`)).json.data.find((x) => x.userId === me);
  await bcall('PUT', `/api/v1/companies/${companyId}/members/${own.id}`, { roles: 10, spendLimitMinor: null, costCentreIds: [], isActive: true });
  const site = await bcall('POST', `/api/v1/companies/${companyId}/sites`, { name: 'Gazipur Plant', contactName: 'Sabbir', contactPhone: '01711000000', line1: 'Plot 5, BSCIC Area', upazila: 'Tongi Industrial Zone', district: 'Gazipur', division: 'Dhaka', isDefaultDelivery: true });
  const LIMIT = 50_000_000; // BDT 500,000.00
  const credit = await staffCall(admin, ADMIN, 'PUT', `/api/v1/admin/companies/${companyId}/credit`, { terms: 30, limitMinorUnits: LIMIT });
  r.check('setup: an active company with Net 30 credit set by an admin (real step-up code)', reg.status === 201 && site.status === 201 && credit.status === 200, `${reg.status}/${site.status}/${credit.status}`);

  // Requester + Approver (roles 1 | 4): may not see the balance. Another person's company is the outsider's.
  const alphaId = (await apiCall(alpha, 'GET', '/api/v1/users/me')).json.data.id;
  const added = await bcall('POST', `/api/v1/companies/${companyId}/members`, { userId: alphaId, roles: 5, spendLimitMinor: null, costCentreIds: [] });
  const accepted = await apiCall(alpha, 'POST', `/api/v1/companies/${companyId}/invitations/accept`);
  let other = await apiCall(beta, 'POST', '/api/v1/companies', { legalName: `Other Mills ${tag} Ltd`, tradeLicenceNo: 'TL-B313X', bin: uniqueBin() });
  // A person may own one company at a time on a reused scratch DB: then the one they already have is "the other company".
  if (other.status === 409) other = { status: 201, json: { data: (await apiCall(beta, 'GET', '/api/v1/companies')).json.data[0].company } };
  const otherId = other.json.data?.id;
  r.check('setup: a Requester+Approver member (accepted) and another company with another owner', added.status === 201 && accepted.status === 200 && other.status === 201, `${added.status}/${accepted.status}/${other.status}`);

  // Two credit orders: the first is paid in part, the second is made overdue.
  let seq = 0;
  const place = async (qty) => {
    const res = await fetch(`${API}/api/v1/orders`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', authorization: `Bearer ${buyer}`, 'idempotency-key': `e2e-b313-${tag}-${++seq}` },
      body: JSON.stringify({ lines: [{ variantId: variant.id, quantity: qty }], companyId, shipToSiteId: site.json.data.id, payOnCredit: true, purchaseOrderNumber: `PO-B313-${tag}-${seq}` })
    });
    const j = await res.json();
    if (!j.data) throw new Error(`placing an order failed: ${res.status} ${JSON.stringify(j).slice(0, 300)}`);
    return (await bcall('GET', `/api/v1/orders/${j.data.orderId}`)).json.data;
  };
  const o1 = await place(2);
  const o2 = await place(1);
  r.check('setup: two credit orders placed and charged to credit (order credit is open)', o1.credit?.status === 'open' && o2.credit?.status === 'open', `${o1.status}/${o1.credit?.status}/${o2.status}/${o2.credit?.status}`);
  const G1 = o1.grandTotalMinor;
  const G2 = o2.grandTotalMinor;

  // A payment recorded by Finance and verified by another person (real step-up code); a second one left waiting.
  const today = dhakaToday();
  const PAY1 = 100_000;
  const PAY2 = 50_000;
  const rec1 = await staffCall(finance, FINANCE, 'POST', `/api/v1/admin/companies/${companyId}/credit/payments`, { method: 'beftn', amountMinor: PAY1, receivedOn: today, reference: `BEFTN${tag}1` }, { 'idempotency-key': `e2e-b313-p1-${tag}` }, false);
  const ver = rec1.status === 201 ? await staffCall(admin2, ADMIN2, 'POST', `/api/v1/admin/credit/payments/${rec1.json.data.id}/verify`, { clearedOn: today }) : rec1;
  const rec2 = await staffCall(finance, FINANCE, 'POST', `/api/v1/admin/companies/${companyId}/credit/payments`, { method: 'bkash', amountMinor: PAY2, receivedOn: today, reference: `BKASH${tag}2` }, { 'idempotency-key': `e2e-b313-p2-${tag}` }, false);
  r.check('setup: one payment recorded and verified by a second person, one left waiting', rec1.status === 201 && ver.status === 200 && rec2.status === 201, `${rec1.status}/${ver.status}/${rec2.status} ${ver.status === 200 ? '' : JSON.stringify(ver.json).slice(0, 200)}`);

  // Make order 2 overdue (scratch DB only): its fixed due date moves 20 days back.
  const overdueDue = addDays(today, -20);
  sql(`BEGIN; SET LOCAL session_replication_role = replica; UPDATE credit_ledger_entries SET due_on = '${overdueDue}' WHERE kind = 'charge' AND order_id = '${o2.id}'; COMMIT;`);
  const api = await bcall('GET', `/api/v1/companies/${companyId}/credit`);
  const A = api.json.data;
  const USED = G1 + G2 - PAY1;
  r.check('setup: the API reports the expected position (used, overdue, on-account, pending)',
    A?.usedMinor === USED && A.limitMinor === LIMIT && A.availableMinor === LIMIT - USED && A.overdueMinor === G2 && A.onAccountMinor === 0 && A.pendingPayments?.length === 1,
    JSON.stringify(A ?? api.json).slice(0, 300));
  console.log(`INFO  onHold=${A?.onHold} oldestDueOn=${A?.oldestDueOn} G1=${G1} G2=${G2}`);

  const browser = await launch();
  const newPage = async (width, prefix = '') => {
    const ctx = await browser.newContext({ viewport: { width, height: width < 600 ? 740 : 900 }, locale: prefix ? 'bn-BD' : 'en-US' });
    const page = await ctx.newPage();
    page.errors = [];
    page.creditCalls = [];
    page.expectNoise = false;
    page.on('console', (m) => { if (m.type() === 'error' && !page.expectNoise) page.errors.push(`console: ${m.text()}`); });
    page.on('pageerror', (e) => page.errors.push(`pageerror: ${e.message}`));
    page.on('response', (res) => {
      if (process.env.E2E_DEBUG && res.url().includes('/api/v1/')) console.log('  api', res.status(), res.request().method(), res.url().replace(/^https?:\/\/[^/]+/, ''));
      if (/\/companies\/[^/]+\/credit/.test(res.url())) page.creditCalls.push({ status: res.status(), url: res.url(), cc: res.headers()['cache-control'] ?? '' });
    });
    return page;
  };
  const creditUrl = (id, prefix = '') => `${prefix}/account/company/${id}/credit`;
  const en = MSG.en.CompanyCredit;

  // ---------- Admin / Buyer (English, desktop)
  {
    const p = await newPage(1280);
    await signIn(p, '', USERS.buyer, '/');
    await p.goto(`${WEB}/account/company/${companyId}`, { waitUntil: 'networkidle' });
    const link = p.getByRole('link', { name: MSG.en.Company.credit.openPanel });
    await link.waitFor();
    r.check('dashboard: the credit card shows the balance and a link to the credit account', (await p.locator('main').innerText()).includes('5,00,000'));
    await link.click();
    await p.waitForURL(new RegExp(`/account/company/${companyId}/credit$`));
    await p.getByTestId('credit-limit').waitFor();
    r.check('credit page: limit, used and available are the API figures', (await p.getByTestId('credit-limit').innerText()) === money(LIMIT) && (await p.getByTestId('credit-used').innerText()) === money(USED) && (await p.getByTestId('credit-available').innerText()) === money(LIMIT - USED),
      `${await p.getByTestId('credit-limit').innerText()} / ${await p.getByTestId('credit-used').innerText()} / ${await p.getByTestId('credit-available').innerText()}`);
    r.check('credit page: overdue and on-account figures', (await p.getByTestId('credit-overdue').innerText()) === money(G2) && (await p.getByTestId('credit-on-account').innerText()) === money(0));
    r.check('credit page: the hold banner matches the API (onHold)', (await p.getByTestId('credit-hold').count()) === (A.onHold ? 1 : 0), `onHold=${A.onHold}`);
    r.check('credit page: aging shows the overdue amount in the 1 to 30 days bucket', (await p.locator('#credit-aging').count()) === 1 && (await p.locator('section[aria-labelledby=credit-aging]').innerText()).includes(money(A.aging.days1To30)));

    // pending payment
    const pend = p.getByTestId('credit-pending');
    r.check('pending payments: the unverified payment is listed with its method, amount and the end of the reference only',
      (await pend.count()) === 1 && (await pend.innerText()).includes(money(PAY2)) && (await pend.innerText()).includes('bKash') && (await pend.innerText()).includes('****') && !(await pend.innerText()).includes(`BKASH${tag}2`), (await pend.innerText()).replace(/\n/g, ' | '));

    // open items: oldest due first (the overdue order), then the partly paid one
    await p.getByTestId('open-item').first().waitFor();
    const rows = p.getByTestId('open-item');
    const t0 = await rows.nth(0).innerText();
    const t1 = await rows.nth(1).innerText();
    r.check('open items: two orders, the overdue one first with its days overdue and amounts', (await rows.count()) === 2 && t0.includes(o2.orderNumber) && t0.includes('Overdue') && /20 days overdue/.test(t0) && t0.includes(money(G2)), t0.replace(/\n/g, ' | '));
    r.check('open items: the partly paid order shows what is still owed', t1.includes(o1.orderNumber) && t1.includes('Partly paid') && t1.includes(money(G1 - PAY1)) && t1.includes(money(G1)), t1.replace(/\n/g, ' | '));

    // statement
    await p.getByTestId('statement-line').first().waitFor();
    const lines = p.getByTestId('statement-line');
    const stText = await p.getByTestId('credit-statement').innerText();
    r.check('statement: charges and the payment, newest rules of the API, kinds in words', (await lines.count()) === 3 && stText.includes('Order charged to credit') && stText.includes('Payment applied to an order') && !/payment_applied|charge_reduced/.test(stText), `${await lines.count()} lines`);
    r.check('statement: current totals from the same answer', (await p.getByTestId('statement-owed').innerText()) === money(USED) && (await p.getByTestId('statement-on-account').innerText()) === money(0));
    r.check('statement: no internal field leaks (no staff note/reason/ids, only the end of a reference)', !stText.includes(`BEFTN${tag}1`) && !/[0-9a-f]{8}-[0-9a-f]{4}-/.test(stText));

    // filter: bad range, then today only, then a window with nothing in it, then clear
    await p.getByLabel(en.statement.from).fill(addDays(today, 2));
    await p.getByLabel(en.statement.to).fill(addDays(today, 1));
    await p.getByRole('button', { name: en.statement.apply }).click();
    await p.getByText(en.statement.order).waitFor();
    r.check('statement filter: From after To is refused in words, the list is kept', (await lines.count()) === 3);
    await p.getByLabel(en.statement.from).fill(addDays(today, 1));
    await p.getByLabel(en.statement.to).fill(addDays(today, 3));
    await p.getByRole('button', { name: en.statement.apply }).click();
    await p.getByTestId('statement-empty').waitFor();
    r.check('statement filter: a window with no entries says so, totals still shown', (await p.getByTestId('statement-owed').innerText()) === money(USED));
    await p.getByLabel(en.statement.from).fill(today);
    await p.getByLabel(en.statement.to).fill(today);
    await p.getByRole('button', { name: en.statement.apply }).click();
    await p.getByTestId('statement-line').first().waitFor();
    r.check('statement filter: today to today shows today\'s entries', (await lines.count()) === 3);
    await p.getByRole('button', { name: en.statement.clear }).click();
    await p.getByTestId('statement-line').first().waitFor();
    r.check('statement filter: clearing the dates empties the boxes', (await p.getByLabel(en.statement.from).inputValue()) === '' && (await p.getByLabel(en.statement.to).inputValue()) === '');
    await p.screenshot({ path: `${process.env.E2E_SHOTS_DIR ?? 'shots'}/credit-en-1280.png`, fullPage: true });
    await axeCheck(p, 'credit page (EN, 1280, admin)');

    // paging: the same real API, asked for one entry per page
    await p.route(STATEMENT_URL, (route) => route.continue({ url: route.request().url().replace(/pageSize=\d+/, 'pageSize=1') }));
    await p.reload({ waitUntil: 'networkidle' });
    await p.getByTestId('statement-line').first().waitFor();
    const first = await p.getByTestId('statement-line').first().innerText();
    const firstText = await p.getByTestId('statement-line').first().textContent();
    await p.getByRole('navigation', { name: en.statement.pagesLabel }).getByRole('button', { name: en.pager.next }).click();
    await p.getByText(MSG.en.CompanyCredit.pager.pageOf.replace('{page}', '2').replace('{total}', '3')).waitFor();
    await p.waitForFunction((was) => document.querySelector('[data-testid=statement-line]')?.textContent !== was && document.querySelector('[data-testid=credit-statement]')?.getAttribute('aria-busy') === 'false', await p.getByTestId('statement-line').first().textContent());
    const second = await p.getByTestId('statement-line').first().innerText();
    r.check('statement paging: next shows the next entry; "Page 2 of 3"; previous comes back', first !== second && (await p.getByTestId('statement-line').count()) === 1, `${first.replace(/\n/g, ' | ')} // ${second.replace(/\n/g, ' | ')}`);
    await p.getByRole('navigation', { name: en.statement.pagesLabel }).getByRole('button', { name: en.pager.prev }).click();
    await p.getByText(MSG.en.CompanyCredit.pager.pageOf.replace('{page}', '1').replace('{total}', '3')).waitFor();
    await p.waitForFunction((want) => document.querySelector('[data-testid=statement-line]')?.textContent === want, firstText);
    r.check('statement paging: previous returns to the first entry', (await p.getByTestId('statement-line').first().innerText()) === first, (await p.getByTestId('statement-line').first().innerText()).replace(/\n/g, ' | '));
    await p.unroute(STATEMENT_URL);

    // every credit answer is private no-store, never a shared cache
    const calls = p.creditCalls;
    r.check('every credit answer carries Cache-Control: private, no-store', calls.length >= 3 && calls.every((c) => /no-store/.test(c.cc) && /private/.test(c.cc)), JSON.stringify(calls.slice(0, 3)));

    // order detail: credit status
    await p.goto(`${WEB}/account/orders/${encodeURIComponent(o1.orderNumber)}`, { waitUntil: 'networkidle' });
    await p.getByTestId('order-credit').waitFor();
    r.check('order detail: partly paid with due date and what is still owed',
      (await p.getByTestId('order-credit-status').innerText()) === MSG.en.Orders.credit.partially_paid && (await p.getByTestId('order-credit-owed').innerText()).includes(money(G1 - PAY1)) && (await p.getByTestId('order-credit-due').innerText()).length > 8,
      (await p.getByTestId('order-credit').innerText()).replace(/\n/g, ' | '));
    await axeCheck(p, 'order detail with credit (EN)');
    await p.goto(`${WEB}/account/orders/${encodeURIComponent(o2.orderNumber)}`, { waitUntil: 'networkidle' });
    await p.getByTestId('order-credit').waitFor();
    r.check('order detail: the overdue order says Overdue', (await p.getByTestId('order-credit-status').innerText()) === MSG.en.Orders.credit.overdue);
    r.check('credit admin: no console or page errors', p.errors.length === 0, p.errors.join(' | ').slice(0, 300));
    await p.context().close();
  }

  // ---------- Admin on a phone, Bangla
  {
    const p = await newPage(360, '/bn');
    await signIn(p, '/bn', USERS.buyer, '/');
    await p.goto(`${WEB}${creditUrl(companyId, '/bn')}`, { waitUntil: 'networkidle' });
    await p.getByTestId('credit-limit').waitFor();
    const bn = MSG.bn.CompanyCredit;
    r.check('Bangla: the balance heading and the figures in Bangla digits', (await p.getByRole('heading', { name: bn.balance.heading }).count()) === 1 && (await p.getByTestId('credit-limit').innerText()) === money(LIMIT, 'bn'), await p.getByTestId('credit-limit').innerText());
    await p.getByTestId('open-item').first().waitFor();
    await p.getByTestId('statement-line').first().waitFor();
    const text = await p.locator('main').innerText();
    r.check('Bangla: kinds and statuses are translated, no English wire text', text.includes(bn.kind.charge) && text.includes(bn.status.overdue) && !/payment_applied|partially_paid|Order charged/.test(text));
    r.check('Bangla: no horizontal scroll at 360 px', !(await overflow(p)));
    await p.screenshot({ path: `${process.env.E2E_SHOTS_DIR ?? 'shots'}/credit-bn-360.png`, fullPage: true });
    await axeCheck(p, 'credit page (BN, 360, admin)');
    await p.goto(`${WEB}/bn/account/orders/${encodeURIComponent(o1.orderNumber)}`, { waitUntil: 'networkidle' });
    await p.getByTestId('order-credit').waitFor();
    r.check('Bangla: order credit status in Bangla', (await p.getByTestId('order-credit-status').innerText()) === MSG.bn.Orders.credit.partially_paid);
    r.check('Bangla phone: no console or page errors', p.errors.length === 0, p.errors.join(' | ').slice(0, 300));
    await p.context().close();
  }

  // ---------- Requester + Approver: no balance, no scare
  {
    const p = await newPage(1280);
    await signIn(p, '', USERS.sellerAlpha, '/');
    await p.goto(`${WEB}/account/company/${companyId}`, { waitUntil: 'networkidle' });
    await p.getByTestId('credit-hidden-note').waitFor();
    const dash = await p.locator('main').innerText();
    r.check('Requester dashboard: the neutral "managed by your company admin" note, no figures, no link', dash.includes(MSG.en.Company.credit.managedByAdmin) && !dash.includes('৳') && (await p.getByRole('link', { name: MSG.en.Company.credit.openPanel }).count()) === 0);
    p.expectNoise = true; // the API's 404 shows up as a network log line
    await p.goto(`${WEB}${creditUrl(companyId)}`, { waitUntil: 'networkidle' });
    await p.getByTestId('credit-hidden').waitFor();
    const txt = await p.locator('main').innerText();
    r.check('Requester credit page: neutral message only; no balance, no error alert, no not-found text', txt.includes(en.hiddenTitle) && !txt.includes('৳') && !txt.includes(en.notFoundBody) && (await p.locator('main [role=alert]').count()) === 0, txt.replace(/\n/g, ' | ').slice(0, 200));
    p.expectNoise = false;
    await axeCheck(p, 'credit page (Requester)');
    // the API says the same (404, no-store) and the company read carries null credit
    const direct = await apiCall(alpha, 'GET', `/api/v1/companies/${companyId}/credit`);
    const comp = await apiCall(alpha, 'GET', `/api/v1/companies/${companyId}`);
    r.check('Requester API: /credit is 404, and the company read has null credit amounts', direct.status === 404 && comp.status === 200 && comp.json.data.creditLimit === null && comp.json.data.creditUsed === null && comp.json.data.creditAvailable === null, `${direct.status}/${comp.status}`);
    r.check('Requester: no page errors', p.errors.length === 0, p.errors.join(' | ').slice(0, 300));
    await p.context().close();
  }

  // ---------- another company's id: not found (and nothing of the company)
  {
    const p = await newPage(1280);
    await signIn(p, '', USERS.sellerBeta, '/');
    p.expectNoise = true;
    await p.goto(`${WEB}${creditUrl(companyId)}`, { waitUntil: 'networkidle' });
    await p.getByTestId('credit-not-found').waitFor();
    const body = await p.locator('body').innerText();
    r.check('outsider: another company\'s credit page is "not found" with none of its data', body.includes(en.notFoundBody) && !body.includes(`Credit Mills ${tag}`) && !body.includes('৳') && !body.includes(o1.orderNumber));
    await axeCheck(p, 'credit page not found');
    await p.goto(`${WEB}${creditUrl('00000000-0000-4000-8000-0000000000aa')}`, { waitUntil: 'networkidle' });
    await p.getByTestId('credit-not-found').waitFor();
    r.check('outsider: an unknown company id looks the same', true);
    p.expectNoise = false;
    await p.context().close();

    const q = await newPage(1280);
    await signIn(q, '', USERS.buyer, '/');
    q.expectNoise = true;
    await q.goto(`${WEB}${creditUrl(otherId)}`, { waitUntil: 'networkidle' });
    await q.getByTestId('credit-not-found').waitFor();
    r.check('the buyer (admin of one company) gets "not found" for the other company\'s credit', true);
    q.expectNoise = false;
    await q.context().close();
  }

  // ---------- signed out
  {
    const ctx = await browser.newContext();
    const p = await ctx.newPage();
    await p.goto(`${WEB}${creditUrl(companyId)}`, { timeout: 120000 });
    await p.waitForURL(/auth\/login/);
    r.check('signed out: the credit page sends the visitor to sign in with returnTo', /returnTo=%2Faccount%2Fcompany%2F/.test(p.url()));
    await ctx.close();
  }

  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error('E2E crashed:', e.stack?.split('\n').slice(0, 8).join('\n') ?? e); process.exit(2); });
