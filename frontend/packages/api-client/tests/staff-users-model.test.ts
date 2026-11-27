import test from 'node:test';
import assert from 'node:assert/strict';
import {
  approveChangeHint, canManageUsers, disableHint, lastSignInText, parseUserFilters, roleChangeProblems, roleDiffText, staffUserErrorText, userFiltersToSearch, userListQuery
} from '../src/staff-users-model.ts';

const me = '11111111-1111-1111-1111-111111111111';
const other = '22222222-2222-2222-2222-222222222222';

test('permission users.manage only', () => {
  assert.equal(canManageUsers(['USERS.MANAGE']), true);
  assert.equal(canManageUsers(['audit.view']), false);
});

test('filters round trip through the address and drop junk', () => {
  const src: Record<string, string> = { q: ' ann ', role: 'admin', status: 'hacker', page: '3' };
  const f = parseUserFilters((k) => src[k] ?? null);
  assert.deepEqual(f, { q: 'ann', role: 'admin', status: '', page: 3 });
  assert.equal(userFiltersToSearch(f), 'q=ann&role=admin&page=3');
  assert.equal(parseUserFilters(() => 'x').page, 1);
  assert.match(userListQuery({ q: '', role: '', status: 'active', page: 2 }), /status=active&page=2&pageSize=25/);
});

test('disable: self is refused in words, a disabled person can be enabled', () => {
  assert.equal(disableHint({ id: me.toUpperCase(), status: 'active' }, me).allowed, false);
  assert.match(disableHint({ id: me, status: 'active' }, me).reason ?? '', /own account/);
  assert.deepEqual(disableHint({ id: other, status: 'suspended' }, me), { allowed: true, action: 'enable', reason: null });
  assert.equal(disableHint({ id: other, status: 'active' }, me).action, 'disable');
});

test('four eyes: requester and the person themself cannot approve', () => {
  const c = { requestedBy: me, userId: other, status: 'requested' };
  assert.match(approveChangeHint(c, me).reason ?? '', /different administrator/);
  assert.equal(approveChangeHint(c, other).allowed, false);
  assert.equal(approveChangeHint(c, '33333333-3333-3333-3333-333333333333').allowed, true);
  assert.equal(approveChangeHint({ ...c, status: 'approved' }, '33333333-3333-3333-3333-333333333333').allowed, false);
});

test('role change: needs a different set and a 5+ character reason', () => {
  assert.ok(roleChangeProblems(['support'], ['support'], 'long enough').roles);
  assert.ok(roleChangeProblems(['support'], ['finance'], 'no').reason);
  assert.deepEqual(roleChangeProblems(['support'], ['finance'], 'covers month end'), {});
  assert.equal(roleDiffText(['support'], ['finance', 'support']), 'adds Finance');
  assert.equal(roleDiffText(['admin'], ['support']), 'adds Support and removes Admin');
});

test('error words and Dhaka sign-in', () => {
  for (const c of ['USER_DISABLE_SELF', 'LAST_ADMIN', 'ROLE_CHANGE_SELF', 'ROLE_CHANGE_FOUR_EYES']) assert.ok(staffUserErrorText({ status: 409, code: c }).length > 20);
  assert.match(staffUserErrorText({ status: null, code: null }), /cannot be reached/);
  assert.equal(lastSignInText(null), 'Never signed in');
  assert.equal(lastSignInText('2026-11-27T20:00:00Z'), '2026-11-28 02:00 Dhaka');
});
