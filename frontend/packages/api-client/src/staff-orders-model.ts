/**
 * Staff order screens (G5.1): orders past a legal deadline, cancel of a confirmed order (E7.2) and the refund queue (E4).
 * Pure shapes and helpers, no runtime imports (Node's type stripping must be able to test it). Hand-written from
 * AdminOrdersController, AdminRefundsController and their contracts (the OpenAPI document has no response bodies).
 * Everything here only helps the person: the API checks permission, state, four eyes and the authenticator code again,
 * and its answer always wins. A code is spent even when the call is then refused, so every rule that can be checked
 * on screen is checked BEFORE a code is asked for.
 */

export const STAFF_ORDER_PERMISSIONS = {
  viewAll: 'orders.view_all',
  cancel: 'orders.cancel',
  refund: 'orders.refund',
  refundApprove: 'payments.refund_approve'
} as const;

const holds = (permissions: readonly string[], p: string): boolean => permissions.some((x) => x.toLowerCase() === p);
export const canViewStaffOrders = (permissions: readonly string[]): boolean => holds(permissions, STAFF_ORDER_PERMISSIONS.viewAll);
export const canCancelOrders = (permissions: readonly string[]): boolean => holds(permissions, STAFF_ORDER_PERMISSIONS.cancel);
export const canViewRefunds = (permissions: readonly string[]): boolean => holds(permissions, STAFF_ORDER_PERMISSIONS.refund);
export const canRequestRefund = canViewRefunds;
export const canApproveRefund = (permissions: readonly string[]): boolean => holds(permissions, STAFF_ORDER_PERMISSIONS.refundApprove);

export interface StaffPage<T> { items: T[]; page: number; pageSize: number; totalCount: number }

function formatMinor(minor: number): string {
  const abs = Math.abs(minor);
  return `${minor < 0 ? '-' : ''}${Math.floor(abs / 100).toLocaleString('en-US')}.${String(abs % 100).padStart(2, '0')}`;
}

// ---------- overdue orders ----------

export const OVERDUE_PAGE_SIZE = 25;

/** `OverdueOrderDto`: no personal data, only number, status, district and deadlines. Status is the wire name (snake case). */
export interface OverdueOrder {
  orderId: string;
  orderNumber: string;
  status: string;
  districtId: string | null;
  district: string | null;
  legalDeliveryDeadlineUtc: string;
  courierHandoverDeadlineUtc: string;
  deliveryOverdue: boolean;
  handoverOverdue: boolean;
  hoursOverdue: number;
}

export function overdueQuery(q: { page?: number; pageSize?: number } = {}): string {
  const p = new URLSearchParams();
  p.set('page', String(Math.max(1, Math.trunc(q.page ?? 1))));
  p.set('pageSize', String(Math.min(50, Math.max(1, Math.trunc(q.pageSize ?? OVERDUE_PAGE_SIZE)))));
  return p.toString();
}

export const STAFF_ORDER_STATUS_LABEL: Record<string, string> = {
  pending: 'Waiting for payment', pending_approval: 'Waiting for approval', confirmed: 'Confirmed', processing: 'Processing', packed: 'Packed',
  shipped: 'Shipped', delivered: 'Delivered', cancelled: 'Cancelled', refunded: 'Refunded', returned: 'Returned'
};

export function staffOrderStatusLabel(status: string): string {
  return STAFF_ORDER_STATUS_LABEL[status] ?? status.replace(/_/g, ' ');
}

/** Only these states can be cancelled by staff (E7.2: nothing has left the warehouse). */
export const CANCELLABLE_STATUSES: readonly string[] = ['confirmed', 'processing', 'packed'];
export const isCancellableStatus = (status: string): boolean => CANCELLABLE_STATUSES.includes(status);

/** "3 h 20 min", "2 days 4 h"; under a minute says so. Whole numbers only, rounded down. */
export function lateByText(hours: number): string {
  if (!Number.isFinite(hours) || hours < 0) return '';
  const totalMinutes = Math.floor(hours * 60);
  if (totalMinutes < 1) return 'under a minute';
  const days = Math.floor(totalMinutes / 1440);
  const h = Math.floor((totalMinutes % 1440) / 60);
  const m = totalMinutes % 60;
  if (days > 0) return `${days} ${days === 1 ? 'day' : 'days'} ${h} h`;
  return h > 0 ? `${h} h ${m} min` : `${m} min`;
}

