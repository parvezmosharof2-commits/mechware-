import test from 'node:test';
import assert from 'node:assert/strict';
import { contactOptionText, expectedSellers, initialSiteId, linesWithoutSeller, lookupFailureText, quantityHint, roleWords, sellersQuery, siteOptionText, syncFees, type QuoteSite } from '../src/quote-lookups-model.ts';
import { formFromVersion, newLineFor, type EditorFee } from '../src/quote-editor-model.ts';

const A = '0b9f1c52-6d3e-4c1a-9a55-2f7e8d1c4b60';
const B = '1c9f1c52-6d3e-4c1a-9a55-2f7e8d1c4b61';
const S1 = '2d9f1c52-6d3e-4c1a-9a55-2f7e8d1c4b62';
const S2 = '3e9f1c52-6d3e-4c1a-9a55-2f7e8d1c4b63';
const site = (o: Partial<QuoteSite> = {}): QuoteSite => ({ id: A, name: 'Gazipur Plant', line1: 'Plot 5', line2: null, upazila: 'Tongi', district: 'Gazipur', division: 'Dhaka', postalCode: null, isDefaultDelivery: false, ...o });
const sellers = [{ variantId: A, sellerId: S1, sellerName: 'Alpha Store' }, { variantId: B, sellerId: S2, sellerName: 'Beta Store' }];
const line = (v: string) => newLineFor({ variantId: v, sku: `SKU-${v.slice(0, 2)}`, partNumber: null, title: 't' });
let n = 0;
const key = () => `k${++n}`;

test('sellersQuery: distinct ids, lower case, at most 50, encoded', () => {
  assert.equal(sellersQuery([A, A.toUpperCase(), B]), `variantIds=${A}&variantIds=${B}`);
  assert.equal(sellersQuery([]), '');
  assert.equal(sellersQuery(Array.from({ length: 60 }, (_, i) => `v${i}`)).split('&').length, 50);
});

test('site and contact words; the default site is chosen when nothing valid is saved', () => {
  assert.equal(siteOptionText(site({ isDefaultDelivery: true })), 'Gazipur Plant: Plot 5, Tongi, Gazipur (default)');
  assert.equal(siteOptionText(site({ line1: '', upazila: null, district: null })), 'Gazipur Plant');
  assert.equal(roleWords(['admin', 'buyer', 'odd']), 'Admin, Buyer, odd');
  assert.equal(contactOptionText({ userId: A, fullName: 'Rahim', email: 'r@x.bd', roles: ['admin', 'buyer'] }), 'Rahim, r@x.bd (Admin, Buyer)');
  const list = [site({ id: A }), site({ id: B, isDefaultDelivery: true })];
  assert.equal(initialSiteId(list, ''), B);
  assert.equal(initialSiteId(list, A.toUpperCase()), A);
  assert.equal(initialSiteId(list, S1), B);
  assert.equal(initialSiteId([site({ id: A }), site({ id: B })], ''), '');
  assert.equal(initialSiteId([site({ id: A })], ''), A);
  assert.equal(initialSiteId([], A), '');
});

test('expectedSellers: one entry per seller, first appearance order; unknown variants are left out', () => {
  const out = expectedSellers([line(B), line(A), line('4f9f1c52-6d3e-4c1a-9a55-2f7e8d1c4b64'), line(B)], sellers);
  assert.deepEqual(out.map((x) => x.sellerName), ['Beta Store', 'Alpha Store']);
  assert.deepEqual(linesWithoutSeller([line(A), line('4f9f1c52-6d3e-4c1a-9a55-2f7e8d1c4b64')], sellers).length, 1);
});

test('syncFees keeps typed fees, adds empty rows, drops rows of sellers no longer on the quote, names rows, and returns the same array when unchanged', () => {
  const typed: EditorFee = { key: 'f1', sellerId: S1, label: 'Alpha Store', feeText: '120.50' };
  const same = syncFees([typed], [{ sellerId: S1, sellerName: 'Alpha Store' }], key);
  assert.equal(same[0], typed);
  const fees = [typed];
  assert.equal(syncFees(fees, [{ sellerId: S1.toUpperCase(), sellerName: 'Alpha Store' }], key), fees);
  const grown = syncFees(fees, [{ sellerId: S1, sellerName: 'Alpha Store' }, { sellerId: S2, sellerName: 'Beta Store' }], key);
  assert.deepEqual(grown.map((f) => [f.sellerId, f.feeText]), [[S1, '120.50'], [S2, '']]);
  const shrunk = syncFees(grown, [{ sellerId: S2, sellerName: 'Beta Store' }], key);
  assert.deepEqual(shrunk.map((f) => f.sellerId), [S2]);
  const named = syncFees([{ key: 'f9', sellerId: S1, label: '', feeText: '5' }], [{ sellerId: S1, sellerName: 'Alpha Store' }], key);
  assert.deepEqual([named[0].label, named[0].feeText], ['Alpha Store', '5']);
  assert.deepEqual(syncFees([typed], [], key), []);
});

test('a saved fixed-delivery draft reopens with its fees as taka text', () => {
  const f = formFromVersion({
    versionNo: 1, status: 'draft', rowVersion: 1, validityDays: 14, paymentTerms: 'as_account', shippingMode: 'fixed', shipToSiteId: A, termsText: null, buyerNote: null, internalNote: null,
    shippingFees: [{ sellerId: S1, shippingMinor: 35050 }, { sellerId: S2, shippingMinor: 0 }], lines: []
  }, B);
  assert.deepEqual(f.fees.map((x) => [x.sellerId, x.feeText]), [[S1, '350.50'], [S2, '0.00']]);
});

test('quantityHint and lookupFailureText', () => {
  assert.equal(quantityHint(5, 5), 'Minimum order 5. Sold in packs of 5.');
  assert.equal(quantityHint(1, 10), 'Sold in packs of 10.');
  assert.equal(quantityHint(1, 1), '');
  assert.equal(quantityHint(undefined, undefined), '');
  assert.match(lookupFailureText('the delivery sites', 403), /quotes\.edit/);
  assert.match(lookupFailureText('the contacts', null), /cannot be reached/);
  assert.match(lookupFailureText('the contacts', 404), /not found/);
  assert.match(lookupFailureText('the contacts', 500), /Try again/);
});
