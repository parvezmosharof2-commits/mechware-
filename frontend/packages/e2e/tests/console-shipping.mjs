// E8.4b: shipping zones editor of the staff console. Needs the API (Development, E8.4a endpoints, `bd_locations` rows: districts
// with the Dhaka division must exist, see the card's HANDOFF), the console and the dev admin. Set E2E_CONSOLE_SHIPPING=1 to run;
// otherwise it exits 0 without doing anything. The database must hold no shipping zones yet (use a scratch DB).
// Real: admin sign-in with TOTP, banner, create with bands, coverage, tariff preview (Priced / free shipping / QuoteOnly / NoZone),
// band edit, stale-version conflict (second API call), other-zone districts disabled, deactivate + delete, seller and buyer refusals.
// Mocked (and marked): Support/Finance permission gating - the console has no staff-creation API and the dev database has no Support
// or Finance user, so `GET /users/me` is answered with the admin's data minus `shipping.manage` (or minus `shipping.view`).
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, CONSOLE as WEB, SHOTS, USERS, apiCall, apiLogin, launch, reporter, totp } from './lib.mjs';

if (process.env.E2E_CONSOLE_SHIPPING !== '1') {
  console.log('SKIP  console shipping E2E (set E2E_CONSOLE_SHIPPING=1 to run it)');
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
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.addScriptTag({ path: findAxe() });
  const v = await page.evaluate(async () => {
    const out = await window.axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice'] } });
    return out.violations.map((x) => ({ id: x.id, impact: x.impact, nodes: x.nodes.slice(0, 3).map((n) => n.target.join(' ')) }));
  });
  r.check(`axe 0 violations: ${label}`, v.length === 0, v.length ? JSON.stringify(v) : '');
}
async function signIn(p, email, password) {
  await p.goto(`${WEB}/login`, { timeout: 120000, waitUntil: 'networkidle' });
  await p.getByLabel('Staff or seller email').fill(email);
  await p.getByLabel('Password').fill(password);
  await p.getByRole('button', { name: 'Sign in' }).click();
}
const overflow = (p) => p.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
const wait = (ms) => new Promise((res) => setTimeout(res, ms));