/** Which deadline is missed, in words, for the table cell. */
export function overdueReasons(o: Pick<OverdueOrder, 'deliveryOverdue' | 'handoverOverdue'>): string[] {
  return [...(o.deliveryOverdue ? ['Past the legal delivery deadline'] : []), ...(o.handoverOverdue ? ['Past the courier handover deadline'] : [])];
}

export const shortWhenUtc = (iso: string | null | undefined): string => (iso ? `${iso.replace('T', ' ').slice(0, 16)} UTC` : '');

// ---------- cancel ----------

export const CANCEL_REASON_MIN = 5;
export const CANCEL_REASON_MAX = 500;

/** 5 to 500 characters after trimming. Check it BEFORE asking for a code: a code is spent even when the call is refused. */
export function cancelReasonProblem(reason: string | null | undefined): string | null {
  const n = (reason ?? '').trim().length;
  if (n === 0) return 'Enter a reason.';
  return n < CANCEL_REASON_MIN || n > CANCEL_REASON_MAX ? `The reason must be ${CANCEL_REASON_MIN} to ${CANCEL_REASON_MAX} characters.` : null;
}

export interface StaffHint { allowed: boolean; reason: string }

/** Hint for the Cancel button; the server decides. */
export function cancelHint(order: Pick<OverdueOrder, 'status'>, permissions: readonly string[]): StaffHint {
  if (!canCancelOrders(permissions)) return { allowed: false, reason: 'Your role may not cancel orders.' };
  if (!isCancellableStatus(order.status)) return { allowed: false, reason: `A ${staffOrderStatusLabel(order.status).toLowerCase()} order cannot be cancelled here.` };
  return { allowed: true, reason: '' };
}

export interface ReturnedStock { variantId: string; quantity: number }

/** `CancelledOrderDto`, the body of `POST /api/v1/admin/orders/{id}/cancel`. */
export interface CancelledOrder {
  orderId: string;
  orderNumber: string;
  status: string;
  alreadyCancelled: boolean;
  creditReleasedMinor: number;
  creditMovedOnAccountMinor: number;
  refundPaymentIds: string[];
  stockReturned: ReturnedStock[];
  stockReturnPending: boolean;
}

export interface CancelOutcome {
  /** Words for the green notice. */
  lines: string[];
  /** Words for a warning that needs follow-up (role=alert); empty when there is nothing to do. */
  warnings: string[];
  needsRefund: boolean;
}

/** What the cancel did, in plain sentences. An already-cancelled order says nothing else changed. */
export function cancelOutcome(c: CancelledOrder): CancelOutcome {
  const lines: string[] = [];
  const warnings: string[] = [];
  if (c.alreadyCancelled) {
    lines.push(`Order ${c.orderNumber} was already cancelled. Nothing else changed.`);
  } else {
    lines.push(`Order ${c.orderNumber} is cancelled.`);
    if (c.creditReleasedMinor > 0) lines.push(`BDT ${formatMinor(c.creditReleasedMinor)} of credit was released: the company owes that much less.`);
    if (c.creditMovedOnAccountMinor > 0) lines.push(`BDT ${formatMinor(c.creditMovedOnAccountMinor)} the company had already paid is now on account.`);
  }
  const units = c.stockReturned.reduce((s, x) => s + x.quantity, 0);
  if (units > 0) lines.push(`${units} ${units === 1 ? 'unit' : 'units'} of stock went back to the warehouse${c.stockReturned.length > 1 ? 's' : ''} it came from.`);
  if (c.stockReturnPending) warnings.push('The order is cancelled, but putting its stock back failed. Cancel it again to finish the stock return; nothing else will change.');
  if (c.refundPaymentIds.length > 0) {
    warnings.push(`${c.refundPaymentIds.length} ${c.refundPaymentIds.length === 1 ? 'payment is' : 'payments are'} now in the refund queue. Money goes back only after a refund is requested and a second person approves it.`);
  }
  return { lines, warnings, needsRefund: c.refundPaymentIds.length > 0 };
}

// ---------- refunds ----------

export type RefundStatus = 'requested' | 'processing' | 'completed' | 'rejected' | 'failed';
export const REFUND_REASON_MIN = 5;
export const REFUND_REASON_MAX = 500;

/** `RefundDto`. The server sends the status in PascalCase ("Requested"); `refundStatus` lower-cases it. */
export interface StaffRefund {
  id: string;
  paymentId: string;
  amountMinor: number;
  currency: string;
  reason: string;
  status: string;
  requestedBy: string;
  decidedBy: string | null;
  requestedAtUtc: string;
  decidedAtUtc: string | null;
  providerRefundRef: string | null;
  note: string | null;
}

