import test from 'node:test';
import assert from 'node:assert/strict';
import {
  AGING_BUCKETS, adjustmentFormProblems, adjustmentReasonProblem, adjustmentSummary, adjustmentsQuery, agingCsvName, agingQuery, applyProblems, applyResultText,
  approveAdjustmentHint, approvedText, bodyAttemptFor, buildAdjustmentBody, buildApplyBody, buildHoldBody, canAdjustCredit, canApproveAdjustment, canOverrideHold,
  emptyAdjustmentForm, emptyApplyForm, holdFormProblems, holdResultText, holdText, ledgerKindText, orderChoices, paymentChoices, rejectAdjustmentHint, signedBdt,
  signedBdtToPoisha, statementCsvName, statementCsvQuery, statementQuery, statementRangeProblem, type AdjustmentForm, type CreditAdjustment
} from '../src/credit-books-model.ts';
import { creditErrorText, shouldReloadCredit } from '../src/credit-payments-model.ts';

const adj = (o: Partial<CreditAdjustment> = {}): CreditAdjustment => ({
  id: 'a1', companyId: 'c1', kind: 'write_off', amountMinor: 100000, currency: 'BDT', orderId: 'o1', paymentId: null, creditNoteId: null, payoutReference: null,
  reason: 'Agreed discount with the buyer', status: 'requested', requestedBy: 'finance-a', requestedAtUtc: '2026-11-17T05:00:00Z', approvedBy: null, approvedAtUtc: null,
  rejectedBy: null, rejectedAtUtc: null, decisionReason: null, ...o
});
const form = (o: Partial<AdjustmentForm> = {}): AdjustmentForm => ({ ...emptyAdjustmentForm(), amountBdt: '1000', reason: 'Agreed discount with the buyer', orderId: 'o1', ...o });
const admin = { id: 'admin', permissions: ['credit.view', 'credit.adjust', 'credit.adjust_approve', 'credit.hold_override'] };
const finance = { id: 'finance-a', permissions: ['credit.view', 'credit.adjust'] };

test('permissions: only the named permission opens each action', () => {
  assert.equal(canAdjustCredit(finance.permissions), true);
  assert.equal(canApproveAdjustment(finance.permissions), false);
  assert.equal(canOverrideHold(finance.permissions), false);
  assert.equal(canApproveAdjustment(['CREDIT.ADJUST_APPROVE']), true);
  assert.equal(canOverrideHold(admin.permissions), true);
});

test('aging: query carries only what is set, buckets are the five server fields', () => {
  assert.equal(agingQuery({ q: '  mills ', overdueOnly: true, page: 2 }), 'q=mills&overdueOnly=true&page=2&pageSize=25');
  assert.equal(agingQuery({ q: '', overdueOnly: false, page: 1 }), 'page=1&pageSize=25');
  assert.equal(agingQuery({ q: '', overdueOnly: false, page: 1, companyId: 'c1' }), 'companyId=c1&page=1&pageSize=25');
  assert.deepEqual(AGING_BUCKETS.map((b) => b.key), ['current', 'days1To30', 'days31To60', 'days61To90', 'days90Plus']);
  assert.equal(agingCsvName(true, '2026-11-17'), 'credit-aging-overdue-2026-11-17.csv');
  assert.equal(agingCsvName(false, '2026-11-17'), 'credit-aging-2026-11-17.csv');
});

test('hold text: on hold, a running lift, an ended lift', () => {
  const now = Date.parse('2026-11-17T00:00:00Z');
  assert.match(holdText(true, null, now), /On hold/);
  assert.match(holdText(false, '2026-11-20T10:30:00Z', now), /lifted until 2026-11-20 10:30 UTC/);
  assert.equal(holdText(false, '2026-11-10T10:30:00Z', now), 'Not on hold');
  assert.equal(holdText(false, null, now), 'Not on hold');
});

