'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@mechware/ui/src/components/Dialog';
import { Textarea } from '@mechware/ui/src/components/Textarea';
import {
  OVERDUE_PAGE_SIZE, cancelHint, cancelOutcome, cancelReasonProblem, canCancelOrders, canViewRefunds, canViewStaffOrders, isStaffStepUpFailure, overdueReasons,
  lateByText, shortWhenUtc, staffOrderStatusLabel, type CancelOutcome, type OverdueOrder
} from '@mechware/api-client';
import { staffOrderError, staffOrdersApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { FailureAlert, Loading, NoAccess, useFocusOn } from './CreditShared';
import { StepUpDialog } from './StepUpDialog';

type Flow = { k: 'reason'; order: OverdueOrder } | { k: 'code'; order: OverdueOrder; reason: string } | null;
interface Done { outcome: CancelOutcome }

function List({ permissions }: { permissions: readonly string[] }) {
  const [page, setPage] = React.useState(1);
  const list = useResource(() => staffOrdersApi.overdue({ page, pageSize: OVERDUE_PAGE_SIZE }), String(page));
  const data = list.data?.data;
  const pages = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1;
  const failure = list.error ? staffOrderError(list.error) : null;

  const [flow, setFlow] = React.useState<Flow>(null);
  const [reason, setReason] = React.useState('');
  const [problem, setProblem] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [codeError, setCodeError] = React.useState<string | null>(null);
  const [done, setDone] = React.useState<Done | null>(null);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const alertRef = useFocusOn(alertText);
  const noticeRef = React.useRef<HTMLDivElement>(null);
  React.useEffect(() => { if (done) noticeRef.current?.focus(); }, [done]);
  const canCancel = canCancelOrders(permissions);
  const canRefunds = canViewRefunds(permissions);

  const close = () => { setFlow(null); setCodeError(null); setProblem(null); setReason(''); };
  const open = (order: OverdueOrder) => { setDone(null); setAlertText(null); setReason(''); setProblem(null); setFlow({ k: 'reason', order }); };

  const toCode = () => {
    if (flow?.k !== 'reason') return;
    // Checked here, BEFORE a code is asked for: a code is spent even when the call is then refused.
    const pr = cancelReasonProblem(reason);
    if (pr) return setProblem(pr);
    setProblem(null); setCodeError(null);
    setFlow({ k: 'code', order: flow.order, reason: reason.trim() });
  };

  const submit = async (code: string) => {
    if (flow?.k !== 'code') return;
    setBusy(true);
    setCodeError(null);
    try {
      const res = await staffOrdersApi.cancel(flow.order.orderId, flow.reason, code);
      close();
      setDone({ outcome: cancelOutcome(res.data) });
      list.reload();
    } catch (err) {
      const f = staffOrderError(err);
      if (isStaffStepUpFailure(f.code)) { setCodeError(f.text); return; }
      close();
      setAlertText(f.text);
      if (f.reload) list.reload();
    } finally { setBusy(false); }
  };

  return (
    <div className="space-y-5">
      <p className="text-sm text-text-muted">
        Orders that are past the legal delivery deadline and not delivered, or past the courier handover deadline and not shipped, most overdue first.
        There is no screen yet to search every order: that needs a staff order list on the server.
      </p>

      {done && (
        <div ref={noticeRef} tabIndex={-1} role="status" data-testid="cancel-notice" className="space-y-1 rounded-md border border-surface-border bg-bg-subtle p-3 text-sm font-medium">
          {done.outcome.lines.map((l) => <p key={l}>{l}</p>)}
          {done.outcome.warnings.map((w) => <p key={w} role="alert" data-testid="cancel-warning" className="text-fault">{w}</p>)}
          {done.outcome.needsRefund && canRefunds && <p><Link href="/refunds" className="text-brand-accent underline">Open the refund queue</Link></p>}
        </div>
      )}
      {alertText && <div ref={alertRef} tabIndex={-1} role="alert" data-testid="cancel-alert" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{alertText}</div>}
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
                        <td className="px-3 py-2 font-mono text-xs font-semibold">{o.orderNumber}</td>
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

      <Dialog open={flow?.k === 'reason'} onOpenChange={(next) => { if (!next && !busy) close(); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Cancel order {flow?.order.orderNumber}?</DialogTitle>
            <DialogDescription>
              Its stock goes back to the warehouses it came from, a credit order&apos;s charge is reduced (money already paid goes on account), and money paid through a
              gateway waits in the refund queue. The buyer is not charged a cancellation fee. This cannot be undone. You will be asked for your authenticator code next.
            </DialogDescription>
          </DialogHeader>
          <Textarea label="Why is it cancelled? (5 to 500 characters)" value={reason} onChange={(e) => setReason(e.target.value)} error={problem ?? undefined} maxLength={500} rows={3} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={close}>Keep the order</Button>
            <Button type="button" variant="destructive" data-testid="cancel-continue" onClick={toCode}>Continue to the authenticator code</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <StepUpDialog
        open={flow?.k === 'code'}
        title="Confirm the cancellation"
        description={flow?.k === 'code' ? `Cancelling order ${flow.order.orderNumber}: ${flow.reason}` : ''}
        confirmLabel="Cancel the order"
        busy={busy}
        error={codeError}
        onCancel={close}
        onSubmit={submit}
      />
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
