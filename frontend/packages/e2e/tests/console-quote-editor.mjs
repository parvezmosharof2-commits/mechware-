// D4.16: the console quote draft editor (/quotes/[id]/edit): header terms, lines added by part search, net price in taka (poisha on the wire), live price check
// (debounced; deep discount, below-floor line, reason field, problems and flags in words), Save with rowVersion, a stale save from a second browser (conflict
// message, edits kept, Load saved), fixed delivery with site and one fee per seller, closed behind the open page, permissions, cost column only for a user with
// pricing.view_cost. SCRATCH DB only. Same setup as console-quotes.mjs: API on a scratch DB mw_* (migrated, catalogue seeded, demo accounts), the console built
// against it, a CORS proxy in front of the API, staff cloned from the admin row with the same TOTP secret (support.e2e@, finance.a.e2e@, admin2.e2e@), and
// E2E_SQL_CMD = a command that reads SQL on stdin against the SCRATCH database, e.g. `docker exec -i mechware_postgres psql -U mechware -d mw_d416 -At -q -v ON_ERROR_STOP=1`.
// Set E2E_CONSOLE_QUOTE_EDITOR=1 to run; otherwise it exits 0 without doing anything. Docs: docs/modules/D4.md HANDOFF D4.16.
import { execSync } from 'node:child_process';
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, CONSOLE as WEB, USERS, apiCall, apiLogin, launch, reporter, totp, uniqueBin } from './lib.mjs';

if (process.env.E2E_CONSOLE_QUOTE_EDITOR !== '1') {
  console.log('SKIP  console quote editor E2E (set E2E_CONSOLE_QUOTE_EDITOR=1 to run it)');
  process.exit(0);
}
if (!process.env.E2E_SQL_CMD || !/-d mw_\w+/.test(process.env.E2E_SQL_CMD)) throw new Error('E2E_SQL_CMD must be a command against a scratch database (-d mw_*)');

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
const person = (email) => ({ email, password: PASSWORD, totpSecret: SECRET });
const PEOPLE = {
  support: person('support.e2e@mechware.com.bd'),
  finance: person('finance.a.e2e@mechware.com.bd'),
  admin: person('admin2.e2e@mechware.com.bd')
};

