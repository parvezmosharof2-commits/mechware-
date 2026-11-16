import test from 'node:test';
import assert from 'node:assert/strict';
import {
  addAlternative, brandsOf, filterRows, groupVariantsByProduct, moveItem, normaliseStatus, priceRange, supplyBody, validateOrderUnits, validatePartNumber,
  type ProductPim
} from '../src/catalog-admin-model.ts';
import type { AdminVariant } from '../src/lookups-model.ts';

const v = (sku: string, productId: string, over: Partial<AdminVariant> = {}): AdminVariant =>
  ({ variantId: sku, productId, sku, partNumber: null, title: `T-${productId}`, brand: 'ABB', productStatus: 'Active', active: true, priceMinor: 10000, ...over });
const pim = (over: Partial<ProductPim> = {}): ProductPim => ({ id: 'p1', slug: 's', titleEn: 'T', status: 'active', supplyStatus: 'active', successor: null, alternatives: [], variants: [], ...over });

test('status names normalise from PascalCase and snake_case', () => {
  assert.equal(normaliseStatus('PendingReview'), 'pending_review');
  assert.equal(normaliseStatus('pending_review'), 'pending_review');
  assert.equal(normaliseStatus('Active'), 'active');
});

test('variants group by product, with price range and counts', () => {
  const rows = groupVariantsByProduct([v('A1', 'p1', { priceMinor: 500, partNumber: 'X-1' }), v('A2', 'p1', { priceMinor: 900, active: false }), v('B1', 'p2', { brand: 'Siemens', productStatus: 'Draft' })]);
  assert.equal(rows.length, 2);
  assert.deepEqual([rows[0].skus, rows[0].partNumbers, rows[0].inactiveSkus, priceRange(rows[0])], [['A1', 'A2'], ['X-1'], 1, '5.00 to 9.00']);
  assert.deepEqual(filterRows(rows, { status: 'draft' }).map((r) => r.productId), ['p2']);
  assert.deepEqual(filterRows(rows, { brand: 'ABB' }).map((r) => r.productId), ['p1']);
  assert.deepEqual(brandsOf(rows), ['ABB', 'Siemens']);
});

test('order units follow the server rules', () => {
  assert.deepEqual(validateOrderUnits('10', '5').value, { minOrderQty: 10, packSize: 5 });
  assert.match(validateOrderUnits('12', '5').errors.minOrderQty, /whole number of packs/);
  assert.ok(validateOrderUnits('0', '1').errors.minOrderQty);
  assert.ok(validateOrderUnits('10001', '1').errors.minOrderQty);
  assert.ok(validateOrderUnits('1', '0').errors.packSize);
  assert.ok(validateOrderUnits('1.5', 'x').errors.packSize);
  assert.equal(validateOrderUnits('1', '1').value?.packSize, 1);
});

test('part number: blank clears, rules mirror the domain', () => {
  assert.deepEqual(validatePartNumber('  '), { value: null });
  assert.equal(validatePartNumber(' 6ES7 214-1AG40 ').value, '6ES7 214-1AG40');
  assert.ok(validatePartNumber('-.').error);
  assert.ok(validatePartNumber('A'.repeat(65)).error);
  assert.ok(validatePartNumber('AB​CD').error);
});

test('supply body: active has no successor, successor needed for notes, clear is explicit', () => {
  assert.ok(supplyBody({ supplyStatus: 'active', successorId: 'p2', isDropIn: false, note: '' }, pim()).errors.successorProductId);
  assert.ok(supplyBody({ supplyStatus: 'nrnd', successorId: null, isDropIn: true, note: '' }, pim()).errors.successorProductId);
  assert.ok(supplyBody({ supplyStatus: 'nrnd', successorId: 'p1', isDropIn: false, note: '' }, pim()).errors.successorProductId);
  assert.ok(supplyBody({ supplyStatus: 'nrnd', successorId: 'p2', isDropIn: false, note: 'x'.repeat(501) }, pim()).errors.successorNote);
  assert.deepEqual(supplyBody({ supplyStatus: 'nrnd', successorId: 'p2', isDropIn: true, note: ' use it ' }, pim()).body, { supplyStatus: 'nrnd', successorProductId: 'p2', successorIsDropIn: true, successorNote: 'use it' });
  const withSucc = pim({ supplyStatus: 'nrnd', successor: { productId: 'p2', slug: 's', titleEn: 'x', status: 'active', isDropIn: false, note: null } });
  assert.deepEqual(supplyBody({ supplyStatus: 'active', successorId: null, isDropIn: false, note: '' }, withSucc).body, { supplyStatus: 'active', clearSuccessor: true });
  assert.deepEqual(supplyBody({ supplyStatus: 'active', successorId: null, isDropIn: false, note: '' }, pim()).body, { supplyStatus: 'active' });
});

test('alternatives: no self, no duplicates, at most 10; move keeps order', () => {
  assert.ok(addAlternative([], 'p1', 'p1').error);
  assert.ok(addAlternative(['a'], 'a', 'p1').error);
  assert.ok(addAlternative(Array.from({ length: 10 }, (_, i) => `x${i}`), 'n', 'p1').error);
  assert.deepEqual(addAlternative(['a'], 'b', 'p1').ids, ['a', 'b']);
  assert.deepEqual(moveItem(['a', 'b', 'c'], 2, 0), ['c', 'a', 'b']);
  assert.deepEqual(moveItem(['a', 'b'], 0, -1), ['a', 'b']);
});
