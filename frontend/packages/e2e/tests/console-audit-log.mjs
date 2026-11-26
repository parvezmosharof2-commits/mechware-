// G5.3: console audit log list (`/audit/log`) and detail (`/audit/log/{id}`).
// SCRATCH DB only (E2E_SQL_CMD must name mw_*; it is only checked, this test sends no SQL). API on the scratch DB (Development) reached by the console
// through a CORS proxy; see docs/modules/G.md HANDOFF G5.3. support.e2e@ is a clone of the admin row with role "support" (same TOTP secret).
// Set E2E_AUDIT_LOG=1 to run; otherwise it exits 0 without doing anything.
// Real: every filter checked against the API as oracle, the URL round trip (reload, a second browser, bad URL values dropped, back), paging on a stable
// filter, a from-after-to dialog that sends nothing, the detail of a real row with a "[hidden]" value shown as a badge in words (and the BIN nowhere),
// the correlation link, Support refused (page text and API 403), axe 0, 360 px, and the 429 words after a real burst. Mocked: nothing.
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, CONSOLE as WEB, USERS, apiCall, apiLogin, launch, reporter, totp, uniqueBin } from './lib.mjs';

if (process.env.E2E_AUDIT_LOG !== '1') {
  console.log('SKIP  console audit log E2E (set E2E_AUDIT_LOG=1 to run it)');
  process.exit(0);
}
if (!process.env.E2E_SQL_CMD || !/mw_/.test(process.env.E2E_SQL_CMD)) throw new Error('E2E_SQL_CMD must name a scratch database (mw_*)');

const r = reporter();
const here = dirname(fileURLToPath(import.meta.url));
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
const sleep = (ms) => new Promise((res) => setTimeout(res, ms));
const overflow = (p) => p.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
// The API of the scratch run is started with RateLimiting__AuditReadPermitPerMinute=150 (so the test's own reads never hit it) and the burst goes over it.
const BURST = Number(process.env.E2E_AUDIT_BURST ?? 70);
const dhakaDay = (iso) => new Date(new Date(iso).getTime() + 6 * 3600 * 1000).toISOString().slice(0, 10);

const PASSWORD = USERS.admin.password;
const PEOPLE = {
  admin: USERS.admin,
  support: { email: 'support.e2e@mechware.com.bd', password: PASSWORD, totpSecret: USERS.admin.totpSecret }
};
const lastStep = new Map();
async function freshCode(person) {
  for (;;) {
    const step = Math.floor(Date.now() / 30000);
    if (step > (lastStep.get(person.email) ?? -1)) { lastStep.set(person.email, step); return totp(person.totpSecret); }
    await sleep(1000);
  }
}
async function apiToken(person) {
  let body;
  for (let attempt = 0; attempt < 8; attempt++) {
    const res = await fetch(`${API}/api/v1/auth/login`, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ identifier: person.email, password: person.password, totpCode: await freshCode(person) })
    });
    body = await res.json();
    if (res.ok) return body.data.accessToken;
    if (res.status !== 401) break;
    await sleep(5000);
  }
  throw new Error(`API login for ${person.email} failed: ${JSON.stringify(body)}`);
}
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
  page.on('pageerror', (e) => page.errors.push('pageerror: ' + e.message));
  return page;
}
const get = (token, path) => fetch(`${API}${path}`, { headers: { authorization: `Bearer ${token}` } }).then(async (res) => ({ status: res.status, json: await res.json().catch(() => ({})) }));

const isList = (x) => x.request().method() === 'GET' && /\/api\/v1\/admin\/audit\?/.test(x.url());
/** Runs `action` and waits for the list request it causes and for the screen to settle. */
async function andList(p, action) {
  const [resp] = await Promise.all([p.waitForResponse(isList, { timeout: 30000 }), action()]);
  await p.waitForLoadState('networkidle');
  await p.locator('[data-testid=audit-table],[data-testid=audit-empty]').first().waitFor();
  return resp;
}
const rowIds = async (p) => (await p.getByTestId('audit-row').locator('td:first-child a').evaluateAll((as) => as.map((a) => a.getAttribute('href').split('/').pop())));
const totalOf = async (p) => Number(/\((\d+) rows\)/.exec(await p.getByTestId('pager').innerText())?.[1] ?? NaN);
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

