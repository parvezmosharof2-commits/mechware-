import test from 'node:test';
import assert from 'node:assert/strict';
import {
  buildCreditNoteBody, canApproveCreditNote, canRequestCreditNote, creditApprovalText, creditBdtToMinor, creditDecision, creditNoteErrorText, creditRequestFingerprint,
  shouldReloadCreditNote, type CreditLineDraft
} from '../src/credit-notes-staff-model.ts';

const line = (o: Partial<CreditLineDraft> = {}): CreditLineDraft => ({ lineNo: 1, maxQuantity: 5, maxNetMinor: 100000, quantity: '', value: '', ...o });

test('permissions: request and approve are separate', () => {
  assert.equal(canRequestCreditNote(['Invoices.Credit_Request']), true);
  assert.equal(canApproveCreditNote(['invoices.credit_request']), false);
  assert.equal(canApproveCreditNote(['invoices.credit_approve']), true);
});

test('BDT text to poisha without floats', () => {
  assert.equal(creditBdtToMinor('1,250.5'), 125050);
  assert.equal(creditBdtToMinor('12.05'), 1205);
  for (const bad of ['', '-1', '1.234', '1e3', 'abc']) assert.equal(creditBdtToMinor(bad), null, bad);
});

test('body: quantity-only omits the value, value-only has quantity 0, empty lines are dropped', () => {
  const r = buildCreditNoteBody([line({ quantity: '2' }), line({ lineNo: 2, value: '250.50' }), line({ lineNo: 3 })], 'error', '  Wrong price  ');
  assert.deepEqual('body' in r && r.body, { reason: 'error', reasonText: 'Wrong price', lines: [{ lineNo: 1, quantity: 2 }, { lineNo: 2, quantity: 0, netMinor: 25050 }] });
});

test('body: every refusal is words, before sending', () => {
  const p = (l: CreditLineDraft[], reason = 'other', text = 'Because') => { const r = buildCreditNoteBody(l, reason, text); return 'problem' in r ? r.problem : null; };
  assert.match(p([line({ quantity: '1' })], ''), /reason/i);
  assert.match(p([line({ quantity: '1' })], 'other', 'abc'), /5 to 500/);
  assert.match(p([line({ quantity: '6' })]), /0 to 5/);
  assert.match(p([line({ quantity: '1.5' })]), /whole number/);
  assert.match(p([line({ value: '1000.01' })]), /more than the line/);
  assert.match(p([line({ value: '0' })]), /amount in BDT/);
  assert.match(p([line()]), /at least one line/);
  assert.match(p([line({ quantity: '0' })]), /at least one line/);
});

test('same form same fingerprint, a change is a new one', () => {
  const b = { reason: 'error', reasonText: 'x', lines: [{ lineNo: 1, quantity: 1 }] };
  assert.equal(creditRequestFingerprint('i', b), creditRequestFingerprint('i', { ...b }));
  assert.notEqual(creditRequestFingerprint('i', b), creditRequestFingerprint('i', { ...b, reasonText: 'y' }));
});

test('four eyes: the requester cannot decide but can withdraw; others need credit_approve', () => {
  const req = { status: 'requested', requestedBy: 'AAA' };
  const own = creditDecision(req, { id: 'aaa', permissions: ['invoices.credit_request', 'invoices.credit_approve'] });
  assert.deepEqual([own.canDecide, own.canWithdraw], [false, true]);
  assert.match(own.hint!, /another person must approve/);
  assert.equal(creditDecision(req, { id: 'bbb', permissions: ['invoices.credit_approve'] }).canDecide, true);
  const support = creditDecision(req, { id: 'ccc', permissions: ['invoices.credit_request'] });
  assert.deepEqual([support.canDecide, support.canWithdraw], [false, false]);
  assert.equal(creditDecision({ ...req, status: 'approved' }, { id: 'bbb', permissions: ['invoices.credit_approve'] }).canDecide, false);
});

test('approval words: refund due when not on credit, credit released otherwise', () => {
  assert.match(creditApprovalText({ number: 'CN-1', refundDueMinor: 123456, creditReleasedMinor: 0 }), /Refund due: BDT 1,234\.56/);
  assert.match(creditApprovalText({ number: 'CN-1', refundDueMinor: 0, creditReleasedMinor: 5000 }), /BDT 50\.00 was taken off.*No refund is due/);
});

test('error words: changed, caps, step-up, four eyes; reload only when the facts are old', () => {
  assert.match(creditNoteErrorText({ status: 409, code: 'CREDIT_NOTE_CHANGED' }), /changed/);
  assert.match(creditNoteErrorText({ status: 409, code: 'CREDIT_NOTE_EXCEEDS_ORIGINAL' }), /left on the invoice/);
  assert.match(creditNoteErrorText({ status: 403, code: 'STEP_UP_FAILED' }), /next code/);
  assert.match(creditNoteErrorText({ status: 403 }), /does not allow/);
  assert.match(creditNoteErrorText({ status: null }), /cannot be reached/);
  assert.equal(shouldReloadCreditNote('CREDIT_NOTE_CHANGED'), true);
  assert.equal(shouldReloadCreditNote('CREDIT_NOTE_FOUR_EYES'), false);
});
