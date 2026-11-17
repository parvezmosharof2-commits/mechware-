import test from 'node:test';
import assert from 'node:assert/strict';
import {
  addDays, bdtToPoisha, bounceHint, buildRecordBody, clearedOnProblem, creditErrorText, creditReasonProblem, dhakaToday, emptyRecordForm, flagsOf,
  formatBdt, isIsoDay, isLateEntry, methodFields, paymentsQuery, recordAttemptFor, recordFormProblems, rejectHint, shouldReloadCredit, sortQueue,
  verifyHint, type CreditPayment, type CreditPaymentListItem, type RecordPaymentForm
} from '../src/credit-payments-model.ts';

const TODAY = '2026-11-17';
const pay = (o: Partial<CreditPayment> = {}): CreditPayment => ({
  id: 'p1', companyId: 'c1', method: 'beftn', amountMinor: 2500000, currency: 'BDT', reference: 'BEFTN123', drawerBank: null, receivedOn: '2026-11-10', chequeDate: null,
  lateEntryReason: null, allocations: [], note: null, status: 'recorded', recordedBy: 'a', recordedAtUtc: '2026-11-10T05:00:00Z', verifiedBy: null, verifiedAtUtc: null,
  clearedOn: null, rejectedBy: null, rejectedAtUtc: null, decisionReason: null, bouncedBy: null, bouncedAtUtc: null, ...o
});
const item = (o: Partial<CreditPayment> = {}, flags: Partial<CreditPaymentListItem> = {}): CreditPaymentListItem => ({ payment: pay(o), companyName: 'X Ltd', lateEntry: false, postDated: false, ...flags });
const form = (o: Partial<RecordPaymentForm> = {}): RecordPaymentForm => ({ ...emptyRecordForm(TODAY), method: 'beftn', amountBdt: '25000', reference: 'BEFTN123', ...o });
const finance = { id: 'b', permissions: ['credit.view', 'credit.verify_payment', 'credit.record_payment'] };

test('bdt: converts by string rules, never a float', () => {
  assert.equal(bdtToPoisha('25000'), 2500000);
  assert.equal(bdtToPoisha('25,000.5'), 2500050);
  assert.equal(bdtToPoisha(' 0.07 '), 7);
  assert.equal(bdtToPoisha('19.99'), 1999);
  assert.equal(bdtToPoisha('1234567890.12'), 123456789012);
  for (const bad of ['', '1.234', '-5', '+5', '1e3', 'abc', '.5', '5.', '1 000']) assert.equal(bdtToPoisha(bad), null, bad);
  assert.equal(formatBdt(2500050), '25,000.50');
  assert.equal(formatBdt(5), '0.05');
  assert.equal(formatBdt(-150), '-1.50');
});

test('dates: Dhaka today moves at 18:00 UTC, add days, valid ISO days', () => {
  assert.equal(dhakaToday(Date.parse('2026-11-17T17:59:00Z')), '2026-11-17');
  assert.equal(dhakaToday(Date.parse('2026-11-17T18:00:00Z')), '2026-11-18');
  assert.equal(addDays('2026-12-31', 1), '2027-01-01');
  assert.ok(isIsoDay('2028-02-29'));
  assert.ok(!isIsoDay('2027-02-29') && !isIsoDay('2026-1-1') && !isIsoDay(''));
});

test('late entry starts after 30 days, not at 30', () => {
  assert.equal(isLateEntry(addDays(TODAY, -30), TODAY), false);
  assert.equal(isLateEntry(addDays(TODAY, -31), TODAY), true);
});

test('form: a good transfer has no problems; the body leaves out boxes the method does not take', () => {
  assert.deepEqual(recordFormProblems(form(), TODAY), {});
  assert.deepEqual(buildRecordBody(form({ drawerBank: 'ignored', chequeDate: '2026-11-01', note: ' hi ' })), {
    method: 'beftn', amountMinor: 2500000, receivedOn: TODAY, reference: 'BEFTN123', note: 'hi'
  });
});

