'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import {
  OVERDUE_PAGE_SIZE, cancelHint, canCancelOrders, canViewStaffOrders, overdueReasons,
  lateByText, shortWhenUtc, staffOrderStatusLabel, type OverdueOrder
} from '@mechware/api-client';
import { staffOrderError, staffOrdersApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { FailureAlert, Loading, NoAccess } from './CreditShared';
import { useOrderCancel } from './OrderCancelFlow';
import { OrdersNav } from './OrdersNav';

function List({ permissions }: { permissions: readonly string[] }) {
  const [page, setPage] = React.useState(1);
  const list = useResource(() => staffOrdersApi.overdue({ page, pageSize: OVERDUE_PAGE_SIZE }), String(page));
  const data = list.data?.data;
  const pages = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1;
  const failure = list.error ? staffOrderError(list.error) : null;
  const cancel = useOrderCancel(permissions, list.reload);
  const canCancel = canCancelOrders(permissions);
  const busy = cancel.busy;
  const open = (o: OverdueOrder) => cancel.open({ orderId: o.orderId, orderNumber: o.orderNumber });

  return (
    <div className="space-y-5">
      <OrdersNav current="overdue" />
      <p className="text-sm text-text-muted">
        Orders that are past the legal delivery deadline and not delivered, or past the courier handover deadline and not shipped, most overdue first.
        Open an order number for its full detail.
      </p>

      {cancel.banner}
      <FailureAlert failure={failure} onRetry={list.reload} />
      {list.loading && !data && <Loading />}
      {data && (
        <section aria-label="Overdue orders" className="space-y-3">
          {data.items.length === 0 ? (
            <p role="status" data-testid="overdue-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">No order is past a legal deadline right now.</p>
          ) : (
            <div className="overflow-x-auto rounded-md border border-surface-border">
              <table className="w-full text-left text-sm" data-testid="overdue-table">
                <caption className="sr-only">Orders past a legal deadline, most overdue first</caption>
                <thead className="bg-bg-subtle text-xs text-text-muted">
                  <tr>
                    <th scope="col" className="px-3 py-2">Order</th>
                    <th scope="col" className="px-3 py-2">Status</th>
                    <th scope="col" className="px-3 py-2">District</th>
                    <th scope="col" className="px-3 py-2">What is late</th>
                    <th scope="col" className="px-3 py-2">Late by</th>
                    <th scope="col" className="px-3 py-2">Deadlines</th>
                    {canCancel && <th scope="col" className="px-3 py-2">Action</th>}
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((o) => {
                    const hint = cancelHint(o, permissions);
                    return (
                      <tr key={o.orderId} className="border-t border-surface-border align-top" data-testid="overdue-row">
                        <td className="px-3 py-2"><Link href={`/orders/${o.orderId}`} className="font-mono text-xs font-semibold text-brand-accent underline">{o.orderNumber}</Link></td>
                        <td className="px-3 py-2">{staffOrderStatusLabel(o.status)}</td>
                        <td className="px-3 py-2">{o.district ?? ''}</td>
                        <td className="px-3 py-2"><span className="inline-flex flex-col gap-1">{overdueReasons(o).map((t) => <Badge key={t} variant="outline">{t}</Badge>)}</span></td>
                        <td className="whitespace-nowrap px-3 py-2">{lateByText(o.hoursOverdue)}</td>
                        <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">
                          delivery {shortWhenUtc(o.legalDeliveryDeadlineUtc)}<br />handover {shortWhenUtc(o.courierHandoverDeadlineUtc)}
                        </td>
                        {canCancel && (
                          <td className="px-3 py-2">
                            {hint.allowed ? (
                              <Button type="button" variant="destructive" size="sm" onClick={() => open(o)} disabled={busy} aria-label={`Cancel order ${o.orderNumber}`}>Cancel order</Button>
                            ) : (
                              <span className="text-xs text-text-muted" data-testid="cancel-hint">{hint.reason}</span>
                            )}
                          </td>
                        )}
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
          <div className="flex flex-wrap items-center gap-3 text-xs">
            <Button type="button" variant="outline" size="sm" disabled={page <= 1 || list.loading} onClick={() => setPage(page - 1)}>Previous</Button>
            <span aria-live="polite">Page {data.page} of {pages} ({data.totalCount} orders)</span>
            <Button type="button" variant="outline" size="sm" disabled={page >= pages || list.loading} onClick={() => setPage(page + 1)}>Next</Button>
            <Button type="button" variant="ghost" size="sm" onClick={list.reload}>Refresh</Button>
          </div>
        </section>
      )}

      {cancel.dialogs}
    </div>
  );
}

export function OverdueOrdersView() {
  const { user } = useAuth();
  const permissions = user?.permissions ?? [];
  return (
    <ConsoleShell activeItemId="orders-overdue" title="Overdue orders">
      {canViewStaffOrders(permissions) ? <List permissions={permissions} /> : <NoAccess permission="orders.view_all" />}
    </ConsoleShell>
  );
}
