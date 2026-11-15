'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { EmptyState } from '@mechware/ui/src/components/EmptyState';
import { Skeleton } from '@mechware/ui/src/components/Skeleton';
import { ORDER_PAGE_SIZE, clampPage, formatPriceMinor, totalPages } from '@mechware/api-client';
import { useAuth } from '../../lib/auth';
import { ordersApi } from '../../lib/checkout-client';
import { formatOrderDate, orderHref, ordersListHref } from '../../lib/orders-ui';
import { useResource } from '../../lib/useResource';
import { OrderStatusChip } from './OrderParts';

const linkClass =
  'inline-flex h-10 items-center rounded-md border border-surface-border px-4 text-sm font-semibold text-text hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';

/** The signed-in buyer's own orders, newest first, 20 per page. The page number lives in the URL (`?page=`). */
export function OrdersListView({ locale }: { locale: 'en' | 'bn' }) {
  const t = useTranslations('Orders');
  const { status } = useAuth();
  const prefix = locale === 'bn' ? '/bn' : '';
  const [page, setPage] = React.useState(1);
  const [ready, setReady] = React.useState(false);

  React.useEffect(() => {
    setPage(clampPage(new URLSearchParams(window.location.search).get('page')));
    setReady(true);
  }, []);

  const go = (next: number) => {
    setPage(next);
    window.history.replaceState(null, '', ordersListHref(prefix, next));
    window.scrollTo(0, 0);
  };

  const orders = useResource(() => ordersApi.list(page, ORDER_PAGE_SIZE), `p${page}`, status === 'authenticated' && ready);

  if (orders.error) {
    return (
      <div role="alert" className="space-y-3 rounded-lg border border-fault/50 bg-fault/10 p-4">
        <p className="text-text">{t('loadError')}</p>
        <Button onClick={orders.reload}>{t('retry')}</Button>
      </div>
    );
  }
  if (!orders.data) {
    return (
      <div aria-hidden="true" className="space-y-3">
        {Array.from({ length: 4 }, (_, i) => (
          <Skeleton key={i} className="h-24 w-full" />
        ))}
      </div>
    );
  }

  const { items, totalCount } = orders.data;
  const pages = totalPages(totalCount, ORDER_PAGE_SIZE);
  if (items.length === 0 && page === 1) {
    return (
      <EmptyState
        headingLevel={2}
        title={t('empty')}
        description={t('emptyBody')}
        action={<Link prefetch={false} href={`${prefix}/search`} className="inline-flex h-10 items-center rounded-md bg-accent px-4 text-sm font-semibold text-accent-fg hover:bg-accent-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent">{t('emptyAction')}</Link>}
      />
    );
  }

  return (
    <div className="space-y-4">
      <ul aria-label={t('listLabel')} className="space-y-3">
        {items.map((o) => (
          <li key={o.id}>
            <Link
              prefetch={false}
              href={orderHref(prefix, o.orderNumber)}
              className="block rounded-lg border border-surface-border p-4 hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
            >
              <div className="flex flex-wrap items-start justify-between gap-2">
                <span data-testid="row-number" className="break-all font-mono text-base font-bold text-text">{o.orderNumber}</span>
                <OrderStatusChip status={o.status} />
              </div>
              <div className="mt-2 flex flex-wrap items-end justify-between gap-x-4 gap-y-1 text-sm text-text-muted">
                <span>
                  {t('placedOn', { date: formatOrderDate(o.createdAt, locale) })} · {t('items', { count: o.itemCount })}
                </span>
                <span data-testid="row-total" className="text-base font-bold text-text">{formatPriceMinor(o.grandTotalMinor, 'BDT', locale)}</span>
              </div>
              {o.purchaseOrderNumber && (
                <p className="mt-1 break-all text-sm text-text-muted">{t('poNumber')}: <span className="font-mono text-text">{o.purchaseOrderNumber}</span></p>
              )}
            </Link>
          </li>
        ))}
      </ul>
      {pages > 1 && (
        <nav aria-label={t('pagesLabel')} className="flex flex-wrap items-center justify-between gap-3">
          {page > 1 ? (
            <button type="button" onClick={() => go(page - 1)} className={linkClass}>{t('prev')}</button>
          ) : (
            <span aria-hidden="true" className="h-10 w-20" />
          )}
          <span className="text-sm text-text-muted">{t('pageOf', { page, total: pages })}</span>
          {page < pages ? (
            <button type="button" onClick={() => go(page + 1)} className={linkClass}>{t('next')}</button>
          ) : (
            <span aria-hidden="true" className="h-10 w-20" />
          )}
        </nav>
      )}
    </div>
  );
}