test('form: amount, future day, short reference, cheque needs date and bank', () => {
  assert.ok(recordFormProblems(form({ amountBdt: '' }), TODAY).amountBdt);
  assert.match(recordFormProblems(form({ amountBdt: '1.234' }), TODAY).amountBdt, /2 decimals/);
  assert.ok(recordFormProblems(form({ amountBdt: '0' }), TODAY).amountBdt);
  assert.match(recordFormProblems(form({ receivedOn: '2026-11-18' }), TODAY).receivedOn, /future/);
  assert.ok(recordFormProblems(form({ reference: 'abc' }), TODAY).reference);
  const cheque = recordFormProblems(form({ method: 'cheque', reference: '000123' }), TODAY);
  assert.ok(cheque.chequeDate && cheque.drawerBank);
  assert.deepEqual(recordFormProblems(form({ method: 'cheque', reference: '000123', chequeDate: TODAY, drawerBank: 'Sonali Bank' }), TODAY), {});
  assert.match(recordFormProblems(form({ method: 'cheque', reference: '000123', chequeDate: '2027-06-01', drawerBank: 'Sonali' }), TODAY).chequeDate, /180 days/);
});

test('form: cash has no reference box; a late payment needs its reason, a recent one must not have one', () => {
  assert.equal(methodFields('cash').reference, false);
  assert.deepEqual(recordFormProblems(form({ method: 'cash', reference: '' }), TODAY), {});
  assert.equal(buildRecordBody(form({ method: 'cash', reference: 'x' })).reference, undefined);
  const late = form({ receivedOn: '2026-10-01' });
  assert.ok(recordFormProblems(late, TODAY).lateEntryReason);
  assert.deepEqual(recordFormProblems({ ...late, lateEntryReason: 'Bank slip arrived late' }, TODAY), {});
  assert.ok(recordFormProblems(form({ lateEntryReason: 'not needed' }), TODAY).lateEntryReason);
});

test('idempotency: the same body keeps its key, any change gets a new one', () => {
  let n = 0;
  const make = () => `key-${++n}-padding`;
  const a = recordAttemptFor(null, 'c1', buildRecordBody(form()), make);
  const again = recordAttemptFor(a, 'c1', buildRecordBody(form()), make);
  assert.equal(again.key, a.key);
  assert.notEqual(recordAttemptFor(a, 'c1', buildRecordBody(form({ amountBdt: '25001' })), make).key, a.key);
  assert.notEqual(recordAttemptFor(a, 'c2', buildRecordBody(form()), make).key, a.key);
});

test('reasons: 5 to 500 characters after trimming', () => {
  assert.ok(creditReasonProblem(''));
  assert.ok(creditReasonProblem('   abc  '));
  assert.equal(creditReasonProblem('Wrong cheque'), null);
  assert.ok(creditReasonProblem('x'.repeat(501)));
  assert.equal(creditReasonProblem('x'.repeat(500)), null);
});

test('clearedOn: from the later of received day and cheque date up to today', () => {
  const p = { receivedOn: '2026-11-10', chequeDate: '2026-11-12' };
  assert.ok(clearedOnProblem(p, '2026-11-11', TODAY));
  assert.equal(clearedOnProblem(p, '2026-11-12', TODAY), null);
  assert.equal(clearedOnProblem(p, TODAY, TODAY), null);
  assert.ok(clearedOnProblem(p, '2026-11-18', TODAY));
  assert.ok(clearedOnProblem(p, '', TODAY));
  assert.equal(clearedOnProblem({ receivedOn: '2026-11-10', chequeDate: null }, '2026-11-10', TODAY), null);
});

