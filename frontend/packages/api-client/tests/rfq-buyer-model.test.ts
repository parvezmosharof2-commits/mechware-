import test from 'node:test';
import assert from 'node:assert/strict';
import {
  buildRfqBody, canWithdrawRfq, classifyRfqError, isRfqDay, rfqKeepKeyAfter, parseRequestQuery, quoteAccess, quoteCompanies, requestQuoteHref, rfqFingerprint,
  rfqInputProblem, rfqLineCounts, rfqLineErrors, rfqLineKey, rfqPageCount, rfqPageQuery, rfqStatusKey, rfqStatusTone, seesAllRfqs, variantTitle
} from '../src/rfq-buyer-model.ts';

const problem = (status: number, code?: string, details: Array<{ field?: string; message: string }> = []) => ({ status, error: code ? { code, message: 'm', details } : undefined });

test('buildRfqBody: only the members the source reads; blanks dropped', () => {
  assert.deepEqual(buildRfqBody({ source: 'cart', text: 'ignored', variantId: 'x', message: '  ' }), { source: 'cart' });
  assert.deepEqual(buildRfqBody({ source: 'text', text: 'a 1', message: ' hi ', neededBy: '2026-12-01', shipToSiteId: 's' }), { source: 'text', text: 'a 1', message: 'hi', neededBy: '2026-12-01', shipToSiteId: 's' });
  assert.deepEqual(buildRfqBody({ source: 'product', variantId: 'v', quantity: 3, text: 'no' }), { source: 'product', variantId: 'v', quantity: 3 });
});

test('rfqFingerprint: same details same text; any change differs; company case does not matter', () => {
  const a = rfqFingerprint('ABC', { source: 'text', text: 'x 1' });
  assert.equal(a, rfqFingerprint('abc', { source: 'text', text: 'x 1' }));
  assert.notEqual(a, rfqFingerprint('abc', { source: 'text', text: 'x 2' }));
  assert.notEqual(a, rfqFingerprint('abc', { source: 'text', text: 'x 1', message: 'm' }));
  assert.equal(rfqFingerprint('abc', { source: 'text', text: 'x 1', message: '   ' }), a);
});

test('rfqInputProblem', () => {
  assert.equal(rfqInputProblem({ source: 'text', text: '  ' }), 'textEmpty');
  assert.equal(rfqInputProblem({ source: 'csv', text: 'x'.repeat(100_001) }), 'textTooLarge');
  assert.equal(rfqInputProblem({ source: 'text', text: 'x'.repeat(100_000) }), null);
  assert.equal(rfqInputProblem({ source: 'product', quantity: 1 }), 'variantMissing');
  assert.equal(rfqInputProblem({ source: 'product', variantId: 'v', quantity: 0 }), 'quantityInvalid');
  assert.equal(rfqInputProblem({ source: 'product', variantId: 'v', quantity: 1.5 }), 'quantityInvalid');
  assert.equal(rfqInputProblem({ source: 'product', variantId: 'v', quantity: 10_001 }), 'quantityTooLarge');
  assert.equal(rfqInputProblem({ source: 'product', variantId: 'v', quantity: 10_000 }), null);
  assert.equal(rfqInputProblem({ source: 'cart', message: 'x'.repeat(2001) }), 'messageTooLong');
  assert.equal(rfqInputProblem({ source: 'cart', neededBy: '2026-02-30' }), 'neededByInvalid');
  assert.equal(rfqInputProblem({ source: 'cart', neededBy: '2026-02-28' }), null);
});

test('isRfqDay', () => {
  assert.equal(isRfqDay('2028-02-29'), true);
  assert.equal(isRfqDay('2027-02-29'), false);
  assert.equal(isRfqDay('1/2/2026'), false);
});

test('classifyRfqError: every documented code, then the status', () => {
  const codes: Record<string, string> = {
    COMPANY_NOT_VERIFIED: 'companyNotVerified', COMPANY_SUSPENDED: 'companySuspended', ACCOUNT_NOT_VERIFIED: 'accountNotVerified',
    RFQ_LINES_INVALID: 'linesInvalid', QTY_EXCEEDS_LIMIT: 'qtyExceeds', INPUT_TOO_LARGE: 'tooLarge', RFQ_RATE_LIMITED: 'rateLimited',
    TOO_MANY_OPEN_RFQS: 'tooManyOpen', IDEMPOTENCY_KEY_REUSED: 'keyReused', RFQ_INVALID_TRANSITION: 'closed'
  };
  for (const [code, kind] of Object.entries(codes)) assert.equal(classifyRfqError(problem(409, code)), kind, code);
  assert.equal(classifyRfqError(problem(404)), 'notFound');
  assert.equal(classifyRfqError(problem(404, 'NOT_FOUND')), 'notFound');
  assert.equal(classifyRfqError(problem(401)), 'signIn');
  assert.equal(classifyRfqError(problem(413)), 'tooLarge');
  assert.equal(classifyRfqError(problem(429)), 'rateLimited');
  assert.equal(classifyRfqError(problem(422)), 'invalid');
  assert.equal(classifyRfqError(problem(500)), 'unavailable');
  assert.equal(classifyRfqError(new TypeError('network')), 'unavailable');
  assert.equal(classifyRfqError(null), 'unavailable');
});

test('rfqKeepKeyAfter: only when the outcome is unknown', () => {
  assert.equal(rfqKeepKeyAfter('unavailable'), true);
  assert.equal(rfqKeepKeyAfter('rateLimited'), true);
  assert.equal(rfqKeepKeyAfter('linesInvalid'), false);
  assert.equal(rfqKeepKeyAfter('keyReused'), false);
  assert.equal(rfqKeepKeyAfter('companyNotVerified'), false);
});

