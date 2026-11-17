/**
 * The rest of the credit books for staff (B3.12): aging, company statement, apply on-account, adjustments, hold override.
 * Pure shapes and helpers, no runtime imports (Node's type stripping must be able to test it). Amounts are
 * whole poisha; forms take BDT as TEXT and convert by string rules. Everything here only helps the person: the API checks
 * permission, four eyes, caps and the authenticator code again, and its answer always wins.
 */
import type { CreditHint, CreditLedgerLine, CreditPage } from './credit-payments-model';

// Small copies of the money and date rules of `credit-payments-model.ts`: Node's test runner cannot follow an extension-less
// runtime import between two model files, and these four are a few lines each (the sibling's own tests pin the same behaviour).
function formatBdt(minor: number): string {
  const abs = Math.abs(minor);
  return `${minor < 0 ? '-' : ''}${Math.floor(abs / 100).toLocaleString('en-US')}.${String(abs % 100).padStart(2, '0')}`;
}
function bdtToPoisha(text: string): number | null {
  const m = /^(\d{1,12})(?:\.(\d{1,2}))?$/.exec(text.trim().replace(/,/g, '').replace(/^৳\s*/, ''));
  return m ? Number(m[1]) * 100 + Number((m[2] ?? '').padEnd(2, '0') || '0') : null;
}
function isIsoDay(v: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(v)) return false;
  const d = new Date(`${v}T00:00:00Z`);
  return !Number.isNaN(d.getTime()) && d.toISOString().slice(0, 10) === v;
}
const sameId = (a: string | null | undefined, b: string | null | undefined): boolean => !!a && !!b && a.toLowerCase() === b.toLowerCase();

export const CREDIT_BOOKS_PERMISSIONS = {
  adjust: 'credit.adjust',
  approve: 'credit.adjust_approve',
  holdOverride: 'credit.hold_override'
} as const;

const holds = (permissions: readonly string[], p: string): boolean => permissions.some((x) => x.toLowerCase() === p);
export const canAdjustCredit = (permissions: readonly string[]): boolean => holds(permissions, CREDIT_BOOKS_PERMISSIONS.adjust);
export const canApproveAdjustment = (permissions: readonly string[]): boolean => holds(permissions, CREDIT_BOOKS_PERMISSIONS.approve);
export const canOverrideHold = (permissions: readonly string[]): boolean => holds(permissions, CREDIT_BOOKS_PERMISSIONS.holdOverride);

// ---------- aging ----------

export interface CreditAgingBuckets { current: number; days1To30: number; days31To60: number; days61To90: number; days90Plus: number }

export const AGING_BUCKETS: ReadonlyArray<{ key: keyof CreditAgingBuckets; label: string }> = [
  { key: 'current', label: 'Current' },
  { key: 'days1To30', label: '1-30 days' },
  { key: 'days31To60', label: '31-60 days' },
  { key: 'days61To90', label: '61-90 days' },
  { key: 'days90Plus', label: '90+ days' }
];

export interface CreditAgingRow {
  companyId: string;
  companyName: string;
  openOrders: number;
  receivableMinor: number;
  overdueMinor: number;
  buckets: CreditAgingBuckets;
  oldestDueOn: string;
  maxDaysPastDue: number;
  onAccountMinor: number;
  onHold: boolean;
  holdOverrideUntilUtc: string | null;
}

export interface CreditAgingReport {
  asOf: string;
  companies: CreditPage<CreditAgingRow>;
  totals: CreditAgingBuckets;
  receivableMinor: number;
  overdueMinor: number;
}

export const AGING_PAGE_SIZE = 25;

export interface AgingFilter { q: string; overdueOnly: boolean; page: number; companyId?: string }

export function agingQuery(f: AgingFilter, pageSize: number = AGING_PAGE_SIZE): string {
  const p = new URLSearchParams();
  if (f.companyId) p.set('companyId', f.companyId);
  if (f.q.trim()) p.set('q', f.q.trim());
  if (f.overdueOnly) p.set('overdueOnly', 'true');
  p.set('page', String(f.page));
  p.set('pageSize', String(pageSize));
  return p.toString();
}

