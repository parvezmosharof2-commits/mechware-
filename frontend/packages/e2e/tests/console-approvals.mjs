// D2.12a: price-list approval queue and decision screens of the staff console. Needs the API (Development, scratch DB), the console and four
// staff accounts on the SAME authenticator secret as the dev admin: admin@ (approves below the floor), admin2.e2e@ (author of one list),
// finance.e2e@ (approves, no below-floor right), support.e2e@ (prepares lists). See docs/modules/D2.md (HANDOFF D2.12a) for how the scratch DB is set up
// (clone the admin row, then `Mechware.Worker migrate` encrypts the secret) and for the cost prices (a few variants need cost_price_minor so a list can be
// above the floor). Set E2E_CONSOLE_APPROVALS=1 to run; otherwise it exits 0 without doing anything.
// Real: sign-in with TOTP, queue, decision panel, four-eyes hint, approve with a real step-up code (a wrong code first), below-floor acknowledgement,
// reject, withdraw, PRICE_LIST_CHANGED (the list is really rejected, edited and resubmitted between opening and confirming).
// Mocked (and marked): a detail response without cost fields (no role lacks view_cost today), and a 409 PRICE_LIST_NOT_PENDING.
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, CONSOLE as WEB, SHOTS, USERS, apiCall, apiLogin, launch, reporter, totp, uniqueBin } from './lib.mjs';

