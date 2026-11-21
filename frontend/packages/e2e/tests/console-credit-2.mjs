// B3.12: the rest of the credit books in the staff console: aging, company statement, hold override, apply on-account, adjustments.
// Needs the same setup as console-credit.mjs (B3.11): the API (Development, SCRATCH DB with the demo catalogue), the console built against it,
// two Finance accounts finance.a.e2e@ and finance.b.e2e@ cloned from the dev admin row (same TOTP secret, then `Mechware.Worker migrate`),
// and the dev admin. The credit orders are placed through the real API; two of their charges are then moved into the past with SQL in the
// SCRATCH DB only (E2E_SQL_CMD = a command that reads SQL on stdin, for example `docker exec -i mechware_postgres psql -U mechware -d mw_b312 -At -q`).
// Set E2E_CONSOLE_CREDIT2=1 to run; otherwise it exits 0 without doing anything.
// Real: sign-in with TOTP, permission gating, aging buckets/totals/hold badge/filter/CSV with a wrong then a real code, statement + range check
// + CSV, hold override (Admin only, code), adjustments (request by Finance, approve by Admin with a code, four eyes on screen, reject, withdraw),
// apply on-account (one Idempotency-Key per body, server refusals in words), axe 0, 360 px. Mocked: nothing.
import { execSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, CONSOLE as WEB, SHOTS, USERS, apiCall, apiLogin, launch, reporter, totp, uniqueBin } from './lib.mjs';

if (process.env.E2E_CONSOLE_CREDIT2 !== '1') {
  console.log('SKIP  console credit 2 E2E (set E2E_CONSOLE_CREDIT2=1 to run it)');
  process.exit(0);
}
if (!process.env.E2E_SQL_CMD) throw new Error('E2E_SQL_CMD is needed (a command that reads SQL from stdin, against the SCRATCH database only)');

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
/** Polls until `fn` returns true (a list is reloaded in the background, so the old rows are still on screen for a moment). */
async function settled(fn, ms = 10000) {
  const end = Date.now() + ms;
  while (Date.now() < end) { if (await fn()) return true; await sleep(200); }
  return false;
}
const overflow = (p) => p.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
const day = (n) => new Date(Date.now() + 6 * 3600000 + n * 86400000).toISOString().slice(0, 10);
const tag = String(Date.now() % 1000000);
const sql = (text) => execSync(process.env.E2E_SQL_CMD, { input: text, encoding: 'utf8' });

const SECRET = USERS.admin.totpSecret;
const PASSWORD = USERS.admin.password;
const PEOPLE = {
  admin: USERS.admin,
  a: { email: 'finance.a.e2e@mechware.com.bd', password: PASSWORD, totpSecret: SECRET },
  b: { email: 'finance.b.e2e@mechware.com.bd', password: PASSWORD, totpSecret: SECRET }
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
  const ctx = await browser.newContext({ viewport: { width, height: 900 }, acceptDownloads: true });
  const page = await ctx.newPage();
  page.errors = [];
  page.on('pageerror', (e) => page.errors.push('pageerror: ' + e.message));
  return page;
}
const send = (token, method, path, body, headers = {}) =>
  fetch(`${API}${path}`, { method, headers: { 'content-type': 'application/json', authorization: `Bearer ${token}`, ...headers }, body: body ? JSON.stringify(body) : undefined })
    .then(async (res) => ({ status: res.status, json: await res.json().catch(() => ({})) }));
const bdt = (minor) => `${Math.floor(minor / 100).toLocaleString('en-US')}.${String(minor % 100).padStart(2, '0')}`;
let keySeq = 0;
const key = (what) => `e2e-b312-${tag}-${what}-${++keySeq}`;

