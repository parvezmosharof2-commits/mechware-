import test from 'node:test';
import assert from 'node:assert/strict';
import {
  RETURN_NOTE_MAX, canRejectCreditReduction, buildReturnBody, canManageReturns, canWithdrawReturn, inspectBody, inspectionProblems, keepReturnKey, noReturnStepText, parseReturnFilters,
  returnAvailability, returnErrorKey, returnFiltersToSearch, returnFormProblems, returnListQuery, returnStatusKey, returnSteps, returnWindowEnd, returnableByLine,
  shouldReloadReturn, staffReturnErrorText, RETURN_PHOTO_BYTES, canAddReturnPhoto, canRemoveReturnPhoto, returnPhotoErrorKey, returnPhotoProblem, EMPTY_RETURN_FILTERS
} from '../src/returns-model.ts';

test('window: closes at Dhaka midnight after the 7th Dhaka day after delivery', () => {
  // Delivered 2026-11-10 20:00 UTC = 11 Nov 02:00 Dhaka -> day 11; +7 = 18th; closes at Dhaka midnight into the 19th = 18 Nov 18:00 UTC.
  assert.equal(returnWindowEnd('2026-11-10T20:00:00Z')?.toISOString(), '2026-11-18T18:00:00.000Z');
  assert.equal(returnWindowEnd('2026-11-10T20:00:00')?.toISOString(), '2026-11-18T18:00:00.000Z');
  assert.equal(returnWindowEnd('junk'), null);
});

test('availability: delivered, inside the window, something left', () => {
  const order = { status: 'delivered', shipments: [{ deliveredAtUtc: '2026-11-10T20:00:00Z' }] };
  assert.equal(returnAvailability(order, 2, new Date('2026-11-18T17:59:59Z')), 'available');
  assert.equal(returnAvailability(order, 2, new Date('2026-11-18T18:00:00Z')), 'windowClosed');
  assert.equal(returnAvailability(order, 0, new Date('2026-11-12T00:00:00Z')), 'nothingLeft');
  assert.equal(returnAvailability({ ...order, status: 'shipped' }, 2), 'notDelivered');
  assert.equal(returnAvailability({ status: 'delivered', shipments: [] }, 1), 'available');
});

test('returnable: ordered minus open and finished claims, not withdrawn or rejected, not other orders', () => {
  const lines = [{ id: 'a', quantity: 5 }, { id: 'b', quantity: 2 }, { quantity: 1 }];
  const mk = (status: string, orderId: string, q: number) => ({ status, orderId, lines: [{ orderItemId: 'a', variantId: 'v', sku: 's', title: 't', quantity: q, acceptedQuantity: null }] });
  assert.deepEqual(returnableByLine(lines, [mk('requested', 'o', 2), mk('inspected', 'o', 1), mk('withdrawn', 'o', 9), mk('rejected', 'o', 9), mk('requested', 'x', 9)], 'o'), { a: 2, b: 2 });
  assert.deepEqual(returnableByLine([{ id: 'a', quantity: 1 }], [mk('requested', 'o', 5)], 'o'), { a: 0 });
});

test('buyer form: lines, quantity cap, reason, note required for other', () => {
  const max = { a: 2, b: 1 };
  const ok = { quantities: { a: 2, b: 0 }, reasonCode: 'damaged', note: '' };
  assert.deepEqual(returnFormProblems(ok, max), []);
  assert.deepEqual(returnFormProblems({ ...ok, quantities: { a: 0 } }, max), ['noLines']);
  assert.deepEqual(returnFormProblems({ ...ok, quantities: { a: 3 } }, max), ['quantity']);
  assert.deepEqual(returnFormProblems({ ...ok, quantities: { a: 1.5 } }, max), ['quantity']);
  assert.deepEqual(returnFormProblems({ ...ok, reasonCode: 'x' }, max), ['reason']);
  assert.deepEqual(returnFormProblems({ ...ok, reasonCode: 'other', note: '   ' }, max), ['noteRequired']);
  assert.deepEqual(returnFormProblems({ ...ok, note: 'x'.repeat(RETURN_NOTE_MAX + 1) }, max), ['noteTooLong']);
});

