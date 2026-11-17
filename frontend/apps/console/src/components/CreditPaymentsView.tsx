'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import { Skeleton } from '@mechware/ui/src/components/Skeleton';
import {
  CREDIT_PAGE_SIZE, CREDIT_QUEUE_STATUSES, CREDIT_STATUS_LABEL, canRecordCredit, canViewCredit, flagsOf, formatBdt, methodLabel, shortWhen, sortQueue,
  type CreditQueueStatus
} from '@mechware/api-client';
import { creditError, creditPaymentsApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';

const FILTER_LABEL: Record<CreditQueueStatus, string> = {
  recorded: 'Waiting for verification', verified: 'Verified', rejected: 'Rejected', bounced: 'Bounced', all: 'All payments'
};

function Queue({ canRecord }: { canRecord: boolean }) {
  const [status, setStatus] = React.useState<CreditQueueStatus>('recorded');
  const [page, setPage] = React.useState(1);
  const list = useResource(() => creditPaymentsApi.list({ status, page, pageSize: CREDIT_PAGE_SIZE }), `${status}|${page}`);
  const data = list.data?.data;
  const pages = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1;
  const failure = list.error ? creditError(list.error) : null;
  const items = data ? sortQueue(data.items, status) : [];

  return (
    <div className="space-y-5">
      <p className="text-sm text-text-muted">
        A payment counts against a company&apos;s credit only after a person other than the one who recorded it verifies it. The oldest waiting payment is first.
      </p>
      <div className="flex flex-wrap items-end gap-4">
        <label className="space-y-1 text-sm font-semibold">
          <span className="block">Show</span>
          <select
            value={status}
            onChange={(e) => { setStatus(e.target.value as CreditQueueStatus); setPage(1); }}
            className="block rounded-md border border-surface-border bg-surface p-2 text-sm font-normal"
            data-testid="status-filter"
          >
            {CREDIT_QUEUE_STATUSES.map((s) => <option key={s} value={s}>{FILTER_LABEL[s]}</option>)}
          </select>
        </label>
        {canRecord && <Link href="/credit/payments/new" className="rounded-md border border-brand-accent px-3 py-2 text-sm font-semibold text-brand-accent underline">Record a payment</Link>}
      </div>

      {failure && (
        <div role="alert" className="flex flex-wrap items-center gap-3 rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">
          <span>{failure.text}</span>
          {failure.retry && <Button type="button" variant="outline" size="sm" onClick={list.reload}>Try again</Button>}
        </div>
      )}
      {list.loading && !data && <div role="status"><span className="sr-only">Loading</span><Skeleton className="h-40 w-full" /></div>}
      {data && (
        <section aria-label="Credit payments" className="space-y-3">
          {items.length === 0 ? (
            <p role="status" data-testid="queue-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">
              {status === 'recorded' ? 'No payment is waiting for verification.' : 'No payment matches.'}
            </p>
          ) : (
            <div className="overflow-x-auto rounded-md border border-surface-border">
              <table className="w-full text-left text-sm" data-testid="payment-queue">
                <caption className="sr-only">Credit payments: {FILTER_LABEL[status]}{status === 'recorded' ? ', oldest first' : ', newest first'}</caption>
                <thead className="bg-bg-subtle text-xs text-text-muted">
                  <tr>
                    <th scope="col" className="px-3 py-2">Company</th>
                    <th scope="col" className="px-3 py-2">Method and reference</th>
                    <th scope="col" className="px-3 py-2 text-right">Amount (BDT)</th>
                    <th scope="col" className="px-3 py-2">Received</th>
                    <th scope="col" className="px-3 py-2">Flags</th>
                    <th scope="col" className="px-3 py-2">Status</th>
                    <th scope="col" className="px-3 py-2">Recorded</th>
                  </tr>
                </thead>
                <tbody>
                  {items.map((item) => {
                    const p = item.payment;
                    return (
                      <tr key={p.id} className="border-t border-surface-border align-top" data-testid="queue-row">
                        <td className="px-3 py-2">{item.companyName}</td>
                        <td className="px-3 py-2">
                          <Link className="font-semibold text-brand-accent underline" href={`/credit/payments/${p.id}`}>{methodLabel(p.method)} <span className="font-mono">{p.reference}</span></Link>
                        </td>
                        <td className="px-3 py-2 text-right font-mono">{formatBdt(p.amountMinor)}</td>
                        <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">{p.receivedOn}{p.chequeDate ? <><br />cheque {p.chequeDate}</> : null}</td>
                        <td className="px-3 py-2"><span className="inline-flex flex-wrap gap-1">{flagsOf(item).map((f) => <Badge key={f} variant="outline">{f}</Badge>)}</span></td>
                        <td className="px-3 py-2">{CREDIT_STATUS_LABEL[p.status]}</td>
                        <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">{shortWhen(p.recordedAtUtc)}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
          <div className="flex flex-wrap items-center gap-3 text-xs">
            <Button type="button" variant="outline" size="sm" disabled={page <= 1 || list.loading} onClick={() => setPage(page - 1)}>Previous</Button>
            <span aria-live="polite">Page {data.page} of {pages} ({data.totalCount} payments)</span>
            <Button type="button" variant="outline" size="sm" disabled={page >= pages || list.loading} onClick={() => setPage(page + 1)}>Next</Button>
            <Button type="button" variant="ghost" size="sm" onClick={list.reload}>Refresh</Button>
          </div>
        </section>
      )}
    </div>
  );
}

export function CreditPaymentsView() {
  const { user } = useAuth();
  const permissions = user?.permissions ?? [];
  return (
    <ConsoleShell activeItemId="credit-payments" title="Credit payments">
      {canViewCredit(permissions) ? (
        <Queue canRecord={canRecordCredit(permissions)} />
      ) : (
        <p role="alert" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">Your role does not allow this action (credit.view).</p>
      )}
    </ConsoleShell>
  );
}
