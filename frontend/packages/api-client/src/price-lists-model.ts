/** Shapes and pure helpers for the staff price-list editor (D2.7 API, D2.11 screens). Limits mirror the server (ADR-0008). */

export const PRICE_LIST_MAX_UNIT_PRICE_MINOR = 100_000_000_000; // 1,000,000,000.00 taka
export const PRICE_LIST_MAX_VALIDITY_DAYS = 366;
export const PRICE_LIST_MAX_BATCH = 500;
export const PRICE_LIST_MAX_ENTRIES = 5_000;
export const PRICE_LIST_MAX_MIN_QTY = 10_000;
export const PRICE_LIST_MAX_CONTRACT_REF = 64;
export const PRICE_LIST_REASON_MIN = 5;
export const PRICE_LIST_REASON_MAX = 500;

export type PriceListScope = 'General' | 'Company';
export type PriceListStatus = 'Draft' | 'PendingApproval' | 'Approved' | 'Withdrawn';
export type PriceListDisplayState = 'Draft' | 'PendingApproval' | 'Withdrawn' | 'Scheduled' | 'Active' | 'Expired' | 'Superseded';

export interface PriceListSummary {
  id: string;
  seriesId: string;
  version: number;
  name: string;
  scope: PriceListScope | 'Group' | string;
  companyId: string | null;
  contractRef: string | null;
  status: PriceListStatus | string;
  displayState: PriceListDisplayState | string;
  validFromUtc: string;
  validToUtc: string;
  entryCount: number;
  createdBy: string;
  createdAtUtc: string;
  submittedBy: string | null;
  submittedAtUtc: string | null;
}

export interface PriceListPage {
  items: PriceListSummary[];
  page: number;
  pageSize: number;
  total: number;
}

/** Cost, floor and the flags exist only for callers holding `pricing.view_cost`; their absence is normal. */
export interface PriceListEntry {
  id: string;
  variantId: string;
  sku: string;
  title: string;
  minQty: number;
  unitPriceMinor: number;
  belowFloorReason: string | null;
  costMinor?: number;
  floorMinor?: number;
  belowFloor?: boolean;
  belowCost?: boolean;
  costUnknown?: boolean;
}

export interface PriceListDetail extends PriceListSummary {
  contentHash: string | null;
  effectiveFromUtc: string | null;
  approvedBy: string | null;
  approvedAtUtc: string | null;
  rejectionReason: string | null;
  withdrawReason: string | null;
  contributors: string[];
  entries: PriceListEntry[];
}

export interface CreatePriceListBody {
  scope: PriceListScope;
  companyId?: string | null;
  contractRef?: string | null;
  name: string;
  validFromUtc: string;
  validToUtc: string;
}

export interface UpdatePriceListHeaderBody {
  name: string;
  contractRef?: string | null;
  validFromUtc: string;
  validToUtc: string;
}

export interface UpsertEntryItem {
  variantId: string;
  minQty: number;
  unitPriceMinor: number;
  belowFloorReason?: string | null;
}

export interface PriceListFilters {
  status?: string;
  scope?: string;
  companyId?: string;
  q?: string;
  page?: number;
  pageSize?: number;
}

export const PRICE_LIST_PAGE_SIZE = 20;

/** Query string for the list endpoint: empties omitted, page size capped at the server's 50. */
export function priceListQuery(f: PriceListFilters = {}): string {
  const p = new URLSearchParams();
  if (f.status) p.set('status', f.status);
  if (f.scope) p.set('scope', f.scope);
  if (f.companyId) p.set('companyId', f.companyId);
  if (f.q?.trim()) p.set('q', f.q.trim());
  p.set('page', String(Math.max(1, Math.floor(f.page ?? 1))));
  p.set('pageSize', String(Math.min(50, Math.max(1, Math.floor(f.pageSize ?? PRICE_LIST_PAGE_SIZE)))));
  return p.toString();
}

export const STATUS_LABEL: Record<string, string> = {
  Draft: 'Draft',
  PendingApproval: 'Pending approval',
  Approved: 'Approved',
  Withdrawn: 'Withdrawn'
};

