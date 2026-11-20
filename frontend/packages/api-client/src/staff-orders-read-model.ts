/**
 * Staff order list and detail (G5.6): the shapes of `GET admin/orders` and `GET admin/orders/{id}` (docs/modules/G.md HANDOFF G5.5) and the pure helpers
 * of the filter bar (URL round trip, checks before a request) and of the words on the detail. No runtime imports (Node's type stripping must run it).
 * The API checks every filter again and its answer wins; nothing here is a permission check.
 */

export const ORDER_LIST_PAGE_SIZE = 25;

export interface StaffOrderListItem {
  id: string; orderNumber: string; status: string; companyId: string; companyName: string; grandTotalMinor: number; paymentState: string;
  placedAtUtc: string; legalDeliveryDeadlineUtc: string | null; courierHandoverDeadlineUtc: string | null; deliveryOverdue: boolean; handoverOverdue: boolean;
  source: 'cart' | 'quote'; quoteNumber: string | null;
}

export interface StaffPriceSnapshot {
  source: string | null; basis: string | null; priceListId: string | null; priceListVersion: number | null; contractRef: string | null;
  publicUnitPriceMinor: number | null; quoteLineId: string | null;
}
export interface StaffOrderLine {
  variantId: string; sku: string; title: string; quantity: number; unitPriceMinor: number; vatRate: number; grossMinor: number; discountMinor: number;
  netMinor: number; taxMinor: number; totalMinor: number; priceSnapshot: StaffPriceSnapshot;
}
export interface StaffSubOrder { id: string; subOrderNumber: string; sellerId: string; storeName: string; status: string; shippingMinor: number; totalMinor: number; lines: StaffOrderLine[] }
export interface StaffOrderEvent { sequence: number; fromStatus: string | null; toStatus: string; actorId: string | null; reason: string | null; atUtc: string }
export interface StaffOrderRefund { id: string; amountMinor: number; status: string; reason: string; requestedBy: string | null; decidedBy: string | null; decidedAtUtc: string | null; createdAtUtc: string }
export interface StaffOrderPayment { id: string; method: string; status: string; amountMinor: number; currency: string; createdAtUtc: string; capturedAtUtc: string | null; refunds: StaffOrderRefund[] }
export interface StaffShipTo { recipientName: string; line1: string; line2: string | null; upazila: string | null; district: string; division: string; postalCode: string | null; phone: string; siteName: string | null }

export interface StaffOrderDetail {
  id: string; orderNumber: string; status: string; currency: string; taxMode: string; placedAtUtc: string; confirmedAtUtc: string | null; paymentDueAtUtc: string | null;
  legalDeliveryDeadlineUtc: string | null; courierHandoverDeadlineUtc: string | null; deadlineSameCity: boolean | null; purchaseOrderNumber: string | null;
  subtotalMinor: number; discountMinor: number; taxMinor: number; shippingMinor: number; grandTotalMinor: number; source: 'cart' | 'quote';
  quote: { quoteId: string; number: string; versionId: string } | null; companyId: string; companyName: string;
  buyer: { userId: string; name: string; phone: string | null }; shipTo: StaffShipTo | null; paymentState: string; paysOnCredit: boolean;
  credit: { status: string; dueOn: string | null; chargedMinor: number; outstandingMinor: number } | null;
  subOrders: StaffSubOrder[]; events: StaffOrderEvent[]; payments: StaffOrderPayment[];
}

// ---------- words ----------

const sentence = (s: string): string => { const t = s.replace(/_/g, ' ').trim(); return t ? t[0]!.toUpperCase() + t.slice(1) : ''; };

/** Every wire status of an order, in the order of its life, for the filter. */
export const ORDER_STATUS_FILTERS: ReadonlyArray<{ value: string; label: string }> = [
  ['pending_approval', 'Waiting for approval'], ['pending_payment', 'Waiting for payment'], ['confirmed', 'Confirmed'], ['processing', 'Processing'], ['packed', 'Packed'],
  ['shipped', 'Shipped'], ['out_for_delivery', 'Out for delivery'], ['delivered', 'Delivered'], ['completed', 'Completed'], ['cancelled', 'Cancelled'],
  ['delivery_failed', 'Delivery failed'], ['return_requested', 'Return requested'], ['return_approved', 'Return approved'], ['returned', 'Returned'], ['refunded', 'Refunded']
].map(([value, label]) => ({ value: value!, label: label! }));

