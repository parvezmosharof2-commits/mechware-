import test from 'node:test';
import assert from 'node:assert/strict';
import {
  PRICE_LIST_MAX_UNIT_PRICE_MINOR, breaksOutOfOrder, canRevise, chunkEntries, dateToUtcIso, displayStateLabel, entriesNeedingReason,
  entryFieldErrors, formatTaka, isEditable, minQtyProblem, parsePaste, priceListErrorText, priceListQuery, priceProblem,
  takaToMinor, validateHeader
} from '../src/price-lists-model.ts';

test('taka text becomes poisha without float errors', () => {
  assert.equal(takaToMinor('12'), 1200);
  assert.equal(takaToMinor('12.5'), 1250);
  assert.equal(takaToMinor('12.50'), 1250);
  assert.equal(takaToMinor('0.29'), 29);
  assert.equal(takaToMinor('19.99'), 1999);
  assert.equal(takaToMinor('1,234.56'), 123456);
  assert.equal(takaToMinor('1234567890.12'), 123456789012);
  assert.equal(takaToMinor('12.345'), null);
  assert.equal(takaToMinor('-5'), null);
  assert.equal(takaToMinor('1e3'), null);
  assert.equal(takaToMinor(''), null);
  assert.equal(takaToMinor('abc'), null);
});

test('poisha formats back with two decimals', () => {
  assert.equal(formatTaka(1250), '12.50');
  assert.equal(formatTaka(5), '0.05');
  assert.equal(formatTaka(123456789), '1,234,567.89');
});

test('price limits: at least 1 poisha, at most the maximum', () => {
  assert.equal(priceProblem(null), 'price.format');
  assert.equal(priceProblem(0), 'price.tooLow');
  assert.equal(priceProblem(1), null);
  assert.equal(priceProblem(PRICE_LIST_MAX_UNIT_PRICE_MINOR), null);
  assert.equal(priceProblem(PRICE_LIST_MAX_UNIT_PRICE_MINOR + 1), 'price.tooHigh');
});

test('minimum quantity: whole, at least 1, General lists need 2 or more', () => {
  assert.notEqual(minQtyProblem(0, 'Company'), null);
  assert.notEqual(minQtyProblem(1.5, 'Company'), null);
  assert.equal(minQtyProblem(1, 'Company'), null);
  assert.notEqual(minQtyProblem(1, 'General'), null);
  assert.equal(minQtyProblem(2, 'General'), null);
  assert.notEqual(minQtyProblem(10_001, 'Company'), null);
});

test('header: company list needs company and contract ref; window at most 366 days and end after start', () => {
  const ok = { scope: 'Company', companyId: 'c', contractRef: 'CT-1', name: 'N', validFromUtc: '2026-01-01T00:00:00Z', validToUtc: '2026-12-31T00:00:00Z' };
  assert.deepEqual(validateHeader(ok), {});
  assert.ok(validateHeader({ ...ok, companyId: null }).companyId);
  assert.ok(validateHeader({ ...ok, contractRef: ' ' }).contractRef);
  assert.deepEqual(validateHeader({ ...ok, scope: 'General', companyId: null, contractRef: null }), {});
  assert.ok(validateHeader({ ...ok, validToUtc: '2026-01-01T00:00:00Z' }).validToUtc);
  assert.ok(validateHeader({ ...ok, validToUtc: '2027-01-03T00:00:00Z' }).validToUtc);
  assert.equal(validateHeader({ ...ok, validToUtc: '2027-01-02T00:00:00Z' }).validToUtc, undefined);
  assert.ok(validateHeader({ ...ok, name: '  ' }).name);
  assert.ok(validateHeader({ ...ok, scope: 'Group' }).scope);
});

test('date input to UTC midnight', () => {
  assert.equal(dateToUtcIso('2026-12-01'), '2026-12-01T00:00:00.000Z');
  assert.equal(dateToUtcIso(''), '');
});

test('paste parser: valid lines, header and blanks skipped, bad lines reported with line numbers', () => {
  const text = ['sku, minQty, price', '', 'A-1, 1, 100', 'B-2;10;1,250.50', 'C-3\t5\t7.5\tPromo deal', 'D-4, x, 5', 'E-5, 1, 0', 'F-6, 1', 'A-1, 1, 90', ', 1, 5'].join('\n');
  const r = parsePaste(text, 'Company');
  assert.deepEqual(r.lines.map((l) => [l.sku, l.minQty, l.unitPriceMinor, l.reason]), [
    ['A-1', 1, 10000, null],
    ['B-2', 10, 125050, null],
    ['C-3', 5, 750, 'Promo deal']
  ].map(([s, q, p, rs]) => [s, q, p, rs]));
  assert.deepEqual(r.problems.map((p) => p.line), [6, 7, 8, 9, 10]);
});

test('paste parser: "1,250.50" splits on commas, so a price with a thousands comma must be quoted away (use plain digits)', () => {
  const r = parsePaste('B-2, 10, 1250.50');
  assert.equal(r.lines[0].unitPriceMinor, 125050);
});

