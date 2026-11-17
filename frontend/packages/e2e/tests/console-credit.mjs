// B3.11: credit payments in the staff console: record, verification queue, verify / reject / bounce with step-up. Needs the API
// (Development, scratch DB), the console and two Finance accounts on the SAME authenticator secret as the dev admin:
// finance.a.e2e@ (records) and finance.b.e2e@ (verifies). Clone the admin row in the scratch DB with role_name 'finance', store the
// secret in clear, then run `Mechware.Worker migrate` once (it encrypts it); see docs/modules/B3.md (HANDOFF B3.11).
// Set E2E_CONSOLE_CREDIT=1 to run; otherwise it exits 0 without doing anything.
// Real: TOTP sign-in, permission gating (seller has no Credit links and is refused), record form (client checks, one Idempotency-Key per
// submit, duplicate reference in plain words), queue with flags, four eyes (button disabled for the recorder AND the real API refusal),
// post-dated cheque (verify disabled, reject allowed), verify with a wrong then a real code, ledger lines, bounce (always a code),
// reject (reference freed again), queue filter, axe 0 on pages and dialogs, 360 px no overflow. Mocked: nothing.
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, CONSOLE as WEB, SHOTS, USERS, apiCall, apiLogin, launch, reporter, totp, uniqueBin } from './lib.mjs';

