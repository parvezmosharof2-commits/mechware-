// D2.12b: margin-floors screen of the staff console. Needs the API (Development, scratch DB), the console and staff accounts on the SAME
// authenticator secret as the dev admin: admin@ (view_cost + manage_floors), finance.e2e@ (view_cost only), support.e2e@ (neither). The finance and
// support rows are clones of the admin row (same password and encrypted secret) with their own role; see docs/modules/D2.md (HANDOFF D2.12b).
// Set E2E_CONSOLE_FLOORS=1 to run; otherwise it exits 0 without doing anything.
// Real: sign-in with TOTP, permission gating, the seeded 10 % global row, add a brand floor with a real step-up code (a wrong code first), a third
// decimal blocked client-side, edit, effective lookup by SKU and by category, delete, GLOBAL_FLOOR_REQUIRED from the API, axe, 360 px.
// Mocked: nothing.
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, CONSOLE as WEB, SHOTS, USERS, apiCall, launch, reporter, totp } from './lib.mjs';

if (process.env.E2E_CONSOLE_FLOORS !== '1') {
  console.log('SKIP  console floors E2E (set E2E_CONSOLE_FLOORS=1 to run it)');
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
const sleep = (ms) => new Promise((res) => setTimeout(res, ms));
const overflow = (p) => p.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);

