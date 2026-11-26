// E6.5d console credit notes: "Request credit note" on the invoice detail (only with invoices.credit_request), the queue /invoices/credit-notes, the decision page
// (approve with a code, reject with a reason and a code, withdraw, four eyes in words), the 6.7 number linked to its invoice, cap and "changed" refusals in words,
// Support can request but the API refuses its approval (403), axe 0, 360 px.
// Real: company A with credit, shipped orders through the API (Invoicing:Mode=Specimen + IssuerSellerId); a request made on screen by Support is approved on screen by
// a second Finance user with a real authenticator code (a wrong code first); a rejection by Finance B; everything is checked against the API as oracle.
// SCRATCH DB only (E2E_SQL_CMD against mw_*). Set E2E_CONSOLE_CREDIT_NOTES=1 to run. E2E_CONSOLE_URL = the console. E2E_VARIANT_A = a variant with stock.
import { CONSOLE, API, USERS, launch } from './lib.mjs';
import { PEOPLE as BASE, VARIANT_A, apiToken, axeCheck, freshCode, key, noOverflow, r, requireScratch, send, setupCompany, tag } from './returns-lib.mjs';

if (process.env.E2E_CONSOLE_CREDIT_NOTES !== '1') {
  console.log('SKIP  console credit notes E2E (set E2E_CONSOLE_CREDIT_NOTES=1 to run it)');
  process.exit(0);
}
requireScratch();
const PEOPLE = { admin: BASE.admin, support: BASE.support, financeA: BASE.finance, financeB: { ...BASE.finance, email: 'finance.b.e2e@mechware.com.bd' } };
const A = '/api/v1/admin';