/** The hold of a company as one line of words. `until` is the end of an Admin's lift, when one is running. */
export function holdText(onHold: boolean, until: string | null, now: number = Date.now()): string {
  if (onHold) return 'On hold: new credit orders are refused';
  if (until && Date.parse(until) > now) return `Hold lifted until ${until.replace('T', ' ').slice(0, 16)} UTC`;
  return 'Not on hold';
}

/** "27 days overdue" / "due today or later". */
export const overdueText = (maxDaysPastDue: number): string => (maxDaysPastDue > 0 ? `${maxDaysPastDue} days overdue` : 'Nothing overdue');

export function agingCsvName(overdueOnly: boolean, today: string): string {
  return `credit-aging${overdueOnly ? '-overdue' : ''}-${today}.csv`;
}

// ---------- statement ----------

export interface CreditStatement {
  companyId: string;
  companyName: string;
  from: string | null;
  to: string | null;
  currentReceivableMinor: number;
  currentOnAccountMinor: number;
  lines: CreditPage<CreditStatementLine>;
}

/** Staff ledger line; the base fields are the same as on the payment detail, plus the adjustment and credit note ids. */
export interface CreditStatementLine extends CreditLedgerLine { adjustmentId: string | null; creditNoteId: string | null }

export const STATEMENT_PAGE_SIZE = 50;

export interface StatementFilter { from: string; to: string; page: number }

/** Dates are Dhaka days; empty = no limit. Returns a plain-words problem or null. Checked before any request or download. */
export function statementRangeProblem(from: string, to: string): string | null {
  if (from && !isIsoDay(from)) return 'Give the first day as a date, or leave it empty.';
  if (to && !isIsoDay(to)) return 'Give the last day as a date, or leave it empty.';
  if (from && to && from > to) return 'The first day must not be after the last day.';
  return null;
}

export function statementQuery(f: StatementFilter, pageSize: number = STATEMENT_PAGE_SIZE): string {
  const p = new URLSearchParams();
  if (f.from) p.set('from', f.from);
  if (f.to) p.set('to', f.to);
  p.set('page', String(f.page));
  p.set('pageSize', String(pageSize));
  return p.toString();
}

export function statementCsvQuery(f: { from: string; to: string }): string {
  const p = new URLSearchParams();
  if (f.from) p.set('from', f.from);
  if (f.to) p.set('to', f.to);
  return p.toString();
}

const KIND_TEXT: Record<string, string> = {
  charge: 'Order charged', payment_applied: 'Payment applied to an order', payment_on_account: 'Payment kept on account',
  payment_reversed: 'Payment reversed (bounce)', on_account_applied: 'On-account money applied', charge_reduced: 'Charge reduced (cancel or credit note)',
  write_off: 'Written off', on_account_correction: 'On-account correction', on_account_payout: 'On-account paid out'
};
export const ledgerKindText = (kind: string): string => KIND_TEXT[kind] ?? kind.replace(/_/g, ' ');

/** A signed amount for a table: "+1,000.00", "-250.00", "0.00". */
export const signedBdt = (minor: number): string => (minor === 0 ? '0.00' : `${minor > 0 ? '+' : ''}${formatBdt(minor)}`);

export function statementCsvName(companyName: string, today: string): string {
  const slug = companyName.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40) || 'company';
  return `credit-statement-${slug}-${today}.csv`;
}

// ---------- shared: sources the person can choose from ----------

export interface OrderChoice { orderId: string; orderNumber: string; owedMinor: number }
export interface PaymentChoice { paymentId: string; label: string }

/**
 * Orders named by the statement lines (each once, first seen first) with what the loaded lines say each still owes. The staff API has
 * no open-orders list, so the statement is the picker; `owedMinor` is exact only when ALL the company's lines were loaded.
 */