test('buyer body: only chosen lines; company id only for a company order; empty note is null', () => {
  const f = { quantities: { a: 2, b: 0 }, reasonCode: 'other', note: '  Cracked  ' };
  assert.deepEqual(buildReturnBody(f), { lines: [{ orderItemId: 'a', quantity: 2 }], reasonCode: 'other', note: 'Cracked' });
  assert.deepEqual(buildReturnBody({ ...f, note: '' }, 'c1'), { lines: [{ orderItemId: 'a', quantity: 2 }], reasonCode: 'other', note: null, companyId: 'c1' });
});

test('errors: refusals in words, key kept only on network and 5xx', () => {
  assert.equal(returnErrorKey(409, 'RETURN_WINDOW_CLOSED'), 'windowClosed');
  assert.equal(returnErrorKey(409, 'RETURN_ORDER_NOT_DELIVERED'), 'notDelivered');
  assert.equal(returnErrorKey(422, 'RETURN_LINE_NOT_RETURNABLE'), 'notReturnable');
  assert.equal(returnErrorKey(409, 'RETURN_QUANTITY_EXCEEDED'), 'quantity');
  assert.equal(returnErrorKey(409, 'RETURN_NOT_WITHDRAWABLE'), 'notWithdrawable');
  assert.equal(returnErrorKey(404, null), 'notFound');
  assert.equal(returnErrorKey(400, 'VALIDATION_ERROR'), 'invalid');
  assert.equal(returnErrorKey(null, null), 'unavailable');
  assert.equal(returnErrorKey(503, null), 'unavailable');
  assert.deepEqual([null, 500, 503, 400, 404, 409, 422, 429].map(keepReturnKey), [true, true, true, false, false, false, false, false]);
});

test('status words and withdraw rule', () => {
  assert.equal(returnStatusKey('credit_noted'), 'credit_noted');
  assert.equal(returnStatusKey('nonsense'), 'unknown');
  assert.equal(returnStatusKey(null), 'unknown');
  assert.deepEqual(['requested', 'approved', 'rejected', 'inspected'].map(canWithdrawReturn), [true, false, false, false]);
});

test('staff steps per status and permission', () => {
  assert.deepEqual(returnSteps('requested'), ['approve', 'reject']);
  assert.deepEqual(returnSteps('approved'), ['received']);
  assert.deepEqual(returnSteps('received'), ['inspect']);
  for (const s of ['inspected', 'rejected', 'withdrawn', 'refunded', 'credit_noted', 'x']) { assert.deepEqual(returnSteps(s), [], s); assert.ok(noReturnStepText(s).length > 10); }
  assert.equal(canManageReturns(['orders.view_all', 'Returns.Manage']), true);
  assert.equal(canManageReturns(['orders.view_all']), false);
});

test('inspection: accepted <= asked, restock <= accepted, whole numbers, note length', () => {
  const row = (accepted: string, restock: string, note = '') => ({ returnLineId: 'l1', asked: 3, accepted, restock, note });
  assert.deepEqual(inspectionProblems([row('3', '3'), row('0', '0'), row(' 2 ', '1')]), {});
  assert.ok(inspectionProblems([row('4', '0')]).l1.accepted);
  assert.ok(inspectionProblems([row('2', '3')]).l1.restock);
  assert.ok(inspectionProblems([row('-1', '0')]).l1.accepted);
  assert.ok(inspectionProblems([row('1.5', '0')]).l1.accepted);
  assert.ok(inspectionProblems([row('', '')]).l1.restock);
  assert.ok(inspectionProblems([row('1', '1', 'x'.repeat(501))]).l1.note);
  assert.deepEqual(inspectBody(7, [row('2', '1', '  burnt ')]), { rowVersion: 7, lines: [{ returnLineId: 'l1', acceptedQty: 2, restockQty: 1, note: 'burnt' }] });
  assert.equal(inspectBody(7, [row('0', '0')]).lines[0].note, null);
});

test('staff errors: stale and wrong-state reload and explain', () => {
  assert.equal(shouldReloadReturn('RETURN_CHANGED'), true);
  assert.equal(shouldReloadReturn('RETURN_INVALID_TRANSITION'), true);
  assert.equal(shouldReloadReturn('VALIDATION_ERROR'), false);
  assert.match(staffReturnErrorText({ status: 409, code: 'RETURN_CHANGED' }), /changed/);
  assert.match(staffReturnErrorText({ status: 409, code: 'RETURN_INVALID_TRANSITION' }), /no longer/);
  assert.match(staffReturnErrorText({ status: 403, code: null }), /returns\.manage/);
  assert.equal(staffReturnErrorText({ status: 400, code: null, fields: { reason: 'Too short.' } }), 'Too short.');
});

