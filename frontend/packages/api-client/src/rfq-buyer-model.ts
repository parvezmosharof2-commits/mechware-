// Buyer RFQ ("Request a quote") types and pure helpers (D4.13). Hand-typed from the D4.5 HANDOFF and Mechware.Application.Quotes.RfqContracts:
// the OpenAPI document has no response schema for these routes. The server is the only authority for lines, matching and caps;
// nothing here computes money or decides who may see what.

export const RFQ_MAX_TEXT = 100_000;
export const RFQ_MAX_MESSAGE = 2000;
export const RFQ_MAX_QUANTITY = 10_000;

export type RfqSource = 'cart' | 'text' | 'csv' | 'product';
export type RfqStatus = 'Submitted' | 'InReview' | 'Quoted' | 'Closed' | (string & {});
export type RfqMatchStatus = 'matched' | 'ambiguous' | 'not_found' | 'obsolete' | (string & {});

export interface RfqVariant {
  id: string;
  sku: string;
  title: string;
  titleBn: string;
}

export interface RfqLine {
  lineNo: number;
  rawText: string;
  partKey: string | null;
  quantity: number;
  matchStatus: RfqMatchStatus;
  variant: RfqVariant | null;
  successor: RfqVariant | null;
  note: string | null;
}

export interface RfqSummary {
  id: string;
  number: string;
  status: RfqStatus;
  source: RfqSource | (string & {});
  lineCount: number;
  message: string | null;
  neededBy: string | null;
  shipToSiteId: string | null;
  requestedByMe: boolean;
  createdAt: string;
  closedAt: string | null;
  closeReason: string | null;
  withdrawnAt: string | null;
}

export interface RfqDetail {
  rfq: RfqSummary;
  lines: RfqLine[];
}

export interface RfqPageDto {
  items: RfqSummary[];
  page: number;
  pageSize: number;
  total: number;
}

/** What the page may send. Lines, prices and the company are never in it (the server reads its own cart / parses the text). */
export interface CreateRfqInput {
  source: RfqSource;
  text?: string;
  variantId?: string;
  quantity?: number;
  message?: string;
  neededBy?: string;
  shipToSiteId?: string;
}

/** The body that is sent: only the members that source reads, blanks dropped, text kept exactly as typed (the server cleans it). */
export function buildRfqBody(input: CreateRfqInput): Record<string, unknown> {
  const body: Record<string, unknown> = { source: input.source };
  if (input.source === 'text' || input.source === 'csv') body.text = input.text ?? '';
  if (input.source === 'product') {
    body.variantId = input.variantId;
    body.quantity = input.quantity;
  }
  const message = input.message?.trim();
  if (message) body.message = message;
  if (input.neededBy) body.neededBy = input.neededBy;
  if (input.shipToSiteId) body.shipToSiteId = input.shipToSiteId;
  return body;
}

/** A stable text of the body: the same details give the same fingerprint, so a double click or retry reuses one Idempotency-Key. */
export function rfqFingerprint(companyId: string, input: CreateRfqInput): string {
  return JSON.stringify({ companyId: companyId.toLowerCase(), body: buildRfqBody(input) });
}

// ---------- input checks (UX only; the server repeats them) ----------

export type RfqInputProblem = 'textEmpty' | 'textTooLarge' | 'variantMissing' | 'quantityInvalid' | 'quantityTooLarge' | 'messageTooLong' | 'neededByInvalid';

/** A calendar day as YYYY-MM-DD (what an <input type="date"> gives). */
export function isRfqDay(value: string): boolean {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!m) return false;
  const d = new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3])));
  return d.getUTCFullYear() === Number(m[1]) && d.getUTCMonth() === Number(m[2]) - 1 && d.getUTCDate() === Number(m[3]);
}

export function rfqInputProblem(input: CreateRfqInput): RfqInputProblem | null {
  if (input.source === 'text' || input.source === 'csv') {
    if (!input.text || input.text.trim() === '') return 'textEmpty';
    if (input.text.length > RFQ_MAX_TEXT) return 'textTooLarge';
  }
  if (input.source === 'product') {
    if (!input.variantId) return 'variantMissing';
    if (!Number.isInteger(input.quantity) || (input.quantity ?? 0) < 1) return 'quantityInvalid';
    if ((input.quantity ?? 0) > RFQ_MAX_QUANTITY) return 'quantityTooLarge';
  }
  if ((input.message?.trim().length ?? 0) > RFQ_MAX_MESSAGE) return 'messageTooLong';
  if (input.neededBy && !isRfqDay(input.neededBy)) return 'neededByInvalid';
  return null;
}

