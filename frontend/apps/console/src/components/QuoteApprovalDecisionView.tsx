'use client';

import * as React from 'react';
import { Button } from '@mechware/ui/src/components/Button';
import { Textarea } from '@mechware/ui/src/components/Textarea';
import {
  CODE_SPENT_NOTE, acknowledgementProblem, approvalCommentProblem, approveHint, buildApproveBody, canApproveQuotes, dhakaWhen, flagsFromMessage, lineRefs, quoteMoney, requiredLines,
  type LineRef, type QuoteApproveBody, type QuoteSendResult, type RequiredLines, type StaffQuoteDetail
} from '@mechware/api-client';
import { staffQuotesApi } from '../lib/api';
import { issueError } from '../lib/quoteIssue';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { BackLink, FailureAlert, FocusAlert, Loading, NoAccess, useFocusOn } from './CreditShared';
import { QuoteStatusBadge } from './QuoteStatusBadge';
import { VersionView } from './QuoteVersionView';

const ReasonDialog = React.lazy(() => import('./ReasonDialog').then((m) => ({ default: m.ReasonDialog })));
const SendResultPanel = React.lazy(() => import('./QuoteIssueShared').then((m) => ({ default: m.SendResultPanel })));
const StepUpDialog = React.lazy(() => import('./StepUpDialog').then((m) => ({ default: m.StepUpDialog })));

function RefLabel({ r }: { r: LineRef }) {
  return <>Line {r.lineNo ?? '?'}: <span className="font-mono text-xs">{r.sku}</span> {r.title}{r.unitPriceMinor !== null && <> at BDT {quoteMoney(r.unitPriceMinor)}</>}{r.reason ? <span className="block text-xs text-text-muted">Reason given: {r.reason}</span> : null}</>;
}