test('filters: round trip through the address, junk status dropped, query only what is set', () => {
  const f = { status: 'approved', orderNumber: ' MW-1 ', companyId: 'c1', companyName: 'Acme', page: 3 };
  const q = returnFiltersToSearch(f);
  const back = parseReturnFilters((k) => new URLSearchParams(q).get(k));
  assert.deepEqual(back, { ...f, orderNumber: 'MW-1' });
  assert.equal(returnFiltersToSearch(EMPTY_RETURN_FILTERS), '');
  assert.equal(parseReturnFilters((k) => (k === 'status' ? 'bogus' : k === 'page' ? '-4' : null)).status, '');
  assert.equal(returnListQuery(EMPTY_RETURN_FILTERS), 'page=1&pageSize=20');
  assert.equal(returnListQuery(f), 'status=approved&orderNumber=MW-1&companyId=c1&page=3&pageSize=20');
});

test('reject credit reduction: only pending, second person, with the permission', () => {
  const r = (o: object = {}) => ({ status: 'inspected', settlement: { kind: 'credit_reduction', amountMinor: 100, requestedBy: 'AA', requestedAtUtc: '', creditNoteRequestId: null, refundId: null, settledBy: null, settledAtUtc: null, ...o } });
  const perm = ['payments.refund_approve'];
  assert.deepEqual(canRejectCreditReduction(r(), { id: 'bb', permissions: perm }), { show: true, canReject: true, hint: null });
  assert.equal(canRejectCreditReduction(r(), { id: 'aa', permissions: perm }).canReject, false);
  assert.equal(canRejectCreditReduction(r(), { id: 'bb', permissions: [] }).canReject, false);
  assert.equal(canRejectCreditReduction(r({ settledAtUtc: 'x' }), { id: 'bb', permissions: perm }).show, false);
  assert.equal(canRejectCreditReduction(r({ kind: 'refund' }), { id: 'bb', permissions: perm }).show, false);
  assert.equal(canRejectCreditReduction({ status: 'inspected', settlement: null }, { id: 'bb', permissions: perm }).show, false);
  assert.match(staffReturnErrorText({ status: 409, code: 'RETURN_FOUR_EYES' }), /another person/);
});

test('photos: count, type and size checked before sending; add Requested|Approved, remove Requested only', () => {
  const ok = { type: 'image/webp', size: 1000 };
  assert.equal(returnPhotoProblem(ok, 4), null);
  assert.equal(returnPhotoProblem(ok, 5), 'full');
  assert.equal(returnPhotoProblem({ type: 'image/gif', size: 10 }, 0), 'type');
  assert.equal(returnPhotoProblem({ type: 'image/png', size: RETURN_PHOTO_BYTES }, 0), null);
  assert.equal(returnPhotoProblem({ type: 'image/png', size: RETURN_PHOTO_BYTES + 1 }, 0), 'size');
  assert.equal(returnPhotoProblem({ type: 'image/png', size: 0 }, 0), 'empty');
  assert.deepEqual(['requested', 'approved', 'received'].map(canAddReturnPhoto), [true, true, false]);
  assert.deepEqual(['requested', 'approved'].map(canRemoveReturnPhoto), [true, false]);
});

test('photo errors: every refusal has its own key', () => {
  assert.equal(returnPhotoErrorKey(409, 'RETURN_PHOTOS_CLOSED'), 'closed');
  assert.equal(returnPhotoErrorKey(409, 'RETURN_PHOTOS_FULL'), 'full');
  assert.equal(returnPhotoErrorKey(413, 'FILE_TOO_LARGE'), 'size');
  assert.equal(returnPhotoErrorKey(400, null), 'invalid');
  assert.equal(returnPhotoErrorKey(503, 'SCAN_UNAVAILABLE'), 'scanDown');
  assert.equal(returnPhotoErrorKey(404, 'NOT_FOUND'), 'notFound');
  assert.equal(returnPhotoErrorKey(null, null), 'unavailable');
});
