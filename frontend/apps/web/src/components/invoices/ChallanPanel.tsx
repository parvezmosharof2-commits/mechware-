'use client';

import * as React from 'react';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { classifyChallanError, hasChallan, type InvoiceLocale, type OrderDetail } from '@mechware/api-client';
import { problemCode, problemStatus } from '../../lib/api';
import { downloadChallan } from '../../lib/invoices-client';

/** "Delivery challan" in English or Bangla, once the order has shipped. A refusal (404, 409, 429, outage) is said in words. */
export function ChallanPanel({ order }: { order: OrderDetail }) {
  const t = useTranslations('Invoices.challan');
  const [busy, setBusy] = React.useState<InvoiceLocale | null>(null);
  const [refusal, setRefusal] = React.useState<string | null>(null);
  const [done, setDone] = React.useState(false);
  if (!hasChallan(order)) return null;
  const go = async (locale: InvoiceLocale) => {
    setBusy(locale);
    setRefusal(null);
    setDone(false);
    try {
      await downloadChallan(order.id, order.orderNumber, locale);
      setDone(true);
    } catch (e) {
      setRefusal(classifyChallanError(problemStatus(e), problemCode(e)));
    } finally {
      setBusy(null);
    }
  };
  return (
    <section aria-labelledby="challan-title" data-testid="challan-panel" className="space-y-2 rounded-lg border border-surface-border p-4">
      <h2 id="challan-title" className="font-display text-lg font-bold text-text">{t('title')}</h2>
      <p className="text-sm text-text-muted">{t('note')}</p>
      <div className="flex flex-wrap gap-2">
        <Button variant="outline" size="sm" data-testid="challan-en" isLoading={busy === 'en'} disabled={busy !== null} onClick={() => go('en')}>{t('en')}</Button>
        <Button variant="outline" size="sm" data-testid="challan-bn" isLoading={busy === 'bn'} disabled={busy !== null} onClick={() => go('bn')}>{t('bn')}</Button>
      </div>
      {refusal && <p role="alert" data-testid="challan-refusal" data-refusal={refusal} className="rounded-md border border-fault/50 bg-fault/10 px-3 py-2 text-sm text-text">{t(refusal)}</p>}
      <p role="status" className="sr-only">{done ? t('saved') : ''}</p>
    </section>
  );
}