export function orderChoices(lines: readonly Pick<CreditStatementLine, 'orderId' | 'orderNumber' | 'receivableDeltaMinor'>[]): OrderChoice[] {
  const seen = new Map<string, OrderChoice>();
  for (const l of lines) {
    if (!l.orderId) continue;
    const known = seen.get(l.orderId);
    if (known) known.owedMinor += l.receivableDeltaMinor;
    else seen.set(l.orderId, { orderId: l.orderId, orderNumber: l.orderNumber ?? l.orderId.slice(0, 8), owedMinor: l.receivableDeltaMinor });
  }
  return [...seen.values()];
}

export function paymentChoices(lines: readonly Pick<CreditStatementLine, 'paymentId' | 'paymentMethod' | 'paymentReference'>[]): PaymentChoice[] {
  const seen = new Map<string, PaymentChoice>();
  for (const l of lines) if (l.paymentId && !seen.has(l.paymentId)) seen.set(l.paymentId, { paymentId: l.paymentId, label: `${l.paymentMethod ?? 'payment'} ${l.paymentReference ?? l.paymentId.slice(0, 8)}` });
  return [...seen.values()];
}

// ---------- apply on-account ----------

export interface ApplyLine { orderId: string; amountBdt: string }
export interface ApplyForm { amountBdt: string; allocations: ApplyLine[] }
export interface ApplyBody { amountMinor?: number; allocations?: Array<{ orderId: string; amountMinor: number }> }

export const MAX_APPLY_ALLOCATIONS = 50;

export const emptyApplyForm = (): ApplyForm => ({ amountBdt: '', allocations: [] });

/** `onAccountMinor` (when the screen knows it) lets us stop an amount the company does not have; the server decides in the end. */
export function applyProblems(f: ApplyForm, onAccountMinor: number | null): Record<string, string> {
  const out: Record<string, string> = {};
  const total = f.amountBdt.trim() === '' ? null : bdtToPoisha(f.amountBdt);
  if (f.amountBdt.trim() !== '') {
    if (total === null) out.amountBdt = 'Use digits and at most 2 decimals, for example 5000 or 5,000.50.';
    else if (total < 1) out.amountBdt = 'The amount must be more than zero, or leave it empty to apply everything that fits.';
    else if (onAccountMinor !== null && total > onAccountMinor) out.amountBdt = `The company has only BDT ${formatBdt(onAccountMinor)} on account.`;
  }
  if (f.allocations.length > MAX_APPLY_ALLOCATIONS) out.allocations = `Name at most ${MAX_APPLY_ALLOCATIONS} orders.`;
  const seen = new Set<string>();
  f.allocations.forEach((a, i) => {
    const m = bdtToPoisha(a.amountBdt);
    if (!a.orderId) out[`allocation${i}`] = 'Choose the order.';
    else if (seen.has(a.orderId)) out[`allocation${i}`] = 'Each order can be named once.';
    else if (m === null || m < 1) out[`allocation${i}`] = 'Enter the amount for this order (digits, at most 2 decimals, more than zero).';
    seen.add(a.orderId);
  });
  return out;
}

/** Empty amount and no rows is valid: the server applies everything that fits, oldest due first. Call only when `applyProblems` is empty. */
export function buildApplyBody(f: ApplyForm): ApplyBody {
  const body: ApplyBody = {};
  if (f.amountBdt.trim() !== '') body.amountMinor = bdtToPoisha(f.amountBdt) ?? 0;
  if (f.allocations.length > 0) body.allocations = f.allocations.map((a) => ({ orderId: a.orderId, amountMinor: bdtToPoisha(a.amountBdt) ?? 0 }));
  return body;
}

export interface BodyAttempt { key: string; fingerprint: string }

/** One Idempotency-Key per distinct (company, body): a retry of the same details keeps its key, any change gets a new one. */
export function bodyAttemptFor(previous: BodyAttempt | null, companyId: string, body: unknown, makeKey: () => string): BodyAttempt {
  const fingerprint = JSON.stringify({ companyId, body });
  return previous && previous.fingerprint === fingerprint ? previous : { key: makeKey(), fingerprint };
}

export interface CreditOnAccountApplication { orderId: string; paymentId: string | null; creditNoteId: string | null; amountMinor: number }
export interface AppliedOnAccount {
  eventId: string;
  applied: CreditOnAccountApplication[];
  appliedMinor: number;
  creditUsedMinor: number;
  creditOnAccountMinor: number;
  replayed: boolean;
}

