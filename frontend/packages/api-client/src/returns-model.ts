/**
 * Returns (F2.4): wire types and pure helpers for the buyer's return screens and the staff return desk. Hand-written from
 * ReturnContracts.cs (the OpenAPI document has no response bodies). No runtime imports, so Node's type stripping can test it.
 * Everything here only helps the person: the API checks the window, quantities, state and permission again and its answer wins.
 */

export const RETURN_REASONS = ['damaged', 'wrong_item', 'not_as_described', 'other'] as const;
export type ReturnReason = (typeof RETURN_REASONS)[number];
/** Wire statuses (snake case). */
export const RETURN_STATUSES = ['requested', 'approved', 'rejected', 'received', 'inspected', 'refunded', 'credit_noted', 'withdrawn'] as const;
export const RETURN_NOTE_MAX = 1000;
/** The default window; the server's `Returns:WindowDays` decides, this only hides a button that would surely be refused. */
export const RETURN_WINDOW_DAYS = 7;
export const RETURN_PAGE_SIZE = 20;

export interface ReturnLine { orderItemId: string; variantId: string; sku: string; title: string; quantity: number; acceptedQuantity: number | null }
export interface BuyerReturn {
  id: string; orderId: string; orderNumber: string; companyId: string | null; requestedBy: string; status: string; reasonCode: string; note: string | null;
  requestedAtUtc: string; withdrawnAtUtc: string | null; decidedAtUtc: string | null; rejectionReason: string | null; receivedAtUtc: string | null;
  inspectedAtUtc: string | null; lines: ReturnLine[];
}
export interface StaffReturnLine extends ReturnLine { id: string; restockQuantity: number | null; inspectionNote: string | null }
/** How the money goes back (F2.3b). `credit_reduction` waits for a second person at `money-back/approve` or `money-back/reject`. */
export interface ReturnSettlement { kind: string; amountMinor: number | null; requestedBy: string; requestedAtUtc: string; creditNoteRequestId: string | null; refundId: string | null; settledBy: string | null; settledAtUtc: string | null }
export interface StaffReturn extends Omit<BuyerReturn, 'lines'> {
  decidedBy: string | null; receivedBy: string | null; inspectedBy: string | null; restockPending: boolean; rowVersion: number; lines: StaffReturnLine[]; settlement?: ReturnSettlement | null;
}
export interface ReturnPage<T> { items: T[]; page: number; pageSize: number; totalCount: number }

const knownStatus = (s: string | null | undefined): string => ((RETURN_STATUSES as readonly string[]).includes(s ?? '') ? (s as string) : 'unknown');
/** i18n key (under `Returns.status`) for a status; anything unknown gets the generic label. */
export const returnStatusKey = knownStatus;
export function returnStatusTone(status: string): 'pending' | 'progress' | 'done' | 'problem' | 'neutral' {
  switch (status) {
    case 'requested': return 'pending';
    case 'approved': case 'received': case 'inspected': return 'progress';
    case 'refunded': case 'credit_noted': return 'done';
    case 'rejected': return 'problem';
    default: return 'neutral';
  }
}
/** A buyer may withdraw only before staff decide. */
export const canWithdrawReturn = (status: string): boolean => status === 'requested';

// ---------- the window (Dhaka calendar days, UTC+6, no daylight saving) ----------

const DHAKA_MS = 6 * 3600_000;
/** The window closes at Dhaka midnight after the N-th Dhaka calendar day after the delivery day (same rule as the server). */
export function returnWindowEnd(deliveredAtUtc: string, days = RETURN_WINDOW_DAYS): Date | null {
  const t = Date.parse(/(Z|[+-]\d{2}:?\d{2})$/.test(deliveredAtUtc) ? deliveredAtUtc : `${deliveredAtUtc}Z`);
  if (Number.isNaN(t)) return null;
  const d = new Date(t + DHAKA_MS);
  return new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate() + days + 1) - DHAKA_MS);
}

export type ReturnAvailability = 'available' | 'notDelivered' | 'windowClosed' | 'nothingLeft';
/** Whether to offer "Return items". Unknown delivery time (no shipment record) is left to the server. */
export function returnAvailability(
  order: { status: string; shipments?: Array<{ deliveredAtUtc?: string | null }> | null }, left: number, now: Date = new Date(), days = RETURN_WINDOW_DAYS
): ReturnAvailability {
  if (order.status !== 'delivered') return 'notDelivered';
  const times = (order.shipments ?? []).map((s) => (s.deliveredAtUtc ? Date.parse(s.deliveredAtUtc.endsWith('Z') ? s.deliveredAtUtc : `${s.deliveredAtUtc}Z`) : NaN)).filter((n) => !Number.isNaN(n));
  if (times.length > 0) {
    const end = returnWindowEnd(new Date(Math.max(...times)).toISOString(), days);
    if (end && now.getTime() >= end.getTime()) return 'windowClosed';
  }
  return left <= 0 ? 'nothingLeft' : 'available';
}

