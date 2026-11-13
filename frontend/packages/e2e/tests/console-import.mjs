// C10.3: bulk catalogue import screen of the staff console. Needs the API (Development), the console, and the dev admin.
// Real calls: dry run, and a commit refused for an unknown seller (writes nothing). The 422 and success screens are
// shown with mocked commit responses, so this run never writes to the catalogue.
import { writeFileSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { CONSOLE as WEB, SHOTS, USERS, launch, reporter, totp } from './lib.mjs';

const r = reporter();
const dir = join(tmpdir(), 'mw-e2e-import');
mkdirSync(dir, { recursive: true });
const HEAD = 'product_slug,product_title_en,product_title_bn,category,sku,manufacturer_part_number,price_bdt\n';
const file = (name, text) => { const p = join(dir, name); writeFileSync(p, text); return p; };
const good = file('good.csv', HEAD + 'zz-e2e-x,Zed,জেড,programmable-logic-controllers,ZZE2E1,ZZE2E-1,100\n');
const bad = file('bad.csv', HEAD + 'zz-e2e-y,Why,ওয়াই,no-such-category,ZZE2E2,ZZE2E-2,=1+1\n');
const notCsv = file('data.txt', 'x');
const SELLER = '11111111-1111-4111-8111-111111111111';

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
  r.check('Import is disabled until a seller id is given', await importBtn.isDisabled());
  await p.getByLabel('Seller id').fill('not-a-guid');
  r.check('Import stays disabled for a non-GUID seller id', await importBtn.isDisabled());
  await p.getByLabel('Seller id').fill(SELLER);
  r.check('Import enables with a clean report and a GUID', await importBtn.isEnabled());
  await importBtn.click();
  await p.getByRole('dialog').getByText(/1 new and 0 existing products/).waitFor();
  r.check('confirm dialog shows the counts', true);
  noise = true;
  await p.getByRole('button', { name: 'Confirm import' }).click();
  await p.getByText('The seller does not exist or is not approved.').waitFor();
  noise = false;
  r.check('unknown seller: the API message is shown, nothing was written', true);
  await p.screenshot({ path: `${SHOTS}/c10-3-import-ok-1280.png`, fullPage: true });

  // 422 (mocked): the returned report is shown
  const cors = { 'access-control-allow-origin': WEB, 'access-control-allow-credentials': 'true' };
  const report = { rows: 2, validRows: 1, invalidRows: 1, newProducts: 1, existingProducts: 0, newVariants: 1, existingVariants: 0, errorCount: 1, errorsTruncated: false, errors: [{ row: 3, column: null, code: 'DOMAIN_RULE', message: 'Mocked domain rule.' }] };
  const commitUrl = '**/api/v1/admin/catalog/import/commit';
  await p.route(commitUrl, (route) => route.fulfill({ status: 422, contentType: 'application/json', headers: cors, body: JSON.stringify({ error: { code: 'IMPORT_REJECTED', message: 'x' }, data: report }) }));
  noise = true;
  await importBtn.click();
  await p.getByRole('button', { name: 'Confirm import' }).click();
  await p.getByText('The server rejected the import. Nothing was written.').waitFor();
  noise = false;
  r.check('422: the returned report is shown', (await p.getByText('Mocked domain rule.').count()) === 1);
  await p.unroute(commitUrl);

  // success (mocked)
  const sha = 'a'.repeat(64);
  const done = { sha256: sha, rows: 1, createdProducts: 1, createdVariants: 1, updatedProducts: 0, updatedVariants: 0, unchangedProducts: 0, unchangedVariants: 0 };
  await p.route(commitUrl, (route) => route.fulfill({ status: 200, contentType: 'application/json', headers: cors, body: JSON.stringify({ data: done }) }));
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
