'use client';

import * as React from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import {
  assigneeIdProblem, assigneeText, canViewQuotes, createQuoteHint, dhakaWhen, isFinishedRfq, quoteStatusLabel, rfqCommandHint, rfqSourceLabel,
  staffLineCounts, staffLineHint, staffLineKind, staffLineLabel, type StaffRfqDetail, type StaffRfqLine
} from '@mechware/api-client';
import { deskError, staffQuotesApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { BackLink, FailureAlert, FocusAlert, Loading, NoAccess, Notice, useFocusOn } from './CreditShared';
import { ReasonDialog, RfqStatusBadge, SlaBadge } from './QuoteDeskShared';

function LineRow({ line }: { line: StaffRfqLine }) {
  const kind = staffLineKind(line.matchStatus);
  const part = (v: NonNullable<StaffRfqLine['variant']>) => <span><span className="font-mono text-xs">{v.sku}</span> {v.title}</span>;
  return (
    <tr className="border-t border-surface-border align-top" data-testid="rfq-line" data-match={line.matchStatus}>
      <td className="px-3 py-2">{line.lineNo}</td>
      <td className="px-3 py-2"><span className="break-words">{line.rawText}</span>{line.note && <span className="block text-xs text-text-muted">{line.note}</span>}</td>
      <td className="px-3 py-2 text-right">{line.quantity}</td>
      <td className="px-3 py-2">
        <Badge variant={kind === 'matched' ? 'success' : 'outline'} className={kind === 'notFound' || kind === 'ambiguous' ? 'font-bold text-fault' : undefined}>{staffLineLabel(line.matchStatus)}</Badge>
        <span className="mt-1 block text-xs text-text-muted">{staffLineHint(line)}</span>
      </td>
      <td className="px-3 py-2">
        {line.variant ? part(line.variant) : <span className="text-text-muted">None</span>}
        {line.successor && <span className="mt-1 block" data-testid="successor">Replaced by: {part(line.successor)}</span>}
      </td>
    </tr>
  );
}

function Detail({ id, me }: { id: string; me: { id: string; permissions: readonly string[] } }) {
  const router = useRouter();
  const res = useResource(() => staffQuotesApi.rfq(id), id);
  const [override, setOverride] = React.useState<StaffRfqDetail | null>(null);
  const detail = override ?? res.data?.data ?? null;
  React.useEffect(() => { setOverride(null); }, [res.data]);
  const failure = res.error ? deskError(res.error) : null;

  const [busy, setBusy] = React.useState(false);
  const inFlight = React.useRef(false);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const alertRef = useFocusOn(alertText);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [closing, setClosing] = React.useState(false);
  const [staffId, setStaffId] = React.useState('');
  const [staffProblem, setStaffProblem] = React.useState<string | undefined>();

  /** One command at a time. A refusal shows its words; a 409 that means "this page is old" reloads the page. */
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

  const assign = (assigneeId: string | null, done: string) => run(async () => {
    const r = await staffQuotesApi.assignRfq(id, assigneeId);
    setOverride(r.data);
    setNotice(done);
  });

  const close = (reason: string) => run(async () => {
    const r = await staffQuotesApi.closeRfq(id, reason);
    setClosing(false);
    setOverride(r.data);
    setNotice('The request is closed. The buyer sees your reason.');
  });

  const createQuote = () => run(async () => {
    const r = await staffQuotesApi.createQuote({ rfqId: id });
    router.push(`/quotes/${r.data.quote.id}`);
  });

  if (failure) return <FailureAlert failure={failure} onRetry={res.reload} />;
  if (!detail) return <Loading />;

  const { rfq } = detail;
  const hint = rfqCommandHint(rfq, me.permissions);
  const canQuote = createQuoteHint(rfq, me.permissions);
  const counts = staffLineCounts(detail.lines);
  const mine = rfq.assignedTo?.toLowerCase() === me.id.toLowerCase();

  return (
    <div className="space-y-6">
      <BackLink href="/quotes/rfqs">Back to the inbox</BackLink>
      {notice && <Notice testId="rfq-notice">{notice}</Notice>}
      {alertText && <FocusAlert ref={alertRef} testId="rfq-alert">{alertText}</FocusAlert>}

      <section aria-labelledby="rfq-summary" className="space-y-3 rounded-xl border border-surface-border bg-surface p-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 id="rfq-summary" className="font-display text-lg font-bold">Request <span className="font-mono" data-testid="rfq-number">{rfq.number}</span></h2>
          <RfqStatusBadge status={rfq.status} />
        </div>
        <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
          <div><dt className="text-xs text-text-muted">Company</dt><dd>{rfq.companyName}</dd></div>
          <div><dt className="text-xs text-text-muted">Asked by</dt><dd>{rfq.requesterName}</dd></div>
          <div><dt className="text-xs text-text-muted">How it was sent</dt><dd>{rfqSourceLabel(rfq.source)}</dd></div>
          <div><dt className="text-xs text-text-muted">Received</dt><dd className="font-mono text-xs">{dhakaWhen(rfq.createdAt)}</dd></div>
          <div><dt className="text-xs text-text-muted">Needed by</dt><dd>{rfq.neededBy ?? 'No date given'}</dd></div>
          <div><dt className="text-xs text-text-muted">Assigned to</dt><dd data-testid="rfq-assignee">{assigneeText(rfq.assigneeName, rfq.assignedTo)}{mine ? ' (you)' : ''}</dd></div>
          <div className="sm:col-span-2"><dt className="text-xs text-text-muted">First-quote clock</dt><dd><SlaBadge rfq={rfq} /></dd></div>
          {detail.message && <div className="sm:col-span-2"><dt className="text-xs text-text-muted">Message from the buyer</dt><dd className="whitespace-pre-wrap break-words">{detail.message}</dd></div>}
          {isFinishedRfq(rfq.status) && rfq.closeReason && <div className="sm:col-span-2"><dt className="text-xs text-text-muted">Closed because</dt><dd data-testid="rfq-close-reason">{rfq.closeReason}</dd></div>}
        </dl>
      </section>

      <section aria-labelledby="rfq-actions" className="space-y-3 rounded-xl border border-surface-border bg-surface p-4">
        <h2 id="rfq-actions" className="font-display text-base font-bold">What you can do</h2>
        {!hint.allowed && <p className="text-sm text-text-muted" data-testid="rfq-command-hint">{hint.reason}</p>}
        {hint.allowed && (
          <div className="space-y-4">
            <div className="flex flex-wrap items-center gap-2">
              <Button type="button" onClick={createQuote} disabled={busy || !canQuote.allowed} data-testid="create-quote">Create a quote from this request</Button>
              <Button type="button" variant="outline" onClick={() => assign(me.id, 'Assigned to you.')} disabled={busy || mine} data-testid="assign-me">Assign to me</Button>
              <Button type="button" variant="outline" onClick={() => assign(null, 'The request is not assigned to anyone now.')} disabled={busy || !rfq.assignedTo} data-testid="unassign">Unassign</Button>
              <Button type="button" variant="destructive" onClick={() => { setAlertText(null); setClosing(true); }} disabled={busy} data-testid="close-rfq">Close the request</Button>
            </div>
            <form className="flex flex-wrap items-end gap-2" onSubmit={(e) => {
              e.preventDefault();
              const p = assigneeIdProblem(staffId);
              setStaffProblem(p ?? undefined);
              if (!p) void assign(staffId.trim(), 'Assigned to that staff member.');
            }}>
              <div className="min-w-64 flex-1">
                <Input label="Assign to another staff member (user id)" value={staffId} onChange={(e) => setStaffId(e.target.value)} error={staffProblem} autoComplete="off" spellCheck={false}
                  helperText="Only a staff member who prepares quotes (Support or Admin) is accepted. The server checks it." />
              </div>
              <Button type="submit" variant="outline" disabled={busy}>Assign</Button>
            </form>
            <p className="text-xs text-text-muted">
              A quote starts as a draft with the matched lines already at the company&apos;s price. Closing a request does not change the quotes already made from it.
            </p>
          </div>
        )}
      </section>

      <section aria-labelledby="rfq-lines" className="space-y-3">
        <h2 id="rfq-lines" className="font-display text-base font-bold">Lines asked for</h2>
        <p className="text-sm text-text-muted" data-testid="rfq-line-counts">
          {detail.lines.length} lines: {counts.matched} matched, {counts.obsolete} obsolete, {counts.unmatched} not matched.
        </p>
        <div className="overflow-x-auto rounded-md border border-surface-border">
          <table className="w-full text-left text-sm" data-testid="rfq-lines">
            <caption className="sr-only">Lines of request {rfq.number}</caption>
            <thead className="bg-bg-subtle text-xs text-text-muted">
              <tr>
                <th scope="col" className="px-3 py-2">No.</th>
                <th scope="col" className="px-3 py-2">As typed</th>
                <th scope="col" className="px-3 py-2 text-right">Quantity</th>
                <th scope="col" className="px-3 py-2">Match</th>
                <th scope="col" className="px-3 py-2">Catalogue part</th>
              </tr>
            </thead>
            <tbody>{detail.lines.map((l) => <LineRow key={l.lineNo} line={l} />)}</tbody>
          </table>
        </div>
      </section>

      <section aria-labelledby="rfq-quotes" className="space-y-2">
        <h2 id="rfq-quotes" className="font-display text-base font-bold">Quotes made from this request</h2>
        {detail.quotes.length === 0 ? (
          <p className="text-sm text-text-muted" data-testid="rfq-no-quotes">No quote has been made from it yet.</p>
        ) : (
          <ul className="space-y-1 text-sm" data-testid="rfq-quotes">
            {detail.quotes.map((q) => (
              <li key={q.id}><Link href={`/quotes/${q.id}`} className="font-mono text-xs font-semibold text-brand-accent underline">{q.number}</Link> <span>{quoteStatusLabel(q.status)}</span></li>
            ))}
          </ul>
        )}
      </section>

      <ReasonDialog open={closing} title={`Close request ${rfq.number}?`} label="Why is it closed? The buyer sees this (5 to 500 characters)" confirmLabel="Close the request" busy={busy}
        description="Use it when you will not bid, for example when the parts are not sold here. Quotes already made from it stay as they are."
        onCancel={() => setClosing(false)} onSubmit={close} />
    </div>
  );
}

export function RfqDetailView({ id }: { id: string }) {
  const { user } = useAuth();
  const permissions = user?.permissions ?? [];
  return (
    <ConsoleShell activeItemId="quotes-rfqs" title="Quote request">
      {canViewQuotes(permissions) && user ? <Detail id={id} me={{ id: user.id, permissions }} /> : <NoAccess permission="quotes.view" />}
    </ConsoleShell>
  );
}
