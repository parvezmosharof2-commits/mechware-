import test from 'node:test';
import assert from 'node:assert/strict';
import { attrParam, formatPriceMinor, fromSearchParams, toSearchQueryString } from '../src/storefront-model.ts';

test('defaults and empties are omitted', () => {
  assert.equal(toSearchQueryString({}), '');
  assert.equal(toSearchQueryString({ q: '  ', page: 1, pageSize: 24, sort: 'relevance', inStock: false, brand: ['', ' '], attr: [] }), '');
});

test('stable order, repeated brand/attr and encoding', () => {
  const qs = toSearchQueryString({
    page: 3, sort: 'price-asc', brand: ['siemens', 'abb'], q: '6ES7 214/1', inStock: true,
    minPriceMinor: 1000, attr: ['power:..750', 'volt:24..']
  });
  assert.equal(qs, 'q=6ES7+214%2F1&brand=siemens&brand=abb&inStock=true&minPriceMinor=1000&attr=power%3A..750&attr=volt%3A24..&sort=price-asc&page=3');
});

test('round trip', () => {
  const params = {
    q: 'motor 750 W', category: 'drives', brand: ['abb', 'siemens'], inStock: true, minPriceMinor: 500, maxPriceMinor: 99900,
    attr: [attrParam('power', 100, 750)], sort: 'most-sold' as const, pageSize: 48, page: 2
  };
  assert.deepEqual(fromSearchParams(new URLSearchParams(toSearchQueryString(params))), params);
});

test('bad values are dropped and numbers clamped', () => {
  const p = fromSearchParams(new URLSearchParams('page=abc&pageSize=500&minPriceMinor=-5&maxPriceMinor=1.5&sort=bogus&inStock=maybe'));
  assert.deepEqual(p, { pageSize: 50 });
  assert.equal(fromSearchParams(new URLSearchParams('page=9999')).page, 200);
  assert.equal(fromSearchParams(new URLSearchParams('page=0')).page, undefined);
  assert.equal(fromSearchParams(new URLSearchParams('pageSize=0')).pageSize, 1);
  assert.equal(toSearchQueryString({ page: 500, pageSize: 0 }), 'pageSize=1&page=200');
});

test('price formatting en and bn', () => {
  assert.equal(formatPriceMinor(123450, 'BDT', 'en'), '৳1,234.50');
  assert.equal(formatPriceMinor(5, 'BDT', 'en'), '৳0.05');
  assert.equal(formatPriceMinor(123450, 'BDT', 'bn'), '৳১,২৩৪.৫০');
  assert.equal(formatPriceMinor(100, 'USD', 'en'), '$1.00');
});

test('attrParam', () => {
  assert.equal(attrParam('power', 100, 750), 'power:100..750');
  assert.equal(attrParam('power', undefined, 750), 'power:..750');
  assert.equal(attrParam('power', 100), 'power:100..');
});
