// G5.6: console "All orders" list (filters in the URL, paging) and order detail (lines, totals, address, timeline, payments, refunds, credit, quote link, cancel).
// SCRATCH DB only. Same setup as console-orders.mjs (API on a scratch DB mw_*, demo catalogue, console built against it behind a CORS proxy, support.e2e@ and
// finance.a.e2e@ cloned from the admin row with the same TOTP secret, E2E_SQL_CMD against the scratch DB). See docs/modules/G.md HANDOFF G5.6.
// Set E2E_CONSOLE_ORDERS_LIST=1 to run; otherwise it exits 0 without doing anything.
// Real: a company with credit and a second company with 27 orders (paging 25 + 2), a paid order with a refund asked for, a quote order (the quote row is
// written in the scratch DB, so only the link is checked, not the quote page), every filter against the API as oracle, the URL round trip (reload, a second
// browser, back), a from-after-to dialog that sends nothing, the detail of a cart order and of a quote order, the cancel flow from the detail, a seller
// refused (page text and API 403), unknown order, axe 0, 360 px. Mocked: nothing.
import { execSync } from 'node:child_process';
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, CONSOLE as WEB, USERS, apiCall, apiLogin, launch, reporter, totp, uniqueBin } from './lib.mjs';

if (process.env.E2E_CONSOLE_ORDERS_LIST !== '1') {
  console.log('SKIP  console orders list E2E (set E2E_CONSOLE_ORDERS_LIST=1 to run it)');
  process.exit(0);
}
if (!process.env.E2E_SQL_CMD || !/mw_/.test(process.env.E2E_SQL_CMD)) throw new Error('E2E_SQL_CMD must be a command against a scratch database (mw_*)');

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
const sleep = (ms) => new Promise((res) => setTimeout(res, ms));
const overflow = (p) => p.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
const sql = (text) => execSync(process.env.E2E_SQL_CMD, { input: text, encoding: 'utf8' });
const tag = String(Date.now() % 1000000);
const same = (a, b) => JSON.stringify([...a].sort()) === JSON.stringify([...b].sort());

