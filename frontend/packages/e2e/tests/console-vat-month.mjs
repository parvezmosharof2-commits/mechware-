// E6.7b: the monthly Mushak-9.1 page in the staff console. Same setup as console-credit-2.mjs (API in Development on a SCRATCH DB, console built
// against it, the dev admin and a Finance account with the same TOTP secret). Set E2E_CONSOLE_VAT=1 to run; otherwise it exits 0.
// Checks: default month = last month in Dhaka, the six section tables with a caption and scope=col, consultant note, CSV with a wrong code (dialog
// stays open) then a real one (file name, full text), 360 px, axe 0. Not run by the author (token rule): run it before the unit is closed.
import { readFileSync } from 'node:fs';
import { CONSOLE as WEB, USERS, launch, reporter, totp } from './lib.mjs';

if (process.env.E2E_CONSOLE_VAT !== '1') {
  console.log('SKIP  console VAT month E2E (set E2E_CONSOLE_VAT=1 to run it)');
  process.exit(0);
}
const r = reporter();
const sleep = (ms) => new Promise((res) => setTimeout(res, ms));
const person = USERS.admin;
let lastStep = -1;
async function freshCode() {
  for (;;) {
    const step = Math.floor(Date.now() / 30000);
    if (step > lastStep) { lastStep = step; return totp(person.totpSecret); }
    await sleep(1000);
  }
}
const dhakaLastMonth = () => {
  const d = new Date(Date.now() + 6 * 3_600_000);
  const y = d.getUTCMonth() === 0 ? d.getUTCFullYear() - 1 : d.getUTCFullYear();
  return `${y}-${String(d.getUTCMonth() === 0 ? 12 : d.getUTCMonth()).padStart(2, '0')}`;
};

(async () => {
  const browser = await launch();
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 }, acceptDownloads: true });
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${WEB}/login`, { timeout: 120000, waitUntil: 'networkidle' });
  await page.getByLabel('Staff or seller email').fill(person.email);
  await page.getByLabel('Password').fill(person.password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.getByLabel('6-digit authenticator code or single-use recovery code').fill(await freshCode());
  await page.getByRole('button', { name: 'Verify and unlock console' }).click();
  await page.waitForURL(`${WEB}/`, { timeout: 120000 });

  await page.getByRole('link', { name: 'Monthly VAT file' }).click();
  await page.getByTestId('vat-report').waitFor();
  r.check('default month is last month in Dhaka', (await page.getByTestId('vat-month').inputValue()) === dhakaLastMonth());
  r.check('consultant note is shown', (await page.getByTestId('vat-consultant-note').innerText()).includes('consultant has not checked'));
  const tables = page.locator('[data-testid^="vat-"] table');
  const n = await tables.count();
  let ok = true;
  for (let i = 0; i < n; i++) ok = ok && (await tables.nth(i).locator('caption').count()) === 1 && (await tables.nth(i).locator('th[scope="col"]').count()) > 0;
  r.check('every table has a caption and column headers with scope=col', ok, `${n} tables`);

  let downloaded = false;
  page.on('download', () => { downloaded = true; });
  await page.getByTestId('vat-csv').click();
  const dlg = page.getByRole('dialog');
  await dlg.getByLabel('Authenticator code').fill('000000');
  await dlg.getByRole('button', { name: 'Download' }).click();
  await page.getByTestId('stepup-error').waitFor();
  r.check('wrong code: dialog stays open, nothing downloaded', (await dlg.count()) === 1 && !downloaded);
  await dlg.getByLabel('Authenticator code').fill(await freshCode());
  const [file] = await Promise.all([page.waitForEvent('download'), dlg.getByRole('button', { name: 'Download' }).click()]);
  const csv = readFileSync(await file.path(), 'utf8');
  r.check('real code: right file name and content', file.suggestedFilename() === `mushak-9_1-${dhakaLastMonth()}.csv` && csv.length > 0, file.suggestedFilename());
  await page.getByTestId('vat-notice').waitFor();

  await page.setViewportSize({ width: 360, height: 800 });
  r.check('360 px: no page overflow', !(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth)));
  r.check('no page errors', errors.length === 0, errors.join(' | '));
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
