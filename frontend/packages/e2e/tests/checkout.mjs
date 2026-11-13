// E3.3 + E3.4 checkout gate: sign-in guard, delivery step (address book, new address, company site + cost centre + PO number),
// review step (shipping quote, 422 outcomes, server prices only), placing with an Idempotency-Key (kept on retry, new after a
// definitive failure, one request on double click), approval outcome, axe, 360 and 1280 px, en + bn.
//
// Needs the API (any database with the demo catalogue seed, and the Development demo accounts) and the web app built with
// NEXT_PUBLIC_API_BASE_URL pointing at it. The API allows CORS only from localhost:3000/3001, so run the web app on one of them.
// Skipped (exit 0) unless E2E_CHECKOUT=1. Creates (and cancels) real orders, companies and one address: use a scratch database.
//   E2E_CHECKOUT=1 E2E_API_URL=http://localhost:5600 E2E_WEB_URL=http://localhost:3000 node tests/checkout.mjs
// With E2E_CHECKOUT_ZONES=1 it also checks the real 422 answers; that needs shipping zones for Gazipur only (no zone for
// Chattogram), a heavy-unit threshold of 20 kg, and `bd_locations` rows for the districts, as set up by hand in the scratch DB.
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { WEB, API, SHOTS, USERS, apiCall, apiLogin, launch, totp, uniqueBin } from './lib.mjs';

if (process.env.E2E_CHECKOUT !== '1') {
  console.log('SKIP  checkout E2E (set E2E_CHECKOUT=1 with the API, a seeded catalogue and the web app running)');
  process.exit(0);
}

const here = dirname(fileURLToPath(import.meta.url));
function findAxe() {
  const ui = resolve(here, '../../ui/node_modules/axe-core/axe.min.js');
  if (existsSync(ui)) return ui;
  const pnpm = resolve(here, '../../../node_modules/.pnpm');
  const d = readdirSync(pnpm).find((n) => n.startsWith('axe-core@'));
  if (d) return join(pnpm, d, 'node_modules/axe-core/axe.min.js');
  throw new Error('axe-core not found');
}
const AXE = findAxe();
const ALL = {
  en: JSON.parse(readFileSync(resolve(here, '../../../apps/web/src/messages/en.json'), 'utf8')),
  bn: JSON.parse(readFileSync(resolve(here, '../../../apps/web/src/messages/bn.json'), 'utf8'))
};
const MSG = { en: ALL.en.Checkout, bn: ALL.bn.Checkout };
const VARIANT_A = process.env.E2E_VARIANT_A ?? '9cf9a430-ddd9-42dd-929f-f32f00b0c0bd'; // MW-HAG-MBN132-4P
const VARIANT_B = process.env.E2E_VARIANT_B ?? '426401c0-76c0-463b-bf68-cf0eacd8109e'; // MW-DAN-KP1, another seller
const VARIANT_HEAVY = process.env.E2E_VARIANT_HEAVY ?? '4c13a85c-17b9-4cf4-a497-8b1a5c6e8d7e'; // 24 kg
const VIEWPORTS = { m360: { width: 360, height: 740 }, d1280: { width: 1280, height: 800 } };
const ZONES = process.env.E2E_CHECKOUT_ZONES === '1';

