import test from 'node:test';
import assert from 'node:assert/strict';
import {
  addAlternative, groupVariantsByProduct, moveItem, normaliseStatus, priceRange, supplyBody, validateOrderUnits, validatePartNumber,
  catalogAdminErrorText, lifecycleButtons, priceTextProblem, priceTextToMinor, productListQuery, validateBasics, validateVariantEdit, weightProblem, basicsToForm,
  type AdminProductDetail, type AdminProductDetailVariant, type ProductPim
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

const variant = (over: Partial<AdminProductDetailVariant> = {}): AdminProductDetailVariant =>
  ({ id: 'v1', sku: 'S1', isDefault: true, status: 'active', weightKg: 0, priceMinor: 125000, compareAtPriceMinor: null, salePriceMinor: null, minOrderQty: 1, packSize: 1, manufacturerPartNumber: null, ...over });
const detail = (over: Partial<AdminProductDetail> = {}): AdminProductDetail => ({
  id: 'p1', slug: 's', titleEn: 'Title', titleBn: 'শিরোনাম', shortDescriptionEn: '', shortDescriptionBn: '', descriptionHtml: '<p>x</p>', brandId: null, brandName: null,
  categoryId: 'c1', categoryName: 'Cat', categoryPath: 'a.cat', sellerId: 's1', sellerStoreName: 'Store', status: 'draft', allowedTransitions: ['publish', 'archive'],
  rejectionReason: null, supplyStatus: 'active', lengthCm: 10, widthCm: 5.5, heightCm: 2, updatedAtUtc: null, rowVersion: 42, variants: [variant()], ...over
});

test('list query leaves empties out, keeps 50 per page', () => {
  assert.equal(productListQuery({ q: ' abb ', status: 'draft', brandId: '', page: 3 }), 'q=abb&status=draft&page=3&pageSize=50');
  assert.equal(productListQuery(), 'page=1&pageSize=50');
});

test('lifecycle buttons follow allowedTransitions only', () => {
  assert.deepEqual(lifecycleButtons(['publish', 'archive']).map((b) => b.label), ['Publish', 'Archive']);
  assert.deepEqual(lifecycleButtons(['restore', 'explode']).map((b) => b.action), ['restore']);
  assert.deepEqual(lifecycleButtons([]), []);
});

test('basics: limits mirror the server, version travels along', () => {
  const ok = validateBasics(basicsToForm(detail()), 42);
  assert.equal(ok.body?.expectedVersion, 42);
  assert.equal(ok.body?.brandId, null);
  assert.equal(ok.body?.widthCm, 5.5);
  const f = basicsToForm(detail());
  const bad = validateBasics({ ...f, titleEn: ' ', titleBn: 'x'.repeat(256), shortDescriptionEn: 'y'.repeat(1001), descriptionHtml: 'z'.repeat(20001), lengthCm: '0', widthCm: '1000.5', heightCm: '1.234' }, 1);
  assert.equal(bad.body, null);
  assert.deepEqual(Object.keys(bad.errors).sort(), ['descriptionHtml', 'heightCm', 'lengthCm', 'shortDescriptionEn', 'titleBn', 'titleEn', 'widthCm']);
  assert.equal(validateBasics({ ...f, titleEn: 'x'.repeat(255), shortDescriptionEn: 'y'.repeat(1000), descriptionHtml: 'z'.repeat(20000) }, 1).body !== null, true);
  assert.equal(validateBasics({ ...f, category: null }, 1).errors.categoryId, 'A category is required.');
});

test('weight is checked as text: 3 decimals ok, 4 refused, 0 and 1000.001 refused', () => {
  for (const t of ['0.001', '1', '12.5', '0.250', '1000']) assert.equal(weightProblem(t), null, t);
  assert.match(weightProblem('0.1234') ?? '', /3 decimals/);
  assert.match(weightProblem('0') ?? '', /more than 0/);
  assert.match(weightProblem('0.000') ?? '', /more than 0/);
  assert.match(weightProblem('1000.001') ?? '', /at most 1000/);
  assert.match(weightProblem('abc') ?? '', /number in kg/);
  assert.match(weightProblem('-1') ?? '', /number in kg/);
  assert.match(weightProblem('') ?? '', /number in kg/);
});

test('price text to poisha without floats; 1 to max', () => {
  assert.equal(priceTextToMinor('1,250.5'), 125050);
  assert.equal(priceTextToMinor('0.29'), 29);
  assert.equal(priceTextToMinor('19.99'), 1999);
  assert.equal(priceTextToMinor('1.234'), null);
  assert.equal(priceTextToMinor('1e5'), null);
  assert.equal(priceTextProblem('0'), 'The price must be more than 0.');
  assert.match(priceTextProblem('1000000001') ?? '', /above the allowed maximum/);
  assert.equal(priceTextProblem('1000000000'), null);
  assert.match(priceTextProblem('abc') ?? '', /taka/);
});

test('variant edit sends only what changed and reports the price change', () => {
  const v = variant({ weightKg: 0.25 });
  const same = validateVariantEdit(v, { weight: '0.25', price: '1250.00', status: 'active' }, 7);
  assert.equal(same.body, null);
  assert.equal(same.errors.form, 'Nothing changed.');
  const w = validateVariantEdit(v, { weight: '0.125', price: '1250.00', status: 'active' }, 7);
  assert.deepEqual(w.body, { expectedVersion: 7, weightKg: 0.125 });
  assert.equal(w.priceChange, null);
  const p = validateVariantEdit(v, { weight: '0.25', price: '1300', status: 'inactive' }, 7);
  assert.deepEqual(p.body, { expectedVersion: 7, priceMinor: 130000, status: 'inactive' });
  assert.deepEqual(p.priceChange, { from: 125000, to: 130000 });
  const bad = validateVariantEdit(v, { weight: '0.1234', price: '1.234', status: 'active' }, 7);
  assert.deepEqual(Object.keys(bad.errors).sort(), ['priceMinor', 'weightKg']);
  assert.equal(validateVariantEdit(variant(), { weight: '2', price: '1250', status: 'active' }, 1).body?.weightKg, 2);
});

test('error codes are put in plain words', () => {
  const e = (status: number, code: string | null, message = '', fields = {}) => catalogAdminErrorText({ status, code, message, fields });
  assert.match(e(409, 'PRODUCT_STALE'), /Someone else changed this product/);
  assert.match(e(409, 'NO_MEDIA'), /Add at least one image first/);
  assert.match(e(409, 'NO_VARIANT'), /at least one SKU/);
  assert.match(e(409, 'NO_SELLABLE_VARIANT'), /active/);
  assert.match(e(409, 'INVALID_PRODUCT_TRANSITION'), /Reload/);
  assert.equal(e(400, 'VALIDATION_FAILED', 'x', { descriptionHtml: 'bad' }), 'Fix the highlighted fields.');
  assert.match(e(403, null), /catalog\.manage/);
});
