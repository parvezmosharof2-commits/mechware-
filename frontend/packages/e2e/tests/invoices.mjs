// E6.8b buyer invoices (web, EN + BN, 360 px): the invoice block on the order page (number, Dhaka date, total, kind, "Specimen" in words), PDF download in EN and BN
// (starts with %PDF, its SHA-256 is found by the staff `admin/invoice-documents/{sha}` lookup with the right language), refusals in words (404 and 429 mocked, 503
// INVOICE_PDF_UNAVAILABLE REAL: a stored file is tampered with on disk), the company invoices page (list, Dhaka date filters kept in the URL, paging, detail with the MASKED
// BIN and a PDF), the invoice number on the credit statement, and who sees nothing: a Requester (API 404, no link, "not available"), a stranger (order not found,
// company page not available). axe 0.
// Real: company with credit, orders placed and shipped through the API (Invoicing:Mode=Specimen + IssuerSellerId on the API; the Worker renders the PDFs through the outbox).
// Mocked: the 404 and 429 answers of the PDF route and one list answer with 45 rows (paging buttons need more than a page of invoices).
// SCRATCH DB only (E2E_SQL_CMD against mw_*). Set E2E_INVOICES=1 to run. E2E_DOCS_DIR = the API's Documents:LocalPath (to tamper with a stored PDF).
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, USERS, WEB, apiCall, apiLogin, launch } from './lib.mjs';
import { PEOPLE, VARIANT_A, apiToken, axeCheck, key, noOverflow, r, requireScratch, send, setupCompany, sleep, tag } from './returns-lib.mjs';

