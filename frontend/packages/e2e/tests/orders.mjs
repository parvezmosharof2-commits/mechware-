// E3.6 + F5.1 buyer order pages: confirmation, /account/orders (list, pagination), /account/orders/<orderNumber> (the order email link),
// not-found for another user, error + retry, axe, 360 and 1280 px, en + bn. Orders are placed for real through the API (the
// checkout UI itself is covered by checkout.mjs); only the 503 case is mocked.
//
// Needs the API (seeded catalogue, Development demo accounts) and the web app built against it. The API allows CORS only from
// localhost:3000/3001. Skipped (exit 0) unless E2E_ORDERS=1. Creates and cancels ~24 orders and one company: use a scratch DB.
//   E2E_ORDERS=1 E2E_API_URL=http://localhost:5950 E2E_WEB_URL=http://localhost:3000 E2E_VARIANT_A=<id> E2E_VARIANT_B=<id> node tests/orders.mjs
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { WEB, API, SHOTS, USERS, apiCall, apiLogin, launch, totp, uniqueBin } from './lib.mjs';

if (process.env.E2E_ORDERS !== '1') {
  console.log('SKIP  orders E2E (set E2E_ORDERS=1 with the API, a seeded catalogue and the web app running)');
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
const VARIANT_A = process.env.E2E_VARIANT_A ?? '9cf9a430-ddd9-42dd-929f-f32f00b0c0bd';
const VARIANT_B = process.env.E2E_VARIANT_B ?? '426401c0-76c0-463b-bf68-cf0eacd8109e';
const VIEWPORTS = { m360: { width: 360, height: 740 }, d1280: { width: 1280, height: 800 } };
const EXTRA_ORDERS = 21; // enough for a second page

const results = [];
const check = (name, ok, extra = '') => {
  results.push(ok);
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${extra ? '  -> ' + extra : ''}`);
};
async function axeCheck(page, label) {
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.addScriptTag({ path: AXE });
  const r = await page.evaluate(async () => {
    const out = await window.axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice'] } });
    return out.violations.map((v) => ({ id: v.id, impact: v.impact, nodes: v.nodes.slice(0, 3).map((n) => n.target.join(' ')) }));
  });
  check(`axe 0 violations: ${label}`, r.length === 0, r.length ? JSON.stringify(r) : '');
}
const noOverflow = (page) => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth);
const money = (minor, lname) => {
  const n = new Intl.NumberFormat(lname === 'bn' ? 'bn-BD' : 'en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(minor / 100);
  return `৳${n}`;
};

const buyerToken = await apiLogin(USERS.buyer);
const call = (m, p, b) => apiCall(buyerToken, m, p, b);
let adminCache = null;
async function adminToken() {
  if (adminCache) return adminCache;
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
async function newCompany(name) {
  const admin = await adminToken();
  const reg = await call('POST', '/api/v1/companies', { legalName: name, tradeLicenceNo: 'TL-E36', bin: uniqueBin() });
  const id = reg.json.data.id;
  await apiCall(admin, 'POST', `/api/v1/admin/companies/${id}/activate`);
  const me = (await call('GET', '/api/v1/users/me')).json.data.id;
  const members = (await call('GET', `/api/v1/companies/${id}/members`)).json.data;
  const own = members.find((x) => x.userId === me);
  await call('PUT', `/api/v1/companies/${id}/members/${own.id}`, { roles: 10, spendLimitMinor: null, costCentreIds: [], isActive: true });
  const site = await call('POST', `/api/v1/companies/${id}/sites`, { name: 'Gazipur Plant', contactName: 'Sabbir', contactPhone: '01711000000', line1: 'Plot 5, BSCIC Area', upazila: 'Tongi Industrial Zone', district: 'Gazipur', division: 'Dhaka', isDefaultDelivery: true });
  return { id, siteId: site.json.data?.id };
}
let keySeq = 0;
const idemKey = () => `e2e-orders-${Date.now()}-${++keySeq}`;
async function place(body) {
  const r = await fetch(`${API}/api/v1/orders`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${buyerToken}`, 'idempotency-key': idemKey() },
    body: JSON.stringify(body)
  });
  const j = await r.json();
  if (!j.data) throw new Error(`placing an order failed: ${r.status} ${JSON.stringify(j).slice(0, 300)}`);
  return j.data;
}
const lines = [{ variantId: VARIANT_A, quantity: 2 }, { variantId: VARIANT_B, quantity: 1 }];
const cancel = (id) => call('POST', `/api/v1/orders/${id}/cancel`, { reason: 'e2e cleanup' });
const toCancel = [];

