// D4.15: the console quote desk: RFQ inbox (filters, SLA clock in words, assigned-to-me default), RFQ detail (matched / obsolete with a successor /
// unmatched lines, assign, unassign, close with a reason, create a quote from it), quotes list (computed status, credit terms, on-hold badge),
// quote detail (versions, lines, totals, cost only when the API sends it), revise, close, "new quote for a company", permissions, conflict of interest.
// SCRATCH DB only. Needs: the API (Development) on a scratch DB mw_* (migrated, catalogue seeded, demo accounts), the console built against it, a CORS proxy
// in front of the API, staff cloned from the admin row with the same TOTP secret: support.e2e@ and support2.e2e@ (support: quotes.view + quotes.edit),
// finance.a.e2e@ (quotes.view only), moderator.e2e@ (no quotes permission), admin2.e2e@ (admin: also pricing.view_cost), and E2E_SQL_CMD = a command that
// reads SQL on stdin against the SCRATCH database, e.g. `docker exec -i mechware_postgres psql -U mechware -d mw_d415 -At -q -v ON_ERROR_STOP=1`.
// Set E2E_CONSOLE_QUOTES=1 to run; otherwise it exits 0 without doing anything. Docs: docs/modules/D4.md HANDOFF D4.15.
// Real: every success path, permissions, the conflict of interest (a Support member of the buyer's company), a race (a quote closed behind the open page),
// SLA breach (a request backdated in the scratch DB), obsolete line (scratch DB row), a quote with a sent version (scratch DB row) so Revise can be used.
// Mocked: the two 409 codes that need a real race (QUOTE_VERSION_CHANGED, QUOTE_NUMBER_CLASH): the documented bodies, in plain words with a reload.
import { execSync } from 'node:child_process';
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, CONSOLE as WEB, USERS, apiCall, apiLogin, launch, reporter, totp, uniqueBin } from './lib.mjs';

if (process.env.E2E_CONSOLE_QUOTES !== '1') {
  console.log('SKIP  console quotes E2E (set E2E_CONSOLE_QUOTES=1 to run it)');
  process.exit(0);
}
if (!process.env.E2E_SQL_CMD || !/-d mw_\w+/.test(process.env.E2E_SQL_CMD)) throw new Error('E2E_SQL_CMD must be a command against a scratch database (-d mw_*)');

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
const sql = (text) => execSync(process.env.E2E_SQL_CMD, { input: text, encoding: 'utf8' });
const tag = String(Date.now() % 1000000);

const SECRET = USERS.admin.totpSecret;
const PASSWORD = USERS.admin.password;
const person = (email) => ({ email, password: PASSWORD, totpSecret: SECRET });
const PEOPLE = {
  support: person('support.e2e@mechware.com.bd'),
  support2: person('support2.e2e@mechware.com.bd'),
  finance: person('finance.a.e2e@mechware.com.bd'),
  moderator: person('moderator.e2e@mechware.com.bd'),
  admin: person('admin2.e2e@mechware.com.bd')
};

