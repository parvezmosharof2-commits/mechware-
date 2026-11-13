import type { CheckoutDestination, CheckoutErrorKind, CheckoutStep } from '@mechware/api-client';

/** The checkout URL carries the whole flow state (no storage): step, address, company, site, cost centre, PO number, attempt key. */
export interface CheckoutParams {
  step: CheckoutStep;
  dest: CheckoutDestination;
  /** The Idempotency-Key of the current placement attempt, so a refresh replays the same order instead of creating another. */
  key: string | null;
}

const UUID = /^[0-9a-fA-F-]{36}$/;
const clean = (v: string | null) => (v && UUID.test(v) ? v : null);

export function readCheckoutParams(params: URLSearchParams): CheckoutParams {
  const company = clean(params.get('c'));
  return {
    step: params.get('step') === 'review' ? 'review' : 'delivery',
    dest: {
      addressId: clean(params.get('a')),
      companyId: company,
      siteId: company ? clean(params.get('s')) : null,
      costCentreId: company ? clean(params.get('cc')) : null,
      poNumber: company ? (params.get('po') ?? '').slice(0, 80) : null
    },
    key: params.get('k') && /^[A-Za-z0-9_-]{8,100}$/.test(params.get('k') as string) ? params.get('k') : null
  };
}

export function writeCheckoutParams({ step, dest, key }: CheckoutParams): string {
  const p = new URLSearchParams();
  if (step !== 'delivery') p.set('step', step);
  if (dest.companyId) {
    p.set('c', dest.companyId);
    if (dest.siteId) p.set('s', dest.siteId);
    if (dest.costCentreId) p.set('cc', dest.costCentreId);
    if (dest.poNumber?.trim()) p.set('po', dest.poNumber.trim());
  } else if (dest.addressId) {
    p.set('a', dest.addressId);
  }
  if (key) p.set('k', key);
  return p.toString();
}

/** Message key (under `Checkout`) for a failed quote or placement. */
export function checkoutErrorKey(kind: CheckoutErrorKind): string {
  return `err_${kind}`;
}

/** Message key for an order status shown on the confirmation page. Unknown statuses get a neutral text. */
export function orderStatusKey(status: string): string {
  return ['pending_payment', 'pending_approval', 'confirmed', 'cancelled'].includes(status) ? `status_${status}` : 'status_other';
}

/** SKUs the server named in a refusal (`SHIPPING_QUOTE_ONLY`, `VARIANT_WEIGHT_MISSING`): `details[].message`. */
export function skusFromError(error: unknown): string[] {
  const details = (error as { error?: { details?: { message?: string }[] } } | null)?.error?.details ?? [];
  return [...new Set(details.map((d) => d.message?.trim() ?? '').filter((m) => m !== '' && m.length <= 64))].slice(0, 20);
}
