// Buyer quotes: list, detail, accept and reject (D4.10 endpoints, D4.14 screens). Types hand-written from `BuyerQuoteContracts.cs`
// (the OpenAPI document has no response bodies for these routes). No runtime imports, so Node's type stripping can test it.
// The server is the only authority: it prices, decides `acceptable`, and refuses. Nothing here computes what the buyer pays.

export type BuyerQuoteStatus = 'sent' | 'expired' | 'accepted' | 'rejected' | 'withdrawn' | 'superseded' | (string & {});

export interface BuyerQuoteSummary {
  id: string;
  number: string;
  rfqId: string | null;
  versionNo: number;
  status: BuyerQuoteStatus;
  sentAt: string;
  validUntil: string;
  lineCount: number;
  goodsTotalMinor: number;
  /** Null while delivery is worked out at the order (calculated delivery): then the goods total is all the quote states. */
  grandTotalMinor: number | null;
  paymentTerms: 'as_account' | 'advance' | (string & {});
  shippingMode: 'calculated' | 'fixed' | (string & {});
  contactIsMe: boolean;
  closedAt: string | null;
}

export interface BuyerQuoteLine {
  id: string;
  lineNo: number;
  variantId: string;
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
  lineNote: string | null;
}

export interface BuyerQuoteShipTo {
  siteName: string;
  recipientName: string;
  phone: string;
  line1: string;
  line2: string | null;
  upazila: string;
  district: string;
  division: string;
  postalCode: string | null;
}

export interface BuyerQuoteVersion {
  versionNo: number;
  status: BuyerQuoteStatus;
  acceptable: boolean;
  sentAt: string;
  validUntil: string;
  validityDays: number;
  paymentTerms: string;
  shippingMode: string;
  shipToSiteId: string | null;
  shipTo: BuyerQuoteShipTo | null;
  shippingFees: { sellerId: string; shippingMinor: number }[];
  termsText: string | null;
  buyerNote: string | null;
  company: { name: string; bin: string | null; billingAddress: string | null };
  preparerName: string | null;
  contentHash: string;
  goodsNetMinor: number;
  taxMinor: number;
  goodsTotalMinor: number;
  shippingTotalMinor: number | null;
  grandTotalMinor: number | null;
  acceptedOrderId: string | null;
  acceptedAt: string | null;
  rejectedAt: string | null;
  rejectReason: string | null;
  withdrawnAt: string | null;
  supersededAt: string | null;
  lines: BuyerQuoteLine[];
}

export interface BuyerQuoteDetail {
  quote: BuyerQuoteSummary;
  versions: BuyerQuoteVersion[];
}

export interface BuyerQuotePage {
  items: BuyerQuoteSummary[];
  page: number;
  pageSize: number;
  total: number;
}

export interface AcceptPreviewLine {
  quoteLineId: string;
  variantId: string;
  sku: string;
  title: string;
  quantity: number;
  unitPriceMinor: number;
  vatRate: number;
  netMinor: number;
  taxMinor: number;
  totalMinor: number;
  vatRateChanged: boolean;
  lowerPriceNow: boolean;
  stockShort: boolean;
}

export interface AcceptPreview {
  quoteId: string;
  versionNo: number;
  lines: AcceptPreviewLine[];
  subtotalMinor: number;
  taxMinor: number;
  shippingMinor: number;
  grandTotalMinor: number;
  vatRateChanged: boolean;
  lowerPriceNow: boolean;
  stockShort: boolean;
}

/** What the page sends to preview and to accept. Lines, prices and the company are never in it. */
export interface QuoteChoice {
  versionNo: number;
  contentHash: string;
  payOnCredit: boolean;
  shipToSiteId?: string | null;
  costCentreId?: string | null;
  purchaseOrderNumber?: string | null;
}

export function previewBody(c: QuoteChoice): Record<string, unknown> {
  const body: Record<string, unknown> = { versionNo: c.versionNo, contentHash: c.contentHash, payOnCredit: c.payOnCredit };
  if (c.shipToSiteId) body.shipToSiteId = c.shipToSiteId;
  if (c.costCentreId) body.costCentreId = c.costCentreId;
  return body;
}

/** The accept body is the preview body plus the previewed total (the server only ever refuses when its own total is higher) and the PO number. */
export function acceptBody(c: QuoteChoice, previewedGrandTotalMinor: number): Record<string, unknown> {
  const body = previewBody(c);
  body.expectedGrandTotalMinor = previewedGrandTotalMinor;
  const po = c.purchaseOrderNumber?.trim();
  if (po) body.purchaseOrderNumber = po;
  return body;
}

