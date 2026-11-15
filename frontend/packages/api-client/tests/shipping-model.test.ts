import test from 'node:test';
import assert from 'node:assert/strict';
import {
  detailToForm, emptyZoneForm, formToBody, gramsToKg, groupByDivision, kgToGrams, mapServerFields, minorToTakaInput, otherZoneOwner,
  previewReasonText, previewToRequest, shippingErrorText, type CoverageDistrict, type ZoneForm
} from '../src/shipping-model.ts';

const good = (): ZoneForm => ({
  ...emptyZoneForm(), code: 'Inside-Dhaka', nameEn: 'Inside Dhaka', nameBn: 'Dhaka BN', isActive: true, districtIds: ['d1'],
  bands: [{ kg: '1', taka: '60' }, { kg: '5', taka: '90.5' }, { kg: '20', taka: '200' }], freeThreshold: '5000', heavyKg: '50', oversizeMm: '1200', cap: '500'
});

test('kg and grams convert as strings', () => {
  assert.equal(kgToGrams('1'), 1000);
  assert.equal(kgToGrams('0.001'), 1);
  assert.equal(kgToGrams('2.5'), 2500);
  assert.equal(kgToGrams('1.005'), 1005);
  assert.equal(kgToGrams('1.0005'), null);
  assert.equal(kgToGrams('-1'), null);
  assert.equal(kgToGrams('1e3'), null);
  assert.equal(kgToGrams(''), null);
  assert.equal(gramsToKg(2500), '2.5');
  assert.equal(gramsToKg(1000), '1');
  assert.equal(gramsToKg(1005), '1.005');
  assert.equal(gramsToKg(1), '0.001');
  assert.equal(minorToTakaInput(5000), '50');
  assert.equal(minorToTakaInput(5050), '50.50');
  assert.equal(minorToTakaInput(5), '0.05');
});

test('a valid form builds the body in poisha and grams, code lower-cased', () => {
  const { body, errors } = formToBody(good(), true);
  assert.deepEqual(errors, {});
  assert.equal(body?.code, 'inside-dhaka');
  assert.deepEqual(body?.rates.bands, [{ maxGrams: 1000, feeMinor: 6000 }, { maxGrams: 5000, feeMinor: 9050 }, { maxGrams: 20000, feeMinor: 20000 }]);
  assert.equal(body?.rates.freeShippingThresholdMinor, 500000);
  assert.equal(body?.rates.heavyUnitGrams, 50000);
  assert.equal(body?.rates.oversizeDimensionMm, 1200);
  assert.equal(body?.rates.maxFeeMinor, 50000);
});

test('editing does not send the code; round trip through the detail keeps values', () => {
  const body = formToBody(good(), true).body!;
  const detail = { id: 'z', code: 'inside-dhaka', nameEn: body.nameEn, nameBn: body.nameBn, isActive: true, version: 3, districts: [{ id: 'd1', nameEn: 'Dhaka', nameBn: 'Dhaka' }], rates: body.rates, createdAtUtc: '', updatedAtUtc: '' };
  const form = detailToForm(detail);
  assert.equal(form.bands[1].taka, '90.50');
  assert.equal(form.heavyKg, '50');
  assert.equal('code' in formToBody(form, false).body!, false);
});

test('code, names and counts are checked', () => {
  const f = { ...good(), code: '-bad', nameEn: '', nameBn: 'x'.repeat(101), districtIds: [] };
  const { body, errors } = formToBody(f, true);
  assert.equal(body, null);
  assert.ok(errors.code && errors.nameEn && errors.nameBn && errors.districts);
  assert.equal(formToBody({ ...f, code: 'ok_1', nameEn: 'a', nameBn: 'b', isActive: false }, true).errors.code, undefined);
  assert.ok(formToBody({ ...good(), districtIds: Array.from({ length: 101 }, (_, i) => String(i)) }, true).errors.districts);
  const many = { ...good(), bands: Array.from({ length: 21 }, (_, i) => ({ kg: String(i + 1), taka: '10' })) };
  assert.ok(formToBody(many, true).errors.bands);
});

test('bands: ascending unique weights, non-decreasing fees, cap, limits', () => {
  const dup = { ...good(), bands: [{ kg: '1', taka: '60' }, { kg: '1', taka: '70' }] };
  assert.ok(formToBody(dup, true).errors['bands.1.kg']);
  const down = { ...good(), bands: [{ kg: '5', taka: '60' }, { kg: '1', taka: '70' }] };
  assert.ok(formToBody(down, true).errors['bands.1.kg']);
  const cheaper = { ...good(), bands: [{ kg: '1', taka: '60' }, { kg: '5', taka: '50' }] };
  assert.ok(formToBody(cheaper, true).errors['bands.1.fee']);
  const equalFee = { ...good(), bands: [{ kg: '1', taka: '60' }, { kg: '5', taka: '60' }] };
  assert.equal(formToBody(equalFee, true).errors['bands.1.fee'], undefined);
  assert.ok(formToBody({ ...good(), cap: '100', bands: [{ kg: '1', taka: '60' }, { kg: '5', taka: '150' }] }, true).errors['bands.1.fee']);
  assert.ok(formToBody({ ...good(), bands: [{ kg: '1000.001', taka: '60' }] }, true).errors['bands.0.kg']);
  assert.equal(formToBody({ ...good(), bands: [{ kg: '1000', taka: '60' }] }, true).errors['bands.0.kg'], undefined);
  assert.ok(formToBody({ ...good(), bands: [{ kg: '1', taka: '100000.01' }] }, true).errors['bands.0.fee']);
  assert.ok(formToBody({ ...good(), bands: [{ kg: '1', taka: '12.345' }] }, true).errors['bands.0.fee']);
  assert.ok(formToBody({ ...good(), bands: [] }, true).errors.bands);
  assert.equal(formToBody({ ...good(), bands: [], isActive: false }, true).errors.bands, undefined);
});

