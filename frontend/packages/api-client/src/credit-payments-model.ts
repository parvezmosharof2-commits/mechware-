/**
 * Customer payments on credit, staff side (B3.4 record/verify/reject/bounce, B3.8 queue + detail; console screens B3.11).
 * Pure shapes and helpers, no runtime imports, so Node's type stripping can test them. Amounts are whole poisha everywhere;
 * the form takes BDT as TEXT and converts it by string rules (never a float). Every rule here only helps the person: the API
 * checks permission, four eyes, dates, duplicates and the authenticator code again.
 */

export const CREDIT_PERMISSIONS = { view: 'credit.view', record: 'credit.record_payment', verify: 'credit.verify_payment' } as const;

export type CreditPaymentMethod = 'cheque' | 'pay_order' | 'beftn' | 'rtgs' | 'npsb' | 'bank_deposit' | 'cash' | 'bkash' | 'nagad';
export type CreditPaymentStatus = 'recorded' | 'verified' | 'rejected' | 'bounced';
export type CreditQueueStatus = 'recorded' | 'verified' | 'rejected' | 'bounced' | 'all';

export const CREDIT_QUEUE_STATUSES: readonly CreditQueueStatus[] = ['recorded', 'verified', 'rejected', 'bounced', 'all'];
export const CREDIT_PAGE_SIZE = 25;

export interface CreditAllocationRequest { orderId: string; amountMinor: number }

/** Staff view of one payment (`CreditPaymentDto`). Dates are `yyyy-MM-dd` (Dhaka), moments are ISO UTC. */
export interface CreditPayment {
  id: string;
  companyId: string;
  method: string;
  amountMinor: number;
  currency: string;
  reference: string;
  drawerBank: string | null;
  receivedOn: string;
  chequeDate: string | null;
  lateEntryReason: string | null;
  allocations: CreditAllocationRequest[];
  note: string | null;
  status: CreditPaymentStatus;
  recordedBy: string;
  recordedAtUtc: string;
  verifiedBy: string | null;
  verifiedAtUtc: string | null;
  clearedOn: string | null;
  rejectedBy: string | null;
  rejectedAtUtc: string | null;
  decisionReason: string | null;
  bouncedBy: string | null;
  bouncedAtUtc: string | null;
}

export interface CreditPaymentListItem { payment: CreditPayment; companyName: string; lateEntry: boolean; postDated: boolean }

export interface CreditLedgerLine {
  seq: number;
  eventId: string;
  kind: string;
  occurredOn: string;
  createdAtUtc: string;
  orderId: string | null;
  orderNumber: string | null;
  paymentId: string | null;
  paymentMethod: string | null;
  paymentReference: string | null;
  receivableDeltaMinor: number;
  onAccountDeltaMinor: number;
  receivableAfterMinor: number;
  onAccountAfterMinor: number;
  dueOn: string | null;
  actorUserId: string | null;
  reason: string | null;
}

export interface CreditPaymentDetail extends CreditPaymentListItem { lines: CreditLedgerLine[] }

export interface CreditPage<T> { items: T[]; page: number; pageSize: number; totalCount: number }

export interface RecordCreditPaymentBody {
  method: CreditPaymentMethod;
  amountMinor: number;
  receivedOn: string;
  reference?: string;
  drawerBank?: string;
  chequeDate?: string;
  lateEntryReason?: string;
  note?: string;
}

// ---------- methods ----------

interface MethodInfo { label: string; chequeDate: boolean; bank: boolean; reference: 'text' | 'receipt'; referenceLabel: string }

