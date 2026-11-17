/**
 * A company's own credit position, buyer side (B3.9 endpoints, B3.13 screens). Pure shapes and helpers, no runtime imports, so Node's
 * type stripping can test them. Amounts are whole poisha; dates are `yyyy-MM-dd` (Dhaka days) and are never run through the browser's
 * time zone. Types are hand-written from `CreditBuyerContracts.cs` (the OpenAPI document has no response bodies for these routes).
 *
 * Who sees what: only a company Admin or Buyer gets an answer. A Requester or Approver gets the same 404 as a stranger, and null credit
 * fields on `CompanyDto` / `OrderDto.credit`. The screens must show no balance and no scare for that.
 */

export interface BuyerPage<T> { items: T[]; page: number; pageSize: number; totalCount: number }

export interface BuyerAging { current: number; days1To30: number; days31To60: number; days61To90: number; days90Plus: number }

export interface BuyerPendingPayment { receivedOn: string; method: string; amountMinor: number; referenceTail: string }

export interface BuyerCredit {
  companyId: string;
  currency: string;
  asOf: string;
  terms: string;
  limitMinor: number;
  usedMinor: number;
  availableMinor: number;
  onAccountMinor: number;
  overdueMinor: number;
  oldestDueOn: string | null;
  onHold: boolean;
  aging: BuyerAging;
  pendingPayments: BuyerPendingPayment[];
}

export type OpenItemStatus = 'open' | 'partially_paid' | 'overdue';

export interface BuyerOpenItem {
  orderNumber: string;
  chargeMinor: number;
  receivableMinor: number;
  dueOn: string;
  daysPastDue: number;
  status: OpenItemStatus | string;
}

export interface BuyerStatementLine {
  seq: number;
  occurredOn: string;
  kind: string;
  orderNumber: string | null;
  paymentMethod: string | null;
  referenceTail: string | null;
  receivableDeltaMinor: number;
  onAccountDeltaMinor: number;
  receivableAfterMinor: number;
  onAccountAfterMinor: number;
  dueOn: string | null;
}

export interface BuyerStatement {
  companyId: string;
  from: string | null;
  to: string | null;
  currentReceivableMinor: number;
  currentOnAccountMinor: number;
  lines: BuyerPage<BuyerStatementLine>;
}

/** `OrderDto.credit`: only on a charged credit order, only for someone who may see the company's credit, else null/absent. */
export type OrderCreditStatus = 'open' | 'partially_paid' | 'paid' | 'overdue' | 'cancelled';
export interface OrderCredit { status: OrderCreditStatus | string; dueOn: string; outstandingMinor: number }

export const CREDIT_PANEL_PAGE_SIZE = 10;
export const CREDIT_STATEMENT_PAGE_SIZE = 20;

// ---------- who sees what ----------

/** True when the company data carries a real balance. Null fields (a Requester/Approver, or no terms) mean: show nothing numeric. */
export function hasCreditBalance<T extends { creditTerms?: string; creditLimit?: unknown; creditUsed?: unknown; creditAvailable?: unknown }>(
  c: T | null | undefined
): c is T & { creditLimit: NonNullable<T['creditLimit']>; creditUsed: NonNullable<T['creditUsed']>; creditAvailable: NonNullable<T['creditAvailable']> } {
  return !!c && c.creditTerms !== 'none' && !!c.creditLimit && !!c.creditUsed && !!c.creditAvailable;
}

export type CreditLoadKind = 'ok' | 'hidden' | 'notFound' | 'unavailable';

/**
 * What a failed `/credit` read means. The API answers 404 for a stranger AND for a member whose role may not see the balance (one body,
 * by design). A person who is a member of the company is therefore shown the neutral "managed by your company admin" message; everyone
 * else gets the not-found page. Network errors, 429 and 5xx are retryable. Never a scare for a member.
 */
export function classifyCreditError(status: number | null | undefined, isMember: boolean): CreditLoadKind {
  if (status === 404) return isMember ? 'hidden' : 'notFound';
  if (status === 401 || status === 403) return isMember ? 'hidden' : 'notFound';
  return 'unavailable';
}

// ---------- amounts and days ----------

/** Share of the limit in use, 0-100 (whole percent). Zero without a limit. */
export function creditUsedPercent(limitMinor: number, usedMinor: number): number {
  if (!(limitMinor > 0) || !(usedMinor > 0)) return 0;
  return Math.min(100, Math.max(0, Math.round((usedMinor / limitMinor) * 100)));
}

export type CreditUsageTone = 'accent' | 'warning' | 'fault';
export function creditUsageTone(percent: number): CreditUsageTone {
  return percent >= 90 ? 'fault' : percent >= 80 ? 'warning' : 'accent';
}

const ISO_DAY = /^\d{4}-\d{2}-\d{2}$/;

export function isCreditDay(value: string): boolean {
  if (!ISO_DAY.test(value)) return false;
  const d = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(d.getTime()) && d.toISOString().slice(0, 10) === value;
}

/** "17 Nov 2026" / "১৭ নভেম্বর ২০২৬" for a Dhaka day. Read as UTC midnight and shown in UTC, so no zone can move the date. Empty for junk. */
export function formatCreditDay(day: string | null | undefined, locale: string): string {
  if (!day || !isCreditDay(day)) return '';
  return new Intl.DateTimeFormat(locale === 'bn' ? 'bn-BD' : 'en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' }).format(new Date(`${day}T00:00:00Z`));
}