export const DISPLAY_STATE_LABEL: Record<string, string> = {
  Draft: 'Draft',
  PendingApproval: 'Pending approval',
  Withdrawn: 'Withdrawn',
  Scheduled: 'Approved, scheduled',
  Active: 'Approved, active',
  Expired: 'Expired',
  Superseded: 'Superseded by a newer version'
};

export function statusLabel(status: string): string {
  return STATUS_LABEL[status] ?? status;
}

export function displayStateLabel(state: string): string {
  return DISPLAY_STATE_LABEL[state] ?? state;
}

/** Only a Draft can be edited or submitted or deleted. */
export const isEditable = (status: string): boolean => status === 'Draft';
export const canRevise = (status: string): boolean => status === 'Approved' || status === 'Withdrawn';

// ---------- money ----------

/** Poisha integer to "1,234.50" (no symbol; the column header says BDT). Integer arithmetic only. */
export function formatTaka(minor: number): string {
  const negative = minor < 0;
  const abs = Math.abs(minor);
  const whole = Math.floor(abs / 100);
  const frac = String(abs % 100).padStart(2, '0');
  return `${negative ? '-' : ''}${whole.toLocaleString('en-US')}.${frac}`;
}

/**
 * Decimal taka text to poisha without floats: "12", "12.5", "12.50", "1,234.5" are fine; more than two decimals, a sign,
 * an exponent or anything else is refused (null). The result is not range checked here (see priceProblem).
 */
export function takaToMinor(text: string): number | null {
  const t = text.trim().replace(/,/g, '').replace(/^৳\s*/, '');
  const m = /^(\d{1,12})(?:\.(\d{1,2}))?$/.exec(t);
  if (!m) return null;
  return Number(m[1]) * 100 + Number((m[2] ?? '').padEnd(2, '0') || '0');
}

// ---------- validation mirroring the server ----------

export type PriceProblem = 'price.format' | 'price.tooLow' | 'price.tooHigh';

export function priceProblem(minor: number | null): PriceProblem | null {
  if (minor === null || !Number.isSafeInteger(minor)) return 'price.format';
  if (minor < 1) return 'price.tooLow';
  if (minor > PRICE_LIST_MAX_UNIT_PRICE_MINOR) return 'price.tooHigh';
  return null;
}

export function minQtyProblem(minQty: number, scope: string): string | null {
  if (!Number.isInteger(minQty) || minQty < 1) return 'Minimum quantity must be a whole number, at least 1.';
  if (minQty > PRICE_LIST_MAX_MIN_QTY) return `Minimum quantity cannot be more than ${PRICE_LIST_MAX_MIN_QTY.toLocaleString('en-US')}.`;
  if (scope === 'General' && minQty < 2) return 'On a General list every entry is a quantity break: minimum quantity must be at least 2.';
  return null;
}

export const PRICE_TEXT: Record<PriceProblem, string> = {
  'price.format': 'Enter the price in taka, with at most two decimals (for example 1250.50).',
  'price.tooLow': 'The price must be at least 0.01.',
  'price.tooHigh': 'The price is above the maximum of 1,000,000,000.00.'
};

export function reasonProblem(reason: string | null | undefined): string | null {
  const r = (reason ?? '').trim();
  if (r.length === 0) return null;
  if (r.length < PRICE_LIST_REASON_MIN) return `The reason needs at least ${PRICE_LIST_REASON_MIN} characters.`;
  if (r.length > PRICE_LIST_REASON_MAX) return `The reason can be at most ${PRICE_LIST_REASON_MAX} characters.`;
  return null;
}

export interface HeaderInput {
  scope: string;
  companyId?: string | null;
  contractRef?: string | null;
  name: string;
  validFromUtc: string;
  validToUtc: string;
}

export type HeaderErrors = Partial<Record<'name' | 'companyId' | 'contractRef' | 'validFromUtc' | 'validToUtc' | 'scope', string>>;

const DAY_MS = 86_400_000;