export const CREDIT_METHODS: Record<CreditPaymentMethod, MethodInfo> = {
  cheque: { label: 'Cheque', chequeDate: true, bank: true, reference: 'text', referenceLabel: 'Cheque number' },
  pay_order: { label: 'Pay order', chequeDate: true, bank: true, reference: 'text', referenceLabel: 'Pay order number' },
  beftn: { label: 'BEFTN transfer', chequeDate: false, bank: false, reference: 'text', referenceLabel: 'Bank reference' },
  rtgs: { label: 'RTGS transfer', chequeDate: false, bank: false, reference: 'text', referenceLabel: 'Bank reference' },
  npsb: { label: 'NPSB transfer', chequeDate: false, bank: false, reference: 'text', referenceLabel: 'Bank reference' },
  bank_deposit: { label: 'Bank deposit', chequeDate: false, bank: true, reference: 'text', referenceLabel: 'Deposit slip number' },
  cash: { label: 'Cash', chequeDate: false, bank: false, reference: 'receipt', referenceLabel: 'Reference' },
  bkash: { label: 'bKash', chequeDate: false, bank: false, reference: 'text', referenceLabel: 'Transaction id (TrxID)' },
  nagad: { label: 'Nagad', chequeDate: false, bank: false, reference: 'text', referenceLabel: 'Transaction id (TrxID)' }
};

export const CREDIT_METHOD_ORDER = Object.keys(CREDIT_METHODS) as CreditPaymentMethod[];

export function methodLabel(method: string): string {
  return (CREDIT_METHODS as Record<string, MethodInfo | undefined>)[method]?.label ?? method;
}

export const CREDIT_STATUS_LABEL: Record<CreditPaymentStatus, string> = {
  recorded: 'Waiting for verification', verified: 'Verified', rejected: 'Rejected', bounced: 'Bounced'
};

// ---------- money ----------

/** Poisha to "1,234.50" (integer maths only). */
export function formatBdt(minor: number): string {
  const abs = Math.abs(minor);
  return `${minor < 0 ? '-' : ''}${Math.floor(abs / 100).toLocaleString('en-US')}.${String(abs % 100).padStart(2, '0')}`;
}

/** BDT text to poisha without floats: "5000", "5,000.5", "5000.50". A third decimal, a sign, an exponent or letters give null. */
export function bdtToPoisha(text: string): number | null {
  const m = /^(\d{1,12})(?:\.(\d{1,2}))?$/.exec(text.trim().replace(/,/g, '').replace(/^৳\s*/, ''));
  return m ? Number(m[1]) * 100 + Number((m[2] ?? '').padEnd(2, '0') || '0') : null;
}

// ---------- dates ----------

const DAY_MS = 86_400_000;
const ISO_DAY = /^\d{4}-\d{2}-\d{2}$/;

export function isIsoDay(v: string): boolean {
  if (!ISO_DAY.test(v)) return false;
  const d = new Date(`${v}T00:00:00Z`);
  return !Number.isNaN(d.getTime()) && d.toISOString().slice(0, 10) === v;
}

/** Today's date in Dhaka (UTC+6, no DST), which is the day the server uses. */
export function dhakaToday(now: number = Date.now()): string {
  return new Date(now + 6 * 3_600_000).toISOString().slice(0, 10);
}

export function addDays(day: string, days: number): string {
  return new Date(new Date(`${day}T00:00:00Z`).getTime() + days * DAY_MS).toISOString().slice(0, 10);
}

function daysBetween(from: string, to: string): number {
  return Math.round((new Date(`${to}T00:00:00Z`).getTime() - new Date(`${from}T00:00:00Z`).getTime()) / DAY_MS);
}

/** Mirrors `CreditPayment.LateAfterDays` / `ChequeWindowDays` (the server stays the authority). */
export const LATE_AFTER_DAYS = 30;
export const CHEQUE_WINDOW_DAYS = 180;

// ---------- the record form ----------

export interface RecordPaymentForm {
  method: CreditPaymentMethod;
  amountBdt: string;
  receivedOn: string;
  reference: string;
  drawerBank: string;
  chequeDate: string;
  lateEntryReason: string;
  note: string;
}

export function emptyRecordForm(today: string): RecordPaymentForm {
  return { method: 'cheque', amountBdt: '', receivedOn: today, reference: '', drawerBank: '', chequeDate: '', lateEntryReason: '', note: '' };
}

/** What a method asks for, so the form shows only those boxes. */
export function methodFields(method: CreditPaymentMethod): { chequeDate: boolean; bank: boolean; reference: boolean; referenceLabel: string } {
  const m = CREDIT_METHODS[method];
  return { chequeDate: m.chequeDate, bank: m.bank, reference: m.reference === 'text', referenceLabel: m.referenceLabel };
}