/** `RefundQueueItem`: a payment holding money that must go back, with the refunds asked for so far. */
export interface RefundQueueItem {
  paymentId: string;
  orderId: string;
  orderNumber: string;
  provider: string;
  paidMinor: number;
  refundableMinor: number;
  currency: string;
  note: string | null;
  refunds: StaffRefund[];
}

export function refundStatus(r: Pick<StaffRefund, 'status'>): RefundStatus {
  const s = r.status.toLowerCase();
  return (['requested', 'processing', 'completed', 'rejected', 'failed'] as const).find((x) => x === s) ?? 'requested';
}

export const REFUND_STATUS_LABEL: Record<RefundStatus, string> = {
  requested: 'Waiting for a second person', processing: 'Sent to the provider, outcome not recorded', completed: 'Refunded', rejected: 'Rejected', failed: 'The provider did not refund'
};

/** Money still free to ask for: the payment's refundable amount minus refunds that still hold an amount (everything not rejected). */
export function askableMinor(item: Pick<RefundQueueItem, 'paidMinor' | 'refunds'>): number {
  const held = item.refunds.filter((r) => refundStatus(r) !== 'rejected').reduce((s, r) => s + r.amountMinor, 0);
  return Math.max(0, item.paidMinor - held);
}

/** BDT text to poisha without floats: "5000", "5,000.5", "5000.50". A third decimal, a sign, an exponent or letters give null. */
export function refundAmountToMinor(text: string): number | null {
  const m = /^(\d{1,12})(?:\.(\d{1,2}))?$/.exec(text.trim().replace(/,/g, '').replace(/^৳\s*/, ''));
  return m ? Number(m[1]) * 100 + Number((m[2] ?? '').padEnd(2, '0') || '0') : null;
}

export interface RefundRequestForm { amountBdt: string; reason: string }

/** Problems found before anything is sent, keyed by field (empty = fine). The server's cap (REFUND_EXCEEDS_PAYMENT) still wins. */
export function refundRequestProblems(f: RefundRequestForm, askable: number): Record<string, string> {
  const out: Record<string, string> = {};
  const minor = refundAmountToMinor(f.amountBdt);
  if (f.amountBdt.trim() === '') out.amountBdt = 'Enter the amount in BDT.';
  else if (minor === null) out.amountBdt = 'Use digits and at most 2 decimals, for example 2500 or 2,500.50.';
  else if (minor < 1) out.amountBdt = 'The amount must be more than zero.';
  else if (minor > askable) out.amountBdt = askable === 0 ? 'Everything on this payment is already asked for or refunded.' : `At most BDT ${formatMinor(askable)} can still be asked for on this payment.`;
  const n = f.reason.trim().length;
  if (n === 0) out.reason = 'Enter a reason.';
  else if (n < REFUND_REASON_MIN || n > REFUND_REASON_MAX) out.reason = `The reason must be ${REFUND_REASON_MIN} to ${REFUND_REASON_MAX} characters.`;
  return out;
}

export const refundReasonProblem = (reason: string | null | undefined): string | null => {
  const n = (reason ?? '').trim().length;
  if (n === 0) return 'Enter a reason.';
  return n < REFUND_REASON_MIN || n > REFUND_REASON_MAX ? `The reason must be ${REFUND_REASON_MIN} to ${REFUND_REASON_MAX} characters.` : null;
};

const sameId = (a: string | null | undefined, b: string | null | undefined): boolean => !!a && !!b && a.toLowerCase() === b.toLowerCase();

/** Approve needs payments.refund_approve and a person other than the requester; only a requested or failed refund can be approved. */
export function approveRefundHint(refund: Pick<StaffRefund, 'status' | 'requestedBy'>, me: { id: string; permissions: readonly string[] }): StaffHint {
  if (!canApproveRefund(me.permissions)) return { allowed: false, reason: 'Your role may not approve refunds.' };
  const s = refundStatus(refund);
  if (s !== 'requested' && s !== 'failed') return { allowed: false, reason: `A ${REFUND_STATUS_LABEL[s].toLowerCase()} refund cannot be approved.` };
  if (sameId(refund.requestedBy, me.id)) return { allowed: false, reason: 'You asked for this refund, so another approver must approve it.' };
  return { allowed: true, reason: '' };
}

/** Reject needs payments.refund_approve; only a requested or failed refund can be rejected (no four eyes on a rejection). */
export function rejectRefundHint(refund: Pick<StaffRefund, 'status'>, permissions: readonly string[]): StaffHint {
  if (!canApproveRefund(permissions)) return { allowed: false, reason: 'Your role may not reject refunds.' };
  const s = refundStatus(refund);
  if (s !== 'requested' && s !== 'failed') return { allowed: false, reason: `A ${REFUND_STATUS_LABEL[s].toLowerCase()} refund cannot be rejected.` };
  return { allowed: true, reason: '' };
}

