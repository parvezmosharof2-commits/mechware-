'use client';

import * as React from 'react';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@mechware/ui/src/components/Dialog';
import { Input } from '@mechware/ui/src/components/Input';
import { Textarea } from '@mechware/ui/src/components/Textarea';
import {
  REFUND_STATUS_LABEL, approveOutcome, approveRefundHint, askableMinor, bdtToPoisha, canApproveRefund, canRequestRefund, canViewRefunds, formatBdt, isStaffStepUpFailure,
  refundReasonProblem, refundRequestProblems, refundStatus, rejectRefundHint, shortWhenUtc, sortRefundQueue, type RefundQueueItem, type StaffRefund
} from '@mechware/api-client';
import { staffOrderError, staffOrdersApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { FailureAlert, Loading, NoAccess, useFocusOn } from './CreditShared';
import { StepUpDialog } from './StepUpDialog';

type Flow =
  | { k: 'request'; item: RefundQueueItem }
  | { k: 'approve'; item: RefundQueueItem; refund: StaffRefund }
  | { k: 'reject'; item: RefundQueueItem; refund: StaffRefund }
  | null;

function Queue({ me }: { me: { id: string; permissions: readonly string[] } }) {
  const list = useResource(() => staffOrdersApi.refundQueue(), 'queue');
  const items = list.data ? sortRefundQueue(list.data.data) : [];
  const failure = list.error ? staffOrderError(list.error) : null;
  const canRequest = canRequestRefund(me.permissions);
  const canDecide = canApproveRefund(me.permissions);

  const [flow, setFlow] = React.useState<Flow>(null);
  const [busy, setBusy] = React.useState(false);
  const [codeError, setCodeError] = React.useState<string | null>(null);
  const [amount, setAmount] = React.useState('');
  const [reason, setReason] = React.useState('');
  const [problems, setProblems] = React.useState<Record<string, string>>({});
  const [notice, setNotice] = React.useState<{ text: string; problem: boolean } | null>(null);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const alertRef = useFocusOn(alertText);
  const noticeRef = React.useRef<HTMLDivElement>(null);
  React.useEffect(() => { if (notice) noticeRef.current?.focus(); }, [notice]);

  const close = () => { setFlow(null); setCodeError(null); setProblems({}); setAmount(''); setReason(''); };
  const begin = (f: NonNullable<Flow>) => { setNotice(null); setAlertText(null); setAmount(''); setReason(''); setProblems({}); setFlow(f); };
  const say = (n: { text: string; problem: boolean }) => setNotice(n);
  const refused = (err: unknown, keepCodeOpen: boolean) => {
    const f = staffOrderError(err);
    if (keepCodeOpen && isStaffStepUpFailure(f.code)) return setCodeError(f.text);
    close();
    setAlertText(f.text);
    if (f.reload || f.code === 'REFUND_FOUR_EYES') list.reload();
  };

  const doRequest = async () => {
    if (flow?.k !== 'request') return;
    const p = refundRequestProblems({ amountBdt: amount, reason }, askableMinor(flow.item));
    if (Object.keys(p).length > 0) return setProblems(p);
    setBusy(true);
    try {
      await staffOrdersApi.requestRefund(flow.item.paymentId, bdtToPoisha(amount) ?? 0, reason.trim());
      close();
      say({ text: `Refund of BDT ${amount.trim()} asked for on order ${flow.item.orderNumber}. A different approver must approve it before money moves.`, problem: false });
      list.reload();
    } catch (err) {
      const f = staffOrderError(err);
      if (f.status === 400 && Object.keys(f.fields).length > 0) setProblems(f.fields); else refused(err, false);
    } finally { setBusy(false); }
  };

  const doApprove = async (code: string) => {
    if (flow?.k !== 'approve') return;
    setBusy(true);
    setCodeError(null);
    try {
      const res = await staffOrdersApi.approveRefund(flow.refund.id, code);
      close();
      say(approveOutcome(res.data));
      list.reload();
    } catch (err) { refused(err, true); } finally { setBusy(false); }
  };

  const doReject = async () => {
    if (flow?.k !== 'reject') return;
    const pr = refundReasonProblem(reason);
    if (pr) return setProblems({ reason: pr });
    setBusy(true);
    try {
      await staffOrdersApi.rejectRefund(flow.refund.id, reason.trim());
      close();
      say({ text: 'Rejected. The amount can be asked for again.', problem: false });
      list.reload();
    } catch (err) { refused(err, false); } finally { setBusy(false); }
  };

  return (
    <div className="space-y-5">
      <p className="text-sm text-text-muted">
        Payments that hold money which must go back, for example after an order was cancelled. Money moves only after one person asks and a different person approves with an authenticator code.
      </p>
      {notice && (
        <div ref={noticeRef} tabIndex={-1} role={notice.problem ? 'alert' : 'status'} data-testid="refund-notice"
          className={`rounded-md border p-3 text-sm font-medium ${notice.problem ? 'border-fault bg-bg-subtle text-fault' : 'border-surface-border bg-bg-subtle'}`}>{notice.text}</div>
      )}
      {alertText && <div ref={alertRef} tabIndex={-1} role="alert" data-testid="refund-alert" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{alertText}</div>}
      <FailureAlert failure={failure} onRetry={list.reload} />
      {list.loading && !list.data && <Loading />}
      {list.data && items.length === 0 && (
        <p role="status" data-testid="refund-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">No payment is waiting for a refund.</p>
      )}
      {list.data && items.length > 0 && (
        <ul aria-label="Refund queue" className="space-y-4" data-testid="refund-queue">
          {items.map((item) => {
            const askable = askableMinor(item);
            return (
              <li key={item.paymentId} className="space-y-3 rounded-xl border border-surface-border bg-surface p-4" data-testid="refund-item">
                <div className="flex flex-wrap items-baseline justify-between gap-2">
                  <h2 className="font-display text-base font-bold">Order <span className="font-mono">{item.orderNumber}</span> <span className="text-sm font-normal text-text-muted">paid by {item.provider}</span></h2>
                  <span className="text-sm">Paid <span className="font-mono">BDT {formatBdt(item.paidMinor)}</span>, still free to ask for <span className="font-mono" data-testid="askable">BDT {formatBdt(askable)}</span></span>
                </div>
                {item.note && <p className="text-sm text-text-muted">{item.note}</p>}
                {item.refunds.length > 0 && (
                  <div className="overflow-x-auto rounded-md border border-surface-border">
                    <table className="w-full text-left text-sm">
                      <caption className="sr-only">Refunds asked for on order {item.orderNumber}</caption>
                      <thead className="bg-bg-subtle text-xs text-text-muted">
                        <tr>
                          <th scope="col" className="px-3 py-2 text-right">Amount (BDT)</th>
                          <th scope="col" className="px-3 py-2">Status</th>
                          <th scope="col" className="px-3 py-2">Reason</th>
                          <th scope="col" className="px-3 py-2">Asked</th>
                          {canDecide && <th scope="col" className="px-3 py-2">Action</th>}
                        </tr>
                      </thead>
                      <tbody>
                        {item.refunds.map((r) => {
                          const approve = approveRefundHint(r, me);
                          const reject = rejectRefundHint(r, me.permissions);
                          const open = ['requested', 'failed'].includes(refundStatus(r));
                          return (
                            <tr key={r.id} className="border-t border-surface-border align-top" data-testid="refund-row">
                              <td className="px-3 py-2 text-right font-mono">{formatBdt(r.amountMinor)}</td>
                              <td className="px-3 py-2"><Badge variant={refundStatus(r) === 'completed' ? 'success' : 'outline'}>{REFUND_STATUS_LABEL[refundStatus(r)]}</Badge>{r.note && <span className="mt-1 block text-xs text-text-muted">{r.note}</span>}</td>
                              <td className="px-3 py-2">{r.reason}</td>
                              <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">{shortWhenUtc(r.requestedAtUtc)}{r.requestedBy.toLowerCase() === me.id.toLowerCase() ? ' (by you)' : ''}</td>
                              {canDecide && (
                                <td className="px-3 py-2">
                                  {open ? (
                                    <span className="inline-flex flex-wrap items-center gap-2">
                                      <Button type="button" size="sm" disabled={busy || !approve.allowed} onClick={() => { begin({ k: 'approve', item, refund: r }); setCodeError(null); }} aria-label={`Approve the refund of BDT ${formatBdt(r.amountMinor)} on order ${item.orderNumber}`}>{refundStatus(r) === 'failed' ? 'Approve again' : 'Approve'}</Button>
                                      <Button type="button" size="sm" variant="outline" disabled={busy || !reject.allowed} onClick={() => begin({ k: 'reject', item, refund: r })} aria-label={`Reject the refund of BDT ${formatBdt(r.amountMinor)} on order ${item.orderNumber}`}>Reject</Button>
                                      {!approve.allowed && <span className="basis-full text-xs text-text-muted" data-testid="approve-hint">{approve.reason}</span>}
                                    </span>
                                  ) : null}
                                </td>
                              )}
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>
                )}
                {canRequest && askable > 0 && (
                  <Button type="button" variant="outline" size="sm" disabled={busy} onClick={() => begin({ k: 'request', item })} aria-label={`Ask for a refund on order ${item.orderNumber}`}>Ask for a refund</Button>
                )}
              </li>
            );
          })}
        </ul>
      )}
      <div><Button type="button" variant="ghost" size="sm" onClick={list.reload}>Refresh</Button></div>

      <Dialog open={flow?.k === 'request'} onOpenChange={(next) => { if (!next && !busy) close(); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Ask for a refund on order {flow?.k === 'request' ? flow.item.orderNumber : ''}</DialogTitle>
            <DialogDescription>
              Nothing is sent to the provider yet. A different person with refund approval rights must approve it, with an authenticator code.
              {flow?.k === 'request' ? ` At most BDT ${formatBdt(askableMinor(flow.item))} is still free to ask for.` : ''}
            </DialogDescription>
          </DialogHeader>
          <Input label="Amount (BDT)" value={amount} onChange={(e) => setAmount(e.target.value)} error={problems.amountBdt ?? problems.amountMinor} inputMode="decimal" autoComplete="off" />
          <Textarea label="Reason (5 to 500 characters)" value={reason} onChange={(e) => setReason(e.target.value)} error={problems.reason} maxLength={500} rows={3} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={close} disabled={busy}>Cancel</Button>
            <Button type="button" onClick={doRequest} isLoading={busy} disabled={busy}>Ask for the refund</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={flow?.k === 'reject'} onOpenChange={(next) => { if (!next && !busy) close(); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Reject this refund?</DialogTitle>
            <DialogDescription>No money moves and no authenticator code is needed. The amount becomes free to ask for again.</DialogDescription>
          </DialogHeader>
          <Textarea label="Why is it rejected? (5 to 500 characters)" value={reason} onChange={(e) => setReason(e.target.value)} error={problems.reason} maxLength={500} rows={3} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={close} disabled={busy}>Cancel</Button>
            <Button type="button" variant="destructive" onClick={doReject} isLoading={busy} disabled={busy}>Reject refund</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <StepUpDialog
        open={flow?.k === 'approve'}
        title="Approve the refund"
        description={flow?.k === 'approve' ? `BDT ${formatBdt(flow.refund.amountMinor)} goes back to the buyer of order ${flow.item.orderNumber} through ${flow.item.provider}. This cannot be undone.` : ''}
        confirmLabel="Approve and refund"
        busy={busy}
        error={codeError}
        onCancel={close}
        onSubmit={doApprove}
      />
    </div>
  );
}

export function RefundQueueView() {
  const { user } = useAuth();
  const permissions = user?.permissions ?? [];
  return (
    <ConsoleShell activeItemId="orders-refund" title="Refunds">
      {canViewRefunds(permissions) && user ? <Queue me={{ id: user.id, permissions }} /> : <NoAccess permission="orders.refund" />}
    </ConsoleShell>
  );
}