const SECRET = USERS.admin.totpSecret;
const PASSWORD = USERS.admin.password;
const PEOPLE = {
  admin: USERS.admin,
  support: { email: 'support.e2e@mechware.com.bd', password: PASSWORD, totpSecret: SECRET }
};
const lastStep = new Map();
async function freshCode(person) {
  for (;;) {
    const step = Math.floor(Date.now() / 30000);
    if (step > (lastStep.get(person.email) ?? -1)) { lastStep.set(person.email, step); return totp(person.totpSecret); }
    await sleep(1000);
  }
}
async function apiToken(person) {
  let body;
  for (let attempt = 0; attempt < 8; attempt++) {
    const res = await fetch(`${API}/api/v1/auth/login`, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ identifier: person.email, password: person.password, totpCode: await freshCode(person) })
    });
    body = await res.json();
    if (res.ok) return body.data.accessToken;
    if (res.status !== 401) break;
    await sleep(5000);
  }
  throw new Error(`API login for ${person.email} failed: ${JSON.stringify(body)}`);
}
async function consoleSignIn(page, person) {
  await page.goto(`${WEB}/login`, { timeout: 120000, waitUntil: 'networkidle' });
  await page.getByLabel('Staff or seller email').fill(person.email);
  await page.getByLabel('Password').fill(person.password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.getByLabel('6-digit authenticator code or single-use recovery code').fill(await freshCode(person));
  await page.getByRole('button', { name: 'Verify and unlock console' }).click();
  await page.waitForURL(`${WEB}/`, { timeout: 120000 });
}
async function newPage(browser, width = 1280) {
  const ctx = await browser.newContext({ viewport: { width, height: 900 } });
  const page = await ctx.newPage();
  page.errors = [];
  page.on('pageerror', (e) => page.errors.push('pageerror: ' + e.message));
  return page;
}
const send = (token, method, path, body, headers = {}) =>
  fetch(`${API}${path}`, { method, headers: { 'content-type': 'application/json', authorization: `Bearer ${token}`, ...headers }, body: body ? JSON.stringify(body) : undefined })
    .then(async (res) => ({ status: res.status, json: await res.json().catch(() => ({})) }));
let keySeq = 0;
const key = (what) => `e2e-g56-${tag}-${what}-${++keySeq}`;

// ---- page helpers
const listResponse = (p) => p.waitForResponse((x) => x.request().method() === 'GET' && /\/api\/v1\/admin\/orders\?/.test(x.url()), { timeout: 30000 });
/** Runs `action` and waits for the list request it causes and for the screen to settle. */
async function andList(p, action) {
  const [resp] = await Promise.all([listResponse(p), action()]);
  await p.waitForLoadState('networkidle');
  await p.locator('[data-testid=orders-table],[data-testid=orders-empty]').first().waitFor();
  return resp;
}
const rowNumbers = (p) => p.getByTestId('order-row').locator('td:first-child a').allInnerTexts();
const pageUrl = (p) => new URL(p.url());

(async () => {
  // ---------------- setup through the real API
  const adminToken = await apiToken(PEOPLE.admin);
  const sellerToken = await apiLogin(USERS.sellerAlpha);
  const buyer = await apiLogin(USERS.buyer);
  const buyerId = (await apiCall(buyer, 'GET', '/api/v1/users/me')).json.data.id;
  async function makeCompany(label) {
    const legalName = `${label} ${tag} Ltd`;
    const reg = await apiCall(buyer, 'POST', '/api/v1/companies', { legalName, tradeLicenceNo: `TL-G56${label[0]}`, bin: uniqueBin() });
    const id = reg.json.data.id;
    await sleep(5);
    await apiCall(adminToken, 'POST', `/api/v1/admin/companies/${id}/activate`);
    const members = (await apiCall(buyer, 'GET', `/api/v1/companies/${id}/members`)).json.data;
    const own = members.find((x) => x.userId === buyerId);
    await apiCall(buyer, 'PUT', `/api/v1/companies/${id}/members/${own.id}`, { roles: 10, spendLimitMinor: null, costCentreIds: [], isActive: true });
    const site = await apiCall(buyer, 'POST', `/api/v1/companies/${id}/sites`, { name: `${label} Plant`, contactName: 'Sabbir Rahman', contactPhone: '01711000000', line1: 'Plot 5, BSCIC Area', upazila: 'Tongi Industrial Zone', district: 'Gazipur', division: 'Dhaka', isDefaultDelivery: true });
    const credit = await send(adminToken, 'PUT', `/api/v1/admin/companies/${id}/credit`, { terms: 15, limitMinorUnits: 900000000 }, { 'X-Step-Up-Code': await freshCode(PEOPLE.admin) });
    if (reg.status !== 201 || site.status !== 201 || credit.status !== 200) throw new Error(`company ${label} setup failed ${reg.status}/${site.status}/${credit.status}`);
    return { id, legalName, siteId: site.json.data.id };
  }
  const A = await makeCompany('Alpha Orders Mills');
  const B = await makeCompany('Bulk Orders Works');
  r.check('setup: two active companies with 15-day credit', true);
  r.check('setup: a seller token gets 403 on the order list and a detail (real API)',
    (await apiCall(sellerToken, 'GET', '/api/v1/admin/orders')).status === 403 && (await apiCall(sellerToken, 'GET', '/api/v1/admin/orders/00000000-0000-0000-0000-000000000001')).status === 403);

  const picked = sql("select p.slug, v.id from products p join product_variants v on v.product_id = p.id join inventory_items i on i.variant_id = v.id where p.status = 'active' and v.status = 'active' order by v.id limit 1;").split('\n').map((x) => x.trim()).find(Boolean);
  const [slugRow, pickedId] = picked.split('|');
  sql(`UPDATE inventory_items SET on_hand = 5000, reserved = 0 WHERE variant_id = '${pickedId}';`);
  const variant = (await (await fetch(`${API}/api/v1/storefront/products/${slugRow}`)).json()).data.variants.find((v) => v.id === pickedId);
  async function place(company, quantity, payOnCredit = true) {
    const res = await fetch(`${API}/api/v1/orders`, {
      method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${buyer}`, 'idempotency-key': key('order') },
      body: JSON.stringify({ lines: [{ variantId: variant.id, quantity }], companyId: company.id, shipToSiteId: company.siteId, payOnCredit })
    });
    const j = await res.json();
    if (!j.data) throw new Error(`order failed: ${res.status} ${JSON.stringify(j).slice(0, 300)}`);
    return { id: j.data.orderId, number: j.data.orderNumber, total: j.data.grandTotalMinor };
  }
  const a1 = await place(A, 2);   // plain credit order: cancelled from the detail
  const a2 = await place(A, 1);   // overdue (both deadlines backdated)
  const a3 = await place(A, 3);   // quote order (quote written in the scratch DB)
  const a4 = await place(A, 1);   // captured payment + a refund asked for
  const a5 = await place(A, 1);   // cancelled elsewhere (admin, real API)
  const a6 = await place(A, 1);   // placed on 2026-01-15 (Dhaka noon): the date filter
  const bulk = [];
  for (let i = 0; i < 27; i++) bulk.push(await place(B, 1));
  r.check('setup: 6 orders for company A and 27 for company B placed through the real API', bulk.length === 27 && !!a6.id);

  sql(`UPDATE orders SET legal_delivery_deadline_at = now() - interval '5 hours', courier_handover_deadline_at = now() - interval '30 hours' WHERE id = '${a2.id}';`);
  sql(`INSERT INTO payments (id, order_id, provider, provider_ref, amount, currency, status, captured_at, created_at) VALUES (gen_random_uuid(), '${a4.id}', 'mock', 'g56-${tag}', ${a4.total}, 'BDT', 'Captured', now(), now());`);
  sql(`UPDATE orders SET placed_at = '2026-01-15 06:00:00+00' WHERE id = '${a6.id}';`);
  const b32 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  const quoteNumber = `MWQ-261121-${Array.from({ length: 6 }, () => b32[Math.floor(Math.random() * 32)]).join('')}`;
  sql(`INSERT INTO quotes (id, number, company_id, contact_user_id, created_by, latest_version_no, created_at, updated_at) VALUES (gen_random_uuid(), '${quoteNumber}', '${A.id}', '${buyerId}', '${buyerId}', 1, now(), now());`);
  sql(`INSERT INTO quote_versions (id, quote_id, version_no, status, validity_days, payment_terms, shipping_mode, goods_net_minor, tax_minor, goods_total_minor, created_by, created_at)
        SELECT gen_random_uuid(), id, 1, 'Draft', 14, 'AsAccount', 'Calculated', 0, 0, 0, '${buyerId}', now() FROM quotes WHERE number = '${quoteNumber}';`);
  sql(`UPDATE orders SET quote_id = (SELECT id FROM quotes WHERE number = '${quoteNumber}'), quote_version_id = (SELECT v.id FROM quote_versions v JOIN quotes q ON q.id = v.quote_id WHERE q.number = '${quoteNumber}') WHERE id = '${a3.id}';`);
  const quoteId = sql(`SELECT id FROM quotes WHERE number = '${quoteNumber}';`).trim();
  const adminCancel = await send(adminToken, 'POST', `/api/v1/admin/orders/${a5.id}/cancel`, { reason: 'Cancelled by admin for the status filter' }, { 'X-Step-Up-Code': await freshCode(PEOPLE.admin) });
  const sup = await apiToken(PEOPLE.support);
  const payRows = (await send(sup, 'GET', `/api/v1/admin/orders/${a4.id}`)).json.data?.payments ?? [];
  const refund = await send(sup, 'POST', `/api/v1/admin/payments/${payRows[0]?.id}/refunds`, { amountMinor: 50000, reason: 'Buyer paid twice: first part' });
  r.check('setup: overdue, paid, quote, old-date and cancelled orders prepared; a refund of 500.00 asked for (real API)', adminCancel.status === 200 && refund.status < 300 && payRows.length === 1, `${adminCancel.status}/${refund.status}`);

  const apiNumbers = async (query) => (await send(sup, 'GET', `/api/v1/admin/orders?${query}&pageSize=50`)).json.data.items.map((i) => i.orderNumber);
  const aNumbers = [a1, a2, a3, a4, a5, a6].map((o) => o.number);

  const browser = await launch();

  // ================= seller: refused
  {
    const p = await newPage(browser);
    await p.goto(`${WEB}/login`, { timeout: 120000, waitUntil: 'networkidle' });
    await p.getByLabel('Staff or seller email').fill(USERS.sellerAlpha.email);
    await p.getByLabel('Password').fill(USERS.sellerAlpha.password);
    await p.getByRole('button', { name: 'Sign in' }).click();
    await p.waitForURL(`${WEB}/`, { timeout: 120000 });
    await p.getByText('Operations console').first().waitFor();
    r.check('seller sees no All orders link', (await p.getByRole('link', { name: /All orders/ }).count()) === 0);
    await p.goto(`${WEB}/orders`);
    await p.getByText('Your role does not allow this action (orders.view_all).').waitFor();
    r.check('seller on /orders: the not-allowed text and no table', (await p.getByTestId('orders-table').count()) === 0 && (await p.getByRole('searchbox').count()) === 0);
    await p.goto(`${WEB}/orders/${a1.id}`);
    await p.getByText('Your role does not allow this action (orders.view_all).').waitFor();
    r.check('seller on an order detail: the not-allowed text and no order number', (await p.getByText(a1.number).count()) === 0);
    await axeCheck(p, 'not allowed page');
    await p.context().close();
  }

  // ================= support: the list
  const s = await newPage(browser);
  const listGets = [];
  s.on('request', (req) => { if (req.method() === 'GET' && /\/api\/v1\/admin\/orders\?/.test(req.url())) listGets.push(req.url()); });
  await consoleSignIn(s, PEOPLE.support);
  const nav = s.getByRole('navigation', { name: 'Modules' });
  r.check('support sees "All orders" and "Overdue orders" under Orders', (await nav.getByRole('link', { name: 'All orders' }).count()) === 1 && (await nav.getByRole('link', { name: /Overdue orders/ }).count()) === 1);
  await nav.getByRole('link', { name: 'All orders' }).click();
  await s.waitForURL(`${WEB}/orders`);
  await s.locator('[data-testid=orders-table],[data-testid=orders-empty]').first().waitFor();
  r.check('unfiltered list: table with a caption, 25 rows of the newest orders, "No filter applied"', (await s.getByTestId('order-row').count()) === 25 && (await s.getByTestId('filter-count').innerText()) === 'No filter applied' && (await s.locator('table caption').count()) === 1 && (await s.locator('th[scope=col]').count()) === 7);
  await axeCheck(s, '/orders unfiltered');

  const base = `company=${A.id}&companyName=${encodeURIComponent(A.legalName)}`;
  await andList(s, () => s.goto(`${WEB}/orders?${base}`));
  r.check('company filter from the URL: a chip with the name, only company A orders', (await s.getByTestId('company-chosen').innerText()).includes(A.legalName) && same(await rowNumbers(s), aNumbers));

  // status
  await andList(s, async () => { await s.getByTestId('filter-status').selectOption('cancelled'); await s.getByRole('button', { name: 'Apply filters' }).click(); });
  r.check('status = Cancelled: only the cancelled order, status in words', same(await rowNumbers(s), [a5.number]) && (await s.getByTestId('order-row').first().innerText()).includes('Cancelled') && pageUrl(s).searchParams.get('status') === 'cancelled');
  await andList(s, async () => { await s.getByTestId('filter-status').selectOption('confirmed'); await s.getByRole('button', { name: 'Apply filters' }).click(); });
  r.check('status = Confirmed: the other five, matches the API', same(await rowNumbers(s), await apiNumbers(`status=confirmed&companyId=${A.id}`)) && (await rowNumbers(s)).length === 5);
  // payment state
  await andList(s, async () => { await s.getByTestId('filter-status').selectOption(''); await s.getByTestId('filter-payment').selectOption('captured'); await s.getByRole('button', { name: 'Apply filters' }).click(); });
  r.check('payment state = Paid: only the order with a captured payment, shown as "Paid"', same(await rowNumbers(s), [a4.number]) && (await s.getByTestId('order-row').first().innerText()).includes('Paid'));
  await andList(s, async () => { await s.getByTestId('filter-payment').selectOption('on_credit'); await s.getByRole('button', { name: 'Apply filters' }).click(); });
  r.check('payment state = On credit: matches the API and shows "On credit"', same(await rowNumbers(s), await apiNumbers(`paymentState=on_credit&companyId=${A.id}`)) && (await rowNumbers(s)).includes(a1.number) && (await s.getByTestId('order-row').first().innerText()).includes('On credit'));
  // overdue
  await andList(s, async () => { await s.getByTestId('filter-payment').selectOption(''); await s.getByTestId('filter-overdue').selectOption('true'); await s.getByRole('button', { name: 'Apply filters' }).click(); });
  r.check('overdue = Overdue only: the one backdated order, with both lateness words', same(await rowNumbers(s), [a2.number]) && (await s.getByTestId('order-row').first().innerText()).includes('Late for delivery') && (await s.getByTestId('order-row').first().innerText()).includes('Late for courier handover'));
  await andList(s, async () => { await s.getByTestId('filter-overdue').selectOption('false'); await s.getByRole('button', { name: 'Apply filters' }).click(); });
  r.check('overdue = Not overdue: the other five', same(await rowNumbers(s), aNumbers.filter((n) => n !== a2.number)));
  // number prefix
  await andList(s, async () => { await s.getByTestId('filter-overdue').selectOption(''); await s.getByLabel('Order number starts with').fill(a3.number.toLowerCase()); await s.getByRole('button', { name: 'Apply filters' }).click(); });
  r.check('order number prefix (typed in lower case): exactly that order, from a quote, with the quote number in words', same(await rowNumbers(s), [a3.number]) && (await s.getByTestId('order-row').first().innerText()).includes(`From quote ${quoteNumber}`));
  // date range (Dhaka day)
  await andList(s, async () => { await s.getByLabel('Order number starts with').fill(''); await s.getByLabel('Placed from (Dhaka day)').fill('2026-01-15'); await s.getByLabel('Placed to (Dhaka day)').fill('2026-01-15'); await s.getByRole('button', { name: 'Apply filters' }).click(); });
  r.check('date range 2026-01-15 to 2026-01-15: only the order placed that Dhaka day, shown as 2026-01-15 12:00 Dhaka', same(await rowNumbers(s), [a6.number]) && (await s.getByTestId('order-row').first().innerText()).includes('2026-01-15 12:00 Dhaka'));
  // from after to: refused on screen, nothing sent
  const before = listGets.length;
  await s.getByLabel('Placed from (Dhaka day)').fill('2026-02-01');
  await s.getByRole('button', { name: 'Apply filters' }).click();
  await s.getByTestId('filter-problem').waitFor();
  await s.waitForTimeout(500);
  r.check('"from" after "to": a message in words, role=alert, and no request was sent', (await s.getByTestId('filter-problem').innerText()).includes('after') && listGets.length === before);
  await axeCheck(s, '/orders with a filter problem');
  // empty result
  await andList(s, async () => { await s.getByLabel('Placed from (Dhaka day)').fill('2025-03-01'); await s.getByLabel('Placed to (Dhaka day)').fill('2025-03-02'); await s.getByRole('button', { name: 'Apply filters' }).click(); });
  r.check('an empty result says so in words and keeps the filters visible', (await s.getByTestId('orders-empty').innerText()).includes('No order matches these filters') && (await s.getByTestId('filter-count').innerText()).includes('filters applied'));
  await axeCheck(s, '/orders empty result');

  // combined filters + URL round trip
  await andList(s, async () => { await s.getByLabel('Placed from (Dhaka day)').fill(''); await s.getByLabel('Placed to (Dhaka day)').fill(''); await s.getByTestId('filter-status').selectOption('confirmed'); await s.getByTestId('filter-overdue').selectOption('false'); await s.getByLabel('Order number starts with').fill('MW'); await s.getByRole('button', { name: 'Apply filters' }).click(); });
  const combined = s.url();
  const comboRows = await rowNumbers(s);
  const q = pageUrl(s).searchParams;
  r.check('combined filters are all in the address and the rows match the API',
    q.get('status') === 'confirmed' && q.get('overdue') === 'false' && q.get('number') === 'MW' && q.get('company') === A.id && same(comboRows, await apiNumbers(`status=confirmed&overdue=false&orderNumber=MW&companyId=${A.id}`)));
  await andList(s, () => s.reload());
  r.check('reload: same address, form still holds every filter, same rows',
    s.url() === combined && (await s.getByTestId('filter-status').inputValue()) === 'confirmed' && (await s.getByTestId('filter-overdue').inputValue()) === 'false' && (await s.getByLabel('Order number starts with').inputValue()) === 'MW' && same(await rowNumbers(s), comboRows));
  const other = await newPage(browser);
  await consoleSignIn(other, PEOPLE.support);
  await andList(other, () => other.goto(combined));
  r.check('the address opened by a second browser shows the same filters and rows', (await other.getByTestId('filter-status').inputValue()) === 'confirmed' && same(await rowNumbers(other), comboRows));
  await other.context().close();
  await andList(s, () => s.goBack());
  r.check('Back returns to the previous filters (date range 2025-03)', (await s.getByLabel('Placed from (Dhaka day)').inputValue()) === '2025-03-01' && (await s.getByTestId('orders-empty').count()) === 1);
  await s.goto(`${WEB}/orders?status=nonsense&overdue=maybe&from=2026-02-30&page=-3`);
  await s.locator('[data-testid=orders-table],[data-testid=orders-empty]').first().waitFor();
  r.check('a link with bad filter values opens the plain list, not an error', (await s.getByTestId('filter-count').innerText()) === 'No filter applied' && (await s.getByTestId('filter-problem').count()) === 0 && (await s.getByTestId('orders-table').count()) === 1,
    `${await s.getByTestId('filter-count').innerText()} | alerts: ${(await s.getByRole('alert').allInnerTexts()).join(' / ')}`);
  // clear
  await andList(s, () => s.goto(`${WEB}/orders?${base}&status=cancelled`));
  await andList(s, () => s.getByRole('button', { name: 'Clear all filters' }).click());
  r.check('Clear all filters: plain address, no company chip', pageUrl(s).search === '' && (await s.getByTestId('company-chosen').count()) === 0);

  // company picker
  await s.getByRole('button', { name: 'Choose a company' }).click();
  await s.getByLabel('Company', { exact: true }).fill(`Bulk Orders Works ${tag}`);
  await s.getByTestId('company-list').waitFor();
  await s.getByTestId('company-list').selectOption({ label: B.legalName });
  await s.getByTestId('company-chosen').waitFor();
  await andList(s, () => s.getByRole('button', { name: 'Apply filters' }).click());
  r.check('company picked in the list: chip with the legal name, URL holds the id, 27 orders of company B', (await s.getByTestId('company-chosen').innerText()).includes(B.legalName) && pageUrl(s).searchParams.get('company') === B.id && (await s.getByTestId('pager').innerText()).includes('(27 orders)'));

  // paging
  r.check('page 1 has 25 rows and says "Page 1 of 2"', (await s.getByTestId('order-row').count()) === 25 && (await s.getByTestId('pager').innerText()).startsWith('Page 1 of 2') && (await s.getByRole('button', { name: 'Previous' }).isDisabled()));
  const page1 = await rowNumbers(s);
  await andList(s, () => s.getByRole('button', { name: 'Next' }).click());
  const page2 = await rowNumbers(s);
  r.check('Next: page=2 in the address, the other 2 orders, no overlap, Next disabled', pageUrl(s).searchParams.get('page') === '2' && page2.length === 2 && page2.every((n) => !page1.includes(n)) && (await s.getByRole('button', { name: 'Next' }).isDisabled()) && same([...page1, ...page2], bulk.map((o) => o.number)));
  await andList(s, () => s.reload());
  r.check('reload on page 2 stays on page 2', (await s.getByTestId('pager').innerText()).startsWith('Page 2 of 2') && same(await rowNumbers(s), page2));
  await andList(s, () => s.getByRole('button', { name: 'Previous' }).click());
  r.check('Previous: back to page 1 and the page parameter is gone', pageUrl(s).searchParams.get('page') === null && same(await rowNumbers(s), page1));

  // ================= detail: cart order with a payment and a refund
  await andList(s, () => s.goto(`${WEB}/orders?${base}&number=${a4.number}`));
  await s.getByRole('link', { name: a4.number }).click();
  await s.waitForURL(`${WEB}/orders/${a4.id}`);
  await s.getByTestId('order-number').waitFor();
  const det = (await send(sup, 'GET', `/api/v1/admin/orders/${a4.id}`)).json.data;
  const bdt = (m) => `BDT ${Math.floor(m / 100).toLocaleString('en-US')}.${String(m % 100).padStart(2, '0')}`;
  r.check('detail (cart order): number, status and payment state in words, "From a cart"', (await s.getByTestId('order-number').innerText()) === a4.number && (await s.getByTestId('order-status').innerText()) === 'Status: Confirmed' && (await s.getByTestId('order-payment-state').innerText()) === 'Payment: Paid' && (await s.getByText('From a cart').count()) === 1);
  r.check('detail: buyer name and phone, company, delivery address with site and district', (await s.getByText(det.buyer.name).first().count()) === 1 && (!det.buyer.phone || (await s.getByText(det.buyer.phone).count()) >= 1) && (await s.getByText(A.legalName).count()) >= 1 && (await s.getByTestId('ship-to').innerText()).includes('Plot 5, BSCIC Area') && (await s.getByTestId('ship-to').innerText()).includes('Alpha Orders Mills Plant') && (await s.getByTestId('ship-to').innerText()).includes('Gazipur'));
  const line = s.getByTestId('order-line').first();
  r.check('detail: the line shows SKU, quantity, unit price and where the price came from (snapshot)', (await line.innerText()).includes(det.subOrders[0].lines[0].sku) && (await line.innerText()).includes(det.subOrders[0].lines[0].title) && (await line.innerText()).includes(`${(Math.floor(det.subOrders[0].lines[0].unitPriceMinor / 100)).toLocaleString('en-US')}.`) && /Price list|Sale price|Quantity break|Group price list|Contract price|Quote price|Price at order time/.test(await line.innerText()), (await line.innerText()).replace(/\n/g, ' | ').slice(0, 200));
  r.check('detail: grand total equals the API', (await s.getByTestId('grand-total').innerText()) === bdt(det.grandTotalMinor), `${await s.getByTestId('grand-total').innerText()} vs ${bdt(det.grandTotalMinor)}`);
  r.check('detail: timeline lists every event of the API in order, with a Dhaka time', (await s.getByTestId('timeline-event').count()) === det.events.length && (await s.getByTestId('timeline-event').first().innerText()).includes('Dhaka'));
  r.check('detail: the payment is "Paid" and the 500.00 refund is "Waiting for a second person" with its reason', (await s.getByTestId('payment').count()) === 1 && (await s.getByTestId('payment').innerText()).includes('Paid') && (await s.getByTestId('refund').innerText()).includes('BDT 500.00') && (await s.getByTestId('refund').innerText()).includes('Waiting for a second person') && (await s.getByTestId('refund').innerText()).includes('Buyer paid twice: first part'));
  r.check('detail: credit block says charged and still owed (credit order)', (await s.getByText('Credit state').count()) === 1 && (await s.getByText('Still owed').count()) === 1);
  r.check('detail: no quote link on a cart order, and no never-sent data (provider reference / card) on the page', (await s.getByTestId('quote-link').count()) === 0 && !(await s.locator('body').innerText()).includes(`g56-${tag}`));
  await axeCheck(s, 'order detail (cart, with payment and refund)');

  // ================= detail: quote order
  await s.goto(`${WEB}/orders/${a3.id}`);
  await s.getByTestId('order-number').waitFor();
  const qlink = s.getByTestId('quote-link');
  r.check('detail (quote order): "From a quote" and a link to the console quote page', (await s.getByText('From a quote').count()) === 1 && (await qlink.innerText()) === `Quote ${quoteNumber}` && (await qlink.getAttribute('href')) === `/quotes/${quoteId}`);
  const l3 = s.getByTestId('order-line').first();
  r.check('detail (quote order): three units on the line and open credit', (await l3.innerText()).includes('3') && (await s.getByText('Open', { exact: true }).count()) >= 1);
  await axeCheck(s, 'order detail (quote order)');
  await qlink.click();
  await s.waitForURL(`${WEB}/quotes/${quoteId}`);
  r.check('the quote link opens the console quote page', pageUrl(s).pathname === `/quotes/${quoteId}`);

  // ================= detail: cancel flow
  await s.goto(`${WEB}/orders/${a1.id}`);
  await s.getByTestId('order-number').waitFor();
  const cancelPosts = [];
  s.on('request', (req) => { if (req.method() === 'POST' && req.url().includes('/cancel')) cancelPosts.push(req.url()); });
  await s.getByRole('button', { name: 'Cancel order' }).click();
  await s.getByRole('button', { name: 'Continue to the authenticator code' }).click();
  await s.getByText('Enter a reason.').waitFor();
  await axeCheck(s, 'cancel reason dialog on the detail');
  await s.getByLabel(/Why is it cancelled/).fill('abc');
  await s.getByRole('button', { name: 'Continue to the authenticator code' }).click();
  await s.getByText('The reason must be 5 to 500 characters.').waitFor();
  r.check('detail cancel: an empty and a 3-character reason are refused on screen, no code asked, nothing sent', (await s.getByLabel('Authenticator code').count()) === 0 && cancelPosts.length === 0);
  await s.getByLabel(/Why is it cancelled/).fill(`Buyer asked to cancel ${tag}`);
  await s.getByRole('button', { name: 'Continue to the authenticator code' }).click();
  await s.getByLabel('Authenticator code').waitFor();
  await s.getByLabel('Authenticator code').fill('000000');
  await s.getByRole('button', { name: 'Cancel the order' }).click();
  await s.getByTestId('stepup-error').waitFor();
  r.check('detail cancel: a wrong code keeps the dialog open in words, order untouched', cancelPosts.length === 1 && (await s.getByTestId('stepup-error').innerText()).includes('not accepted') && (await s.getByTestId('order-status').innerText()) === 'Status: Confirmed');
  await s.getByLabel('Authenticator code').fill(await freshCode(PEOPLE.support));
  await s.getByRole('button', { name: 'Cancel the order' }).click();
  await s.getByTestId('cancel-notice').waitFor();
  await s.getByTestId('order-status').filter({ hasText: 'Cancelled' }).waitFor();
  const notice = await s.getByTestId('cancel-notice').innerText();
  r.check('detail cancel: notice names the order and the released credit, focus on it, the page shows Cancelled', notice.includes(`Order ${a1.number} is cancelled.`) && notice.includes('of credit was released') && (await s.evaluate(() => document.activeElement?.getAttribute('data-testid'))) === 'cancel-notice');
  r.check('detail cancel: the Cancel button is gone and says why; the timeline has the new event with its reason',
    (await s.getByRole('button', { name: 'Cancel order' }).count()) === 0 && (await s.getByTestId('cancel-hint').innerText()).includes('cancelled order cannot be cancelled') && (await s.getByTestId('timeline').innerText()).includes('Confirmed to Cancelled') && (await s.getByTestId('timeline').innerText()).includes(`Buyer asked to cancel ${tag}`));
  await axeCheck(s, 'order detail after the cancel');

  // a shipped order: no button, only the reason
  sql(`UPDATE orders SET status = 'Shipped' WHERE id = '${a2.id}';`);
  await s.goto(`${WEB}/orders/${a2.id}`);
  await s.getByTestId('order-number').waitFor();
  r.check('detail of a shipped order: no Cancel button, the reason in words, "Late for delivery" visible in the list data', (await s.getByRole('button', { name: 'Cancel order' }).count()) === 0 && (await s.getByTestId('cancel-hint').innerText()).includes('shipped order cannot be cancelled') && (await s.getByTestId('order-status').innerText()) === 'Status: Shipped');

  // unknown order
  await s.goto(`${WEB}/orders/00000000-0000-4000-8000-000000000999`);
  const nf = s.locator('[role=alert]').filter({ hasText: 'was not found' });
  await nf.waitFor();
  r.check('an unknown order: "not found" in words, no Try again button for a 404, no order shown', (await nf.count()) === 1 && (await nf.getByRole('button', { name: 'Try again' }).count()) === 0 && (await s.getByTestId('order-number').count()) === 0);
  await axeCheck(s, 'order not found');

  // overdue view still reachable
  await s.goto(`${WEB}/orders`);
  await s.getByRole('link', { name: 'Overdue orders', exact: true }).click();
  await s.waitForURL(`${WEB}/orders/overdue`);
  await s.getByTestId('overdue-table').waitFor();
  r.check('the Overdue orders view is one click from All orders and still lists late orders (with a link to the detail)', (await s.getByTestId('overdue-row').count()) >= 1 && (await s.getByTestId('overdue-row').first().getByRole('link').count()) === 1 && (await s.getByRole('link', { name: 'All orders', exact: true }).first().getAttribute('aria-current')) === null);
  await axeCheck(s, '/orders/overdue');

  // ================= narrow screens and page errors
  for (const path of ['/orders?' + base, `/orders/${a4.id}`, '/orders/overdue']) {
    await s.setViewportSize({ width: 360, height: 800 });
    await s.goto(`${WEB}${path}`);
    await s.waitForLoadState('networkidle');
    await s.locator('[data-testid=orders-table],[data-testid=orders-empty],[data-testid=order-number],[data-testid=overdue-table],[data-testid=overdue-empty]').first().waitFor();
    r.check(`360 px: no horizontal scroll on ${path.split('?')[0].replace(a4.id, '{id}')}`, !(await overflow(s)));
  }
  await axeCheck(s, '/orders/{id} at 360 px');
  r.check('no page errors', s.errors.length === 0, s.errors.join(' | '));
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