(async () => {
  // ---- setup through the API
  const usedCode = await totp(USERS.admin.totpSecret);
  const adminLogin = await fetch(`${API}/api/v1/auth/login`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ identifier: USERS.admin.email, password: USERS.admin.password, totpCode: usedCode })
  });
  const adminToken = (await adminLogin.json()).data.accessToken;
  const buyerToken = await apiLogin(USERS.buyer);
  const sellerToken = await apiLogin(USERS.sellerAlpha);
  const preZones = await apiCall(adminToken, 'GET', '/api/v1/admin/shipping/zones');
  r.check('setup: no shipping zones exist yet (scratch database)', preZones.status === 200 && preZones.json.data.length === 0, JSON.stringify(preZones.json).slice(0, 120));
  const cov0 = (await apiCall(adminToken, 'GET', '/api/v1/admin/shipping/coverage')).json.data;
  const all = [...cov0.covered, ...cov0.uncovered];
  const id = (name) => all.find((d) => d.nameEn === name)?.districtId;
  r.check('setup: Dhaka, Gazipur, Narayanganj and Chattogram districts exist', ['Dhaka', 'Gazipur', 'Narayanganj', 'Chattogram'].every((n) => id(n)), all.map((d) => d.nameEn).join(','));
  r.check('buyer token: GET /admin/shipping/zones is 403 (real API)', (await apiCall(buyerToken, 'GET', '/api/v1/admin/shipping/zones')).status === 403);
  r.check('seller token: GET /admin/shipping/coverage is 403 (real API)', (await apiCall(sellerToken, 'GET', '/api/v1/admin/shipping/coverage')).status === 403);
  // an inactive zone made through the API: its district must show disabled in the editor
  const other = await apiCall(adminToken, 'POST', '/api/v1/admin/shipping/zones', {
    code: 'other-zone', nameEn: 'Other zone', nameBn: 'Other zone', isActive: false, districtIds: [id('Narayanganj')],
    rates: { bands: [{ maxGrams: 1000, feeMinor: 5000 }], freeShippingThresholdMinor: null, heavyUnitGrams: null, oversizeDimensionMm: null, maxFeeMinor: 100000 }
  });
  r.check('setup: an inactive "other-zone" holds Narayanganj', other.status === 201, other.status + ' ' + JSON.stringify(other.json).slice(0, 300));

  const browser = await launch();
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const p = await ctx.newPage();
  const errors = [];
  p.on('console', (m) => { if (m.type() === 'error' && !m.text().includes('Failed to load resource')) errors.push(m.text()); });
  p.on('pageerror', (e) => errors.push('pageerror: ' + e.message));

  // ---- seller: no link, page refuses
  await signIn(p, USERS.sellerAlpha.email, USERS.sellerAlpha.password);
  await p.waitForURL(`${WEB}/`);
  await p.getByText('Operations console').first().waitFor();
  r.check('seller sees no Shipping zones link', (await p.getByRole('link', { name: /Shipping zones/ }).count()) === 0);
  await p.goto(`${WEB}/shipping/zones`);
  await p.getByText('Your role does not allow this action (shipping.view).').waitFor();
  await p.goto(`${WEB}/shipping/zones/new`);
  await p.getByText('Your role does not allow this action (shipping.manage).').waitFor();
  r.check('seller opening /shipping/zones and /new is told no', true);
  await p.getByRole('button', { name: 'Sign out' }).click();
  await p.waitForURL(/\/login$/);

  // ---- admin
  await signIn(p, USERS.admin.email, USERS.admin.password);
  let code = await totp(USERS.admin.totpSecret);
  while (code === usedCode) { await wait(1000); code = await totp(USERS.admin.totpSecret); }
  await p.getByLabel('6-digit authenticator code or single-use recovery code').fill(code);
  await p.getByRole('button', { name: 'Verify and unlock console' }).click();
  await p.waitForURL(`${WEB}/`);
  r.check('sidebar groups the link under a Shipping heading', (await p.getByRole('navigation', { name: 'Modules' }).getByText('Shipping', { exact: true }).count()) === 1);
  await p.getByRole('link', { name: /Shipping zones/ }).first().click();
  await p.waitForURL(/\/shipping\/zones$/);
  await p.getByTestId('no-zone-banner').waitFor();
  r.check('no active zone: the production banner is shown', (await p.getByTestId('no-zone-banner').innerText()).includes('customers cannot place orders in production'));
  await p.getByTestId('empty').or(p.getByTestId('zone-table')).waitFor();
  await p.getByTestId('coverage-summary').waitFor();
  r.check('table lists the inactive other-zone (1 district, 1 band)', (await p.getByTestId('zone-table').innerText()).includes('other-zone'));
  const uncovered = await p.getByTestId('uncovered').innerText();
  r.check('uncovered districts are listed by division, Narayanganj shows its inactive owner', uncovered.toLowerCase().includes('dhaka (') && uncovered.includes('Chattogram') && uncovered.includes('other-zone (inactive)'));
  await p.getByRole('link', { name: 'New zone' }).waitFor();
  await axeCheck(p, '/shipping/zones');
  await p.screenshot({ path: `${SHOTS}/e8-4b-list-banner-1280.png`, fullPage: true });

  // ---- create (validation first)
  await p.getByRole('link', { name: 'New zone' }).click();
  await p.waitForURL(/\/shipping\/zones\/new$/);
  await p.getByRole('button', { name: 'Create zone' }).click();
  await p.getByText('Use 1 to 40 lowercase letters, digits').waitFor();
  r.check('create: empty form shows code, name and weight errors', (await p.getByText('Enter a name of 1 to 100 characters.').count()) === 2 && (await p.getByText(/Enter the weight in kg/).count()) === 1);
  const narayanganj = p.getByRole('checkbox', { name: /Narayanganj/ });
  r.check('picker: a district owned by another zone is disabled and names the owner', (await narayanganj.isDisabled()) && (await p.getByText('(in other-zone)').count()) === 1);
  await p.getByLabel('Code', { exact: true }).fill('inside-dhaka');
  await p.getByLabel('Name (English)').fill('Inside Dhaka');
  await p.getByLabel('Name (Bangla)').fill('ঢাকার ভেতরে');
  await p.getByLabel('Search districts').fill('gaz');
  r.check('picker: search narrows the list', (await p.getByRole('checkbox').filter({ hasNot: p.locator('[disabled]') }).count()) >= 1 && (await p.getByRole('checkbox', { name: 'Chattogram' }).count()) === 0);
  await p.getByLabel('Search districts').fill('');
  for (const n of ['Dhaka', 'Gazipur']) await p.getByRole('checkbox', { name: n, exact: true }).check();
  await p.getByLabel('Band 1 up to (kg)').fill('1');
  await p.getByLabel('Band 1 fee (taka)').fill('60');
  await p.getByRole('button', { name: 'Add band' }).click();
  await p.getByLabel('Band 2 up to (kg)').fill('5');
  await p.getByLabel('Band 2 fee (taka)').fill('50');
  await p.getByRole('button', { name: 'Add band' }).click();
  await p.getByLabel('Band 3 up to (kg)').fill('20');
  await p.getByLabel('Band 3 fee (taka)').fill('150.5');
  await p.getByLabel('Free shipping from subtotal (taka)').fill('5000');
  await p.getByLabel('Heavy-unit limit (kg)').fill('25');
  await p.getByLabel('Oversize side limit (mm)').fill('1200');
  await p.getByLabel('Fee cap (taka)').fill('500');
  await p.getByRole('button', { name: 'Create zone' }).click();
  await p.getByText('A heavier band cannot cost less than the lighter one.').waitFor();
  r.check('create: a cheaper heavier band is refused live, nothing is sent', (await p.getByTestId('save-failure').innerText()).includes('Fix the highlighted fields'));
  await p.getByLabel('Band 2 fee (taka)').fill('90.5');
  await axeCheck(p, '/shipping/zones/new');
  await p.screenshot({ path: `${SHOTS}/e8-4b-new-1280.png`, fullPage: true });
  await p.getByLabel('Active (orders to its districts are priced from this zone)').check();
  await p.getByRole('button', { name: 'Create zone' }).click();
  await p.waitForURL(/\/shipping\/zones\/[0-9a-f-]{36}$/);
  const zoneUrl = p.url();
  const zoneId = zoneUrl.split('/').pop();
  await p.getByLabel('Code', { exact: true }).waitFor();
  r.check('create: the zone opens, code is read-only, values come back', (await p.getByLabel('Code', { exact: true }).isDisabled()) && (await p.getByLabel('Band 2 fee (taka)').inputValue()) === '90.50' && (await p.getByLabel('Heavy-unit limit (kg)').inputValue()) === '25');

  // ---- coverage and banner after the zone is active
  await p.goto(`${WEB}/shipping/zones`);
  await p.getByTestId('coverage-summary').waitFor();
  r.check('list: banner is gone once an active zone exists', (await p.getByTestId('no-zone-banner').count()) === 0);
  r.check('coverage: Dhaka and Gazipur covered, Chattogram not', (await p.getByTestId('coverage-summary').innerText()).includes('2 districts covered') && (await p.getByTestId('uncovered').innerText()).includes('Chattogram'));
  await p.screenshot({ path: `${SHOTS}/e8-4b-list-covered-1280.png`, fullPage: true });

  // ---- tariff preview (server computed)
  await p.goto(zoneUrl);
  await p.getByTestId('tariff-preview').waitFor();
  await axeCheck(p, '/shipping/zones/[id]');
  const preview = p.getByTestId('tariff-preview');
  const line = preview.getByTestId('preview-line').first();
  await preview.getByLabel('Destination district').selectOption({ label: 'Dhaka' });
  await preview.getByLabel('Order subtotal (taka)').fill('1000');
  await line.getByLabel('Unit weight (kg)').fill('0.5');
  await preview.getByRole('button', { name: 'Price this basket' }).click();
  await preview.getByTestId('preview-total').waitFor();
  r.check('preview: 0.5 kg to Dhaka is Priced at 60.00 (band 1)', (await preview.getByTestId('preview-outcome').innerText()).startsWith('Priced') && (await preview.getByTestId('preview-total').innerText()) === '60.00');
  await preview.getByLabel('Order subtotal (taka)').fill('6000');
  await preview.getByRole('button', { name: 'Price this basket' }).click();
  await preview.getByText('(free shipping applied)').waitFor();
  r.check('preview: subtotal over the threshold applies free shipping (0.00)', (await preview.getByTestId('preview-total').innerText()) === '0.00');
  await line.getByLabel('Unit weight (kg)').fill('30');
  await preview.getByRole('button', { name: 'Price this basket' }).click();
  await preview.getByText(/Quote only/).waitFor();
  r.check('preview: a 30 kg unit is Quote only with its reason', (await preview.getByTestId('preview-result').innerText()).includes('heavier than the heavy-unit limit'));
  await preview.getByLabel('Destination district').selectOption({ label: 'Chattogram' });
  await line.getByLabel('Unit weight (kg)').fill('1');
  await preview.getByRole('button', { name: 'Price this basket' }).click();
  await preview.getByText(/^No zone/).waitFor();
  r.check('preview: an uncovered district is No zone', true);
  await line.getByLabel('Unit weight (kg)').fill('1.2345');
  await preview.getByRole('button', { name: 'Price this basket' }).click();
  await preview.getByText(/unit weight is 0.001 to 1,000 kg/).waitFor();
  r.check('preview: more than 3 decimals of kg is refused before sending', true);
  await p.screenshot({ path: `${SHOTS}/e8-4b-editor-preview-1280.png`, fullPage: true });

  // ---- edit a band and save; the preview sees the new fee
  await p.getByLabel('Band 1 fee (taka)').fill('70');
  await p.getByRole('button', { name: 'Save changes' }).click();
  await p.getByTestId('saved').waitFor();
  await preview.getByLabel('Destination district').selectOption({ label: 'Dhaka' });
  await preview.getByLabel('Order subtotal (taka)').fill('100');
  await line.getByLabel('Unit weight (kg)').fill('0.5');
  await preview.getByRole('button', { name: 'Price this basket' }).click();
  await preview.getByText(/^Priced/).waitFor();
  r.check('edit: band 1 fee 70 saved and priced by the server (70.00)', (await preview.getByTestId('preview-total').innerText()) === '70.00');
  await p.reload();
  r.check('edit: the new fee survives a reload', (await p.getByLabel('Band 1 fee (taka)').inputValue()) === '70');

  // ---- server-side field error is mapped onto the field: band weight over 1000 kg is stopped by the client, so force a server 400 with a
  // district that does not exist via a mocked 400 on PUT (mocked, marked)
  await p.route('**/api/v1/admin/shipping/zones/*', async (route) => {
    if (route.request().method() !== 'PUT') return route.fallback();
    await route.fulfill({
      status: 400, contentType: 'application/json',
      headers: { 'access-control-allow-origin': WEB, 'access-control-allow-credentials': 'true' },
      body: JSON.stringify({ type: 'x', title: 'x', status: 400, detail: 'x', error: { code: 'VALIDATION_FAILED', message: 'The zone is not valid.', details: [{ field: 'rates.bands[1].feeMinor', message: 'Fee is above the cap.' }] } })
    });
  });
  await p.getByLabel('Band 2 fee (taka)').fill('95');
  await p.getByRole('button', { name: 'Save changes' }).click();
  await p.getByText('Fee is above the cap.').first().waitFor();
  r.check('mocked 400 with details: message shown under band 2 fee, edits kept', (await p.getByLabel('Band 2 fee (taka)').inputValue()) === '95' && (await p.getByTestId('save-failure').count()) === 1);
  await p.unroute('**/api/v1/admin/shipping/zones/*');
  await p.reload();
  await p.getByLabel('Band 1 fee (taka)').waitFor();

  // ---- stale version: change the zone behind the editor's back through the API
  const cur = (await apiCall(adminToken, 'GET', `/api/v1/admin/shipping/zones/${zoneId}`)).json.data;
  const body = { nameEn: 'Inside Dhaka (renamed elsewhere)', nameBn: cur.nameBn, isActive: true, districtIds: cur.districts.map((d) => d.id), rates: cur.rates, expectedVersion: cur.version };
  const bump = await apiCall(adminToken, 'PUT', `/api/v1/admin/shipping/zones/${zoneId}`, body);
  r.check('stale: a second API call changed the zone', bump.status === 200, String(bump.status));
  await p.getByLabel('Band 3 fee (taka)').fill('160');
  await p.getByRole('button', { name: 'Save changes' }).click();
  await p.getByTestId('save-failure').waitFor();
  r.check('stale: the failure text', (await p.getByTestId('save-failure').innerText()).includes('Someone else changed this zone'), await p.getByTestId('save-failure').innerText());
  r.check('stale: conflict message with Reload, unsaved edit kept on screen', (await p.getByLabel('Band 3 fee (taka)').inputValue()) === '160' && (await p.getByRole('button', { name: 'Reload' }).count()) === 1);
  await p.getByRole('button', { name: 'Reload' }).click();
  await p.waitForFunction(() => document.querySelector('input[aria-label="Band 3 fee (taka)"]')?.value === '150.50');
  r.check('stale: Reload shows the other change and drops the old edit', (await p.getByLabel('Name (English)').inputValue()) === 'Inside Dhaka (renamed elsewhere)');

  // ---- delete needs inactive
  r.check('delete: disabled while the zone is active', await p.getByRole('button', { name: 'Delete zone' }).isDisabled());
  await p.getByLabel('Active (orders to its districts are priced from this zone)').uncheck();
  await p.getByRole('button', { name: 'Save changes' }).click();
  await p.getByTestId('saved').waitFor();
  await p.getByRole('button', { name: 'Delete zone' }).click();
  await p.getByRole('dialog').getByText('Delete zone inside-dhaka?').waitFor();
  await p.getByRole('dialog').getByRole('button', { name: 'Delete zone' }).click();
  await p.waitForURL(/\/shipping\/zones$/);
  await p.getByTestId('no-zone-banner').waitFor();
  r.check('delete: the zone is gone and the production banner is back', (await p.getByTestId('zone-table').innerText()).includes('other-zone') && !(await p.getByTestId('zone-table').innerText()).includes('inside-dhaka'));

  // ---- permission gating with a mocked profile (see the header comment)
  const profile = (drop) => p.route('**/api/v1/users/me', async (route) => {
    const res = await route.fetch();
    const json = await res.json();
    json.data.permissions = json.data.permissions.filter((x) => !drop.includes(x));
    json.data.roles = ['support'];
    await route.fulfill({ response: res, json });
  });
  await profile(['shipping.manage']);
  await p.goto(`${WEB}/shipping/zones`);
  await p.getByTestId('zone-table').waitFor();
  r.check('mocked Support: list has no New zone button', (await p.getByRole('link', { name: 'New zone' }).count()) === 0);
  await p.goto(`${WEB}/shipping/zones/new`);
  await p.getByText('Your role does not allow this action (shipping.manage).').waitFor();
  const otherId = other.json.data.id;
  await p.goto(`${WEB}/shipping/zones/${otherId}`);
  await p.getByTestId('read-only').waitFor();
  r.check('mocked Support: editor is read-only (no Save, no Delete, inputs read-only)', (await p.getByRole('button', { name: 'Save changes' }).count()) === 0 && (await p.getByRole('button', { name: 'Delete zone' }).count()) === 0 && (await p.getByLabel('Band 1 fee (taka)').getAttribute('readonly')) !== null);
  r.check('mocked Support: the tariff preview is still available', (await p.getByTestId('tariff-preview').count()) === 1);
  await p.unroute('**/api/v1/users/me');
  await profile(['shipping.view', 'shipping.manage']);
  await p.goto(`${WEB}/shipping/zones`);
  await p.getByText('Your role does not allow this action (shipping.view).').waitFor();
  r.check('mocked role without shipping.view: no sidebar link, page refuses', (await p.getByRole('link', { name: /Shipping zones/ }).count()) === 0);
  await p.unroute('**/api/v1/users/me');

  // ---- 360 px
  const m = await browser.newContext({ viewport: { width: 360, height: 800 } });
  await m.addCookies(await ctx.cookies());
  const mp = await m.newPage();
  await mp.goto(`${WEB}/shipping/zones`);
  await mp.getByTestId('zone-table').waitFor();
  r.check('360px: zones list has no horizontal page scroll', !(await overflow(mp)));
  await mp.goto(`${WEB}/shipping/zones/new`);
  await mp.getByRole('button', { name: 'Create zone' }).waitFor();
  r.check('360px: new zone page has no horizontal page scroll', !(await overflow(mp)));
  await mp.goto(`${WEB}/shipping/zones/${otherId}`);
  await mp.getByTestId('tariff-preview').waitFor();
  r.check('360px: zone editor with preview has no horizontal page scroll', !(await overflow(mp)));
  await mp.screenshot({ path: `${SHOTS}/e8-4b-editor-360.png`, fullPage: true });
  await m.close();

  // ---- clean up the zone made through the API
  const del = await apiCall(adminToken, 'DELETE', `/api/v1/admin/shipping/zones/${otherId}`);
  r.check('cleanup: other-zone deleted through the API', del.status === 204, String(del.status));

  r.check('no unexpected console errors', errors.length === 0, errors.join(' | ').slice(0, 400));
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error('E2E crashed:', e.message.split('\n').slice(0, 8).join('\n')); process.exit(2); });
