'use client';

import * as React from 'react';
import { useLocale, useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { formatPriceMinor, type BuyerInvoiceDetail } from '@mechware/api-client';
import { useAuth } from '../../lib/auth';
import { invoicesApi } from '../../lib/invoices-client';
import { useResource } from '../../lib/useResource';
import { ListSkeleton, PageShell, RequireSignIn, useLocalePath } from '../company/shared';
import { NotAvailable } from './CompanyInvoicesView';
import { InvoiceFacts, InvoicePdfButtons, RefusalText, refusalOf } from './InvoiceParts';

function Party({ title, name, rows }: { title: string; name: string; rows: [string, string | null | undefined][] }) {
  return (
    <section aria-label={title} className="rounded-lg border border-surface-border p-4">
      <h2 className="text-sm font-semibold text-text-muted">{title}</h2>
      <p className="mt-1 break-words font-semibold text-text">{name}</p>
      <dl className="mt-2 space-y-1 text-sm">
        {rows.filter(([, v]) => !!v).map(([k, v]) => (
          <div key={k}><dt className="inline text-text-muted">{k}: </dt><dd className="inline break-all font-mono">{v}</dd></div>
        ))}
      </dl>
    </section>
  );
}

function Detail({ d }: { d: BuyerInvoiceDetail }) {
  const t = useTranslations('Invoices');
  const locale = useLocale() === 'bn' ? 'bn' : 'en';
  const money = (m: number) => formatPriceMinor(m, d.invoice.currency, locale);
  return (
    <div className="space-y-5" data-testid="invoice-detail">
      <section className="rounded-lg border border-surface-border p-4">
        <InvoiceFacts invoice={d.invoice} locale={locale} />
        <p className="mt-2 break-all text-sm text-text-muted">{t('forOrder', { number: d.invoice.orderNumber })}</p>
        <div className="mt-3"><InvoicePdfButtons invoice={d.invoice} /></div>
        <p className="mt-2 text-xs text-text-muted">{t('pdfNote')}</p>
      </section>
      <div className="grid gap-3 sm:grid-cols-2">
        <Party title={t('seller')} name={locale === 'bn' && d.seller.legalNameBn ? d.seller.legalNameBn : d.seller.legalNameEn} rows={[['BIN', d.seller.bin], ['TIN', d.seller.tin]]} />
        <Party title={t('buyer')} name={d.buyer.name} rows={[['BIN', d.buyer.bin], [t('address'), d.buyer.address]]} />
      </div>
      <section aria-label={t('linesLabel')} className="rounded-lg border border-surface-border p-4">
        <h2 className="text-sm font-semibold text-text-muted">{t('linesLabel')}</h2>
        <ul className="mt-2 divide-y divide-surface-border">
          {d.lines.map((l) => (
            <li key={l.lineNo} data-testid="invoice-line" className="flex flex-wrap items-start justify-between gap-x-4 gap-y-1 py-3 text-sm">
              <div className="min-w-0 flex-1">
                <p className="break-words font-medium text-text">{locale === 'bn' && l.descriptionBn ? l.descriptionBn : l.descriptionEn}</p>
                {l.sku && <p className="text-text-muted">{t('sku')}: <span className="break-all font-mono">{l.sku}</span></p>}
                <p className="text-text-muted">{t('qty', { count: l.quantity, unit: l.unit })} · {t('vat', { amount: money(l.vatMinor) })}</p>
              </div>
              <p className="font-semibold tabular-nums">{money(l.totalMinor)}</p>
            </li>
          ))}
        </ul>
        <dl className="mt-3 space-y-1 border-t border-surface-border pt-3 text-sm">
          <div className="flex justify-between gap-3"><dt className="text-text-muted">{t('net')}</dt><dd className="tabular-nums">{money(d.invoice.netMinor)}</dd></div>
          <div className="flex justify-between gap-3"><dt className="text-text-muted">{t('vatTotal')}</dt><dd className="tabular-nums">{money(d.invoice.vatMinor)}</dd></div>
          <div className="flex justify-between gap-3 font-semibold"><dt>{t('total')}</dt><dd className="tabular-nums">{money(d.invoice.totalMinor)}</dd></div>
        </dl>
      </section>
    </div>
  );
}

function Body({ companyId, invoiceId }: { companyId: string; invoiceId: string }) {
  const t = useTranslations('Invoices');
  const { user } = useAuth();
  const res = useResource(() => invoicesApi.companyDetail(companyId, invoiceId), `${user?.id ?? ''}:${companyId}:${invoiceId}`);
  if (res.loading) return <ListSkeleton rows={3} />;
  if (res.error) {
    if (refusalOf(res.error) === 'notFound') return <NotAvailable companyId={companyId} />;
    return (
      <div className="space-y-3">
        <RefusalText refusal={refusalOf(res.error)} />
        <Button variant="outline" size="sm" onClick={res.reload}>{t('retry')}</Button>
      </div>
    );
  }
  return res.data ? <Detail d={res.data} /> : null;
}

export function CompanyInvoiceDetailView({ companyId, invoiceId }: { companyId: string; invoiceId: string }) {
  const t = useTranslations('Invoices');
  const toPath = useLocalePath();
  return (
    <PageShell title={t('detailTitle')} back={{ href: toPath(`/account/company/${encodeURIComponent(companyId)}/invoices`), label: t('backToList') }}>
      <RequireSignIn>
        <Body companyId={companyId} invoiceId={invoiceId} />
      </RequireSignIn>
    </PageShell>
  );
}
