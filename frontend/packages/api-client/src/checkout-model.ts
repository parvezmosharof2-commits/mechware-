// Checkout types and pure helpers (E3.3/E3.4). The server is the only authority for prices, shipping, stock and approval:
// nothing here computes what the buyer pays. Helpers only validate input shape, guard steps and map errors to messages.

export type CheckoutStep = 'delivery' | 'review';

/** `POST /orders/shipping-quote` data. Money is the server's, in minor units. */
export interface ShippingQuote {
  perSeller: { sellerId: string; shippingMinor: number }[];
  totalMinor: number;
  freeShippingApplied: boolean;
}

/** `POST /orders` data. `status` is the wire form of the order status (`pending_payment`, `pending_approval`, ...). */
export interface PlacedOrder {
  orderId: string;
  orderNumber: string;
  status: string;
  grandTotalMinor: number;
  reservationExpiresAtUtc: string;
  replayed: boolean;
}

/** Where the order goes and who it is for. Personal: `addressId`. Company: `companyId` + `siteId` (+ cost centre, PO number). */
export interface CheckoutDestination {
  addressId?: string | null;
  companyId?: string | null;
  siteId?: string | null;
  costCentreId?: string | null;
  poNumber?: string | null;
}

export interface CheckoutLine {
  variantId: string;
  quantity: number;
}

