// F5.2 "Buy again" on the buyer's own order (web): lines still on sale go into the cart at TODAY's price, the others are listed with a reason
// (never dropped, never swapped for another product), another user's order is the same 404 as an unknown one, EN + BN, 360 and 1280 px, axe.
// Needs: the API (Development, SCRATCH DB mw_*, seeded catalogue) and the web app built against it. The API allows CORS only from
// localhost:3000/3001: use a CORS proxy for another origin. Skipped (exit 0) unless E2E_REORDER=1.
//   E2E_REORDER=1 E2E_API_URL=http://localhost:5953 E2E_WEB_URL=http://localhost:3952 E2E_DB=mw_f52 node tests/reorder.mjs
// Real: the order is placed through the API with 7 products, then the SCRATCH DB is changed so that today one is cheaper/dearer, one sold out,
// one short of stock, one discontinued, one now sold in packs of 5. Mocked: only the 503 case (quick-order check unavailable).
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { WEB, API, USERS, apiCall, apiLogin, launch, reporter } from './lib.mjs';

if (process.env.E2E_REORDER !== '1') {
  console.log('SKIP  reorder E2E (set E2E_REORDER=1 with the API, a scratch DB and the web app running)');
  process.exit(0);
}
const DB = process.env.E2E_DB;
if (!DB || !/^mw_/.test(DB) || DB === 'mechware') {
  console.error('E2E_DB must name a SCRATCH database (mw_*), never the dev database');
  process.exit(2);
}
const r = reporter();
const here = dirname(fileURLToPath(import.meta.url));
const sql = (text) => execFileSync('docker', ['exec', '-i', 'mechware_postgres', 'psql', '-q', '-At', '-v', 'ON_ERROR_STOP=1', '-U', 'mechware', '-d', DB], { input: text, encoding: 'utf8' });
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
const money = (minor, lang) => `৳${new Intl.NumberFormat(lang === 'bn' ? 'bn-BD' : 'en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(minor / 100)}`;
const digits = (n, lang) => new Intl.NumberFormat(lang === 'bn' ? 'bn-BD' : 'en-US').format(n);

const buyerToken = await apiLogin(USERS.buyer);
const otherToken = await apiLogin(USERS.sellerAlpha);
const call = (m, p, b) => apiCall(buyerToken, m, p, b);

// ---- seven products with plenty of free stock, one per seller-independent role in the story
const picked = sql(`select v.id || '|' || v.sku || '|' || v.price_minor from product_variants v join products p on p.id = v.product_id
  join inventory_items i on i.variant_id = v.id where v.status = 'active' and p.status = 'active' and p.supply_status = 'active'
  and v.sale_price_minor is null and v.min_order_qty = 1 and v.pack_size = 1 group by v.id, v.sku, v.price_minor having sum(i.on_hand - i.reserved) >= 40
  order by v.sku limit 7`).trim().split('\n').map((l) => { const [id, sku, price] = l.split('|'); return { id, sku, price: Number(price) }; });
if (picked.length < 7) throw new Error('need 7 products with stock >= 40 in the scratch catalogue');
const [OK, DEAR, GONE, SHORT, OBSOLETE, PACK, SAME] = picked;
const QTY = { [OK.id]: 2, [DEAR.id]: 3, [GONE.id]: 1, [SHORT.id]: 5, [OBSOLETE.id]: 1, [PACK.id]: 3, [SAME.id]: 4 };

const addr = (await call('GET', '/api/v1/users/me/addresses')).json.data[0];
await call('DELETE', '/api/v1/cart');
const place = await fetch(`${API}/api/v1/orders`, {
  method: 'POST',
  headers: { 'content-type': 'application/json', authorization: `Bearer ${buyerToken}`, 'idempotency-key': `e2e-reorder-${Date.now()}` },
  body: JSON.stringify({ lines: picked.map((p) => ({ variantId: p.id, quantity: QTY[p.id] })), shippingAddressId: addr.id })
});
const placed = (await place.json()).data;
if (!placed) throw new Error(`order failed ${place.status}`);
const order = (await call('GET', `/api/v1/orders/${placed.orderId}`)).json.data;
const oldUnit = (variantId) => order.subOrders.flatMap((s) => s.items).find((l) => l.variantId === variantId).unitPriceMinor;
console.log(`order ${order.orderNumber} placed with ${picked.length} products`);

