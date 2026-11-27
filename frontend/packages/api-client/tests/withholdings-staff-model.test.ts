import test from 'node:test';
import assert from 'node:assert/strict';
import {
  buildWithholdingBody, canRecordWithholding, canVerifyWithholding, vdsTotalMinor, verifyWithholdingText, withholdingBdtToMinor, withholdingDecision, withholdingErrorText,
  withholdingFiltersFromSearch, withholdingFiltersToSearch, withholdingFingerprint, shouldReloadWithholding
} from '../src/withholdings-staff-model.ts';

const form = (o: Record<string, string> = {}) => ({ kind: 'vds', certificateNumber: ' mu/12  34 ', certificateDate: '2026-11-01', amount: '1,500.5', ...o });

test('permissions are the credit ones, separate', () => {
  assert.equal(canRecordWithholding(['Credit.Record_Payment']), true);
  assert.equal(canVerifyWithholding(['credit.record_payment']), false);
});
test('amount text to poisha without floats', () => {
  assert.equal(withholdingBdtToMinor('1,500.5'), 150050);
  assert.equal(withholdingBdtToMinor('12.345'), null);
  assert.equal(withholdingBdtToMinor('-1'), null);
});
test('body: normalises the number, checks every field in order', () => {
  const r = buildWithholdingBody(form(), '2026-11-27');
  assert.deepEqual('body' in r && r.body, { kind: 'vds', certificateNumber: 'MU/12 34', certificateDate: '2026-11-01', amountMinor: 150050 });
  assert.match((buildWithholdingBody(form({ kind: '' }), '2026-11-27') as { problem: string }).problem, /VDS or AIT/);
  assert.match((buildWithholdingBody(form({ certificateNumber: 'a#1' }), '2026-11-27') as { problem: string }).problem, /certificate number/);
  assert.match((buildWithholdingBody(form({ certificateDate: '2026-12-01' }), '2026-11-27') as { problem: string }).problem, /future/);
  assert.match((buildWithholdingBody(form({ amount: '0' }), '2026-11-27') as { problem: string }).problem, /amount/);
});
test('fingerprint changes with the amount, so a changed form gets a new key', () => {
  const b = { kind: 'vds' as const, certificateNumber: 'A1', certificateDate: '2026-11-01', amountMinor: 100 };
  assert.notEqual(withholdingFingerprint('i', b), withholdingFingerprint('i', { ...b, amountMinor: 101 }));
});
test('URL filters round trip; junk falls back to the default', () => {
  const f = withholdingFiltersFromSearch(new URLSearchParams('status=verified&kind=ait&page=2'));
  assert.deepEqual(f, { status: 'verified', kind: 'ait', invoiceId: '', page: 2 });
  assert.equal(withholdingFiltersToSearch(f), 'status=verified&kind=ait&page=2');
  assert.deepEqual(withholdingFiltersFromSearch(new URLSearchParams('status=x&kind=y&page=-3&invoiceId=zz')), { status: 'recorded', kind: '', invoiceId: '', page: 1 });
  assert.equal(withholdingFiltersToSearch({ status: 'recorded', kind: '', invoiceId: '', page: 1 }), '');
});
test('decision: four eyes for the recorder, who may still reject', () => {
  const me = { id: 'AAA', permissions: ['credit.verify_payment', 'credit.record_payment'] };
  assert.deepEqual(withholdingDecision({ status: 'recorded', recordedBy: 'aaa' }, me), { canVerify: false, canReject: true, hint: withholdingDecision({ status: 'recorded', recordedBy: 'aaa' }, me).hint });
  assert.match(withholdingDecision({ status: 'recorded', recordedBy: 'aaa' }, me).hint ?? '', /another person must verify/);
  assert.equal(withholdingDecision({ status: 'recorded', recordedBy: 'bbb' }, me).canVerify, true);
  assert.equal(withholdingDecision({ status: 'verified', recordedBy: 'bbb' }, me).canReject, false);
  assert.equal(withholdingDecision({ status: 'recorded', recordedBy: 'bbb' }, { id: 'c', permissions: ['credit.record_payment'] }).canVerify, false);
});
test('409 codes in words; the cap text adds the server message', () => {
  assert.match(withholdingErrorText({ status: 409, code: 'WITHHOLDING_EXCEEDS_VAT', message: 'Left: BDT 10.00.' }), /VAT still left.*Left: BDT 10\.00\./);
  assert.match(withholdingErrorText({ status: 409, code: 'WITHHOLDING_CERTIFICATE_DUPLICATE' }), /already has a certificate/);
  for (const c of ['WITHHOLDING_NEEDS_COMPANY_INVOICE', 'WITHHOLDING_NEEDS_CREDIT_ORDER', 'WITHHOLDING_FOUR_EYES', 'WITHHOLDING_NOT_RECORDED', 'IDEMPOTENCY_KEY_REUSED', 'CREDIT_EXCEEDS_RECEIVABLE', 'STEP_UP_FAILED']) {
    assert.doesNotMatch(withholdingErrorText({ status: 409, code: c, message: 'ZZQ' }), /ZZQ|WITHHOLDING_/);
  }
  assert.match(withholdingErrorText({ status: null }), /cannot be reached/);
  assert.equal(shouldReloadWithholding('WITHHOLDING_NOT_RECORDED'), true);
});
test('verify text and the section c total', () => {
  assert.match(verifyWithholdingText({ kind: 'vds', certificateNumber: 'A1', amountMinor: 123456 }), /VDS certificate A1.*BDT 1,234\.56/);
  assert.equal(vdsTotalMinor([{ amountMinor: 5 }, { amountMinor: 7 }]), 12);
});
