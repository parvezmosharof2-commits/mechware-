'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import {
  ASSIGNED_FILTERS, DESK_PAGE_SIZE, QUOTE_STATUS_FILTERS, assigneeText, canEditQuotes, canViewQuotes, creditTermsLabel, dhakaWhen, type StaffQuoteSummary
} from '@mechware/api-client';
import { deskError, staffQuotesApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { FailureAlert, Loading, NoAccess } from './CreditShared';
import { Pager, QuoteStatusBadge } from './QuoteDeskShared';

const selectClass = 'block h-11 w-full rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm';

/** The company's credit terms and whether new credit orders are on hold. No balance or limit: the quote desk never sees those. */
export function CreditCell({ terms, onHold }: { terms: string; onHold: boolean }) {
  return (
    <span className="inline-flex flex-col items-start gap-1">
      <span>{creditTermsLabel(terms)}</span>
      {onHold && <Badge variant="outline" className="border-fault font-bold text-fault" data-testid="credit-hold">New credit orders on hold</Badge>}
    </span>
  );
}

function List({ canEdit }: { canEdit: boolean }) {
  const [status, setStatus] = React.useState('');
  const [assigned, setAssigned] = React.useState('');
  const [search, setSearch] = React.useState('');
  const [q, setQ] = React.useState('');
  const [page, setPage] = React.useState(1);
  const list = useResource(() => staffQuotesApi.quotes({ status, assigned, q, page, pageSize: DESK_PAGE_SIZE }), `${status}|${assigned}|${q}|${page}`);
  const data = list.data?.data;
  const failure = list.error ? deskError(list.error) : null;

  return (
    <div className="space-y-5">
      <p className="text-sm text-text-muted">
        Every quote, newest first. The status is the one a buyer would see now: a sent quote past its date reads Expired.
        {canEdit && <> To start a quote from a request, open it in the <Link href="/quotes/rfqs" className="text-brand-accent underline">RFQ inbox</Link>.</>}
      </p>
      <form role="search" aria-label="Filter the quotes" className="grid items-end gap-3 sm:grid-cols-2 lg:grid-cols-4"
        onSubmit={(e) => { e.preventDefault(); setQ(search.trim()); setPage(1); }}>
        <div className="space-y-1.5">
          <label htmlFor="quote-status" className="text-sm font-medium">Status</label>
          <select id="quote-status" className={selectClass} value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }} data-testid="filter-status">
            {QUOTE_STATUS_FILTERS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
          </select>
        </div>
        <div className="space-y-1.5">
          <label htmlFor="quote-assigned" className="text-sm font-medium">Assigned to</label>
          <select id="quote-assigned" className={selectClass} value={assigned} onChange={(e) => { setAssigned(e.target.value); setPage(1); }} data-testid="filter-assigned">
            {ASSIGNED_FILTERS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
          </select>
        </div>
        <Input label="Quote number or company" type="search" value={search} onChange={(e) => setSearch(e.target.value)} maxLength={100} autoComplete="off" />
        <div><Button type="submit" variant="outline">Search</Button></div>
      </form>

      <FailureAlert failure={failure} onRetry={list.reload} />
      {list.loading && !data && <Loading />}
      {data && (
        <section aria-label="Quotes" className="space-y-3">
          {data.items.length === 0 ? (
            <p role="status" data-testid="quote-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">No quote matches these filters.</p>
          ) : (
            <div className="overflow-x-auto rounded-md border border-surface-border">
              <table className="w-full text-left text-sm" data-testid="quote-table">
                <caption className="sr-only">Quotes</caption>
                <thead className="bg-bg-subtle text-xs text-text-muted">
                  <tr>
                    <th scope="col" className="px-3 py-2">Quote</th>
                    <th scope="col" className="px-3 py-2">Company</th>
                    <th scope="col" className="px-3 py-2">Credit terms</th>
                    <th scope="col" className="px-3 py-2">Status</th>
                    <th scope="col" className="px-3 py-2">Version</th>
                    <th scope="col" className="px-3 py-2">Assigned to</th>
                    <th scope="col" className="px-3 py-2">Updated</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((qt: StaffQuoteSummary) => (
                    <tr key={qt.id} className="border-t border-surface-border align-top" data-testid="quote-row">
                      <td className="px-3 py-2">
                        <Link href={`/quotes/${qt.id}`} className="font-mono text-xs font-semibold text-brand-accent underline">{qt.number}</Link>
                        {qt.rfqId && qt.rfqNumber && <span className="block text-xs text-text-muted">From <Link href={`/quotes/rfqs/${qt.rfqId}`} className="underline">{qt.rfqNumber}</Link></span>}
                      </td>
                      <td className="px-3 py-2">{qt.companyName}<span className="block text-xs text-text-muted">{qt.contactName}</span></td>
                      <td className="px-3 py-2"><CreditCell terms={qt.creditTerms} onHold={qt.creditOnHold} /></td>
                      <td className="px-3 py-2"><QuoteStatusBadge status={qt.status} />{qt.closeReason && <span className="mt-1 block text-xs text-text-muted">{qt.closeReason}</span>}</td>
                      <td className="px-3 py-2">{qt.latestVersionNo}</td>
                      <td className="px-3 py-2">{assigneeText(qt.assigneeName, qt.assignedTo)}</td>
                      <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">{dhakaWhen(qt.updatedAt)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <Pager page={data.page} total={data.total} pageSize={data.pageSize} loading={list.loading} noun="quotes" onPage={setPage} onRefresh={list.reload} />
        </section>
      )}
    </div>
  );
}

export function QuotesListView() {
  const { user } = useAuth();
  const permissions = user?.permissions ?? [];
  return (
    <ConsoleShell activeItemId="quotes-list" title="Quotes">
      {canViewQuotes(permissions) ? <List canEdit={canEditQuotes(permissions)} /> : <NoAccess permission="quotes.view" />}
    </ConsoleShell>
  );
}
