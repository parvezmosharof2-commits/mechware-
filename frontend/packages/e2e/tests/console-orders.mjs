// G5.1: staff order screens in the console: orders past a legal deadline with cancel (E7.2), and the refund queue (E4).
// SCRATCH DB only. Needs: the API (Development) on a scratch DB (mw_*) with the demo catalogue, the console built against it, a CORS proxy in front
// of the API, the dev admin plus two clones in the scratch DB with the same TOTP secret: support.e2e@ (role support) and finance.a.e2e@ (role
// finance), and E2E_SQL_CMD = a command that reads SQL on stdin against the SCRATCH DB, e.g.
// `docker exec -i mechware_postgres psql -U mechware -d mw_g51o -At -q`. See docs/modules/G.md HANDOFF G5.1 (orders).
// Set E2E_CONSOLE_ORDERS=1 to run; otherwise it exits 0 without doing anything.
// Real: seller refused (no links, pages say no, API 403), Support cancel flow (reason checked before a code, wrong code keeps the dialog, real
// cancel of a credit order: credit released), an order cancelled elsewhere meanwhile (alreadyCancelled), an order shipped meanwhile
// (ORDER_NOT_CANCELLABLE in words), a paid order cancelled (payment lands in the refund queue), refund request (amount cap before sending),
// four eyes (button disabled for the requester AND the real API refusal), approve with a code (the scratch provider cannot refund, so the
// answer is a Failed refund shown as a problem, not as success), reject, axe 0, 360 px. Mocked: nothing.
import { execSync } from 'node:child_process';
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, CONSOLE as WEB, USERS, apiCall, apiLogin, launch, reporter, totp, uniqueBin } from './lib.mjs';

if (process.env.E2E_CONSOLE_ORDERS !== '1') {
  console.log('SKIP  console orders E2E (set E2E_CONSOLE_ORDERS=1 to run it)');
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
  fin: { email: 'finance.a.e2e@mechware.com.bd', password: PASSWORD, totpSecret: SECRET }
};

/** A code is single-use per account: wait for a 30 s step this account has not used yet. */
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
const key = (what) => `e2e-g51o-${tag}-${what}-${++keySeq}`;
const row = (p, orderNumber) => p.getByTestId('overdue-row').filter({ hasText: orderNumber });

