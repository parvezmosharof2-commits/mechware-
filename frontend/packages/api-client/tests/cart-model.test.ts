import test from 'node:test';
import assert from 'node:assert/strict';
import type { MechWareApiClient } from '../src/index.ts';
import { createCartApi } from '../src/cart.ts';
import {
  blockedVariantIds,
  canProceedToCheckout,
  cartNoticeMessageKey,
  classifyCartError,
  clampCartQuantity,
  groupNotices,
  lineTotalIsConsistent,
  minCartQuantity,
  shouldRefetchAfter,
  stepCartQuantity,
  subtotalIsConsistent
} from '../src/cart-model.ts';

const line = (id: string, over = {}) => ({ variantId: id, sku: id, title: id, quantity: 2, unitPriceMinor: 1050, lineTotalMinor: 2100, minOrderQty: 1, packSize: 1, ...over });

test('notice kinds map to message keys, unknown kinds to a generic one', () => {
  assert.equal(cartNoticeMessageKey('priceChanged'), 'notice_priceChanged');
  assert.equal(cartNoticeMessageKey('cartFull'), 'notice_cartFull');
  assert.equal(cartNoticeMessageKey('somethingNew'), 'notice_unknown');
});

test('checkout is blocked by outOfStock/unavailable only, and never for an empty cart', () => {
  const lines = [line('a'), line('b')];
  assert.equal(canProceedToCheckout({ lines, notices: [] }), true);
  assert.equal(canProceedToCheckout({ lines, notices: [{ kind: 'priceChanged', variantId: 'a', was: 1, now: 2 }] }), true);
  assert.equal(canProceedToCheckout({ lines, notices: [{ kind: 'outOfStock', variantId: 'b', was: 3, now: 0 }] }), false);
  assert.equal(canProceedToCheckout({ lines, notices: [{ kind: 'unavailable', variantId: 'a', was: 3, now: 0 }] }), false);
  assert.equal(canProceedToCheckout({ lines: [], notices: [] }), false);
  assert.deepEqual([...blockedVariantIds({ notices: [{ kind: 'unavailable', variantId: 'a', was: 0, now: 0 }] })], ['a']);
});

test('notices group by line; notices without a line are orphans', () => {
  const { byVariant, orphans } = groupNotices({
    lines: [line('a')],
    notices: [
      { kind: 'stockReduced', variantId: 'a', was: 5, now: 3 },
      { kind: 'cartFull', variantId: 'zzz', was: 4, now: 0 }
    ]
  });
  assert.equal(byVariant.get('a')?.length, 1);
  assert.equal(orphans.length, 1);
});

test('totals consistency checks', () => {
  assert.equal(lineTotalIsConsistent(line('a')), true);
  assert.equal(lineTotalIsConsistent(line('a', { lineTotalMinor: 1 })), false);
  assert.equal(subtotalIsConsistent({ lines: [line('a'), line('b')], subtotalMinor: 4200 }), true);
  assert.equal(subtotalIsConsistent({ lines: [line('a')], subtotalMinor: 5 }), false);
});

test('quantity hints respect MOQ, pack size and the cap', () => {
  const l = { minOrderQty: 10, packSize: 5 };
  assert.equal(minCartQuantity(l), 10);
  assert.equal(minCartQuantity({ minOrderQty: 7, packSize: 5 }), 10);
  assert.equal(clampCartQuantity(3, l), 10);
  assert.equal(clampCartQuantity(11, l), 15);
  assert.equal(clampCartQuantity(Number.NaN, l), 10);
  assert.equal(clampCartQuantity(99_999, l), 10_000);
  assert.equal(clampCartQuantity(99_999, { minOrderQty: 1, packSize: 3 }), 9999);
  assert.equal(stepCartQuantity(10, 1, l), 15);
  assert.equal(stepCartQuantity(15, -1, l), 10);
  assert.equal(stepCartQuantity(10, -1, l), null);
});

test('errors classify into buyer-facing kinds; unanswered calls are "unavailable", never empty', () => {
  assert.equal(classifyCartError(new TypeError('Failed to fetch')), 'unavailable');
  assert.equal(classifyCartError({ status: 503, error: { code: 'CART_UNAVAILABLE' } }), 'unavailable');
  assert.equal(classifyCartError({ status: 409, error: { code: 'CART_BUSY' } }), 'busy');
  assert.equal(classifyCartError({ status: 409, error: { code: 'CART_CHANGED' } }), 'busy');
  assert.equal(classifyCartError({ status: 409, error: { code: 'INSUFFICIENT_STOCK' } }), 'insufficientStock');
  assert.equal(classifyCartError({ status: 409, error: { code: 'CART_FULL' } }), 'cartFull');
  assert.equal(classifyCartError({ status: 404 }), 'notFound');
  assert.equal(classifyCartError({ status: 429 }), 'rateLimited');
  assert.equal(classifyCartError({ status: 400 }), 'invalid');
  assert.equal(shouldRefetchAfter('busy'), true);
  assert.equal(shouldRefetchAfter('unavailable'), false);
});

test('createCartApi sends a variant id and quantity only (no prices) and unwraps data', async () => {
  const calls: Array<{ path: string; init: RequestInit }> = [];
  const view = { lines: [], notices: [], lineCount: 0, totalQuantity: 0, subtotalMinor: 0, currency: 'BDT', etag: 'e' };
  // The shared client attaches credentials and the Bearer token itself; here only what the cart module asks it to send.
  const client = {
    fetch: async (path: string, init: RequestInit = {}) => {
      calls.push({ path, init });
      return { data: view };
    }
  } as unknown as MechWareApiClient;
  const api = createCartApi(client);
  assert.deepEqual(await api.addItem('v-1', 3), view);
  await api.setItem('v-1', 0);
  await api.removeItem('v-1');
  await api.clear();
  await api.get();
  assert.deepEqual(calls.map((c) => [c.init.method ?? 'GET', c.path]), [
    ['POST', '/api/v1/cart/items'],
    ['PUT', '/api/v1/cart/items/v-1'],
    ['DELETE', '/api/v1/cart/items/v-1'],
    ['DELETE', '/api/v1/cart'],
    ['GET', '/api/v1/cart']
  ]);
  assert.deepEqual(JSON.parse(String(calls[0]!.init.body)), { variantId: 'v-1', quantity: 3 });
  assert.deepEqual(JSON.parse(String(calls[1]!.init.body)), { quantity: 0 });
});