/** How many of each order line can still be asked back: ordered minus what open or finished returns (not withdrawn, not rejected) already claim. */
export function returnableByLine(lines: Array<{ id?: string; quantity: number }>, returns: Array<Pick<BuyerReturn, 'status' | 'orderId' | 'lines'>>, orderId: string): Record<string, number> {
  const claimed: Record<string, number> = {};
  for (const r of returns) {
    if (r.orderId !== orderId || r.status === 'withdrawn' || r.status === 'rejected') continue;
    for (const l of r.lines) claimed[l.orderItemId] = (claimed[l.orderItemId] ?? 0) + l.quantity;
  }
  const out: Record<string, number> = {};
  for (const l of lines) if (l.id) out[l.id] = Math.max(0, l.quantity - (claimed[l.id] ?? 0));
  return out;
}

// ---------- the buyer's form ----------

export interface ReturnForm { quantities: Record<string, number>; reasonCode: string; note: string }
export type ReturnFormProblem = 'noLines' | 'reason' | 'noteRequired' | 'noteTooLong' | 'quantity';
const cleanNote = (n: string) => n.trim();

/** Message keys (under `Returns.form`) for what is wrong; empty when the form can be sent. `max` is returnableByLine. */
export function returnFormProblems(f: ReturnForm, max: Record<string, number>): ReturnFormProblem[] {
  const out: ReturnFormProblem[] = [];
  const picked = Object.entries(f.quantities).filter(([, q]) => q > 0);
  if (picked.length === 0) out.push('noLines');
  else if (picked.some(([id, q]) => !Number.isInteger(q) || q > (max[id] ?? 0))) out.push('quantity');
  if (!(RETURN_REASONS as readonly string[]).includes(f.reasonCode)) out.push('reason');
  const note = cleanNote(f.note);
  if (f.reasonCode === 'other' && note.length === 0) out.push('noteRequired');
  if (note.length > RETURN_NOTE_MAX) out.push('noteTooLong');
  return out;
}

/** The create body; a company order sends the order's company id, a personal one nothing. */
export function buildReturnBody(f: ReturnForm, companyId?: string | null) {
  const note = cleanNote(f.note);
  return {
    lines: Object.entries(f.quantities).filter(([, q]) => q > 0).map(([orderItemId, quantity]) => ({ orderItemId, quantity })),
    reasonCode: f.reasonCode, note: note || null, ...(companyId ? { companyId } : {})
  };
}

/** Message key (under `Returns.error`) for a failed buyer call. */
export function returnErrorKey(status: number | null, code: string | null): string {
  switch (code) {
    case 'RETURN_WINDOW_CLOSED': return 'windowClosed';
    case 'RETURN_ORDER_NOT_DELIVERED': return 'notDelivered';
    case 'RETURN_LINE_NOT_RETURNABLE': return 'notReturnable';
    case 'RETURN_QUANTITY_EXCEEDED': return 'quantity';
    case 'RETURN_NOT_WITHDRAWABLE': return 'notWithdrawable';
    case 'IDEMPOTENCY_KEY_REUSED': return 'keyReused';
    default: break;
  }
  if (status === 404) return 'notFound';
  if (status === 400 || status === 422) return 'invalid';
  if (status === 401 || status === 403) return 'signedOut';
  return 'unavailable';
}
/** A network error or a 5xx may have been processed: retry with the SAME Idempotency-Key. Any other answer is final: next time a new key. */
export const keepReturnKey = (status: number | null): boolean => status === null || status >= 500;

// ---------- staff desk ----------

export const RETURNS_MANAGE = 'returns.manage';
export const canManageReturns = (permissions: readonly string[]): boolean => permissions.some((p) => p.toLowerCase() === RETURNS_MANAGE);
export const MANAGE_MISSING_TEXT = 'You can read this return, but approving, rejecting, receiving and inspecting need the permission returns.manage (Support and Admin).';

