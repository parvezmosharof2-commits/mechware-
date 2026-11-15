import test from 'node:test';
import assert from 'node:assert/strict';
import type { MechWareApiClient } from '../src/index.ts';
import { createQuickOrderApi } from '../src/quick-order.ts';
import {
  addSummary,
  buildCartBatch,
  classifyQuickOrderError,
  fixPart,
  fixQuantity,
  formatLine,
  groupLines,
  issueMessageKey,
  lineNoticeKey,
  lineTone,
  maxAvailableFix,
  offersFix,
  orderedRows,
  removeLines,
  rewriteLines,
  statusMessageKey,
  summarize,
  variantPartText,
  type QuickOrderLine,
  type QuickOrderResult,
  type QuickOrderVariant
} from '../src/quick-order-model.ts';

const variant = (id: string, sku: string, partNumber: string | null = null): QuickOrderVariant => ({
  id, sku, partNumber, title: sku, titleBn: sku, brand: null, slug: sku.toLowerCase(), options: {}
});

const line = (over: Partial<QuickOrderLine>): QuickOrderLine => ({
  lineNumbers: [1], raw: 'x', part: 'X', requestedQuantity: 1, status: 'OK', message: '', valid: true,
  variant: variant('v1', 'SKU-1', 'PN-1'), quantity: 1, notices: [], stock: { available: 10, status: 'in_stock' },
  unitPriceMinor: 100, lineTotalMinor: 100, priceSource: 'list', successor: null, candidates: [], suggestions: [], ...over
});

test('status and issue messages map to keys, unknown ones to a generic key', () => {
  assert.equal(statusMessageKey('OK'), 'status_ok');
  assert.equal(statusMessageKey('PART_NOT_FOUND'), 'status_notFound');
  assert.equal(statusMessageKey('PART_AMBIGUOUS'), 'status_ambiguous');
  assert.equal(statusMessageKey('PART_OBSOLETE'), 'status_obsolete');
  assert.equal(statusMessageKey('OUT_OF_STOCK'), 'status_outOfStock');
  assert.equal(statusMessageKey('INSUFFICIENT_STOCK'), 'status_insufficientStock');
  assert.equal(statusMessageKey('QTY_EXCEEDS_LIMIT'), 'status_qtyExceeds');
  assert.equal(statusMessageKey('SOMETHING_NEW'), 'status_unknown');
  assert.equal(issueMessageKey('QTY_MISSING'), 'issue_QTY_MISSING');
  assert.equal(issueMessageKey('NEW_CODE'), 'issue_unknown');
  assert.equal(lineNoticeKey('quantityAdjustedToPack'), 'notice_quantityAdjustedToPack');
  assert.equal(lineNoticeKey('nrnd'), 'notice_nrnd');
  assert.equal(lineNoticeKey('weird'), 'notice_unknown');
});

test('tone: invalid lines are errors, adjusted quantities are warnings', () => {
  assert.equal(lineTone(line({})), 'ok');
  assert.equal(lineTone(line({ valid: false, status: 'OUT_OF_STOCK' })), 'error');
  assert.equal(lineTone(line({ notices: [{ kind: 'quantityAdjustedToPack', was: 3, now: 10 }] })), 'warning');
  assert.equal(lineTone(line({ notices: [{ kind: 'belowMinimumOrder', was: 1, now: 5 }] })), 'warning');
  assert.equal(lineTone(line({ notices: [{ kind: 'nrnd', was: null, now: null }] })), 'ok');
});

test('which lines offer a one-click fix', () => {
  assert.equal(offersFix(line({})), false);
  assert.equal(offersFix(line({ valid: false, status: 'PART_NOT_FOUND', variant: null })), false);
  assert.equal(offersFix(line({ valid: false, status: 'PART_NOT_FOUND', variant: null, suggestions: [variant('a', 'A')] })), true);
  assert.equal(offersFix(line({ valid: false, status: 'PART_AMBIGUOUS', variant: null, candidates: [variant('a', 'A'), variant('b', 'B')] })), true);
  assert.equal(offersFix(line({ valid: false, status: 'PART_OBSOLETE', successor: { variant: variant('n', 'N'), isDropIn: true, note: null } })), true);
  assert.equal(offersFix(line({ valid: false, status: 'PART_OBSOLETE' })), false);
  assert.equal(offersFix(line({ valid: false, status: 'INSUFFICIENT_STOCK', stock: { available: 4, status: 'low_stock' } })), true);
  assert.equal(offersFix(line({ valid: false, status: 'OUT_OF_STOCK', stock: { available: 0, status: 'out_of_stock' } })), false);
});

