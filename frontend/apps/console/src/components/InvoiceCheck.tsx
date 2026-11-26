'use client';

import * as React from 'react';
import Link from 'next/link';
import { documentCheckText, sha256Hex, type InvoiceDocumentMatch } from '@mechware/api-client';
import { staffInvoicesApi } from '../lib/api';
import { invoiceFailure } from './InvoicePdfButtons';

type Outcome = { kind: 'match'; match: InvoiceDocumentMatch } | { kind: 'none' } | { kind: 'error'; text: string };

/**
 * "Check a PDF": the file is read and hashed in this browser (Web Crypto SHA-256); only the 64-character hash is sent to
 * `admin/invoice-documents/{sha256}`. The bytes never leave the page.
 */
export function InvoiceCheck() {
  const id = React.useId();
  const [busy, setBusy] = React.useState(false);
  const [name, setName] = React.useState('');
  const [outcome, setOutcome] = React.useState<Outcome | null>(null);

  const onFile = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) return;
    setBusy(true); setOutcome(null); setName(file.name);
    try {
      const match = await staffInvoicesApi.check(await sha256Hex(await file.arrayBuffer()));
      setOutcome(match ? { kind: 'match', match } : { kind: 'none' });
    } catch (err) {
      setOutcome({ kind: 'error', text: invoiceFailure(err) });
    } finally { setBusy(false); }
  };

  return (
    <section aria-labelledby={id} className="space-y-2 rounded-md border border-surface-border p-4" data-testid="invoice-check">
      <h2 id={id} className="text-base font-semibold">Check a PDF</h2>
      <p className="text-sm text-text-muted">Choose a PDF someone gave you to see whether we issued it. The file is checked in your browser; only its SHA-256 fingerprint is sent, never the file.</p>
      <label className="block space-y-1.5 text-sm font-medium">
        PDF file
        <input type="file" accept="application/pdf,.pdf" disabled={busy} onChange={(e) => void onFile(e)} data-testid="check-file"
          className="block w-full text-sm file:mr-3 file:h-11 file:rounded-md file:border file:border-surface-border-strong file:bg-bg-muted file:px-3 file:text-sm" />
      </label>
      <div role="status" aria-live="polite" data-testid="check-result" className="text-sm">
        {busy && 'Checking...'}
        {outcome?.kind === 'match' && (
          <p data-testid="check-match" className="font-medium">
            <span className="font-mono">{name}</span>: {documentCheckText(outcome.match)}{' '}
            <Link href={`/invoices/${outcome.match.invoiceId}`} className="text-brand-accent underline">Open the invoice</Link>
          </p>
        )}
        {outcome?.kind === 'none' && <p data-testid="check-none" className="font-medium"><span className="font-mono">{name}</span>: {documentCheckText(null)}</p>}
        {outcome?.kind === 'error' && <p data-testid="check-error" className="font-medium text-fault">{outcome.text}</p>}
      </div>
    </section>
  );
}
