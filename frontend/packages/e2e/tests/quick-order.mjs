// D5.3 quick-order page: paste a mixed list -> statuses -> fix a line with a suggestion -> change a quantity (debounced, server
// re-validates) -> add valid lines to the cart -> the cart shows the same server prices; plus refusals (501 lines, 413-ish, mocked
// 429 / 503), mixed add results (409 / 429 per line), file picker, axe, 360 and 1280 px, EN + BN.
// Needs a running API with the seeded catalogue (LC1D09, MBN132, ATV12H018M2 in stock, EDS-205A out of stock) and the web app
// built with NEXT_PUBLIC_API_BASE_URL pointing at it. Skipped (exit 0) unless E2E_QUICKORDER=1. The guest-cart limit is 20 new
// carts per IP per hour per API process: this test uses 4 (one per locale and viewport).
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { WEB, SHOTS, launch } from './lib.mjs';

if (process.env.E2E_QUICKORDER !== '1') {
  console.log('SKIP  quick-order E2E (set E2E_QUICKORDER=1 with the API + seeded catalogue running)');
  process.exit(0);
}

const here = dirname(fileURLToPath(import.meta.url));
function findAxe() {
  const ui = resolve(here, '../../ui/node_modules/axe-core/axe.min.js');
  if (existsSync(ui)) return ui;
  const pnpm = resolve(here, '../../../node_modules/.pnpm');
  const d = readdirSync(pnpm).find((n) => n.startsWith('axe-core@'));
  if (d) return join(pnpm, d, 'node_modules/axe-core/axe.min.js');
  throw new Error('axe-core not found (it is a devDependency of @mechware/ui)');
}
const AXE = findAxe();