const result = (): QuickOrderResult => ({
  lines: [
    line({ lineNumbers: [3] }),
    line({ lineNumbers: [1], valid: false, status: 'PART_NOT_FOUND', variant: null }),
    line({ lineNumbers: [4, 6], quantity: 7 })
  ],
  errors: [{ lineNumber: 2, code: 'QTY_INVALID', message: 'x', relatedLines: null }],
  notices: [],
  summary: { lines: 3, valid: 2, needsAttention: 1, subtotalMinor: 200, currency: 'BDT' }
});

test('rows come back in the order of the pasted text, errors included', () => {
  assert.deepEqual(orderedRows(result()).map((r) => r.order), [1, 2, 3, 4]);
});

test('grouping: valid lines apart, everything else (parser errors too) under needs attention', () => {
  const g = groupLines(result());
  assert.deepEqual(g.valid.map((l) => l.lineNumbers[0]), [3, 4]);
  assert.deepEqual(g.attention.map((r) => r.order), [1, 2]);
  assert.deepEqual(g.attention.map((r) => r.kind), ['line', 'error']);
});

test('summary adds the parser errors to the lines needing attention', () => {
  assert.deepEqual(summarize(result()), { valid: 2, needsAttention: 2 });
});

test('formatLine writes a line the parser reads back (comma form, quotes when needed)', () => {
  assert.equal(formatLine('6ES7 214-1AG40-0XB0', 5), '6ES7 214-1AG40-0XB0, 5');
  assert.equal(formatLine('A,B', 2), '"A,B", 2');
  assert.equal(formatLine('A"B', 2), '"A""B", 2');
  assert.equal(formatLine('  LC1D09 ', 0), 'LC1D09, 1');
  assert.equal(formatLine('X', 2.9), 'X, 2');
  assert.equal(formatLine('A\tB\n', 1), 'A B, 1');
});

test('rewriting lines keeps every other line where it is', () => {
  const text = 'a 1\nb 2\nc 3\r\nd 4';
  assert.equal(rewriteLines(text, [2], 'B, 9'), 'a 1\nB, 9\nc 3\nd 4');
  // merged duplicates: the first line takes the new text, the others are blanked (numbers stay stable)
  assert.equal(rewriteLines(text, [1, 3], 'Z, 4'), 'Z, 4\nb 2\n\nd 4');
  assert.equal(rewriteLines(text, [9], 'nope'), 'a 1\nb 2\nc 3\nd 4');
  assert.equal(rewriteLines('only', [1], 'x'), 'x');
});

test('fixing a part replaces the part and keeps the requested quantity', () => {
  const l = line({ lineNumbers: [2], requestedQuantity: 25, part: 'LC1DO9' });
  assert.equal(fixPart('a 1\nLC1DO9 25\nc 3', l, 'LC1D09'), 'a 1\nLC1D09, 25\nc 3');
  assert.equal(fixPart('a 1\nLC1DO9 25\nc 3', l, 'LC1D09', 4), 'a 1\nLC1D09, 4\nc 3');
});

test('fixing a quantity keeps the part as typed', () => {
  const l = line({ lineNumbers: [1, 2], part: '6ES7 214-1AG40-0XB0' });
  assert.equal(fixQuantity('6ES7 214-1AG40-0XB0 5\n6es7214-1ag40-0xb0 2', l, 12), '6ES7 214-1AG40-0XB0, 12\n');
});

