// Shared setup for the F2.4 returns E2Es (returns.mjs, console-returns.mjs). SCRATCH DB only: E2E_SQL_CMD must be a psql command against a scratch database (mw_*).
// Everything is done through the real API (company with credit, orders placed, moved to Delivered by staff, returns opened); only the screens are driven by the browser.
// Same environment as console-fulfilment.mjs: API on a scratch DB, a CORS proxy on localhost, web and console built from temporary copies, support.e2e@ and
// finance.a.e2e@ cloned from the admin row with the same TOTP secret, Returns:NonReturnableCategorySlugs naming the category of VARIANT_B.
import { execSync } from 'node:child_process';
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, USERS, apiCall, apiLogin, reporter, totp, uniqueBin } from './lib.mjs';

const here = dirname(fileURLToPath(import.meta.url));
export const sleep = (ms) => new Promise((res) => setTimeout(res, ms));
export const r = reporter();
export const tag = String(Date.now() % 1000000);

export function requireScratch() {
  if (!process.env.E2E_SQL_CMD || !/mw_/.test(process.env.E2E_SQL_CMD)) throw new Error('E2E_SQL_CMD must be a command against a scratch database (mw_*)');
}
export const sql = (text) => execSync(process.env.E2E_SQL_CMD, { input: text, encoding: 'utf8' });

function findAxe() {
  const ui = resolve(here, '../../ui/node_modules/axe-core/axe.min.js');
  if (existsSync(ui)) return ui;
  const pnpm = resolve(here, '../../../node_modules/.pnpm');
  const d = readdirSync(pnpm).find((n) => n.startsWith('axe-core@'));
  if (d) return join(pnpm, d, 'node_modules/axe-core/axe.min.js');
  throw new Error('axe-core not found');
}
export async function axeCheck(page, label) {
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.addScriptTag({ path: findAxe() });
  const v = await page.evaluate(async () => {
    const out = await window.axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice'] } });
    return out.violations.map((x) => ({ id: x.id, impact: x.impact, nodes: x.nodes.slice(0, 3).map((n) => n.target.join(' ')) }));
  });
  r.check(`axe 0 violations: ${label}`, v.length === 0, v.length ? JSON.stringify(v) : '');
}
export const noOverflow = (page) => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth);

export const VARIANT_A = process.env.E2E_VARIANT_A ?? '00c96822-7c94-4dc6-b14e-9aa894ed1202'; // returnable
export const VARIANT_B = process.env.E2E_VARIANT_B ?? '007d10b3-04bd-48f7-baa8-eb9fc8f7a4c1'; // its category is in Returns:NonReturnableCategorySlugs

