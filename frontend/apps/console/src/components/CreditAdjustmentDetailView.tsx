'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@mechware/ui/src/components/Dialog';
import { Textarea } from '@mechware/ui/src/components/Textarea';
import {
  ADJUSTMENT_STATUS_LABEL, adjustmentSummary, approveAdjustmentHint, approvedText, canViewCredit, creditReasonProblem, formatBdt, isCreditStepUpFailure, kindLabel,
  ledgerKindText, rejectAdjustmentHint, sameId, shortWhen, signedBdt, type CreditAdjustmentDetail
} from '@mechware/api-client';
import { creditError } from '../lib/api';
import { creditBooksApi } from '../lib/creditBooks';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { BackLink, FailureAlert, FocusAlert, Loading, NoAccess, Notice, useFocusOn } from './CreditShared';
import { StepUpDialog } from './StepUpDialog';

type Flow = 'approve' | 'reject' | null;

function Detail({ detail, me, onChanged }: { detail: CreditAdjustmentDetail; me: { id: string; permissions: readonly string[] }; onChanged: () => void }) {
  const a = detail.adjustment;
  const [flow, setFlow] = React.useState<Flow>(null);
  const [busy, setBusy] = React.useState(false);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const [codeError, setCodeError] = React.useState<string | null>(null);
  const [reason, setReason] = React.useState('');
  const [problem, setProblem] = React.useState<string | null>(null);
  const alertRef = useFocusOn(alertText);

  const approve = approveAdjustmentHint(a, me);
  const reject = rejectAdjustmentHint(a, me);
  const hint = a.status === 'requested' ? [approve, reject].find((h) => !h.allowed)?.reason : undefined;
  const close = () => { setFlow(null); setCodeError(null); setProblem(null); setReason(''); };

  const doApprove = async (code: string) => {
    setBusy(true);
    setCodeError(null);
    try {
      const res = await creditBooksApi.approveAdjustment(a.id, code);
      close(); setAlertText(null); setNotice(approvedText(res.data));
      onChanged();
    } catch (err) {
      const f = creditError(err);
      if (isCreditStepUpFailure(f.code)) return setCodeError(f.text);
      close(); setNotice(null); setAlertText(f.text);
      if (f.reload || f.code === 'CREDIT_ADJUSTMENT_FOUR_EYES') onChanged();
    } finally { setBusy(false); }
  };

  const doReject = async () => {
    const pr = creditReasonProblem(reason);
    if (pr) return setProblem(pr);
    setBusy(true);
    try {
      await creditBooksApi.rejectAdjustment(a.id, reason.trim());
      close(); setAlertText(null); setNotice(reject.withdraw ? 'Withdrawn. Nothing moved.' : 'Rejected. Nothing moved.');
      onChanged();
    } catch (err) {
      const f = creditError(err);
      close(); setNotice(null); setAlertText(f.text);
      if (f.reload) onChanged();
    } finally { setBusy(false); }
  };

  const rows: Array<[string, React.ReactNode]> = [
    ['Company', <Link key="c" className="font-semibold text-brand-accent underline" href={`/credit/companies/${a.companyId}`}>{detail.companyName}</Link>],
    ['Kind', kindLabel(a.kind)],
    ['Amount', <span key="a" className="font-mono">{a.amountMinor < 0 ? '-' : ''}BDT {formatBdt(Math.abs(a.amountMinor))}</span>],
    ...(a.payoutReference ? [['Transfer reference', <span key="p" className="font-mono">{a.payoutReference}</span>] as [string, React.ReactNode]] : []),
    ['Reason', a.reason],
    ['requested', `${shortWhen(a.requestedAtUtc)}${sameId(a.requestedBy, me.id) ? ' (by you)' : ''}`],
    ...(a.approvedAtUtc ? [['approved', `${shortWhen(a.approvedAtUtc)}${sameId(a.approvedBy, me.id) ? ' (by you)' : ''}`] as [string, React.ReactNode]] : []),
    ...(a.rejectedAtUtc ? [['rejected', `${shortWhen(a.rejectedAtUtc)}: ${a.decisionReason ?? ''}`] as [string, React.ReactNode]] : [])
  ];

  return (
    <section aria-label="Adjustment" className="space-y-5 rounded-xl border border-surface-border bg-surface p-4 sm:p-6" data-testid="adjustment-detail">
      <div className="flex flex-wrap items-center gap-2">
        <Badge variant={a.status === 'approved' ? 'success' : 'outline'} data-testid="adjust-status-badge">{ADJUSTMENT_STATUS_LABEL[a.status]}</Badge>
        <span className="text-sm font-semibold">{adjustmentSummary(a)}</span>
      </div>
      <dl className="grid grid-cols-1 gap-x-6 gap-y-1 text-sm sm:grid-cols-2">
        {rows.map(([k, v]) => <div key={k}><dt className="inline text-text-muted">{k}: </dt><dd className="inline" data-testid={`arow-${k.toLowerCase().replace(/[^a-z]+/g, '-')}`}>{v}</dd></div>)}
      </dl>

      {notice && <Notice testId="adjust-decision-notice">{notice}</Notice>}
      {alertText && <FocusAlert ref={alertRef} testId="adjust-decision-alert">{alertText}</FocusAlert>}

      {a.status === 'requested' && (
        <div className="space-y-2">
          <div className="flex flex-wrap gap-3">
            <Button type="button" onClick={() => { setAlertText(null); setNotice(null); setCodeError(null); setFlow('approve'); }} disabled={busy || !approve.allowed} aria-describedby={hint ? 'adjust-hint' : undefined}>Approve</Button>
            <Button type="button" variant="outline" onClick={() => { setAlertText(null); setNotice(null); setReason(''); setProblem(null); setFlow('reject'); }} disabled={busy || !reject.allowed} aria-describedby={hint ? 'adjust-hint' : undefined}>
              {reject.withdraw ? 'Withdraw' : 'Reject'}
            </Button>
          </div>
          {hint && <p id="adjust-hint" data-testid="adjust-hint" className="text-sm font-medium text-text-muted">{hint}</p>}
          <p className="text-xs text-text-muted">The server also refuses an approval by whoever recorded or verified the payment this adjustment changes.</p>
        </div>
      )}

      <div className="space-y-2">
        <h2 className="font-display text-lg font-bold">Ledger lines</h2>
        {detail.lines.length === 0 ? (
          <p className="text-sm text-text-muted" data-testid="adjust-no-lines">No ledger line was written. Only an approval writes one.</p>
        ) : (
          <div className="overflow-x-auto rounded-md border border-surface-border">
            <table className="w-full text-left text-sm" data-testid="adjust-lines">
              <caption className="sr-only">Ledger lines written by this adjustment</caption>
              <thead className="bg-bg-subtle text-xs text-text-muted">
                <tr>
                  <th scope="col" className="px-3 py-2">No.</th>
                  <th scope="col" className="px-3 py-2">Day</th>
                  <th scope="col" className="px-3 py-2">What happened</th>
                  <th scope="col" className="px-3 py-2 text-right">Owed (BDT)</th>
                  <th scope="col" className="px-3 py-2 text-right">On account (BDT)</th>
                </tr>
              </thead>
              <tbody>
                {detail.lines.map((l) => (
                  <tr key={l.seq} className="border-t border-surface-border align-top">
                    <td className="px-3 py-2 font-mono text-xs">{l.seq}</td>
                    <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">{l.occurredOn}</td>
                    <td className="px-3 py-2">{ledgerKindText(l.kind)}</td>
                    <td className="px-3 py-2 text-right font-mono">{signedBdt(l.receivableDeltaMinor)}</td>
                    <td className="px-3 py-2 text-right font-mono">{signedBdt(l.onAccountDeltaMinor)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <StepUpDialog
        open={flow === 'approve'}
        title="Approve this adjustment"
        description={`${adjustmentSummary(a)} for ${detail.companyName}. Reason: ${a.reason}. Approving changes the company's books for good.`}
        confirmLabel="Approve"
        busy={busy}
        error={codeError}
        onCancel={close}
        onSubmit={doApprove}
      />

      <Dialog open={flow === 'reject'} onOpenChange={(next) => { if (!next && !busy) close(); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{reject.withdraw ? 'Withdraw this request?' : 'Reject this adjustment?'}</DialogTitle>
            <DialogDescription>Nothing moves and no authenticator code is needed. The reason is kept with the request.</DialogDescription>
          </DialogHeader>
          <Textarea label="Reason (5 to 500 characters)" value={reason} onChange={(e) => setReason(e.target.value)} error={problem ?? undefined} maxLength={500} rows={3} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={close} disabled={busy}>Cancel</Button>
            <Button type="button" onClick={doReject} isLoading={busy} disabled={busy}>{reject.withdraw ? 'Withdraw request' : 'Reject adjustment'}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </section>
  );
}

export function CreditAdjustmentDetailView({ id }: { id: string }) {
  const { user } = useAuth();
  const permissions = user?.permissions ?? [];
  const allowed = canViewCredit(permissions);
  const res = useResource(() => creditBooksApi.adjustment(id), id, allowed);
  return (
    <ConsoleShell activeItemId="credit-adjustments" title="Credit adjustment">
      <BackLink href="/credit/adjustments">Back to the adjustments</BackLink>
      {!allowed ? (
        <NoAccess permission="credit.view" />
      ) : (
        <>
          <FailureAlert failure={res.error ? creditError(res.error) : null} onRetry={res.reload} />
          {res.loading && !res.data && <Loading />}
          {res.data && user && <Detail detail={res.data.data} me={{ id: user.id, permissions }} onChanged={res.reload} />}
        </>
      )}
    </ConsoleShell>
  );
}
