'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { RETURN_WINDOW_DAYS, returnAvailability, returnableByLine, type OrderDetail } from '@mechware/api-client';
import { formatOrderDate } from '../../lib/orders-ui';
import { returnsApi } from '../../lib/returns-client';
import { useResource } from '../../lib/useResource';
import { ReturnForm } from './ReturnForm';
import { ReturnStatusChip } from './ReturnParts';

/** On a delivered order: "Return items" while the window is open, the returns already asked for this order, or the reason there is no button. */
export function ReturnPanel({ order, companyId, locale }: { order: OrderDetail; companyId: string | null; locale: 'en' | 'bn' }) {
  const t = useTranslations('Returns');
  const prefix = locale === 'bn' ? '/bn' : '';
  const [open, setOpen] = React.useState(false);
  const returns = useResource(() => returnsApi.list(1, 50), order.id, order.status === 'delivered');
  const lines = React.useMemo(() => order.subOrders.flatMap((s) => s.items).filter((l) => l.id), [order]);
  if (order.status !== 'delivered') return null;
  const mine = (returns.data?.items ?? []).filter((r) => r.orderId === order.id);
  const max = returnableByLine(lines, returns.data?.items ?? [], order.id);
  const left = Object.values(max).reduce((a, b) => a + b, 0);
  const availability = returns.data ? returnAvailability(order, left) : 'available';
  const lastDelivered = (order.shipments ?? []).map((s) => s.deliveredAtUtc).filter(Boolean).sort().pop();

  return (
    <section aria-labelledby="returns-title" data-testid="return-panel" className="space-y-3 rounded-lg border border-surface-border p-4">
      <h2 id="returns-title" className="font-display text-lg font-bold text-text">{t('panel.title')}</h2>
      {mine.length > 0 && (
        <ul aria-label={t('panel.existing')} className="space-y-2">
          {mine.map((r) => (
            <li key={r.id} className="flex flex-wrap items-center justify-between gap-2 text-sm">
              <Link prefetch={false} href={`${prefix}/account/returns/${r.id}`} className="font-semibold text-accent underline" data-testid="order-return-link">
                {t('panel.requestedOn', { date: formatOrderDate(r.requestedAtUtc, locale) })}
              </Link>
              <ReturnStatusChip status={r.status} />
            </li>
          ))}
        </ul>
      )}
      {returns.error ? (
        <p role="alert" className="text-sm text-text">{t('loadError')} <Button variant="outline" size="sm" onClick={returns.reload}>{t('retry')}</Button></p>
      ) : open ? (
        <ReturnForm orderNumber={order.orderNumber} companyId={companyId} lines={lines} max={max} prefix={prefix} onCancel={() => setOpen(false)} onSent={returns.reload} />
      ) : availability === 'windowClosed' ? (
        <p data-testid="return-closed" className="text-sm text-text">{t('error.windowClosed')}</p>
      ) : availability === 'nothingLeft' ? (
        <p data-testid="return-nothing-left" className="text-sm text-text">{t('panel.nothingLeft')}</p>
      ) : (
        <div className="space-y-2">
          <p className="text-sm text-text-muted">{t('panel.window', { days: RETURN_WINDOW_DAYS })}{lastDelivered ? ` ${t('panel.delivered', { date: formatOrderDate(lastDelivered, locale) })}` : ''}</p>
          <Button type="button" onClick={() => setOpen(true)} disabled={returns.loading && !returns.data} data-testid="return-open">{t('panel.open')}</Button>
        </div>
      )}
    </section>
  );
}
