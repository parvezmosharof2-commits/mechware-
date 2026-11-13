'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { isAwaitingApproval, type OrderSummary } from '@mechware/api-client';
import { useAuth } from '../../lib/auth';
import { ordersApi } from '../../lib/checkout-client';
import { orderStatusKey } from '../../lib/checkout-ui';

// TODO(E3.6): link to the buyer's orders list once that page exists.
/** Placeholder confirmation (E3.6 replaces it): the order number from the URL and the status the server reports. */
export function ConfirmationView({ locale }: { locale: 'en' | 'bn' }) {
  const t = useTranslations('Checkout');
  const { status } = useAuth();
  const prefix = locale === 'bn' ? '/bn' : '';
  const [number, setNumber] = React.useState('');
  const [order, setOrder] = React.useState<OrderSummary | null>(null);

  React.useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const orderNumber = (params.get('order') ?? '').slice(0, 40);
    setNumber(/^[A-Za-z0-9-]+$/.test(orderNumber) ? orderNumber : '');
    const id = params.get('id') ?? '';
    if (status !== 'authenticated' || !/^[0-9a-fA-F-]{36}$/.test(id)) return;
    ordersApi.get(id).then(setOrder, () => undefined);
  }, [status]);

  if (!number) return <p className="text-text-muted">{t('confirmNoOrder')}</p>;
  const approval = order ? isAwaitingApproval(order.status) : false;
  return (
    <div className="space-y-4 rounded-lg border border-surface-border p-6">
      <p className="text-sm text-text-muted">{t('orderNumber')}</p>
      <p data-testid="order-number" className="break-all font-mono text-2xl font-bold text-text">{number}</p>
      {order && <p className="text-text">{t('orderStatus')}: <span className="font-semibold">{t(orderStatusKey(order.status))}</span></p>}
      {approval && <p role="status" className="rounded-md border border-status-pending/50 bg-status-pending/10 px-3 py-2 text-sm text-text">{t('sentForApproval')}</p>}
      <Link prefetch={false} href={`${prefix}/search`} className="inline-flex h-10 items-center rounded-md bg-accent px-4 text-sm font-semibold text-accent-fg hover:bg-accent-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent">
        {t('continueShopping')}
      </Link>
    </div>
  );
}