export const orderStatusText = (status: string | null | undefined): string => ORDER_STATUS_FILTERS.find((s) => s.value === status)?.label ?? sentence(status ?? '');

export const PAYMENT_STATE_FILTERS: ReadonlyArray<{ value: string; label: string }> = [
  { value: 'none', label: 'No payment' }, { value: 'on_credit', label: 'On credit' }, { value: 'pending', label: 'Payment pending' }, { value: 'captured', label: 'Paid' },
  { value: 'failed', label: 'Payment failed' }, { value: 'needs_refund', label: 'Needs a refund' }, { value: 'refunded', label: 'Refunded' }
];
export const paymentStateText = (state: string | null | undefined): string => PAYMENT_STATE_FILTERS.find((s) => s.value === state)?.label ?? sentence(state ?? '');

export const OVERDUE_FILTERS: ReadonlyArray<{ value: string; label: string }> = [{ value: '', label: 'Any' }, { value: 'true', label: 'Overdue only' }, { value: 'false', label: 'Not overdue' }];

export const CREDIT_STATE_LABEL: Record<string, string> = { open: 'Open', partially_paid: 'Partly paid', paid: 'Paid', overdue: 'Overdue', cancelled: 'Cancelled' };
export const creditStateText = (s: string): string => CREDIT_STATE_LABEL[s] ?? sentence(s);

/** "Overdue" words for a list row: which deadline is missed, or an empty list. */
export function listOverdueWords(o: Pick<StaffOrderListItem, 'deliveryOverdue' | 'handoverOverdue'>): string[] {
  return [...(o.deliveryOverdue ? ['Late for delivery'] : []), ...(o.handoverOverdue ? ['Late for courier handover'] : [])];
}

const SOURCE_LABEL: Record<string, string> = { list: 'Price list', sale: 'Sale price', quantity_break: 'Quantity break', group_list: 'Group price list', contract: 'Contract price', quote: 'Quote price' };
const BASIS_LABEL: Record<string, string> = { gross_incl_vat: 'incl. VAT', net_of_vat: 'excl. VAT' };

/** How a line's unit price came about: "Contract price (ref C-7), excl. VAT". Missing parts are left out. */
export function priceSnapshotText(s: StaffPriceSnapshot): string {
  const head = s.source ? (SOURCE_LABEL[s.source] ?? sentence(s.source)) : 'Price at order time';
  const bits = [s.contractRef ? `ref ${s.contractRef}` : '', s.priceListVersion != null ? `list version ${s.priceListVersion}` : ''].filter(Boolean);
  return `${head}${bits.length ? ` (${bits.join(', ')})` : ''}${s.basis ? `, ${BASIS_LABEL[s.basis] ?? sentence(s.basis)}` : ''}`;
}

/** Payment attempt status, in words (the wire name when unknown). */
export const paymentStatusText = (status: string): string => ({ pending: 'Pending', captured: 'Paid', failed: 'Failed', needs_refund: 'Needs a refund', refunded: 'Refunded' } as Record<string, string>)[status] ?? sentence(status);

export const timelineText = (e: Pick<StaffOrderEvent, 'fromStatus' | 'toStatus'>): string =>
  e.fromStatus ? `${orderStatusText(e.fromStatus)} to ${orderStatusText(e.toStatus)}` : `Started as ${orderStatusText(e.toStatus).toLowerCase()}`;

// ---------- filters in the URL ----------

