// E6.8c console invoices: /invoices list (filters number, order number, company, Dhaka dates, kind, mode, all in the URL, 50 a page), /invoices/{id} (whole BIN,
// lines, totals, links, PDF EN/BN, real 503 tamper words), "Print Mushak-6.3" on the order detail after ship, "Check a PDF" (SHA-256 in the browser, only the hash is
// sent), a Moderator without invoices.view (not-allowed text, API 403), axe 0, 360 px.
// Real: company A (2 orders) and company B (1 order) placed and shipped through the API (Invoicing:Mode=Specimen + IssuerSellerId); every filter has the API as
// oracle; the PDF is downloaded through the screens and found by the staff SHA lookup; the stored file is changed on disk for the 503.
// Mocked: one 120-row list answer (the paging buttons need more than a page of invoices).
// SCRATCH DB only (E2E_SQL_CMD against mw_*). Set E2E_CONSOLE_INVOICES=1 to run. E2E_DOCS_DIR = the API's Documents:LocalPath. E2E_CONSOLE_URL = the console.
import { createHash, randomBytes } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { CONSOLE, API, USERS, apiCall, launch } from './lib.mjs';
import { PEOPLE as BASE, VARIANT_A, apiToken, axeCheck, freshCode, key, noOverflow, r, requireScratch, send, setupCompany, tag } from './returns-lib.mjs';

if (process.env.E2E_CONSOLE_INVOICES !== '1') {
  console.log('SKIP  console invoices E2E (set E2E_CONSOLE_INVOICES=1 to run it)');
  process.exit(0);
}
requireScratch();
if (!process.env.E2E_DOCS_DIR) throw new Error('E2E_DOCS_DIR must name the API Documents:LocalPath');
const person = (email) => ({ email, password: USERS.admin.password, totpSecret: USERS.admin.totpSecret });
const PEOPLE = { admin: BASE.admin, support: person('support.e2e@mechware.com.bd'), moderator: person('moderator.e2e@mechware.com.bd') };
const today = new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Dhaka' }).format(new Date());
const dayShift = (n) => new Date(Date.parse(`${today}T00:00:00Z`) + n * 86400000).toISOString().slice(0, 10);
const same = (a, b) => JSON.stringify([...a].sort()) === JSON.stringify([...b].sort());
const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');

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
    body: JSON.stringify({ lines: [{ variantId: VARIANT_A, quantity: 2 }], companyId: company.companyId, shipToSiteId: company.siteId, payOnCredit: true })
  });
  const placed = await res.json();
  if (!placed.data) throw new Error(`order failed: ${res.status} ${JSON.stringify(placed).slice(0, 300)}`);
  const id = placed.data.orderId;
  for (const step of ['processing', 'packed', 'ship']) {
    const rowVersion = (await send(staff, 'GET', `/api/v1/admin/orders/${id}`)).json.data.rowVersion;
    const body = step === 'ship' ? { rowVersion, courierName: 'Steadfast Courier', trackingNumber: `SF-${tag}-${Math.floor(Math.random() * 9000 + 1000)}`, trackingUrl: null } : { rowVersion };
    const m = await send(staff, 'POST', `/api/v1/admin/orders/${id}/${step}`, body);
    if (m.status !== 200) throw new Error(`${step} failed: ${m.status} ${JSON.stringify(m.json).slice(0, 200)}`);
  }
  return (await send(buyer, 'GET', `/api/v1/orders/${id}`)).json.data;
}

