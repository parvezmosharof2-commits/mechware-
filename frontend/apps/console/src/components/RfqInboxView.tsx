'use client';

import * as React from 'react';
import Link from 'next/link';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import {
  ASSIGNED_FILTERS, DESK_PAGE_SIZE, RFQ_STATUS_FILTERS, assigneeText, canViewQuotes, dhakaWhen, rfqSourceLabel, type StaffRfqSummary
} from '@mechware/api-client';
import { deskError, staffQuotesApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { FailureAlert, Loading, NoAccess } from './CreditShared';
import { Pager, RfqStatusBadge, SlaBadge } from './QuoteDeskShared';

const selectClass = 'block h-11 w-full rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm';

function Inbox() {
  // The default view is what the person has to answer: open requests assigned to them.
  const [status, setStatus] = React.useState('open');
  const [assigned, setAssigned] = React.useState('me');
  const [search, setSearch] = React.useState('');
  const [q, setQ] = React.useState('');
  const [page, setPage] = React.useState(1);
  const list = useResource(() => staffQuotesApi.rfqs({ status, assigned, q, page, pageSize: DESK_PAGE_SIZE }), `${status}|${assigned}|${q}|${page}`);
  const data = list.data?.data;
  const failure = list.error ? deskError(list.error) : null;
  const filtered = status !== 'open' || assigned !== 'me' || q !== '';

  return (
    <div className="space-y-5">
      <p className="text-sm text-text-muted">
        Quote requests from companies. Open requests are listed oldest first, so the one that has waited longest is on top. The clock counts working time only
        (Sunday to Thursday, 09:00 to 18:00 Dhaka) and is shown, not enforced.
      </p>

      <form role="search" aria-label="Filter the requests" className="grid items-end gap-3 sm:grid-cols-2 lg:grid-cols-4"
        onSubmit={(e) => { e.preventDefault(); setQ(search.trim()); setPage(1); }}>
        <div className="space-y-1.5">
          <label htmlFor="rfq-status" className="text-sm font-medium">Status</label>
          <select id="rfq-status" className={selectClass} value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }} data-testid="filter-status">
            {RFQ_STATUS_FILTERS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
          </select>
        </div>
        <div className="space-y-1.5">
          <label htmlFor="rfq-assigned" className="text-sm font-medium">Assigned to</label>
          <select id="rfq-assigned" className={selectClass} value={assigned} onChange={(e) => { setAssigned(e.target.value); setPage(1); }} data-testid="filter-assigned">
            {ASSIGNED_FILTERS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
          </select>
        </div>
        <Input label="Request number or company" type="search" value={search} onChange={(e) => setSearch(e.target.value)} maxLength={100} autoComplete="off" />
        <div className="flex gap-2">
          <Button type="submit" variant="outline">Search</Button>
          {filtered && (
            <Button type="button" variant="ghost" onClick={() => { setStatus('open'); setAssigned('me'); setSearch(''); setQ(''); setPage(1); }}>Back to my open requests</Button>
          )}
        </div>
      </form>

      <FailureAlert failure={failure} onRetry={list.reload} />
      {list.loading && !data && <Loading />}
      {data && (
        <section aria-label="Quote requests" className="space-y-3">
          {data.items.length === 0 ? (
            <p role="status" data-testid="rfq-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">
              {assigned === 'me' && status === 'open' && !q
                ? 'No open request is assigned to you. Choose "Not assigned" to pick up new ones.'
                : 'No request matches these filters.'}
            </p>
          ) : (
            <div className="overflow-x-auto rounded-md border border-surface-border">
              <table className="w-full text-left text-sm" data-testid="rfq-table">
                <caption className="sr-only">Quote requests</caption>
                <thead className="bg-bg-subtle text-xs text-text-muted">
                  <tr>
                    <th scope="col" className="px-3 py-2">Request</th>
                    <th scope="col" className="px-3 py-2">Company</th>
                    <th scope="col" className="px-3 py-2">Lines</th>
                    <th scope="col" className="px-3 py-2">Status</th>
                    <th scope="col" className="px-3 py-2">Assigned to</th>
                    <th scope="col" className="px-3 py-2">First-quote clock</th>
                    <th scope="col" className="px-3 py-2">Received</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((r: StaffRfqSummary) => (
                    <tr key={r.id} className="border-t border-surface-border align-top" data-testid="rfq-row">
                      <td className="px-3 py-2">
                        <Link href={`/quotes/rfqs/${r.id}`} className="font-mono text-xs font-semibold text-brand-accent underline">{r.number}</Link>
                        <span className="block text-xs text-text-muted">{rfqSourceLabel(r.source)}</span>
                      </td>
                      <td className="px-3 py-2">{r.companyName}<span className="block text-xs text-text-muted">{r.requesterName}</span></td>
                      <td className="px-3 py-2">{r.lineCount}</td>
                      <td className="px-3 py-2"><RfqStatusBadge status={r.status} /></td>
                      <td className="px-3 py-2" data-testid="rfq-assignee">{assigneeText(r.assigneeName, r.assignedTo)}</td>
                      <td className="px-3 py-2"><SlaBadge rfq={r} /></td>
                      <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">{dhakaWhen(r.createdAt)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <Pager page={data.page} total={data.total} pageSize={data.pageSize} loading={list.loading} noun="requests" onPage={setPage} onRefresh={list.reload} />
        </section>
      )}
    </div>
  );
}

export function RfqInboxView() {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="quotes-rfqs" title="RFQ inbox">
      {canViewQuotes(user?.permissions ?? []) ? <Inbox /> : <NoAccess permission="quotes.view" />}
    </ConsoleShell>
  );
}