/** A code is single-use per account: wait for a 30 s step this account has not used yet. */
const lastStep = new Map();
async function freshCode(p) {
  for (;;) {
    const step = Math.floor(Date.now() / 30000);
    if (step > (lastStep.get(p.email) ?? -1)) { lastStep.set(p.email, step); return totp(p.totpSecret); }
    await sleep(1000);
  }
}
async function apiToken(p) {
  let body;
  for (let attempt = 0; attempt < 8; attempt++) {
    const res = await fetch(`${API}/api/v1/auth/login`, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ identifier: p.email, password: p.password, totpCode: await freshCode(p) })
    });
    body = await res.json();
    if (res.ok && body.data?.accessToken) return body.data.accessToken;
    await sleep(5000);
  }
  throw new Error(`API login for ${p.email} failed: ${JSON.stringify(body)}`);
}
async function consoleSignIn(page, p) {
  await page.goto(`${WEB}/login`, { timeout: 120000, waitUntil: 'networkidle' });
  await page.getByLabel('Staff or seller email').fill(p.email);
  await page.getByLabel('Password').fill(p.password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.getByLabel('6-digit authenticator code or single-use recovery code').fill(await freshCode(p));
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
const send = (token, method, path, body, headers = {}) =>
  fetch(`${API}${path}`, { method, headers: { 'content-type': 'application/json', authorization: `Bearer ${token}`, ...headers }, body: body ? JSON.stringify(body) : undefined })
    .then(async (res) => ({ status: res.status, json: await res.json().catch(() => ({})), cacheControl: res.headers.get('cache-control') }));
let keySeq = 0;
const key = (what) => `e2e-d415-${tag}-${what}-${++keySeq}`;
const rfqRow = (p, number) => p.getByTestId('rfq-row').filter({ hasText: number });
const focused = (p) => p.evaluate(() => document.activeElement?.getAttribute('data-testid'));

(async () => {
  // ---------------- setup through the real API
  const adminToken = await apiToken(PEOPLE.admin);
  const sellerToken = await apiLogin(USERS.sellerAlpha);
  const buyer = await apiLogin(USERS.buyer);
  const legalName = `Quote Desk Mills ${tag} Ltd`;
  const reg = await apiCall(buyer, 'POST', '/api/v1/companies', { legalName, tradeLicenceNo: 'TL-D415', bin: uniqueBin() });
  const companyId = reg.json.data.id;
  const act = await apiCall(adminToken, 'POST', `/api/v1/admin/companies/${companyId}/activate`);
  const buyerId = (await apiCall(buyer, 'GET', '/api/v1/users/me')).json.data.id;
  const own = (await apiCall(buyer, 'GET', `/api/v1/companies/${companyId}/members`)).json.data.find((x) => x.userId === buyerId);
  await apiCall(buyer, 'PUT', `/api/v1/companies/${companyId}/members/${own.id}`, { roles: 10, spendLimitMinor: null, costCentreIds: [], isActive: true });
  const credit = await send(adminToken, 'PUT', `/api/v1/admin/companies/${companyId}/credit`, { terms: 15, limitMinorUnits: 500000000 }, { 'X-Step-Up-Code': await freshCode(PEOPLE.admin) });
  r.check('setup: an active company with 15-day credit and an Admin+Buyer owner', reg.status === 201 && act.status === 200 && credit.status === 200, `${reg.status}/${act.status}/${credit.status}`);

  const picked = sql("select v.id, v.sku from products p join product_variants v on v.product_id = p.id join inventory_items i on i.variant_id = v.id where p.status = 'active' and v.status = 'active' order by v.id limit 2;").split('\n').map((x) => x.trim()).filter(Boolean).map((x) => x.split('|'));
  const [[variantA, skuA], [variantB]] = picked;
  const ask = async (body) => {
    const res = await fetch(`${API}/api/v1/companies/${companyId}/rfqs`, { method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${buyer}`, 'idempotency-key': key('rfq') }, body: JSON.stringify(body) });
    const j = await res.json();
    if (res.status !== 201 && res.status !== 200) throw new Error(`RFQ failed: ${res.status} ${JSON.stringify(j).slice(0, 300)}`);
    return { id: j.data.rfq.id, number: j.data.rfq.number, lines: j.data.lines };
  };
  const r1 = await ask({ source: 'product', variantId: variantA, quantity: 3, message: `Please quote ${tag} for the Gazipur plant` });
  const r2 = await ask({ source: 'text', text: `${skuA}, 5\nZZNOPE${tag}, 2` });
  const r3 = await ask({ source: 'product', variantId: variantB, quantity: 2 }); // backdated: SLA breached; closed through the screen later
  const r4 = await ask({ source: 'product', variantId: variantB, quantity: 4 }); // line turned obsolete with a successor in the scratch DB
  sql(`SET session_replication_role = replica;
    UPDATE rfqs SET created_at = now() - interval '6 days' WHERE id = '${r3.id}';`);
  sql(`SET session_replication_role = replica;
    UPDATE rfq_lines SET match_status = 'Obsolete', successor_variant_id = '${variantA}' WHERE rfq_id = '${r4.id}';`);
  const statusRaw = (await send(buyer, 'GET', `/api/v1/companies/${companyId}/rfqs/${r2.id}`)).json.data;
  const kinds = statusRaw.lines.map((l) => l.matchStatus).sort();
  r.check('setup: four RFQs by the buyer; r2 has a matched and a not-found line, r4 an obsolete line with a successor',
    kinds.join() === 'matched,not_found' && (await send(buyer, 'GET', `/api/v1/companies/${companyId}/rfqs/${r4.id}`)).json.data.lines[0].matchStatus === 'obsolete', kinds.join());

  // a re-run on the same scratch DB: requests left open by an earlier run are closed, so the inbox holds only this run's four and the default view starts empty
  sql(`SET session_replication_role = replica;
    UPDATE rfqs SET status = 'Closed', closed_at = now(), closed_by = (SELECT id FROM users WHERE email = 'admin@mechware.com.bd'), close_reason = 'E2E cleanup', assigned_to = NULL
    WHERE status IN ('Submitted', 'Quoting') AND id NOT IN ('${r1.id}', '${r2.id}', '${r3.id}', '${r4.id}');`);
  const sup = await apiToken(PEOPLE.support);
  const sup2 = await apiToken(PEOPLE.support2);
  const fin = await apiToken(PEOPLE.finance);
  const mod = await apiToken(PEOPLE.moderator);
  const none = await send(sup, 'GET', '/api/v1/admin/rfqs?status=open&assigned=none&pageSize=50');
  const listed = (none.json.data?.items ?? []).map((i) => i.number);
  r.check('setup: Support reads the inbox through the real API and the four requests are in it', none.status === 200 && [r1, r2, r3, r4].every((x) => listed.includes(x.number)), `${none.status} ${listed.length}`);
  r.check('setup: a seller and a moderator get 403 on the inbox, Finance can read it but not assign (real API)',
    (await send(sellerToken, 'GET', '/api/v1/admin/rfqs')).status === 403 && (await send(mod, 'GET', '/api/v1/admin/rfqs')).status === 403
    && (await send(fin, 'GET', '/api/v1/admin/rfqs')).status === 200 && (await send(fin, 'POST', `/api/v1/admin/rfqs/${r1.id}/assign`, { assigneeId: null })).status === 403);

  // Support 2 becomes a member of the buyer's company (conflict of interest): invited, then accepted.
  const sup2Id = (await send(sup2, 'GET', '/api/v1/users/me')).json.data.id;
  const supId = (await send(sup, 'GET', '/api/v1/users/me')).json.data.id;
  const add = await apiCall(buyer, 'POST', `/api/v1/companies/${companyId}/members`, { userId: sup2Id, roles: 2, spendLimitMinor: null, costCentreIds: [] });
  const accept = await send(sup2, 'POST', `/api/v1/companies/${companyId}/invitations/accept`);
  r.check('setup: Support 2 is an accepted member of the buyer\'s company', add.status < 300 && accept.status === 200, `${add.status}/${accept.status}`);

  const browser = await launch();

  // ================= seller: no links, refused pages
  {
    const p = await newPage(browser);
    await p.goto(`${WEB}/login`, { timeout: 120000, waitUntil: 'networkidle' });
    await p.getByLabel('Staff or seller email').fill(USERS.sellerAlpha.email);
    await p.getByLabel('Password').fill(USERS.sellerAlpha.password);
    await p.getByRole('button', { name: 'Sign in' }).click();
    await p.waitForURL(`${WEB}/`, { timeout: 120000 });
    await p.getByText('Operations console').first().waitFor();
    r.check('seller sees no Quotes links', (await p.getByRole('link', { name: /RFQ inbox|New quote for a company/ }).count()) === 0 && (await p.getByRole('navigation', { name: 'Modules' }).locator('p', { hasText: /^Quotes$/ }).count()) === 0);
    for (const [path, perm] of [['/quotes/rfqs', 'quotes.view'], ['/quotes', 'quotes.view'], [`/quotes/rfqs/${r1.id}`, 'quotes.view'], ['/quotes/new', 'quotes.edit']]) {
      await p.goto(`${WEB}${path}`);
      await p.getByText(`Your role does not allow this action (${perm}).`).waitFor();
    }
    r.check('seller opening the inbox, list, an RFQ and the new-quote page is told no', true);
    await p.context().close();
  }

  // ================= moderator and finance: permissions follow the role
  {
    const p = await newPage(browser);
    await consoleSignIn(p, PEOPLE.moderator);
    r.check('moderator sees no Quotes links', (await p.getByRole('link', { name: /RFQ inbox|^Quotes/ }).count()) === 0);
    await p.goto(`${WEB}/quotes/rfqs`);
    await p.getByText('Your role does not allow this action (quotes.view).').waitFor();
    await p.context().close();

    const f = await newPage(browser);
    await consoleSignIn(f, PEOPLE.finance);
    const nav = f.getByRole('navigation', { name: 'Modules' });
    r.check('finance (quotes.view only) sees RFQ inbox and Quotes, but not "New quote for a company"',
      (await nav.getByRole('link', { name: /RFQ inbox/ }).count()) === 1 && (await nav.getByRole('link', { name: /^Quotes/ }).count()) === 1 && (await nav.getByRole('link', { name: /New quote for a company/ }).count()) === 0);
    await f.goto(`${WEB}/quotes/rfqs/${r1.id}`);
    await f.getByTestId('rfq-number').waitFor();
    r.check('finance reads the request but has no action buttons, and is told why',
      (await f.getByTestId('create-quote').count()) === 0 && (await f.getByTestId('close-rfq').count()) === 0 && (await f.getByTestId('rfq-command-hint').innerText()).includes('quotes.edit'));
    await f.goto(`${WEB}/quotes/new`);
    await f.getByText('Your role does not allow this action (quotes.edit).').waitFor();
    r.check('finance on /quotes/new is told no', true);
    await f.context().close();
  }

  // ================= support: inbox
  const s = await newPage(browser);
  const posts = [];
  s.on('request', (req) => { if (req.method() === 'POST' && req.url().includes('/api/v1/admin/')) posts.push(req.url().replace(/^.*\/api\/v1\/admin\//, '')); });
  await consoleSignIn(s, PEOPLE.support);
  const nav = s.getByRole('navigation', { name: 'Modules' });
  r.check('support sees a Quotes heading with three links',
    (await nav.locator('p', { hasText: /^Quotes$/ }).count()) === 1 && (await nav.getByRole('link', { name: /RFQ inbox/ }).count()) === 1 && (await nav.getByRole('link', { name: /^Quotes/ }).count()) === 1 && (await nav.getByRole('link', { name: /New quote for a company/ }).count()) === 1);
  await nav.getByRole('link', { name: /RFQ inbox/ }).click();
  await s.waitForURL(/\/quotes\/rfqs$/);
  await s.getByTestId('rfq-empty').waitFor();
  r.check('the default view is "open, assigned to me": empty for a new Support person, and the empty text says what to do',
    (await s.getByTestId('filter-status').inputValue()) === 'open' && (await s.getByTestId('filter-assigned').inputValue()) === 'me' && (await s.getByTestId('rfq-empty').innerText()).includes('Not assigned'));
  await s.getByTestId('filter-assigned').selectOption('none');
  await s.getByTestId('rfq-table').waitFor();
  const rows = await s.getByTestId('rfq-row').allInnerTexts();
  r.check('"Not assigned" lists the four open requests, oldest first (the six-day-old one is above the new ones)', rows.length >= 4 && [r1, r2, r4].every((x) => rows.findIndex((t) => t.includes(r3.number)) < rows.findIndex((t) => t.includes(x.number))), rows.map((x) => x.split('\n')[0]).join(','));
  const breached = await rfqRow(s, r3.number).getByTestId('sla-badge').innerText();
  const fresh = await rfqRow(s, r1.number).getByTestId('sla-badge').innerText();
  r.check('the breach is in words, not colour: "SLA breached" with the waited time and the due time; a fresh request says "Within SLA"',
    breached === 'SLA breached' && /Waiting .* of working time\. A first quote was due by \d{4}-\d{2}-\d{2} \d{2}:\d{2} Dhaka\./.test(await rfqRow(s, r3.number).getByTestId('sla-detail').innerText()) && fresh === 'Within SLA', `${breached} / ${fresh}`);
  r.check('the row names company, requester, lines, status and "Not assigned"', (await rfqRow(s, r2.number).innerText()).includes(legalName) && (await rfqRow(s, r2.number).innerText()).includes('New') && (await rfqRow(s, r2.number).getByTestId('rfq-assignee').innerText()) === 'Not assigned');
  await axeCheck(s, '/quotes/rfqs');
  await s.getByLabel('Request number or company').fill(r2.number);
  await s.getByRole('button', { name: 'Search' }).click();
  await s.waitForFunction((n) => document.querySelectorAll('[data-testid="rfq-row"]').length === 1 && document.body.innerText.includes(n), r2.number);
  r.check('search by request number narrows the list to one row', (await s.getByTestId('rfq-row').count()) === 1);
  await s.getByRole('button', { name: 'Back to my open requests' }).click();
  await s.getByTestId('rfq-empty').waitFor();
  r.check('"Back to my open requests" restores the default view', (await s.getByTestId('filter-assigned').inputValue()) === 'me');
  await s.getByTestId('filter-status').selectOption('closed');
  await s.getByTestId('filter-assigned').selectOption('');
  await s.getByTestId('rfq-empty').waitFor();
  r.check('a filter with no match says so', (await s.getByTestId('rfq-empty').innerText()).includes('No request matches'));

  // ================= support: RFQ detail
  await s.goto(`${WEB}/quotes/rfqs/${r2.id}`);
  await s.getByTestId('rfq-lines').waitFor();
  const l2 = await s.getByTestId('rfq-line').allInnerTexts();
  r.check('r2: a matched line (catalogue part shown) and a not-found line with its hint', l2.length === 2 && l2.some((t) => t.includes('Matched') && t.includes(skuA)) && l2.some((t) => t.includes('Not in the catalogue') && t.includes(`ZZNOPE${tag}`) && t.includes('Find the part by hand')), l2.join(' | '));
  r.check('the line summary counts them', (await s.getByTestId('rfq-line-counts').innerText()).includes('2 lines: 1 matched, 0 obsolete, 1 not matched'));
  await axeCheck(s, 'RFQ detail');
  await s.goto(`${WEB}/quotes/rfqs/${r4.id}`);
  await s.getByTestId('rfq-lines').waitFor();
  r.check('r4: an obsolete line shows "Obsolete part" and the successor', (await s.getByTestId('rfq-line').innerText()).includes('Obsolete part') && (await s.getByTestId('successor').innerText()).includes(skuA), await s.getByTestId('rfq-line').innerText());

  // assign / unassign
  await s.goto(`${WEB}/quotes/rfqs/${r2.id}`);
  await s.getByTestId('assign-me').waitFor();
  r.check('an unassigned request: "Unassign" is disabled, "Assign to me" is enabled', !(await s.getByTestId('unassign').isEnabled()) && (await s.getByTestId('assign-me').isEnabled()));
  const before = posts.length;
  await s.getByLabel(/Assign to another staff member/).fill('not-an-id');
  await s.getByRole('button', { name: 'Assign', exact: true }).click();
  await s.getByText('A user id looks like').waitFor();
  r.check('a malformed staff id is refused on screen, nothing sent', posts.length === before);
  await s.getByTestId('assign-me').click();
  await s.getByTestId('rfq-notice').filter({ hasText: 'Assigned to you.' }).waitFor();
  r.check('assigned to me: the notice, "(you)" beside the name, Unassign enabled', (await s.getByTestId('rfq-assignee').innerText()).includes('(you)') && (await s.getByTestId('unassign').isEnabled()));
  await s.getByTestId('unassign').click();
  await s.getByTestId('rfq-notice').filter({ hasText: 'not assigned to anyone' }).waitFor();
  r.check('unassigned again (real API)', (await s.getByTestId('rfq-assignee').innerText()) === 'Not assigned');
  // a user who is not allowed to prepare quotes is refused by the server (the buyer)
  await s.getByLabel(/Assign to another staff member/).fill(buyerId);
  await s.getByRole('button', { name: 'Assign', exact: true }).click();
  await s.getByTestId('rfq-alert').waitFor();
  r.check('assigning to a person without quotes.edit: the server refuses, shown in words, role=alert, focus on it', (await s.getByTestId('rfq-alert').innerText()).length > 10 && (await focused(s)) === 'rfq-alert', await s.getByTestId('rfq-alert').innerText());
  await s.getByLabel(/Assign to another staff member/).fill(sup2Id);
  await s.getByRole('button', { name: 'Assign', exact: true }).click();
  await s.getByTestId('rfq-notice').filter({ hasText: 'Assigned to that staff member.' }).waitFor();
  r.check('assigned to another Support person: their name is shown', (await s.getByTestId('rfq-assignee').innerText()).includes('Support Two'));
  await s.getByTestId('assign-me').click();
  await s.getByTestId('rfq-notice').filter({ hasText: 'Assigned to you.' }).waitFor();
  // r2 is now mine: it is the default view
  await s.goto(`${WEB}/quotes/rfqs`);
  await rfqRow(s, r2.number).waitFor();
  r.check('the default view now shows r2 (assigned to me) and not the others', (await s.getByTestId('rfq-row').count()) === 1);

  // close with a reason
  await s.goto(`${WEB}/quotes/rfqs/${r3.id}`);
  await s.getByTestId('close-rfq').click();
  const closePosts = posts.length;
  await s.getByTestId('reason-confirm').click();
  await s.getByText('Enter a reason.').waitFor();
  await s.getByTestId('reason-input').fill('abc');
  await s.getByTestId('reason-confirm').click();
  await s.getByText('The reason must be 5 to 500 characters.').waitFor();
  r.check('an empty and a 3-character reason are refused on screen, nothing sent', posts.length === closePosts);
  await axeCheck(s, 'close dialog with an error');
  await s.getByTestId('reason-input').fill('We do not stock these parts.');
  await s.getByTestId('reason-confirm').click();
  await s.getByTestId('rfq-notice').filter({ hasText: 'The request is closed' }).waitFor();
  r.check('closed: status Closed, the reason shown, and the clock reads "Closed, target missed"',
    (await s.getByTestId('rfq-status').innerText()) === 'Closed' && (await s.getByTestId('rfq-close-reason').innerText()) === 'We do not stock these parts.' && (await s.getByTestId('sla-badge').innerText()) === 'Closed, target missed');
  r.check('a closed request has no buttons and says it cannot be changed', (await s.getByTestId('create-quote').count()) === 0 && (await s.getByTestId('rfq-command-hint').innerText()).includes('cannot be changed'));
  const asBuyer = (await send(buyer, 'GET', `/api/v1/companies/${companyId}/rfqs/${r3.id}`)).json.data;
  r.check('the buyer sees the close reason (real API)', JSON.stringify(asBuyer).includes('We do not stock these parts.'));

  // create a quote from the request
  await s.goto(`${WEB}/quotes/rfqs/${r1.id}`);
  await s.getByTestId('create-quote').click();
  await s.waitForURL(/\/quotes\/(?!rfqs|new)[0-9a-f-]{36}$/);
  await s.getByTestId('quote-number').waitFor();
  const q1Id = s.url().split('/').pop();
  const q1Json = (await send(sup, 'GET', `/api/v1/admin/quotes/${q1Id}`));
  r.check('a quote was made from r1: one draft version, one pre-filled line, assigned to Support (real API)', q1Json.json.data.versions.length === 1 && q1Json.json.data.versions[0].lines.length === 1 && q1Json.json.data.quote.assignedTo === supId, JSON.stringify(q1Json.json.data.versions[0].lines.length));
  r.check('the page shows Version 1 (latest), Draft, the line and the totals, and the link to the editor', (await s.getByTestId('quote-version').count()) === 1 && (await s.getByTestId('quote-version').innerText()).includes('Version 1 (latest)') && (await s.getByTestId('version-line').count()) === 1 && (await s.getByTestId('version-totals').innerText()).includes('Goods, net') && (await s.getByRole('link', { name: /Edit this draft/ }).count()) === 1);
  r.check('Support has no cost column and the API JSON has no cost object for Support', (await s.getByTestId('cost-head').count()) === 0 && !JSON.stringify(q1Json.json).includes('"cost"'));
  r.check('"Revise" is disabled with the reason while the draft is open', !(await s.getByTestId('revise-quote').isEnabled()) && (await s.getByTestId('revise-hint').innerText()).includes('open version'));
  await axeCheck(s, 'quote detail (draft)');
  await s.getByRole('link', { name: /Edit this draft/ }).click();
  await s.getByTestId('editor-heading').waitFor();
  r.check('the editor link opens the draft editor (D4.16)', (await s.getByTestId('editor-heading').innerText()).includes('draft'));
  await s.goBack();
  await s.getByTestId('quote-number').waitFor();

  // the RFQ now lists its quote and is being quoted
  await s.goto(`${WEB}/quotes/rfqs/${r1.id}`);
  await s.getByTestId('rfq-quotes').waitFor();
  r.check('r1 is "Being quoted" and lists the quote it produced', (await s.getByTestId('rfq-status').innerText()) === 'Being quoted' && (await s.getByTestId('rfq-quotes').innerText()).includes('Draft'));

  // new quote for a company (company picker + contact)
  await s.goto(`${WEB}/quotes/new`);
  const newPosts = posts.length;
  await s.getByRole('button', { name: 'Create the quote' }).click();
  await s.getByText('Choose a company.').waitFor();
  await s.getByText("Enter the buyer contact's user id.").waitFor();
  r.check('nothing chosen: both problems shown, nothing sent', posts.length === newPosts);
  await s.getByLabel('Company', { exact: true }).fill(`Quote Desk Mills ${tag}`);
  await s.getByTestId('company-list').waitFor();
  await s.getByTestId('company-list').selectOption({ label: legalName });
  await s.getByLabel(/Buyer contact/).fill('xyz');
  await s.getByRole('button', { name: 'Create the quote' }).click();
  await s.getByText('A user id looks like').waitFor();
  r.check('a malformed contact id is refused on screen', posts.length === newPosts);
  await s.getByLabel(/Buyer contact/).fill('11111111-2222-4333-8444-555555555555');
  await s.getByRole('button', { name: 'Create the quote' }).click();
  await s.getByTestId('quote-create-alert').waitFor();
  r.check('a contact who is not a member: the server says so in words, role=alert, nothing created', (await s.getByTestId('quote-create-alert').innerText()).includes('current member') && (await focused(s)) === 'quote-create-alert', await s.getByTestId('quote-create-alert').innerText());
  await axeCheck(s, '/quotes/new with an error');
  await s.getByLabel(/Buyer contact/).fill(buyerId);
  await s.getByRole('button', { name: 'Create the quote' }).click();
  await s.waitForURL(/\/quotes\/(?!rfqs|new)[0-9a-f-]{36}$/);
  await s.getByTestId('quote-number').waitFor();
  const q2Id = s.url().split('/').pop();
  r.check('a quote for the company: an empty draft, no request behind it, the credit terms of the company shown',
    (await s.getByText('None: made for the company').count()) === 1 && (await s.getByTestId('version-no-lines').count()) === 1 && (await s.getByText('Net 15 days').count()) >= 1);

  // quotes list: computed status, credit terms, on-hold badge
  sql(`UPDATE companies SET credit_hold_since = now() WHERE id = '${companyId}';`);
  await s.goto(`${WEB}/quotes`);
  await s.getByTestId('quote-table').waitFor();
  const qRow = s.getByTestId('quote-row').filter({ hasText: legalName });
  r.check('the list shows both quotes of the company with "Draft", "Net 15 days" and the on-hold badge in words',
    (await qRow.count()) >= 2 && (await qRow.first().innerText()).includes('Draft') && (await qRow.first().innerText()).includes('Net 15 days') && (await qRow.first().getByTestId('credit-hold').innerText()) === 'New credit orders on hold');
  r.check('the list carries no balance or limit', !/5,000,000|limit/i.test(await s.getByTestId('quote-table').innerText()));
  await axeCheck(s, '/quotes');
  await s.getByTestId('filter-status').selectOption('sent');
  await s.getByTestId('quote-empty').waitFor();
  r.check('a filter with no match says so', (await s.getByTestId('quote-empty').innerText()).includes('No quote matches'));
  await s.getByTestId('filter-status').selectOption('draft');
  await s.getByTestId('quote-table').waitFor();
  sql(`UPDATE companies SET credit_hold_since = NULL WHERE id = '${companyId}';`);

  // a sent version in the scratch DB (send is built in D4.8): then Revise works and keeps the sent one
  const adminId = (await send(adminToken, 'GET', '/api/v1/users/me')).json.data.id;
  sql(`SET session_replication_role = replica;
    UPDATE quote_versions SET status = 'Sent', content_hash = repeat('a', 64), submitted_by = created_by, submitted_at = now(), approved_by = '${adminId}', approved_at = now(),
      flags = '{"belowFloorLineIds":[],"overAuthorityLineIds":[],"longValidity":false}'::jsonb, company_snapshot = '{}'::jsonb, preparer_name = 'Support One',
      sent_by = created_by, sent_at = now(), valid_until_utc = now() + interval '30 days' WHERE quote_id = '${q2Id}' AND version_no = 1;`);
  await s.goto(`${WEB}/quotes/${q2Id}`);
  await s.getByTestId('quote-number').waitFor();
  r.check('the sent version reads "Sent to the buyer"; Revise is enabled', (await s.getByTestId('quote-status').first().innerText()) === 'Sent to the buyer' && (await s.getByTestId('revise-quote').isEnabled()));
  await s.getByTestId('revise-quote').click();
  await s.getByTestId('quote-notice').waitFor();
  const versions = await s.getByTestId('quote-version').allInnerTexts();
  r.check('revise (real API): Version 2 (latest) is a new draft on top, Version 1 stays Sent and untouched', versions.length === 2 && versions[0].includes('Version 2 (latest)') && versions[0].includes('Draft') && versions[1].includes('Version 1') && versions[1].includes('Sent to the buyer'), versions.map((v) => v.split('\n')[0]).join(','));
  r.check('after the revise the button is disabled again with the reason', !(await s.getByTestId('revise-quote').isEnabled()));

  // two 409 race codes, mocked with the documented bodies: plain words and a reload
  const raceBody = (code, message) => JSON.stringify({ type: 'x', title: 'Conflict', status: 409, detail: message, error: { code, message, details: [] } });
  for (const [code, words, label] of [['QUOTE_VERSION_CHANGED', /changed this quote at the same moment/, 'a race on the version'], ['QUOTE_NUMBER_CLASH', /could not be numbered/, 'a quote number clash']]) {
    await s.route(`**/api/v1/admin/quotes/${q2Id}/close`, (route) => route.fulfill({ status: 409, contentType: 'application/json', headers: { 'access-control-allow-origin': WEB, 'access-control-allow-credentials': 'true' }, body: raceBody(code, 'English server text') }));
    await s.getByTestId('close-quote').click();
    await s.getByTestId('reason-input').fill('Closing during a race');
    await s.getByTestId('reason-confirm').click();
    await s.getByTestId('quote-alert').waitFor();
    r.check(`mocked ${code}: plain words (${label}), role=alert, the dialog closes, focus on the alert`, words.test(await s.getByTestId('quote-alert').innerText()) && (await focused(s)) === 'quote-alert' && (await s.getByRole('dialog').count()) === 0, await s.getByTestId('quote-alert').innerText());
    await s.unroute(`**/api/v1/admin/quotes/${q2Id}/close`);
  }
  await axeCheck(s, 'quote detail with an error');

  // a real race: the quote is closed behind the open page, then Revise is clicked
  const closeOther = await send(adminToken, 'POST', `/api/v1/admin/quotes/${q1Id}/close`, { reason: 'Closed by an admin meanwhile' });
  r.check('setup: an admin closes q1 behind the open page (real API)', closeOther.status === 200, String(closeOther.status));
  // open q2 (draft v2) in a second tab state: close it elsewhere then try to close it again from the stale page
  const closeQ2 = await send(adminToken, 'POST', `/api/v1/admin/quotes/${q2Id}/close`, { reason: 'Closed by an admin behind the page' });
  r.check('setup: an admin closes q2 behind the open page (real API); the Sent version is withdrawn', closeQ2.status === 200, String(closeQ2.status));
  await s.getByTestId('close-quote').click();
  await s.getByTestId('reason-input').fill('Closing a quote that is closed already');
  await s.getByTestId('reason-confirm').click();
  await s.getByTestId('quote-alert').waitFor();
  r.check('a quote closed behind the page: QUOTE_CLOSED in words, then the page reloads and shows it closed with the reason and no buttons',
    (await s.getByTestId('quote-alert').innerText()).includes('closed') && (await s.getByTestId('quote-close-reason').innerText()).includes('Closed by an admin behind the page') && !(await s.getByTestId('close-quote').isEnabled()), await s.getByTestId('quote-alert').innerText());
  r.check('a closed quote: Revise disabled with "closed", Close disabled with "already closed"', (await s.getByTestId('revise-hint').innerText()).includes('closed') && (await s.getByTestId('close-hint').innerText()).includes('already closed'));

  // ================= admin: cost only when the API sends it
  const a = await newPage(browser);
  await consoleSignIn(a, PEOPLE.admin);
  await a.goto(`${WEB}/quotes/${q1Id}`);
  await a.getByTestId('quote-number').waitFor();
  const costApi = (await send(adminToken, 'GET', `/api/v1/admin/quotes/${q1Id}`)).json.data.versions[0].lines[0];
  r.check('Admin (pricing.view_cost): the API sends the cost object and the page shows the Cost / floor column', 'cost' in costApi && (await a.getByTestId('cost-head').count()) === 1 && (await a.getByTestId('cost-cell').count()) === 1, JSON.stringify(costApi.cost));
  r.check('the closed q1 shows its close reason', (await a.getByTestId('quote-close-reason').innerText()) === 'Closed by an admin meanwhile');
  await axeCheck(a, 'quote detail (closed, with cost)');
  await a.context().close();

  // ================= support 2 belongs to the buyer's company: conflict of interest
  const c = await newPage(browser);
  await consoleSignIn(c, PEOPLE.support2);
  await c.goto(`${WEB}/quotes/rfqs/${r4.id}`);
  await c.getByTestId('create-quote').waitFor();
  await c.getByTestId('create-quote').click();
  await c.getByTestId('rfq-alert').waitFor();
  const conflict = await c.getByTestId('rfq-alert').innerText();
  r.check('QUOTE_CONFLICT_OF_INTEREST (real API): "you belong to this company", role=alert, focus on it, the request is unchanged and still open',
    conflict.includes('You belong to this company') && (await focused(c)) === 'rfq-alert' && (await c.getByTestId('rfq-status').innerText()) === 'New', conflict);
  await axeCheck(c, 'RFQ detail with the conflict message');
  await c.goto(`${WEB}/quotes/new`);
  await c.getByLabel('Company', { exact: true }).fill(`Quote Desk Mills ${tag}`);
  await c.getByTestId('company-list').waitFor();
  await c.getByTestId('company-list').selectOption({ label: legalName });
  await c.getByLabel(/Buyer contact/).fill(buyerId);
  await c.getByRole('button', { name: 'Create the quote' }).click();
  await c.getByTestId('quote-create-alert').waitFor();
  r.check('"New quote for a company" for their own company: the same words', (await c.getByTestId('quote-create-alert').innerText()).includes('You belong to this company'));
  r.check('no quote was created for them (real API: still the two quotes of Support)', (await send(sup, 'GET', `/api/v1/admin/quotes?companyId=${companyId}&pageSize=50`)).json.data.total === 2);
  await c.context().close();

  // ================= narrow screens and page errors
  await s.setViewportSize({ width: 360, height: 800 });
  for (const [path, label] of [['/quotes/rfqs', 'the inbox'], [`/quotes/rfqs/${r2.id}`, 'an RFQ'], ['/quotes', 'the list'], [`/quotes/${q1Id}`, 'a quote'], ['/quotes/new', 'new quote']]) {
    await s.goto(`${WEB}${path}`);
    await s.waitForLoadState('networkidle');
    r.check(`360 px: no horizontal scroll on ${label}`, !(await overflow(s)));
  }
  r.check('no page errors', s.errors.length === 0, s.errors.join(' | '));
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
