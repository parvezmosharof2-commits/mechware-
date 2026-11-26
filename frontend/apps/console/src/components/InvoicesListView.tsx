'use client';

import * as React from 'react';
import Link from 'next/link';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import {
  EMPTY_INVOICE_FILTERS, INVOICE_KIND_OPTIONS, INVOICE_MODE_OPTIONS, activeStaffInvoiceFilterCount, canViewInvoices, dhakaWhen, formatBdt, invoiceKindText, invoiceModeText,
  parseStaffInvoiceFilters, staffInvoiceFilterProblem, staffInvoiceFiltersToSearch, type StaffInvoiceFilters, type StaffInvoiceListItem
} from '@mechware/api-client';
import { staffInvoicesApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { FailureAlert, Loading, NoAccess } from './CreditShared';
import { InvoiceCheck } from './InvoiceCheck';
import { invoiceFailure } from './InvoicePdfButtons';
import { Pager } from './QuoteDeskShared';

// The company list is fetched only when a person asks to filter by company.
const CompanyPicker = React.lazy(() => import('./CompanyPicker').then((m) => ({ default: m.CompanyPicker })));
const selectClass = 'block h-11 w-full rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm';

function Select({ id, label, value, onChange, options, any }: { id: string; label: string; value: string; onChange: (v: string) => void; options: ReadonlyArray<{ value: string; label: string }>; any: string }) {
  return (
    <div className="space-y-1.5">
      <label htmlFor={id} className="text-sm font-medium">{label}</label>
      <select id={id} className={selectClass} value={value} onChange={(e) => onChange(e.target.value)} data-testid={`filter-${id}`}>
        <option value="">{any}</option>
        {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </div>
  );
}

function List() {
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const search = params.toString();
  const applied = React.useMemo(() => parseStaffInvoiceFilters((k) => new URLSearchParams(search).get(k)), [search]);
  const [draft, setDraft] = React.useState<StaffInvoiceFilters>(applied);
  const [problem, setProblem] = React.useState<string | null>(null);
  const [pickCompany, setPickCompany] = React.useState(false);
  // Back, forward, a shared link or a page change: the form shows what the URL says.
  React.useEffect(() => { setDraft(applied); setProblem(null); }, [applied]);

  const go = (f: StaffInvoiceFilters) => { const q = staffInvoiceFiltersToSearch(f); router.push(q ? `${pathname}?${q}` : pathname); };
  const set = (patch: Partial<StaffInvoiceFilters>) => setDraft((d) => ({ ...d, ...patch }));
  const apply = (e: React.FormEvent) => {
    e.preventDefault();
    const found = staffInvoiceFilterProblem(draft);
    setProblem(found);
    if (!found) go({ ...draft, number: draft.number.trim(), orderNumber: draft.orderNumber.trim(), page: 1 });
  };

  const list = useResource(() => staffInvoicesApi.list(applied), search);
  const data = list.data;
  const failure = list.error ? { text: invoiceFailure(list.error), retry: true } : null;
  const count = activeStaffInvoiceFilterCount(applied);

  return (
    <div className="space-y-5">
      <p className="text-sm text-text-muted">Every tax invoice and credit note, newest first. Dates are Dhaka days, both ends included. The filters are in the address, so you can share or reload this page.</p>
      <form role="search" aria-label="Filter the invoices" onSubmit={apply} className="space-y-3" noValidate>
        <div className="grid items-end gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <Input label="Invoice number starts with" value={draft.number} onChange={(e) => set({ number: e.target.value })} maxLength={40} autoComplete="off" spellCheck={false} />
          <Input label="Order number starts with" value={draft.orderNumber} onChange={(e) => set({ orderNumber: e.target.value })} maxLength={40} autoComplete="off" spellCheck={false} />
          <Input label="Issued from (Dhaka day)" type="date" value={draft.from} onChange={(e) => set({ from: e.target.value })} />
          <Input label="Issued to (Dhaka day)" type="date" value={draft.to} onChange={(e) => set({ to: e.target.value })} />
          <Select id="kind" label="Kind" value={draft.kind} onChange={(v) => set({ kind: v })} options={INVOICE_KIND_OPTIONS} any="Any kind" />
          <Select id="mode" label="Mode" value={draft.mode} onChange={(v) => set({ mode: v })} options={INVOICE_MODE_OPTIONS} any="Any mode" />
          <div className="space-y-1.5 sm:col-span-2">
            <span className="block text-sm font-medium">Company</span>
            {draft.companyId ? (
              <p className="flex flex-wrap items-center gap-2 text-sm" data-testid="company-chosen">
                <Badge variant="outline">{draft.companyName || draft.companyId}</Badge>
                <Button type="button" variant="ghost" size="sm" onClick={() => set({ companyId: '', companyName: '' })}>Remove the company filter</Button>
              </p>
            ) : pickCompany ? null : (
              <Button type="button" variant="outline" onClick={() => setPickCompany(true)}>Choose a company</Button>
            )}
          </div>
        </div>
        {pickCompany && !draft.companyId && (
          <React.Suspense fallback={<p role="status" className="text-xs text-text-muted">Loading companies...</p>}>
            <CompanyPicker value="" onChange={(id, name) => { set({ companyId: id, companyName: name }); setPickCompany(false); }} />
          </React.Suspense>
        )}
        {problem && <p role="alert" data-testid="filter-problem" className="text-sm font-medium text-fault">{problem}</p>}
        <div className="flex flex-wrap items-center gap-3">
          <Button type="submit">Apply filters</Button>
          <Button type="button" variant="outline" disabled={count === 0 && activeStaffInvoiceFilterCount(draft) === 0} onClick={() => { setPickCompany(false); setDraft(EMPTY_INVOICE_FILTERS); go(EMPTY_INVOICE_FILTERS); }}>Clear all filters</Button>
          <span className="text-xs text-text-muted" data-testid="filter-count">{count === 0 ? 'No filter applied' : `${count} ${count === 1 ? 'filter' : 'filters'} applied`}</span>
        </div>
      </form>

      <FailureAlert failure={failure} onRetry={list.reload} />
      {list.loading && !data && <Loading />}
      {data && (
        <section aria-label="Invoices" className="space-y-3">
          {data.items.length === 0 ? (
            <p role="status" data-testid="invoices-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">
              {count === 0 ? 'No invoice has been issued yet. One is issued when an order ships.' : 'No invoice matches these filters. Change or clear a filter.'}
            </p>
          ) : (
            <div className="overflow-x-auto rounded-md border border-surface-border" role="region" tabIndex={0} aria-label="Invoices table">
              <table className="w-full text-left text-sm" data-testid="invoices-table">
                <caption className="sr-only">Invoices, newest first. Page {data.page}, {data.totalCount} invoices match.</caption>
                <thead className="bg-bg-subtle text-xs text-text-muted">
                  <tr>
                    <th scope="col" className="px-3 py-2">Number</th>
                    <th scope="col" className="px-3 py-2">Kind</th>
                    <th scope="col" className="px-3 py-2">Order</th>
                    <th scope="col" className="px-3 py-2">Buyer</th>
                    <th scope="col" className="px-3 py-2 text-right">Total (BDT)</th>
                    <th scope="col" className="px-3 py-2">Issued</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((o: StaffInvoiceListItem) => (
                    <tr key={o.invoice.id} className="border-t border-surface-border align-top" data-testid="invoice-row">
                      <td className="px-3 py-2">
                        <Link href={`/invoices/${o.invoice.id}`} className="font-mono text-xs font-semibold text-brand-accent underline" data-testid="invoice-link">{o.invoice.number}</Link>
                        {o.invoice.mode === 'specimen' && <span className="block text-xs text-text-muted">{invoiceModeText('specimen')}</span>}
                      </td>
                      <td className="px-3 py-2">{invoiceKindText(o.invoice.kind)}{o.invoice.originalNumber && <span className="block text-xs text-text-muted">of {o.invoice.originalNumber}</span>}</td>
                      <td className="px-3 py-2"><Link href={`/orders/${o.invoice.orderId}`} className="font-mono text-xs text-brand-accent underline">{o.invoice.orderNumber}</Link></td>
                      <td className="px-3 py-2">{o.buyerName}</td>
                      <td className="whitespace-nowrap px-3 py-2 text-right font-mono text-xs">{formatBdt(o.invoice.totalMinor)}</td>
                      <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">{dhakaWhen(o.invoice.issuedAtUtc)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <Pager page={data.page} total={data.totalCount} pageSize={data.pageSize} loading={list.loading} noun="invoices" onPage={(p) => go({ ...applied, page: p })} onRefresh={list.reload} />
        </section>
      )}
      <InvoiceCheck />
    </div>
  );
}

export function InvoicesListView() {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="invoices" title="Invoices">
      {canViewInvoices(user?.permissions ?? []) ? <React.Suspense fallback={<Loading />}><List /></React.Suspense> : <NoAccess permission="invoices.view" />}
    </ConsoleShell>
  );
}
