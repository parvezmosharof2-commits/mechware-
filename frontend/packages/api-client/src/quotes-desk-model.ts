/**
 * Staff quote desk (D4.15): the RFQ inbox, RFQ detail, quotes list and quote detail of the console. Pure shapes and helpers, no
 * runtime imports (Node's type stripping must be able to test it). Hand-typed from `StaffQuoteContracts.cs` and the D4.6 handoff
 * because the OpenAPI document has no response bodies for these routes. Nothing here computes money or decides who may do what:
 * it words what the API sent and checks what can be checked before sending. The API checks permission, conflict of interest and
 * races again, and its answer always wins.
 */

export const QUOTE_DESK_PERMISSIONS = { view: 'quotes.view', edit: 'quotes.edit', viewCost: 'pricing.view_cost' } as const;

const holds = (permissions: readonly string[], p: string): boolean => permissions.some((x) => x.toLowerCase() === p);
export const canViewQuotes = (permissions: readonly string[]): boolean => holds(permissions, QUOTE_DESK_PERMISSIONS.view);
export const canEditQuotes = (permissions: readonly string[]): boolean => holds(permissions, QUOTE_DESK_PERMISSIONS.edit);

export const DESK_PAGE_SIZE = 20;

export interface DeskPage<T> { items: T[]; page: number; pageSize: number; total: number }

// ---------- RFQ shapes (StaffRfqSummaryDto, StaffRfqDetailDto) ----------

export interface StaffRfqSummary {
  id: string;
  number: string;
  /** Wire name, lower case: submitted, quoting, answered, closed, withdrawn. */
  status: string;
  source: string;
  companyId: string;
  companyName: string;
  requestedBy: string;
  requesterName: string;
  assignedTo: string | null;
  assigneeName: string | null;
  lineCount: number;
  neededBy: string | null;
  createdAt: string;
  firstQuoteSentAt: string | null;
  slaDueAt: string;
  slaWorkingMinutes: number;
  slaBreached: boolean;
  closedAt: string | null;
  closeReason: string | null;
  withdrawnAt: string | null;
}

export interface StaffRfqVariant { id: string; sku: string; title: string; titleBn: string }

export interface StaffRfqLine {
  lineNo: number;
  rawText: string;
  partKey: string | null;
  quantity: number;
  /** matched, ambiguous, not_found or obsolete. */
  matchStatus: string;
  variant: StaffRfqVariant | null;
  successor: StaffRfqVariant | null;
  note: string | null;
}

export interface StaffQuoteRef { id: string; number: string; status: string }

export interface StaffRfqDetail {
  rfq: StaffRfqSummary;
  message: string | null;
  shipToSiteId: string | null;
  lines: StaffRfqLine[];
  quotes: StaffQuoteRef[];
}

// ---------- quote shapes (StaffQuoteSummaryDto, StaffQuoteDetailDto) ----------

export interface StaffQuoteSummary {
  id: string;
  number: string;
  /** closed, or the latest version's status: draft, pending_approval, sent, expired, accepted, rejected, withdrawn. */
  status: string;
  companyId: string;
  companyName: string;
  /** none, net15 or net30 (the company's terms; never balances or limits). */
  creditTerms: string;
  creditOnHold: boolean;
  rfqId: string | null;
  rfqNumber: string | null;
  contactUserId: string;
  contactName: string;
  assignedTo: string | null;
  assigneeName: string | null;
  latestVersionNo: number;
  createdAt: string;
  updatedAt: string;
  closedAt: string | null;
  closeReason: string | null;
}

export interface StaffQuoteLineCost { costMinorAtCheck: number | null; floorMinorAtCheck: number | null }

export interface StaffQuoteLine {
  id: string;
  lineNo: number;
  variantId: string;
  rfqLineId: string | null;
  sku: string;
  partNumber: string | null;
  titleEn: string;
  titleBn: string;
  quantity: number;
  unitPriceMinor: number;
  vatRate: number;
  lineNetMinor: number;
  lineTaxMinor: number;
  lineTotalMinor: number;
  publicUnitPriceMinor: number;
  lineNote: string | null;
  belowFloorReason: string | null;
  referenceUnitPriceMinor: number | null;
  referenceSource: string | null;
  belowFloor: boolean;
  overAuthority: boolean;
  /** Absent from the JSON unless the signed-in person holds pricing.view_cost. */
  cost?: StaffQuoteLineCost | null;
}