test('verify hint: needs permission, a recorded payment, another person, and a cheque that is due', () => {
  assert.equal(verifyHint(item(), finance, TODAY).allowed, true);
  assert.match(verifyHint(item(), { id: 'a', permissions: finance.permissions }, TODAY).reason, /recorded this payment/);
  assert.match(verifyHint(item(), { id: 'b', permissions: ['credit.view'] }, TODAY).reason, /may not verify/);
  assert.match(verifyHint(item({ status: 'verified' }), finance, TODAY).reason, /already verified/);
  assert.match(verifyHint(item({ method: 'cheque', chequeDate: '2026-11-20' }, { postDated: true }), finance, TODAY).reason, /post-dated.*2026-11-20/);
  assert.equal(verifyHint(item({ method: 'cheque', chequeDate: '2026-11-17' }), finance, TODAY).allowed, true);
});

test('reject hint ignores the post-dated rule; bounce is for verified, non-cash payments, any verifier', () => {
  assert.equal(rejectHint(item({ method: 'cheque', chequeDate: '2026-11-20' }), finance).allowed, true);
  assert.equal(rejectHint(item(), { id: 'a', permissions: finance.permissions }).allowed, false);
  assert.equal(bounceHint(item({ status: 'verified', recordedBy: 'b' }), finance).allowed, true);
  assert.match(bounceHint(item({ status: 'recorded' }), finance).reason, /verified/);
  assert.match(bounceHint(item({ status: 'verified', method: 'cash' }), finance).reason, /Cash/);
  assert.equal(bounceHint(item({ status: 'verified' }), { permissions: [] }).allowed, false);
});

test('queue: oldest first for recorded, server order kept for other statuses; flags; query string', () => {
  const a = item({ id: 'new', recordedAtUtc: '2026-11-12T00:00:00Z' });
  const b = item({ id: 'old', recordedAtUtc: '2026-11-01T00:00:00Z' });
  assert.deepEqual(sortQueue([a, b], 'recorded').map((x) => x.payment.id), ['old', 'new']);
  assert.deepEqual(sortQueue([a, b], 'verified').map((x) => x.payment.id), ['new', 'old']);
  assert.deepEqual(flagsOf({ lateEntry: true, postDated: true }), ['Late entry', 'Post-dated']);
  assert.deepEqual(flagsOf({ lateEntry: false, postDated: false }), []);
  assert.equal(paymentsQuery({ status: 'recorded', page: 2 }), 'status=recorded&page=2&pageSize=25');
  assert.equal(paymentsQuery({ companyId: 'c 1' }), 'companyId=c+1&page=1&pageSize=25');
});

test('errors: four eyes and the other codes in plain words, server field message for a 400, reload codes', () => {
  const e = (o: Partial<{ status: number | null; code: string | null; message: string; fields: Record<string, string> }>) => ({ status: 400, code: null, message: '', fields: {}, ...o });
  assert.match(creditErrorText(e({ status: 409, code: 'CREDIT_PAYMENT_FOUR_EYES' })), /Someone else must/);
  assert.match(creditErrorText(e({ status: 409, code: 'CREDIT_PAYMENT_CHEQUE_NOT_DUE' })), /post-dated/);
  assert.match(creditErrorText(e({ status: 409, code: 'CREDIT_PAYMENT_DUPLICATE_REFERENCE' })), /already recorded/);
  assert.match(creditErrorText(e({ status: 401, code: 'STEP_UP_FAILED' })), /work once/);
  assert.equal(creditErrorText(e({ fields: { reference: 'Enter the transaction id (TrxID), never the wallet\'s phone number.' } })), 'Enter the transaction id (TrxID), never the wallet\'s phone number.');
  assert.match(creditErrorText(e({ status: 403 })), /permission/);
  assert.match(creditErrorText(e({ status: null })), /cannot be reached/);
  assert.match(creditErrorText(e({ status: 503 })), /server had a problem/);
  assert.ok(shouldReloadCredit('CREDIT_PAYMENT_NOT_RECORDED'));
  assert.ok(!shouldReloadCredit('CREDIT_PAYMENT_FOUR_EYES'));
});
