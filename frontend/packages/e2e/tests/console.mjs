import { CONSOLE as WEB, SHOTS, USERS, apiCall, apiLogin, launch, totp, uniqueBin } from './lib.mjs';

const results = [];
const check = (name, ok, extra = '') => { results.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${extra ? '  -> ' + extra : ''}`); };

async function signIn(page, email, password) {
  await page.goto(`${WEB}/login`, { timeout: 120000 });
  await page.getByLabel('Staff or seller email').fill(email);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
}

(async () => {
  const browser = await launch();
  const errors = []; let noise = false;
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const p = await ctx.newPage();
  p.on('console', (m) => { if (m.type() === 'error' && !noise) errors.push(m.text()); });
  p.on('pageerror', (e) => errors.push('pageerror: ' + e.message));

  // 1 signed out
  await p.goto(`${WEB}/companies`, { timeout: 120000 });
  await p.waitForURL(/\/login$/);
  check('signed-out /companies redirects to /login', true);

  // 2 wrong password
  noise = true;
  await signIn(p, USERS.admin.email, 'WrongPass#1234');
  await p.locator('[role=alert]').filter({ hasText: /\S/ }).first().waitFor();
  check('wrong password shows the API error', true, await p.locator('[role=alert]').first().innerText());

  // 3 buyer is refused
  await signIn(p, USERS.buyer.email, USERS.buyer.password);
  await p.getByText('This portal is for MECHWARE staff and sellers.').waitFor();
  check('a storefront buyer cannot use the console', true);
  noise = false;
  const cookiesAfterBuyer = (await ctx.cookies()).some((c) => c.name === 'mw_console_session');
  check('no console session marker is left after the refused buyer', !cookiesAfterBuyer);

  // 4 seller: signs in, has no companies module, API refuses the page
  await signIn(p, USERS.sellerAlpha.email, USERS.sellerAlpha.password);
  await p.waitForURL(`${WEB}/`);
  await p.getByText('Operations console').first().waitFor();
  check('seller signs in; sidebar has no B2B module', (await p.getByText('B2B Companies & Credit').count()) === 0);
  noise = true;
  await p.goto(`${WEB}/companies`);
  await p.getByText('Your role does not allow this action.').waitFor();
  noise = false;
  check('seller opening /companies is refused by the API (403)', true);
  await p.getByRole('button', { name: 'Sign out' }).click();
  await p.waitForURL(/\/login$/);
  check('sign out returns to /login', true);

  // 5 buyer registers a company through the API (the storefront page was verified earlier)
  const buyerToken = await apiLogin(USERS.buyer);
  const bin = uniqueBin();
  const reg = await apiCall(buyerToken, 'POST', '/api/v1/companies', { legalName: 'Console Test Mills Ltd', tradeLicenceNo: 'TL-C-1', bin });
  const companyId = reg.json.data.id;
  check('buyer registered a pending company', reg.status === 201 && reg.json.data.status === 'pending');

  // 6 admin: password then TOTP
  await signIn(p, USERS.admin.email, USERS.admin.password);
  await p.getByLabel('6-digit authenticator code or single-use recovery code').waitFor();
  check('admin must pass the TOTP step', true);
  noise = true;
  await p.getByLabel('6-digit authenticator code or single-use recovery code').fill('000000');
  await p.getByRole('button', { name: 'Verify and unlock console' }).click();
  await p.locator('[role=alert]').filter({ hasText: /\S/ }).first().waitFor();
  check('wrong TOTP is rejected', true, await p.locator('[role=alert]').first().innerText());
  noise = false;
  // the failed attempt returns to the same step; re-enter password flow if needed
  if (await p.getByLabel('Password').count()) { await p.getByLabel('Password').fill(USERS.admin.password); await p.getByRole('button', { name: 'Sign in' }).click(); await p.getByLabel('6-digit authenticator code or single-use recovery code').waitFor(); }
  await p.getByLabel('6-digit authenticator code or single-use recovery code').fill(await totp(USERS.admin.totpSecret));
  await p.getByRole('button', { name: 'Verify and unlock console' }).click();
  await p.waitForURL(`${WEB}/`);
  await p.getByText('B2B Companies & Credit').first().waitFor();
  check('correct TOTP unlocks the console with the companies module', true);

  // 7 verify the company
  await p.getByRole('link', { name: /B2B Companies & Credit/ }).first().click();
  await p.waitForURL(/\/companies$/);
  const row = p.getByRole('row').filter({ hasText: 'Console Test Mills Ltd' });
  await row.waitFor();
  check('admin sees the pending company', (await row.innerText()).includes('Awaiting verification'));
  await row.getByRole('button', { name: 'Verify' }).click();
  await p.getByRole('dialog').getByRole('button', { name: 'Activate' }).click();
  await row.getByText('Active', { exact: true }).waitFor();
  const afterVerify = await apiCall(buyerToken, 'GET', `/api/v1/companies/${companyId}`);
  check('company became active (seen by the buyer through the API)', afterVerify.json.data.status === 'active');

  // 8 credit
  await row.getByRole('button', { name: 'Credit' }).click();
  await p.getByLabel('Terms', { exact: true }).selectOption('30');
  await p.getByRole('dialog').getByRole('button', { name: 'Save' }).click();
  await p.getByText('Net terms need a limit greater than zero.').waitFor();
  check('net terms without a limit are refused in the form', true);
  await p.getByLabel('Terms', { exact: true }).selectOption('0');
  await p.getByLabel('Credit limit (BDT)').fill('500');
  await p.getByRole('dialog').getByRole('button', { name: 'Save' }).click();
  await p.getByText('With no credit terms the limit must be zero.').waitFor();
  check('a limit with no terms is refused in the form', true);
  await p.getByLabel('Terms', { exact: true }).selectOption('30');
  await p.getByLabel('Credit limit (BDT)').fill('1,00,000');
  await p.getByRole('dialog').getByRole('button', { name: 'Save' }).click();
  await row.getByText('Net 30').waitFor();
  const afterCredit = await apiCall(buyerToken, 'GET', `/api/v1/companies/${companyId}`);
  check('net 30 / BDT 1,00,000 saved', afterCredit.json.data.creditTerms === 'net30' && afterCredit.json.data.creditLimit.minorUnits === 10000000);
  await p.screenshot({ path: `${SHOTS}/c1-companies-1280.png`, fullPage: true });

  // 9 suspend
  await row.getByRole('button', { name: 'Suspend' }).click();
  await p.getByRole('dialog').getByRole('button', { name: 'Suspend' }).click();
  await row.getByText('Suspended', { exact: true }).waitFor();
  const blocked = await apiCall(buyerToken, 'POST', `/api/v1/companies/${companyId}/cost-centres`, { code: 'X', name: 'X' });
  check('a suspended company is blocked from changes (409)', blocked.status === 409 && blocked.json.error?.code === 'COMPANY_SUSPENDED');
  check('a Reactivate action appears for a suspended company', (await row.getByRole('button', { name: 'Reactivate' }).count()) === 1);

  // 10 filter
  await p.getByLabel('Status', { exact: true }).selectOption('active');
  await p.getByText('No company has this status.').waitFor();
  check('status filter shows the empty state', true);
  await p.getByLabel('Status', { exact: true }).selectOption('');

  // 11 reload keeps session
  await p.reload();
  await p.getByRole('row').filter({ hasText: 'Console Test Mills Ltd' }).waitFor();
  check('reload restores the staff session', true);

  // 12 mobile
  const m = await browser.newContext({ viewport: { width: 360, height: 800 } });
  await m.addCookies(await ctx.cookies());
  const mp = await m.newPage();
  await mp.goto(`${WEB}/companies`);
  await mp.getByRole('row').filter({ hasText: 'Console Test Mills Ltd' }).waitFor();
  const overflow = await mp.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
  if (overflow) console.log('WIDE:', JSON.stringify(await mp.evaluate(() => [...document.querySelectorAll('body *')].filter(e => e.getBoundingClientRect().right > 362).slice(0, 8).map(e => e.tagName + '.' + String(e.className).slice(0, 70) + ' right=' + Math.round(e.getBoundingClientRect().right)))));
  check('360px console page has no horizontal page scroll', !overflow);
  await mp.screenshot({ path: `${SHOTS}/c2-companies-360.png`, fullPage: true });
  await m.close();

  // 13 sign out
  await p.getByRole('button', { name: 'Sign out' }).click();
  await p.waitForURL(/\/login$/);
  await p.goto(`${WEB}/companies`);
  await p.waitForURL(/\/login$/);
  check('after sign out the console is locked again', true);

  check('no unexpected console errors', errors.length === 0, errors.join(' | ').slice(0, 300));
  await browser.close();
  const failed = results.filter((r) => !r).length;
  console.log(`\n${results.length - failed}/${results.length} passed`);
  process.exit(failed ? 1 : 0);
})().catch((e) => { console.error('E2E crashed:', e.message.split('\n').slice(0, 8).join('\n')); process.exit(2); });
