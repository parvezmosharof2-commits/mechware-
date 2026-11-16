import test from 'node:test';
import assert from 'node:assert/strict';
import {
  asksKey, buildItemsQuery, buyerCompanies, chooseCompany, contractLastDay, indexItems, pickDisplay, tierRows, vatPercent, type CompanyPriceItem
} from '../src/company-prices-model.ts';

const A = '9cf9a430-ddd9-42dd-929f-f32f00b0c0bd';
const B = '426401c0-76c0-463b-bf68-cf0eacd8109e';

const item = (over: Partial<CompanyPriceItem> = {}): CompanyPriceItem => ({
  variantId: A, status: 'available', quantity: 2, unitPriceMinorNet: 80000, publicUnitPriceMinorNet: 100000, guestUnitPriceMinorInclVat: 115000,
  source: 'contract', contractRef: 'PL-7', validToUtc: '2027-01-01T00:00:00Z', vatRate: 15, lineNetMinor: 160000, lineTaxMinor: 24000, lineTotalMinor: 184000, ...over
});

test('buildItemsQuery joins variant:qty, keeps each variant once and clamps the quantity', () => {
  assert.equal(buildItemsQuery([{ variantId: A, quantity: 3 }, { variantId: B, quantity: 0 }, { variantId: A.toUpperCase(), quantity: 9 }]), `${A}:3,${B}:1`);
  assert.equal(buildItemsQuery([{ variantId: A, quantity: 99999 }]), `${A}:10000`);
  assert.equal(buildItemsQuery([{ variantId: A, quantity: 2.9 }]), `${A}:2`);
  assert.equal(buildItemsQuery([{ variantId: A, quantity: Number.NaN }]), `${A}:1`);
});

test('buildItemsQuery drops bad ids and caps at 100 items', () => {
  assert.equal(buildItemsQuery([{ variantId: 'nope', quantity: 1 }, { variantId: '', quantity: 1 }]), '');
  const many = Array.from({ length: 150 }, (_, i) => ({ variantId: `00000000-0000-4000-8000-${String(i).padStart(12, '0')}`, quantity: 1 }));
  assert.equal(buildItemsQuery(many).split(',').length, 100);
  assert.equal(asksKey('c1', [{ variantId: A, quantity: 1 }]), `c1|${A}:1`);
});

test('pickDisplay: contract cheaper than public is a contract price with its ref', () => {
  const d = pickDisplay(item())!;
  assert.equal(d.kind, 'contract');
  assert.equal(d.contractRef, 'PL-7');
  assert.equal(d.savingPerUnitNetMinor, 20000);
  assert.equal(d.vatPercent, 15);
  assert.equal(d.guestUnitInclVatMinor, 115000);
});

test('pickDisplay: quantity break or sale is "your price", without a contract ref', () => {
  const q = pickDisplay(item({ source: 'quantity_break', contractRef: 'X' }))!;
  assert.equal(q.kind, 'yourPrice');
  assert.equal(q.contractRef, undefined);
  assert.equal(pickDisplay(item({ source: 'sale' }))!.kind, 'yourPrice');
});

test('pickDisplay: nothing extra for list, equal or dearer prices, unavailable or incomplete answers', () => {
  assert.equal(pickDisplay(item({ source: 'list' }))!.kind, 'none');
  assert.equal(pickDisplay(item({ unitPriceMinorNet: 100000 }))!.kind, 'none');
  assert.equal(pickDisplay(item({ unitPriceMinorNet: 120000 }))!.kind, 'none');
  assert.equal(pickDisplay({ variantId: A, status: 'unavailable' }), null);
  assert.equal(pickDisplay(undefined), null);
  assert.equal(pickDisplay(item({ lineTotalMinor: undefined })), null);
});

test('comparison is on the net basis: a net price is never compared with the VAT-inclusive guest price', () => {
  // net 90,000 is below the guest VAT-inclusive 115,000 but NOT below the public net 80,000: no badge.
  assert.equal(pickDisplay(item({ unitPriceMinorNet: 90000, publicUnitPriceMinorNet: 80000 }))!.kind, 'none');
});

test('vatPercent accepts a percent or a fraction', () => {
  assert.equal(vatPercent(15), 15);
  assert.equal(vatPercent(0.15), 15);
  assert.equal(vatPercent(undefined), 0);
  assert.equal(vatPercent(-1), 0);
});

test('tierRows sorts and marks the tier of the asked quantity', () => {
  const it = item({ breaks: [{ minQty: 50, unitPriceMinorNet: 70000 }, { minQty: 10, unitPriceMinorNet: 75000 }] });
  assert.deepEqual(tierRows(it, 1).map((r) => r.current), [false, false]);
  assert.deepEqual(tierRows(it, 10).map((r) => [r.minQty, r.current]), [[10, true], [50, false]]);
  assert.deepEqual(tierRows(it, 60).map((r) => [r.minQty, r.current]), [[10, false], [50, true]]);
  assert.deepEqual(tierRows(item(), 5), []);
});

test('contractLastDay shows the day before the exclusive end, in Dhaka time', () => {
  assert.equal(contractLastDay('2027-01-01T00:00:00Z'), '2027-01-01'); // 06:00 on Jan 1 in Dhaka: still the 1st
  assert.equal(contractLastDay('2026-12-31T18:00:00Z'), '2026-12-31'); // exactly midnight Jan 1 Dhaka: last day is Dec 31
  assert.equal(contractLastDay(undefined), null);
  assert.equal(contractLastDay('garbage'), null);
});

test('indexItems and company choice', () => {
  const map = indexItems({ data: { companyId: 'c', asOfUtc: 'x', items: [item(), item({ variantId: B.toUpperCase() })] } });
  assert.ok(map.has(A) && map.has(B));
  assert.equal(indexItems(null).size, 0);
  const mine = [
    { company: { id: 'p', legalName: 'Pending', status: 'pending' }, roles: 2 },
    { company: { id: 'r', legalName: 'Requester only', status: 'active' }, roles: 1 },
    { company: { id: 'b1', legalName: 'Buyer one', status: 'active' }, roles: 3 },
    { company: { id: 'b2', legalName: 'Buyer two', status: 'active' }, roles: 10 }
  ];
  const usable = buyerCompanies(mine);
  assert.deepEqual(usable.map((m) => m.company.id), ['b1', 'b2']);
  assert.equal(chooseCompany(usable)!.company.id, 'b1');
  assert.equal(chooseCompany(usable, 'b2')!.company.id, 'b2');
  assert.equal(chooseCompany(usable, 'gone')!.company.id, 'b1');
  assert.equal(chooseCompany([]), undefined);
});