export interface StaffQuoteFlags { belowFloorLineIds: string[]; overAuthorityLineIds: string[]; longValidity: boolean }

export interface StaffQuoteVersion {
  id: string;
  versionNo: number;
  /** draft, pending_approval, sent, expired, accepted, rejected, withdrawn, superseded. */
  status: string;
  rowVersion: number;
  validityDays: number;
  /** as_account or advance. */
  paymentTerms: string;
  /** calculated or fixed. */
  shippingMode: string;
  shipToSiteId: string | null;
  /** Saved fixed-delivery fees, one per seller (D4.18); null when delivery is calculated. */
  shippingFees?: { sellerId: string; shippingMinor: number }[] | null;
  termsText: string | null;
  buyerNote: string | null;
  internalNote: string | null;
  preparerName: string | null;
  contentHash: string | null;
  flags: StaffQuoteFlags | null;
  goodsNetMinor: number;
  taxMinor: number;
  goodsTotalMinor: number;
  shippingTotalMinor: number | null;
  grandTotalMinor: number | null;
  createdBy: string;
  contributors: string[];
  submittedBy: string | null;
  submittedAt: string | null;
  approvedBy: string | null;
  approvedAt: string | null;
  approvalComment: string | null;
  approvalRejectedBy: string | null;
  approvalRejectedAt: string | null;
  sentBy: string | null;
  sentAt: string | null;
  validUntil: string | null;
  acceptedOrderId: string | null;
  acceptedAt: string | null;
  rejectedAt: string | null;
  rejectReason: string | null;
  withdrawnBy: string | null;
  withdrawnAt: string | null;
  withdrawReason: string | null;
  supersededAt: string | null;
  createdAt: string;
  lines: StaffQuoteLine[];
}

export interface StaffQuoteDetail { quote: StaffQuoteSummary; versions: StaffQuoteVersion[] }

// ---------- queries ----------

export type DeskFilter = { status?: string; assigned?: string; companyId?: string; q?: string; page?: number; pageSize?: number };

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export const isDeskGuid = (value: string | null | undefined): boolean => GUID.test((value ?? '').trim());

/**
 * The query string of the inbox and the quote list. Empty values are left out; `assigned` is `me`, `none` or a staff id (anything else
 * is dropped rather than sent: the server answers 400 to it); the page size is at most 50; `q` at most 100 characters (the server cuts there).
 */
export function deskQuery(f: DeskFilter = {}): string {
  const p = new URLSearchParams();
  const status = (f.status ?? '').trim().toLowerCase();
  if (status) p.set('status', status);
  const assigned = (f.assigned ?? '').trim().toLowerCase();
  if (assigned === 'me' || assigned === 'none' || isDeskGuid(assigned)) p.set('assigned', assigned);
  if (f.companyId && isDeskGuid(f.companyId)) p.set('companyId', f.companyId.trim());
  const q = (f.q ?? '').trim().slice(0, 100);
  if (q) p.set('q', q);
  p.set('page', String(Math.max(1, Math.trunc(f.page ?? 1))));
  p.set('pageSize', String(Math.min(50, Math.max(1, Math.trunc(f.pageSize ?? DESK_PAGE_SIZE)))));
  return p.toString();
}

export const deskPageCount = (total: number, pageSize: number): number => Math.max(1, Math.ceil(Math.max(0, total) / Math.max(1, pageSize)));

export const RFQ_STATUS_FILTERS: readonly { value: string; label: string }[] = [
  { value: 'open', label: 'Open (new or being quoted)' },
  { value: 'submitted', label: 'New' },
  { value: 'quoting', label: 'Being quoted' },
  { value: 'answered', label: 'Answered' },
  { value: 'closed', label: 'Closed' },
  { value: 'withdrawn', label: 'Withdrawn by the buyer' },
  { value: '', label: 'Every status' }
];

export const ASSIGNED_FILTERS: readonly { value: string; label: string }[] = [
  { value: 'me', label: 'Assigned to me' },
  { value: 'none', label: 'Not assigned' },
  { value: '', label: 'Anyone' }
];