(async () => {
  // ---------------- setup through the real API
  const adminToken = await apiToken(PEOPLE.admin);
  const sellerToken = await apiLogin(USERS.sellerAlpha);
  const buyer = await apiLogin(USERS.buyer);
  const legalName = `Aging Test Mills ${tag} Ltd`;
  const reg = await apiCall(buyer, 'POST', '/api/v1/companies', { legalName, tradeLicenceNo: 'TL-B312', bin: uniqueBin() });
  const companyId = reg.json.data.id;
  const act = await apiCall(adminToken, 'POST', `/api/v1/admin/companies/${companyId}/activate`);
  const me = (await apiCall(buyer, 'GET', '/api/v1/users/me')).json.data.id;
  const members = (await apiCall(buyer, 'GET', `/api/v1/companies/${companyId}/members`)).json.data;
  const own = members.find((x) => x.userId === me);
  await apiCall(buyer, 'PUT', `/api/v1/companies/${companyId}/members/${own.id}`, { roles: 10, spendLimitMinor: null, costCentreIds: [], isActive: true });
  const site = await apiCall(buyer, 'POST', `/api/v1/companies/${companyId}/sites`, { name: 'Gazipur Plant', contactName: 'Sabbir', contactPhone: '01711000000', line1: 'Plot 5, BSCIC Area', upazila: 'Tongi Industrial Zone', district: 'Gazipur', division: 'Dhaka', isDefaultDelivery: true });
  const credit = await send(adminToken, 'PUT', `/api/v1/admin/companies/${companyId}/credit`, { terms: 15, limitMinorUnits: 500000000 }, { 'X-Step-Up-Code': await freshCode(PEOPLE.admin) });
  r.check('setup: an active company with 15-day credit', reg.status === 201 && act.status === 200 && site.status === 201 && credit.status === 200, `${reg.status}/${act.status}/${site.status}/${credit.status} ${JSON.stringify(credit.json).slice(0, 150)}`);
  r.check('setup: a seller token gets 403 on the aging report (real API)', (await apiCall(sellerToken, 'GET', '/api/v1/admin/credit/aging')).status === 403);

  // A variant with stock (scratch DB: topped up, so a re-run never runs dry); the demo catalogue's stock is small.
  const picked = sql("select p.slug, v.id from products p join product_variants v on v.product_id = p.id join inventory_items i on i.variant_id = v.id where p.status = 'active' and v.status = 'active' order by v.id limit 1;").split(String.fromCharCode(10)).map((x) => x.trim()).find(Boolean);
  const [slugRow, pickedId] = picked.split('|');
  sql(`UPDATE inventory_items SET on_hand = 500, reserved = 0 WHERE variant_id = '${pickedId}';`);
  const variant = (await (await fetch(`${API}/api/v1/storefront/products/${slugRow}`)).json()).data.variants.find((v) => v.id === pickedId) ?? (await (await fetch(`${API}/api/v1/storefront/products/${slugRow}`)).json()).data.variants[0];
  async function placeCreditOrder(quantity) {
    const res = await fetch(`${API}/api/v1/orders`, {
      method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${buyer}`, 'idempotency-key': key('order') },
      body: JSON.stringify({ lines: [{ variantId: variant.id, quantity }], companyId, shipToSiteId: site.json.data.id, payOnCredit: true })
    });
    const j = await res.json();
    if (!j.data) throw new Error(`credit order failed: ${res.status} ${JSON.stringify(j).slice(0, 300)}`);
    return j.data.orderId;
  }
  const o1 = await placeCreditOrder(2);
  const o2 = await placeCreditOrder(1);
  // Move the two charges into the past (scratch DB only; the ledger is append-only, so the guards are switched off for this one statement).
  sql(`BEGIN; SET LOCAL session_replication_role = replica;
    UPDATE credit_ledger_entries SET due_on = current_date - 40 WHERE order_id = '${o1}' AND kind = 'charge';
    UPDATE credit_ledger_entries SET due_on = current_date - 5 WHERE order_id = '${o2}' AND kind = 'charge'; COMMIT;`);
  const books = async () => (await send(adminToken, 'GET', `/api/v1/admin/companies/${companyId}/credit/statement?pageSize=200`)).json.data;
  const aging = async () => (await send(adminToken, 'GET', `/api/v1/admin/credit/aging?companyId=${companyId}`)).json.data;
  const ag0 = await aging();
  const row0 = ag0.companies.items[0];
  const owed1 = row0.buckets.days31To60;
  const owed2 = row0.buckets.days1To30;
  r.check('setup: two credit orders, one 40 days and one 5 days past due; the company is on hold', owed1 > 0 && owed2 > 0 && row0.onHold === true && row0.receivableMinor === owed1 + owed2, JSON.stringify(row0.buckets));

  const browser = await launch();

  // ================= seller: no Credit links, refused pages
  {
    const p = await newPage(browser);
    await p.goto(`${WEB}/login`, { timeout: 120000, waitUntil: 'networkidle' });
    await p.getByLabel('Staff or seller email').fill(USERS.sellerAlpha.email);
    await p.getByLabel('Password').fill(USERS.sellerAlpha.password);
    await p.getByRole('button', { name: 'Sign in' }).click();
    await p.waitForURL(`${WEB}/`, { timeout: 120000 });
    await p.getByText('Operations console').first().waitFor();
    r.check('seller sees no new Credit links', (await p.getByRole('link', { name: /Credit aging|Company credit books|Credit adjustments/ }).count()) === 0);
    for (const [path, perm] of [['/credit/aging', 'credit.view'], ['/credit/adjustments', 'credit.view'], [`/credit/companies/${companyId}`, 'credit.view'], [`/credit/companies/${companyId}/apply`, 'credit.record_payment'], [`/credit/companies/${companyId}/adjust`, 'credit.adjust']]) {
      await p.goto(`${WEB}${path}`);
      await p.getByText(`Your role does not allow this action (${perm}).`).waitFor();
    }
    r.check('seller opening every new route is told no (aging, adjustments, company, apply, adjust)', true);
    await p.context().close();
  }

  // ================= finance A: aging
  const a = await newPage(browser);
  await consoleSignIn(a, PEOPLE.a);
  const nav = a.getByRole('navigation', { name: 'Modules' });
  r.check('finance sees the three new Credit links', (await nav.getByRole('link', { name: /Credit aging/ }).count()) === 1 && (await nav.getByRole('link', { name: /Company credit books/ }).count()) === 1 && (await nav.getByRole('link', { name: /Credit adjustments/ }).count()) === 1);
  await nav.getByRole('link', { name: /Credit aging/ }).click();
  await a.waitForURL(/\/credit\/aging$/);
  await a.getByTestId('aging-table').waitFor();
  const mine = a.getByTestId('aging-row').filter({ hasText: legalName });
  await mine.waitFor();
  const text = (await mine.innerText()).replace(/\s+/g, ' ');
  r.check('aging row: company, On hold badge, the 31-60 bucket holds order 1, the 1-30 bucket holds order 2', text.includes('On hold') && text.includes(bdt(owed1)) && text.includes(bdt(owed2)) && text.includes(bdt(owed1 + owed2)), text);
  r.check('aging totals line and total row are shown (all matching companies)', (await a.getByTestId('aging-totals').innerText()).includes('all') && (await a.getByTestId('aging-total-row').count()) === 1);
  await axeCheck(a, '/credit/aging');
  await a.getByLabel('Company name').fill(`Aging Test Mills ${tag}`);
  await a.getByRole('button', { name: 'Search' }).click();
  await a.getByTestId('aging-row').filter({ hasText: legalName }).waitFor();
  r.check('search by name narrows the report to this company', await settled(async () => (await a.getByTestId('aging-row').count()) === 1));
  await a.getByTestId('aging-overdue').check();
  await a.getByTestId('aging-row').filter({ hasText: legalName }).waitFor();
  r.check('overdue only keeps this company (it has overdue debt)', await settled(async () => (await a.getByTestId('aging-row').count()) === 1));
  await a.getByLabel('Company name').fill('zzzz no such company');
  await a.getByRole('button', { name: 'Search' }).click();
  await a.getByTestId('aging-empty').waitFor();
  r.check('no match: an empty-state sentence', true);
  await a.getByLabel('Company name').fill('');
  await a.getByRole('button', { name: 'Search' }).click();
  await a.getByTestId('aging-row').filter({ hasText: legalName }).waitFor();

  // CSV: wrong code first, then a real one
  let downloaded = false;
  a.on('download', () => { downloaded = true; });
  await a.getByTestId('aging-csv').click();
  const dlg = a.getByRole('dialog');
  await dlg.getByLabel('Authenticator code').fill('000000');
  await dlg.getByRole('button', { name: 'Download' }).click();
  await a.getByTestId('stepup-error').waitFor();
  r.check('aging CSV with a wrong code: dialog stays open, plain words, nothing downloaded', (await a.getByTestId('stepup-error').innerText()).includes('not accepted') && !downloaded);
  await axeCheck(a, 'aging CSV code dialog with an error');
  await dlg.getByLabel('Authenticator code').fill(await freshCode(PEOPLE.a));
  const [file] = await Promise.all([a.waitForEvent('download'), dlg.getByRole('button', { name: 'Download' }).click()]);
  const csv = readFileSync(await file.path(), 'utf8');
  r.check('aging CSV with a real code: right file name, byte-order mark, header row, this company', file.suggestedFilename() === `credit-aging-overdue-${day(0)}.csv` && csv.charCodeAt(0) === 0xfeff && csv.includes(legalName), `${file.suggestedFilename()} ${JSON.stringify(csv.slice(0, 60))}`);
  await a.getByTestId('aging-notice').waitFor();
  r.check('aging CSV: the notice says it is audited', (await a.getByTestId('aging-notice').innerText()).includes('audit'));

  // ---- company books: statement, range check, CSV, read-only hold panel
  await a.getByRole('link', { name: legalName }).click();
  await a.getByTestId('company-summary').waitFor();
  await a.getByTestId('statement-table').waitFor();
  r.check('company page: owes now = both orders; the statement has both charges', (await a.getByTestId('owes-now').innerText()).includes(bdt(owed1 + owed2)) && (await a.getByTestId('statement-row').count()) >= 2);
  r.check('company page: hold panel says on hold, and Finance may not lift it (read-only sentence, no form)', (await a.getByTestId('hold-state').innerText()).includes('On hold') && (await a.getByTestId('hold-readonly').count()) === 1 && (await a.getByRole('form', { name: 'Lift the hold' }).count()) === 0);
  r.check('company page: Finance sees Apply and Request links', (await a.getByRole('link', { name: 'Apply on-account money' }).count()) === 1 && (await a.getByRole('link', { name: 'Request an adjustment' }).count()) === 1);
  await axeCheck(a, 'company credit books (read-only hold)');
  await a.getByLabel('From (day)').fill(day(1));
  await a.getByLabel('To (day)').fill(day(0));
  const posts = [];
  a.on('request', (req) => { if (req.url().includes('/statement')) posts.push(req.url()); });
  await a.getByRole('button', { name: 'Show' }).click();
  await a.getByText('The first day must not be after the last day.').waitFor();
  r.check('a backwards date range is refused on screen, nothing is requested', posts.length === 0);
  await a.getByLabel('From (day)').fill(day(-1));
  await a.getByLabel('To (day)').fill(day(1));
  await a.getByRole('button', { name: 'Show' }).click();
  await a.getByTestId('statement-row').first().waitFor();
  r.check('a valid range reloads the statement (from/to in the request)', posts.some((u) => u.includes(`from=${day(-1)}`) && u.includes(`to=${day(1)}`)), posts.join(' '));
  const [sfile] = await Promise.all([a.waitForEvent('download'), a.getByTestId('statement-csv').click()]);
  const scsv = readFileSync(await sfile.path(), 'utf8');
  r.check('statement CSV downloads without a code: file name, byte-order mark, the order numbers', sfile.suggestedFilename().startsWith('credit-statement-aging-test-mills-') && scsv.charCodeAt(0) === 0xfeff && scsv.length > 100, sfile.suggestedFilename());
  await a.getByTestId('statement-notice').waitFor();

  // ================= admin: hold override with a code
  const admin = await newPage(browser);
  await consoleSignIn(admin, PEOPLE.admin);
  await admin.goto(`${WEB}/credit/companies/${companyId}`);
  await admin.getByTestId('hold-panel').waitFor();
  r.check('admin sees the lift form', (await admin.getByRole('form', { name: 'Lift the hold' }).count()) === 1);
  await admin.getByLabel('Days', { exact: true }).fill('8');
  await admin.getByLabel(/Reason/).fill('abc');
  await admin.getByTestId('hold-continue').click();
  await admin.getByText(/Whole days from 0 to 7/).waitFor();
  await admin.getByText('The reason must be 5 to 500 characters.').waitFor();
  r.check('8 days and a 3-letter reason are stopped BEFORE any code is asked for', (await admin.getByLabel('Authenticator code').count()) === 0);
  await admin.getByLabel('Days', { exact: true }).fill('3');
  await admin.getByLabel(/Reason/).fill('Promised payment on Sunday (E2E)');
  await axeCheck(admin, 'hold override form');
  await admin.getByTestId('hold-continue').click();
  const hd = admin.getByRole('dialog');
  await hd.getByLabel('Authenticator code').fill('000000');
  await hd.getByRole('button', { name: 'Lift the hold' }).click();
  await admin.getByTestId('stepup-error').waitFor();
  r.check('hold override with a wrong code: the dialog stays open with plain words', (await admin.getByTestId('stepup-error').innerText()).includes('not accepted'));
  await hd.getByLabel('Authenticator code').fill(await freshCode(PEOPLE.admin));
  await hd.getByRole('button', { name: 'Lift the hold' }).click();
  await admin.getByTestId('hold-notice').waitFor();
  await admin.getByTestId('hold-state').filter({ hasText: 'Hold lifted until' }).waitFor();
  r.check('hold lifted for 3 days: notice, state line shows the end, API agrees (onHold false)', (await admin.getByTestId('hold-notice').innerText()).includes('lifted for 3 days') && (await aging()).companies.items[0].onHold === false);
  await admin.goto(`${WEB}/credit/aging`);
  await admin.getByTestId('aging-row').filter({ hasText: legalName }).filter({ hasText: 'Hold lifted' }).waitFor();
  r.check('aging shows "Hold lifted" instead of "On hold" for this company', true);

  // ================= adjustments: Finance A requests, Admin approves
  await a.goto(`${WEB}/credit/companies/${companyId}/adjust`);
  await a.getByRole('form', { name: 'Request an adjustment' }).waitFor();
  await axeCheck(a, 'request an adjustment');
  await a.getByRole('button', { name: 'Request adjustment' }).click();
  await a.getByText('Choose the order to write off.').waitFor();
  await a.getByText('Enter the amount in BDT.').waitFor();
  await a.getByText('Enter a reason.').waitFor();
  r.check('empty request form: order, amount and reason errors, focus on the alert', (await a.evaluate(() => document.activeElement?.getAttribute('data-testid'))) === 'adjust-alert');
  await a.getByTestId('adjust-order').selectOption({ index: 1 });
  r.check('write-off order list shows what each order owes', (await a.getByTestId('adjust-order').innerText()).includes('owes BDT'));
  await a.getByLabel('Amount (BDT)').fill('-5');
  await a.getByLabel(/Reason/).fill('Agreed discount (E2E)');
  await a.getByRole('button', { name: 'Request adjustment' }).click();
  await a.getByText(/Use digits and at most 2 decimals/).waitFor();
  r.check('a negative write-off amount and a 20 character reason are refused before sending', true);
  await a.getByTestId('adjust-kind').selectOption('on_account_correction');
  await a.getByTestId('adjust-help').getByText(/ONE payment or credit note/).waitFor();
  r.check('choosing another kind shows its own boxes (payment, signed amount) and help', (await a.getByTestId('adjust-payment').count()) === 1 && (await a.getByTestId('adjust-order').count()) === 0);
  await a.getByTestId('adjust-kind').selectOption('write_off');
  const orderOpt = a.getByTestId('adjust-order');
  const o2Label = await orderOpt.locator('option').evaluateAll((opts, owed) => opts.find((o) => o.textContent.includes(owed))?.value ?? '', bdt(owed2));
  await orderOpt.selectOption(o2Label || { index: 1 });
  await a.getByLabel('Amount (BDT)').fill('100');
  await a.getByLabel(/Reason/).fill('Agreed discount on delivery delay (E2E)');
  const adjKeys = [];
  a.on('request', (req) => { if (req.method() === 'POST' && /credit\/adjustments$/.test(req.url())) adjKeys.push(req.headers()['idempotency-key']); });
  await a.getByRole('button', { name: 'Request adjustment' }).click();
  await a.getByTestId('adjust-requested').waitFor();
  r.check('write-off requested: panel says it moves no money; one valid Idempotency-Key', (await a.getByTestId('adjust-requested').innerText()).includes('moves no money') && adjKeys.length === 1 && /^[A-Za-z0-9_-]{8,100}$/.test(adjKeys[0] ?? ''));
  const before = (await books()).currentReceivableMinor;
  r.check('the request moved nothing (API receivable unchanged)', before === owed1 + owed2);
  const adjId1 = (await a.getByRole('link', { name: 'Open this adjustment' }).getAttribute('href')).split('/').pop();
  await a.getByRole('link', { name: 'Open this adjustment' }).click();
  await a.getByTestId('adjustment-detail').waitFor();
  r.check('Finance A (requester, no approve permission): Approve disabled, reason on screen; Withdraw offered', (await a.getByRole('button', { name: 'Approve' }).isDisabled()) && (await a.getByRole('button', { name: 'Withdraw' }).isEnabled()) && (await a.getByTestId('adjust-hint').innerText()).includes('Only an Admin'));
  await axeCheck(a, 'adjustment detail (requester)');

  // A second request the Admin can reject, and a third that Finance A withdraws
  const request = async (page, amount, reason) => {
    await page.goto(`${WEB}/credit/companies/${companyId}/adjust`);
    await page.getByRole('form', { name: 'Request an adjustment' }).waitFor();
    await page.getByTestId('adjust-order').selectOption({ index: 1 });
    await page.getByLabel('Amount (BDT)').fill(amount);
    await page.getByLabel(/Reason/).fill(reason);
    await page.getByRole('button', { name: 'Request adjustment' }).click();
    await page.getByTestId('adjust-requested').waitFor();
    return (await page.getByRole('link', { name: 'Open this adjustment' }).getAttribute('href')).split('/').pop();
  };
  const adjId2 = await request(a, '7', 'Second request, to be rejected (E2E)');
  const adjId3 = await request(admin, '8', 'Third request, made by the Admin (E2E)');

  // Admin approves #1 with a code
  await admin.goto(`${WEB}/credit/adjustments`);
  await admin.getByTestId('adjust-queue').waitFor();
  r.check('queue (waiting): all three requests, oldest first', (await admin.getByTestId('adjust-row').filter({ hasText: legalName }).count()) === 3);
  await axeCheck(admin, '/credit/adjustments');
  await admin.goto(`${WEB}/credit/adjustments/${adjId1}`);
  await admin.getByTestId('adjustment-detail').waitFor();
  r.check('Admin (not the requester): Approve and Reject are enabled', (await admin.getByRole('button', { name: 'Approve' }).isEnabled()) && (await admin.getByRole('button', { name: 'Reject' }).isEnabled()));
  await admin.getByRole('button', { name: 'Approve' }).click();
  await axeCheck(admin, 'approve dialog');
  await admin.getByRole('dialog').getByLabel('Authenticator code').fill('000000');
  await admin.getByRole('dialog').getByRole('button', { name: 'Approve' }).click();
  await admin.getByTestId('stepup-error').waitFor();
  r.check('approve with a wrong code: the dialog stays open', (await admin.getByTestId('stepup-error').innerText()).includes('not accepted'));
  await admin.getByRole('dialog').getByLabel('Authenticator code').fill(await freshCode(PEOPLE.admin));
  await admin.getByRole('dialog').getByRole('button', { name: 'Approve' }).click();
  await admin.getByTestId('adjust-decision-notice').waitFor();
  await admin.getByTestId('adjust-lines').waitFor();
  r.check('approved with a real code: notice, status Approved, a write-off ledger line of -100.00', (await admin.getByTestId('adjust-status-badge').innerText()) === 'Approved' && (await admin.getByTestId('adjust-lines').innerText()).includes('-100.00') && (await admin.getByTestId('adjust-lines').innerText()).includes('Written off'));
  r.check('the API agrees: the company owes exactly 100.00 BDT less', (await books()).currentReceivableMinor === owed1 + owed2 - 10000);

  // #3 made by the Admin: four eyes on screen, then withdrawn by the Admin
  await admin.goto(`${WEB}/credit/adjustments/${adjId3}`);
  await admin.getByTestId('adjustment-detail').waitFor();
  r.check('Admin who requested it: Approve disabled with "You requested this adjustment", Withdraw offered', (await admin.getByRole('button', { name: 'Approve' }).isDisabled()) && (await admin.getByTestId('adjust-hint').innerText()).includes('You requested this adjustment') && (await admin.getByRole('button', { name: 'Withdraw' }).isEnabled()));
  const refused = await send(adminToken, 'POST', `/api/v1/admin/credit/adjustments/${adjId3}/approve`, undefined, { 'X-Step-Up-Code': await freshCode(PEOPLE.admin) });
  r.check('the real API also refuses: 409 CREDIT_ADJUSTMENT_FOUR_EYES', refused.status === 409 && refused.json.error?.code === 'CREDIT_ADJUSTMENT_FOUR_EYES', `${refused.status} ${refused.json.error?.code}`);
  await admin.getByRole('button', { name: 'Withdraw' }).click();
  await admin.getByRole('dialog').getByRole('button', { name: 'Withdraw request' }).click();
  await admin.getByRole('dialog').getByText('Enter a reason.').waitFor();
  await admin.getByRole('dialog').getByLabel(/Reason/).fill('Entered by mistake (E2E)');
  await axeCheck(admin, 'withdraw dialog');
  await admin.getByRole('dialog').getByRole('button', { name: 'Withdraw request' }).click();
  await admin.getByTestId('adjust-decision-notice').filter({ hasText: 'Withdrawn' }).waitFor();
  await admin.getByTestId('adjust-status-badge').filter({ hasText: 'Rejected' }).waitFor(); // the notice shows before the reload lands
  r.check('withdrawn without a code: status Rejected, reason shown, nothing moved', (await admin.getByTestId('adjust-status-badge').innerText()) === 'Rejected' && (await admin.getByTestId('arow-rejected').innerText()).includes('Entered by mistake') && (await books()).currentReceivableMinor === owed1 + owed2 - 10000);

  // #2 rejected by Finance B (credit.adjust, not the requester)
  const b = await newPage(browser);
  await consoleSignIn(b, PEOPLE.b);
  await b.goto(`${WEB}/credit/adjustments/${adjId2}`);
  await b.getByTestId('adjustment-detail').waitFor();
  r.check('Finance B: may Reject (not Withdraw), may not approve (Admin only)', (await b.getByRole('button', { name: 'Reject' }).isEnabled()) && (await b.getByRole('button', { name: 'Approve' }).isDisabled()));
  await b.getByRole('button', { name: 'Reject' }).click();
  await b.getByRole('dialog').getByLabel(/Reason/).fill('Not agreed with the buyer (E2E)');
  await b.getByRole('dialog').getByRole('button', { name: 'Reject adjustment' }).click();
  await b.getByTestId('adjust-decision-notice').filter({ hasText: 'Rejected' }).waitFor();
  await b.getByTestId('adjust-status-badge').filter({ hasText: 'Rejected' }).waitFor(); // the notice shows before the reload lands
  r.check('rejected by another Finance user: status Rejected', (await b.getByTestId('adjust-status-badge').innerText()) === 'Rejected');
  await b.goto(`${WEB}/credit/adjustments`);
  await b.getByTestId('adjust-status-filter').selectOption('approved');
  await b.getByTestId('adjust-row').filter({ hasText: legalName }).waitFor();
  await b.getByTestId('adjust-status-filter').selectOption('rejected');
  await b.getByTestId('adjust-row').filter({ hasText: legalName }).nth(1).waitFor();
  r.check('queue filters: Approved shows the write-off, Rejected shows the other two', await settled(async () => (await b.getByTestId('adjust-row').filter({ hasText: legalName }).count()) === 2));

  // ================= payment that clears the debt and leaves money on account; then a new order to apply it to
  const owedNow = (await books()).currentReceivableMinor;
  const aToken = await apiToken(PEOPLE.a);
  const bToken = await apiToken(PEOPLE.b);
  const rec = await send(aToken, 'POST', `/api/v1/admin/companies/${companyId}/credit/payments`, { method: 'beftn', amountMinor: owedNow + 200000, receivedOn: day(0), reference: `B312-${tag}-P1` }, { 'Idempotency-Key': key('pay') });
  const ver = await send(bToken, 'POST', `/api/v1/admin/credit/payments/${rec.json.data?.id}/verify`, { clearedOn: day(0) }, { 'X-Step-Up-Code': await freshCode(PEOPLE.b) });
  const after = await books();
  r.check('setup: a payment of everything owed plus 2,000.00 BDT is verified; it all stays on account', rec.status === 201 && ver.status === 200 && after.currentReceivableMinor === 0 && after.currentOnAccountMinor === 200000, `${rec.status}/${ver.status} owed ${after.currentReceivableMinor} onAccount ${after.currentOnAccountMinor}`);
  const o3 = await placeCreditOrder(1);
  const afterO3 = await books();
  r.check('setup: the hold is gone (no overdue debt): a new credit order is accepted', afterO3.currentReceivableMinor > 0 || afterO3.currentOnAccountMinor < 200000, `owed ${afterO3.currentReceivableMinor} onAccount ${afterO3.currentOnAccountMinor}`);
  // Placing an order does not take on-account money by itself: on-account is still 2,000.00 and the new order is open.
  r.check('the new order is open and the 2,000.00 BDT is still on account', afterO3.currentOnAccountMinor === 200000 && afterO3.currentReceivableMinor > 200000, `owed ${afterO3.currentReceivableMinor}`);

  // A correction of -500.00 on that payment: Finance A requests, Admin approves
  await a.goto(`${WEB}/credit/companies/${companyId}/adjust`);
  await a.getByRole('form', { name: 'Request an adjustment' }).waitFor();
  await a.getByTestId('adjust-kind').selectOption('on_account_correction');
  await a.getByTestId('adjust-payment').selectOption({ index: 1 });
  await a.getByLabel('Amount (BDT, minus takes money off)').fill('-500');
  await a.getByLabel(/Reason/).fill('Overstated payment, bank slip says less (E2E)');
  await a.getByRole('button', { name: 'Request adjustment' }).click();
  await a.getByTestId('adjust-requested').waitFor();
  const adjId4 = (await a.getByRole('link', { name: 'Open this adjustment' }).getAttribute('href')).split('/').pop();
  await admin.goto(`${WEB}/credit/adjustments/${adjId4}`);
  await admin.getByTestId('adjustment-detail').waitFor();
  await admin.getByRole('button', { name: 'Approve' }).click();
  await admin.getByRole('dialog').getByLabel('Authenticator code').fill(await freshCode(PEOPLE.admin));
  await admin.getByRole('dialog').getByRole('button', { name: 'Approve' }).click();
  await admin.getByTestId('adjust-decision-notice').waitFor();
  r.check('correction -500.00 approved: on-account goes from 2,000.00 to 1,500.00 (API)', (await books()).currentOnAccountMinor === 150000 && (await admin.getByTestId('adjust-lines').innerText()).includes('On-account correction'));

  // ================= apply on-account
  await a.goto(`${WEB}/credit/companies/${companyId}/apply`);
  await a.getByTestId('apply-position').waitFor();
  r.check('apply page shows the on-account money (1,500.00)', (await a.getByTestId('apply-on-account').innerText()).includes('1,500.00'));
  await axeCheck(a, 'apply on-account');
  await a.getByLabel('Amount to apply (BDT, optional)').fill('2,000');
  await a.getByRole('button', { name: 'Apply on-account money' }).click();
  await a.getByText('The company has only BDT 1,500.00 on account.').waitFor();
  r.check('more than the on-account money is stopped on screen, nothing sent', (await a.evaluate(() => document.activeElement?.getAttribute('data-testid'))) === 'apply-alert');
  await a.getByLabel('Amount to apply (BDT, optional)').fill('1.234');
  await a.getByRole('button', { name: 'Apply on-account money' }).click();
  await a.getByText(/at most 2 decimals/).waitFor();
  await a.getByLabel('Amount to apply (BDT, optional)').fill('');
  await a.getByRole('button', { name: 'Name an order' }).click();
  await a.getByRole('button', { name: 'Apply on-account money' }).click();
  await a.getByText('Choose the order.').waitFor();
  await a.getByTestId('allocation-row').locator('select').selectOption(o3);
  await a.getByLabel('Amount for order 1 (BDT)').fill('5,000');
  const applyKeys = [];
  a.on('request', (req) => { if (req.method() === 'POST' && /apply-on-account$/.test(req.url())) applyKeys.push(req.headers()['idempotency-key']); });
  await a.getByRole('button', { name: 'Apply on-account money' }).click();
  await a.getByTestId('apply-alert').waitFor();
  const refusal = await a.getByTestId('apply-alert').innerText();
  r.check('naming 5,000.00 BDT with 1,500.00 on account: the server refuses in plain words (not a raw code)', applyKeys.length === 1 && refusal.length > 30 && !/^[A-Z_]+$/.test(refusal.trim()) && !refusal.includes('Check the highlighted'), refusal);
  r.check('nothing was applied by the refused request (API on-account still 1,500.00)', (await books()).currentOnAccountMinor === 150000);
  await a.getByLabel('Amount for order 1 (BDT)').fill('1,000');
  await a.getByRole('button', { name: 'Apply on-account money' }).click();
  await a.getByTestId('apply-result').waitFor();
  r.check('1,000.00 applied to the named order: result sentence, a changed body got its own key', (await a.getByTestId('apply-result').innerText()).includes('BDT 1,000.00 applied to 1 order') && applyKeys.length === 2 && applyKeys[0] !== applyKeys[1] && /^[A-Za-z0-9_-]{8,100}$/.test(applyKeys[1] ?? ''), applyKeys.join(','));
  const mid = await books();
  r.check('the API agrees: on-account 500.00, owed down by 1,000.00', mid.currentOnAccountMinor === 50000 && mid.currentReceivableMinor === afterO3.currentReceivableMinor - 100000, `${mid.currentOnAccountMinor} ${mid.currentReceivableMinor}`);
  await a.getByTestId('apply-on-account').filter({ hasText: '500.00' }).waitFor();
  await a.getByRole('button', { name: 'Apply on-account money' }).click();
  await a.getByTestId('apply-result').filter({ hasText: 'BDT 500.00 applied' }).waitFor();
  await a.getByTestId('apply-on-account').filter({ hasText: 'BDT 0.00' }).waitFor();
  await a.getByRole('button', { name: 'Apply on-account money' }).click();
  await a.getByTestId('apply-alert').filter({ hasText: 'nothing to apply' }).waitFor().catch(() => {});
  const last = (await a.getByTestId('apply-alert').innerText().catch(() => '')) || '';
  r.check('applying everything (empty amount) takes the last 500.00; a further apply is refused in words ("nothing to apply")', /nothing to apply/i.test(last), last);
  await axeCheck(a, 'apply on-account with a server refusal');

  // ================= 360 px
  const m = await newPage(browser, 360);
  await consoleSignIn(m, PEOPLE.b);
  for (const path of ['/credit/aging', `/credit/companies/${companyId}`, '/credit/adjustments', `/credit/adjustments/${adjId1}`, `/credit/companies/${companyId}/apply`, `/credit/companies/${companyId}/adjust`]) {
    await m.goto(`${WEB}${path}`);
    await m.getByRole('heading', { level: 1 }).waitFor();
    await sleep(900);
    r.check(`360 px: no page overflow on ${path.replace(companyId, '{company}').replace(adjId1, '{adj}')}`, !(await overflow(m)));
  }
  await a.screenshot({ path: `${SHOTS}/b3-12-apply-1280.png`, fullPage: true });
  await admin.goto(`${WEB}/credit/companies/${companyId}`);
  await admin.getByTestId('statement-table').waitFor();
  await admin.screenshot({ path: `${SHOTS}/b3-12-company-1280.png`, fullPage: true });

  r.check('no page errors in any window', [a, admin, b, m].every((pg) => pg.errors.length === 0), [a, admin, b, m].flatMap((pg) => pg.errors).join(' | '));
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