export function isLateEntry(receivedOn: string, today: string): boolean {
  return isIsoDay(receivedOn) && daysBetween(receivedOn, today) > LATE_AFTER_DAYS;
}

/**
 * Field problems found before anything is sent, keyed by form field (empty object = fine). A reference is checked for its length
 * only; duplicates, the cash limit and a wallet phone number are the server's to decide and come back as field messages.
 */
export function recordFormProblems(f: RecordPaymentForm, today: string): Record<string, string> {
  const out: Record<string, string> = {};
  const fields = methodFields(f.method);
  const minor = bdtToPoisha(f.amountBdt);
  if (f.amountBdt.trim() === '') out.amountBdt = 'Enter the amount in BDT.';
  else if (minor === null) out.amountBdt = 'Use digits and at most 2 decimals, for example 25000 or 25,000.50.';
  else if (minor < 1) out.amountBdt = 'The amount must be more than zero.';
  if (!isIsoDay(f.receivedOn)) out.receivedOn = 'Give the day the money was received.';
  else if (f.receivedOn > today) out.receivedOn = 'A payment cannot be received in the future.';
  const late = isLateEntry(f.receivedOn, today);
  if (late && f.lateEntryReason.trim().length < 5) out.lateEntryReason = `Received more than ${LATE_AFTER_DAYS} days ago: give a reason of 5 to 500 characters.`;
  if (!late && f.lateEntryReason.trim() !== '') out.lateEntryReason = `A late-entry reason is only for payments received more than ${LATE_AFTER_DAYS} days ago.`;
  if (fields.reference) {
    const n = f.reference.trim().length;
    if (n < 4 || n > 40) out.reference = `Enter the ${fields.referenceLabel.toLowerCase()}, 4 to 40 characters.`;
  }
  if (fields.chequeDate) {
    if (!isIsoDay(f.chequeDate)) out.chequeDate = 'Give the date written on the cheque or pay order.';
    else if (isIsoDay(f.receivedOn) && Math.abs(daysBetween(f.receivedOn, f.chequeDate)) > CHEQUE_WINDOW_DAYS) out.chequeDate = `The cheque date must be within ${CHEQUE_WINDOW_DAYS} days of the day it was received.`;
  }
  if (fields.bank && (f.drawerBank.trim().length < 2 || f.drawerBank.trim().length > 60)) out.drawerBank = 'Enter the bank, 2 to 60 characters.';
  if (f.note.trim().length > 500) out.note = 'At most 500 characters.';
  return out;
}

/** The request body; boxes a method does not take are left out (the server refuses them). Call only when `recordFormProblems` is empty. */
export function buildRecordBody(f: RecordPaymentForm): RecordCreditPaymentBody {
  const fields = methodFields(f.method);
  const body: RecordCreditPaymentBody = { method: f.method, amountMinor: bdtToPoisha(f.amountBdt) ?? 0, receivedOn: f.receivedOn };
  if (fields.reference) body.reference = f.reference.trim();
  if (fields.bank) body.drawerBank = f.drawerBank.trim();
  if (fields.chequeDate) body.chequeDate = f.chequeDate;
  if (f.lateEntryReason.trim()) body.lateEntryReason = f.lateEntryReason.trim();
  if (f.note.trim()) body.note = f.note.trim();
  return body;
}

/** A change to the body must get a new Idempotency-Key (the server refuses the same key with another body); the same body keeps its key. */
export interface RecordAttempt { key: string; fingerprint: string }

export function recordAttemptFor(previous: RecordAttempt | null, companyId: string, body: RecordCreditPaymentBody, makeKey: () => string): RecordAttempt {
  const fingerprint = JSON.stringify({ companyId, body });
  return previous && previous.fingerprint === fingerprint ? previous : { key: makeKey(), fingerprint };
}

// ---------- decisions ----------

/** A reason for reject or bounce: 5 to 500 characters after trimming. Check it BEFORE asking for a code, a code is spent even when the call is refused. */
export function creditReasonProblem(reason: string | null | undefined): string | null {
  const n = (reason ?? '').trim().length;
  if (n === 0) return 'Enter a reason.';
  return n < 5 || n > 500 ? 'The reason must be 5 to 500 characters.' : null;
}

