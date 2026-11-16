'use client';

import * as React from 'react';
import Link from 'next/link';
import { Button, Skeleton } from '@mechware/ui';
import { PRICE_LIST_PAGE_SIZE, type PriceListSummary } from '@mechware/api-client';
import { priceListError, priceListsApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { fmtDate } from './PriceListsView';

function Queue() {
  const [page, setPage] = React.useState(1);
  const list = useResource(() => priceListsApi.list({ status: 'PendingApproval', page, pageSize: PRICE_LIST_PAGE_SIZE }), `pending|${page}`);
  const data = list.data?.data;
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  const failure = list.error ? priceListError(list.error) : null;
  // Newest submission first; the server lists newest first too, this keeps it right if that ever changes.
  const items: PriceListSummary[] = data ? [...data.items].sort((a, b) => (b.submittedAtUtc ?? b.createdAtUtc).localeCompare(a.submittedAtUtc ?? a.createdAtUtc)) : [];

  return (
    <div className="space-y-5">
      <p className="text-sm text-text-muted">Lists submitted by someone else and waiting for a decision. Open one to see who prepared it, its entries and its content hash.</p>
      {failure && (
        <div role="alert" className="flex flex-wrap items-center gap-3 rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">
          <span>{failure.text}</span>
          {failure.retry && <Button type="button" variant="outline" size="sm" onClick={list.reload}>Try again</Button>}
        </div>
      )}
      {list.loading && !data && <div role="status"><span className="sr-only">Loading</span><Skeleton className="h-40 w-full" /></div>}
      {data && (
        <section aria-label="Lists waiting for approval" className="space-y-3">
          {items.length === 0 ? (
            <p role="status" data-testid="queue-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">Nothing is waiting for approval.</p>
          ) : (
            <div className="overflow-x-auto rounded-md border border-surface-border">
              <table className="w-full text-left text-sm" data-testid="approval-queue">
                <caption className="sr-only">Price lists waiting for approval, newest first</caption>
                <thead className="bg-bg-subtle text-xs text-text-muted">
                  <tr>
                    <th scope="col" className="px-3 py-2">Name</th>
                    <th scope="col" className="px-3 py-2">For</th>
                    <th scope="col" className="px-3 py-2">Valid (UTC)</th>
                    <th scope="col" className="px-3 py-2 text-right">Entries</th>
                    <th scope="col" className="px-3 py-2 text-right">Version</th>
                    <th scope="col" className="px-3 py-2">Submitted</th>
                  </tr>
                </thead>
                <tbody>
                  {items.map((item) => (
                    <tr key={item.id} className="border-t border-surface-border align-top" data-testid="queue-row">
                      <td className="px-3 py-2"><Link className="font-semibold text-brand-accent underline" href={`/pricing/price-lists/${item.id}`}>{item.name}</Link></td>
                      <td className="px-3 py-2">{item.companyId ? <>{item.companyName ?? `Company ${item.companyId.slice(0, 8)}`}<br /><span className="font-mono text-xs text-text-muted">{item.contractRef}</span></> : <span className="text-text-muted">All customers</span>}</td>
                      <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">{fmtDate(item.validFromUtc)} to {fmtDate(item.validToUtc)}</td>
                      <td className="px-3 py-2 text-right font-mono">{item.entryCount}</td>
                      <td className="px-3 py-2 text-right font-mono">v{item.version}</td>
                      <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">{fmtDate(item.submittedAtUtc ?? item.createdAtUtc)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <div className="flex flex-wrap items-center gap-3 text-xs">
            <Button type="button" variant="outline" size="sm" disabled={page <= 1 || list.loading} onClick={() => setPage(page - 1)}>Previous</Button>
            <span>Page {data.page} of {pages} ({data.total} waiting)</span>
            <Button type="button" variant="outline" size="sm" disabled={page >= pages || list.loading} onClick={() => setPage(page + 1)}>Next</Button>
            <Button type="button" variant="ghost" size="sm" onClick={list.reload}>Refresh</Button>
          </div>
        </section>
      )}
    </div>
  );
}

export function PriceListApprovalsView() {
  const { user } = useAuth();
  const held = new Set((user?.permissions ?? []).map((p) => p.toLowerCase()));
  return (
    <ConsoleShell activeItemId="price-list-approvals" title="Pending approval">
      {held.has('pricelists.approve') ? (
        <Queue />
      ) : (
        <p role="alert" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">
          Your role does not allow this action (pricelists.approve).
        </p>
      )}
    </ConsoleShell>
  );
}
