'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge, Button, Input, Skeleton } from '@mechware/ui';
import { PRICE_LIST_PAGE_SIZE, displayStateLabel, statusLabel, type PriceListSummary } from '@mechware/api-client';
import { priceListError, priceListsApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';

export const fmtDate = (iso: string | null | undefined) => (iso ? iso.slice(0, 10) : '');

export function StateBadges({ status, displayState }: { status: string; displayState: string }) {
  const variant = displayState === 'Active' ? 'success' : displayState === 'PendingApproval' || displayState === 'Scheduled' ? 'low-stock' : displayState === 'Draft' ? 'outline' : 'default';
  return (
    <span className="inline-flex flex-wrap items-center gap-1">
      <Badge variant="outline" data-testid="status-badge">{statusLabel(status)}</Badge>
      {displayState !== status && <Badge variant={variant} data-testid="display-state-badge">{displayStateLabel(displayState)}</Badge>}
    </span>
  );
}

const SELECT = 'block w-full rounded-md border border-surface-border bg-surface p-2 text-sm';

function Row({ item }: { item: PriceListSummary }) {
  const company = item.companyName ?? `Company ${(item.companyId ?? '').slice(0, 8)}`;
  return (
    <tr className="border-t border-surface-border align-top">
      <td className="px-3 py-2"><Link className="font-semibold text-brand-accent underline" href={`/pricing/price-lists/${item.id}`}>{item.name}</Link></td>
      <td className="px-3 py-2">{item.scope}</td>
      <td className="px-3 py-2">{item.companyId ? <>{company}<br /><span className="font-mono text-xs text-text-muted">{item.contractRef}</span></> : <span className="text-text-muted">All customers</span>}</td>
      <td className="px-3 py-2"><StateBadges status={item.status} displayState={item.displayState} /></td>
      <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">{fmtDate(item.validFromUtc)} to {fmtDate(item.validToUtc)}</td>
      <td className="px-3 py-2 text-right font-mono">{item.entryCount}</td>
      <td className="px-3 py-2 text-right font-mono">v{item.version}</td>
      <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">{fmtDate(item.submittedAtUtc ?? item.createdAtUtc)}</td>
    </tr>
  );
}

function PriceLists({ canEdit }: { canEdit: boolean }) {
  const [status, setStatus] = React.useState('');
  const [scope, setScope] = React.useState('');
  const [q, setQ] = React.useState('');
  const [applied, setApplied] = React.useState({ status: '', scope: '', q: '' });
  const [page, setPage] = React.useState(1);
  const list = useResource(() => priceListsApi.list({ ...applied, page, pageSize: PRICE_LIST_PAGE_SIZE }), `${applied.status}|${applied.scope}|${applied.q}|${page}`);

  const apply = (e: React.FormEvent) => {
    e.preventDefault();
    setPage(1);
    setApplied({ status, scope, q });
  };
  const data = list.data?.data;
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  const failure = list.error ? priceListError(list.error) : null;

  return (
    <div className="space-y-5">
      <form onSubmit={apply} className="grid grid-cols-1 items-end gap-3 rounded-xl border border-surface-border bg-surface p-4 sm:grid-cols-2 lg:grid-cols-5" aria-label="Filters">
        <div>
          <label htmlFor="f-status" className="mb-1 block text-xs font-semibold text-text-muted">Status</label>
          <select id="f-status" className={SELECT} value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">All</option>
            <option value="Draft">Draft</option>
            <option value="PendingApproval">Pending approval</option>
            <option value="Approved">Approved</option>
            <option value="Withdrawn">Withdrawn</option>
          </select>
        </div>
        <div>
          <label htmlFor="f-scope" className="mb-1 block text-xs font-semibold text-text-muted">Scope</label>
          <select id="f-scope" className={SELECT} value={scope} onChange={(e) => setScope(e.target.value)}>
            <option value="">All</option>
            <option value="General">General</option>
            <option value="Company">Company</option>
          </select>
        </div>
        <div className="sm:col-span-2 lg:col-span-1">
          <Input label="Search" type="search" placeholder="Name or contract reference" value={q} onChange={(e) => setQ(e.target.value)} autoComplete="off" />
        </div>
        <div className="flex gap-2 lg:col-span-2">
          <Button type="submit">Apply filters</Button>
          {canEdit && <Link href="/pricing/price-lists/new" className="inline-flex items-center rounded-md border border-surface-border px-4 py-2 text-sm font-semibold text-brand-accent hover:bg-bg-subtle">New price list</Link>}
        </div>
      </form>

      {failure && (
        <div role="alert" className="flex flex-wrap items-center gap-3 rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">
          <span>{failure.text}</span>
          {failure.retry && <Button type="button" variant="outline" size="sm" onClick={list.reload}>Try again</Button>}
        </div>
      )}
      {list.loading && !data && <div role="status"><span className="sr-only">Loading</span><Skeleton className="h-40 w-full" /></div>}

      {data && (
        <section aria-label="Price lists" className="space-y-3">
          {data.items.length === 0 ? (
            <p role="status" data-testid="empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">
              No price lists match. {canEdit ? 'Create one with "New price list".' : ''}
            </p>
          ) : (
            <div className="overflow-x-auto rounded-md border border-surface-border">
              <table className="w-full text-left text-sm" data-testid="price-list-table">
                <caption className="sr-only">Price lists, newest first</caption>
                <thead className="bg-bg-subtle text-xs text-text-muted">
                  <tr>
                    <th scope="col" className="px-3 py-2">Name</th>
                    <th scope="col" className="px-3 py-2">Scope</th>
                    <th scope="col" className="px-3 py-2">Company / contract</th>
                    <th scope="col" className="px-3 py-2">Status</th>
                    <th scope="col" className="px-3 py-2">Valid (UTC)</th>
                    <th scope="col" className="px-3 py-2 text-right">Entries</th>
                    <th scope="col" className="px-3 py-2 text-right">Version</th>
                    <th scope="col" className="px-3 py-2">Updated</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((item) => <Row key={item.id} item={item} />)}
                </tbody>
              </table>
            </div>
          )}
          <div className="flex flex-wrap items-center gap-3 text-xs">
            <Button type="button" variant="outline" size="sm" disabled={page <= 1 || list.loading} onClick={() => setPage(page - 1)}>Previous</Button>
            <span>Page {data.page} of {pages} ({data.total} lists)</span>
            <Button type="button" variant="outline" size="sm" disabled={page >= pages || list.loading} onClick={() => setPage(page + 1)}>Next</Button>
          </div>
        </section>
      )}
    </div>
  );
}

export function PriceListsView() {
  const { user } = useAuth();
  const held = new Set((user?.permissions ?? []).map((p) => p.toLowerCase()));
  return (
    <ConsoleShell activeItemId="price-lists" title="Price lists">
      {held.has('pricelists.view') ? (
        <PriceLists canEdit={held.has('pricelists.edit')} />
      ) : (
        <p role="alert" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">
          Your role does not allow this action (pricelists.view).
        </p>
      )}
    </ConsoleShell>
  );
}
