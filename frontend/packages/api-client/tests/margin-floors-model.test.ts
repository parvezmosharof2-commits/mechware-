import test from 'node:test';
import assert from 'node:assert/strict';
import {
  buildUpsertBody, effectiveQuery, effectiveSummary, filterByText, findExistingRow, flattenCategories, floorErrorText, floorTarget, formatMarkup, groupFloors,
  markupPercentProblem, markupToInput, markupToNumber, scopeShapeProblem, updatedByText, type MarginFloorRow
} from '../src/margin-floors-model.ts';

const row = (o: Partial<MarginFloorRow>): MarginFloorRow => ({
  id: 'r', scope: 'Global', brandId: null, brandName: null, categoryId: null, categoryName: null, categoryPath: [], minMarkupPercent: 10,
  updatedBy: null, updatedByName: null, createdAtUtc: '2026-01-01T00:00:00Z', updatedAtUtc: '2026-01-01T00:00:00Z', ...o
});

test('markup: accepts 0 to 1000 with at most 2 decimals', () => {
  for (const ok of ['0', '0.5', '10', '15.25', '999.99', '1000', '1000.00', '1000.0', '007']) assert.equal(markupPercentProblem(ok), null, ok);
});

test('markup: refuses a third decimal, out of range, junk', () => {
  for (const bad of ['', '  ', '15.255', '10.001', '1000.01', '1001', '10000', '-1', '1e2', '1,5', 'abc', '.5', '1 0', '+5', '12.']) {
    assert.ok(markupPercentProblem(bad), `should refuse "${bad}"`);
  }
  assert.match(markupPercentProblem('15.255')!, /2 decimals/);
});

test('markup: a third decimal is never rounded away', () => {
  assert.equal(markupToNumber('15.255'), null);
  assert.equal(markupToNumber('15.25'), 15.25);
  assert.equal(markupToNumber(' 20 '), 20);
});

test('markup display and edit text', () => {
  assert.equal(formatMarkup(10), '10.00');
  assert.equal(formatMarkup(12.5), '12.50');
  assert.equal(formatMarkup(null), '-');
  assert.equal(markupToInput(10), '10');
  assert.equal(markupToInput(15.25), '15.25');
  assert.equal(markupToInput(12.5), '12.5');
  assert.equal(markupToInput(0), '0');
  assert.equal(markupToInput(1000), '1000');
});

test('scope shape rules', () => {
  assert.equal(scopeShapeProblem('Global', null, null), null);
  assert.ok(scopeShapeProblem('Global', 'b', null));
  assert.equal(scopeShapeProblem('Brand', 'b', null), null);
  assert.ok(scopeShapeProblem('Brand', null, null));
  assert.ok(scopeShapeProblem('Brand', 'b', 'c'));
  assert.equal(scopeShapeProblem('Category', null, 'c'), null);
  assert.ok(scopeShapeProblem('Category', 'b', 'c'));
  assert.equal(scopeShapeProblem('BrandAndCategory', 'b', 'c'), null);
  assert.ok(scopeShapeProblem('BrandAndCategory', 'b', null));
  assert.ok(scopeShapeProblem('BrandAndCategory', null, 'c'));
});

test('upsert body carries only the ids the scope takes and the number', () => {
  assert.deepEqual(buildUpsertBody({ scope: 'Brand', brandId: 'b', categoryId: 'stale', markup: '15.25' }), { body: { scope: 'Brand', brandId: 'b', minMarkupPercent: 15.25 } });
  assert.deepEqual(buildUpsertBody({ scope: 'Global', brandId: null, categoryId: null, markup: '10' }), { body: { scope: 'Global', minMarkupPercent: 10 } });
  const r = buildUpsertBody({ scope: 'Brand', brandId: 'b', categoryId: null, markup: '15.255' });
  assert.ok('problem' in r && r.field === 'markup');
  const s = buildUpsertBody({ scope: 'Category', brandId: null, categoryId: null, markup: '5' });
  assert.ok('problem' in s && s.field === 'shape');
});