// ---------- errors ----------

export type RfqErrorKind =
  | 'companyNotVerified'
  | 'companySuspended'
  | 'accountNotVerified'
  | 'linesInvalid'
  | 'qtyExceeds'
  | 'tooLarge'
  | 'rateLimited'
  | 'tooManyOpen'
  | 'keyReused'
  | 'notFound'
  | 'closed'
  | 'signIn'
  | 'unavailable'
  | 'invalid';

function asProblem(error: unknown): { status?: number; code?: string } {
  if (typeof error !== 'object' || error === null) return {};
  const e = error as { status?: unknown; error?: { code?: unknown } };
  return { status: typeof e.status === 'number' ? e.status : undefined, code: typeof e.error?.code === 'string' ? e.error.code : undefined };
}

/** One kind per documented error code; anything else falls back on the HTTP status. */
export function classifyRfqError(error: unknown): RfqErrorKind {
  const { status, code } = asProblem(error);
  switch (code) {
    case 'COMPANY_NOT_VERIFIED': return 'companyNotVerified';
    case 'COMPANY_SUSPENDED': return 'companySuspended';
    case 'ACCOUNT_NOT_VERIFIED': return 'accountNotVerified';
    case 'RFQ_LINES_INVALID': return 'linesInvalid';
    case 'QTY_EXCEEDS_LIMIT': return 'qtyExceeds';
    case 'INPUT_TOO_LARGE': return 'tooLarge';
    case 'RFQ_RATE_LIMITED': return 'rateLimited';
    case 'TOO_MANY_OPEN_RFQS': return 'tooManyOpen';
    case 'IDEMPOTENCY_KEY_REUSED': return 'keyReused';
    case 'RFQ_INVALID_TRANSITION': return 'closed';
    default: break;
  }
  if (status === 404) return 'notFound';
  if (status === 401) return 'signIn';
  if (status === 413) return 'tooLarge';
  if (status === 429) return 'rateLimited';
  if (status === 422 || status === 400) return 'invalid';
  return 'unavailable';
}

/** Whether the same Idempotency-Key may be reused on the next try: only when the answer is unknown (network, 5xx, 429), never after a refusal that depends on the body. */
export function rfqKeepKeyAfter(kind: RfqErrorKind): boolean {
  return kind === 'unavailable' || kind === 'rateLimited' || kind === 'signIn';
}

export interface RfqLineError {
  /** The physical line of the pasted text. */
  lineNo: number;
  code: string | null;
  message: string;
}

/** The per-line problems of a 422 (`lines[n]` = `CODE: message`), sorted by line. Other details are ignored. */
export function rfqLineErrors(error: unknown): RfqLineError[] {
  if (typeof error !== 'object' || error === null) return [];
  const details = (error as { error?: { details?: unknown } }).error?.details;
  if (!Array.isArray(details)) return [];
  const out: RfqLineError[] = [];
  for (const d of details) {
    if (typeof d !== 'object' || d === null) continue;
    const { field, message } = d as { field?: unknown; message?: unknown };
    if (typeof field !== 'string' || typeof message !== 'string') continue;
    const m = /^lines\[(\d+)\]$/i.exec(field);
    if (!m) continue;
    const c = /^([A-Z][A-Z0-9_]+):\s*([\s\S]*)$/.exec(message);
    out.push({ lineNo: Number(m[1]), code: c ? c[1]! : null, message: c ? c[2]! : message });
  }
  return out.sort((a, b) => a.lineNo - b.lineNo);
}

// ---------- who may use it ----------

interface MemberLike {
  company: { id: string; status: string; legalName: string };
  roles: number;
}

export type QuoteAccess = { kind: 'ready'; companyId: string; companyName: string; status: string } | { kind: 'none' };

/** The companies a person may open the RFQ form for: any membership (the API decides by status and answers with a code), active ones first. */
export function quoteCompanies<T extends MemberLike>(mine: readonly T[]): T[] {
  const rank = (s: string) => (s === 'active' ? 0 : s === 'pending' ? 1 : 2);
  return mine
    .filter((m) => m.company.status !== 'rejected')
    .map((m, i) => ({ m, i }))
    .sort((a, b) => rank(a.m.company.status) - rank(b.m.company.status) || a.i - b.i)
    .map((x) => x.m);
}

