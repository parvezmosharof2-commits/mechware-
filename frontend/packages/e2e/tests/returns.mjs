// F2.4 buyer returns (web, EN + BN): "Return items" on a delivered order inside the window, the form (lines, quantity cap, reason, note for "other"), refusals in words
// (empty, quantity, note, non-returnable category, quantity taken meanwhile, window closed meanwhile), the Idempotency-Key kept across a 5xx, the company id sent,
// My returns list + detail (status in words, accepted quantities, rejection reason), withdraw before approval (and refused after), outside the window, a not-delivered
// order, axe 0, 360 px. Orders and staff moves go through the real API (see returns-lib.mjs); only the 503 in the idempotency check is mocked
// (the real request reaches the API first, its answer is replaced).
// SCRATCH DB only. Set E2E_RETURNS=1 to run; otherwise it exits 0 without doing anything.
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, USERS, WEB, apiLogin, launch } from './lib.mjs';
import { PEOPLE, VARIANT_A, VARIANT_B, apiToken, axeCheck, deliveredOrder, key, lineOf, move, moveDelivery, noOverflow, openReturn, r, requireScratch, send, setupCompany, sql, staffReturn, tag } from './returns-lib.mjs';

if (process.env.E2E_RETURNS !== '1') {
  console.log('SKIP  returns E2E (set E2E_RETURNS=1 to run it)');
  process.exit(0);
}
requireScratch();
const here = dirname(fileURLToPath(import.meta.url));
const ALL = {
  en: JSON.parse(readFileSync(resolve(here, '../../../apps/web/src/messages/en.json'), 'utf8')),
  bn: JSON.parse(readFileSync(resolve(here, '../../../apps/web/src/messages/bn.json'), 'utf8'))
};
// next-intl prints plain numbers with Latin digits in both locales (same as the rest of the storefront).
const fmt = (_lang, n) => String(n);

async function signIn(page, prefix, lang, path) {
  const auth = ALL[lang].Auth;
  await page.goto(`${WEB}${prefix}/auth/login?returnTo=${encodeURIComponent(prefix + path)}`, { waitUntil: 'networkidle' });
  await page.getByLabel(auth.emailOrPhoneLabel).fill(USERS.buyer.email);
  await page.locator('#auth-pw').fill(USERS.buyer.password);
  await page.getByRole('button', { name: auth.signInButton }).click();
  await page.waitForURL(new RegExp(`${prefix}${path.replace(/[/?.]/g, '\\$&')}`), { timeout: 60000 });
}
const text = (page, id) => page.getByTestId(id).first().innerText();