function Decision({ id, versionNo, me }: { id: string; versionNo: number; me: { id: string; permissions: readonly string[] } }) {
  const res = useResource(() => staffQuotesApi.quote(id), id);
  const [override, setOverride] = React.useState<StaffQuoteDetail | null>(null);
  React.useEffect(() => { setOverride(null); }, [res.data]);
  const detail = override ?? res.data?.data ?? null;
  const failure = res.error ? issueError(res.error) : null;

  const [serverFlags, setServerFlags] = React.useState<RequiredLines | null>(null);
  const [below, setBelow] = React.useState<ReadonlySet<string>>(new Set());
  const [over, setOver] = React.useState<ReadonlySet<string>>(new Set());
  const [comment, setComment] = React.useState('');
  const [ackError, setAckError] = React.useState<string | null>(null);
  const [commentError, setCommentError] = React.useState<string | null>(null);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const [changedNote, setChangedNote] = React.useState<string | null>(null);
  const [codeFor, setCodeFor] = React.useState<QuoteApproveBody | null>(null);
  const [codeError, setCodeError] = React.useState<string | null>(null);
  const [rejecting, setRejecting] = React.useState(false);
  const [busy, setBusy] = React.useState(false);
  const inFlight = React.useRef(false);
  const [done, setDone] = React.useState<{ kind: 'approved'; result: QuoteSendResult } | { kind: 'rejected'; text: string } | null>(null);
  const alertRef = useFocusOn(alertText ?? ackError);
  const doneRef = React.useRef<HTMLDivElement>(null);
  React.useEffect(() => { if (done?.kind === 'rejected') doneRef.current?.focus(); }, [done]);

  const version = detail?.versions.find((v) => v.versionNo === versionNo) ?? null;
  const hash = version?.contentHash ?? '';
  // What was ticked belongs to one content and one set of flags: a reload that changes either starts the ticks again.
  React.useEffect(() => { setBelow(new Set()); setOver(new Set()); }, [hash, serverFlags]);

  if (failure) return <FailureAlert failure={failure} onRetry={res.reload} />;
  if (!detail) return <Loading />;
  const { quote } = detail;
  if (!version) {
    return <div className="space-y-3"><BackLink href="/quotes/approvals">Back to the approval queue</BackLink><p role="status" className="rounded-md border border-surface-border bg-surface p-4 text-sm">Quote {quote.number} has no version {versionNo}.</p></div>;
  }

  const required = requiredLines(version, serverFlags);
  const belowRefs = lineRefs(required.belowFloorLineIds, version.lines);
  const overRefs = lineRefs(required.overAuthorityLineIds, version.lines);
  const highlight = new Set([...required.belowFloorLineIds, ...required.overAuthorityLineIds]);
  const hint = approveHint(version, me, required);
  const pending = version.status === 'pending_approval';

  const toggle = (set: ReadonlySet<string>, lineId: string, on: boolean): Set<string> => { const n = new Set(set); if (on) n.add(lineId); else n.delete(lineId); return n; };
  const lineNos = (ids: readonly string[]) => lineRefs(ids, version.lines).map((r) => (r.lineNo === null ? 'unknown' : String(r.lineNo))).join(', ') || 'none';

  /** Everything that can be checked here is checked BEFORE the code is asked for: a code is spent even when the approval is then refused. */
  const startApprove = () => {
    if (!hint.allowed) return;
    setAlertText(null);
    const ticks = { belowFloor: below, overAuthority: over };
    const ack = acknowledgementProblem(required, ticks);
    const cp = approvalCommentProblem(comment, false);
    setAckError(ack);
    setCommentError(cp);
    if (ack || cp) return;
    const body = buildApproveBody(version, required, ticks, comment);
    if (!body) { setAckError('This version has no content hash yet. Reload the page.'); return; }
    setCodeError(null);
    setCodeFor(body);
  };

  const approve = async (code: string) => {
    if (!codeFor || inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    setCodeError(null);
    try {
      const r = await staffQuotesApi.approveVersion(id, versionNo, codeFor, code);
      const sentVersion = r.data.versions.find((v) => v.versionNo === versionNo);
      setCodeFor(null);
      setOverride(r.data);
      setDone({ kind: 'approved', result: { status: 'sent', flags: sentVersion?.flags ?? version.flags ?? { belowFloorLineIds: [], overAuthorityLineIds: [], longValidity: false }, quote: r.data } });
    } catch (err) {
      const f = issueError(err);
      if (f.stepUp) { setCodeError(f.text); return; }
      setCodeFor(null);
      setAlertText(f.status === null ? f.text : `${f.text} ${CODE_SPENT_NOTE}`);
      if (f.code === 'QUOTE_FLAGS_NOT_ACKNOWLEDGED') {
        const now = flagsFromMessage(f.message);
        if (now) {
          setServerFlags(now);
          setChangedNote(`When it was sent the flags were: below the floor on line ${lineNos(version.flags?.belowFloorLineIds ?? [])}; beyond authority on line ${lineNos(version.flags?.overAuthorityLineIds ?? [])}. Now they are: below the floor on line ${lineNos(now.belowFloorLineIds)}; beyond authority on line ${lineNos(now.overAuthorityLineIds)}. Tick these.`);
        }
      }
      if (f.code === 'QUOTE_CHANGED') { setServerFlags(null); setChangedNote(null); }
      if (f.reload) res.reload();
    } finally { inFlight.current = false; setBusy(false); }
  };

  const reject = async (text: string) => {
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    try {
      const r = await staffQuotesApi.rejectApproval(id, versionNo, text);
      setRejecting(false);
      setOverride(r.data);
      setDone({ kind: 'rejected', text: `Version ${versionNo} was sent back to its author as a draft, with your comment. Nothing went to the buyer.` });
    } catch (err) {
      const f = issueError(err);
      setRejecting(false);
      setAlertText(f.text);
      if (f.reload) res.reload();
    } finally { inFlight.current = false; setBusy(false); }
  };

  return (
    <div className="space-y-6">
      <BackLink href="/quotes/approvals">Back to the approval queue</BackLink>
      {alertText && <FocusAlert ref={alertRef} testId="decision-alert">{alertText}</FocusAlert>}
      {changedNote && pending && <div role="status" data-testid="flags-changed" className="rounded-md border border-surface-border bg-bg-subtle p-3 text-sm font-medium">{changedNote}</div>}
      {done?.kind === 'approved' && <React.Suspense fallback={null}><SendResultPanel result={done.result} quoteId={quote.id} /></React.Suspense>}
      {done?.kind === 'rejected' && <div ref={doneRef} tabIndex={-1} role="status" data-testid="decision-rejected" className="rounded-md border border-surface-border bg-bg-subtle p-3 text-sm font-medium">{done.text}</div>}

      <section aria-labelledby="decision-summary" className="space-y-2 rounded-xl border border-surface-border bg-surface p-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 id="decision-summary" className="font-display text-lg font-bold" data-testid="decision-heading">Quote <span className="font-mono">{quote.number}</span>, version {versionNo}</h2>
          <QuoteStatusBadge status={version.status} />
        </div>
        <dl className="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-3">
          <div><dt className="text-xs text-text-muted">Company</dt><dd>{quote.companyName}</dd></div>
          <div><dt className="text-xs text-text-muted">Prepared by</dt><dd>{version.preparerName ?? 'Not recorded'}</dd></div>
          <div><dt className="text-xs text-text-muted">Sent for approval</dt><dd className="font-mono text-xs">{version.submittedAt ? dhakaWhen(version.submittedAt) : 'Not yet'}</dd></div>
        </dl>
        {version.contentHash && <p className="text-xs text-text-muted">Content you are deciding on (hash): <span className="select-all break-all font-mono" data-testid="decision-hash">{version.contentHash.slice(0, 16)}</span>. If it changes before you decide, the server refuses and the page reloads.</p>}
      </section>

      {!pending && !done && (
        <p role="status" data-testid="not-pending" className="rounded-md border border-surface-border bg-surface p-4 text-sm">
          Version {versionNo} is not waiting for approval any more (it is {version.status.replace(/_/g, ' ')}), so there is nothing to decide. <BackLink href={`/quotes/${quote.id}`}>Open the quote</BackLink>
        </p>
      )}

      {pending && (
        <section aria-labelledby="decision-h" className="space-y-4 rounded-xl border border-surface-border bg-surface p-4" data-testid="decision-panel">
          <h2 id="decision-h" className="font-display text-base font-bold">Your decision</h2>
          {highlight.size === 0 ? (
            <p className="text-sm" data-testid="nothing-flagged">No line is flagged, so there is nothing to tick. The second approval is needed for another reason (for example a long validity): read the version below.</p>
          ) : (
            <p className="text-sm">The flagged lines are marked in the table below. Tick each one you have looked at. You must tick exactly the flagged lines: not more, not fewer.</p>
          )}
          {belowRefs.length > 0 && (
            <fieldset className="space-y-2 rounded-md border border-surface-border p-3" data-testid="ack-below" aria-describedby={ackError ? 'ack-error' : undefined}>
              <legend className="px-1 text-sm font-semibold">Below the margin floor ({belowRefs.length})</legend>
              {belowRefs.map((r) => (
                <label key={r.id} className="flex items-start gap-2 text-sm">
                  <input type="checkbox" className="mt-1 h-4 w-4" checked={below.has(r.id)} onChange={(e) => { setAckError(null); setBelow(toggle(below, r.id, e.target.checked)); }} data-testid="ack-below-line" />
                  <span><RefLabel r={r} /></span>
                </label>
              ))}
            </fieldset>
          )}
          {overRefs.length > 0 && (
            <fieldset className="space-y-2 rounded-md border border-surface-border p-3" data-testid="ack-over" aria-describedby={ackError ? 'ack-error' : undefined}>
              <legend className="px-1 text-sm font-semibold">Beyond the preparer&apos;s price authority ({overRefs.length})</legend>
              {overRefs.map((r) => (
                <label key={r.id} className="flex items-start gap-2 text-sm">
                  <input type="checkbox" className="mt-1 h-4 w-4" checked={over.has(r.id)} onChange={(e) => { setAckError(null); setOver(toggle(over, r.id, e.target.checked)); }} data-testid="ack-over-line" />
                  <span><RefLabel r={r} /></span>
                </label>
              ))}
            </fieldset>
          )}
          {ackError && <div id="ack-error" ref={alertText ? undefined : alertRef} tabIndex={-1} role="alert" data-testid="ack-error" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{ackError}</div>}

          <Textarea label="Comment (optional, up to 500 characters; the author sees it)" value={comment} onChange={(e) => setComment(e.target.value)} rows={2} maxLength={500} error={commentError ?? undefined} data-testid="decision-comment" />

          <div className="flex flex-wrap items-center gap-3">
            <Button type="button" onClick={startApprove} disabled={busy || !hint.allowed} aria-describedby={hint.allowed ? undefined : 'approve-hint'} data-testid="approve-quote">Approve and send to the buyer</Button>
            <Button type="button" variant="outline" onClick={() => { setAlertText(null); setRejecting(true); }} disabled={busy} data-testid="reject-quote">Send back to the author</Button>
          </div>
          {!hint.allowed && <p id="approve-hint" data-testid="approve-hint" className="text-sm font-medium text-text-muted">{hint.reason}</p>}
          <p className="text-xs text-text-muted">Approving asks for the 6-digit code of your authenticator app. Sending back asks for a comment and no code.</p>
        </section>
      )}

      <VersionView v={version} quoteId={quote.id} latest={version.versionNo === quote.latestVersionNo} canEdit={false} highlight={highlight} />

      {codeFor !== null && <React.Suspense fallback={null}><StepUpDialog
        open
        title="Confirm the approval"
        description={`You are approving version ${versionNo} of quote ${quote.number} for ${quote.companyName}${highlight.size > 0 ? `, with ${highlight.size} flagged line${highlight.size === 1 ? '' : 's'} acknowledged` : ''}. The buyer can see it as soon as you confirm.`}
        confirmLabel="Approve"
        busy={busy}
        error={codeError}
        onCancel={() => { setCodeFor(null); setCodeError(null); }}
        onSubmit={approve}
      /></React.Suspense>}
      {rejecting && <React.Suspense fallback={null}><ReasonDialog open title={`Send version ${versionNo} back to its author?`} label="Comment for the author (5 to 500 characters)" confirmLabel="Send back" busy={busy}
        description="The version becomes a draft again so the author can correct it. Nothing goes to the buyer and no authenticator code is needed."
        onCancel={() => setRejecting(false)} onSubmit={(text) => void reject(text)} /></React.Suspense>}
    </div>
  );
}

export function QuoteApprovalDecisionView({ id, versionNo }: { id: string; versionNo: number }) {
  const { user } = useAuth();
  const permissions = user?.permissions ?? [];
  return (
    <ConsoleShell activeItemId="quotes-approvals" title="Approve a quote">
      {canApproveQuotes(permissions) && user
        ? (Number.isInteger(versionNo) && versionNo > 0 ? <Decision id={id} versionNo={versionNo} me={{ id: user.id, permissions }} /> : <p role="alert" className="rounded-md border border-surface-border bg-surface p-4 text-sm">That is not a version number.</p>)
        : <NoAccess permission="quotes.approve" />}
    </ConsoleShell>
  );
}