export function quoteAccess(mine: readonly MemberLike[], preferredId?: string | null): QuoteAccess {
  const all = quoteCompanies(mine);
  const pick = all.find((m) => m.company.id === preferredId) ?? all[0];
  return pick ? { kind: 'ready', companyId: pick.company.id, companyName: pick.company.legalName, status: pick.company.status } : { kind: 'none' };
}

/** Admin (8) and Buyer (2) see every request of the company; everyone else sees their own. The server decides; this only words the page. */
export function seesAllRfqs(roles: number): boolean {
  return (roles & (8 | 2)) !== 0;
}

// ---------- statuses and lines ----------

export type RfqTone = 'neutral' | 'info' | 'ok' | 'muted';

export function rfqStatusKey(status: string): 'submitted' | 'inreview' | 'quoted' | 'closed' | 'other' {
  const s = status.toLowerCase();
  return s === 'submitted' || s === 'inreview' || s === 'quoted' || s === 'closed' ? s : 'other';
}

export function rfqStatusTone(status: string): RfqTone {
  switch (rfqStatusKey(status)) {
    case 'submitted':
    case 'inreview':
      return 'info';
    case 'quoted':
      return 'ok';
    case 'closed':
      return 'muted';
    default:
      return 'neutral';
  }
}

/** A request the buyer may still withdraw: open and not already withdrawn. */
export function canWithdrawRfq(rfq: Pick<RfqSummary, 'status' | 'withdrawnAt' | 'closedAt'>): boolean {
  return rfqStatusKey(rfq.status) !== 'closed' && !rfq.withdrawnAt && !rfq.closedAt;
}

export function rfqLineKey(status: string): 'matched' | 'ambiguous' | 'notFound' | 'obsolete' | 'other' {
  switch (status) {
    case 'matched': return 'matched';
    case 'ambiguous': return 'ambiguous';
    case 'not_found': return 'notFound';
    case 'obsolete': return 'obsolete';
    default: return 'other';
  }
}

export function rfqLineCounts(lines: readonly Pick<RfqLine, 'matchStatus'>[]): { matched: number; obsolete: number; unmatched: number } {
  let matched = 0;
  let obsolete = 0;
  let unmatched = 0;
  for (const l of lines) {
    const k = rfqLineKey(l.matchStatus);
    if (k === 'matched') matched++;
    else if (k === 'obsolete') obsolete++;
    else unmatched++;
  }
  return { matched, obsolete, unmatched };
}

export function variantTitle(v: Pick<RfqVariant, 'title' | 'titleBn'> | null, locale: 'en' | 'bn'): string {
  if (!v) return '';
  return locale === 'bn' && v.titleBn ? v.titleBn : v.title;
}

// ---------- page query and links ----------

export function rfqPageQuery(q: { status?: string; page?: number; pageSize?: number }): string {
  const p = new URLSearchParams();
  if (q.status) p.set('status', q.status);
  p.set('page', String(Math.max(1, Math.floor(q.page ?? 1))));
  p.set('pageSize', String(Math.min(50, Math.max(1, Math.floor(q.pageSize ?? 20)))));
  return p.toString();
}

export function rfqPageCount(total: number, pageSize: number): number {
  return pageSize > 0 ? Math.max(1, Math.ceil(total / pageSize)) : 1;
}

/** Where an entry point links. `source` and the product go in the query; nothing private does. */
export function requestQuoteHref(source: RfqSource, extra: { variantId?: string; quantity?: number; label?: string } = {}): string {
  const p = new URLSearchParams({ source });
  if (extra.variantId) p.set('variantId', extra.variantId);
  if (extra.label) p.set('label', extra.label.slice(0, 120));
  if (extra.quantity && extra.quantity > 0) p.set('quantity', String(Math.floor(extra.quantity)));
  return `/request-quote?${p.toString()}`;
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Reads the page's query. Unknown sources fall back to `text`; a bad variant id or quantity is dropped. */
export function parseRequestQuery(params: { source?: string | null; variantId?: string | null; quantity?: string | null; label?: string | null }): { source: RfqSource; variantId: string; quantity: number; label: string } {
  const wanted: RfqSource = params.source === 'cart' || params.source === 'product' ? params.source : 'text';
  const variantId = params.variantId && UUID.test(params.variantId) ? params.variantId : '';
  const q = Number(params.quantity);
  const quantity = Number.isInteger(q) && q >= 1 && q <= RFQ_MAX_QUANTITY ? q : 1;
  return { source: wanted === 'product' && !variantId ? 'text' : wanted, variantId, quantity, label: (params.label ?? '').slice(0, 120) };
}
