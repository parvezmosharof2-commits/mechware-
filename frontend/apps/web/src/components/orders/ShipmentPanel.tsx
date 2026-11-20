'use client';

import * as React from 'react';
import { useTranslations } from 'next-intl';
import { beforeShipping, safeTrackingUrl, type OrderDetail } from '@mechware/api-client';
import { formatOrderDate } from '../../lib/orders-ui';
import { CopyPartNumber } from '../search/CopyPartNumber';

/** Where the parcel is: courier, tracking number (with a copy button), the courier's own link and the dates; before shipping, the status in words. No staff data is ever sent. */
export function ShipmentPanel({ order, locale }: { order: OrderDetail; locale: string }) {
  const t = useTranslations('Orders.tracking');
  const shipments = order.shipments ?? [];
  if (shipments.length === 0) {
    if (!beforeShipping(order.status)) return null;
    return (
      <section aria-labelledby="tracking-title" className="rounded-lg border border-surface-border p-4" data-testid="tracking-waiting">
        <h2 id="tracking-title" className="mb-1 font-display text-lg font-bold text-text">{t('title')}</h2>
        <p className="text-sm text-text">{t(`wait_${order.status}`)}</p>
      </section>
    );
  }
  return (
    <section aria-labelledby="tracking-title" className="rounded-lg border border-surface-border p-4" data-testid="tracking-block">
      <h2 id="tracking-title" className="mb-3 font-display text-lg font-bold text-text">{t('title')}</h2>
      {shipments.map((s) => {
        const link = safeTrackingUrl(s.trackingUrl);
        const shipped = formatOrderDate(s.shippedAtUtc, locale);
        const delivered = formatOrderDate(s.deliveredAtUtc, locale);
        return (
          <dl key={`${s.trackingNumber}-${s.shippedAtUtc}`} className="space-y-1.5 text-sm" data-testid="tracking-shipment">
            <div className="flex justify-between gap-4"><dt className="text-text-muted">{t('courier')}</dt><dd className="break-words text-right text-text" data-testid="tracking-courier">{s.courierName}</dd></div>
            <div className="flex items-center justify-between gap-4">
              <dt className="text-text-muted">{t('number')}</dt>
              <dd className="flex min-w-0 items-center gap-1 text-text"><span className="break-all font-mono" data-testid="tracking-number">{s.trackingNumber}</span><CopyPartNumber value={s.trackingNumber} label={t('copy')} copiedLabel={t('copied')} /></dd>
            </div>
            {link && (
              <div className="flex justify-between gap-4">
                <dt className="text-text-muted">{t('link')}</dt>
                <dd className="text-right"><a href={link} target="_blank" rel="noopener noreferrer" data-testid="tracking-link" className="inline-flex min-h-[44px] items-center font-semibold text-accent underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent">{t('open')}<span className="sr-only"> {t('newTab')}</span></a></dd>
              </div>
            )}
            {shipped && <div className="flex justify-between gap-4"><dt className="text-text-muted">{t('shipped')}</dt><dd className="text-text" data-testid="tracking-shipped">{shipped}</dd></div>}
            {delivered && <div className="flex justify-between gap-4"><dt className="text-text-muted">{t('delivered')}</dt><dd className="text-text" data-testid="tracking-delivered">{delivered}</dd></div>}
          </dl>
        );
      })}
    </section>
  );
}