const results = [];
const check = (name, ok, extra = '') => {
  results.push(ok);
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${extra ? '  -> ' + extra : ''}`);
};
async function axeCheck(page, label) {
  await page.evaluate(() => window.scrollTo(0, 0)); // the sticky translucent header would otherwise sit over page content
  await page.addScriptTag({ path: AXE });
  const r = await page.evaluate(async () => {
    const out = await window.axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice'] } });
    return out.violations.map((v) => ({ id: v.id, impact: v.impact, nodes: v.nodes.slice(0, 3).map((n) => n.target.join(' ')) }));
  });
  check(`axe 0 violations: ${label}`, r.length === 0, r.length ? JSON.stringify(r) : '');
}
// A required field's label ends with an asterisk; match the text exactly apart from it.
const escapeRe = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const byLabel = (page, text) => page.getByLabel(new RegExp(`^${escapeRe(text)}\\*?$`));
const noOverflow = (page) => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth);

// ----- API-side fixtures (the browser then sees them as the same signed-in user) -----
const buyerToken = await apiLogin(USERS.buyer);
const call = (m, p, b) => apiCall(buyerToken, m, p, b);
let adminCache = null;
async function adminToken() {
  // One sign-in only: the server refuses to accept the same authenticator code twice.
  if (adminCache) return adminCache;
  // A code from the previous minute may already have been used by an earlier run, so the neighbouring windows are tried too.
  for (const offset of [0, 30_000, -30_000]) {
    const r = await fetch(`${API}/api/v1/auth/login`, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ identifier: USERS.admin.email, password: USERS.admin.password, totpCode: await totp(USERS.admin.totpSecret, Date.now() + offset) })
    });
    const token = (await r.json()).data?.accessToken;
    if (token) return (adminCache = token);
  }
  throw new Error('admin sign-in with TOTP failed');
}
async function newCompany(name, approvals) {
  const admin = await adminToken();
  const reg = await call('POST', '/api/v1/companies', { legalName: name, tradeLicenceNo: 'TL-E33', bin: uniqueBin() });
  const id = reg.json.data.id;
  await apiCall(admin, 'POST', `/api/v1/admin/companies/${id}/activate`);
  // The founder is a company admin only; ordering needs the Buyer role too (Admin 8 + Buyer 2).
  const me = (await call('GET', '/api/v1/users/me')).json.data.id;
  const members = (await call('GET', `/api/v1/companies/${id}/members`)).json.data;
  const own = members.find((x) => x.userId === me);
  await call('PUT', `/api/v1/companies/${id}/members/${own.id}`, { roles: 10, spendLimitMinor: null, costCentreIds: [], isActive: true });
  const site = (n, def) => call('POST', `/api/v1/companies/${id}/sites`, { name: n, contactName: 'Sabbir', contactPhone: '01711000000', line1: 'Plot 5, BSCIC Area', upazila: 'Tongi Industrial Zone', district: 'Gazipur', division: 'Dhaka', isDefaultDelivery: def });
  await site('Gazipur Plant', true);
  await site('Spare Depot', false);
  const cc = await call('POST', `/api/v1/companies/${id}/cost-centres`, { code: 'PROD', name: 'Production' });
  if (approvals) {
    // Nobody approves their own request, so a second user (a demo seller account) joins as the approver and accepts the invitation.
    const alpha = await apiLogin(USERS.sellerAlpha);
    const alphaId = (await apiCall(alpha, 'GET', '/api/v1/users/me')).json.data.id;
    await call('POST', `/api/v1/companies/${id}/members`, { userId: alphaId, roles: 4, costCentreIds: [] });
    await apiCall(alpha, 'POST', `/api/v1/companies/${id}/invitations/accept`);
    await call('POST', `/api/v1/companies/${id}/approvals/rules`, { name: 'Everything', minAmountMinor: 1, costCentreId: null, categoryIds: [], approverUserIds: [alphaId], isActive: true });
  }
  return { id, name, costCentreId: cc.json.data?.id };
}
async function fillCart(lines = [[VARIANT_A, 2], [VARIANT_B, 1]]) {
  await call('DELETE', '/api/v1/cart');
  for (const [variantId, quantity] of lines) await call('POST', '/api/v1/cart/items', { variantId, quantity });
}
async function cancelOpenOrders() {
  // Unpaid orders hold stock and count against the buyer's limit of three; leave nothing behind.
  const placed = [];
  return { track: (id) => placed.push(id), done: async () => { for (const id of placed.splice(0)) await call('POST', `/api/v1/orders/${id}/cancel`, { reason: 'e2e cleanup' }); } };
}
const cleanup = await cancelOpenOrders();

async function signIn(page, prefix, path = '/checkout') {
  const auth = ALL[prefix ? 'bn' : 'en'].Auth;
  await page.goto(`${WEB}${prefix}/auth/login?returnTo=${encodeURIComponent(prefix + path)}`, { waitUntil: 'networkidle' });
  await page.getByLabel(auth.emailOrPhoneLabel).fill(USERS.buyer.email);
  await page.locator('#auth-pw').fill(USERS.buyer.password);
  await page.getByRole('button', { name: auth.signInButton }).click();
  await page.waitForURL(new RegExp(`${prefix}${path.replace(/\//g, '\\/')}`), { timeout: 60000 });
}
const jsonBody = (status, code, message = 'x', details = []) => ({ status, contentType: 'application/json', body: JSON.stringify({ status, error: { code, message, details } }) });

(async () => {
  const browser = await launch();
  const companyA = await newCompany(`E33 Mills ${Date.now() % 100000} Ltd`, false);
  const companyB = await newCompany(`E33 Approval ${Date.now() % 100000} Co`, true);

  // Signed out: /checkout goes to sign-in and comes back (checked once; the middleware is locale-agnostic)
  {
    const ctx = await browser.newContext();
    const p = await ctx.newPage();
    await p.goto(`${WEB}/checkout`);
    await p.waitForURL(/auth\/login/);
    check('signed-out /checkout redirects to login with returnTo', /returnTo=%2Fcheckout/.test(p.url()), p.url());
    await ctx.close();
  }

  let firstPass = true;
  for (const lname of ['en', 'bn']) {
    const m = MSG[lname];
    const prefix = lname === 'bn' ? '/bn' : '';
    for (const [vname, vp] of Object.entries(VIEWPORTS)) {
      const tag = `${lname} ${vp.width}px`;
      const ctx = await browser.newContext({ viewport: vp, locale: lname === 'bn' ? 'bn-BD' : 'en-US' });
      const page = await ctx.newPage();
      const errors = [];
      page.on('pageerror', (e) => errors.push(`${page.url()} ${e.message.slice(0, 80)}`));
      await fillCart();

      // Empty cart goes back to the cart; then the filled one stays on checkout
      await call('DELETE', '/api/v1/cart');
      await signIn(page, prefix);
      await page.waitForURL(new RegExp(`${prefix}/cart$`), { timeout: 30000 });
      check(`[${tag}] empty cart redirects /checkout to /cart`, true);
      await fillCart();
      await page.goto(`${WEB}${prefix}/checkout`, { waitUntil: 'networkidle' });

      // Step 1: delivery, personal
      await page.getByRole('heading', { level: 1, name: m.title }).waitFor();
      check(`[${tag}] checkout noindex, no JSON-LD`, (await page.locator('meta[name="robots"][content*="noindex"]').count()) === 1 && (await page.locator('script[type="application/ld+json"]').count()) === 0);
      await page.getByRole('heading', { level: 2, name: m.deliveryAddress }).waitFor();
      const radios = page.locator('input[name="address"]');
      check(`[${tag}] saved addresses are listed and one is chosen`, (await radios.count()) >= 1 && (await page.locator('input[name="address"]:checked').count()) === 1);
      check(`[${tag}] step list marks the current step`, (await page.locator('ol[aria-label] li[aria-current="step"]').count()) === 1);
      check(`[${tag}] delivery no horizontal overflow`, await noOverflow(page));
      await axeCheck(page, `${tag} /checkout delivery`);

      // New address form: validation, then (first pass only) a real save
      await page.getByRole('button', { name: m.addAddress }).click();
      await page.getByRole('button', { name: m.saveAddress }).click();
      check(`[${tag}] address form shows required errors`, (await page.getByText(m.required).count()) >= 4);
      await byLabel(page, m.fieldPhone).fill('12345');
      await page.getByRole('button', { name: m.saveAddress }).click();
      check(`[${tag}] address form rejects a non-BD mobile`, (await page.getByText(m.invalidPhone).count()) === 1);
      await page.locator('#addr-division').selectOption({ index: 1 });
      await page.waitForFunction(() => document.querySelectorAll('#addr-district option').length > 1);
      const districtCount = await page.locator('#addr-district option').count();
      check(`[${tag}] districts load after choosing a division`, districtCount > 1, String(districtCount));
      await axeCheck(page, `${tag} /checkout address form`);
      check(`[${tag}] address form no horizontal overflow`, await noOverflow(page));
      if (firstPass) {
        await byLabel(page, m.fieldLabel).fill('E2E Depot');
        await byLabel(page, m.fieldRecipient).fill('Test Receiver');
        await byLabel(page, m.fieldPhone).fill('+8801712345678');
        await byLabel(page, m.fieldLine1).fill('Plot 9, Test Road');
        await page.locator('#addr-division').selectOption({ index: 1 });
        await page.waitForFunction(() => document.querySelectorAll('#addr-district option').length > 1);
        await page.locator('#addr-district').selectOption({ index: 1 });
        // the upazila field is a select once its list loaded, or a text field when the district has none seeded
        await page.waitForTimeout(1500);
        if ((await page.locator('#addr-upazila option').count()) > 1) await page.locator('#addr-upazila').selectOption({ index: 1 });
        else await byLabel(page, m.fieldUpazila).fill('Test Upazila');
        const before = await radios.count();
        await page.getByRole('button', { name: m.saveAddress }).click();
        await page.waitForFunction((n) => document.querySelectorAll('input[name="address"]').length > n, before, { timeout: 20000 }).catch(() => undefined);
        check(`[${tag}] new address is saved, listed and selected`, (await radios.count()) === before + 1 && (await page.locator('input[name="address"]:checked + span', { hasText: 'E2E Depot' }).count()) === 1);
        firstPass = false;
      } else {
        await page.getByRole('button', { name: m.cancel }).click();
      }

      // Select the seeded default address (first radio), continue
      await radios.first().check();
      await page.getByRole('button', { name: m.continueToReview }).click();

      // Step 3: review
      await page.getByRole('heading', { level: 2, name: m.summary }).waitFor();
      await page.getByText(m.finalTotalNote).waitFor();
      const placeBtn = page.getByRole('button', { name: m.placeOrder, exact: true });
      await placeBtn.waitFor();
      await page.waitForFunction((t) => !document.body.innerText.includes(t), m.quoting, { timeout: 20000 });
      const asideText = await page.locator('aside').innerText();
      check(`[${tag}] review shows subtotal, shipping and the final-total note`, asideText.includes(m.subtotal) && asideText.includes(m.shipping) && asideText.includes(m.finalTotalNote));
      check(`[${tag}] review URL carries step and address (refresh-safe)`, /step=review/.test(page.url()) && /[?&]a=/.test(page.url()), page.url());
      check(`[${tag}] two sellers: shipping per seller is listed`, (await page.locator('aside p', { hasText: new RegExp(m.shippingSeller.replace('{n}', '\\S+')) }).count()) === 2 || !ZONES);
      if (lname === 'bn') check(`[${tag}] review uses Bangla digits`, /[০-৯]/.test(await page.locator('main').innerText()));
      check(`[${tag}] review no horizontal overflow`, await noOverflow(page));
      check(`[${tag}] Place order enabled once the quote is ok`, await placeBtn.isEnabled());
      await axeCheck(page, `${tag} /checkout review`);
      await page.screenshot({ path: `${SHOTS}/checkout-review-${lname}-${vname}.png` });

      // Refresh keeps the step
      await page.reload({ waitUntil: 'networkidle' });
      await page.getByRole('heading', { level: 2, name: m.summary }).waitFor();
      check(`[${tag}] refresh on review stays on review`, /step=review/.test(page.url()));

      // 422 outcomes (mocked: the real ones need zones, see E2E_CHECKOUT_ZONES)
      const outcomes = [
        ['SHIPPING_NOT_AVAILABLE', 'err_notDeliverable'],
        ['SHIPPING_QUOTE_ONLY', 'err_quoteOnly', [{ field: 'heavy', message: 'MW-WEG-WG2022KWI40' }]],
        ['VARIANT_WEIGHT_MISSING', 'err_weightMissing', [{ field: 'weight', message: 'MW-HAG-MBN132-4P' }]]
      ];
      for (const [code, key, details] of outcomes) {
        await page.route('**/api/v1/orders/shipping-quote', (route) => route.fulfill(jsonBody(422, code, 'x', details ?? [])));
        await page.reload({ waitUntil: 'networkidle' });
        await page.getByText(m[key], { exact: true }).waitFor();
        const sub = details ? (await page.locator('aside [role=alert]').innerText()).includes(details[0].message) : true;
        check(`[${tag}] ${code} shows plain text${details ? ' and the SKU' : ''}; Place order stays disabled`, sub && (await placeBtn.isDisabled()));
        if (code === 'SHIPPING_QUOTE_ONLY') {
          check(`[${tag}] quote-only mentions requesting a quote`, (await page.getByText(m.quoteOnlyHint).count()) === 1);
          await axeCheck(page, `${tag} /checkout quote-only error`);
        }
        await page.unroute('**/api/v1/orders/shipping-quote');
      }
      await page.route('**/api/v1/orders/shipping-quote', (route) => route.fulfill(jsonBody(503, 'X')));
      await page.reload({ waitUntil: 'networkidle' });
      await page.getByText(m.err_unavailable, { exact: true }).waitFor();
      check(`[${tag}] quote 503 shows retry and keeps Place order disabled`, (await placeBtn.isDisabled()) && (await page.getByRole('button', { name: m.retry }).count()) === 1);
      await page.unroute('**/api/v1/orders/shipping-quote');
      await page.getByRole('button', { name: m.retry }).click();
      await page.waitForFunction((t) => !document.querySelector('aside [role=alert]') && !document.body.innerText.includes(t), m.quoting);
      check(`[${tag}] retry recovers the quote`, await placeBtn.isEnabled());

      // Placing: 503 first (key kept), 409 stock (new key), then a real order; double click sends one request
      const keys = [];
      let postCount = 0;
      let mode = 'down';
      await page.route('**/api/v1/orders', async (route) => {
        if (route.request().method() !== 'POST') return route.continue();
        postCount++;
        keys.push(route.request().headers()['idempotency-key']);
        if (mode === 'down') return route.fulfill(jsonBody(503, 'X'));
        if (mode === 'stock') return route.fulfill(jsonBody(409, 'INSUFFICIENT_STOCK', 'x'));
        if (mode === 'reused') return route.fulfill(jsonBody(409, 'IDEMPOTENCY_KEY_REUSED', 'x'));
        return route.continue();
      });
      await placeBtn.dblclick();
      await page.getByText(m.err_unavailable, { exact: true }).waitFor();
      check(`[${tag}] double click sends one POST /orders`, postCount === 1, String(postCount));
      check(`[${tag}] 503 on place: cart kept, safe-retry hint, button usable again`, (await page.getByText(m.retrySafe).count()) === 1 && (await placeBtn.isEnabled()));
      check(`[${tag}] attempt key is in the URL and valid`, /[?&]k=[A-Za-z0-9_-]{8,100}/.test(page.url()));
      mode = 'stock';
      await placeBtn.click();
      await page.getByText(m.err_insufficientStock, { exact: true }).waitFor();
      check(`[${tag}] retry after a 503 reused the same Idempotency-Key`, keys.length === 2 && keys[0] === keys[1], keys.join(' '));
      mode = 'reused';
      await placeBtn.click();
      await page.getByText(m.err_keyReused, { exact: true }).waitFor();
      check(`[${tag}] after a definitive failure (409 stock) the next attempt has a new key`, keys.length === 3 && keys[2] !== keys[1], keys.join(' '));
      check(`[${tag}] cart survived the errors`, (await call('GET', '/api/v1/cart')).json.data.lines.length === 2);
      mode = 'real';
      await placeBtn.click();
      await page.waitForURL(new RegExp(`${prefix}/checkout/confirmation\\?order=`), { timeout: 30000 });
      check(`[${tag}] after the 409s a fresh key placed the order`, keys.length === 4 && keys[3] !== keys[2], keys.join(' '));
      const orderNumber = new URL(page.url()).searchParams.get('order');
      const orderId = new URL(page.url()).searchParams.get('id');
      cleanup.track(orderId);
      await page.getByTestId('order-number').waitFor();
      await page.getByText(m.status_pending_payment).waitFor();
      check(`[${tag}] confirmation shows the order number and status`, (await page.getByTestId('order-number').innerText()) === orderNumber && (await page.getByText(m.status_pending_payment).count()) === 1, orderNumber);
      check(`[${tag}] confirmation no horizontal overflow`, await noOverflow(page));
      await axeCheck(page, `${tag} /checkout/confirmation`);
      await page.unroute('**/api/v1/orders');
      const order = (await call('GET', `/api/v1/orders/${orderId}`)).json.data;
      check(`[${tag}] placed order has server prices and no PO number`, order.status === 'pending_payment' && order.purchaseOrderNumber == null && order.grandTotalMinor > 0, `${order.grandTotalMinor}`);
      check(`[${tag}] cart is emptied after the order`, (await call('GET', '/api/v1/cart')).json.data.lines.length === 0);
      await cleanup.done();

      // Company order: site + cost centre + PO number, validated, sent with the order
      await fillCart();
      await page.goto(`${WEB}${prefix}/checkout`, { waitUntil: 'networkidle' });
      await page.getByRole('heading', { level: 2, name: m.deliveryAddress }).waitFor();
      await page.getByLabel(m.companyOrder.replace('{name}', companyA.name)).check();
      await page.getByRole('heading', { level: 2, name: m.deliverySite }).waitFor();
      await page.locator('input[name="site"]:checked').waitFor();
      await page.locator('#co-cc').waitFor().catch(() => undefined);
      check(`[${tag}] company: default site is preselected`, (await page.locator('input[name="site"]:checked + span', { hasText: 'Gazipur Plant' }).count()) === 1);
      check(`[${tag}] company: cost centre select is offered`, (await page.locator('#co-cc option').count()) === 2);
      await page.locator('#co-cc').selectOption({ index: 1 });
      await byLabel(page, m.poNumber).fill('PO=1<>');
      check(`[${tag}] PO number with bad characters is flagged`, (await page.getByText(m.poBadCharacters).count()) === 1);
      await page.getByRole('button', { name: m.continueToReview }).click();
      check(`[${tag}] invalid PO number blocks Continue`, !/step=review/.test(page.url()) && (await page.getByText(m.needSite).count()) === 1);
      await byLabel(page, m.poNumber).fill('PO-2026/11 #7');
      check(`[${tag}] company delivery no horizontal overflow`, await noOverflow(page));
      await axeCheck(page, `${tag} /checkout company delivery`);
      await page.getByRole('button', { name: m.continueToReview }).click();
      await page.getByRole('heading', { level: 2, name: m.summary }).waitFor();
      check(`[${tag}] review shows the PO number`, (await page.getByText('PO-2026/11 #7').count()) === 1);
      await page.waitForFunction((t) => !document.body.innerText.includes(t), m.quoting);
      await page.getByRole('button', { name: m.placeOrder, exact: true }).click();
      await page.waitForURL(/checkout\/confirmation/, { timeout: 30000 }).catch(async () => console.log('company place failed:', await page.locator('aside [role=alert]').allInnerTexts()));
      const idA = new URL(page.url()).searchParams.get('id');
      cleanup.track(idA);
      const orderA = (await call('GET', `/api/v1/orders/${idA}`)).json.data;
      check(`[${tag}] company order carries the PO number`, orderA.purchaseOrderNumber === 'PO-2026/11 #7' && orderA.status === 'pending_payment', JSON.stringify([orderA.purchaseOrderNumber, orderA.status]));
      await cleanup.done();

      // Company with an approval rule: the order is sent for approval
      await fillCart();
      await page.goto(`${WEB}${prefix}/checkout`, { waitUntil: 'networkidle' });
      await page.getByLabel(m.companyOrder.replace('{name}', companyB.name)).check();
      await page.locator('input[name="site"]').first().waitFor();
      await page.getByRole('button', { name: m.continueToReview }).click();
      await page.getByRole('heading', { level: 2, name: m.summary }).waitFor();
      await page.waitForFunction((t) => !document.body.innerText.includes(t), m.quoting);
      await page.getByRole('button', { name: m.placeOrder, exact: true }).click();
      await page.waitForURL(/checkout\/confirmation/, { timeout: 30000 }).catch(async () => console.log('approval place failed:', await page.locator('aside [role=alert]').allInnerTexts()));
      await page.getByText(m.sentForApproval).waitFor();
      check(`[${tag}] approval-required order shows the sent-for-approval state`, (await page.getByText(m.status_pending_approval).count()) === 1);
      cleanup.track(new URL(page.url()).searchParams.get('id'));
      await axeCheck(page, `${tag} /checkout/confirmation approval`);
      await cleanup.done();

      // Real 422s from shipping zones (scratch database set up by hand)
      if (ZONES) {
        const addrs = (await call('GET', '/api/v1/users/me/addresses')).json.data;
        const chat = addrs.find((a) => a.district === 'Chattogram') ?? (await call('POST', '/api/v1/users/me/addresses', { label: 'E2E Chattogram', recipientName: 'T', phone: '+8801712345678', line1: 'Port', division: 'Chattogram', district: 'Chattogram', upazila: 'Chattogram EPZ (Halishahar)', isDefaultShipping: false, isDefaultBilling: false })).json.data;
        await fillCart();
        await page.goto(`${WEB}${prefix}/checkout?a=${chat.id}&step=review`, { waitUntil: 'networkidle' });
        await page.getByText(m.err_notDeliverable, { exact: true }).waitFor();
        check(`[${tag}] real zones: district without a zone gives the not-deliverable text`, await page.getByRole('button', { name: m.placeOrder, exact: true }).isDisabled());
        await fillCart([[VARIANT_HEAVY, 1]]);
        const gaz = addrs.find((a) => a.district === 'Gazipur');
        await page.goto(`${WEB}${prefix}/checkout?a=${gaz.id}&step=review`, { waitUntil: 'networkidle' });
        await page.getByText(m.err_quoteOnly, { exact: true }).waitFor();
        check(`[${tag}] real zones: heavy item gives the quote-only text with its SKU`, (await page.locator('aside [role=alert]').innerText()).includes('MW-WEG-WG2022KWI40'));
        await fillCart();
        await page.goto(`${WEB}${prefix}/checkout?a=${gaz.id}&step=review`, { waitUntil: 'networkidle' });
        await page.waitForFunction((t) => !document.body.innerText.includes(t), m.quoting);
        check(`[${tag}] real zones: a served district gets a priced quote`, !(await page.locator('aside [role=alert]').count()) && (await page.locator('aside').innerText()).includes(m.shipping));
      }

      check(`[${tag}] no page errors`, errors.length === 0, errors.join(' | '));
      await ctx.close();
    }
  }
  await call('DELETE', '/api/v1/cart');
  await cleanup.done();
  await browser.close();
  const failed = results.filter((r) => !r).length;
  console.log(`\n${results.length - failed}/${results.length} passed`);
  process.exit(failed ? 1 : 0);
})().catch(async (e) => { console.error(e); await cleanup.done().catch(() => undefined); process.exit(1); });
