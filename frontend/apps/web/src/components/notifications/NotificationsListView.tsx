'use client';

import * as React from 'react';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { EmptyState } from '@mechware/ui/src/components/EmptyState';
import { Skeleton } from '@mechware/ui/src/components/Skeleton';
import { NOTIFICATION_PAGE_SIZE, clampPage, totalPages, type BuyerNotification } from '@mechware/api-client';
import { useAuth } from '../../lib/auth';
import { notificationsApi } from '../../lib/notifications-client';
import { useResource } from '../../lib/useResource';
import { NotificationItem } from './NotificationItem';

const pageBtn = 'inline-flex h-10 items-center rounded-md border border-surface-border px-4 text-sm font-semibold text-text hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';

/** The buyer's notifications, newest first, paged, with an unread filter. Filter and page live in the URL (`?unread=1&page=`). */
export function NotificationsListView({ locale }: { locale: 'en' | 'bn' }) {
  const t = useTranslations('Notifications');
  const { status } = useAuth();
  const prefix = locale === 'bn' ? '/bn' : '';
  const [unreadOnly, setUnreadOnly] = React.useState(false);
  const [page, setPage] = React.useState(1);
  const [ready, setReady] = React.useState(false);
  const [done, setDone] = React.useState<Record<string, true>>({});
  React.useEffect(() => {
    const q = new URLSearchParams(window.location.search);
    setUnreadOnly(q.get('unread') === '1');
    setPage(clampPage(q.get('page')));
    setReady(true);
  }, []);
  const go = (unread: boolean, next: number) => {
    setUnreadOnly(unread);
    setPage(next);
    const q = [unread ? 'unread=1' : '', next > 1 ? `page=${next}` : ''].filter(Boolean).join('&');
    window.history.replaceState(null, '', `${prefix}/account/notifications${q ? `?${q}` : ''}`);
  };
  const res = useResource(() => notificationsApi.list(unreadOnly, page), `${unreadOnly}-${page}`, status === 'authenticated' && ready);
  const mark = (n: BuyerNotification) => {
    setDone((d) => ({ ...d, [n.id]: true }));
    if (n.readAt === null) void notificationsApi.markRead(n.id).catch(() => undefined);
  };
  const readAll = () => { void notificationsApi.markAllRead().then(() => res.reload(), () => res.reload()); };

  const filter = (
    <div role="group" aria-label={t('filterLabel')} className="mb-4 flex flex-wrap items-center gap-2">
      <button type="button" aria-pressed={!unreadOnly} onClick={() => go(false, 1)} className={`${pageBtn} aria-pressed:bg-surface`}>{t('filterAll')}</button>
      <button type="button" aria-pressed={unreadOnly} onClick={() => go(true, 1)} className={`${pageBtn} aria-pressed:bg-surface`}>{t('filterUnread')}</button>
      <Button variant="ghost" onClick={readAll} className="ml-auto">{t('markAll')}</Button>
    </div>
  );
  if (res.error) {
    return (
      <div role="alert" className="space-y-3 rounded-lg border border-fault/50 bg-fault/10 p-4">
        <p className="text-text">{t('loadError')}</p>
        <Button onClick={res.reload}>{t('retry')}</Button>
      </div>
    );
  }
  if (!res.data) return <div aria-hidden="true" className="space-y-3">{Array.from({ length: 4 }, (_, i) => <Skeleton key={i} className="h-16 w-full" />)}</div>;
  const pages = totalPages(res.data.total, NOTIFICATION_PAGE_SIZE);
  return (
    <div>
      {filter}
      {res.data.items.length === 0 ? (
        <EmptyState headingLevel={2} title={unreadOnly ? t('emptyUnread') : t('empty')} description={t('emptyBody')} />
      ) : (
        <ul aria-label={t('listLabel')} className="divide-y divide-surface-border rounded-lg border border-surface-border">
          {res.data.items.map((n) => <li key={n.id}><NotificationItem n={done[n.id] ? { ...n, readAt: n.readAt ?? 'read' } : n} locale={locale} onOpen={mark} /></li>)}
        </ul>
      )}
      {pages > 1 && (
        <nav aria-label={t('pagesLabel')} className="mt-4 flex flex-wrap items-center justify-between gap-3">
          {page > 1 ? <button type="button" onClick={() => go(unreadOnly, page - 1)} className={pageBtn}>{t('prev')}</button> : <span aria-hidden="true" className="h-10 w-20" />}
          <span className="text-sm text-text-muted">{t('pageOf', { page, total: pages })}</span>
          {page < pages ? <button type="button" onClick={() => go(unreadOnly, page + 1)} className={pageBtn}>{t('next')}</button> : <span aria-hidden="true" className="h-10 w-20" />}
        </nav>
      )}
    </div>
  );
}
