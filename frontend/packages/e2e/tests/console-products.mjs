// C4.1/C4.2: product list and product information (PIM) editor of the staff console. Needs the API (Development, seeded catalogue:
// `Mechware.Worker seed-catalog`), the console and the dev admin. Set E2E_CONSOLE_PRODUCTS=1 to run; otherwise it exits 0.
// It EDITS seeded products (supply status, order units, part number, alternatives): run it on a scratch database only.
// Real: admin sign-in with TOTP, search by SKU and by title, brand filter, open, edit order units and part number, supply status
// with successor, alternatives, persistence after reload, storefront API shows the new order units, seller refusals (UI and API).
// Mocked (and marked): the 409 CONCURRENT_MODIFICATION and a 400 field error (PUT answered by the test), and the Support permission
// gating (`GET /users/me` minus `catalog.manage`; the dev database has no Support user).
// Not covered because the API has no such endpoints: title/description edit, attributes, weight, publish/unpublish/archive.
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, CONSOLE as WEB, SHOTS, USERS, apiCall, apiLogin, launch, reporter, totp } from './lib.mjs';

if (process.env.E2E_CONSOLE_PRODUCTS !== '1') {
  console.log('SKIP  console products E2E (set E2E_CONSOLE_PRODUCTS=1 to run it)');
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
  const partNumber = `6AV2123-2DB03-0AX0 / T${Date.now().toString(36)}`; // unique per run so a rerun on the same scratch database still changes something
  // ---- setup through the API
  const usedCode = await totp(USERS.admin.totpSecret);
  const login = await fetch(`${API}/api/v1/auth/login`, {
    method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ identifier: USERS.admin.email, password: USERS.admin.password, totpCode: usedCode })
  });
  const adminToken = (await login.json()).data.accessToken;
  const sellerToken = await apiLogin(USERS.sellerAlpha);
  const buyerToken = await apiLogin(USERS.buyer);
  const find = async (q) => (await apiCall(adminToken, 'GET', `/api/v1/admin/variants?q=${encodeURIComponent(q)}&pageSize=50`)).json.data.items;
  const ktp400 = (await find('KTP400'))[0];
  const ktp700 = (await find('KTP700'))[0];
  const ktp900 = (await find('KTP900'))[0];
  r.check('setup: seeded Siemens KTP400/KTP700/KTP900 exist', !!(ktp400 && ktp700 && ktp900));
  const productId = ktp400.productId;
  // A successor must be a product of the same seller, published and in supply: find one the server accepts (and put the product back).
  const supplyPut = (succ) => apiCall(adminToken, 'PUT', `/api/v1/admin/products/${productId}/supply`, succ ? { supplyStatus: 'nrnd', successorProductId: succ.productId } : { supplyStatus: 'active', clearSuccessor: true });
  const ktp700Refused = (await supplyPut(ktp700)).status === 400;
  let succ = null;
  for (const c of await find('Siemens')) {
    if (c.productId === productId || c.productStatus.toLowerCase() !== 'active') continue;
    if ((await supplyPut(c)).status === 200) { succ = c; break; }
  }
  await supplyPut(null);
  await apiCall(adminToken, 'PUT', `/api/v1/admin/products/${productId}/alternatives`, { alternativeProductIds: [] }); // a rerun on the same scratch database starts clean
  r.check('setup: a same-seller successor exists for KTP400 (' + (succ?.sku ?? 'none') + '); KTP700 ' + (ktp700Refused ? 'is refused by the server' : 'is accepted'), !!succ);
  r.check('seller token: GET /admin/products/{id}/pim is 403 (real API)', (await apiCall(sellerToken, 'GET', `/api/v1/admin/products/${productId}/pim`)).status === 403);
  r.check('buyer token: PUT /admin/products/{id}/supply is 403 (real API)', (await apiCall(buyerToken, 'PUT', `/api/v1/admin/products/${productId}/supply`, { supplyStatus: 'nrnd' })).status === 403);

  const browser = await launch();
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const p = await ctx.newPage();
  const errors = [];
  p.on('console', (m) => { if (m.type() === 'error' && !m.text().includes('Failed to load resource')) errors.push(m.text()); });
  p.on('pageerror', (e) => errors.push('pageerror: ' + e.message));

  // ---- seller: no link, pages refuse
  await signIn(p, USERS.sellerAlpha.email, USERS.sellerAlpha.password);
  await p.waitForURL(`${WEB}/`);
  await p.getByText('Operations console').first().waitFor();
  r.check('seller sees no Products link', (await p.getByRole('link', { name: /^Products/ }).count()) === 0);
  await p.goto(`${WEB}/catalog/products`);
  await p.getByText('Your role does not allow this action (catalog.manage).').waitFor();
  await p.goto(`${WEB}/catalog/products/${productId}`);
  await p.getByText('Your role does not allow this action (catalog.manage).').waitFor();
  r.check('seller opening the list and an edit page is told no', true);
  await p.getByRole('button', { name: 'Sign out' }).click();
  await p.waitForURL(/\/login$/);

  // ---- admin
  await signIn(p, USERS.admin.email, USERS.admin.password);
  let code = await totp(USERS.admin.totpSecret);
  while (code === usedCode) { await wait(1000); code = await totp(USERS.admin.totpSecret); }
  await p.getByLabel('6-digit authenticator code or single-use recovery code').fill(code);
  await p.getByRole('button', { name: 'Verify and unlock console' }).click();
  await p.waitForURL(`${WEB}/`);
  r.check('sidebar groups Products and Bulk catalogue import under Catalogue', (await p.getByRole('navigation', { name: 'Modules' }).getByText('Catalogue', { exact: true }).count()) === 1);
  await p.getByRole('link', { name: /^Products/ }).first().click();
  await p.waitForURL(/\/catalog\/products$/);
  await p.getByTestId('product-table').waitFor();
  r.check('list: seeded products shown, 50 SKUs per page', (await p.getByTestId('product-row').count()) >= 10 && (await p.getByTestId('page-info').innerText()).includes('Page 1 of'));
  await axeCheck(p, '/catalog/products');
  await p.screenshot({ path: `${SHOTS}/c4-1-list-1280.png`, fullPage: true });

  // search by SKU
  await p.getByLabel('Search title, SKU or part number').fill(ktp400.sku);
  await p.getByRole('button', { name: 'Search' }).click();
  await p.waitForFunction(() => document.querySelectorAll('[data-testid="product-row"]').length === 1);
  r.check('search by SKU finds exactly that product', (await p.getByTestId('product-row').innerText()).includes('KTP400'));
  // search by title
  await p.getByLabel('Search title, SKU or part number').fill('KTP700 Basic');
  await p.getByRole('button', { name: 'Search' }).click();
  await p.waitForFunction(() => document.querySelectorAll('[data-testid="product-row"]').length === 1 && document.body.innerText.includes('KTP700'));
  r.check('search by title finds the KTP700 panel', true);
  // brand filter
  await p.getByLabel('Search title, SKU or part number').fill('Siemens');
  await p.getByRole('button', { name: 'Search' }).click();
  await p.waitForFunction(() => document.querySelectorAll('[data-testid="product-row"]').length >= 3);
  await p.getByLabel('Brand (this page)').selectOption('Siemens');
  const rows = await p.getByTestId('product-row').allInnerTexts();
  r.check('brand filter keeps Siemens rows only', rows.length >= 3 && rows.every((t) => t.includes('Siemens')), String(rows.length));
  await p.getByLabel('Status (this page)').selectOption('archived');
  await p.waitForFunction(() => { const t = [...document.querySelectorAll('[data-testid="product-row"]')]; return t.length >= 1 && t.length < 10; });
  const archived = await p.getByTestId('product-row').allInnerTexts();
  r.check('status filter Archived: only archived rows remain', archived.length >= 1 && archived.every((t) => t.includes('Archived')), String(archived.length));
  await p.getByLabel('Status (this page)').selectOption('rejected');
  await p.getByTestId('empty').waitFor();
  r.check('status filter with no match shows the empty message', true);
  await p.getByLabel('Status (this page)').selectOption('');
  await p.getByLabel('Search title, SKU or part number').fill('zzz-no-such-thing');
  await p.getByRole('button', { name: 'Search' }).click();
  await p.getByText('No product matches "zzz-no-such-thing".').waitFor();
  r.check('search with no hit shows an empty state', true);

  // ---- open the product
  await p.getByLabel('Search title, SKU or part number').fill('KTP400');
  await p.getByRole('button', { name: 'Search' }).click();
  await p.getByRole('link', { name: /KTP400/ }).click();
  await p.waitForURL(new RegExp(`/catalog/products/${productId}$`));
  await p.getByTestId('product-title').waitFor();
  await p.getByTestId('variant-row').first().waitFor();
  r.check('edit: title, status and first SKU shown', (await p.getByTestId('product-title').innerText()).includes('KTP400') && (await p.getByTestId('product-status').innerText()) === 'Published');
  r.check('edit: weight hint names shipping and the missing endpoint', (await p.getByTestId('weight-note').innerText()).includes('orders are refused without it'));
  await p.getByText(/List price [\d,]+\.\d{2}/).waitFor();
  r.check('edit: list price shown VAT-inclusive from the lookup (39,100.00)', (await p.getByText('List price 39,100.00').count()) === 1);
  await axeCheck(p, '/catalog/products/[id]');
  await p.screenshot({ path: `${SHOTS}/c4-2-edit-1280.png`, fullPage: true });

  // ---- order units: client validation, then save
  const variant = p.getByTestId('variant-row').first();
  await variant.getByLabel('Minimum order quantity').fill('12');
  await variant.getByLabel('Pack size').fill('5');
  await variant.getByRole('button', { name: /^Save / }).click();
  await variant.getByText('The minimum order quantity must be a whole number of packs.').waitFor();
  r.check('units: 12 with packs of 5 is refused locally, edits kept', (await variant.getByLabel('Minimum order quantity').inputValue()) === '12');
  await variant.getByLabel('Minimum order quantity').fill('10');
  await variant.getByLabel('Manufacturer part number').fill(partNumber);
  await variant.getByRole('button', { name: /^Save / }).click();
  await variant.getByTestId(`variant-${ktp400.sku}-saved`).waitFor();
  const pim1 = (await apiCall(adminToken, 'GET', `/api/v1/admin/products/${productId}/pim`)).json.data;
  r.check('units: server has MOQ 10, pack 5 and the new part number', pim1.variants[0].minOrderQty === 10 && pim1.variants[0].packSize === 5 && pim1.variants[0].manufacturerPartNumber === partNumber, JSON.stringify(pim1.variants[0]));
  const sf = await fetch(`${API}/api/v1/storefront/products/${pim1.slug}`);
  const sfText = await sf.text();
  r.check('storefront product API shows the new order units (10 / 5)', sf.status === 200 && /"minOrderQty":10/.test(sfText) && /"packSize":5/.test(sfText), String(sf.status));
  // server-side refusal shown under the field: a part number of one letter passes nothing locally? "A" fails locally; use the mocked 400 below.

  // ---- supply status and successor
  await p.getByLabel('Supply status', { exact: true }).selectOption('nrnd');
  const pick = async (sku) => {
    await p.getByTestId('successor-picker').getByLabel('Find a successor (title, SKU or part number)').fill(sku);
    await p.getByTestId('successor-picker').getByRole('button', { name: 'Find' }).click();
    await p.getByTestId('successor-picker').getByRole('button', { name: 'Use' }).first().click();
    await p.getByTestId('successor-chosen').waitFor();
  };
  if (ktp700Refused) {
    await pick(ktp700.sku);
    await p.getByRole('button', { name: 'Save supply status' }).click();
    await p.getByTestId('supply-failure').waitFor();
    r.check('supply: the server refusal (successor of another seller) is shown, nothing saved, edits kept', (await p.getByTestId('supply-failure').innerText()).length > 0 && (await p.getByLabel('Supply status', { exact: true }).inputValue()) === 'nrnd' && (await p.getByTestId('successor-chosen').count()) === 1, await p.getByTestId('supply-failure').innerText());
  }
  await pick(succ.sku);
  await p.getByLabel('The successor is a drop-in replacement').check();
  await p.getByLabel('Compatibility note').fill('Same cut-out, larger screen');
  await p.getByRole('button', { name: 'Save supply status' }).click();
  await p.getByTestId('supply-saved').waitFor();
  const pim2 = (await apiCall(adminToken, 'GET', `/api/v1/admin/products/${productId}/pim`)).json.data;
  r.check('supply: NRND with a same-seller drop-in successor and note persisted', pim2.supplyStatus === 'nrnd' && pim2.successor?.productId === succ.productId && pim2.successor.isDropIn === true && pim2.successor.note === 'Same cut-out, larger screen', JSON.stringify(pim2.successor));
  // local rule: Active cannot keep a successor
  await p.getByLabel('Supply status', { exact: true }).selectOption('active');
  await p.getByRole('button', { name: 'Save supply status' }).click();
  await p.getByText('Only a product that is being phased out can name a successor.').waitFor();
  r.check('supply: Active with a successor is refused locally, nothing sent', (await apiCall(adminToken, 'GET', `/api/v1/admin/products/${productId}/pim`)).json.data.supplyStatus === 'nrnd');
  await p.getByLabel('Supply status', { exact: true }).selectOption('nrnd');

  // ---- alternatives
  await p.getByTestId('alt-picker').getByLabel('Add an alternative (title, SKU or part number)').fill('KTP900');
  await p.getByTestId('alt-picker').getByRole('button', { name: 'Find' }).click();
  await p.getByTestId('alt-picker').getByRole('button', { name: 'Use' }).first().click();
  await p.getByTestId('alt-picker').getByLabel('Add an alternative (title, SKU or part number)').fill('KTP700');
  await p.getByTestId('alt-picker').getByRole('button', { name: 'Find' }).click();
  await p.getByTestId('alt-picker').getByRole('button', { name: 'Use' }).first().click();
  await p.getByRole('button', { name: /^Move .*KTP700.* up$/ }).click();
  await p.getByRole('button', { name: 'Save alternatives' }).click();
  await p.getByTestId('alts-saved').waitFor();
  const pim3 = (await apiCall(adminToken, 'GET', `/api/v1/admin/products/${productId}/pim`)).json.data;
  r.check('alternatives: KTP700 first, KTP900 second, persisted in order', pim3.alternatives.map((a) => a.productId).join() === [ktp700.productId, ktp900.productId].join(), pim3.alternatives.map((a) => a.titleEn).join(' | '));

  // ---- reload shows the persisted values
  await p.reload();
  await p.getByTestId('variant-row').first().waitFor();
  r.check('reload: MOQ 10, pack 5, part number, NRND, successor and 2 alternatives are all still there',
    (await p.getByLabel('Minimum order quantity').inputValue()) === '10' && (await p.getByLabel('Pack size').inputValue()) === '5'
    && (await p.getByLabel('Manufacturer part number').inputValue()) === partNumber
    && (await p.getByLabel('Supply status', { exact: true }).inputValue()) === 'nrnd' && (await p.getByTestId('successor-chosen').count()) === 1
    && (await p.getByTestId('alts-list').locator('li').count()) === 2);
  // clearing the successor sends clearSuccessor
  await p.getByRole('button', { name: 'Remove successor' }).click();
  await p.getByRole('button', { name: 'Save supply status' }).click();
  await p.getByTestId('supply-saved').waitFor();
  r.check('supply: Remove successor clears it on the server (explicit clearSuccessor)', (await apiCall(adminToken, 'GET', `/api/v1/admin/products/${productId}/pim`)).json.data.successor === null);

  // ---- mocked server answers: field error (400) and concurrency (409); edits are kept
  await p.route('**/api/v1/admin/products/*/variants/*/order-units', async (route) => {
    await route.fulfill({
      status: 400, contentType: 'application/json',
      headers: { 'access-control-allow-origin': WEB, 'access-control-allow-credentials': 'true' },
      body: JSON.stringify({ type: 'x', title: 'x', status: 400, detail: 'x', error: { code: 'VALIDATION_FAILED', message: 'The order units are not valid.', details: [{ field: 'packSize', message: 'Pack too large for one line (mocked).' }] } })
    });
  });
  await p.getByLabel('Pack size').fill('7');
  await p.getByLabel('Minimum order quantity').fill('14');
  await p.getByRole('button', { name: /^Save MW-/ }).click();
  await p.getByText('Pack too large for one line (mocked).').waitFor();
  r.check('mocked 400 with details: message under Pack size, edits kept, button enabled again', (await p.getByLabel('Pack size').inputValue()) === '7' && (await p.getByRole('button', { name: /^Save MW-/ }).isEnabled()));
  await p.unroute('**/api/v1/admin/products/*/variants/*/order-units');
  await p.route('**/api/v1/admin/products/*/alternatives', async (route) => {
    await route.fulfill({
      status: 409, contentType: 'application/json',
      headers: { 'access-control-allow-origin': WEB, 'access-control-allow-credentials': 'true' },
      body: JSON.stringify({ type: 'x', title: 'x', status: 409, detail: 'x', error: { code: 'CONCURRENT_MODIFICATION', message: 'Someone else changed this product at the same time. Reload and retry.' } })
    });
  });
  await p.getByRole('button', { name: /^Remove .*KTP900/ }).click();
  await p.getByRole('button', { name: 'Save alternatives' }).click();
  await p.getByTestId('alts-failure').waitFor();
  r.check('mocked 409: "Someone else changed this product", Reload button, unsaved edit kept', (await p.getByTestId('alts-failure').innerText()).includes('Someone else changed this product') && (await p.getByTestId('alts-list').locator('li').count()) === 1);
  await p.unroute('**/api/v1/admin/products/*/alternatives');
  await p.getByTestId('alts-failure').getByRole('button', { name: 'Reload' }).click();
  await p.waitForFunction(() => document.querySelector('[data-testid="alts-list"]')?.querySelectorAll('li').length === 2);
  r.check('409 Reload: server version back (2 alternatives), the pack edit is dropped', (await p.getByLabel('Pack size').inputValue()) === '5');

  // ---- permission gating with a mocked profile (the dev database has no Support user)
  await p.route('**/api/v1/users/me', async (route) => {
    const res = await route.fetch();
    const json = await res.json();
    json.data.permissions = json.data.permissions.filter((x) => x !== 'catalog.manage');
    json.data.roles = ['support'];
    await route.fulfill({ response: res, json });
  });
  await p.goto(`${WEB}/catalog/products`);
  await p.getByText('Your role does not allow this action (catalog.manage).').waitFor();
  r.check('mocked Support: no Products link, list refuses', (await p.getByRole('link', { name: /^Products/ }).count()) === 0);
  await p.unroute('**/api/v1/users/me');

  // ---- 360 px
  const m = await browser.newContext({ viewport: { width: 360, height: 800 } });
  await m.addCookies(await ctx.cookies());
  const mp = await m.newPage();
  await mp.goto(`${WEB}/catalog/products`);
  await mp.getByTestId('product-table').waitFor();
  r.check('360px: list has no horizontal page scroll', !(await overflow(mp)));
  await mp.goto(`${WEB}/catalog/products/${productId}`);
  await mp.getByTestId('variant-row').first().waitFor();
  r.check('360px: edit page has no horizontal page scroll', !(await overflow(mp)));
  await mp.screenshot({ path: `${SHOTS}/c4-2-edit-360.png`, fullPage: true });
  await m.close();

  r.check('no unexpected console errors', errors.length === 0, errors.join(' | ').slice(0, 400));
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error('E2E crashed:', e.message.split('\n').slice(0, 8).join('\n')); process.exit(2); });
