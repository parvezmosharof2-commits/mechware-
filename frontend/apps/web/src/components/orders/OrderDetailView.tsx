'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { Skeleton } from '@mechware/ui/src/components/Skeleton';
import {
  TIMELINE_STAGES,
  findOrderByNumber,
  formatPriceMinor,
  isContractPrice,
  isValidOrderNumber,
  orderTimelineIndex,
  type OrderDetail,
  type OrderLine,
  type SubOrder
} from '@mechware/api-client';
import { useAuth } from '../../lib/auth';
import { ordersApi } from '../../lib/checkout-client';
import { classifyOrderError, formatOrderDate } from '../../lib/orders-ui';
import { useResource } from '../../lib/useResource';
import { OrderCreditNote, OrderStatusChip, OrderTotals } from './OrderParts';
import { OrderInvoicesPanel } from '../invoices/OrderInvoicesPanel';
import { ReorderPanel } from './ReorderPanel';
import { ReturnPanel } from '../returns/ReturnPanel';
import { ShipmentPanel } from './ShipmentPanel';

type Translate = ReturnType<typeof useTranslations>;

class OrderNotFound extends Error {
  readonly status = 404;
}

/** The email link carries the order number; the API takes the order id, so the id is found through the buyer's own list. */
async function loadByNumber(orderNumber: string): Promise<OrderDetail & { companyId: string | null }> {
  if (!isValidOrderNumber(orderNumber)) throw new OrderNotFound();
  const found = await findOrderByNumber(orderNumber, ordersApi.list);
  if (!found) throw new OrderNotFound();
  return { ...(await ordersApi.get(found.id)), companyId: found.companyId ?? null };
}

function Timeline({ status, t }: { status: string; t: Translate }) {
  const index = orderTimelineIndex(status);
  if (index === null) return null;
  return (
    <ol aria-label={t('timelineLabel')} className="grid grid-cols-4 gap-1 text-center text-xs sm:text-sm">
      {TIMELINE_STAGES.map((stage, i) => (
        <li
          key={stage}
          aria-current={i === index ? 'step' : undefined}
          className={`rounded-md border px-1 py-2 ${i <= index ? 'border-accent/60 bg-accent/10 font-semibold text-text' : 'border-surface-border text-text-muted'}`}
        >
          {t(`tl_${stage}`)}
        </li>
      ))}
    </ol>
  );
}

function Line({ line, currency, locale, t }: { line: OrderLine; currency: string; locale: string; t: Translate }) {
  const money = (minor: number) => formatPriceMinor(minor, currency, locale);
  return (
    <li className="flex flex-wrap items-start justify-between gap-x-4 gap-y-1 py-3">
      <div className="min-w-0 flex-1">
        <p className="break-words font-medium text-text">{line.title}</p>
        <p className="text-sm text-text-muted">{t('sku')}: <span className="break-all font-mono">{line.sku}</span></p>
        <p className="text-sm text-text-muted">{t('quantity', { count: line.quantity })} · {t('unitPrice', { amount: money(line.unitPriceMinor) })}</p>
        {isContractPrice(line) && (
          <p className="text-sm font-medium text-text">{line.contractRef ? t('contractPrice', { ref: line.contractRef }) : t('contractPriceNoRef')}</p>
        )}
      </div>
      <p className="font-semibold text-text">{money(line.totalMinor)}</p>
    </li>
  );
}

function Shipment({ sub, currency, locale, t }: { sub: SubOrder; currency: string; locale: string; t: Translate }) {
  return (
    <section aria-label={t('linesLabel', { number: sub.subOrderNumber })} className="rounded-lg border border-surface-border p-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="break-all font-display text-lg font-bold text-text">{t('shipment', { number: sub.subOrderNumber })}</h2>
        <OrderStatusChip status={sub.status} />
      </div>
      <ul className="divide-y divide-surface-border">
        {sub.items.map((line) => (
          <Line key={`${sub.id}-${line.variantId}`} line={line} currency={currency} locale={locale} t={t} />
        ))}
      </ul>
      <p className="mt-2 text-sm text-text-muted">{t('shipmentFee', { amount: formatPriceMinor(sub.shippingMinor, currency, locale) })}</p>
    </section>
  );
}

/** One order, found by the number in the URL. Someone else's order and a wrong number look the same: "not found". */
export function OrderDetailView({ locale, orderNumber }: { locale: 'en' | 'bn'; orderNumber: string }) {
  const t = useTranslations('Orders');
  const { status } = useAuth();
  const prefix = locale === 'bn' ? '/bn' : '';
  const order = useResource(() => loadByNumber(orderNumber), orderNumber, status === 'authenticated');
  const backLink = (
    <Link prefetch={false} href={`${prefix}/account/orders`} className="inline-flex h-10 items-center rounded-md border border-surface-border px-4 text-sm font-semibold text-text hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent">
      {t('backToOrders')}
    </Link>
  );

  if (order.error) {
    const kind = classifyOrderError(order.error);
    if (kind === 'notFound') {
      return (
        <div data-testid="order-not-found" className="space-y-4 rounded-lg border border-surface-border p-6">
          <h2 className="font-display text-xl font-bold text-text">{t('notFoundTitle')}</h2>
          <p className="text-text-muted">{t('notFoundBody')}</p>
          {backLink}
        </div>
      );
    }
    return (
      <div role="alert" className="space-y-3 rounded-lg border border-fault/50 bg-fault/10 p-4">
        <p className="text-text">{t('detailLoadError')}</p>
        <Button onClick={order.reload}>{t('retry')}</Button>
      </div>
    );
  }
  if (!order.data) {
    return (
      <div aria-hidden="true" className="space-y-3">
        <Skeleton className="h-12 w-full" />
        <Skeleton className="h-40 w-full" />
        <Skeleton className="h-28 w-full" />
      </div>
    );
  }

  const o = order.data;
  const expected = formatOrderDate(o.estimatedDeliveryBy, locale);
  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <p data-testid="order-number" className="break-all font-mono text-xl font-bold text-text">{o.orderNumber}</p>
          <p className="text-sm text-text-muted">{t('placedAt', { date: formatOrderDate(o.placedAtUtc, locale) })}</p>
          {o.quoteNumber && (
            <p className="break-all text-sm text-text-muted" data-testid="order-source">{t('sourceQuote', { number: o.quoteNumber })}</p>
          )}
          {o.purchaseOrderNumber && (
            <p className="break-all text-sm text-text-muted">{t('poNumber')}: <span data-testid="po-number" className="font-mono text-text">{o.purchaseOrderNumber}</span></p>
          )}
        </div>
        <OrderStatusChip status={o.status} />
      </div>
      <Timeline status={o.status} t={t} />
      {expected && (
        <p data-testid="expected-by" className="rounded-md border border-surface-border px-3 py-2 text-sm text-text">
          <span className="font-semibold">{t('expectedBy', { date: expected })}</span>
          <span className="block text-text-muted">{t('expectedNote')}</span>
        </p>
      )}
      <ShipmentPanel order={o} locale={locale} />
      {o.subOrders.map((sub) => (
        <Shipment key={sub.id} sub={sub} currency={o.currency} locale={locale} t={t} />
      ))}
      <OrderTotals order={o} locale={locale} />
      <OrderCreditNote order={o} locale={locale} />
      <OrderInvoicesPanel orderId={o.id} locale={locale} />
      <ReturnPanel order={o} companyId={o.companyId} locale={locale} />
      <ReorderPanel order={o} locale={locale} />
      {backLink}
    </div>
  );
}
