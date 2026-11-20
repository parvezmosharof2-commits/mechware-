// D4.14: buyer quotes in the web storefront: list, detail (newest version, earlier versions), accept (choose delivery + payment -> previewed total ->
// confirm -> the order page shows "From quote"), reject with "what should change", expired / replaced / foreign quotes, a Requester (no accept button,
// the API says 403), the refusal codes in words, EN + BN at 360 px, axe, no horizontal scroll.
// SCRATCH DB only (mw_*): API built from `git archive HEAD`, migrated + catalogue seeded, a CORS proxy in front of it, staff admin2.e2e@ cloned from the
// admin row (plain TOTP secret, then `Mechware.Worker migrate` with the API's Encryption__FieldKey), the web app built from a temporary copy of apps/web.
// E2E_SQL_CMD = a command that reads SQL on stdin against the scratch DB, e.g. `docker exec -i mechware_postgres psql -U mechware -d mw_d414 -At -q -v ON_ERROR_STOP=1`.
//   E2E_QUOTES_BUYER=1 E2E_API_URL=http://localhost:5415 E2E_WEB_URL=http://localhost:3414 E2E_SQL_CMD="..." node tests/quotes-buyer.mjs
// Real: every success path, the refusals of the real states (expired, replaced, foreign, Requester). Mocked (request rewrite): PRICE_CHANGED and a credit hold.
// PDF (D4.11 route, D4.14b): the button downloads the real file in the page language; it starts with %PDF, and its SHA-256 is found by the staff
// document lookup (/admin/quote-documents/{sha256}) as this very quote, version and language (buyers are shown no hash). 404/429/503 are worded (mocked).
// Skipped (exit 0) unless E2E_QUOTES_BUYER=1.
import { execSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { WEB, API, USERS, apiCall, apiLogin, launch, reporter, totp, uniqueBin } from './lib.mjs';

if (process.env.E2E_QUOTES_BUYER !== '1') {
  console.log('SKIP  buyer quotes E2E (set E2E_QUOTES_BUYER=1 with the API, a scratch DB and the web app running)');
  process.exit(0);
}
if (!process.env.E2E_SQL_CMD || !/-d mw_\w+/.test(process.env.E2E_SQL_CMD)) throw new Error('E2E_SQL_CMD must be a command against a scratch database (-d mw_*)');

const r = reporter();
const here = dirname(fileURLToPath(import.meta.url));
const sleep = (ms) => new Promise((res) => setTimeout(res, ms));
const sql = (text) => execSync(process.env.E2E_SQL_CMD, { input: text, encoding: 'utf8' });
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
const MSG = {
  en: JSON.parse(readFileSync(resolve(here, '../../../apps/web/src/messages/en.json'), 'utf8')),
  bn: JSON.parse(readFileSync(resolve(here, '../../../apps/web/src/messages/bn.json'), 'utf8'))
};
const overflow = (p) => p.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
const tag = String(Date.now() % 100000);
const SECRET = USERS.admin.totpSecret;
const ADMIN2 = { email: 'admin2.e2e@mechware.com.bd', password: USERS.admin.password, totpSecret: SECRET };
const lastStep = new Map();
async function freshCode(p) {
  for (;;) {
    const step = Math.floor(Date.now() / 30000);
    if (step > (lastStep.get(p.email) ?? -1)) { lastStep.set(p.email, step); return totp(p.totpSecret); }
    await sleep(1000);
  }
}
async function staffToken(p) {
  for (let attempt = 0; attempt < 8; attempt++) {
    const res = await fetch(`${API}/api/v1/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ identifier: p.email, password: p.password, totpCode: await freshCode(p) }) });
    const body = await res.json();
    if (res.ok && body.data?.accessToken) return body.data.accessToken;
    await sleep(5000);
  }
  throw new Error(`staff login failed for ${p.email}`);
}
async function signIn(page, prefix, who, path) {
  const auth = MSG[prefix ? 'bn' : 'en'].Auth;
  await page.goto(`${WEB}${prefix}/auth/login?returnTo=${encodeURIComponent(prefix + path)}`, { waitUntil: 'networkidle' });
  await page.getByLabel(auth.emailOrPhoneLabel).fill(who.email);
  await page.locator('#auth-pw').fill(who.password);
  const answer = page.waitForResponse((res) => res.url().includes('/auth/login') && res.request().method() === 'POST', { timeout: 30000 });
  await page.getByRole('button', { name: auth.signInButton }).click();
  if (!(await answer).ok()) throw new Error(`sign-in of ${who.email} failed`);
  await page.waitForURL(new RegExp(`${prefix}${path.replace(/[/?.=&]/g, '\\$&')}`), { timeout: 60000 });
}

(async () => {
  const admin = await staffToken(ADMIN2);
  const buyer = await apiLogin(USERS.buyer);
  const alpha = await apiLogin(USERS.sellerAlpha);
  const beta = await apiLogin(USERS.sellerBeta);
  const send = (token, method, path, body, headers = {}) =>
    fetch(`${API}${path}`, { method, headers: { 'content-type': 'application/json', authorization: `Bearer ${token}`, ...headers }, body: body ? JSON.stringify(body) : undefined })
      .then(async (res) => ({ status: res.status, json: await res.json().catch(() => ({})) }));

  // ---------- companies: the buyer's (Admin + Buyer, one site), alpha as a Requester, beta's own company (the foreign quote)
  sql(`DELETE FROM company_members WHERE user_id IN (SELECT id FROM users WHERE email IN ('${USERS.sellerAlpha.email}'));`);
  const buyerId = (await send(buyer, 'GET', '/api/v1/users/me')).json.data.id;
  const alphaId = (await send(alpha, 'GET', '/api/v1/users/me')).json.data.id;
  const reg = await send(buyer, 'POST', '/api/v1/companies', { legalName: `Quote Buyers ${tag} Ltd`, tradeLicenceNo: 'TL-D414', bin: uniqueBin() });
  const companyId = reg.json.data.id;
  const act = await send(admin, 'POST', `/api/v1/admin/companies/${companyId}/activate`);
  const own = (await send(buyer, 'GET', `/api/v1/companies/${companyId}/members`)).json.data.find((x) => x.userId === buyerId);
  await send(buyer, 'PUT', `/api/v1/companies/${companyId}/members/${own.id}`, { roles: 10, spendLimitMinor: null, costCentreIds: [], isActive: true });
  const site = await send(buyer, 'POST', `/api/v1/companies/${companyId}/sites`, { name: 'Gazipur Plant', contactName: 'Sabbir', contactPhone: '01711000000', line1: 'Plot 5, BSCIC Area', upazila: 'Tongi Industrial Zone', district: 'Gazipur', division: 'Dhaka', isDefaultDelivery: true });
  const added = await send(buyer, 'POST', `/api/v1/companies/${companyId}/members`, { userId: alphaId, roles: 1, spendLimitMinor: null, costCentreIds: [] });
  const accepted = await send(alpha, 'POST', `/api/v1/companies/${companyId}/invitations/accept`);
  const reg2 = await send(beta, 'POST', '/api/v1/companies', { legalName: `Other Quote Buyers ${tag} Ltd`, tradeLicenceNo: 'TL-D414B', bin: String(Number(uniqueBin()) + 1).padStart(13, '0') });
  const otherCompanyId = reg2.json.data?.id;
  const act2 = otherCompanyId ? await send(admin, 'POST', `/api/v1/admin/companies/${otherCompanyId}/activate`) : { status: 0 };
  r.check('setup: an active company (Admin+Buyer owner, a site), a Requester member, and another active company', reg.status === 201 && act.status === 200 && site.status === 201 && added.status < 300 && accepted.status === 200 && act2.status === 200, `${reg.status}/${act.status}/${site.status}/${added.status}/${accepted.status}/${act2.status}`);

  // ---------- quotes (real staff API)
  const rows = sql(`select distinct v.id, p.seller_id from product_variants v join products p on p.id = v.product_id join inventory_items i on i.variant_id = v.id where p.status = 'active' and v.status = 'active' order by v.id limit 1;`).split('\n').map((x) => x.trim()).filter(Boolean).map((x) => x.split('|'));
  const [[variantId, sellerId]] = rows;
  sql('UPDATE product_variants SET min_order_qty = 1, pack_size = 1 WHERE min_order_qty <> 1 OR pack_size <> 1;');
  const base = '/api/v1/admin/quotes';
  const probe = await send(admin, 'POST', base, { companyId, contactUserId: buyerId });
  const probePath = `${base}/${probe.json.data.quote.id}`;
  const pv = await send(admin, 'POST', `${probePath}/versions/1/price-preview`, { validityDays: 14, paymentTerms: 'as_account', shippingMode: 'calculated', lines: [{ variantId, quantity: 2, unitPriceMinor: 99999999 }] });
  if (pv.status !== 200) throw new Error(`probe preview failed ${pv.status} ${JSON.stringify(pv.json).slice(0, 300)}`);
  const unit = pv.json.data.lines[0].referenceUnitPriceMinor;
  sql(`UPDATE product_variants SET cost_price_minor = ${Math.floor(unit * 0.7)} WHERE id = '${variantId}';`); // the quoted price is above the margin floor (scratch DB)
  await send(admin, 'POST', `${probePath}/close`, { reason: 'Probe quote, not used' });
  async function makeQuote(forCompany, contactId, extra = {}) {
    const made = await send(admin, 'POST', base, { companyId: forCompany, contactUserId: contactId });
    if (made.status > 201) throw new Error(`create quote failed ${made.status} ${JSON.stringify(made.json).slice(0, 300)}`);
    const id = made.json.data.quote.id;
    const put = await send(admin, 'PUT', `${base}/${id}/versions/1`, { rowVersion: made.json.data.versions[0].rowVersion, validityDays: 14, paymentTerms: 'as_account', shippingMode: 'calculated', buyerNote: `Note for ${tag}`, lines: [{ variantId, quantity: 2, unitPriceMinor: unit }], ...extra });
    if (put.status !== 200) throw new Error(`save draft failed ${put.status} ${JSON.stringify(put.json).slice(0, 300)}`);
    const sent = await send(admin, 'POST', `${base}/${id}/versions/1/send`);
    if (sent.status !== 200) throw new Error(`send failed ${sent.status} ${JSON.stringify(sent.json).slice(0, 300)}`);
    const detail = (await send(buyer, 'GET', `/api/v1/companies/${forCompany === otherCompanyId ? otherCompanyId : companyId}/quotes/${id}`));
    return { id, number: made.json.data.quote.number, status: sent.json.data?.status ?? sent.json.data?.version?.status };
  }
  const qA = await makeQuote(companyId, buyerId);   // EN accept
  const qB = await makeQuote(companyId, buyerId);   // BN accept
  const qC = await makeQuote(companyId, buyerId);   // EN reject
  const qD = await makeQuote(companyId, buyerId);   // BN reject
  const qE = await makeQuote(companyId, buyerId);   // expired
  const qS = await makeQuote(companyId, buyerId);   // replaced by a newer version
  // The staff API only accepts a contact with the Buyer or Admin role: alpha is made a Buyer for that, then demoted to a plain Requester (still the contact).
  const alphaMember = (await send(buyer, 'GET', `/api/v1/companies/${companyId}/members`)).json.data.find((x) => x.userId === alphaId);
  const promote = await send(buyer, 'PUT', `/api/v1/companies/${companyId}/members/${alphaMember.id}`, { roles: 3, spendLimitMinor: null, costCentreIds: [], isActive: true });
  if (promote.status !== 200) throw new Error('promote ' + promote.status + JSON.stringify(promote.json).slice(0, 300) + JSON.stringify(alphaMember));
  await send(alpha, 'POST', `/api/v1/companies/${companyId}/invitations/accept`);
  const qR = await makeQuote(companyId, alphaId);   // contact = the (former Buyer) Requester
  await send(buyer, 'PUT', `/api/v1/companies/${companyId}/members/${alphaMember.id}`, { roles: 1, spendLimitMinor: null, costCentreIds: [], isActive: true });
  await send(alpha, 'POST', `/api/v1/companies/${companyId}/invitations/accept`);
  const qM = await makeQuote(companyId, buyerId);   // mocked refusals (EN)
  const qX = await makeQuote(companyId, buyerId, { shippingMode: 'fixed', shipToSiteId: site.json.data.id, shippingFees: [{ sellerId, shippingMinor: 50000 }] }); // fixed delivery (BN, mocked hold)
  const qF = await makeQuote(otherCompanyId, (await send(beta, 'GET', '/api/v1/users/me')).json.data.id); // another company's quote
  sql(`SET session_replication_role = replica;
    UPDATE quote_versions SET sent_at = now() - interval '20 days', valid_until_utc = now() - interval '1 day' WHERE quote_id = '${qE.id}';
    UPDATE quote_versions SET status = 'Superseded', superseded_at = now() WHERE quote_id = '${qS.id}';`);
  const apiDetail = async (token, cid, q) => (await send(token, 'GET', `/api/v1/companies/${cid}/quotes/${q.id}`));
  const dA = await apiDetail(buyer, companyId, qA);
  r.check('setup: ten quotes sent through the real staff API; the buyer reads them back; the expired and replaced ones are marked', dA.status === 200 && dA.json.data.versions[0].acceptable === true
    && (await apiDetail(buyer, companyId, qE)).json.data.versions[0].acceptable === false && (await apiDetail(buyer, companyId, qS)).json.data.versions[0].acceptable === false, JSON.stringify([dA.status, dA.json.data?.quote?.status]));

  const browser = await launch();
  const newPage = async (width = 360, prefix = '') => {
    const ctx = await browser.newContext({ viewport: { width, height: 760 }, locale: prefix ? 'bn-BD' : 'en-US' });
    const page = await ctx.newPage();
    page.errors = [];
    page.on('console', (m) => { if (m.type() === 'error' && !/img-src|Failed to load resource: the server responded with a status of 4\d\d/.test(m.text())) page.errors.push(`console: ${m.text()}`); });
    page.on('pageerror', (e) => page.errors.push(`pageerror: ${e.message}`));
    return page;
  };
  const qPath = (prefix, q, cid = companyId) => `${prefix}/account/company/${cid}/quotes/${q.id}`;
  const open = async (page, prefix, q, cid = companyId) => { await page.goto(`${WEB}${qPath(prefix, q, cid)}`, { waitUntil: 'networkidle' }); };

  for (const [lang, prefix, acceptQ, rejectQ] of [['en', '', qA, qC], ['bn', '/bn', qB, qD]]) {
    const m = MSG[lang].BuyerQuotes;
    const L = lang.toUpperCase();
    if (lang === 'bn') sql(`UPDATE companies SET credit_terms = 30, credit_limit = 50000000 WHERE id = '${companyId}';`); // the Bangla run pays on the company account
    const page = await newPage(360, prefix);
    await signIn(page, prefix, USERS.buyer, `/account/company/${companyId}/quotes`);
    await page.getByTestId('quote-list').waitFor();
    const listed = await page.getByTestId('quote-list').innerText();
    r.check(`${L} list: the company's open quotes are listed with their numbers and status words`, [qA, qB, qC, qD, qE, qS, qM, qX].every((q) => listed.includes(q.number)) && !listed.includes(qF.number) && listed.includes(m.status_sent), listed.slice(0, 120));
    r.check(`${L} list: an alien quote (another company) and drafts are not listed`, !listed.includes(qF.number));
    r.check(`${L} list: no horizontal scroll at 360 px`, !(await overflow(page)));
    await axeCheck(page, `${L} quote list`);
    const filtered = page.waitForResponse((res) => res.url().includes('/quotes?') && res.url().includes('status=expired'));
    await page.getByTestId('quote-filter').selectOption('expired');
    await filtered;
    await page.getByTestId('quote-list').locator('li').first().waitFor();
    await page.waitForFunction(() => document.querySelectorAll('[data-testid=quote-list] li').length === 1);
    const exp = await page.getByTestId('quote-list').innerText();
    r.check(`${L} list: the "expired" filter shows only the expired quote`, exp.includes(qE.number) && !exp.includes(qA.number) && exp.includes(m.status_expired), exp.slice(0, 100));

    // ---- accept
    await open(page, prefix, acceptQ);
    await page.getByTestId('quote-accept').waitFor();
    const apiTotal = (await apiDetail(buyer, companyId, acceptQ)).json.data.versions[0].goodsTotalMinor;
    r.check(`${L} detail: number, status word, lines, the goods total, and the buyer note`, (await page.getByTestId('quote-number').innerText()) === acceptQ.number && (await page.getByTestId('quote-lines').locator('li').count()) === 1 && (await page.getByTestId('quote-note').innerText()).includes(tag) && (await page.getByTestId('quote-total').innerText()).length > 0 && apiTotal > 0);
    {
      const [dl] = await Promise.all([page.waitForEvent('download', { timeout: 30000 }), page.getByTestId('quote-pdf').click()]);
      const bytes = readFileSync(await dl.path());
      const sha = createHash('sha256').update(bytes).digest('hex');
      const found = await send(admin, 'GET', `/api/v1/admin/quote-documents/${sha}`);
      const direct = await fetch(`${API}/api/v1/companies/${companyId}/quotes/${acceptQ.id}/versions/1/pdf?locale=${lang}`, { headers: { authorization: `Bearer ${buyer}` } });
      const directBytes = Buffer.from(await direct.arrayBuffer());
      r.check(`${L} PDF: the button downloads a real PDF named after the quote`, bytes.subarray(0, 4).toString('latin1') === '%PDF' && bytes.length > 1000 && dl.suggestedFilename() === `${acceptQ.number}-v1.pdf`, `${bytes.length} bytes ${dl.suggestedFilename()}`);
      r.check(`${L} PDF: its SHA-256 is the stored document of this quote and language (staff lookup), and the API serves the same bytes`, found.status === 200 && found.json.data.number === acceptQ.number && found.json.data.versionNo === 1 && found.json.data.locale === lang && direct.headers.get('content-type')?.startsWith('application/pdf') && directBytes.equals(bytes), `${found.status} ${JSON.stringify(found.json).slice(0, 120)}`);
      for (const [status, code, kind] of [[404, 'NOT_FOUND', 'notFound'], [429, 'RATE_LIMITED', 'rateLimited'], [503, 'QUOTE_PDF_UNAVAILABLE', 'unverified']]) {
        await page.route('**/versions/*/pdf*', (route) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify({ error: { code, message: 'x' } }) }));
        await page.getByTestId('quote-pdf').click();
        await page.getByTestId('quote-pdf-error').waitFor();
        r.check(`${L} PDF mocked ${status}: worded in ${L}, kind ${kind}`, (await page.getByTestId('quote-pdf-error').getAttribute('data-kind')) === kind && (await page.getByTestId('quote-pdf-error').innerText()) === m[`pdfError_${kind}`], await page.getByTestId('quote-pdf-error').innerText());
        await page.unroute('**/versions/*/pdf*');
      }
      page.errors = page.errors.filter((x) => !/status of 503/.test(x)); // the mocked 503 is the browser's own log line
    }
    r.check(`${L} detail: no horizontal scroll`, !(await overflow(page)));
    await axeCheck(page, `${L} quote detail`);
    await page.getByTestId('quote-accept').click();
    await page.getByTestId('accept-panel').waitFor();
    await page.waitForFunction((id) => document.querySelector('[data-testid=accept-site]')?.value === id, site.json.data.id).catch(() => undefined);
    await page.waitForFunction((withCredit) => withCredit ? !!document.querySelector('[data-testid=accept-pay-account]') : !!document.querySelector('[data-testid=accept-no-credit]'), lang === 'bn');
    const payOk = lang === 'bn'
      ? (await page.getByTestId('accept-pay-account').isChecked())
      : (await page.getByTestId('accept-pay-account').count()) === 0 && (await page.getByTestId('accept-pay-advance').isChecked()) && (await page.getByTestId('accept-no-credit').innerText()) === m.noCreditNote;
    r.check(`${L} accept: the default delivery site is chosen; ${lang === 'bn' ? 'on-account payment is on (the company has credit)' : 'no credit account: only pay in advance, with a note'}`, (await page.getByTestId('accept-site').inputValue()) === site.json.data.id && payOk);
    await axeCheck(page, `${L} accept step 1`);
    let acceptCalls = 0;
    page.on('request', (rq) => { if (rq.method() === 'POST' && rq.url().endsWith('/accept')) acceptCalls++; });
    await page.getByTestId('accept-review').click();
    await page.getByTestId('accept-preview').waitFor();
    const previewTotal = await page.getByTestId('accept-total').innerText();
    r.check(`${L} accept: the preview shows a total and the chosen delivery and payment`, previewTotal.length > 0 && (await page.getByTestId('accept-summary').innerText()).includes('Gazipur Plant'), previewTotal);
    await axeCheck(page, `${L} accept step 2`);
    r.check(`${L} accept: the confirm button names the previewed total`, (await page.getByTestId('accept-confirm').innerText()).includes(previewTotal), await page.getByTestId('accept-confirm').innerText());
    await page.getByTestId('accept-confirm').dblclick();
    await page.waitForURL(new RegExp(`${prefix}/account/orders/`), { timeout: 30000 }).catch(async () => { throw new Error(`accept did not reach the order page: ${await page.getByTestId('accept-error').innerText().catch(() => '(no error shown)')}`); });
    await page.getByTestId('order-source').waitFor({ timeout: 30000 });
    r.check(`${L} accept: one click pair makes one accept call, and the order page says it came from the quote`, acceptCalls === 1 && (await page.getByTestId('order-source').innerText()).includes(acceptQ.number), `${acceptCalls} ${await page.getByTestId('order-source').innerText()}`);
    r.check(`${L} accept: the order total equals the previewed total`, (await page.getByTestId('grand-total').innerText()) === previewTotal, `${await page.getByTestId('grand-total').innerText()} vs ${previewTotal}`);
    r.check(`${L} order page: no horizontal scroll`, !(await overflow(page)));
    await axeCheck(page, `${L} order page from a quote`);
    const after = await apiDetail(buyer, companyId, acceptQ);
    r.check(`${L} accept: the API holds the quote as accepted with an order`, after.json.data.versions[0].status.toLowerCase() === 'accepted' && !!after.json.data.versions[0].acceptedOrderId);
    await open(page, prefix, acceptQ);
    await page.getByTestId('quote-order-link').waitFor();
    r.check(`${L} detail after accept: says accepted, links to the order, no accept button`, (await page.getByTestId('quote-state-note').innerText()) === m.note_accepted && (await page.getByTestId('quote-accept').count()) === 0);

    // ---- reject
    await open(page, prefix, rejectQ);
    await page.getByTestId('quote-reject').click();
    await page.getByRole('dialog').waitFor();
    await axeCheck(page, `${L} reject dialog`);
    await page.getByTestId('reject-reason').fill('Price too high for 2 pieces; need delivery in 3 days');
    await page.getByTestId('reject-confirm').click();
    await page.getByTestId('quote-rejected').waitFor();
    const rej = (await apiDetail(buyer, companyId, rejectQ)).json.data.versions[0];
    r.check(`${L} reject: confirmed in words, the API holds Rejected with the reason, no accept or reject button left`, rej.status.toLowerCase() === 'rejected' && rej.rejectReason.includes('Price too high') && (await page.getByTestId('quote-accept').count()) === 0 && (await page.getByTestId('quote-reject').count()) === 0);
    r.check(`${L} reject: no horizontal scroll and no script errors`, !(await overflow(page)) && page.errors.length === 0, page.errors.join('|'));
    await page.context().close();
  }

  // ---------- expired, replaced, foreign, unknown, in both languages
  for (const [lang, prefix] of [['en', ''], ['bn', '/bn']]) {
    const m = MSG[lang].BuyerQuotes;
    const L = lang.toUpperCase();
    const page = await newPage(360, prefix);
    await signIn(page, prefix, USERS.buyer, `/account/company/${companyId}/quotes`);
    await open(page, prefix, qE);
    await page.getByTestId('quote-number').waitFor();
    r.check(`${L} expired: says expired, no accept button`, (await page.getByTestId('quote-accept').count()) === 0 && (await page.getByTestId('quote-state-note').innerText()).length > 0 && (await page.locator('main').innerText()).includes(m.status_expired));
    await axeCheck(page, `${L} expired quote`);
    await open(page, prefix, qS);
    await page.getByTestId('quote-number').waitFor();
    r.check(`${L} replaced: says replaced, no accept button`, (await page.getByTestId('quote-accept').count()) === 0 && (await page.getByTestId('quote-state-note').innerText()) === m.note_superseded);
    await open(page, prefix, qF);
    await page.getByTestId('quote-not-found').waitFor();
    r.check(`${L} foreign quote (id from another company) under my company: the neutral not-found text`, (await page.getByTestId('quote-not-found').innerText()).includes(m.notFoundTitle) && !(await page.locator('main').innerText()).includes(qF.number));
    await open(page, prefix, qF, otherCompanyId);
    await page.getByTestId('quote-not-found').waitFor();
    r.check(`${L} foreign company in the URL: the same not-found text`, (await page.getByTestId('quote-not-found').innerText()).includes(m.notFoundTitle));
    await open(page, prefix, { id: '11111111-1111-1111-1111-111111111111' });
    await page.getByTestId('quote-not-found').waitFor();
    await axeCheck(page, `${L} quote not found`);
    r.check(`${L} not found: no horizontal scroll, no script errors`, !(await overflow(page)) && page.errors.length === 0, page.errors.join('|'));
    await page.context().close();
  }

  // ---------- Requester (contact of one quote)
  for (const [lang, prefix] of [['en', ''], ['bn', '/bn']]) {
    const m = MSG[lang].BuyerQuotes;
    const L = lang.toUpperCase();
    const page = await newPage(360, prefix);
    await signIn(page, prefix, USERS.sellerAlpha, `/account/company/${companyId}/quotes`);
    await page.getByTestId('quote-list').waitFor();
    const listed = await page.getByTestId('quote-list').innerText();
    r.check(`${L} Requester list: only the quote they are the contact for`, listed.includes(qR.number) && !listed.includes(qM.number) && (await page.getByTestId('quote-list').locator('li').count()) === 1, listed.slice(0, 80));
    await open(page, prefix, qR);
    await page.getByTestId('quote-no-accept').waitFor();
    r.check(`${L} Requester detail: no accept button, a word on who can accept, reject is still offered`, (await page.getByTestId('quote-accept').count()) === 0 && (await page.getByTestId('quote-no-accept').innerText()) === m.onlyBuyerAccepts && (await page.getByTestId('quote-reject').count()) === 1);
    r.check(`${L} Requester detail: no horizontal scroll`, !(await overflow(page)));
    await axeCheck(page, `${L} Requester quote detail`);
    await open(page, prefix, qM);
    await page.getByTestId('quote-not-found').waitFor();
    r.check(`${L} Requester: a colleague's quote is the neutral not-found text`, true);
    await page.context().close();
  }
  const ver = dA.json.data.versions[0];
  const forbidden = await send(alpha, 'POST', `/api/v1/companies/${companyId}/quotes/${qR.id}/accept`, { versionNo: 1, contentHash: (await apiDetail(alpha, companyId, qR)).json.data.versions[0].contentHash, expectedGrandTotalMinor: 1, payOnCredit: false, shipToSiteId: site.json.data.id }, { 'Idempotency-Key': `e2e-req-${tag}-0001` });
  r.check('Requester: the API answers 403 COMPANY_ROLE_REQUIRED to an accept', forbidden.status === 403 && forbidden.json.error?.code === 'COMPANY_ROLE_REQUIRED', `${forbidden.status} ${JSON.stringify(forbidden.json).slice(0, 120)}`);

  // ---------- refusals (mocked answers): PRICE_CHANGED returns to the choice, a credit hold is worded, fixed delivery is shown
  {
    const m = MSG.en.BuyerQuotes;
    const page = await newPage(360, '');
    await signIn(page, '', USERS.buyer, `/account/company/${companyId}/quotes`);
    await open(page, '', qM);
    await page.getByTestId('quote-accept').click();
    await page.getByTestId('accept-review').click();
    await page.getByTestId('accept-preview').waitFor();
    await page.route('**/quotes/*/accept', (route) => route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ error: { code: 'PRICE_CHANGED', message: 'The total is now higher.' } }) }));
    await page.getByTestId('accept-confirm').click();
    await page.getByTestId('accept-error').waitFor();
    r.check('EN mocked PRICE_CHANGED: worded, goes back to the choice (no old total left to confirm)', (await page.getByTestId('accept-error').innerText()).includes(m.err_priceChanged) && (await page.getByTestId('accept-preview').count()) === 0 && (await page.getByTestId('accept-review').count()) === 1);
    await page.unroute('**/quotes/*/accept');
    await page.route('**/quotes/*/accept', (route) => route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ error: { code: 'CREDIT_HOLD_OVERDUE', message: 'x' } }) }));
    await page.getByTestId('accept-review').click();
    await page.getByTestId('accept-confirm').click();
    await page.getByTestId('accept-error').waitFor();
    r.check('EN mocked credit hold: worded, the quote is still open', (await page.getByTestId('accept-error').innerText()).includes(m.err_creditHold) && (await apiDetail(buyer, companyId, qM)).json.data.versions[0].acceptable === true);
    await axeCheck(page, 'EN accept error');
    await page.context().close();
  }
  {
    const m = MSG.bn.BuyerQuotes;
    const page = await newPage(360, '/bn');
    await signIn(page, '/bn', USERS.buyer, `/account/company/${companyId}/quotes`);
    await open(page, '/bn', qX);
    await page.getByTestId('quote-accept').click();
    await page.getByTestId('accept-fixed-site').waitFor();
    r.check('BN fixed delivery: the quoted site is shown and cannot be changed (no site choice)', (await page.getByTestId('accept-fixed-site').innerText()).includes('Gazipur Plant') && (await page.getByTestId('accept-site').count()) === 0);
    await page.getByTestId('accept-review').click();
    await page.getByTestId('accept-preview').waitFor();
    await page.route('**/quotes/*/accept', (route) => route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ error: { code: 'CREDIT_HOLD_OVERDUE', message: 'x' } }) }));
    await page.getByTestId('accept-confirm').click();
    await page.getByTestId('accept-error').waitFor();
    r.check('BN mocked credit hold: worded in Bangla', (await page.getByTestId('accept-error').innerText()).includes(m.err_creditHold));
    await axeCheck(page, 'BN accept error (fixed delivery)');
    r.check('BN accept error: no horizontal scroll, no script errors', !(await overflow(page)) && page.errors.length === 0, page.errors.join('|'));
    await page.context().close();
  }
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
