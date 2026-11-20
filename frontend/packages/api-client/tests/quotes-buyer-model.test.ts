import test from 'node:test';
import assert from 'node:assert/strict';
import {
  acceptBody, acceptFingerprint, canRejectVersion, classifyQuoteError, lineTitle, mayAccept, newestAcceptable, quotePaymentChoices, quotedTotal, previewBody, quoteKeepKeyAfter,
  quoteNeedsReload, quotePageCount, quotePageQuery, quotePdfPath, quoteStatusKey, quoteStatusTone, rejectBody
} from '../src/quotes-buyer-model.ts';

const problem = (status: number, code?: string) => ({ status, error: code ? { code, message: 'm' } : undefined });
const choice = { versionNo: 2, contentHash: 'abc', payOnCredit: true, shipToSiteId: 's1', costCentreId: '', purchaseOrderNumber: ' PO-1 ' };

test('previewBody and acceptBody carry only the allowed members', () => {
  assert.deepEqual(previewBody(choice), { versionNo: 2, contentHash: 'abc', payOnCredit: true, shipToSiteId: 's1' });
  assert.deepEqual(acceptBody(choice, 123400), { versionNo: 2, contentHash: 'abc', payOnCredit: true, shipToSiteId: 's1', expectedGrandTotalMinor: 123400, purchaseOrderNumber: 'PO-1' });
  const bare = acceptBody({ versionNo: 1, contentHash: 'h', payOnCredit: false }, 5);
  assert.deepEqual(bare, { versionNo: 1, contentHash: 'h', payOnCredit: false, expectedGrandTotalMinor: 5 });
});

test('acceptFingerprint: same choice same text; another total, hash or payment differs', () => {
  const a = acceptFingerprint('C', 'Q', choice, 100);
  assert.equal(a, acceptFingerprint('c', 'q', choice, 100));
  assert.notEqual(a, acceptFingerprint('c', 'q', choice, 101));
  assert.notEqual(a, acceptFingerprint('c', 'q', { ...choice, payOnCredit: false }, 100));
  assert.notEqual(a, acceptFingerprint('c', 'q', { ...choice, contentHash: 'x' }, 100));
});

test('rejectBody: reason trimmed, left out when blank', () => {
  assert.deepEqual(rejectBody(3, '  too dear '), { versionNo: 3, reason: 'too dear' });
  assert.deepEqual(rejectBody(3, '   '), { versionNo: 3 });
});

test('statuses in words and tones', () => {
  assert.equal(quoteStatusKey('Sent'), 'sent');
  assert.equal(quoteStatusKey('superseded'), 'superseded');
  assert.equal(quoteStatusKey('draft'), 'other');
  assert.equal(quoteStatusTone('accepted'), 'ok');
  assert.equal(quoteStatusTone('expired'), 'warn');
  assert.equal(quoteStatusTone('withdrawn'), 'muted');
});

test('acceptability: only the newest version, only when the server says so; Buyer role only', () => {
  assert.equal(newestAcceptable({ versions: [] }), null);
  const v = (n: number, acceptable: boolean) => ({ versionNo: n, acceptable }) as never;
  assert.equal(newestAcceptable({ versions: [v(1, true), v(2, false)] }), null);
  assert.equal(newestAcceptable({ versions: [v(1, false), v(2, true)] })?.versionNo, 2);
  assert.equal(mayAccept(1), false);
  assert.equal(mayAccept(2), true);
  assert.equal(mayAccept(10), true);
  assert.equal(mayAccept(8), false);
  assert.equal(canRejectVersion({ status: 'sent' }), true);
  assert.equal(canRejectVersion({ status: 'accepted' }), false);
});

test('quotePaymentChoices: advance quotes cannot go on credit', () => {
  assert.deepEqual(quotePaymentChoices('advance'), { onAccount: false, advance: true });
  assert.deepEqual(quotePaymentChoices('as_account'), { onAccount: true, advance: true });
});

