import test from 'node:test';
import assert from 'node:assert/strict';
import { auditErrorText, auditVerdict, auditVerdictText, canViewAudit, groupHash, type AuditChainStatus } from '../src/audit-model.ts';

const ok: AuditChainStatus = { sealed: 12345, unsealed: 3, intact: true, firstBrokenPosition: null, headHash: 'ab'.repeat(32) };

test('permission: only audit.view, any case', () => {
  assert.equal(canViewAudit(['audit.view']), true);
  assert.equal(canViewAudit(['AUDIT.VIEW']), true);
  assert.equal(canViewAudit(['credit.view', 'users.manage']), false);
  assert.equal(canViewAudit([]), false);
});

test('verdict: intact, empty, broken', () => {
  assert.equal(auditVerdict(ok), 'intact');
  assert.equal(auditVerdict({ ...ok, sealed: 0, headHash: null }), 'empty');
  assert.equal(auditVerdict({ ...ok, intact: false, firstBrokenPosition: 7 }), 'broken');
  assert.equal(auditVerdict({ ...ok, intact: true, firstBrokenPosition: 7 }), 'broken');
  assert.equal(auditVerdict({ ...ok, intact: false, firstBrokenPosition: null }), 'broken');
});

test('verdict text names the row and never calls a broken log fine', () => {
  assert.match(auditVerdictText(ok), /All 12,345 sealed rows match/);
  assert.match(auditVerdictText({ ...ok, intact: false, firstBrokenPosition: 7 }), /starting at row 7/);
  assert.match(auditVerdictText({ ...ok, intact: false, firstBrokenPosition: null }), /security incident/);
  assert.match(auditVerdictText({ ...ok, sealed: 0 }), /nothing to check/);
});

test('groupHash groups by 8 and keeps every character', () => {
  const g = groupHash('0123456789abcdef0123');
  assert.equal(g, '01234567 89abcdef 0123');
  assert.equal(g.replace(/ /g, ''), '0123456789abcdef0123');
  assert.equal(groupHash(null), '—');
  assert.equal(groupHash(''), '—');
});

test('error text by status', () => {
  assert.match(auditErrorText({ status: 403 }), /audit\.view/);
  assert.match(auditErrorText({ status: 401 }), /Sign in/);
  assert.match(auditErrorText({ status: 429 }), /Wait a minute/);
  assert.match(auditErrorText({ status: null }), /try again/);
});