const SECRET = USERS.admin.totpSecret;
const PASSWORD = USERS.admin.password;
const PEOPLE = {
  admin: USERS.admin,
  finance: { email: 'finance.e2e@mechware.com.bd', password: PASSWORD, totpSecret: SECRET },
  support: { email: 'support.e2e@mechware.com.bd', password: PASSWORD, totpSecret: SECRET }
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
const text = (loc) => loc.innerText();

(async () => {
  // ---- setup through the API: a variant that has a brand, and no leftover brand rows
  const adminToken = await apiToken(PEOPLE.admin);
  const vars = (await apiCall(adminToken, 'GET', '/api/v1/admin/variants?pageSize=50')).json.data?.items ?? [];
  const sample = vars.find((v) => v.brand);
  r.check('setup: a variant with a brand exists', !!sample, sample ? `${sample.sku} / ${sample.brand}` : 'none');
  const before = (await apiCall(adminToken, 'GET', '/api/v1/admin/margin-floors')).json.data ?? [];
  const global = before.find((f) => f.scope === 'Global');
  r.check('setup: only the seeded global row at 10 % exists', before.length === 1 && global && global.minMarkupPercent === 10 && global.updatedBy === null, JSON.stringify(before.map((f) => [f.scope, f.minMarkupPercent])));

  const browser = await launch();

  // ================= support: no link, the page refuses
  {
    const p = await newPage(browser);
    await consoleSignIn(p, PEOPLE.support);
    await p.getByRole('navigation', { name: 'Modules' }).waitFor();
    r.check('support has no Margin floors link', (await p.getByRole('link', { name: /Margin floors/ }).count()) === 0);
    await p.goto(`${WEB}/pricing/margin-floors`);
    await p.getByText('Your role does not allow this page (pricing.view_cost).').waitFor();
    r.check('support opening the page is told no', (await p.getByTestId('global-card').count()) === 0);
    await p.context().close();
  }

  // ================= finance: reads, no edit controls
  {
    const p = await newPage(browser);
    await consoleSignIn(p, PEOPLE.finance);
    await p.getByRole('navigation', { name: 'Modules' }).getByRole('link', { name: /Margin floors/ }).click();
    await p.waitForURL(/\/pricing\/margin-floors$/);
    await p.getByTestId('global-card').waitFor();
    r.check('finance sees the global default 10.00 %', (await text(p.getByTestId('global-markup'))) === '10.00 %');
    r.check('finance has no Add floor, no Change buttons', (await p.getByTestId('add-floor').count()) === 0 && (await p.getByTestId('global-edit').count()) === 0 && (await p.getByRole('button', { name: /^Change/ }).count()) === 0);
    r.check('finance sees the effective lookup', (await p.getByTestId('effective-panel').count()) === 1);
    await p.context().close();
  }

  // ================= admin
  const p = await newPage(browser);
  await consoleSignIn(p, PEOPLE.admin);
  await p.getByRole('navigation', { name: 'Modules' }).getByRole('link', { name: /Margin floors/ }).click();
  await p.waitForURL(/\/pricing\/margin-floors$/);
  await p.getByTestId('global-card').waitFor();
  r.check('admin: sidebar item sits under the Pricing heading and is the current page', (await p.getByRole('link', { name: /Margin floors/ }).getAttribute('aria-current')) === 'page');
  r.check('admin sees the global default 10.00 % with the plain-words rule', (await text(p.getByTestId('global-markup'))) === '10.00 %' && (await text(p.getByTestId('global-card'))).includes('there must always be one') && (await text(p.getByTestId('global-card'))).includes('Updated by system'));
  r.check('admin: Add floor and the global Change button are offered, the global row has no delete', (await p.getByTestId('add-floor').count()) === 1 && (await p.getByTestId('global-edit').count()) === 1 && (await p.getByRole('button', { name: /^Delete/ }).count()) === 0);
  r.check('admin: empty table says the global default applies everywhere', (await p.getByTestId('empty').count()) === 1);
  await axeCheck(p, 'margin floors page');
  await p.setViewportSize({ width: 360, height: 800 });
  r.check('page: no horizontal overflow at 360 px', !(await overflow(p)));
  await p.setViewportSize({ width: 1280, height: 900 });
  await p.screenshot({ path: `${SHOTS}/d2-12b-floors-1280.png`, fullPage: true });

  // ---- add a brand floor: a third decimal is blocked, then 15.25 with a wrong code first
  await p.getByTestId('add-floor').click();
  const dlg = p.getByRole('dialog');
  await dlg.getByTestId('floor-markup-input').waitFor();
  r.check('add: scope defaults to Brand, brand picker shown', (await p.getByTestId('floor-scope').inputValue()) === 'Brand' && (await p.getByTestId('add-brand-picker').count()) === 1);
  await dlg.getByLabel('Brand: SKU of any product of the brand').fill(sample.sku);
  await dlg.getByRole('button', { name: 'Find brand' }).click();
  await p.getByTestId('add-brand-chosen').waitFor();
  r.check('add: the brand is found from the SKU', (await text(p.getByTestId('add-brand-chosen'))) === `Brand: ${sample.brand}`);
  await dlg.getByTestId('floor-markup-input').fill('15.255');
  await dlg.getByTestId('floor-review').click();
  await dlg.getByText(/At most 2 decimals/).waitFor();
  r.check('a third decimal is refused on the screen, no confirm and no code dialog opened', (await p.getByTestId('floor-confirm-statement').count()) === 0 && (await p.getByRole('dialog', { name: 'Confirm with your authenticator' }).count()) === 0);
  await axeCheck(p, 'add floor dialog with an error');
  await dlg.getByTestId('floor-markup-input').fill('15.25');
  await dlg.getByTestId('floor-review').click();
  await p.getByTestId('floor-confirm-statement').waitFor();
  r.check('confirm dialog restates the change and the pending-lists note', (await text(p.getByTestId('floor-confirm-statement'))).includes(`Add a floor of 15.25 % for "${sample.brand}"`) && (await text(p.getByTestId('floor-change-note'))).includes('Lowering a floor does not approve anything by itself; pending lists are re-checked when approved'));
  await p.getByTestId('floor-confirm').click();
  const code = p.getByRole('dialog').getByLabel('Authenticator code');
  await code.waitFor();
  await axeCheck(p, 'step-up dialog');
  await code.fill('000000');
  await p.getByRole('button', { name: 'Save floor' }).click();
  await p.getByTestId('stepup-error').waitFor();
  r.check('wrong code: refusal in plain words, dialog stays open, box emptied', (await text(p.getByTestId('stepup-error'))).includes('Codes work once') && (await code.inputValue()) === '' && (await p.getByRole('button', { name: 'Save floor' }).isEnabled()));
  await code.fill(await freshCode(PEOPLE.admin));
  await p.getByRole('button', { name: 'Save floor' }).click();
  await p.getByTestId('floor-notice').waitFor();
  const row = p.getByTestId('floor-row').filter({ hasText: sample.brand });
  r.check('the new brand row shows 15.25 % and a person rather than system', (await row.count()) === 1 && (await text(row.getByTestId('floor-markup'))) === '15.25 %' && !(await text(row)).includes('system'));
  const server = (await apiCall(adminToken, 'GET', '/api/v1/admin/margin-floors')).json.data;
  r.check('the server holds exactly that row (Brand, 15.25) next to the untouched global', server.length === 2 && server.some((f) => f.scope === 'Brand' && f.minMarkupPercent === 15.25 && f.brandName === sample.brand) && server.find((f) => f.scope === 'Global').minMarkupPercent === 10);
  await p.screenshot({ path: `${SHOTS}/d2-12b-floors-after-add-1280.png`, fullPage: true });

  // ---- edit to 20
  await p.getByRole('button', { name: `Change the floor for ${sample.brand}` }).click();
  await p.getByTestId('floor-markup-input').waitFor();
  r.check('edit: the box holds the current value, the scope picker is not offered', (await p.getByTestId('floor-markup-input').inputValue()) === '15.25' && (await p.getByTestId('floor-scope').count()) === 0);
  await p.getByTestId('floor-markup-input').fill('20');
  await p.getByTestId('floor-review').click();
  await p.getByTestId('floor-confirm-statement').waitFor();
  r.check('edit: the statement names old and new', (await text(p.getByTestId('floor-confirm-statement'))).includes('from 15.25 % to 20.00 %'));
  await p.getByTestId('floor-confirm').click();
  await p.getByRole('dialog').getByLabel('Authenticator code').fill(await freshCode(PEOPLE.admin));
  await p.getByRole('button', { name: 'Save floor' }).click();
  await p.getByText(/^Saved: 20\.00 %/).waitFor();
  r.check('edit: the row now shows 20.00 %', (await text(p.getByTestId('floor-row').filter({ hasText: sample.brand }).getByTestId('floor-markup'))) === '20.00 %');

  // ---- effective lookup
  await p.getByTestId('effective-sku').fill(sample.sku);
  await p.getByTestId('effective-run').click();
  await p.getByTestId('effective-summary').waitFor();
  r.check('effective by SKU: the brand row applies at 20.00 %', (await text(p.getByTestId('effective-summary'))).includes('20.00 % applies, from the brand row'), await text(p.getByTestId('effective-summary')));
  await p.getByTestId('effective-sku').fill('NO-SUCH-SKU-123');
  await p.getByTestId('effective-run').click();
  await p.getByText('No product has the SKU "NO-SUCH-SKU-123".').waitFor();
  r.check('effective by an unknown SKU: plain words, no result card', (await p.getByTestId('effective-result').count()) === 0);
  await p.getByRole('button', { name: 'By brand and category' }).click();
  await p.getByTestId('eff-category-list').waitFor();
  await p.getByTestId('eff-category-list').selectOption({ index: 1 });
  await p.getByTestId('effective-run').click();
  await p.getByTestId('effective-summary').waitFor();
  r.check('effective by category only: a category without its own row follows the global default', (await text(p.getByTestId('effective-summary'))).includes('10.00 % applies, from the global default'), await text(p.getByTestId('effective-summary')));
  await axeCheck(p, 'effective lookup by category');

  // ---- the global row can not be deleted (server answer, forced through the API)
  const forced = await fetch(`${API}/api/v1/admin/margin-floors/${global.id}`, { method: 'DELETE', headers: { authorization: `Bearer ${adminToken}`, 'X-Step-Up-Code': await freshCode(PEOPLE.admin) } });
  const forcedBody = await forced.json().catch(() => ({}));
  r.check('forcing a DELETE of the global row through the API: 409 GLOBAL_FLOOR_REQUIRED', forced.status === 409 && forcedBody?.error?.code === 'GLOBAL_FLOOR_REQUIRED', `${forced.status} ${forcedBody?.error?.code}`);
  r.check('the global row still exists after the forced delete', (await apiCall(adminToken, 'GET', '/api/v1/admin/margin-floors')).json.data.some((f) => f.scope === 'Global'));

  // ---- delete the brand row
  await p.getByRole('button', { name: `Delete the floor for ${sample.brand}` }).click();
  await p.getByTestId('floor-delete-statement').waitFor();
  r.check('delete: confirm dialog names the row and its markup', (await text(p.getByTestId('floor-delete-statement'))).includes(`Delete the 20.00 % floor for "${sample.brand}"`));
  await p.getByTestId('floor-delete-continue').click();
  await p.getByRole('dialog').getByLabel('Authenticator code').fill(await freshCode(PEOPLE.admin));
  await p.getByRole('button', { name: 'Delete floor' }).click();
  await p.getByText(/^Deleted the floor for/).waitFor();
  await p.getByTestId('empty').waitFor();
  r.check('delete: the brand row is gone, the global default stays', (await p.getByTestId('floor-row').count()) === 0 && (await text(p.getByTestId('global-markup'))) === '10.00 %');
  r.check('the server agrees: one row left', (await apiCall(adminToken, 'GET', '/api/v1/admin/margin-floors')).json.data.length === 1);

  r.check('no uncaught page errors', p.errors.length === 0, p.errors.join(' | '));
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => {
  console.error('FAIL  unexpected error:', e);
  process.exit(1);
});