/** `superseded` is never the latest status, so the server refuses it as a filter and it is not offered. */
export const QUOTE_STATUS_FILTERS: readonly { value: string; label: string }[] = [
  { value: '', label: 'Every status' },
  { value: 'draft', label: 'Draft' },
  { value: 'pending_approval', label: 'Waiting for approval' },
  { value: 'sent', label: 'Sent to the buyer' },
  { value: 'expired', label: 'Expired' },
  { value: 'accepted', label: 'Accepted' },
  { value: 'rejected', label: 'Rejected by the buyer' },
  { value: 'withdrawn', label: 'Withdrawn' },
  { value: 'closed', label: 'Closed' }
];

// ---------- words for statuses ----------

const RFQ_STATUS_LABEL: Record<string, string> = {
  submitted: 'New', quoting: 'Being quoted', answered: 'Answered', closed: 'Closed', withdrawn: 'Withdrawn by the buyer'
};
export const rfqStatusLabel = (status: string): string => RFQ_STATUS_LABEL[status] ?? status.replace(/_/g, ' ');

/** An RFQ still waiting for staff: new or being quoted. */
export const isOpenRfq = (status: string): boolean => status === 'submitted' || status === 'quoting';
/** Closed or withdrawn: nothing can be assigned, closed or quoted any more. */
export const isFinishedRfq = (status: string): boolean => status === 'closed' || status === 'withdrawn';

const QUOTE_STATUS_LABEL: Record<string, string> = {
  draft: 'Draft', pending_approval: 'Waiting for approval', sent: 'Sent to the buyer', expired: 'Expired', accepted: 'Accepted',
  rejected: 'Rejected by the buyer', withdrawn: 'Withdrawn', superseded: 'Replaced by a newer version', closed: 'Closed'
};
export const quoteStatusLabel = (status: string): string => QUOTE_STATUS_LABEL[status] ?? status.replace(/_/g, ' ');

const CREDIT_TERMS_LABEL: Record<string, string> = { none: 'No credit terms', net15: 'Net 15 days', net30: 'Net 30 days' };
export const creditTermsLabel = (terms: string): string => CREDIT_TERMS_LABEL[terms] ?? terms;

const PAYMENT_TERMS_LABEL: Record<string, string> = { as_account: 'As the account allows', advance: 'Advance payment only' };
export const paymentTermsLabel = (terms: string): string => PAYMENT_TERMS_LABEL[terms] ?? terms.replace(/_/g, ' ');

const SHIPPING_MODE_LABEL: Record<string, string> = { calculated: 'Calculated at order time', fixed: 'Fixed amount' };
export const shippingModeLabel = (mode: string): string => SHIPPING_MODE_LABEL[mode] ?? mode.replace(/_/g, ' ');

const SOURCE_LABEL: Record<string, string> = { cart: 'From the cart', text: 'Pasted list', csv: 'File upload', product: 'One product' };
export const rfqSourceLabel = (source: string): string => SOURCE_LABEL[source] ?? source;

export const assigneeText = (name: string | null | undefined, assignedTo: string | null | undefined): string =>
  name ? name : assignedTo ? 'A staff member' : 'Not assigned';

// ---------- lines of an RFQ ----------

export type StaffLineKind = 'matched' | 'ambiguous' | 'notFound' | 'obsolete' | 'other';

export function staffLineKind(matchStatus: string): StaffLineKind {
  switch (matchStatus) {
    case 'matched': return 'matched';
    case 'ambiguous': return 'ambiguous';
    case 'not_found': return 'notFound';
    case 'obsolete': return 'obsolete';
    default: return 'other';
  }
}

const LINE_LABEL: Record<StaffLineKind, string> = {
  matched: 'Matched', ambiguous: 'Several parts match', notFound: 'Not in the catalogue', obsolete: 'Obsolete part', other: 'Unknown'
};
export const staffLineLabel = (matchStatus: string): string => LINE_LABEL[staffLineKind(matchStatus)];

/** What staff should do about a line, in one sentence. */
export function staffLineHint(line: Pick<StaffRfqLine, 'matchStatus' | 'successor'>): string {
  switch (staffLineKind(line.matchStatus)) {
    case 'matched': return 'Pre-filled at the company price when the quote is created.';
    case 'obsolete': return line.successor ? 'No longer sold. The replacement below can be quoted instead.' : 'No longer sold and no replacement is known.';
    case 'ambiguous': return 'More than one part fits this text. Pick the right one in the quote editor.';
    case 'notFound': return 'Not found in the catalogue. Find the part by hand, or tell the buyer.';
    default: return '';
  }
}

