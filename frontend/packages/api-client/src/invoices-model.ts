/**
 * Invoices a buyer may read (E6.8a read APIs, E6.4b PDF). Pure shapes and helpers with no runtime imports of the client, so Node's type
 * stripping can test them. Hand-written from `InvoiceReadContracts.cs` (the OpenAPI document has no response bodies for these routes).
 * Amounts are whole poisha. `issuedAtUtc` is a UTC instant: show it as a Dhaka day. The buyer BIN arrives already masked (last 4 digits);
 * it is shown as sent. Staff calls live in their own `invoices-staff*` modules, not here.
 */
export type InvoiceKind = 'tax_invoice' | 'credit_note';
export type InvoiceMode = 'live' | 'specimen';

export interface InvoiceSummary {
  id: string;
  number: string;
  kind: InvoiceKind | string;
  mode: InvoiceMode | string;
  issuedAtUtc: string;
  orderId: string;
  orderNumber: string;
  originalInvoiceId: string | null;
  originalNumber: string | null;
  currency: string;
  netMinor: number;
  sdMinor: number;
  vatMinor: number;
  totalMinor: number;
}

export interface InvoicePage { items: InvoiceSummary[]; page: number; pageSize: number; totalCount: number }

export interface InvoiceLine {
  lineNo: number;
  kind: 'goods' | 'delivery' | string;
  sku: string | null;
  partNumber: string | null;
  descriptionEn: string;
  descriptionBn: string | null;
  unit: string;
  quantity: number;
  unitPriceMinor: number;
  netMinor: number;
  vatRate: number;
  vatMinor: number;
  totalMinor: number;
}

export interface BuyerInvoiceDetail {
  invoice: InvoiceSummary;
  seller: { legalNameEn: string; legalNameBn: string | null; bin: string | null; tin: string | null };
  /** `bin` is MASKED by the API ("*********0123"); show as sent. */
  buyer: { name: string; bin: string | null; address: string | null };
  delivery: { courierName: string | null; trackingNumber: string | null } | null;
  purchaseOrderNumber: string | null;
  quoteNumber: string | null;
  lines: InvoiceLine[];
}

const ISO_DAY = /^\d{4}-\d{2}-\d{2}$/;
/** A real calendar day, yyyy-MM-dd (a Dhaka day; never run through the browser's zone). */
export function isInvoiceDay(value: string): boolean {
  if (!ISO_DAY.test(value)) return false;
  const d = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(d.getTime()) && d.toISOString().slice(0, 10) === value;
}

export const INVOICE_PAGE_SIZE = 20;
export type InvoiceLocale = 'en' | 'bn';

const KINDS: readonly string[] = ['tax_invoice', 'credit_note'];
const MODES: readonly string[] = ['live', 'specimen'];

/** Message key under `Invoices.kind`; unknown wire text gets the generic label, never printed raw. */
export function invoiceKindKey(kind: string | null | undefined): string {
  return KINDS.includes(kind ?? '') ? (kind as string) : 'other';
}
export function invoiceModeKey(mode: string | null | undefined): string {
  return MODES.includes(mode ?? '') ? (mode as string) : 'other';
}
/** A specimen is not a tax invoice: the screen says so in words next to the number. */
export function isSpecimenInvoice(i: Pick<InvoiceSummary, 'mode'>): boolean {
  return i.mode === 'specimen';
}

export type InvoiceRefusal = 'notFound' | 'rateLimited' | 'pdfUnavailable' | 'unavailable';

/**
 * What a failed invoice read or download means. 404 is one body for "not yours" and "does not exist"; 429 is the per-minute limit; a 503
 * with code `INVOICE_PDF_UNAVAILABLE` means the stored file did not verify (staff were told); anything else is a retryable outage.
 */
export function classifyInvoiceError(status: number | null | undefined, code?: string | null): InvoiceRefusal {
  if (status === 404) return 'notFound';
  if (status === 429) return 'rateLimited';
  if (status === 503 && code === 'INVOICE_PDF_UNAVAILABLE') return 'pdfUnavailable';
  return 'unavailable';
}

export function invoicePdfPath(orderId: string, invoiceId: string, locale: InvoiceLocale): string {
  const l = locale === 'bn' ? 'bn' : 'en';
  return `/api/v1/orders/${encodeURIComponent(orderId)}/invoices/${encodeURIComponent(invoiceId)}/pdf?locale=${l}`;
}

/** File name for a saved PDF: the number with anything unsafe replaced, plus the language. */
export function invoicePdfFileName(number: string, locale: InvoiceLocale): string {
  const safe = number.replace(/[^A-Za-z0-9._-]+/g, '-').replace(/^[-.]+|[-.]+$/g, '') || 'invoice';
  return `${safe}-${locale === 'bn' ? 'bn' : 'en'}.pdf`;
}

export type InvoiceFilterProblem = 'fromFormat' | 'toFormat' | 'order';

/** Empty means no bound. The server checks again (bad day = 400). */
export function invoiceFilterProblem(from: string, to: string): InvoiceFilterProblem | null {
  const f = from.trim();
  const t = to.trim();
  if (f && !isInvoiceDay(f)) return 'fromFormat';
  if (t && !isInvoiceDay(t)) return 'toFormat';
  if (f && t && f > t) return 'order';
  return null;
}

