import test from 'node:test';
import assert from 'node:assert/strict';
import {
  classifyInvoiceError, invoiceFilterProblem, invoiceKindKey, invoiceListQuery, invoiceModeKey, invoicePageCount, invoicePdfFileName,
  invoicePdfPath, companyInvoicePdfPath, challanPdfPath, staffChallanPdfPath, challanFileName, hasChallan, classifyChallanError, canPrintChallan,
  staffChallanErrorText, invoiceUrlQuery, isSpecimenInvoice, parseInvoiceUrl
} from '../src/invoices-model.ts';

test('classifyInvoiceError: the three refusals in words, everything else is a retryable outage', () => {
  assert.equal(classifyInvoiceError(404), 'notFound');
  assert.equal(classifyInvoiceError(429), 'rateLimited');
  assert.equal(classifyInvoiceError(503, 'INVOICE_PDF_UNAVAILABLE'), 'pdfUnavailable');
  assert.equal(classifyInvoiceError(503, 'SOMETHING_ELSE'), 'unavailable');
  assert.equal(classifyInvoiceError(503), 'unavailable');
  assert.equal(classifyInvoiceError(500, 'INVOICE_PDF_UNAVAILABLE'), 'unavailable');
  assert.equal(classifyInvoiceError(null), 'unavailable');
  assert.equal(classifyInvoiceError(undefined, null), 'unavailable');
});

test('kind and mode keys: known wire values only, unknown text never reaches the screen', () => {
  assert.equal(invoiceKindKey('tax_invoice'), 'tax_invoice');
  assert.equal(invoiceKindKey('credit_note'), 'credit_note');
  assert.equal(invoiceKindKey('<script>'), 'other');
  assert.equal(invoiceKindKey(null), 'other');
  assert.equal(invoiceModeKey('live'), 'live');
  assert.equal(invoiceModeKey('specimen'), 'specimen');
  assert.equal(invoiceModeKey('Specimen'), 'other');
  assert.equal(isSpecimenInvoice({ mode: 'specimen' }), true);
  assert.equal(isSpecimenInvoice({ mode: 'live' }), false);
});

test('invoicePdfPath: ids encoded, only en or bn reaches the server', () => {
  assert.equal(invoicePdfPath('o/1', 'i 2', 'bn'), '/api/v1/orders/o%2F1/invoices/i%202/pdf?locale=bn');
  assert.equal(invoicePdfPath('o', 'i', 'en'), '/api/v1/orders/o/invoices/i/pdf?locale=en');
  assert.equal(invoicePdfPath('o', 'i', 'fr' as never), '/api/v1/orders/o/invoices/i/pdf?locale=en');
});

test('invoicePdfFileName: safe characters and the language', () => {
  assert.equal(invoicePdfFileName('MW-2026/27-000012', 'en'), 'MW-2026-27-000012-en.pdf');
  assert.equal(invoicePdfFileName('../../x', 'bn'), 'x-bn.pdf');
  assert.equal(invoicePdfFileName('///', 'en'), 'invoice-en.pdf');
});

test('invoiceFilterProblem: format and order, empty is fine', () => {
  assert.equal(invoiceFilterProblem('', ''), null);
  assert.equal(invoiceFilterProblem('2026-11-01', ''), null);
  assert.equal(invoiceFilterProblem('2026-13-01', ''), 'fromFormat');
  assert.equal(invoiceFilterProblem('', '26-11-01'), 'toFormat');
  assert.equal(invoiceFilterProblem('2026-11-30', '2026-11-01'), 'order');
  assert.equal(invoiceFilterProblem('2026-11-01', '2026-11-01'), null);
});

test('invoiceListQuery: valid days only, page >= 1, page size 1..50', () => {
  assert.equal(invoiceListQuery({}), 'page=1&pageSize=20');
  assert.equal(invoiceListQuery({ from: '2026-11-01', to: '2026-11-30', page: 3, pageSize: 10 }), 'from=2026-11-01&to=2026-11-30&page=3&pageSize=10');
  assert.equal(invoiceListQuery({ from: 'junk', to: '2026-02-30', page: 0, pageSize: 500 }), 'page=1&pageSize=50');
  assert.equal(invoiceListQuery({ page: Number.NaN, pageSize: 0 }), 'page=1&pageSize=1');
});

test('parseInvoiceUrl and invoiceUrlQuery round-trip and drop junk', () => {
  const q = (s: string) => { const p = new URLSearchParams(s); return (k: string) => p.get(k); };
  assert.deepEqual(parseInvoiceUrl(q('from=2026-11-01&to=2026-11-30&page=2')), { from: '2026-11-01', to: '2026-11-30', page: 2 });
  assert.deepEqual(parseInvoiceUrl(q('from=x&to=2026-11-30&page=-4')), { from: '', to: '2026-11-30', page: 1 });
  assert.deepEqual(parseInvoiceUrl(q('from=2026-11-30&to=2026-11-01')), { from: '2026-11-30', to: '', page: 1 });
  assert.deepEqual(parseInvoiceUrl(q('page=1.5')), { from: '', to: '', page: 1 });
  assert.equal(invoiceUrlQuery({ from: '', to: '', page: 1 }), '');
  assert.equal(invoiceUrlQuery({ from: '2026-11-01', to: '', page: 3 }), 'from=2026-11-01&page=3');
});

test('invoicePageCount never goes below 1', () => {
  assert.equal(invoicePageCount(0, 20), 1);
  assert.equal(invoicePageCount(41, 20), 3);
  assert.equal(invoicePageCount(-5, 0), 1);
});

test('PDF paths: company invoice, buyer challan, staff challan (ids encoded, language en|bn only)', () => {
  assert.equal(companyInvoicePdfPath('c 1', 'i/2', 'bn'), '/api/v1/companies/c%201/invoices/i%2F2/pdf?locale=bn');
  assert.equal(companyInvoicePdfPath('c', 'i', 'xx' as 'en'), '/api/v1/companies/c/invoices/i/pdf?locale=en');
  assert.equal(challanPdfPath('o1', 'en'), '/api/v1/orders/o1/challan?locale=en');
  assert.equal(staffChallanPdfPath('o1', 'bn'), '/api/v1/admin/orders/o1/challan?locale=bn');
  assert.equal(challanFileName('MW-261127-ABC234', 'bn'), 'challan-MW-261127-ABC234-bn.pdf');
  assert.equal(challanFileName('///', 'en'), 'challan-order-en.pdf');
});

test('challan: shown only once a shipment exists; 409 is words, other refusals as for invoices', () => {
  assert.equal(hasChallan({ shipments: [] }), false);
  assert.equal(hasChallan({}), false);
  assert.equal(hasChallan({ shipments: [{}] }), true);
  assert.equal(classifyChallanError(409, 'CHALLAN_NOT_AVAILABLE'), 'notShipped');
  assert.equal(classifyChallanError(409, 'OTHER'), 'unavailable');
  assert.equal(classifyChallanError(404), 'notFound');
  assert.equal(classifyChallanError(429), 'rateLimited');
  assert.match(staffChallanErrorText({ status: 409, code: 'CHALLAN_NOT_AVAILABLE' }), /not been shipped/);
  assert.match(staffChallanErrorText({ status: 403 }), /orders\.fulfil/);
});

test('canPrintChallan: orders.fulfil OR orders.view_all, case-insensitive, nothing else', () => {
  assert.equal(canPrintChallan(['orders.fulfil']), true);
  assert.equal(canPrintChallan(['Orders.View_All']), true);
  assert.equal(canPrintChallan(['orders.cancel', 'invoices.view']), false);
  assert.equal(canPrintChallan([]), false);
});
