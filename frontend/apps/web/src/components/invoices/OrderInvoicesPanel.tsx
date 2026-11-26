'use client';

import * as React from 'react';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { Skeleton } from '@mechware/ui/src/components/Skeleton';
import { invoicesApi } from '../../lib/invoices-client';
import { useResource } from '../../lib/useResource';
import { InvoiceFacts, InvoicePdfButtons, RefusalText, refusalOf } from './InvoiceParts';

/** The invoices (and credit notes) of one order, with a PDF in English or Bangla each. Shown on the buyer's order page. */
export function OrderInvoicesPanel({ orderId, locale }: { orderId: string; locale: 'en' | 'bn' }) {
  const t = useTranslations('Invoices');
  const res = useResource(() => invoicesApi.forOrder(orderId), orderId);
  return (
    <section aria-labelledby="order-invoices" aria-busy={res.loading} data-testid="order-invoices" className="space-y-3 rounded-lg border border-surface-border p-4">
      <h2 id="order-invoices" className="font-display text-lg font-bold text-text">{t('orderHeading')}</h2>
      {res.error ? (
        <div className="space-y-3">
          <RefusalText refusal={refusalOf(res.error)} />
          {refusalOf(res.error) !== 'notFound' && <Button variant="outline" size="sm" onClick={res.reload}>{t('retry')}</Button>}
        </div>
      ) : !res.data ? (
        <div aria-hidden="true"><Skeleton className="h-20 w-full" /></div>
      ) : res.data.length === 0 ? (
        <p data-testid="order-invoices-empty" className="text-sm text-text-muted">{t('orderEmpty')}</p>
      ) : (
        <ul aria-label={t('orderListLabel')} className="divide-y divide-surface-border">
          {res.data.map((inv) => (
            <li key={inv.id} data-testid="order-invoice" className="space-y-3 py-3 first:pt-0 last:pb-0">
              <InvoiceFacts invoice={inv} locale={locale} />
              <InvoicePdfButtons invoice={inv} />
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