export function staffLineCounts(lines: readonly Pick<StaffRfqLine, 'matchStatus'>[]): { matched: number; obsolete: number; unmatched: number } {
  let matched = 0, obsolete = 0, unmatched = 0;
  for (const l of lines) {
    const k = staffLineKind(l.matchStatus);
    if (k === 'matched') matched++; else if (k === 'obsolete') obsolete++; else unmatched++;
  }
  return { matched, obsolete, unmatched };
}

// ---------- time ----------

/** "35 min", "2 h 05 min", "11 h" of working time (Sunday to Thursday, 09:00 to 18:00 Dhaka). */
export function workingTimeText(minutes: number): string {
  if (!Number.isFinite(minutes) || minutes < 0) return '';
  const total = Math.floor(minutes);
  const h = Math.floor(total / 60);
  const m = total % 60;
  if (h === 0) return `${m} min`;
  return m === 0 ? `${h} h` : `${h} h ${String(m).padStart(2, '0')} min`;
}

function asUtcMs(iso: string): number {
  const hasZone = /(Z|[+-]\d{2}:?\d{2})$/.test(iso);
  return Date.parse(hasZone ? iso : `${iso}Z`);
}

/** A moment on the Dhaka clock (UTC+6, no daylight saving): "2026-11-22 10:00 Dhaka". Empty for a missing or broken value. */
export function dhakaWhen(iso: string | null | undefined): string {
  if (!iso) return '';
  const ms = asUtcMs(iso);
  if (!Number.isFinite(ms)) return '';
  return `${new Date(ms + 6 * 3600 * 1000).toISOString().replace('T', ' ').slice(0, 16)} Dhaka`;
}

export const dhakaDay = (iso: string | null | undefined): string => dhakaWhen(iso).slice(0, 10);

// ---------- SLA ----------

export type SlaState = 'breached' | 'on_track' | 'answered_in_time' | 'answered_late' | 'stopped_in_time' | 'stopped_late';
export interface SlaView {
  state: SlaState;
  /** True when the time budget was used up. Always spelled out in `label`: never only a colour. */
  breached: boolean;
  /** Short badge text. */
  label: string;
  /** The sentence behind the badge. */
  detail: string;
}

/**
 * The first-quote clock of an RFQ in words. The server counts working minutes only and stops the clock when the first quote is sent or
 * the request is closed or withdrawn; `slaBreached` is its own verdict (waited more than the budget) and is not recomputed here.
 */
export function rfqSla(r: Pick<StaffRfqSummary, 'status' | 'firstQuoteSentAt' | 'slaDueAt' | 'slaWorkingMinutes' | 'slaBreached'>): SlaView {
  const waited = workingTimeText(r.slaWorkingMinutes);
  const due = dhakaWhen(r.slaDueAt);
  if (r.firstQuoteSentAt) {
    return r.slaBreached
      ? { state: 'answered_late', breached: true, label: 'Answered late', detail: `The first quote went out after ${waited} of working time, past the ${due} target.` }
      : { state: 'answered_in_time', breached: false, label: 'Answered in time', detail: `The first quote went out after ${waited} of working time.` };
  }
  if (isFinishedRfq(r.status)) {
    return r.slaBreached
      ? { state: 'stopped_late', breached: true, label: 'Closed, target missed', detail: `Closed after ${waited} of working time without a quote, past the ${due} target.` }
      : { state: 'stopped_in_time', breached: false, label: 'Closed in time', detail: `Closed after ${waited} of working time.` };
  }
  return r.slaBreached
    ? { state: 'breached', breached: true, label: 'SLA breached', detail: `Waiting ${waited} of working time. A first quote was due by ${due}.` }
    : { state: 'on_track', breached: false, label: 'Within SLA', detail: `Waiting ${waited} of working time. A first quote is due by ${due}.` };
}

// ---------- commands ----------

export const DESK_REASON_MIN = 5;
export const DESK_REASON_MAX = 500;

