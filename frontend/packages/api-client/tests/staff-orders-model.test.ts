import test from 'node:test';
import assert from 'node:assert/strict';
import {
  approveOutcome, approveRefundHint, askableMinor, cancelHint, cancelOutcome, cancelReasonProblem, canApproveRefund, canCancelOrders, canViewRefunds, canViewStaffOrders,
  isCancellableStatus, overdueQuery, overdueReasons, lateByText, refundAmountToMinor, refundRequestProblems, refundStatus, rejectRefundHint, shouldReloadStaffOrders,
  sortRefundQueue, staffOrderErrorText, staffOrderStatusLabel, type CancelledOrder, type RefundQueueItem, type StaffRefund
} from '../src/staff-orders-model.ts';

const refund = (o: Partial<StaffRefund> = {}): StaffRefund => ({
  id: 'r1', paymentId: 'p1', amountMinor: 100000, currency: 'BDT', reason: 'Order cancelled', status: 'Requested', requestedBy: 'a', decidedBy: null,
  requestedAtUtc: '2026-11-17T05:00:00Z', decidedAtUtc: null, providerRefundRef: null, note: null, ...o
});
const item = (o: Partial<RefundQueueItem> = {}): RefundQueueItem => ({
  paymentId: 'p1', orderId: 'o1', orderNumber: 'MW-1', provider: 'bkash', paidMinor: 500000, refundableMinor: 500000, currency: 'BDT', note: null, refunds: [], ...o
});
const cancelled = (o: Partial<CancelledOrder> = {}): CancelledOrder => ({
  orderId: 'o1', orderNumber: 'MW-1', status: 'cancelled', alreadyCancelled: false, creditReleasedMinor: 0, creditMovedOnAccountMinor: 0, refundPaymentIds: [], stockReturned: [], stockReturnPending: false, ...o
});

test('permissions: exact names, any case', () => {
  assert.equal(canViewStaffOrders(['ORDERS.VIEW_ALL']), true);
  assert.equal(canViewStaffOrders(['orders.view_own']), false);
  assert.equal(canCancelOrders(['orders.cancel']), true);
  assert.equal(canCancelOrders(['orders.refund']), false);
  assert.equal(canViewRefunds(['orders.refund']), true);
  assert.equal(canApproveRefund(['payments.refund_approve']), true);
  assert.equal(canApproveRefund(['orders.refund']), false);
});

test('overdue query: page at least 1, page size capped at 50', () => {
  assert.equal(overdueQuery(), 'page=1&pageSize=25');
  assert.equal(overdueQuery({ page: 0, pageSize: 500 }), 'page=1&pageSize=50');
  assert.equal(overdueQuery({ page: 3.9, pageSize: 10 }), 'page=3&pageSize=10');
});

test('overdue text: whole units, rounded down', () => {
  assert.equal(lateByText(0), 'under a minute');
  assert.equal(lateByText(0.5), '30 min');
  assert.equal(lateByText(3.34), '3 h 20 min');
  assert.equal(lateByText(28), '1 day 4 h');
  assert.equal(lateByText(49.9), '2 days 1 h');
  assert.equal(lateByText(-1), '');
  assert.equal(lateByText(Number.NaN), '');
  assert.equal(overdueReasons({ deliveryOverdue: true, handoverOverdue: true }).length, 2);
  assert.deepEqual(overdueReasons({ deliveryOverdue: false, handoverOverdue: false }), []);
});

test('cancellable states and labels', () => {
  for (const s of ['confirmed', 'processing', 'packed']) assert.equal(isCancellableStatus(s), true, s);
  for (const s of ['pending', 'pending_approval', 'shipped', 'delivered', 'cancelled', 'refunded', 'returned']) assert.equal(isCancellableStatus(s), false, s);
  assert.equal(staffOrderStatusLabel('pending_approval'), 'Waiting for approval');
  assert.equal(staffOrderStatusLabel('some_new_state'), 'some new state');
});

