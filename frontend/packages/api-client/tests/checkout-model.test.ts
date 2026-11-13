import test from 'node:test';
import assert from 'node:assert/strict';
import type { MechWareApiClient } from '../src/index.ts';
import { createOrdersApi } from '../src/orders.ts';
import {
  attemptFingerprint,
  attemptFor,
  buildOrderBody,
  buildQuoteBody,
  cartChangedSince,
  classifyCheckoutError,
  guardCheckoutStep,
  isAwaitingApproval,
  isBdMobile,
  isDestinationComplete,
  isValidIdempotencyKey,
  keepKeyAfter,
  newIdempotencyKey,
  normalisePurchaseOrderNumber,
  parseCheckoutStep,
  purchaseOrderNumberProblem,
  shouldRefreshCartAfter
} from '../src/checkout-model.ts';

const lines = [
  { variantId: 'b', quantity: 2 },
  { variantId: 'a', quantity: 1 }
];
const err = (status: number, code?: string) => ({ status, error: code ? { code, message: 'x', details: [] } : undefined });

test('PO number rule mirrors the server (1-40, letters digits space - _ . # /)', () => {
  assert.equal(purchaseOrderNumberProblem(''), null);
  assert.equal(purchaseOrderNumberProblem('   '), null);
  assert.equal(purchaseOrderNumberProblem('PO-2026/11 #7_a.b'), null);
  assert.equal(purchaseOrderNumberProblem('A'.repeat(40)), null);
  assert.equal(purchaseOrderNumberProblem('A'.repeat(41)), 'tooLong');
  assert.equal(purchaseOrderNumberProblem(' ' + 'A'.repeat(40) + ' '), null, 'trimmed before the length check');
  for (const bad of ['PO=1', 'PO<1>', 'পিও-১', 'a\tb', "a'b", 'a;b']) assert.equal(purchaseOrderNumberProblem(bad), 'badCharacters', bad);
  assert.equal(normalisePurchaseOrderNumber('  PO 1 '), 'PO 1');
  assert.equal(normalisePurchaseOrderNumber('  '), undefined);
});

test('BD mobile numbers: local, +880, Bangla digits; rejects others', () => {
  for (const ok of ['01711000004', '+8801711000004', '8801711-000004', '017 1100 0004', '০১৭১১০০০০০৪']) assert.equal(isBdMobile(ok), true, ok);
  for (const bad of ['', '0171100000', '01211000004', '+8801211000004', '+01711000004', '123456789', '01711000004x', '+44 7911 123456']) assert.equal(isBdMobile(bad), false, bad);
});

test('step guards: empty cart leaves, review needs a complete destination and no blocked line', () => {
  const personal = { addressId: 'ad1' };
  const ok = { lineCount: 2, blocked: false };
  assert.deepEqual(guardCheckoutStep('review', null, personal), { wait: true });
  assert.deepEqual(guardCheckoutStep('delivery', { lineCount: 0, blocked: false }, personal), { redirect: 'cart' });
  assert.deepEqual(guardCheckoutStep('review', ok, personal), { step: 'review' });
  assert.deepEqual(guardCheckoutStep('review', ok, {}), { step: 'delivery' });
  assert.deepEqual(guardCheckoutStep('review', { lineCount: 1, blocked: true }, personal), { step: 'delivery' });
  assert.deepEqual(guardCheckoutStep('delivery', ok, personal), { step: 'delivery' });
  assert.equal(parseCheckoutStep('review'), 'review');
  assert.equal(parseCheckoutStep('garbage'), 'delivery');
  assert.equal(parseCheckoutStep(null), 'delivery');
});

