// C4.1/C4.2/C4.2b: product list and product editor of the staff console. Needs the API (Development, seeded catalogue:
// `Mechware.Worker seed-catalog`), the console and the dev admin. Set E2E_CONSOLE_PRODUCTS=1 to run; otherwise it exits 0.
// It EDITS seeded products (supply, order units, part number, alternatives, titles, weight, price, lifecycle): run it on a scratch
// database only (E2E_API_URL pointing at an API on that database; the API's CORS allows the console on 3000/3001). The storefront
// checks after a status change need the Worker running on the same database (it runs the outbox relay that evicts the Redis page cache).
// Real: admin sign-in with TOTP, server-side list filters (text, brand picker, status, supply), open, basics save + persistence,
// markup refusal under the field, stale edit through a second API call, weight/price edit with the price confirmation, the
// storefront API after a price change and after archive/restore, publish (or the server's refusal text), supply, alternatives,
// order units, part number, seller/buyer refusals (UI and API), axe on pages and dialogs, 360 px.
// Mocked (and marked): a 400 field error and a 409 on the old PIM endpoints (PUT answered by the test) and the Support
// permission gating (`GET /users/me` minus `catalog.manage`; the dev database has no Support user).
// Not covered because the API has no such endpoint: attributes editor, unpublish (the domain has none; archive + restore instead).
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
const wideNodes = (p) => p.evaluate(() => [...document.querySelectorAll('body *')].filter((e) => e.getBoundingClientRect().right > document.documentElement.clientWidth + 1 && !e.closest('.overflow-x-auto')).slice(0, 4).map((e) => `${e.tagName}.${String(e.className).slice(0, 60)}`).join(' | '));
/** Storefront reads are cached in Redis and evicted through the outbox relay, so a staff change shows up there a moment later: poll. */
async function eventually(fn, ms = 90000) { const end = Date.now() + ms; for (;;) { const v = await fn(); if (v || Date.now() > end) return v; await wait(500); } }
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

  r.check('seller token: GET /admin/products (list) and GET /admin/products/{id} are 403 (real API)',
    (await apiCall(sellerToken, 'GET', '/api/v1/admin/products')).status === 403 && (await apiCall(sellerToken, 'GET', `/api/v1/admin/products/${productId}`)).status === 403);
  r.check('buyer token: POST /admin/products/{id}/archive and GET /admin/brands are 403 (real API)',
    (await apiCall(buyerToken, 'POST', `/api/v1/admin/products/${productId}/archive`, { expectedVersion: 1 })).status === 403 && (await apiCall(buyerToken, 'GET', '/api/v1/admin/brands')).status === 403);
  const detailOf = async (id) => (await apiCall(adminToken, 'GET', `/api/v1/admin/products/${id}`)).json.data;
  const storefront = (slug) => fetch(`${API}/api/v1/storefront/products/${slug}`);

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
  r.check('list: seeded products shown, 50 products per page', (await p.getByTestId('product-row').count()) >= 10 && (await p.getByTestId('page-info').innerText()).includes('Page 1 of'));
  await axeCheck(p, '/catalog/products');
  await p.screenshot({ path: `${SHOTS}/c4-1-list-1280.png`, fullPage: true });

  // server-side filters: record the list requests the page makes
  const listCalls = [];
  p.on('request', (rq) => { if (/\/api\/v1\/admin\/products\?/.test(rq.url())) listCalls.push(rq.url()); });
  const search = async (text) => { await p.getByLabel('Search title, SKU or part number').fill(text); await p.getByRole('button', { name: 'Search' }).click(); };
  const rowCount = (fn) => p.waitForFunction(fn, null, { timeout: 15000 });
  await search(ktp400.sku);
  await rowCount(() => document.querySelectorAll('[data-testid="product-row"]').length === 1);
  r.check('search by SKU finds exactly that product (server-side q)', (await p.getByTestId('product-row').innerText()).includes('KTP400') && listCalls.some((u) => u.includes('q=' + ktp400.sku)));
  await search('KTP700 Basic');
  await rowCount(() => document.querySelectorAll('[data-testid="product-row"]').length === 1 && document.body.innerText.includes('KTP700'));
  r.check('search by title finds the KTP700 panel', true);
  await search('');
  await rowCount(() => document.querySelectorAll('[data-testid="product-row"]').length >= 10);
  const firstRow = (await p.getByTestId('product-row').first().innerText());
  r.check('list columns: brand, category, seller, status, supply, SKUs, price, updated are shown', /Siemens|Fluke|AirTAC|ABB|Schneider|Omron|Festo|[A-Za-z]+/.test(firstRow) && (await p.getByRole('columnheader').allInnerTexts()).join('|') === 'Title|Brand|Category|Seller|Status|Supply|SKUs|Price (VAT incl.)|Updated');
  // brand picker (new lookup)
  const brandBox = p.getByTestId('filter-brand-picker');
  await brandBox.getByLabel('Filter by brand').fill('Siemens');
  await brandBox.getByRole('button', { name: 'Find' }).click();
  await brandBox.getByRole('button', { name: 'Use Siemens' }).click();
  await rowCount(() => { const t = [...document.querySelectorAll('[data-testid="product-row"]')]; return t.length >= 3 && t.every((x) => x.innerText.includes('Siemens')); });
  r.check('brand filter (picker, server-side brandId): Siemens rows only', listCalls.at(-1).includes('brandId=') && (await p.getByTestId('product-row').count()) >= 3);
  // category picker
  await p.getByTestId('filter-category-picker').getByLabel('Filter by category').fill('Multimeters');
  await p.getByTestId('filter-category-picker').getByRole('button', { name: 'Find' }).click();
  await Promise.all([p.waitForRequest((rq) => rq.url().includes('categoryId=')), p.getByTestId('filter-category-picker').getByRole('button', { name: /^Use / }).first().click()]);
  await p.getByTestId('empty').waitFor();
  r.check('category filter sends categoryId alongside brandId', listCalls.at(-1).includes('categoryId=') && listCalls.at(-1).includes('brandId='), listCalls.at(-1));
  await p.getByTestId('filter-category-picker').getByRole('button', { name: 'Clear category' }).click();
  await brandBox.getByRole('button', { name: 'Clear brand' }).click();
  // status + supply
  await p.getByLabel('Status', { exact: true }).selectOption('archived');
  await rowCount(() => { const t = [...document.querySelectorAll('[data-testid="product-row"]')]; return t.length >= 1 && t.length < 50 && t.every((x) => x.innerText.includes('Archived')); });
  r.check('status filter Archived (server-side): archived rows only', listCalls.at(-1).includes('status=archived') && (await p.getByTestId('product-row').count()) >= 1);
  await p.getByLabel('Status', { exact: true }).selectOption('');
  await Promise.all([p.waitForRequest((rq) => rq.url().includes('supplyStatus=obsolete')), p.getByLabel('Supply status', { exact: true }).selectOption('obsolete')]);
  r.check('supply filter sends supplyStatus', listCalls.at(-1).includes('supplyStatus=obsolete'));
  await p.getByLabel('Supply status', { exact: true }).selectOption('');
  await search('zzz-no-such-thing');
  await p.getByTestId('empty').waitFor();
  r.check('search with no hit shows an empty state', true);
  await axeCheck(p, '/catalog/products (filtered, empty)');

  // ---- open the product
  await p.getByLabel('Search title, SKU or part number').fill('KTP400');
  await p.getByRole('button', { name: 'Search' }).click();
  await p.getByRole('link', { name: /KTP400/ }).click();
  await p.waitForURL(new RegExp(`/catalog/products/${productId}$`));
  await p.getByTestId('product-title').waitFor();
  await p.getByTestId('variant-row').first().waitFor();
  r.check('edit: title, status and first SKU shown', (await p.getByTestId('product-title').innerText()).includes('KTP400') && (await p.getByTestId('product-status').innerText()) === 'Published');
  r.check('edit: weight hint names shipping', (await p.getByText('Needed for shipping prices; orders are refused without it.').count()) >= 1);
  r.check('edit: list price is an editable VAT-inclusive field (39100.00)', (await p.getByLabel('List price, VAT included (BDT)').first().inputValue()) === '39100.00');
  await axeCheck(p, '/catalog/products/[id]');
  await p.screenshot({ path: `${SHOTS}/c4-2-edit-1280.png`, fullPage: true });

  // ---- order units: client validation, then save
  const variant = p.getByTestId('variant-row').first(); // the order-units and part-number form of the first SKU
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

  // ======================= C4.2b: basics, variants, lifecycle on a Fluke product =======================
  const fluke = (await apiCall(adminToken, 'GET', '/api/v1/admin/products?q=Fluke%201587&status=active&pageSize=5')).json.data.items[0];
  r.check('setup: an active seeded Fluke 1587 product exists for the basics, variant and lifecycle tests', !!fluke);
  const fid = fluke.id;
  const f0 = await detailOf(fid);
  const stamp = Date.now().toString(36);
  await p.goto(`${WEB}/catalog/products`);
  await p.getByTestId('product-table').waitFor();
  await search('Fluke 1587');
  await p.getByRole('link', { name: /Fluke 1587 FC/ }).first().click();
  await p.waitForURL(new RegExp(`/catalog/products/${fid}$`));
  await p.getByTestId('sku-card').first().waitFor();
  r.check('edit: Lifecycle shows Published with an Archive button only (allowedTransitions: archive)',
    (await p.getByTestId('product-status').innerText()) === 'Published' && (await p.getByRole('button', { name: 'Archive', exact: true }).count()) === 1 && (await p.getByRole('button', { name: 'Publish', exact: true }).count()) === 0);
  await axeCheck(p, '/catalog/products/[id] (Fluke, full form)');
  await p.screenshot({ path: `${SHOTS}/c4-2b-edit-1280.png`, fullPage: true });

  // basics: client limits, then save and persistence
  await p.getByLabel('Title (English)').fill('x'.repeat(256));
  await p.getByRole('button', { name: 'Save basics' }).click();
  await p.getByText('English title is at most 255 characters.').waitFor();
  r.check('basics: a 256-character title is refused locally, nothing sent', (await detailOf(fid)).titleEn === f0.titleEn);
  const newTitle = `Fluke 1587 FC (E2E ${stamp})`;
  await p.getByLabel('Title (English)').fill(newTitle);
  await p.getByLabel('Short description (English)').fill(`Short text ${stamp}`);
  await p.getByRole('button', { name: 'Save basics' }).click();
  await p.getByTestId('basics-saved').waitFor();
  const f1 = await detailOf(fid);
  r.check('basics: English title and short description saved on the server, version moved', f1.titleEn === newTitle && f1.shortDescriptionEn === `Short text ${stamp}` && f1.rowVersion !== f0.rowVersion, String(f1.rowVersion));
  r.check('basics: slug did not change', f1.slug === f0.slug);
  await p.reload();
  await p.getByTestId('sku-card').first().waitFor();
  r.check('basics: after reload the saved title and short description are shown', (await p.getByLabel('Title (English)').inputValue()) === newTitle && (await p.getByLabel('Short description (English)').inputValue()) === `Short text ${stamp}`);

  // markup refusal: the server's field error under Description, nothing saved, edits kept
  const before = (await detailOf(fid)).descriptionHtml;
  await p.getByLabel('Description (HTML)').fill('<p>ok</p><script>alert(1)</script>');
  await p.getByLabel('Title (English)').fill(`${newTitle} unsaved`);
  await p.getByRole('button', { name: 'Save basics' }).click();
  await p.getByTestId('basics-failure').waitFor();
  const descErr = await p.locator('#' + (await p.getByLabel('Description (HTML)').getAttribute('id')) + '-error').innerText().catch(() => '');
  r.check('markup: a script tag is refused by the server with its message under the Description field', descErr.length > 0, descErr);
  const f2 = await detailOf(fid);
  r.check('markup: nothing was saved (title and description unchanged)', f2.descriptionHtml === before && f2.titleEn === newTitle);
  r.check('markup: unsaved edits stay and the Save button works again', (await p.getByLabel('Title (English)').inputValue()) === `${newTitle} unsaved` && (await p.getByRole('button', { name: 'Save basics' }).isEnabled()));
  await p.getByLabel('Description (HTML)').fill(`<p>Plain description ${stamp}</p><ul><li>one</li></ul>`);
  await p.getByLabel('Title (English)').fill(newTitle);
  await p.getByRole('button', { name: 'Save basics' }).click();
  await p.getByTestId('basics-saved').waitFor();
  r.check('markup: allowed markup saves', (await detailOf(fid)).descriptionHtml.includes(`Plain description ${stamp}`));

  // brand and category pickers: clear the brand, pick it again through the lookup
  await p.getByTestId('basics-brand-picker').getByRole('button', { name: 'Clear brand' }).click();
  await p.getByRole('button', { name: 'Save basics' }).click();
  await p.getByTestId('basics-saved').waitFor();
  r.check('brand picker: clearing the brand is saved (brandId null)', (await detailOf(fid)).brandId === null);
  await p.getByTestId('basics-brand-picker').getByLabel('Brand', { exact: true }).fill('Fluke');
  await p.getByTestId('basics-brand-picker').getByRole('button', { name: 'Find' }).click();
  await p.getByTestId('basics-brand-picker').getByRole('button', { name: 'Use Fluke' }).click();
  await p.getByTestId('basics-category-picker').getByLabel('Category', { exact: true }).fill('Multimeters');
  await p.getByTestId('basics-category-picker').getByRole('button', { name: 'Find' }).click();
  await p.getByTestId('basics-category-picker').getByRole('button', { name: /^Use .*Digital Multimeters/ }).click();
  await p.getByLabel('Length (cm)').fill('12.5');
  await p.getByRole('button', { name: 'Save basics' }).click();
  await p.getByTestId('basics-saved').waitFor();
  const f3 = await detailOf(fid);
  r.check('pickers: brand Fluke re-chosen and category Digital Multimeters saved through the lookups, length 12.5 cm', f3.brandId === f0.brandId && f3.categoryName === 'Digital Multimeters' && f3.lengthCm === 12.5, `${f3.brandName} / ${f3.categoryName} / ${f3.lengthCm}`);
  await p.getByLabel('Length (cm)').fill('0');
  await p.getByRole('button', { name: 'Save basics' }).click();
  await p.getByText('A dimension is above 0 and at most 1000 cm, with at most 2 decimals.').first().waitFor();
  r.check('dimension 0 refused locally', true);
  await p.getByLabel('Length (cm)').fill('12.5');

  // stale: a second API call changes the product, then the UI save is refused with Reload
  const cur = await detailOf(fid);
  const other = await apiCall(adminToken, 'PUT', `/api/v1/admin/products/${fid}`, {
    titleEn: `${newTitle} by someone else`, titleBn: cur.titleBn, shortDescriptionEn: cur.shortDescriptionEn, shortDescriptionBn: cur.shortDescriptionBn, descriptionHtml: cur.descriptionHtml,
    brandId: cur.brandId, categoryId: cur.categoryId, expectedVersion: cur.rowVersion
  });
  r.check('stale: the second API call changed the title (200)', other.status === 200, String(other.status));
  await p.getByLabel('Title (English)').fill(`${newTitle} my edit`);
  await p.getByRole('button', { name: 'Save basics' }).click();
  await p.getByTestId('basics-failure').waitFor();
  const staleText = await p.getByTestId('basics-failure').innerText();
  r.check('stale: "Someone else changed this product" with a Reload button, my edit still typed', staleText.includes('Someone else changed this product') && (await p.getByLabel('Title (English)').inputValue()) === `${newTitle} my edit`);
  r.check('stale: server kept the other person\'s title', (await detailOf(fid)).titleEn === `${newTitle} by someone else`);
  await p.getByTestId('basics-failure').getByRole('button', { name: 'Reload' }).click();
  await p.waitForFunction((t) => document.querySelector('input[id]')?.closest('form') && [...document.querySelectorAll('input')].some((i) => i.value === t), `${newTitle} by someone else`);
  r.check('stale: Reload shows the server version and drops the unsaved edit', (await p.getByLabel('Title (English)').inputValue()) === `${newTitle} by someone else`);

  // variant weight: 3 decimals ok, 4 refused client-side
  const sku = p.getByTestId('variant-commercial').first();
  await sku.getByLabel('Weight (kg)').fill('0.1234');
  await sku.getByRole('button', { name: /^Save price and weight/ }).click();
  await sku.getByText('Weight can have at most 3 decimals (grams).').waitFor();
  r.check('weight: 4 decimals refused locally, nothing sent', (await detailOf(fid)).variants[0].weightKg === f0.variants[0].weightKg);
  const grams = 200 + (Date.now() % 700); // a rerun on the same scratch database must still change the weight
  const wText = `0.${grams}`;
  const wNum = grams / 1000;
  await sku.getByLabel('Weight (kg)').fill(wText);
  await sku.getByRole('button', { name: /^Save price and weight/ }).click();
  await p.getByTestId(/commercial-.*-saved/).first().waitFor();
  const f4 = await detailOf(fid);
  r.check(`weight: ${wText} kg (3 decimals) saved, price unchanged`, f4.variants[0].weightKg === wNum && f4.variants[0].priceMinor === f0.variants[0].priceMinor, String(f4.variants[0].weightKg));

  // price: confirm dialog restating old -> new, cancel keeps the old price, confirm saves it
  const oldMinor = f4.variants[0].priceMinor;
  const newMinor = oldMinor + 10000;
  const taka = (m) => `${Math.floor(m / 100).toLocaleString('en-US')}.${String(m % 100).padStart(2, '0')}`;
  await sku.getByLabel('List price, VAT included (BDT)').fill(`${Math.floor(newMinor / 100)}.${String(newMinor % 100).padStart(2, '0')}`);
  await sku.getByRole('button', { name: /^Save price and weight/ }).click();
  const dlg = p.getByRole('dialog');
  await dlg.waitFor();
  const dlgText = await dlg.innerText();
  r.check('price: the confirm dialog restates old and new price and the warning', dlgText.includes(taka(oldMinor)) && dlgText.includes(taka(newMinor)) && dlgText.includes('This changes what customers pay immediately'), dlgText.slice(0, 200).replace(/\n/g, ' '));
  await axeCheck(p, 'price confirm dialog');
  await p.screenshot({ path: `${SHOTS}/c4-2b-price-dialog.png` });
  await dlg.getByRole('button', { name: 'Cancel' }).click();
  await dlg.waitFor({ state: 'hidden' });
  r.check('price: Cancel sends nothing (server price unchanged)', (await detailOf(fid)).variants[0].priceMinor === oldMinor);
  await sku.getByRole('button', { name: /^Save price and weight/ }).click();
  await dlg.waitFor();
  await dlg.getByRole('button', { name: 'Change price' }).click();
  await p.getByTestId(/commercial-.*-saved/).first().waitFor();
  const f5 = await detailOf(fid);
  r.check('price: after Confirm the server has the new price', f5.variants[0].priceMinor === newMinor, String(f5.variants[0].priceMinor));
  const t0 = Date.now();
  const seen = await eventually(async () => { const x = await storefront(f5.slug); return x.status === 200 && (await x.text()).includes(`"amountMinor":${newMinor}`); });
  r.check('price: the storefront API shows the new price (after the cache is evicted through the outbox)', seen, `${Date.now() - t0} ms`);
  // SKU status: inactive then active again through the select (the server needs one active SKU to publish)
  await sku.getByLabel('SKU status').selectOption('inactive');
  await sku.getByRole('button', { name: /^Save price and weight/ }).click();
  await p.getByTestId(/commercial-.*-saved/).first().waitFor();
  r.check('SKU status: inactive saved', (await detailOf(fid)).variants[0].status === 'inactive');

  // lifecycle: publishing with no active SKU is refused with the server's reason
  await apiCall(adminToken, 'POST', `/api/v1/admin/products/${fid}/archive`, { expectedVersion: (await detailOf(fid)).rowVersion }).then(() => null);
  await p.reload();
  await p.getByTestId('sku-card').first().waitFor();
  r.check('lifecycle: archived (by API) shows Archived with Restore only', (await p.getByTestId('product-status').innerText()) === 'Archived' && (await p.getByRole('button', { name: 'Restore', exact: true }).count()) === 1 && (await p.getByRole('button', { name: 'Archive', exact: true }).count()) === 0);
  r.check('lifecycle: the storefront API no longer returns the archived product', await eventually(async () => (await storefront(f5.slug)).status === 404));
  await p.getByRole('button', { name: 'Restore', exact: true }).click();
  await dlg.waitFor();
  r.check('lifecycle: the Restore dialog explains it goes back to draft', (await dlg.innerText()).includes('back to draft'));
  await axeCheck(p, 'lifecycle confirm dialog');
  await dlg.getByRole('button', { name: 'Restore', exact: true }).click();
  await p.waitForFunction(() => document.querySelector('[data-testid="product-status"]')?.innerText === 'Draft');
  r.check('lifecycle: Restore moves it to Draft; buttons are now Publish and Archive', (await p.getByRole('button', { name: 'Publish', exact: true }).count()) === 1 && (await p.getByRole('button', { name: 'Archive', exact: true }).count()) === 1);
  await p.getByRole('button', { name: 'Publish', exact: true }).click();
  await dlg.waitFor();
  await dlg.getByRole('button', { name: 'Publish', exact: true }).click();
  await p.getByTestId('lifecycle-refusal').waitFor();
  const refusal = await p.getByTestId('lifecycle-refusal').innerText();
  r.check('lifecycle: Publish with the only SKU inactive is refused with the plain reason (NO_SELLABLE_VARIANT), dialog stays, product still Draft',
    refusal.includes('Make at least one SKU active first') && (await detailOf(fid)).status === 'draft', refusal);
  await dlg.getByRole('button', { name: 'Cancel' }).click();
  await dlg.waitFor({ state: 'hidden' });
  // make the SKU active again and publish
  await sku.getByLabel('SKU status').selectOption('active');
  await sku.getByRole('button', { name: /^Save price and weight/ }).click();
  await p.getByTestId(/commercial-.*-saved/).first().waitFor();
  await p.getByRole('button', { name: 'Publish', exact: true }).click();
  await dlg.waitFor();
  await dlg.getByRole('button', { name: 'Publish', exact: true }).click();
  await Promise.race([p.getByTestId('lifecycle-refusal').waitFor(), p.waitForFunction(() => document.querySelector('[data-testid="product-status"]')?.innerText === 'Published')]);
  const published = (await detailOf(fid)).status === 'active';
  if (published) {
    r.check('lifecycle: Publish works (the seeded product has media and an active SKU); status Published, storefront API returns it again', await eventually(async () => (await storefront(f5.slug)).status === 200));
  } else {
    const why = await p.getByTestId('lifecycle-refusal').innerText();
    r.check('lifecycle: Publish refused by the server with its plain reason (seeded product lacks what publishing needs): ' + why, /Add at least one (image|SKU) first|Make at least one SKU active/.test(why) && (await detailOf(fid)).status === 'draft', why);
    await dlg.getByRole('button', { name: 'Cancel' }).click();
  }
  // a stale lifecycle click: change the product through the API, then Archive from the page
  if (published) {
    const c2 = await detailOf(fid);
    await apiCall(adminToken, 'PUT', `/api/v1/admin/products/${fid}`, { titleEn: c2.titleEn, titleBn: c2.titleBn, shortDescriptionEn: c2.shortDescriptionEn, shortDescriptionBn: c2.shortDescriptionBn, descriptionHtml: c2.descriptionHtml, brandId: c2.brandId, categoryId: c2.categoryId, expectedVersion: c2.rowVersion });
    await p.getByRole('button', { name: 'Archive', exact: true }).click();
    await dlg.waitFor();
    await dlg.getByRole('button', { name: 'Archive', exact: true }).click();
    await p.getByTestId('lifecycle-refusal').waitFor();
    r.check('lifecycle: Archive on a stale page says "Someone else changed this product", nothing archived', (await p.getByTestId('lifecycle-refusal').innerText()).includes('Someone else changed this product') && (await detailOf(fid)).status === 'active');
    await dlg.getByRole('button', { name: 'Cancel' }).click();
    await dlg.waitFor({ state: 'hidden' });
    // archive for real (after Reload) and check the storefront
    await p.reload();
    await p.getByTestId('sku-card').first().waitFor();
    await p.getByRole('button', { name: 'Archive', exact: true }).click();
    await dlg.waitFor();
    await dlg.getByRole('button', { name: 'Archive', exact: true }).click();
    await p.waitForFunction(() => document.querySelector('[data-testid="product-status"]')?.innerText === 'Archived');
    r.check('lifecycle: Archive from the page: status Archived, storefront API 404', await eventually(async () => (await storefront(f5.slug)).status === 404));
    await p.getByRole('button', { name: 'Restore', exact: true }).click();
    await dlg.waitFor();
    await dlg.getByRole('button', { name: 'Restore', exact: true }).click();
    await p.waitForFunction(() => document.querySelector('[data-testid="product-status"]')?.innerText === 'Draft');
    await p.getByRole('button', { name: 'Publish', exact: true }).click();
    await dlg.waitFor();
    await dlg.getByRole('button', { name: 'Publish', exact: true }).click();
    await p.waitForFunction(() => document.querySelector('[data-testid="product-status"]')?.innerText === 'Published');
    r.check('lifecycle: restored and published again, storefront API 200', await eventually(async () => (await storefront(f5.slug)).status === 200));
  }
  // the old PIM sections still work on this product and the page version stays usable (a supply save bumps the product version)
  await p.getByLabel('Supply status', { exact: true }).selectOption('nrnd');
  await p.getByRole('button', { name: 'Save supply status' }).click();
  await p.getByTestId('supply-saved').waitFor();
  await p.getByLabel('Supply status', { exact: true }).selectOption('active');
  await p.getByRole('button', { name: 'Save supply status' }).click();
  await p.getByTestId('supply-saved').waitFor();
  await p.getByLabel('Short description (English)').fill(`After supply ${stamp}`);
  await p.getByRole('button', { name: 'Save basics' }).click();
  await p.getByTestId('basics-saved').waitFor();
  r.check('after supply saves the basics form still saves (the page picked up the new product version)', (await detailOf(fid)).shortDescriptionEn === `After supply ${stamp}`);

  // 360 px on the new screens (before the permission mock)
  const m1 = await browser.newContext({ viewport: { width: 360, height: 800 } });
  await m1.addCookies(await ctx.cookies());
  const mp1 = await m1.newPage();
  await mp1.goto(`${WEB}/catalog/products`);
  await mp1.getByTestId('product-table').waitFor();
  r.check('360px: list (filters, wide table) has no horizontal page scroll', !(await overflow(mp1)));
  await mp1.goto(`${WEB}/catalog/products/${fid}`);
  await mp1.getByTestId('sku-card').first().waitFor();
  r.check('360px: the product page with basics, variants and lifecycle has no horizontal page scroll', !(await overflow(mp1)), await wideNodes(mp1));
  await mp1.getByRole('button', { name: 'Archive', exact: true }).click();
  await mp1.getByRole('dialog').waitFor();
  r.check('360px: the confirm dialog fits', !(await overflow(mp1)), await wideNodes(mp1));
  await mp1.screenshot({ path: `${SHOTS}/c4-2b-dialog-360.png` });
  await m1.close();

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
  await mp.getByTestId('sku-card').first().waitFor();
  r.check('360px: edit page has no horizontal page scroll', !(await overflow(mp)), await wideNodes(mp));
  await mp.screenshot({ path: `${SHOTS}/c4-2-edit-360.png`, fullPage: true });
  await m.close();

  r.check('no unexpected console errors', errors.length === 0, errors.join(' | ').slice(0, 400));
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error('E2E crashed:', e.message.split('\n').slice(0, 8).join('\n')); process.exit(2); });
