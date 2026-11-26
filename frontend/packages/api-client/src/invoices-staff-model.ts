/**
 * Staff invoice screens (E6.8c). Pure shapes and helpers, no runtime imports of the client. Hand-written from `InvoiceReadContracts.cs` and
 * `InvoicePdfContracts.cs` (E6.8a/E6.4b staff routes, `invoices.view`). Unlike the buyer shapes, the staff detail carries the WHOLE buyer BIN.
 * The buyer module is not changed.
 */
import type { BuyerInvoiceDetail, InvoiceKind, InvoiceLocale, InvoiceSummary } from './invoices-model';

/** A real calendar day, yyyy-MM-dd (same rule as the buyer `isInvoiceDay`; no runtime import so Node can test this file alone). */
function isInvoiceDay(v: string): boolean {
  const d = new Date(`${v}T00:00:00Z`);
  return /^\d{4}-\d{2}-\d{2}$/.test(v) && !Number.isNaN(d.getTime()) && d.toISOString().slice(0, 10) === v;
}

export const STAFF_INVOICE_PAGE_SIZE = 50;

export interface StaffInvoiceListItem { invoice: InvoiceSummary; sellerId: string; companyId: string; buyerName: string }
export interface StaffInvoicePage { items: StaffInvoiceListItem[]; page: number; pageSize: number; totalCount: number }
export interface StaffInvoiceDetail {
  detail: BuyerInvoiceDetail; series: string; fiscalYear: number; serial: number; sellerId: string; companyId: string; subOrderId: string | null;
  shipmentId: string | null; consultantChecked: boolean; contentHash: string; issuedBy: string | null;
}
/** `GET admin/invoice-documents/{sha256}`: which invoice, language and kind a file is. */
export interface InvoiceDocumentMatch { invoiceId: string; number: string; kind: InvoiceKind | string; locale: InvoiceLocale | string }

export const canViewInvoices = (permissions: readonly string[]): boolean => permissions.some((p) => p.toLowerCase() === 'invoices.view');

export const INVOICE_KIND_OPTIONS: ReadonlyArray<{ value: string; label: string }> = [
  { value: 'tax_invoice', label: 'Tax invoice (Mushak-6.3)' }, { value: 'credit_note', label: 'Credit note (Mushak-6.7)' }
];
export const INVOICE_MODE_OPTIONS: ReadonlyArray<{ value: string; label: string }> = [
  { value: 'live', label: 'Live' }, { value: 'specimen', label: 'Specimen (not a tax invoice)' }
];
export const invoiceKindText = (k: string | null | undefined): string => INVOICE_KIND_OPTIONS.find((o) => o.value === k)?.label ?? 'Other document';
export const invoiceModeText = (m: string | null | undefined): string => INVOICE_MODE_OPTIONS.find((o) => o.value === m)?.label ?? 'Other mode';
export const invoiceLocaleText = (l: string | null | undefined): string => (l === 'bn' ? 'Bangla' : l === 'en' ? 'English' : 'Unknown language');

// ---------- filters in the URL ----------

export interface StaffInvoiceFilters {
  number: string; orderNumber: string; companyId: string; companyName: string; from: string; to: string; kind: string; mode: string; page: number;
}
export const EMPTY_INVOICE_FILTERS: StaffInvoiceFilters = { number: '', orderNumber: '', companyId: '', companyName: '', from: '', to: '', kind: '', mode: '', page: 1 };

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const PREFIX = /^[A-Za-z0-9._/-]{1,40}$/;

/** Reads the filters from a query string. A value that is not valid is dropped (a shared link with a typo opens the unfiltered list). */
export function parseStaffInvoiceFilters(get: (key: string) => string | null): StaffInvoiceFilters {
  const pick = (k: string) => (get(k) ?? '').trim();
  const company = pick('company'), from = pick('from'), to = pick('to'), kind = pick('kind'), mode = pick('mode'), page = Number(pick('page'));
  const f = isInvoiceDay(from) ? from : '';
  const t = isInvoiceDay(to) ? to : '';
  return {
    number: PREFIX.test(pick('number')) ? pick('number') : '', orderNumber: PREFIX.test(pick('order')) ? pick('order') : '',
    companyId: GUID.test(company) ? company : '', companyName: GUID.test(company) ? pick('companyName').slice(0, 200) : '',
    from: f, to: f && t && f > t ? '' : t,
    kind: INVOICE_KIND_OPTIONS.some((o) => o.value === kind) ? kind : '', mode: INVOICE_MODE_OPTIONS.some((o) => o.value === mode) ? mode : '',
    page: Number.isInteger(page) && page >= 1 && page <= 100000 ? page : 1
  };
}

