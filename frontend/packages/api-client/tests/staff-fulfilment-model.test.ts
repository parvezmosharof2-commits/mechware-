import test from 'node:test';
import assert from 'node:assert/strict';
import {
  FULFIL_MISSING_PERMISSION_TEXT, beforeShipping, canFulfilOrders, cleanCourierName, fulfilErrorText, nextFulfilStep, noStepReason, safeTrackingUrl,
  shipBody, shipFieldErrors, shipProblems, shouldReloadFulfil
} from '../src/staff-fulfilment-model.ts';

const ok = { courierName: 'Steadfast Courier', trackingNumber: 'SF-12345-AB', trackingUrl: '' };

test('permission: only orders.fulfil (any case) allows the buttons', () => {
  assert.equal(canFulfilOrders(['orders.view_all', 'Orders.Fulfil']), true);
  assert.equal(canFulfilOrders(['orders.view_all', 'orders.cancel', 'payments.refund_approve']), false);
  assert.equal(canFulfilOrders([]), false);
  assert.ok(FULFIL_MISSING_PERMISSION_TEXT.includes('orders.fulfil'));
});

test('next step: exactly one step per status, none elsewhere', () => {
  assert.deepEqual(['confirmed', 'processing', 'packed', 'shipped', 'out_for_delivery'].map((s) => nextFulfilStep(s)?.step), ['processing', 'packed', 'ship', 'delivered', 'delivered']);
  assert.deepEqual(['Start processing', 'Mark packed', 'Ship…', 'Mark delivered'], ['confirmed', 'processing', 'packed', 'shipped'].map((s) => nextFulfilStep(s)!.label));
  for (const s of ['pending_approval', 'pending_payment', 'delivered', 'completed', 'cancelled', 'returned', 'refunded', 'nonsense', '']) {
    assert.equal(nextFulfilStep(s), null, s);
    assert.ok(noStepReason(s).length > 10, s);
  }
  assert.ok(noStepReason('cancelled').includes('cancelled'));
});

test('ship form: a good form has no problems; the name is cleaned', () => {
  assert.deepEqual(shipProblems(ok), {});
  assert.deepEqual(shipProblems({ ...ok, trackingUrl: 'https://steadfast.com.bd/t/SF-12345-AB' }), {});
  assert.equal(cleanCourierName('  Pathao   Courier \n'), 'Pathao Courier');
  assert.deepEqual(shipBody(7, { courierName: ' Pathao  Courier ', trackingNumber: ' PA-1 ', trackingUrl: '  ' }), { rowVersion: 7, courierName: 'Pathao Courier', trackingNumber: 'PA-1', trackingUrl: null });
});

test('courier name: 2 to 80 characters after cleaning, plain text', () => {
  assert.ok(shipProblems({ ...ok, courierName: 'A' }).courierName);
  assert.ok(shipProblems({ ...ok, courierName: '   ' }).courierName);
  assert.equal(shipProblems({ ...ok, courierName: 'AB' }).courierName, undefined);
  assert.equal(shipProblems({ ...ok, courierName: 'x'.repeat(80) }).courierName, undefined);
  assert.ok(shipProblems({ ...ok, courierName: 'x'.repeat(81) }).courierName);
  assert.ok(shipProblems({ ...ok, courierName: 'A <b>B</b>' }).courierName);
});

test('tracking number: 1 to 60 letters, digits and dashes', () => {
  assert.ok(shipProblems({ ...ok, trackingNumber: '' }).trackingNumber);
  assert.ok(shipProblems({ ...ok, trackingNumber: 'AB 12' }).trackingNumber);
  assert.ok(shipProblems({ ...ok, trackingNumber: 'AB_12' }).trackingNumber);
  assert.ok(shipProblems({ ...ok, trackingNumber: 'বাংলা1' }).trackingNumber);
  assert.ok(shipProblems({ ...ok, trackingNumber: 'A'.repeat(61) }).trackingNumber);
  assert.equal(shipProblems({ ...ok, trackingNumber: 'A'.repeat(60) }).trackingNumber, undefined);
  assert.equal(shipProblems({ ...ok, trackingNumber: ' aB-9 ' }).trackingNumber, undefined);
});

test('tracking link: https on a courier site only, in the form and in safeTrackingUrl', () => {
  const good = ['https://steadfast.com.bd/t/1', 'https://www.pathao.com/track?id=1', 'https://REDX.com.bd', 'https://a.b.paperfly.com.bd/x', 'https://ecourier.com.bd/'];
  for (const u of good) { assert.equal(shipProblems({ ...ok, trackingUrl: u }).trackingUrl, undefined, u); assert.ok(safeTrackingUrl(u), u); }
  const bad = ['http://steadfast.com.bd/t', 'https://evil.example/steadfast.com.bd', 'https://steadfast.com.bd.evil.com/', 'https://notpathao.com/', 'https://user:pw@pathao.com/', 'https://pathao.com:8443/',
    'javascript:alert(1)', 'ftp://pathao.com', 'pathao.com/track', 'https://pathao.com/' + 'a'.repeat(500)];
  for (const u of bad) { assert.ok(shipProblems({ ...ok, trackingUrl: u }).trackingUrl, u); assert.equal(safeTrackingUrl(u), null, u); }
  assert.equal(safeTrackingUrl(null), null);
  assert.equal(safeTrackingUrl(''), null);
});

test('every field is checked at once', () => {
  const p = shipProblems({ courierName: '', trackingNumber: '', trackingUrl: 'http://x.example' });
  assert.deepEqual(Object.keys(p).sort(), ['courierName', 'trackingNumber', 'trackingUrl']);
});

test('errors: the two 409 codes reload and say why in words; field errors map onto the form', () => {
  assert.equal(shouldReloadFulfil('ORDER_CHANGED'), true);
  assert.equal(shouldReloadFulfil('ORDER_INVALID_TRANSITION'), true);
  assert.equal(shouldReloadFulfil('SOMETHING'), false);
  assert.equal(shouldReloadFulfil(null), false);
  const e = (code: string | null, status: number | null, fields = {}) => ({ status, code, message: '', fields });
  assert.ok(fulfilErrorText('ship', e('ORDER_CHANGED', 409)).includes('Someone else changed this order'));
  const inv = fulfilErrorText('ship', e('ORDER_INVALID_TRANSITION', 409));
  assert.ok(inv.includes('Only a packed order can be shipped') && inv.includes('cancelled'));
  assert.ok(fulfilErrorText('delivered', e('ORDER_INVALID_TRANSITION', 409)).includes('shipped order'));
  assert.equal(fulfilErrorText('packed', e(null, 400, { trackingUrl: 'bad link' })), 'bad link');
  assert.ok(fulfilErrorText('packed', e(null, 403)).includes('permission'));
  assert.ok(fulfilErrorText('packed', e(null, null)).includes('cannot be reached'));
  assert.ok(fulfilErrorText('packed', e(null, 500)).includes('server had a problem'));
  assert.deepEqual(shipFieldErrors({ CourierName: 'a', trackingnumber: 'b', TrackingUrl: 'c', other: 'd' }), { courierName: 'a', trackingNumber: 'b', trackingUrl: 'c' });
});

test('buyer: the status is words before shipping only', () => {
  for (const s of ['pending_approval', 'pending_payment', 'confirmed', 'processing', 'packed']) assert.equal(beforeShipping(s), true, s);
  for (const s of ['shipped', 'out_for_delivery', 'delivered', 'cancelled']) assert.equal(beforeShipping(s), false, s);
});
