/** Tiny event so the header badge follows the cart page and the product page without sharing state or loading the cart module. */
export const CART_EVENT = 'mw:cart-changed';

export interface CartChangedDetail {
  lineCount: number;
}

/** Tells every listener (the header badge) how many different items the cart now holds, as the server just reported. */
export function announceCart(lineCount: number): void {
  if (typeof window === 'undefined') return;
  window.dispatchEvent(new CustomEvent<CartChangedDetail>(CART_EVENT, { detail: { lineCount } }));
}

/** Bangla digits for the 'bn' locale; "99+" above 99 so the badge keeps its size. */
export function badgeText(count: number, locale: string): string {
  const fmt = new Intl.NumberFormat(locale === 'bn' ? 'bn-BD' : 'en-US');
  return count > 99 ? `${fmt.format(99)}+` : fmt.format(count);
}
