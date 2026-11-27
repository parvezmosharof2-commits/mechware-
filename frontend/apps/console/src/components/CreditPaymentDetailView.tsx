'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import { Modal, ModalFooter } from './Modal';
import { Input } from '@mechware/ui/src/components/Input';
import { Skeleton } from '@mechware/ui/src/components/Skeleton';
import { Textarea } from '@mechware/ui/src/components/Textarea';
import {
  CREDIT_STATUS_LABEL, bounceHint, canViewCredit, clearedOnProblem, creditReasonProblem, dhakaToday, flagsOf, formatBdt, isCreditStepUpFailure, methodLabel, rejectHint,
  sameId, shortWhen, verifyHint, type CreditLedgerLine, type CreditPaymentDetail
} from '@mechware/api-client';
import { creditError, creditPaymentsApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { StepUpDialog } from './StepUpDialog';

type Flow =
  | { k: 'verify'; clearedOn: string }
  | { k: 'verify-code'; clearedOn: string }
  | { k: 'reject' }
  | { k: 'bounce' }
  | { k: 'bounce-code'; reason: string }
  | null;

const LINE_KIND: Record<string, string> = {
  payment_applied: 'Applied to an order', payment_on_account: 'Kept on account', payment_reversed: 'Reversed (bounce)',
  on_account_applied: 'On-account money applied', on_account_reversed: 'On-account money taken back'
};
const kindText = (k: string) => LINE_KIND[k] ?? k.replace(/_/g, ' ');
const signed = (n: number) => (n === 0 ? '0.00' : `${n > 0 ? '+' : ''}${formatBdt(n)}`);

function Lines({ lines }: { lines: CreditLedgerLine[] }) {
  if (lines.length === 0) return <p className="text-sm text-text-muted" data-testid="no-lines">No ledger line was written for this payment. Only a verification writes lines.</p>;
  return (
    <div className="overflow-x-auto rounded-md border border-surface-border">
      <table className="w-full text-left text-sm" data-testid="ledger-lines">
        <caption className="sr-only">Ledger lines written for this payment</caption>
        <thead className="bg-bg-subtle text-xs text-text-muted">
          <tr>
            <th scope="col" className="px-3 py-2">No.</th>
            <th scope="col" className="px-3 py-2">Day</th>
            <th scope="col" className="px-3 py-2">What happened</th>
            <th scope="col" className="px-3 py-2">Order</th>
            <th scope="col" className="px-3 py-2 text-right">Owed (BDT)</th>
            <th scope="col" className="px-3 py-2 text-right">On account (BDT)</th>
          </tr>
        </thead>
        <tbody>
          {lines.map((l) => (
            <tr key={l.seq} className="border-t border-surface-border align-top">
              <td className="px-3 py-2 font-mono text-xs">{l.seq}</td>
              <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">{l.occurredOn}</td>
              <td className="px-3 py-2">{kindText(l.kind)}</td>
              <td className="px-3 py-2 font-mono text-xs">{l.orderNumber ?? ''}</td>
              <td className="px-3 py-2 text-right font-mono">{signed(l.receivableDeltaMinor)}</td>
              <td className="px-3 py-2 text-right font-mono">{signed(l.onAccountDeltaMinor)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Detail({ detail, me, onChanged }: { detail: CreditPaymentDetail; me: { id: string; permissions: readonly string[] }; onChanged: () => void }) {
  const p = detail.payment;
  const today = dhakaToday();
  const [flow, setFlow] = React.useState<Flow>(null);
  const [busy, setBusy] = React.useState(false);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const [codeError, setCodeError] = React.useState<string | null>(null);
  const [reason, setReason] = React.useState('');
  const [problem, setProblem] = React.useState<string | null>(null);
  const alertRef = React.useRef<HTMLDivElement>(null);

  React.useEffect(() => { if (alertText) alertRef.current?.focus(); }, [alertText]);

  const verify = verifyHint(detail, me, today);
  const reject = rejectHint(detail, me);
  const bounce = bounceHint(detail, me);
  const shown = p.status === 'recorded' ? [verify, reject] : p.status === 'verified' ? [bounce] : [];
  const hint = shown.find((h) => !h.allowed)?.reason;

  const close = () => { setFlow(null); setCodeError(null); setProblem(null); setReason(''); };

  /** A refusal: a wrong code keeps the code dialog open; everything else closes it, says it in words and reloads what may be stale. */
  const failed = (err: unknown, keepCodeOpen: boolean) => {
    const f = creditError(err);
    if (keepCodeOpen && isCreditStepUpFailure(f.code)) return setCodeError(f.text);
    close();
    setAlertText(f.text);
    if (f.reload || f.code === 'CREDIT_PAYMENT_FOUR_EYES') onChanged();
  };
  const succeeded = (text: string) => { close(); setAlertText(null); setNotice(text); onChanged(); };

  const doVerify = async (clearedOn: string, code: string) => {
    setBusy(true);
    setCodeError(null);
    try {
      await creditPaymentsApi.verify(p.id, clearedOn, code);
      succeeded(`Verified. BDT ${formatBdt(p.amountMinor)} now counts for ${detail.companyName}.`);
    } catch (err) { failed(err, true); } finally { setBusy(false); }
  };
  const doReject = async () => {
    const pr = creditReasonProblem(reason);
    if (pr) return setProblem(pr);
    setBusy(true);
    try {
      await creditPaymentsApi.reject(p.id, reason.trim());
      succeeded('Rejected. The reference is free again and the payment can be recorded correctly.');
    } catch (err) { failed(err, false); } finally { setBusy(false); }
  };
  const doBounce = async (why: string, code: string) => {
    setBusy(true);
    setCodeError(null);
    try {
      await creditPaymentsApi.bounce(p.id, why, code);
      succeeded('Bounced. The debt is back on the orders this payment had paid.');
    } catch (err) { failed(err, true); } finally { setBusy(false); }
  };

  const openVerify = () => {
    setAlertText(null); setNotice(null); setProblem(null);
    setFlow({ k: 'verify', clearedOn: today });
  };
  const toVerifyCode = (clearedOn: string) => {
    // The day is checked here, BEFORE a code is asked for: a code is spent even when the call is then refused.
    const pr = clearedOnProblem(p, clearedOn, today);
    if (pr) return setProblem(pr);
    setProblem(null); setCodeError(null);
    setFlow({ k: 'verify-code', clearedOn });
  };
  const toBounceCode = () => {
    const pr = creditReasonProblem(reason);
    if (pr) return setProblem(pr);
    setProblem(null); setCodeError(null);
    setFlow({ k: 'bounce-code', reason: reason.trim() });
  };

  const rows: Array<[string, React.ReactNode]> = [
    ['Company', detail.companyName],
    ['Method', methodLabel(p.method)],
    ['Reference', <span key="r" className="font-mono">{p.reference}</span>],
    ['Amount', <span key="a" className="font-mono">BDT {formatBdt(p.amountMinor)}</span>],
    ['Received on', <span key="d" className="font-mono">{p.receivedOn}</span>],
    ...(p.chequeDate ? [['Date on the cheque', <span key="c" className="font-mono">{p.chequeDate}</span>] as [string, React.ReactNode]] : []),
    ...(p.drawerBank ? [['Bank', p.drawerBank] as [string, React.ReactNode]] : []),
    ...(p.lateEntryReason ? [['Late-entry reason', p.lateEntryReason] as [string, React.ReactNode]] : []),
    ...(p.note ? [['Staff note', p.note] as [string, React.ReactNode]] : []),
    ['recorded', `${shortWhen(p.recordedAtUtc)}${sameId(p.recordedBy, me.id) ? ' (by you)' : ''}`],
    ...(p.verifiedAtUtc ? [['verified', `${shortWhen(p.verifiedAtUtc)}${p.clearedOn ? `, cleared ${p.clearedOn}` : ''}${sameId(p.verifiedBy, me.id) ? ' (by you)' : ''}`] as [string, React.ReactNode]] : []),
    ...(p.rejectedAtUtc ? [['rejected', `${shortWhen(p.rejectedAtUtc)}: ${p.decisionReason ?? ''}`] as [string, React.ReactNode]] : []),
    ...(p.bouncedAtUtc ? [['bounced', `${shortWhen(p.bouncedAtUtc)}: ${p.decisionReason ?? ''}`] as [string, React.ReactNode]] : [])
  ];

  return (
    <section aria-label="Payment" className="space-y-5 rounded-xl border border-surface-border bg-surface p-4 sm:p-6" data-testid="payment-detail">
      <div className="flex flex-wrap items-center gap-2">
        <Badge variant={p.status === 'verified' ? 'success' : 'outline'} data-testid="status-badge">{CREDIT_STATUS_LABEL[p.status]}</Badge>
        {flagsOf(detail).map((f) => <Badge key={f} variant="outline">{f}</Badge>)}
      </div>
      <dl className="grid grid-cols-1 gap-x-6 gap-y-1 text-sm sm:grid-cols-2">
        {rows.map(([k, v]) => <div key={k}><dt className="inline text-text-muted">{k}: </dt><dd className="inline" data-testid={`row-${k.toLowerCase().replace(/[^a-z]+/g, '-')}`}>{v}</dd></div>)}
      </dl>

      {notice && <div role="status" data-testid="decision-notice" className="rounded-md border border-surface-border bg-bg-subtle p-3 text-sm font-medium">{notice}</div>}
      {alertText && <div ref={alertRef} tabIndex={-1} role="alert" data-testid="decision-alert" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{alertText}</div>}

      {shown.length > 0 && (
        <div className="space-y-2">
          <div className="flex flex-wrap gap-3">
            {p.status === 'recorded' && (
              <>
                <Button type="button" onClick={openVerify} disabled={busy || !verify.allowed} aria-describedby={hint ? 'action-hint' : undefined}>Verify</Button>
                <Button type="button" variant="outline" onClick={() => { setAlertText(null); setNotice(null); setReason(''); setProblem(null); setFlow({ k: 'reject' }); }} disabled={busy || !reject.allowed} aria-describedby={hint ? 'action-hint' : undefined}>Reject</Button>
              </>
            )}
            {p.status === 'verified' && (
              <Button type="button" variant="destructive" onClick={() => { setAlertText(null); setNotice(null); setReason(''); setProblem(null); setFlow({ k: 'bounce' }); }} disabled={busy || !bounce.allowed} aria-describedby={hint ? 'action-hint' : undefined}>Bounce</Button>
            )}
          </div>
          {hint && <p id="action-hint" data-testid="action-hint" className="text-sm font-medium text-text-muted">{hint}</p>}
        </div>
      )}

      <div className="space-y-2">
        <h2 className="font-display text-lg font-bold">Ledger lines</h2>
        <Lines lines={detail.lines} />
      </div>

      <Modal open={flow?.k === 'verify'} onOpenChange={(next) => { if (!next && !busy) close(); }} title={<>Verify this payment?</>} description={<>You confirm that BDT {formatBdt(p.amountMinor)} from {detail.companyName} is in the bank. It then counts against their credit, oldest invoices first. You will be asked for your authenticator code next.</>}>
          <Input
            label="Day the money cleared"
            type="date"
            value={flow?.k === 'verify' ? flow.clearedOn : ''}
            max={today}
            onChange={(e) => { setProblem(null); if (flow?.k === 'verify') setFlow({ k: 'verify', clearedOn: e.target.value }); }}
            error={problem ?? undefined}
          />
          <ModalFooter>
            <Button type="button" variant="outline" onClick={close}>Cancel</Button>
            <Button type="button" data-testid="verify-continue" onClick={() => flow?.k === 'verify' && toVerifyCode(flow.clearedOn)}>Continue to the authenticator code</Button>
          </ModalFooter>
      </Modal>

      <StepUpDialog
        open={flow?.k === 'verify-code'}
        title="Confirm the verification"
        description={`Verifying BDT ${formatBdt(p.amountMinor)} from ${detail.companyName}, cleared ${flow?.k === 'verify-code' ? flow.clearedOn : ''}.`}
        confirmLabel="Verify"
        busy={busy}
        error={codeError}
        onCancel={close}
        onSubmit={async (code) => { if (flow?.k === 'verify-code') await doVerify(flow.clearedOn, code); }}
      />

      <Modal open={flow?.k === 'reject'} onOpenChange={(next) => { if (!next && !busy) close(); }} title={<>Reject this payment?</>} description={<>Use this for a mistaken entry. Nothing was released, so no authenticator code is needed. The reference becomes free to record again.</>}>
          <Textarea label="Reason (5 to 500 characters)" value={reason} onChange={(e) => setReason(e.target.value)} error={problem ?? undefined} maxLength={500} rows={3} />
          <ModalFooter>
            <Button type="button" variant="outline" onClick={close} disabled={busy}>Cancel</Button>
            <Button type="button" onClick={doReject} isLoading={busy} disabled={busy}>Reject payment</Button>
          </ModalFooter>
      </Modal>

      <Modal open={flow?.k === 'bounce'} onOpenChange={(next) => { if (!next && !busy) close(); }} title={<>Bounce this payment?</>} description={<>The bank returned the money. The debt goes back on the orders this payment paid, and the company may go over its limit or on hold. You will be asked for your authenticator code next.</>}>
          <Textarea label="Why did it bounce? (5 to 500 characters)" value={reason} onChange={(e) => setReason(e.target.value)} error={problem ?? undefined} maxLength={500} rows={3} />
          <ModalFooter>
            <Button type="button" variant="outline" onClick={close}>Cancel</Button>
            <Button type="button" variant="destructive" data-testid="bounce-continue" onClick={toBounceCode}>Continue to the authenticator code</Button>
          </ModalFooter>
      </Modal>

      <StepUpDialog
        open={flow?.k === 'bounce-code'}
        title="Confirm the bounce"
        description={`The bank returned BDT ${formatBdt(p.amountMinor)} (${p.reference}).`}
        confirmLabel="Bounce"
        busy={busy}
        error={codeError}
        onCancel={close}
        onSubmit={async (code) => { if (flow?.k === 'bounce-code') await doBounce(flow.reason, code); }}
      />
    </section>
  );
}

export function CreditPaymentDetailView({ id }: { id: string }) {
  const { user } = useAuth();
  const permissions = user?.permissions ?? [];
  const allowed = canViewCredit(permissions);
  const res = useResource(() => creditPaymentsApi.get(id), id, allowed);
  const failure = res.error ? creditError(res.error) : null;
  return (
    <ConsoleShell activeItemId="credit-payments" title="Credit payment">
      <Link href="/credit/payments" className="text-sm font-semibold text-brand-accent underline">Back to the queue</Link>
      {!allowed ? (
        <p role="alert" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">Your role does not allow this action (credit.view).</p>
      ) : (
        <>
          {failure && (
            <div role="alert" className="flex flex-wrap items-center gap-3 rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">
              <span>{failure.text}</span>
              {failure.retry && <Button type="button" variant="outline" size="sm" onClick={res.reload}>Try again</Button>}
            </div>
          )}
          {res.loading && !res.data && <div role="status"><span className="sr-only">Loading</span><Skeleton className="h-40 w-full" /></div>}
          {res.data && user && <Detail detail={res.data.data} me={{ id: user.id, permissions }} onChanged={res.reload} />}
        </>
      )}
    </ConsoleShell>
  );
}
