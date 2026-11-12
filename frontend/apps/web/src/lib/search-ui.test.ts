import test from 'node:test';
import assert from 'node:assert/strict';
import { clearFilters, hasActiveFilters, maxPage, minorToTaka, pageWindow, takaToMinor, toggleBrand, withFilter } from './search-ui.ts';

test('taka to poisha', () => {
  assert.equal(takaToMinor('1,250.5'), 125050);
  assert.equal(takaToMinor('0'), 0);
  assert.equal(takaToMinor(''), undefined);
  assert.equal(takaToMinor('-5'), undefined);
  assert.equal(takaToMinor('1.234'), undefined);
  assert.equal(minorToTaka(125050), '1250.5');
  assert.equal(minorToTaka(undefined), '');
});

test('max page is capped at 200 and at least 1', () => {
  assert.equal(maxPage(0, 24), 1);
  assert.equal(maxPage(25, 24), 2);
  assert.equal(maxPage(100000, 24), 200);
});

test('page window has gaps', () => {
  assert.deepEqual(pageWindow(1, 1), [1]);
  assert.deepEqual(pageWindow(1, 3), [1, 2, 3]);
  assert.deepEqual(pageWindow(6, 20), [1, null, 5, 6, 7, null, 20]);
  assert.deepEqual(pageWindow(2, 20), [1, 2, 3, null, 20]);
});

test('filter changes reset the page', () => {
  assert.equal(withFilter({ q: 'a', page: 4 }, { inStock: true }).page, undefined);
  const p = toggleBrand({ page: 3, brand: ['abb'] }, 'siemens');
  assert.deepEqual(p.brand, ['abb', 'siemens']);
  assert.equal(p.page, undefined);
  assert.equal(toggleBrand({ brand: ['abb'] }, 'abb').brand, undefined);
});

test('clear filters keeps q and sort', () => {
  const cleared = clearFilters({ q: 'x', sort: 'price-asc', brand: ['a'], inStock: true, minPriceMinor: 5, page: 2 });
  assert.deepEqual(cleared, { q: 'x', sort: 'price-asc', pageSize: undefined });
  assert.equal(hasActiveFilters(cleared), false);
  assert.equal(hasActiveFilters({ inStock: true }), true);
});