/** A stable text of what is being accepted: the same choice and total reuse one Idempotency-Key; any change gets a new one. */
export function acceptFingerprint(companyId: string, quoteId: string, c: QuoteChoice, total: number): string {
  return JSON.stringify({ companyId: companyId.toLowerCase(), quoteId: quoteId.toLowerCase(), body: acceptBody(c, total) });
}

export function rejectBody(versionNo: number, reason: string): Record<string, unknown> {
  const text = reason.trim();
  return text ? { versionNo, reason: text } : { versionNo };
}

export const QUOTE_REJECT_MAX = 1000;

// ---------- statuses and who may do what ----------

export type QuoteStatusKey = 'sent' | 'expired' | 'accepted' | 'rejected' | 'withdrawn' | 'superseded' | 'other';
export type QuoteTone = 'info' | 'ok' | 'muted' | 'warn' | 'neutral';

export function quoteStatusKey(status: string): QuoteStatusKey {
  const s = status.toLowerCase();
  return s === 'sent' || s === 'expired' || s === 'accepted' || s === 'rejected' || s === 'withdrawn' || s === 'superseded' ? s : 'other';
}

export function quoteStatusTone(status: string): QuoteTone {
  switch (quoteStatusKey(status)) {
    case 'sent': return 'info';
    case 'accepted': return 'ok';
    case 'expired': return 'warn';
    case 'rejected':
    case 'withdrawn':
    case 'superseded': return 'muted';
    default: return 'neutral';
  }
}

/** The filters offered on the list (the server's `status` values). */
export const QUOTE_FILTERS = ['', 'sent', 'expired', 'accepted', 'rejected', 'withdrawn', 'superseded'] as const;

/** The Buyer role bit. Only the server decides; the page hides the accept button from everyone else so it is not offered in vain. */
export const ROLE_BUYER = 2;
export const ROLE_ADMIN = 8;

export function mayAccept(roles: number): boolean {
  return (roles & ROLE_BUYER) !== 0;
}

/** The version a buyer can act on: the newest one, and only when the server says it is acceptable. */
export function newestAcceptable(detail: Pick<BuyerQuoteDetail, 'versions'>): BuyerQuoteVersion | null {
  const newest = detail.versions[detail.versions.length - 1];
  return newest && newest.acceptable ? newest : null;
}

/** The version to show first: the newest. */
export function newestVersion(detail: Pick<BuyerQuoteDetail, 'versions'>): BuyerQuoteVersion | null {
  return detail.versions[detail.versions.length - 1] ?? null;
}

/** Reject is offered while the newest version is still sent (an expired one may still be rejected by the domain). */
export function canRejectVersion(v: Pick<BuyerQuoteVersion, 'status'>): boolean {
  const k = quoteStatusKey(v.status);
  return k === 'sent' || k === 'expired';
}

/** Payment choice: an "advance" quote can only be paid in advance; an "as account" one may go on the company's credit. */
export function quotePaymentChoices(paymentTerms: string): { onAccount: boolean; advance: boolean } {
  return paymentTerms === 'advance' ? { onAccount: false, advance: true } : { onAccount: true, advance: true };
}

/** What a quote states as its total: the grand total, or the goods total (incl. VAT) while delivery is worked out at the order. */
export function quotedTotal(q: { grandTotalMinor: number | null; goodsTotalMinor: number }): { minor: number; withDelivery: boolean } {
  return q.grandTotalMinor === null ? { minor: q.goodsTotalMinor, withDelivery: false } : { minor: q.grandTotalMinor, withDelivery: true };
}

export function lineTitle(l: Pick<BuyerQuoteLine, 'titleEn' | 'titleBn'>, locale: 'en' | 'bn'): string {
  return locale === 'bn' && l.titleBn ? l.titleBn : l.titleEn;
}

export function quotePageQuery(q: { status?: string; page?: number; pageSize?: number }): string {
  const p = new URLSearchParams();
  if (q.status) p.set('status', q.status);
  p.set('page', String(Math.max(1, Math.floor(q.page ?? 1))));
  p.set('pageSize', String(Math.min(50, Math.max(1, Math.floor(q.pageSize ?? 20)))));
  return p.toString();
}

export function quotePageCount(total: number, pageSize: number): number {
  return pageSize > 0 ? Math.max(1, Math.ceil(total / pageSize)) : 1;
}

/** Where the PDF of a sent version is served (ADR-0011 §13, D4.11): the buyer route under the company, verified on every download. */
export function quotePdfPath(companyId: string, quoteId: string, versionNo: number, locale: 'en' | 'bn' = 'en'): string {
  return `/api/v1/companies/${encodeURIComponent(companyId)}/quotes/${encodeURIComponent(quoteId)}/versions/${Math.floor(versionNo)}/pdf?locale=${locale === 'bn' ? 'bn' : 'en'}`;
}