(async () => {
  const admin = await apiToken(PEOPLE.admin);
  const buyer = await apiLogin(USERS.buyer);
  const company = await setupCompany(admin, buyer, 'Returns Works');
  r.check('setup: a company with credit; orders are placed and delivered through the real API', true);
  const browser = await launch();

  for (const [lang, prefix] of [['en', ''], ['bn', '/bn']]) {
    const R = ALL[lang].Returns;
    const L = `[${lang}]`;
    const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 }, locale: lang === 'bn' ? 'bn-BD' : 'en-GB' });
    const page = await ctx.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push(e.message));

    const order = await deliveredOrder(admin, buyer, company);
    const A = lineOf(order, VARIANT_A);
    const B = lineOf(order, VARIANT_B);
    r.check(`${L} the buyer's order detail now carries an id on every line`, !!A?.id && !!B?.id);
    await signIn(page, prefix, lang, `/account/orders/${order.orderNumber}`);
    await page.getByTestId('return-panel').waitFor();
    const openBtn = page.getByTestId('return-open');
    r.check(`${L} Delivered order inside the window: the "${R.panel.open}" action is shown`, (await openBtn.count()) === 1 && (await openBtn.innerText()) === R.panel.open);
    await openBtn.click();
    await page.getByTestId('return-form').waitFor();
    await axeCheck(page, `${L} order page with the return form open (1280)`);

    // ---- refusals on the screen (nothing is sent)
    const posts = [];
    page.on('request', (q) => { if (q.method() === 'POST' && /\/returns$/.test(q.url())) posts.push(q); });
    const submit = () => page.getByTestId('return-submit').click();
    const problems = () => text(page, 'return-problems');
    await submit();
    r.check(`${L} nothing chosen: "${R.form.problem.noLines}"`, (await problems()).includes(R.form.problem.noLines));
    await page.getByTestId(`return-qty-${A.sku}`).fill('4');
    await submit();
    r.check(`${L} more than the maximum (3): quantity problem in words`, (await problems()).includes(R.form.problem.quantity));
    await page.getByTestId(`return-qty-${A.sku}`).fill('2');
    await page.getByTestId('return-reason').selectOption('other');
    await submit();
    r.check(`${L} reason "other" without a note: note required`, (await problems()).includes(R.form.problem.noteRequired));
    r.check(`${L} none of these refusals sent a request`, posts.length === 0);

    // ---- the server's word: a non-returnable category (422)
    await page.getByTestId(`return-qty-${A.sku}`).fill('0');
    await page.getByTestId(`return-qty-${B.sku}`).fill('1');
    await page.getByTestId('return-note').fill('Flow meter does not read');
    await submit();
    await page.getByText(R.error.notReturnable).first().waitFor();
    r.check(`${L} a line from a non-returnable category: refused in words (422)`, posts.length === 1);

    // ---- a 5xx after the server processed the request: retry with the SAME key, one return
    await page.getByTestId(`return-qty-${B.sku}`).fill('0');
    await page.getByTestId(`return-qty-${A.sku}`).fill('2');
    let first = true;
    const keys = [];
    const bodies = [];
    await page.route('**/api/v1/orders/*/returns', async (route) => {
      keys.push(route.request().headers()['idempotency-key']);
      bodies.push(route.request().postData());
      if (first) {
        first = false;
        await route.fetch();
        return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: { code: 'UNAVAILABLE', message: 'down', details: [] } }) });
      }
      return route.continue();
    });
    await submit();
    await page.getByText(R.error.unavailable).first().waitFor();
    r.check(`${L} a 503 is explained ("press send again, no second request")`, true);
    await submit();
    await page.getByTestId('return-sent').waitFor();
    await page.unroute('**/api/v1/orders/*/returns');
    r.check(`${L} the retry used the same Idempotency-Key and body`, keys.length === 2 && keys[0] === keys[1] && bodies[0] === bodies[1] && !!keys[0], keys.join(' / '));
    r.check(`${L} the company order sends the company id`, JSON.parse(bodies[0]).companyId === company.companyId);
    const mine = (await send(buyer, 'GET', '/api/v1/returns?page=1&pageSize=50')).json.data.items.filter((x) => x.orderId === order.id);
    r.check(`${L} the server holds exactly one return of 2 units (the replay did not duplicate)`, mine.length === 1 && mine[0].lines[0].quantity === 2, JSON.stringify(mine.map((x) => x.lines.map((l) => l.quantity))));
    const r1 = mine[0];

    // ---- list and detail
    await page.goto(`${WEB}${prefix}/account/returns`, { waitUntil: 'networkidle' });
    await page.getByTestId('return-row').first().waitFor();
    r.check(`${L} My returns: the row shows the order number and the status in words`, (await page.getByTestId('return-row').first().innerText()).includes(order.orderNumber) && (await text(page, 'return-status')) === R.status.requested);
    await axeCheck(page, `${L} My returns list (1280)`);
    await page.getByTestId('return-row').first().click();
    await page.getByTestId('return-order-number').waitFor();
    r.check(`${L} detail: status, asked quantity and "not inspected yet"`, (await text(page, 'return-status')) === R.status.requested && (await text(page, 'return-line')).includes(R.detail.asked.replace('{count}', fmt(lang, 2))) && (await text(page, 'return-accepted')) === R.detail.notInspected);
    await axeCheck(page, `${L} return detail with the withdraw button (1280)`);

    // ---- withdraw before approval
    await page.getByTestId('withdraw-open').click();
    await page.getByTestId('withdraw-confirm').click();
    await page.getByText(R.status.withdrawn, { exact: true }).first().waitFor();
    r.check(`${L} withdraw: status "${R.status.withdrawn}", no withdraw button any more`, (await page.getByTestId('withdraw-open').count()) === 0 && (await staffReturn(admin, r1.id)).status === 'withdrawn');

    // ---- approve -> received -> inspect by staff; the buyer sees accepted quantities
    const r2 = (await openReturn(buyer, order, 2, { company })).json.data;
    r.check(`${L} after the withdrawal the units can be asked again`, !!r2?.id);
    await move(admin, r2.id, 'approve');
    await move(admin, r2.id, 'received');
    const staff2 = await staffReturn(admin, r2.id);
    const inspected = await move(admin, r2.id, 'inspect', { lines: [{ returnLineId: staff2.lines[0].id, acceptedQty: 1, restockQty: 1, note: 'one fine' }] });
    r.check(`${L} (staff, API) approve, received, inspect all answered 200`, inspected.status === 200 && inspected.json.data.status === 'inspected');
    await page.goto(`${WEB}${prefix}/account/returns/${r2.id}`, { waitUntil: 'networkidle' });
    await page.getByTestId('return-order-number').waitFor();
    r.check(`${L} inspected return: status in words and accepted quantity 1 of 2`, (await text(page, 'return-status')) === R.status.inspected && (await text(page, 'return-accepted')) === R.detail.accepted.replace('{count}', fmt(lang, 1)) && (await page.getByTestId('withdraw-open').count()) === 0);
    const detailText = await page.locator('main').innerText();
    r.check(`${L} the buyer never sees the staff's inspection note`, !detailText.includes('one fine'));

    // ---- reject: the reason is shown
    const r3 = (await openReturn(buyer, order, 1, { company })).json.data;
    const rej = await move(admin, r3.id, 'reject', { reason: 'Photos show installation damage' });
    await page.goto(`${WEB}${prefix}/account/returns/${r3.id}`, { waitUntil: 'networkidle' });
    await page.getByTestId('return-order-number').waitFor();
    r.check(`${L} rejected return: status and the staff's reason in words`, rej.status === 200 && (await text(page, 'return-status')) === R.status.rejected && (await text(page, 'return-rejection')).includes('Photos show installation damage'));

    // ---- stale withdraw: approved meanwhile
    const r4 = (await openReturn(buyer, order, 1, { company })).json.data;
    await page.goto(`${WEB}${prefix}/account/returns/${r4.id}`, { waitUntil: 'networkidle' });
    await page.getByTestId('withdraw-open').waitFor();
    await move(admin, r4.id, 'approve');
    await page.getByTestId('withdraw-open').click();
    await page.getByTestId('withdraw-confirm').click();
    await page.getByText(R.error.notWithdrawable).first().waitFor();
    r.check(`${L} withdraw after approval: refused in words (409), the page reloads to "approved"`, true);
    await page.getByText(R.status.approved, { exact: true }).first().waitFor();
    r.check(`${L} the reloaded detail shows the approved status and no withdraw button`, (await page.getByTestId('withdraw-open').count()) === 0);

    // ---- 360 px
    await page.setViewportSize({ width: 360, height: 740 });
    await page.goto(`${WEB}${prefix}/account/orders/${order.orderNumber}`, { waitUntil: 'networkidle' });
    await page.getByTestId('return-panel').waitFor();
    r.check(`${L} the order page lists the returns made for it`, (await page.getByTestId('order-return-link').count()) >= 3);
    await axeCheck(page, `${L} order page with returns (360)`);
    r.check(`${L} 360 px: no horizontal scroll on the order page`, await noOverflow(page));
    for (const [name, url] of [['list', `${prefix}/account/returns`], ['detail', `${prefix}/account/returns/${r2.id}`]]) {
      await page.goto(`${WEB}${url}`, { waitUntil: 'networkidle' });
      await page.getByTestId(name === 'list' ? 'return-row' : 'return-order-number').first().waitFor();
      await axeCheck(page, `${L} returns ${name} (360)`);
      r.check(`${L} 360 px: no horizontal scroll on the returns ${name}`, await noOverflow(page));
    }
    await page.setViewportSize({ width: 360, height: 740 });
    await page.goto(`${WEB}${prefix}/account/orders/${order.orderNumber}`, { waitUntil: 'networkidle' });
    await page.getByTestId('return-panel').waitFor();
    if (await page.getByTestId('return-open').count()) {
      await page.getByTestId('return-open').click();
      await page.getByTestId('return-form').waitFor();
      await axeCheck(page, `${L} return form (360)`);
      r.check(`${L} 360 px: no horizontal scroll with the form open`, await noOverflow(page));
    }
    r.check(`${L} no page errors`, errors.length === 0, errors.join(' | '));

    // ---- outside the window
    const old = await deliveredOrder(admin, buyer, company, { deliveredDaysAgo: 10 });
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(`${WEB}${prefix}/account/orders/${old.orderNumber}`, { waitUntil: 'networkidle' });
    await page.getByTestId('return-panel').waitFor();
    await page.getByTestId('return-closed').waitFor();
    r.check(`${L} delivered 10 days ago: no button, the ended window is explained`, (await page.getByTestId('return-open').count()) === 0 && (await text(page, 'return-closed')) === R.error.windowClosed);
    const refused = await openReturn(buyer, old, 1, { company });
    r.check(`${L} the API agrees: 409 RETURN_WINDOW_CLOSED`, refused.status === 409 && refused.json.error?.code === 'RETURN_WINDOW_CLOSED', JSON.stringify(refused.json.error));
    await axeCheck(page, `${L} order page, window closed (1280)`);

    if (lang === 'en') {
      // ---- the quantity taken meanwhile (409) and the window closing meanwhile (409), on one open form
      const o2 = await deliveredOrder(admin, buyer, company);
      const A2 = lineOf(o2, VARIANT_A);
      await page.goto(`${WEB}${prefix}/account/orders/${o2.orderNumber}`, { waitUntil: 'networkidle' });
      await page.getByTestId('return-open').click();
      await page.getByTestId('return-form').waitFor();
      await openReturn(buyer, o2, 2, { company });
      await page.getByTestId(`return-qty-${A2.sku}`).fill('2');
      await page.getByTestId('return-submit').click();
      await page.getByText(R.error.quantity).first().waitFor();
      r.check(`${L} quantity taken by another return meanwhile: 409 explained in words`, true);
      moveDelivery(o2.id, 10);
      await page.getByTestId(`return-qty-${A2.sku}`).fill('1');
      await page.getByTestId('return-submit').click();
      await page.getByText(R.error.windowClosed).first().waitFor();
      r.check(`${L} window closed meanwhile: 409 explained in words`, true);

      // ---- not delivered: no panel at all
      const placed = await (await fetch(`${API}/api/v1/orders`, {
        method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${buyer}`, 'idempotency-key': key('undelivered') },
        body: JSON.stringify({ lines: [{ variantId: VARIANT_A, quantity: 1 }], companyId: company.companyId, shipToSiteId: company.siteId, payOnCredit: true })
      })).json();
      await page.goto(`${WEB}${prefix}/account/orders/${placed.data.orderNumber}`, { waitUntil: 'networkidle' });
      await page.getByTestId('order-number').waitFor();
      r.check(`${L} an order that is not delivered shows no return panel`, (await page.getByTestId('return-panel').count()) === 0);
    }
    await ctx.close();
  }
  await browser.close();
  sql(`select 1;`);
  console.log(`scratch tag ${tag}`);
  const ok = r.finish();
  process.exit(ok ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