/**
 * Approve answers 200 with the refund even when the provider failed: read the refund's status, not the HTTP status.
 * Returns the words for the notice and whether it is a problem (role=alert).
 */
export function approveOutcome(r: Pick<StaffRefund, 'status' | 'amountMinor' | 'note'>): { text: string; problem: boolean } {
  switch (refundStatus(r)) {
    case 'completed': return { text: `Refunded BDT ${formatMinor(r.amountMinor)}.`, problem: false };
    case 'failed': return { text: `The provider did not refund${r.note ? `: ${r.note}` : '.'} Approve again to retry; the provider will not pay twice.`, problem: true };
    case 'processing': return { text: 'The refund was sent to the provider but its outcome is not recorded yet. Check again in a moment.', problem: true };
    default: return { text: `The refund is now ${REFUND_STATUS_LABEL[refundStatus(r)].toLowerCase()}.`, problem: false };
  }
}

/** Queue order: payments with something to decide first, then by order number. */
export function sortRefundQueue(items: readonly RefundQueueItem[]): RefundQueueItem[] {
  const rank = (i: RefundQueueItem) => (i.refunds.some((r) => ['requested', 'failed'].includes(refundStatus(r))) ? 0 : askableMinor(i) > 0 ? 1 : 2);
  return [...items].sort((a, b) => rank(a) - rank(b) || a.orderNumber.localeCompare(b.orderNumber));
}

// ---------- errors ----------

export interface StaffOrderErrorLike { status: number | null; code: string | null; message: string; fields: Record<string, string> }

export const STAFF_ORDER_ERROR_TEXT: Record<string, string> = {
  ORDER_NOT_CANCELLABLE: 'This order can no longer be cancelled here. Only a confirmed, processing or packed order that has not shipped can be. The list was reloaded.',
  REFUND_FOUR_EYES: 'You asked for this refund, so you cannot approve it. Another approver must.',
  REFUND_NOT_APPROVABLE: 'Someone else already decided this refund. The queue was reloaded.',
  REFUND_NOT_REJECTABLE: 'Someone else already decided this refund. The queue was reloaded.',
  REFUND_NOT_PROCESSING: 'The refund is not in a state that can be finished. The queue was reloaded.',
  REFUND_EXCEEDS_PAYMENT: 'That is more than this payment still holds. Lower the amount.',
  PAYMENT_NOT_REFUNDABLE: 'This payment holds no money to send back (it may already be refunded). The queue was reloaded.',
  PROVIDER_UNAVAILABLE: 'The payment provider is not set up here, so it cannot refund.',
  STEP_UP_REQUIRED: 'This action needs the 6-digit code from your authenticator app.',
  STEP_UP_FAILED: 'The code was not accepted. Codes work once: wait for the next code (up to 30 seconds) and enter that one.'
};

/** Codes after which what the person saw is out of date, so the screen reloads. */
export const STAFF_ORDER_RELOAD_CODES: ReadonlySet<string> = new Set([
  'ORDER_NOT_CANCELLABLE', 'REFUND_NOT_APPROVABLE', 'REFUND_NOT_REJECTABLE', 'REFUND_NOT_PROCESSING', 'PAYMENT_NOT_REFUNDABLE'
]);

export const isStaffStepUpFailure = (code: string | null | undefined): boolean => code === 'STEP_UP_FAILED' || code === 'STEP_UP_REQUIRED';
export const shouldReloadStaffOrders = (code: string | null | undefined): boolean => !!code && STAFF_ORDER_RELOAD_CODES.has(code);

export function staffOrderErrorText(e: StaffOrderErrorLike): string {
  if (e.code && STAFF_ORDER_ERROR_TEXT[e.code]) return STAFF_ORDER_ERROR_TEXT[e.code];
  const first = Object.values(e.fields)[0];
  if (e.status === 400 && first) return first;
  if (e.status === null) return 'The server cannot be reached. Check your connection and try again.';
  if (e.status === 401) return 'Your session ended. Sign in again.';
  if (e.status === 403) return 'You do not have permission for this action.';
  if (e.status === 404) return 'This order, payment or refund was not found.';
  if (e.status === 429) return 'Too many requests. Wait a moment and try again.';
  if (e.status >= 500) return 'The server had a problem. Try again in a moment; if it keeps happening, tell the technical team.';
  return e.message || 'The request was refused.';
}