export const STAFF_RETURN_STATUS_LABEL: Record<string, string> = {
  requested: 'Requested, waiting for a decision', approved: 'Approved, waiting for the goods', rejected: 'Rejected', received: 'Goods received, waiting for inspection',
  inspected: 'Inspected, waiting for the refund or credit note', refunded: 'Refunded', credit_noted: 'Credit note issued', withdrawn: 'Withdrawn by the buyer'
};
export const staffReturnStatusText = (s: string): string => STAFF_RETURN_STATUS_LABEL[s] ?? 'Unknown status';
export const STAFF_REASON_LABEL: Record<string, string> = { damaged: 'Damaged', wrong_item: 'Wrong item', not_as_described: 'Not as described', other: 'Other' };

export type ReturnStep = 'approve' | 'reject' | 'received' | 'inspect';
/** The moves open for a status (the API's state graph; it checks again). */
export function returnSteps(status: string): ReturnStep[] {
  return status === 'requested' ? ['approve', 'reject'] : status === 'approved' ? ['received'] : status === 'received' ? ['inspect'] : [];
}
export function noReturnStepText(status: string): string {
  if (status === 'inspected') return 'Inspected. Settling the money (credit note or refund) is a later step; nothing more can be done here yet.';
  if (['rejected', 'withdrawn', 'refunded', 'credit_noted'].includes(status)) return 'This return is closed; nothing more can be done.';
  return 'No step is open for this return.';
}

export interface StaffReturnFilters { status: string; orderNumber: string; companyId: string; companyName: string; page: number }
export const EMPTY_RETURN_FILTERS: StaffReturnFilters = { status: '', orderNumber: '', companyId: '', companyName: '', page: 1 };
export function returnFiltersToSearch(f: StaffReturnFilters): string {
  const p = new URLSearchParams();
  if (f.status) p.set('status', f.status);
  if (f.orderNumber.trim()) p.set('orderNumber', f.orderNumber.trim());
  if (f.companyId) { p.set('companyId', f.companyId); if (f.companyName) p.set('companyName', f.companyName); }
  if (f.page > 1) p.set('page', String(f.page));
  return p.toString();
}
export function parseReturnFilters(get: (k: string) => string | null): StaffReturnFilters {
  const status = get('status') ?? '';
  const page = Number.parseInt(get('page') ?? '1', 10);
  return { status: (RETURN_STATUSES as readonly string[]).includes(status) ? status : '', orderNumber: (get('orderNumber') ?? '').slice(0, 16), companyId: get('companyId') ?? '',
    companyName: get('companyName') ?? '', page: Number.isFinite(page) && page > 0 ? page : 1 };
}
/** Query for `GET /admin/returns`: only what is set. */
export function returnListQuery(f: StaffReturnFilters, pageSize = RETURN_PAGE_SIZE): string {
  const p = new URLSearchParams();
  if (f.status) p.set('status', f.status);
  if (f.orderNumber.trim()) p.set('orderNumber', f.orderNumber.trim());
  if (f.companyId) p.set('companyId', f.companyId);
  p.set('page', String(Math.max(1, f.page)));
  p.set('pageSize', String(Math.min(50, pageSize)));
  return p.toString();
}

export const REJECT_REASON_MIN = 5;
export const REJECT_REASON_MAX = 500;
export const INSPECTION_NOTE_MAX = 500;

export interface InspectionRow { returnLineId: string; asked: number; accepted: string; restock: string; note: string }
export interface InspectionProblem { accepted?: string; restock?: string; note?: string }
const wholeNumber = (s: string): number | null => (/^\d{1,5}$/.test(s.trim()) ? Number.parseInt(s.trim(), 10) : null);

/** Per line: accepted 0..asked, restock 0..accepted, note up to 500. Keyed by returnLineId; empty object = can send. */
export function inspectionProblems(rows: InspectionRow[]): Record<string, InspectionProblem> {
  const out: Record<string, InspectionProblem> = {};
  for (const r of rows) {
    const p: InspectionProblem = {};
    const a = wholeNumber(r.accepted);
    const s = wholeNumber(r.restock);
    if (a === null) p.accepted = 'Enter a whole number, 0 or more.';
    else if (a > r.asked) p.accepted = `At most ${r.asked}, the number the buyer asked to return.`;
    if (s === null) p.restock = 'Enter a whole number, 0 or more.';
    else if (a !== null && s > a) p.restock = `At most ${a}, the number accepted.`;
    if (r.note.trim().length > INSPECTION_NOTE_MAX) p.note = `At most ${INSPECTION_NOTE_MAX} characters.`;
    if (Object.keys(p).length) out[r.returnLineId] = p;
  }
  return out;
}
export function inspectBody(rowVersion: number, rows: InspectionRow[]) {
  return { rowVersion, lines: rows.map((r) => ({ returnLineId: r.returnLineId, acceptedQty: Number.parseInt(r.accepted, 10), restockQty: Number.parseInt(r.restock, 10), note: r.note.trim() || null })) };
}

