import test from 'node:test';
import assert from 'node:assert/strict';
import { actionErrorKey, addErrorKey } from './cart-ui.ts';
import { noticeMessage, noticeTone } from '../../../../packages/api-client/src/cart-model.ts';
import { formatPriceMinor } from '../../../../packages/api-client/src/storefront-model.ts';
import { badgeText } from './cart-events.ts';

test('price notices carry formatted money, quantity notices carry numbers', () => {
  const price = noticeMessage({ kind: 'priceChanged', variantId: 'a', was: 105000, now: 99950 }, (m) => formatPriceMinor(m, 'BDT', 'en'));
  assert.equal(price.key, 'notice_priceChanged');
  assert.deepEqual(price.values, { was: '৳1,050.00', now: '৳999.50' });
  const bn = noticeMessage({ kind: 'priceChanged', variantId: 'a', was: 105000, now: 99950 }, (m) => formatPriceMinor(m, 'BDT', 'bn'));
  assert.match(String(bn.values.now), /[০-৯]/);
  assert.doesNotMatch(String(bn.values.now), /[0-9]/);
  assert.deepEqual(noticeMessage({ kind: 'stockReduced', variantId: 'a', was: 5, now: 3 }, (m) => formatPriceMinor(m, 'BDT', 'en')), { key: 'notice_stockReduced', values: { was: 5, now: 3 } });
  assert.equal(noticeMessage({ kind: 'fromTheFuture', variantId: 'a', was: 0, now: 0 }, (m) => formatPriceMinor(m, 'BDT', 'en')).key, 'notice_unknown');
});

test('only out-of-stock and unavailable notices are blocking', () => {
  assert.equal(noticeTone('outOfStock'), 'blocking');
  assert.equal(noticeTone('unavailable'), 'blocking');
  assert.equal(noticeTone('priceChanged'), 'warning');
});

test('error kinds map to message keys', () => {
  assert.equal(actionErrorKey('unavailable'), 'actionUnavailable');
  assert.equal(actionErrorKey('busy'), 'actionBusy');
  assert.equal(actionErrorKey('other'), 'actionOther');
  assert.equal(addErrorKey('notFound'), 'addErrorNotFound');
  assert.equal(addErrorKey('busy'), 'addErrorOther');
});

test('badge text uses Bangla digits and caps at 99+', () => {
  assert.equal(badgeText(7, 'en'), '7');
  assert.equal(badgeText(120, 'en'), '99+');
  assert.equal(badgeText(7, 'bn'), '৭');
  assert.equal(badgeText(120, 'bn'), '৯৯+');
});
