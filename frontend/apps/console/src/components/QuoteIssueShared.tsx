'use client';

import * as React from 'react';
import Link from 'next/link';
import { Button } from '@mechware/ui/src/components/Button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@mechware/ui/src/components/Dialog';
import { dhakaWhen, flagWords, sendOutcome, sendOutcomeTitle, type QuoteSendResult } from '@mechware/api-client';

/** What a send did, in words: sent (with the validity dates on the Dhaka clock) or waiting for approval (with the flags). Takes the focus when it appears. */
export function SendResultPanel({ result, quoteId }: { result: QuoteSendResult; quoteId: string }) {
  const o = sendOutcome(result);
  const ref = React.useRef<HTMLDivElement>(null);
  React.useEffect(() => { ref.current?.focus(); }, []);
  const version = result.quote.versions.find((v) => v.versionNo === o.versionNo);
  const flags = flagWords(o.flags, new Map((version?.lines ?? []).map((l) => [l.id, l.lineNo] as const)));
  return (
    <div ref={ref} tabIndex={-1} role="status" data-testid="send-result" data-send-status={o.kind} className="space-y-3 rounded-xl border border-surface-border bg-surface p-4">
      <h2 className="font-display text-base font-bold" data-testid="send-result-title">{o.kind === 'sent' ? 'Sent' : o.kind === 'pending' ? 'Waiting for approval' : 'Sent state unknown'}</h2>
      <p className="text-sm">{sendOutcomeTitle(o)}</p>
      {o.kind === 'sent' && (
        <dl className="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-3" data-testid="send-validity">
          <div><dt className="text-xs text-text-muted">Sent on</dt><dd className="font-mono text-xs">{dhakaWhen(o.sentAt)}</dd></div>
          <div><dt className="text-xs text-text-muted">Valid for</dt><dd>{o.validityDays} days</dd></div>
          <div><dt className="text-xs text-text-muted">Valid until</dt><dd className="font-mono text-xs">{dhakaWhen(o.validUntil)}</dd></div>
        </dl>
      )}
      {o.kind === 'pending' && (
        <>
          {flags.length > 0 && (
            <ul aria-label="Why it needs a second person" className="space-y-1 rounded-md border border-surface-border p-3 text-sm font-medium" data-testid="send-flags">
              {flags.map((f) => <li key={f}>{f}</li>)}
            </ul>
          )}
          <p className="text-sm text-text-muted">A second person with approval rights has to look at it and enter an authenticator code. Until then you can recall it to edit it again.</p>
        </>
      )}
      <p className="text-sm"><Link href={`/quotes/${quoteId}`} className="font-semibold text-brand-accent underline" data-testid="send-result-link">Go to the quote</Link></p>
    </div>
  );
}

/** Asks once before a version goes to the buyer (or to a second person). Nothing is sent until Confirm. */
export function SendConfirmDialog({ open, title, description, confirmLabel, busy, onCancel, onConfirm }: {
  open: boolean; title: string; description: string; confirmLabel: string; busy: boolean; onCancel: () => void; onConfirm: () => void;
}) {
  return (
    <Dialog open={open} onOpenChange={(next) => { if (!next && !busy) onCancel(); }}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>{description}</DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onCancel} disabled={busy}>Not yet</Button>
          <Button type="button" onClick={onConfirm} isLoading={busy} disabled={busy} data-testid="send-confirm">{confirmLabel}</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