test('classifyQuoteError: every documented code, then the HTTP status', () => {
  const cases: Array<[string, string]> = [
    ['PRICE_CHANGED', 'priceChanged'], ['QUOTE_NOT_ACCEPTABLE', 'notAcceptable'], ['QUOTE_ALREADY_ACCEPTED', 'alreadyAccepted'], ['QUOTE_CHANGED', 'changed'],
    ['QUOTE_REQUIRES_ADVANCE_PAYMENT', 'requiresAdvance'], ['QUOTE_DELIVERY_FIXED', 'deliveryFixed'], ['QUOTE_DELIVERY_SITE_GONE', 'deliverySiteGone'],
    ['VARIANT_UNAVAILABLE', 'variantUnavailable'], ['INSUFFICIENT_STOCK', 'insufficientStock'], ['CREDIT_HOLD_OVERDUE', 'creditHold'],
    ['CREDIT_LIMIT_EXCEEDED', 'creditLimit'], ['NO_CREDIT_TERMS', 'noCreditTerms'], ['COMPANY_SUSPENDED', 'companySuspended'], ['TOO_MANY_UNPAID_ORDERS', 'tooManyUnpaid'],
    ['SPEND_LIMIT_EXCEEDED', 'spendLimit'], ['BUDGET_EXCEEDED', 'budget'], ['COST_CENTRE_NOT_ASSIGNED', 'costCentre'], ['COMPANY_ROLE_REQUIRED', 'roleRequired'],
    ['IDEMPOTENCY_KEY_REUSED', 'keyReused'], ['QUOTE_INVALID_TRANSITION', 'closed'], ['QUOTE_CLOSED', 'closed']
  ];
  for (const [code, kind] of cases) assert.equal(classifyQuoteError(problem(409, code)), kind, code);
  assert.equal(classifyQuoteError(problem(404, 'NOT_FOUND')), 'notFound');
  assert.equal(classifyQuoteError(problem(403)), 'roleRequired');
  assert.equal(classifyQuoteError(problem(401)), 'signIn');
  assert.equal(classifyQuoteError(problem(429)), 'rateLimited');
  assert.equal(classifyQuoteError(problem(400)), 'invalid');
  assert.equal(classifyQuoteError(problem(503)), 'unavailable');
  assert.equal(classifyQuoteError(new TypeError('network')), 'unavailable');
  assert.equal(classifyQuoteError(null), 'unavailable');
});

test('key reuse and reload rules', () => {
  assert.equal(quoteKeepKeyAfter('unavailable'), true);
  assert.equal(quoteKeepKeyAfter('busy'), true);
  assert.equal(quoteKeepKeyAfter('priceChanged'), false);
  assert.equal(quoteKeepKeyAfter('keyReused'), false);
  assert.equal(quoteNeedsReload('priceChanged'), true);
  assert.equal(quoteNeedsReload('creditHold'), false);
});

test('quotedTotal: the goods total while delivery is worked out at the order', () => {
  assert.deepEqual(quotedTotal({ grandTotalMinor: null, goodsTotalMinor: 100 }), { minor: 100, withDelivery: false });
  assert.deepEqual(quotedTotal({ grandTotalMinor: 150, goodsTotalMinor: 100 }), { minor: 150, withDelivery: true });
});

test('paging, titles and the pdf path', () => {
  assert.equal(quotePageQuery({ status: 'sent', page: 0, pageSize: 999 }), 'status=sent&page=1&pageSize=50');
  assert.equal(quotePageCount(41, 20), 3);
  assert.equal(quotePageCount(0, 20), 1);
  assert.equal(lineTitle({ titleEn: 'Drill', titleBn: 'ড্রিল' }, 'bn'), 'ড্রিল');
  assert.equal(lineTitle({ titleEn: 'Drill', titleBn: '' }, 'bn'), 'Drill');
  assert.equal(quotePdfPath('c 1', 'q', 2.9), '/api/v1/companies/c%201/quotes/q/versions/2/pdf?locale=en');
  assert.ok(quotePdfPath('c', 'q', 1, 'bn').endsWith('/pdf?locale=bn'));
});