/** The query string of the page URL (no leading "?"); defaults are left out. */
export function staffInvoiceFiltersToSearch(f: StaffInvoiceFilters): string {
  const p = new URLSearchParams();
  if (f.number) p.set('number', f.number);
  if (f.orderNumber) p.set('order', f.orderNumber);
  if (f.companyId) { p.set('company', f.companyId); if (f.companyName) p.set('companyName', f.companyName); }
  if (f.from) p.set('from', f.from);
  if (f.to) p.set('to', f.to);
  if (f.kind) p.set('kind', f.kind);
  if (f.mode) p.set('mode', f.mode);
  if (f.page > 1) p.set('page', String(f.page));
  return p.toString();
}

/** A problem to show instead of sending, or null. */
export function staffInvoiceFilterProblem(f: Pick<StaffInvoiceFilters, 'number' | 'orderNumber' | 'from' | 'to'>): string | null {
  if (f.number.trim() && !PREFIX.test(f.number.trim())) return 'The invoice number may hold only letters, digits and . / - (up to 40).';
  if (f.orderNumber.trim() && !PREFIX.test(f.orderNumber.trim())) return 'The order number may hold only letters, digits and . / - (up to 40).';
  if (f.from && !isInvoiceDay(f.from)) return 'The "from" date is not a valid date.';
  if (f.to && !isInvoiceDay(f.to)) return 'The "to" date is not a valid date.';
  if (f.from && f.to && f.from > f.to) return 'The "from" date is after the "to" date.';
  return null;
}

/** The API query for `GET admin/invoices`. */
export function staffInvoiceListQuery(f: StaffInvoiceFilters): string {
  const p = new URLSearchParams();
  if (f.number) p.set('number', f.number);
  if (f.orderNumber) p.set('orderNumber', f.orderNumber);
  if (f.companyId) p.set('companyId', f.companyId);
  if (f.from) p.set('from', f.from);
  if (f.to) p.set('to', f.to);
  if (f.kind) p.set('kind', f.kind);
  if (f.mode) p.set('mode', f.mode);
  p.set('page', String(Math.max(1, Math.trunc(f.page))));
  p.set('pageSize', String(STAFF_INVOICE_PAGE_SIZE));
  return p.toString();
}

export const activeStaffInvoiceFilterCount = (f: StaffInvoiceFilters): number =>
  [f.number, f.orderNumber, f.companyId, f.from, f.to, f.kind, f.mode].filter(Boolean).length;

export const staffInvoicePdfPath = (invoiceId: string, locale: InvoiceLocale): string =>
  `/api/v1/admin/invoices/${encodeURIComponent(invoiceId)}/pdf?locale=${locale === 'bn' ? 'bn' : 'en'}`;

// ---------- words ----------

/** Plain words for a failed staff invoice call; 503 with the integrity code is the tamper case. */
export function staffInvoiceErrorText(e: { status: number | null; code?: string | null }): string {
  if (e.status === 503 && e.code === 'INVOICE_PDF_UNAVAILABLE') return 'The stored PDF could not be verified, so it was not given out. The technical team has been told (an integrity alert is in the audit log).';
  if (e.status === null) return 'The server cannot be reached. Check your connection and try again.';
  if (e.status === 401) return 'Your session ended. Sign in again.';
  if (e.status === 403) return 'You do not have permission to see invoices (invoices.view).';
  if (e.status === 404) return 'This invoice or document was not found.';
  if (e.status === 429) return 'Too many downloads. Wait a minute and try again.';
  if (e.status >= 500) return 'The server had a problem. Try again in a moment; if it keeps happening, tell the technical team.';
  return 'The request was refused.';
}

/** What a checked file is, in one sentence; a `null` match means the lookup answered 404. */
export function documentCheckText(match: InvoiceDocumentMatch | null): string {
  if (!match) return 'Not a document we issued. No invoice PDF has these exact bytes (a re-saved, edited or scanned copy is not the same file).';
  return `Issued by us: ${invoiceKindText(match.kind)} ${match.number}, ${invoiceLocaleText(match.locale)} version.`;
}

// ---------- check a file ----------

/** SHA-256 of the bytes as lowercase hex (Web Crypto, also in Node). The file stays with the caller; only this hash is sent. */
export async function sha256Hex(bytes: ArrayBuffer): Promise<string> {
  const digest = await globalThis.crypto.subtle.digest('SHA-256', bytes);
  return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, '0')).join('');
}
export const isSha256Hex = (s: string): boolean => /^[0-9a-f]{64}$/i.test(s);