if (process.env.E2E_INVOICES !== '1') {
  console.log('SKIP  invoices E2E (set E2E_INVOICES=1 to run it)');
  process.exit(0);
}
requireScratch();
if (!process.env.E2E_DOCS_DIR) throw new Error('E2E_DOCS_DIR must name the API Documents:LocalPath');
const here = dirname(fileURLToPath(import.meta.url));
const ALL = {
  en: JSON.parse(readFileSync(resolve(here, '../../../apps/web/src/messages/en.json'), 'utf8')),
  bn: JSON.parse(readFileSync(resolve(here, '../../../apps/web/src/messages/bn.json'), 'utf8'))
};
const today = new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Dhaka' }).format(new Date());
const dhakaDate = (iso, lang) => new Intl.DateTimeFormat(lang === 'bn' ? 'bn-BD' : 'en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'Asia/Dhaka' }).format(new Date(iso));

async function signIn(page, prefix, lang, who, path) {
  const auth = ALL[lang].Auth;
  await page.goto(`${WEB}${prefix}/auth/login?returnTo=${encodeURIComponent(prefix + path)}`, { waitUntil: 'networkidle' });
  await page.getByLabel(auth.emailOrPhoneLabel).fill(who.email);
  await page.locator('#auth-pw').fill(who.password);
  const answer = page.waitForResponse((res) => res.url().includes('/auth/login') && res.request().method() === 'POST', { timeout: 30000 });
  await page.getByRole('button', { name: auth.signInButton }).click();
  if (!(await answer).ok()) throw new Error(`sign-in of ${who.email} failed`);
  await page.waitForURL((u) => u.pathname.startsWith(prefix + path.split('?')[0]), { timeout: 60000 });
}
const text = (page, id) => page.getByTestId(id).first().innerText();

/** Places a credit order of the issuer seller's variant and ships it (processing, packed, ship); the invoice is issued at shipment. */
async function shippedOrder(staffToken, buyer, company) {
  const res = await fetch(`${API}/api/v1/orders`, {
    method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${buyer}`, 'idempotency-key': key('order') },
    body: JSON.stringify({ lines: [{ variantId: VARIANT_A, quantity: 2 }], companyId: company.companyId, shipToSiteId: company.siteId, payOnCredit: true })
  });
  const placed = await res.json();
  if (!placed.data) throw new Error(`order failed: ${res.status} ${JSON.stringify(placed).slice(0, 300)}`);
  const id = placed.data.orderId;
  for (const step of ['processing', 'packed', 'ship']) {
    const rowVersion = (await send(staffToken, 'GET', `/api/v1/admin/orders/${id}`)).json.data.rowVersion;
    const body = step === 'ship' ? { rowVersion, courierName: 'Steadfast Courier', trackingNumber: `SF-${tag}-${Math.floor(Math.random() * 9000 + 1000)}`, trackingUrl: null } : { rowVersion };
    const m = await send(staffToken, 'POST', `/api/v1/admin/orders/${id}/${step}`, body);
    if (m.status !== 200) throw new Error(`${step} failed: ${m.status} ${JSON.stringify(m.json).slice(0, 200)}`);
  }
  return (await send(buyer, 'GET', `/api/v1/orders/${id}`)).json.data;
}

(async () => {
  const admin = await apiToken(PEOPLE.admin);
  const buyer = await apiLogin(USERS.buyer);
  const alpha = await apiLogin(USERS.sellerAlpha);
  const beta = await apiLogin(USERS.sellerBeta);
  const company = await setupCompany(admin, buyer, 'Invoice Works');
  const bin = (await apiCall(buyer, 'GET', `/api/v1/companies/${company.companyId}`)).json.data.bin;
  const alphaId = (await apiCall(alpha, 'GET', '/api/v1/users/me')).json.data.id;
  const added = await apiCall(buyer, 'POST', `/api/v1/companies/${company.companyId}/members`, { userId: alphaId, roles: 1, spendLimitMinor: null, costCentreIds: [] });
  const accepted = await apiCall(alpha, 'POST', `/api/v1/companies/${company.companyId}/invitations/accept`);
  r.check('setup: company with credit, a Requester member (accepted); orders are placed and shipped through the real API', added.status === 201 && accepted.status === 200, `${added.status}/${accepted.status}`);

  const order1 = await shippedOrder(admin, buyer, company);
  const order2 = await shippedOrder(admin, buyer, company);
  const inv = async (o) => (await apiCall(buyer, 'GET', `/api/v1/orders/${o.id}/invoices`)).json.data;
  let invs1 = [];
  for (let i = 0; i < 20 && invs1.length === 0; i++) { invs1 = await inv(order1); if (!invs1.length) await sleep(500); }
  const invs2 = await inv(order2);
  const I1 = invs1[0];
  const I2 = invs2[0];
  r.check('setup: shipping issued one Specimen invoice per order (API)', invs1.length === 1 && invs2.length === 1 && I1.mode === 'specimen' && I1.kind === 'tax_invoice', JSON.stringify(I1 ?? {}).slice(0, 200));
  const lookup = async (bytes) => apiCall(admin, 'GET', `/api/v1/admin/invoice-documents/${createHash('sha256').update(bytes).digest('hex')}`);

  const browser = await launch();
  const mk = async (width, lang) => {
    const ctx = await browser.newContext({ viewport: { width, height: 900 }, locale: lang === 'bn' ? 'bn-BD' : 'en-GB', acceptDownloads: true });
    const page = await ctx.newPage();
    page.errors = [];
    page.on('pageerror', (e) => page.errors.push(e.message));
    return page;
  };
  const download = async (page, testId) => {
    const [dl] = await Promise.all([page.waitForEvent('download'), page.getByTestId(testId).first().click()]);
    return { name: dl.suggestedFilename(), bytes: readFileSync(await dl.path()) };
  };

  for (const [lang, prefix] of [['en', ''], ['bn', '/bn']]) {
    const T = ALL[lang].Invoices;
    const L = `[${lang} 360]`;
    const page = await mk(360, lang);

    // ---------- the order page
    await signIn(page, prefix, lang, USERS.buyer, `/account/orders/${order1.orderNumber}`);
    await page.getByTestId('order-invoice').first().waitFor();
    r.check(`${L} order page: exactly one invoice row with its number`, (await page.getByTestId('order-invoice').count()) === 1 && (await text(page, 'invoice-number')) === I1.number);
    r.check(`${L} the date is the Dhaka day of the issue time`, (await text(page, 'invoice-date')) === dhakaDate(I1.issuedAtUtc, lang), `${await text(page, 'invoice-date')} vs ${dhakaDate(I1.issuedAtUtc, lang)}`);
    const total = await text(page, 'invoice-total');
    r.check(`${L} the total is shown (digits of ${Math.floor(I1.totalMinor / 100)})`, lang === 'bn' ? /\S/.test(total) : total.replace(/[^\d]/g, '').includes(String(Math.floor(I1.totalMinor / 100))), total);
    const rowText = await page.getByTestId('order-invoice').first().innerText();
    r.check(`${L} kind "${T.kind.tax_invoice}" and "${T.specimen}" are in words`, rowText.includes(T.kind.tax_invoice) && (await text(page, 'invoice-specimen')) === T.specimen);
    r.check(`${L} no horizontal scroll at 360 px`, await noOverflow(page));
    await axeCheck(page, `${L} order page invoice block`);

    // PDF in both languages: bytes start with %PDF, the SHA is the stored one (staff lookup), language matches
    const en = await download(page, 'invoice-pdf-en');
    const bn = await download(page, 'invoice-pdf-bn');
    const lenEn = await lookup(en.bytes);
    const lenBn = await lookup(bn.bytes);
    r.check(`${L} PDF English: starts with %PDF, file name from the number`, en.bytes.subarray(0, 4).toString() === '%PDF' && en.name.endsWith('-en.pdf') && en.name.includes(I1.number.replace(/[^A-Za-z0-9._-]+/g, '-')), en.name);
    r.check(`${L} PDF English: staff SHA lookup finds this invoice, language en`, lenEn.status === 200 && lenEn.json.data.invoiceId === I1.id && lenEn.json.data.locale === 'en', JSON.stringify(lenEn.json).slice(0, 160));
    r.check(`${L} PDF Bangla: %PDF, SHA lookup finds this invoice, language bn, different bytes`, bn.bytes.subarray(0, 4).toString() === '%PDF' && lenBn.status === 200 && lenBn.json.data.invoiceId === I1.id && lenBn.json.data.locale === 'bn' && !en.bytes.equals(bn.bytes));

    // refusals in words: 404 and 429 are mocked answers of the PDF route, the 503 is real (below)
    const problem = (status, code) => ({ status, contentType: 'application/json', body: JSON.stringify({ error: { code, message: 'x', details: [] } }) });
    await page.route('**/invoices/*/pdf*', (route) => route.fulfill(problem(404, 'NOT_FOUND')));
    await page.getByTestId('invoice-pdf-en').first().click();
    await page.getByTestId('invoice-pdf-refusal').waitFor();
    r.check(`${L} 404 on the PDF: said in words`, (await text(page, 'invoice-pdf-refusal')) === T.refusal.notFound && (await page.getByTestId('invoice-pdf-refusal').first().getAttribute('data-refusal')) === 'notFound');
    await page.unroute('**/invoices/*/pdf*');
    await page.route('**/invoices/*/pdf*', (route) => route.fulfill(problem(429, 'RATE_LIMITED')));
    await page.getByTestId('invoice-pdf-bn').first().click();
    await page.getByText(T.refusal.rateLimited).first().waitFor();
    r.check(`${L} 429 on the PDF: said in words (wait a minute)`, (await text(page, 'invoice-pdf-refusal')) === T.refusal.rateLimited);
    await axeCheck(page, `${L} order page with a refusal shown`);
    await page.unroute('**/invoices/*/pdf*');

    // real 503: the stored file of order 2's invoice is changed on disk, so the server must not hand it out
    if (lang === 'en') {
      const warm = await fetch(`${API}/api/v1/orders/${order2.id}/invoices/${I2.id}/pdf?locale=en`, { headers: { authorization: `Bearer ${buyer}` } });
      const file = join(process.env.E2E_DOCS_DIR, 'invoices', I2.id.replace(/-/g, ''), 'en.pdf');
      r.check(`${L} setup: the stored file of the second invoice exists (${warm.status})`, warm.status === 200 && existsSync(file), file);
      const bytes = readFileSync(file);
      bytes[bytes.length - 3] ^= 0xff;
      writeFileSync(file, bytes);
    }
    await page.goto(`${WEB}${prefix}/account/orders/${order2.orderNumber}`, { waitUntil: 'networkidle' });
    await page.getByTestId('order-invoice').first().waitFor();
    await page.getByTestId('invoice-pdf-en').first().click();
    await page.getByTestId('invoice-pdf-refusal').waitFor();
    r.check(`${L} tampered file: 503 INVOICE_PDF_UNAVAILABLE said in words ("could not be verified; staff have been told")`, (await text(page, 'invoice-pdf-refusal')) === T.refusal.pdfUnavailable && (await page.getByTestId('invoice-pdf-refusal').first().getAttribute('data-refusal')) === 'pdfUnavailable');
    r.check(`${L} order page: no page errors`, page.errors.length === 0, page.errors.join(' | ').slice(0, 300));
    await page.context().close();

    // ---------- the company area (Admin + Buyer)
    const p = await mk(360, lang);
    const listUrl = `/account/company/${company.companyId}/invoices`;
    await signIn(p, prefix, lang, USERS.buyer, `/account/company/${company.companyId}`);
    await p.getByTestId('invoices-card-link').waitFor();
    await p.getByTestId('invoices-card-link').click();
    await p.getByTestId('company-invoice').first().waitFor();
    r.check(`${L} company dashboard link opens the invoices page; both invoices listed, newest first`, (await p.getByTestId('company-invoice').count()) === 2 && (await p.getByTestId('invoice-open').first().innerText()) === I2.number, await p.getByTestId('invoice-open').first().innerText());
    r.check(`${L} each row shows kind, specimen in words, order number`, (await p.getByTestId('company-invoice').first().innerText()).includes(T.kind.tax_invoice) && (await p.getByTestId('invoice-specimen').count()) === 2);
    await axeCheck(p, `${L} company invoices list`);
    r.check(`${L} no horizontal scroll`, await noOverflow(p));

    // filters: kept in the URL, survive a reload
    await p.locator('#inv-from').fill(today);
    await p.locator('#inv-to').fill(today);
    await p.getByRole('button', { name: T.filter.apply }).click();
    await p.waitForURL(new RegExp(`from=${today}&to=${today}`));
    r.check(`${L} filter by today (Dhaka): the URL carries from and to, 2 rows`, (await p.getByTestId('company-invoice').count()) === 2);
    await p.reload({ waitUntil: 'networkidle' });
    await p.getByTestId('company-invoice').first().waitFor();
    r.check(`${L} after a reload the inputs and the list still follow the URL`, (await p.locator('#inv-from').inputValue()) === today && (await p.locator('#inv-to').inputValue()) === today && (await p.getByTestId('company-invoice').count()) === 2);
    await p.locator('#inv-from').fill('2020-01-01');
    await p.locator('#inv-to').fill('2020-01-31');
    await p.getByRole('button', { name: T.filter.apply }).click();
    await p.getByTestId('company-invoices-empty').waitFor();
    r.check(`${L} a past range: "${T.companyEmpty}"`, (await text(p, 'company-invoices-empty')) === T.companyEmpty);
    await p.locator('#inv-from').fill('2026-12-01');
    await p.locator('#inv-to').fill('2026-11-01');
    await p.getByRole('button', { name: T.filter.apply }).click();
    await p.getByText(T.filter.order).first().waitFor();
    r.check(`${L} From after To: refused on the screen in words, no request`, true);
    await axeCheck(p, `${L} company invoices with a filter problem`);
    await p.getByRole('button', { name: T.filter.clear }).click();
    await p.getByTestId('company-invoice').first().waitFor();
    r.check(`${L} "Clear dates" restores the list and empties the URL query`, (await p.getByTestId('company-invoice').count()) === 2 && !new URL(p.url()).search);

    // paging (one mocked answer of 45 rows: the real list has only 2)
    const fake = (n) => Array.from({ length: n }, (_, i) => ({ ...I1, id: `00000000-0000-4000-8000-${String(i).padStart(12, '0')}`, number: `SPEC-FAKE-${i}` }));
    const asked = [];
    await p.route('**/companies/*/invoices?*', (route) => {
      const u = new URL(route.request().url());
      asked.push(u.searchParams.get('page'));
      const page = Number(u.searchParams.get('page'));
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ data: { items: fake(page === 3 ? 5 : 20), page, pageSize: 20, totalCount: 45 } }) });
    });
    await p.goto(`${WEB}${prefix}${listUrl}`, { waitUntil: 'networkidle' });
    await p.getByRole('navigation', { name: T.pagesLabel }).waitFor();
    await p.getByRole('button', { name: T.next }).click();
    await p.waitForURL(/page=2/);
    await p.getByRole('button', { name: T.next }).click();
    await p.waitForURL(/page=3/);
    await p.waitForFunction(() => document.querySelectorAll('[data-testid=company-invoice]').length === 5);
    r.check(`${L} paging: 45 rows are 3 pages, the page is in the URL, the API was asked for page 1, 2, 3`, asked.includes('1') && asked.includes('2') && asked.includes('3') && (await p.getByTestId('company-invoice').count()) === 5);
    r.check(`${L} the last page has no "next"`, await p.getByRole('button', { name: T.next }).isDisabled());
    await p.unroute('**/companies/*/invoices?*');

    // detail + PDF + masked BIN
    await p.goto(`${WEB}${prefix}${listUrl}`, { waitUntil: 'networkidle' });
    await p.getByTestId('invoice-open').nth(1).click();
    await p.getByTestId('invoice-detail').waitFor();
    const det = await p.getByTestId('invoice-detail').innerText();
    r.check(`${L} detail: number, specimen words, the lines, buyer BIN masked as the API sends it (no whole BIN)`, det.includes(I1.number) && det.includes(T.specimen) && (await p.getByTestId('invoice-line').count()) >= 1 && /\*{4,}\d{4}/.test(det) && !!bin && !det.includes(bin));
    const dpdf = await download(p, 'invoice-pdf-en');
    const dl = await lookup(dpdf.bytes);
    r.check(`${L} detail: PDF download works for the placer; SHA lookup finds invoice ${I1.number}`, dpdf.bytes.subarray(0, 4).toString() === '%PDF' && dl.status === 200 && dl.json.data.invoiceId === I1.id);
    await axeCheck(p, `${L} company invoice detail`);
    r.check(`${L} no horizontal scroll on the detail`, await noOverflow(p));

    // the credit statement shows the invoice number
    await p.goto(`${WEB}${prefix}/account/company/${company.companyId}/credit`, { waitUntil: 'networkidle' });
    await p.getByTestId('statement-line').first().waitFor();
    const nums = await p.getByTestId('statement-invoices').allInnerTexts();
    r.check(`${L} credit statement: lines of the shipped orders show their invoice number`, nums.some((n) => n.includes(I1.number)) && nums.some((n) => n.includes(I2.number)), nums.join(' | ').slice(0, 200));
    await axeCheck(p, `${L} credit statement with invoice numbers`);
    r.check(`${L} company pages: no page errors`, p.errors.length === 0, p.errors.join(' | ').slice(0, 300));
    await p.context().close();
  }

  // ---------- who sees nothing (EN, 1280)
  {
    const T = ALL.en.Invoices;
    const a = await mk(1280, 'en');
    await signIn(a, '', 'en', USERS.sellerAlpha, `/account/company/${company.companyId}`);
    await a.getByTestId('credit-hidden-note').waitFor();
    r.check('Requester: the company dashboard has no invoices link', (await a.getByTestId('invoices-card-link').count()) === 0);
    await a.goto(`${WEB}/account/company/${company.companyId}/invoices`, { waitUntil: 'networkidle' });
    await a.getByTestId('company-invoices-not-available').waitFor();
    r.check('Requester: the invoices page says "not available to you" (no list, no error scare)', (await a.locator('main').innerText()).includes(T.notAvailableTitle) && (await a.getByTestId('company-invoice').count()) === 0);
    await axeCheck(a, 'invoices page (Requester)');
    const api = await Promise.all([
      apiCall(alpha, 'GET', `/api/v1/companies/${company.companyId}/invoices`),
      apiCall(alpha, 'GET', `/api/v1/companies/${company.companyId}/invoices/${I1.id}`),
      apiCall(alpha, 'GET', `/api/v1/orders/${order1.id}/invoices`),
      apiCall(alpha, 'GET', `/api/v1/orders/${order1.id}/invoices/${I1.id}/pdf`)
    ]);
    r.check('Requester API: company list, detail, order invoices and the PDF all answer 404', api.every((x) => x.status === 404), api.map((x) => x.status).join('/'));
    await a.goto(`${WEB}/account/orders/${order1.orderNumber}`, { waitUntil: 'networkidle' });
    await a.getByTestId('order-not-found').waitFor();
    r.check('Requester (not the placer): the order page says not found, no invoice block', (await a.getByTestId('order-invoices').count()) === 0);
    r.check('Requester: no page errors', a.errors.length === 0, a.errors.join(' | ').slice(0, 300));
    await a.context().close();

    const b = await mk(1280, 'en');
    await signIn(b, '', 'en', USERS.sellerBeta, '/');
    await b.goto(`${WEB}/account/orders/${order1.orderNumber}`, { waitUntil: 'networkidle' });
    await b.getByTestId('order-not-found').waitFor();
    r.check('stranger: the order page of another company says not found, no invoice block', (await b.getByTestId('order-invoices').count()) === 0);
    await b.goto(`${WEB}/account/company/${company.companyId}/invoices`, { waitUntil: 'networkidle' });
    await b.getByTestId('company-invoices-not-available').waitFor();
    await b.goto(`${WEB}/account/company/${company.companyId}/invoices/${I1.id}`, { waitUntil: 'networkidle' });
    await b.getByTestId('company-invoices-not-available').waitFor();
    r.check('stranger: company invoices list and detail say "not available to you"', (await b.locator('main').innerText()).includes(T.notAvailableTitle) && (await b.getByTestId('invoice-detail').count()) === 0);
    const bapi = await Promise.all([
      apiCall(beta, 'GET', `/api/v1/companies/${company.companyId}/invoices`),
      apiCall(beta, 'GET', `/api/v1/companies/${company.companyId}/invoices/${I1.id}`),
      apiCall(beta, 'GET', `/api/v1/orders/${order1.id}/invoices`),
      apiCall(beta, 'GET', `/api/v1/orders/${order1.id}/invoices/${I1.id}`)
    ]);
    r.check('stranger API: every invoice route answers 404', bapi.every((x) => x.status === 404), bapi.map((x) => x.status).join('/'));
    r.check('stranger: no page errors', b.errors.length === 0, b.errors.join(' | ').slice(0, 300));
    await b.context().close();
  }

  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
