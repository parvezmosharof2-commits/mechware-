import { WEB, SHOTS, USERS, launch, uniqueBin } from './lib.mjs';

const results = [];
const check = (name, ok, extra = '') => { results.push([ok, name, extra]); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${extra ? '  -> ' + extra : ''}`); };

async function login(page, email, password) {
  await page.getByLabel(/Corporate Email or BD Phone/).fill(email);
  await page.locator('#auth-pw').fill(password);
  await page.getByRole('button', { name: 'Sign In to MECHWARE' }).click();
}

(async () => {
  const browser = await launch();
  const errors = [];
  let expectNoise = false; // negative-path steps legitimately produce 4xx network logs
  const ctxA = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const a = await ctxA.newPage();
  a.on('console', (m) => { if (m.type() === 'error' && !expectNoise) errors.push('A console: ' + m.text()); });
  a.on('pageerror', (e) => errors.push('A pageerror: ' + e.message));

  // 1. signed-out visit is redirected to login with returnTo
  await a.goto(`${WEB}/account/company`, { timeout: 120000 });
  await a.waitForURL(/auth\/login/);
  check('signed-out /account/company redirects to login with returnTo', /returnTo=%2Faccount%2Fcompany/.test(a.url()), a.url());

  // 2. wrong password shows the API error, no session
  expectNoise = true;
  await login(a, USERS.sellerBeta.email, 'WrongPass#1234');
  const alertBox = a.locator('[role=alert]').filter({ hasText: /\S/ }).first();
  await alertBox.waitFor();
  check('wrong password shows an API error', true, await alertBox.innerText());
  expectNoise = false;

  // 3. real login returns to the company list (empty state)
  await a.locator('#auth-pw').fill(USERS.sellerBeta.password);
  await a.getByRole('button', { name: 'Sign In to MECHWARE' }).click();
  await a.waitForURL(`${WEB}/account/company`);
  await a.getByText('No company account yet').waitFor();
  check('login redirects back and shows empty company list', true);
  await a.screenshot({ path: `${SHOTS}/01-empty-1280.png` });

  // 4. register: invalid BIN is caught on the client
  await a.getByRole('link', { name: 'Register a company' }).first().click();
  await a.waitForURL(/company\/register/);
  await a.getByLabel('Legal name').fill('Gazipur Textile Mills Ltd');
  await a.getByLabel('Trade licence number').fill('TL-2020-1234');
  await a.getByLabel('BIN (13 digits)').fill('123');
  await a.getByRole('button', { name: 'Register company' }).click();
  await a.getByText('BIN must be exactly 13 digits.').waitFor();
  check('invalid BIN shows a field error without calling the API', true);

  // 5. valid registration lands on the dashboard (pending)
  const bin = uniqueBin();
  await a.getByLabel('BIN (13 digits)').fill(bin);
  await a.getByRole('button', { name: 'Register company' }).click();
  await a.waitForURL(/account\/company\/[0-9a-f-]{36}$/);
  const companyUrl = a.url();
  await a.getByText('Awaiting verification').first().waitFor();
  check('registration opens the dashboard with pending status', true, companyUrl);

  // 6. duplicate BIN is a conflict shown to the user
  expectNoise = true;
  const b2 = await ctxA.newPage();
  await b2.goto(`${WEB}/account/company/register`);
  await b2.getByLabel('Legal name').fill('Copycat Ltd');
  await b2.getByLabel('Trade licence number').fill('TL-9999');
  await b2.getByLabel('BIN (13 digits)').fill(bin);
  await b2.getByRole('button', { name: 'Register company' }).click();
  const dup = b2.locator('[role=alert]').filter({ hasText: /BIN/i }).first();
  await dup.waitFor();
  check('duplicate BIN is rejected with a clear message', true, await dup.innerText());
  await b2.close();
  expectNoise = false;

  // 7. add a delivery site
  await a.getByRole('button', { name: 'Add' }).first().click();
  await a.getByLabel('Site name').fill('Gazipur Plant');
  await a.getByLabel('Contact person').fill('Sabbir');
  await a.getByLabel('Contact phone').fill('01711000000');
  await a.getByLabel('Address line 1').fill('Plot 5, BSCIC Industrial Area');
  await a.getByLabel('Upazila').fill('Gazipur Sadar');
  await a.getByLabel('District').fill('Gazipur');
  await a.getByLabel('Division').fill('Dhaka');
  await a.getByRole('dialog').getByRole('button', { name: 'Save' }).click();
  await a.getByText('Gazipur Plant').waitFor();
  check('site is added and flagged as default (first site)', await a.getByText('Default', { exact: true }).isVisible());
  await a.screenshot({ path: `${SHOTS}/02-dashboard-sites-1280.png`, fullPage: true });

  // 8. cost centre
  await a.getByRole('tab', { name: 'Cost centres' }).click();
  await a.getByRole('button', { name: 'Add' }).first().click();
  await a.getByLabel('Code').fill('prod');
  await a.getByLabel('Name').fill('Production');
  await a.getByLabel(/Monthly budget/).fill('250000');
  await a.getByRole('dialog').getByRole('button', { name: 'Save' }).click();
  await a.getByText('PROD', { exact: true }).waitFor();
  check('cost centre added (code upper-cased, budget formatted)', await a.getByText(/৳2,50,000/).isVisible());

  // 9. members tab visible to admin; user id validation
  await a.getByRole('tab', { name: 'Members' }).click();
  await a.getByRole('button', { name: 'Add' }).first().click();
  await a.getByLabel('User ID').fill('not-a-uuid');
  await a.getByRole('dialog').getByRole('button', { name: 'Save' }).click();
  await a.getByText('Enter a valid user ID (UUID).').waitFor();
  check('member form validates the user id', true);
  await a.keyboard.press('Escape');

  // 10. last admin cannot be removed (API conflict surfaces in UI)
  expectNoise = true;
  await a.getByRole('button', { name: 'Remove' }).first().click();
  await a.getByRole('dialog').getByRole('button', { name: 'Remove' }).click();
  await a.getByText('A company must keep at least one active admin.').waitFor();
  check('removing the last admin is refused with the API message', true);
  expectNoise = false;
  await a.keyboard.press('Escape');

  // 11. reload keeps the session (restored from the httpOnly refresh cookie)
  await a.reload();
  await a.getByText('Gazipur Textile Mills Ltd').first().waitFor();
  check('page reload restores the session', true);

  // 12. another user (different tenant) cannot see this company
  const ctxB = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const b = await ctxB.newPage();
  b.on('pageerror', (e) => errors.push('B pageerror: ' + e.message));
  await b.goto(`${WEB}/auth/login`);
  await login(b, USERS.sellerAlpha.email, USERS.sellerAlpha.password);
  await b.waitForURL(`${WEB}/`);
  await b.goto(companyUrl);
  await b.getByText('This company was not found, or you are not a member of it.').waitFor();
  const bodyText = await b.locator('body').innerText();
  check('outsider gets "not found" and none of the company data', !bodyText.includes('Gazipur Textile Mills Ltd') && !bodyText.includes(bin));
  await b.screenshot({ path: `${SHOTS}/03-outsider-1280.png` });
  await ctxB.close();

  // 13. mobile + Bangla
  const ctxM = await browser.newContext({ viewport: { width: 360, height: 800 } });
  await ctxM.addCookies(await ctxA.cookies());
  const m = await ctxM.newPage();
  const apiLog = [];
  m.on('response', (r) => { if (r.url().includes('/api/v1/')) apiLog.push(`${r.status()} ${r.request().method()} ${r.url().replace(/^https?:\/\/[^/]+/, '')}`); });
  try {
    await m.goto(`${WEB}/bn/account/company`);
    // The page title renders without a session, so wait for the signed-in company row instead.
    await m.getByText('Gazipur Textile Mills Ltd').first().waitFor();
    await m.screenshot({ path: `${SHOTS}/04-bn-list-360.png`, fullPage: true });
    await m.goto(companyUrl.replace(WEB, WEB + '/bn'));
    await m.getByText('যাচাইয়ের অপেক্ষায়').first().waitFor();
    const overflow = await m.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
    check('Bangla dashboard renders at 360px without horizontal scroll', !overflow);
    await m.screenshot({ path: `${SHOTS}/05-bn-dashboard-360.png`, fullPage: true });
  } catch (e) {
    console.log('Bangla step failed. URL:', m.url());
    console.log('API calls seen:', JSON.stringify(apiLog));
    console.log('Page text:', (await m.locator('body').innerText().catch(() => '')).slice(0, 400));
    await m.screenshot({ path: `${SHOTS}/bn-failure-360.png`, fullPage: true }).catch(() => {});
    throw e;
  }
  await ctxM.close();

  check('no console errors / page errors', errors.length === 0, errors.join(' | ').slice(0, 400));
  await browser.close();
  const failed = results.filter((r) => !r[0]).length;
  console.log(`\n${results.length - failed}/${results.length} passed`);
  process.exit(failed ? 1 : 0);
})().catch((e) => { console.error('E2E crashed:', e.message.split('\n').slice(0, 6).join('\n')); process.exit(2); });
