import test from 'node:test';
import assert from 'node:assert/strict';
import {
  MAX_CHAIN_LENGTH,
  decideState,
  isFinished,
  parseAmountToMinor,
  shortId,
  validateDecision,
  validateRuleForm
} from '../src/approvals-model.ts';

const ids = (n: number) => Array.from({ length: n }, (_, i) => `00000000-0000-0000-0000-00000000000${i}`);

test('rule form mirrors the API: name, threshold and a chain of 1 to 5 distinct approvers', () => {
  const ok = { name: 'Over 50k', minAmountText: '50,000', approverUserIds: ids(2) };
  assert.deepEqual(validateRuleForm(ok), {});
  assert.deepEqual(validateRuleForm({ ...ok, minAmountText: '' }), {}, 'an empty threshold means 0');
  assert.equal(validateRuleForm({ ...ok, name: '  ' }).name, 'name.required');
  assert.equal(validateRuleForm({ ...ok, name: 'x'.repeat(101) }).name, 'name.length');
  assert.equal(validateRuleForm({ ...ok, minAmountText: '-1' }).minAmount, 'amount.format');
  assert.equal(validateRuleForm({ ...ok, minAmountText: 'abc' }).minAmount, 'amount.format');
  assert.equal(validateRuleForm({ ...ok, approverUserIds: [] }).approverUserIds, 'chain.empty');
  assert.equal(validateRuleForm({ ...ok, approverUserIds: ids(MAX_CHAIN_LENGTH + 1) }).approverUserIds, 'chain.tooLong');
  assert.equal(validateRuleForm({ ...ok, approverUserIds: ids(MAX_CHAIN_LENGTH) }).approverUserIds, undefined);
  assert.equal(validateRuleForm({ ...ok, approverUserIds: [ids(1)[0], ids(1)[0]] }).approverUserIds, 'chain.duplicate');
});

test('amounts become whole minor units, Bangla digits and separators included', () => {
  assert.equal(parseAmountToMinor('1,25,000'), 12500000);
  assert.equal(parseAmountToMinor('১০০০.৫০'), 100050);
  assert.equal(parseAmountToMinor('0'), 0);
  assert.equal(parseAmountToMinor(''), null);
  assert.equal(parseAmountToMinor('-5'), null);
  assert.equal(parseAmountToMinor('1e999'), null);
  for (let minor = 0; minor < 3000; minor += 11) assert.equal(parseAmountToMinor(String(minor / 100)), minor, `minor=${minor}`);
});

test('who can decide: only the current approver, never the requester, never a finished request', () => {
  const base = { requesterUserId: 'req', currentApproverUserId: 'app1' };
  assert.equal(decideState({ ...base, status: 'pending' }, 'app1'), 'can_decide');
  assert.equal(decideState({ ...base, status: 'pending' }, 'app2'), 'not_your_turn');
  assert.equal(decideState({ ...base, status: 'pending' }, 'req'), 'own_request');
  assert.equal(decideState({ ...base, currentApproverUserId: 'req', status: 'pending' }, 'req'), 'own_request');
  for (const status of ['approved', 'rejected', 'cancelled'] as const) {
    assert.equal(decideState({ ...base, status }, 'app1'), 'finished');
    assert.equal(isFinished(status), true);
  }
  assert.equal(isFinished('pending'), false);
});

test('a rejection needs a comment; comments are limited to 500 characters', () => {
  assert.equal(validateDecision(false, ''), 'comment.required');
  assert.equal(validateDecision(false, '   '), 'comment.required');
  assert.equal(validateDecision(false, 'Over budget'), null);
  assert.equal(validateDecision(true, ''), null);
  assert.equal(validateDecision(true, 'x'.repeat(501)), 'comment.tooLong');
  assert.equal(validateDecision(true, 'x'.repeat(500)), null);
});

test('short ids keep the first 8 characters', () => {
  assert.equal(shortId('abcdef12-3456-7890-abcd-ef1234567890'), 'abcdef12');
});
