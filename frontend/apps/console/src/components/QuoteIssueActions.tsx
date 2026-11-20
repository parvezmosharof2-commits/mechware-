'use client';

import * as React from 'react';
import Link from 'next/link';
import { Button } from '@mechware/ui/src/components/Button';
import { canApproveQuotes, recallHint, sendFromDetailHint, withdrawHint, type QuoteSendResult, type StaffQuoteDetail } from '@mechware/api-client';
import { staffQuotesApi } from '../lib/api';
import { issueError } from '../lib/quoteIssue';
import { ReasonDialog } from './ReasonDialog';
import { SendConfirmDialog, SendResultPanel } from './QuoteIssueShared';

export interface IssueChange { detail?: StaffQuoteDetail; notice?: string; alert?: string; reload?: boolean }

/**
 * Send, recall and withdraw from the quote page. Loaded on demand by the page (so the page's first load stays small); it keeps its own
 * busy flag, checks reasons before sending, and reports what happened through `onChange`. The server decides everything again.
 */
export default function QuoteIssueActions({ quote, permissions, disabled, onBusy, onChange }: {
  quote: StaffQuoteDetail; permissions: readonly string[]; disabled: boolean; onBusy: (busy: boolean) => void; onChange: (change: IssueChange) => void;
}) {
  const id = quote.quote.id;
  const versions = [...quote.versions].sort((a, b) => b.versionNo - a.versionNo);
  const draft = versions.find((v) => v.status === 'draft' && v.versionNo === quote.quote.latestVersionNo) ?? null;
  const pending = versions.find((v) => v.status === 'pending_approval') ?? null;
  const sent = versions.find((v) => v.status === 'sent') ?? null;
  const sendState = draft ? sendFromDetailHint(draft, permissions) : null;
  const recallState = pending ? recallHint(pending, permissions) : null;
  const withdrawState = sent ? withdrawHint(sent, permissions) : null;

  const [busy, setBusy] = React.useState(false);
  const inFlight = React.useRef(false);
  const [sending, setSending] = React.useState(false);
  const [withdrawing, setWithdrawing] = React.useState<number | null>(null);
  const [result, setResult] = React.useState<QuoteSendResult | null>(null);

  const run = async (work: () => Promise<IssueChange & { result?: QuoteSendResult | null }>) => {
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    onBusy(true);
    try {
      const { result: next, ...change } = await work();
      setResult(next ?? null);
      onChange(change);
    } catch (err) {
      const f = issueError(err);
      onChange({ alert: f.text, reload: f.reload });
    } finally { inFlight.current = false; setBusy(false); setSending(false); setWithdrawing(null); onBusy(false); }
  };

  const off = busy || disabled;
  if (!draft && !pending && !sent) return null;
  return (
    <>
      <div className="flex flex-wrap items-center gap-2" data-testid="issue-actions">
        {draft && sendState && <Button type="button" onClick={() => setSending(true)} disabled={off || !sendState.allowed} data-testid="send-draft">Send version {draft.versionNo}</Button>}
        {pending && recallState && (
          <Button type="button" variant="outline" disabled={off || !recallState.allowed} data-testid="recall-version"
            onClick={() => void run(async () => ({ detail: (await staffQuotesApi.recallVersion(id, pending.versionNo)).data, notice: `Version ${pending.versionNo} was taken back from approval. It is a draft again: edit it, then send it.` }))}>
            Recall version {pending.versionNo} from approval
          </Button>
        )}
        {pending && canApproveQuotes(permissions) && <Link href={`/quotes/approvals/${id}/${pending.versionNo}`} className="text-sm font-semibold text-brand-accent underline" data-testid="review-approval">Review version {pending.versionNo} for approval</Link>}
        {sent && withdrawState && <Button type="button" variant="destructive" onClick={() => setWithdrawing(sent.versionNo)} disabled={off || !withdrawState.allowed} data-testid="withdraw-version">Withdraw version {sent.versionNo}</Button>}
      </div>
      {result && <SendResultPanel result={result} quoteId={id} />}
      {sendState && !sendState.allowed && <p className="text-xs text-text-muted" data-testid="send-hint">{sendState.reason}</p>}
      {pending && recallState && !recallState.allowed && <p className="text-xs text-text-muted" data-testid="recall-hint">{recallState.reason}</p>}

      {sending && draft && (
        <SendConfirmDialog open busy={busy} onCancel={() => setSending(false)} title={`Send version ${draft.versionNo} of ${quote.quote.number}?`} confirmLabel="Send"
          description="If nothing needs a second person the buyer can see and accept it at once; otherwise it goes to the approval queue. The server checks the prices again. Use the editor first if you want to see the check."
          onConfirm={() => void run(async () => { const r = await staffQuotesApi.sendVersion(id, draft.versionNo); return { detail: r.data.quote, result: r.data }; })} />
      )}
      <ReasonDialog open={withdrawing !== null} title={`Withdraw version ${withdrawing ?? ''} of ${quote.quote.number}?`} label="Why is it withdrawn? (5 to 500 characters)" confirmLabel="Withdraw it" busy={busy}
        description="The buyer will no longer be able to accept it. An order that was already placed from it is not touched."
        onCancel={() => setWithdrawing(null)}
        onSubmit={(reason) => { if (withdrawing !== null) void run(async () => ({ detail: (await staffQuotesApi.withdrawVersion(id, withdrawing, reason)).data, notice: `Version ${withdrawing} was withdrawn. The buyer can no longer accept it.` })); }} />
    </>
  );
}
