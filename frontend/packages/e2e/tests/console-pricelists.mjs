// D2.11: price-list editor of the staff console. Needs the API (Development, with the D2.7 endpoints and the demo catalogue),
// the console and the dev admin. Set E2E_CONSOLE_PRICELISTS=1 to run; otherwise it exits 0 without doing anything.
// Real: sign-in with TOTP, company registration + activation (API), search/SKU lookup, create, paste, manual add, edit, below-floor
// flag, submit, pending read-only state, delete of a draft, permission refusals. Mocked (and marked): server errors on save, and an
// Approved list's page (approving is D2.12, so no real Approved list can be made yet).
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, CONSOLE as WEB, SHOTS, USERS, apiCall, apiLogin, launch, reporter, totp, uniqueBin } from './lib.mjs';

if (process.env.E2E_CONSOLE_PRICELISTS !== '1') {
  console.log('SKIP  console price-list E2E (set E2E_CONSOLE_PRICELISTS=1 to run it)');
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
const cors = { 'access-control-allow-origin': WEB, 'access-control-allow-credentials': 'true' };
const preflight = (route, methods) => route.fulfill({ status: 204, headers: { ...cors, 'access-control-allow-headers': '*', 'access-control-allow-methods': methods } });
const overflow = (p) => p.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
const listName = `E2E contract prices ${Date.now() % 100000}`;
const day = (n) => new Date(Date.now() + n * 86400000).toISOString().slice(0, 10);

(async () => {
  // ---- setup through the API: an active company and three real SKUs
  const usedCode = await totp(USERS.admin.totpSecret);
  const adminLogin = await fetch(`${API}/api/v1/auth/login`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ identifier: USERS.admin.email, password: USERS.admin.password, totpCode: usedCode })
  });
  const adminToken = (await adminLogin.json()).data.accessToken;
  const buyerToken = await apiLogin(USERS.buyer);
  const legalName = `Price List Test Mills ${Date.now() % 100000} Ltd`;
  const reg = await apiCall(buyerToken, 'POST', '/api/v1/companies', { legalName, tradeLicenceNo: 'TL-PL-1', bin: uniqueBin() });
  const act = await apiCall(adminToken, 'POST', `/api/v1/admin/companies/${reg.json.data.id}/activate`);
  r.check('setup: an active company exists', reg.status === 201 && act.status === 200, `${reg.status}/${act.status}`);
  const found = await (await fetch(`${API}/api/v1/search?q=plc&pageSize=10`)).json();
  const skus = found.data.map((h) => h.partNumbers.find((n) => n.startsWith('MW-'))).filter(Boolean);
  r.check('setup: three SKUs from the demo catalogue', skus.length >= 4, skus.slice(0, 4).join(', '));
  const [skuA, skuB, skuC, skuD] = skus;
  const buyerList = await apiCall(buyerToken, 'GET', '/api/v1/admin/price-lists');
  r.check('buyer token: GET /admin/price-lists is 403 (real API)', buyerList.status === 403, String(buyerList.status));

  const browser = await launch();
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const p = await ctx.newPage();
  const errors = [];
  let noise = false;
  p.on('console', (m) => { if (m.type() === 'error' && !noise) errors.push(m.text()); });
  p.on('pageerror', (e) => errors.push('pageerror: ' + e.message));

  // ---- seller: no link, page refuses
  await signIn(p, USERS.sellerAlpha.email, USERS.sellerAlpha.password);
  await p.waitForURL(`${WEB}/`);
  await p.getByText('Operations console').first().waitFor();
  r.check('seller sees no Price lists link', (await p.getByRole('link', { name: /Price lists/ }).count()) === 0);
  await p.goto(`${WEB}/pricing/price-lists`);
  await p.getByText('Your role does not allow this action (pricelists.view).').waitFor();
  r.check('seller opening /pricing/price-lists is told no', true);
  await p.goto(`${WEB}/pricing/price-lists/new`);
  await p.getByText('Your role does not allow this action (pricelists.edit).').waitFor();
  r.check('seller opening /pricing/price-lists/new is told no', true);
  await p.getByRole('button', { name: 'Sign out' }).click();
  await p.waitForURL(/\/login$/);

  // ---- buyer cannot get into the console at all
  await signIn(p, USERS.buyer.email, USERS.buyer.password);
  await p.waitForTimeout(2500);
  r.check('buyer does not reach the console', !(await p.url()).endsWith('/pricing/price-lists') && (await p.getByRole('link', { name: /Price lists/ }).count()) === 0, p.url());

  // ---- admin
  await signIn(p, USERS.admin.email, USERS.admin.password);
  // a code is single-use: wait for the next 30 s step if the setup call just used this one
  let code = await totp(USERS.admin.totpSecret);
  while (code === usedCode) { await new Promise((res) => setTimeout(res, 1000)); code = await totp(USERS.admin.totpSecret); }
  await p.getByLabel('6-digit authenticator code or single-use recovery code').fill(code);
  await p.getByRole('button', { name: 'Verify and unlock console' }).click();
  await p.waitForURL(`${WEB}/`);
  r.check('sidebar groups the link under a Pricing heading', (await p.getByRole('navigation', { name: 'Modules' }).getByText('Pricing', { exact: true }).count()) === 1);
  await p.getByRole('link', { name: /Price lists/ }).first().click();
  await p.waitForURL(/\/pricing\/price-lists$/);
  await p.getByRole('button', { name: 'Apply filters' }).waitFor();
  await p.getByRole('link', { name: 'New price list' }).waitFor();
  r.check('admin reaches the price-list page from the sidebar and may create', true);
  await p.getByTestId('empty').or(p.getByTestId('price-list-table')).waitFor();
  await axeCheck(p, '/pricing/price-lists');

  // ---- create (validation first)
  await p.getByRole('link', { name: 'New price list' }).click();
  await p.waitForURL(/\/new$/);
  await p.getByRole('button', { name: 'Create draft' }).click();
  await p.getByText('Enter a name.').waitFor();
  r.check('create: empty form shows name, company, contract and date errors', (await p.getByText('Choose the company this contract is for.').count()) === 1 && (await p.getByText('A Company list needs the contract reference.').count()) === 1 && (await p.getByText('Enter a start date.').count()) === 1);
  await p.getByLabel('Name', { exact: false }).first().fill(listName);
  await p.getByLabel('Valid from (UTC date)').fill(day(1));
  await p.getByLabel('Valid to (UTC date, exclusive)').fill(day(500));
  await p.getByRole('button', { name: 'Create draft' }).click();
  await p.getByText('A list can be valid for at most 366 days.').waitFor();
  r.check('create: more than 366 days is refused on the client', true);
  await p.getByLabel('Valid to (UTC date, exclusive)').fill(day(91));
  await p.getByLabel('Search by legal name or BIN').or(p.getByLabel('Company', { exact: true })).first().fill(legalName.slice(0, 22));
  const companyList = p.getByTestId('company-list');
  await companyList.waitFor();
  await companyList.selectOption({ label: legalName });
  await p.getByLabel('Contract reference').fill('CT-E2E-1');
  await axeCheck(p, '/pricing/price-lists/new');
  await p.screenshot({ path: `${SHOTS}/d2-11-new-1280.png`, fullPage: true });
  await p.getByRole('button', { name: 'Create draft' }).click();
  await p.waitForURL(/\/pricing\/price-lists\/[0-9a-f-]{36}$/);
  const listUrl = p.url();
  await p.getByTestId('list-name').waitFor();
  r.check('create: a Company draft for the seeded company opens', (await p.getByTestId('list-name').innerText()) === listName && (await p.getByTestId('status-badge').innerText()) === 'Draft');

  // ---- bulk paste: a bad paste writes nothing and says which line
  const paste = p.getByLabel('Bulk entry');
  await paste.fill(`${skuA}, 1, 100.50\n${skuA}, 10, 12.345\n${skuB}, x, 5`);
  await p.getByRole('button', { name: 'Add lines' }).click();
  await p.getByTestId('paste-problems').waitFor();
  const problems = await p.getByTestId('paste-problems').innerText();
  r.check('paste: bad lines are reported by line number and nothing is saved', problems.includes('Line 2') && problems.includes('Line 3') && (await p.getByTestId('no-entries').count()) === 1);
  await paste.fill(`${skuA}, 1, 90000.50\n${skuA}, 10, 89000\n${skuB}, 1, 70000`);
  await p.getByRole('button', { name: 'Add lines' }).click();
  await p.getByText('3 lines added.').waitFor();
  r.check('paste: three lines became three entries (SKUs resolved through search)', (await p.getByTestId('entry-row').count()) === 3);
  await paste.fill('NO-SUCH-SKU-ZZ, 1, 5');
  await p.getByRole('button', { name: 'Add lines' }).click();
  await p.getByText(/No variant has the SKU: NO-SUCH-SKU-ZZ/).waitFor();
  r.check('paste: an unknown SKU is named and nothing is added', (await p.getByTestId('entry-row').count()) === 3);

  // ---- manual add
  await p.getByRole('form', { name: 'Add one entry' }).getByLabel('SKU').fill(skuC);
  await p.getByRole('form', { name: 'Add one entry' }).getByLabel('Price (taka)').fill('55000');
  await p.getByRole('button', { name: 'Add entry' }).click();
  await p.getByText(`${skuC} added.`).waitFor();
  r.check('manual add: a fourth entry', (await p.getByTestId('entry-row').count()) === 4);

  // ---- inline edit of a price, then reload to prove it was saved
    const priceInput = p.getByLabel(`Price in taka for ${skuB}, minimum quantity 1`, { exact: true });
  await priceInput.fill('69999.99');
  await p.getByTestId('entry-row').filter({ hasText: skuB }).getByRole('button', { name: 'Save' }).click();
  await p.getByText('Entry saved.').waitFor();
  await p.reload();
  await p.getByTestId('entries-table').waitFor();
  r.check('inline edit: the new price survives a reload', (await p.getByLabel(`Price in taka for ${skuB}, minimum quantity 1`, { exact: true }).inputValue()) === '69999.99');
  r.check('contributors are listed', (await p.getByTestId('contributors').innerText()).length >= 8);

  // ---- cost / floor columns come from the API only with pricing.view_cost (the admin has it)
  const costShown = (await p.getByRole('columnheader', { name: 'Cost' }).count()) === 1;
  r.check('cost and floor columns are shown (admin holds pricing.view_cost)', costShown);
  const rowD = await (async () => {
    await p.getByRole('form', { name: 'Add one entry' }).getByLabel('SKU').fill(skuD);
    await p.getByRole('form', { name: 'Add one entry' }).getByLabel('Price (taka)').fill('90000');
    await p.getByRole('button', { name: 'Add entry' }).click();
    await p.getByText(`${skuD} added.`).waitFor();
    return p.getByTestId('entry-row').filter({ hasText: skuD });
  })();
  const costText = (await rowD.locator('td').nth(3).innerText()).replace(/,/g, '').trim();
  if (costText) {
    // the floor is 10 % above cost: pricing exactly at cost is above cost (allowed) but below the floor
    await p.getByLabel(`Price in taka for ${skuD}, minimum quantity 1`).fill(costText);
    await rowD.getByRole('button', { name: 'Save' }).click();
    await p.getByText('Entry saved.').waitFor();
  }
  await p.getByTestId('entry-row').filter({ hasText: skuD }).getByText(/Below floor|Cost unknown/).first().waitFor();
  r.check('an entry at cost shows the Below floor flag', (await p.getByTestId('entry-row').filter({ hasText: skuD }).getAttribute('data-belowfloor')) === 'true', costText ? `cost ${costText}` : 'cost unknown');
  await p.getByText('a reason is required before submitting').first().waitFor();
  r.check('the reason is required before submit (hint under the field)', true);
  await axeCheck(p, 'price-list editor (draft)');
  await p.screenshot({ path: `${SHOTS}/d2-11-editor-draft-1280.png`, fullPage: true });

  // ---- submit is blocked until the reason is given
  await p.getByRole('button', { name: 'Submit for approval' }).click();
  await p.getByTestId('submit-issues').waitFor();
  r.check('submit dialog: missing reason blocks Confirm and names the SKU', (await p.getByTestId('submit-issues').innerText()).includes(skuD) && (await p.getByRole('button', { name: 'Confirm submit' }).isDisabled()));
  r.check('submit dialog explains four-eyes approval', (await p.getByRole('dialog').innerText()).includes('Someone else must approve'));
  await p.getByRole('button', { name: 'Cancel' }).click();
  // the demo catalogue has no cost prices, so every entry counts as below floor and needs its reason
  r.check('every entry is flagged (no cost data in the demo catalogue)', (await p.locator('[data-testid=entry-row][data-belowfloor=true]').count()) === (await p.getByTestId('entry-row').count()));
  for (const row of await p.getByTestId('entry-row').all()) {
    await row.getByLabel(/Below-floor reason for/).fill('Agreed with the owner for the E2E');
    const saved = p.waitForResponse((res) => /\/entries$/.test(res.url()) && res.request().method() === 'PUT' && res.status() === 200);
    await row.getByRole('button', { name: 'Save' }).click();
    await saved;
  }
  await p.reload();
  await p.getByTestId('entries-table').waitFor();
  r.check('reasons are saved with the entries', (await p.getByLabel(/Below-floor reason for/).evaluateAll((els) => els.every((e) => e.value.length > 5))));

  // ---- mocked failure: keep unsaved edits and show plain words + retry (the server is not touched)
  const putUrl = '**/api/v1/admin/price-lists/*/entries';
  let mode = '500';
  await p.route(putUrl, (route) => {
    if (route.request().method() === 'OPTIONS') return preflight(route, 'PUT');
    const body = mode === '500' ? { error: { code: 'X', message: 'x' } } : { error: { code: 'PRICE_LIST_NOT_EDITABLE', message: 'x' } };
    return route.fulfill({ status: mode === '500' ? 500 : 409, contentType: 'application/json', headers: cors, body: JSON.stringify(body) });
  });
  noise = true;
  const edit = p.getByLabel(`Price in taka for ${skuA}, minimum quantity 1`, { exact: true });
  const editRow = p.getByTestId('entry-row').filter({ has: edit });
  await edit.fill('88888.88');
  await editRow.getByRole('button', { name: 'Save' }).click();
  await p.getByText(/The server had a problem/).waitFor();
  r.check('mocked 500 on save: plain words, Reload offered, edit kept', (await edit.inputValue()) === '88888.88' && (await p.getByRole('button', { name: 'Reload' }).count()) === 1);
  mode = '409';
  await editRow.getByRole('button', { name: 'Save' }).click();
  await p.getByText(/no longer a draft/).waitFor();
  r.check('mocked 409 PRICE_LIST_NOT_EDITABLE: plain words, edit kept', (await edit.inputValue()) === '88888.88');
  noise = false;
  await p.unroute(putUrl);

  // ---- submit for real
  await p.reload();
  await p.getByRole('button', { name: 'Submit for approval' }).click();
  await p.getByRole('button', { name: 'Confirm submit' }).isEnabled();
  await p.getByRole('button', { name: 'Confirm submit' }).click();
  await p.getByText('Submitted for approval.').waitFor();
  const hash = await p.getByTestId('content-hash').innerText();
  r.check('submitted: shows the content hash (64 hex)', /^[0-9a-f]{64}$/.test(hash), hash.slice(0, 12));
  r.check('submitted: status Pending approval, read-only banner', (await p.getByTestId('status-badge').innerText()) === 'Pending approval' && (await p.getByTestId('readonly-banner').innerText()).includes('waiting for approval'));
  r.check('pending: no inputs, no Save/Remove/Submit/Delete/Revise', (await p.getByTestId('entries-table').locator('input').count()) === 0
    && (await p.getByRole('button', { name: /^(Save|Remove|Submit for approval|Delete draft|Revise)/ }).count()) === 0 && (await p.getByRole('button', { name: 'Add lines' }).count()) === 0);
  r.check('pending: no approve/reject/withdraw buttons (D2.12)', (await p.getByRole('button', { name: /Approve|Reject|Withdraw/ }).count()) === 0);
  await axeCheck(p, 'price-list editor (pending, read-only)');
  await p.screenshot({ path: `${SHOTS}/d2-11-editor-pending-1280.png`, fullPage: true });

  // ---- the list shows it
  await p.getByRole('link', { name: 'All price lists' }).click();
  await p.getByTestId('price-list-table').waitFor();
  const row = p.getByTestId('price-list-table').locator('tbody tr').filter({ hasText: listName }).first();
  const rowText = await row.innerText();
  r.check('list row: name, scope, company, contract, state, entries, version', rowText.includes('Company') && rowText.includes(legalName) && rowText.includes('CT-E2E-1') && rowText.includes('Pending approval') && rowText.includes('v1'), rowText.replace(/\s+/g, ' ').slice(0, 140));
  await p.getByLabel('Status').selectOption('Draft');
  const filtered = p.waitForResponse((res) => res.url().includes('status=Draft') && res.status() === 200);
  await p.getByRole('button', { name: 'Apply filters' }).click();
  await filtered;
  await p.waitForTimeout(300);
  r.check('filter by status Draft hides the pending list', (await p.getByTestId('price-list-table').locator('tbody tr').filter({ hasText: listName }).count()) === 0);
  await p.getByLabel('Status').selectOption('PendingApproval');
  await p.getByLabel('Search').fill(listName);
  await p.getByRole('button', { name: 'Apply filters' }).click();
  await p.getByTestId('price-list-table').locator('tbody tr').filter({ hasText: listName }).first().waitFor();
  r.check('filters status + search find it again', true);

  // ---- create and delete a draft (General scope)
  await p.getByRole('link', { name: 'New price list' }).click();
  await p.getByLabel('General quantity breaks (everyone)').check();
  await p.getByLabel('Name', { exact: false }).first().fill('E2E throwaway general');
  await p.getByLabel('Valid from (UTC date)').fill(day(1));
  await p.getByLabel('Valid to (UTC date, exclusive)').fill(day(30));
  await p.getByRole('button', { name: 'Create draft' }).click();
  await p.waitForURL(/\/pricing\/price-lists\/[0-9a-f-]{36}$/);
  await p.getByRole('form', { name: 'Add one entry' }).getByLabel('SKU').fill(skuA);
  await p.getByRole('form', { name: 'Add one entry' }).getByLabel('Min quantity').fill('1');
  await p.getByRole('form', { name: 'Add one entry' }).getByLabel('Price (taka)').fill('1');
  await p.getByRole('button', { name: 'Add entry' }).click();
  await p.getByText('On a General list every entry is a quantity break').waitFor();
  r.check('General list: minimum quantity 1 is refused on the client', true);
  await p.getByRole('button', { name: 'Delete draft' }).click();
  await p.getByRole('dialog').getByRole('button', { name: 'Delete draft' }).click();
  await p.waitForURL(/\/pricing\/price-lists$/);
  r.check('draft deleted: back on the list', true);

  // ---- mocked Approved list: Revise is offered, edit controls are not (approval screens are D2.12)
  const approved = {
    id: '99999999-9999-4999-8999-999999999999', seriesId: '88888888-8888-4888-8888-888888888888', version: 2, name: 'Mock approved', scope: 'General', companyId: null, contractRef: null,
    status: 'Approved', displayState: 'Active', validFromUtc: '2026-01-01T00:00:00Z', validToUtc: '2026-12-01T00:00:00Z', entryCount: 0, createdBy: '11111111-1111-4111-8111-111111111111', createdAtUtc: '2026-01-01T00:00:00Z',
    submittedBy: null, submittedAtUtc: null, contentHash: 'b'.repeat(64), effectiveFromUtc: '2026-01-01T00:01:00Z', approvedBy: null, approvedAtUtc: null, rejectionReason: null, withdrawReason: null, contributors: [], entries: []
  };
  const detailUrl = `**/api/v1/admin/price-lists/${approved.id}`;
  await p.route(detailUrl, (route) => (route.request().method() === 'OPTIONS' ? preflight(route, 'GET') : route.fulfill({ status: 200, contentType: 'application/json', headers: cors, body: JSON.stringify({ data: approved }) })));
  await p.route(`${detailUrl}/revise`, (route) => (route.request().method() === 'OPTIONS' ? preflight(route, 'POST') : route.fulfill({ status: 409, contentType: 'application/json', headers: cors, body: JSON.stringify({ error: { code: 'PRICE_LIST_OPEN_VERSION_EXISTS', message: 'x' } }) })));
  await p.goto(`${WEB}/pricing/price-lists/${approved.id}`);
  await p.getByRole('button', { name: /^Revise/ }).waitFor();
  r.check('mocked Approved list: banner, content hash, Revise offered, no edit controls', (await p.getByTestId('readonly-banner').innerText()).includes('revise') && (await p.getByRole('button', { name: /Submit for approval|Delete draft|Add lines/ }).count()) === 0 && (await p.getByTestId('effective-from').count()) === 1);
  noise = true;
  await p.getByRole('button', { name: /^Revise/ }).click();
  await p.getByText(/already has a newer draft or pending version/).waitFor();
  noise = false;
  r.check('mocked 409 PRICE_LIST_OPEN_VERSION_EXISTS on revise: plain words', true);

  // ---- API 403 state (mocked): the page says so in plain words
  await p.unroute(detailUrl);
  await p.unroute(`${detailUrl}/revise`);
  await p.route('**/api/v1/admin/price-lists?**', (route) => (route.request().method() === 'OPTIONS' ? preflight(route, 'GET') : route.fulfill({ status: 403, contentType: 'application/json', headers: cors, body: JSON.stringify({ error: { code: 'FORBIDDEN', message: 'x' } }) })));
  noise = true;
  await p.goto(`${WEB}/pricing/price-lists`);
  await p.getByText('You do not have permission to do this.').waitFor();
  noise = false;
  r.check('mocked API 403 on the list: "You do not have permission"', true);
  await p.unroute('**/api/v1/admin/price-lists?**');

  // ---- 360 px
  const m = await browser.newContext({ viewport: { width: 360, height: 800 } });
  await m.addCookies(await ctx.cookies());
  const mp = await m.newPage();
  await mp.goto(listUrl);
  await mp.getByTestId('entries-table').waitFor();
  r.check('360px: price-list page has no horizontal page scroll', !(await overflow(mp)));
  await mp.goto(`${WEB}/pricing/price-lists`);
  await mp.getByTestId('price-list-table').waitFor();
  r.check('360px: list page has no horizontal page scroll', !(await overflow(mp)));
  await mp.goto(`${WEB}/pricing/price-lists/new`);
  await mp.getByRole('button', { name: 'Create draft' }).waitFor();
  r.check('360px: create page has no horizontal page scroll', !(await overflow(mp)));
  await mp.screenshot({ path: `${SHOTS}/d2-11-new-360.png`, fullPage: true });
  await m.close();

  r.check('no unexpected console errors', errors.length === 0, errors.join(' | ').slice(0, 400));
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error('E2E crashed:', e.message.split('\n').slice(0, 8).join('\n')); process.exit(2); });
