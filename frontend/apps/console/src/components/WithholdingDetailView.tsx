'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import {
  canViewInvoices, dhakaWhen, formatBdt, isWithholdingStepUp, shouldReloadWithholding, verifyWithholdingText, withholdingDecision, withholdingKindText, withholdingStatusText,
  type TaxWithholding
} from '@mechware/api-client';
import { staffWithholdingsApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { withholdingFailure } from '../lib/withholdingFailure';
import { BackLink, FailureAlert, FocusAlert, Loading, NoAccess, Notice, useFocusOn } from './CreditShared';
import { ReasonDialog } from './ReasonDialog';
import { StepUpDialog } from './StepUpDialog';

function Decision({ id, me }: { id: string; me: { id: string; permissions: readonly string[] } }) {
  const res = useResource(() => staffWithholdingsApi.get(id), id);
  const [override, setOverride] = React.useState<TaxWithholding | null>(null);
  React.useEffect(() => { setOverride(null); }, [res.data]);
  const w = override ?? res.data;
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const [note, setNote] = React.useState<string | null>(null);
  const [rejecting, setRejecting] = React.useState(false);
  const [verifying, setVerifying] = React.useState(false);
  const [codeError, setCodeError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const inFlight = React.useRef(false);
  const alertRef = useFocusOn(alertText);
  const failure = res.error ? { text: withholdingFailure(res.error).text, retry: true } : null;

  const run = async (work: () => Promise<void>, stepUp: boolean) => {
    if (inFlight.current) return;
    inFlight.current = true; setBusy(true); setAlertText(null); setCodeError(null);
    try { await work(); } catch (err) {
      const f = withholdingFailure(err);
      if (stepUp && isWithholdingStepUp(f.code)) setCodeError(f.text);
      else { setVerifying(false); setRejecting(false); setAlertText(f.text); if (shouldReloadWithholding(f.code)) res.reload(); }
    } finally { inFlight.current = false; setBusy(false); }
  };
  const verify = (code: string) => run(async () => { setOverride(await staffWithholdingsApi.verify(id, code)); setVerifying(false); setNote('The certificate was verified and the amount was taken off what the company owes on credit.'); }, true);
  const reject = (reason: string) => run(async () => { setOverride(await staffWithholdingsApi.reject(id, reason)); setRejecting(false); setNote('The certificate was rejected. Nothing was taken off the account.'); }, false);

  if (failure) return <><BackLink href="/invoices/withholdings">Back to the certificates</BackLink><FailureAlert failure={failure} onRetry={res.reload} /></>;
  if (!w) return <Loading />;
  const d = withholdingDecision(w, me);
  return (
    <div className="space-y-6">
      <BackLink href="/invoices/withholdings">Back to the certificates</BackLink>
      {alertText && <FocusAlert ref={alertRef} testId="wh-alert">{alertText}</FocusAlert>}
      {note && <Notice testId="wh-note">{note}</Notice>}
      <section aria-labelledby="wh-h" className="space-y-3 rounded-xl border border-surface-border bg-surface p-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 id="wh-h" className="font-display text-lg font-bold">{withholdingKindText(w.kind)} certificate <span className="font-mono">{w.certificateNumber}</span></h2>
          <Badge variant="outline" data-testid="wh-state">{withholdingStatusText(w.status)}</Badge>
        </div>
        <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-3">
          <div><dt className="text-xs text-text-muted">Invoice</dt><dd><Link className="font-mono text-brand-accent underline" href={`/invoices/${w.invoiceId}`}>{w.invoiceNumber}</Link></dd></div>
          <div><dt className="text-xs text-text-muted">Order</dt><dd><Link className="text-brand-accent underline" href={`/orders/${w.orderId}`}>Open the order</Link></dd></div>
          <div><dt className="text-xs text-text-muted">Company</dt><dd><Link className="text-brand-accent underline" href={`/credit/companies/${w.companyId}`}>{w.companyName}</Link></dd></div>
          <div><dt className="text-xs text-text-muted">Certificate date</dt><dd className="font-mono text-xs">{w.certificateDate}</dd></div>
          <div><dt className="text-xs text-text-muted">Amount (BDT)</dt><dd className="font-mono" data-testid="wh-amount">{formatBdt(w.amountMinor)}</dd></div>
          <div><dt className="text-xs text-text-muted">Recorded</dt><dd className="font-mono text-xs">{dhakaWhen(w.recordedAtUtc)}{w.recordedBy.toLowerCase() === me.id.toLowerCase() ? ' (by you)' : ''}</dd></div>
          {w.decidedAtUtc && <div><dt className="text-xs text-text-muted">Decided</dt><dd className="font-mono text-xs">{dhakaWhen(w.decidedAtUtc)}</dd></div>}
          {w.decisionReason && <div className="sm:col-span-3"><dt className="text-xs text-text-muted">Decision reason</dt><dd className="break-words">{w.decisionReason}</dd></div>}
        </dl>
      </section>
      {w.status === 'recorded' && (
        <section aria-labelledby="wh-d" className="space-y-3 rounded-xl border border-surface-border bg-surface p-4" data-testid="wh-decision">
          <h2 id="wh-d" className="font-display text-base font-bold">Your decision</h2>
          <div className="flex flex-wrap items-center gap-3">
            <Button type="button" onClick={() => { setCodeError(null); setVerifying(true); }} disabled={busy || !d.canVerify} aria-describedby={d.hint ? 'wh-hint' : undefined} data-testid="wh-verify">Verify the certificate</Button>
            <Button type="button" variant="outline" onClick={() => setRejecting(true)} disabled={busy || !d.canReject} data-testid="wh-reject">Reject</Button>
          </div>
          {d.hint && <p id="wh-hint" data-testid="wh-hint" className="text-sm font-medium text-text-muted">{d.hint}</p>}
          <p className="text-xs text-text-muted">Verifying asks for the 6-digit code of your authenticator app. Rejecting asks for a reason.</p>
        </section>
      )}
      {rejecting && <React.Suspense fallback={null}><ReasonDialog open title="Reject this certificate?" label="Reason (5 to 500 characters)" confirmLabel="Reject" busy={busy}
        description="Nothing is taken off the account. The certificate number can be recorded again later."
        onCancel={() => { if (!busy) setRejecting(false); }} onSubmit={(text) => void reject(text)} /></React.Suspense>}
      {verifying && <React.Suspense fallback={null}><StepUpDialog open title="Confirm the verification" description={verifyWithholdingText(w)} confirmLabel="Verify" busy={busy} error={codeError}
        onCancel={() => { if (!busy) { setVerifying(false); setCodeError(null); } }} onSubmit={verify} /></React.Suspense>}
    </div>
  );
}

export function WithholdingDetailView({ id }: { id: string }) {
  const { user } = useAuth();
  const permissions = user?.permissions ?? [];
  return (
    <ConsoleShell activeItemId="withholdings" title="Withholding certificate">
      {canViewInvoices(permissions) && user ? <Decision id={id} me={{ id: user.id, permissions }} /> : <NoAccess permission="invoices.view" />}
    </ConsoleShell>
  );
}