test('grouping puts the global row apart and sorts by precedence then name', () => {
  const rows = [
    row({ id: 'g' }),
    row({ id: 'c1', scope: 'Category', categoryPath: ['Tools', 'Drills'] }),
    row({ id: 'b2', scope: 'Brand', brandName: 'Zeta' }),
    row({ id: 'bc', scope: 'BrandAndCategory', brandName: 'Acme', categoryPath: ['Tools'] }),
    row({ id: 'b1', scope: 'Brand', brandName: 'Acme' })
  ];
  const g = groupFloors(rows);
  assert.equal(g.global?.id, 'g');
  assert.deepEqual(g.specific.map((r) => r.id), ['bc', 'b1', 'b2', 'c1']);
  assert.equal(groupFloors([]).global, null);
});

test('targets and people', () => {
  assert.equal(floorTarget(row({ scope: 'Category', categoryPath: ['Tools', 'Drills'] })), 'Tools › Drills');
  assert.equal(floorTarget(row({ scope: 'BrandAndCategory', brandName: 'Acme', categoryPath: ['Tools'] })), 'Acme in Tools');
  assert.equal(updatedByText(row({})), 'system');
  assert.equal(updatedByText(row({ updatedBy: 'u', updatedByName: 'Rina' })), 'Rina');
});

test('existing row lookup by scope key', () => {
  const rows = [row({ id: 'b', scope: 'Brand', brandId: 'b1' }), row({ id: 'g' })];
  assert.equal(findExistingRow(rows, 'Brand', 'b1', null)?.id, 'b');
  assert.equal(findExistingRow(rows, 'Brand', 'b2', null), null);
  assert.equal(findExistingRow(rows, 'Global', null, null)?.id, 'g');
});

test('effective query and summary', () => {
  assert.equal(effectiveQuery({ variantId: 'v 1' }), 'variantId=v+1');
  assert.equal(effectiveQuery({ brandId: 'b', categoryId: 'c' }), 'brandId=b&categoryId=c');
  assert.equal(effectiveQuery({ categoryId: 'c' }), 'categoryId=c');
  const e = { variantId: null, brandId: null, categoryId: null, categoryAncestry: [], floor: row({ minMarkupPercent: 10 }), minMarkupPercent: 10, failClosed: false };
  assert.match(effectiveSummary(e), /10\.00 % applies, from the global default/);
  assert.match(effectiveSummary({ ...e, floor: null, minMarkupPercent: null, failClosed: true }), /no global row/);
});

test('error codes in plain words', () => {
  const e = (status: number | null, code: string | null, fields: Record<string, string> = {}) => floorErrorText({ status, code, message: '', fields });
  assert.match(e(409, 'GLOBAL_FLOOR_REQUIRED'), /there must always be one/);
  assert.match(e(403, 'STEP_UP_REQUIRED'), /6-digit/);
  assert.match(e(403, 'STEP_UP_FAILED'), /next code/);
  assert.match(e(403, null), /permission/);
  assert.match(e(400, 'VALIDATION_ERROR', { minMarkupPercent: 'Too many decimals.' }), /Too many decimals/);
  assert.match(e(404, null), /no longer exists/);
  assert.match(e(500, null), /server had a problem/);
  assert.match(e(null, null), /Could not reach/);
});

test('category tree flattening and text filter', () => {
  const flat = flattenCategories([{ id: '1', nameEn: 'Tools', children: [{ id: '2', nameEn: 'Drills', children: [] }] }, { id: '3', nameEn: 'Bearings' }]);
  assert.deepEqual(flat.map((c) => c.label), ['Tools', 'Tools › Drills', 'Bearings']);
  assert.deepEqual(filterByText(flat, 'drill').map((c) => c.id), ['2']);
  assert.equal(filterByText(flat, '  ').length, 3);
});
