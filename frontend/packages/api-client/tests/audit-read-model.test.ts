import test from 'node:test';
import assert from 'node:assert/strict';
import {
  AUDIT_HIDDEN, EMPTY_AUDIT_FILTERS, activeAuditFilterCount, auditChangeSummary, auditCorrelationHref, auditFilterProblem, auditFiltersToSearch, auditListQuery,
  auditReadErrorText, buildAuditDiff, parseAuditFilters
} from '../src/audit-read-model.ts';

const G = '3f2a1b4c-1111-2222-3333-444455556666';
const parse = (q: string) => { const p = new URLSearchParams(q); return parseAuditFilters((k) => p.get(k)); };

test('filters: URL round trip keeps every field and drops defaults', () => {
  const f = { actorId: G, action: 'order.cancelled', entityName: 'orders', entityId: 'abc', correlationId: 'corr-1', from: '2026-11-01', to: '2026-11-26', page: 3 };
  assert.deepEqual(parse(auditFiltersToSearch(f)), f);
  assert.equal(auditFiltersToSearch(EMPTY_AUDIT_FILTERS), '');
  assert.equal(activeAuditFilterCount(f), 7);
});

test('filters: bad values are dropped, not sent', () => {
  assert.deepEqual(parse('actor=nope&from=2026-02-30&to=x&page=-4&action=%20%20'), EMPTY_AUDIT_FILTERS);
  assert.equal(parse('page=2').page, 2);
  assert.equal(parse(`action=${'a'.repeat(150)}`).action.length, 100);
});

test('filters: API query uses the API names and page size 25', () => {
  const q = new URLSearchParams(auditListQuery({ ...EMPTY_AUDIT_FILTERS, actorId: G, entityName: 'orders', correlationId: 'c', page: 2 }));
  assert.equal(q.get('actorId'), G);
  assert.equal(q.get('entityName'), 'orders');
  assert.equal(q.get('correlationId'), 'c');
  assert.equal(q.get('page'), '2');
  assert.equal(q.get('pageSize'), '25');
  assert.equal(q.has('action'), false);
});

test('filter check: names the problem', () => {
  const ok = { ...EMPTY_AUDIT_FILTERS };
  assert.equal(auditFilterProblem(ok), null);
  assert.match(auditFilterProblem({ ...ok, actorId: 'x' })!, /actor id/);
  assert.match(auditFilterProblem({ ...ok, from: '2026-12-01', to: '2026-11-01' })!, /after/);
  assert.match(auditFilterProblem({ ...ok, to: '2026-13-01' })!, /"to"/);
  assert.match(auditFilterProblem({ ...ok, action: 'a'.repeat(101) })!, /100/);
});

test('correlation link opens the list filtered by that id', () => {
  assert.equal(auditCorrelationHref('a b'), '/audit/log?correlation=a+b');
  assert.equal(parse(auditCorrelationHref('abc').split('?')[1]).correlationId, 'abc');
});

test('diff: changed, added, removed, same and nested paths', () => {
  const rows = buildAuditDiff({ status: 'open', items: [{ qty: 1 }], gone: 1, same: true }, { status: 'paid', items: [{ qty: 1 }, { qty: 2 }], added: 'x', same: true });
  const by = Object.fromEntries(rows.map((r) => [r.path, r]));
  assert.equal(by.status.change, 'changed');
  assert.equal(by['items[0].qty'].change, 'same');
  assert.equal(by['items[1].qty'].change, 'added');
  assert.equal(by.gone.change, 'removed');
  assert.equal(by.added.after, 'x');
  assert.equal(by.same.before, 'true');
  assert.equal(auditChangeSummary(rows), '1 changed, 2 added, 1 removed, 2 unchanged');
});

test('diff: one side null lists the other side; both null is empty', () => {
  assert.deepEqual(buildAuditDiff(null, { a: 1 }).map((r) => r.change), ['added']);
  assert.deepEqual(buildAuditDiff({ a: 1 }, null).map((r) => r.change), ['removed']);
  assert.equal(buildAuditDiff(null, null).length, 0);
});

test('diff: a hidden value is flagged and never unmasked', () => {
  const rows = buildAuditDiff({ passwordHash: AUDIT_HIDDEN, name: 'a' }, { passwordHash: AUDIT_HIDDEN, name: 'b' });
  assert.equal(rows.find((r) => r.path === 'passwordHash')!.hidden, true);
  assert.equal(rows.find((r) => r.path === 'name')!.hidden, false);
});

test('error text: 429 has its own words', () => {
  assert.match(auditReadErrorText({ status: 429 }), /too often/);
  assert.match(auditReadErrorText({ status: 403 }), /audit\.view/);
  assert.match(auditReadErrorText({ status: 404 }), /does not exist/);
  assert.match(auditReadErrorText({ status: null }), /could not be read/);
});