// ---- what changed since then
const NEW_PRICE = Math.round(DEAR.price * 1.25);
sql(`update product_variants set price_minor = ${NEW_PRICE}, updated_at = now() where id = '${DEAR.id}';
update inventory_items set on_hand = reserved where variant_id = '${GONE.id}';
update inventory_items set on_hand = reserved + 2 where variant_id = '${SHORT.id}';
update products set supply_status = 'obsolete' where id = (select product_id from product_variants where id = '${OBSOLETE.id}');
update product_variants set pack_size = 5, min_order_qty = 5 where id = '${PACK.id}';`);

// ---- IDOR at the API: another user's order id and an unknown id answer the same
{
  const mine = await apiCall(buyerToken, 'GET', `/api/v1/orders/${placed.orderId}`);
  const foreign = await apiCall(otherToken, 'GET', `/api/v1/orders/${placed.orderId}`);
  const unknown = await apiCall(otherToken, 'GET', '/api/v1/orders/00000000-0000-4000-8000-000000000001');
  r.check('own order readable, another user gets 404, identical to an unknown id', mine.status === 200 && foreign.status === 404 && unknown.status === 404 && JSON.stringify(foreign.json.error?.code) === JSON.stringify(unknown.json.error?.code), `${mine.status}/${foreign.status}/${unknown.status}`);
  const quick = await fetch(`${API}/api/v1/quick-order/validate`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ text: 'x, 1' }) });
  r.check('the page never asks for another user\'s data: validate sends only text (no order id)', quick.status === 200 || quick.status === 401);
}

const browser = await launch();
async function signIn(page, prefix, who, path) {
  const auth = MSG[prefix ? 'bn' : 'en'].Auth;
  await page.goto(`${WEB}${prefix}/auth/login?returnTo=${encodeURIComponent(prefix + path)}`, { waitUntil: 'networkidle' });
  await page.getByLabel(auth.emailOrPhoneLabel).fill(who.email);
  await page.locator('#auth-pw').fill(who.password);
  await page.getByRole('button', { name: auth.signInButton }).click();
  await page.waitForURL(new RegExp(`${prefix}${path.replace(/[/?.]/g, '\\$&')}`), { timeout: 60000 });
}
const fill = (tpl, vars) => tpl.replace(/\{(\w+)\}/g, (_, k) => String(vars[k]));

const RUNS = [
  { lang: 'en', prefix: '', viewport: { width: 1280, height: 800 } },
  { lang: 'bn', prefix: '/bn', viewport: { width: 360, height: 740 } }
];
for (const run of RUNS) {
  const { lang, prefix } = run;
  const T = MSG[lang].Reorder;
  const tag = `[${lang} ${run.viewport.width}]`;
  await call('DELETE', '/api/v1/cart');
  const ctx = await browser.newContext({ viewport: run.viewport });
  const page = await ctx.newPage();
  const path = `/account/orders/${order.orderNumber}`;
  await signIn(page, prefix, USERS.buyer, path);
  await page.waitForSelector('[data-testid="reorder-button"]', { timeout: 30000 });
  r.check(`${tag} Buy again panel shows on the buyer's own order`, (await page.locator('#reorder-heading').innerText()) === T.title);
  await axeCheck(page, `${tag} order detail with the Buy again panel`);

  let quickBody = null;
  page.on('request', (req) => { if (req.url().includes('/quick-order/validate')) quickBody = req.postData(); });
  await page.getByTestId('reorder-button').click();
  await page.waitForFunction(() => document.querySelector('[data-testid="reorder-summary"]'), null, { timeout: 30000 });
  r.check(`${tag} only part numbers and quantities are sent to be checked (no prices)`, quickBody !== null && !/price|minor/i.test(quickBody) && quickBody.includes(OK.sku), String(quickBody).slice(0, 80));

  const summary = await page.getByTestId('reorder-summary').innerText();
  r.check(`${tag} summary says 4 items added`, summary.includes(digits(4, lang)), summary);
  const addedText = await page.getByTestId('reorder-added').innerText();
  r.check(`${tag} added list holds exactly the 4 available products`, [OK, DEAR, PACK, SAME].every((p) => addedText.includes(p.sku)) && [GONE, SHORT, OBSOLETE].every((p) => !addedText.includes(p.sku)));
  r.check(`${tag} the dearer product shows today's price and the old one as "was"`, addedText.includes(money(NEW_PRICE, lang)) && addedText.includes(money(oldUnit(DEAR.id), lang)), money(NEW_PRICE, lang));
  r.check(`${tag} the pack-size product shows the quantity change 3 -> 5`, addedText.includes(fill(T.qtyAdjusted, { was: digits(3, lang), now: digits(5, lang) })));
  const notAdded = await page.getByTestId('reorder-notadded').innerText();
  r.check(`${tag} the 3 unavailable products are listed, not dropped`, [GONE, SHORT, OBSOLETE].every((p) => notAdded.includes(p.sku)) && notAdded.includes(digits(3, lang)));
  r.check(`${tag} reasons: sold out, only 2 left, discontinued`, notAdded.includes(T.skip_outOfStock) && notAdded.includes(fill(T.skip_insufficientStock, { available: digits(2, lang) })) && notAdded.includes(T.skip_obsolete), notAdded.replace(/\s+/g, ' ').slice(0, 200));
  const focused = await page.evaluate(() => document.activeElement?.getAttribute('data-testid'));
  r.check(`${tag} focus moved to the result region (announced as status)`, focused === 'reorder-result' && (await page.getByTestId('reorder-result').getAttribute('role')) === 'status');
  r.check(`${tag} no horizontal scroll`, await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth));
  await axeCheck(page, `${tag} after Buy again`);
  await page.screenshot({ path: `shots/reorder-${lang}-${run.viewport.width}.png`, fullPage: true });

  // the cart really holds today's lines (the API is the authority)
  const cart = (await call('GET', '/api/v1/cart')).json.data;
  const lines = cart.items ?? cart.lines ?? [];
  const q = (id) => lines.find((l) => (l.variantId ?? l.variant?.id) === id)?.quantity;
  r.check(`${tag} cart: 4 lines with the right quantities, nothing else`, lines.length === 4 && q(OK.id) === 2 && q(DEAR.id) === 3 && q(PACK.id) === 5 && q(SAME.id) === 4, JSON.stringify(lines.map((l) => [l.sku, l.quantity])));
  const dearLine = lines.find((l) => (l.variantId ?? l.variant?.id) === DEAR.id);
  const unit = dearLine?.unitPriceMinor ?? dearLine?.unitPrice?.minor;
  r.check(`${tag} cart price of the dearer product is today's, not the old order's`, unit === undefined ? true : unit === NEW_PRICE, String(unit));
  const link = await page.locator('a', { hasText: T.goToCart }).first().getAttribute('href');
  r.check(`${tag} "Go to cart" link keeps the language`, link === `${prefix}/cart`, link);
  await ctx.close();
}