/** 5 to 500 characters after trimming (the server's rule). */
export function deskReasonProblem(reason: string | null | undefined): string | null {
  const n = (reason ?? '').trim().length;
  if (n === 0) return 'Enter a reason.';
  return n < DESK_REASON_MIN || n > DESK_REASON_MAX ? `The reason must be ${DESK_REASON_MIN} to ${DESK_REASON_MAX} characters.` : null;
}

export interface DeskHint { allowed: boolean; reason: string }
const OK: DeskHint = { allowed: true, reason: '' };

/** Hint for the assign and close buttons of an RFQ; the server decides. */
export function rfqCommandHint(rfq: Pick<StaffRfqSummary, 'status'>, permissions: readonly string[]): DeskHint {
  if (!canEditQuotes(permissions)) return { allowed: false, reason: 'Your role may read quotes but not change them (quotes.edit).' };
  if (isFinishedRfq(rfq.status)) return { allowed: false, reason: `A ${rfqStatusLabel(rfq.status).toLowerCase()} request cannot be changed.` };
  return OK;
}

/** The buyer's request can start a quote while it is open and has at least one line. */
export function createQuoteHint(rfq: Pick<StaffRfqSummary, 'status'>, permissions: readonly string[]): DeskHint {
  const base = rfqCommandHint(rfq, permissions);
  return base.allowed ? OK : base;
}

/** A quote can be revised when it is not closed and its latest version is no longer open (sent, expired, rejected, withdrawn, accepted). */
export function reviseHint(q: Pick<StaffQuoteSummary, 'status'>, permissions: readonly string[]): DeskHint {
  if (!canEditQuotes(permissions)) return { allowed: false, reason: 'Your role may read quotes but not change them (quotes.edit).' };
  if (q.status === 'closed') return { allowed: false, reason: 'A closed quote cannot be revised.' };
  if (q.status === 'draft' || q.status === 'pending_approval') return { allowed: false, reason: 'This quote already has an open version. Finish or recall it first.' };
  return OK;
}

export function closeQuoteHint(q: Pick<StaffQuoteSummary, 'status'>, permissions: readonly string[]): DeskHint {
  if (!canEditQuotes(permissions)) return { allowed: false, reason: 'Your role may read quotes but not change them (quotes.edit).' };
  if (q.status === 'closed') return { allowed: false, reason: 'This quote is already closed.' };
  return OK;
}

/** Problem with the staff id typed for an assignment, or null. */
export function assigneeIdProblem(value: string): string | null {
  const v = value.trim();
  if (!v) return 'Enter the staff member\'s user id.';
  return isDeskGuid(v) ? null : 'A user id looks like 0b9f1c52-6d3e-4c1a-9a55-2f7e8d1c4b60.';
}

/** Problems with the "new quote for a company" form, keyed by field. The server decides everything else (active company, contact role). */
export function newQuoteProblems(input: { companyId: string; contactUserId: string }): Record<string, string> {
  const out: Record<string, string> = {};
  if (!isDeskGuid(input.companyId)) out.companyId = 'Choose a company.';
  const contact = input.contactUserId.trim();
  if (!contact) out.contactUserId = 'Enter the buyer contact\'s user id.';
  else if (!isDeskGuid(contact)) out.contactUserId = 'A user id looks like 0b9f1c52-6d3e-4c1a-9a55-2f7e8d1c4b60.';
  return out;
}

/** Body of `POST /admin/quotes`: from an RFQ (its requester is the contact) or for a company with a chosen contact. */
export function createQuoteBody(input: { rfqId: string } | { companyId: string; contactUserId: string }): Record<string, string> {
  return 'rfqId' in input ? { rfqId: input.rfqId } : { companyId: input.companyId, contactUserId: input.contactUserId.trim() };
}

// ---------- the quote itself ----------

/** The version staff read first: the one with the highest number. */
export function latestVersion(versions: readonly StaffQuoteVersion[]): StaffQuoteVersion | null {
  let best: StaffQuoteVersion | null = null;
  for (const v of versions) if (!best || v.versionNo > best.versionNo) best = v;
  return best;
}

/** Newest first, the order the page lists them in. */
export const versionsNewestFirst = (versions: readonly StaffQuoteVersion[]): StaffQuoteVersion[] => [...versions].sort((a, b) => b.versionNo - a.versionNo);

