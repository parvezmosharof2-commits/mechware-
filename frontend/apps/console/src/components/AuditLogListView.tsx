'use client';

import * as React from 'react';
import Link from 'next/link';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import {
  EMPTY_AUDIT_FILTERS, activeAuditFilterCount, auditFilterProblem, auditFiltersToSearch, auditReadErrorText, canViewAudit, dhakaWhen, parseAuditFilters,
  type AuditFilters, type AuditListItem
} from '@mechware/api-client';
import { problemStatus } from '../lib/api';
import { auditApi } from '../lib/audit';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { FailureAlert, Loading, NoAccess } from './CreditShared';
import { Pager } from './QuoteDeskShared';

function List() {
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const search = params.toString();
  const applied = React.useMemo(() => parseAuditFilters((k) => new URLSearchParams(search).get(k)), [search]);
  const [draft, setDraft] = React.useState<AuditFilters>(applied);
  const [problem, setProblem] = React.useState<string | null>(null);
  // Back, forward, a shared link or a page change: the form shows what the URL says.
  React.useEffect(() => { setDraft(applied); setProblem(null); }, [applied]);

  const go = (f: AuditFilters) => { const q = auditFiltersToSearch(f); router.push(q ? `${pathname}?${q}` : pathname); };
  const set = (patch: Partial<AuditFilters>) => setDraft((d) => ({ ...d, ...patch }));
  const apply = (e: React.FormEvent) => {
    e.preventDefault();
    const found = auditFilterProblem(draft);
    setProblem(found);
    if (!found) go({ ...draft, actorId: draft.actorId.trim(), action: draft.action.trim(), entityName: draft.entityName.trim(), entityId: draft.entityId.trim(), correlationId: draft.correlationId.trim(), page: 1 });
  };

  const list = useResource(() => auditApi.list(applied), search);
  const data = list.data?.data;
  const status = list.error ? problemStatus(list.error) : null;
  const failure = list.error ? { text: auditReadErrorText({ status }), retry: status === null || status >= 500 || status === 429 } : null;
  const count = activeAuditFilterCount(applied);
  const text = (id: keyof AuditFilters, label: string) => (
    <Input label={label} value={String(draft[id])} onChange={(e) => set({ [id]: e.target.value } as Partial<AuditFilters>)} maxLength={id === 'actorId' ? 36 : 100} autoComplete="off" spellCheck={false} />
  );

  return (
    <div className="space-y-5">
      <p className="text-sm text-text-muted">
        Who did what, newest first. Secret values are hidden by the server. Reading this log is itself recorded. Dates are Dhaka days, both ends included.
        The filters are in the address, so you can share or reload this page. <Link href="/audit" className="font-semibold text-brand-accent underline">Check the integrity of the log</Link>.
      </p>
      <form role="search" aria-label="Filter the audit log" onSubmit={apply} className="space-y-3" noValidate>
        <div className="grid items-end gap-3 sm:grid-cols-2 lg:grid-cols-4">
          {text('actorId', 'Actor id')}
          {text('action', 'Action (exact)')}
          {text('entityName', 'Entity name (exact)')}
          {text('entityId', 'Entity id (exact)')}
          {text('correlationId', 'Correlation id (exact)')}
          <Input label="From (Dhaka day)" type="date" value={draft.from} onChange={(e) => set({ from: e.target.value })} />
          <Input label="To (Dhaka day)" type="date" value={draft.to} onChange={(e) => set({ to: e.target.value })} />
        </div>
        {problem && <p role="alert" data-testid="filter-problem" className="text-sm font-medium text-fault">{problem}</p>}
        <div className="flex flex-wrap items-center gap-3">
          <Button type="submit">Apply filters</Button>
          <Button type="button" variant="outline" disabled={count === 0 && activeAuditFilterCount(draft) === 0} onClick={() => { setDraft(EMPTY_AUDIT_FILTERS); go(EMPTY_AUDIT_FILTERS); }}>Clear all filters</Button>
          <span className="text-xs text-text-muted" data-testid="filter-count">{count === 0 ? 'No filter applied' : `${count} ${count === 1 ? 'filter' : 'filters'} applied`}</span>
        </div>
      </form>

      <FailureAlert failure={failure} onRetry={list.reload} />
      {status === 429 && <p data-testid="rate-limited" className="sr-only">Rate limit reached</p>}
      {list.loading && !data && <Loading />}
      {data && (
        <section aria-label="Audit events" className="space-y-3">
          {data.items.length === 0 ? (
            <p role="status" data-testid="audit-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">
              {count === 0 ? 'The audit log has no rows yet.' : 'No audit row matches these filters. Change or clear a filter.'}
            </p>
          ) : (
            <div className="overflow-x-auto rounded-md border border-surface-border">
              <table className="w-full text-left text-sm" data-testid="audit-table">
                <caption className="sr-only">Audit events, newest first. Page {data.page}, {data.totalCount} rows match.</caption>
                <thead className="bg-bg-subtle text-xs text-text-muted">
                  <tr>
                    <th scope="col" className="px-3 py-2">Time (Dhaka)</th>
                    <th scope="col" className="px-3 py-2">Actor</th>
                    <th scope="col" className="px-3 py-2">Action</th>
                    <th scope="col" className="px-3 py-2">Entity</th>
                    <th scope="col" className="px-3 py-2">Entity id</th>
                    <th scope="col" className="px-3 py-2">Correlation id</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((r: AuditListItem) => (
                    <tr key={r.id} className="border-t border-surface-border align-top" data-testid="audit-row">
                      <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">
                        <Link href={`/audit/log/${r.id}`} className="font-semibold text-brand-accent underline">{dhakaWhen(r.createdAtUtc)}<span className="sr-only">, open the details</span></Link>
                      </td>
                      <td className="px-3 py-2 break-all">{r.actorEmail ?? (r.actorId ? <span className="font-mono text-xs">{r.actorId}</span> : 'System')}</td>
                      <td className="px-3 py-2 font-mono text-xs">{r.action}</td>
                      <td className="px-3 py-2">{r.entityName}</td>
                      <td className="px-3 py-2 break-all font-mono text-xs">{r.entityId}</td>
                      <td className="px-3 py-2 break-all font-mono text-xs">{r.correlationId ?? 'none'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <Pager page={data.page} total={data.totalCount} pageSize={data.pageSize} loading={list.loading} noun="rows" onPage={(p) => go({ ...applied, page: p })} onRefresh={list.reload} />
        </section>
      )}
    </div>
  );
}

export function AuditLogListView() {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="audit-log" title="Audit log">
      {canViewAudit(user?.permissions ?? []) ? <React.Suspense fallback={<Loading />}><List /></React.Suspense> : <NoAccess permission="audit.view" />}
    </ConsoleShell>
  );
}