(async () => {
  const admin = await apiToken(PEOPLE.admin);
  const support = await apiToken(PEOPLE.support);
  const moderator = await apiToken(PEOPLE.moderator);
  const buyerToken = (await (await fetch(`${API}/api/v1/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ identifier: USERS.buyer.email, password: USERS.buyer.password }) })).json()).data.accessToken;
  const A = await setupCompany(admin, buyerToken, 'Console Invoices A');
  const B = await setupCompany(admin, buyerToken, 'Console Invoices B');
  const o1 = await shippedOrder(admin, buyerToken, A);
  const o2 = await shippedOrder(admin, buyerToken, A);
  const o3 = await shippedOrder(admin, buyerToken, B);
  const listApi = async (qs) => (await send(support, 'GET', `/api/v1/admin/invoices?${qs}&pageSize=50`)).json.data;
  const numbersApi = async (qs) => (await listApi(qs)).items.map((i) => i.invoice.number);
  const find = async (o) => (await listApi(`orderNumber=${o.orderNumber}`)).items.find((i) => i.invoice.orderId === o.id) ?? (() => { throw new Error(`no invoice for ${o.orderNumber}`); })();
  const I1 = (await find(o1)).invoice, I2 = (await find(o2)).invoice, I3 = (await find(o3)).invoice;
  r.check('setup: companies A (2 orders) and B (1 order) shipped through the real API; each has one Specimen tax invoice', [I1, I2, I3].every((i) => i.mode === 'specimen' && i.kind === 'tax_invoice'), [I1, I2, I3].map((i) => i.number).join(' '));
  const detailApi = async (id) => (await send(support, 'GET', `/api/v1/admin/invoices/${id}`)).json.data;
  const lookup = (bytes) => apiCall(admin, 'GET', `/api/v1/admin/invoice-documents/${sha(bytes)}`);

  const browser = await launch();
  const mk = async (width) => {
    const ctx = await browser.newContext({ viewport: { width, height: 900 }, acceptDownloads: true });
    const page = await ctx.newPage();
    page.errors = [];
    page.on('pageerror', (e) => page.errors.push(e.message));
    return page;
  };
  const rows = (p) => p.getByTestId('invoice-link').allInnerTexts();
  const url = (p) => new URL(p.url());
  const settle = async (p) => { await p.waitForLoadState('networkidle'); await p.waitForTimeout(150); };
  const listGets = [];
  const download = async (page, testId) => {
    const [dl] = await Promise.all([page.waitForEvent('download'), page.getByTestId(testId).first().click()]);
    return { name: dl.suggestedFilename(), bytes: readFileSync(await dl.path()) };
  };

  // ================= Moderator: no invoices.view
  {
    const m = await mk(1280);
    await consoleSignIn(m, PEOPLE.moderator);
    await m.getByText('Operations console').first().waitFor();
    r.check('Moderator: no Invoices link in the sidebar', (await m.getByRole('link', { name: 'Invoices', exact: true }).count()) === 0);
    await m.goto(`${CONSOLE}/invoices`);
    await m.getByText('Your role does not allow this action (invoices.view).').waitFor();
    r.check('Moderator on /invoices: the not-allowed text, no table, no filter form', (await m.getByTestId('invoices-table').count()) === 0 && (await m.getByRole('search').count()) === 0);
    await m.goto(`${CONSOLE}/invoices/${I1.id}`);
    await m.getByText('Your role does not allow this action (invoices.view).').waitFor();
    r.check('Moderator on a detail: not-allowed text and no invoice number', (await m.getByText(I1.number).count()) === 0);
    await axeCheck(m, 'not allowed page (Moderator)');
    const api = await Promise.all([send(moderator, 'GET', '/api/v1/admin/invoices'), send(moderator, 'GET', `/api/v1/admin/invoices/${I1.id}`), send(moderator, 'GET', `/api/v1/admin/invoices/${I1.id}/pdf`)]);
    r.check('Moderator API: list, detail and PDF answer 403', api.every((x) => x.status === 403), api.map((x) => x.status).join('/'));
    await m.context().close();
  }

  // ================= Support: list and filters
  const s = await mk(1280);
  s.on('request', (req) => { if (req.method() === 'GET' && /\/api\/v1\/admin\/invoices\?/.test(req.url())) listGets.push(req.url()); });
  await consoleSignIn(s, PEOPLE.support);
  await s.getByRole('link', { name: 'Invoices', exact: true }).click();
  await s.getByTestId('filter-count').waitFor();
  await settle(s);
  r.check('Support: the sidebar link opens /invoices', url(s).pathname === '/invoices');
  const all = await listApi('');
  r.check('list: rows and pager total equal the API (default, newest first)', same(await rows(s), all.items.map((i) => i.invoice.number)) && (await s.getByTestId('pager').innerText()).includes(`(${all.totalCount} invoices)`), `${all.items.length}/${all.totalCount}`);
  r.check('list: mine are in it with "Specimen" in words', (await rows(s)).includes(I1.number) && (await s.getByTestId('invoice-row').first().innerText()).includes('Specimen'));
  await axeCheck(s, 'invoices list');

  const apply = async (fill) => { await fill(); await Promise.all([s.waitForResponse((x) => /\/api\/v1\/admin\/invoices\?/.test(x.url()), { timeout: 30000 }), s.getByRole('button', { name: 'Apply filters' }).click()]); await settle(s); };
  const clear = async () => { await s.getByRole('button', { name: 'Clear all filters' }).click(); await settle(s); };
  const param = (k) => url(s).searchParams.get(k);

  await apply(() => s.getByLabel('Invoice number starts with').fill(I2.number));
  r.check('filter number: exactly that invoice, URL holds number=, equals the API', same(await rows(s), await numbersApi(`number=${encodeURIComponent(I2.number)}`)) && (await rows(s)).join() === I2.number && param('number') === I2.number);
  await clear();
  await apply(() => s.getByLabel('Order number starts with').fill(o3.orderNumber));
  r.check('filter order number: only company B invoice, URL holds order=, equals the API', same(await rows(s), await numbersApi(`orderNumber=${o3.orderNumber}`)) && (await rows(s)).join() === I3.number && param('order') === o3.orderNumber);
  await clear();
  await apply(async () => { await s.getByLabel('Issued from (Dhaka day)').fill(dayShift(1)); });
  r.check('filter from tomorrow: nothing, "No invoice matches", equals the API', (await rows(s)).length === 0 && (await numbersApi(`from=${dayShift(1)}`)).length === 0 && (await s.getByTestId('invoices-empty').count()) === 1);
  await clear();
  await apply(async () => { await s.getByLabel('Issued from (Dhaka day)').fill(today); await s.getByLabel('Issued to (Dhaka day)').fill(today); });
  r.check('filter today..today: includes mine, equals the API, URL holds from and to', same(await rows(s), await numbersApi(`from=${today}&to=${today}`)) && (await rows(s)).includes(I1.number) && param('from') === today && param('to') === today);
  await clear();
  await apply(async () => { await s.getByLabel('Issued to (Dhaka day)').fill(dayShift(-1)); });
  r.check('filter to yesterday: none of mine, equals the API', same(await rows(s), await numbersApi(`to=${dayShift(-1)}`)) && !(await rows(s)).includes(I1.number));
  await clear();
  await apply(() => s.getByTestId('filter-kind').selectOption('credit_note'));
  r.check('filter kind = credit note: equals the API, none of mine', same(await rows(s), await numbersApi('kind=credit_note')) && !(await rows(s)).includes(I1.number) && param('kind') === 'credit_note');
  await clear();
  await apply(() => s.getByTestId('filter-kind').selectOption('tax_invoice'));
  r.check('filter kind = tax invoice: equals the API, includes mine', same(await rows(s), await numbersApi('kind=tax_invoice')) && (await rows(s)).includes(I1.number));
  await clear();
  await apply(() => s.getByTestId('filter-mode').selectOption('live'));
  r.check('filter mode = live: equals the API, none of mine (they are Specimen)', same(await rows(s), await numbersApi('mode=live')) && !(await rows(s)).includes(I1.number));
  await clear();
  await apply(() => s.getByTestId('filter-mode').selectOption('specimen'));
  r.check('filter mode = specimen: equals the API, includes mine', same(await rows(s), await numbersApi('mode=specimen')) && (await rows(s)).includes(I1.number) && param('mode') === 'specimen');
  await clear();
  // company picker
  await s.getByRole('button', { name: 'Choose a company' }).click();
  await s.getByTestId('company-list').waitFor();
  await s.getByRole('searchbox', { name: 'Company' }).fill(B.legalName);
  await s.getByTestId('company-list').selectOption({ label: B.legalName });
  await s.getByTestId('company-chosen').waitFor();
  await apply(async () => {});
  r.check('filter company B (picker): chip with the name, only B invoices equal to the API, URL holds the id', same(await rows(s), await numbersApi(`companyId=${B.companyId}`)) && (await rows(s)).join() === I3.number && param('company') === B.companyId && (await s.getByTestId('company-chosen').innerText()).includes(B.legalName));
  await clear();

  // all filters together, then the URL round trip
  await apply(async () => {
    await s.getByLabel('Invoice number starts with').fill(I1.number.slice(0, 3));
    await s.getByLabel('Order number starts with').fill(o1.orderNumber.slice(0, 6));
    await s.getByLabel('Issued from (Dhaka day)').fill(dayShift(-1));
    await s.getByLabel('Issued to (Dhaka day)').fill(dayShift(1));
    await s.getByTestId('filter-kind').selectOption('tax_invoice');
    await s.getByTestId('filter-mode').selectOption('specimen');
  });
  const comboQs = `number=${encodeURIComponent(I1.number.slice(0, 3))}&orderNumber=${o1.orderNumber.slice(0, 6)}&from=${dayShift(-1)}&to=${dayShift(1)}&kind=tax_invoice&mode=specimen`;
  const comboRows = await rows(s);
  r.check('all filters together equal the API and the counter says 6', same(comboRows, await numbersApi(comboQs)) && comboRows.length >= 3 && (await s.getByTestId('filter-count').innerText()).startsWith('6 filters'));
  const comboUrl = s.url();
  await s.reload({ waitUntil: 'networkidle' });
  await s.getByTestId('filter-count').waitFor();
  r.check('reload: same rows and the form shows the URL values', same(await rows(s), comboRows) && (await s.getByLabel('Invoice number starts with').inputValue()) === I1.number.slice(0, 3) && (await s.getByTestId('filter-mode').inputValue()) === 'specimen');
  const second = await mk(1280);
  await consoleSignIn(second, PEOPLE.support);
  await second.goto(comboUrl, { waitUntil: 'networkidle' });
  await second.getByTestId('filter-count').waitFor();
  await second.waitForTimeout(500);
  r.check('a second browser opening the shared link sees the same rows', same(await rows(second), comboRows));
  await second.context().close();
  await clear();
  await apply(() => s.getByLabel('Invoice number starts with').fill(I2.number));
  await s.goBack();
  await settle(s);
  r.check('Back: the previous (empty) filter state and the full list return', url(s).search === '' && same(await rows(s), all.items.map((i) => i.invoice.number)) === true);
  await s.goForward();
  await settle(s);
  r.check('Forward: the number filter is back', (await rows(s)).join() === I2.number);
  await clear();

  // from after to: refused on screen, nothing sent
  const before = listGets.length;
  await s.getByLabel('Issued from (Dhaka day)').fill(dayShift(2));
  await s.getByLabel('Issued to (Dhaka day)').fill(dayShift(1));
  await s.getByRole('button', { name: 'Apply filters' }).click();
  await s.getByTestId('filter-problem').waitFor();
  r.check('from after to: said on screen, nothing sent, address unchanged', (await s.getByTestId('filter-problem').innerText()).includes('after') && listGets.length === before && url(s).search === '');
  await clear();

  // paging with a mocked 120-row answer: Next asks for page 2 and the address holds page=2
  const fake = Array.from({ length: 50 }, (_, i) => ({ invoice: { ...I1, id: `${I1.id.slice(0, 24)}${String(i).padStart(12, '0')}`, number: `FAKE-${String(i + 1).padStart(4, '0')}` }, sellerId: I1.id, companyId: A.companyId, buyerName: 'Fake Buyer' }));
  const pageAsked = [];
  await s.route('**/api/v1/admin/invoices?*', (route) => {
    const u = new URL(route.request().url());
    pageAsked.push(`${u.searchParams.get('page')}/${u.searchParams.get('pageSize')}`);
    return route.fulfill({ status: 200, contentType: 'application/json', headers: { 'access-control-allow-origin': CONSOLE, 'access-control-allow-credentials': 'true' }, body: JSON.stringify({ data: { items: fake, page: Number(u.searchParams.get('page')), pageSize: 50, totalCount: 120 } }) });
  });
  await s.reload({ waitUntil: 'networkidle' });
  await s.getByTestId('invoice-row').first().waitFor();
  r.check('paging (mocked 120): page 1 of 3, 50 rows, pageSize 50 asked', (await s.getByTestId('pager').innerText()).includes('Page 1 of 3') && (await s.getByTestId('invoice-row').count()) === 50 && pageAsked.at(-1) === '1/50');
  await s.getByRole('button', { name: 'Next' }).click();
  await settle(s);
  r.check('paging: Next asks page 2 and the address holds page=2', pageAsked.at(-1) === '2/50' && param('page') === '2' && (await s.getByTestId('pager').innerText()).includes('Page 2 of 3'));
  await s.unroute('**/api/v1/admin/invoices?*');
  await s.goto(`${CONSOLE}/invoices`, { waitUntil: 'networkidle' });
  await s.getByTestId('invoice-row').first().waitFor();

  // ================= detail
  await s.getByLabel('Invoice number starts with').fill(I1.number);
  await s.getByRole('button', { name: 'Apply filters' }).click();
  await s.getByTestId('invoice-link').getByText(I1.number).click();
  await s.getByTestId('invoice-number').waitFor();
  await settle(s);
  const det = await detailApi(I1.id);
  const bin = det.detail.buyer.bin;
  r.check('detail: number, Specimen words, whole BIN equal to the API (not masked)', (await s.getByTestId('invoice-number').innerText()) === I1.number && (await s.getByTestId('invoice-specimen').count()) === 1 && /^\d{9,}$/.test(bin) && (await s.getByTestId('buyer-bin').innerText()) === bin, `bin=${bin}`);
  r.check('detail: every line and the total equal the API', (await s.getByTestId('invoice-line').count()) === det.detail.lines.length && (await s.getByTestId('invoice-total').innerText()).replace(/[^\d]/g, '').includes(String(Math.floor(I1.totalMinor / 100))));
  r.check('detail: links to the order and the company', (await s.getByTestId('order-link').getAttribute('href')) === `/orders/${o1.id}` && (await s.getByTestId('company-link').getAttribute('href')) === `/credit/companies/${A.companyId}`);
  await axeCheck(s, 'invoice detail');

  const en = await download(s, 'invoice-pdf-en');
  const bn = await download(s, 'invoice-pdf-bn');
  const lenEn = await lookup(en.bytes), lenBn = await lookup(bn.bytes);
  r.check('PDF from the detail: English and Bangla start with %PDF, file names carry the language, different bytes', en.bytes.subarray(0, 4).toString() === '%PDF' && bn.bytes.subarray(0, 4).toString() === '%PDF' && en.name.endsWith('-en.pdf') && bn.name.endsWith('-bn.pdf') && !en.bytes.equals(bn.bytes));
  r.check('PDF from the detail: the staff SHA lookup finds this invoice with the right language', lenEn.json.data?.invoiceId === I1.id && lenEn.json.data.locale === 'en' && lenBn.json.data?.invoiceId === I1.id && lenBn.json.data.locale === 'bn');

  // link to the order opens the order detail with the Print action
  await s.getByTestId('order-link').click();
  await s.getByTestId('order-number').waitFor();
  await s.getByTestId('print-mushak').waitFor();
  r.check('order link opens the order detail; it lists the invoice number and offers "Print Mushak-6.3"', (await s.getByTestId('invoice-numbers').innerText()).includes(I1.number) && (await s.getByTestId('print-mushak-en').innerText()).includes('Print Mushak-6.3'));
  await axeCheck(s, 'order detail with the print action');
  r.check('shipped order: "Print challan" EN and BN download a PDF (NOT RUN)', (await download(s, 'print-challan-en')).bytes.subarray(0, 4).toString() === '%PDF' && (await download(s, 'print-challan-bn')).bytes.subarray(0, 4).toString() === '%PDF');
  const pen = await download(s, 'print-mushak-en');
  const pbn = await download(s, 'print-mushak-bn');
  const lpen = await lookup(pen.bytes), lpbn = await lookup(pbn.bytes);
  r.check('Print Mushak-6.3 (English, Bangla): %PDF, the invoice of this order, right language', pen.bytes.subarray(0, 4).toString() === '%PDF' && lpen.json.data?.invoiceId === I1.id && lpen.json.data.locale === 'en' && lpbn.json.data?.invoiceId === I1.id && lpbn.json.data.locale === 'bn');
  r.check('Print Mushak-6.3 sends the same bytes as the detail download (stored file, same hash)', sha(pen.bytes) === sha(en.bytes) && sha(pbn.bytes) === sha(bn.bytes));
  // an order that has not shipped has no invoice and no print action
  const placedOnly = await fetch(`${API}/api/v1/orders`, {
    method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${buyerToken}`, 'idempotency-key': key('unshipped') },
    body: JSON.stringify({ lines: [{ variantId: VARIANT_A, quantity: 1 }], companyId: A.companyId, shipToSiteId: A.siteId, payOnCredit: true })
  }).then((x) => x.json());
  await s.goto(`${CONSOLE}/orders/${placedOnly.data.orderId}`, { waitUntil: 'networkidle' });
  await s.getByTestId('order-number').waitFor();
  r.check('an order that has not shipped: no challan button (NOT RUN)', (await s.getByTestId('print-challan').count()) === 0);
  r.check('an order that has not shipped: no invoice block and no print action', (await s.getByTestId('print-mushak').count()) === 0 && (await s.getByTestId('invoice-numbers').count()) === 0);

  // ================= real 503: the stored file of invoice 2 is changed on disk
  await s.goto(`${CONSOLE}/invoices/${I2.id}`, { waitUntil: 'networkidle' });
  await s.getByTestId('invoice-number').waitFor();
  await download(s, 'invoice-pdf-en');
  const file = join(process.env.E2E_DOCS_DIR, 'invoices', I2.id.replace(/-/g, ''), 'en.pdf');
  r.check('setup: the stored file of invoice 2 exists after the first download', existsSync(file), file);
  writeFileSync(file, Buffer.concat([readFileSync(file), Buffer.from('tampered')]));
  await s.getByTestId('invoice-pdf-en').click();
  await s.getByTestId('invoice-pdf-refusal').waitFor();
  r.check('tampered file: 503 said in words ("could not be verified"), no file saved', (await s.getByTestId('invoice-pdf-refusal').innerText()).includes('could not be verified'));
  await axeCheck(s, 'detail with the 503 words');
  await s.goto(`${CONSOLE}/orders/${o2.id}`, { waitUntil: 'networkidle' });
  await s.getByTestId('print-mushak-en').click();
  await s.getByTestId('invoice-pdf-refusal').waitFor();
  r.check('Print Mushak-6.3 on the tampered invoice: the same words', (await s.getByTestId('invoice-pdf-refusal').innerText()).includes('could not be verified'));

  // ================= check a PDF
  await s.goto(`${CONSOLE}/invoices`, { waitUntil: 'networkidle' });
  await s.getByTestId('check-file').waitFor();
  const dir = process.env.E2E_DOCS_DIR;
  writeFileSync(join(dir, 'issued-en.pdf'), en.bytes);
  writeFileSync(join(dir, 'issued-bn.pdf'), bn.bytes);
  writeFileSync(join(dir, 'random.pdf'), Buffer.concat([Buffer.from('%PDF-1.4\n'), randomBytes(2000)]));
  const requests = [];
  s.on('request', (req) => requests.push({ method: req.method(), url: req.url(), body: req.postData(), type: req.headers()['content-type'] ?? '' }));
  const check = async (f) => { requests.length = 0; await s.getByTestId('check-file').setInputFiles(join(dir, f)); await s.getByTestId('check-result').locator('[data-testid^="check-"]').first().waitFor(); };
  await check('issued-en.pdf');
  const hit = (await s.getByTestId('check-match').innerText());
  r.check('check an issued English PDF: says which invoice, kind, English version', hit.includes(I1.number) && hit.includes('English') && hit.includes('Tax invoice'), hit);
  const lookups = requests.filter((q) => q.url.includes('/invoice-documents/'));
  r.check('only the hash was sent: one GET with the SHA-256 in the path, no body, no multipart, no other call', lookups.length === 1 && lookups[0].method === 'GET' && lookups[0].url.endsWith(`/invoice-documents/${sha(en.bytes)}`) && requests.every((q) => !q.body && !q.type.includes('multipart')) && requests.filter((q) => q.method !== 'GET').length === 0);
  await axeCheck(s, 'check result (match)');
  await check('issued-bn.pdf');
  r.check('check the issued Bangla PDF: Bangla version of the same invoice', (await s.getByTestId('check-match').innerText()).includes('Bangla') && (await s.getByTestId('check-match').innerText()).includes(I1.number));
  await check('random.pdf');
  r.check('check a random file: "Not a document we issued", no invoice named', (await s.getByTestId('check-none').innerText()).includes('Not a document we issued') && (await s.getByTestId('check-match').count()) === 0);
  await axeCheck(s, 'check result (not ours)');
  r.check('Support: no page errors', s.errors.length === 0, s.errors.join(' | ').slice(0, 300));
  await s.context().close();

  // ================= 360 px
  const n = await mk(360);
  await consoleSignIn(n, PEOPLE.support);
  await n.goto(`${CONSOLE}/invoices`, { waitUntil: 'networkidle' });
  await n.getByTestId('invoice-row').first().waitFor();
  r.check('360 px list: no horizontal scroll of the page', await noOverflow(n));
  await axeCheck(n, 'list at 360 px');
  await n.goto(`${CONSOLE}/invoices/${I1.id}`, { waitUntil: 'networkidle' });
  await n.getByTestId('invoice-number').waitFor();
  r.check('360 px detail: no horizontal scroll of the page', await noOverflow(n));
  await axeCheck(n, 'detail at 360 px');
  r.check('360 px: no page errors', n.errors.length === 0, n.errors.join(' | ').slice(0, 300));
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
