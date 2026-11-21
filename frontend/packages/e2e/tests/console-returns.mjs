// F2.4 console returns desk: /returns list (status, order number and company filters in the address, paging text, sidebar item) and /returns/[id] detail
// (actions by status, gated by returns.manage; read-only with a reason for Finance, API 403), approve -> mark received -> inspect (per-line limits refused on
// screen, restock really adds stock), reject with a reason (5-500), a stale save (RETURN_CHANGED, reload + words), RETURN_INVALID_TRANSITION (real answer to a
// request whose rowVersion was corrected), restockPending as a warning, link to the order, axe 0, 360 px.
// SCRATCH DB only (see returns-lib.mjs). Set E2E_CONSOLE_RETURNS=1 to run; otherwise it exits 0 without doing anything.
// Mocked: the restockPending flag in one GET answer (it needs a crashed restock to happen for real) and the rowVersion in one POST body (the way a row that is
// current but in the wrong state is reached, as in console-fulfilment.mjs). Everything else is the real API.
import { API, CONSOLE as WEB, USERS, apiLogin, launch } from './lib.mjs';
import { PEOPLE, VARIANT_A, apiToken, axeCheck, deliveredOrder, freshCode, lineOf, move, noOverflow, openReturn, r, requireScratch, send, setupCompany, sql, staffReturn, tag } from './returns-lib.mjs';

if (process.env.E2E_CONSOLE_RETURNS !== '1') {
  console.log('SKIP  console returns E2E (set E2E_CONSOLE_RETURNS=1 to run it)');
  process.exit(0);
}
requireScratch();

