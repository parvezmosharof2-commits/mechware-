'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge } from '@mechware/ui/src/components/Badge';
import { canRecordWithholding, canRequestCreditNote, canViewInvoices, dhakaWhen, formatBdt, invoiceKindText, invoiceModeText, type StaffInvoiceDetail } from '@mechware/api-client';
import { useAuth } from '../lib/auth';
import { staffInvoicesApi } from '../lib/api';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { BackLink, FailureAlert, Loading, NoAccess } from './CreditShared';
const CreditNoteRequestButton = React.lazy(() => import('./CreditNoteRequestButton').then((m) => ({ default: m.CreditNoteRequestButton })));
const WithholdingRecordButton = React.lazy(() => import('./WithholdingRecordButton').then((m) => ({ default: m.WithholdingRecordButton })));
import { InvoicePdfButtons, invoiceFailure } from './InvoicePdfButtons';

const th = 'px-3 py-2';
const money = (minor: number, currency = 'BDT') => `${currency} ${formatBdt(minor)}`;

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  const id = React.useId();
  return <section aria-labelledby={id} className="space-y-2"><h2 id={id} className="text-base font-semibold">{title}</h2>{children}</section>;
}
function Facts({ rows }: { rows: Array<[string, React.ReactNode]> }) {
  return (
    <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2 lg:grid-cols-3">
      {rows.filter(([, v]) => v !== null && v !== '' && v !== undefined).map(([k, v]) => <div key={k} className="min-w-0"><dt className="text-xs text-text-muted">{k}</dt><dd className="break-words font-medium">{v}</dd></div>)}
    </dl>
  );
}