/** Header checks mirroring the server. Dates are ISO strings (or anything Date parses). */
export function validateHeader(h: HeaderInput): HeaderErrors {
  const e: HeaderErrors = {};
  if (h.scope !== 'General' && h.scope !== 'Company') e.scope = 'Choose General or Company.';
  if (!h.name.trim()) e.name = 'Enter a name.';
  else if (h.name.trim().length > 200) e.name = 'The name can be at most 200 characters.';
  const ref = (h.contractRef ?? '').trim();
  if (h.scope === 'Company') {
    if (!h.companyId) e.companyId = 'Choose the company this contract is for.';
    if (!ref) e.contractRef = 'A Company list needs the contract reference.';
  }
  if (ref.length > PRICE_LIST_MAX_CONTRACT_REF) e.contractRef = `The contract reference can be at most ${PRICE_LIST_MAX_CONTRACT_REF} characters.`;
  const from = Date.parse(h.validFromUtc);
  const to = Date.parse(h.validToUtc);
  if (Number.isNaN(from)) e.validFromUtc = 'Enter a start date.';
  if (Number.isNaN(to)) e.validToUtc = 'Enter an end date.';
  if (!e.validFromUtc && !e.validToUtc) {
    if (to <= from) e.validToUtc = 'The end must be after the start.';
    else if ((to - from) / DAY_MS > PRICE_LIST_MAX_VALIDITY_DAYS) e.validToUtc = `A list can be valid for at most ${PRICE_LIST_MAX_VALIDITY_DAYS} days.`;
  }
  return e;
}

/** "2026-12-01" (a local date from an <input type="date">) to the start of that day in UTC, as an ISO string. */
export function dateToUtcIso(date: string): string {
  return /^\d{4}-\d{2}-\d{2}$/.test(date) ? `${date}T00:00:00.000Z` : '';
}

/** ISO instant to the "YYYY-MM-DD" an <input type="date"> shows (UTC date). */
export function isoToDateInput(iso: string): string {
  return /^\d{4}-\d{2}-\d{2}/.test(iso) ? iso.slice(0, 10) : '';
}

export function chunkEntries<T>(items: readonly T[], size = PRICE_LIST_MAX_BATCH): T[][] {
  const out: T[][] = [];
  for (let i = 0; i < items.length; i += size) out.push(items.slice(i, i + size));
  return out;
}

// ---------- bulk paste ----------

export interface PasteLine {
  line: number;
  sku: string;
  minQty: number;
  unitPriceMinor: number;
  reason: string | null;
}

export interface PasteProblem {
  line: number;
  text: string;
  message: string;
}

export interface PasteResult {
  lines: PasteLine[];
  problems: PasteProblem[];
}

/**
 * Parses "SKU, minQty, price[, reason]" lines (tab, semicolon or comma separated, first one found wins, so use ';' when a price has a thousands comma; blank lines and a header row starting with
 * "sku" are skipped; later duplicates of the same SKU and minQty are reported). Price is decimal taka, parsed as text.
 */
export function parsePaste(text: string, scope: string = 'Company'): PasteResult {
  const lines: PasteLine[] = [];
  const problems: PasteProblem[] = [];
  const seen = new Set<string>();
  text.split(/\r?\n/).forEach((raw, i) => {
    const line = i + 1;
    const trimmed = raw.trim();
    if (!trimmed) return;
    const delimiter = trimmed.includes('\t') ? '\t' : trimmed.includes(';') ? ';' : ',';
    const parts = trimmed.split(delimiter).map((p) => p.trim());
    if (/^sku$/i.test(parts[0] ?? '')) return;
    if (parts.length < 3) return void problems.push({ line, text: trimmed, message: 'Expected "SKU, minimum quantity, price".' });
    const [sku, qtyText, priceText, ...rest] = parts;
    if (!sku) return void problems.push({ line, text: trimmed, message: 'The SKU is empty.' });
    if (!/^\d+$/.test(qtyText)) return void problems.push({ line, text: trimmed, message: 'Minimum quantity must be a whole number.' });
    const minQty = Number(qtyText);
    const qtyError = minQtyProblem(minQty, scope);
    if (qtyError) return void problems.push({ line, text: trimmed, message: qtyError });
    const minor = takaToMinor(priceText);
    const pp = priceProblem(minor);
    if (pp || minor === null) return void problems.push({ line, text: trimmed, message: PRICE_TEXT[pp ?? 'price.format'] });
    const reason = rest.join(delimiter).trim() || null;
    const rp = reasonProblem(reason);
    if (rp) return void problems.push({ line, text: trimmed, message: rp });
    const key = `${sku.toLowerCase()}|${minQty}`;
    if (seen.has(key)) return void problems.push({ line, text: trimmed, message: `${sku} with minimum quantity ${minQty} appears twice.` });
    seen.add(key);
    lines.push({ line, sku, minQty, unitPriceMinor: minor, reason });
  });
  return { lines, problems };
}