export type QuotePdfErrorKind = 'notFound' | 'rateLimited' | 'unverified' | 'signIn' | 'failed';

/** The PDF route's refusals in words: 404 (one answer for anything not the buyer's), 429 (too many downloads), 503 QUOTE_PDF_UNAVAILABLE (hash check failed, staff told). */
export function classifyQuotePdfError(error: unknown): QuotePdfErrorKind {
  const e = (typeof error === 'object' && error !== null ? error : {}) as { status?: unknown; error?: { code?: unknown } };
  const status = typeof e.status === 'number' ? e.status : null;
  if (e.error?.code === 'QUOTE_PDF_UNAVAILABLE') return 'unverified';
  if (status === 404) return 'notFound';
  if (status === 429) return 'rateLimited';
  if (status === 401) return 'signIn';
  return 'failed';
}

// ---------- errors ----------

export type QuoteErrorKind =
  | 'priceChanged'
  | 'notAcceptable'
  | 'alreadyAccepted'
  | 'changed'
  | 'requiresAdvance'
  | 'deliveryFixed'
  | 'deliverySiteGone'
  | 'variantUnavailable'
  | 'insufficientStock'
  | 'creditHold'
  | 'creditLimit'
  | 'noCreditTerms'
  | 'companySuspended'
  | 'tooManyUnpaid'
  | 'spendLimit'
  | 'budget'
  | 'costCentre'
  | 'roleRequired'
  | 'keyReused'
  | 'busy'
  | 'closed'
  | 'notFound'
  | 'signIn'
  | 'rateLimited'
  | 'invalid'
  | 'unavailable';

const CODE_KINDS: Record<string, QuoteErrorKind> = {
  PRICE_CHANGED: 'priceChanged',
  QUOTE_NOT_ACCEPTABLE: 'notAcceptable',
  QUOTE_ALREADY_ACCEPTED: 'alreadyAccepted',
  QUOTE_CHANGED: 'changed',
  QUOTE_REQUIRES_ADVANCE_PAYMENT: 'requiresAdvance',
  QUOTE_DELIVERY_FIXED: 'deliveryFixed',
  QUOTE_DELIVERY_SITE_GONE: 'deliverySiteGone',
  VARIANT_UNAVAILABLE: 'variantUnavailable',
  INSUFFICIENT_STOCK: 'insufficientStock',
  CREDIT_HOLD_OVERDUE: 'creditHold',
  CREDIT_LIMIT_EXCEEDED: 'creditLimit',
  NO_CREDIT_TERMS: 'noCreditTerms',
  COMPANY_SUSPENDED: 'companySuspended',
  TOO_MANY_UNPAID_ORDERS: 'tooManyUnpaid',
  SPEND_LIMIT_EXCEEDED: 'spendLimit',
  BUDGET_EXCEEDED: 'budget',
  COST_CENTRE_NOT_ASSIGNED: 'costCentre',
  COMPANY_ROLE_REQUIRED: 'roleRequired',
  IDEMPOTENCY_KEY_REUSED: 'keyReused',
  CHECKOUT_BUSY: 'busy',
  IDEMPOTENCY_RACE: 'busy',
  QUOTE_INVALID_TRANSITION: 'closed',
  QUOTE_CLOSED: 'closed'
};

/** One kind per documented code; anything else falls back on the HTTP status. A 5xx or no answer is `unavailable`: nothing was decided. */
export function classifyQuoteError(error: unknown): QuoteErrorKind {
  const e = (typeof error === 'object' && error !== null ? error : {}) as { status?: unknown; error?: { code?: unknown } };
  const status = typeof e.status === 'number' ? e.status : null;
  const code = typeof e.error?.code === 'string' ? e.error.code : '';
  if (status === null || status >= 500) return 'unavailable';
  if (status === 401) return 'signIn';
  const byCode = CODE_KINDS[code];
  if (byCode) return byCode;
  if (status === 404) return 'notFound';
  if (status === 403) return 'roleRequired';
  if (status === 429) return 'rateLimited';
  return 'invalid';
}

/** The same Idempotency-Key may be reused only while the outcome is unknown or nothing was decided. */
export function quoteKeepKeyAfter(kind: QuoteErrorKind): boolean {
  return kind === 'unavailable' || kind === 'busy' || kind === 'rateLimited' || kind === 'signIn';
}

/** Refusals after which the page must read the quote again: what it showed is out of date. */
export function quoteNeedsReload(kind: QuoteErrorKind): boolean {
  return kind === 'priceChanged' || kind === 'notAcceptable' || kind === 'alreadyAccepted' || kind === 'changed' || kind === 'closed' || kind === 'variantUnavailable';
}