test('paste parser: General list refuses minQty 1; short reason refused', () => {
  assert.equal(parsePaste('A, 1, 10', 'General').problems.length, 1);
  assert.equal(parsePaste('A, 2, 10', 'General').lines.length, 1);
  assert.equal(parsePaste('A, 2, 10, abc', 'General').problems.length, 1);
});

test('batches are split at 500', () => {
  const chunks = chunkEntries(Array.from({ length: 1201 }, (_, i) => i));
  assert.deepEqual(chunks.map((c) => c.length), [500, 500, 201]);
});

test('list query: empties dropped, page size capped at 50', () => {
  assert.equal(priceListQuery({}), 'page=1&pageSize=20');
  assert.equal(priceListQuery({ status: 'Draft', q: ' abc ', page: 3, pageSize: 500 }), 'status=Draft&q=abc&page=3&pageSize=50');
});

test('below-floor entries without a reason block submit; absent flags (no view_cost) never do', () => {
  const e = (o: object) => ({ id: 'i', variantId: 'v', sku: 'S', title: 't', minQty: 1, unitPriceMinor: 1, belowFloorReason: null, ...o });
  assert.equal(entriesNeedingReason([e({ belowFloor: true })]).length, 1);
  assert.equal(entriesNeedingReason([e({ belowFloor: true, belowFloorReason: 'agreed with owner' })]).length, 0);
  assert.equal(entriesNeedingReason([e({})]).length, 0);
});

test('breaks must not get dearer as the quantity grows', () => {
  const e = (minQty: number, p: number) => ({ id: `${minQty}`, variantId: 'v', sku: 'S', title: 't', minQty, unitPriceMinor: p, belowFloorReason: null });
  assert.deepEqual(breaksOutOfOrder([e(1, 100), e(10, 90)]), []);
  assert.deepEqual(breaksOutOfOrder([e(1, 100), e(10, 110)]), ['S']);
});

test('errors in plain words', () => {
  const base = { code: null, message: '', fields: {} };
  assert.match(priceListErrorText({ ...base, status: 403 }), /permission/);
  assert.match(priceListErrorText({ ...base, status: 401 }), /Sign in/);
  assert.match(priceListErrorText({ ...base, status: 409, code: 'PRICE_LIST_NOT_EDITABLE' }), /no longer a draft/);
  assert.match(priceListErrorText({ ...base, status: 409, code: 'PRICE_LIST_OPEN_VERSION_EXISTS' }), /newer draft/);
  assert.match(priceListErrorText({ ...base, status: 500 }), /try again/);
  assert.match(priceListErrorText({ ...base, status: null }), /Could not reach/);
  assert.deepEqual(entryFieldErrors({ 'entries.abc.belowFloorReason': 'needs reason', other: 'x' }), { abc: 'needs reason' });
});

test('state labels and capabilities', () => {
  assert.equal(displayStateLabel('PendingApproval'), 'Pending approval');
  assert.equal(displayStateLabel('Weird'), 'Weird');
  assert.equal(isEditable('Draft'), true);
  assert.equal(isEditable('PendingApproval'), false);
  assert.equal(canRevise('Approved'), true);
  assert.equal(canRevise('Withdrawn'), true);
  assert.equal(canRevise('PendingApproval'), false);
});

// ---------- D2.12a decisions ----------
import {
  DECISION_ERROR_TEXT, belowFloorSummary, canApprove, canReject, canWithdraw, decisionErrorText, decisionReasonProblem, isStepUpCodeShape,
  shortHash, shouldReloadAfter, actorName, type PriceListDetail
} from '../src/price-lists-model.ts';

const detail = (over: Partial<PriceListDetail> = {}): PriceListDetail => ({
  id: 'l1', seriesId: 's1', version: 2, name: 'N', scope: 'Company', companyId: 'c1', contractRef: 'CT-1', status: 'PendingApproval', displayState: 'PendingApproval',
  validFromUtc: '2026-12-01T00:00:00Z', validToUtc: '2027-01-01T00:00:00Z', entryCount: 2, createdBy: 'author', createdAtUtc: '2026-11-01T00:00:00Z',
  submittedBy: 'submitter', submittedAtUtc: '2026-11-02T00:00:00Z', contentHash: 'abcdef0123456789', effectiveFromUtc: null, approvedBy: null, approvedAtUtc: null,
  rejectionReason: null, withdrawReason: null, contributors: ['editor'], belowFloorEntryIds: [], actorNames: { author: 'Ann Author' },
  entries: [
    { id: 'e1', variantId: 'v1', sku: 'SKU-1', title: 'One', minQty: 1, unitPriceMinor: 1000, belowFloorReason: ' contract deal ' },
    { id: 'e2', variantId: 'v2', sku: 'SKU-2', title: 'Two', minQty: 5, unitPriceMinor: 900, belowFloorReason: null }
  ],
  ...over
});
const finance = { id: 'fin', permissions: ['pricelists.approve', 'pricing.view_cost'] };
const admin = { id: 'adm', permissions: ['pricelists.approve', 'pricelists.approve_below_floor', 'pricing.view_cost'] };

