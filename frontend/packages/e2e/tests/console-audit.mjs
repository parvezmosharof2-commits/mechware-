// G5.1: audit log integrity screen in the staff console (the only audit endpoint today is GET /api/v1/admin/audit/chain).
// SCRATCH DB only. Needs: an API on the scratch DB (Development) reached by the console through a CORS proxy that can switch its
// target at run time (GET <proxy>/__target?port=N, see docs/modules/G.md HANDOFF G5.1), a SECOND API instance (fresh 5-minute cache)
// for the tampered log, the admin's authenticator secret stored in clear in the scratch DB, and E2E_SQL_CMD = a command that reads
// SQL on stdin against the scratch DB, e.g. `docker exec -i mechware_postgres psql -U mechware -d mw_g51 -At -q`.
// E2E_PROXY_URL = the proxy, E2E_API2_PORT = the second API's port. Set E2E_AUDIT=1 to run; otherwise it exits 0.
// Real: seller refused (no link, page says no), admin sees the verdict equal to the API answer, the 5-minute cache shared notice,
// a row changed in the scratch DB (replication role replica, guards off for that one statement) shows Broken with the first broken
// row and role=alert, axe 0 on both states, 360 px no overflow, no page errors. Mocked: nothing.
import { execSync } from 'node:child_process';
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, CONSOLE as WEB, USERS, apiLogin, launch, reporter, totp } from './lib.mjs';

if (process.env.E2E_AUDIT !== '1') {
  console.log('SKIP  console audit E2E (set E2E_AUDIT=1 to run it)');
  process.exit(0);
}
if (!process.env.E2E_SQL_CMD || !process.env.E2E_PROXY_URL || !process.env.E2E_API2_PORT) throw new Error('E2E_SQL_CMD, E2E_PROXY_URL and E2E_API2_PORT are needed');
if (!/mw_/.test(process.env.E2E_SQL_CMD)) throw new Error('E2E_SQL_CMD must name a scratch database (mw_*)');

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
const admin = USERS.admin;