(async () => {
  // ---------------- setup through the real API
  const adminToken = await apiToken(PEOPLE.admin);
  const supportToken = await apiToken(PEOPLE.support);
  const sellerToken = await apiLogin(USERS.sellerAlpha);
  const buyer = await apiLogin(USERS.buyer);
  const buyerId = (await apiCall(buyer, 'GET', '/api/v1/users/me')).json.data.id;
  r.check('setup: Support and seller tokens get 403 on the audit list and a detail (real API)',
    (await get(supportToken, '/api/v1/admin/audit')).status === 403 && (await get(sellerToken, '/api/v1/admin/audit/00000000-0000-0000-0000-000000000001')).status === 403);

  // Rows by the buyer: each company registration writes several audited rows. Enough for a second page of 25.
  const bins = [];
  let buyerTotal = 0;
  for (let i = 0; i < 20 && buyerTotal < 27; i++) {
    const bin = uniqueBin();
    const reg = await apiCall(buyer, 'POST', '/api/v1/companies', { legalName: `Audit Log ${Date.now() % 100000}-${i} Ltd`, tradeLicenceNo: `TL-G53${i}`, bin });
    if (reg.status !== 201) throw new Error(`company setup failed ${reg.status}`);
    bins.push(bin);
    await apiCall(adminToken, 'POST', `/api/v1/admin/companies/${reg.json.data.id}/activate`);
    buyerTotal = (await get(adminToken, `/api/v1/admin/audit?actorId=${buyerId}&pageSize=1`)).json.data.totalCount;
  }
  r.check(`setup: the buyer's real actions wrote ${buyerTotal} audit rows (more than one page of 25)`, buyerTotal >= 27);

  const sample = (await get(adminToken, `/api/v1/admin/audit?actorId=${buyerId}&action=companies.created&pageSize=50`)).json.data.items;
  const created = sample[0];
  const withCorr = sample.find((x) => x.correlationId) ?? created;
  // A real row holding "[hidden]": the company's BIN is redacted by the server.
  let hiddenRow = null;
  for (const it of sample) { const d = (await get(adminToken, `/api/v1/admin/audit/${it.id}`)).json.data; if (JSON.stringify(d).includes('[hidden]')) { hiddenRow = d; break; } }
  r.check('setup: a real audit row with a "[hidden]" value exists (company BIN) and the API never sends the BIN', !!hiddenRow && !JSON.stringify(hiddenRow).includes(bins[0]) , hiddenRow ? '' : 'none found');

  const apiList = async (query) => (await get(adminToken, `/api/v1/admin/audit?${query}&pageSize=50`)).json.data;
  const browser = await launch();

  // ================= Support: refused
  {
    const p = await newPage(browser);
    await consoleSignIn(p, PEOPLE.support);
    await p.getByText('Operations console').first().waitFor();
    r.check('support sees no Audit log link', (await p.getByRole('link', { name: 'Audit log', exact: true }).count()) === 0);
    await p.goto(`${WEB}/audit/log`);
    await p.getByText('Your role does not allow this action (audit.view).').waitFor();
    r.check('support on /audit/log: the not-allowed text, no table, no filter form', (await p.getByTestId('audit-table').count()) === 0 && (await p.getByRole('search').count()) === 0);
    await p.goto(`${WEB}/audit/log/${created.id}`);
    await p.getByText('Your role does not allow this action (audit.view).').waitFor();
    r.check('support on a detail: the not-allowed text and no values', (await p.getByTestId('audit-diff').count()) === 0 && (await p.getByTestId('audit-facts').count()) === 0);
    await axeCheck(p, 'not allowed page');
    await p.context().close();
  }

  // ================= Admin: the list
  const s = await newPage(browser);
  await consoleSignIn(s, PEOPLE.admin);
  const listGets = [];
  s.on('request', (req) => { if (isList({ request: () => req, url: () => req.url() })) listGets.push(req.url()); });
  await s.getByRole('link', { name: 'Audit log', exact: true }).click();
  await s.waitForURL(`${WEB}/audit/log`);
  await s.locator('[data-testid=audit-table],[data-testid=audit-empty]').first().waitFor();
  r.check('the sidebar link opens /audit/log; the table has a caption, newest first, 25 rows at most',
    (await s.locator('caption').count()) === 1 && (await s.locator('th[scope=col]').count()) === 6 && (await s.getByTestId('audit-row').count()) <= 25);
  const times = await s.getByTestId('audit-row').locator('td:first-child').allInnerTexts();
  r.check('rows are in time order, newest first (Dhaka time shown)', times.every((t, i) => i === 0 || times[i - 1].split(',')[0] >= t.split(',')[0]) && /\d{4}-\d{2}-\d{2}/.test(times[0]));
  r.check('the actor column shows an email when the API gave one', (await s.getByTestId('audit-table').innerText()).includes('admin@mechware.com.bd'));
  await axeCheck(s, '/audit/log');

  // each filter against the API as oracle
  const fields = { actor: 'Actor id', action: 'Action (exact)', entity: 'Entity name (exact)', entityId: 'Entity id (exact)', correlation: 'Correlation id (exact)' };
  const cases = [
    { name: 'actor id', fill: { actor: buyerId }, query: `actorId=${buyerId}`, param: 'actor' },
    { name: 'action', fill: { action: 'companies.created' }, query: 'action=companies.created', param: 'action' },
    { name: 'entity name', fill: { entity: 'companies' }, query: 'entityName=companies', param: 'entity' },
    { name: 'entity id', fill: { entityId: created.entityId }, query: `entityId=${created.entityId}`, param: 'entityId' },
    { name: 'correlation id', fill: { correlation: withCorr.correlationId }, query: `correlationId=${withCorr.correlationId}`, param: 'correlation' }
  ];
  for (const c of cases) {
    await s.goto(`${WEB}/audit/log`);
    await s.locator('[data-testid=audit-table],[data-testid=audit-empty]').first().waitFor();
    for (const [k, v] of Object.entries(c.fill)) await s.getByLabel(fields[k], { exact: true }).fill(v);
    await andList(s, () => s.getByRole('button', { name: 'Apply filters' }).click());
    const oracle = await apiList(c.query);
    const ids = await rowIds(s);
    r.check(`filter ${c.name}: rows and total equal the API (${oracle.totalCount})`, same(ids, oracle.items.slice(0, 25).map((i) => i.id)) && (await totalOf(s)) === oracle.totalCount, `${ids.length} rows`);
    r.check(`filter ${c.name}: in the address (?${c.param}=) and "1 filter applied"`, new URL(s.url()).searchParams.get(c.param) === Object.values(c.fill)[0] && (await s.getByTestId('filter-count').innerText()) === '1 filter applied');
  }

  // dates: from/to are Dhaka days; stable ranges and the live day (bracket, the reads themselves add rows)
  const day = dhakaDay(created.createdAtUtc);
  await s.goto(`${WEB}/audit/log?actor=${buyerId}&from=${day}&to=${day}`);
  await s.locator('[data-testid=audit-table],[data-testid=audit-empty]').first().waitFor();
  const dayOracle = await apiList(`actorId=${buyerId}&from=${day}&to=${day}`);
  r.check(`date range (one Dhaka day) with the actor: rows and total equal the API (${dayOracle.totalCount})`, same(await rowIds(s), dayOracle.items.slice(0, 25).map((i) => i.id)) && (await totalOf(s)) === dayOracle.totalCount);
  r.check('the date inputs show the range from the address', (await s.getByLabel('From (Dhaka day)').inputValue()) === day && (await s.getByLabel('To (Dhaka day)').inputValue()) === day && (await s.getByTestId('filter-count').innerText()) === '3 filters applied');
  await s.goto(`${WEB}/audit/log?from=2001-01-01&to=2001-01-02`);
  await s.getByTestId('audit-empty').waitFor();
  r.check('an empty result says so in words and names the filters', (await s.getByTestId('audit-empty').innerText()).includes('No audit row matches these filters'));
  await axeCheck(s, '/audit/log empty result');

  // a filter problem sends nothing
  await s.goto(`${WEB}/audit/log`);
  await s.locator('[data-testid=audit-table]').waitFor();
  const before = listGets.length;
  await s.getByLabel('From (Dhaka day)').fill('2026-11-20');
  await s.getByLabel('To (Dhaka day)').fill('2026-11-01');
  await s.getByRole('button', { name: 'Apply filters' }).click();
  await s.getByTestId('filter-problem').waitFor();
  r.check('from after to: the problem is in words and no request is sent', (await s.getByTestId('filter-problem').innerText()).includes('after') && listGets.length === before && !s.url().includes('from='));
  await s.getByLabel('Actor id', { exact: true }).fill('not-an-id');
  await s.getByRole('button', { name: 'Apply filters' }).click();
  await s.getByLabel('From (Dhaka day)').fill('');
  await s.getByLabel('To (Dhaka day)').fill('');
  await s.getByRole('button', { name: 'Apply filters' }).click();
  r.check('a bad actor id: the problem is named and no request is sent', (await s.getByTestId('filter-problem').innerText()).includes('actor id') && listGets.length === before);

  // URL round trip
  const combo = `actor=${buyerId}&action=companies.created&entity=companies&from=${day}&to=${day}`;
  await s.goto(`${WEB}/audit/log?${combo}`);
  await s.locator('[data-testid=audit-table],[data-testid=audit-empty]').first().waitFor();
  const comboOracle = await apiList(`actorId=${buyerId}&action=companies.created&entityName=companies&from=${day}&to=${day}`);
  const comboIds = await rowIds(s);
  r.check('five filters in the address: the form is filled from it and the rows equal the API', same(comboIds, comboOracle.items.slice(0, 25).map((i) => i.id)) && (await s.getByLabel('Action (exact)').inputValue()) === 'companies.created' && (await s.getByTestId('filter-count').innerText()) === '5 filters applied');
  await s.reload();
  await s.locator('[data-testid=audit-table],[data-testid=audit-empty]').first().waitFor();
  r.check('reload keeps the filters and the rows', same(await rowIds(s), comboIds) && (await s.getByLabel('Entity name (exact)').inputValue()) === 'companies');
  const p2 = await newPage(browser);
  await consoleSignIn(p2, PEOPLE.admin);
  await p2.goto(`${WEB}/audit/log?${combo}`);
  await p2.locator('[data-testid=audit-table],[data-testid=audit-empty]').first().waitFor();
  r.check('the link opens the same result in a second browser (shared link)', same(await rowIds(p2), comboIds));
  await p2.context().close();
  await s.goto(`${WEB}/audit/log?actor=nope&from=2026-02-30&page=-3&action=companies.created`);
  await s.locator('[data-testid=audit-table],[data-testid=audit-empty]').first().waitFor();
  r.check('bad values in the address are dropped: only the valid filter applies', (await s.getByTestId('filter-count').innerText()) === '1 filter applied' && (await s.getByLabel('Actor id', { exact: true }).inputValue()) === '');
  await andList(s, () => s.getByRole('button', { name: 'Clear all filters' }).click());
  r.check('Clear all filters returns to the plain address', new URL(s.url()).search === '' && (await s.getByTestId('filter-count').innerText()) === 'No filter applied');

  // paging on a stable filter (the buyer's rows)
  await s.goto(`${WEB}/audit/log?actor=${buyerId}`);
  await s.locator('[data-testid=audit-table]').waitFor();
  const pageOne = await rowIds(s);
  const allBuyer = (await get(adminToken, `/api/v1/admin/audit?actorId=${buyerId}&pageSize=50`)).json.data;
  r.check(`page 1: 25 rows, the pager says "Page 1 of ${Math.ceil(allBuyer.totalCount / 25)}", rows equal the API's first 25`, pageOne.length === 25 && same(pageOne, allBuyer.items.slice(0, 25).map((i) => i.id)) && (await s.getByTestId('pager').innerText()).includes(`Page 1 of ${Math.ceil(allBuyer.totalCount / 25)}`));
  await andList(s, () => s.getByRole('button', { name: 'Next' }).click());
  const pageTwo = await rowIds(s);
  r.check('Next: ?page=2 in the address, the next rows (no overlap) equal the API', new URL(s.url()).searchParams.get('page') === '2' && same(pageTwo, allBuyer.items.slice(25, 50).map((i) => i.id)) && !pageTwo.some((x) => pageOne.includes(x)));
  await andList(s, () => s.goBack());
  r.check('the browser Back button returns to page 1', same(await rowIds(s), pageOne));
  r.check('the page-size cap is 50 on the API', (await get(adminToken, '/api/v1/admin/audit?pageSize=500')).json.data.pageSize === 50);

  // ================= the detail
  await s.goto(`${WEB}/audit/log?action=companies.created&entity=companies&actor=${buyerId}`);
  await s.locator('[data-testid=audit-table]').waitFor();
  await s.locator(`a[href="/audit/log/${hiddenRow.id}"]`).first().click();
  await s.getByTestId('audit-facts').waitFor();
  await s.getByTestId('audit-diff').waitFor();
  r.check('the detail of a real row: action, entity id and the actor are the API values', (await s.getByTestId('audit-action').innerText()) === hiddenRow.action && (await s.getByTestId('audit-facts').innerText()).includes(hiddenRow.entityId));
  const hiddenBadges = await s.getByTestId('hidden-value').count();
  const bodyText = await s.locator('main').innerText();
  r.check('a "[hidden]" value is a badge in words ("Hidden (secret)"), the raw marker and the BIN are not on the page', hiddenBadges >= 1 && !bodyText.includes('[hidden]') && !bodyText.includes(bins[0]), `${hiddenBadges} badges`);
  r.check('the values table has a caption, column and row headers, a summary in words', (await s.locator('table caption').count()) === 1 && (await s.locator('th[scope=col]').count()) === 4 && (await s.locator('th[scope=row]').count()) >= 3 && /\d+ changed, \d+ added, \d+ removed, \d+ unchanged/.test(await s.getByTestId('diff-summary').innerText()));
  await axeCheck(s, '/audit/log/{id}');
  await s.setViewportSize({ width: 360, height: 800 });
  r.check('360 px: no horizontal scroll on the detail', !(await overflow(s)));

  // correlation link back to a filtered list (a row that has one)
  const corrId = hiddenRow.correlationId ?? withCorr.correlationId;
  const corrRow = hiddenRow.correlationId ? hiddenRow : (await get(adminToken, `/api/v1/admin/audit/${withCorr.id}`)).json.data;
  await s.setViewportSize({ width: 1280, height: 900 });
  await s.goto(`${WEB}/audit/log/${corrRow.id}`);
  await s.getByTestId('audit-correlation').waitFor();
  await Promise.all([s.waitForResponse(isList), s.getByTestId('audit-correlation').getByRole('link').click()]);
  await s.locator('[data-testid=audit-table]').waitFor();
  const corrOracle = await apiList(`correlationId=${corrId}`);
  r.check(`the correlation id links to the list filtered by it (${corrOracle.totalCount} rows, equal the API)`, new URL(s.url()).searchParams.get('correlation') === corrId && (await s.getByLabel('Correlation id (exact)').inputValue()) === corrId && same(await rowIds(s), corrOracle.items.slice(0, 25).map((i) => i.id)));

  // unknown row
  await s.goto(`${WEB}/audit/log/00000000-0000-4000-8000-000000000999`);
  const nf = s.locator('[role=alert]').filter({ hasText: 'does not exist' });
  await nf.waitFor();
  r.check('an unknown row: "does not exist" in words, no Try again for a 404', (await nf.getByRole('button', { name: 'Try again' }).count()) === 0 && (await s.getByTestId('audit-facts').count()) === 0);
  await axeCheck(s, 'audit row not found');

  // integrity page links to the log
  await s.goto(`${WEB}/audit`);
  await s.getByRole('link', { name: 'open the audit log' }).click();
  await s.waitForURL(`${WEB}/audit/log`);
  r.check('the integrity page links to the audit log', true);

  // narrow screen on the list
  await s.setViewportSize({ width: 360, height: 800 });
  await s.goto(`${WEB}/audit/log?actor=${buyerId}`);
  await s.locator('[data-testid=audit-table]').waitFor();
  r.check('360 px: no horizontal scroll on the list (the table scrolls inside its box)', !(await overflow(s)));
  await axeCheck(s, '/audit/log at 360 px');

  // ================= 429: a real burst over the 60-a-minute limit
  const burst = await Promise.all(Array.from({ length: BURST }, () => get(adminToken, '/api/v1/admin/audit?pageSize=1')));
  const limited = burst.filter((x) => x.status === 429).length;
  r.check(`the API answers 429 after a burst of ${BURST} reads (${limited} refused)`, limited > 0);
  await s.setViewportSize({ width: 1280, height: 900 });
  await s.goto(`${WEB}/audit/log`);
  const tooMany = s.locator('[role=alert]').filter({ hasText: 'too often' });
  await tooMany.waitFor({ timeout: 30000 });
  r.check('the list shows the 429 words (too often, 60 a minute, wait) and a Try again button', (await tooMany.innerText()).includes('60 reads a minute') && (await tooMany.getByRole('button', { name: 'Try again' }).count()) === 1);
  await axeCheck(s, '/audit/log rate limited');

  r.check('no page errors', s.errors.length === 0, s.errors.join(' | '));
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
