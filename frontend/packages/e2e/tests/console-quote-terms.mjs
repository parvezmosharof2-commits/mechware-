// D4.21: the console "Quote terms" page (/quotes/terms): EN and BN line lists (edit, add, remove, move up/down, live counter, 12 x 300 limits checked before
// saving), read-only for a role without quotes.approve (the API answers 403), Save with rowVersion, a stale save from a second browser (someone else changed it:
// their lines in words, Load their version / Keep my edits and save over theirs), last changed (Dhaka) and by whom, the audit row, axe, 360 px.
// SCRATCH DB only. Same setup as console-quote-editor.mjs: API on a scratch DB mw_* (migrated, demo accounts), the console built against it, a CORS proxy in front
// of the API, staff cloned from the admin row with the same TOTP secret (support.e2e@, finance.a.e2e@), E2E_SQL_CMD = a command that reads SQL on stdin against the
// SCRATCH database, e.g. `docker exec -i mechware_postgres psql -U mechware -d mw_d421 -At -q -v ON_ERROR_STOP=1`.
// Not covered here: that a SENT quote's PDF keeps its terms after a save (needs a full company + quote + send setup; D4.20 integration test
// `ASentVersion_KeepsThePdfTermsItWasSentWith_...` proves it on the API). Set E2E_CONSOLE_QUOTE_TERMS=1 to run. Docs: docs/modules/D4.md HANDOFF D4.21.
import { execSync } from 'node:child_process';
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API, CONSOLE as WEB, USERS, launch, reporter, totp } from './lib.mjs';

if (process.env.E2E_CONSOLE_QUOTE_TERMS !== '1') {
  console.log('SKIP  console quote terms E2E (set E2E_CONSOLE_QUOTE_TERMS=1 to run it)');
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

const SECRET = USERS.admin.totpSecret;
const PASSWORD = USERS.admin.password;
const person = (email) => ({ email, password: PASSWORD, totpSecret: SECRET });
const PEOPLE = { support: person('support.e2e@mechware.com.bd'), finance: person('finance.a.e2e@mechware.com.bd') };

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
  page.puts = 0;
  page.on('pageerror', (e) => page.errors.push('pageerror: ' + e.message));
  page.on('request', (q) => { if (q.method() === 'PUT' && q.url().includes('/admin/quote-terms')) page.puts++; });
  return page;
}
const send = (token, method, path, body) =>
  fetch(`${API}${path}`, { method, headers: { 'content-type': 'application/json', authorization: `Bearer ${token}` }, body: body ? JSON.stringify(body) : undefined })
    .then(async (res) => ({ status: res.status, json: await res.json().catch(() => ({})) }));
const PATH = '/api/v1/admin/quote-terms';
const en = (p) => p.getByTestId('terms-en-line');
const bn = (p) => p.getByTestId('terms-bn-line');
const texts = (loc) => loc.locator('textarea').evaluateAll((els) => els.map((e) => e.value));
const open = async (p) => { await p.goto(`${WEB}/quotes/terms`); await p.getByTestId('terms-explain').waitFor({ timeout: 60000 }); };