test('every decision error code has plain words and the reload set is right', () => {
  for (const code of ['PRICE_LIST_NOT_PENDING', 'PRICE_LIST_FOUR_EYES', 'PRICE_LIST_CHANGED', 'PRICE_LIST_WINDOW_PASSED', 'PRICE_BELOW_COST', 'BELOW_FLOOR_REASON_REQUIRED',
    'BELOW_FLOOR_PERMISSION_REQUIRED', 'BELOW_FLOOR_NOT_ACKNOWLEDGED', 'STEP_UP_REQUIRED', 'STEP_UP_FAILED']) {
    assert.ok(DECISION_ERROR_TEXT[code], code);
    assert.equal(decisionErrorText({ status: 409, code, message: 'server text', fields: {} }), DECISION_ERROR_TEXT[code]);
  }
  assert.match(decisionErrorText({ status: 403, code: 'STEP_UP_FAILED', message: '', fields: {} }), /The code was not accepted/);
  assert.match(decisionErrorText({ status: 409, code: 'PRICE_LIST_NOT_PENDING', message: '', fields: {} }), /Someone else already decided/);
  assert.match(decisionErrorText({ status: 409, code: 'PRICE_LIST_CHANGED', message: '', fields: {} }), /changed after you opened it/);
  assert.equal(decisionErrorText({ status: 500, code: null, message: '', fields: {} }), priceListErrorText({ status: 500, code: null, message: '', fields: {} }));
  assert.ok(shouldReloadAfter('PRICE_LIST_CHANGED') && shouldReloadAfter('PRICE_LIST_NOT_PENDING'));
  assert.ok(!shouldReloadAfter('STEP_UP_FAILED') && !shouldReloadAfter(null));
});

test('reason is checked before a step-up code is asked for', () => {
  assert.equal(decisionReasonProblem(''), 'Enter a reason.');
  assert.equal(decisionReasonProblem('    '), 'Enter a reason.');
  assert.match(decisionReasonProblem('abcd')!, /at least 5/);
  assert.equal(decisionReasonProblem('abcde'), null);
  assert.equal(decisionReasonProblem('  abcde  '), null);
  assert.match(decisionReasonProblem('x'.repeat(501))!, /at most 500/);
  assert.equal(decisionReasonProblem('x'.repeat(500)), null);
  assert.ok(isStepUpCodeShape('123456') && isStepUpCodeShape(' 123456 ') && !isStepUpCodeShape('12345') && !isStepUpCodeShape('12345a'));
});

test('canApprove is a hint: four eyes, permission, below-floor permission, state', () => {
  assert.deepEqual(canApprove(detail(), finance), { allowed: true, reason: null });
  for (const who of ['author', 'submitter', 'editor']) {
    const h = canApprove(detail(), { ...finance, id: who });
    assert.equal(h.allowed, false);
    assert.equal(h.reason, 'You prepared this list; someone else must approve it.');
  }
  assert.equal(canApprove(detail(), { id: 'x', permissions: ['pricelists.view'] }).allowed, false);
  const below = detail({ belowFloorEntryIds: ['e1'] });
  assert.equal(canApprove(below, finance).reason, 'An administrator with below-floor permission must approve this list.');
  assert.equal(canApprove(below, admin).allowed, true);
  assert.equal(canApprove(detail({ status: 'Approved' }), admin).allowed, false);
  assert.ok(canReject(detail(), finance) && !canReject(detail({ status: 'Approved' }), finance));
  assert.ok(canWithdraw(detail({ status: 'Approved' }), finance) && !canWithdraw(detail(), finance));
});

test('below-floor summary uses ids and reasons only, never amounts', () => {
  const s = belowFloorSummary(detail({ belowFloorEntryIds: ['e1', 'e1', 'e2', 'gone'] }));
  assert.equal(s.count, 3);
  assert.deepEqual(s.entries.map((e) => [e.id, e.reason]), [['e1', 'contract deal'], ['e2', '']]);
  assert.equal(s.text, '3 entries are below the margin floor.');
  assert.equal(belowFloorSummary(detail({ belowFloorEntryIds: ['e2'] })).text, '1 entry is below the margin floor.');
  assert.equal(belowFloorSummary(detail()).count, 0);
  assert.equal(belowFloorSummary(detail({ belowFloorEntryIds: undefined })).count, 0);
  assert.ok(!JSON.stringify(s).includes('1000'));
});

test('names and hashes read well', () => {
  assert.equal(shortHash('abcdef0123456789'), 'abcdef01…');
  assert.equal(shortHash(null), 'none');
  assert.equal(actorName(detail(), 'author'), 'Ann Author');
  assert.equal(actorName(detail(), 'zzzzzzzzzzzz'), 'user zzzzzzzz');
  assert.equal(actorName(detail(), null), '');
});
