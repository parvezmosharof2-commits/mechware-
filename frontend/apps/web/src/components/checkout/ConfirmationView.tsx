'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { Skeleton } from '@mechware/ui/src/components/Skeleton';
import { isAwaitingApproval, isOrderId, isValidOrderNumber, type OrderDetail } from '@mechware/api-client';
import { useAuth } from '../../lib/auth';
import { ordersApi } from '../../lib/checkout-client';
import { formatOrderDate, orderHref } from '../../lib/orders-ui';
import { OrderStatusChip, OrderTotals } from '../orders/OrderParts';

const button = 'inline-flex h-10 items-center rounded-md px-4 text-sm font-semibold focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';

/** What the buyer sees right after placing: the order the server reports (`?order=<number>&id=<orderId>`), what happens next, where to go on. */
export function ConfirmationView({ locale }: { locale: 'en' | 'bn' }) {
  const t = useTranslations('Orders');
  const { status } = useAuth();
  const prefix = locale === 'bn' ? '/bn' : '';
  const [number, setNumber] = React.useState('');
  const [order, setOrder] = React.useState<OrderDetail | null>(null);
  const [failed, setFailed] = React.useState(false);

  React.useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const orderNumber = (params.get('order') ?? '').slice(0, 40);
    setNumber(isValidOrderNumber(orderNumber) ? orderNumber : '');
    const id = params.get('id');
    if (status !== 'authenticated' || !isOrderId(id)) return;
    ordersApi.get(id).then(setOrder, () => setFailed(true));
  }, [status]);

  if (!number) return <p className="text-text-muted">{t('confirmNoOrder')}</p>;
  const approval = order ? isAwaitingApproval(order.status) : false;
  const waitingPayment = order?.status === 'pending_payment';
  return (
    <div className="space-y-5">
      <div className="space-y-2 rounded-lg border border-surface-border p-6">
        <p className="text-sm text-text-muted">{t('orderNumber')}</p>
        <p data-testid="order-number" className="break-all font-mono text-2xl font-bold text-text">{number}</p>
        {order && (
          <p className="flex flex-wrap items-center gap-2 text-text">
            {t('status')}: <OrderStatusChip status={order.status} />
          </p>
        )}
        {order?.purchaseOrderNumber && <p className="break-all text-sm text-text-muted">{t('poNumber')}: <span className="font-mono text-text">{order.purchaseOrderNumber}</span></p>}
        {order && formatOrderDate(order.estimatedDeliveryBy, locale) !== '' && (
          <p data-testid="expected-by" className="text-sm text-text">
            <span className="font-semibold">{t('expectedBy', { date: formatOrderDate(order.estimatedDeliveryBy, locale) })}</span>
            <span className="block text-text-muted">{t('expectedNote')}</span>
          </p>
        )}
      </div>
      {!order && !failed && <Skeleton aria-hidden="true" className="h-40 w-full" />}
      {failed && <p role="status" className="rounded-md border border-status-pending/50 bg-status-pending/10 px-3 py-2 text-sm text-text">{t('confirmLoadError')}</p>}
      {order && (
        <>
          {approval && <p role="status" className="rounded-md border border-status-pending/50 bg-status-pending/10 px-3 py-2 text-sm text-text">{t('sentForApproval')}</p>}
          <OrderTotals order={order} locale={locale} />
          <section aria-labelledby="next-steps" className="space-y-2 rounded-lg border border-surface-border p-4">
            <h2 id="next-steps" className="font-display text-lg font-bold text-text">{t('nextTitle')}</h2>
            <p className="text-text">{approval ? t('nextApproval') : waitingPayment ? t('nextPayment') : t('nextOther')}</p>
            {waitingPayment && (
              <div data-testid="payment-area" className="text-sm">
                <p className="text-text">{t('payment')}: <span className="font-semibold">{t('paymentUnpaid')}</span></p>
                {/* TODO(E3.5): the payment choice and the "Pay now" link go here once the payment gateway exists. */}
                <p className="text-text-muted">{t('paymentTodo')}</p>
              </div>
            )}
          </section>
        </>
      )}
      <div className="flex flex-wrap gap-3">
        <Link prefetch={false} href={orderHref(prefix, number)} className={`${button} bg-accent text-accent-fg hover:bg-accent-hover`}>{t('viewOrder')}</Link>
        <Link prefetch={false} href={`${prefix}/search`} className={`${button} border border-surface-border text-text hover:bg-surface`}>{t('continueShopping')}</Link>
        <Link prefetch={false} href={`${prefix}/account/orders`} className={`${button} border border-surface-border text-text hover:bg-surface`}>{t('viewOrders')}</Link>
      </div>
    </div>
  );
}
