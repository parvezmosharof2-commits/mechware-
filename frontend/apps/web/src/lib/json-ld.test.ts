import test from 'node:test';
import assert from 'node:assert/strict';
import { buildBreadcrumbJsonLd, buildProductJsonLd, jsonLdString, minorToDecimal, truncateText } from './json-ld.ts';

const money = (minor: number) => ({ amount: String(minor / 100), amountMinor: minor, currency: 'BDT', formatted: '' });
const variant = (sku: string, minor: number, status = 'in_stock') =>
  ({
    id: sku,
    sku,
    options: {},
    isDefault: sku === 'A',
    pricing: { price: money(minor) },
    stock: { available: 1, status },
    manufacturerPartNumber: 'MPN-1'
  }) as never;
const base = (over: Record<string, unknown> = {}) =>
  ({
    brand: { name: 'Acme' },
    media: [{ url: '/media/a.jpg', type: 'image' }],
    variants: [variant('A', 123450)],
    ratingAvg: 0,
    ratingCount: 0,
    supplyStatus: 'active',
    condition: 'new',
    ...over
  }) as never;
const input = (product: never) => ({ product, title: 'T', description: 'd', url: 'https://x.test/products/p', mediaBase: 'https://api.test' });
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type Json = Record<string, any>;

test('</script> in a product name cannot close the script tag', () => {
  const out = jsonLdString({ name: 'Bolt </script><script>alert(1)</script> & "co"' });
  assert.ok(!out.includes('</script>'));
  assert.ok(!out.includes('<'));
  assert.equal((JSON.parse(out) as { name: string }).name, 'Bolt </script><script>alert(1)</script> & "co"');
});

test('prices are taka decimals and text is truncated', () => {
  assert.equal(minorToDecimal(123450), '1234.50');
  assert.equal(minorToDecimal(5), '0.05');
  assert.equal(truncateText('a'.repeat(600), 500).length, 500);
});

test('single variant gives an Offer with absolute image and no rating', () => {
  const ld = buildProductJsonLd(input(base())) as Json;
  assert.equal(ld.offers['@type'], 'Offer');
  assert.equal(ld.offers.price, '1234.50');
  assert.equal(ld.offers.availability, 'https://schema.org/InStock');
  assert.deepEqual(ld.image, ['https://api.test/media/a.jpg']);
  assert.equal(ld.mpn, 'MPN-1');
  assert.equal(ld.aggregateRating, undefined);
});

test('several variants give an AggregateOffer; obsolete is Discontinued; rating only with votes', () => {
  const p = base({ variants: [variant('A', 1000), variant('B', 3000, 'out_of_stock')], ratingCount: 3, ratingAvg: 4.5 });
  const ld = buildProductJsonLd(input(p)) as Json;
  assert.equal(ld.offers['@type'], 'AggregateOffer');
  assert.equal(ld.offers.lowPrice, '10.00');
  assert.equal(ld.offers.highPrice, '30.00');
  assert.equal(ld.aggregateRating.reviewCount, 3);
  const old = buildProductJsonLd(input(base({ supplyStatus: 'obsolete' }))) as Json;
  assert.equal(old.offers.availability, 'https://schema.org/Discontinued');
});

test('price 0 means no offer; breadcrumb positions start at 1', () => {
  const ld = buildProductJsonLd(input(base({ variants: [variant('A', 0)] }))) as Json;
  assert.equal(ld.offers, undefined);
  const bc = buildBreadcrumbJsonLd([
    { name: 'Home', url: 'u' },
    { name: 'X', url: 'v' }
  ]) as Json;
  assert.equal(bc.itemListElement[1].position, 2);
});