test('statement: range checks and queries', () => {
  assert.equal(statementRangeProblem('', ''), null);
  assert.equal(statementRangeProblem('2026-11-01', '2026-11-17'), null);
  assert.match(statementRangeProblem('2026-11-18', '2026-11-17') ?? '', /must not be after/);
  assert.match(statementRangeProblem('2026-13-01', '') ?? '', /first day/);
  assert.match(statementRangeProblem('', 'x') ?? '', /last day/);
  assert.equal(statementQuery({ from: '2026-11-01', to: '', page: 3 }), 'from=2026-11-01&page=3&pageSize=50');
  assert.equal(statementCsvQuery({ from: '', to: '' }), '');
  assert.equal(statementCsvQuery({ from: '2026-11-01', to: '2026-11-17' }), 'from=2026-11-01&to=2026-11-17');
  assert.equal(statementCsvName('Credit Test Mills 123 Ltd.', '2026-11-17'), 'credit-statement-credit-test-mills-123-ltd-2026-11-17.csv');
  assert.equal(signedBdt(0), '0.00');
  assert.equal(signedBdt(2500050), '+25,000.50');
  assert.equal(signedBdt(-5), '-0.05');
  assert.equal(ledgerKindText('on_account_payout'), 'On-account paid out');
  assert.equal(ledgerKindText('something_new'), 'something new');
});

test('choices: each order and payment once, in first-seen order', () => {
  const lines = [
    { orderId: 'o1', orderNumber: 'ORD-1', receivableDeltaMinor: 100000, paymentId: null, paymentMethod: null, paymentReference: null },
    { orderId: 'o1', orderNumber: 'ORD-1', receivableDeltaMinor: -40000, paymentId: 'p1', paymentMethod: 'cheque', paymentReference: 'CHQ1' },
    { orderId: null, orderNumber: null, receivableDeltaMinor: 0, paymentId: 'p1', paymentMethod: 'cheque', paymentReference: 'CHQ1' },
    { orderId: 'o2', orderNumber: null, receivableDeltaMinor: 5000, paymentId: 'p2', paymentMethod: 'beftn', paymentReference: null }
  ];
  assert.deepEqual(orderChoices(lines), [{ orderId: 'o1', orderNumber: 'ORD-1', owedMinor: 60000 }, { orderId: 'o2', orderNumber: 'o2', owedMinor: 5000 }]);
  assert.deepEqual(paymentChoices(lines).map((p) => p.label), ['cheque CHQ1', 'beftn p2']);
});

test('apply: empty form is valid (everything that fits); amounts by string rules; named orders once', () => {
  assert.deepEqual(applyProblems(emptyApplyForm(), 500), {});
  assert.deepEqual(buildApplyBody(emptyApplyForm()), {});
  assert.deepEqual(buildApplyBody({ amountBdt: '5,000.50', allocations: [{ orderId: 'o1', amountBdt: '100' }] }), { amountMinor: 500050, allocations: [{ orderId: 'o1', amountMinor: 10000 }] });
  assert.ok(applyProblems({ amountBdt: '1.234', allocations: [] }, null).amountBdt);
  assert.ok(applyProblems({ amountBdt: '0', allocations: [] }, null).amountBdt);
  assert.match(applyProblems({ amountBdt: '10', allocations: [] }, 500).amountBdt ?? '', /only BDT 5\.00 on account/);
  assert.equal(applyProblems({ amountBdt: '10', allocations: [] }, 1000).amountBdt, undefined);
  const p = applyProblems({ amountBdt: '', allocations: [{ orderId: '', amountBdt: '1' }, { orderId: 'o1', amountBdt: '1' }, { orderId: 'o1', amountBdt: '1' }, { orderId: 'o2', amountBdt: '-1' }] }, null);
  assert.match(p.allocation0, /Choose the order/);
  assert.equal(p.allocation1, undefined);
  assert.match(p.allocation2, /once/);
  assert.match(p.allocation3, /Enter the amount/);
  const many = Array.from({ length: 51 }, (_, i) => ({ orderId: `o${i}`, amountBdt: '1' }));
  assert.ok(applyProblems({ amountBdt: '', allocations: many }, null).allocations);
});

test('apply: result text counts orders and handles a replay', () => {
  const r = { appliedMinor: 150000, applied: [{ orderId: 'o1', paymentId: 'p', creditNoteId: null, amountMinor: 100000 }, { orderId: 'o1', paymentId: 'q', creditNoteId: null, amountMinor: 50000 }], creditUsedMinor: 0, creditOnAccountMinor: 25000, replayed: false };
  assert.equal(applyResultText(r), 'BDT 1,500.00 applied to 1 order. Credit used is now BDT 0.00; on account BDT 250.00.');
  assert.match(applyResultText({ ...r, replayed: true }), /^This was already applied/);
});

