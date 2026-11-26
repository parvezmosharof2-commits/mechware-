'use client';

import * as React from 'react';
import Link from 'next/link';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import { Textarea } from '@mechware/ui/src/components/Textarea';
import {
  CREDIT_REASON_OPTIONS, attemptFor, buildCreditNoteBody, creditNoteErrorText, creditRequestFingerprint, formatBdt, type BuyerInvoiceDetail, type CreditLineDraft, type PlaceAttempt
} from '@mechware/api-client';
import { problemCode, problemMessage, problemStatus, staffCreditNotesApi } from '../lib/api';
import { Modal, ModalFooter } from './Modal';
import { Notice } from './CreditShared';

export const creditNoteFailure = (err: unknown): { text: string; code: string | null } => {
  const code = problemCode(err);
  return { text: creditNoteErrorText({ status: problemStatus(err), code, message: problemMessage(err, '') }), code };
};

/** "Request credit note" on a tax invoice: per-line quantity and/or value, a reason and a text. Only shown with `invoices.credit_request`. */
export function CreditNoteRequestButton({ detail }: { detail: BuyerInvoiceDetail }) {
  const [open, setOpen] = React.useState(false);
  const [drafts, setDrafts] = React.useState<Record<number, { quantity: string; value: string }>>({});
  const [reason, setReason] = React.useState('');
  const [text, setText] = React.useState('');
  const [problem, setProblem] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [sent, setSent] = React.useState<string | null>(null);
  const attempt = React.useRef<PlaceAttempt | null>(null);
  const inFlight = React.useRef(false);
  const alertRef = React.useRef<HTMLDivElement>(null);
  React.useEffect(() => { if (problem) alertRef.current?.focus(); }, [problem]);

  const set = (lineNo: number, k: 'quantity' | 'value', v: string) => { setProblem(null); setDrafts((d) => ({ ...d, [lineNo]: { quantity: d[lineNo]?.quantity ?? '', value: d[lineNo]?.value ?? '', [k]: v } })); };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (inFlight.current) return;
    const lines: CreditLineDraft[] = detail.lines.map((l) => ({ lineNo: l.lineNo, maxQuantity: l.quantity, maxNetMinor: l.netMinor, quantity: drafts[l.lineNo]?.quantity ?? '', value: drafts[l.lineNo]?.value ?? '' }));
    const built = buildCreditNoteBody(lines, reason, text);
    if ('problem' in built) { setProblem(built.problem); return; }
    inFlight.current = true; setBusy(true); setProblem(null);
    attempt.current = attemptFor(attempt.current, creditRequestFingerprint(detail.invoice.id, built.body));
    try {
      const r = await staffCreditNotesApi.request(detail.invoice.id, built.body, attempt.current.key);
      attempt.current = null;
      setOpen(false); setDrafts({}); setReason(''); setText('');
      setSent(r.id);
    } catch (err) { setProblem(creditNoteFailure(err).text); } finally { inFlight.current = false; setBusy(false); }
  };

  return (
    <div className="space-y-2">
      <Button type="button" variant="outline" onClick={() => { setSent(null); setOpen(true); }} data-testid="request-credit-note">Request credit note</Button>
      {sent && (
        <Notice testId="credit-requested">
          Credit note requested. It has no number yet: another person with the right to approve credit notes must approve it. <Link className="text-brand-accent underline" href={`/invoices/credit-notes/${sent}`} data-testid="credit-requested-link">Open the request</Link>
        </Notice>
      )}
      <Modal open={open} onOpenChange={(o) => { if (!busy) setOpen(o); }} title={`Request a credit note for ${detail.invoice.number}`} description="Choose how much to credit on each line: a quantity, a value, or both. The figures shown are the invoice line; credit notes issued earlier also count, and the server checks them." contentTestId="credit-form">
        <form onSubmit={submit} noValidate className="max-h-[70vh] space-y-4 overflow-y-auto pr-1" aria-label="Request a credit note">
          {problem && <div ref={alertRef} tabIndex={-1} role="alert" data-testid="credit-problem" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{problem}</div>}
          {detail.lines.map((l) => (
            <fieldset key={l.lineNo} className="space-y-2 rounded-md border border-surface-border p-3" data-testid="credit-line">
              <legend className="px-1 text-sm font-semibold">Line {l.lineNo}: {l.descriptionEn}</legend>
              <p className="text-xs text-text-muted">Invoiced: quantity {l.quantity}, net BDT {formatBdt(l.netMinor)}</p>
              <div className="grid gap-3 sm:grid-cols-2">
                <Input label={`Quantity (0 to ${l.quantity})`} inputMode="numeric" value={drafts[l.lineNo]?.quantity ?? ''} onChange={(e) => set(l.lineNo, 'quantity', e.target.value)} disabled={busy} data-testid={`credit-qty-${l.lineNo}`} />
                <Input label="Value in BDT (optional)" inputMode="decimal" helperText="Leave empty to credit the full price of the quantity." value={drafts[l.lineNo]?.value ?? ''} onChange={(e) => set(l.lineNo, 'value', e.target.value)} disabled={busy} data-testid={`credit-value-${l.lineNo}`} />
              </div>
            </fieldset>
          ))}
          <div className="space-y-1.5">
            <label htmlFor="credit-reason" className="text-sm font-medium">Reason</label>
            <select id="credit-reason" className="block h-11 w-full rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm" value={reason} onChange={(e) => { setProblem(null); setReason(e.target.value); }} disabled={busy} data-testid="credit-reason">
              <option value="">Choose a reason</option>
              {CREDIT_REASON_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </div>
          <Textarea label="Explain in words (5 to 500 characters; printed on the credit note)" value={text} onChange={(e) => { setProblem(null); setText(e.target.value); }} rows={3} maxLength={500} disabled={busy} data-testid="credit-text" />
          <ModalFooter>
            <Button type="button" variant="outline" onClick={() => setOpen(false)} disabled={busy}>Cancel</Button>
            <Button type="submit" isLoading={busy} disabled={busy} data-testid="credit-send">Send the request</Button>
          </ModalFooter>
        </form>
      </Modal>
    </div>
  );
}
