// G5.8: console staff users (`/staff/users`, `/staff/users/{id}`, `/staff/role-changes`, `/staff/roles`).
// NOT RUN in the unit that wrote it (owner token rule). SCRATCH DB only (E2E_SQL_CMD must name mw_*; only checked, no SQL is sent).
// Needs the scratch API, a second administrator (admin2.e2e@, a clone of the admin row, same TOTP secret) and support.e2e@. Set E2E_STAFF_USERS=1 to run.
// Checks: support refused; list filter in the URL; own account cannot be disabled (button disabled, words); change roles -> pending request shown;
// the requester sees Approve disabled with the four-eyes words; admin2 approves with a code; roles table; axe 0; 360 px.
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { CONSOLE as WEB, USERS, launch, reporter, totp } from './lib.mjs';

if (process.env.E2E_STAFF_USERS !== '1') { console.log('SKIP  console staff users E2E (set E2E_STAFF_USERS=1 to run it)'); process.exit(0); }
if (!process.env.E2E_SQL_CMD || !/mw_/.test(process.env.E2E_SQL_CMD)) throw new Error('E2E_SQL_CMD must name a scratch database (mw_*)');

const r = reporter();
const here = dirname(fileURLToPath(import.meta.url));
const axeFile = () => {
  const ui = resolve(here, '../../ui/node_modules/axe-core/axe.min.js');
  if (existsSync(ui)) return ui;
  const pnpm = resolve(here, '../../../node_modules/.pnpm');
  return join(pnpm, readdirSync(pnpm).find((n) => n.startsWith('axe-core@')), 'node_modules/axe-core/axe.min.js');
};
async function axeCheck(page, label) {
  await page.addScriptTag({ path: axeFile() });
  const v = await page.evaluate(async () => (await window.axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice'] } })).violations.map((x) => x.id));
  r.check(`axe 0 violations: ${label}`, v.length === 0, v.join(','));
}
const sleep = (ms) => new Promise((res) => setTimeout(res, ms));
const lastStep = new Map();
async function freshCode(person) {
  for (;;) {
    const step = Math.floor(Date.now() / 30000);
    if (step > (lastStep.get(person.email) ?? -1)) { lastStep.set(person.email, step); return totp(person.totpSecret); }
    await sleep(1000);
  }
}
const PEOPLE = {
  admin: USERS.admin,
  admin2: { email: 'admin2.e2e@mechware.com.bd', password: USERS.admin.password, totpSecret: USERS.admin.totpSecret },
  support: { email: 'support.e2e@mechware.com.bd', password: USERS.admin.password, totpSecret: USERS.admin.totpSecret }
};
async function signIn(browser, person, width = 1280) {
  const page = await (await browser.newContext({ viewport: { width, height: 900 } })).newPage();
  page.errors = [];
  page.on('pageerror', (e) => page.errors.push(e.message));
  await page.goto(`${WEB}/login`, { timeout: 120000, waitUntil: 'networkidle' });
  await page.getByLabel('Staff or seller email').fill(person.email);
  await page.getByLabel('Password').fill(person.password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.getByLabel('6-digit authenticator code or single-use recovery code').fill(await freshCode(person));
  await page.getByRole('button', { name: 'Verify and unlock console' }).click();
  await page.waitForURL(`${WEB}/`, { timeout: 120000 });
  return page;
}

(async () => {
  const browser = await launch();
  const sup = await signIn(browser, PEOPLE.support);
  await sup.goto(`${WEB}/staff/users`);
  await sup.getByText('Your role does not allow this action (users.manage).').waitFor();
  r.check('support: not allowed, no sidebar Staff users link', (await sup.getByRole('link', { name: 'Staff users' }).count()) === 0);

  const a = await signIn(browser, PEOPLE.admin);
  await a.getByRole('link', { name: 'Staff users' }).click();
  await a.waitForURL(`${WEB}/staff/users`);
  await a.getByTestId('users-table').waitFor();
  await a.getByLabel('Role').selectOption('support');
  await a.getByRole('button', { name: 'Apply filters' }).click();
  await a.waitForURL(/role=support/);
  r.check('role filter is in the URL and every row is Support', (await a.getByTestId('user-row').allInnerTexts()).every((t) => t.includes('Support')));
  await axeCheck(a, '/staff/users');

  await a.goto(`${WEB}/staff/users`);
  await a.getByTestId('user-row').filter({ hasText: PEOPLE.admin.email }).getByRole('link').click();
  await a.getByTestId('user-status').waitFor();
  r.check('own page: Disable is disabled and the words say why', (await a.getByRole('button', { name: 'Disable this account' }).isDisabled()) && /own account/.test(await a.getByTestId('disable-hint').innerText()));

  await a.goto(`${WEB}/staff/users?q=${encodeURIComponent(PEOPLE.support.email)}`);
  await a.getByTestId('user-row').first().getByRole('link').click();
  await a.getByRole('button', { name: 'Change roles' }).click();
  await a.getByLabel('Finance').check();
  await a.getByLabel(/Reason/).fill('Covers month end close');
  await a.getByRole('button', { name: 'Continue' }).click();
  await a.getByLabel(/authenticator code/i).fill(await freshCode(PEOPLE.admin));
  await a.getByRole('button', { name: 'Send the request' }).click();
  await a.getByTestId('pending-change').waitFor();
  r.check('pending request shown with the diff words', /adds Finance/.test(await a.getByTestId('pending-change').innerText()));

  await a.goto(`${WEB}/staff/role-changes`);
  await a.getByTestId('change-item').first().waitFor();
  r.check('requester: Approve disabled, four-eyes words written out', (await a.getByRole('button', { name: 'Approve' }).first().isDisabled()) && /different administrator/.test(await a.getByTestId('approve-hint').first().innerText()));
  await axeCheck(a, '/staff/role-changes');

  const b = await signIn(browser, PEOPLE.admin2);
  await b.goto(`${WEB}/staff/role-changes`);
  await b.getByRole('button', { name: 'Approve' }).first().click();
  await b.getByLabel(/authenticator code/i).fill(await freshCode(PEOPLE.admin2));
  await b.getByRole('button', { name: 'Approve', exact: true }).last().click();
  await b.getByTestId('change-notice').waitFor();
  r.check('a different administrator approved it', /Approved/.test(await b.getByTestId('change-notice').innerText()));

  await b.goto(`${WEB}/staff/roles`);
  await b.getByTestId('roles-list').waitFor();
  r.check('roles table lists the 4 staff roles', (await b.getByTestId('role-card').count()) === 4);
  await axeCheck(b, '/staff/roles');

  const narrow = await signIn(browser, PEOPLE.admin, 360);
  await narrow.goto(`${WEB}/staff/users`);
  await narrow.getByTestId('users-table').waitFor();
  r.check('360 px: no horizontal page scroll', !(await narrow.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth)));
  r.check('no page errors', [sup, a, b, narrow].every((p) => p.errors.length === 0));
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