/** The company list: Dhaka days only when valid, page >= 1, page size 1..50 (the API caps at 50). */
export function invoiceListQuery(q: { from?: string; to?: string; page?: number; pageSize?: number }): string {
  const p = new URLSearchParams();
  if (q.from && isInvoiceDay(q.from)) p.set('from', q.from);
  if (q.to && isInvoiceDay(q.to)) p.set('to', q.to);
  p.set('page', String(Number.isFinite(q.page) && (q.page as number) >= 1 ? Math.floor(q.page as number) : 1));
  const size = Number.isFinite(q.pageSize) ? Math.floor(q.pageSize as number) : INVOICE_PAGE_SIZE;
  p.set('pageSize', String(Math.min(50, Math.max(1, size))));
  return p.toString();
}

/** Reads the page filters out of a URL's query: junk is dropped, never thrown at. */
export function parseInvoiceUrl(get: (key: string) => string | null): { from: string; to: string; page: number } {
  const day = (v: string | null) => (v && isInvoiceDay(v) ? v : '');
  const raw = Number(get('page'));
  const from = day(get('from'));
  const to = day(get('to'));
  return { from, to: from && to && from > to ? '' : to, page: Number.isInteger(raw) && raw >= 1 ? raw : 1 };
}

/** The query string for the page URL (no leading "?"); empty values and page 1 are left out. */
export function invoiceUrlQuery(f: { from: string; to: string; page: number }): string {
  const p = new URLSearchParams();
  if (f.from) p.set('from', f.from);
  if (f.to) p.set('to', f.to);
  if (f.page > 1) p.set('page', String(f.page));
  return p.toString();
}

export function invoicePageCount(totalCount: number, pageSize: number): number {
  return Math.max(1, Math.ceil(Math.max(0, totalCount) / Math.max(1, pageSize)));
}

/** The company route (E6.4d): any effective company Admin/Buyer gets the PDF, even of an order a colleague placed. */
export function companyInvoicePdfPath(companyId: string, invoiceId: string, locale: InvoiceLocale): string {
  return `/api/v1/companies/${encodeURIComponent(companyId)}/invoices/${encodeURIComponent(invoiceId)}/pdf?locale=${locale === 'bn' ? 'bn' : 'en'}`;
}

/** Delivery challan routes (E6.4d): the buyer's, and the staff one (`orders.fulfil` or `orders.view_all`). */
export function challanPdfPath(orderId: string, locale: InvoiceLocale): string {
  return `/api/v1/orders/${encodeURIComponent(orderId)}/challan?locale=${locale === 'bn' ? 'bn' : 'en'}`;
}
export function staffChallanPdfPath(orderId: string, locale: InvoiceLocale): string {
  return `/api/v1/admin/orders/${encodeURIComponent(orderId)}/challan?locale=${locale === 'bn' ? 'bn' : 'en'}`;
}
export function challanFileName(orderNumber: string, locale: InvoiceLocale): string {
  const safe = orderNumber.replace(/[^A-Za-z0-9._-]+/g, '-').replace(/^[-.]+|[-.]+$/g, '') || 'order';
  return `challan-${safe}-${locale === 'bn' ? 'bn' : 'en'}.pdf`;
}

/** A challan exists once a shipment does (any shipment row, so Delivered and later stay printable). */
export function hasChallan(order: { shipments?: readonly unknown[] | null }): boolean {
  return (order.shipments?.length ?? 0) > 0;
}

export type ChallanRefusal = InvoiceRefusal | 'notShipped';

/** 409 `CHALLAN_NOT_AVAILABLE` = no shipment yet; the rest is as for invoices (404 is one body for "not yours"). */
export function classifyChallanError(status: number | null | undefined, code?: string | null): ChallanRefusal {
  if (status === 409 && code === 'CHALLAN_NOT_AVAILABLE') return 'notShipped';
  return classifyInvoiceError(status, code);
}

/** Staff can print a challan with `orders.fulfil` or `orders.view_all` (checked in the action, not as AND attributes). */
export function canPrintChallan(permissions: readonly string[]): boolean {
  return permissions.some((p) => { const k = p.toLowerCase(); return k === 'orders.fulfil' || k === 'orders.view_all'; });
}

/** Console words for a failed challan download. */
export function staffChallanErrorText(e: { status: number | null; code?: string | null }): string {
  if (e.status === 409 && e.code === 'CHALLAN_NOT_AVAILABLE') return 'There is no challan yet: the order has not been shipped.';
  if (e.status === 403) return 'You need orders.fulfil or orders.view_all to print a challan.';
  if (e.status === 404) return 'This order was not found.';
  if (e.status === 429) return 'Too many downloads. Wait a minute and try again.';
  if (e.status === null) return 'The server cannot be reached. Check your connection and try again.';
  if (e.status === 401) return 'Your session ended. Sign in again.';
  return 'The challan could not be made. Try again in a moment.';
}
