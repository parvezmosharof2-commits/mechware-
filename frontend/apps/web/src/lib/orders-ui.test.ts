import test from 'node:test';
import assert from 'node:assert/strict';
import { classifyOrderError, formatOrderDate, orderHref, ordersListHref, parseServerDate, statusChipClass } from './orders-ui.ts';

test('server dates without a zone are UTC; junk gives null/empty', () => {
  assert.equal(parseServerDate('2026-11-16T10:00:00')?.toISOString(), '2026-11-16T10:00:00.000Z');
  assert.equal(parseServerDate('2026-11-16T10:00:00Z')?.toISOString(), '2026-11-16T10:00:00.000Z');
  assert.equal(parseServerDate('nope'), null);
  assert.equal(parseServerDate(null), null);
  assert.equal(formatOrderDate(undefined, 'en'), '');
});

test('dates are shown in Bangladesh time in the buyer locale', () => {
  // 20:00 UTC is already the next day in Dhaka (UTC+6)
  assert.equal(formatOrderDate('2026-11-16T20:00:00Z', 'en'), '17 Nov 2026');
  assert.match(formatOrderDate('2026-11-16T20:00:00Z', 'bn'), /[০-৯]/);
});

test('order errors: 404 is not found, network/5xx/429 retryable, the rest other', () => {
  assert.equal(classifyOrderError({ status: 404 }), 'notFound');
  assert.equal(classifyOrderError({ status: 503 }), 'unavailable');
  assert.equal(classifyOrderError(new TypeError('Failed to fetch')), 'unavailable');
  assert.equal(classifyOrderError({ status: 429 }), 'unavailable');
  assert.equal(classifyOrderError({ status: 403 }), 'other');
});

test('links keep the locale prefix and encode the order number', () => {
  assert.equal(orderHref('', 'MW-1'), '/account/orders/MW-1');
  assert.equal(orderHref('/bn', 'a b'), '/bn/account/orders/a%20b');
  assert.equal(ordersListHref('', 1), '/account/orders');
  assert.equal(ordersListHref('/bn', 3), '/bn/account/orders?page=3');
});

test('every tone has chip classes', () => {
  for (const tone of ['pending', 'progress', 'done', 'problem', 'neutral'] as const) assert.ok(statusChipClass(tone).length > 10);
});