test('destination completeness: personal needs an address, company a site (+ cost centre when required, valid PO)', () => {
  assert.equal(isDestinationComplete({}), false);
  assert.equal(isDestinationComplete({ addressId: 'x' }), true);
  assert.equal(isDestinationComplete({ companyId: 'c' }), false);
  assert.equal(isDestinationComplete({ companyId: 'c', siteId: 's' }), true);
  assert.equal(isDestinationComplete({ companyId: 'c', siteId: 's' }, { costCentreRequired: true }), false);
  assert.equal(isDestinationComplete({ companyId: 'c', siteId: 's', costCentreId: 'k' }, { costCentreRequired: true }), true);
  assert.equal(isDestinationComplete({ companyId: 'c', siteId: 's', poNumber: 'a=b' }), false);
});

test('request bodies carry ids and quantities only; personal orders drop company fields', () => {
  assert.deepEqual(buildQuoteBody(lines, { addressId: 'ad1', poNumber: 'ignored', companyId: null }), {
    lines: [{ variantId: 'b', quantity: 2 }, { variantId: 'a', quantity: 1 }],
    shippingAddressId: 'ad1'
  });
  const company = buildOrderBody(lines, { companyId: 'c', siteId: 's', costCentreId: 'k', poNumber: ' PO-1 ', addressId: 'ad1' });
  assert.deepEqual(company, { lines: lines.map((l) => ({ ...l })), companyId: 'c', shipToSiteId: 's', costCentreId: 'k', purchaseOrderNumber: 'PO-1' });
  assert.equal('shippingAddressId' in company, false);
  assert.deepEqual(Object.keys(buildOrderBody(lines, { addressId: 'ad1', poNumber: 'PO' })).sort(), ['lines', 'shippingAddressId']);
  assert.equal(JSON.stringify(buildOrderBody(lines, { addressId: 'a' })).includes('Minor'), false);
});

test('idempotency key lifecycle: kept for the same request, new for a changed one, valid shape', () => {
  let n = 0;
  const make = () => `key-${String(++n).padStart(8, '0')}`;
  const fp1 = attemptFingerprint(lines, { addressId: 'ad1' });
  const first = attemptFor(null, fp1, make);
  assert.equal(attemptFor(first, fp1, make), first, 'double-click / retry / refresh reuses the key');
  assert.equal(attemptFingerprint([...lines].reverse(), { addressId: 'ad1' }), fp1, 'line order does not matter');
  const other = attemptFor(first, attemptFingerprint(lines, { addressId: 'ad2' }), make);
  assert.notEqual(other.key, first.key, 'a changed destination needs a new key');
  assert.notEqual(attemptFor(first, attemptFingerprint([{ variantId: 'a', quantity: 3 }], { addressId: 'ad1' }), make).key, first.key);
  assert.notEqual(attemptFor({ key: 'short', fingerprint: fp1 }, fp1, make).key, 'short', 'an invalid stored key is replaced');
  assert.equal(isValidIdempotencyKey(newIdempotencyKey()), true);
  assert.equal(isValidIdempotencyKey(newIdempotencyKey(() => 'a1b2c3d4-e5f6')), true);
  assert.equal(isValidIdempotencyKey('has space 12345'), false);
  assert.equal(isValidIdempotencyKey('x'.repeat(101)), false);
});

