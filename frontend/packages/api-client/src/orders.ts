import type { MechWareApiClient } from './index';
import type { PlacedOrder, ShippingQuote, buildOrderBody, buildQuoteBody } from './checkout-model';

type Data<T> = { data: T };

export interface OrderSummary {
  id: string;
  orderNumber: string;
  status: string;
  currency: string;
  grandTotalMinor: number;
  purchaseOrderNumber?: string | null;
}

/** Typed wrappers for the checkout part of /api/v1/orders. Bodies carry ids and quantities only; the server prices everything. */
export function createOrdersApi(client: MechWareApiClient) {
  return {
    /** `body` comes from `buildQuoteBody`. */
    quoteShipping: (body: ReturnType<typeof buildQuoteBody>) =>
      client
        .fetch<Data<ShippingQuote>>('/api/v1/orders/shipping-quote', { method: 'POST', body: JSON.stringify(body) })
        .then((r) => r.data),

    /** `body` comes from `buildOrderBody`. `Idempotency-Key` is required; the same key with the same body returns the first order (`replayed: true`). */
    place: (body: ReturnType<typeof buildOrderBody>, idempotencyKey: string) =>
      client
        .fetch<Data<PlacedOrder>>('/api/v1/orders', {
          method: 'POST',
          headers: { 'Idempotency-Key': idempotencyKey },
          body: JSON.stringify(body)
        })
        .then((r) => r.data),

    get: (orderId: string) => client.fetch<Data<OrderSummary>>(`/api/v1/orders/${encodeURIComponent(orderId)}`).then((r) => r.data)
  };
}

export type OrdersApi = ReturnType<typeof createOrdersApi>;