/** Signed text for a ledger change: "+" for more owed / more on account, a real minus sign otherwise, nothing for zero. */
export function creditSignOf(minor: number): '+' | '-' | '' {
  return minor > 0 ? '+' : minor < 0 ? '-' : '';
}

// ---------- statuses ----------

const OPEN_ITEM_STATUSES: readonly string[] = ['open', 'partially_paid', 'overdue'];
const ORDER_CREDIT_STATUSES: readonly string[] = ['open', 'partially_paid', 'paid', 'overdue', 'cancelled'];

/** Message key under the `CompanyCredit.status` / `Orders.credit` group; anything unknown gets the generic label. */
export function creditStatusKey(status: string | null | undefined, known: 'item' | 'order' = 'order'): string {
  const list = known === 'item' ? OPEN_ITEM_STATUSES : ORDER_CREDIT_STATUSES;
  return list.includes(status ?? '') ? (status as string) : 'other';
}

export type CreditTone = 'neutral' | 'pending' | 'done' | 'problem';
export function creditStatusTone(status: string | null | undefined): CreditTone {
  switch (status) {
    case 'overdue':
    case 'cancelled':
      return 'problem';
    case 'paid':
      return 'done';
    case 'open':
    case 'partially_paid':
      return 'pending';
    default:
      return 'neutral';
  }
}

/** An order's credit line is worth showing at all: a known status. Unknown statuses are hidden rather than guessed at. */
export function orderCreditVisible(credit: OrderCredit | null | undefined): credit is OrderCredit {
  return !!credit && ORDER_CREDIT_STATUSES.includes(credit.status) && isCreditDay(credit.dueOn);
}

/** Still owed on this order: only these statuses show an outstanding amount. */
export function orderCreditOwes(credit: OrderCredit): boolean {
  return credit.status === 'open' || credit.status === 'partially_paid' || credit.status === 'overdue';
}

const KNOWN_KINDS = [
  'charge', 'payment_applied', 'payment_on_account', 'on_account_applied', 'payment_reversed',
  'charge_reduced', 'write_off', 'on_account_correction', 'on_account_payout'
] as const;

/** Message key for a statement line kind; an unknown kind gets the generic label (the screen never prints raw wire text). */
export function buyerStatementKindKey(kind: string | null | undefined): string {
  return (KNOWN_KINDS as readonly string[]).includes(kind ?? '') ? (kind as string) : 'other';
}

const METHODS = ['cheque', 'pay_order', 'beftn', 'rtgs', 'npsb', 'bank_deposit', 'cash', 'bkash', 'nagad'] as const;
export function buyerPaymentMethodKey(method: string | null | undefined): string {
  return (METHODS as readonly string[]).includes(method ?? '') ? (method as string) : 'other';
}

// ---------- statement filter + paging ----------

export type BuyerStatementFilterProblem = 'fromFormat' | 'toFormat' | 'order';

/** Empty values are allowed (no bound). Returns the first problem or null. The server checks again (from after to is a 400). */
export function buyerStatementFilterProblem(from: string, to: string): BuyerStatementFilterProblem | null {
  const f = from.trim();
  const t = to.trim();
  if (f && !isCreditDay(f)) return 'fromFormat';
  if (t && !isCreditDay(t)) return 'toFormat';
  if (f && t && f > t) return 'order';
  return null;
}

export function buyerStatementQuery(q: { from?: string; to?: string; page?: number; pageSize?: number }): string {
  const p = new URLSearchParams();
  if (q.from && isCreditDay(q.from)) p.set('from', q.from);
  if (q.to && isCreditDay(q.to)) p.set('to', q.to);
  p.set('page', String(clampCreditPage(q.page)));
  p.set('pageSize', String(clampCreditPageSize(q.pageSize)));
  return p.toString();
}

export function buyerPageQuery(q: { page?: number; pageSize?: number }): string {
  return `page=${clampCreditPage(q.page)}&pageSize=${clampCreditPageSize(q.pageSize)}`;
}

export function clampCreditPage(page: number | undefined): number {
  return Number.isFinite(page) && (page as number) >= 1 ? Math.floor(page as number) : 1;
}

/** The API takes 1..100. */
export function clampCreditPageSize(size: number | undefined): number {
  const n = Number.isFinite(size) ? Math.floor(size as number) : CREDIT_PANEL_PAGE_SIZE;
  return Math.min(100, Math.max(1, n));
}

export function creditPageCount(totalCount: number, pageSize: number): number {
  return Math.max(1, Math.ceil(Math.max(0, totalCount) / Math.max(1, pageSize)));
}

/** Aging rows in display order; zero buckets are kept so the table is steady. */
export function buyerAgingRows(a: BuyerAging): { key: 'current' | 'days1To30' | 'days31To60' | 'days61To90' | 'days90Plus'; minor: number }[] {
  return [
    { key: 'current', minor: a.current },
    { key: 'days1To30', minor: a.days1To30 },
    { key: 'days31To60', minor: a.days31To60 },
    { key: 'days61To90', minor: a.days61To90 },
    { key: 'days90Plus', minor: a.days90Plus }
  ];
}