/** True when the API sent a cost object on at least one line (so the cost column is shown). Never inferred from permissions. */
export const hasCostFigures = (lines: readonly Pick<StaffQuoteLine, 'cost'>[]): boolean => lines.some((l) => l.cost != null);

/** Money as "1,234.50" (BDT minor units). */
export function quoteMoney(minor: number | null | undefined): string {
  if (minor === null || minor === undefined || !Number.isFinite(minor)) return '';
  const abs = Math.abs(minor);
  return `${minor < 0 ? '-' : ''}${Math.floor(abs / 100).toLocaleString('en-US')}.${String(abs % 100).padStart(2, '0')}`;
}

/** Plain words for the flags a version carries, so the reader never relies on a colour. */
export function flagWords(flags: StaffQuoteFlags | null, lineNoById: ReadonlyMap<string, number> = new Map()): string[] {
  if (!flags) return [];
  const lines = (ids: readonly string[]) => ids.map((id) => lineNoById.get(id)).filter((n): n is number => n !== undefined).sort((a, b) => a - b);
  const out: string[] = [];
  const below = lines(flags.belowFloorLineIds);
  if (flags.belowFloorLineIds.length > 0) out.push(below.length > 0 ? `Below the margin floor on line ${below.join(', ')}.` : `${flags.belowFloorLineIds.length} line(s) below the margin floor.`);
  const over = lines(flags.overAuthorityLineIds);
  if (flags.overAuthorityLineIds.length > 0) out.push(over.length > 0 ? `Beyond the preparer's price authority on line ${over.join(', ')}.` : `${flags.overAuthorityLineIds.length} line(s) beyond the preparer's price authority.`);
  if (flags.longValidity) out.push('Valid for longer than the usual period.');
  return out;
}

// ---------- errors ----------

export interface DeskErrorLike { status: number | null; code: string | null; message: string; fields: Record<string, string> }

const DESK_ERROR_TEXT: Record<string, string> = {
  QUOTE_CONFLICT_OF_INTEREST: 'You belong to this company, so you may not create or change its quotes. Ask a colleague who does not work for it.',
  QUOTE_VERSION_CHANGED: 'Someone changed this quote at the same moment. Reload it, look at what changed, then try again.',
  QUOTE_NUMBER_CLASH: 'The quote could not be numbered because another one was created at the same moment. Try again.',
  QUOTE_OPEN_VERSION_EXISTS: 'This quote already has an open version. Reload it: finish or recall that version first.',
  QUOTE_CLOSED: 'This quote is closed. Reload to see its state.',
  QUOTE_ALREADY_ACCEPTED: 'The buyer already accepted this quote, so it cannot be revised or closed here. Reload to see the order.',
  RFQ_INVALID_TRANSITION: 'This request was already closed or withdrawn, so nothing more can be done with it. Reload to see its state.',
  COMPANY_NOT_ACTIVE: 'Quotes are made only for active companies. This company is not active.'
};

/** The 409 codes that mean the screen shows an old state: it offers Reload (and reloads itself). */
export const DESK_RELOAD_CODES: ReadonlySet<string> = new Set([
  'QUOTE_VERSION_CHANGED', 'QUOTE_OPEN_VERSION_EXISTS', 'QUOTE_CLOSED', 'QUOTE_ALREADY_ACCEPTED', 'RFQ_INVALID_TRANSITION'
]);
export const shouldReloadDesk = (code: string | null | undefined): boolean => !!code && DESK_RELOAD_CODES.has(code);

export function deskErrorText(e: DeskErrorLike): string {
  if (e.code && DESK_ERROR_TEXT[e.code]) return DESK_ERROR_TEXT[e.code];
  const first = Object.values(e.fields)[0];
  if (e.status === 400 && first) return first;
  if (e.status === null) return 'The server cannot be reached. Check your connection and try again.';
  if (e.status === 401) return 'Your session ended. Sign in again.';
  if (e.status === 403) return 'You do not have permission for this action.';
  if (e.status === 404) return 'This request, quote or company was not found.';
  if (e.status === 429) return 'Too many requests. Wait a moment and try again.';
  if (e.status >= 500) return 'The server had a problem. Try again in a moment; if it keeps happening, tell the technical team.';
  return e.message || 'The request was refused.';
}