/** Valid clearing days: from the later of the received day and the cheque date, to today. Returns null when fine. */
export function clearedOnProblem(p: Pick<CreditPayment, 'receivedOn' | 'chequeDate'>, clearedOn: string, today: string): string | null {
  if (!isIsoDay(clearedOn)) return 'Give the day the money cleared.';
  const earliest = p.chequeDate && p.chequeDate > p.receivedOn ? p.chequeDate : p.receivedOn;
  if (clearedOn > today) return 'The money cannot have cleared in the future.';
  if (clearedOn < earliest) return `The money cleared between ${earliest} and today.`;
  return null;
}

/** Ids compared the way the server does: ignoring case. */
export const sameId = (a: string | null | undefined, b: string | null | undefined): boolean => !!a && !!b && a.toLowerCase() === b.toLowerCase();

export interface CreditHint { allowed: boolean; reason: string }

/** Hints only. The server decides; this stops a click that is sure to be refused. */
export function verifyHint(item: Pick<CreditPaymentListItem, 'payment' | 'postDated'>, me: { id: string; permissions: readonly string[] }, today: string): CreditHint {
  const held = new Set(me.permissions.map((p) => p.toLowerCase()));
  if (!held.has(CREDIT_PERMISSIONS.verify)) return { allowed: false, reason: 'Your role may not verify payments.' };
  if (item.payment.status !== 'recorded') return { allowed: false, reason: `This payment is already ${CREDIT_STATUS_LABEL[item.payment.status].toLowerCase()}.` };
  if (sameId(item.payment.recordedBy, me.id)) return { allowed: false, reason: 'You recorded this payment, so someone else must verify or reject it.' };
  if (item.postDated || (item.payment.chequeDate && item.payment.chequeDate > today)) {
    return { allowed: false, reason: `This cheque is post-dated and cannot be verified before ${item.payment.chequeDate}.` };
  }
  return { allowed: true, reason: '' };
}

/** A reject has the same rules as a verify except the post-dated one (a mistaken post-dated entry may be rejected). */
export function rejectHint(item: Pick<CreditPaymentListItem, 'payment'>, me: { id: string; permissions: readonly string[] }): CreditHint {
  const held = new Set(me.permissions.map((p) => p.toLowerCase()));
  if (!held.has(CREDIT_PERMISSIONS.verify)) return { allowed: false, reason: 'Your role may not reject payments.' };
  if (item.payment.status !== 'recorded') return { allowed: false, reason: `This payment is already ${CREDIT_STATUS_LABEL[item.payment.status].toLowerCase()}.` };
  if (sameId(item.payment.recordedBy, me.id)) return { allowed: false, reason: 'You recorded this payment, so someone else must verify or reject it.' };
  return { allowed: true, reason: '' };
}

/** Bounce: any verifier (no four eyes) of a verified, non-cash payment. */
export function bounceHint(item: Pick<CreditPaymentListItem, 'payment'>, me: { permissions: readonly string[] }): CreditHint {
  const held = new Set(me.permissions.map((p) => p.toLowerCase()));
  if (!held.has(CREDIT_PERMISSIONS.verify)) return { allowed: false, reason: 'Your role may not bounce payments.' };
  if (item.payment.status !== 'verified') return { allowed: false, reason: 'Only a verified payment can bounce.' };
  if (item.payment.method === 'cash') return { allowed: false, reason: 'Cash does not bounce. Correct a mistaken cash entry with an adjustment.' };
  return { allowed: true, reason: '' };
}

export const canViewCredit = (permissions: readonly string[]): boolean => permissions.some((p) => p.toLowerCase() === CREDIT_PERMISSIONS.view);
export const canRecordCredit = (permissions: readonly string[]): boolean => permissions.some((p) => p.toLowerCase() === CREDIT_PERMISSIONS.record);
export const canVerifyCredit = (permissions: readonly string[]): boolean => permissions.some((p) => p.toLowerCase() === CREDIT_PERMISSIONS.verify);

// ---------- queue ----------

