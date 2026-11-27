'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import {
  canViewInvoices, creditApprovalText, creditDecision, creditReasonText, creditStatusText, dhakaWhen, replacementApprovalText, formatBdt, isCreditNoteStepUp, shouldReloadCreditNote,
  type CreditNoteApproval, type CreditNoteRequest
} from '@mechware/api-client';
import { staffCreditNotesApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { creditNoteFailure } from './CreditNoteRequestButton';
import { BackLink, FailureAlert, FocusAlert, Loading, NoAccess, Notice, useFocusOn } from './CreditShared';
import { ReasonDialog } from './ReasonDialog';
import { StepUpDialog } from './StepUpDialog';

type Action = 'approve' | 'reject';

function Decision({ id, me }: { id: string; me: { id: string; permissions: readonly string[] } }) {
  const res = useResource(() => staffCreditNotesApi.get(id), id);
  const [override, setOverride] = React.useState<CreditNoteRequest | null>(null);
  React.useEffect(() => { setOverride(null); }, [res.data]);
  const req = override ?? res.data;
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const [asking, setAsking] = React.useState(false);
  const [codeFor, setCodeFor] = React.useState<{ action: Action; reason: string } | null>(null);
  const [codeError, setCodeError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [issued, setIssued] = React.useState<CreditNoteApproval | null>(null);
  const [note, setNote] = React.useState<string | null>(null);
  const inFlight = React.useRef(false);
  const alertRef = useFocusOn(alertText);
  const failure = res.error ? { text: creditNoteFailure(res.error).text, retry: true } : null;

  const run = async (work: () => Promise<void>, stepUp: boolean) => {
    if (inFlight.current) return;
    inFlight.current = true; setBusy(true); setAlertText(null); setCodeError(null);
    try { await work(); } catch (err) {
      const f = creditNoteFailure(err);
      if (stepUp && isCreditNoteStepUp(f.code)) setCodeError(f.text);
      else { setCodeFor(null); setAlertText(f.text); if (shouldReloadCreditNote(f.code)) res.reload(); }
    } finally { inFlight.current = false; setBusy(false); }
  };
  const submitCode = (code: string) => run(async () => {
    if (!codeFor) return;
    if (codeFor.action === 'approve') { const a = await staffCreditNotesApi.approve(id, code); setIssued(a); setOverride(a.request); }
    else { setOverride(await staffCreditNotesApi.reject(id, codeFor.reason, code)); setNote('The request was rejected. No credit note was issued and nothing changed on the order.'); }
    setCodeFor(null);
  }, true);
  const withdraw = () => run(async () => { setOverride(await staffCreditNotesApi.withdraw(id)); setNote('You withdrew the request. No credit note was issued.'); }, false);

  if (failure) return <><BackLink href="/invoices/credit-notes">Back to the credit notes queue</BackLink><FailureAlert failure={failure} onRetry={res.reload} /></>;
  if (!req) return <Loading />;
  const d = creditDecision(req, me);
  const number = issued?.number ?? req.issuedNumber;
  const issuedId = issued?.creditNoteId ?? req.issuedInvoiceId;

  return (
    <div className="space-y-6">
      <BackLink href="/invoices/credit-notes">Back to the credit notes queue</BackLink>
      {alertText && <FocusAlert ref={alertRef} testId="decision-alert">{alertText}</FocusAlert>}
      {note && <Notice testId="decision-note">{note}</Notice>}
      {issued && <Notice testId="decision-issued">{creditApprovalText(issued)}</Notice>}
      {issued?.replacementNumber && issued.replacementInvoiceId && (
        <Notice testId="decision-replacement">{replacementApprovalText(issued)} <Link className="font-mono font-semibold text-brand-accent underline" href={`/invoices/${issued.replacementInvoiceId}`} data-testid="cn-replacement-link">{issued.replacementNumber}</Link></Notice>
      )}
      {req.replace && (
        <Notice testId="cn-replace-note">This is a replacement request: approving credits the whole invoice and issues a new tax invoice with the buyer details as they are now. If the buyer BIN is no longer valid, the approval is refused.</Notice>
      )}

      <section aria-labelledby="cn-h" className="space-y-3 rounded-xl border border-surface-border bg-surface p-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 id="cn-h" className="font-display text-lg font-bold">Credit note for invoice <Link className="font-mono text-brand-accent underline" href={`/invoices/${req.originalInvoiceId}`} data-testid="cn-invoice-link">{req.originalNumber}</Link></h2>
          <Badge variant="outline" data-testid="cn-status">{creditStatusText(req.status)}</Badge>
        </div>
        <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-3">
          <div><dt className="text-xs text-text-muted">Order</dt><dd><Link className="text-brand-accent underline" href={`/orders/${req.orderId}`}>{req.orderNumber}</Link></dd></div>
          <div><dt className="text-xs text-text-muted">Reason</dt><dd>{creditReasonText(req.reason)}</dd></div>
          <div><dt className="text-xs text-text-muted">Asked</dt><dd className="font-mono text-xs">{dhakaWhen(req.requestedAtUtc)}{me.id.toLowerCase() === req.requestedBy.toLowerCase() ? ' (by you)' : ''}</dd></div>
          <div className="sm:col-span-3"><dt className="text-xs text-text-muted">Explanation</dt><dd className="break-words" data-testid="cn-reason-text">{req.reasonText}</dd></div>
          {req.decisionReason && <div className="sm:col-span-3"><dt className="text-xs text-text-muted">Decision reason</dt><dd className="break-words">{req.decisionReason}</dd></div>}
          {number && issuedId && <div><dt className="text-xs text-text-muted">Credit note (Mushak-6.7)</dt><dd><Link className="font-mono font-semibold text-brand-accent underline" href={`/invoices/${issuedId}`} data-testid="cn-number-link">{number}</Link></dd></div>}
        </dl>
        <div className="overflow-x-auto rounded-md border border-surface-border" role="region" tabIndex={0} aria-label="Credit lines">
          <table className="w-full text-left text-sm" data-testid="cn-lines">
            <caption className="sr-only">Lines of the credit note request</caption>
            <thead className="bg-bg-subtle text-xs text-text-muted"><tr><th scope="col" className="px-3 py-2">Line</th><th scope="col" className="px-3 py-2 text-right">Qty</th><th scope="col" className="px-3 py-2 text-right">Net</th><th scope="col" className="px-3 py-2 text-right">VAT</th><th scope="col" className="px-3 py-2 text-right">Total</th></tr></thead>
            <tbody>
              {req.lines.map((l) => (
                <tr key={l.lineNo} className="border-t border-surface-border align-top"><td className="px-3 py-2">{l.lineNo}. {l.descriptionEn}</td><td className="px-3 py-2 text-right">{l.quantity}</td>
                  <td className="whitespace-nowrap px-3 py-2 text-right font-mono text-xs">{formatBdt(l.netMinor)}</td><td className="whitespace-nowrap px-3 py-2 text-right font-mono text-xs">{formatBdt(l.vatMinor)}</td><td className="whitespace-nowrap px-3 py-2 text-right font-mono text-xs">{formatBdt(l.totalMinor)}</td></tr>
              ))}
            </tbody>
            <tfoot><tr className="border-t border-surface-border font-semibold"><td className="px-3 py-2" colSpan={4}>Total credit (BDT)</td><td className="whitespace-nowrap px-3 py-2 text-right font-mono text-xs" data-testid="cn-total">{formatBdt(req.totalMinor)}</td></tr></tfoot>
          </table>
        </div>
      </section>

      {req.status === 'requested' && (
        <section aria-labelledby="cn-d" className="space-y-3 rounded-xl border border-surface-border bg-surface p-4" data-testid="cn-decision">
          <h2 id="cn-d" className="font-display text-base font-bold">Your decision</h2>
          <div className="flex flex-wrap items-center gap-3">
            <Button type="button" onClick={() => { setCodeError(null); setCodeFor({ action: 'approve', reason: '' }); }} disabled={busy || !d.canDecide} aria-describedby={d.hint ? 'cn-hint' : undefined} data-testid="cn-approve">Approve and issue the credit note</Button>
            <Button type="button" variant="outline" onClick={() => setAsking(true)} disabled={busy || !d.canDecide} data-testid="cn-reject">Reject</Button>
            {d.canWithdraw && <Button type="button" variant="outline" onClick={() => void withdraw()} isLoading={busy} disabled={busy} data-testid="cn-withdraw">Withdraw my request</Button>}
          </div>
          {d.hint && <p id="cn-hint" data-testid="cn-hint" className="text-sm font-medium text-text-muted">{d.hint}</p>}
          <p className="text-xs text-text-muted">Approving or rejecting asks for the 6-digit code of your authenticator app. Rejecting also asks for a reason. Withdrawing needs no code.</p>
        </section>
      )}

      {asking && <React.Suspense fallback={null}><ReasonDialog open title="Reject this credit note?" label="Reason (5 to 500 characters; the person who asked can read it)" confirmLabel="Continue" busy={false}
        description="Nothing is issued and the order does not change. You will be asked for your authenticator code next."
        onCancel={() => setAsking(false)} onSubmit={(text) => { setAsking(false); setCodeError(null); setCodeFor({ action: 'reject', reason: text }); }} /></React.Suspense>}
      {codeFor && <React.Suspense fallback={null}><StepUpDialog open
        title={codeFor.action === 'approve' ? 'Confirm the approval' : 'Confirm the rejection'}
        description={codeFor.action === 'approve' ? `This issues a credit note of BDT ${formatBdt(req.totalMinor)} against ${req.originalNumber} and cannot be undone.` : `This rejects the credit note request for ${req.originalNumber}.`}
        confirmLabel={codeFor.action === 'approve' ? 'Approve' : 'Reject'} busy={busy} error={codeError}
        onCancel={() => { setCodeFor(null); setCodeError(null); }} onSubmit={submitCode} /></React.Suspense>}
    </div>
  );
}

export function CreditNoteDecisionView({ id }: { id: string }) {
  const { user } = useAuth();
  const permissions = user?.permissions ?? [];
  return (
    <ConsoleShell activeItemId="credit-notes" title="Credit note request">
      {canViewInvoices(permissions) && user ? <Decision id={id} me={{ id: user.id, permissions }} /> : <NoAccess permission="invoices.view" />}
    </ConsoleShell>
  );
}
