import test from 'node:test';
import assert from 'node:assert/strict';
import {
  CompanyRoles,
  amountToMinor,
  creditTermsValue,
  creditUsagePercent,
  hasRole,
  isValidRoles,
  minorToAmount,
  normalizeDigits,
  roleNames,
  rolesFromNames,
  toRegisterRequest,
  validateCompanyRegistration
} from '../src/companies-model.ts';

const valid = { legalName: 'Square Textiles Ltd', tradeLicenceNo: 'TL-2020-1234', bin: '0012345678901', tin: '123456789012', industry: 'Textile' };

test('role flags match the API values and combine', () => {
  assert.deepEqual(CompanyRoles, { Requester: 1, Buyer: 2, Approver: 4, Admin: 8 });
  const roles = rolesFromNames(['Buyer', 'Approver']);
  assert.equal(roles, 6);
  assert.deepEqual(roleNames(roles), ['Buyer', 'Approver']);
  assert.equal(hasRole(roles, CompanyRoles.Admin), false);
  assert.equal(hasRole(roles, CompanyRoles.Buyer), true);
});

test('role validity mirrors the API (non-zero, known bits only)', () => {
  for (const [roles, ok] of [[0, false], [1, true], [6, true], [15, true], [16, false], [17, false], [-1, false], [1.5, false]] as const) {
    assert.equal(isValidRoles(roles), ok, `roles=${roles}`);
  }
});

test('money conversion is exact for whole-poisha amounts', () => {
  assert.equal(minorToAmount({ minorUnits: 12500000, currency: 'BDT' }), 125000);
  assert.equal(amountToMinor(125000), 12500000);
  assert.equal(amountToMinor(0.1 + 0.2), 30);
  assert.equal(amountToMinor(1.005), 101);
  for (let minor = 0; minor < 5000; minor += 7) {
    assert.equal(amountToMinor(minor / 100), minor, `minor=${minor}`);
  }
});

test('credit usage percent is clamped and safe without a limit', () => {
  const c = (limit: number, used: number) => ({ creditLimit: { minorUnits: limit, currency: 'BDT' }, creditUsed: { minorUnits: used, currency: 'BDT' } });
  assert.equal(creditUsagePercent(c(0, 0)), 0);
  assert.equal(creditUsagePercent(c(1000, 250)), 25);
  assert.equal(creditUsagePercent(c(1000, 1000)), 100);
  assert.equal(creditUsagePercent(c(1000, 5000)), 100);
  assert.equal(creditUsagePercent(c(1000, -5)), 0);
});

test('credit terms map to the API day values', () => {
  assert.equal(creditTermsValue('none'), 0);
  assert.equal(creditTermsValue('net15'), 15);
  assert.equal(creditTermsValue('net30'), 30);
});

test('Bangla digits and separators normalise for BIN / TIN', () => {
  assert.equal(normalizeDigits('০০১২-৩৪৫ ৬৭৮৯০১'), '0012345678901');
  assert.equal(normalizeDigits('12 34-56'), '123456');
});

test('registration validation matches the API rules', () => {
  assert.deepEqual(validateCompanyRegistration(valid), {});
  assert.deepEqual(validateCompanyRegistration({ ...valid, tin: '' }), {});
  assert.deepEqual(validateCompanyRegistration({ ...valid, tin: null }), {});
  assert.equal(validateCompanyRegistration({ ...valid, legalName: 'A' }).legalName, 'legalName.length');
  assert.equal(validateCompanyRegistration({ ...valid, legalName: 'x'.repeat(201) }).legalName, 'legalName.length');
  assert.equal(validateCompanyRegistration({ ...valid, tradeLicenceNo: 'ab' }).tradeLicenceNo, 'tradeLicenceNo.length');
  assert.equal(validateCompanyRegistration({ ...valid, bin: '123' }).bin, 'bin.format');
  assert.equal(validateCompanyRegistration({ ...valid, bin: '00123456789AB' }).bin, 'bin.format');
  assert.equal(validateCompanyRegistration({ ...valid, bin: '00123456789012' }).bin, 'bin.format');
  assert.equal(validateCompanyRegistration({ ...valid, tin: '12345' }).tin, 'tin.format');
  assert.equal(validateCompanyRegistration({ ...valid, industry: 'x'.repeat(101) }).industry, 'industry.length');
  assert.deepEqual(validateCompanyRegistration({ ...valid, bin: '০০১২৩৪৫৬৭৮৯০১' }), {});
});

test('toRegisterRequest trims, normalises digits and nulls empty optionals', () => {
  assert.deepEqual(
    toRegisterRequest({ legalName: '  Acme Ltd ', tradeLicenceNo: ' TL-9 ', bin: '0012 3456 78901', tin: '  ', industry: '' }),
    { legalName: 'Acme Ltd', tradeLicenceNo: 'TL-9', bin: '0012345678901', tin: null, industry: null }
  );
});
