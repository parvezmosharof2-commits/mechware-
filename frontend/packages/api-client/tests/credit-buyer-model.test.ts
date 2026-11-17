import test from 'node:test';
import assert from 'node:assert/strict';
import {
  buyerAgingRows, classifyCreditError, clampCreditPage, clampCreditPageSize, creditPageCount, creditStatusKey, creditStatusTone, creditUsedPercent,
  formatCreditDay, hasCreditBalance, isCreditDay, orderCreditOwes, orderCreditVisible, buyerPageQuery, buyerPaymentMethodKey, creditSignOf, buyerStatementFilterProblem,
  buyerStatementKindKey, buyerStatementQuery, creditUsageTone
} from '../src/credit-buyer-model.ts';

const money = { minorUnits: 1, currency: 'BDT' };

test('hasCreditBalance: null fields (Requester/Approver) and no terms show nothing numeric', () => {
  assert.equal(hasCreditBalance({ creditTerms: 'net30', creditLimit: money, creditUsed: money, creditAvailable: money }), true);
  assert.equal(hasCreditBalance({ creditTerms: 'net30', creditLimit: null, creditUsed: null, creditAvailable: null }), false);
  assert.equal(hasCreditBalance({ creditTerms: 'net30', creditLimit: money, creditUsed: null, creditAvailable: money }), false);
  assert.equal(hasCreditBalance({ creditTerms: 'none', creditLimit: money, creditUsed: money, creditAvailable: money }), false);
  assert.equal(hasCreditBalance(null), false);
  assert.equal(hasCreditBalance(undefined), false);
});

test('classifyCreditError: 404 is "hidden" for a member and "not found" for everyone else; outages are retryable', () => {
  assert.equal(classifyCreditError(404, true), 'hidden');
  assert.equal(classifyCreditError(404, false), 'notFound');
  assert.equal(classifyCreditError(403, true), 'hidden');
  assert.equal(classifyCreditError(403, false), 'notFound');
  assert.equal(classifyCreditError(500, true), 'unavailable');
  assert.equal(classifyCreditError(429, false), 'unavailable');
  assert.equal(classifyCreditError(null, true), 'unavailable');
  assert.equal(classifyCreditError(undefined, false), 'unavailable');
});

test('creditUsedPercent clamps and never divides by zero', () => {
  assert.equal(creditUsedPercent(0, 100), 0);
  assert.equal(creditUsedPercent(-5, 100), 0);
  assert.equal(creditUsedPercent(1000, 0), 0);
  assert.equal(creditUsedPercent(1000, 250), 25);
  assert.equal(creditUsedPercent(1000, 5000), 100);
  assert.equal(creditUsedPercent(Number.NaN, 5), 0);
});

test('creditUsageTone thresholds at 80 and 90', () => {
  assert.equal(creditUsageTone(79), 'accent');
  assert.equal(creditUsageTone(80), 'warning');
  assert.equal(creditUsageTone(89), 'warning');
  assert.equal(creditUsageTone(90), 'fault');
});

test('isCreditDay accepts only real calendar days', () => {
  assert.equal(isCreditDay('2026-11-17'), true);
  assert.equal(isCreditDay('2026-02-29'), false);
  assert.equal(isCreditDay('2028-02-29'), true);
  assert.equal(isCreditDay('17-11-2026'), false);
  assert.equal(isCreditDay(''), false);
  assert.equal(isCreditDay('2026-11-17T00:00:00Z'), false);
});

test('formatCreditDay keeps the Dhaka day whatever the zone, English and Bangla, junk is empty', () => {
  assert.equal(formatCreditDay('2026-11-17', 'en'), '17 Nov 2026');
  assert.equal(formatCreditDay('2026-01-01', 'en'), '1 Jan 2026');
  assert.match(formatCreditDay('2026-11-17', 'bn'), /১৭/);
  assert.equal(formatCreditDay('nope', 'en'), '');
  assert.equal(formatCreditDay(null, 'en'), '');
});

test('creditSignOf', () => {
  assert.equal(creditSignOf(5), '+');
  assert.equal(creditSignOf(-5), '-');
  assert.equal(creditSignOf(0), '');
});