async function consoleSignIn(page, who) {
  await page.goto(`${CONSOLE}/login`, { timeout: 120000, waitUntil: 'networkidle' });
  await page.getByLabel('Staff or seller email').fill(who.email);
  await page.getByLabel('Password').fill(who.password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.getByLabel('6-digit authenticator code or single-use recovery code').fill(await freshCode(who));
  await page.getByRole('button', { name: 'Verify and unlock console' }).click();
  await page.waitForURL(`${CONSOLE}/`, { timeout: 120000 });
}

async function shippedOrder(staff, buyer, company) {
  const res = await fetch(`${API}/api/v1/orders`, {
    method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${buyer}`, 'idempotency-key': key('order') },
    body: JSON.stringify({ lines: [{ variantId: VARIANT_A, quantity: 4 }], companyId: company.companyId, shipToSiteId: company.siteId, payOnCredit: true })
  });
  const placed = await res.json();
  if (!placed.data) throw new Error(`order failed: ${res.status} ${JSON.stringify(placed).slice(0, 300)}`);
  const id = placed.data.orderId;
  for (const step of ['processing', 'packed', 'ship']) {
    const rowVersion = (await send(staff, 'GET', `${A}/orders/${id}`)).json.data.rowVersion;
    const body = step === 'ship' ? { rowVersion, courierName: 'Steadfast Courier', trackingNumber: `SF-${tag}-${Math.floor(Math.random() * 9000 + 1000)}`, trackingUrl: null } : { rowVersion };
    const m = await send(staff, 'POST', `${A}/orders/${id}/${step}`, body);
    if (m.status !== 200) throw new Error(`${step} failed: ${m.status} ${JSON.stringify(m.json).slice(0, 200)}`);
  }
  return (await send(buyer, 'GET', `/api/v1/orders/${id}`)).json.data;
}

(async () => {
  const admin = await apiToken(PEOPLE.admin);
  const support = await apiToken(PEOPLE.support);
  const buyerToken = (await (await fetch(`${API}/api/v1/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ identifier: USERS.buyer.email, password: USERS.buyer.password }) })).json()).data.accessToken;
  const company = await setupCompany(admin, buyerToken, 'Console Credit Notes');
  const orders = [];
  for (let i = 0; i < 5; i++) orders.push(await shippedOrder(admin, buyerToken, company));
  const invoiceOf = async (o) => {
    const page = (await send(support, 'GET', `${A}/invoices?orderNumber=${o.orderNumber}&pageSize=50`)).json.data;
    const row = page.items.find((i) => i.invoice.orderId === o.id && i.invoice.kind === 'tax_invoice');
    return (await send(support, 'GET', `${A}/invoices/${row.invoice.id}`)).json.data.detail;
  };
  const inv = [];
  for (const o of orders) inv.push(await invoiceOf(o));
  r.check('setup: five shipped credit orders, each with a Specimen tax invoice', inv.every((d) => d.invoice.mode === 'specimen' && d.invoice.kind === 'tax_invoice'), inv.map((d) => d.invoice.number).join(' '));
  const goods = (d) => d.lines.find((l) => l.kind === 'goods');
  const body = (d, qty, extra = {}) => ({ reason: 'goods_returned', reasonText: 'E2E goods came back damaged', lines: [{ lineNo: goods(d).lineNo, quantity: qty }], ...extra });
  const request = (token, d, qty) => send(token, 'POST', `${A}/invoices/${d.invoice.id}/credit-notes`, body(d, qty), { 'Idempotency-Key': key('cn') });
  const queueApi = async (token) => (await send(token, 'GET', `${A}/credit-note-requests?status=requested&pageSize=50`)).json.data;
  const stepUp = async (who) => ({ 'X-Step-Up-Code': await freshCode(who) });
  const getReq = async (id) => (await send(support, 'GET', `${A}/credit-note-requests/${id}`)).json.data;

  const base = (await queueApi(support)).totalCount;
  const browser = await launch();
  const mk = async (width) => {
    const ctx = await browser.newContext({ viewport: { width, height: 900 } });
    const page = await ctx.newPage();
    page.errors = [];
    page.on('pageerror', (e) => page.errors.push(e.message));
    return page;
  };

  // ================= Support: request on screen
  const s = await mk(1280);
  await consoleSignIn(s, PEOPLE.support);
  await s.goto(`${CONSOLE}/invoices/${inv[0].invoice.id}`, { waitUntil: 'networkidle' });
  await s.getByTestId('invoice-number').waitFor();
  r.check('Support (invoices.credit_request): "Request credit note" is on the tax invoice detail', (await s.getByTestId('request-credit-note').count()) === 1);
  await s.getByTestId('request-credit-note').click();
  await s.getByTestId('credit-form').waitFor();
  await axeCheck(s, 'request form open');
  const g0 = goods(inv[0]);
  await s.getByTestId('credit-reason').selectOption('goods_returned');
  await s.getByTestId('credit-text').fill('One unit came back damaged (E2E)');
  await s.getByTestId(`credit-qty-${g0.lineNo}`).fill(String(g0.quantity + 1));
  await s.getByTestId('credit-send').click();
  r.check('form: a quantity above the invoice line is refused in words, nothing sent', (await s.getByTestId('credit-problem').innerText()).includes(`0 to ${g0.quantity}`) && (await queueApi(support)).totalCount === base);
  await s.getByTestId(`credit-qty-${g0.lineNo}`).fill('1');
  await s.getByTestId('credit-reason').selectOption('');
  await s.getByTestId('credit-send').click();
  r.check('form: a missing reason is refused in words', (await s.getByTestId('credit-problem').innerText()).includes('Choose a reason'));
  await s.getByTestId('credit-reason').selectOption('goods_returned');
  await s.getByTestId('credit-text').fill('abc');
  await s.getByTestId('credit-send').click();
  r.check('form: a short explanation is refused in words', (await s.getByTestId('credit-problem').innerText()).includes('5 to 500'));
  await s.getByTestId('credit-text').fill('One unit came back damaged (E2E)');
  await s.getByTestId('credit-send').click();
  await s.getByTestId('credit-requested').waitFor();
  const q1 = await queueApi(support);
  const mine1 = q1.items.filter((i) => i.originalNumber === inv[0].invoice.number);
  r.check('request sent: the API has one pending request of Support for this invoice, quantity 1, no number yet', q1.totalCount === base + 1 && mine1.length === 1 && mine1[0].lines[0].quantity === 1 && !mine1[0].issuedNumber);
  const req1 = mine1[0];
  r.check('notice says it has no number yet and links the request', (await s.getByTestId('credit-requested').innerText()).includes('no number yet'));

  // Support is the requester: decision page
  await s.getByTestId('credit-requested-link').click();
  await s.getByTestId('cn-decision').waitFor();
  r.check('requester: Approve and Reject are disabled with the four-eyes words, Withdraw is offered', (await s.getByTestId('cn-approve').isDisabled()) && (await s.getByTestId('cn-reject').isDisabled()) && (await s.getByTestId('cn-hint').innerText()).includes('another person must approve') && (await s.getByTestId('cn-withdraw').count()) === 1);
  await axeCheck(s, 'decision page (requester)');
  const sApprove = await send(support, 'POST', `${A}/credit-note-requests/${req1.id}/approve`, undefined, await stepUp(PEOPLE.support));
  r.check('Support cannot approve: the API answers 403', sApprove.status === 403, String(sApprove.status));
  // Support sees the queue and the sidebar link
  await s.getByRole('link', { name: /^Credit notes/ }).click();
  await s.getByTestId('credit-queue').waitFor();
  r.check('queue: the sidebar link opens /invoices/credit-notes; rows equal the API; "Asked by you"', new URL(s.url()).pathname === '/invoices/credit-notes' && (await s.getByTestId('credit-row').count()) === base + 1 && (await s.getByTestId('credit-queue').innerText()).includes('Asked by you'));
  await axeCheck(s, 'credit notes queue');

  // ================= Finance A: approve with a code (wrong code first)
  const fa = await mk(1280);
  await consoleSignIn(fa, PEOPLE.financeA);
  await fa.goto(`${CONSOLE}/invoices/credit-notes`, { waitUntil: 'networkidle' });
  await fa.getByTestId('credit-row').filter({ hasText: inv[0].invoice.number }).getByTestId('credit-open').click();
  await fa.getByTestId('cn-decision').waitFor();
  r.check('Finance (not the requester): Approve is enabled, no four-eyes hint, no Withdraw', (await fa.getByTestId('cn-approve').isEnabled()) && (await fa.getByTestId('cn-hint').count()) === 0 && (await fa.getByTestId('cn-withdraw').count()) === 0);
  await fa.getByTestId('cn-approve').click();
  await fa.getByLabel('Authenticator code').fill('000000');
  await fa.getByRole('button', { name: 'Approve', exact: true }).click();
  await fa.getByTestId('stepup-error').waitFor();
  r.check('a wrong code: the dialog stays open and says so in words; still not issued', (await fa.getByTestId('stepup-error').innerText()).toLowerCase().includes('code') && (await getReq(req1.id)).status === 'requested');
  await fa.getByLabel('Authenticator code').fill(await freshCode(PEOPLE.financeA));
  await fa.getByRole('button', { name: 'Approve', exact: true }).click();
  await fa.getByTestId('decision-issued').waitFor({ timeout: 30000 });
  const done = await getReq(req1.id);
  const numberOnScreen = await fa.getByTestId('cn-number-link').innerText();
  r.check('approved: the API has the request approved with a 6.7 number; the screen shows the same number and "No refund is due" for a credit order', done.status === 'approved' && done.issuedNumber === numberOnScreen && (await fa.getByTestId('decision-issued').innerText()).includes('No refund is due'), numberOnScreen);
  await fa.getByTestId('cn-number-link').click();
  await fa.getByTestId('invoice-number').waitFor();
  const cnDetail = (await send(support, 'GET', `${A}/invoices/${done.issuedInvoiceId}`)).json.data.detail;
  r.check('the number links to the credit note detail: kind credit note of the original invoice', (await fa.getByTestId('invoice-number').innerText()) === done.issuedNumber && cnDetail.invoice.kind === 'credit_note' && cnDetail.invoice.originalNumber === inv[0].invoice.number && (await fa.getByTestId('request-credit-note').count()) === 0);
  await axeCheck(fa, 'credit note detail');
  await fa.goto(`${CONSOLE}/invoices/credit-notes/${req1.id}`, { waitUntil: 'networkidle' });
  r.check('the decided request shows no decision buttons and keeps the number link', (await fa.getByTestId('cn-decision').count()) === 0 && (await fa.getByTestId('cn-number-link').innerText()) === done.issuedNumber);

  // ================= Finance B: reject with a reason and a code
  const rej = await request(support, inv[1], 1);
  r.check('API: Support requests a second credit note (201)', rej.status === 201);
  const fb = await mk(1280);
  await consoleSignIn(fb, PEOPLE.financeB);
  await fb.goto(`${CONSOLE}/invoices/credit-notes/${rej.json.data.id}`, { waitUntil: 'networkidle' });
  await fb.getByTestId('cn-decision').waitFor();
  await fb.getByTestId('cn-reject').click();
  await fb.getByTestId('reason-input').fill('no');
  await fb.getByRole('button', { name: 'Continue' }).click();
  r.check('reject: a too short reason is refused before any code is asked', (await fb.getByLabel('Authenticator code').count()) === 0 && (await fb.getByTestId('reason-input').count()) === 1);
  await fb.getByTestId('reason-input').fill('The goods were not returned (E2E)');
  await fb.getByRole('button', { name: 'Continue' }).click();
  await fb.getByLabel('Authenticator code').fill(await freshCode(PEOPLE.financeB));
  await fb.getByRole('button', { name: 'Reject', exact: true }).click();
  await fb.getByTestId('decision-note').waitFor({ timeout: 30000 });
  const rejected = await getReq(rej.json.data.id);
  r.check('rejected: status rejected with the reason, no number; the screen says nothing was issued', rejected.status === 'rejected' && rejected.decisionReason === 'The goods were not returned (E2E)' && !rejected.issuedNumber && (await fb.getByTestId('decision-note').innerText()).includes('No credit note was issued'));

  // ================= Withdraw on screen (Support)
  const wd = await request(support, inv[2], 1);
  await s.goto(`${CONSOLE}/invoices/credit-notes/${wd.json.data.id}`, { waitUntil: 'networkidle' });
  await s.getByTestId('cn-withdraw').click();
  await s.getByTestId('decision-note').waitFor();
  r.check('withdraw: no code needed, the API shows it withdrawn', (await getReq(wd.json.data.id)).status === 'withdrawn' && (await s.getByTestId('cn-status').innerText()).includes('Withdrawn'));

  // ================= Cap refusal and "changed", in words
  const full = await request(support, inv[3], goods(inv[3]).quantity);
  const small = await request(support, inv[3], 1);
  r.check('API: a full request and a small one for the same invoice are both accepted as requests', full.status === 201 && small.status === 201, `${full.status}/${small.status}`);
  const appr = await send(await apiToken(PEOPLE.financeA), 'POST', `${A}/credit-note-requests/${full.json.data.id}/approve`, undefined, await stepUp(PEOPLE.financeA));
  r.check('API: the full one is approved by Finance A (200)', appr.status === 200, String(appr.status));
  await fb.goto(`${CONSOLE}/invoices/credit-notes/${small.json.data.id}`, { waitUntil: 'networkidle' });
  await fb.getByTestId('cn-approve').click();
  await fb.getByLabel('Authenticator code').fill(await freshCode(PEOPLE.financeB));
  await fb.getByRole('button', { name: 'Approve', exact: true }).click();
  await fb.getByTestId('decision-alert').waitFor({ timeout: 30000 });
  const alertText = await fb.getByTestId('decision-alert').innerText();
  r.check('approve after the invoice was credited in full: refused in words (cap or changed), nothing issued, page reloaded', /left on the invoice|Something changed/.test(alertText) && (await getReq(small.json.data.id)).status === 'requested', alertText.slice(0, 120));
  // a new request above what is left is refused by the server on the form
  await s.goto(`${CONSOLE}/invoices/${inv[3].invoice.id}`, { waitUntil: 'networkidle' });
  await s.getByTestId('request-credit-note').click();
  await s.getByTestId(`credit-qty-${goods(inv[3]).lineNo}`).fill('1');
  await s.getByTestId('credit-reason').selectOption('error');
  await s.getByTestId('credit-text').fill('Over the cap on purpose (E2E)');
  await s.getByTestId('credit-send').click();
  await s.getByTestId('credit-problem').waitFor();
  r.check('request over what is left (server): the cap refusal in words, the form stays open', (await s.getByTestId('credit-problem').innerText()).includes('left on the invoice') && (await s.getByTestId('credit-form').count()) === 1);

  // ================= Roles: a credit note detail has no request button; the queue equals the API
  const qNow = await queueApi(support);
  await s.goto(`${CONSOLE}/invoices/credit-notes`, { waitUntil: 'networkidle' });
  const shown = await s.getByTestId('credit-open').allInnerTexts();
  r.check('queue equals the API (pending only)', shown.length === qNow.items.length && [...shown].sort().join() === qNow.items.map((i) => i.originalNumber).sort().join(), `${shown.length}/${qNow.totalCount}`);
  r.check('no page errors', [s, fa, fb].every((p) => p.errors.length === 0), [s, fa, fb].flatMap((p) => p.errors).join(' | ').slice(0, 300));

  // ================= 360 px
  await s.setViewportSize({ width: 360, height: 900 });
  await s.goto(`${CONSOLE}/invoices/credit-notes`, { waitUntil: 'networkidle' });
  r.check('360 px queue: no horizontal scroll of the page', await noOverflow(s));
  await axeCheck(s, 'queue at 360 px');
  await s.goto(`${CONSOLE}/invoices/credit-notes/${small.json.data.id}`, { waitUntil: 'networkidle' });
  await s.getByTestId('cn-lines').waitFor();
  r.check('360 px decision: no horizontal scroll of the page', await noOverflow(s));
  await axeCheck(s, 'decision at 360 px');
  await s.goto(`${CONSOLE}/invoices/${inv[4].invoice.id}`, { waitUntil: 'networkidle' });
  await s.getByTestId('request-credit-note').click();
  await s.getByTestId('credit-form').waitFor();
  r.check('360 px form: the dialog fits the screen', await noOverflow(s));
  await axeCheck(s, 'request form at 360 px');
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