export function applyResultText(r: Pick<AppliedOnAccount, 'appliedMinor' | 'applied' | 'creditUsedMinor' | 'creditOnAccountMinor' | 'replayed'>): string {
  const orders = new Set(r.applied.map((a) => a.orderId)).size;
  const head = r.replayed ? 'This was already applied (same request). ' : '';
  return `${head}BDT ${formatBdt(r.appliedMinor)} applied to ${orders} order${orders === 1 ? '' : 's'}. Credit used is now BDT ${formatBdt(r.creditUsedMinor)}; on account BDT ${formatBdt(r.creditOnAccountMinor)}.`;
}

// ---------- adjustments ----------

export type AdjustmentKind = 'write_off' | 'on_account_correction' | 'on_account_payout';
export type AdjustmentStatus = 'requested' | 'approved' | 'rejected';
export type AdjustmentQueueStatus = AdjustmentStatus | 'all';

export const ADJUSTMENT_KINDS: Record<AdjustmentKind, { label: string; help: string; order: boolean; source: boolean; signed: boolean }> = {
  write_off: { label: 'Write-off', help: 'Lowers what one order still owes (a bad debt or an agreed discount). Choose the order.', order: true, source: false, signed: false },
  on_account_correction: { label: 'On-account correction', help: 'Fixes on-account money of ONE payment or credit note. A minus takes money off the account; a plus gives back only what an earlier correction took.', order: false, source: true, signed: true },
  on_account_payout: { label: 'On-account payout', help: 'Records that MECHWARE paid on-account money back to the company by bank transfer (the transfer itself is made outside this system). Cheque and transfer money can be paid out only some days after it cleared.', order: false, source: true, signed: false }
};
export const ADJUSTMENT_KIND_ORDER = Object.keys(ADJUSTMENT_KINDS) as AdjustmentKind[];

export const ADJUSTMENT_STATUS_LABEL: Record<AdjustmentStatus, string> = { requested: 'Waiting for approval', approved: 'Approved', rejected: 'Rejected' };
export const ADJUSTMENT_QUEUE_STATUSES: readonly AdjustmentQueueStatus[] = ['requested', 'approved', 'rejected', 'all'];
export const ADJUSTMENT_PAGE_SIZE = 25;

export const kindLabel = (kind: string): string => (ADJUSTMENT_KINDS as Record<string, { label: string } | undefined>)[kind]?.label ?? kind;

export interface CreditAdjustment {
  id: string;
  companyId: string;
  kind: AdjustmentKind;
  amountMinor: number;
  currency: string;
  orderId: string | null;
  paymentId: string | null;
  creditNoteId: string | null;
  payoutReference: string | null;
  reason: string;
  status: AdjustmentStatus;
  requestedBy: string;
  requestedAtUtc: string;
  approvedBy: string | null;
  approvedAtUtc: string | null;
  rejectedBy: string | null;
  rejectedAtUtc: string | null;
  decisionReason: string | null;
}

export interface CreditAdjustmentListItem { adjustment: CreditAdjustment; companyName: string }
export interface CreditAdjustmentDetail extends CreditAdjustmentListItem { lines: CreditLedgerLine[] }

export interface ApprovedAdjustment {
  adjustment: CreditAdjustment;
  eventId: string;
  receivableDeltaMinor: number;
  onAccountDeltaMinor: number;
  creditUsedMinor: number;
  creditOnAccountMinor: number;
}

export function adjustmentsQuery(q: { status?: AdjustmentQueueStatus; companyId?: string; page?: number; pageSize?: number }): string {
  const p = new URLSearchParams();
  if (q.status) p.set('status', q.status);
  if (q.companyId) p.set('companyId', q.companyId);
  p.set('page', String(q.page ?? 1));
  p.set('pageSize', String(q.pageSize ?? ADJUSTMENT_PAGE_SIZE));
  return p.toString();
}

