'use client';

import * as React from 'react';
import Link from 'next/link';
import { APPROVAL_PAGE_SIZE, canApproveQuotes, dhakaWhen, queueRows, quoteMoney, waitingText } from '@mechware/api-client';
import { staffQuotesApi } from '../lib/api';
import { issueError } from '../lib/quoteIssue';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { FailureAlert, Loading, NoAccess } from './CreditShared';
import { Pager } from './QuoteDeskShared';

function Queue() {
  const [page, setPage] = React.useState(1);
  const list = useResource(() => staffQuotesApi.approvals(page, APPROVAL_PAGE_SIZE), `approvals|${page}`);
  const data = list.data?.data ?? null;
  const failure = list.error ? issueError(list.error) : null;
  // The wait is worded against the clock at the moment the list arrived, so a re-render never changes the words by itself.
  const [now, setNow] = React.useState(() => Date.now());
  React.useEffect(() => { if (data) setNow(Date.now()); }, [data]);
  const rows = data ? queueRows(data.items) : [];

  return (
    <div className="space-y-5">
      <p className="text-sm text-text-muted">Quote versions that someone sent for approval, the oldest first. Open one to see its lines, tick the lines that need a decision and approve it with your authenticator code.</p>
      <FailureAlert failure={failure} onRetry={list.reload} />
      {list.loading && !data && <Loading />}
      {data && (
        <section aria-label="Quote versions waiting for approval" className="space-y-3">
          {rows.length === 0 ? (
            <p role="status" data-testid="queue-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">Nothing is waiting for approval.</p>
          ) : (
            <div className="relative overflow-x-auto rounded-md border border-surface-border">
              <table className="w-full text-left text-sm" data-testid="quote-approval-queue">
                <caption className="sr-only">Quote versions waiting for approval, oldest first</caption>
                <thead className="bg-bg-subtle text-xs text-text-muted">
                  <tr>
                    <th scope="col" className="px-3 py-2">Quote</th>
                    <th scope="col" className="px-3 py-2">Company</th>
                    <th scope="col" className="px-3 py-2">Sent by</th>
                    <th scope="col" className="px-3 py-2">Waiting</th>
                    <th scope="col" className="px-3 py-2 text-right">Lines</th>
                    <th scope="col" className="px-3 py-2 text-right">Goods, net (BDT)</th>
                    <th scope="col" className="px-3 py-2">Needs a decision on</th>
                    <th scope="col" className="px-3 py-2">For you</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r) => (
                    <tr key={r.key} className="border-t border-surface-border align-top" data-testid="approval-row" data-quote-number={r.number} data-can-approve={r.canApprove ? 'yes' : 'no'}>
                      <td className="px-3 py-2"><Link className="font-mono font-semibold text-brand-accent underline" href={r.href} data-testid="approval-open">{r.number} v{r.versionNo}</Link></td>
                      <td className="px-3 py-2">{r.companyName}</td>
                      <td className="px-3 py-2">{r.sender}</td>
                      <td className="px-3 py-2"><span data-testid="approval-wait">{waitingText(r.submittedAt, now)}</span><span className="block font-mono text-xs text-text-muted">since {dhakaWhen(r.submittedAt)}</span></td>
                      <td className="px-3 py-2 text-right">{r.lines}</td>
                      <td className="px-3 py-2 text-right font-mono">{quoteMoney(r.goodsNetMinor)}</td>
                      <td className="px-3 py-2" data-testid="approval-flags">{r.flags.length === 0 ? <span className="text-text-muted">Nothing flagged</span> : <ul className="space-y-0.5 text-xs font-medium">{r.flags.map((f) => <li key={f}>{f}</li>)}</ul>}</td>
                      <td className="px-3 py-2 text-xs" data-testid="approval-can"><span className="font-semibold">{r.canApprove ? 'You can decide' : 'Not you'}</span><span className="block text-text-muted">{r.hint}</span></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <Pager page={data.page} total={data.total} pageSize={data.pageSize} loading={list.loading} noun="waiting" onPage={setPage} onRefresh={list.reload} />
        </section>
      )}
    </div>
  );
}

export function QuoteApprovalsView() {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="quotes-approvals" title="Quote approvals">
      {canApproveQuotes(user?.permissions ?? []) ? <Queue /> : <NoAccess permission="quotes.approve" />}
    </ConsoleShell>
  );
}