test('cancel reason: 5 to 500 after trimming', () => {
  assert.match(cancelReasonProblem('') ?? '', /Enter a reason/);
  assert.match(cancelReasonProblem('   ab  ') ?? '', /5 to 500/);
  assert.equal(cancelReasonProblem('  abcde '), null);
  assert.equal(cancelReasonProblem('x'.repeat(500)), null);
  assert.match(cancelReasonProblem('x'.repeat(501)) ?? '', /5 to 500/);
  assert.match(cancelReasonProblem(null) ?? '', /Enter a reason/);
});

test('cancel hint: permission first, then state', () => {
  assert.equal(cancelHint({ status: 'confirmed' }, ['orders.cancel']).allowed, true);
  assert.equal(cancelHint({ status: 'confirmed' }, ['orders.view_all']).allowed, false);
  const shipped = cancelHint({ status: 'shipped' }, ['orders.cancel']);
  assert.equal(shipped.allowed, false);
  assert.match(shipped.reason, /shipped order cannot be cancelled/);
});

test('cancel outcome: credit, on account, stock, refunds, pending stock, replay', () => {
  const plain = cancelOutcome(cancelled());
  assert.deepEqual(plain.lines, ['Order MW-1 is cancelled.']);
  assert.deepEqual(plain.warnings, []);
  const credit = cancelOutcome(cancelled({ creditReleasedMinor: 1234500, creditMovedOnAccountMinor: 50, stockReturned: [{ variantId: 'v', quantity: 2 }, { variantId: 'w', quantity: 1 }] }));
  assert.ok(credit.lines.some((l) => l.includes('BDT 12,345.00 of credit was released')));
  assert.ok(credit.lines.some((l) => l.includes('BDT 0.50 the company had already paid is now on account')));
  assert.ok(credit.lines.some((l) => l.startsWith('3 units of stock went back to the warehouses')));
  const pending = cancelOutcome(cancelled({ stockReturnPending: true, refundPaymentIds: ['p1'] }));
  assert.equal(pending.warnings.length, 2);
  assert.match(pending.warnings[0], /Cancel it again/);
  assert.match(pending.warnings[1], /1 payment is now in the refund queue/);
  assert.equal(pending.needsRefund, true);
  const again = cancelOutcome(cancelled({ alreadyCancelled: true, stockReturned: [{ variantId: 'v', quantity: 1 }] }));
  assert.match(again.lines[0], /already cancelled. Nothing else changed/);
  assert.equal(again.lines.length, 2);
});

test('refund status: PascalCase from the server, unknown falls back to requested', () => {
  assert.equal(refundStatus({ status: 'Completed' }), 'completed');
  assert.equal(refundStatus({ status: 'FAILED' }), 'failed');
  assert.equal(refundStatus({ status: 'weird' }), 'requested');
});

test('askable: rejected refunds free their amount, others hold it', () => {
  assert.equal(askableMinor(item()), 500000);
  assert.equal(askableMinor(item({ refunds: [refund({ amountMinor: 200000 }), refund({ amountMinor: 100000, status: 'Rejected' })] })), 300000);
  assert.equal(askableMinor(item({ refunds: [refund({ amountMinor: 900000, status: 'Failed' })] })), 0);
});

test('refund amount: string rules, never a float', () => {
  assert.equal(refundAmountToMinor('2,500.5'), 250050);
  assert.equal(refundAmountToMinor('0.07'), 7);
  for (const bad of ['', '1.234', '-5', '1e3', 'abc', '.5', '5.']) assert.equal(refundAmountToMinor(bad), null, bad);
});

