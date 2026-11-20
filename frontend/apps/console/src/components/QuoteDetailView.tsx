'use client';

import * as React from 'react';
import Link from 'next/link';
import { Button } from '@mechware/ui/src/components/Button';
import {
  assigneeText, canViewQuotes, closeQuoteHint, dhakaWhen, reviseHint, versionsNewestFirst, type StaffQuoteDetail
} from '@mechware/api-client';
import { deskError, staffQuotesApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { BackLink, FailureAlert, FocusAlert, Loading, NoAccess, Notice, useFocusOn } from './CreditShared';
import { QuoteStatusBadge } from './QuoteStatusBadge';
import type { IssueChange } from './QuoteIssueActions';
import { VersionView } from './QuoteVersionView';
import { CreditCell } from './QuotesListView';

// Loaded when first needed, so the page's first load stays small (the dialog library is heavy).
const ReasonDialog = React.lazy(() => import('./ReasonDialog').then((m) => ({ default: m.ReasonDialog })));
const IssueActions = React.lazy(() => import('./QuoteIssueActions'));

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
  const [issueBusy, setIssueBusy] = React.useState(false);
  const onIssueChange = (c: IssueChange) => {
    setAlertText(null);
    setNotice(c.notice ?? null);
    if (c.detail) setOverride(c.detail);
    if (c.alert) setAlertText(c.alert);
    if (c.reload) res.reload();
  };

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
              <Button type="button" onClick={revise} disabled={busy || issueBusy || !reviseState.allowed} data-testid="revise-quote">Revise: start a new version</Button>
              <Button type="button" variant="destructive" onClick={() => { setAlertText(null); setClosing(true); }} disabled={busy || issueBusy || !closeState.allowed} data-testid="close-quote">Close the quote</Button>
            </div>
            <React.Suspense fallback={null}><IssueActions quote={detail} permissions={permissions} disabled={busy} onBusy={setIssueBusy} onChange={onIssueChange} /></React.Suspense>
            {!reviseState.allowed && <p className="text-xs text-text-muted" data-testid="revise-hint">{reviseState.reason}</p>}
            {!closeState.allowed && <p className="text-xs text-text-muted" data-testid="close-hint">{closeState.reason}</p>}
          </div>
        )}
      </section>

      <div className="space-y-4">
        {versions.map((v, i) => <VersionView key={v.id} v={v} quoteId={quote.id} latest={i === 0} canEdit={canEdit} />)}
        {versions.length === 0 && <p className="text-sm text-text-muted">This quote has no version.</p>}
      </div>

      <React.Suspense fallback={null}><ReasonDialog open={closing} title={`Close quote ${quote.number}?`} label="Why is it closed? (5 to 500 characters)" confirmLabel="Close the quote" busy={busy}
        description="A closed quote cannot be edited or accepted. A version that is out with the buyer is withdrawn."
        onCancel={() => setClosing(false)} onSubmit={close} /></React.Suspense>
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