(async () => {
  const sup = await apiToken(PEOPLE.support);
  const fin = await apiToken(PEOPLE.finance);
  const start = await send(fin, 'GET', PATH);
  r.check('setup: Finance reads the starting lines (EN and BN)', start.status === 200 && start.json.data.termsEn.length > 0 && start.json.data.termsBn.length > 0, String(start.status));
  const startEn = start.json.data.termsEn;
  const startBn = start.json.data.termsBn;
  const supRead = await send(sup, 'GET', PATH);
  const supPut = await send(sup, 'PUT', PATH, { termsEn: ['x'], termsBn: ['y'], rowVersion: start.json.data.rowVersion });
  r.check('API: Support reads (200) but a save is 403', supRead.status === 200 && supPut.status === 403, `${supRead.status}/${supPut.status}`);

  const browser = await launch();

  // ================= Finance edits and saves
  const f = await newPage(browser);
  await consoleSignIn(f, PEOPLE.finance);
  await open(f);
  r.check('the page lists the starting lines in both languages', (await en(f).count()) === startEn.length && (await bn(f).count()) === startBn.length && (await texts(en(f))).join('|') === startEn.join('|'));
  r.check('the sidebar has "Quote terms" under Quotes and it is the current page', (await f.getByRole('link', { name: /Quote terms/ }).getAttribute('aria-current')) === 'page');
  const explain = await f.getByTestId('terms-explain').innerText();
  r.check('the page says in words that sent quotes keep their terms', /keeps the terms it was sent with/.test(explain) && /only the quotes sent after you save/.test(explain));
  r.check('before any change it says these are the starting lines (or when they were last changed, on a used DB); Save is off', (start.json.data.updatedAtUtc ? /Last changed/ : /starting lines/).test(await f.getByTestId('terms-meta').innerText()) && !(await f.getByTestId('terms-save').isEnabled()));
  r.check('a live counter sits under every line (n / 300)', /^\d+ \/ 300$/.test((await f.locator('[data-testid^="terms-en-"][data-testid$="-count"]').first().innerText()).trim()));
  await axeCheck(f, 'Finance, terms page (editable)');

  // limits are refused on the page, nothing is sent
  await f.getByTestId('terms-en-add').click();
  const newLine = en(f).last().locator('textarea');
  r.check('Add puts focus in the new line', await newLine.evaluate((e) => e === document.activeElement));
  await f.getByTestId('terms-save').click();
  const emptyAlert = await f.getByTestId('terms-alert').innerText();
  r.check('an empty line is refused before sending, in words, and focus is on the alert', /Fix the 1 marked line/.test(emptyAlert) && /empty/.test(emptyAlert) && f.puts === 0 && (await f.getByTestId('terms-alert').evaluate((e) => e === document.activeElement)));
  await newLine.fill('x'.repeat(301));
  r.check('301 characters: the counter says 301 / 300 and the line says it is 1 character too long', /301 \/ 300/.test(await en(f).last().innerText()) && /1 characters too long/.test(await en(f).last().innerText()));
  await f.getByTestId('terms-save').click();
  r.check('301 characters is refused before sending', f.puts === 0 && /Fix the 1 marked line/.test(await f.getByTestId('terms-alert').innerText()));
  await axeCheck(f, 'Finance, a refused line');
  await newLine.fill('x'.repeat(300));
  r.check('300 characters is fine (no problem text)', !/too long/.test(await en(f).last().innerText()));
  // up to 12 lines
  while ((await en(f).count()) < 12) { await f.getByTestId('terms-en-add').click(); await en(f).last().locator('textarea').fill('Filler'); }
  r.check('at 12 lines "Add" is off and the page says the most is 12', !(await f.getByTestId('terms-en-add').isEnabled()) && /The most is 12 lines/.test(await f.getByTestId('terms-en').innerText()));
  await f.reload();
  await f.getByTestId('terms-explain').waitFor();

  // a real edit: change line 1, add a line, move it up, remove another
  const first = `Payment within 7 days of invoice. ${Date.now() % 100000}`;
  await en(f).first().locator('textarea').fill(first);
  await f.getByTestId('terms-en-add').click();
  await en(f).last().locator('textarea').fill('Delivery is ex-works unless the quote says otherwise.');
  await f.getByRole('button', { name: `Move English line ${startEn.length + 1} up` }).click();
  r.check('Up moves the new line above the last old one', (await texts(en(f))).at(-2) === 'Delivery is ex-works unless the quote says otherwise.');
  await f.getByRole('button', { name: `Remove Bangla line ${startBn.length}` }).click();
  await bn(f).first().locator('textarea').fill('নতুন প্রথম শর্ত {validUntil}');
  r.check('unsaved changes are announced and Save is on', (await f.getByText('You have changes that are not saved.').count()) === 1 && (await f.getByTestId('terms-save').isEnabled()));
  const wantEn = await texts(en(f));
  const wantBn = await texts(bn(f));
  await f.getByTestId('terms-save').click();
  await f.getByTestId('terms-notice').waitFor();
  const notice = await f.getByTestId('terms-notice').innerText();
  r.check('Saved: the notice says new sends use the lines and sent quotes keep theirs', /Quotes sent from now on print these lines/.test(notice) && /already sent keep the lines they were sent with/.test(notice));
  const after = await send(fin, 'GET', PATH);
  r.check('the API holds the saved EN and BN lines in order, and the row version moved on',
    after.json.data.termsEn.join('|') === wantEn.join('|') && after.json.data.termsBn.join('|') === wantBn.join('|') && after.json.data.rowVersion > start.json.data.rowVersion);
  const meta = await f.getByTestId('terms-meta').innerText();
  r.check('last changed shows the Dhaka time and the name of the person', /Last changed \d{4}-\d\d-\d\d \d\d:\d\d Dhaka by Farid Finance\./.test(meta), meta);
  const audit = sql(`select count(*) from audit_logs where action = 'quote.terms_changed' and entity_name = 'quote_terms_settings' and actor_id = (select id from users where email = '${PEOPLE.finance.email}') and new_values::text like '%${first}%';`).trim();
  r.check('an audit row quote.terms_changed exists for Finance with the new lines', audit === '1', audit);
  r.check('Save is off again and the page is clean', !(await f.getByTestId('terms-save').isEnabled()) && (await f.getByText('No changes to save.').count()) === 1);

  // ================= stale save: a second browser saved first
  const g = await newPage(browser);
  await consoleSignIn(g, PEOPLE.finance);
  await open(g);
  const a = await send(fin, 'GET', PATH);
  const theirEn = ['Their line one.', 'Their line two.'];
  const theirPut = await send(fin, 'PUT', PATH, { termsEn: theirEn, termsBn: a.json.data.termsBn, rowVersion: a.json.data.rowVersion });
  r.check('setup: another person saves while the page is open', theirPut.status === 200, String(theirPut.status));
  await en(g).first().locator('textarea').fill('My edited first line.');
  await g.getByTestId('terms-save').click();
  await g.getByTestId('terms-load-theirs').waitFor({ timeout: 20000 });
  const staleText = await g.getByTestId('terms-alert').innerText();
  r.check('a stale save says someone else changed it, my edits are kept, the alert has focus', /Someone else changed these terms/.test(staleText) && (await en(g).first().locator('textarea').inputValue()) === 'My edited first line.' && (await g.getByTestId('terms-alert').evaluate((e) => e === document.activeElement)));
  await g.getByText(/What they saved/).click();
  r.check('their lines can be read before choosing', (await g.getByTestId('terms-alert').innerText()).includes('Their line two.'));
  await axeCheck(g, 'stale save choice');
  await g.getByTestId('terms-overwrite').click();
  r.check('"Keep my edits and save over theirs" keeps my lines and asks for a second press of Save', /Press Save to confirm/.test(await g.getByTestId('terms-notice').innerText()) && (await en(g).first().locator('textarea').inputValue()) === 'My edited first line.' && g.puts === 1);
  await g.getByTestId('terms-save').click();
  await g.getByText(/^Saved\./).waitFor();
  const over = await send(fin, 'GET', PATH);
  r.check('the second Save overwrote theirs with my lines', over.json.data.termsEn[0] === 'My edited first line.' && g.puts === 2);

  // load theirs instead
  const h = await newPage(browser);
  await consoleSignIn(h, PEOPLE.finance);
  await open(h);
  const b = await send(fin, 'GET', PATH);
  await send(fin, 'PUT', PATH, { termsEn: ['Theirs again.'], termsBn: b.json.data.termsBn, rowVersion: b.json.data.rowVersion });
  await en(h).first().locator('textarea').fill('Lost edit.');
  await h.getByTestId('terms-save').click();
  await h.getByTestId('terms-load-theirs').click();
  r.check('"Load their version" replaces my edits with theirs and the page is clean again', (await texts(en(h))).join('|') === 'Theirs again.' && (await h.getByText('No changes to save.').count()) === 1 && (await h.getByTestId('terms-alert').count()) === 0);

  // ================= Support: read-only
  const s = await newPage(browser);
  await consoleSignIn(s, PEOPLE.support);
  await open(s);
  r.check('Support sees the lines, the sidebar item, a note saying why it is read-only, and no Save or Add button',
    (await en(s).count()) === 1 && (await s.getByRole('link', { name: /Quote terms/ }).count()) === 1 && /quotes\.approve/.test(await s.getByTestId('terms-readonly').innerText())
    && (await s.getByTestId('terms-save').count()) === 0 && (await s.getByTestId('terms-en-add').count()) === 0 && (await s.getByRole('button', { name: /Remove/ }).count()) === 0);
  r.check('the textareas are read-only', await en(s).first().locator('textarea').evaluate((e) => e.readOnly));
  await axeCheck(s, 'Support, read-only');
  const a2 = await send(fin, 'GET', PATH);
  const denied = await send(sup, 'PUT', PATH, { termsEn: ['x'], termsBn: ['y'], rowVersion: a2.json.data.rowVersion });
  r.check('API: Support save is 403 and nothing changed', denied.status === 403 && (await send(fin, 'GET', PATH)).json.data.rowVersion === a2.json.data.rowVersion, String(denied.status));

  // ================= failed load in words
  const e = await newPage(browser);
  await consoleSignIn(e, PEOPLE.finance);
  let hits = 0;
  await e.route('**/api/v1/admin/quote-terms', (route) => { hits++; return hits === 1 ? route.fulfill({ status: 500, contentType: 'application/json', body: '{"error":{"code":"X","message":"x"}}', headers: { 'access-control-allow-origin': WEB, 'access-control-allow-credentials': 'true' } }) : route.continue(); });
  await e.goto(`${WEB}/quotes/terms`);
  await e.getByRole('alert').filter({ hasText: 'The server had a problem' }).waitFor({ timeout: 30000 });
  await e.getByRole('button', { name: 'Try again' }).click();
  await e.getByTestId('terms-explain').waitFor();
  r.check('a failed load says so in words with Try again, which loads the page', (await en(e).count()) >= 1);

  // ================= empty lists in words, 360 px
  const empty = await send(fin, 'GET', PATH);
  await send(fin, 'PUT', PATH, { termsEn: [], termsBn: [], rowVersion: empty.json.data.rowVersion });
  const m = await newPage(browser, 360);
  await consoleSignIn(m, PEOPLE.finance);
  await open(m);
  r.check('empty lists say so in words', /Quotes would print no English terms/.test(await m.getByTestId('terms-en-empty').innerText()) && /no Bangla terms/.test(await m.getByTestId('terms-bn-empty').innerText()));
  r.check('360 px: no horizontal page scroll (empty lists)', !(await overflow(m)));
  await m.getByTestId('terms-en-add').click();
  await en(m).first().locator('textarea').fill('A long line '.repeat(20));
  await m.getByTestId('terms-bn-add').click();
  await bn(m).first().locator('textarea').fill('একটি দীর্ঘ শর্ত '.repeat(15));
  r.check('360 px: no horizontal page scroll with long lines', !(await overflow(m)));
  await axeCheck(m, '360 px with long lines');
  // restore the starting lines so the scratch DB ends as it began
  const last = await send(fin, 'GET', PATH);
  await send(fin, 'PUT', PATH, { termsEn: startEn, termsBn: startBn, rowVersion: last.json.data.rowVersion });
  r.check('no page errors', [f, g, h, s, e, m].every((p) => p.errors.length === 0), [f, g, h, s, e, m].flatMap((p) => p.errors).join(' | '));
  await browser.close();
  process.exit(r.finish() ? 0 : 1);
})().catch((err) => { console.error(err); process.exit(1); });
