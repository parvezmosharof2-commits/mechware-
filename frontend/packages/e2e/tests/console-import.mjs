// C10.3: bulk catalogue import screen of the staff console. Needs the API (Development), the console, and the dev admin.
// Real calls: dry run and the seller list (GET /api/v1/admin/sellers). Every commit is mocked (the picker can only choose real
// approved sellers, so a real commit would write); this run never writes to the catalogue.
import { writeFileSync, mkdirSync, existsSync, readdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { CONSOLE as WEB, SHOTS, USERS, launch, reporter, totp } from './lib.mjs';

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
const dir = join(tmpdir(), 'mw-e2e-import');
mkdirSync(dir, { recursive: true });
const HEAD = 'product_slug,product_title_en,product_title_bn,category,sku,manufacturer_part_number,price_bdt\n';
const file = (name, text) => { const p = join(dir, name); writeFileSync(p, text); return p; };
const good = file('good.csv', HEAD + 'zz-e2e-x,Zed,জেড,programmable-logic-controllers,ZZE2E1,ZZE2E-1,100\n');
const bad = file('bad.csv', HEAD + 'zz-e2e-y,Why,ওয়াই,no-such-category,ZZE2E2,ZZE2E-2,=1+1\n');
const notCsv = file('data.txt', 'x');

async function signIn(p, email, password) {
  await p.goto(`${WEB}/login`, { timeout: 120000 });
  await p.getByLabel('Staff or seller email').fill(email);
  await p.getByLabel('Password').fill(password);
  await p.getByRole('button', { name: 'Sign in' }).click();
}

(async () => {
  const browser = await launch();
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const p = await ctx.newPage();
  const errors = [];
  let noise = false;
  p.on('console', (m) => { if (m.type() === 'error' && !noise) errors.push(m.text()); });
  p.on('pageerror', (e) => errors.push('pageerror: ' + e.message));

  // seller: no catalog.manage -> no link, page refuses
  await signIn(p, USERS.sellerAlpha.email, USERS.sellerAlpha.password);
  await p.waitForURL(`${WEB}/`);
  await p.getByText('Operations console').first().waitFor();
  r.check('seller sees no import link', (await p.getByRole('link', { name: /Bulk catalogue import/ }).count()) === 0);
  await p.goto(`${WEB}/catalog/import`);
  await p.getByText('Your role does not allow this action (catalog.manage).').waitFor();
  r.check('seller opening /catalog/import is told no', true);
  await p.getByRole('button', { name: 'Sign out' }).click();
  await p.waitForURL(/\/login$/);

  // admin
  await signIn(p, USERS.admin.email, USERS.admin.password);
  await p.getByLabel('6-digit authenticator code or single-use recovery code').fill(await totp(USERS.admin.totpSecret));
  await p.getByRole('button', { name: 'Verify and unlock console' }).click();
  await p.waitForURL(`${WEB}/`);
  await p.getByRole('link', { name: /Bulk catalogue import/ }).first().click();
  await p.waitForURL(/\/catalog\/import$/);
  r.check('admin reaches the import page from the sidebar', true);

  const checkBtn = p.getByRole('button', { name: 'Check file' });
  r.check('Check file is disabled before a file is chosen', await checkBtn.isDisabled());
  await p.setInputFiles('#import-file', notCsv);
  await p.getByText('Only .csv files are accepted').waitFor();
  r.check('a non-CSV file is refused on the client', await checkBtn.isDisabled());

  // bad file: errors listed, Import never offered
  await p.setInputFiles('#import-file', bad);
  await checkBtn.click();
  await p.getByTestId('error-table').waitFor();
  const rows = await p.getByTestId('error-table').locator('tbody tr').allInnerTexts();
  r.check('dry run lists errors (row, column, code, message)', rows.length >= 1 && rows.some((t) => t.includes('UNKNOWN_CATEGORY')), `${rows.length} errors`);
  r.check('Import is disabled while the file has errors', await p.getByRole('button', { name: 'Import', exact: true }).isDisabled());
  const [dl] = await Promise.all([p.waitForEvent('download'), p.getByRole('button', { name: 'Download errors (CSV)' }).click()]);
  r.check('errors download as import-errors.csv', dl.suggestedFilename() === 'import-errors.csv');
  await p.screenshot({ path: `${SHOTS}/c10-3-import-errors-1280.png`, fullPage: true });

  // good file: counts, then seller validation, then a real refused commit (unknown seller -> 400, nothing written)
  await p.setInputFiles('#import-file', good);
  await checkBtn.click();
  await p.getByText('No errors. The file can be imported.').waitFor();
  r.check('valid file: 1 new product, 1 new variant', (await p.getByTestId('stat-new-products').innerText()) === '1' && (await p.getByTestId('stat-new-variants').innerText()) === '1');
  const importBtn = p.getByRole('button', { name: 'Import', exact: true });
  const cors = { 'access-control-allow-origin': WEB, 'access-control-allow-credentials': 'true' };
  const preflight = (route, methods) => route.fulfill({ status: 204, headers: { ...cors, 'access-control-allow-headers': '*', 'access-control-allow-methods': methods } });
  const commitUrl = '**/api/v1/admin/catalog/import/commit';
  const report = { rows: 2, validRows: 1, invalidRows: 1, newProducts: 1, existingProducts: 0, newVariants: 1, existingVariants: 0, errorCount: 1, errorsTruncated: false, errors: [{ row: 3, column: null, code: 'DOMAIN_RULE', message: 'Mocked domain rule.' }] };
  let commitBody = '';
  await p.route(commitUrl, (route) => {
    if (route.request().method() === 'OPTIONS') return preflight(route, 'POST');
    commitBody = route.request().postData() ?? '';
    return route.fulfill({ status: 422, contentType: 'application/json', headers: cors, body: JSON.stringify({ error: { code: 'IMPORT_REJECTED', message: 'x' }, data: report }) });
  });
  const list = p.getByTestId('seller-list');
  await list.waitFor();
  const all = (await list.locator('option').allInnerTexts()).filter(Boolean);
  const search = p.getByLabel('Seller', { exact: true });
  r.check('seller picker lists approved sellers by store name (real API)', all.length >= 2, `${all.length} sellers`);
  await axeCheck(p, '/catalog/import with the seller picker');
  r.check('Import is disabled until a seller is chosen', await importBtn.isDisabled());
  r.check('no seller is preselected when several exist', (await list.inputValue()) === '');
  const part = all[0].slice(0, 5).toLowerCase();
  await search.fill(part);
  await p.waitForFunction((n) => document.querySelectorAll('[data-testid=seller-list] option').length - 1 < n, all.length);
  const found = (await list.locator('option').allInnerTexts()).filter(Boolean);
  r.check('typing in the search box filters the list (debounced)', found.length >= 1 && found.every((t) => t.toLowerCase().includes(part)), found.join(' | '));
  await search.fill('zzz-no-such-store');
  await p.getByTestId('seller-empty').waitFor();
  r.check('no match shows an empty state', (await p.getByTestId('seller-empty').innerText()).includes('zzz-no-such-store'));
  await search.fill(all[1]);
  await p.waitForFunction((t) => document.querySelectorAll('[data-testid=seller-list] option').length === 2 && document.querySelectorAll('[data-testid=seller-list] option')[1].textContent === t, all[1]);
  await list.focus();
  await p.keyboard.press('ArrowDown');
  r.check('keyboard: arrow key chooses a seller and enables Import', (await list.inputValue()) !== '' && (await importBtn.isEnabled()));
  const chosenId = await list.inputValue();
  await importBtn.click();
  await p.getByRole('dialog').getByText(/1 new and 0 existing products/).waitFor();
  r.check('confirm dialog shows the counts and the store name', (await p.getByRole('dialog').innerText()).includes(all[1]));
  noise = true;
  await p.getByRole('button', { name: 'Confirm import' }).click();
  await p.getByText('The server rejected the import. Nothing was written.').waitFor();
  noise = false;
  r.check('the commit carried the chosen seller id (mocked response, nothing written)', commitBody.includes(chosenId), chosenId);
  r.check('422: the returned report is shown', (await p.getByText('Mocked domain rule.').count()) === 1);
  await p.screenshot({ path: `${SHOTS}/c10-3b-picker-1280.png`, fullPage: true });
  await p.unroute(commitUrl);

  // seller list states (mocked list): exactly one seller is preselected, none shows the reason, an error offers a retry
  const listPage = async (respond) => {
    const q = await ctx.newPage();
    await q.route('**/api/v1/admin/sellers**', (route) => (route.request().method() === 'OPTIONS' ? preflight(route, 'GET') : respond(route)));
    await q.goto(`${WEB}/catalog/import`);
    await q.setInputFiles('#import-file', good);
    await q.getByRole('button', { name: 'Check file' }).click();
    await q.getByText('No errors. The file can be imported.').waitFor();
    return q;
  };
  const json = (route, status, body) => route.fulfill({ status, contentType: 'application/json', headers: cors, body: JSON.stringify(body) });
  const paged = (items) => ({ data: { items, page: 1, pageSize: 50, totalCount: items.length } });
  const ONE = '22222222-2222-4222-8222-222222222222';
  const one = await listPage((route) => json(route, 200, paged([{ id: ONE, storeName: 'Only Store', status: 'approved' }])));
  await one.getByTestId('seller-list').waitFor();
  r.check('exactly one approved seller is preselected and Import is enabled', (await one.getByTestId('seller-list').inputValue()) === ONE && (await one.getByRole('button', { name: 'Import', exact: true }).isEnabled()));
  await one.close();
  const none = await listPage((route) => json(route, 200, paged([])));
  await none.getByTestId('seller-empty').waitFor();
  r.check('no approved sellers: empty state gives the reason, Import stays disabled', (await none.getByTestId('seller-empty').innerText()).includes('No approved sellers') && (await none.getByRole('button', { name: 'Import', exact: true }).isDisabled()));
  await none.close();
  let fail = true;
  noise = true;
  const flaky = await listPage((route) => (fail ? json(route, 500, { error: { code: 'X', message: 'x' } }) : json(route, 200, paged([{ id: '33333333-3333-4333-8333-333333333333', storeName: 'Back Store', status: 'approved' }, { id: '44444444-4444-4444-8444-444444444444', storeName: 'Other Store', status: 'approved' }]))));
  await flaky.getByText('Could not load the seller list.').waitFor();
  noise = false;
  r.check('list error shows a message and a retry button', (await flaky.getByRole('button', { name: 'Try again' }).count()) === 1);
  fail = false;
  await flaky.getByRole('button', { name: 'Try again' }).click();
  await flaky.getByTestId('seller-list').waitFor();
  r.check('retry loads the list', (await flaky.getByTestId('seller-list').locator('option').count()) === 3);
  await flaky.close();

  // success (mocked)
  const sha = 'a'.repeat(64);
  const done = { sha256: sha, rows: 1, createdProducts: 1, createdVariants: 1, updatedProducts: 0, updatedVariants: 0, unchangedProducts: 0, unchangedVariants: 0 };
  await p.route(commitUrl, (route) => (route.request().method() === 'OPTIONS' ? preflight(route, 'POST') : route.fulfill({ status: 200, contentType: 'application/json', headers: cors, body: JSON.stringify({ data: done }) })));
  await importBtn.click();
  await p.getByRole('button', { name: 'Confirm import' }).click();
  await p.getByText('Import finished').waitFor();
  r.check('success: totals and SHA-256 shown', (await p.getByTestId('sha256').innerText()) === sha && (await p.getByTestId('stat-created-products').innerText()) === '1');
  await p.screenshot({ path: `${SHOTS}/c10-3-import-done-1280.png`, fullPage: true });

  // 360px
  const m = await browser.newContext({ viewport: { width: 360, height: 800 } });
  await m.addCookies(await ctx.cookies());
  const mp = await m.newPage();
  await mp.goto(`${WEB}/catalog/import`);
  await mp.setInputFiles('#import-file', bad);
  await mp.getByRole('button', { name: 'Check file' }).click();
  await mp.getByTestId('error-table').waitFor();
  r.check('360px: no horizontal page scroll', !(await mp.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth)));
  await m.close();

  r.check('no unexpected console errors', errors.length === 0, errors.join(' | ').slice(0, 300));
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error('E2E crashed:', e.message.split('\n').slice(0, 8).join('\n')); process.exit(2); });