test('idempotency: the same details keep the key, any change gets a new one', () => {
  let n = 0;
  const make = () => `key-${++n}`;
  const body = { amountMinor: 100 };
  const a = bodyAttemptFor(null, 'c1', body, make);
  assert.equal(bodyAttemptFor(a, 'c1', { amountMinor: 100 }, make).key, a.key);
  assert.notEqual(bodyAttemptFor(a, 'c1', { amountMinor: 101 }, make).key, a.key);
  assert.notEqual(bodyAttemptFor(a, 'c2', body, make).key, a.key);
});

test('signed bdt: a minus only in front, integer maths', () => {
  assert.equal(signedBdtToPoisha('-250'), -25000);
  assert.equal(signedBdtToPoisha('1,000.50'), 100050);
  assert.equal(signedBdtToPoisha('-0.05'), -5);
  for (const bad of ['', '--5', '5-', '+5', '-1.234', 'abc', '- 5']) assert.equal(signedBdtToPoisha(bad), null, bad);
});

test('adjustment form: each kind asks for its own boxes', () => {
  assert.deepEqual(adjustmentFormProblems(form()), {});
  assert.ok(adjustmentFormProblems(form({ orderId: '' })).orderId);
  assert.ok(adjustmentFormProblems(form({ amountBdt: '-5' })).amountBdt, 'a write-off is never negative');
  assert.ok(adjustmentFormProblems(form({ amountBdt: '0' })).amountBdt);
  assert.ok(adjustmentFormProblems(form({ amountBdt: '' })).amountBdt);
  assert.ok(adjustmentFormProblems(form({ reason: 'too short' })).reason);
  assert.equal(adjustmentFormProblems(form({ reason: '          ' })).reason, 'Enter a reason.');
  assert.equal(adjustmentReasonProblem('x'.repeat(501)), 'The reason must be 10 to 500 characters.');
  assert.equal(adjustmentReasonProblem('x'.repeat(10)), null);
  const corr = form({ kind: 'on_account_correction', amountBdt: '-250', orderId: '', paymentId: 'p1' });
  assert.deepEqual(adjustmentFormProblems(corr), {});
  assert.ok(adjustmentFormProblems({ ...corr, paymentId: '' }).paymentId);
  assert.ok(adjustmentFormProblems({ ...corr, sourceType: 'credit_note', creditNoteId: 'nope' }).creditNoteId);
  assert.deepEqual(adjustmentFormProblems({ ...corr, sourceType: 'credit_note', creditNoteId: '0b1c2d3e-0000-4000-8000-0123456789ab' }), {});
  const payout = form({ kind: 'on_account_payout', amountBdt: '500', orderId: '', paymentId: 'p1', payoutReference: 'TRF-9' });
  assert.deepEqual(adjustmentFormProblems(payout), {});
  assert.ok(adjustmentFormProblems({ ...payout, payoutReference: 'abc' }).payoutReference);
  assert.ok(adjustmentFormProblems({ ...payout, payoutReference: 'TRF-9981', amountBdt: '-5' }).amountBdt, 'a payout is never negative');
});

test('adjustment body: only the boxes the kind takes', () => {
  assert.deepEqual(buildAdjustmentBody(form()), { kind: 'write_off', amountMinor: 100000, reason: 'Agreed discount with the buyer', orderId: 'o1' });
  const corr = form({ kind: 'on_account_correction', amountBdt: '-250', paymentId: 'p1', orderId: 'stale', payoutReference: 'stale' });
  assert.deepEqual(buildAdjustmentBody(corr), { kind: 'on_account_correction', amountMinor: -25000, reason: 'Agreed discount with the buyer', paymentId: 'p1' });
  const note = form({ kind: 'on_account_payout', amountBdt: '10', sourceType: 'credit_note', creditNoteId: ' 0b1c2d3e-0000-4000-8000-0123456789ab ', paymentId: 'stale', payoutReference: ' TRF-9981 ' });
  assert.deepEqual(buildAdjustmentBody(note), { kind: 'on_account_payout', amountMinor: 1000, reason: 'Agreed discount with the buyer', creditNoteId: '0b1c2d3e-0000-4000-8000-0123456789ab', payoutReference: 'TRF-9981' });
});

