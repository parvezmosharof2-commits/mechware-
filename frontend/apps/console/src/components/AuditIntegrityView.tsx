'use client';

import * as React from 'react';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import { AUDIT_CHECK_CACHE_MINUTES, auditErrorText, auditVerdict, auditVerdictText, canViewAudit, groupHash } from '@mechware/api-client';
import { problemStatus } from '../lib/api';
import { auditApi } from '../lib/audit';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { FailureAlert, Loading, NoAccess } from './CreditShared';

function Check() {
  const res = useResource(() => auditApi.chain(), 'chain');
  const s = res.data?.data;
  const status = res.error ? problemStatus(res.error) : null;
  const failure = res.error ? { text: auditErrorText({ status }), retry: status === null || status >= 500 } : null;
  const verdict = s ? auditVerdict(s) : null;

  return (
    <div className="space-y-5">
      <p className="text-sm text-text-muted">
        Every audit row is sealed into a hash chain, so a changed, removed or inserted row shows up here. This is the integrity check only: the
        list of events (who did what, when) is not available in the console yet. The check reads the whole log, so one result is shared for
        up to {AUDIT_CHECK_CACHE_MINUTES} minutes.
      </p>
      <FailureAlert failure={failure} onRetry={res.reload} />
      {res.loading && !s && <Loading />}
      {s && verdict && (
        <section aria-label="Audit log check" className="space-y-4" data-testid="audit-check" data-verdict={verdict}>
          <div role={verdict === 'broken' ? 'alert' : 'status'} className={`rounded-md border p-4 text-sm font-medium ${verdict === 'broken' ? 'border-fault text-fault' : 'border-surface-border'}`}>
            <Badge variant={verdict === 'broken' ? 'low-stock' : verdict === 'intact' ? 'success' : 'outline'}>
              {verdict === 'broken' ? 'Broken' : verdict === 'intact' ? 'Intact' : 'Nothing sealed'}
            </Badge>
            <p className="mt-2" data-testid="audit-verdict">{auditVerdictText(s)}</p>
          </div>
          <dl className="grid gap-3 text-sm sm:grid-cols-3">
            <div className="rounded-md border border-surface-border p-3"><dt className="text-xs text-text-muted">Sealed rows</dt><dd className="font-mono text-lg" data-testid="audit-sealed">{s.sealed.toLocaleString('en-US')}</dd></div>
            <div className="rounded-md border border-surface-border p-3"><dt className="text-xs text-text-muted">Not sealed yet (too new)</dt><dd className="font-mono text-lg" data-testid="audit-unsealed">{s.unsealed.toLocaleString('en-US')}</dd></div>
            <div className="rounded-md border border-surface-border p-3"><dt className="text-xs text-text-muted">First broken row</dt><dd className="font-mono text-lg" data-testid="audit-broken">{s.firstBrokenPosition ?? 'none'}</dd></div>
          </dl>
          <div className="rounded-md border border-surface-border p-3 text-sm">
            <p className="text-xs text-text-muted">Head hash (the seal of the newest sealed row; compare it with the copy kept in the daily log)</p>
            <p className="mt-1 break-all font-mono text-xs" data-testid="audit-head">{groupHash(s.headHash)}</p>
          </div>
          <Button type="button" variant="outline" size="sm" onClick={res.reload} disabled={res.loading}>Check again</Button>
        </section>
      )}
    </div>
  );
}

export function AuditIntegrityView() {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="audit-logs" title="Audit log integrity">
      {canViewAudit(user?.permissions ?? []) ? <Check /> : <NoAccess permission="audit.view" />}
    </ConsoleShell>
  );
}