/** What the server accepts as a purchase order number: 1-40 ASCII letters, digits, space and - _ . # / (trimmed). */
export const PO_NUMBER_MAX = 40;
const PO_PATTERN = /^[A-Za-z0-9 \-_.#/]+$/;

export type PoNumberProblem = 'tooLong' | 'badCharacters';

/** Null when valid or blank (blank means "no PO number"). The server stays the authority. */
export function purchaseOrderNumberProblem(raw: string | null | undefined): PoNumberProblem | null {
  const value = (raw ?? '').trim();
  if (value === '') return null;
  if (value.length > PO_NUMBER_MAX) return 'tooLong';
  return PO_PATTERN.test(value) ? null : 'badCharacters';
}

/** The trimmed number to send, or undefined for blank. */
export function normalisePurchaseOrderNumber(raw: string | null | undefined): string | undefined {
  const value = (raw ?? '').trim();
  return value === '' ? undefined : value;
}

const BANGLA_DIGITS = '০১২৩৪৫৬৭৮৯';

/** Bangladeshi mobile (01[3-9]XXXXXXXX; +880 / 880 forms; spaces, dashes and Bangla digits allowed). Mirrors BdMobileNumber on the server. */
export function isBdMobile(raw: string): boolean {
  const input = raw.trim();
  if (input === '' || input.length > 32) return false;
  const latin = input.replace(/[০-৯]/g, (d) => String(BANGLA_DIGITS.indexOf(d)));
  if (/[^0-9+\-().\s]/.test(latin)) return false;
  const digits = latin.replace(/\D/g, '');
  let national = '';
  if (digits.length === 13 && digits.startsWith('880')) national = digits.slice(3);
  else if (digits.length === 11 && digits.startsWith('0') && !latin.startsWith('+')) national = digits.slice(1);
  return /^1[3-9]\d{8}$/.test(national);
}

export function isPersonalOrder(dest: CheckoutDestination): boolean {
  return !dest.companyId;
}

/** True when the destination is chosen well enough to ask for a quote. A cost centre is required only when the company has any. */
export function isDestinationComplete(dest: CheckoutDestination, opts: { costCentreRequired?: boolean } = {}): boolean {
  if (isPersonalOrder(dest)) return !!dest.addressId;
  if (!dest.siteId) return false;
  if (opts.costCentreRequired && !dest.costCentreId) return false;
  return purchaseOrderNumberProblem(dest.poNumber) === null;
}

export function parseCheckoutStep(raw: string | null | undefined): CheckoutStep {
  return raw === 'review' ? 'review' : 'delivery';
}

export type StepDecision = { step: CheckoutStep } | { redirect: 'cart' } | { wait: true };

/** Which step may be shown: an empty cart goes back to the cart; review needs a complete destination and no blocked line. */
export function guardCheckoutStep(
  requested: CheckoutStep,
  cart: { lineCount: number; blocked: boolean } | null,
  dest: CheckoutDestination,
  opts: { costCentreRequired?: boolean } = {}
): StepDecision {
  if (cart === null) return { wait: true };
  if (cart.lineCount === 0) return { redirect: 'cart' };
  if (requested === 'review' && !cart.blocked && isDestinationComplete(dest, opts)) return { step: 'review' };
  return { step: 'delivery' };
}

/** Body of `POST /orders/shipping-quote`: lines and destination, never money. */
export function buildQuoteBody(lines: readonly CheckoutLine[], dest: CheckoutDestination) {
  return {
    lines: lines.map((l) => ({ variantId: l.variantId, quantity: l.quantity })),
    ...(isPersonalOrder(dest) ? { shippingAddressId: dest.addressId } : { companyId: dest.companyId, shipToSiteId: dest.siteId })
  };
}

/** Body of `POST /orders`. A personal order never carries a company, site, cost centre or PO number. */
export function buildOrderBody(lines: readonly CheckoutLine[], dest: CheckoutDestination) {
  const base = buildQuoteBody(lines, dest);
  if (isPersonalOrder(dest)) return base;
  const po = normalisePurchaseOrderNumber(dest.poNumber);
  return { ...base, ...(dest.costCentreId ? { costCentreId: dest.costCentreId } : {}), ...(po ? { purchaseOrderNumber: po } : {}) };
}

// ----- Idempotency key lifecycle ------------------------------------------------------------------------------------

export interface PlaceAttempt {
  key: string;
  /** What the key was issued for. The server refuses the same key with a different request, so a new request needs a new key. */
  fingerprint: string;
}

export function attemptFingerprint(lines: readonly CheckoutLine[], dest: CheckoutDestination): string {
  return JSON.stringify(buildOrderBody([...lines].sort((a, b) => a.variantId.localeCompare(b.variantId)), dest));
}

/** The server wants 8-100 letters, digits, '-' or '_'. */
export function isValidIdempotencyKey(key: string): boolean {
  return /^[A-Za-z0-9_-]{8,100}$/.test(key);
}

/** A fresh key. `random` is injectable for tests; the default is the browser's crypto UUID. */
export function newIdempotencyKey(random: () => string = () => globalThis.crypto.randomUUID()): string {
  return random().replace(/[^A-Za-z0-9_-]/g, '');
}

/** Keeps the key while the request is unchanged (retry, double-click, refresh) and issues a new one when it changed. */
export function attemptFor(previous: PlaceAttempt | null, fingerprint: string, makeKey: () => string = newIdempotencyKey): PlaceAttempt {
  if (previous && previous.fingerprint === fingerprint && isValidIdempotencyKey(previous.key)) return previous;
  return { key: makeKey(), fingerprint };
}

// ----- Errors -------------------------------------------------------------------------------------------------------

export type CheckoutErrorKind =
  | 'signIn'
  | 'notDeliverable'
  | 'quoteOnly'
  | 'weightMissing'
  | 'insufficientStock'
  | 'unavailableItems'
  | 'keyReused'
  | 'busy'
  | 'spendLimit'
  | 'budget'
  | 'costCentre'
  | 'companyNotVerified'
  | 'companyBlocked'
  | 'companyRole'
  | 'noApprover'
  | 'accountNotVerified'
  | 'tooManyUnpaid'
  | 'coupon'
  | 'invalid'
  | 'notFound'
  | 'forbidden'
  | 'rateLimited'
  | 'unavailable'
  | 'other';

const CODE_KINDS: Record<string, CheckoutErrorKind> = {
  SHIPPING_NOT_AVAILABLE: 'notDeliverable',
  SHIPPING_QUOTE_ONLY: 'quoteOnly',
  VARIANT_WEIGHT_MISSING: 'weightMissing',
  INSUFFICIENT_STOCK: 'insufficientStock',
  VARIANT_UNAVAILABLE: 'unavailableItems',
  IDEMPOTENCY_KEY_REUSED: 'keyReused',
  CHECKOUT_BUSY: 'busy',
  IDEMPOTENCY_RACE: 'busy',
  SPEND_LIMIT_EXCEEDED: 'spendLimit',
  BUDGET_EXCEEDED: 'budget',
  COST_CENTRE_NOT_ASSIGNED: 'costCentre',
  COMPANY_NOT_VERIFIED: 'companyNotVerified',
  COMPANY_SUSPENDED: 'companyBlocked',
  COMPANY_REJECTED: 'companyBlocked',
  COMPANY_ROLE_REQUIRED: 'companyRole',
  NO_ELIGIBLE_APPROVER: 'noApprover',
  ACCOUNT_NOT_VERIFIED: 'accountNotVerified',
  TOO_MANY_UNPAID_ORDERS: 'tooManyUnpaid'
};

/** Buyer-facing class of a failed quote or placement. No answer at all (network) and 5xx are `unavailable`: try again, nothing is lost. */
export function classifyCheckoutError(error: unknown): CheckoutErrorKind {
  const e = (typeof error === 'object' && error !== null ? error : {}) as { status?: number; error?: { code?: string } };
  const status = typeof e.status === 'number' ? e.status : null;
  const code = e.error?.code ?? '';
  if (status === null || status >= 500) return 'unavailable';
  if (status === 401) return 'signIn';
  const byCode = CODE_KINDS[code];
  if (byCode) return byCode;
  if (code.startsWith('COUPON_')) return 'coupon';
  if (status === 429) return 'rateLimited';
  if (status === 404) return 'notFound';
  if (status === 403) return 'forbidden';
  if (status === 400 || status === 422) return 'invalid';
  return 'other';
}

/**
 * Whether the same Idempotency-Key may be reused for the next try. True when the outcome is unknown or nothing was
 * decided (no answer, 5xx, busy, rate limit): a replay then returns the first order instead of creating a second one.
 * A definitive refusal gets a new key.
 */
export function keepKeyAfter(kind: CheckoutErrorKind): boolean {
  return kind === 'unavailable' || kind === 'busy' || kind === 'rateLimited';
}

/** Errors that mean the cart the page showed is out of date: re-read it before anything else. */
export function shouldRefreshCartAfter(kind: CheckoutErrorKind): boolean {
  return kind === 'insufficientStock' || kind === 'unavailableItems' || kind === 'keyReused';
}

/** True when the order waits for a company approval, so there is nothing to pay yet. */
export function isAwaitingApproval(status: string): boolean {
  return status === 'pending_approval';
}

interface CartSnapshot {
  etag: string;
  subtotalMinor: number;
  lines: readonly (CheckoutLine & { unitPriceMinor: number })[];
}

/** Has the cart changed since the review was shown (lines, quantities, prices or version)? Checked before placing. */
export function cartChangedSince(shown: CartSnapshot, fresh: CartSnapshot): boolean {
  if (shown.etag !== fresh.etag || shown.subtotalMinor !== fresh.subtotalMinor || shown.lines.length !== fresh.lines.length) return true;
  return shown.lines.some((l, i) => {
    const f = fresh.lines[i];
    return !f || f.variantId !== l.variantId || f.quantity !== l.quantity || f.unitPriceMinor !== l.unitPriceMinor;
  });
}
