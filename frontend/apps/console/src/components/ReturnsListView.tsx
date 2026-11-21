'use client';

import * as React from 'react';
import Link from 'next/link';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import {
  EMPTY_RETURN_FILTERS, RETURN_STATUSES, canViewStaffOrders, dhakaWhen, parseReturnFilters, returnFiltersToSearch, staffReturnStatusText, type StaffReturn, type StaffReturnFilters
} from '@mechware/api-client';
import { staffReturnError, staffReturnsApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { FailureAlert, Loading, NoAccess } from './CreditShared';
import { Pager } from './QuoteDeskShared';

// The company list (and its search) is fetched only when a person asks to filter by company.
const CompanyPicker = React.lazy(() => import('./CompanyPicker').then((m) => ({ default: m.CompanyPicker })));

function List() {
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const search = params.toString();
  const applied = React.useMemo(() => parseReturnFilters((k) => new URLSearchParams(search).get(k)), [search]);
  const [draft, setDraft] = React.useState<StaffReturnFilters>(applied);
  const [pickCompany, setPickCompany] = React.useState(false);
  React.useEffect(() => setDraft(applied), [applied]);

  const go = (f: StaffReturnFilters) => { const q = returnFiltersToSearch(f); router.push(q ? `${pathname}?${q}` : pathname); };
  const set = (patch: Partial<StaffReturnFilters>) => setDraft((d) => ({ ...d, ...patch }));
  const list = useResource(() => staffReturnsApi.list(applied), search);
  const data = list.data?.data;
  const failure = list.error ? staffReturnError(list.error) : null;
  const count = [applied.status, applied.orderNumber, applied.companyId].filter(Boolean).length;

  return (
    <div className="space-y-5">
      <p className="text-sm text-text-muted">Return requests, newest first. The filters are in the address, so you can share or reload this page.</p>
      <form role="search" aria-label="Filter the returns" onSubmit={(e) => { e.preventDefault(); go({ ...draft, orderNumber: draft.orderNumber.trim(), page: 1 }); }} className="space-y-3">
        <div className="grid items-end gap-3 sm:grid-cols-2 lg:grid-cols-3">
          <div className="space-y-1.5">
            <label htmlFor="status" className="text-sm font-medium">Status</label>
            <select id="status" data-testid="filter-status" className="block h-11 w-full rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm" value={draft.status} onChange={(e) => set({ status: e.target.value })}>
              <option value="">Any status</option>
              {RETURN_STATUSES.map((s) => <option key={s} value={s}>{staffReturnStatusText(s)}</option>)}
            </select>
          </div>
          <Input label="Order number starts with" value={draft.orderNumber} onChange={(e) => set({ orderNumber: e.target.value })} maxLength={16} autoComplete="off" spellCheck={false} data-testid="filter-order" />
          <div className="space-y-1.5">
            <span className="block text-sm font-medium">Company</span>
            {draft.companyId ? (
              <p className="flex flex-wrap items-center gap-2 text-sm" data-testid="company-chosen">
                <Badge variant="outline">{draft.companyName || draft.companyId}</Badge>
                <Button type="button" variant="ghost" size="sm" onClick={() => set({ companyId: '', companyName: '' })}>Remove the company filter</Button>
              </p>
            ) : pickCompany ? null : <Button type="button" variant="outline" onClick={() => setPickCompany(true)}>Choose a company</Button>}
          </div>
        </div>
        {pickCompany && !draft.companyId && (
          <React.Suspense fallback={<p role="status" className="text-xs text-text-muted">Loading companies...</p>}>
            <CompanyPicker value="" onChange={(id, name) => { set({ companyId: id, companyName: name }); setPickCompany(false); }} />
          </React.Suspense>
        )}
        <div className="flex flex-wrap items-center gap-3">
          <Button type="submit">Apply filters</Button>
          <Button type="button" variant="outline" onClick={() => { setPickCompany(false); setDraft(EMPTY_RETURN_FILTERS); go(EMPTY_RETURN_FILTERS); }}>Clear all filters</Button>
          <span className="text-xs text-text-muted" data-testid="filter-count">{count === 0 ? 'No filter applied' : `${count} ${count === 1 ? 'filter' : 'filters'} applied`}</span>
        </div>
      </form>

      <FailureAlert failure={failure} onRetry={list.reload} />
      {list.loading && !data && <Loading />}
      {data && (
        <section aria-label="Returns" className="space-y-3">
          {data.items.length === 0 ? (
            <p role="status" data-testid="returns-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">
              {count === 0 ? 'There are no return requests yet.' : 'No return matches these filters. Change or clear a filter.'}
            </p>
          ) : (
            <div className="overflow-x-auto rounded-md border border-surface-border" role="region" tabIndex={0} aria-label="Returns table">
              <table className="w-full text-left text-sm" data-testid="returns-table">
                <caption className="sr-only">Returns, newest first. Page {data.page}, {data.totalCount} returns match.</caption>
                <thead className="bg-bg-subtle text-xs text-text-muted">
                  <tr>
                    <th scope="col" className="px-3 py-2">Order</th><th scope="col" className="px-3 py-2">Status</th><th scope="col" className="px-3 py-2">Company</th>
                    <th scope="col" className="px-3 py-2 text-right">Units</th><th scope="col" className="px-3 py-2">Requested</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((r: StaffReturn) => (
                    <tr key={r.id} className="border-t border-surface-border align-top" data-testid="return-row">
                      <td className="px-3 py-2"><Link href={`/returns/${r.id}`} className="font-mono text-xs font-semibold text-brand-accent underline">{r.orderNumber}</Link></td>
                      <td className="px-3 py-2">{staffReturnStatusText(r.status)}{r.restockPending && <Badge variant="outline" className="ml-2">Restock pending</Badge>}</td>
                      <td className="px-3 py-2 font-mono text-xs">{r.companyId ? r.companyId.slice(0, 8) : 'Personal'}</td>
                      <td className="px-3 py-2 text-right">{r.lines.reduce((n, l) => n + l.quantity, 0)}</td>
                      <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">{dhakaWhen(r.requestedAtUtc)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <Pager page={data.page} total={data.totalCount} pageSize={data.pageSize} loading={list.loading} noun="returns" onPage={(p) => go({ ...applied, page: p })} onRefresh={list.reload} />
        </section>
      )}
    </div>
  );
}

export function ReturnsListView() {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="returns" title="Returns">
      {canViewStaffOrders(user?.permissions ?? []) ? <React.Suspense fallback={<Loading />}><List /></React.Suspense> : <NoAccess permission="orders.view_all" />}
    </ConsoleShell>
  );
}
