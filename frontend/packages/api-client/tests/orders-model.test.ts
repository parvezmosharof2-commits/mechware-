import test from 'node:test';
import assert from 'node:assert/strict';
import {
  ORDER_STATUSES,
  clampPage,
  findOrderIdByNumber,
  isContractPrice,
  isOrderId,
  isValidOrderNumber,
  orderStatusLabelKey,
  orderStatusTone,
  orderTimelineIndex,
  totalPages,
  type OrderPage
} from '../src/orders-model.ts';

test('every wire status maps to its own label key; unknown ones to the generic key', () => {
  assert.equal(ORDER_STATUSES.length, 15);
  for (const s of ORDER_STATUSES) assert.equal(orderStatusLabelKey(s), `status_${s}`);
  for (const s of ['', 'weird', 'PENDING_PAYMENT', null, undefined]) assert.equal(orderStatusLabelKey(s), 'status_other');
});

test('every status has a tone and a timeline position without throwing', () => {
  for (const s of [...ORDER_STATUSES, 'unknown']) {
    assert.ok(['pending', 'progress', 'done', 'problem', 'neutral'].includes(orderStatusTone(s)));
    const i = orderTimelineIndex(s);
    assert.ok(i === null || (i >= 0 && i <= 3));
  }
  assert.equal(orderTimelineIndex('pending_payment'), 0);
  assert.equal(orderTimelineIndex('packed'), 1);
  assert.equal(orderTimelineIndex('out_for_delivery'), 2);
  assert.equal(orderTimelineIndex('completed'), 3);
  assert.equal(orderTimelineIndex('cancelled'), null);
  assert.equal(orderStatusTone('cancelled'), 'problem');
});

test('pagination helpers', () => {
  assert.equal(totalPages(0), 1);
  assert.equal(totalPages(20), 1);
  assert.equal(totalPages(21), 2);
  assert.equal(totalPages(NaN), 1);
  assert.equal(clampPage('3'), 3);
  assert.equal(clampPage('0'), 1);
  assert.equal(clampPage('-4'), 1);
  assert.equal(clampPage('abc'), 1);
  assert.equal(clampPage(null), 1);
  assert.equal(clampPage('9', 4), 4);
  assert.equal(clampPage('2.7'), 2);
});

test('order number and id shape checks', () => {
  assert.equal(isValidOrderNumber('MW-2026-000123'), true);
  assert.equal(isValidOrderNumber('../x'), false);
  assert.equal(isValidOrderNumber('a b'), false);
  assert.equal(isValidOrderNumber(''), false);
  assert.equal(isValidOrderNumber('x'.repeat(41)), false);
  assert.equal(isOrderId('11111111-1111-4111-8111-111111111111'), true);
  assert.equal(isOrderId('MW-1'), false);
});

const row = (n: string, id: string) => ({ id, orderNumber: n, status: 'confirmed', grandTotalMinor: 1, createdAt: '2026-01-01T00:00:00Z', itemCount: 1 });
function fakeList(all: ReturnType<typeof row>[]) {
  const calls: number[] = [];
  const fetchPage = async (page: number, pageSize: number): Promise<OrderPage> => {
    calls.push(page);
    return { items: all.slice((page - 1) * pageSize, page * pageSize), page, pageSize, totalCount: all.length };
  };
  return { fetchPage, calls };
}

test('order number lookup pages until found and stops there', async () => {
  const all = Array.from({ length: 45 }, (_, i) => row(`MW-${i}`, `id-${i}`));
  const a = fakeList(all);
  assert.equal(await findOrderIdByNumber('MW-3', a.fetchPage), 'id-3');
  assert.deepEqual(a.calls, [1]);
  const b = fakeList(all);
  assert.equal(await findOrderIdByNumber('mw-44', b.fetchPage), 'id-44'); // case-insensitive, third page
  assert.deepEqual(b.calls, [1, 2, 3]);
});

test('order number lookup gives up at the last page or the page limit, and never invents an id', async () => {
  const small = fakeList([row('MW-1', 'a')]);
  assert.equal(await findOrderIdByNumber('MW-9', small.fetchPage), null);
  assert.deepEqual(small.calls, [1]);
  const big = fakeList(Array.from({ length: 300 }, (_, i) => row(`MW-${i}`, `id-${i}`)));
  assert.equal(await findOrderIdByNumber('MW-299', big.fetchPage, 5), null);
  assert.deepEqual(big.calls, [1, 2, 3, 4, 5]);
});

test('a failing list request is an error, not "not found"', async () => {
  await assert.rejects(findOrderIdByNumber('MW-1', async () => { throw new Error('down'); }), /down/);
});

test('contract price detection', () => {
  assert.equal(isContractPrice({ priceSource: 'contract' }), true);
  assert.equal(isContractPrice({ priceSource: 'list' }), false);
  assert.equal(isContractPrice({ priceSource: null }), false);
});
