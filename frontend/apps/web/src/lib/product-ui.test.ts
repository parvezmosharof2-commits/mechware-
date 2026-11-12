import test from 'node:test';
import assert from 'node:assert/strict';
import { absoluteMediaUrl, clampQuantity, formatFileSize, galleryFor, specBoolean, specGroups, localePath, minQuantity, pickLocalized, pickVariant, productPath } from './product-ui.ts';

const variant = (sku: string, isDefault = false, id = sku) =>
  ({ id, sku, isDefault, options: {}, minOrderQty: 1, packSize: 1 }) as never;

test('locale paths', () => {
  assert.equal(localePath('en', '/products/a'), '/products/a');
  assert.equal(productPath('bn', 'a b'), '/bn/products/a%20b');
});

test('bangla falls back to english', () => {
  assert.equal(pickLocalized('bn', 'Drive', ' '), 'Drive');
  assert.equal(pickLocalized('bn', 'Drive', 'ড্রাইভ'), 'ড্রাইভ');
  assert.equal(pickLocalized('en', 'Drive', 'ড্রাইভ'), 'Drive');
});

test('variant selection', () => {
  const vs = [variant('A'), variant('B', true), variant('C')];
  assert.equal(pickVariant(vs, 'c')?.sku, 'C');
  assert.equal(pickVariant(vs, 'nope')?.sku, 'B');
  assert.equal(pickVariant(vs, null)?.sku, 'B');
  assert.equal(pickVariant([variant('A')], undefined)?.sku, 'A');
  assert.equal(pickVariant([], 'A'), undefined);
});

test('gallery prefers variant media', () => {
  const media = [
    { type: 'image', variantId: null, url: 'shared' },
    { type: 'image', variantId: 'B', url: 'own' },
    { type: 'video', variantId: null, url: 'v' }
  ] as never[];
  assert.deepEqual(galleryFor(media, variant('B')).map((m) => m.url), ['own']);
  assert.deepEqual(galleryFor(media, variant('A')).map((m) => m.url), ['shared']);
});

test('quantity respects MOQ and pack size', () => {
  const v = { minOrderQty: 5, packSize: 4 };
  assert.equal(minQuantity(v), 8);
  assert.equal(clampQuantity(1, v), 8);
  assert.equal(clampQuantity(9, v), 12);
  assert.equal(clampQuantity(Number.NaN, v), 8);
  assert.equal(clampQuantity(3, { minOrderQty: 1, packSize: 1 }), 3);
});

test('media urls', () => {
  assert.equal(absoluteMediaUrl('/m/1.svg', 'http://api/'), 'http://api/m/1.svg');
  assert.equal(absoluteMediaUrl('https://cdn/x.png', 'http://api'), 'https://cdn/x.png');
});

test('spec helpers', () => {
  assert.equal(specBoolean("True"), true);
  assert.equal(specBoolean("no"), false);
  assert.equal(specBoolean("IP20"), undefined);
  assert.equal(formatFileSize(0), '');
  assert.equal(formatFileSize(512), '512 B');
  assert.equal(formatFileSize(2048), '2 KB');
  assert.equal(formatFileSize(1572864), '1.5 MB');
  assert.deepEqual(specGroups({ A: { x: '1', y: ' ' }, B: {} }), [['A', [['x', '1']]]]);
  assert.deepEqual(specGroups(null), []);
});