// ---------- staff (TOTP) ----------
const SECRET = USERS.admin.totpSecret;
const PASSWORD = USERS.admin.password;
export const PEOPLE = {
  admin: USERS.admin,
  support: { email: 'support.e2e@mechware.com.bd', password: PASSWORD, totpSecret: SECRET },
  finance: { email: 'finance.a.e2e@mechware.com.bd', password: PASSWORD, totpSecret: SECRET }
};
const lastStep = new Map();
export async function freshCode(person) {
  for (;;) {
    const step = Math.floor(Date.now() / 30000);
    if (step > (lastStep.get(person.email) ?? -1)) { lastStep.set(person.email, step); return totp(person.totpSecret); }
    await sleep(1000);
  }
}
export async function apiToken(person) {
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
export const send = (token, method, path, body, headers = {}) =>
  fetch(`${API}${path}`, { method, headers: { 'content-type': 'application/json', authorization: `Bearer ${token}`, ...headers }, body: body ? JSON.stringify(body) : undefined })
    .then(async (res) => ({ status: res.status, json: await res.json().catch(() => ({})) }));

let keySeq = 0;
export const key = (what) => `e2e-f24-${tag}-${what}-${++keySeq}`;

/** A company with credit and a default site, owned by the demo buyer. */
export async function setupCompany(adminToken, buyer, label) {
  const buyerId = (await apiCall(buyer, 'GET', '/api/v1/users/me')).json.data.id;
  const reg = await apiCall(buyer, 'POST', '/api/v1/companies', { legalName: `${label} ${tag} Ltd`, tradeLicenceNo: 'TL-F24A', bin: uniqueBin() });
  const companyId = reg.json.data.id;
  await sleep(5);
  await apiCall(adminToken, 'POST', `/api/v1/admin/companies/${companyId}/activate`);
  const own = (await apiCall(buyer, 'GET', `/api/v1/companies/${companyId}/members`)).json.data.find((x) => x.userId === buyerId);
  await apiCall(buyer, 'PUT', `/api/v1/companies/${companyId}/members/${own.id}`, { roles: 10, spendLimitMinor: null, costCentreIds: [], isActive: true });
  const site = await apiCall(buyer, 'POST', `/api/v1/companies/${companyId}/sites`, { name: 'Returns Plant', contactName: 'Sabbir Rahman', contactPhone: '01711000000', line1: 'Plot 5, BSCIC Area', upazila: 'Tongi Industrial Zone', district: 'Gazipur', division: 'Dhaka', isDefaultDelivery: true });
  const credit = await fetch(`${API}/api/v1/admin/companies/${companyId}/credit`, {
    method: 'PUT', headers: { 'content-type': 'application/json', authorization: `Bearer ${adminToken}`, 'X-Step-Up-Code': await freshCode(PEOPLE.admin) },
    body: JSON.stringify({ terms: 15, limitMinorUnits: 900000000 })
  });
  if (reg.status !== 201 || site.status !== 201 || credit.status !== 200) throw new Error(`company setup failed ${reg.status}/${site.status}/${credit.status}`);
  return { companyId, siteId: site.json.data.id, legalName: `${label} ${tag} Ltd` };
}

/** Places a credit order (A x3 and B x1), then moves it Confirmed -> Delivered through the staff API. Returns the buyer's order detail. */
export async function deliveredOrder(staffToken, buyer, company, { qtyA = 3, deliveredDaysAgo = 0 } = {}) {
  const res = await fetch(`${API}/api/v1/orders`, {
    method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${buyer}`, 'idempotency-key': key('order') },
    body: JSON.stringify({ lines: [{ variantId: VARIANT_A, quantity: qtyA }, { variantId: VARIANT_B, quantity: 1 }], companyId: company.companyId, shipToSiteId: company.siteId, payOnCredit: true })
  });
  const placed = await res.json();
  if (!placed.data) throw new Error(`order failed: ${res.status} ${JSON.stringify(placed).slice(0, 300)}`);
  const id = placed.data.orderId;
  for (const step of ['processing', 'packed', 'ship', 'delivered']) {
    const rowVersion = (await send(staffToken, 'GET', `/api/v1/admin/orders/${id}`)).json.data.rowVersion;
    const body = step === 'ship' ? { rowVersion, courierName: 'Steadfast Courier', trackingNumber: `SF-${tag}-${Math.floor(Math.random() * 9000 + 1000)}`, trackingUrl: null } : { rowVersion };
    const m = await send(staffToken, 'POST', `/api/v1/admin/orders/${id}/${step}`, body);
    if (m.status !== 200) throw new Error(`${step} failed: ${m.status} ${JSON.stringify(m.json).slice(0, 200)}`);
  }
  if (deliveredDaysAgo > 0) moveDelivery(id, deliveredDaysAgo);
  return (await send(buyer, 'GET', `/api/v1/orders/${id}`)).json.data;
}
/** Pretends the parcel was delivered N days ago (scratch DB only). */
export function moveDelivery(orderId, days) {
  sql(`UPDATE shipments SET shipped_at = now() - interval '${days + 2} days', delivered_at = now() - interval '${days} days' WHERE order_id = '${orderId}';`);
}
export const lineOf = (order, variantId) => order.subOrders.flatMap((s) => s.items).find((i) => i.variantId === variantId);

/** Opens a return through the buyer API. */
export function openReturn(buyer, order, qty, { variant = VARIANT_A, reasonCode = 'damaged', note = null, company } = {}) {
  return send(buyer, 'POST', `/api/v1/orders/${order.orderNumber}/returns`,
    { lines: [{ orderItemId: lineOf(order, variant).id, quantity: qty }], reasonCode, note, ...(company ? { companyId: company.companyId } : {}) }, { 'idempotency-key': key('ret') });
}
export const staffReturn = async (token, id) => (await send(token, 'GET', `/api/v1/admin/returns/${id}`)).json.data;
export async function move(token, id, step, extra = {}) {
  const cur = await staffReturn(token, id);
  return send(token, 'POST', `/api/v1/admin/returns/${id}/${step}`, { rowVersion: cur.rowVersion, ...extra });
}