test('variantPartText prefers the part number, then the SKU', () => {
  assert.equal(variantPartText({ sku: 'S', partNumber: 'P' }), 'P');
  assert.equal(variantPartText({ sku: 'S', partNumber: '  ' }), 'S');
  assert.equal(variantPartText({ sku: 'S', partNumber: null }), 'S');
});

test('max-available fix only for insufficient stock with something left', () => {
  assert.equal(maxAvailableFix({ status: 'INSUFFICIENT_STOCK', stock: { available: 7, status: 'in_stock' } }), 7);
  assert.equal(maxAvailableFix({ status: 'INSUFFICIENT_STOCK', stock: { available: 0, status: 'out_of_stock' } }), null);
  assert.equal(maxAvailableFix({ status: 'OK', stock: { available: 7, status: 'in_stock' } }), null);
  assert.equal(maxAvailableFix({ status: 'INSUFFICIENT_STOCK', stock: null }), null);
});

test('the cart batch holds valid lines only, with the server quantity', () => {
  const batch = buildCartBatch([
    line({ variant: variant('v1', 'S1', 'P1'), quantity: 10, lineNumbers: [1] }),
    line({ valid: false, status: 'OUT_OF_STOCK', variant: variant('v2', 'S2'), quantity: 5 }),
    line({ variant: variant('v3', 'S3'), quantity: 0 }),
    line({ variant: null }),
    line({ variant: variant('v4', 'S4'), quantity: 3, lineNumbers: [5, 7] })
  ]);
  assert.deepEqual(batch, [
    { variantId: 'v1', quantity: 10, lineNumbers: [1], label: 'P1' },
    { variantId: 'v4', quantity: 3, lineNumbers: [5, 7], label: 'S4' }
  ]);
});

test('errors are classified for the buyer', () => {
  assert.equal(classifyQuickOrderError({ status: 413, error: { code: 'INPUT_TOO_LARGE' } }), 'tooLarge');
  assert.equal(classifyQuickOrderError({ status: 422, error: { code: 'TOO_MANY_LINES' } }), 'tooManyLines');
  assert.equal(classifyQuickOrderError({ status: 422, error: { code: 'INPUT_INVALID' } }), 'invalid');
  assert.equal(classifyQuickOrderError({ status: 429 }), 'rateLimited');
  assert.equal(classifyQuickOrderError({ status: 503 }), 'unavailable');
  assert.equal(classifyQuickOrderError(new TypeError('Failed to fetch')), 'unavailable');
  assert.equal(classifyQuickOrderError({ status: 400 }), 'other');
});

test('add summary counts successes and failures', () => {
  const item = { variantId: 'v', quantity: 1, lineNumbers: [1], label: 'x' };
  assert.deepEqual(addSummary([{ item, ok: true }, { item, ok: false, reason: 'insufficientStock' }, { item, ok: true }]), { added: 2, failed: 1 });
  assert.deepEqual(addSummary([]), { added: 0, failed: 0 });
});

test('the api posts only the text and unwraps the data', async () => {
  const calls: Array<{ path: string; init: RequestInit }> = [];
  const client = {
    fetch: async (path: string, init: RequestInit) => {
      calls.push({ path, init });
      return { data: { lines: [], errors: [], notices: [], summary: { lines: 0, valid: 0, needsAttention: 0, subtotalMinor: 0, currency: 'BDT' } } };
    }
  } as unknown as MechWareApiClient;
  const out = await createQuickOrderApi(client).validate('LC1D09 5');
  assert.equal(out.summary.currency, 'BDT');
  assert.equal(calls[0]!.path, '/api/v1/quick-order/validate');
  assert.equal(calls[0]!.init.method, 'POST');
  assert.deepEqual(JSON.parse(calls[0]!.init.body as string), { text: 'LC1D09 5' });
});

test('removing lines drops exactly those physical lines', () => {
  assert.equal(removeLines('a 1\nb 2\nc 3\nd 4', [2, 4]), 'a 1\nc 3');
  assert.equal(removeLines('a 1\nb 2', [1, 2]), '');
  assert.equal(removeLines('a 1\nb 2', []), 'a 1\nb 2');
});