/** BDT text with an optional leading minus (a correction only): "-250", "1,000.50". Integer maths only. */
export function signedBdtToPoisha(text: string): number | null {
  const t = text.trim();
  const neg = t.startsWith('-');
  if (neg && /^-\s/.test(t)) return null;
  const minor = bdtToPoisha(neg ? t.slice(1) : t);
  return minor === null ? null : neg ? -minor : minor;
}

export interface AdjustmentForm {
  kind: AdjustmentKind;
  amountBdt: string;
  reason: string;
  orderId: string;
  sourceType: 'payment' | 'credit_note';
  paymentId: string;
  creditNoteId: string;
  payoutReference: string;
}

export const emptyAdjustmentForm = (): AdjustmentForm => ({ kind: 'write_off', amountBdt: '', reason: '', orderId: '', sourceType: 'payment', paymentId: '', creditNoteId: '', payoutReference: '' });

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export const isCreditGuid = (v: string): boolean => GUID.test(v.trim());

/** A reason for an adjustment, 10 to 500 characters after trimming (a reject or withdrawal uses 5 to 500: `creditReasonProblem`). */
export function adjustmentReasonProblem(reason: string): string | null {
  const n = reason.trim().length;
  if (n === 0) return 'Enter a reason.';
  return n < 10 || n > 500 ? 'The reason must be 10 to 500 characters.' : null;
}

export function adjustmentFormProblems(f: AdjustmentForm): Record<string, string> {
  const out: Record<string, string> = {};
  const info = ADJUSTMENT_KINDS[f.kind];
  const minor = f.amountBdt.trim() === '' ? null : info.signed ? signedBdtToPoisha(f.amountBdt) : bdtToPoisha(f.amountBdt);
  if (f.amountBdt.trim() === '') out.amountBdt = 'Enter the amount in BDT.';
  else if (minor === null) out.amountBdt = info.signed ? 'Use digits and at most 2 decimals; a minus takes money off, for example -250 or 100.50.' : 'Use digits and at most 2 decimals, for example 5000 or 5,000.50.';
  else if (minor === 0) out.amountBdt = 'The amount cannot be zero.';
  if (info.order && !f.orderId) out.orderId = 'Choose the order to write off.';
  if (info.source) {
    if (f.sourceType === 'payment' && !f.paymentId) out.paymentId = 'Choose the payment whose on-account money this changes.';
    if (f.sourceType === 'credit_note' && !isCreditGuid(f.creditNoteId)) out.creditNoteId = 'Paste the credit note id (a long id with dashes).';
  }
  if (f.kind === 'on_account_payout') {
    const n = f.payoutReference.trim().length;
    if (n < 4 || n > 40) out.payoutReference = 'Enter the bank transfer reference MECHWARE used, 4 to 40 characters.';
  }
  const rp = adjustmentReasonProblem(f.reason);
  if (rp) out.reason = rp;
  return out;
}

export interface AdjustmentBody {
  kind: AdjustmentKind;
  amountMinor: number;
  reason: string;
  orderId?: string;
  paymentId?: string;
  creditNoteId?: string;
  payoutReference?: string;
}

/** Boxes a kind does not take are left out (the server refuses them). Call only when `adjustmentFormProblems` is empty. */
export function buildAdjustmentBody(f: AdjustmentForm): AdjustmentBody {
  const info = ADJUSTMENT_KINDS[f.kind];
  const amountMinor = (info.signed ? signedBdtToPoisha(f.amountBdt) : bdtToPoisha(f.amountBdt)) ?? 0;
  const body: AdjustmentBody = { kind: f.kind, amountMinor, reason: f.reason.trim() };
  if (info.order) body.orderId = f.orderId;
  if (info.source) {
    if (f.sourceType === 'payment') body.paymentId = f.paymentId;
    else body.creditNoteId = f.creditNoteId.trim();
  }
  if (f.kind === 'on_account_payout') body.payoutReference = f.payoutReference.trim();
  return body;
}

