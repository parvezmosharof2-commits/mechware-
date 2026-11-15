import test from 'node:test';
import assert from 'node:assert/strict';
import { companyLookupQuery, exactSkuMatch, variantLookupQuery, type AdminVariant } from '../src/lookups-model.ts';

const v = (sku: string): AdminVariant => ({ variantId: sku, productId: 'p', sku, partNumber: null, title: sku, brand: null, productStatus: 'Draft', active: true, priceMinor: 100 });

test('variant query: sku and q trimmed, empties dropped, page size capped at 50', () => {
  assert.equal(variantLookupQuery({ sku: ' MW-1 ', q: '', page: 2, pageSize: 500 }), 'sku=MW-1&page=2&pageSize=50');
  assert.equal(variantLookupQuery({ q: 'valve 1/4' }), 'q=valve+1%2F4&page=1&pageSize=25');
  assert.equal(variantLookupQuery(), 'page=1&pageSize=25');
  assert.equal(variantLookupQuery({ sku: 'a', page: 0, pageSize: 0 }), 'sku=a&page=1&pageSize=1');
});

test('company query: q, status, 50 per page by default', () => {
  assert.equal(companyLookupQuery({ q: ' mills ', status: 'active' }), 'q=mills&status=active&page=1&pageSize=50');
  assert.equal(companyLookupQuery(), 'page=1&pageSize=50');
});

test('exact SKU match ignores case and refuses near misses', () => {
  const items = [v('MW-100-X'), v('MW-100')];
  assert.equal(exactSkuMatch(items, 'mw-100')?.sku, 'MW-100');
  assert.equal(exactSkuMatch(items, 'MW-10'), null);
  assert.equal(exactSkuMatch([], 'MW-100'), null);
});
