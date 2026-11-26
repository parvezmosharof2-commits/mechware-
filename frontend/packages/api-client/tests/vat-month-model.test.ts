import test from 'node:test';
import assert from 'node:assert/strict';
import { canExportVat, isVatMonth, lastMonthDhaka, vatBandText, vatBdt, vatCsvName, vatMonthErrorText, vatMonthQuery, vatRateText } from '../src/vat-month-model.ts';

test('lastMonthDhaka uses Dhaka time and rolls over the year', () => {
  assert.equal(lastMonthDhaka(Date.parse('2026-11-27T10:00:00Z')), '2026-10');
  assert.equal(lastMonthDhaka(Date.parse('2027-01-15T00:00:00Z')), '2026-12');
  // 2026-11-30 18:00 UTC is already 1 December in Dhaka, so last month is November.
  assert.equal(lastMonthDhaka(Date.parse('2026-11-30T18:00:00Z')), '2026-11');
  assert.equal(lastMonthDhaka(Date.parse('2026-11-30T17:59:00Z')), '2026-10');
});

test('isVatMonth accepts only yyyy-MM', () => {
  assert.ok(isVatMonth('2026-10'));
  for (const bad of ['2026-13', '2026-00', '2026-1', '2026-10-01', '', 'abcd-ef']) assert.equal(isVatMonth(bad), false, bad);
});

test('query, file name, money and bands', () => {
  assert.equal(vatMonthQuery('2026-10', 'csv'), 'month=2026-10&format=csv');
  assert.equal(vatCsvName('2026-10'), 'mushak-9_1-2026-10.csv');
  assert.equal(vatBdt(125050), '1,250.50');
  assert.equal(vatBdt(-5), '-0.05');
  assert.equal(vatBandText('zero_or_exempt'), 'Zero-rated or exempt (0%)');
  assert.equal(vatBandText('weird'), 'weird');
  assert.equal(vatRateText(0.15), '15%');
  assert.equal(vatRateText(7.5), '7.5%');
});

test('permission and error words', () => {
  assert.ok(canExportVat(['Invoices.Export']));
  assert.equal(canExportVat(['invoices.view']), false);
  assert.match(vatMonthErrorText({ status: 429 }), /too many times/);
  assert.match(vatMonthErrorText({ status: 403 }), /permission/);
  assert.match(vatMonthErrorText({ status: null }), /cannot be reached/);
  assert.equal(vatMonthErrorText({ status: 418, message: 'teapot' }), 'teapot');
});