test('approve hint: Admin only, requested only, never the requester', () => {
  assert.equal(approveAdjustmentHint(adj(), admin).allowed, true);
  assert.match(approveAdjustmentHint(adj(), finance).reason, /Only an Admin/);
  assert.match(approveAdjustmentHint(adj({ requestedBy: 'ADMIN' }), admin).reason, /You requested this adjustment/);
  assert.match(approveAdjustmentHint(adj({ status: 'approved' }), admin).reason, /already approved/);
  assert.match(approveAdjustmentHint(adj({ status: 'rejected' }), admin).reason, /already rejected/);
});

test('reject hint: anyone with credit.adjust; the requester is withdrawing', () => {
  const h = rejectAdjustmentHint(adj(), finance);
  assert.deepEqual([h.allowed, h.withdraw], [true, true]);
  const o = rejectAdjustmentHint(adj(), admin);
  assert.deepEqual([o.allowed, o.withdraw], [true, false]);
  assert.equal(rejectAdjustmentHint(adj(), { id: 'x', permissions: ['credit.view'] }).allowed, false);
  assert.equal(rejectAdjustmentHint(adj({ status: 'approved' }), admin).allowed, false);
});

test('adjustment words and queue query', () => {
  assert.equal(adjustmentSummary(adj()), 'Write-off of BDT 1,000.00');
  assert.equal(adjustmentSummary(adj({ kind: 'on_account_correction', amountMinor: -25000 })), 'On-account correction of -BDT 250.00');
  assert.match(approvedText({ receivableDeltaMinor: -100000, onAccountDeltaMinor: 0, creditUsedMinor: 0, creditOnAccountMinor: 5000 }), /Owed -1,000\.00, on account 0\.00/);
  assert.equal(adjustmentsQuery({ status: 'requested', companyId: 'c1' }), 'status=requested&companyId=c1&page=1&pageSize=25');
});

test('hold form: whole days 0 to 7 and a reason, checked before a code', () => {
  assert.deepEqual(holdFormProblems({ days: '3', reason: 'Payment promised on Sunday' }), {});
  assert.deepEqual(holdFormProblems({ days: '0', reason: 'Ending early' }), {});
  assert.deepEqual(holdFormProblems({ days: '7', reason: 'Seven days' }), {});
  for (const d of ['', '8', '-1', '1.5', 'x', '07x', '100']) assert.ok(holdFormProblems({ days: d, reason: 'okay reason' }).days, d);
  assert.ok(holdFormProblems({ days: '1', reason: '' }).reason);
  assert.ok(holdFormProblems({ days: '1', reason: 'abcd' }).reason);
  assert.deepEqual(buildHoldBody({ days: ' 3 ', reason: ' why ' }), { days: 3, reason: 'why' });
  const held = { companyId: 'c', overdueSince: '2026-10-01', onHold: false, overrideUntilUtc: '2026-11-20T10:30:00Z', holdNoticedAtUtc: null };
  assert.match(holdResultText(held, 3), /lifted for 3 days, until 2026-11-20 10:30 UTC/);
  assert.match(holdResultText({ ...held, onHold: true }, 0), /on hold again/);
  assert.match(holdResultText({ ...held, onHold: false }, 0), /not on hold/);
});

test('errors: every new refusal code has plain words; only a decided adjustment reloads', () => {
  const codes = ['CREDIT_NOTHING_TO_APPLY', 'CREDIT_ON_ACCOUNT_INSUFFICIENT', 'CREDIT_ON_ACCOUNT_EXCEEDS_OPEN', 'ALLOCATION_ORDER_NOT_OPEN', 'ALLOCATION_EXCEEDS_OUTSTANDING',
    'CREDIT_ADJUSTMENT_FOUR_EYES', 'CREDIT_ADJUSTMENT_NOT_REQUESTED', 'CREDIT_ADJUSTMENT_TARGET_NOT_FOUND', 'CREDIT_EXCEEDS_RECEIVABLE', 'CREDIT_CORRECTION_EXCEEDS_TAKEN',
    'CREDIT_ADJUSTMENT_SOURCE_NOT_VERIFIED', 'CREDIT_PAYOUT_TOO_EARLY', 'IDEMPOTENCY_KEY_REUSED'];
  for (const code of codes) {
    const t = creditErrorText({ status: 409, code, message: 'raw server text', fields: {} });
    assert.ok(t.length > 20 && t !== 'raw server text', code);
  }
  assert.equal(shouldReloadCredit('CREDIT_ADJUSTMENT_NOT_REQUESTED'), true);
  assert.equal(shouldReloadCredit('CREDIT_ADJUSTMENT_FOUR_EYES'), false);
});