test('other limits: threshold, heavy kg, oversize mm', () => {
  assert.ok(formToBody({ ...good(), freeThreshold: '100000000.01' }, true).errors.freeThreshold);
  assert.ok(formToBody({ ...good(), heavyKg: '1000.5' }, true).errors.heavyKg);
  assert.ok(formToBody({ ...good(), oversizeMm: '20001' }, true).errors.oversizeMm);
  assert.ok(formToBody({ ...good(), oversizeMm: '12.5' }, true).errors.oversizeMm);
  const blank = formToBody({ ...good(), freeThreshold: '', heavyKg: '', oversizeMm: '' }, true).body!;
  assert.equal(blank.rates.freeShippingThresholdMinor, null);
  assert.equal(blank.rates.heavyUnitGrams, null);
  assert.ok(formToBody({ ...good(), cap: '' }, true).errors.cap, 'the server requires the fee cap');
  assert.equal(formToBody({ ...good(), bands: [{ kg: '1', taka: '0' }, { kg: '2', taka: '10' }] }, true).errors['bands.0.fee'], undefined, 'a free band (fee 0) is allowed');
});

test('server field keys map onto form keys', () => {
  const m = mapServerFields({ 'rates.bands[2].maxGrams': 'a', 'rates.bands[0].feeMinor': 'b', bands: 'c', districtIds: 'd', 'rates.maxFeeMinor': 'e', expectedVersion: 'f' });
  assert.equal(m['bands.2.kg'], 'a');
  assert.equal(m['bands.0.fee'], 'b');
  assert.equal(m.bands, 'c');
  assert.equal(m.districts, 'd');
  assert.equal(m.cap, 'e');
  assert.equal(m.expectedVersion, 'f');
});

test('error codes become plain words', () => {
  const e = (status: number, code: string | null = null, message = '', fields: Record<string, string> = {}) => shippingErrorText({ status, code, message, fields });
  assert.match(e(409, 'SHIPPING_ZONE_STALE'), /Someone else changed this zone/);
  assert.match(e(409, 'SHIPPING_ZONE_ACTIVE'), /inactive/);
  assert.match(e(409, 'SHIPPING_ZONE_CODE_TAKEN'), /code is already used/);
  assert.match(e(409, 'SHIPPING_DISTRICT_ALREADY_IN_ZONE', 'Mymensingh is in zone x (active).'), /^Mymensingh is in zone x/);
  assert.match(e(409, 'SHIPPING_ZONE_CONFLICT'), /same moment/);
  assert.match(e(401), /session/);
  assert.match(e(403), /permission/);
  assert.match(e(500), /server had a problem/);
  assert.match(e(400, null, 'Invalid.', { code: 'Bad code' }), /Bad code/);
  assert.match(shippingErrorText({ status: null, code: null, message: '', fields: {} }), /Could not reach/);
});

test('coverage grouping and owner', () => {
  const d = (districtId: string, nameEn: string, divisionNameEn: string, zoneId: string | null, zoneCode: string | null): CoverageDistrict => ({ districtId, nameEn, nameBn: nameEn, divisionNameEn, zoneId, zoneCode, zoneActive: zoneId ? true : null });
  const groups = groupByDivision([d('1', 'Gazipur', 'Dhaka', null, null), d('2', 'Bogura', 'Rajshahi', 'z', 'x'), d('3', 'Dhaka', 'Dhaka', 'z', 'x')]);
  assert.deepEqual(groups.map((g) => g.division), ['Dhaka', 'Rajshahi']);
  assert.deepEqual(groups[0].districts.map((x) => x.nameEn), ['Dhaka', 'Gazipur']);
  assert.equal(otherZoneOwner(d('2', 'a', 'b', 'z', 'x'), 'z'), null);
  assert.equal(otherZoneOwner(d('2', 'a', 'b', 'z', 'x'), 'y'), 'x');
  assert.equal(otherZoneOwner(d('2', 'a', 'b', null, null), 'y'), null);
});

test('preview request: grams, poisha, optional dimensions; bad input refused', () => {
  const ok = previewToRequest({ districtId: 'd', subtotal: '1,500.5', lines: [{ quantity: '2', kg: '0.75', lengthMm: '300', widthMm: '', heightMm: '' }] });
  assert.deepEqual(ok.request, { districtId: 'd', subtotalMinor: 150050, lines: [{ quantity: 2, unitWeightGrams: 750, lengthMm: 300 }] });
  assert.ok(previewToRequest({ districtId: '', subtotal: '', lines: [] }).error);
  assert.ok(previewToRequest({ districtId: 'd', subtotal: '', lines: [{ quantity: '0', kg: '1', lengthMm: '', widthMm: '', heightMm: '' }] }).error);
  assert.ok(previewToRequest({ districtId: 'd', subtotal: '', lines: [{ quantity: '1', kg: '', lengthMm: '', widthMm: '', heightMm: '' }] }).error);
  assert.ok(previewToRequest({ districtId: 'd', subtotal: '1.234', lines: [{ quantity: '1', kg: '1', lengthMm: '', widthMm: '', heightMm: '' }] }).error);
  assert.equal(previewReasonText({ code: 'HEAVY_ITEM', lineIndex: 1 }), 'Line 2: A unit is heavier than the heavy-unit limit.');
});
