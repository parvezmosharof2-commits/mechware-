'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge } from '@mechware/ui/src/components/Badge';
import {
  AUDIT_CHANGE_TEXT, AUDIT_HIDDEN, auditChangeSummary, auditCorrelationHref, auditReadErrorText, buildAuditDiff, canViewAudit, dhakaWhen, type AuditDiffRow
} from '@mechware/api-client';
import { problemStatus } from '../lib/api';
import { auditApi } from '../lib/audit';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { BackLink, FailureAlert, Loading, NoAccess } from './CreditShared';

/** A shown value; the server's "[hidden]" is a badge in words, never the raw text alone. */
function Value({ v }: { v: string | null }) {
  if (v === null) return <span className="text-text-muted">not present</span>;
  if (v === AUDIT_HIDDEN) return <Badge variant="outline" data-testid="hidden-value">Hidden (secret)</Badge>;
  return <span className="break-all font-mono text-xs">{v === '' ? '(empty text)' : v}</span>;
}

function DiffTable({ rows }: { rows: AuditDiffRow[] }) {
  return (
    <div className="overflow-x-auto rounded-md border border-surface-border">
      <table className="w-full text-left text-sm" data-testid="audit-diff">
        <caption className="sr-only">What changed, one row per field. {auditChangeSummary(rows)}.</caption>
        <thead className="bg-bg-subtle text-xs text-text-muted">
          <tr>
            <th scope="col" className="px-3 py-2">Field</th>
            <th scope="col" className="px-3 py-2">Change</th>
            <th scope="col" className="px-3 py-2">Before</th>
            <th scope="col" className="px-3 py-2">After</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.path} className="border-t border-surface-border align-top" data-testid="diff-row" data-change={r.change}>
              <th scope="row" className="break-all px-3 py-2 font-mono text-xs font-medium">{r.path}</th>
              <td className="px-3 py-2"><Badge variant={r.change === 'same' ? 'outline' : 'default'}>{AUDIT_CHANGE_TEXT[r.change]}</Badge></td>
              <td className="px-3 py-2"><Value v={r.before} /></td>
              <td className="px-3 py-2"><Value v={r.after} /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Detail({ id }: { id: string }) {
  const res = useResource(() => auditApi.detail(id), id);
  const r = res.data?.data;
  const status = res.error ? problemStatus(res.error) : null;
  const failure = res.error ? { text: auditReadErrorText({ status }), retry: status === null || status >= 500 || status === 429 } : null;
  const rows = React.useMemo(() => (r ? buildAuditDiff(r.oldValues, r.newValues) : []), [r]);
  const kind = !r ? '' : r.oldValues == null && r.newValues != null ? 'These values were recorded when the entity was created or the event happened.' : r.oldValues != null && r.newValues == null ? 'These values were recorded when the entity was removed.' : '';

  return (
    <div className="space-y-5">
      <BackLink href="/audit/log">Back to the audit log</BackLink>
      <FailureAlert failure={failure} onRetry={res.reload} />
      {res.loading && !r && <Loading />}
      {r && (
        <>
          <dl className="grid gap-3 text-sm sm:grid-cols-2 lg:grid-cols-3" data-testid="audit-facts">
            <div><dt className="text-xs text-text-muted">Time (Dhaka)</dt><dd className="font-mono text-xs">{dhakaWhen(r.createdAtUtc)}</dd></div>
            <div><dt className="text-xs text-text-muted">Action</dt><dd className="font-mono text-xs" data-testid="audit-action">{r.action}</dd></div>
            <div><dt className="text-xs text-text-muted">Actor</dt><dd className="break-all">{r.actorEmail ?? (r.actorId ? <span className="font-mono text-xs">{r.actorId}</span> : 'System')}</dd></div>
            <div><dt className="text-xs text-text-muted">Actor id</dt><dd className="break-all font-mono text-xs">{r.actorId ?? 'none'}</dd></div>
            <div><dt className="text-xs text-text-muted">Actor IP address</dt><dd className="font-mono text-xs">{r.actorIp ?? 'none'}</dd></div>
            <div><dt className="text-xs text-text-muted">Entity</dt><dd>{r.entityName}</dd></div>
            <div><dt className="text-xs text-text-muted">Entity id</dt><dd className="break-all font-mono text-xs">{r.entityId}</dd></div>
            <div>
              <dt className="text-xs text-text-muted">Correlation id</dt>
              <dd className="break-all font-mono text-xs" data-testid="audit-correlation">
                {r.correlationId ? <Link href={auditCorrelationHref(r.correlationId)} className="font-semibold text-brand-accent underline">{r.correlationId}<span className="sr-only">, list every row with this correlation id</span></Link> : 'none'}
              </dd>
            </div>
            <div><dt className="text-xs text-text-muted">Seal position</dt><dd className="font-mono text-xs">{r.chainPosition ?? 'not sealed yet'}</dd></div>
          </dl>
          <section aria-labelledby="diff-h" className="space-y-2">
            <h2 id="diff-h" className="text-base font-semibold">Values</h2>
            <p className="text-sm text-text-muted">Secret values are replaced by the server, and shown here as &quot;Hidden (secret)&quot;. {kind}</p>
            {rows.length === 0 ? (
              <p role="status" data-testid="diff-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">This event recorded no values.</p>
            ) : (
              <>
                <p className="text-sm" data-testid="diff-summary">{auditChangeSummary(rows)}.</p>
                <DiffTable rows={rows} />
              </>
            )}
          </section>
        </>
      )}
    </div>
  );
}

export function AuditLogDetailView({ id }: { id: string }) {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="audit-log" title="Audit row">
      {canViewAudit(user?.permissions ?? []) ? <Detail id={id} /> : <NoAccess permission="audit.view" />}
    </ConsoleShell>
  );
}
