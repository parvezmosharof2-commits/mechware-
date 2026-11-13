import test from 'node:test';
import assert from 'node:assert/strict';
import { checkoutErrorKey, orderStatusKey, readCheckoutParams, skusFromError, writeCheckoutParams } from './checkout-ui.ts';

const A = '11111111-1111-4111-8111-111111111111';
const C = '22222222-2222-4222-8222-222222222222';
const S = '33333333-3333-4333-8333-333333333333';

test('personal flow state round-trips through the URL', () => {
  const qs = writeCheckoutParams({ step: 'review', dest: { addressId: A, poNumber: 'ignored' }, key: 'abcd1234-ef' });
  assert.equal(qs, `step=review&a=${A}&k=abcd1234-ef`);
  const back = readCheckoutParams(new URLSearchParams(qs));
  assert.equal(back.step, 'review');
  assert.equal(back.dest.addressId, A);
  assert.equal(back.dest.companyId, null);
  assert.equal(back.key, 'abcd1234-ef');
});

test('company flow state keeps site, cost centre and PO number; the address is dropped', () => {
  const qs = writeCheckoutParams({ step: 'delivery', dest: { addressId: A, companyId: C, siteId: S, costCentreId: S, poNumber: ' PO 1/2 ' }, key: null });
  const back = readCheckoutParams(new URLSearchParams(qs));
  assert.deepEqual(back.dest, { addressId: null, companyId: C, siteId: S, costCentreId: S, poNumber: 'PO 1/2' });
  assert.equal(back.step, 'delivery');
  assert.equal(back.key, null);
});

test('hostile or malformed parameters are ignored', () => {
  const back = readCheckoutParams(new URLSearchParams({ step: 'pay', a: '../../x', c: 'nope', s: S, k: 'bad key!' }));
  assert.equal(back.step, 'delivery');
  assert.equal(back.dest.addressId, null);
  assert.equal(back.dest.companyId, null);
  assert.equal(back.dest.siteId, null);
  assert.equal(back.key, null);
});

test('message keys and server SKU details', () => {
  assert.equal(checkoutErrorKey('notDeliverable'), 'err_notDeliverable');
  assert.equal(orderStatusKey('pending_approval'), 'status_pending_approval');
  assert.equal(orderStatusKey('weird'), 'status_other');
  const e = { status: 422, error: { code: 'SHIPPING_QUOTE_ONLY', message: 'x', details: [{ field: 'heavy', message: 'SKU-1' }, { field: 'heavy', message: 'SKU-1' }, { message: '' }, { message: 'x'.repeat(100) }] } };
  assert.deepEqual(skusFromError(e), ['SKU-1']);
  assert.deepEqual(skusFromError(null), []);
});
