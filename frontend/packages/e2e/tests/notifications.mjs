// F3d in-app notifications (web, EN + BN): bell with the unread count in words, dropdown (title from key, Dhaka time, link marks read, mark all read),
// unknown key gets the generic text, an unsafe link is not a link, /account/notifications with the unread filter, axe 0. The notifications API is mocked
// (the sign-in is real). SCRATCH DB only. Set E2E_NOTIFICATIONS=1 to run; otherwise it exits 0 without doing anything.
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { USERS, WEB, launch } from './lib.mjs';
import { axeCheck, r } from './returns-lib.mjs';

if (process.env.E2E_NOTIFICATIONS !== '1') {
  console.log('SKIP  notifications E2E (set E2E_NOTIFICATIONS=1 to run it)');
  process.exit(0);
}
const here = dirname(fileURLToPath(import.meta.url));
const load = (l) => JSON.parse(readFileSync(resolve(here, `../../../apps/web/src/messages/${l}.json`), 'utf8'));
const ALL = { en: load('en'), bn: load('bn') };

(async () => {
  const browser = await launch();
  for (const [lang, prefix] of [['en', ''], ['bn', '/bn']]) {
    const N = ALL[lang].Notifications;
    const L = `[${lang}]`;
    const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 }, locale: lang === 'bn' ? 'bn-BD' : 'en-GB' });
    const page = await ctx.newPage();
    const reads = [];
    let items = [
      { id: 'n1', kind: 'order', titleKey: 'notifications.order_delivered', params: { orderNumber: 'MW-1' }, link: `/${lang}/account/orders/MW-1`, createdAt: '2026-11-27T20:30:00Z', readAt: null },
      { id: 'n2', kind: 'x', titleKey: 'notifications.from_the_future', params: {}, link: '//evil.example', createdAt: '2026-11-26T10:00:00Z', readAt: null },
      { id: 'n3', kind: 'order', titleKey: 'notifications.order_shipped', params: { orderNumber: 'MW-2', courierName: 'Pathao', trackingNumber: 'T9' }, link: null, createdAt: '2026-11-25T10:00:00Z', readAt: '2026-11-25T11:00:00Z' }
    ];
    const unread = () => items.filter((i) => !i.readAt).length;
    await page.route('**/api/v1/notifications**', async (route) => {
      const url = new URL(route.request().url());
      const ok = (data) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ data }) });
      if (url.pathname.endsWith('/unread-count')) return ok({ count: unread() });
      if (url.pathname.endsWith('/read-all')) { items = items.map((i) => ({ ...i, readAt: i.readAt ?? 'x' })); return ok({ updated: 2 }); }
      if (url.pathname.endsWith('/read')) { reads.push(url.pathname.split('/').at(-2)); items = items.map((i) => (i.id === reads.at(-1) ? { ...i, readAt: 'x' } : i)); return route.fulfill({ status: 204 }); }
      const only = url.searchParams.get('unread') === 'true';
      const list = only ? items.filter((i) => !i.readAt) : items;
      return ok({ items: list, page: 1, pageSize: 50, total: list.length });
    });
    await page.goto(`${WEB}${prefix}/auth/login?returnTo=${encodeURIComponent(`${prefix}/`)}`, { waitUntil: 'networkidle' });
    await page.getByLabel(ALL[lang].Auth.emailOrPhoneLabel).fill(USERS.buyer.email);
    await page.locator('#auth-pw').fill(USERS.buyer.password);
    await page.getByRole('button', { name: ALL[lang].Auth.signInButton }).click();
    const bell = page.getByRole('button', { name: new RegExp(N.panelLabel) }).first();
    await bell.waitFor({ timeout: 60000 });
    r.check(`${L} bell names the unread count in words`, /2/.test(await bell.getAttribute('aria-label')));
    await bell.click();
    r.check(`${L} unknown key shows the generic text`, await page.getByText(N.title.generic).first().isVisible());
    r.check(`${L} an unsafe link is not a link`, (await page.locator('#notification-panel a[href^="//"]').count()) === 0);
    await axeCheck(page, `${L} bell dropdown`);
    await page.locator('#notification-panel a[href*="/account/orders/MW-1"]').first().click();
    await page.waitForTimeout(500);
    r.check(`${L} opening a link marks that item read`, reads.includes('n1'));
    await page.goto(`${WEB}${prefix}/account/notifications`, { waitUntil: 'networkidle' });
    await page.getByRole('button', { name: N.filterUnread }).click();
    r.check(`${L} unread filter hides read items`, (await page.getByRole('list', { name: N.listLabel }).locator('li').count()) === 1);
    await page.getByRole('button', { name: N.markAll }).click();
    await page.waitForTimeout(500);
    r.check(`${L} mark all read empties the unread list`, await page.getByText(N.emptyUnread).first().isVisible());
    await axeCheck(page, `${L} list page`);
    await ctx.close();
  }
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
