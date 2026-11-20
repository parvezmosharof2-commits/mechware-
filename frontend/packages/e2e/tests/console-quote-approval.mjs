// D4.17: send, approval queue, approval decision (acknowledge exactly the flagged lines, authenticator code), reject, recall, withdraw in the console.
// SCRATCH DB only. Same setup as console-quote-editor.mjs: API on a scratch DB mw_* (migrated, catalogue seeded, demo accounts), the console built against
// it, a CORS proxy in front of the API, staff cloned from the admin row with the PLAIN TOTP secret and `Mechware.Worker migrate` run with the same
// Encryption__FieldKey as the API (support.e2e@, finance.a.e2e@, admin2.e2e@), and E2E_SQL_CMD = a command that reads SQL on stdin against the SCRATCH
// database, e.g. `docker exec -i mechware_postgres psql -U mechware -d mw_d417 -At -q -v ON_ERROR_STOP=1`.
// Set E2E_CONSOLE_QUOTE_APPROVAL=1 to run; otherwise it exits 0 without doing anything. Docs: docs/modules/D4.md HANDOFF D4.17.
import { execSync } from 'node:child_process';
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, CONSOLE as WEB, USERS, apiCall, apiLogin, launch, reporter, totp, uniqueBin } from './lib.mjs';

if (process.env.E2E_CONSOLE_QUOTE_APPROVAL !== '1') {
  console.log('SKIP  console quote approval E2E (set E2E_CONSOLE_QUOTE_APPROVAL=1 to run it)');
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
const send = (token, method, path, body, headers = {}) =>
  fetch(`${API}${path}`, { method, headers: { 'content-type': 'application/json', authorization: `Bearer ${token}`, ...headers }, body: body ? JSON.stringify(body) : undefined })
    .then(async (res) => ({ status: res.status, json: await res.json().catch(() => ({})) }));
const toTaka = (minor) => `${Math.floor(minor / 100)}.${String(minor % 100).padStart(2, '0')}`;
const waitPreview = (p) => p.getByTestId('preview-state').filter({ hasText: 'Up to date' }).waitFor({ timeout: 20000 });
const activeTestId = (p) => p.evaluate(() => document.activeElement?.getAttribute('data-testid'));
const text = (loc) => loc.innerText();

(async () => {
  // ---------------- setup through the real API
  const adminToken = await apiToken(PEOPLE.admin);
  const buyer = await apiLogin(USERS.buyer);
  const reg = await apiCall(buyer, 'POST', '/api/v1/companies', { legalName: `Quote Approval Works ${tag} Ltd`, tradeLicenceNo: 'TL-D417', bin: uniqueBin() });
  const companyId = reg.json.data.id;
  const act = await apiCall(adminToken, 'POST', `/api/v1/admin/companies/${companyId}/activate`);
  const buyerId = (await apiCall(buyer, 'GET', '/api/v1/users/me')).json.data.id;
  const own = (await apiCall(buyer, 'GET', `/api/v1/companies/${companyId}/members`)).json.data.find((x) => x.userId === buyerId);
  await apiCall(buyer, 'PUT', `/api/v1/companies/${companyId}/members/${own.id}`, { roles: 10, spendLimitMinor: null, costCentreIds: [], isActive: true });
  r.check('setup: an active company', reg.status === 201 && act.status === 200, `${reg.status}/${act.status}`);

  const rows = sql(`select distinct v.id, v.sku from products p join product_variants v on v.product_id = p.id join inventory_items i on i.variant_id = v.id
    where p.status = 'active' and v.status = 'active' order by v.id limit 2;`).split('\n').map((x) => x.trim()).filter(Boolean).map((x) => x.split('|'));
  const [[vA, skuA], [vB, skuB]] = rows;
  sql('UPDATE product_variants SET min_order_qty = 1, pack_size = 1 WHERE min_order_qty <> 1 OR pack_size <> 1;');
  const sup = await apiToken(PEOPLE.support);
  const fin = await apiToken(PEOPLE.finance);
  const base = '/api/v1/admin/quotes';
  const probeQuote = await send(sup, 'POST', base, { companyId, contactUserId: buyerId });
  const probePath = `${base}/${probeQuote.json.data.quote.id}`;
  const probe = await send(adminToken, 'POST', `${probePath}/versions/1/price-preview`, { validityDays: 14, lines: [vA, vB].map((v) => ({ variantId: v, quantity: 1, unitPriceMinor: 99999999 })) });
  if (probe.status !== 200) throw new Error(`probe preview failed: ${probe.status} ${JSON.stringify(probe.json).slice(0, 300)}`);
  const ref = probe.json.data.lines.map((l) => l.referenceUnitPriceMinor);
  sql(`UPDATE product_variants SET cost_price_minor = ${Math.floor(ref[0] * 0.7)} WHERE id = '${vA}'; UPDATE product_variants SET cost_price_minor = ${Math.floor(ref[1] * 0.7)} WHERE id = '${vB}';`);
  await send(sup, 'POST', `${probePath}/close`, { reason: 'Probe quote, not used' });
  const pct = (n, p) => Math.floor((n * p) / 100);
  const priceClean = ref[0];
  const priceOver = pct(ref[0], 90);   // beyond the preparer's authority, above the floor
  const priceBelow = pct(ref[1], 75);  // below the floor (needs a reason) and beyond authority

  /** A draft quote for the company with the given lines, made by the token's owner. */
  async function makeQuote(token, lines) {
    const made = await send(token, 'POST', base, { companyId, contactUserId: buyerId });
    if (made.status > 201) throw new Error(`create quote failed ${made.status} ${JSON.stringify(made.json).slice(0, 300)}`);
    const id = made.json.data.quote.id;
    const put = await send(token, 'PUT', `${base}/${id}/versions/1`, { rowVersion: made.json.data.versions[0].rowVersion, validityDays: 14, lines });
    if (put.status !== 200) throw new Error(`save draft failed ${put.status} ${JSON.stringify(put.json).slice(0, 300)}`);
    return { id, number: made.json.data.quote.number, path: `${base}/${id}` };
  }
  const lineClean = { variantId: vA, quantity: 1, unitPriceMinor: priceClean };
  const lineOver = { variantId: vA, quantity: 1, unitPriceMinor: priceOver };
  const lineBelow = { variantId: vB, quantity: 1, unitPriceMinor: priceBelow, belowFloorReason: 'Matching a competitor offer' };
  const version = async (token, q, n = 1) => (await send(token, 'GET', q.path)).json.data.versions.find((v) => v.versionNo === n);
  const status = async (q, n = 1) => (await version(sup, q, n)).status;

  const q1 = await makeQuote(sup, [lineClean]);           // clean: Support sends it from the editor
  const q2 = await makeQuote(sup, [lineOver]);            // beyond authority: Support sends from the detail page, Finance approves
  const q3 = await makeQuote(adminToken, [lineBelow]);    // below floor, Admin is the author: cannot approve
  const q4 = await makeQuote(sup, [lineBelow]);           // below floor: Finance cannot, Admin approves
  const q5 = await makeQuote(sup, [lineOver]);            // Finance rejects with a comment
  const q6 = await makeQuote(sup, [lineOver]);            // Support recalls
  const q7 = await makeQuote(sup, [lineOver]);            // mocked refusals (flags changed, content changed)
  const preview = (await send(adminToken, 'POST', `${q4.path}/versions/1/price-preview`, { validityDays: 14, lines: [lineBelow] })).json.data;
  r.check('setup: seven draft quotes; the real check flags the below-floor line (and the deep discount) so the screens have something to show',
    preview.belowFloorLineNos.includes(1) && preview.overAuthorityLineNos.includes(1), JSON.stringify([preview.belowFloorLineNos, preview.overAuthorityLineNos]));

  const browser = await launch();

  // ================= Support: Send from the editor (clean quote)
  const s = await newPage(browser);
  await consoleSignIn(s, PEOPLE.support);
  await s.goto(`${WEB}/quotes/${q1.id}/edit`);
  await s.getByTestId('editor-line').first().waitFor();
  await waitPreview(s);
  r.check('editor: a clean saved draft: the button says "Send to the buyer" and is on, with the reason in words',
    (await text(s.getByTestId('send-quote'))) === 'Send to the buyer' && (await s.getByTestId('send-quote').isEnabled()) && (await text(s.getByTestId('send-note'))).includes('Nothing needs a second person'), await text(s.getByTestId('send-note')));
  await s.getByLabel('Note to the buyer').fill('Typed but not saved yet');
  r.check('editor: with unsaved changes Send is off and says to save first (the send uses the saved version)',
    !(await s.getByTestId('send-quote').isEnabled()) && (await text(s.getByTestId('send-note'))).includes('Save the draft first'));
  await s.getByTestId('save-draft').click();
  await s.getByTestId('editor-notice').waitFor();
  await waitPreview(s);
  await s.getByTestId('send-quote').click();
  await s.getByRole('dialog').waitFor();
  r.check('editor: Send asks once ("Send version 1 to the buyer?") and nothing is sent yet',
    (await text(s.getByRole('dialog'))).includes('Send version 1 to the buyer?') && (await status(q1)) === 'draft');
  await axeCheck(s, 'send confirmation dialog');
  await s.getByTestId('send-confirm').click();
  await s.getByTestId('send-result').waitFor();
  const sentV = await version(sup, q1);
  r.check('editor: the result says Sent with the validity dates, and the API holds a Sent version valid 14 days',
    (await text(s.getByTestId('send-result-title'))) === 'Sent' && (await s.getByTestId('send-result').getAttribute('data-send-status')) === 'sent' && (await text(s.getByTestId('send-validity'))).includes('14 days')
    && sentV.status === 'sent' && !!sentV.validUntil && sentV.validityDays === 14, JSON.stringify([sentV.status, sentV.validUntil]));
  r.check('editor: the result takes the keyboard focus', (await activeTestId(s)) === 'send-result');
  await axeCheck(s, 'send result (sent)');

  // ================= Support: Send from the quote page (flagged quote) -> waiting for approval, flags in words
  await s.goto(`${WEB}/quotes/${q2.id}`);
  await s.getByTestId('send-draft').waitFor({ timeout: 30000 });
  await s.getByTestId('send-draft').click();
  await s.getByTestId('send-confirm').click();
  await s.getByTestId('send-result').waitFor();
  r.check('detail: a flagged quote goes to "Waiting for approval", the flags are in words, the buyer cannot see it yet',
    (await text(s.getByTestId('send-result-title'))) === 'Waiting for approval' && (await text(s.getByTestId('send-flags'))).includes('price authority on line 1') && (await status(q2)) === 'pending_approval');
  r.check('detail: Recall is offered, Support has no approval link (no quotes.approve)', (await s.getByTestId('recall-version').count()) === 1 && (await s.getByTestId('review-approval').count()) === 0);
  await axeCheck(s, 'send result (waiting for approval)');

  // the other quotes reach the queue through the API, in this order
  const sendApi = async (token, q) => (await send(token, 'POST', `${q.path}/versions/1/send`));
  const s3 = await sendApi(adminToken, q3);
  const s4 = await sendApi(sup, q4);
  const s5 = await sendApi(sup, q5);
  const s6 = await sendApi(sup, q6);
  const s7 = await sendApi(sup, q7);
  r.check('setup: the five other flagged quotes are waiting for approval', [s3, s4, s5, s6, s7].every((x) => x.status === 200 && x.json.data.status === 'pending_approval'), [s3, s4, s5, s6, s7].map((x) => x.status + ':' + x.json.data?.status).join(' '));
  const flagsOf = (x) => x.json.data.flags;

  // ================= Finance: queue
  const f = await newPage(browser);
  await consoleSignIn(f, PEOPLE.finance);
  await f.goto(`${WEB}/quotes`);
  await f.getByRole('link', { name: 'Quote approvals' }).waitFor({ timeout: 30000 });
  r.check('sidebar: Finance has "Quote approvals"', (await f.getByRole('link', { name: 'Quote approvals' }).count()) === 1);
  await s.getByRole('link', { name: 'RFQ inbox' }).waitFor({ timeout: 30000 });
  r.check('sidebar: Support has no "Quote approvals"', (await s.getByRole('link', { name: 'Quote approvals' }).count()) === 0);
  await f.getByRole('link', { name: 'Quote approvals' }).click();
  await f.getByTestId('quote-approval-queue').waitFor({ timeout: 30000 });
  const order = (await f.getByTestId('approval-row').evaluateAll((els) => els.map((e) => e.getAttribute('data-quote-number'))));
  const mine = order.filter((n) => [q2, q3, q4, q5, q6, q7].some((q) => q.number === n));
  r.check('queue: our six versions are listed oldest first (the order they were sent)', JSON.stringify(mine) === JSON.stringify([q2, q3, q4, q5, q6, q7].map((q) => q.number)), JSON.stringify(mine));
  const rowOf = (q) => f.getByTestId('approval-row').filter({ hasText: q.number });
  r.check('queue: flags in words, who sent it, the wait, and "You can decide" for the over-authority quote',
    (await text(rowOf(q2).getByTestId('approval-flags'))).includes("beyond the preparer's price authority") && (await text(rowOf(q2))).includes('Support One') && (await text(rowOf(q2).getByTestId('approval-wait'))).length > 0
    && (await rowOf(q2).getAttribute('data-can-approve')) === 'yes');
  r.check('queue: the below-floor quotes say "Not you" for Finance (no below-floor permission), with the reason; the line count is there',
    (await rowOf(q4).getAttribute('data-can-approve')) === 'no' && (await text(rowOf(q4).getByTestId('approval-can'))).includes('below-floor permission') && (await text(rowOf(q4).getByTestId('approval-flags'))).includes('below the margin floor'));
  await axeCheck(f, 'approval queue');

  // ================= Finance approves Q2: ticks, comment, wrong code, right code
  await rowOf(q2).getByTestId('approval-open').click();
  await f.getByTestId('decision-panel').waitFor({ timeout: 30000 });
  const flagged = await f.getByTestId('version-line').evaluateAll((els) => els.map((e) => e.getAttribute('data-flagged')));
  r.check('decision: the version, its totals and flagged line are shown; the flagged line is marked in the table and in words',
    flagged.includes('true') && (await f.getByTestId('version-totals').count()) === 1 && (await text(f.getByTestId('version-lines'))).includes("Beyond the preparer's price authority"));
  r.check('decision: the over-authority line has its own group of ticks (fieldset with a legend); no below-floor group',
    (await f.getByTestId('ack-over').count()) === 1 && (await f.getByTestId('ack-below').count()) === 0 && (await text(f.getByTestId('ack-over').locator('legend'))).includes('price authority'));
  r.check('decision: Approve is on for Finance (not the author, not below floor)', await f.getByTestId('approve-quote').isEnabled());
  await axeCheck(f, 'decision screen (fieldset of ticks)');
  await f.getByTestId('approve-quote').click();
  await f.getByTestId('ack-error').waitFor();
  r.check('decision: nothing ticked -> Approve says so under the ticks, focuses it, and asks for no code',
    (await text(f.getByTestId('ack-error'))).includes('Tick') && (await f.getByLabel('Authenticator code').count()) === 0 && (await activeTestId(f)) === 'ack-error');
  await f.getByTestId('ack-over-line').first().check();
  r.check('decision: the comment box stops at 500 characters', (await f.getByTestId('decision-comment').getAttribute('maxlength')) === '500');
  await f.getByTestId('decision-comment').fill('Seen the contract, fine');
  await f.getByTestId('approve-quote').click();
  await f.getByLabel('Authenticator code').waitFor();
  await axeCheck(f, 'step-up dialog');
  await f.getByLabel('Authenticator code').fill('12345');
  await f.getByRole('button', { name: 'Approve', exact: true }).click();
  r.check('decision: a code of the wrong shape is refused on the spot (no request)', (await f.getByText('Enter the 6-digit code').count()) >= 1 && (await status(q2)) === 'pending_approval');
  await f.getByLabel('Authenticator code').fill('000000');
  await f.getByRole('button', { name: 'Approve', exact: true }).click();
  await f.getByTestId('stepup-error').waitFor();
  r.check('decision: a wrong code keeps the dialog open with the reason in words; nothing was approved',
    (await text(f.getByTestId('stepup-error'))).includes('not accepted') && (await status(q2)) === 'pending_approval');
  await f.getByLabel('Authenticator code').fill(await freshCode(PEOPLE.finance));
  await f.getByRole('button', { name: 'Approve', exact: true }).click();
  await f.getByTestId('send-result').waitFor({ timeout: 30000 });
  const apv = await version(sup, q2);
  r.check('decision: the right code approves and sends it: the result says Sent with the dates; the API holds Sent, the comment and a validity from the approval',
    (await text(f.getByTestId('send-result-title'))) === 'Sent' && apv.status === 'sent' && apv.approvalComment === 'Seen the contract, fine' && !!apv.validUntil && !!apv.approvedBy, JSON.stringify([apv.status, apv.approvalComment]));
  r.check('decision: once decided the page no longer offers Approve', (await f.getByTestId('approve-quote').count()) === 0);

  // ================= Finance on a below-floor quote: disabled in words; the API agrees (403)
  await f.goto(`${WEB}/quotes/approvals/${q4.id}/1`);
  await f.getByTestId('decision-panel').waitFor({ timeout: 30000 });
  r.check('decision (below floor, Finance): Approve is OFF and the reason names the below-floor permission; Send back is still on',
    !(await f.getByTestId('approve-quote').isEnabled()) && (await text(f.getByTestId('approve-hint'))).includes('quotes.approve_below_floor') && (await f.getByTestId('reject-quote').isEnabled()));
  r.check('decision (below floor): both groups of ticks are listed, with the line, its price and the reason given',
    (await f.getByTestId('ack-below-line').count()) === 1 && (await f.getByTestId('ack-over-line').count()) === 1 && (await text(f.getByTestId('ack-below'))).includes('Matching a competitor offer'));
  const v4 = await version(sup, q4);
  const body4 = { contentHash: v4.contentHash, belowFloorLineIds: flagsOf(s4).belowFloorLineIds, overAuthorityLineIds: flagsOf(s4).overAuthorityLineIds };
  const refused = await send(fin, 'POST', `${q4.path}/versions/1/approve`, body4, { 'X-Step-Up-Code': await freshCode(PEOPLE.finance) });
  r.check('the real API refuses Finance on a below-floor version: 403 BELOW_FLOOR_PERMISSION_REQUIRED', refused.status === 403 && refused.json.code === 'BELOW_FLOOR_PERMISSION_REQUIRED' || refused.json.error?.code === 'BELOW_FLOOR_PERMISSION_REQUIRED' || JSON.stringify(refused.json).includes('BELOW_FLOOR_PERMISSION_REQUIRED'), `${refused.status} ${JSON.stringify(refused.json).slice(0, 160)}`);

  // ================= Admin (the author of Q3): disabled, and a real 409
  const a = await newPage(browser);
  await consoleSignIn(a, PEOPLE.admin);
  await a.goto(`${WEB}/quotes/approvals/${q3.id}/1`);
  await a.getByTestId('decision-panel').waitFor({ timeout: 30000 });
  r.check('decision (the author): Approve is OFF with "you created, changed or sent this version"; Send back is allowed',
    !(await a.getByTestId('approve-quote').isEnabled()) && (await text(a.getByTestId('approve-hint'))).includes('created, changed or sent') && (await a.getByTestId('reject-quote').isEnabled()));
  const v3 = await version(adminToken, q3);
  const four = await send(adminToken, 'POST', `${q3.path}/versions/1/approve`, { contentHash: v3.contentHash, belowFloorLineIds: flagsOf(s3).belowFloorLineIds, overAuthorityLineIds: flagsOf(s3).overAuthorityLineIds },
    { 'X-Step-Up-Code': await freshCode(PEOPLE.admin) });
  r.check('the real API refuses the author: 409 QUOTE_FOUR_EYES (the code is spent)', four.status === 409 && JSON.stringify(four.json).includes('QUOTE_FOUR_EYES'), `${four.status} ${JSON.stringify(four.json).slice(0, 160)}`);
  r.check('the author\'s own queue row says "Not you"', await (async () => { await a.goto(`${WEB}/quotes/approvals`); await a.getByTestId('quote-approval-queue').waitFor(); return (await a.getByTestId('approval-row').filter({ hasText: q3.number }).getAttribute('data-can-approve')) === 'no'
    && (await a.getByTestId('approval-row').filter({ hasText: q4.number }).getAttribute('data-can-approve')) === 'yes'; })());

  // ================= Admin approves Q4 (below floor): exact ticks, wrong code, right code
  await a.goto(`${WEB}/quotes/approvals/${q4.id}/1`);
  await a.getByTestId('decision-panel').waitFor({ timeout: 30000 });
  r.check('decision (Admin, below floor): Approve is on', await a.getByTestId('approve-quote').isEnabled());
  await a.getByTestId('ack-below-line').first().check();
  await a.getByTestId('approve-quote').click();
  await a.getByTestId('ack-error').waitFor();
  r.check('decision: only the below-floor line ticked -> "1 of 2 still to tick", no code asked', (await text(a.getByTestId('ack-error'))).includes('1 of 2 still to tick') && (await a.getByLabel('Authenticator code').count()) === 0, await text(a.getByTestId('ack-error')));
  await a.getByTestId('ack-over-line').first().check();
  r.check('decision: ticking again clears the message', (await a.getByTestId('ack-error').count()) === 0);
  await a.getByTestId('approve-quote').click();
  await a.getByLabel('Authenticator code').waitFor();
  await a.getByLabel('Authenticator code').fill('000000');
  await a.getByRole('button', { name: 'Approve', exact: true }).click();
  await a.getByTestId('stepup-error').waitFor();
  r.check('decision: wrong code first: refused in words, still pending', (await status(q4)) === 'pending_approval');
  await a.getByLabel('Authenticator code').fill(await freshCode(PEOPLE.admin));
  await a.getByRole('button', { name: 'Approve', exact: true }).click();
  await a.getByTestId('send-result').waitFor({ timeout: 30000 });
  r.check('decision: the right code lets the Admin approve the below-floor version', (await status(q4)) === 'sent');

  // ================= Finance rejects Q5 with a comment
  await f.goto(`${WEB}/quotes/approvals/${q5.id}/1`);
  await f.getByTestId('reject-quote').waitFor({ timeout: 30000 });
  await f.getByTestId('reject-quote').click();
  await f.getByTestId('reason-confirm').click();
  r.check('reject: an empty comment is refused in the dialog (nothing sent)', (await f.getByText('Enter a reason').count()) >= 1 && (await status(q5)) === 'pending_approval');
  await f.getByTestId('reason-input').fill('abc');
  await f.getByTestId('reason-confirm').click();
  r.check('reject: a comment under 5 characters is refused', (await f.getByText('5 to 500').count()) >= 1 && (await status(q5)) === 'pending_approval');
  await f.getByTestId('reason-input').fill('Price too low for this buyer, please revise');
  await f.getByTestId('reason-confirm').click();
  await f.getByTestId('decision-rejected').waitFor({ timeout: 30000 });
  const v5 = await version(sup, q5);
  r.check('reject: the version is a draft again, the page says it went back (no code asked), and the focus is on the message',
    v5.status === 'draft' && (await text(f.getByTestId('decision-rejected'))).includes('sent back to its author') && (await activeTestId(f)) === 'decision-rejected', v5.status);

  // ================= Support recalls Q6
  await s.goto(`${WEB}/quotes/${q6.id}`);
  await s.getByTestId('recall-version').waitFor({ timeout: 30000 });
  await s.getByTestId('recall-version').click();
  await s.getByTestId('quote-notice').waitFor();
  r.check('recall: the version is a draft again and the page says so', (await status(q6)) === 'draft' && (await text(s.getByTestId('quote-notice'))).includes('taken back from approval'));
  r.check('recall: the quote page now offers "Send version 1" again', await s.getByTestId('send-draft').isEnabled());

  // ================= Support withdraws Q1 (Sent)
  await s.goto(`${WEB}/quotes/${q1.id}`);
  await s.getByTestId('withdraw-version').waitFor({ timeout: 30000 });
  await s.getByTestId('withdraw-version').click();
  await s.getByTestId('reason-input').fill('abc');
  await s.getByTestId('reason-confirm').click();
  r.check('withdraw: a reason under 5 characters is refused, still Sent', (await s.getByText('5 to 500').count()) >= 1 && (await status(q1)) === 'sent');
  await s.getByTestId('reason-input').fill('Customer asked for a new offer');
  await s.getByTestId('reason-confirm').click();
  await s.getByTestId('quote-notice').waitFor({ timeout: 30000 });
  const w = await version(sup, q1);
  r.check('withdraw: the version is Withdrawn with the reason, the page says the buyer can no longer accept it', w.status === 'withdrawn' && w.withdrawReason === 'Customer asked for a new offer' && (await text(s.getByTestId('quote-notice'))).includes('can no longer accept'), w.status);
  r.check('withdraw: no Withdraw button remains', (await s.getByTestId('withdraw-version').count()) === 0);

  // ================= mocked refusals that need another world state (flags changed, content changed, two more codes)
  const answer = async (page, code, message, httpStatus = 409) => {
    await page.route('**/versions/1/approve', (route) => route.fulfill({ status: httpStatus, contentType: 'application/problem+json', headers: { 'access-control-allow-origin': WEB, 'access-control-allow-credentials': 'true' },
      body: JSON.stringify({ type: `https://api.mechware.com.bd/errors/${code}`, title: message, status: httpStatus, code, error: { code, message } }) }), { times: 1 });
  };
  await f.goto(`${WEB}/quotes/approvals/${q7.id}/1`);
  await f.getByTestId('decision-panel').waitFor({ timeout: 30000 });
  await f.getByTestId('ack-over-line').first().check();
  await answer(f, 'QUOTE_FLAGS_NOT_ACKNOWLEDGED', 'Acknowledge exactly the lines flagged now. Below the floor: []; over the discount authority: [].');
  await f.getByTestId('approve-quote').click();
  await f.getByLabel('Authenticator code').fill('123456');
  await f.getByRole('button', { name: 'Approve', exact: true }).click();
  await f.getByTestId('decision-alert').waitFor({ timeout: 30000 });
  const alertText = await text(f.getByTestId('decision-alert'));
  r.check('flags changed (mocked 409): the dialog closes, the alert explains in words and says the code is used up, the focus is on it',
    alertText.includes('tick the lines flagged now') && alertText.includes('used up') && (await f.getByLabel('Authenticator code').count()) === 0 && (await activeTestId(f)) === 'decision-alert', alertText);
  await f.getByTestId('flags-changed').waitFor();
  r.check('flags changed: the page reloads, says what the flags were and are now, and the ticks start again (no group now: nothing flagged)',
    (await text(f.getByTestId('flags-changed'))).includes('Now they are') && (await f.getByTestId('nothing-flagged').count()) === 1 && (await f.getByTestId('ack-over-line').count()) === 0);
  await answer(f, 'QUOTE_CHANGED', 'The quote is not the one you reviewed. Reload it and review again.');
  await f.getByTestId('approve-quote').click();
  await f.getByLabel('Authenticator code').fill('123456');
  await f.getByRole('button', { name: 'Approve', exact: true }).click();
  await f.getByTestId('decision-alert').filter({ hasText: 'changed after you opened it' }).waitFor({ timeout: 30000 });
  r.check('content changed (mocked 409 QUOTE_CHANGED): plain words, reloaded, the server flags forgotten', (await f.getByTestId('flags-changed').count()) === 0 && (await f.getByTestId('decision-panel').count()) === 1);
  await send(sup, 'POST', `${q7.path}/versions/1/recall`);
  await f.goto(`${WEB}/quotes/approvals/${q7.id}/1`);
  await f.getByTestId('not-pending').waitFor({ timeout: 30000 });
  r.check('a version that left approval (recalled): the screen says there is nothing to decide and links to the quote, no Approve', (await f.getByTestId('approve-quote').count()) === 0);

  // ================= permissions
  await s.goto(`${WEB}/quotes/approvals`);
  await s.getByText('Your role does not allow this action (quotes.approve).').waitFor();
  r.check('Support opening the queue URL is told no; the API refuses too (403)', (await send(sup, 'GET', `${base}/approvals?page=1&pageSize=5`)).status === 403);
  await s.goto(`${WEB}/quotes/approvals/${q4.id}/1`);
  await s.getByText('Your role does not allow this action (quotes.approve).').waitFor();
  r.check('Support opening a decision URL is told no (no form)', (await s.getByTestId('decision-panel').count()) === 0);
  await f.goto(`${WEB}/quotes/${q2.id}`);
  await f.getByTestId('quote-number').waitFor();
  r.check('Finance (quotes.approve, no quotes.edit) sees no Send / Recall / Withdraw on the quote page', (await f.getByTestId('send-draft').count()) === 0 && (await f.getByTestId('withdraw-version').count()) === 0 && (await f.getByTestId('recall-version').count()) === 0);

  // ================= narrow screen and page errors
  await a.setViewportSize({ width: 360, height: 800 });
  await a.goto(`${WEB}/quotes/approvals`);
  await a.getByTestId('quote-approval-queue').waitFor();
  r.check('360 px: the queue has no horizontal page scroll', !(await overflow(a)));
  await a.goto(`${WEB}/quotes/approvals/${q3.id}/1`);
  await a.getByTestId('decision-panel').waitFor({ timeout: 30000 });
  r.check('360 px: the decision screen has no horizontal page scroll', !(await overflow(a)));
  r.check('no page errors', s.errors.length === 0 && a.errors.length === 0 && f.errors.length === 0, [...s.errors, ...a.errors, ...f.errors].join(' | '));
  // cleanup so a re-run starts clean: close the quotes that are still open
  for (const q of [q2, q3, q4, q5, q6, q7]) await send(sup, 'POST', `${q.path}/close`, { reason: 'E2E cleanup' });
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