/** A code is single-use per account: wait for a 30 s step this account has not used yet. */
const lastStep = new Map();
async function freshCode(p) {
  for (;;) {
    const step = Math.floor(Date.now() / 30000);
    if (step > (lastStep.get(p.email) ?? -1)) { lastStep.set(p.email, step); return totp(p.totpSecret); }
    await sleep(1000);
  }
}
async function apiToken(p) {
  let body;
  for (let attempt = 0; attempt < 8; attempt++) {
    const res = await fetch(`${API}/api/v1/auth/login`, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ identifier: p.email, password: p.password, totpCode: await freshCode(p) })
    });
    body = await res.json();
    if (res.ok && body.data?.accessToken) return body.data.accessToken;
    await sleep(5000);
  }
  throw new Error(`API login for ${p.email} failed: ${JSON.stringify(body)}`);
}
async function consoleSignIn(page, p) {
  await page.goto(`${WEB}/login`, { timeout: 120000, waitUntil: 'networkidle' });
  await page.getByLabel('Staff or seller email').fill(p.email);
  await page.getByLabel('Password').fill(p.password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.getByLabel('6-digit authenticator code or single-use recovery code').fill(await freshCode(p));
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
let keySeq = 0;
const key = (what) => `e2e-d416-${tag}-${what}-${++keySeq}`;

const send = (token, method, path, body, headers = {}) =>
  fetch(`${API}${path}`, { method, headers: { 'content-type': 'application/json', authorization: `Bearer ${token}`, ...headers }, body: body ? JSON.stringify(body) : undefined })
    .then(async (res) => ({ status: res.status, json: await res.json().catch(() => ({})) }));
const toTaka = (minor) => `${Math.floor(minor / 100)}.${String(minor % 100).padStart(2, '0')}`;
const waitPreview = (p) => p.getByTestId('preview-state').filter({ hasText: 'Up to date' }).waitFor({ timeout: 20000 });
const activeTestId = (p) => p.evaluate(() => document.activeElement?.getAttribute('data-testid'));

(async () => {
  // ---------------- setup through the real API
  const adminToken = await apiToken(PEOPLE.admin);
  const buyer = await apiLogin(USERS.buyer);
  const reg = await apiCall(buyer, 'POST', '/api/v1/companies', { legalName: `Quote Editor Works ${tag} Ltd`, tradeLicenceNo: 'TL-D416', bin: uniqueBin() });
  const companyId = reg.json.data.id;
  const act = await apiCall(adminToken, 'POST', `/api/v1/admin/companies/${companyId}/activate`);
  const buyerId = (await apiCall(buyer, 'GET', '/api/v1/users/me')).json.data.id;
  const own = (await apiCall(buyer, 'GET', `/api/v1/companies/${companyId}/members`)).json.data.find((x) => x.userId === buyerId);
  await apiCall(buyer, 'PUT', `/api/v1/companies/${companyId}/members/${own.id}`, { roles: 10, spendLimitMinor: null, costCentreIds: [], isActive: true });
  const site = await apiCall(buyer, 'POST', `/api/v1/companies/${companyId}/sites`, { name: 'Gazipur Plant', contactName: 'Sabbir', contactPhone: '01711000000', line1: 'Plot 5, BSCIC Area', upazila: 'Tongi', district: 'Gazipur', division: 'Dhaka', isDefaultDelivery: true });
  const siteId = site.json.data.id;
  r.check('setup: an active company with a delivery site', reg.status === 201 && act.status === 200 && site.status < 300, `${reg.status}/${act.status}/${site.status}`);

  // three active in-stock variants of ONE seller (one delivery fee); the third gets minimum order 5 and pack size 5 for the quantity error
  const rows = sql(`select distinct v.id, v.sku, p.seller_id from products p join product_variants v on v.product_id = p.id join inventory_items i on i.variant_id = v.id
    where p.status = 'active' and v.status = 'active' and p.seller_id = (select p2.seller_id from products p2 join product_variants v2 on v2.product_id = p2.id join inventory_items i2 on i2.variant_id = v2.id
      where p2.status = 'active' and v2.status = 'active' group by p2.seller_id having count(*) >= 3 order by count(*) desc limit 1) order by v.id limit 3;`)
    .split('\n').map((x) => x.trim()).filter(Boolean).map((x) => x.split('|'));
  const [[vA, skuA, sellerId], [vB, skuB], [vC, skuC]] = rows;
  sql('UPDATE product_variants SET min_order_qty = 1, pack_size = 1 WHERE min_order_qty <> 1 OR pack_size <> 1;');
  sql(`UPDATE product_variants SET min_order_qty = 5, pack_size = 5 WHERE id = '${vC}';`);
  const sup = await apiToken(PEOPLE.support);
  const fin = await apiToken(PEOPLE.finance);
  const made = await send(sup, 'POST', '/api/v1/admin/quotes', { companyId, contactUserId: buyerId });
  const quoteId = made.json.data.quote.id;
  const quotePath = `/api/v1/admin/quotes/${quoteId}`;
  r.check('setup: Support made a quote for the company (draft version 1)', made.status === 201 || made.status === 200, String(made.status));
  // the buyer's own net price of each part (the resolver's reference) through a high-price probe, then a cost at 70 % of it
  const probe = await send(adminToken, 'POST', `${quotePath}/versions/1/price-preview`, { validityDays: 14, lines: [vA, vB, vC].map((v) => ({ variantId: v, quantity: 5, unitPriceMinor: 99999999 })) });
  if (probe.status !== 200) throw new Error(`probe preview failed: ${probe.status} ${JSON.stringify(probe.json).slice(0, 400)}`);
  const ref = probe.json.data.lines.map((l) => l.referenceUnitPriceMinor);
  sql(`UPDATE product_variants SET cost_price_minor = ${Math.floor(ref[0] * 0.7)} WHERE id = '${vA}'; UPDATE product_variants SET cost_price_minor = ${Math.floor(ref[1] * 0.7)} WHERE id = '${vB}';`);
  const pct = (n, p) => Math.floor((n * p) / 100);
  const priceA = pct(ref[0], 90), priceB = pct(ref[1], 75), priceC = ref[2];
  const check = await send(adminToken, 'POST', `${quotePath}/versions/1/price-preview`, { validityDays: 14, lines: [
    { variantId: vA, quantity: 1, unitPriceMinor: priceA }, { variantId: vB, quantity: 1, unitPriceMinor: priceB }] });
  r.check('setup: the real preview flags A as beyond authority and B as below the floor (so the screen has something to show)',
    check.status === 200 && check.json.data.overAuthorityLineNos.includes(1) && check.json.data.belowFloorLineNos.includes(2) && !check.json.data.belowFloorLineNos.includes(1),
    JSON.stringify(check.json.data ?? check.json).slice(0, 300));

  const browser = await launch();

  // ================= Support: build the quote in the editor
  const s = await newPage(browser);
  await consoleSignIn(s, PEOPLE.support);
  await s.goto(`${WEB}/quotes/${quoteId}`);
  await s.getByTestId('quote-number').waitFor();
  await s.getByTestId('edit-draft').click();
  await s.getByTestId('editor-heading').waitFor();
  r.check('the quote page links to the editor; the editor names quote, company and version', (await s.getByTestId('editor-heading').innerText()).includes('version 1, draft'));
  r.check('an empty draft says so; Send is disabled with the reason (add a line first); the back link is there',
    (await s.getByTestId('no-lines').count()) === 1 && !(await s.getByTestId('send-quote').isEnabled()) && (await s.getByTestId('send-note').innerText()).includes('Add at least one line') && (await s.getByRole('link', { name: 'Back to the quote' }).count()) === 1);
  await axeCheck(s, 'editor (empty draft)');

  const add = async (sku) => {
    const box = s.getByLabel('Part number, SKU or title');
    await box.fill(sku);
    await box.press('Enter');
    await s.getByRole('button', { name: `Add ${sku} to the quote` }).waitFor();
    await s.getByRole('button', { name: `Add ${sku} to the quote` }).click();
  };
  await add(skuA);
  await s.getByTestId('editor-line').first().waitFor();
  const focusLabel = await s.evaluate(() => document.activeElement?.getAttribute('aria-label') ?? '');
  r.check('after Add, the net price box of the new line has the keyboard focus', focusLabel.startsWith('Net price in taka for line 1'), focusLabel);
  await s.keyboard.type(toTaka(priceA));
  await add(skuB);
  await s.keyboard.type(toTaka(priceB));
  r.check('two lines on the screen, quantity 1 each', (await s.getByTestId('editor-line').count()) === 2 && (await s.getByTestId('line-qty').first().inputValue()) === '1');
  await s.getByLabel('Part number, SKU or title').fill(skuB);
  await s.getByLabel('Part number, SKU or title').press('Enter');
  await s.getByRole('button', { name: `Add ${skuB} to the quote` }).waitFor();
  await s.getByRole('button', { name: `Add ${skuB} to the quote` }).click();
  r.check('the same part cannot be added twice (said in words)', (await s.getByTestId('part-note').innerText()).includes('already on this quote') && (await s.getByTestId('editor-line').count()) === 2);
  await s.getByLabel('Part number, SKU or title').fill('');

  await waitPreview(s);
  const lineA = s.getByTestId('editor-line').nth(0);
  const lineB = s.getByTestId('editor-line').nth(1);
  r.check('the check flags line 1 as beyond price authority and shows "% below the buyer\'s price"',
    (await lineA.getByTestId('line-flags').innerText()).includes('price authority') && /\d+% below the buyer's price/.test(await lineA.getByTestId('line-discount').innerText()));
  r.check('the check flags line 2 as below the margin floor and shows the reason box ("Required")',
    (await lineB.getByTestId('line-flags').innerText()).includes('Below the margin floor') && (await lineB.getByTestId('reason-needed').count()) === 1 && (await lineB.getByTestId('line-reason').getAttribute('placeholder')).startsWith('Required'));
  r.check('line 1 (above the floor) has no reason box', (await lineA.getByTestId('line-reason').count()) === 0);
  r.check('the outcome says it cannot be sent yet; "needs a reason" is listed as a problem; totals show',
    (await s.getByTestId('preview-outcome').getAttribute('data-outcome')) === 'refused' && (await s.getByTestId('preview-problems').innerText()).includes('needs a reason') && (await s.getByTestId('preview-totals').innerText()).includes('Goods, net'));
  const flagsText = await s.getByTestId('preview-flags').innerText();
  r.check('flags in words: below the floor on line 2, beyond authority on line 1', flagsText.includes('Below the margin floor on line 2') && flagsText.includes('price authority allows on line 1'), flagsText);
  const refText = await lineA.getByTestId('line-reference').innerText();
  r.check('the buyer\'s own price and its source are shown per line', /\d/.test(refText) && refText.length > 6, refText);
  r.check('Support has no cost column (the API sends no cost object to Support)', (await s.getByTestId('cost-head').count()) === 0 && (await s.getByTestId('cost-cell').count()) === 0);
  await axeCheck(s, 'editor with flags');

  await lineB.getByTestId('line-reason').fill('Matching a competitor\'s offer');
  await waitPreview(s);
  r.check('with a reason the "needs a reason" problem is gone and the outcome becomes "needs a second person" (the flag stays)',
    (await s.getByTestId('preview-problems').count()) === 0 && (await s.getByTestId('preview-outcome').getAttribute('data-outcome')) === 'approval' && (await lineB.getByTestId('reason-needed').count()) === 0 && (await s.getByTestId('preview-flags').innerText()).includes('Below the margin floor'));

  await add(skuC);
  await s.keyboard.type(toTaka(priceC));
  await s.getByTestId('line-qty').nth(2).fill('3');
  await s.getByTestId('line-error').first().waitFor({ timeout: 20000 });
  const packText = await s.getByTestId('line-error').first().innerText();
  r.check('a quantity that breaks the pack size: the server\'s message shows on that line, the screen keeps the number', packText.length > 5 && (await s.getByTestId('line-qty').nth(2).inputValue()) === '3', packText);
  await s.getByTestId('line-qty').nth(2).fill('5');
  await waitPreview(s);
  r.check('quantity 5 (a full pack) clears it', (await s.getByTestId('line-error').count()) === 0);

  await s.getByTestId('validity-days').fill('45');
  r.check('45 days: the note says a second person must approve', (await s.getByTestId('validity-note').innerText()).includes('second person'));
  await s.getByTestId('validity-days').fill('91');
  r.check('91 days is refused on the field (1 to 90)', (await s.getByText('Enter whole days, 1 to 90.').count()) >= 1);
  await s.getByTestId('validity-days').fill('45');
  await s.getByTestId('payment-terms').selectOption('advance');
  await s.getByLabel('Terms (the buyer sees them)').fill(`Delivery within 7 days ${tag}`);
  await s.getByLabel('Internal note (the buyer does not see it)').fill('Competitor match');
  await waitPreview(s);
  r.check('the long validity is also flagged by the check', (await s.getByTestId('preview-flags').innerText()).includes('more than 30 days'));

  await s.getByTestId('line-price').nth(1).fill('12.345');
  await s.getByTestId('save-draft').click();
  await s.getByTestId('editor-alert').waitFor();
  const stillV1 = (await send(sup, 'GET', quotePath)).json.data.versions[0];
  r.check('three decimals: Save refuses, says what to fix, and nothing was sent', (await s.getByTestId('editor-alert').innerText()).includes('Nothing was saved') && stillV1.lines.length === 0);
  await s.getByTestId('line-price').nth(1).fill(toTaka(priceB));

  await s.getByTestId('save-draft').click();
  await s.getByTestId('editor-notice').waitFor();
  const v1 = (await send(sup, 'GET', quotePath)).json.data.versions[0];
  r.check('Save: the notice says so, and the API holds 3 lines at the typed poisha, the reason, 45 days, advance, terms',
    (await s.getByTestId('editor-notice').innerText()).includes('3 lines') && v1.lines.length === 3 && v1.lines[0].unitPriceMinor === priceA && v1.lines[1].unitPriceMinor === priceB && v1.lines[1].belowFloorReason === 'Matching a competitor\'s offer'
    && v1.lines[2].quantity === 5 && v1.validityDays === 45 && v1.paymentTerms === 'advance' && v1.termsText.includes('Delivery within 7 days') && v1.status === 'draft',
    JSON.stringify(v1.lines.map((l) => [l.unitPriceMinor, l.quantity])));
  r.check('after Save there is no "unsaved changes" note', (await s.getByTestId('dirty-note').count()) === 0);

  await s.getByRole('button', { name: new RegExp(`^Remove line 3, ${skuC}`) }).click();
  r.check('Remove drops the line and the unsaved note shows', (await s.getByTestId('editor-line').count()) === 2 && (await s.getByTestId('dirty-note').count()) === 1);
  await s.getByTestId('save-draft').click();
  await s.getByTestId('editor-notice').filter({ hasText: '2 lines' }).waitFor();
  r.check('a second save by the same editor works (it uses the new token) and the API has 2 lines', (await send(sup, 'GET', quotePath)).json.data.versions[0].lines.length === 2);

  await s.getByTestId('mode-fixed').check();
  await s.getByTestId('save-draft').click();
  await s.getByTestId('editor-alert').waitFor();
  r.check('fixed delivery without site and fee: the fields say so, nothing sent', (await s.getByText('needs the company\'s delivery site').count()) >= 1 && (await s.getByText('needs one fee for each seller').count()) >= 1);
  await s.getByTestId('site-id').fill(siteId);
  await s.getByTestId('add-fee').click();
  await s.getByLabel('Seller id for fee 1').fill(sellerId);
  await s.getByTestId('fee-amount').fill('350.50');
  await waitPreview(s);
  r.check('the check now shows the delivery and the grand total', (await s.getByTestId('preview-totals').innerText()).includes('BDT 350.50') && (await s.getByTestId('preview-totals').innerText()).includes('Total'));
  await axeCheck(s, 'editor with fixed delivery');
  await s.getByTestId('save-draft').click();
  await s.getByTestId('editor-notice').filter({ hasText: 'Saved' }).waitFor();
  const v2 = (await send(sup, 'GET', quotePath)).json.data.versions[0];
  r.check('the API has fixed delivery with the site, the ship-to copy and 350.50 delivery', v2.shippingMode === 'fixed' && v2.shipToSiteId === siteId && v2.shipTo?.line1 === 'Plot 5, BSCIC Area' && v2.shippingTotalMinor === 35050, JSON.stringify([v2.shippingMode, v2.shippingTotalMinor]));

  // ================= Admin (pricing.view_cost) in a second browser: cost column, then the stale save
  const a = await newPage(browser);
  await consoleSignIn(a, PEOPLE.admin);
  await a.goto(`${WEB}/quotes/${quoteId}/edit`);
  await a.getByTestId('editor-line').first().waitFor();
  r.check('a saved fixed delivery opens with no fee rows (the read has none): the field says a fee is needed and the check waits',
    (await a.getByText('needs one fee for each seller').count()) >= 1 && (await a.getByTestId('preview-state').innerText()).includes('Fix the marked fields'));
  await a.getByTestId('add-fee').click();
  await a.getByLabel('Seller id for fee 1').fill(sellerId);
  await a.getByTestId('fee-amount').fill('350.50');
  await a.getByTestId('cost-head').waitFor({ timeout: 30000 });
  const costTexts = await a.getByTestId('cost-cell').allInnerTexts();
  r.check('Admin (pricing.view_cost) sees the Cost / floor column with amounts for the costed parts', costTexts.length === 2 && costTexts.every((t) => /\d/.test(t)), costTexts.join(' | '));
  await axeCheck(a, 'editor with cost column');
  const rowVersionBefore = v2.rowVersion;
  await a.getByLabel('Note to the buyer').fill('Edited in the other browser');
  await a.getByTestId('save-draft').click();
  await a.getByTestId('editor-notice').waitFor();
  r.check('Admin saved first: the version\'s rowVersion moved on', (await send(sup, 'GET', quotePath)).json.data.versions[0].rowVersion !== rowVersionBefore);

  await s.getByTestId('line-qty').first().fill('7');
  await s.getByTestId('save-draft').click();
  await s.getByTestId('editor-alert').waitFor();
  const stale = await s.getByTestId('editor-alert').innerText();
  r.check('the stale save says someone else saved, and that the edits are still on the screen', stale.includes('Someone else saved') && stale.includes('still on the screen'), stale);
  r.check('the unsaved quantity 7 is still visible and the alert has the focus', (await s.getByTestId('line-qty').first().inputValue()) === '7' && (await activeTestId(s)) === 'editor-alert');
  const after = (await send(sup, 'GET', quotePath)).json.data.versions[0];
  r.check('the API still holds the other editor\'s version (quantity 1 on line 1, their note)', after.lines[0].quantity === 1 && after.buyerNote === 'Edited in the other browser');
  await axeCheck(s, 'editor with the stale alert');
  await s.getByTestId('load-saved').click();
  await s.waitForFunction(() => document.querySelector('[data-testid="editor-alert"]') === null);
  await s.getByLabel('Note to the buyer').waitFor();
  r.check('Load the saved version replaces the screen with the saved draft (their note, quantity back to 1)', (await s.getByLabel('Note to the buyer').inputValue()) === 'Edited in the other browser' && (await s.getByTestId('line-qty').first().inputValue()) === '1');

  // ================= Finance (quotes.view only)
  const f = await newPage(browser);
  await consoleSignIn(f, PEOPLE.finance);
  await f.goto(`${WEB}/quotes/${quoteId}`);
  await f.getByTestId('quote-number').waitFor();
  r.check('Finance reads the draft and is told editing needs quotes.edit; no edit link', (await f.getByTestId('edit-draft').count()) === 0 && (await f.getByTestId('editor-placeholder').innerText()).includes('quotes.edit'));
  await f.goto(`${WEB}/quotes/${quoteId}/edit`);
  await f.getByText('Your role does not allow this action (quotes.edit).').waitFor();
  r.check('Finance opening the editor URL is told no, no form is shown', (await f.getByTestId('save-draft').count()) === 0);
  const put = await send(fin, 'PUT', `${quotePath}/versions/1`, { rowVersion: 1, validityDays: 14, lines: [] });
  r.check('and the API refuses Finance\'s PUT too (403)', put.status === 403, String(put.status));

  // ================= the quote is closed behind the open editor
  await s.getByTestId('line-qty').first().fill('2');
  await s.getByTestId('add-fee').click();
  await s.getByLabel('Seller id for fee 1').fill(sellerId);
  await s.getByTestId('fee-amount').fill('350.50');
  const closed = await send(sup, 'POST', `${quotePath}/close`, { reason: 'Customer withdrew the request' });
  r.check('setup: the quote is closed through the API while the editor is open', closed.status === 200, String(closed.status));
  await s.getByTestId('save-draft').click();
  await s.getByTestId('editor-alert').waitFor();
  const goneText = await s.getByTestId('editor-alert').innerText();
  r.check('saving into a closed quote: plain words, a link to the quote, the edits stay', /closed|no longer a draft/.test(goneText) && (await s.getByRole('link', { name: 'Go to the quote' }).count()) === 1 && (await s.getByTestId('line-qty').first().inputValue()) === '2', goneText);
  await s.goto(`${WEB}/quotes/${quoteId}/edit`);
  await s.getByTestId('not-a-draft').waitFor();
  r.check('opening the editor of a closed quote says there is no draft to edit', (await s.getByTestId('not-a-draft').innerText()).includes('no draft to edit'));

  // ================= narrow screen and page errors (a fresh draft for the layout)
  const q2 = await send(sup, 'POST', '/api/v1/admin/quotes', { companyId, contactUserId: buyerId });
  await send(sup, 'PUT', `/api/v1/admin/quotes/${q2.json.data.quote.id}/versions/1`, { rowVersion: q2.json.data.versions[0].rowVersion, validityDays: 14, lines: [{ variantId: vA, quantity: 1, unitPriceMinor: ref[0] }] });
  await s.setViewportSize({ width: 360, height: 800 });
  await s.goto(`${WEB}/quotes/${q2.json.data.quote.id}/edit`);
  await s.getByTestId('editor-line').first().waitFor();
  const wide = await s.evaluate(() => [`sw=${document.documentElement.scrollWidth} cw=${document.documentElement.clientWidth}`, ...[...document.querySelectorAll('body *')].map((e) => [e, e.getBoundingClientRect().right]).sort((x, y) => y[1] - x[1]).slice(0, 6)
    .map(([e, rt]) => `${e.tagName}.${String(e.className).slice(0, 50)}:${Math.round(rt)}`)]);
  r.check('360 px: the editor has no horizontal page scroll (the line table scrolls inside its box)', !(await overflow(s)), (await overflow(s)) ? wide.join(' | ') : '');
  r.check('no page errors', s.errors.length === 0 && a.errors.length === 0 && f.errors.length === 0, [...s.errors, ...a.errors, ...f.errors].join(' | '));
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