if (process.env.E2E_CONSOLE_CREDIT !== '1') {
  console.log('SKIP  console credit E2E (set E2E_CONSOLE_CREDIT=1 to run it)');
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
const day = (n) => new Date(Date.now() + 6 * 3600000 + n * 86400000).toISOString().slice(0, 10);
const tag = String(Date.now() % 1000000);

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
  const ctx = await browser.newContext({ viewport: { width, height: 900 } });
  const page = await ctx.newPage();
  page.errors = [];
  page.on('pageerror', (e) => page.errors.push('pageerror: ' + e.message));
  return page;
}
const post = (token, path, body, headers = {}) =>
  fetch(`${API}${path}`, { method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${token}`, ...headers }, body: JSON.stringify(body) })
    .then(async (res) => ({ status: res.status, json: await res.json().catch(() => ({})) }));

/** Fills the record form through the screen (company picked from the list). */
async function fillRecord(p, company, { method, amount, received, reference, bank, chequeDate }) {
  await p.getByLabel('Company', { exact: true }).fill(company.slice(0, 18));
  await p.getByTestId('company-list').selectOption({ label: company });
  await p.getByTestId('method').selectOption(method);
  await p.getByLabel('Amount (BDT)').fill(amount);
  if (received) await p.getByLabel('Date received').fill(received);
  if (reference !== undefined) await p.getByLabel(/number|reference|Transaction id/i).first().fill(reference);
  if (bank) await p.getByLabel('Bank', { exact: true }).fill(bank);
  if (chequeDate) await p.getByLabel('Date on the cheque').fill(chequeDate);
}

(async () => {
  // ---- setup through the API
  const adminToken = await apiToken(PEOPLE.admin);
  const sellerToken = await apiLogin(USERS.sellerAlpha);
  const buyer = await apiLogin(USERS.buyer);
  const legalName = `Credit Test Mills ${tag} Ltd`;
  const reg = await apiCall(buyer, 'POST', '/api/v1/companies', { legalName, tradeLicenceNo: 'TL-CR-1', bin: uniqueBin() });
  const act = await apiCall(adminToken, 'POST', `/api/v1/admin/companies/${reg.json.data.id}/activate`);
  r.check('setup: an active company exists', reg.status === 201 && act.status === 200, `${reg.status}/${act.status}`);
  const companyId = reg.json.data.id;
  r.check('setup: a seller token gets 403 on the payment queue (real API)', (await apiCall(sellerToken, 'GET', '/api/v1/admin/credit/payments')).status === 403);

  const browser = await launch();
  const REF1 = `BEFTN${tag}A`;
  const REF2 = `CHQ${tag}9`;
  let paymentId1 = null;
  let paymentId2 = null;

  // ================= seller: no Credit links, refused pages
  {
    const p = await newPage(browser);
    await p.goto(`${WEB}/login`, { timeout: 120000, waitUntil: 'networkidle' });
    await p.getByLabel('Staff or seller email').fill(USERS.sellerAlpha.email);
    await p.getByLabel('Password').fill(USERS.sellerAlpha.password);
    await p.getByRole('button', { name: 'Sign in' }).click();
    await p.waitForURL(`${WEB}/`, { timeout: 120000 });
    await p.getByText('Operations console').first().waitFor();
    r.check('seller sees no Credit links', (await p.getByRole('link', { name: /Credit payments|Record a payment/ }).count()) === 0);
    await p.goto(`${WEB}/credit/payments`);
    await p.getByText('Your role does not allow this action (credit.view).').waitFor();
    await p.goto(`${WEB}/credit/payments/new`);
    await p.getByText('Your role does not allow this action (credit.record_payment).').waitFor();
    r.check('seller opening the queue and the record form is told no', true);
    await p.context().close();
  }

  // ================= finance A: record
  const a = await newPage(browser);
  await consoleSignIn(a, PEOPLE.a);
  const nav = a.getByRole('navigation', { name: 'Modules' });
  r.check('finance sees a Credit heading with both links', (await nav.getByText('Credit', { exact: true }).count()) === 1 && (await nav.getByRole('link', { name: /Credit payments/ }).count()) === 1 && (await nav.getByRole('link', { name: /Record a payment/ }).count()) === 1);
  await nav.getByRole('link', { name: /Record a payment/ }).click();
  await a.waitForURL(/\/credit\/payments\/new$/);
  await a.getByRole('form', { name: 'Record a customer payment' }).waitFor();
  await axeCheck(a, '/credit/payments/new');

  // client checks first: nothing is sent
  const sent = [];
  a.on('request', (req) => { if (req.method() === 'POST' && req.url().includes('/credit/payments')) sent.push(req.url()); });
  await a.getByRole('button', { name: 'Record payment' }).click();
  await a.getByText('Choose the company that paid.').waitFor();
  await a.getByText('Enter the amount in BDT.').waitFor();
  r.check('empty form: company, amount and reference errors, focus on the alert, nothing sent', (await a.getByText(/Enter the .*4 to 40 characters/).count()) === 1 && sent.length === 0 && (await a.evaluate(() => document.activeElement?.getAttribute('data-testid'))) === 'record-alert');
  await a.getByLabel('Amount (BDT)').fill('1.234');
  await a.getByRole('button', { name: 'Record payment' }).click();
  await a.getByText(/at most 2 decimals/i).first().waitFor();
  r.check('a third decimal is refused, never rounded', true);
  await a.getByLabel('Amount (BDT)').fill('25,000.50');
  await a.getByText('25,000.50 BDT (2,500,050 poisha)').waitFor();
  r.check('amount helper shows BDT and the poisha that will be sent', true);
  await a.getByTestId('method').selectOption('cheque');
  await a.getByRole('button', { name: 'Record payment' }).click();
  r.check('cheque asks for its date and bank', (await a.getByText('Give the date written on the cheque or pay order.').count()) === 1 && (await a.getByText('Enter the bank, 2 to 60 characters.').count()) === 1);
  await a.getByTestId('method').selectOption('cash');
  r.check('cash has no reference box and says so', (await a.getByLabel(/reference/i).count()) === 0 && (await a.getByText(/receipt number from the system/).count()) === 1);
  await a.getByLabel('Date received').fill(day(-45));
  await a.getByLabel(/Why is this entered late/).waitFor();
  r.check('a payment received more than 30 days ago asks for the late-entry reason', true);
  await a.reload();

  // the real record: BEFTN
  await a.getByRole('form', { name: 'Record a customer payment' }).waitFor();
  await fillRecord(a, legalName, { method: 'beftn', amount: '25,000.50', reference: REF1 });
  const keys = [];
  a.on('request', (req) => { if (req.method() === 'POST' && /\/companies\/[^/]+\/credit\/payments$/.test(req.url())) keys.push(req.headers()['idempotency-key']); });
  await a.getByRole('button', { name: 'Record payment' }).click();
  await a.getByTestId('recorded-panel').waitFor();
  r.check('recorded: panel says it waits for another person, one valid Idempotency-Key was sent', (await a.getByTestId('recorded-panel').innerText()).includes('25,000.50') && keys.length === 1 && /^[A-Za-z0-9_-]{8,100}$/.test(keys[0] ?? ''), keys.join(','));
  paymentId1 = (await a.getByRole('link', { name: 'Open this payment' }).getAttribute('href')).split('/').pop();
  await axeCheck(a, 'recorded panel');

  // duplicate reference: plain words, field kept
  await a.getByRole('button', { name: 'Record another payment' }).click();
  await fillRecord(a, legalName, { method: 'beftn', amount: '100', reference: REF1.toLowerCase() });
  await a.getByRole('button', { name: 'Record payment' }).click();
  await a.getByTestId('record-alert').waitFor();
  r.check('duplicate reference (written differently): plain words, nothing lost', (await a.getByTestId('record-alert').innerText()).includes('already recorded') && (await a.getByLabel('Amount (BDT)').inputValue()) === '100');
  await axeCheck(a, 'record form with a server error');
  const dupKeyCount = keys.length;
  r.check('the changed form got its own key', dupKeyCount === 2 && keys[0] !== keys[1]);

  // a post-dated cheque through the screen
  await a.goto(`${WEB}/credit/payments/new`);
  await a.getByRole('form', { name: 'Record a customer payment' }).waitFor();
  await fillRecord(a, legalName, { method: 'cheque', amount: '10000', reference: REF2, bank: 'Sonali Bank PLC', chequeDate: day(5) });
  await a.getByRole('button', { name: 'Record payment' }).click();
  await a.getByTestId('recorded-panel').waitFor();
  paymentId2 = (await a.getByRole('link', { name: 'Open this payment' }).getAttribute('href')).split('/').pop();
  r.check('post-dated cheque recorded', !!paymentId2);

  // ---- A: queue and detail; four eyes
  await a.goto(`${WEB}/credit/payments`);
  await a.getByTestId('payment-queue').waitFor();
  const rowsText = await a.getByTestId('queue-row').allInnerTexts();
  r.check('queue (default: waiting) lists both, oldest first, with the Post-dated flag', rowsText.length >= 2 && rowsText.findIndex((t) => t.includes(REF1)) < rowsText.findIndex((t) => t.includes(REF2)) && rowsText.some((t) => t.includes(REF2) && t.includes('Post-dated')), rowsText.map((t) => t.replace(/\s+/g, ' ').slice(0, 60)).join(' | '));
  await axeCheck(a, '/credit/payments');
  await a.getByRole('link', { name: new RegExp(REF1) }).click();
  await a.getByTestId('payment-detail').waitFor();
  const verifyBtn = a.getByRole('button', { name: 'Verify', exact: true });
  r.check('A (the recorder): Verify and Reject are disabled and the reason is on screen', (await verifyBtn.isDisabled()) && (await a.getByRole('button', { name: 'Reject' }).isDisabled()) && (await a.getByTestId('action-hint').innerText()).includes('You recorded this payment'));
  const aToken = await apiToken(PEOPLE.a);
  const refused = await post(aToken, `/api/v1/admin/credit/payments/${paymentId1}/verify`, { clearedOn: day(0) }, { 'X-Step-Up-Code': await freshCode(PEOPLE.a) });
  r.check('the real API also refuses A: 409 CREDIT_PAYMENT_FOUR_EYES', refused.status === 409 && refused.json.error?.code === 'CREDIT_PAYMENT_FOUR_EYES', `${refused.status} ${refused.json.error?.code}`);
  await axeCheck(a, 'payment detail (recorder)');

  // ================= finance B: verify, bounce, reject
  const b = await newPage(browser);
  await consoleSignIn(b, PEOPLE.b);
  await b.goto(`${WEB}/credit/payments/${paymentId2}`);
  await b.getByTestId('payment-detail').waitFor();
  r.check('B: post-dated cheque cannot be verified yet (button disabled, date in the reason), Reject is allowed', (await b.getByRole('button', { name: 'Verify', exact: true }).isDisabled()) && (await b.getByTestId('action-hint').innerText()).includes(`before ${day(5)}`) && (await b.getByRole('button', { name: 'Reject' }).isEnabled()));

  await b.goto(`${WEB}/credit/payments/${paymentId1}`);
  await b.getByTestId('payment-detail').waitFor();
  r.check('B: BEFTN payment is verifiable, no ledger line yet', (await b.getByRole('button', { name: 'Verify', exact: true }).isEnabled()) && (await b.getByTestId('no-lines').count()) === 1);
  await b.getByRole('button', { name: 'Verify', exact: true }).click();
  const vdlg = b.getByRole('dialog');
  await vdlg.getByLabel('Day the money cleared').fill('2020-01-01');
  await vdlg.getByRole('button', { name: 'Continue to the authenticator code' }).click();
  await vdlg.getByText(/The money cleared between/).waitFor();
  r.check('a clearing day before the received day is stopped BEFORE a code is asked for', (await b.getByLabel('Authenticator code').count()) === 0);
  await vdlg.getByLabel('Day the money cleared').fill(day(0));
  await axeCheck(b, 'verify dialog');
  await vdlg.getByRole('button', { name: 'Continue to the authenticator code' }).click();
  const code = b.getByRole('dialog').getByLabel('Authenticator code');
  await code.fill('000000');
  await b.getByRole('dialog').getByRole('button', { name: 'Verify', exact: true }).click();
  await b.getByTestId('stepup-error').waitFor();
  r.check('wrong code: the code dialog stays open with plain words', (await b.getByTestId('stepup-error').innerText()).includes('not accepted'));
  await axeCheck(b, 'step-up dialog with an error');
  await b.getByRole('dialog').getByLabel('Authenticator code').fill(await freshCode(PEOPLE.b));
  await b.getByRole('dialog').getByRole('button', { name: 'Verify', exact: true }).click();
  await b.getByTestId('decision-notice').waitFor();
  await b.getByTestId('ledger-lines').waitFor();
  r.check('verified with a real code: status, who cleared, notice', (await b.getByTestId('status-badge').innerText()) === 'Verified' && (await b.getByTestId('decision-notice').innerText()).includes('25,000.50') && (await b.getByTestId('row-verified').innerText()).includes('(by you)'));
  r.check('ledger lines show the money kept on account (+25,000.50)', (await b.getByTestId('ledger-lines').innerText()).includes('+25,000.50') && (await b.getByTestId('ledger-lines').innerText()).includes('Kept on account'));
  const viaApi = await apiCall(adminToken, 'GET', `/api/v1/admin/credit/payments/${paymentId1}`);
  r.check('the API agrees: Verified by B, not by A', viaApi.json.data?.payment.status === 'verified' && viaApi.json.data.payment.verifiedBy !== viaApi.json.data.payment.recordedBy);
  await axeCheck(b, 'verified payment detail');

  // bounce: reason first, then a code
  await b.getByRole('button', { name: 'Bounce' }).click();
  const bd = b.getByRole('dialog');
  await bd.getByRole('button', { name: 'Continue to the authenticator code' }).click();
  await bd.getByText('Enter a reason.').waitFor();
  r.check('bounce: an empty reason is stopped BEFORE a code is asked for', (await b.getByLabel('Authenticator code').count()) === 0);
  await bd.getByLabel(/Why did it bounce/).fill('Returned by the bank, insufficient funds');
  await bd.getByRole('button', { name: 'Continue to the authenticator code' }).click();
  await b.getByRole('dialog').getByLabel('Authenticator code').fill(await freshCode(PEOPLE.b));
  await b.getByRole('dialog').getByRole('button', { name: 'Bounce', exact: true }).click();
  await b.getByTestId('decision-notice').filter({ hasText: 'Bounced' }).waitFor();
  await b.getByTestId('status-badge').filter({ hasText: 'Bounced' }).waitFor();
  await b.getByTestId('row-bounced').waitFor();
  r.check('bounced with a real code: status Bounced, reason shown, reversal lines', (await b.getByTestId('status-badge').innerText()) === 'Bounced' && (await b.getByTestId('row-bounced').innerText()).includes('insufficient funds') && (await b.getByTestId('ledger-lines').innerText()).includes('Reversed (bounce)'));
  r.check('a bounced payment offers no further action', (await b.getByRole('button', { name: /Verify|Reject|Bounce/ }).count()) === 0);

  // reject the post-dated cheque (no code) and record its reference again
  await b.goto(`${WEB}/credit/payments/${paymentId2}`);
  await b.getByTestId('payment-detail').waitFor();
  await b.getByRole('button', { name: 'Reject' }).click();
  const rd = b.getByRole('dialog');
  await rd.getByRole('button', { name: 'Reject payment' }).click();
  await rd.getByText('Enter a reason.').waitFor();
  await rd.getByLabel(/Reason/).fill('Wrong cheque number typed (E2E)');
  await axeCheck(b, 'reject dialog');
  await rd.getByRole('button', { name: 'Reject payment' }).click();
  await b.getByTestId('decision-notice').filter({ hasText: 'Rejected' }).waitFor();
  await b.getByTestId('status-badge').filter({ hasText: 'Rejected' }).waitFor();
  r.check('rejected without a code: status and reason, reference free again', (await b.getByTestId('status-badge').innerText()) === 'Rejected' && (await b.getByTestId('row-rejected').innerText()).includes('Wrong cheque number'));
  const again = await post(aToken, `/api/v1/admin/companies/${companyId}/credit/payments`, { method: 'cheque', amountMinor: 1000000, receivedOn: day(0), reference: REF2, drawerBank: 'Sonali Bank PLC', chequeDate: day(0) }, { 'Idempotency-Key': `e2e-${tag}-again-1` });
  r.check('the rejected reference can be recorded again (API 201)', again.status === 201, `${again.status} ${JSON.stringify(again.json).slice(0, 120)}`);

  // queue filter
  await b.goto(`${WEB}/credit/payments`);
  await b.getByTestId('payment-queue').waitFor();
  r.check('B queue now holds only the re-recorded cheque (waiting)', (await b.getByTestId('queue-row').filter({ hasText: legalName }).count()) === 1);
  await b.getByTestId('status-filter').selectOption('bounced');
  await b.getByTestId('queue-row').filter({ hasText: REF1 }).waitFor();
  await b.getByTestId('status-filter').selectOption('rejected');
  await b.getByTestId('queue-row').filter({ hasText: REF2 }).waitFor();
  r.check('filter: Bounced shows the BEFTN payment, Rejected the old cheque', true);

  // ---- 360 px
  const m = await newPage(browser, 360);
  await consoleSignIn(m, PEOPLE.b);
  for (const path of ['/credit/payments', `/credit/payments/${paymentId1}`]) {
    await m.goto(`${WEB}${path}`);
    await m.getByRole('heading', { level: 1 }).waitFor();
    await sleep(800);
    r.check(`360 px: no page overflow on ${path.slice(0, 22)}`, !(await overflow(m)));
  }
  await a.screenshot({ path: `${SHOTS}/b3-11-detail-recorder-1280.png`, fullPage: true });
  await b.screenshot({ path: `${SHOTS}/b3-11-detail-bounced-1280.png`, fullPage: true });

  r.check('no page errors in any window', [a, b, m].every((pg) => pg.errors.length === 0), [a, b, m].flatMap((pg) => pg.errors).join(' | '));
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
