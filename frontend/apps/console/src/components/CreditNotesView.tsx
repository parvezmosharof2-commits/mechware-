'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge } from '@mechware/ui/src/components/Badge';
import { canViewInvoices, creditReasonText, creditStatusText, dhakaWhen, formatBdt, CREDIT_QUEUE_PAGE_SIZE } from '@mechware/api-client';
import { staffCreditNotesApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { creditNoteFailure } from './CreditNoteRequestButton';
import { FailureAlert, Loading, NoAccess } from './CreditShared';
import { Pager } from './QuoteDeskShared';

function Queue({ meId }: { meId: string }) {
  const [page, setPage] = React.useState(1);
  const res = useResource(() => staffCreditNotesApi.queue(page), `cn|${page}`);
  const data = res.data;
  const failure = res.error ? { text: creditNoteFailure(res.error).text, retry: true } : null;
  return (
    <div className="space-y-4">
      <p className="text-sm text-text-muted">Credit notes that someone asked for and nobody has decided yet, the oldest first. A request has no number: the 6.7 is issued only when another person approves it with their authenticator code.</p>
      <FailureAlert failure={failure} onRetry={res.reload} />
      {res.loading && !data && <Loading />}
      {data && (data.items.length === 0 ? (
        <p role="status" data-testid="credit-queue-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">No credit note is waiting for a decision.</p>
      ) : (
        <div className="overflow-x-auto rounded-md border border-surface-border" role="region" tabIndex={0} aria-label="Credit note requests">
          <table className="w-full text-left text-sm" data-testid="credit-queue">
            <caption className="sr-only">Credit note requests waiting for a decision, oldest first</caption>
            <thead className="bg-bg-subtle text-xs text-text-muted">
              <tr><th scope="col" className="px-3 py-2">Invoice</th><th scope="col" className="px-3 py-2">Order</th><th scope="col" className="px-3 py-2">Reason</th><th scope="col" className="px-3 py-2 text-right">Total (BDT)</th><th scope="col" className="px-3 py-2">Asked</th><th scope="col" className="px-3 py-2">Status</th></tr>
            </thead>
            <tbody>
              {data.items.map((r) => (
                <tr key={r.id} className="border-t border-surface-border align-top" data-testid="credit-row" data-invoice={r.originalNumber}>
                  <td className="px-3 py-2"><Link href={`/invoices/credit-notes/${r.id}`} className="font-mono font-semibold text-brand-accent underline" data-testid="credit-open">{r.originalNumber}</Link></td>
                  <td className="px-3 py-2">{r.orderNumber}</td>
                  <td className="px-3 py-2">{creditReasonText(r.reason)}</td>
                  <td className="whitespace-nowrap px-3 py-2 text-right font-mono text-xs">{formatBdt(r.totalMinor)}</td>
                  <td className="px-3 py-2 font-mono text-xs">{dhakaWhen(r.requestedAtUtc)}{r.requestedBy.toLowerCase() === meId.toLowerCase() && <span className="block font-sans font-medium">Asked by you</span>}</td>
                  <td className="px-3 py-2"><Badge variant="outline">{creditStatusText(r.status)}</Badge></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ))}
      {data && data.totalCount > CREDIT_QUEUE_PAGE_SIZE && <Pager page={data.page} pageSize={data.pageSize} total={data.totalCount} loading={res.loading} noun="requests" onPage={setPage} onRefresh={res.reload} />}
    </div>
  );
}

export function CreditNotesView() {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="credit-notes" title="Credit notes">
      {canViewInvoices(user?.permissions ?? []) && user ? <Queue meId={user.id} /> : <NoAccess permission="invoices.view" />}
    </ConsoleShell>
  );
}