let lastStep = -1;
async function freshCode() {
  for (;;) {
    const step = Math.floor(Date.now() / 30000);
    if (step > lastStep) { lastStep = step; return totp(admin.totpSecret); }
    await sleep(1000);
  }
}
async function adminToken(base) {
  for (let i = 0; i < 8; i++) {
    const res = await fetch(`${base}/api/v1/auth/login`, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ identifier: admin.email, password: admin.password, totpCode: await freshCode() })
    });
    const body = await res.json();
    if (res.ok) return body.data.accessToken;
    await sleep(5000);
  }
  throw new Error('admin API login failed');
}
async function newPage(browser, width = 1280) {
  const ctx = await browser.newContext({ viewport: { width, height: 900 } });
  const page = await ctx.newPage();
  page.errors = [];
  page.on('pageerror', (e) => page.errors.push('pageerror: ' + e.message));
  return page;
}
async function adminSignIn(page) {
  await page.goto(`${WEB}/login`, { timeout: 120000, waitUntil: 'networkidle' });
  await page.getByLabel('Staff or seller email').fill(admin.email);
  await page.getByLabel('Password').fill(admin.password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.getByLabel('6-digit authenticator code or single-use recovery code').fill(await freshCode());
  await page.getByRole('button', { name: 'Verify and unlock console' }).click();
  await page.waitForURL(`${WEB}/`, { timeout: 120000 });
}
const apiAudit = (base, token) => fetch(`${base}/api/v1/admin/audit/chain`, { headers: { authorization: `Bearer ${token}` } }).then(async (res) => ({ status: res.status, json: await res.json().catch(() => ({})) }));
const fmt = (n) => Number(n).toLocaleString('en-US');

(async () => {
  const api2 = `http://localhost:${process.env.E2E_API2_PORT}`;
  const token = await adminToken(API);
  const sellerToken = await apiLogin(USERS.sellerAlpha);
  r.check('setup: a seller token gets 403 on the audit check (real API)', (await apiAudit(API, sellerToken)).status === 403);
  const before = await apiAudit(API, token);
  r.check('setup: the audit check answers 200 with sealed rows', before.status === 200 && before.json.data.sealed > 0 && before.json.data.intact === true, JSON.stringify(before.json.data));
  const good = before.json.data;

  const browser = await launch();

  // ---- seller: no link, page refuses
  {
    const p = await newPage(browser);
    await p.goto(`${WEB}/login`, { timeout: 120000, waitUntil: 'networkidle' });
    await p.getByLabel('Staff or seller email').fill(USERS.sellerAlpha.email);
    await p.getByLabel('Password').fill(USERS.sellerAlpha.password);
    await p.getByRole('button', { name: 'Sign in' }).click();
    await p.waitForURL(`${WEB}/`, { timeout: 120000 });
    await p.getByText('Operations console').first().waitFor();
    r.check('seller sees no audit link', (await p.getByRole('link', { name: /Audit log integrity/ }).count()) === 0 && (await p.getByRole('navigation', { name: 'Modules' }).getByRole('link').count()) >= 0);
    await p.goto(`${WEB}/audit`);
    await p.getByText('Your role does not allow this action (audit.view).').waitFor();
    r.check('seller opening /audit is told no and no check is shown', (await p.getByTestId('audit-check').count()) === 0);
    await p.context().close();
  }

  // ---- admin: intact log
  const page = await newPage(browser);
  await adminSignIn(page);
  const link = page.getByRole('navigation', { name: 'Modules' }).getByRole('link', { name: /Audit log integrity/ });
  r.check('admin sees the audit link in the sidebar', (await link.count()) === 1);
  await link.click();
  await page.waitForURL(`${WEB}/audit`);
  await page.getByTestId('audit-check').waitFor();
  r.check('verdict is intact', (await page.getByTestId('audit-check').getAttribute('data-verdict')) === 'intact');
  r.check('verdict sentence names the sealed count', (await page.getByTestId('audit-verdict').innerText()).includes(`All ${fmt(good.sealed)} sealed rows match`));
  r.check('sealed number equals the API', (await page.getByTestId('audit-sealed').innerText()) === fmt(good.sealed));
  r.check('first broken row says none', (await page.getByTestId('audit-broken').innerText()) === 'none');
  r.check('head hash shown in groups of 8 and equals the API hash', (await page.getByTestId('audit-head').innerText()).replace(/\s/g, '') === good.headHash);
  r.check('page mentions the shared 5-minute check and the link to the audit log', /5 minutes/.test(await page.locator('main').innerText()) && /open the audit log/.test(await page.locator('main').innerText()));
  r.check('intact state is a polite status, not an alert', (await page.getByTestId('audit-check').getByRole('status').count()) === 1 && (await page.getByTestId('audit-check').getByRole('alert').count()) === 0);
  await axeCheck(page, 'audit intact');
  r.check('active sidebar item is marked current', (await page.locator('a[aria-current="page"]').count()) === 1);

  // ---- tamper with one sealed row in the SCRATCH db; switch the proxy to the second (fresh-cache) API
  const pos = good.sealed > 10 ? 10 : 1;
  sql(`SET session_replication_role = replica; UPDATE audit_logs SET action = action || ' (changed)' WHERE chain_position = ${pos};`);
  const stillCached = await apiAudit(API, token);
  r.check('the first API still serves the cached intact answer (shared up to 5 minutes)', stillCached.json.data.intact === true);
  await page.getByRole('button', { name: 'Check again' }).click();
  await sleep(1500);
  r.check('Check again on the cached API still says intact (as stated on the page)', (await page.getByTestId('audit-check').getAttribute('data-verdict')) === 'intact');
  const sw = await fetch(`${process.env.E2E_PROXY_URL}/__target?port=${process.env.E2E_API2_PORT}`);
  r.check('proxy switched to the second API', sw.status === 200);
  const bad = await apiAudit(api2, await adminToken(api2));
  r.check('the second API reports the break (real API)', bad.status === 200 && bad.json.data.intact === false && bad.json.data.firstBrokenPosition === pos, JSON.stringify(bad.json.data));
  await page.getByRole('button', { name: 'Check again' }).click();
  await page.locator('[data-verdict="broken"]').waitFor({ timeout: 30000 });
  r.check('verdict is broken', true);
  r.check('broken state is an alert', (await page.getByTestId('audit-check').getByRole('alert').count()) === 1);
  r.check('sentence names the first broken row', (await page.getByTestId('audit-verdict').innerText()).includes(`starting at row ${pos}`));
  r.check('first broken row number shown', (await page.getByTestId('audit-broken').innerText()) === String(pos));
  r.check('nothing says the log is fine', !/match their seal\. No row/.test(await page.getByTestId('audit-check').innerText()));
  await axeCheck(page, 'audit broken');

  // ---- 360 px
  {
    const m = await newPage(browser, 360);
    await adminSignIn(m);
    await m.goto(`${WEB}/audit`);
    await m.getByTestId('audit-check').waitFor();
    r.check('360 px: no horizontal overflow', !(await overflow(m)));
    await m.context().close();
  }
  r.check('no page errors', page.errors.length === 0, page.errors.join(' | '));

  // ---- put the scratch row back so a re-run starts clean
  sql(`SET session_replication_role = replica; UPDATE audit_logs SET action = replace(action, ' (changed)', '') WHERE chain_position = ${pos};`);
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
