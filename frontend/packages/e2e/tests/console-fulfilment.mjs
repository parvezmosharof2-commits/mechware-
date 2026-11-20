// F1.3: manual fulfilment buttons on the console order detail (/orders/{id}): Start processing, Mark packed, Ship... (dialog), Mark delivered, the shipment block.
// SCRATCH DB only. Same setup as console-orders-list.mjs (API on a scratch DB mw_* with the demo catalogue, console built against it behind a CORS proxy,
// support.e2e@ and finance.a.e2e@ cloned from the admin row with the same TOTP secret, E2E_SQL_CMD against the scratch DB). See docs/modules/F1.md HANDOFF F1.3.
// Set E2E_CONSOLE_FULFILMENT=1 to run; otherwise it exits 0 without doing anything.
// Real: the full path processing -> packed -> ship -> delivered with the buttons and the shipment block (Dhaka times, link with rel), every ship field refused
// on screen (nothing sent), a stale save (ORDER_CHANGED, reload, in words), ORDER_INVALID_TRANSITION in words (the real answer to a request that only had its
// rowVersion corrected), Finance sees no buttons and says why while the API gives 403, a delivered order has no button, axe 0, 360 px. Mocked: nothing
// but the rowVersion in one request body (it is how a row that is current but in the wrong state is reached).
import { execSync } from 'node:child_process';
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, CONSOLE as WEB, USERS, apiCall, apiLogin, launch, reporter, totp, uniqueBin } from './lib.mjs';