/** E6.5e: a second person (not the asker) with refund approval may reject a pending credit reduction. */
export function canRejectCreditReduction(r: Pick<StaffReturn, 'status' | 'settlement'>, me: { id: string; permissions: readonly string[] }): { show: boolean; canReject: boolean; hint: string | null } {
  const s = r.settlement;
  if (r.status !== 'inspected' || !s || s.kind !== 'credit_reduction' || s.settledAtUtc) return { show: false, canReject: false, hint: null };
  if (!me.permissions.some((p) => p.toLowerCase() === 'payments.refund_approve')) return { show: true, canReject: false, hint: 'Your role may not decide credit reductions (payments.refund_approve).' };
  if (s.requestedBy.toLowerCase() === me.id.toLowerCase()) return { show: true, canReject: false, hint: 'You asked for this credit reduction, so another person must decide it (two people must agree).' };
  return { show: true, canReject: true, hint: null };
}
export const MONEY_BACK_REJECTED_TEXT = 'The credit reduction was rejected. The return is back to Inspected and the money back can be asked again.';

/** The server's answers that mean the screen shows an old state: reload it. */
export const shouldReloadReturn = (code: string | null): boolean => code === 'RETURN_CHANGED' || code === 'RETURN_INVALID_TRANSITION' || code === 'RETURN_NOT_AWAITING_APPROVAL';

export function staffReturnErrorText(e: { status: number | null; code: string | null; message?: string; fields?: Record<string, string> }): string {
  if (e.code === 'RETURN_CHANGED') return 'Someone changed this return while you had it open. The page was reloaded; check it and try again.';
  if (e.code === 'RETURN_INVALID_TRANSITION') return 'This return is no longer in the state this step needs (it was withdrawn or already moved on). The page was reloaded.';
  if (e.code === 'RETURN_FOUR_EYES') return 'You asked for this money back, so another person must decide it.';
  if (e.code === 'RETURN_NOT_AWAITING_APPROVAL') return 'No credit reduction is waiting for a decision on this return any more. The page was reloaded.';
  switch (e.status) {
    case 400: return Object.values(e.fields ?? {})[0] ?? 'Some of what you entered is not allowed. Check it and try again.';
    case 401: return 'Your session has ended. Sign in again.';
    case 403: return 'Your role does not allow this step (returns.manage is needed).';
    case 404: return 'This return was not found.';
    case 429: return 'Too many attempts. Wait a moment and try again.';
    default: return e.status === null || e.status >= 500 ? 'The service did not answer. Nothing was changed that you can see; reload and check before you try again.' : 'The step was refused.';
  }
}

// ---------- photos ----------

export interface ReturnPhoto { id: string; slot: number; contentType: string; sizeBytes: number; width: number; height: number; createdAtUtc: string }
export const RETURN_PHOTO_MAX = 5;
export const RETURN_PHOTO_BYTES = 5 * 1024 * 1024;
export const RETURN_PHOTO_TYPES: readonly string[] = ['image/jpeg', 'image/png', 'image/webp'];
export const RETURN_PHOTO_ACCEPT = RETURN_PHOTO_TYPES.join(',');
/** Photos can be added while Requested or Approved, removed only while Requested. */
export const canAddReturnPhoto = (status: string): boolean => status === 'requested' || status === 'approved';
export const canRemoveReturnPhoto = (status: string): boolean => status === 'requested';
/** Checked before anything is sent; the server checks again (type by the first bytes). */
export function returnPhotoProblem(file: { type: string; size: number }, count: number): 'full' | 'type' | 'size' | 'empty' | null {
  if (count >= RETURN_PHOTO_MAX) return 'full';
  if (!RETURN_PHOTO_TYPES.includes(file.type)) return 'type';
  if (file.size <= 0) return 'empty';
  if (file.size > RETURN_PHOTO_BYTES) return 'size';
  return null;
}
/** Message key (under `Returns.photos.error`) for a failed photo call. */
export function returnPhotoErrorKey(status: number | null, code: string | null): string {
  if (code === 'RETURN_PHOTOS_CLOSED') return 'closed';
  if (code === 'RETURN_PHOTOS_FULL') return 'full';
  if (code === 'SCAN_UNAVAILABLE' || status === 503) return 'scanDown';
  if (status === 413) return 'size';
  if (status === 400) return 'invalid';
  if (status === 404) return 'notFound';
  if (status === 401 || status === 403) return 'signedOut';
  return 'unavailable';
}
