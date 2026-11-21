'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { EmptyState } from '@mechware/ui/src/components/EmptyState';
import { Skeleton } from '@mechware/ui/src/components/Skeleton';
import { RETURN_PAGE_SIZE, clampPage, totalPages } from '@mechware/api-client';
import { useAuth } from '../../lib/auth';
import { formatOrderDate } from '../../lib/orders-ui';
import { returnsApi } from '../../lib/returns-client';
import { useResource } from '../../lib/useResource';
import { ReturnStatusChip } from './ReturnParts';

const linkClass = 'inline-flex h-10 items-center rounded-md border border-surface-border px-4 text-sm font-semibold text-text hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';

/** The signed-in buyer's own returns, newest first. The page number lives in the URL (`?page=`). */
export function ReturnsListView({ locale }: { locale: 'en' | 'bn' }) {
  const t = useTranslations('Returns');
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
    window.history.replaceState(null, '', next > 1 ? `${prefix}/account/returns?page=${next}` : `${prefix}/account/returns`);
    window.scrollTo(0, 0);
  };
  const res = useResource(() => returnsApi.list(page, RETURN_PAGE_SIZE), `p${page}`, status === 'authenticated' && ready);

  if (res.error) {
    return (
      <div role="alert" className="space-y-3 rounded-lg border border-fault/50 bg-fault/10 p-4">
        <p className="text-text">{t('loadError')}</p>
        <Button onClick={res.reload}>{t('retry')}</Button>
      </div>
    );
  }
  if (!res.data) {
    return <div aria-hidden="true" className="space-y-3">{Array.from({ length: 3 }, (_, i) => <Skeleton key={i} className="h-24 w-full" />)}</div>;
  }
  const { items, totalCount } = res.data;
  const pages = totalPages(totalCount, RETURN_PAGE_SIZE);
  if (items.length === 0 && page === 1) {
    return <EmptyState headingLevel={2} title={t('empty')} description={t('emptyBody')} />;
  }
  return (
    <div className="space-y-4">
      <ul aria-label={t('listLabel')} className="space-y-3">
        {items.map((r) => (
          <li key={r.id}>
            <Link prefetch={false} href={`${prefix}/account/returns/${r.id}`} className="block rounded-lg border border-surface-border p-4 hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent" data-testid="return-row">
              <div className="flex flex-wrap items-start justify-between gap-2">
                <span className="break-all font-mono text-base font-bold text-text">{r.orderNumber}</span>
                <ReturnStatusChip status={r.status} />
              </div>
              <p className="mt-2 text-sm text-text-muted">
                {t('panel.requestedOn', { date: formatOrderDate(r.requestedAtUtc, locale) })} · {t('items', { count: r.lines.reduce((n, l) => n + l.quantity, 0) })} · {t(`reason.${r.reasonCode}`)}
              </p>
            </Link>
          </li>
        ))}
      </ul>
      {pages > 1 && (
        <nav aria-label={t('pagesLabel')} className="flex flex-wrap items-center justify-between gap-3">
          {page > 1 ? <button type="button" onClick={() => go(page - 1)} className={linkClass}>{t('prev')}</button> : <span aria-hidden="true" className="h-10 w-20" />}
          <span className="text-sm text-text-muted">{t('pageOf', { page, total: pages })}</span>
          {page < pages ? <button type="button" onClick={() => go(page + 1)} className={linkClass}>{t('next')}</button> : <span aria-hidden="true" className="h-10 w-20" />}
        </nav>
      )}
      <Link prefetch={false} href={`${prefix}/account/orders`} className={linkClass}>{t('toOrders')}</Link>
    </div>
  );
}