test('error mapping: codes and statuses to buyer-facing kinds', () => {
  assert.equal(classifyCheckoutError(err(422, 'SHIPPING_NOT_AVAILABLE')), 'notDeliverable');
  assert.equal(classifyCheckoutError(err(422, 'SHIPPING_QUOTE_ONLY')), 'quoteOnly');
  assert.equal(classifyCheckoutError(err(422, 'VARIANT_WEIGHT_MISSING')), 'weightMissing');
  assert.equal(classifyCheckoutError(err(409, 'INSUFFICIENT_STOCK')), 'insufficientStock');
  assert.equal(classifyCheckoutError(err(409, 'VARIANT_UNAVAILABLE')), 'unavailableItems');
  assert.equal(classifyCheckoutError(err(409, 'IDEMPOTENCY_KEY_REUSED')), 'keyReused');
  assert.equal(classifyCheckoutError(err(409, 'CHECKOUT_BUSY')), 'busy');
  assert.equal(classifyCheckoutError(err(409, 'SPEND_LIMIT_EXCEEDED')), 'spendLimit');
  assert.equal(classifyCheckoutError(err(409, 'BUDGET_EXCEEDED')), 'budget');
  assert.equal(classifyCheckoutError(err(409, 'COUPON_EXPIRED')), 'coupon');
  assert.equal(classifyCheckoutError(err(403, 'ACCOUNT_NOT_VERIFIED')), 'accountNotVerified');
  assert.equal(classifyCheckoutError(err(403, 'COMPANY_ROLE_REQUIRED')), 'companyRole');
  assert.equal(classifyCheckoutError(err(409, 'NO_ELIGIBLE_APPROVER')), 'noApprover');
  assert.equal(classifyCheckoutError(err(409, 'COMPANY_SUSPENDED')), 'companyBlocked');
  assert.equal(classifyCheckoutError(err(401)), 'signIn');
  assert.equal(classifyCheckoutError(err(503)), 'unavailable');
  assert.equal(classifyCheckoutError(new TypeError('Failed to fetch')), 'unavailable');
  assert.equal(classifyCheckoutError(err(400)), 'invalid');
  assert.equal(classifyCheckoutError(err(404)), 'notFound');
  assert.equal(classifyCheckoutError(err(429)), 'rateLimited');
  assert.equal(classifyCheckoutError(err(418)), 'other');
});

test('the key is kept only when the outcome is unknown; stale-cart errors refresh the cart', () => {
  for (const k of ['unavailable', 'busy', 'rateLimited'] as const) assert.equal(keepKeyAfter(k), true, k);
  for (const k of ['insufficientStock', 'keyReused', 'notDeliverable', 'invalid', 'spendLimit', 'signIn'] as const) assert.equal(keepKeyAfter(k), false, k);
  for (const k of ['insufficientStock', 'unavailableItems', 'keyReused'] as const) assert.equal(shouldRefreshCartAfter(k), true, k);
  assert.equal(shouldRefreshCartAfter('notDeliverable'), false);
  assert.equal(isAwaitingApproval('pending_approval'), true);
  assert.equal(isAwaitingApproval('pending_payment'), false);
});

test('cartChangedSince notices a new etag, price, quantity or line', () => {
  const base = { etag: 'e1', subtotalMinor: 3000, lines: [{ variantId: 'a', quantity: 2, unitPriceMinor: 1500 }] };
  assert.equal(cartChangedSince(base, { ...base, lines: [...base.lines] }), false);
  assert.equal(cartChangedSince(base, { ...base, etag: 'e2' }), true);
  assert.equal(cartChangedSince(base, { ...base, lines: [{ variantId: 'a', quantity: 2, unitPriceMinor: 1600 }], subtotalMinor: 3200 }), true);
  assert.equal(cartChangedSince(base, { ...base, lines: [{ variantId: 'a', quantity: 3, unitPriceMinor: 1500 }] }), true);
  assert.equal(cartChangedSince(base, { ...base, lines: [] }), true);
});

test('orders api sends the Idempotency-Key header and unwraps data', async () => {
  const calls: { path: string; init: RequestInit }[] = [];
  const client = {
    fetch: async (path: string, init: RequestInit) => {
      calls.push({ path, init });
      return { data: { orderId: 'o1', orderNumber: 'MW-1', status: 'pending_payment', grandTotalMinor: 1, reservationExpiresAtUtc: '', replayed: false } };
    }
  } as unknown as MechWareApiClient;
  const api = createOrdersApi(client);
  const placed = await api.place(buildOrderBody(lines, { addressId: 'ad1' }), 'abcdefgh-1234');
  assert.equal(placed.orderNumber, 'MW-1');
  assert.equal(calls[0].path, '/api/v1/orders');
  assert.equal((calls[0].init.headers as Record<string, string>)['Idempotency-Key'], 'abcdefgh-1234');
  await api.quoteShipping(buildQuoteBody(lines, { addressId: 'ad1' }));
  assert.equal(calls[1].path, '/api/v1/orders/shipping-quote');
});