function Body({ s, canRequest, canWithhold }: { s: StaffInvoiceDetail; canRequest: boolean; canWithhold: boolean }) {
  const d = s.detail;
  const i = d.invoice;
  return (
    <>
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div className="space-y-1">
          <h2 className="font-mono text-xl font-bold" data-testid="invoice-number">{i.number}</h2>
          <p className="flex flex-wrap items-center gap-2 text-sm">
            <Badge variant="outline">{invoiceKindText(i.kind)}</Badge>
            <Badge variant="outline" data-testid="invoice-mode">{invoiceModeText(i.mode)}</Badge>
          </p>
          {i.mode === 'specimen' && <p className="text-sm font-medium" data-testid="invoice-specimen">Specimen, not a tax invoice.</p>}
        </div>
        <div className="space-y-3">
          <InvoicePdfButtons invoiceId={i.id} number={i.number} />
          {canRequest && i.kind === 'tax_invoice' && <React.Suspense fallback={null}><CreditNoteRequestButton detail={d} /></React.Suspense>}
          {canWithhold && i.kind === 'tax_invoice' && i.mode !== 'specimen' && <React.Suspense fallback={null}><WithholdingRecordButton invoiceId={i.id} invoiceNumber={i.number} /></React.Suspense>}
          {i.kind === 'tax_invoice' && <Link href={`/invoices/withholdings?status=verified&invoiceId=${i.id}`} className="block text-sm font-semibold text-brand-accent underline" data-testid="withholdings-link">VDS/AIT certificates of this invoice</Link>}
          {i.kind === 'credit_note' && <Link href="/invoices/credit-notes" className="block text-sm font-semibold text-brand-accent underline">Credit note queue</Link>}
        </div>
      </header>

      <Section title="Invoice">
        <Facts rows={[
          ['Issued (Dhaka)', dhakaWhen(i.issuedAtUtc)], ['Series, year, serial', `${s.series} / ${s.fiscalYear} / ${s.serial}`],
          ['Order', <Link key="o" href={`/orders/${i.orderId}`} className="text-brand-accent underline" data-testid="order-link">Order {i.orderNumber}</Link>],
          ['Company', <Link key="c" href={`/credit/companies/${s.companyId}`} className="text-brand-accent underline" data-testid="company-link">Open the company</Link>],
          ['Credit note of', i.originalNumber ? <Link key="n" href={`/invoices/${i.originalInvoiceId}`} className="text-brand-accent underline">{i.originalNumber}</Link> : null],
          ['Purchase order', d.purchaseOrderNumber], ['Quote', d.quoteNumber],
          ['Checked by a VAT consultant', s.consultantChecked ? 'Yes' : 'Not yet'], ['Issued by', s.issuedBy ? `staff ${s.issuedBy.slice(0, 8)}` : 'the system'],
          ['Content hash', <span key="h" className="break-all font-mono text-xs">{s.contentHash}</span>]
        ]} />
      </Section>

      <Section title="Seller and buyer">
        <Facts rows={[
          ['Seller', d.seller.legalNameEn], ['Seller BIN', d.seller.bin], ['Seller TIN', d.seller.tin],
          ['Buyer', d.buyer.name], ['Buyer BIN (whole)', d.buyer.bin ? <span key="b" className="font-mono" data-testid="buyer-bin">{d.buyer.bin}</span> : 'Not provided'], ['Buyer address', d.buyer.address],
          ['Courier', d.delivery?.courierName ?? null], ['Tracking number', d.delivery?.trackingNumber ?? null]
        ]} />
      </Section>

      <Section title="Lines">
        <div className="overflow-x-auto rounded-md border border-surface-border" role="region" tabIndex={0} aria-label="Invoice lines">
          <table className="w-full text-left text-sm" data-testid="invoice-lines">
            <caption className="sr-only">Lines of invoice {i.number}</caption>
            <thead className="bg-bg-subtle text-xs text-text-muted">
              <tr>
                <th scope="col" className={th}>Item</th><th scope="col" className={`${th} text-right`}>Qty</th><th scope="col" className={`${th} text-right`}>Unit price</th>
                <th scope="col" className={`${th} text-right`}>Net</th><th scope="col" className={`${th} text-right`}>VAT %</th><th scope="col" className={`${th} text-right`}>VAT</th>
                <th scope="col" className={`${th} text-right`}>Total</th>
              </tr>
            </thead>
            <tbody>
              {d.lines.map((l) => (
                <tr key={l.lineNo} className="border-t border-surface-border align-top" data-testid="invoice-line">
                  <td className="px-3 py-2">{l.descriptionEn}<span className="block font-mono text-xs text-text-muted">{l.sku ?? l.kind} · {l.unit}</span></td>
                  <td className="px-3 py-2 text-right">{l.quantity}</td>
                  <td className="whitespace-nowrap px-3 py-2 text-right font-mono text-xs">{formatBdt(l.unitPriceMinor)}</td>
                  <td className="whitespace-nowrap px-3 py-2 text-right font-mono text-xs">{formatBdt(l.netMinor)}</td>
                  <td className="px-3 py-2 text-right">{l.vatRate}</td>
                  <td className="whitespace-nowrap px-3 py-2 text-right font-mono text-xs">{formatBdt(l.vatMinor)}</td>
                  <td className="whitespace-nowrap px-3 py-2 text-right font-mono text-xs">{formatBdt(l.totalMinor)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Section>

      <Section title="Totals">
        <Facts rows={[['Net', money(i.netMinor, i.currency)], ['Supplementary duty', money(i.sdMinor, i.currency)], ['VAT', money(i.vatMinor, i.currency)],
          ['Total', <strong key="t" data-testid="invoice-total">{money(i.totalMinor, i.currency)}</strong>]]} />
      </Section>
    </>
  );
}

function Detail({ id, canRequest, canWithhold }: { id: string; canRequest: boolean; canWithhold: boolean }) {
  const res = useResource(() => staffInvoicesApi.detail(id), id);
  const failure = res.error ? { text: invoiceFailure(res.error), retry: true } : null;
  return (
    <div className="space-y-6">
      <BackLink href="/invoices">Back to all invoices</BackLink>
      <FailureAlert failure={failure} onRetry={res.reload} />
      {res.loading && !res.data && <Loading />}
      {res.data && <Body s={res.data} canRequest={canRequest} canWithhold={canWithhold} />}
    </div>
  );
}

export function InvoiceDetailView({ id }: { id: string }) {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="invoices" title="Invoice">
      {canViewInvoices(user?.permissions ?? []) ? <Detail id={id} canRequest={canRequestCreditNote(user?.permissions ?? [])} canWithhold={canRecordWithholding(user?.permissions ?? [])} /> : <NoAccess permission="invoices.view" />}
    </ConsoleShell>
  );
}
