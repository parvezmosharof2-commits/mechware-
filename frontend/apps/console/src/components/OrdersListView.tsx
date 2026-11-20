'use client';

import * as React from 'react';
import Link from 'next/link';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import {
  EMPTY_ORDER_FILTERS, ORDER_STATUS_FILTERS, OVERDUE_FILTERS, PAYMENT_STATE_FILTERS, activeOrderFilterCount, canViewStaffOrders, dhakaWhen, formatBdt, listOverdueWords,
  orderFilterProblem, orderFiltersToSearch, orderStatusText, parseOrderFilters, paymentStateText, type OrderFilters, type StaffOrderListItem
} from '@mechware/api-client';
import { staffOrderError, staffOrdersApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { FailureAlert, Loading, NoAccess } from './CreditShared';
import { OrdersNav } from './OrdersNav';
import { Pager } from './QuoteDeskShared';

// The company list (and its search) is fetched only when a person asks to filter by company.
const CompanyPicker = React.lazy(() => import('./CompanyPicker').then((m) => ({ default: m.CompanyPicker })));
const selectClass = 'block h-11 w-full rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm';

function Select({ id, label, value, onChange, options }: { id: string; label: string; value: string; onChange: (v: string) => void; options: ReadonlyArray<{ value: string; label: string }> }) {
  return (
    <div className="space-y-1.5">
      <label htmlFor={id} className="text-sm font-medium">{label}</label>
      <select id={id} className={selectClass} value={value} onChange={(e) => onChange(e.target.value)} data-testid={`filter-${id}`}>
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
  const applied = React.useMemo(() => parseOrderFilters((k) => new URLSearchParams(search).get(k)), [search]);
  const [draft, setDraft] = React.useState<OrderFilters>(applied);
  const [problem, setProblem] = React.useState<string | null>(null);
  const [pickCompany, setPickCompany] = React.useState(false);
  // Back, forward, a shared link or a page change: the form shows what the URL says.
  React.useEffect(() => { setDraft(applied); setProblem(null); }, [applied]);

  const go = (f: OrderFilters) => { const q = orderFiltersToSearch(f); router.push(q ? `${pathname}?${q}` : pathname); };
  const set = (patch: Partial<OrderFilters>) => setDraft((d) => ({ ...d, ...patch }));
  const apply = (e: React.FormEvent) => {
    e.preventDefault();
    const found = orderFilterProblem(draft);
    setProblem(found);
    if (!found) go({ ...draft, orderNumber: draft.orderNumber.trim(), page: 1 });
  };

  const list = useResource(() => staffOrdersApi.orders(applied), search);
  const data = list.data?.data;
  const failure = list.error ? staffOrderError(list.error) : null;
  const count = activeOrderFilterCount(applied);

  return (
    <div className="space-y-5">
      <OrdersNav current="all" />
      <p className="text-sm text-text-muted">Every order, newest first. Dates are Dhaka days, both ends included. The filters are in the address, so you can share or reload this page.</p>
      <form role="search" aria-label="Filter the orders" onSubmit={apply} className="space-y-3" noValidate>
        <div className="grid items-end gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <Select id="status" label="Status" value={draft.status} onChange={(v) => set({ status: v })} options={[{ value: '', label: 'Any status' }, ...ORDER_STATUS_FILTERS]} />
          <Select id="payment" label="Payment state" value={draft.paymentState} onChange={(v) => set({ paymentState: v })} options={[{ value: '', label: 'Any payment state' }, ...PAYMENT_STATE_FILTERS]} />
          <Select id="overdue" label="Overdue" value={draft.overdue} onChange={(v) => set({ overdue: v })} options={OVERDUE_FILTERS} />
          <Input label="Order number starts with" value={draft.orderNumber} onChange={(e) => set({ orderNumber: e.target.value })} maxLength={40} autoComplete="off" spellCheck={false} />
          <Input label="Placed from (Dhaka day)" type="date" value={draft.from} onChange={(e) => set({ from: e.target.value })} />
          <Input label="Placed to (Dhaka day)" type="date" value={draft.to} onChange={(e) => set({ to: e.target.value })} />
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
          <Button type="button" variant="outline" disabled={count === 0 && activeOrderFilterCount(draft) === 0} onClick={() => { setPickCompany(false); setDraft(EMPTY_ORDER_FILTERS); go(EMPTY_ORDER_FILTERS); }}>Clear all filters</Button>
          <span className="text-xs text-text-muted" data-testid="filter-count">{count === 0 ? 'No filter applied' : `${count} ${count === 1 ? 'filter' : 'filters'} applied`}</span>
        </div>
      </form>

      <FailureAlert failure={failure} onRetry={list.reload} />
      {list.loading && !data && <Loading />}
      {data && (
        <section aria-label="Orders" className="space-y-3">
          {data.items.length === 0 ? (
            <p role="status" data-testid="orders-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">
              {count === 0 ? 'There are no orders yet.' : 'No order matches these filters. Change or clear a filter.'}
            </p>
          ) : (
            <div className="overflow-x-auto rounded-md border border-surface-border">
              <table className="w-full text-left text-sm" data-testid="orders-table">
                <caption className="sr-only">Orders, newest first. Page {data.page}, {data.totalCount} orders match.</caption>
                <thead className="bg-bg-subtle text-xs text-text-muted">
                  <tr>
                    <th scope="col" className="px-3 py-2">Order</th>
                    <th scope="col" className="px-3 py-2">Company</th>
                    <th scope="col" className="px-3 py-2">Status</th>
                    <th scope="col" className="px-3 py-2">Payment</th>
                    <th scope="col" className="px-3 py-2 text-right">Total (BDT)</th>
                    <th scope="col" className="px-3 py-2">Placed</th>
                    <th scope="col" className="px-3 py-2">Late</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((o: StaffOrderListItem) => (
                    <tr key={o.id} className="border-t border-surface-border align-top" data-testid="order-row">
                      <td className="px-3 py-2">
                        <Link href={`/orders/${o.id}`} className="font-mono text-xs font-semibold text-brand-accent underline">{o.orderNumber}</Link>
                        {o.source === 'quote' && <span className="block text-xs text-text-muted">From quote {o.quoteNumber ?? ''}</span>}
                      </td>
                      <td className="px-3 py-2">{o.companyName}</td>
                      <td className="px-3 py-2">{orderStatusText(o.status)}</td>
                      <td className="px-3 py-2">{paymentStateText(o.paymentState)}</td>
                      <td className="whitespace-nowrap px-3 py-2 text-right font-mono text-xs">{formatBdt(o.grandTotalMinor)}</td>
                      <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">{dhakaWhen(o.placedAtUtc)}</td>
                      <td className="px-3 py-2"><span className="inline-flex flex-col gap-1">{listOverdueWords(o).map((t) => <Badge key={t} variant="outline">{t}</Badge>)}</span></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <Pager page={data.page} total={data.totalCount} pageSize={data.pageSize} loading={list.loading} noun="orders" onPage={(p) => go({ ...applied, page: p })} onRefresh={list.reload} />
        </section>
      )}
    </div>
  );
}

export function OrdersListView() {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="orders-all" title="All orders">
      {canViewStaffOrders(user?.permissions ?? []) ? <React.Suspense fallback={<Loading />}><List /></React.Suspense> : <NoAccess permission="orders.view_all" />}
    </ConsoleShell>
  );
}
