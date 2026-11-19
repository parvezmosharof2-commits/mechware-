'use client';

import * as React from 'react';
import Link from 'next/link';
import { Button } from '@mechware/ui/src/components/Button';
import {
  assigneeText, canViewQuotes, closeQuoteHint, dhakaWhen, flagWords, hasCostFigures, paymentTermsLabel, quoteMoney, reviseHint,
  shippingModeLabel, versionsNewestFirst, type StaffQuoteDetail, type StaffQuoteVersion
} from '@mechware/api-client';
import { deskError, staffQuotesApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { BackLink, FailureAlert, FocusAlert, Loading, NoAccess, Notice, useFocusOn } from './CreditShared';
import { QuoteStatusBadge, ReasonDialog } from './QuoteDeskShared';
import { CreditCell } from './QuotesListView';

const VERSION_EVENTS: readonly [keyof StaffQuoteVersion, string][] = [
  ['submittedAt', 'Submitted for approval'], ['approvedAt', 'Approved'], ['approvalRejectedAt', 'Approval refused'], ['sentAt', 'Sent to the buyer'],
  ['validUntil', 'Valid until'], ['acceptedAt', 'Accepted by the buyer'], ['rejectedAt', 'Rejected by the buyer'], ['withdrawnAt', 'Withdrawn'], ['supersededAt', 'Replaced by a newer version']
];

function VersionView({ v, quoteId, latest, canEdit }: { v: StaffQuoteVersion; quoteId: string; latest: boolean; canEdit: boolean }) {
  const showCost = hasCostFigures(v.lines);
  const lineNos = new Map(v.lines.map((l) => [l.id, l.lineNo] as const));
  const flags = flagWords(v.flags, lineNos);
  const headingId = `version-${v.versionNo}`;
  return (
    <section aria-labelledby={headingId} className="space-y-3 rounded-xl border border-surface-border bg-surface p-4" data-testid="quote-version" data-version-no={v.versionNo}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id={headingId} className="font-display text-base font-bold">Version {v.versionNo}{latest ? ' (latest)' : ''}</h2>
        <QuoteStatusBadge status={v.status} />
      </div>
      <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-3">
        <div><dt className="text-xs text-text-muted">Valid for</dt><dd>{v.validityDays} days</dd></div>
        <div><dt className="text-xs text-text-muted">Payment</dt><dd>{paymentTermsLabel(v.paymentTerms)}</dd></div>
        <div><dt className="text-xs text-text-muted">Delivery charge</dt><dd>{shippingModeLabel(v.shippingMode)}</dd></div>
        <div><dt className="text-xs text-text-muted">Prepared by</dt><dd>{v.preparerName ?? 'Not set yet'}</dd></div>
        <div><dt className="text-xs text-text-muted">Created</dt><dd className="font-mono text-xs">{dhakaWhen(v.createdAt)}</dd></div>
        {VERSION_EVENTS.filter(([k]) => v[k]).map(([k, label]) => <div key={k}><dt className="text-xs text-text-muted">{label}</dt><dd className="font-mono text-xs">{dhakaWhen(String(v[k]))}</dd></div>)}
      </dl>
      {v.withdrawReason && <p className="text-sm"><span className="text-xs text-text-muted">Withdrawn because: </span>{v.withdrawReason}</p>}
      {v.rejectReason && <p className="text-sm"><span className="text-xs text-text-muted">Rejected because: </span>{v.rejectReason}</p>}
      {v.approvalComment && <p className="text-sm"><span className="text-xs text-text-muted">Approval comment: </span>{v.approvalComment}</p>}
      {v.buyerNote && <p className="whitespace-pre-wrap break-words text-sm"><span className="text-xs text-text-muted">Note to the buyer: </span>{v.buyerNote}</p>}
      {v.termsText && <p className="whitespace-pre-wrap break-words text-sm"><span className="text-xs text-text-muted">Terms: </span>{v.termsText}</p>}
      {v.internalNote && <p className="whitespace-pre-wrap break-words text-sm"><span className="text-xs text-text-muted">Internal note (the buyer does not see it): </span>{v.internalNote}</p>}
      {flags.length > 0 && (
        <ul aria-label="Checks that need a decision" className="space-y-1 rounded-md border border-fault p-3 text-sm font-medium text-fault" data-testid="version-flags">
          {flags.map((f) => <li key={f}>{f}</li>)}
        </ul>
      )}

      {v.lines.length === 0 ? (
        <p className="text-sm text-text-muted" data-testid="version-no-lines">No line yet.</p>
      ) : (
        <div className="overflow-x-auto rounded-md border border-surface-border">
          <table className="w-full text-left text-sm" data-testid="version-lines">
            <caption className="sr-only">Lines of version {v.versionNo}</caption>
            <thead className="bg-bg-subtle text-xs text-text-muted">
              <tr>
                <th scope="col" className="px-3 py-2">No.</th>
                <th scope="col" className="px-3 py-2">Part</th>
                <th scope="col" className="px-3 py-2 text-right">Quantity</th>
                <th scope="col" className="px-3 py-2 text-right">Unit price (BDT)</th>
                <th scope="col" className="px-3 py-2 text-right">Net (BDT)</th>
                <th scope="col" className="px-3 py-2 text-right">VAT (BDT)</th>
                <th scope="col" className="px-3 py-2 text-right">Total (BDT)</th>
                {showCost && <th scope="col" className="px-3 py-2 text-right" data-testid="cost-head">Cost / floor (BDT)</th>}
              </tr>
            </thead>
            <tbody>
              {v.lines.map((l) => (
                <tr key={l.id} className="border-t border-surface-border align-top" data-testid="version-line">
                  <td className="px-3 py-2">{l.lineNo}</td>
                  <td className="px-3 py-2">
                    <span className="font-mono text-xs">{l.sku}</span> {l.titleEn}
                    {l.partNumber && <span className="block text-xs text-text-muted">Part no. {l.partNumber}</span>}
                    {l.lineNote && <span className="block text-xs text-text-muted">{l.lineNote}</span>}
                    {l.belowFloor && <span className="block text-xs font-bold text-fault">Below the margin floor{l.belowFloorReason ? `: ${l.belowFloorReason}` : ''}</span>}
                    {l.overAuthority && <span className="block text-xs font-bold text-fault">Beyond the preparer&apos;s price authority</span>}
                  </td>
                  <td className="px-3 py-2 text-right">{l.quantity}</td>
                  <td className="px-3 py-2 text-right font-mono">{quoteMoney(l.unitPriceMinor)}</td>
                  <td className="px-3 py-2 text-right font-mono">{quoteMoney(l.lineNetMinor)}</td>
                  <td className="px-3 py-2 text-right font-mono">{quoteMoney(l.lineTaxMinor)}</td>
                  <td className="px-3 py-2 text-right font-mono">{quoteMoney(l.lineTotalMinor)}</td>
                  {showCost && (
                    <td className="px-3 py-2 text-right font-mono" data-testid="cost-cell">
                      {l.cost ? `${quoteMoney(l.cost.costMinorAtCheck) || 'no cost'} / ${quoteMoney(l.cost.floorMinorAtCheck) || 'no floor'}` : ''}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <dl className="ml-auto grid max-w-sm grid-cols-2 gap-x-4 gap-y-1 text-sm" data-testid="version-totals">
        <dt>Goods, net</dt><dd className="text-right font-mono">BDT {quoteMoney(v.goodsNetMinor)}</dd>
        <dt>VAT</dt><dd className="text-right font-mono">BDT {quoteMoney(v.taxMinor)}</dd>
        <dt>Goods, total</dt><dd className="text-right font-mono">BDT {quoteMoney(v.goodsTotalMinor)}</dd>
        <dt>Delivery</dt><dd className="text-right font-mono">{v.shippingTotalMinor === null ? 'Worked out when the buyer orders' : `BDT ${quoteMoney(v.shippingTotalMinor)}`}</dd>
        {v.grandTotalMinor !== null && <><dt className="font-semibold">Total</dt><dd className="text-right font-mono font-semibold">BDT {quoteMoney(v.grandTotalMinor)}</dd></>}
      </dl>
      {latest && v.status === 'draft' && (
        <p className="text-sm text-text-muted" data-testid="editor-placeholder">
          {canEdit
            ? <>Changing the lines, prices and terms, and sending the quote, are built in the next units. <Link href={`/quotes/${quoteId}/edit`} className="text-brand-accent underline">Open the editor page (not built yet)</Link>.</>
            : 'You can read this draft. Editing needs the quotes.edit permission.'}
        </p>
      )}
    </section>
  );
}

function Detail({ id, permissions }: { id: string; permissions: readonly string[] }) {
  const res = useResource(() => staffQuotesApi.quote(id), id);
  const [override, setOverride] = React.useState<StaffQuoteDetail | null>(null);
  React.useEffect(() => { setOverride(null); }, [res.data]);
  const detail = override ?? res.data?.data ?? null;
  const failure = res.error ? deskError(res.error) : null;

  const [busy, setBusy] = React.useState(false);
  const inFlight = React.useRef(false);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const alertRef = useFocusOn(alertText);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [closing, setClosing] = React.useState(false);

  const run = async (work: () => Promise<void>) => {
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    setAlertText(null);
    setNotice(null);
    try { await work(); } catch (err) {
      const f = deskError(err);
      setClosing(false);
      setAlertText(f.text);
      if (f.reload) res.reload();
    } finally { inFlight.current = false; setBusy(false); }
  };

  const revise = () => run(async () => {
    const r = await staffQuotesApi.reviseQuote(id);
    setOverride(r.data);
    setNotice(`Version ${r.data.quote.latestVersionNo} is a new draft, a copy of the one before. The earlier versions are untouched.`);
  });
  const close = (reason: string) => run(async () => {
    const r = await staffQuotesApi.closeQuote(id, reason);
    setClosing(false);
    setOverride(r.data);
    setNotice('The quote is closed. If a version was out with the buyer, it is withdrawn.');
  });

  if (failure) return <FailureAlert failure={failure} onRetry={res.reload} />;
  if (!detail) return <Loading />;

  const { quote } = detail;
  const versions = versionsNewestFirst(detail.versions);
  const canEdit = permissions.some((p) => p.toLowerCase() === 'quotes.edit');
  const reviseState = reviseHint(quote, permissions);
  const closeState = closeQuoteHint(quote, permissions);

  return (
    <div className="space-y-6">
      <BackLink href="/quotes">Back to the quotes</BackLink>
      {notice && <Notice testId="quote-notice">{notice}</Notice>}
      {alertText && <FocusAlert ref={alertRef} testId="quote-alert">{alertText}</FocusAlert>}

      <section aria-labelledby="quote-summary" className="space-y-3 rounded-xl border border-surface-border bg-surface p-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 id="quote-summary" className="font-display text-lg font-bold">Quote <span className="font-mono" data-testid="quote-number">{quote.number}</span></h2>
          <QuoteStatusBadge status={quote.status} />
        </div>
        <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
          <div><dt className="text-xs text-text-muted">Company</dt><dd>{quote.companyName}</dd></div>
          <div><dt className="text-xs text-text-muted">Buyer contact</dt><dd>{quote.contactName}</dd></div>
          <div><dt className="text-xs text-text-muted">Credit terms of the company</dt><dd><CreditCell terms={quote.creditTerms} onHold={quote.creditOnHold} /></dd></div>
          <div><dt className="text-xs text-text-muted">Assigned to</dt><dd>{assigneeText(quote.assigneeName, quote.assignedTo)}</dd></div>
          <div><dt className="text-xs text-text-muted">From request</dt><dd>{quote.rfqId && quote.rfqNumber ? <Link href={`/quotes/rfqs/${quote.rfqId}`} className="font-mono text-xs text-brand-accent underline">{quote.rfqNumber}</Link> : 'None: made for the company'}</dd></div>
          <div><dt className="text-xs text-text-muted">Created</dt><dd className="font-mono text-xs">{dhakaWhen(quote.createdAt)}</dd></div>
          {quote.closedAt && <div className="sm:col-span-2"><dt className="text-xs text-text-muted">Closed because ({dhakaWhen(quote.closedAt)})</dt><dd data-testid="quote-close-reason">{quote.closeReason}</dd></div>}
        </dl>
        <p className="text-xs text-text-muted">The status is worked out now from the latest version and the clock.</p>
      </section>

      <section aria-labelledby="quote-actions" className="space-y-3 rounded-xl border border-surface-border bg-surface p-4">
        <h2 id="quote-actions" className="font-display text-base font-bold">What you can do</h2>
        {!canEdit && <p className="text-sm text-text-muted" data-testid="quote-command-hint">{reviseState.reason}</p>}
        {canEdit && (
          <div className="space-y-2">
            <div className="flex flex-wrap items-center gap-2">
              <Button type="button" onClick={revise} disabled={busy || !reviseState.allowed} data-testid="revise-quote">Revise: start a new version</Button>
              <Button type="button" variant="destructive" onClick={() => { setAlertText(null); setClosing(true); }} disabled={busy || !closeState.allowed} data-testid="close-quote">Close the quote</Button>
            </div>
            {!reviseState.allowed && <p className="text-xs text-text-muted" data-testid="revise-hint">{reviseState.reason}</p>}
            {!closeState.allowed && <p className="text-xs text-text-muted" data-testid="close-hint">{closeState.reason}</p>}
          </div>
        )}
      </section>

      <div className="space-y-4">
        {versions.map((v, i) => <VersionView key={v.id} v={v} quoteId={quote.id} latest={i === 0} canEdit={canEdit} />)}
        {versions.length === 0 && <p className="text-sm text-text-muted">This quote has no version.</p>}
      </div>

      <ReasonDialog open={closing} title={`Close quote ${quote.number}?`} label="Why is it closed? (5 to 500 characters)" confirmLabel="Close the quote" busy={busy}
        description="A closed quote cannot be edited or accepted. A version that is out with the buyer is withdrawn."
        onCancel={() => setClosing(false)} onSubmit={close} />
    </div>
  );
}

export function QuoteDetailView({ id }: { id: string }) {
  const { user } = useAuth();
  const permissions = user?.permissions ?? [];
  return (
    <ConsoleShell activeItemId="quotes-list" title="Quote">
      {canViewQuotes(permissions) ? <Detail id={id} permissions={permissions} /> : <NoAccess permission="quotes.view" />}
    </ConsoleShell>
  );
}
