import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  EMPTY_INVOICE_FILTERS, activeStaffInvoiceFilterCount, canViewInvoices, documentCheckText, isSha256Hex, parseStaffInvoiceFilters, sha256Hex,
  staffInvoiceErrorText, staffInvoiceFilterProblem, staffInvoiceFiltersToSearch, staffInvoiceListQuery, staffInvoicePdfPath
} from '../src/invoices-staff-model.ts';

const q = (s: string) => { const p = new URLSearchParams(s); return (k: string) => p.get(k); };
const ID = '0f8fad5b-d9cb-469f-a165-70867728950e';

test('canViewInvoices: invoices.view only, any case', () => {
  assert.equal(canViewInvoices(['Invoices.View']), true);
  assert.equal(canViewInvoices(['orders.view_all']), false);
  assert.equal(canViewInvoices([]), false);
});

test('filters round-trip through the URL and junk is dropped', () => {
  const f = { number: 'MW-26', orderNumber: 'ORD-1', companyId: ID, companyName: 'Acme', from: '2026-11-01', to: '2026-11-30', kind: 'credit_note', mode: 'specimen', page: 3 };
  assert.deepEqual(parseStaffInvoiceFilters(q(staffInvoiceFiltersToSearch(f))), f);
  assert.equal(staffInvoiceFiltersToSearch(EMPTY_INVOICE_FILTERS), '');
  const junk = parseStaffInvoiceFilters(q('number=<x>&order=a b&company=nope&companyName=x&from=2026-02-30&to=zz&kind=other&mode=Live&page=-4'));
  assert.deepEqual(junk, EMPTY_INVOICE_FILTERS);
  assert.equal(parseStaffInvoiceFilters(q('from=2026-11-30&to=2026-11-01')).to, '');
});

test('list query sends every filter, page size 50, and the filter count', () => {
  const f = { ...EMPTY_INVOICE_FILTERS, number: 'MW', orderNumber: 'O', companyId: ID, from: '2026-11-01', to: '2026-11-02', kind: 'tax_invoice', mode: 'live', page: 2 };
  assert.equal(staffInvoiceListQuery(f), `number=MW&orderNumber=O&companyId=${ID}&from=2026-11-01&to=2026-11-02&kind=tax_invoice&mode=live&page=2&pageSize=50`);
  assert.equal(staffInvoiceListQuery({ ...EMPTY_INVOICE_FILTERS, page: 0 }), 'page=1&pageSize=50');
  assert.equal(activeStaffInvoiceFilterCount(f), 7);
  assert.equal(activeStaffInvoiceFilterCount(EMPTY_INVOICE_FILTERS), 0);
});

test('filter problems in words', () => {
  assert.equal(staffInvoiceFilterProblem({ number: '', orderNumber: '', from: '', to: '' }), null);
  assert.match(staffInvoiceFilterProblem({ number: '<b>', orderNumber: '', from: '', to: '' })!, /invoice number/);
  assert.match(staffInvoiceFilterProblem({ number: '', orderNumber: '??', from: '', to: '' })!, /order number/);
  assert.match(staffInvoiceFilterProblem({ number: '', orderNumber: '', from: '2026-13-01', to: '' })!, /"from" date is not/);
  assert.match(staffInvoiceFilterProblem({ number: '', orderNumber: '', from: '2026-12-01', to: '2026-11-01' })!, /after/);
});

test('staffInvoicePdfPath: id encoded, only en or bn', () => {
  assert.equal(staffInvoicePdfPath('a/b', 'bn'), '/api/v1/admin/invoices/a%2Fb/pdf?locale=bn');
  assert.equal(staffInvoicePdfPath('x', 'fr' as never), '/api/v1/admin/invoices/x/pdf?locale=en');
});

test('error words: tamper, outage, refusals', () => {
  assert.match(staffInvoiceErrorText({ status: 503, code: 'INVOICE_PDF_UNAVAILABLE' }), /could not be verified/);
  assert.match(staffInvoiceErrorText({ status: 503, code: 'X' }), /server had a problem/);
  assert.match(staffInvoiceErrorText({ status: null }), /cannot be reached/);
  assert.match(staffInvoiceErrorText({ status: 403 }), /invoices\.view/);
  assert.match(staffInvoiceErrorText({ status: 429 }), /minute/);
});

test('sha256Hex equals the reference hash; isSha256Hex checks 64 hex digits', async () => {
  const bytes = new TextEncoder().encode('%PDF-1.7 test').buffer as ArrayBuffer;
  assert.equal(await sha256Hex(bytes), createHash('sha256').update(Buffer.from(bytes)).digest('hex'));
  assert.equal(await sha256Hex(new ArrayBuffer(0)), 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855');
  assert.equal(isSha256Hex('a'.repeat(64)), true);
  assert.equal(isSha256Hex('g'.repeat(64)), false);
  assert.equal(isSha256Hex('a'.repeat(63)), false);
});

test('documentCheckText: names the invoice and language, or says it is not ours', () => {
  assert.match(documentCheckText({ invoiceId: 'i', number: 'SPEC-1', kind: 'tax_invoice', locale: 'bn' }), /Tax invoice \(Mushak-6\.3\) SPEC-1, Bangla/);
  assert.match(documentCheckText(null), /^Not a document we issued/);
});