// ---------- submit readiness ----------

/** Entries the API flagged below floor (only visible with pricing.view_cost) that still lack a valid reason. */
export function entriesNeedingReason(entries: readonly PriceListEntry[]): PriceListEntry[] {
  return entries.filter((e) => e.belowFloor === true && (e.belowFloorReason ?? '').trim().length < PRICE_LIST_REASON_MIN);
}

export function breaksOutOfOrder(entries: readonly PriceListEntry[]): string[] {
  const byVariant = new Map<string, PriceListEntry[]>();
  for (const e of entries) byVariant.set(e.variantId, [...(byVariant.get(e.variantId) ?? []), e]);
  const bad: string[] = [];
  for (const list of byVariant.values()) {
    const sorted = [...list].sort((a, b) => a.minQty - b.minQty);
    for (let i = 1; i < sorted.length; i++) {
      if (sorted[i].unitPriceMinor > sorted[i - 1].unitPriceMinor) {
        bad.push(sorted[i].sku);
        break;
      }
    }
  }
  return bad;
}

// ---------- error mapping ----------

export const CONFLICT_TEXT: Record<string, string> = {
  PRICE_LIST_NOT_EDITABLE: 'This list is no longer a draft, so it cannot be changed. Reload to see its current state.',
  PRICE_LIST_NOT_REVISABLE: 'Only an approved or withdrawn list can be revised.',
  PRICE_LIST_NOT_LATEST: 'A newer version of this list exists. Open the newest version to revise it.',
  PRICE_LIST_OPEN_VERSION_EXISTS: 'This list already has a newer draft or pending version. Finish or delete that one first.',
  PRICE_LIST_NOT_DELETABLE: 'Only a draft can be deleted.'
};

export interface ErrorLike {
  status: number | null;
  code: string | null;
  message: string;
  fields: Record<string, string>;
}

/** Plain-words message for a failed price-list call. `err` fields come from problemStatus/Code/Message/FieldErrors. */
export function priceListErrorText(e: ErrorLike): string {
  if (e.status === 401) return 'Your session has ended. Sign in again.';
  if (e.status === 403) return 'You do not have permission to do this.';
  if (e.status === 404) return 'This price list no longer exists.';
  if (e.status === 409) return (e.code && CONFLICT_TEXT[e.code]) || e.message || 'Another change got in the way. Reload and try again.';
  if (e.status === 400) {
    const first = Object.values(e.fields)[0];
    return first ? `${e.message || 'The request was rejected.'} ${first}`.trim() : e.message || 'The request was rejected.';
  }
  if (e.status !== null && e.status >= 500) return 'The server had a problem. Nothing was changed; try again in a moment.';
  return 'Could not reach the server. Check your connection and try again.';
}

/** Server field keys like `entries.<entryId>.belowFloorReason` to a map of entry id to message. */
export function entryFieldErrors(fields: Record<string, string>): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [key, message] of Object.entries(fields)) {
    const m = /^entries\.([^.]+)\./i.exec(key);
    if (m && !out[m[1]]) out[m[1]] = message;
  }
  return out;
}
