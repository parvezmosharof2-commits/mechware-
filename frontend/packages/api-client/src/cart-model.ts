// Cart view types and pure helpers. The server is the only authority for prices, stock and quantities: everything
// here is for display, mapping and UI hints (never to compute what the buyer is charged).

export const MAX_CART_QUANTITY = 10_000;

export interface CartLine {
  variantId: string;
  sku: string;
  title: string;
  quantity: number;
  unitPriceMinor: number;
  lineTotalMinor: number;
  minOrderQty: number;
  packSize: number;
}

export type CartNoticeKind =
  | 'priceChanged'
  | 'stockReduced'
  | 'outOfStock'
  | 'unavailable'
  | 'belowMinimumOrder'
  | 'quantityAdjustedToPack'
  | 'quantityCapped'
  | 'cartFull';

export interface CartNotice {
  kind: CartNoticeKind | (string & {});
  variantId: string;
  was: number;
  now: number;
}

export interface CartView {
  lines: CartLine[];
  notices: CartNotice[];
  lineCount: number;
  totalQuantity: number;
  subtotalMinor: number;
  currency: string;
  etag: string;
}

/** `{ data: CartView }` as sent by every cart endpoint. */
export interface CartEnvelope {
  data: CartView;
}

/** The merge result carried by sign-in and refresh responses (`cartMerged`); null or absent when nothing was merged. */
export interface CartMergeResult {
  lineCount: number;
  notices: CartNotice[];
}

const KNOWN_KINDS: ReadonlySet<string> = new Set<CartNoticeKind>([
  'priceChanged',
  'stockReduced',
  'outOfStock',
  'unavailable',
  'belowMinimumOrder',
  'quantityAdjustedToPack',
  'quantityCapped',
  'cartFull'
]);

/** Message key (under the `Cart` translations) for a notice; an unknown kind from a newer server gets a generic one. */
export function cartNoticeMessageKey(kind: string): string {
  return KNOWN_KINDS.has(kind) ? `notice_${kind}` : 'notice_unknown';
}

/** Notices that stop checkout for their line: the line cannot be bought as it is. */
export function isBlockingNotice(kind: string): boolean {
  return kind === 'outOfStock' || kind === 'unavailable';
}

/** Translation key and values for a notice. Price notices carry money in minor units; the others carry quantities. */
export function noticeMessage(notice: CartNotice, formatPrice: (minor: number) => string): { key: string; values: { was: string | number; now: string | number } } {
  const key = cartNoticeMessageKey(notice.kind);
  if (notice.kind === 'priceChanged') {
    return { key, values: { was: formatPrice(notice.was), now: formatPrice(notice.now) } };
  }
  return { key, values: { was: notice.was, now: notice.now } };
}

export function noticeTone(kind: string): 'blocking' | 'warning' {
  return isBlockingNotice(kind) ? 'blocking' : 'warning';
}

/** Variant ids that cannot be bought right now (out of stock or no longer available). */
export function blockedVariantIds(cart: Pick<CartView, 'notices'>): Set<string> {
  return new Set(cart.notices.filter((n) => isBlockingNotice(n.kind)).map((n) => n.variantId));
}

/** True when the cart has lines and none is blocked. The server re-checks at checkout; this only gates the button. */
export function canProceedToCheckout(cart: Pick<CartView, 'lines' | 'notices'>): boolean {
  return cart.lines.length > 0 && blockedVariantIds(cart).size === 0;
}

/** Notices grouped by variant; notices without a matching line (e.g. cartFull for a left-out line) go under `orphans`. */
export function groupNotices(cart: Pick<CartView, 'lines' | 'notices'>): { byVariant: Map<string, CartNotice[]>; orphans: CartNotice[] } {
  const ids = new Set(cart.lines.map((l) => l.variantId));
  const byVariant = new Map<string, CartNotice[]>();
  const orphans: CartNotice[] = [];
  for (const n of cart.notices) {
    if (!ids.has(n.variantId)) {
      orphans.push(n);
      continue;
    }
    const list = byVariant.get(n.variantId) ?? [];
    list.push(n);
    byVariant.set(n.variantId, list);
  }
  return { byVariant, orphans };
}

/** Consistency checks on what the server sent (a safety net for display, never a source of prices). */
export function lineTotalIsConsistent(line: Pick<CartLine, 'quantity' | 'unitPriceMinor' | 'lineTotalMinor'>): boolean {
  return line.quantity * line.unitPriceMinor === line.lineTotalMinor;
}

export function subtotalIsConsistent(cart: Pick<CartView, 'lines' | 'subtotalMinor'>): boolean {
  return cart.lines.reduce((sum, l) => sum + l.lineTotalMinor, 0) === cart.subtotalMinor;
}

/** Smallest valid quantity for a line: at least MOQ, rounded up to a pack multiple. */
export function minCartQuantity(line: Pick<CartLine, 'minOrderQty' | 'packSize'>): number {
  const pack = Math.max(1, line.packSize);
  return Math.max(pack, Math.ceil(Math.max(1, line.minOrderQty) / pack) * pack);
}

/** UI hint only: snaps typed input to MOQ, pack size and the 10,000 cap. The server validates and may still refuse. */
export function clampCartQuantity(raw: number, line: Pick<CartLine, 'minOrderQty' | 'packSize'>): number {
  const pack = Math.max(1, line.packSize);
  const min = minCartQuantity(line);
  if (!Number.isFinite(raw)) return min;
  const snapped = Math.max(min, Math.ceil(Math.trunc(raw) / pack) * pack);
  const maxMultiple = Math.floor(MAX_CART_QUANTITY / pack) * pack;
  return Math.min(snapped, Math.max(min, maxMultiple));
}

/** Next quantity for a stepper press (one pack up or down); null when going down would pass the minimum (use remove). */
export function stepCartQuantity(current: number, direction: 1 | -1, line: Pick<CartLine, 'minOrderQty' | 'packSize'>): number | null {
  const pack = Math.max(1, line.packSize);
  const next = current + direction * pack;
  if (direction === -1 && next < minCartQuantity(line)) return null;
  return clampCartQuantity(next, line);
}

export type CartErrorKind = 'unavailable' | 'busy' | 'notFound' | 'insufficientStock' | 'cartFull' | 'rateLimited' | 'invalid' | 'forbidden' | 'other';

/**
 * Buyer-facing class of a failed cart call. A call that never got an answer (network, CORS) or a 5xx counts as
 * `unavailable`: the UI must say "try again", never show an empty cart.
 */
export function classifyCartError(error: unknown): CartErrorKind {
  const e = (typeof error === 'object' && error !== null ? error : {}) as { status?: number; error?: { code?: string } };
  const status = typeof e.status === 'number' ? e.status : null;
  const code = e.error?.code ?? '';
  if (status === null || status >= 500 || code === 'CART_UNAVAILABLE') return 'unavailable';
  if (code === 'CART_BUSY' || code === 'CART_CHANGED') return 'busy';
  if (code === 'INSUFFICIENT_STOCK') return 'insufficientStock';
  if (code === 'CART_FULL') return 'cartFull';
  if (status === 404) return 'notFound';
  if (status === 429) return 'rateLimited';
  if (status === 403) return 'forbidden';
  if (status === 400) return 'invalid';
  if (status === 409) return 'busy';
  return 'other';
}

/** Whether re-reading the cart is the right reaction (someone else changed it, or what we asked for is gone). */
export function shouldRefetchAfter(kind: CartErrorKind): boolean {
  return kind === 'busy' || kind === 'insufficientStock' || kind === 'notFound' || kind === 'cartFull';
}