test('status keys: unknown statuses and kinds fall back to "other", never raw wire text', () => {
  assert.equal(creditStatusKey('partially_paid'), 'partially_paid');
  assert.equal(creditStatusKey('cancelled'), 'cancelled');
  assert.equal(creditStatusKey('paid', 'item'), 'other'); // a paid order is never an open item
  assert.equal(creditStatusKey('weird'), 'other');
  assert.equal(creditStatusKey(null), 'other');
  assert.equal(buyerStatementKindKey('charge'), 'charge');
  assert.equal(buyerStatementKindKey('on_account_payout'), 'on_account_payout');
  assert.equal(buyerStatementKindKey('<script>'), 'other');
  assert.equal(buyerPaymentMethodKey('bkash'), 'bkash');
  assert.equal(buyerPaymentMethodKey('???'), 'other');
  assert.equal(buyerPaymentMethodKey(null), 'other');
});

test('creditStatusTone', () => {
  assert.equal(creditStatusTone('overdue'), 'problem');
  assert.equal(creditStatusTone('cancelled'), 'problem');
  assert.equal(creditStatusTone('paid'), 'done');
  assert.equal(creditStatusTone('open'), 'pending');
  assert.equal(creditStatusTone('partially_paid'), 'pending');
  assert.equal(creditStatusTone('x'), 'neutral');
});

test('orderCreditVisible: null, absent, unknown status or junk date hide the line; owes only for open statuses', () => {
  assert.equal(orderCreditVisible(null), false);
  assert.equal(orderCreditVisible(undefined), false);
  assert.equal(orderCreditVisible({ status: 'mystery', dueOn: '2026-12-01', outstandingMinor: 1 }), false);
  assert.equal(orderCreditVisible({ status: 'open', dueOn: 'x', outstandingMinor: 1 }), false);
  const open = { status: 'open', dueOn: '2026-12-01', outstandingMinor: 500 };
  assert.equal(orderCreditVisible(open), true);
  assert.equal(orderCreditOwes(open), true);
  assert.equal(orderCreditOwes({ ...open, status: 'overdue' }), true);
  assert.equal(orderCreditOwes({ ...open, status: 'paid', outstandingMinor: 0 }), false);
  assert.equal(orderCreditOwes({ ...open, status: 'cancelled', outstandingMinor: 0 }), false);
});

test('buyerStatementFilterProblem: empty is fine, bad days and from > to are named', () => {
  assert.equal(buyerStatementFilterProblem('', ''), null);
  assert.equal(buyerStatementFilterProblem('2026-11-01', ''), null);
  assert.equal(buyerStatementFilterProblem('', '2026-11-30'), null);
  assert.equal(buyerStatementFilterProblem('2026-11-30', '2026-11-30'), null);
  assert.equal(buyerStatementFilterProblem('2026-13-01', ''), 'fromFormat');
  assert.equal(buyerStatementFilterProblem('', '30/11/2026'), 'toFormat');
  assert.equal(buyerStatementFilterProblem('2026-12-01', '2026-11-01'), 'order');
});

test('buyerStatementQuery drops bad days and clamps paging', () => {
  assert.equal(buyerStatementQuery({}), 'page=1&pageSize=10');
  assert.equal(buyerStatementQuery({ from: '2026-11-01', to: '2026-11-30', page: 3, pageSize: 20 }), 'from=2026-11-01&to=2026-11-30&page=3&pageSize=20');
  assert.equal(buyerStatementQuery({ from: 'junk', to: '2026-02-30', page: 0, pageSize: 5000 }), 'page=1&pageSize=100');
  assert.equal(buyerStatementQuery({ page: -4, pageSize: 0 }), 'page=1&pageSize=1');
  assert.equal(buyerPageQuery({ page: 2.9, pageSize: 25 }), 'page=2&pageSize=25');
});

test('paging helpers', () => {
  assert.equal(clampCreditPage(undefined), 1);
  assert.equal(clampCreditPage(Number.NaN), 1);
  assert.equal(clampCreditPageSize(undefined), 10);
  assert.equal(creditPageCount(0, 10), 1);
  assert.equal(creditPageCount(21, 10), 3);
  assert.equal(creditPageCount(-3, 10), 1);
});

test('buyerAgingRows keeps all five buckets in order', () => {
  const rows = buyerAgingRows({ current: 1, days1To30: 2, days31To60: 3, days61To90: 4, days90Plus: 5 });
  assert.deepEqual(rows.map((r) => r.key), ['current', 'days1To30', 'days31To60', 'days61To90', 'days90Plus']);
  assert.deepEqual(rows.map((r) => r.minor), [1, 2, 3, 4, 5]);
});