test('rfqLineErrors: lines[n] parsed, split into code and message, sorted; other fields ignored', () => {
  const e = problem(422, 'RFQ_LINES_INVALID', [
    { field: 'lines[7]', message: 'QUANTITY_INVALID: Quantity must be a whole number.' },
    { field: 'lines[2]', message: 'plain words' },
    { field: 'lines', message: 'Give at least one line.' },
    { field: 'neededBy', message: 'x' }
  ]);
  assert.deepEqual(rfqLineErrors(e), [
    { lineNo: 2, code: null, message: 'plain words' },
    { lineNo: 7, code: 'QUANTITY_INVALID', message: 'Quantity must be a whole number.' }
  ]);
  assert.deepEqual(rfqLineErrors(null), []);
  assert.deepEqual(rfqLineErrors({ error: { details: 'x' } }), []);
});

test('quoteAccess: active first, rejected never, none for a personal account', () => {
  const m = (id: string, status: string) => ({ company: { id, status, legalName: id }, roles: 1 });
  assert.deepEqual(quoteCompanies([m('p', 'pending'), m('r', 'rejected'), m('a', 'active')]).map((x) => x.company.id), ['a', 'p']);
  assert.deepEqual(quoteAccess([m('p', 'pending'), m('s', 'suspended')]), { kind: 'ready', companyId: 'p', companyName: 'p', status: 'pending' });
  assert.deepEqual(quoteAccess([m('a', 'active'), m('b', 'active')], 'b'), { kind: 'ready', companyId: 'b', companyName: 'b', status: 'active' });
  assert.deepEqual(quoteAccess([]), { kind: 'none' });
  assert.deepEqual(quoteAccess([m('r', 'rejected')]), { kind: 'none' });
});

test('seesAllRfqs: Admin and Buyer only', () => {
  assert.equal(seesAllRfqs(1), false);
  assert.equal(seesAllRfqs(4), false);
  assert.equal(seesAllRfqs(2), true);
  assert.equal(seesAllRfqs(8), true);
  assert.equal(seesAllRfqs(1 | 4), false);
});

test('statuses, withdraw, line kinds and counts', () => {
  assert.equal(rfqStatusKey('Submitted'), 'submitted');
  assert.equal(rfqStatusKey('InReview'), 'inreview');
  assert.equal(rfqStatusKey('Weird'), 'other');
  assert.equal(rfqStatusTone('Quoted'), 'ok');
  assert.equal(rfqStatusTone('Closed'), 'muted');
  assert.equal(canWithdrawRfq({ status: 'Submitted', withdrawnAt: null, closedAt: null }), true);
  assert.equal(canWithdrawRfq({ status: 'Closed', withdrawnAt: null, closedAt: null }), false);
  assert.equal(canWithdrawRfq({ status: 'Submitted', withdrawnAt: 'x', closedAt: null }), false);
  assert.equal(rfqLineKey('not_found'), 'notFound');
  assert.equal(rfqLineKey('zzz'), 'other');
  assert.deepEqual(rfqLineCounts([{ matchStatus: 'matched' }, { matchStatus: 'obsolete' }, { matchStatus: 'not_found' }, { matchStatus: 'ambiguous' }]), { matched: 1, obsolete: 1, unmatched: 2 });
});

test('variantTitle follows the language and falls back on English', () => {
  const v = { title: 'Relay', titleBn: 'রিলে' };
  assert.equal(variantTitle(v, 'bn'), 'রিলে');
  assert.equal(variantTitle({ title: 'Relay', titleBn: '' }, 'bn'), 'Relay');
  assert.equal(variantTitle(v, 'en'), 'Relay');
  assert.equal(variantTitle(null, 'en'), '');
});

test('paging and links', () => {
  assert.equal(rfqPageQuery({ page: 0, pageSize: 500 }), 'page=1&pageSize=50');
  assert.equal(rfqPageQuery({ status: 'Quoted', page: 2 }), 'status=Quoted&page=2&pageSize=20');
  assert.equal(rfqPageCount(0, 20), 1);
  assert.equal(rfqPageCount(41, 20), 3);
  assert.equal(requestQuoteHref('cart'), '/request-quote?source=cart');
  assert.equal(requestQuoteHref('product', { variantId: 'abc', quantity: 4.9 }), '/request-quote?source=product&variantId=abc&quantity=4');
});

test('parseRequestQuery: unknown source -> text; product needs a valid variant; quantity bounded', () => {
  const id = '3f2b8c1e-0a4d-4e5f-9b6a-1c2d3e4f5a6b';
  assert.deepEqual(parseRequestQuery({}), { source: 'text', variantId: '', quantity: 1, label: '' });
  assert.deepEqual(parseRequestQuery({ source: 'cart' }), { source: 'cart', variantId: '', quantity: 1, label: '' });
  assert.deepEqual(parseRequestQuery({ source: 'product', variantId: id, quantity: '12', label: 'Relay' }), { source: 'product', variantId: id, quantity: 12, label: 'Relay' });
  assert.equal(parseRequestQuery({ source: 'product', variantId: 'nope' }).source, 'text');
  assert.equal(parseRequestQuery({ source: 'product', variantId: id, quantity: '0' }).quantity, 1);
  assert.equal(parseRequestQuery({ source: 'product', variantId: id, quantity: '10001' }).quantity, 1);
  assert.equal(parseRequestQuery({ source: 'csv' }).source, 'text');
});
