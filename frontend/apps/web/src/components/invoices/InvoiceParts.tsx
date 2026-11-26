'use client';

import * as React from 'react';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import {
  classifyInvoiceError,
  formatPriceMinor,
  invoiceKindKey,
  isSpecimenInvoice,
  type InvoiceLocale,
  type InvoiceRefusal,
  type InvoiceSummary
} from '@mechware/api-client';
import { problemCode, problemStatus } from '../../lib/api';
import { downloadInvoicePdf } from '../../lib/invoices-client';
import { formatOrderDate } from '../../lib/orders-ui';

/** The refusal as words (message key under `Invoices.refusal`). */
export function refusalOf(error: unknown): InvoiceRefusal {
  return classifyInvoiceError(problemStatus(error), problemCode(error));
}

export function RefusalText({ refusal, testId }: { refusal: InvoiceRefusal; testId?: string }) {
  const t = useTranslations('Invoices.refusal');
  return <p role="alert" data-testid={testId ?? 'invoice-refusal'} data-refusal={refusal} className="rounded-md border border-fault/50 bg-fault/10 px-3 py-2 text-sm text-text">{t(refusal)}</p>;
}

/** Kind and, for a specimen, the words "specimen, not a tax invoice" (colour is never the only signal). */
export function InvoiceChips({ invoice }: { invoice: InvoiceSummary }) {
  const t = useTranslations('Invoices');
  return (
    <span className="inline-flex flex-wrap items-center gap-2">
      <span className="inline-flex items-center rounded-full border border-surface-border bg-surface px-2.5 py-0.5 text-xs font-semibold text-text">{t(`kind.${invoiceKindKey(invoice.kind)}`)}</span>
      {isSpecimenInvoice(invoice) && (
        <span data-testid="invoice-specimen" className="inline-flex items-center rounded-full border border-status-pending/50 bg-status-pending/10 px-2.5 py-0.5 text-xs font-semibold text-text">{t('specimen')}</span>
      )}
    </span>
  );
}

/** Two buttons, one per language. A failed download is said in words under the buttons. */
export function InvoicePdfButtons({ invoice, companyId }: { invoice: InvoiceSummary; companyId?: string }) {
  const t = useTranslations('Invoices');
  const [busy, setBusy] = React.useState<InvoiceLocale | null>(null);
  const [refusal, setRefusal] = React.useState<InvoiceRefusal | null>(null);
  const [done, setDone] = React.useState<InvoiceLocale | null>(null);
  const go = async (locale: InvoiceLocale) => {
    setBusy(locale);
    setRefusal(null);
    setDone(null);
    try {
      await downloadInvoicePdf(invoice.orderId, invoice.id, invoice.number, locale, companyId);
      setDone(locale);
    } catch (e) {
      setRefusal(refusalOf(e));
    } finally {
      setBusy(null);
    }
  };
  return (
    <div className="space-y-2">
      <div className="flex flex-wrap gap-2" role="group" aria-label={t('pdfGroup', { number: invoice.number })}>
        <Button variant="outline" size="sm" data-testid="invoice-pdf-en" isLoading={busy === 'en'} disabled={busy !== null} onClick={() => go('en')}>{t('pdfEn')}</Button>
        <Button variant="outline" size="sm" data-testid="invoice-pdf-bn" isLoading={busy === 'bn'} disabled={busy !== null} onClick={() => go('bn')}>{t('pdfBn')}</Button>
      </div>
      {refusal && <RefusalText refusal={refusal} testId="invoice-pdf-refusal" />}
      <p role="status" className="sr-only">{done ? t('pdfSaved', { number: invoice.number }) : ''}</p>
    </div>
  );
}

/** Number, Dhaka date, total and kind of one invoice; callers add the actions. */
export function InvoiceFacts({ invoice, locale, numberNode }: { invoice: InvoiceSummary; locale: 'en' | 'bn'; numberNode?: React.ReactNode }) {
  const t = useTranslations('Invoices');
  return (
    <>
      <div className="flex flex-wrap items-start justify-between gap-2">
        {numberNode ?? <span data-testid="invoice-number" className="break-all font-mono font-semibold text-text">{invoice.number}</span>}
        <InvoiceChips invoice={invoice} />
      </div>
      <dl className="mt-2 grid grid-cols-2 gap-x-3 gap-y-1 text-sm">
        <div>
          <dt className="text-xs text-text-muted">{t('date')}</dt>
          <dd data-testid="invoice-date">{formatOrderDate(invoice.issuedAtUtc, locale)}</dd>
        </div>
        <div>
          <dt className="text-xs text-text-muted">{t('total')}</dt>
          <dd data-testid="invoice-total" className="font-semibold tabular-nums">{formatPriceMinor(invoice.totalMinor, invoice.currency, locale)}</dd>
        </div>
        {invoice.originalNumber && (
          <div className="col-span-2">
            <dt className="text-xs text-text-muted">{t('originalInvoice')}</dt>
            <dd className="break-all font-mono">{invoice.originalNumber}</dd>
          </div>
        )}
      </dl>
    </>
  );
}
