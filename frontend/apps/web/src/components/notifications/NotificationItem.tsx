'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { formatNotificationTime, localeLink, notificationMessageKey, notificationParams, type BuyerNotification } from '@mechware/api-client';

const rowClass = 'block rounded-md px-3 py-2 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';

/** One notification: title from its key and params (unknown key: generic text), Dhaka time, and a link that marks it read when opened. */
export function NotificationItem({ n, locale, onOpen }: { n: BuyerNotification; locale: 'en' | 'bn'; onOpen: (n: BuyerNotification) => void }) {
  const t = useTranslations('Notifications');
  const unread = n.readAt === null;
  const href = localeLink(n.link);
  const body = (
    <>
      <span className="flex items-start gap-2">
        {unread && <span aria-hidden="true" className="mt-1.5 h-2 w-2 shrink-0 rounded-full bg-accent" />}
        <span className={unread ? 'font-semibold text-text' : 'text-text-muted'}>
          {unread && <span className="sr-only">{t('unreadMark')} </span>}
          {t(`title.${notificationMessageKey(n.titleKey)}`, notificationParams(n.params))}
        </span>
      </span>
      <time dateTime={n.createdAt} className="mt-0.5 block text-xs text-text-muted">{formatNotificationTime(n.createdAt, locale)}</time>
    </>
  );
  return href ? (
    <Link prefetch={false} href={href} onClick={() => onOpen(n)} className={`${rowClass} hover:bg-surface`}>{body}</Link>
  ) : (
    <div className={rowClass}>{body}</div>
  );
}
