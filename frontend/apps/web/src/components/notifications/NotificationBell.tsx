'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { BELL_ITEMS, NOTIFICATION_POLL_MS, badgeText, type BuyerNotification } from '@mechware/api-client';
import { notificationsApi } from '../../lib/notifications-client';
import { NotificationItem } from './NotificationItem';

/** Bell for a signed-in buyer: unread count (polled every 60 s while the tab is visible, and on focus) and the latest 10 in a dropdown. */
export function NotificationBell({ locale }: { locale: 'en' | 'bn' }) {
  const t = useTranslations('Notifications');
  const prefix = locale === 'bn' ? '/bn' : '';
  const [count, setCount] = React.useState(0);
  const [open, setOpen] = React.useState(false);
  const [items, setItems] = React.useState<BuyerNotification[] | null>(null);
  const [failed, setFailed] = React.useState(false);
  const root = React.useRef<HTMLDivElement>(null);
  const button = React.useRef<HTMLButtonElement>(null);

  const refreshCount = React.useCallback(() => {
    notificationsApi.unreadCount().then(setCount, () => undefined);
  }, []);

  React.useEffect(() => {
    refreshCount();
    const tick = () => { if (document.visibilityState === 'visible') refreshCount(); };
    const timer = window.setInterval(tick, NOTIFICATION_POLL_MS);
    window.addEventListener('focus', tick);
    document.addEventListener('visibilitychange', tick);
    return () => {
      window.clearInterval(timer);
      window.removeEventListener('focus', tick);
      document.removeEventListener('visibilitychange', tick);
    };
  }, [refreshCount]);

  React.useEffect(() => {
    if (!open) return;
    setFailed(false);
    notificationsApi.list(false, 1).then((p) => setItems(p.items.slice(0, BELL_ITEMS)), () => setFailed(true));
    const away = (e: MouseEvent) => { if (!root.current?.contains(e.target as Node)) setOpen(false); };
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') { setOpen(false); button.current?.focus(); } };
    document.addEventListener('mousedown', away);
    document.addEventListener('keydown', esc);
    return () => { document.removeEventListener('mousedown', away); document.removeEventListener('keydown', esc); };
  }, [open]);

  const opened = (n: BuyerNotification) => {
    setOpen(false);
    if (n.readAt !== null) return;
    setItems((cur) => cur && cur.map((x) => (x.id === n.id ? { ...x, readAt: new Date().toISOString() } : x)));
    setCount((c) => Math.max(0, c - 1));
    notificationsApi.markRead(n.id).catch(() => refreshCount());
  };
  const readAll = () => {
    notificationsApi.markAllRead().then(
      () => { setCount(0); setItems((cur) => cur && cur.map((x) => ({ ...x, readAt: x.readAt ?? new Date().toISOString() }))); },
      () => refreshCount()
    );
  };

  const text = badgeText(count);
  return (
    <div ref={root} className="relative">
      <button
        ref={button}
        type="button"
        aria-expanded={open}
        aria-controls="notification-panel"
        aria-label={t('bell.label', { count })}
        onClick={() => setOpen((v) => !v)}
        className="relative inline-flex h-10 w-10 items-center justify-center rounded-md border border-surface-border text-text hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
      >
        <svg aria-hidden="true" viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9" /><path d="M10.3 21a1.94 1.94 0 0 0 3.4 0" />
        </svg>
        {text && <span aria-hidden="true" className="absolute -right-1 -top-1 min-w-5 rounded-full bg-fault px-1 text-center text-xs font-bold leading-5 text-white">{text}</span>}
      </button>
      {open && (
        <div id="notification-panel" role="region" aria-label={t('panelLabel')} className="absolute right-0 z-40 mt-2 w-80 max-w-[calc(100vw-2rem)] rounded-md border border-surface-border bg-bg p-2 shadow-lg">
          <div className="flex items-center justify-between gap-2 px-3 py-1">
            <h2 className="text-sm font-bold text-text">{t('title.heading')}</h2>
            <button type="button" onClick={readAll} disabled={count === 0} className="min-h-8 rounded-md px-2 text-xs font-semibold text-accent hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:opacity-50">{t('markAll')}</button>
          </div>
          {failed ? (
            <p role="alert" className="px-3 py-2 text-sm text-text">{t('loadError')}</p>
          ) : !items ? (
            <p className="px-3 py-2 text-sm text-text-muted">{t('loading')}</p>
          ) : items.length === 0 ? (
            <p className="px-3 py-2 text-sm text-text-muted">{t('empty')}</p>
          ) : (
            <ul className="max-h-96 divide-y divide-surface-border overflow-y-auto">
              {items.map((n) => <li key={n.id}><NotificationItem n={n} locale={locale} onOpen={opened} /></li>)}
            </ul>
          )}
          <Link prefetch={false} href={`${prefix}/account/notifications`} onClick={() => setOpen(false)} className="mt-1 block rounded-md px-3 py-2 text-center text-sm font-semibold text-accent hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent">{t('viewAll')}</Link>
        </div>
      )}
    </div>
  );
}
