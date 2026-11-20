import test from 'node:test';
import assert from 'node:assert/strict';
import {
  EMPTY_ORDER_FILTERS, activeOrderFilterCount, creditStateText, isDhakaDate, listOverdueWords, orderFilterProblem, orderFiltersToSearch, orderListQuery, orderStatusText,
  parseOrderFilters, paymentStateText, paymentStatusText, priceSnapshotText, timelineText, ORDER_STATUS_FILTERS
} from '../src/staff-orders-read-model.ts';
import { staffOrderStatusLabel } from '../src/staff-orders-model.ts';

const GUID = '3f2b8c1e-0a4d-4e6b-9c7d-1234567890ab';
const parse = (q: string) => { const p = new URLSearchParams(q); return parseOrderFilters((k) => p.get(k)); };

test('dates: only real calendar days in yyyy-MM-dd', () => {
  assert.equal(isDhakaDate('2026-11-21'), true);
  assert.equal(isDhakaDate('2024-02-29'), true);
  assert.equal(isDhakaDate('2026-02-29'), false);
  assert.equal(isDhakaDate('2026-13-01'), false);
  assert.equal(isDhakaDate('21-11-2026'), false);
  assert.equal(isDhakaDate(''), false);
});

test('URL round trip: every filter survives write then read, and defaults are left out', () => {
  const f = { status: 'confirmed', paymentState: 'on_credit', overdue: 'true', orderNumber: 'MW-26', companyId: GUID, companyName: 'Rahim Mills & Sons Ltd', from: '2026-11-01', to: '2026-11-21', page: 3 };
  const search = orderFiltersToSearch(f);
  assert.deepEqual(parse(search), f);
  assert.equal(orderFiltersToSearch(EMPTY_ORDER_FILTERS), '');
  assert.deepEqual(parse(''), EMPTY_ORDER_FILTERS);
});

test('a shared link with bad values opens the list without those filters', () => {
  const f = parse(`status=nonsense&payment=paid&overdue=maybe&number=MW%2F1&company=abc&companyName=X&from=2026-02-30&to=x&page=-4`);
  assert.deepEqual(f, EMPTY_ORDER_FILTERS);
  assert.equal(parse('page=2.5').page, 1);
  assert.equal(parse(`companyName=Orphan`).companyName, '');
});

test('every status in the filter has words, and the shared label helper knows the long names', () => {
  for (const s of ORDER_STATUS_FILTERS) assert.ok(s.label.length > 0 && staffOrderStatusLabel(s.value) !== s.value, s.value);
  assert.equal(orderStatusText('pending_payment'), 'Waiting for payment');
  assert.equal(orderStatusText('some_new_state'), 'Some new state');
  assert.equal(orderStatusText(null), '');
});

test('API query: wire names, page and a fixed page size of at most 50', () => {
  const q = new URLSearchParams(orderListQuery({ ...EMPTY_ORDER_FILTERS, status: 'packed', paymentState: 'needs_refund', overdue: 'false', orderNumber: 'MW', companyId: GUID, from: '2026-11-01', to: '2026-11-02', page: 2 }));
  assert.equal(q.get('status'), 'packed');
  assert.equal(q.get('paymentState'), 'needs_refund');
  assert.equal(q.get('overdue'), 'false');
  assert.equal(q.get('orderNumber'), 'MW');
  assert.equal(q.get('companyId'), GUID);
  assert.equal(q.get('from'), '2026-11-01');
  assert.equal(q.get('page'), '2');
  assert.ok(Number(q.get('pageSize')) <= 50);
  assert.equal(new URLSearchParams(orderListQuery({ ...EMPTY_ORDER_FILTERS, page: 0 })).get('page'), '1');
  assert.equal(new URLSearchParams(orderListQuery(EMPTY_ORDER_FILTERS)).get('status'), null);
});

test('filter problems are found before anything is sent', () => {
  assert.equal(orderFilterProblem({ orderNumber: 'MW-26', from: '2026-11-01', to: '2026-11-01' }), null);
  assert.match(orderFilterProblem({ orderNumber: 'MW 26', from: '', to: '' }) ?? '', /letters, digits and dashes/);
  assert.match(orderFilterProblem({ orderNumber: '', from: '2026-11-05', to: '2026-11-01' }) ?? '', /after/);
  assert.match(orderFilterProblem({ orderNumber: '', from: '2026-02-30', to: '' }) ?? '', /from/);
  assert.match(orderFilterProblem({ orderNumber: 'A'.repeat(41), from: '', to: '' }) ?? '', /up to 40/);
});

test('active filter count ignores page and the company name', () => {
  assert.equal(activeOrderFilterCount(EMPTY_ORDER_FILTERS), 0);
  assert.equal(activeOrderFilterCount({ ...EMPTY_ORDER_FILTERS, status: 'packed', companyId: GUID, companyName: 'X', page: 4 }), 2);
});

test('words: payment state, payment status, credit, timeline, overdue', () => {
  assert.equal(paymentStateText('on_credit'), 'On credit');
  assert.equal(paymentStateText('needs_refund'), 'Needs a refund');
  assert.equal(paymentStatusText('captured'), 'Paid');
  assert.equal(paymentStatusText('partly_odd'), 'Partly odd');
  assert.equal(creditStateText('partially_paid'), 'Partly paid');
  assert.equal(timelineText({ fromStatus: 'confirmed', toStatus: 'cancelled' }), 'Confirmed to Cancelled');
  assert.equal(timelineText({ fromStatus: null, toStatus: 'pending_payment' }), 'Started as waiting for payment');
  assert.deepEqual(listOverdueWords({ deliveryOverdue: true, handoverOverdue: true }), ['Late for delivery', 'Late for courier handover']);
  assert.deepEqual(listOverdueWords({ deliveryOverdue: false, handoverOverdue: false }), []);
});

test('price snapshot reads in words and leaves missing parts out', () => {
  const none = { source: null, basis: null, priceListId: null, priceListVersion: null, contractRef: null, publicUnitPriceMinor: null, quoteLineId: null };
  assert.equal(priceSnapshotText(none), 'Price at order time');
  assert.equal(priceSnapshotText({ ...none, source: 'contract', contractRef: 'C-7', basis: 'net_of_vat' }), 'Contract price (ref C-7), excl. VAT');
  assert.equal(priceSnapshotText({ ...none, source: 'list', priceListVersion: 3, basis: 'gross_incl_vat' }), 'Price list (list version 3), incl. VAT');
  assert.equal(priceSnapshotText({ ...none, source: 'quote' }), 'Quote price');
});