export interface OrderFilters { status: string; paymentState: string; overdue: string; orderNumber: string; companyId: string; companyName: string; from: string; to: string; page: number }
export const EMPTY_ORDER_FILTERS: OrderFilters = { status: '', paymentState: '', overdue: '', orderNumber: '', companyId: '', companyName: '', from: '', to: '', page: 1 };

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const NUMBER_PREFIX = /^[A-Za-z0-9-]{1,40}$/;

/** True for a real calendar date written yyyy-MM-dd. */
export function isDhakaDate(text: string): boolean {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text);
  if (!m) return false;
  const d = new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3])));
  return d.getUTCFullYear() === Number(m[1]) && d.getUTCMonth() === Number(m[2]) - 1 && d.getUTCDate() === Number(m[3]);
}

/** Reads the filters from a query string. A value that is not valid is dropped (a shared link with a typo opens the unfiltered list, not an error). */
export function parseOrderFilters(get: (key: string) => string | null): OrderFilters {
  const pick = (k: string) => (get(k) ?? '').trim();
  const status = pick('status'), paymentState = pick('payment'), overdue = pick('overdue'), orderNumber = pick('number'), companyId = pick('company');
  const from = pick('from'), to = pick('to'), page = Number(pick('page'));
  return {
    status: ORDER_STATUS_FILTERS.some((s) => s.value === status) ? status : '',
    paymentState: PAYMENT_STATE_FILTERS.some((s) => s.value === paymentState) ? paymentState : '',
    overdue: overdue === 'true' || overdue === 'false' ? overdue : '',
    orderNumber: NUMBER_PREFIX.test(orderNumber) ? orderNumber : '',
    companyId: GUID.test(companyId) ? companyId : '',
    companyName: GUID.test(companyId) ? pick('companyName').slice(0, 200) : '',
    from: isDhakaDate(from) ? from : '',
    to: isDhakaDate(to) ? to : '',
    page: Number.isInteger(page) && page >= 1 && page <= 100000 ? page : 1
  };
}

/** The query string of the page URL (defaults are left out, so the plain list has a plain URL). */
export function orderFiltersToSearch(f: OrderFilters): string {
  const p = new URLSearchParams();
  if (f.status) p.set('status', f.status);
  if (f.paymentState) p.set('payment', f.paymentState);
  if (f.overdue) p.set('overdue', f.overdue);
  if (f.orderNumber) p.set('number', f.orderNumber);
  if (f.companyId) { p.set('company', f.companyId); if (f.companyName) p.set('companyName', f.companyName); }
  if (f.from) p.set('from', f.from);
  if (f.to) p.set('to', f.to);
  if (f.page > 1) p.set('page', String(f.page));
  return p.toString();
}

/** A problem to show instead of sending, or null. */
export function orderFilterProblem(input: { orderNumber: string; from: string; to: string }): string | null {
  if (input.orderNumber.trim() && !NUMBER_PREFIX.test(input.orderNumber.trim())) return 'The order number may hold only letters, digits and dashes (up to 40).';
  if (input.from && !isDhakaDate(input.from)) return 'The "from" date is not a valid date.';
  if (input.to && !isDhakaDate(input.to)) return 'The "to" date is not a valid date.';
  if (input.from && input.to && input.from > input.to) return 'The "from" date is after the "to" date.';
  return null;
}

/** The API query for `GET admin/orders`. */
export function orderListQuery(f: OrderFilters): string {
  const p = new URLSearchParams();
  if (f.status) p.set('status', f.status);
  if (f.paymentState) p.set('paymentState', f.paymentState);
  if (f.overdue) p.set('overdue', f.overdue);
  if (f.orderNumber) p.set('orderNumber', f.orderNumber);
  if (f.companyId) p.set('companyId', f.companyId);
  if (f.from) p.set('from', f.from);
  if (f.to) p.set('to', f.to);
  p.set('page', String(Math.max(1, Math.trunc(f.page))));
  p.set('pageSize', String(ORDER_LIST_PAGE_SIZE));
  return p.toString();
}

export const activeOrderFilterCount = (f: OrderFilters): number => [f.status, f.paymentState, f.overdue, f.orderNumber, f.companyId, f.from, f.to].filter(Boolean).length;
