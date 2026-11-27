'use client';

import * as React from 'react';
import { canEditQuotes, type StaffQuoteDetail } from '@mechware/api-client';
import { deskError, staffQuotesApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { BackLink, FailureAlert, Loading, NoAccess } from './CreditShared';

// The editor itself loads after the page shell, so the route's first load stays under the 170 kB budget.
const EditorBody = React.lazy(() => import('./QuoteEditorBody'));

function Editor({ id, permissions }: { id: string; permissions: readonly string[] }) {
  const res = useResource(() => staffQuotesApi.quote(id), id);
  const [fresh, setFresh] = React.useState<{ detail: StaffQuoteDetail; epoch: number } | null>(null);
  const detail = fresh?.detail ?? res.data?.data ?? null;
  const failure = res.error ? deskError(res.error) : null;
  const loadSaved = React.useCallback(async () => {
    const r = await staffQuotesApi.quote(id);
    setFresh((p) => ({ detail: r.data, epoch: (p?.epoch ?? 0) + 1 }));
  }, [id]);
  if (failure) return <FailureAlert failure={failure} onRetry={res.reload} />;
  if (!detail) return <Loading />;
  return <React.Suspense fallback={<Loading />}><EditorBody key={fresh?.epoch ?? 0} id={id} detail={detail} permissions={permissions} onLoadSaved={loadSaved} /></React.Suspense>;
}

export function QuoteEditorView({ id }: { id: string }) {
  const { user } = useAuth();
  const permissions = user?.permissions ?? [];
  return (
    <ConsoleShell activeItemId="quotes-list" title="Edit quote draft">
      {canEditQuotes(permissions) ? <Editor id={id} permissions={permissions} /> : (
        <div className="space-y-3">
          <NoAccess permission="quotes.edit" />
          <BackLink href={`/quotes/${id}`}>Back to the quote</BackLink>
        </div>
      )}
    </ConsoleShell>
  );
}