export function paymentsQuery(q: { status?: CreditQueueStatus; companyId?: string; page?: number; pageSize?: number }): string {
  const p = new URLSearchParams();
  if (q.status) p.set('status', q.status);
  if (q.companyId) p.set('companyId', q.companyId);
  p.set('page', String(q.page ?? 1));
  p.set('pageSize', String(q.pageSize ?? CREDIT_PAGE_SIZE));
  return p.toString();
}

/** The queue is oldest first; this keeps it right if the server order ever changes. Other statuses keep the server's newest-first order. */
export function sortQueue(items: readonly CreditPaymentListItem[], status: CreditQueueStatus): CreditPaymentListItem[] {
  const copy = [...items];
  if (status === 'recorded') copy.sort((a, b) => a.payment.recordedAtUtc.localeCompare(b.payment.recordedAtUtc));
  return copy;
}

export function flagsOf(item: Pick<CreditPaymentListItem, 'lateEntry' | 'postDated'>): string[] {
  return [...(item.lateEntry ? ['Late entry'] : []), ...(item.postDated ? ['Post-dated'] : [])];
}

/** Last 4 characters are enough on a busy table; the detail shows the whole reference. */
export const shortWhen = (iso: string | null | undefined): string => (iso ? `${iso.replace('T', ' ').slice(0, 16)} UTC` : '');

// ---------- errors ----------

export interface CreditErrorLike { status: number | null; code: string | null; message: string; fields: Record<string, string> }

export const CREDIT_ERROR_TEXT: Record<string, string> = {
  CREDIT_PAYMENT_FOUR_EYES: 'You recorded this payment, so you cannot verify or reject it. Someone else must.',
  CREDIT_PAYMENT_NOT_RECORDED: 'Someone else already decided this payment. The queue was reloaded.',
  CREDIT_PAYMENT_NOT_VERIFIED: 'Only a verified payment can bounce (and only once). The page was reloaded.',
  CREDIT_PAYMENT_CHEQUE_NOT_DUE: 'This is a post-dated cheque. It cannot be verified before the date written on it.',
  CREDIT_PAYMENT_CASH_CANNOT_BOUNCE: 'Cash does not bounce. Correct a mistaken cash entry with an adjustment.',
  CREDIT_PAYMENT_DUPLICATE_REFERENCE: 'This reference was already recorded (for this or another company). Check the cheque or transfer number; a rejected payment frees its reference.',
  IDEMPOTENCY_KEY_REUSED: 'This form was already sent with different details. Reload the page and enter the payment again.',
  STEP_UP_REQUIRED: 'This action needs the 6-digit code from your authenticator app.',
  STEP_UP_FAILED: 'The code was not accepted. Codes work once: wait for the next code (up to 30 seconds) and enter that one.'
};

/** Codes after which what the person saw is out of date, so the screen reloads. */
export const CREDIT_RELOAD_CODES: ReadonlySet<string> = new Set(['CREDIT_PAYMENT_NOT_RECORDED', 'CREDIT_PAYMENT_NOT_VERIFIED']);

export const isCreditStepUpFailure = (code: string | null | undefined): boolean => code === 'STEP_UP_FAILED' || code === 'STEP_UP_REQUIRED';
export const shouldReloadCredit = (code: string | null | undefined): boolean => !!code && CREDIT_RELOAD_CODES.has(code);

export function creditErrorText(e: CreditErrorLike): string {
  if (e.code && CREDIT_ERROR_TEXT[e.code]) return CREDIT_ERROR_TEXT[e.code];
  const first = Object.values(e.fields)[0];
  if (e.status === 400 && first) return first;
  if (e.status === null) return 'The server cannot be reached. Check your connection and try again.';
  if (e.status === 401) return 'Your session ended. Sign in again.';
  if (e.status === 403) return 'You do not have permission for this action.';
  if (e.status === 404) return 'This payment or company was not found. It may have been removed, or it is not Active.';
  if (e.status === 429) return 'Too many requests. Wait a moment and try again.';
  if (e.status >= 500) return 'The server had a problem. Try again in a moment; if it keeps happening, tell the technical team.';
  return e.message || 'The request was refused.';
}