async function consoleSignIn(page, person) {
  await page.goto(`${WEB}/login`, { timeout: 120000, waitUntil: 'networkidle' });
  await page.getByLabel('Staff or seller email').fill(person.email);
  await page.getByLabel('Password').fill(person.password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.getByLabel('6-digit authenticator code or single-use recovery code').fill(await freshCode(person));
  await page.getByRole('button', { name: 'Verify and unlock console' }).click();
  await page.waitForURL(`${WEB}/`, { timeout: 120000 });
}
async function newPage(browser, width = 1280) {
  const ctx = await browser.newContext({ viewport: { width, height: 900 } });
  const page = await ctx.newPage();
  page.errors = [];
  page.on('pageerror', (e) => page.errors.push(e.message));
  return page;
}
const stockOf = (variant) => Number(sql(`select coalesce(sum(on_hand),0) from inventory_items where variant_id = '${variant}';`).trim());

(async () => {
  const admin = await apiToken(PEOPLE.admin);
  const buyer = await apiLogin(USERS.buyer);
  const coA = await setupCompany(admin, buyer, 'Returns Desk A');
  const coB = await setupCompany(admin, buyer, 'Returns Desk B');
  const mk = async (co, qty, opts = {}) => {
    const order = await deliveredOrder(admin, buyer, co);
    const ret = (await openReturn(buyer, order, qty, { company: co, ...opts })).json.data;
    return { order, ret };
  };
  const full = await mk(coA, 2);
  const rej = await mk(coA, 1, { reasonCode: 'other', note: 'Wrong voltage' });
  const stale = await mk(coA, 1);
  const inval = await mk(coA, 1);
  const fin = await mk(coA, 1);
  const other = await mk(coB, 1);
  r.check('setup: two companies, six delivered orders and six requested returns, through the real API', [full, rej, stale, inval, fin, other].every((x) => !!x.ret?.id));

  const sup = await apiToken(PEOPLE.support);
  const finTok = await apiToken(PEOPLE.finance);
  const browser = await launch();
  const s = await newPage(browser);
  const posts = [];
  s.on('request', (q) => { if (q.method() === 'POST' && /\/api\/v1\/admin\/returns\/[^/]+\/(approve|reject|received|inspect)$/.test(q.url())) posts.push(q.url().split('/').pop()); });
  await consoleSignIn(s, PEOPLE.support);

  // ================= list
  await s.goto(`${WEB}/returns`, { waitUntil: 'networkidle' });
  await s.getByTestId('returns-table').waitFor();
  const rowsText = async () => (await s.getByTestId('return-row').allInnerTexts()).join('\n');
  const numbers = [full, rej, stale, inval, fin, other].map((x) => x.order.orderNumber);
  r.check('list: every new return is a row with its order number', (await Promise.all(numbers.map(async (n) => (await rowsText()).includes(n)))).every(Boolean));
  r.check('sidebar: a "Returns" item is there for Support', (await s.getByRole('navigation', { name: 'Modules' }).getByRole('link', { name: /^Returns/ }).count()) === 1);
  await axeCheck(s, 'returns list (1280)');
  const url = () => new URL(s.url());
  const filtered = async (act) => { await act(); await s.waitForLoadState('networkidle'); await s.getByTestId('filter-count').waitFor(); await sleepShort(); };
  const sleepShort = () => new Promise((res) => setTimeout(res, 700));

  await s.getByTestId('filter-status').selectOption('approved');
  await filtered(() => s.getByRole('button', { name: 'Apply filters' }).click());
  const approvedNow = (await send(sup, 'GET', '/api/v1/admin/returns?status=approved&page=1&pageSize=20')).json.data.totalCount;
  r.check('status filter: address holds status=approved and the table matches the API', url().searchParams.get('status') === 'approved' && (approvedNow === 0 ? (await s.getByTestId('returns-empty').count()) === 1 : (await s.getByTestId('return-row').count()) === approvedNow), `api ${approvedNow}`);
  await s.getByTestId('filter-status').selectOption('requested');
  await filtered(() => s.getByRole('button', { name: 'Apply filters' }).click());
  const requestedNow = (await send(sup, 'GET', '/api/v1/admin/returns?status=requested&page=1&pageSize=20')).json.data.items.length;
  r.check('status "requested": the six new returns are in it, rows match the API', url().searchParams.get('status') === 'requested' && (await s.getByTestId('return-row').count()) === requestedNow && (await rowsText()).includes(full.order.orderNumber), `${requestedNow}`);
  await filtered(() => s.getByRole('button', { name: 'Clear all filters' }).click());
  await s.getByLabel('Order number starts with').fill(stale.order.orderNumber);
  await filtered(() => s.getByRole('button', { name: 'Apply filters' }).click());
  r.check('order number filter: one row, the right order, in the address', (await s.getByTestId('return-row').count()) === 1 && (await rowsText()).includes(stale.order.orderNumber) && url().searchParams.get('orderNumber') === stale.order.orderNumber);
  await filtered(() => s.reload());
  r.check('reload keeps the filter (it lives in the address)', (await s.getByLabel('Order number starts with').inputValue()) === stale.order.orderNumber && (await s.getByTestId('return-row').count()) === 1);
  await filtered(() => s.getByRole('button', { name: 'Clear all filters' }).click());
  await s.getByRole('button', { name: 'Choose a company' }).click();
  await s.getByLabel('Company', { exact: true }).fill(coB.legalName);
  await s.getByTestId('company-list').waitFor();
  await s.getByTestId('company-list').selectOption({ label: coB.legalName });
  await s.getByTestId('company-chosen').waitFor();
  await filtered(() => s.getByRole('button', { name: 'Apply filters' }).click());
  r.check('company filter: chip with the legal name, address holds the id, only company B\'s return', (await s.getByTestId('company-chosen').innerText()).includes(coB.legalName) && url().searchParams.get('companyId') === coB.companyId && (await s.getByTestId('return-row').count()) === 1 && (await rowsText()).includes(other.order.orderNumber));
  await filtered(() => s.getByRole('button', { name: 'Clear all filters' }).click());
  await s.getByLabel('Order number starts with').fill('NOPE-0000');
  await filtered(() => s.getByRole('button', { name: 'Apply filters' }).click());
  r.check('no match: an empty state in words, not an error', (await s.getByTestId('returns-empty').innerText()).includes('No return matches') && (await s.getByTestId('return-row').count()) === 0);
  await filtered(() => s.getByRole('button', { name: 'Clear all filters' }).click());

  // ================= full path
  await s.goto(`${WEB}/returns/${full.ret.id}`, { waitUntil: 'networkidle' });
  await s.getByTestId('return-order').waitFor();
  const status = () => s.getByTestId('return-status').innerText();
  r.check('Requested: Approve and Reject are there, no Mark received, no inspect form; the order link points at the order',
    (await status()).includes('Requested') && (await s.getByTestId('approve-button').count()) === 1 && (await s.getByTestId('reject-button').count()) === 1 && (await s.getByTestId('received-button').count()) === 0 &&
    (await s.getByTestId('inspect-form').count()) === 0 && (await s.getByTestId('order-link').getAttribute('href')) === `/orders/${full.order.id}`);
  await axeCheck(s, 'return detail, requested (1280)');
  await s.getByTestId('approve-button').click();
  await s.getByTestId('return-notice').waitFor();
  r.check('Approve: notice in words, status Approved, next button is "Mark goods received", one POST', (await status()).includes('Approved') && (await s.getByTestId('received-button').innerText()) === 'Mark goods received' && (await s.getByTestId('approve-button').count()) === 0 && posts.join() === 'approve');
  await s.getByTestId('received-button').click();
  await s.getByTestId('inspect-form').waitFor();
  r.check('Mark goods received: status Received, the inspect form appears with one fieldset per line', (await status()).includes('received') && (await s.getByTestId('inspect-line').count()) === 1 && posts.join() === 'approve,received');
  await axeCheck(s, 'return detail with the inspect form (1280)');
  const sku = (await staffReturn(admin, full.ret.id)).lines[0].sku;
  const before = posts.length;
  await s.getByTestId(`accepted-${sku}`).fill('5');
  await s.getByTestId('inspect-submit').click();
  await s.getByText('At most 2, the number the buyer asked to return.').waitFor();
  await s.getByTestId(`accepted-${sku}`).fill('1');
  await s.getByTestId(`restock-${sku}`).fill('2');
  await s.getByTestId('inspect-submit').click();
  await s.getByText('At most 1, the number accepted.').waitFor();
  await s.getByTestId(`restock-${sku}`).fill('');
  await s.getByTestId('inspect-submit').click();
  r.check('inspect refusals on screen: accepted > asked, restock > accepted, blank number; nothing sent', (await s.getByText('Enter a whole number, 0 or more.').count()) >= 1 && posts.length === before);
  await axeCheck(s, 'inspect form with errors (1280)');
  const stockBefore = stockOf(VARIANT_A);
  await s.getByTestId(`restock-${sku}`).fill('1');
  await s.getByTestId(`note-${sku}`).fill('one fine');
  await s.getByTestId('inspect-submit').click();
  await s.getByTestId('step-hint').waitFor();
  const line = s.getByTestId('return-line').first();
  r.check('inspect saved: status Inspected, accepted 1, restocked 1, internal note shown, no action left, hint in words',
    (await status()).includes('Inspected') && (await s.getByTestId('line-accepted').innerText()) === '1' && (await s.getByTestId('line-restocked').innerText()) === '1' && (await line.innerText()).includes('one fine') &&
    (await s.getByTestId('step-hint').innerText()).includes('later step') && (await s.getByTestId('inspect-form').count()) === 0);
  r.check('the restock really put 1 unit back on the shelf', stockOf(VARIANT_A) === stockBefore + 1, `${stockBefore} -> ${stockOf(VARIANT_A)}`);
  r.check('no restock warning when nothing is pending', (await s.getByTestId('restock-pending').count()) === 0 && !(await staffReturn(admin, full.ret.id)).restockPending);
  await axeCheck(s, 'return detail, inspected (1280)');
  // restockPending (mocked: the flag in one answer)
  await s.route(`**/api/v1/admin/returns/${full.ret.id}`, async (route) => {
    const res = await route.fetch();
    const j = await res.json();
    j.data.restockPending = true;
    await route.fulfill({ response: res, json: j });
  });
  await s.reload({ waitUntil: 'networkidle' });
  await s.getByTestId('restock-pending').waitFor();
  r.check('restockPending (mocked answer): a warning in words, role alert, says it finishes by itself', (await s.getByTestId('restock-pending').innerText()).includes('not back on the shelf yet') && (await s.getByTestId('restock-pending').getAttribute('role')) === 'alert');
  await axeCheck(s, 'return detail with the restock warning (1280)');
  await s.unroute(`**/api/v1/admin/returns/${full.ret.id}`);
  await s.getByTestId('order-link').click();
  await s.waitForURL(`${WEB}/orders/${full.order.id}`);
  r.check('the link opens the order detail', true);

  // ================= reject
  await s.goto(`${WEB}/returns/${rej.ret.id}`, { waitUntil: 'networkidle' });
  await s.getByTestId('reject-button').click();
  const dlg = s.getByRole('dialog');
  await dlg.waitFor();
  const n = posts.length;
  await dlg.getByTestId('reason-input').fill('no');
  await dlg.getByTestId('reason-confirm').click();
  await sleepShort();
  r.check('reject: a too short reason is refused in the dialog, nothing sent', (await dlg.count()) === 1 && posts.length === n && (await dlg.innerText()).match(/5/) !== null);
  await axeCheck(s, 'reject dialog (1280)');
  await dlg.getByTestId('reason-input').fill('Photos show installation damage');
  await dlg.getByTestId('reason-confirm').click();
  await s.getByTestId('rejection-reason').waitFor();
  r.check('reject with a reason: status Rejected, the reason shown, no action left', (await status()).includes('Rejected') && (await s.getByTestId('rejection-reason').innerText()).includes('Photos show installation damage') && (await s.getByTestId('approve-button').count()) === 0 && (await s.getByTestId('step-hint').count()) === 1);
  const buyerView = (await send(buyer, 'GET', `/api/v1/returns/${rej.ret.id}`)).json.data;
  r.check('the buyer API shows the same status and reason', buyerView.status === 'rejected' && buyerView.rejectionReason === 'Photos show installation damage');

  // ================= stale save
  await s.goto(`${WEB}/returns/${stale.ret.id}`, { waitUntil: 'networkidle' });
  await s.getByTestId('approve-button').waitFor();
  await move(admin, stale.ret.id, 'approve');
  await s.getByTestId('approve-button').click();
  await s.getByTestId('return-alert').waitFor();
  const staleText = await s.getByTestId('return-alert').innerText();
  await s.getByTestId('received-button').waitFor();
  r.check('stale rowVersion: 409 RETURN_CHANGED explained, the page reloaded to Approved with the new button', staleText.includes('Someone changed this return') && (await status()).includes('Approved') && (await s.getByTestId('approve-button').count()) === 0);

  // ================= wrong state with a current rowVersion
  await s.goto(`${WEB}/returns/${inval.ret.id}`, { waitUntil: 'networkidle' });
  await s.getByTestId('approve-button').waitFor();
  const wd = await send(buyer, 'POST', `/api/v1/returns/${inval.ret.id}/withdraw`);
  const current = (await staffReturn(admin, inval.ret.id)).rowVersion;
  await s.route(`**/api/v1/admin/returns/${inval.ret.id}/approve`, (route) => route.continue({ postData: JSON.stringify({ rowVersion: current }) }));
  await s.getByTestId('approve-button').click();
  await s.getByTestId('return-alert').waitFor();
  const invalText = await s.getByTestId('return-alert').innerText();
  await s.getByTestId('step-hint').waitFor();
  r.check('withdrawn meanwhile: 409 RETURN_INVALID_TRANSITION explained, reloaded to Withdrawn with no action', wd.status === 200 && invalText.includes('no longer in the state') && (await status()).includes('Withdrawn'));
  await s.unroute(`**/api/v1/admin/returns/${inval.ret.id}/approve`);
  r.check('no page errors (Support)', s.errors.length === 0, s.errors.join(' | '));

  // ================= Finance: read-only
  const f = await newPage(browser);
  await consoleSignIn(f, PEOPLE.finance);
  await f.goto(`${WEB}/returns/${fin.ret.id}`, { waitUntil: 'networkidle' });
  await f.getByTestId('manage-hint').waitFor();
  const hint = await f.getByTestId('manage-hint').innerText();
  r.check('Finance: the return is readable, no action buttons, the hint names returns.manage', hint.includes('returns.manage') && (await f.getByTestId('approve-button').count()) === 0 && (await f.getByTestId('reject-button').count()) === 0 && (await f.getByTestId('return-order').innerText()) === fin.order.orderNumber);
  const cur = (await staffReturn(admin, fin.ret.id)).rowVersion;
  const denied = await send(finTok, 'POST', `/api/v1/admin/returns/${fin.ret.id}/approve`, { rowVersion: cur });
  r.check('Finance: the API answers 403 to an approve', denied.status === 403, `${denied.status}`);
  r.check('Finance can read the list through the API (orders.view_all)', (await send(finTok, 'GET', '/api/v1/admin/returns?page=1&pageSize=5')).status === 200);
  await axeCheck(f, 'return detail, read-only (1280)');
  await f.goto(`${WEB}/returns`, { waitUntil: 'networkidle' });
  await f.getByTestId('returns-table').waitFor();
  r.check('Finance sees the list', (await f.getByTestId('return-row').count()) >= 1);

  // ================= 360 px
  await s.setViewportSize({ width: 360, height: 740 });
  for (const [label, path, wait] of [['list', '/returns', 'returns-table'], ['detail, approved', `/returns/${stale.ret.id}`, 'received-button'], ['detail, inspected', `/returns/${full.ret.id}`, 'step-hint']]) {
    await s.goto(`${WEB}${path}`, { waitUntil: 'networkidle' });
    await s.getByTestId(wait).waitFor();
    await axeCheck(s, `returns ${label} (360)`);
    r.check(`360 px: no horizontal scroll on the returns ${label}`, await noOverflow(s));
  }
  // inspect form at 360 px: a fresh received return
  const narrow = await mk(coA, 2);
  await move(admin, narrow.ret.id, 'approve');
  await move(admin, narrow.ret.id, 'received');
  await s.goto(`${WEB}/returns/${narrow.ret.id}`, { waitUntil: 'networkidle' });
  await s.getByTestId('inspect-form').waitFor();
  await axeCheck(s, 'inspect form (360)');
  r.check('360 px: no horizontal scroll with the inspect form', await noOverflow(s));
  r.check('no page errors (all)', s.errors.length === 0 && f.errors.length === 0, [...s.errors, ...f.errors].join(' | '));
  await browser.close();
  console.log(`scratch tag ${tag}`);
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
