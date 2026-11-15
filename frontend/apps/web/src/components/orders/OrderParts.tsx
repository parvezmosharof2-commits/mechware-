'use client';

import * as React from 'react';
import { useTranslations } from 'next-intl';
import { formatPriceMinor, orderStatusLabelKey, orderStatusTone, type OrderDetail } from '@mechware/api-client';
import { statusChipClass } from '../../lib/orders-ui';

/** The status as words (never colour alone). Unknown statuses get a generic label. */
export function OrderStatusChip({ status }: { status: string }) {
  const t = useTranslations('Orders');
  return (
    <span className={`inline-flex items-center rounded-full border px-2.5 py-0.5 text-xs font-semibold ${statusChipClass(orderStatusTone(status))}`}>
      {t(orderStatusLabelKey(status))}
    </span>
  );
}

/** Totals exactly as the server returned them; nothing is recomputed here. */
export function OrderTotals({ order, locale }: { order: OrderDetail; locale: string }) {
  const t = useTranslations('Orders');
  const money = (minor: number) => formatPriceMinor(minor, order.currency, locale);
  const rows: [string, number][] = [[t('subtotal'), order.subtotalMinor]];
  if (order.discountMinor > 0) rows.push([t('discount'), -order.discountMinor]);
  rows.push([t('tax'), order.taxMinor], [t('shipping'), order.shippingMinor]);
  return (
    <section aria-labelledby="order-totals" className="rounded-lg border border-surface-border p-4">
      <h2 id="order-totals" className="mb-3 font-display text-lg font-bold text-text">{t('totals')}</h2>
      <dl className="space-y-1.5 text-sm">
        {rows.map(([label, minor]) => (
          <div key={label} className="flex justify-between gap-4">
            <dt className="text-text-muted">{label}</dt>
            <dd data-testid={`total-${label}`} className="text-text">{money(minor)}</dd>
          </div>
        ))}
        <div className="flex justify-between gap-4 border-t border-surface-border pt-2 text-base font-bold">
          <dt className="text-text">{t('grandTotal')}</dt>
          <dd data-testid="grand-total" className="text-text">{money(order.grandTotalMinor)}</dd>
        </div>
      </dl>
    </section>
  );
}
