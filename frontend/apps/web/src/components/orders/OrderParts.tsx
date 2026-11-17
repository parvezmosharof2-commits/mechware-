'use client';

import * as React from 'react';
import { useTranslations } from 'next-intl';
import {
  creditStatusKey,
  creditStatusTone,
  formatCreditDay,
  formatPriceMinor,
  orderCreditOwes,
  orderCreditVisible,
  orderStatusLabelKey,
  orderStatusTone,
  type OrderDetail
} from '@mechware/api-client';
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

/**
 * Credit position of a credit order (status, due date, what is still owed). Shown only when the API sends it: it is null for a member
 * whose role may not see the company's credit, and for orders that are not on credit; then nothing is rendered at all (no error, no hint).
 */
export function OrderCreditNote({ order, locale }: { order: OrderDetail; locale: string }) {
  const t = useTranslations('Orders');
  const credit = order.credit;
  if (!orderCreditVisible(credit)) return null;
  const tone = creditStatusTone(credit.status);
  const key = creditStatusKey(credit.status);
  return (
    <section aria-labelledby="order-credit" data-testid="order-credit" className="rounded-lg border border-surface-border p-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id="order-credit" className="font-display text-lg font-bold text-text">{t('credit.title')}</h2>
        <span data-testid="order-credit-status" className={`inline-flex items-center rounded-full border px-2.5 py-0.5 text-xs font-semibold ${statusChipClass(tone)}`}>
          {t(`credit.${key}`)}
        </span>
      </div>
      <p data-testid="order-credit-due" className="mt-2 text-sm text-text-muted">{t('credit.due', { date: formatCreditDay(credit.dueOn, locale) })}</p>
      {orderCreditOwes(credit) && (
        <p data-testid="order-credit-owed" className="mt-1 text-sm font-semibold text-text">
          {t('credit.outstanding', { amount: formatPriceMinor(credit.outstandingMinor, order.currency, locale) })}
        </p>
      )}
    </section>
  );
}