(async () => {
  // ---------------- setup through the real API
  const adminToken = await apiToken(PEOPLE.admin);
  const sellerToken = await apiLogin(USERS.sellerAlpha);
  const buyer = await apiLogin(USERS.buyer);
  const legalName = `Orders Test Mills ${tag} Ltd`;
  const reg = await apiCall(buyer, 'POST', '/api/v1/companies', { legalName, tradeLicenceNo: 'TL-G51O', bin: uniqueBin() });
  const companyId = reg.json.data.id;
  const act = await apiCall(adminToken, 'POST', `/api/v1/admin/companies/${companyId}/activate`);
  const me = (await apiCall(buyer, 'GET', '/api/v1/users/me')).json.data.id;
  const members = (await apiCall(buyer, 'GET', `/api/v1/companies/${companyId}/members`)).json.data;
  const own = members.find((x) => x.userId === me);
  await apiCall(buyer, 'PUT', `/api/v1/companies/${companyId}/members/${own.id}`, { roles: 10, spendLimitMinor: null, costCentreIds: [], isActive: true });
  const site = await apiCall(buyer, 'POST', `/api/v1/companies/${companyId}/sites`, { name: 'Gazipur Plant', contactName: 'Sabbir', contactPhone: '01711000000', line1: 'Plot 5, BSCIC Area', upazila: 'Tongi Industrial Zone', district: 'Gazipur', division: 'Dhaka', isDefaultDelivery: true });
  const credit = await send(adminToken, 'PUT', `/api/v1/admin/companies/${companyId}/credit`, { terms: 15, limitMinorUnits: 500000000 }, { 'X-Step-Up-Code': await freshCode(PEOPLE.admin) });
  r.check('setup: an active company with 15-day credit', reg.status === 201 && act.status === 200 && site.status === 201 && credit.status === 200, `${reg.status}/${act.status}/${site.status}/${credit.status}`);
  r.check('setup: a seller token gets 403 on the overdue list and the refund queue (real API)',
    (await apiCall(sellerToken, 'GET', '/api/v1/admin/orders/overdue')).status === 403 && (await apiCall(sellerToken, 'GET', '/api/v1/admin/refunds')).status === 403);

  const picked = sql("select p.slug, v.id from products p join product_variants v on v.product_id = p.id join inventory_items i on i.variant_id = v.id where p.status = 'active' and v.status = 'active' order by v.id limit 1;").split('\n').map((x) => x.trim()).find(Boolean);
  const [slugRow, pickedId] = picked.split('|');
  sql(`UPDATE inventory_items SET on_hand = 500, reserved = 0 WHERE variant_id = '${pickedId}';`);
  const variant = (await (await fetch(`${API}/api/v1/storefront/products/${slugRow}`)).json()).data.variants.find((v) => v.id === pickedId);
  async function placeCreditOrder(quantity) {
    const res = await fetch(`${API}/api/v1/orders`, {
      method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${buyer}`, 'idempotency-key': key('order') },
      body: JSON.stringify({ lines: [{ variantId: variant.id, quantity }], companyId, shipToSiteId: site.json.data.id, payOnCredit: true })
    });
    const j = await res.json();
    if (!j.data) throw new Error(`credit order failed: ${res.status} ${JSON.stringify(j).slice(0, 300)}`);
    return { id: j.data.orderId, number: j.data.orderNumber, total: j.data.grandTotalMinor };
  }
  const o1 = await placeCreditOrder(2); // cancelled through the screen: credit released
  const o2 = await placeCreditOrder(1); // gets a captured gateway payment, then cancelled through the screen: refund queue
  const o3 = await placeCreditOrder(1); // cancelled elsewhere while the page is open: alreadyCancelled
  const o4 = await placeCreditOrder(1); // shipped elsewhere while the page is open: ORDER_NOT_CANCELLABLE
  const ids = [o1, o2, o3, o4].map((o) => `'${o.id}'`).join(',');
  sql(`UPDATE orders SET legal_delivery_deadline_at = now() - interval '5 hours', courier_handover_deadline_at = now() - interval '30 hours' WHERE id IN (${ids});`);
  sql(`INSERT INTO payments (id, order_id, provider, provider_ref, amount, currency, status, captured_at, created_at) VALUES (gen_random_uuid(), '${o2.id}', 'mock', 'g51o-${tag}', 600000, 'BDT', 'Captured', now(), now());`);
  const sup = await apiToken(PEOPLE.support);
  const ov = await send(sup, 'GET', '/api/v1/admin/orders/overdue?pageSize=50');
  const listed = (ov.json.data?.items ?? []).map((i) => i.orderNumber);
  r.check('setup: four confirmed orders are overdue and Support reads the list (real API)', ov.status === 200 && [o1, o2, o3, o4].every((o) => listed.includes(o.number)), `${ov.status} ${listed.length}`);
  const noCode = await send(sup, 'POST', `/api/v1/admin/orders/${o1.id}/cancel`, { reason: 'No code was given' });
  r.check('setup: the cancel endpoint without an authenticator code is refused and nothing changed (real API)', noCode.status !== 200 && (await send(sup, 'GET', '/api/v1/admin/orders/overdue?pageSize=50')).json.data.items.some((i) => i.orderNumber === o1.number), String(noCode.status));

  const browser = await launch();

  // ================= seller: no links, refused pages
  {
    const p = await newPage(browser);
    await p.goto(`${WEB}/login`, { timeout: 120000, waitUntil: 'networkidle' });
    await p.getByLabel('Staff or seller email').fill(USERS.sellerAlpha.email);
    await p.getByLabel('Password').fill(USERS.sellerAlpha.password);
    await p.getByRole('button', { name: 'Sign in' }).click();
    await p.waitForURL(`${WEB}/`, { timeout: 120000 });
    await p.getByText('Operations console').first().waitFor();
    r.check('seller sees no Orders links', (await p.getByRole('link', { name: /Overdue orders|^Refunds/ }).count()) === 0);
    await p.goto(`${WEB}/orders`);
    await p.getByText('Your role does not allow this action (orders.view_all).').waitFor();
    await p.goto(`${WEB}/refunds`);
    await p.getByText('Your role does not allow this action (orders.refund).').waitFor();
    r.check('seller opening /orders and /refunds is told no', true);
    await p.context().close();
  }

  // ================= support: overdue list and cancel
  const s = await newPage(browser);
  const cancelPosts = [];
  s.on('request', (req) => { if (req.method() === 'POST' && req.url().includes('/cancel')) cancelPosts.push(req.url()); });
  await consoleSignIn(s, PEOPLE.support);
  const nav = s.getByRole('navigation', { name: 'Modules' });
  r.check('support sees an Orders heading with the order links', (await nav.getByText('Orders', { exact: true }).count()) === 1 && (await nav.getByRole('link', { name: /Overdue orders/ }).count()) === 1 && (await nav.getByRole('link', { name: /Refunds/ }).count()) === 1);
  await nav.getByRole('link', { name: /Overdue orders/ }).click();
  await s.waitForURL(/\/orders\/overdue$/);
  await s.getByTestId('overdue-table').waitFor();
  r.check('the table lists the four overdue orders with their status and what is late', (await row(s, o1.number).count()) === 1 && (await row(s, o4.number).innerText()).includes('Confirmed') && (await row(s, o1.number).innerText()).includes('Past the legal delivery deadline') && (await row(s, o1.number).innerText()).includes('Past the courier handover deadline'));
  r.check('overdue time is shown in words (most overdue first: handover is 30 h late)', (await row(s, o1.number).innerText()).includes('1 day 6 h'));
  await axeCheck(s, '/orders/overdue');

  // reason is checked before a code is asked for
  await s.getByRole('button', { name: `Cancel order ${o1.number}` }).click();
  await s.getByRole('dialog').getByRole('button', { name: 'Continue to the authenticator code' }).click();
  await s.getByText('Enter a reason.').waitFor();
  await s.getByLabel(/Why is it cancelled/).fill('abc');
  await s.getByRole('button', { name: 'Continue to the authenticator code' }).click();
  await s.getByText('The reason must be 5 to 500 characters.').waitFor();
  r.check('an empty and a 3-character reason are refused on screen: no code asked, nothing sent', (await s.getByLabel('Authenticator code').count()) === 0 && cancelPosts.length === 0);
  await axeCheck(s, 'cancel reason dialog with an error');
  await s.getByLabel(/Why is it cancelled/).fill(`Buyer asked to cancel ${tag}`);
  await s.getByRole('button', { name: 'Continue to the authenticator code' }).click();
  await s.getByLabel('Authenticator code').waitFor();
  await axeCheck(s, 'cancel code dialog');
  // a code of the wrong shape is refused on screen; a wrong 6-digit code is refused by the server and the dialog stays open
  await s.getByLabel('Authenticator code').fill('12');
  await s.getByRole('button', { name: 'Cancel the order' }).click();
  await s.getByText('Enter the 6-digit code your authenticator app shows now.').waitFor();
  r.check('a short code never reaches the server', cancelPosts.length === 0);
  await s.getByLabel('Authenticator code').fill('000000');
  await s.getByRole('button', { name: 'Cancel the order' }).click();
  await s.getByTestId('stepup-error').waitFor();
  r.check('a wrong code: the server refuses, the dialog stays open and says so, the order is untouched', cancelPosts.length === 1 && (await s.getByTestId('stepup-error').innerText()).includes('not accepted') && (await row(s, o1.number).count()) === 1);
  await s.getByLabel('Authenticator code').fill(await freshCode(PEOPLE.support));
  await s.getByRole('button', { name: 'Cancel the order' }).click();
  await s.getByTestId('cancel-notice').waitFor();
  const notice1 = await s.getByTestId('cancel-notice').innerText();
  r.check('cancelled: notice names the order, the released credit and the stock; focus is on the notice', notice1.includes(`Order ${o1.number} is cancelled.`) && notice1.includes(`of credit was released`) && /unit(s)? of stock went back/.test(notice1) && (await s.evaluate(() => document.activeElement?.getAttribute('data-testid'))) === 'cancel-notice', notice1.replace(/\n/g, ' | '));
  await row(s, o1.number).waitFor({ state: 'detached' });
  r.check('the cancelled order left the overdue list', (await row(s, o1.number).count()) === 0);
  const mine = (await send(adminToken, 'GET', `/api/v1/admin/companies/${companyId}/credit/statement?pageSize=200`)).json.data;
  r.check('the company owes less (real ledger): the o1 charge was reduced', JSON.stringify(mine).includes(o1.number) && JSON.stringify(mine).includes('reduce'), JSON.stringify(mine).slice(0, 200));
  await axeCheck(s, '/orders after a cancel');

  // o3 is cancelled elsewhere while this page is open: the second cancel is a replay
  const adminCancel = await send(adminToken, 'POST', `/api/v1/admin/orders/${o3.id}/cancel`, { reason: 'Cancelled by admin meanwhile' }, { 'X-Step-Up-Code': await freshCode(PEOPLE.admin) });
  r.check('setup: o3 cancelled by an admin behind the open page (real API)', adminCancel.status === 200 && adminCancel.json.data.alreadyCancelled === false, `${adminCancel.status}`);
  await s.getByRole('button', { name: `Cancel order ${o3.number}` }).click();
  await s.getByLabel(/Why is it cancelled/).fill('Second cancel of the same order');
  await s.getByRole('button', { name: 'Continue to the authenticator code' }).click();
  await s.getByLabel('Authenticator code').fill(await freshCode(PEOPLE.support));
  await s.getByRole('button', { name: 'Cancel the order' }).click();
  await s.getByTestId('cancel-notice').filter({ hasText: 'already cancelled' }).waitFor();
  r.check('an order cancelled elsewhere: "already cancelled. Nothing else changed", no error', (await s.getByTestId('cancel-notice').innerText()).includes(`Order ${o3.number} was already cancelled. Nothing else changed.`) && (await s.getByTestId('cancel-alert').count()) === 0);

  // o4 is shipped elsewhere while this page is open: ORDER_NOT_CANCELLABLE
  sql(`UPDATE orders SET status = 'Shipped' WHERE id = '${o4.id}';`);
  await s.getByRole('button', { name: `Cancel order ${o4.number}` }).click();
  await s.getByLabel(/Why is it cancelled/).fill('Cancel an order that already shipped');
  await s.getByRole('button', { name: 'Continue to the authenticator code' }).click();
  await s.getByLabel('Authenticator code').fill(await freshCode(PEOPLE.support));
  await s.getByRole('button', { name: 'Cancel the order' }).click();
  await s.getByTestId('cancel-alert').waitFor();
  r.check('an order that shipped meanwhile: ORDER_NOT_CANCELLABLE in plain words, role=alert, focus on it', (await s.getByTestId('cancel-alert').innerText()).includes('can no longer be cancelled') && (await s.evaluate(() => document.activeElement?.getAttribute('data-testid'))) === 'cancel-alert');
  await row(s, o4.number).getByTestId('cancel-hint').waitFor();
  r.check('after the reload the shipped order has no Cancel button, only the reason', (await row(s, o4.number).getByRole('button').count()) === 0 && (await row(s, o4.number).getByTestId('cancel-hint').innerText()).includes('shipped order cannot be cancelled'));
  await axeCheck(s, '/orders with an error');

  // o2 has a captured gateway payment: its cancel lands in the refund queue
  await s.getByRole('button', { name: `Cancel order ${o2.number}` }).click();
  await s.getByLabel(/Why is it cancelled/).fill('Buyer paid twice, cancel the order');
  await s.getByRole('button', { name: 'Continue to the authenticator code' }).click();
  await s.getByLabel('Authenticator code').fill(await freshCode(PEOPLE.support));
  await s.getByRole('button', { name: 'Cancel the order' }).click();
  await s.getByTestId('cancel-warning').waitFor();
  const warn = await s.getByTestId('cancel-warning').innerText();
  r.check('a paid order: warning says the payment is in the refund queue and a second person must approve', warn.includes('1 payment is now in the refund queue') && (await s.getByRole('link', { name: 'Open the refund queue' }).count()) === 1, warn);
  await s.getByRole('link', { name: 'Open the refund queue' }).click();
  await s.waitForURL(/\/refunds$/);

  // ================= support: refund queue (may ask, may not decide)
  await s.getByTestId('refund-queue').waitFor();
  const item = s.getByTestId('refund-item').filter({ hasText: o2.number });
  r.check('the cancelled order\'s payment is in the queue with the cancel reason and the full amount free to ask for', (await item.innerText()).includes('Order cancelled after payment: Buyer paid twice') && (await item.getByTestId('askable').innerText()) === 'BDT 6,000.00', await item.innerText());
  r.check('Support has no Approve or Reject (no payments.refund_approve)', (await s.getByRole('button', { name: /^Approve|^Reject/ }).count()) === 0);
  await axeCheck(s, '/refunds');
  const refundPosts = [];
  s.on('request', (req) => { if (req.method() === 'POST' && /\/payments\/[^/]+\/refunds$/.test(req.url())) refundPosts.push(req.url()); });
  await item.getByRole('button', { name: `Ask for a refund on order ${o2.number}` }).click();
  await s.getByRole('button', { name: 'Ask for the refund' }).click();
  await s.getByText('Enter the amount in BDT.').waitFor();
  await s.getByLabel('Amount (BDT)').fill('6,000.01');
  await s.getByLabel(/Reason \(5 to 500/).fill('Too much');
  await s.getByRole('button', { name: 'Ask for the refund' }).click();
  await s.getByText('At most BDT 6,000.00 can still be asked for on this payment.').waitFor();
  r.check('an amount above the payment and an empty/short reason are refused on screen, nothing sent', refundPosts.length === 0);
  await axeCheck(s, 'refund request dialog with errors');
  await s.getByLabel('Amount (BDT)').fill('2,500.50');
  await s.getByLabel(/Reason \(5 to 500/).fill('Buyer paid twice: first refund');
  await s.getByRole('button', { name: 'Ask for the refund' }).click();
  await s.getByTestId('refund-notice').waitFor();
  await item.getByTestId('refund-row').first().waitFor();
  r.check('refund asked for: it waits for a second person, 3,499.50 is still free to ask for', (await item.getByTestId('refund-row').first().innerText()).includes('Waiting for a second person') && (await item.getByTestId('refund-row').first().innerText()).includes('2,500.50') && (await item.getByTestId('askable').innerText()) === 'BDT 3,499.50' && refundPosts.length === 1);

  // ================= finance A: four eyes, approve with a code, reject
  const f = await newPage(browser);
  await consoleSignIn(f, PEOPLE.fin);
  await f.goto(`${WEB}/refunds`);
  await f.getByTestId('refund-queue').waitFor();
  const fitem = f.getByTestId('refund-item').filter({ hasText: o2.number });
  const supportRefundId = (await send(sup, 'GET', '/api/v1/admin/refunds')).json.data.find((x) => x.orderNumber === o2.number).refunds[0].id;
  r.check('Finance may approve the refund asked for by Support: Approve is enabled', await fitem.getByRole('button', { name: /^Approve the refund of BDT 2,500.50/ }).isEnabled());
  // Finance asks for the rest: Approve is disabled for the requester, and the real API refuses too
  await fitem.getByRole('button', { name: `Ask for a refund on order ${o2.number}` }).click();
  await f.getByLabel('Amount (BDT)').fill('3,499.50');
  await f.getByLabel(/Reason \(5 to 500/).fill('Buyer paid twice: the rest');
  await f.getByRole('button', { name: 'Ask for the refund' }).click();
  await f.getByTestId('refund-notice').waitFor();
  await f.getByText('Everything on this payment is already asked for', { exact: false }).count();
  const mineRow = fitem.getByTestId('refund-row').filter({ hasText: '3,499.50' });
  await mineRow.waitFor();
  r.check('the requester sees "(by you)", a disabled Approve and the reason why', (await mineRow.innerText()).includes('(by you)') && !(await mineRow.getByRole('button', { name: /^Approve/ }).isEnabled()) && (await mineRow.getByTestId('approve-hint').innerText()).includes('You asked for this refund'));
  r.check('no more money is free to ask for: the Ask button is gone', (await fitem.getByRole('button', { name: /Ask for a refund/ }).count()) === 0 && (await fitem.getByTestId('askable').innerText()) === 'BDT 0.00');
  const finToken = await apiToken(PEOPLE.fin);
  const ownRefundId = (await send(finToken, 'GET', '/api/v1/admin/refunds')).json.data.find((x) => x.orderNumber === o2.number).refunds.find((x) => x.amountMinor === 349950).id;
  const four = await send(finToken, 'POST', `/api/v1/admin/refunds/${ownRefundId}/approve`, undefined, { 'X-Step-Up-Code': await freshCode(PEOPLE.fin) });
  r.check('the real API refuses the requester too: 409 REFUND_FOUR_EYES', four.status === 409 && JSON.stringify(four.json).includes('REFUND_FOUR_EYES'), `${four.status} ${JSON.stringify(four.json).slice(0, 150)}`);
  await axeCheck(f, '/refunds as Finance');

  // approve Support's refund with a code: the scratch provider cannot refund, so it ends Failed, and the screen must not say success
  await f.getByRole('button', { name: /^Approve the refund of BDT 2,500.50/ }).click();
  await f.getByLabel('Authenticator code').waitFor();
  await axeCheck(f, 'approve code dialog');
  await f.getByLabel('Authenticator code').fill('000000');
  await f.getByRole('button', { name: 'Approve and refund' }).click();
  await f.getByTestId('stepup-error').waitFor();
  r.check('approve with a wrong code: refused by the server, dialog stays open', (await f.getByTestId('stepup-error').innerText()).includes('not accepted'));
  await f.getByLabel('Authenticator code').fill(await freshCode(PEOPLE.fin));
  await f.getByRole('button', { name: 'Approve and refund' }).click();
  await f.getByTestId('refund-notice').waitFor();
  const outcome = await f.getByTestId('refund-notice').innerText();
  r.check('the provider could not refund: the screen says so (role=alert) and does not claim success', outcome.includes('The provider did not refund') && (await f.getByTestId('refund-notice').getAttribute('role')) === 'alert' && !outcome.includes('Refunded BDT'), outcome);
  const after = (await send(finToken, 'GET', '/api/v1/admin/refunds')).json.data.find((x) => x.orderNumber === o2.number).refunds.find((x) => x.id === supportRefundId);
  r.check('the real refund is Failed and can be approved again', after.status === 'Failed' && (await fitem.getByRole('button', { name: /^Approve the refund of BDT 2,500.50/ }).innerText()) === 'Approve again');
  // reject the failed one: reason checked first, then it frees the amount
  const rejectPosts = [];
  f.on('request', (req) => { if (req.method() === 'POST' && req.url().includes('/reject')) rejectPosts.push(req.url()); });
  await f.getByRole('button', { name: /^Reject the refund of BDT 2,500.50/ }).click();
  await f.getByRole('button', { name: 'Reject refund' }).click();
  await f.getByText('Enter a reason.').waitFor();
  r.check('an empty rejection reason is refused on screen, nothing sent', rejectPosts.length === 0);
  await f.getByLabel(/Why is it rejected/).fill('Provider cannot refund this payment');
  await f.getByRole('button', { name: 'Reject refund' }).click();
  await f.getByTestId('refund-notice').filter({ hasText: 'Rejected.' }).waitFor();
  r.check('rejected: the amount is free to ask for again (2,500.50)', (await fitem.getByTestId('askable').innerText()) === 'BDT 2,500.50' && (await fitem.getByRole('button', { name: /Ask for a refund/ }).count()) === 1);

  // ================= narrow screens and page errors
  for (const [pg, path, label] of [[s, '/orders/overdue', '/orders/overdue'], [f, '/refunds', '/refunds']]) {
    await pg.setViewportSize({ width: 360, height: 800 });
    await pg.goto(`${WEB}${path}`);
    await pg.getByRole('heading', { level: 1 }).first().waitFor().catch(() => {});
    await pg.waitForLoadState('networkidle');
    r.check(`360 px: no horizontal scroll on ${label}`, !(await overflow(pg)));
  }
  r.check('no page errors', s.errors.length === 0 && f.errors.length === 0, [...s.errors, ...f.errors].join(' | '));
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
