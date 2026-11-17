'use client';

import * as React from 'react';
import Link from 'next/link';
import { Button } from '@mechware/ui/src/components/Button';
import {
  ADJUSTMENT_PAGE_SIZE, ADJUSTMENT_QUEUE_STATUSES, ADJUSTMENT_STATUS_LABEL, adjustmentSummary, canViewCredit, shortWhen, type AdjustmentQueueStatus
} from '@mechware/api-client';
import { creditError } from '../lib/api';
import { creditBooksApi } from '../lib/creditBooks';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { FailureAlert, Loading, NoAccess } from './CreditShared';

const FILTER_LABEL: Record<AdjustmentQueueStatus, string> = { requested: 'Waiting for approval', approved: 'Approved', rejected: 'Rejected', all: 'All adjustments' };

function Queue({ companyId }: { companyId?: string }) {
  const [status, setStatus] = React.useState<AdjustmentQueueStatus>('requested');
  const [page, setPage] = React.useState(1);
  const res = useResource(() => creditBooksApi.adjustments({ status, companyId, page }), `${status}|${companyId ?? ''}|${page}`);
  const data = res.data?.data;
  const pages = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1;

  return (
    <div className="space-y-5">
      <p className="text-sm text-text-muted">An adjustment changes what a company owes or what it has on account. It takes effect only when an Admin other than the requester approves it. The oldest waiting request is first.</p>
      {companyId && <p className="text-sm">Only one company is shown. <Link className="font-semibold text-brand-accent underline" href="/credit/adjustments">Show all companies</Link></p>}
      <label className="block space-y-1 text-sm font-semibold">
        <span className="block">Show</span>
        <select value={status} onChange={(e) => { setStatus(e.target.value as AdjustmentQueueStatus); setPage(1); }} className="block rounded-md border border-surface-border bg-surface p-2 text-sm font-normal" data-testid="adjust-status-filter">
          {ADJUSTMENT_QUEUE_STATUSES.map((s) => <option key={s} value={s}>{FILTER_LABEL[s]}</option>)}
        </select>
      </label>
      <FailureAlert failure={res.error ? creditError(res.error) : null} onRetry={res.reload} />
      {res.loading && !data && <Loading />}
      {data && (
        <section aria-label="Adjustments" className="space-y-3">
          {data.items.length === 0 ? (
            <p role="status" data-testid="adjust-queue-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">{status === 'requested' ? 'No adjustment is waiting for approval.' : 'No adjustment matches.'}</p>
          ) : (
            <div className="overflow-x-auto rounded-md border border-surface-border">
              <table className="w-full text-left text-sm" data-testid="adjust-queue">
                <caption className="sr-only">Credit adjustments: {FILTER_LABEL[status]}</caption>
                <thead className="bg-bg-subtle text-xs text-text-muted">
                  <tr>
                    <th scope="col" className="px-3 py-2">Company</th>
                    <th scope="col" className="px-3 py-2">Adjustment</th>
                    <th scope="col" className="px-3 py-2">Reason</th>
                    <th scope="col" className="px-3 py-2">Status</th>
                    <th scope="col" className="px-3 py-2">Requested</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map(({ adjustment: a, companyName }) => (
                    <tr key={a.id} className="border-t border-surface-border align-top" data-testid="adjust-row">
                      <td className="px-3 py-2">{companyName}</td>
                      <td className="px-3 py-2"><Link className="font-semibold text-brand-accent underline" href={`/credit/adjustments/${a.id}`}>{adjustmentSummary(a)}</Link></td>
                      <td className="max-w-xs px-3 py-2">{a.reason}</td>
                      <td className="px-3 py-2">{ADJUSTMENT_STATUS_LABEL[a.status]}</td>
                      <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">{shortWhen(a.requestedAtUtc)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <div className="flex flex-wrap items-center gap-3 text-xs">
            <Button type="button" variant="outline" size="sm" disabled={page <= 1 || res.loading} onClick={() => setPage(page - 1)}>Previous</Button>
            <span aria-live="polite">Page {data.page} of {pages} ({data.totalCount} adjustments, {ADJUSTMENT_PAGE_SIZE} a page)</span>
            <Button type="button" variant="outline" size="sm" disabled={page >= pages || res.loading} onClick={() => setPage(page + 1)}>Next</Button>
            <Button type="button" variant="ghost" size="sm" onClick={res.reload}>Refresh</Button>
          </div>
        </section>
      )}
    </div>
  );
}

export function CreditAdjustmentsView({ companyId }: { companyId?: string }) {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="credit-adjustments" title="Credit adjustments">
      {canViewCredit(user?.permissions ?? []) ? <Queue companyId={companyId} /> : <NoAccess permission="credit.view" />}
    </ConsoleShell>
  );
}