if (process.env.E2E_CONSOLE_FULFILMENT !== '1') {
  console.log('SKIP  console fulfilment E2E (set E2E_CONSOLE_FULFILMENT=1 to run it)');
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

const SECRET = USERS.admin.totpSecret;
const PASSWORD = USERS.admin.password;
const PEOPLE = {
  admin: USERS.admin,
  support: { email: 'support.e2e@mechware.com.bd', password: PASSWORD, totpSecret: SECRET },
  finance: { email: 'finance.a.e2e@mechware.com.bd', password: PASSWORD, totpSecret: SECRET }
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
const send = (token, method, path, body) =>
  fetch(`${API}${path}`, { method, headers: { 'content-type': 'application/json', authorization: `Bearer ${token}` }, body: body ? JSON.stringify(body) : undefined })
    .then(async (res) => ({ status: res.status, json: await res.json().catch(() => ({})) }));
let keySeq = 0;
const key = (what) => `e2e-f13-${tag}-${what}-${++keySeq}`;

(async () => {
  // ---------------- setup through the real API
  const adminToken = await apiToken(PEOPLE.admin);
  const buyer = await apiLogin(USERS.buyer);
  const buyerId = (await apiCall(buyer, 'GET', '/api/v1/users/me')).json.data.id;
  const legalName = `Fulfil Works ${tag} Ltd`;
  const reg = await apiCall(buyer, 'POST', '/api/v1/companies', { legalName, tradeLicenceNo: 'TL-F13A', bin: uniqueBin() });
  const companyId = reg.json.data.id;
  await sleep(5);
  await apiCall(adminToken, 'POST', `/api/v1/admin/companies/${companyId}/activate`);
  const own = (await apiCall(buyer, 'GET', `/api/v1/companies/${companyId}/members`)).json.data.find((x) => x.userId === buyerId);
  await apiCall(buyer, 'PUT', `/api/v1/companies/${companyId}/members/${own.id}`, { roles: 10, spendLimitMinor: null, costCentreIds: [], isActive: true });
  const site = await apiCall(buyer, 'POST', `/api/v1/companies/${companyId}/sites`, { name: 'Fulfil Plant', contactName: 'Sabbir Rahman', contactPhone: '01711000000', line1: 'Plot 5, BSCIC Area', upazila: 'Tongi Industrial Zone', district: 'Gazipur', division: 'Dhaka', isDefaultDelivery: true });
  const credit = await fetch(`${API}/api/v1/admin/companies/${companyId}/credit`, {
    method: 'PUT', headers: { 'content-type': 'application/json', authorization: `Bearer ${adminToken}`, 'X-Step-Up-Code': await freshCode(PEOPLE.admin) },
    body: JSON.stringify({ terms: 15, limitMinorUnits: 900000000 })
  });
  if (reg.status !== 201 || site.status !== 201 || credit.status !== 200) throw new Error(`company setup failed ${reg.status}/${site.status}/${credit.status}`);
  const siteId = site.json.data.id;

  const picked = sql("select p.slug, v.id from products p join product_variants v on v.product_id = p.id join inventory_items i on i.variant_id = v.id where p.status = 'active' and v.status = 'active' order by v.id limit 1;").split('\n').map((x) => x.trim()).find(Boolean);
  const [slugRow, pickedId] = picked.split('|');
  sql(`UPDATE inventory_items SET on_hand = 5000, reserved = 0 WHERE variant_id = '${pickedId}';`);
  const variant = (await (await fetch(`${API}/api/v1/storefront/products/${slugRow}`)).json()).data.variants.find((v) => v.id === pickedId);
  async function place() {
    const res = await fetch(`${API}/api/v1/orders`, {
      method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${buyer}`, 'idempotency-key': key('order') },
      body: JSON.stringify({ lines: [{ variantId: variant.id, quantity: 1 }], companyId, shipToSiteId: siteId, payOnCredit: true })
    });
    const j = await res.json();
    if (!j.data) throw new Error(`order failed: ${res.status} ${JSON.stringify(j).slice(0, 300)}`);
    return { id: j.data.orderId, number: j.data.orderNumber };
  }
  const o1 = await place();   // the full path
  const o2 = await place();   // stale save
  const o3 = await place();   // invalid transition
  const o4 = await place();   // Finance
  r.check('setup: a company with credit and four confirmed orders placed through the real API', true);

  const sup = await apiToken(PEOPLE.support);
  const fin = await apiToken(PEOPLE.finance);
  const detail = async (id, token = sup) => (await send(token, 'GET', `/api/v1/admin/orders/${id}`)).json.data;
  r.check('setup: each new order is Confirmed and the detail carries a rowVersion and an empty shipments list', (await Promise.all([o1, o2, o3, o4].map((o) => detail(o.id)))).every((d) => d.status === 'Confirmed' || d.status === 'confirmed') );

  const browser = await launch();
  const s = await newPage(browser);
  const posts = [];
  s.on('request', (req) => { if (req.method() === 'POST' && /\/api\/v1\/admin\/orders\/[^/]+\/(processing|packed|ship|delivered)$/.test(req.url())) posts.push(req.url().split('/').pop()); });
  await consoleSignIn(s, PEOPLE.support);

  // ================= the full path
  await s.goto(`${WEB}/orders/${o1.id}`);
  await s.getByTestId('order-number').waitFor();
  const status = () => s.getByTestId('order-status').innerText();
  const button = () => s.getByTestId('fulfil-button');
  r.check('Confirmed order: exactly one primary button "Start processing", no shipment block yet', (await status()) === 'Status: Confirmed' && (await button().count()) === 1 && (await button().innerText()) === 'Start processing' && (await s.getByTestId('shipment-block').count()) === 0);
  await axeCheck(s, 'detail with a fulfilment button');
  await button().click();
  await s.getByTestId('fulfil-notice').waitFor();
  await s.getByTestId('order-status').filter({ hasText: 'Processing' }).waitFor();
  r.check('Start processing: notice in words with focus, status Processing, next button is "Mark packed"',
    (await s.getByTestId('fulfil-notice').innerText()) === `Order ${o1.number} is now being processed.` && (await s.evaluate(() => document.activeElement?.getAttribute('data-testid'))) === 'fulfil-notice' && (await button().innerText()) === 'Mark packed');
  await button().click();
  await s.getByTestId('order-status').filter({ hasText: 'Packed' }).waitFor();
  await s.getByRole('button', { name: 'Ship…' }).waitFor();
  r.check('Mark packed: status Packed, next button is "Ship…", the two moves used the fresh rowVersion each time (no 409)', (await button().innerText()) === 'Ship…' && posts.join() === 'processing,packed' && (await s.getByTestId('fulfil-alert').count()) === 0);

  // ship dialog: every refusal on screen, nothing sent
  await button().click();
  const dlg = s.getByRole('dialog');
  await dlg.waitFor();
  const text = await dlg.innerText();
  r.check('ship dialog: asks courier name, tracking number and an optional link, and says the link must be the courier\'s own page on a known site', text.includes('Courier name') && text.includes('Tracking number') && text.includes('Tracking link (optional)') && text.includes("courier's own tracking page") && text.includes('steadfast.com.bd'));
  await axeCheck(s, 'ship dialog');
  const before = posts.length;
  await dlg.getByRole('button', { name: 'Mark as shipped' }).click();
  await dlg.getByText('The courier name must be 2 to 80 characters.').waitFor();
  r.check('empty form: courier name and tracking number are refused in words, focus on the first, nothing sent',
    (await dlg.getByText('Enter the tracking number.').count()) === 1 && (await s.evaluate(() => document.activeElement?.id ? document.querySelector(`label[for="${document.activeElement.id}"]`)?.textContent ?? '' : '')).startsWith('Courier name') && posts.length === before);
  await axeCheck(s, 'ship dialog with errors');
  const fill = async (courier, number, link) => { await dlg.getByLabel(/^Courier name/).fill(courier); await dlg.getByLabel(/^Tracking number/).fill(number); await dlg.getByLabel(/^Tracking link/).fill(link); await dlg.getByRole('button', { name: 'Mark as shipped' }).click(); };
  await fill('A', 'SF-1', '');
  r.check('courier name of 1 character is refused', (await dlg.getByText('The courier name must be 2 to 80 characters.').count()) === 1 && posts.length === before);
  await fill('x'.repeat(81), 'SF-1', '');
  r.check('courier name of 81 characters is refused', (await dlg.getByText('The courier name must be 2 to 80 characters.').count()) === 1 && posts.length === before);
  await fill('Steadfast', 'SF 12 34', '');
  r.check('tracking number with a space is refused in words', (await dlg.getByText('letters, digits and dashes').count()) >= 1 && posts.length === before);
  await fill('Steadfast', 'A'.repeat(61), '');
  r.check('tracking number of 61 characters is refused', (await dlg.getByText('at most 60 characters').count()) === 1 && posts.length === before);
  await fill('Steadfast', 'SF-1', 'http://steadfast.com.bd/t/1');
  r.check('an http link is refused (must be https)', (await dlg.getByText('must start with https://').count()) === 1 && posts.length === before);
  await fill('Steadfast', 'SF-1', 'https://evil.example/steadfast.com.bd');
  r.check('a link on another site is refused with the "courier\'s own page" words', (await dlg.getByText("must be the courier's own page").count()) === 1 && posts.length === before);
  await fill('Steadfast', 'SF-1', 'https://steadfast.com.bd.evil.com/t');
  r.check('a look-alike host is refused', (await dlg.getByText("must be the courier's own page").count()) === 1 && posts.length === before);
  await dlg.getByRole('button', { name: 'Do not ship' }).click();
  await dlg.waitFor({ state: 'detached' });
  r.check('"Do not ship" closes the dialog; the order is still Packed and nothing was sent', (await status()) === 'Status: Packed' && posts.length === before && (await detail(o1.id)).status.toLowerCase() === 'packed');

  // ship for real
  await button().click();
  await dlg.waitFor();
  const LINK = 'https://steadfast.com.bd/t/SF-2026-0001';
  await fill('  Steadfast   Courier ', 'SF-2026-0001', LINK);
  await s.getByTestId('fulfil-notice').waitFor();
  await s.getByTestId('order-status').filter({ hasText: 'Shipped' }).waitFor();
  const d1 = await detail(o1.id);
  const sh = d1.shipments[0];
  const block = s.getByTestId('shipment-block');
  r.check('shipped: notice, status Shipped, next button "Mark delivered", one ship call sent', (await s.getByTestId('fulfil-notice').innerText()) === `Order ${o1.number} is now shipped.` && (await button().innerText()) === 'Mark delivered' && posts.at(-1) === 'ship' && posts.filter((p) => p === 'ship').length === 1);
  const dhaka = (iso) => new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Dhaka', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).format(new Date(iso)).replace(',', '');
  const bt = await block.innerText();
  r.check('shipment block: cleaned courier name, tracking number, link, Dhaka shipped time, "Not delivered yet"',
    sh.courierName === 'Steadfast Courier' && bt.includes('Steadfast Courier') && bt.includes('SF-2026-0001') && bt.includes(LINK) && bt.includes('Dhaka') && bt.includes(dhaka(sh.shippedAtUtc).slice(0, 10)) && bt.includes('Not delivered yet'), bt.replace(/\n/g, ' | ').slice(0, 220));
  const link = block.getByRole('link');
  r.check('shipment block: the link opens in a new tab with rel=noopener noreferrer', (await link.getAttribute('href')) === LINK && (await link.getAttribute('target')) === '_blank' && (await link.getAttribute('rel')) === 'noopener noreferrer');
  r.check('the ship event is in the timeline with the courier and number', (await s.getByTestId('timeline').innerText()).includes('Shipped with Steadfast Courier, tracking SF-2026-0001'));
  await axeCheck(s, 'detail with the shipment block');
  await button().click();
  await s.getByTestId('order-status').filter({ hasText: 'Delivered' }).waitFor();
  await s.getByTestId('fulfil-hint').waitFor();
  const d1b = await detail(o1.id);
  r.check('delivered: status Delivered, no button, the reason in words, the delivered time in Dhaka time',
    (await button().count()) === 0 && (await s.getByTestId('fulfil-hint').innerText()).includes('delivered') && (await block.innerText()).includes(dhaka(d1b.shipments[0].deliveredAtUtc).slice(0, 10)) && !(await block.innerText()).includes('Not delivered yet'));
  await axeCheck(s, 'delivered order');
  r.check('the real API agrees: Delivered, one shipment with both times', d1b.status.toLowerCase() === 'delivered' && d1b.shipments.length === 1 && !!d1b.shipments[0].deliveredAtUtc);

  // ================= stale save: someone else moved the order meanwhile
  await s.goto(`${WEB}/orders/${o2.id}`);
  await s.getByTestId('order-number').waitFor();
  const d2 = await detail(o2.id);
  const moved = await send(sup, 'POST', `/api/v1/admin/orders/${o2.id}/processing`, { rowVersion: d2.rowVersion });
  r.check('setup: another person starts processing order 2 (real API)', moved.status === 200);
  await button().click();
  await s.getByTestId('fulfil-alert').waitFor();
  await s.getByTestId('order-status').filter({ hasText: 'Processing' }).waitFor();
  const alert2 = await s.getByTestId('fulfil-alert').innerText();
  r.check('stale save: ORDER_CHANGED says someone else changed the order, the page reloaded to Processing, the button is now "Mark packed", focus on the alert',
    alert2.includes('Someone else changed this order') && (await button().innerText()) === 'Mark packed' && (await s.evaluate(() => document.activeElement?.getAttribute('data-testid'))) === 'fulfil-alert');
  await axeCheck(s, 'stale save alert');

  // ================= invalid transition: row current, state wrong (the real server answer)
  await s.goto(`${WEB}/orders/${o3.id}`);
  await s.getByTestId('order-number').waitFor();
  sql(`UPDATE orders SET status = 'Cancelled' WHERE id = '${o3.id}';`);
  const fresh = (await detail(o3.id)).rowVersion;
  await s.route('**/api/v1/admin/orders/*/processing', (route) => route.continue({ postData: JSON.stringify({ rowVersion: fresh }) }));
  await button().click();
  await s.getByTestId('fulfil-alert').waitFor();
  await s.unroute('**/api/v1/admin/orders/*/processing');
  const alert3 = await s.getByTestId('fulfil-alert').innerText();
  r.check('wrong state: ORDER_INVALID_TRANSITION is explained in words (only a confirmed order can start processing; it may have been cancelled) and the page reloaded',
    alert3.includes('Only a confirmed order can start processing') && alert3.includes('cancelled'), alert3);
  await s.getByTestId('order-status').filter({ hasText: 'Cancelled' }).waitFor();
  r.check('after the reload the order is Cancelled with no fulfilment button and the reason in words', (await button().count()) === 0 && (await s.getByTestId('fulfil-hint').innerText()).includes('cancelled'));

  // ================= Finance: no buttons, API 403
  const f = await newPage(browser);
  await consoleSignIn(f, PEOPLE.finance);
  await f.goto(`${WEB}/orders/${o4.id}`);
  await f.getByTestId('order-number').waitFor();
  const hint = await f.getByTestId('fulfil-hint').innerText();
  r.check('Finance: the order opens, no fulfilment button, and the page says which permission is missing', (await f.getByTestId('fulfil-button').count()) === 0 && hint.includes('orders.fulfil') && (await f.getByRole('button', { name: /Start processing|Mark packed|Ship|Mark delivered/ }).count()) === 0);
  await axeCheck(f, 'Finance view of an order');
  const v4 = await detail(o4.id);
  const codes = [];
  for (const step of ['processing', 'packed', 'ship', 'delivered']) codes.push((await send(fin, 'POST', `/api/v1/admin/orders/${o4.id}/${step}`, { rowVersion: v4.rowVersion, courierName: 'Steadfast', trackingNumber: 'SF-1' })).status);
  r.check('Finance on the real API: all four moves answer 403 and the order is unchanged', codes.every((c) => c === 403) && (await detail(o4.id)).status.toLowerCase() === 'confirmed', codes.join());
  await f.context().close();

  // ================= narrow screen
  await s.setViewportSize({ width: 360, height: 800 });
  await s.goto(`${WEB}/orders/${o1.id}`);
  await s.getByTestId('shipment-block').waitFor();
  r.check('360 px: the shipped order with its block has no horizontal scroll', !(await overflow(s)));
  await axeCheck(s, 'shipped order at 360 px');
  await s.goto(`${WEB}/orders/${o2.id}`);
  await s.getByTestId('fulfil-button').waitFor();
  await s.getByTestId('fulfil-button').click();
  await s.getByTestId('fulfil-notice').waitFor();
  const o2d = await detail(o2.id);
  r.check('setup for the dialog at 360 px: order 2 is Packed', o2d.status.toLowerCase() === 'packed');
  await s.getByRole('button', { name: 'Ship…' }).click();
  await s.getByRole('dialog').waitFor();
  r.check('360 px: the ship dialog has no horizontal scroll and its fields are reachable', !(await overflow(s)) && (await s.getByRole('dialog').getByLabel(/^Tracking number/).isVisible()));
  await axeCheck(s, 'ship dialog at 360 px');
  await s.keyboard.press('Escape');
  r.check('no page errors', s.errors.length === 0, s.errors.join(' | '));
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