test('refund request form: amount cap and reason checked before sending', () => {
  assert.deepEqual(refundRequestProblems({ amountBdt: '1000', reason: 'Order cancelled' }, 500000), {});
  assert.match(refundRequestProblems({ amountBdt: '', reason: 'ok ok' }, 500000).amountBdt, /Enter the amount/);
  assert.match(refundRequestProblems({ amountBdt: '1.234', reason: 'ok ok' }, 500000).amountBdt, /2 decimals/);
  assert.match(refundRequestProblems({ amountBdt: '0', reason: 'ok ok' }, 500000).amountBdt, /more than zero/);
  assert.match(refundRequestProblems({ amountBdt: '5000.01', reason: 'ok ok' }, 500000).amountBdt, /At most BDT 5,000.00/);
  assert.match(refundRequestProblems({ amountBdt: '1', reason: 'ok ok' }, 0).amountBdt, /already asked for or refunded/);
  assert.match(refundRequestProblems({ amountBdt: '1', reason: 'abc' }, 100).reason, /5 to 500/);
  assert.match(refundRequestProblems({ amountBdt: '1', reason: '' }, 100).reason, /Enter a reason/);
});

test('approve hint: permission, four eyes, state', () => {
  const me = { id: 'B', permissions: ['payments.refund_approve'] };
  assert.equal(approveRefundHint(refund(), me).allowed, true);
  assert.equal(approveRefundHint(refund({ status: 'Failed' }), me).allowed, true);
  assert.equal(approveRefundHint(refund({ requestedBy: 'b' }), me).allowed, false);
  assert.match(approveRefundHint(refund({ requestedBy: 'b' }), me).reason, /another approver/);
  assert.equal(approveRefundHint(refund({ status: 'Completed' }), me).allowed, false);
  assert.equal(approveRefundHint(refund(), { id: 'B', permissions: ['orders.refund'] }).allowed, false);
  assert.equal(rejectRefundHint(refund({ requestedBy: 'b' }), me.permissions).allowed, true);
  assert.equal(rejectRefundHint(refund({ status: 'Rejected' }), me.permissions).allowed, false);
  assert.equal(rejectRefundHint(refund(), ['orders.refund']).allowed, false);
});

test('approve outcome: a failed provider is a 200 that must read as a problem', () => {
  assert.deepEqual(approveOutcome({ status: 'Completed', amountMinor: 250000, note: null }), { text: 'Refunded BDT 2,500.00.', problem: false });
  const failed = approveOutcome({ status: 'Failed', amountMinor: 1, note: 'Provider down' });
  assert.equal(failed.problem, true);
  assert.match(failed.text, /Provider down.*Approve again to retry/);
  assert.equal(approveOutcome({ status: 'Processing', amountMinor: 1, note: null }).problem, true);
});

test('refund queue: payments with a decision waiting come first', () => {
  const sorted = sortRefundQueue([
    item({ orderNumber: 'MW-3', paidMinor: 100, refunds: [refund({ amountMinor: 100, status: 'Completed' })] }),
    item({ orderNumber: 'MW-2' }),
    item({ orderNumber: 'MW-9', refunds: [refund()] })
  ]);
  assert.deepEqual(sorted.map((i) => i.orderNumber), ['MW-9', 'MW-2', 'MW-3']);
});

test('errors: known codes, reload codes, status fallbacks', () => {
  const e = (status: number | null, code: string | null = null, fields: Record<string, string> = {}) => staffOrderErrorText({ status, code, message: 'm', fields });
  assert.match(e(409, 'ORDER_NOT_CANCELLABLE'), /can no longer be cancelled/);
  assert.match(e(409, 'REFUND_FOUR_EYES'), /cannot approve it/);
  assert.match(e(400, null, { reason: 'Give a reason of 5 to 500 characters.' }), /5 to 500/);
  assert.match(e(null), /cannot be reached/);
  assert.match(e(403), /permission/);
  assert.match(e(404), /not found/);
  assert.match(e(500), /server had a problem/);
  assert.equal(shouldReloadStaffOrders('ORDER_NOT_CANCELLABLE'), true);
  assert.equal(shouldReloadStaffOrders('STEP_UP_FAILED'), false);
  assert.equal(shouldReloadStaffOrders(null), false);
});