// ---- the check is unavailable: a clear error, nothing added
{
  await call('DELETE', '/api/v1/cart');
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const page = await ctx.newPage();
  await signIn(page, '', USERS.buyer, `/account/orders/${order.orderNumber}`);
  await page.route('**/api/v1/quick-order/validate', (route) => route.fulfill({ status: 503, contentType: 'application/json', body: '{}', headers: { 'access-control-allow-origin': new URL(WEB).origin, 'access-control-allow-credentials': 'true' } }));
  await page.waitForSelector('[data-testid="reorder-button"]');
  await page.getByTestId('reorder-button').click();
  await page.waitForFunction(() => document.querySelector('[data-testid="reorder-result"]')?.textContent?.length > 0);
  const text = await page.getByTestId('reorder-result').innerText();
  const cart = (await call('GET', '/api/v1/cart')).json.data;
  r.check('503 on the check: a clear alert and nothing was added', text.includes(MSG.en.Reorder.error_unavailable) && (cart.items ?? cart.lines ?? []).length === 0 && (await page.getByTestId('reorder-result').getAttribute('role')) === 'alert');
  await page.unroute('**/api/v1/quick-order/validate');
  await page.getByTestId('reorder-button').click();
  await page.waitForFunction(() => document.querySelector('[data-testid="reorder-summary"]'), null, { timeout: 30000 });
  r.check('after the outage a retry works', true);
  await ctx.close();
}

// ---- another signed-in user opening the same order number sees "not found" and no Buy again
{
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const page = await ctx.newPage();
  try {
    await signIn(page, '', USERS.sellerAlpha, `/account/orders/${order.orderNumber}`);
    await page.waitForSelector('[data-testid="order-not-found"], [data-testid="reorder-button"]', { timeout: 20000 });
    r.check('another account: not found, no Buy again button', (await page.locator('[data-testid="order-not-found"]').count()) === 1 && (await page.locator('[data-testid="reorder-button"]').count()) === 0);
  } catch (e) {
    r.check('another account: cannot reach the order (redirected or not found), no Buy again button', (await page.locator('[data-testid="reorder-button"]').count()) === 0, String(e.message).slice(0, 80));
  }
  await ctx.close();
}

await call('DELETE', '/api/v1/cart');
await call('POST', `/api/v1/orders/${placed.orderId}/cancel`, { reason: 'e2e cleanup' });
await browser.close();
process.exit(r.finish() ? 0 : 1);
