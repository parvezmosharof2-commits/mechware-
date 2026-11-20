import type { MechWareApiClient } from './index';
import type { CancelledOrder, OverdueOrder, RefundQueueItem, StaffPage, StaffRefund } from './staff-orders-model';
import { overdueQuery } from './staff-orders-model';
import { orderListQuery, type OrderFilters, type StaffOrderDetail, type StaffOrderListItem } from './staff-orders-read-model';

import { shipBody, type FulfilStep, type ShipFields, type StaffShipment } from './staff-fulfilment-model';

type Data<T> = { data: T };
export interface FulfilResult { id: string; orderNumber: string; status: string; rowVersion: number; shipments: StaffShipment[] }

/**
 * Staff order screens (`/api/v1/admin`). Overdue list needs orders.view_all; cancel needs orders.cancel and ALWAYS an authenticator
 * code in `X-Step-Up-Code`; the refund queue and a refund request need orders.refund; approve (code) and reject need
 * payments.refund_approve. A code is spent even when the call is then refused. `orders`/`order` read the list and one order (orders.view_all, never cached).
 */
export function createStaffOrdersApi(client: MechWareApiClient) {
  const base = '/api/v1/admin';
  const id = (v: string) => encodeURIComponent(v);
  return {
    overdue: (q: { page?: number; pageSize?: number } = {}) => client.fetch<Data<StaffPage<OverdueOrder>>>(`${base}/orders/overdue?${overdueQuery(q)}`),
    orders: (f: OrderFilters) => client.fetch<Data<StaffPage<StaffOrderListItem>>>(`${base}/orders?${orderListQuery(f)}`),
    order: (orderId: string) => client.fetch<Data<StaffOrderDetail>>(`${base}/orders/${id(orderId)}`),
    cancel: (orderId: string, reason: string, stepUpCode: string) =>
      client.fetch<Data<CancelledOrder>>(`${base}/orders/${id(orderId)}/cancel`, {
        method: 'POST', body: JSON.stringify({ reason }), headers: { 'X-Step-Up-Code': stepUpCode.trim() }
      }),
    /** One fulfilment move (orders.fulfil, no code); `rowVersion` is the one the detail showed. Ship carries the courier fields. */
    fulfil: (orderId: string, step: FulfilStep, rowVersion: number, ship?: ShipFields) =>
      client.fetch<Data<FulfilResult>>(`${base}/orders/${id(orderId)}/${step}`, { method: 'POST', body: JSON.stringify(step === 'ship' && ship ? shipBody(rowVersion, ship) : { rowVersion }) }),
    refundQueue: () => client.fetch<Data<RefundQueueItem[]>>(`${base}/refunds`),
    requestRefund: (paymentId: string, amountMinor: number, reason: string) =>
      client.fetch<Data<StaffRefund>>(`${base}/payments/${id(paymentId)}/refunds`, { method: 'POST', body: JSON.stringify({ amountMinor, reason }) }),
    approveRefund: (refundId: string, stepUpCode: string) =>
      client.fetch<Data<StaffRefund>>(`${base}/refunds/${id(refundId)}/approve`, { method: 'POST', headers: { 'X-Step-Up-Code': stepUpCode.trim() } }),
    rejectRefund: (refundId: string, reason: string) =>
      client.fetch<Data<StaffRefund>>(`${base}/refunds/${id(refundId)}/reject`, { method: 'POST', body: JSON.stringify({ reason }) })
  };
}

export type StaffOrdersApi = ReturnType<typeof createStaffOrdersApi>;
