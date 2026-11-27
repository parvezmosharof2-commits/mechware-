import test from 'node:test';
import assert from 'node:assert/strict';
import { badgeText, formatNotificationTime, localeLink, notificationListQuery, notificationMessageKey, notificationParams, safeNotificationLink } from '../src/notifications-model.ts';

test('message key: known keys map, unknown and empty get generic', () => {
  assert.equal(notificationMessageKey('notifications.order_shipped'), 'order_shipped');
  assert.equal(notificationMessageKey('notifications.quote_withdrawn'), 'quote_withdrawn');
  assert.equal(notificationMessageKey('notifications.brand_new'), 'generic');
  assert.equal(notificationMessageKey('order_shipped.__proto__'), 'generic');
  assert.equal(notificationMessageKey(null), 'generic');
});

test('params: only strings and numbers survive', () => {
  assert.deepEqual(notificationParams({ a: 'x', b: 3, c: null, d: { e: 1 }, f: true }), { a: 'x', b: '3' });
  assert.deepEqual(notificationParams(null), {});
});

test('safe link: one relative path only', () => {
  assert.equal(safeNotificationLink('/en/account/orders/MW-1'), '/en/account/orders/MW-1');
  for (const bad of ['//evil.com', 'https://evil.com', '/\\evil', 'javascript:alert(1)', '', null, undefined, '/a\nb', 'account/x'])
    assert.equal(safeNotificationLink(bad as string | null), null);
});

test('locale link drops the default /en prefix, keeps /bn', () => {
  assert.equal(localeLink('/en/account/orders/MW-1'), '/account/orders/MW-1');
  assert.equal(localeLink('/bn/account/orders/MW-1'), '/bn/account/orders/MW-1');
  assert.equal(localeLink('/en'), '/');
  assert.equal(localeLink('/english'), '/english');
  assert.equal(localeLink('//x'), null);
});

test('time: Dhaka zone, a value without designator is UTC', () => {
  assert.match(formatNotificationTime('2026-11-27T20:30:00Z', 'en'), /28 Nov 2026/);
  assert.equal(formatNotificationTime('2026-11-27T20:30:00Z', 'en'), formatNotificationTime('2026-11-27T20:30:00', 'en'));
  assert.equal(formatNotificationTime('junk', 'en'), '');
});

test('badge and query', () => {
  assert.equal(badgeText(0), '');
  assert.equal(badgeText(7), '7');
  assert.equal(badgeText(100), '99+');
  assert.equal(notificationListQuery(true, 2), 'unread=true&page=2');
  assert.equal(notificationListQuery(false, 0), 'page=1');
});
