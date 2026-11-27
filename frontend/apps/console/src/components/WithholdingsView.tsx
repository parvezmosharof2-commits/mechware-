'use client';

import * as React from 'react';
import Link from 'next/link';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import {
  canViewInvoices, formatBdt, WITHHOLDING_KIND_OPTIONS, WITHHOLDING_PAGE_SIZE, WITHHOLDING_STATUS_OPTIONS, withholdingFiltersFromSearch, withholdingFiltersToSearch,
  withholdingKindText, withholdingStatusText, type WithholdingFilters
} from '@mechware/api-client';
import { staffWithholdingsApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { withholdingFailure } from '../lib/withholdingFailure';
import { FailureAlert, Loading, NoAccess } from './CreditShared';
import { Pager } from './QuoteDeskShared';

const selectClass = 'block h-11 w-full rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm';

function List({ meId }: { meId: string }) {
  const router = useRouter();
  const pathname = usePathname();
  const search = useSearchParams().toString();
  const applied = React.useMemo(() => withholdingFiltersFromSearch(new URLSearchParams(search)), [search]);
  const [draft, setDraft] = React.useState<WithholdingFilters>(applied);
  React.useEffect(() => { setDraft(applied); }, [applied]);
  const go = (f: WithholdingFilters) => { const q = withholdingFiltersToSearch(f); router.push(q ? `${pathname}?${q}` : pathname); };
  const set = (patch: Partial<WithholdingFilters>) => setDraft((d) => ({ ...d, ...patch }));

  const res = useResource(() => staffWithholdingsApi.list(applied), search);
  const data = res.data;
  const failure = res.error ? { text: withholdingFailure(res.error).text, retry: true } : null;
  return (
    <div className="space-y-4">
      <p className="text-sm text-text-muted">VDS and AIT certificates that buyers gave us. A certificate only counts once a second person verifies it with their authenticator code; then it settles part of what the company owes on credit. The oldest come first.</p>
      <form onSubmit={(e) => { e.preventDefault(); go({ ...draft, invoiceId: draft.invoiceId.trim(), page: 1 }); }} aria-label="Filter certificates" className="flex flex-wrap items-end gap-4">
        <div className="space-y-1.5">
          <label htmlFor="wh-status" className="text-sm font-medium">Status</label>
          <select id="wh-status" className={selectClass} value={draft.status} onChange={(e) => set({ status: e.target.value })} data-testid="wh-status">
            {WITHHOLDING_STATUS_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
          </select>
        </div>
        <div className="space-y-1.5">
          <label htmlFor="wh-kind" className="text-sm font-medium">Kind</label>
          <select id="wh-kind" className={selectClass} value={draft.kind} onChange={(e) => set({ kind: e.target.value })} data-testid="wh-kind">
            <option value="">Any kind</option>
            {WITHHOLDING_KIND_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
          </select>
        </div>
        <Input label="Invoice id (optional)" value={draft.invoiceId} onChange={(e) => set({ invoiceId: e.target.value })} data-testid="wh-invoice" />
        <Button type="submit" data-testid="wh-apply">Apply</Button>
      </form>
      <FailureAlert failure={failure} onRetry={res.reload} />
      {res.loading && !data && <Loading />}
      {data && (data.items.length === 0 ? (
        <p role="status" data-testid="wh-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">No certificate matches these filters.</p>
      ) : (
        <div className="overflow-x-auto rounded-md border border-surface-border" role="region" tabIndex={0} aria-label="Withholding certificates">
          <table className="w-full text-left text-sm" data-testid="wh-table">
            <caption className="sr-only">Withholding certificates, oldest first</caption>
            <thead className="bg-bg-subtle text-xs text-text-muted">
              <tr><th scope="col" className="px-3 py-2">Certificate</th><th scope="col" className="px-3 py-2">Kind</th><th scope="col" className="px-3 py-2">Invoice</th><th scope="col" className="px-3 py-2">Company</th><th scope="col" className="px-3 py-2 text-right">Amount (BDT)</th><th scope="col" className="px-3 py-2">Status</th></tr>
            </thead>
            <tbody>
              {data.items.map((w) => (
                <tr key={w.id} className="border-t border-surface-border align-top" data-testid="wh-row">
                  <td className="px-3 py-2"><Link href={`/invoices/withholdings/${w.id}`} className="font-mono font-semibold text-brand-accent underline">{w.certificateNumber}</Link>
                    <span className="block font-mono text-xs text-text-muted">{w.certificateDate}{w.recordedBy.toLowerCase() === meId.toLowerCase() ? ' · recorded by you' : ''}</span></td>
                  <td className="px-3 py-2">{withholdingKindText(w.kind)}</td>
                  <td className="px-3 py-2"><Link href={`/invoices/${w.invoiceId}`} className="font-mono text-brand-accent underline">{w.invoiceNumber}</Link></td>
                  <td className="px-3 py-2">{w.companyName}</td>
                  <td className="whitespace-nowrap px-3 py-2 text-right font-mono text-xs">{formatBdt(w.amountMinor)}</td>
                  <td className="px-3 py-2"><Badge variant="outline">{withholdingStatusText(w.status)}</Badge></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ))}
      {data && data.totalCount > WITHHOLDING_PAGE_SIZE && <Pager page={data.page} pageSize={data.pageSize} total={data.totalCount} loading={res.loading} noun="certificates" onPage={(p) => go({ ...applied, page: p })} onRefresh={res.reload} />}
    </div>
  );
}

export function WithholdingsView() {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="withholdings" title="Withholdings (VDS/AIT)">
      {canViewInvoices(user?.permissions ?? []) && user ? <List meId={user.id} /> : <NoAccess permission="invoices.view" />}
    </ConsoleShell>
  );
}