if (process.env.E2E_CONSOLE_APPROVALS !== '1') {
  console.log('SKIP  console approvals E2E (set E2E_CONSOLE_APPROVALS=1 to run it)');
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
const cors = { 'access-control-allow-origin': WEB, 'access-control-allow-credentials': 'true' };
const sleep = (ms) => new Promise((res) => setTimeout(res, ms));
const overflow = (p) => p.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
const day = (n) => new Date(Date.now() + n * 86400000).toISOString().slice(0, 10);
const FROM = day(2);
const TO = day(60);
const tag = String(Date.now() % 100000);

const SECRET = USERS.admin.totpSecret;
const PASSWORD = USERS.admin.password;
const PEOPLE = {
  admin: USERS.admin,
  admin2: { email: 'admin2.e2e@mechware.com.bd', password: PASSWORD, totpSecret: SECRET },
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
  // The account may have used this 30 s step in an earlier run: wait for the next one and try again.
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

(async () => {
  // ---- setup through the API
  const t = {};
  t.admin2 = await apiToken(PEOPLE.admin2);
  t.support = await apiToken(PEOPLE.support);
  const buyer = await apiLogin(USERS.buyer);
  const legalName = `Approvals Test Mills ${tag} Ltd`;
  const reg = await apiCall(buyer, 'POST', '/api/v1/companies', { legalName, tradeLicenceNo: 'TL-AP-1', bin: uniqueBin() });
  const act = await apiCall(t.admin2, 'POST', `/api/v1/admin/companies/${reg.json.data.id}/activate`);
  r.check('setup: an active company exists', reg.status === 201 && act.status === 200, `${reg.status}/${act.status}`);
  const companyId = reg.json.data.id;

  const vars = (await apiCall(t.admin2, 'GET', '/api/v1/admin/variants?pageSize=50')).json.data?.items ?? [];
  const costed = vars.filter((v) => typeof v.costMinor === 'number' && v.costMinor > 0 && v.costMinor < 5000);
  const uncosted = vars.filter((v) => v.costMinor === undefined || v.costMinor === null || v.costMinor === 0);
  r.check('setup: variants with and without a cost price', costed.length >= 6 && uncosted.length >= 2, `${costed.length} costed, ${uncosted.length} without cost`);

  const refs = new Set();
  async function makeList(token, name, variants, { reason = null, submit = true } = {}) {
    const ref = `CT-AP-${tag}-${refs.size + 1}`;
    refs.add(ref);
    const c = await apiCall(token, 'POST', '/api/v1/admin/price-lists', { scope: 'Company', companyId, contractRef: ref, name, validFromUtc: `${FROM}T00:00:00Z`, validToUtc: `${TO}T00:00:00Z` });
    if (c.status !== 201) throw new Error(`create ${name}: ${c.status} ${JSON.stringify(c.json)}`);
    const id = c.json.data.id;
    const e = await apiCall(token, 'PUT', `/api/v1/admin/price-lists/${id}/entries`, { entries: variants.map((v, i) => ({ variantId: v.variantId, minQty: 1, unitPriceMinor: 20000 + i * 100, belowFloorReason: reason })) });
    if (e.status !== 200) throw new Error(`entries ${name}: ${e.status} ${JSON.stringify(e.json)}`);
    if (submit) {
      const s = await apiCall(token, 'POST', `/api/v1/admin/price-lists/${id}/submit`);
      if (s.status !== 200) throw new Error(`submit ${name}: ${s.status} ${JSON.stringify(s.json)}`);
    }
    return { id, name, entries: e.json.data.entries };
  }
  const n = (s) => `${s} ${tag}`;
  const L1 = await makeList(t.support, n('Clean list'), costed.slice(0, 2));
  const L2 = await makeList(t.support, n('Below-floor list'), uncosted.slice(0, 2), { reason: 'Agreed with the customer for the E2E' });
  const L3 = await makeList(t.admin2, n('Admin2 own list'), costed.slice(2, 3));
  const L4 = await makeList(t.support, n('Reject me'), costed.slice(3, 4));
  const L5 = await makeList(t.support, n('Changes under you'), costed.slice(4, 6));
  r.check('setup: five lists are waiting for approval', true);

  const browser = await launch();

  // ================= support: no approval link, refused page
  {
    const p = await newPage(browser);
    await consoleSignIn(p, PEOPLE.support);
    await p.getByRole('navigation', { name: 'Modules' }).getByText('Price lists', { exact: false }).first().waitFor();
    r.check('support (edit only) has no Pending approval link', (await p.getByRole('link', { name: /Pending approval/ }).count()) === 0);
    await p.goto(`${WEB}/pricing/price-lists/approvals`);
    await p.getByText('Your role does not allow this action (pricelists.approve).').waitFor();
    r.check('support opening the queue is told no', true);
    await p.goto(`${WEB}/pricing/price-lists/${L1.id}`);
    await p.getByTestId('list-name').waitFor();
    r.check('support sees the pending list read-only with the people but no Approve button', (await p.getByRole('button', { name: 'Approve', exact: true }).count()) === 0 && (await p.getByTestId('created-by').innerText()) === 'Sara Support');
    await p.context().close();
  }

  // ================= finance: queue, approve, reject, withdraw, stale
  const fin = await newPage(browser);
  await consoleSignIn(fin, PEOPLE.finance);
  const nav = fin.getByRole('navigation', { name: 'Modules' });
  r.check('finance sees Pending approval under Pricing with a count badge', (await nav.getByRole('link', { name: /Pending approval/ }).count()) === 1);
  await fin.getByTestId('pending-count').waitFor();
  r.check('badge counts the waiting lists', Number(await fin.getByTestId('pending-count').innerText()) >= 5, await fin.getByTestId('pending-count').innerText());
  await nav.getByRole('link', { name: /Pending approval/ }).click();
  await fin.waitForURL(/\/pricing\/price-lists\/approvals$/);
  await fin.getByTestId('approval-queue').waitFor();
  const names = await fin.getByTestId('queue-row').evaluateAll((rows) => rows.map((x) => x.querySelector('a')?.textContent));
  r.check('queue lists the five pending lists, newest first', [L1, L2, L3, L4, L5].every((l) => names.includes(l.name)) && names.indexOf(L5.name) < names.indexOf(L1.name), names.join(' | '));
  r.check('queue shows company and contract', (await fin.getByTestId('approval-queue').innerText()).includes(legalName));
  await axeCheck(fin, 'approval queue');
  await fin.setViewportSize({ width: 360, height: 800 });
  r.check('queue: no horizontal overflow at 360 px', !(await overflow(fin)));
  await fin.setViewportSize({ width: 1280, height: 900 });
  await fin.screenshot({ path: `${SHOTS}/d2-12a-queue-1280.png`, fullPage: true });

  // ---- L2: below the floor, finance has no below-floor permission
  await fin.getByRole('link', { name: L2.name }).click();
  await fin.getByTestId('decision-panel').waitFor();
  r.check('below-floor list: Approve is disabled with the administrator hint', (await fin.getByRole('button', { name: 'Approve', exact: true }).isDisabled()) && (await fin.getByTestId('approve-hint').innerText()) === 'An administrator with below-floor permission must approve this list.');
  r.check('below-floor note names the entries and their reason, no amount sentence needed', (await fin.getByTestId('below-floor-note').innerText()).includes('2 entries are below the margin floor') && (await fin.getByTestId('below-floor-list').innerText()).includes('Agreed with the customer'));
  r.check('below-floor rows are highlighted', (await fin.locator('[data-testid=entry-row][data-belowfloor=true]').count()) === 2);
  r.check('prepared-by and submitted-by are names, not ids', (await fin.getByTestId('created-by').innerText()) === 'Sara Support' && (await fin.getByTestId('submitted-by').innerText()).startsWith('Sara Support'));
  r.check('hash is short with the full value on expand', (await fin.getByTestId('hash-short').innerText()).endsWith('…') && (await fin.getByTestId('hash-full').evaluate((el) => el.textContent)).length === 64);
  await fin.screenshot({ path: `${SHOTS}/d2-12a-decision-below-floor-1280.png`, fullPage: true });
  // MOCKED: a caller without pricing.view_cost: the same detail with every cost member removed (no role lacks it today)
  await fin.route(`**/api/v1/admin/price-lists/${L2.id}`, async (route) => {
    if (route.request().method() !== 'GET') return route.continue();
    const real = await route.fetch();
    const body = await real.json();
    for (const e of body.data.entries) for (const k of ['costMinor', 'floorMinor', 'belowFloor', 'belowCost', 'costUnknown']) delete e[k];
    return route.fulfill({ response: real, json: body, headers: { ...real.headers(), ...cors } });
  });
  await fin.reload();
  await fin.getByTestId('entries-table').waitFor();
  r.check('MOCKED no view_cost: no Cost/Floor columns, rows still highlighted from the ids, sentence says amounts are hidden',
    (await fin.getByRole('columnheader', { name: 'Cost', exact: true }).count()) === 0 && (await fin.getByRole('columnheader', { name: 'Floor', exact: true }).count()) === 0
    && (await fin.locator('[data-testid=entry-row][data-belowfloor=true]').count()) === 2 && (await fin.getByTestId('below-floor-note').innerText()).includes('2 entries are below the margin floor'));
  await fin.unroute(`**/api/v1/admin/price-lists/${L2.id}`);

  // ---- L1: approve with a wrong code first, then a real one
  await fin.goto(`${WEB}/pricing/price-lists/${L1.id}`);
  await fin.getByTestId('decision-panel').waitFor();
  const hashOnScreen = await fin.getByTestId('hash-full').evaluate((el) => el.textContent);
  r.check('clean list: Approve is enabled (finance did not prepare it, nothing below the floor)', await fin.getByRole('button', { name: 'Approve', exact: true }).isEnabled());
  r.check('clean list: cost, floor and margin columns are shown to a caller with view_cost', (await fin.getByRole('columnheader', { name: 'Cost', exact: true }).count()) === 1 && (await fin.getByRole('columnheader', { name: 'Floor', exact: true }).count()) === 1);
  await fin.getByRole('button', { name: 'Approve', exact: true }).click();
  const confirm = fin.getByRole('dialog');
  const statement = await confirm.getByTestId('approve-statement').innerText();
  r.check('confirm dialog restates version, hash, validity and the company', statement.includes('version 1') && statement.includes(hashOnScreen.slice(0, 12)) && statement.includes(FROM) && statement.includes(TO) && statement.includes(legalName), statement);
  r.check('no acknowledgement box when nothing is below the floor', (await confirm.getByTestId('ack-box').count()) === 0);
  await axeCheck(fin, 'approve confirm dialog');
  await confirm.getByTestId('approve-continue').click();
  const stepUp = fin.getByRole('dialog');
  await stepUp.getByLabel('Authenticator code').waitFor();
  await axeCheck(fin, 'step-up code dialog');
  await stepUp.getByLabel('Authenticator code').fill('12345');
  await stepUp.getByRole('button', { name: 'Approve', exact: true }).click();
  await stepUp.getByText('Enter the 6-digit code your authenticator app shows now.').waitFor();
  r.check('a code that is not 6 digits is stopped before any request', true);
  let approveCalls = 0;
  fin.on('request', (q) => { if (q.method() === 'POST' && /\/approve$/.test(q.url())) approveCalls++; });
  await stepUp.getByLabel('Authenticator code').fill('000000');
  await stepUp.getByRole('button', { name: 'Approve', exact: true }).click();
  await stepUp.getByTestId('stepup-error').waitFor();
  r.check('a wrong code: "The code was not accepted", dialog stays open, box emptied', (await stepUp.getByTestId('stepup-error').innerText()).startsWith('The code was not accepted') && (await stepUp.getByLabel('Authenticator code').inputValue()) === '' && approveCalls === 1);
  await stepUp.getByLabel('Authenticator code').fill(await freshCode(PEOPLE.finance));
  await stepUp.getByRole('button', { name: 'Approve', exact: true }).click();
  await fin.getByText(/^Approved version 1\./).waitFor();
  r.check('approved with a real step-up code: status Approved, who and when in the history', (await fin.getByTestId('status-badge').innerText()) === 'Approved' && (await fin.getByTestId('history').innerText()).includes('Approved: Farid Finance'));
  r.check('the page shows when the price applies', (await fin.getByTestId('effective-from').count()) === 1);
  r.check('no Approve button on an approved list; Withdraw is offered with its warning', (await fin.getByRole('button', { name: 'Approve', exact: true }).count()) === 0 && (await fin.getByText('New orders will no longer get this price; placed orders keep theirs.').first().isVisible()));
  const viaApi = await apiCall(t.admin2, 'GET', `/api/v1/admin/price-lists/${L1.id}`);
  r.check('the API agrees: Approved by the finance user, same hash', viaApi.json.data.status === 'Approved' && viaApi.json.data.contentHash === hashOnScreen);
  await fin.screenshot({ path: `${SHOTS}/d2-12a-approved-1280.png`, fullPage: true });

  // ---- withdraw L1: the reason is checked BEFORE a code is asked for
  let withdrawCalls = 0;
  fin.on('request', (q) => { if (q.method() === 'POST' && /\/withdraw$/.test(q.url())) withdrawCalls++; });
  await fin.getByRole('button', { name: 'Withdraw', exact: true }).click();
  let dlg = fin.getByRole('dialog');
  await dlg.getByLabel('Reason (5 to 500 characters)').fill('abc');
  await dlg.getByRole('button', { name: 'Continue to the authenticator code' }).click();
  await dlg.getByText('The reason needs at least 5 characters.').waitFor();
  r.check('withdraw: a 3-character reason is refused before the code dialog (no code spent)', (await fin.getByLabel('Authenticator code').count()) === 0 && withdrawCalls === 0);
  await axeCheck(fin, 'withdraw reason dialog');
  await dlg.getByLabel('Reason (5 to 500 characters)').fill('Contract ended early (E2E)');
  await dlg.getByRole('button', { name: 'Continue to the authenticator code' }).click();
  dlg = fin.getByRole('dialog');
  await dlg.getByLabel('Authenticator code').fill(await freshCode(PEOPLE.finance));
  await dlg.getByRole('button', { name: 'Withdraw', exact: true }).click();
  await fin.getByText(/^Withdrawn\. New orders no longer get this price\./).waitFor();
  r.check('withdrawn: status, reason and who in the history', (await fin.getByTestId('status-badge').innerText()) === 'Withdrawn' && (await fin.getByTestId('history').innerText()).includes('Withdrawn: Farid Finance') && (await fin.getByTestId('history').innerText()).includes('Contract ended early (E2E)'));

  // ---- reject L4 (no code)
  await fin.goto(`${WEB}/pricing/price-lists/${L4.id}`);
  await fin.getByTestId('decision-panel').waitFor();
  await fin.getByRole('button', { name: 'Reject', exact: true }).click();
  dlg = fin.getByRole('dialog');
  await dlg.getByRole('button', { name: 'Send back' }).click();
  await dlg.getByText('Enter a reason.').waitFor();
  await dlg.getByLabel('Reason (5 to 500 characters)').fill('Prices look too low (E2E)');
  await axeCheck(fin, 'reject dialog');
  await dlg.getByRole('button', { name: 'Send back' }).click();
  await fin.getByText('Sent back to the author as a draft.').waitFor();
  const afterReject = await apiCall(t.admin2, 'GET', `/api/v1/admin/price-lists/${L4.id}`);
  r.check('reject: no code asked, the list is a Draft with the reason', afterReject.json.data.status === 'Draft' && afterReject.json.data.rejectionReason === 'Prices look too low (E2E)');
  await fin.goto(`${WEB}/pricing/price-lists/approvals`);
  await fin.getByTestId('approval-queue').waitFor();
  const afterNames = await fin.getByTestId('queue-row').evaluateAll((rows) => rows.map((x) => x.querySelector('a')?.textContent));
  r.check('the queue no longer lists the decided lists', !afterNames.includes(L1.name) && !afterNames.includes(L4.name) && afterNames.includes(L5.name), afterNames.join(' | '));

  // ---- L5: the list changes between opening and confirming (real: rejected, edited, resubmitted through the API)
  await fin.getByRole('link', { name: L5.name }).click();
  await fin.getByTestId('decision-panel').waitFor();
  const hashBefore = await fin.getByTestId('hash-full').evaluate((el) => el.textContent);
  await fin.getByRole('button', { name: 'Approve', exact: true }).click();
  await fin.getByRole('dialog').getByTestId('approve-continue').click();
  const codeDlg = fin.getByRole('dialog');
  await codeDlg.getByLabel('Authenticator code').waitFor();
  const rej = await apiCall(t.admin2, 'POST', `/api/v1/admin/price-lists/${L5.id}/reject`, { reason: 'Please re-check the prices (E2E)' });
  const first = L5.entries[0];
  const edit = await apiCall(t.support, 'PUT', `/api/v1/admin/price-lists/${L5.id}/entries`, { entries: [{ variantId: first.variantId, minQty: first.minQty, unitPriceMinor: first.unitPriceMinor + 500, belowFloorReason: null }] });
  const resub = await apiCall(t.support, 'POST', `/api/v1/admin/price-lists/${L5.id}/submit`);
  r.check('setup: the list was rejected, edited and resubmitted behind the approver', rej.status === 200 && edit.status === 200 && resub.status === 200, `${rej.status}/${edit.status}/${resub.status}`);
  await codeDlg.getByLabel('Authenticator code').fill(await freshCode(PEOPLE.finance));
  await codeDlg.getByRole('button', { name: 'Approve', exact: true }).click();
  await fin.getByTestId('decision-alert').waitFor();
  r.check('PRICE_LIST_CHANGED: "This list changed after you opened it", dialog closed', (await fin.getByTestId('decision-alert').innerText()).startsWith('This list changed after you opened it') && (await fin.getByLabel('Authenticator code').count()) === 0);
  await fin.waitForFunction((old) => document.querySelector('[data-testid=hash-full]')?.textContent !== old, hashBefore);
  r.check('the page reloaded: new content hash, still pending, nothing was approved', (await fin.getByTestId('status-badge').innerText()) === 'Pending approval' && (await apiCall(t.admin2, 'GET', `/api/v1/admin/price-lists/${L5.id}`)).json.data.status === 'PendingApproval');

  // ---- MOCKED 409 PRICE_LIST_NOT_PENDING (no code is spent: the call is answered by the test)
  await fin.route(`**/api/v1/admin/price-lists/${L5.id}/approve`, (route) => {
    if (route.request().method() === 'OPTIONS') return route.fulfill({ status: 204, headers: { ...cors, 'access-control-allow-headers': '*', 'access-control-allow-methods': 'POST' } });
    return route.fulfill({ status: 409, contentType: 'application/json', headers: cors, body: JSON.stringify({ error: { code: 'PRICE_LIST_NOT_PENDING', message: 'x' } }) });
  });
  await fin.getByRole('button', { name: 'Approve', exact: true }).click();
  await fin.getByRole('dialog').getByTestId('approve-continue').click();
  await fin.getByRole('dialog').getByLabel('Authenticator code').fill('123456');
  await fin.getByRole('dialog').getByRole('button', { name: 'Approve', exact: true }).click();
  await fin.getByText(/^Someone else already decided this list/).waitFor();
  r.check('MOCKED NOT_PENDING: "Someone else already decided this list" and the page reloads', true);
  await fin.unroute(`**/api/v1/admin/price-lists/${L5.id}/approve`);
  await fin.setViewportSize({ width: 360, height: 800 });
  r.check('decision screen: no horizontal overflow at 360 px', !(await overflow(fin)));
  await fin.screenshot({ path: `${SHOTS}/d2-12a-decision-360.png`, fullPage: true });
  r.check('finance: no page errors', fin.errors.length === 0, fin.errors.join(' | '));
  await fin.context().close();

  // ================= admin: below-floor approval needs the acknowledgement
  {
    const p = await newPage(browser);
    await consoleSignIn(p, PEOPLE.admin);
    await p.goto(`${WEB}/pricing/price-lists/${L2.id}`);
    await p.getByTestId('decision-panel').waitFor();
    r.check('admin: Approve is enabled on a below-floor list', await p.getByRole('button', { name: 'Approve', exact: true }).isEnabled());
    await p.getByRole('button', { name: 'Approve', exact: true }).click();
    const d = p.getByRole('dialog');
    await d.getByTestId('ack-box').waitFor();
    r.check('acknowledgement box lists the entries with their reason; Continue is disabled until ticked', (await d.getByTestId('ack-box').innerText()).includes('Agreed with the customer') && (await d.getByTestId('approve-continue').isDisabled()));
    await axeCheck(p, 'approve dialog with the below-floor acknowledgement');
    await d.getByLabel('I acknowledge these 2 below-floor prices').check();
    await d.getByTestId('approve-continue').click();
    await p.getByRole('dialog').getByLabel('Authenticator code').fill(await freshCode(PEOPLE.admin));
    await p.getByRole('dialog').getByRole('button', { name: 'Approve', exact: true }).click();
    await p.getByText(/^Approved version 1\./).waitFor();
    const l2 = (await apiCall(t.admin2, 'GET', `/api/v1/admin/price-lists/${L2.id}`)).json.data;
    r.check('admin approved the below-floor list with the acknowledgement', l2.status === 'Approved' && (await p.getByTestId('history').innerText()).includes('Approved: '));
    await p.context().close();
  }

  // ================= admin2: the author sees the four-eyes hint
  {
    const p = await newPage(browser);
    await consoleSignIn(p, PEOPLE.admin2);
    await p.goto(`${WEB}/pricing/price-lists/${L3.id}`);
    await p.getByTestId('decision-panel').waitFor();
    r.check('four eyes: the author sees a disabled Approve with the reason', (await p.getByRole('button', { name: 'Approve', exact: true }).isDisabled()) && (await p.getByTestId('approve-hint').innerText()) === 'You prepared this list; someone else must approve it.');
    r.check('the author may still send it back (reject is offered)', await p.getByRole('button', { name: 'Reject', exact: true }).isEnabled());
    await p.context().close();
  }

  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => {
  console.error('FAIL  unexpected error:', e);
  process.exit(1);
});