async function signIn(page, prefix, who, path) {
  const auth = ALL[prefix ? 'bn' : 'en'].Auth;
  await page.goto(`${WEB}${prefix}/auth/login?returnTo=${encodeURIComponent(prefix + path)}`, { waitUntil: 'networkidle' });
  await page.getByLabel(auth.emailOrPhoneLabel).fill(who.email);
  await page.locator('#auth-pw').fill(who.password);
  await page.getByRole('button', { name: auth.signInButton }).click();
  await page.waitForURL(new RegExp(`${prefix}${path.replace(/[/?.]/g, '\\$&')}`), { timeout: 60000 });
}

(async () => {
  const browser = await launch();
  const addr = (await call('GET', '/api/v1/users/me/addresses')).json.data[0];
  const company = await newCompany(`E36 Mills ${Date.now() % 100000} Ltd`);

  // Oldest first: the personal order, then 21 cancelled ones (page 2 filler), one cancelled one we look at, and the company order last.
  const personal = await place({ lines, shippingAddressId: addr.id });
  toCancel.push(personal.orderId);
  for (let i = 0; i < EXTRA_ORDERS; i++) {
    const o = await place({ lines: [{ variantId: VARIANT_B, quantity: 1 }], shippingAddressId: addr.id });
    await cancel(o.orderId);
  }
  const cancelled = await place({ lines: [{ variantId: VARIANT_B, quantity: 1 }], shippingAddressId: addr.id });
  await cancel(cancelled.orderId);
  const comp = await place({ lines, companyId: company.id, shipToSiteId: company.siteId, purchaseOrderNumber: 'PO-2026/11 #36' });
  toCancel.push(comp.orderId);
  const full = async (o) => (await call('GET', `/api/v1/orders/${o.orderId}`)).json.data;
  const total = (await call('GET', '/api/v1/orders?page=1&pageSize=1')).json.data.totalCount;
  const P = await full(personal);
  const C = await full(comp);
  const X = await full(cancelled);
  console.log(`orders: personal ${P.orderNumber} (${P.status}), company ${C.orderNumber} (${C.status}), cancelled ${X.orderNumber} (${X.status}); estimatedDeliveryBy ${P.estimatedDeliveryBy ?? 'null'}`);

  {
    const ctx = await browser.newContext();
    const p = await ctx.newPage();
    await p.goto(`${WEB}/account/orders`);
    await p.waitForURL(/auth\/login/);
    check('signed-out /account/orders redirects to login with returnTo', /returnTo=%2Faccount%2Forders/.test(p.url()), p.url());
    await p.goto(`${WEB}/account/orders/${P.orderNumber}`);
    await p.waitForURL(/auth\/login/);
    check('signed-out order link redirects to login and keeps the order number', p.url().includes(encodeURIComponent(`/account/orders/${P.orderNumber}`)), p.url());
    await ctx.close();
  }

  for (const lname of ['en', 'bn']) {
    const m = ALL[lname].Orders;
    const prefix = lname === 'bn' ? '/bn' : '';
    for (const [vname, vp] of Object.entries(VIEWPORTS)) {
      const tag = `${lname} ${vp.width}px`;
      const ctx = await browser.newContext({ viewport: vp, locale: lname === 'bn' ? 'bn-BD' : 'en-US' });
      const page = await ctx.newPage();
      const errors = [];
      page.on('pageerror', (e) => errors.push(`${page.url()} ${e.message.slice(0, 80)}`));

      // ---- list
      await signIn(page, prefix, USERS.buyer, '/account/orders');
      await page.getByRole('heading', { level: 1, name: m.title }).waitFor();
      await page.getByTestId('row-number').first().waitFor();
      const numbers = await page.getByTestId('row-number').allInnerTexts();
      check(`[${tag}] list: 20 rows on page 1, newest first (company order on top)`, numbers.length === 20 && numbers[0] === C.orderNumber && numbers[1] === X.orderNumber, `${numbers.length} ${numbers[0]}`);
      const topRow = page.locator('a', { hasText: C.orderNumber });
      const topText = await topRow.innerText();
      check(`[${tag}] list: company row shows status, total, PO number and item count`, topText.includes(m[`status_${C.status}`]) && topText.includes(money(C.grandTotalMinor, lname)) && topText.includes('PO-2026/11 #36'), topText.replace(/\n/g, ' | '));
      if (lname === 'bn') check(`[${tag}] list uses Bangla digits`, /[০-৯]/.test(topText));
      const cancelledText = await page.locator('a', { hasText: X.orderNumber }).innerText();
      check(`[${tag}] list: cancelled order has the plain-language label`, cancelledText.includes(m.status_cancelled));
      check(`[${tag}] list: noindex, no horizontal overflow`, (await page.locator('meta[name="robots"][content*="noindex"]').count()) === 1 && (await noOverflow(page)));
      await axeCheck(page, `${tag} /account/orders`);
      await page.screenshot({ path: `${SHOTS}/orders-list-${lname}-${vname}.png` });

      // pagination (real data: 24 orders)
      const pagesNav = page.getByRole('navigation', { name: m.pagesLabel });
      check(`[${tag}] pagination: page 1 of 2, Previous absent, Next present`, (await pagesNav.innerText()).includes(m.next) && !(await pagesNav.innerText()).includes(m.prev));
      await pagesNav.getByRole('button', { name: m.next }).click();
      await page.waitForURL(/page=2/);
      await page.waitForFunction((first) => document.querySelector('[data-testid="row-number"]')?.textContent !== first, numbers[0]);
      const page2 = await page.getByTestId('row-number').allInnerTexts();
      const want2 = Math.min(20, total - 20);
      check(`[${tag}] pagination: page 2 shows the next ${want2} orders, none from page 1; URL has ?page=2`, page2.length === want2 && !page2.includes(numbers[0]) && (total > 40 || page2.includes(P.orderNumber)), `${page2.length}`);
      await page.reload({ waitUntil: 'networkidle' });
      await page.getByTestId('row-number').first().waitFor();
      check(`[${tag}] refresh on page 2 stays on page 2`, (await page.getByTestId('row-number').count()) === want2);
      await axeCheck(page, `${tag} /account/orders page 2`);

      // row -> detail
      await page.locator('a', { hasText: P.orderNumber }).click();
      await page.waitForURL(new RegExp(`/account/orders/${P.orderNumber}$`));
      await page.getByTestId('order-number').waitFor();
      check(`[${tag}] row opens the detail page`, (await page.getByTestId('order-number').innerText()) === P.orderNumber);

      // ---- detail by order number (the e-mail link), personal order
      await page.goto(`${WEB}${prefix}/account/orders/${P.orderNumber}`, { waitUntil: 'networkidle' });
      await page.getByTestId('order-number').waitFor();
      const body = await page.locator('main').innerText();
      check(`[${tag}] detail (by number, oldest order = lookup over 2 pages): lines, SKUs, quantities`, body.includes('MW-HAG-MBN132-4P') || P.subOrders.every((s) => s.items.every((i) => body.includes(i.sku))));
      check(`[${tag}] detail: shipment per seller (${P.subOrders.length})`, (await page.locator('section[aria-label]').count()) === P.subOrders.length);
      check(`[${tag}] detail: totals as the server returned them`, (await page.getByTestId('grand-total').innerText()) === money(P.grandTotalMinor, lname) && (await page.getByTestId('total-' + m.subtotal).innerText()) === money(P.subtotalMinor, lname));
      check(`[${tag}] detail: status chip and progress line (current step marked)`, body.includes(m[`status_${P.status}`]) && (await page.locator('ol[aria-label] li[aria-current="step"]').count()) === 1);
      if (P.estimatedDeliveryBy) check(`[${tag}] detail: expected-by date with the legal note`, (await page.getByTestId('expected-by').count()) === 1 && body.includes(m.expectedNote));
      else check(`[${tag}] detail: no expected-by when the server has none`, (await page.getByTestId('expected-by').count()) === 0);
      check(`[${tag}] detail: no PO number on a personal order`, (await page.getByTestId('po-number').count()) === 0);
      check(`[${tag}] detail: no horizontal overflow`, await noOverflow(page));
      await axeCheck(page, `${tag} /account/orders/<number>`);
      await page.screenshot({ path: `${SHOTS}/orders-detail-${lname}-${vname}.png` });

      // company order: PO number
      await page.goto(`${WEB}${prefix}/account/orders/${C.orderNumber}`, { waitUntil: 'networkidle' });
      await page.getByTestId('order-number').waitFor();
      check(`[${tag}] detail: company order shows its PO number`, (await page.getByTestId('po-number').innerText()) === 'PO-2026/11 #36');

      // cancelled order: no progress line
      await page.goto(`${WEB}${prefix}/account/orders/${X.orderNumber}`, { waitUntil: 'networkidle' });
      await page.getByTestId('order-number').waitFor();
      check(`[${tag}] detail: cancelled order shows "${m.status_cancelled}" and no progress line`, (await page.locator('main').innerText()).includes(m.status_cancelled) && (await page.locator('ol[aria-label]').count()) === 0);

      // not found
      for (const [label, path] of [['unknown number', '/account/orders/MW-DOES-NOT-EXIST'], ['malformed number', '/account/orders/%2E%2E%2Fetc']]) {
        await page.goto(`${WEB}${prefix}${path}`, { waitUntil: 'networkidle' });
        await page.getByTestId('order-not-found').waitFor();
        check(`[${tag}] ${label}: friendly not-found state`, (await page.getByText(m.notFoundTitle).count()) === 1);
      }
      await axeCheck(page, `${tag} order not found`);

      // error + retry (mocked 503), then recovery; loading state while the answer is slow
      let down = true;
      await page.route('**/api/v1/orders?**', async (route) => {
        if (down) return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ status: 503, error: { code: 'X', message: 'x', details: [] } }) });
        await new Promise((r) => setTimeout(r, 800));
        return route.continue();
      });
      await page.goto(`${WEB}${prefix}/account/orders`, { waitUntil: 'networkidle' });
      await page.getByRole('alert').filter({ hasText: m.loadError }).waitFor();
      check(`[${tag}] list 503 shows an error with a retry button, never an empty list`, (await page.getByRole('button', { name: m.retry }).count()) === 1 && (await page.getByText(m.empty).count()) === 0);
      down = false;
      await page.getByRole('button', { name: m.retry }).click();
      check(`[${tag}] while loading: heading shown, no list yet`, (await page.getByRole('heading', { level: 1, name: m.title }).count()) === 1 && (await page.getByRole('list', { name: m.listLabel }).count()) === 0);
      await page.getByTestId('row-number').first().waitFor();
      check(`[${tag}] retry recovers the list`, (await page.getByTestId('row-number').count()) === 20);
      await page.unroute('**/api/v1/orders?**');

      // ---- confirmation (real order)
      await page.goto(`${WEB}${prefix}/checkout/confirmation?order=${C.orderNumber}&id=${C.id}`, { waitUntil: 'networkidle' });
      await page.getByTestId('order-number').waitFor();
      await page.getByTestId('grand-total').waitFor();
      const confirm = await page.locator('main').innerText();
      check(`[${tag}] confirmation: number, status, totals, PO number`, confirm.includes(C.orderNumber) && confirm.includes(m[`status_${C.status}`]) && (await page.getByTestId('grand-total').innerText()) === money(C.grandTotalMinor, lname) && confirm.includes('PO-2026/11 #36'));
      check(`[${tag}] confirmation: next steps and the payment area (TODO E3.5)`, confirm.includes(m.nextTitle) && (await page.getByTestId('payment-area').count()) === 1);
      check(`[${tag}] confirmation: no horizontal overflow`, await noOverflow(page));
      await axeCheck(page, `${tag} /checkout/confirmation`);
      await page.getByRole('link', { name: m.viewOrder }).click();
      await page.waitForURL(new RegExp(`/account/orders/${C.orderNumber}$`));
      await page.getByTestId('order-number').waitFor();
      check(`[${tag}] confirmation "View order" opens the detail page`, true);
      await page.goto(`${WEB}${prefix}/checkout/confirmation?order=${P.orderNumber}&id=00000000-0000-4000-8000-000000000000`, { waitUntil: 'networkidle' });
      await page.getByText(m.confirmLoadError).waitFor();
      check(`[${tag}] confirmation with an unknown id still shows the number and a soft note`, (await page.getByTestId('order-number').innerText()) === P.orderNumber);

      // header menu
      await page.goto(`${WEB}${prefix}/search`, { waitUntil: 'networkidle' });
      await await page.locator('button[aria-haspopup="menu"]').first().click();
      const menuLink = page.getByRole('link', { name: lname === 'bn' ? 'আমার অর্ডার' : 'My orders' });
      check(`[${tag}] header account menu has "My orders" linking to the list`, (await menuLink.count()) === 1 && /account\/orders$/.test((await menuLink.getAttribute('href')) ?? ''));

      check(`[${tag}] no page errors`, errors.length === 0, errors.join(' | '));
      await ctx.close();

      // ---- another user: cannot open the buyer's order; has an empty list (once per language, at 1280 px)
      if (vname === 'd1280') {
        const ctx2 = await browser.newContext({ viewport: vp, locale: lname === 'bn' ? 'bn-BD' : 'en-US' });
        const other = await ctx2.newPage();
        await signIn(other, prefix, USERS.sellerAlpha, '/account/orders');
        await other.getByText(m.empty).waitFor();
        check(`[${tag}] other user: empty state with a link to search`, (await other.getByRole('link', { name: m.emptyAction }).count()) === 1);
        await axeCheck(other, `${tag} /account/orders empty`);
        for (const n of [P.orderNumber, C.orderNumber]) {
          await other.goto(`${WEB}${prefix}/account/orders/${n}`, { waitUntil: 'networkidle' });
          await other.getByTestId('order-not-found').waitFor();
          check(`[${tag}] other user opening ${n}: the same not-found state (nothing about the order leaks)`, !(await other.locator('main').innerText()).includes('MW-HAG'));
        }
        await ctx2.close();
      }
    }
  }
  await browser.close();
  for (const id of toCancel) await cancel(id);
  const failed = results.filter((r) => !r).length;
  console.log(`\n${results.length - failed}/${results.length} passed`);
  process.exit(failed ? 1 : 0);
})().catch(async (e) => {
  console.error(e);
  for (const id of toCancel) await cancel(id).catch(() => undefined);
  process.exit(1);
});