/** Hints only: the server decides (it also refuses someone who recorded or verified the source payment). */
export function approveAdjustmentHint(a: Pick<CreditAdjustment, 'status' | 'requestedBy'>, me: { id: string; permissions: readonly string[] }): CreditHint {
  if (!canApproveAdjustment(me.permissions)) return { allowed: false, reason: 'Only an Admin may approve adjustments (credit.adjust_approve).' };
  if (a.status !== 'requested') return { allowed: false, reason: `This adjustment is already ${ADJUSTMENT_STATUS_LABEL[a.status].toLowerCase()}.` };
  if (sameId(a.requestedBy, me.id)) return { allowed: false, reason: 'You requested this adjustment, so someone else must approve it.' };
  return { allowed: true, reason: '' };
}

/** Anyone with credit.adjust may refuse; the requester doing so is a withdrawal. */
export function rejectAdjustmentHint(a: Pick<CreditAdjustment, 'status' | 'requestedBy'>, me: { id: string; permissions: readonly string[] }): CreditHint & { withdraw: boolean } {
  const withdraw = sameId(a.requestedBy, me.id);
  if (!canAdjustCredit(me.permissions)) return { allowed: false, reason: 'Your role may not reject adjustments (credit.adjust).', withdraw };
  if (a.status !== 'requested') return { allowed: false, reason: `This adjustment is already ${ADJUSTMENT_STATUS_LABEL[a.status].toLowerCase()}.`, withdraw };
  return { allowed: true, reason: '', withdraw };
}

/** "Write-off of BDT 1,000.00" / "Correction of -BDT 250.00" for dialogs and notices. */
export function adjustmentSummary(a: Pick<CreditAdjustment, 'kind' | 'amountMinor'>): string {
  const money = `${a.amountMinor < 0 ? '-' : ''}BDT ${formatBdt(Math.abs(a.amountMinor))}`;
  return `${kindLabel(a.kind)} of ${money}`;
}

export function approvedText(r: Pick<ApprovedAdjustment, 'receivableDeltaMinor' | 'onAccountDeltaMinor' | 'creditUsedMinor' | 'creditOnAccountMinor'>): string {
  return `Approved. Owed ${signedBdt(r.receivableDeltaMinor)}, on account ${signedBdt(r.onAccountDeltaMinor)} (BDT). The company now has BDT ${formatBdt(r.creditUsedMinor)} in use and BDT ${formatBdt(r.creditOnAccountMinor)} on account.`;
}

// ---------- hold override ----------

export const HOLD_MAX_DAYS = 7;

export interface CreditHold { companyId: string; overdueSince: string | null; onHold: boolean; overrideUntilUtc: string | null; holdNoticedAtUtc: string | null }

export interface HoldForm { days: string; reason: string }

/** `days`: a whole number 0 to 7 (0 ends a lift). The reason is 5 to 500 characters. Checked BEFORE a code is asked for. */
export function holdFormProblems(f: HoldForm): Record<string, string> {
  const out: Record<string, string> = {};
  const t = f.days.trim();
  if (t === '') out.days = `Enter the number of days, 0 to ${HOLD_MAX_DAYS}.`;
  else if (!/^\d{1,2}$/.test(t) || Number(t) > HOLD_MAX_DAYS) out.days = `Whole days from 0 to ${HOLD_MAX_DAYS}. Use 0 to end a lift.`;
  const n = f.reason.trim().length;
  if (n === 0) out.reason = 'Enter a reason.';
  else if (n < 5 || n > 500) out.reason = 'The reason must be 5 to 500 characters.';
  return out;
}

export const buildHoldBody = (f: HoldForm): { days: number; reason: string } => ({ days: Number(f.days.trim()), reason: f.reason.trim() });

export function holdResultText(r: CreditHold, days: number): string {
  if (days === 0) return r.onHold ? 'The lift was ended. The company is on hold again because it still has overdue invoices.' : 'The lift was ended. The company is not on hold.';
  const until = r.overrideUntilUtc ? r.overrideUntilUtc.replace('T', ' ').slice(0, 16) : '';
  return `The hold is lifted for ${days} day${days === 1 ? '' : 's'}${until ? `, until ${until} UTC` : ''}. After that it applies again if invoices are still overdue.`;
}
