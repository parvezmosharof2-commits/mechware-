'use client';

import * as React from 'react';
import Link from 'next/link';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import { WITHHOLDING_KIND_OPTIONS, attemptFor, buildWithholdingBody, withholdingFingerprint, type PlaceAttempt } from '@mechware/api-client';
import { staffWithholdingsApi } from '../lib/api';
import { withholdingFailure } from '../lib/withholdingFailure';
import { Modal, ModalFooter } from './Modal';
import { Notice } from './CreditShared';

const todayDhaka = () => new Date(Date.now() + 6 * 3600_000).toISOString().slice(0, 10);

/** "Record VDS/AIT certificate" on a tax invoice. Only shown with `credit.record_payment`. A cap refusal says what is left, in the server's words. */
export function WithholdingRecordButton({ invoiceId, invoiceNumber }: { invoiceId: string; invoiceNumber: string }) {
  const [open, setOpen] = React.useState(false);
  const [kind, setKind] = React.useState('');
  const [certificateNumber, setCertificateNumber] = React.useState('');
  const [certificateDate, setCertificateDate] = React.useState('');
  const [amount, setAmount] = React.useState('');
  const [problem, setProblem] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [sent, setSent] = React.useState<string | null>(null);
  const attempt = React.useRef<PlaceAttempt | null>(null);
  const inFlight = React.useRef(false);
  const alertRef = React.useRef<HTMLDivElement>(null);
  React.useEffect(() => { if (problem) alertRef.current?.focus(); }, [problem]);
  const edit = (set: (v: string) => void) => (e: { target: { value: string } }) => { setProblem(null); set(e.target.value); };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (inFlight.current) return;
    const built = buildWithholdingBody({ kind, certificateNumber, certificateDate, amount }, todayDhaka());
    if ('problem' in built) { setProblem(built.problem); return; }
    inFlight.current = true; setBusy(true); setProblem(null);
    attempt.current = attemptFor(attempt.current, withholdingFingerprint(invoiceId, built.body));
    try {
      const w = await staffWithholdingsApi.record(invoiceId, built.body, attempt.current.key);
      attempt.current = null;
      setOpen(false); setKind(''); setCertificateNumber(''); setCertificateDate(''); setAmount('');
      setSent(w.id);
    } catch (err) { setProblem(withholdingFailure(err).text); } finally { inFlight.current = false; setBusy(false); }
  };

  return (
    <div className="space-y-2">
      <Button type="button" variant="outline" onClick={() => { setSent(null); setOpen(true); }} data-testid="record-withholding">Record VDS/AIT certificate</Button>
      {sent && (
        <Notice testId="withholding-recorded">
          Certificate recorded. It counts only after another person verifies it. <Link className="text-brand-accent underline" href={`/invoices/withholdings/${sent}`} data-testid="withholding-link">Open the certificate</Link>
        </Notice>
      )}
      <Modal open={open} onOpenChange={(o) => { if (!busy) setOpen(o); }} title={`Record a certificate for ${invoiceNumber}`} description="Copy the figures from the certificate the buyer gave us. VDS cannot be more than the VAT left on the invoice, AIT not more than its net value; earlier certificates and credit notes count." contentTestId="withholding-form">
        <form onSubmit={submit} noValidate className="space-y-4" aria-label="Record a withholding certificate">
          {problem && <div ref={alertRef} tabIndex={-1} role="alert" data-testid="withholding-problem" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{problem}</div>}
          <div className="space-y-1.5">
            <label htmlFor="wh-new-kind" className="text-sm font-medium">Kind</label>
            <select id="wh-new-kind" className="block h-11 w-full rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm" value={kind} onChange={edit(setKind)} disabled={busy} data-testid="withholding-kind">
              <option value="">Choose VDS or AIT</option>
              {WITHHOLDING_KIND_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </div>
          <Input label="Certificate number (as printed)" value={certificateNumber} onChange={edit(setCertificateNumber)} maxLength={40} disabled={busy} data-testid="withholding-number" />
          <Input label="Certificate date" type="date" value={certificateDate} onChange={edit(setCertificateDate)} disabled={busy} data-testid="withholding-date" />
          <Input label="Amount withheld (BDT)" inputMode="decimal" helperText="For example 1500 or 1500.50." value={amount} onChange={edit(setAmount)} disabled={busy} data-testid="withholding-amount" />
          <ModalFooter>
            <Button type="button" variant="outline" onClick={() => setOpen(false)} disabled={busy}>Cancel</Button>
            <Button type="submit" isLoading={busy} disabled={busy} data-testid="withholding-send">Record the certificate</Button>
          </ModalFooter>
        </form>
      </Modal>
    </div>
  );
}
