// E6.6c console withholdings: "Record VDS/AIT certificate" on the invoice detail (only with credit.record_payment), the list /invoices/withholdings (status, kind
// and invoice filters in the URL), the detail page (Verify with a code, disabled with a four-eyes reason for the recorder), 409 texts in words,
// 9.1 section c with its total, axe 0, 360 px. Checked against the API as oracle.
// SCRATCH DB only (E2E_SQL_CMD against mw_*). Set E2E_CONSOLE_WITHHOLDINGS=1 to run. E2E_CONSOLE_URL = the console. E2E_VARIANT_A = a variant with stock.
// Written, not yet run (E6.6c). Users: finance A records, finance B verifies, Support only reads.
import { CONSOLE, API, USERS, launch } from './lib.mjs';
import { PEOPLE as BASE, VARIANT_A, apiToken, axeCheck, freshCode, key, noOverflow, r, requireScratch, send, setupCompany, tag } from './returns-lib.mjs';

if (process.env.E2E_CONSOLE_WITHHOLDINGS !== '1') {
  console.log('SKIP  console withholdings E2E (set E2E_CONSOLE_WITHHOLDINGS=1 to run it)');
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

(async () => {
  const admin = await apiToken(PEOPLE.admin);
  const support = await apiToken(PEOPLE.support);
  const login = await fetch(`${API}/api/v1/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ identifier: USERS.buyer.email, password: USERS.buyer.password }) });
  const buyerToken = (await login.json()).data.accessToken;
  const company = await setupCompany(admin, buyerToken, 'Console Withholdings');
  const placed = await (await fetch(`${API}/api/v1/orders`, {
    method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${buyerToken}`, 'idempotency-key': key('order') },
    body: JSON.stringify({ lines: [{ variantId: VARIANT_A, quantity: 4 }], companyId: company.companyId, shipToSiteId: company.siteId, payOnCredit: true })
  })).json();
  const orderId = placed.data.orderId;
  for (const step of ['processing', 'packed', 'ship']) {
    const rowVersion = (await send(admin, 'GET', `${A}/orders/${orderId}`)).json.data.rowVersion;
    const body = step === 'ship' ? { rowVersion, courierName: 'Steadfast Courier', trackingNumber: `SF-${tag}-1234`, trackingUrl: null } : { rowVersion };
    await send(admin, 'POST', `${A}/orders/${orderId}/${step}`, body);
  }
  const rows = (await send(support, 'GET', `${A}/invoices?pageSize=50`)).json.data.items;
  const inv = rows.find((i) => i.invoice.orderId === orderId && i.invoice.kind === 'tax_invoice').invoice;

  const browser = await launch();
  const mk = async (width) => {
    const page = await (await browser.newContext({ viewport: { width, height: 900 } })).newPage();
    page.errors = [];
    page.on('pageerror', (e) => page.errors.push(e.message));
    return page;
  };

  // Finance A records on screen: an empty form and an over-the-cap amount are refused in words, then a valid one is sent
  const a = await mk(1280);
  await consoleSignIn(a, PEOPLE.financeA);
  await a.goto(`${CONSOLE}/invoices/${inv.id}`, { waitUntil: 'networkidle' });
  await a.getByTestId('record-withholding').click();
  await a.getByTestId('withholding-form').waitFor();
  await axeCheck(a, 'record form open');
  await a.getByTestId('withholding-send').click();
  r.check('empty form: a problem in words, nothing sent', (await a.getByTestId('withholding-problem').innerText()).includes('VDS or AIT'));
  await a.getByTestId('withholding-kind').selectOption('vds');
  await a.getByTestId('withholding-number').fill(`E2E-${tag}`);
  await a.getByTestId('withholding-date').fill(new Date().toISOString().slice(0, 10));
  await a.getByTestId('withholding-amount').fill('99999999');
  await a.getByTestId('withholding-send').click();
  await a.getByTestId('withholding-problem').waitFor();
  r.check('over the VAT cap: refused in words', /VAT still left/.test(await a.getByTestId('withholding-problem').innerText()));
  await a.getByTestId('withholding-amount').fill('1');
  await a.getByTestId('withholding-send').click();
  await a.getByTestId('withholding-recorded').waitFor();
  const listed = (await send(admin, 'GET', `${A}/withholdings?invoiceId=${inv.id}`)).json.data;
  r.check('API oracle: one recorded certificate of BDT 1', listed.totalCount === 1 && listed.items[0].amountMinor === 100 && listed.items[0].status === 'recorded');
  const wid = listed.items[0].id;

  // Four eyes: the recorder sees Verify disabled with the reason; Support has no record button
  await a.goto(`${CONSOLE}/invoices/withholdings/${wid}`, { waitUntil: 'networkidle' });
  await a.getByTestId('wh-verify').waitFor();
  r.check('recorder: Verify is disabled with the four-eyes reason', (await a.getByTestId('wh-verify').isDisabled()) && /another person must verify/.test(await a.getByTestId('wh-hint').innerText()));
  await axeCheck(a, 'withholding detail');
  const s = await mk(1280);
  await consoleSignIn(s, PEOPLE.support);
  await s.goto(`${CONSOLE}/invoices/${inv.id}`, { waitUntil: 'networkidle' });
  await s.getByTestId('invoice-number').waitFor();
  r.check('Support: no "Record certificate" button', (await s.getByTestId('record-withholding').count()) === 0);

  // The list filters live in the URL
  await a.goto(`${CONSOLE}/invoices/withholdings?kind=vds&invoiceId=${inv.id}`, { waitUntil: 'networkidle' });
  await a.getByTestId('wh-table').waitFor();
  r.check('list: the filtered row is shown, the kind select follows the URL', (await a.getByTestId('wh-row').count()) === 1 && (await a.getByTestId('wh-kind').inputValue()) === 'vds');
  await axeCheck(a, 'withholdings list');

  // Finance B verifies with a code (a wrong one first)
  const b = await mk(1280);
  await consoleSignIn(b, PEOPLE.financeB);
  await b.goto(`${CONSOLE}/invoices/withholdings/${wid}`, { waitUntil: 'networkidle' });
  await b.getByTestId('wh-verify').click();
  await b.getByLabel(/6-digit/).fill('000000');
  await b.getByRole('button', { name: 'Verify', exact: true }).click();
  await b.getByRole('alert').waitFor();
  await b.getByLabel(/6-digit/).fill(await freshCode(PEOPLE.financeB));
  await b.getByRole('button', { name: 'Verify', exact: true }).click();
  await b.getByTestId('wh-note').waitFor();
  r.check('API oracle: verified', (await send(admin, 'GET', `${A}/withholdings/${wid}`)).json.data.status === 'verified');

  // 9.1 section c shows the verified VDS and a total
  const month = new Date(Date.now() + 6 * 3600000).toISOString().slice(0, 7);
  const v = await mk(1280);
  await consoleSignIn(v, PEOPLE.admin);
  await v.goto(`${CONSOLE}/invoices/vat-month`, { waitUntil: 'networkidle' });
  await v.getByTestId('vat-month').fill(month);
  await v.getByTestId('vat-c').waitFor();
  r.check('9.1 (c) lists the certificate number', (await v.getByTestId('vat-c').innerText()).includes(`E2E-${tag}`));
  r.check('9.1 (c) shows its total', (await v.getByTestId('vat-c-total').innerText()).includes('BDT'));

  const m = await mk(360);
  await consoleSignIn(m, PEOPLE.financeA);
  await m.goto(`${CONSOLE}/invoices/withholdings?status=verified`, { waitUntil: 'networkidle' });
  r.check('360 px: no horizontal overflow', await noOverflow(m));
  r.check('no page errors', [a, b, s, v, m].every((p) => p.errors.length === 0));
  await browser.close();
  r.done();
})().catch((e) => { console.error(e); process.exit(1); });
