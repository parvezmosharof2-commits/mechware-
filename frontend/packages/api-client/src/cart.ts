import type { MechWareApiClient } from './index';
import type { CartEnvelope, CartView } from './cart-model';

/**
 * Typed wrappers for /api/v1/cart. Anonymous visitors are identified by the HttpOnly `__Host-mw_cart` cookie the API sets
 * (the shared client always sends credentials); signed-in users by the Bearer token the shared client attaches.
 * Prices are never sent: a body carries a variant id and a quantity only. Responses are unwrapped to the CartView.
 */
export function createCartApi(client: MechWareApiClient) {
  const json = (body: unknown): RequestInit => ({ body: JSON.stringify(body) });
  const unwrap = (p: Promise<CartEnvelope>): Promise<CartView> => p.then((r) => r.data);

  return {
    get: () => unwrap(client.fetch<CartEnvelope>('/api/v1/cart')),

    /** Adds to the line (quantity must be > 0). */
    addItem: (variantId: string, quantity: number) =>
      unwrap(client.fetch<CartEnvelope>('/api/v1/cart/items', { method: 'POST', ...json({ variantId, quantity }) })),

    /** Sets the line's quantity; 0 removes it. */
    setItem: (variantId: string, quantity: number) =>
      unwrap(client.fetch<CartEnvelope>(`/api/v1/cart/items/${encodeURIComponent(variantId)}`, { method: 'PUT', ...json({ quantity }) })),

    /** Idempotent. */
    removeItem: (variantId: string) =>
      unwrap(client.fetch<CartEnvelope>(`/api/v1/cart/items/${encodeURIComponent(variantId)}`, { method: 'DELETE' })),

    clear: () => unwrap(client.fetch<CartEnvelope>('/api/v1/cart', { method: 'DELETE' }))
  };
}

export type CartApi = ReturnType<typeof createCartApi>;
