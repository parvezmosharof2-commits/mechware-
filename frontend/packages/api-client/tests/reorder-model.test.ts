import test from 'node:test';
import assert from 'node:assert/strict';
import { planReorder, reorderReport, reorderText, reorderWanted } from '../src/quick-order-model.ts';
import type { QuickOrderLine } from '../src/quick-order-model.ts';

const item = (variantId: string, sku: string, quantity: number, unitPriceMinor = 1000) => ({
  variantId, sku, title: `T ${sku}`, quantity, unitPriceMinor, discountMinor: 0, taxMinor: 0, totalMinor: quantity * unitPriceMinor
});
const order = {
  subOrders: [
    { id: 's1', subOrderNumber: 'A-1', sellerId: 'x', status: 'confirmed', shippingMinor: 0, totalMinor: 0, items: [item('v1', 'SKU-1', 2), item('v2', 'SKU,2', 1)] },
    { id: 's2', subOrderNumber: 'A-2', sellerId: 'y', status: 'confirmed', shippingMinor: 0, totalMinor: 0, items: [item('v1', 'SKU-1', 3), item('v3', 'SKU-3', 4)] }
  ]
};
const variant = (id: string) => ({ id, sku: 's', partNumber: null, title: '', titleBn: '', brand: null, slug: 's', options: {} });
const line = (n: number, over: Partial<QuickOrderLine>): QuickOrderLine => ({
  lineNumbers: [n], raw: '', part: '', requestedQuantity: 1, status: 'OK', message: '', valid: true, variant: variant('v1'), quantity: 1,
  notices: [], stock: null, unitPriceMinor: 1000, lineTotalMinor: 1000, priceSource: 'list', successor: null, candidates: [], suggestions: [], ...over
});

test('reorderWanted sums the same variant across shipments and keeps order', () => {
  const w = reorderWanted(order);
  assert.deepEqual(w.map((x) => [x.variantId, x.quantity]), [['v1', 5], ['v2', 1], ['v3', 4]]);
});

test('reorderText: one line per wanted, part quoted when needed', () => {
  assert.equal(reorderText(reorderWanted(order)), 'SKU-1, 5\n"SKU,2", 1\nSKU-3, 4');
});

test('planReorder adds valid same-variant lines at the server price and quantity, flags change', () => {
  const w = reorderWanted(order);
  const plan = planReorder(w, {
    errors: [],
    lines: [
      line(1, { variant: variant('v1'), quantity: 6, unitPriceMinor: 1200 }),
      line(2, { variant: variant('v2'), quantity: 1, unitPriceMinor: 1000 }),
      line(3, { variant: variant('v3'), quantity: 4, unitPriceMinor: null })
    ]
  });
  assert.equal(plan.skip.length, 0);
  assert.deepEqual(plan.add.map((a) => [a.wanted.variantId, a.quantity, a.adjusted, a.priceChanged]), [['v1', 6, true, true], ['v2', 1, false, false], ['v3', 4, false, false]]);
});

test('planReorder never swaps in another variant and lists every unavailable line with a reason', () => {
  const w = reorderWanted(order);
  const plan = planReorder(w, {
    errors: [],
    lines: [
      line(1, { variant: variant('other') }),
      line(2, { valid: false, status: 'INSUFFICIENT_STOCK', quantity: null, variant: variant('v2'), stock: { available: 3, status: 'low_stock' } }),
      line(3, { valid: false, status: 'PART_OBSOLETE', quantity: null, variant: null, successor: { variant: variant('n'), isDropIn: true, note: null } })
    ]
  });
  assert.equal(plan.add.length, 0);
  assert.deepEqual(plan.skip.map((s) => [s.reason, s.available]), [['changed', null], ['insufficientStock', 3], ['obsolete', null]]);
});

test('planReorder: parser error, missing result and over-cap lines are skipped, never dropped', () => {
  const w = reorderWanted(order);
  const plan = planReorder(w, { errors: [{ lineNumber: 1, code: 'PART_INVALID', message: '', relatedLines: null }], lines: [line(2, { variant: variant('v2') })] });
  assert.deepEqual(plan.skip.map((s) => s.reason), ['unreadable', 'other']);
  assert.equal(plan.add.length, 1);
  const many = Array.from({ length: 502 }, (_, i) => ({ variantId: `v${i}`, sku: `S${i}`, title: '', quantity: 1, previousUnitPriceMinor: 1 }));
  const p = planReorder(many, { errors: [], lines: [] });
  assert.equal(p.skip.filter((s) => s.reason === 'tooMany').length, 2);
  assert.equal(p.add.length + p.skip.length, 502);
});

test('reorderReport splits cart outcomes', () => {
  const w = reorderWanted(order);
  const plan = planReorder(w, { errors: [], lines: [line(1, {}), line(2, { variant: variant('v2') }), line(3, { variant: variant('zz') })] });
  const r = reorderReport(plan, [{ add: plan.add[0]!, ok: true }, { add: plan.add[1]!, ok: false, reason: 'cartFull' }]);
  assert.equal(r.added.length, 1);
  assert.equal(r.refused[0]!.reason, 'cartFull');
  assert.equal(r.skipped.length, 1);
});