const results = [];
const check = (name, ok, extra = '') => {
  results.push(ok);
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${extra ? '  -> ' + extra : ''}`);
};

const BN_DIGITS = '০১২৩৪৫৬৭৮৯';
const LOCALES = {
  en: {
    prefix: '', digits: (n) => String(n), check: 'Check lines', goToCart: 'Go to cart',
    live: (v, a) => `${v} valid, ${a} need attention`, add: (n) => `Add ${n} valid lines to cart`, addRe: /^Add \d+ valid lines? to cart$/,
    none: 'No valid lines to add yet.', stale: 'You changed the text. Check the lines again before adding them.',
    tooMany: 'Up to 500 lines per check', rate: 'Too many checks', unavailable: 'temporarily unavailable', retry: 'Check again',
    large: 'larger than 100 KB', wrongType: 'Choose a .csv or .txt file.', cartTitle: 'Your cart', insufficient: 'Not enough stock', rateAdd: 'Too many requests',
    replace: /^Replace with /, useAvail: /^Use [0-9]/, pickLabel: /^Choose the product for /, notFound: 'Not found'
  },
  bn: {
    prefix: '/bn', digits: (n) => String(n).replace(/\d/g, (c) => BN_DIGITS[Number(c)]), check: 'লাইন যাচাই করুন', goToCart: 'কার্টে যান',
    live: (v, a) => `${v}টি ঠিক আছে, ${a}টিতে নজর দরকার`, addRe: /ঠিক লাইন কার্টে যোগ করুন$/, add: (n) => `${n}টি ঠিক লাইন কার্টে যোগ করুন`,
    none: 'যোগ করার মতো ঠিক লাইন এখনো নেই।', stale: 'আপনি লেখা বদলেছেন। যোগ করার আগে আবার যাচাই করুন।',
    tooMany: 'সর্বোচ্চ ৫০০ লাইন', rate: 'অল্প সময়ে অনেকবার', unavailable: 'সাময়িকভাবে পাওয়া যাচ্ছে না', retry: 'আবার যাচাই করুন',
    large: '১০০ কিলোবাইটের বেশি', wrongType: 'একটি .csv বা .txt ফাইল বেছে নিন।', cartTitle: 'আপনার কার্ট', insufficient: 'পর্যাপ্ত স্টক নেই', rateAdd: 'অনেক বেশি অনুরোধ',
    replace: / দিয়ে বদলান$/, useAvail: / নিন, /, pickLabel: /-এর জন্য পণ্য বেছে নিন$/, notFound: 'পাওয়া যায়নি'
  }
};
const VIEWPORTS = { m360: { width: 360, height: 740 }, d1280: { width: 1280, height: 800 } };

async function axeCheck(page, label) {
  await page.addScriptTag({ path: AXE });
  const r = await page.evaluate(async () => {
    const out = await window.axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice'] } });
    return out.violations.map((v) => ({ id: v.id, impact: v.impact, nodes: v.nodes.slice(0, 3).map((n) => n.target.join(' ')) }));
  });
  check(`axe 0 violations: ${label}`, r.length === 0, r.length ? JSON.stringify(r) : '');
}

const noOverflow = (page) => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth);
const VALIDATE = '**/api/v1/quick-order/validate';
const problem = (status, code) => ({ status, contentType: 'application/json', body: JSON.stringify({ status, error: { code, message: code } }) });

const LIST = ['LC1D09 10', 'LC1DO9 4', 'ZZZ-NOPE-999 2', 'MBN132 ৩', 'lc1d09 5', 'ATV12H018M2 2', 'EDS-205A 3'].join('\n');

(async () => {
  const browser = await launch();
  for (const [lname, loc] of Object.entries(LOCALES)) {
    for (const [vname, vp] of Object.entries(VIEWPORTS)) {
      const tag = `${lname} ${vp.width}px`;
      const ctx = await browser.newContext({ viewport: vp, locale: lname === 'bn' ? 'bn-BD' : 'en-US' });
      const page = await ctx.newPage();
      const errors = [];
      page.on('pageerror', (e) => errors.push(`${page.url()} ${e.message.slice(0, 80)}`));
      const area = page.locator('#qo-text');
      const rows = page.locator('section[aria-labelledby="qo-result"] ul > li');
      const live = page.locator('p[role="status"]');
      const addBtn = page.locator('#qo-add');
      const validateCalls = () => { let n = 0; const h = (r) => r.url().includes('/quick-order/validate') && n++; page.on('request', h); return () => { page.off('request', h); return n; }; };
      const checkBtn = page.getByRole('button', { name: loc.check, exact: true });
      const waitResult = () => page.waitForResponse((r) => r.url().includes('/quick-order/validate') && r.status() === 200);

      // header link, noindex, no JSON-LD, empty page axe
      await page.goto(`${WEB}${loc.prefix}/`, { waitUntil: 'networkidle' });
      check(`[${tag}] header has a plain link to the quick-order page`, (await page.locator(`header a[href="${loc.prefix}/quick-order"]`).count()) === 1);
      await page.locator(`header a[href="${loc.prefix}/quick-order"]`).click();
      await page.waitForURL(/\/quick-order$/);
      await page.waitForLoadState('networkidle');
      await area.waitFor();
      check(`[${tag}] page is noindex, has one h1, no JSON-LD`, (await page.locator('meta[name="robots"][content*="noindex"]').count()) === 1 && (await page.locator('h1').count()) === 1 && (await page.locator('script[type="application/ld+json"]').count()) === 0);
      check(`[${tag}] empty page no horizontal overflow`, await noOverflow(page));
      await axeCheck(page, `${tag} /quick-order empty`);

      // empty input is refused client-side (no request)
      let stop = validateCalls();
      await checkBtn.click();
      const emptyAlerts = await page.locator('main div[role="alert"]').count();
      const emptyCalls = stop();
      check(`[${tag}] empty textarea shows a message and sends nothing`, emptyAlerts === 1 && emptyCalls === 0, `alerts=${emptyAlerts} requests=${emptyCalls}`);

      // paste the mixed list; Ctrl+Enter checks it
      await area.fill(LIST);
      const first = waitResult();
      await area.press('Control+Enter');
      await first;
      await rows.first().waitFor();
      check(`[${tag}] live region says 3 valid, 3 need attention`, (await live.innerText()).trim() === loc.live(loc.digits(3), loc.digits(3)), await live.innerText());
      check(`[${tag}] focus moves to the result heading`, await page.evaluate(() => document.activeElement?.id === 'qo-result'));
      const attention = page.locator('section[aria-labelledby="qo-result"] li[data-status]:not([data-status="OK"])');
      check(`[${tag}] needs-attention rows: 2 not found + 1 out of stock`, (await page.locator('li[data-status="PART_NOT_FOUND"]').count()) === 2 && (await page.locator('li[data-status="OUT_OF_STOCK"]').count()) === 1 && (await attention.count()) === 3);
      const dup = page.locator('li[data-status="OK"]', { hasText: 'LC1D09' }).first();
      check(`[${tag}] duplicate lines merged (15) and Bangla-digit quantity read (3)`, (await dup.locator('input[type="number"]').inputValue()) === '15' && (await page.locator('li[data-status="OK"]', { hasText: 'MBN132' }).locator('input[type="number"]').inputValue()) === '3');
      const sumText = await page.locator('dl').innerText();
      if (lname === 'bn') check(`[${tag}] Bangla digits for prices and counts`, /[০-৯]/.test(sumText) && !/[0-9]/.test(sumText) && /[০-৯]/.test(await dup.innerText()), sumText.replace(/\s+/g, ' '));
      else check(`[${tag}] subtotal shown in BDT`, /৳|BDT/.test(sumText), sumText.replace(/\s+/g, ' '));
      check(`[${tag}] result no horizontal overflow`, await noOverflow(page));
      const headerVisible = await page.locator('div[aria-hidden="true"].md\\:grid').first().isVisible();
      check(`[${tag}] ${vp.width < 768 ? 'small screens show stacked cards (no column header)' : 'wide screens show the column header'}`, vp.width < 768 ? !headerVisible : headerVisible);
      await axeCheck(page, `${tag} /quick-order result`);
      await page.screenshot({ path: `${SHOTS}/quick-order-${lname}-${vname}.png` });

      // keyboard: the suggestion button is reachable and activates with Enter
      const typo = page.locator('li[data-status="PART_NOT_FOUND"]', { hasText: 'LC1DO9' });
      const suggestion = typo.getByRole('button');
      await suggestion.focus();
      const second = waitResult();
      await page.keyboard.press('Enter');
      await second;
      await page.waitForFunction(() => document.querySelectorAll('li[data-status="PART_NOT_FOUND"]').length === 1);
      check(`[${tag}] suggestion fixes the line in the text (one-click)`, (await area.inputValue()).split('\n')[1] === 'LC1D09, 4', JSON.stringify((await area.inputValue()).split('\n')[1]));
      check(`[${tag}] three lines of LC1D09 are merged to 19 and the unknown line is still flagged`, (await dup.locator('input[type="number"]').inputValue()) === '19' && (await live.innerText()).trim() === loc.live(loc.digits(3), loc.digits(2)), await live.innerText());

      // quantity edit: debounced (one request for two keystrokes), server re-validates, no client-side maths
      const totalBefore = (await dup.locator('p.font-semibold').last().innerText()).trim();
      stop = validateCalls();
      const qty = dup.locator('input[type="number"]');
      await qty.fill('');
      await qty.pressSequentially('20', { delay: 60 });
      const third = waitResult();
      await third;
      await page.waitForFunction((b) => [...document.querySelectorAll('li[data-status="OK"]')].some((li) => li.textContent.includes('LC1D09') && !li.textContent.includes(b)), totalBefore);
      check(`[${tag}] quantity edit is checked by the server once (debounced) and the text follows`, stop() === 1 && (await area.inputValue()).split('\n')[0] === 'LC1D09, 20', `${(await area.inputValue()).split('\n')[0]}`);
      const unitText = (await dup.locator('p.text-sm.text-text').nth(0).innerText()).trim();
      const unitQuick = (await dup.innerText()).replace(/\s+/g, ' ');

      // typing after a check makes the result stale: the add button is blocked until checked again
      await area.press('End');
      await area.pressSequentially(' ');
      check(`[${tag}] changing the text blocks adding until checked again`, (await addBtn.isDisabled()) && (await page.getByText(loc.stale).count()) === 1);
      const fourth = waitResult();
      await checkBtn.click();
      await fourth;
      await page.waitForFunction(() => !document.querySelector('section[aria-busy="true"]'));
      check(`[${tag}] check again clears the stale note`, (await page.getByText(loc.stale).count()) === 0 && !(await addBtn.isDisabled()));

      // add: valid lines go into the cart, the badge follows, the report says so, added lines leave the list
      check(`[${tag}] add button names the count (3) and the note says only valid lines are added`, (await addBtn.innerText()).trim() === loc.add(loc.digits(3)) && (await page.locator('#qo-add-note').innerText()).length > 20, await addBtn.innerText());
      await addBtn.click();
      await page.getByRole('link', { name: loc.goToCart }).waitFor();
      const badge = page.locator('header a[href$="/cart"] span[aria-hidden="true"]');
      await badge.waitFor();
      check(`[${tag}] header cart badge shows 3 items`, (await badge.innerText()).trim() === loc.digits(3), await badge.innerText());
      check(`[${tag}] report lists the added lines and focus moves to it`, (await page.locator('section[aria-labelledby="qo-report"] li').count()) === 3 && (await page.evaluate(() => document.activeElement?.id === 'qo-report')));
      await page.waitForFunction(() => document.querySelectorAll('li[data-status="OK"]').length === 0);
      check(`[${tag}] added lines left the text, the rest stays (2 lines) and is re-checked`, (await area.inputValue()).split('\n').filter(Boolean).length === 2 && (await addBtn.isDisabled()) && (await addBtn.innerText()).trim() === loc.none, JSON.stringify(await area.inputValue()));
      check(`[${tag}] report view no horizontal overflow`, await noOverflow(page));
      await axeCheck(page, `${tag} /quick-order after add`);
      await page.getByRole('link', { name: loc.goToCart }).click();
      await page.getByRole('heading', { level: 1, name: loc.cartTitle }).waitFor();
      const cartRows = page.locator('main section ul > li');
      await cartRows.first().waitFor();
      const cartText = (await cartRows.filter({ hasText: 'LC1D09' }).first().innerText()).replace(/\s+/g, ' ');
      check(`[${tag}] cart has the 3 lines with the server price and quantity 20`, (await cartRows.count()) === 3 && cartText.includes(unitText) && (await cartRows.filter({ hasText: 'LC1D09' }).locator('input[type="number"]').inputValue()) === '20', `${cartText} | quick order: ${unitQuick}`);

      // refusals keep the text. 501 lines -> 422 TOO_MANY_LINES (real server)
      await page.goto(`${WEB}${loc.prefix}/quick-order`, { waitUntil: 'networkidle' });
      const big = Array.from({ length: 501 }, () => 'LC1D09 1').join('\n');
      await area.fill(big);
      await checkBtn.click();
      await page.getByRole('alert').filter({ hasText: loc.tooMany }).waitFor();
      check(`[${tag}] 501 lines are refused with a friendly message and the text is kept`, (await area.inputValue()) === big);
      await axeCheck(page, `${tag} /quick-order 501 error`);

      // mocked 429 and 503, then recovery
      await area.fill('LC1D09 2');
      await page.route(VALIDATE, (route) => route.fulfill(problem(429, 'RATE_LIMITED')));
      await checkBtn.click();
      await page.getByRole('alert').filter({ hasText: loc.rate }).waitFor();
      check(`[${tag}] 429 says wait and retry, text kept`, (await area.inputValue()) === 'LC1D09 2');
      await page.unroute(VALIDATE);
      await page.route(VALIDATE, (route) => route.fulfill(problem(503, 'UNAVAILABLE')));
      await page.getByRole('button', { name: loc.retry }).click();
      await page.getByRole('alert').filter({ hasText: loc.unavailable }).waitFor();
      check(`[${tag}] 503 says temporarily unavailable with a retry, text kept`, (await area.inputValue()) === 'LC1D09 2' && (await page.getByRole('button', { name: loc.retry }).count()) === 1);
      await page.unroute(VALIDATE);
      const ok = waitResult();
      await page.getByRole('button', { name: loc.retry }).click();
      await ok;
      await rows.first().waitFor();
      check(`[${tag}] retry works after the outage`, (await page.locator('main div[role="alert"]').count()) === 0 && (await rows.count()) === 1);

      // statuses the seed has no data for: answered by a mocked server, then the real one confirms the fix
      const v = (id, part, title = 'Mock part') => ({ id, sku: `SKU-${part}`, partNumber: part, title, titleBn: title, brand: null, slug: part.toLowerCase(), options: {} });
      const base = { raw: '', part: '', requestedQuantity: 2, message: '', valid: false, variant: null, quantity: null, notices: [], stock: null, unitPriceMinor: null, lineTotalMinor: null, priceSource: null, successor: null, candidates: [], suggestions: [] };
      const mock = {
        lines: [
          { ...base, lineNumbers: [1], raw: 'OLD-1 2', part: 'OLD-1', status: 'PART_OBSOLETE', variant: v('11', 'OLD-1'), successor: { variant: v('12', 'LC1D09', 'Contactor'), isDropIn: true, note: null } },
          { ...base, lineNumbers: [2], raw: 'AMB 1', part: 'AMB', requestedQuantity: 1, status: 'PART_AMBIGUOUS', candidates: [v('21', 'MBN132', 'Breaker A'), v('22', 'ATV12H018M2', 'Drive B')] },
          { ...base, lineNumbers: [3], raw: 'MBN132 900', part: 'MBN132', requestedQuantity: 900, status: 'INSUFFICIENT_STOCK', variant: v('31', 'MBN132'), quantity: 900, stock: { available: 428, status: 'in_stock' }, unitPriceMinor: 1000, lineTotalMinor: 900000 },
          { ...base, lineNumbers: [4], raw: 'PACK 3', part: 'PACK', requestedQuantity: 3, status: 'OK', valid: true, variant: v('41', 'PACK'), quantity: 10, notices: [{ kind: 'quantityAdjustedToPack', was: 3, now: 10 }], stock: { available: 50, status: 'in_stock' }, unitPriceMinor: 500, lineTotalMinor: 5000 }
        ],
        errors: [{ lineNumber: 5, code: 'QTY_MISSING', message: 'x', relatedLines: null }], notices: [],
        summary: { lines: 4, valid: 1, needsAttention: 3, subtotalMinor: 5000, currency: 'BDT' }
      };
      await area.fill('OLD-1 2\nAMB 1\nMBN132 900\nPACK 3\nBADLINE,');
      await page.route(VALIDATE, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ data: mock }) }));
      await checkBtn.click();
      await page.locator('li[data-status="PART_OBSOLETE"]').waitFor();
      const mc = [await page.getByRole('button', { name: loc.replace }).count(), await page.getByRole('combobox', { name: loc.pickLabel }).count(), await page.getByRole('button', { name: loc.useAvail }).count(), await page.locator('li[data-status="QTY_MISSING"]').count(), await page.locator('li[data-status="OK"] p[class*="border-status-pending"]').count()];
      check(`[${tag}] mocked: obsolete offers the successor, ambiguous a select, short stock the max, pack notice, parser error row`, mc[0] === 1 && mc[1] === 1 && mc[2] >= 1 && mc[3] === 1 && mc[4] === 1, mc.join(','));
      check(`[${tag}] mocked: the successor is never swapped in on its own`, (await area.inputValue()).startsWith('OLD-1 2'));
      await axeCheck(page, `${tag} /quick-order statuses (mocked)`);
      check(`[${tag}] mocked statuses no horizontal overflow`, await noOverflow(page));
      await page.unroute(VALIDATE);
      const fix = waitResult();
      await page.getByRole('button', { name: loc.replace }).click();
      await fix;
      await page.waitForFunction(() => document.querySelectorAll('li[data-status="PART_OBSOLETE"]').length === 0 && document.querySelector('textarea').value.startsWith('LC1D09, 2'));
      check(`[${tag}] one click replaces the obsolete part, keeping the quantity`, (await area.inputValue()).split('\n')[0] === 'LC1D09, 2');

      // mixed add result: 1 added, 409 INSUFFICIENT_STOCK, then 429 stops the rest; the failed lines stay; retry adds them
      await area.fill('LC1D09 1\nMBN132 1\nATV12H018M2 1');
      const chk = waitResult();
      await checkBtn.click();
      await chk;
      await page.waitForFunction(() => document.querySelectorAll('li[data-status="OK"]').length === 3);
      let n = 0;
      await page.route('**/api/v1/cart/items', (route) => {
        if (route.request().method() !== 'POST') return route.continue();
        n += 1;
        return n === 1 ? route.continue() : n === 2 ? route.fulfill(problem(409, 'INSUFFICIENT_STOCK')) : route.fulfill(problem(429, 'RATE_LIMITED'));
      });
      await addBtn.click();
      await page.getByRole('heading', { name: /^(Not added|যোগ হয়নি)$/ }).waitFor();
      const reportText = await page.locator('section[aria-labelledby="qo-report"]').innerText();
      check(`[${tag}] mixed add: 1 added, one "not enough stock", one "too many requests"; the failed lines stay in the text`, reportText.includes(loc.insufficient) && reportText.includes(loc.rateAdd) && (await page.locator('section[aria-labelledby="qo-report"] ul').first().locator('li').count()) === 1 && (await area.inputValue()).split('\n').filter(Boolean).length === 2, reportText.replace(/\s+/g, ' '));
      await page.unroute('**/api/v1/cart/items');
      await page.waitForFunction(() => document.querySelectorAll('li[data-status="OK"]').length === 2);
      await addBtn.click();
      await page.waitForFunction(() => document.querySelectorAll('section[aria-labelledby="qo-report"] ul')[0]?.children.length === 3);
      check(`[${tag}] retrying adds the remaining lines; the report accumulates (3 added)`, (await page.locator('section[aria-labelledby="qo-report"] h3').count()) === 0);

      // file picker: reads client-side into the textarea; too big and wrong type are refused
      await page.goto(`${WEB}${loc.prefix}/quick-order`, { waitUntil: 'networkidle' });
      const input = page.locator('input[type="file"]');
      await input.setInputFiles({ name: 'list.csv', mimeType: 'text/csv', buffer: Buffer.from('part,qty\nLC1D09,2\nMBN132,1\n') });
      await page.waitForFunction(() => document.querySelector('textarea').value.includes('LC1D09,2'));
      check(`[${tag}] .csv file fills the textarea (read in the browser)`, (await area.inputValue()).includes('MBN132,1'));
      await input.setInputFiles({ name: 'big.txt', mimeType: 'text/plain', buffer: Buffer.alloc(101 * 1024, 'a') });
      await page.getByRole('alert').filter({ hasText: loc.large }).waitFor();
      check(`[${tag}] file over 100 KB is refused, text kept`, (await area.inputValue()).includes('LC1D09,2'));
      await input.setInputFiles({ name: 'x.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF') });
      await page.getByRole('alert').filter({ hasText: loc.wrongType }).waitFor();
      check(`[${tag}] wrong file type is refused`, true);

      check(`[${tag}] no page errors`, errors.length === 0, errors.join(' | '));
      await ctx.close();
    }
  }
  await browser.close();
  const failed = results.filter((r) => !r).length;
  console.log(`\n${results.length - failed}/${results.length} passed`);
  process.exit(failed ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(1); });
