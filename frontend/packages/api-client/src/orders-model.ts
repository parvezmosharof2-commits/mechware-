// Buyer order pages (E3.6 + F5.1): wire types and pure helpers. Money is always the server's, in minor units; nothing here computes a price.

import type { BuyerShipment } from './staff-fulfilment-model';

export interface OrderListItem {
  id: string;
  orderNumber: string;
  status: string;
  grandTotalMinor: number;
  createdAt: string;
  itemCount: number;
  companyId?: string | null;
  purchaseOrderNumber?: string | null;
}

export interface OrderPage {
  items: OrderListItem[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface OrderLine {
  /** The order line id (F2.4): a return request names its lines by it. */
  id?: string;
  variantId: string;
  sku: string;
  title: string;
  quantity: number;
  unitPriceMinor: number;
  discountMinor: number;
  taxMinor: number;
  totalMinor: number;
  priceSource?: string | null;
  contractRef?: string | null;
}

export interface SubOrder {
  id: string;
  subOrderNumber: string;
  sellerId: string;
  status: string;
  shippingMinor: number;
  totalMinor: number;
  items: OrderLine[];
}

/** `GET /orders/{id}` data. There is no shipping address, payment status or store name in this contract yet (backend follow-ups). */
export interface OrderDetail {
  id: string;
  orderNumber: string;
  status: string;
  currency: string;
  subtotalMinor: number;
  discountMinor: number;
  taxMinor: number;
  shippingMinor: number;
  grandTotalMinor: number;
  placedAtUtc: string;
  subOrders: SubOrder[];
  purchaseOrderNumber?: string | null;
  /** The quote this order was accepted from (D4.9), or null for a cart order. */
  quoteNumber?: string | null;
  estimatedDeliveryBy?: string | null;
  /** Credit position of a charged credit order, only for a member who may see the company's credit (B3.9); otherwise null or absent. */
  credit?: { status: string; dueOn: string; outstandingMinor: number } | null;
  /** Manual tracking (F1.1): oldest first, empty before shipping. */
  shipments?: BuyerShipment[];
}

export const ORDER_PAGE_SIZE = 20;
/** The email link carries the order number, which `GET /orders/{id}` does not take: the list is searched this many pages deep. */
export const ORDER_LOOKUP_MAX_PAGES = 5;

/** Every wire status the server can send (OrderStatus, snake_case). */
export const ORDER_STATUSES = [
  'pending_approval', 'pending_payment', 'confirmed', 'processing', 'packed', 'shipped', 'out_for_delivery', 'delivered',
  'completed', 'cancelled', 'delivery_failed', 'returned', 'return_requested', 'return_approved', 'refunded'
] as const;

/** Message key (under `Orders`) for a status. Anything unknown, including an empty value, gets the generic label. */
export function orderStatusLabelKey(status: string | null | undefined): string {
  return (ORDER_STATUSES as readonly string[]).includes(status ?? '') ? `status_${status}` : 'status_other';
}

export type OrderStatusTone = 'pending' | 'progress' | 'done' | 'problem' | 'neutral';

export function orderStatusTone(status: string | null | undefined): OrderStatusTone {
  switch (status) {
    case 'pending_approval':
    case 'pending_payment':
      return 'pending';
    case 'confirmed':
    case 'processing':
    case 'packed':
    case 'shipped':
    case 'out_for_delivery':
      return 'progress';
    case 'delivered':
    case 'completed':
      return 'done';
    case 'cancelled':
    case 'delivery_failed':
      return 'problem';
    default:
      return 'neutral';
  }
}

export type TimelineStage = 'placed' | 'confirmed' | 'shipped' | 'delivered';
export const TIMELINE_STAGES: readonly TimelineStage[] = ['placed', 'confirmed', 'shipped', 'delivered'];

/**
 * How far along the simple placed -> confirmed -> shipped -> delivered line an order is, from its current status only (no dates are
 * invented). Null for orders that left the line (cancelled, failed, returns, refunds): the page shows the status text alone.
 */
export function orderTimelineIndex(status: string | null | undefined): number | null {
  switch (status) {
    case 'pending_approval':
    case 'pending_payment':
      return 0;
    case 'confirmed':
    case 'processing':
    case 'packed':
      return 1;
    case 'shipped':
    case 'out_for_delivery':
      return 2;
    case 'delivered':
    case 'completed':
      return 3;
    default:
      return null;
  }
}

/** Number of pages for a total (at least 1, so an empty list still has "page 1 of 1"). */
export function totalPages(totalCount: number, pageSize: number = ORDER_PAGE_SIZE): number {
  if (!Number.isFinite(totalCount) || totalCount <= 0 || pageSize <= 0) return 1;
  return Math.ceil(totalCount / pageSize);
}

/** A page number from user input (URL), clamped to 1..max; junk becomes 1. */
export function clampPage(raw: string | number | null | undefined, max: number = Number.MAX_SAFE_INTEGER): number {
  const n = typeof raw === 'number' ? raw : Number.parseInt(String(raw ?? ''), 10);
  if (!Number.isFinite(n) || n < 1) return 1;
  return Math.min(Math.floor(n), Math.max(1, max));
}

/** Order numbers are short ASCII codes (letters, digits, dashes); anything else never reaches the API. */
export function isValidOrderNumber(value: string | null | undefined): value is string {
  return typeof value === 'string' && value.length >= 3 && value.length <= 40 && /^[A-Za-z0-9-]+$/.test(value);
}

export function isOrderId(value: string | null | undefined): value is string {
  return typeof value === 'string' && /^[0-9a-fA-F-]{36}$/.test(value);
}

/**
 * The order id for an order number, found by paging the buyer's own list (newest first) until it shows up, at most
 * `maxPages` pages. Null when it is not in those pages: the page then shows the same "not found" as another buyer's order.
 * Errors from `fetchPage` propagate (a failed lookup is not "not found").
 */
export async function findOrderByNumber(
  orderNumber: string,
  fetchPage: (page: number, pageSize: number) => Promise<OrderPage>,
  maxPages: number = ORDER_LOOKUP_MAX_PAGES,
  pageSize: number = ORDER_PAGE_SIZE
): Promise<OrderListItem | null> {
  const wanted = orderNumber.trim().toLowerCase();
  for (let page = 1; page <= maxPages; page++) {
    const result = await fetchPage(page, pageSize);
    const hit = result.items.find((o) => o.orderNumber.toLowerCase() === wanted);
    if (hit) return hit;
    if (page >= totalPages(result.totalCount, result.pageSize || pageSize) || result.items.length === 0) return null;
  }
  return null;
}

export async function findOrderIdByNumber(orderNumber: string, fetchPage: (page: number, pageSize: number) => Promise<OrderPage>, maxPages?: number, pageSize?: number): Promise<string | null> {
  return (await findOrderByNumber(orderNumber, fetchPage, maxPages, pageSize))?.id ?? null;
}

/** "Your contract price (ref X)" applies to contract prices only. */
export function isContractPrice(line: Pick<OrderLine, 'priceSource'>): boolean {
  return line.priceSource === 'contract';
}
