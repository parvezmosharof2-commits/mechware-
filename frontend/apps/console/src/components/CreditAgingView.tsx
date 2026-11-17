'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import {
  AGING_BUCKETS, AGING_PAGE_SIZE, agingCsvName, canViewCredit, dhakaToday, formatBdt, holdText, isCreditStepUpFailure, overdueText, type CreditAgingRow
} from '@mechware/api-client';
import { creditError } from '../lib/api';
import { creditBooksApi } from '../lib/creditBooks';
import { useAuth } from '../lib/auth';
import { saveFile } from '../lib/download';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { FailureAlert, FocusAlert, Loading, NoAccess, Notice, useFocusOn } from './CreditShared';
import { StepUpDialog } from './StepUpDialog';

function Row({ row }: { row: CreditAgingRow }) {
  return (
    <tr className="border-t border-surface-border align-top" data-testid="aging-row">
      <th scope="row" className="px-3 py-2 text-left font-semibold">
        <Link className="text-brand-accent underline" href={`/credit/companies/${row.companyId}`}>{row.companyName}</Link>
        <span className="mt-1 flex flex-wrap gap-1">
          {row.onHold && <Badge variant="low-stock">On hold</Badge>}
          {!row.onHold && row.holdOverrideUntilUtc && Date.parse(row.holdOverrideUntilUtc) > Date.now() && <Badge variant="outline">Hold lifted</Badge>}
        </span>
        <span className="sr-only">{holdText(row.onHold, row.holdOverrideUntilUtc)}</span>
      </th>
      <td className="px-3 py-2 text-right">{row.openOrders}</td>
      {AGING_BUCKETS.map((b) => <td key={b.key} className="px-3 py-2 text-right font-mono">{formatBdt(row.buckets[b.key])}</td>)}
      <td className="px-3 py-2 text-right font-mono font-semibold">{formatBdt(row.receivableMinor)}</td>
      <td className="px-3 py-2 text-right font-mono">{formatBdt(row.onAccountMinor)}</td>
      <td className="px-3 py-2 text-xs">{overdueText(row.maxDaysPastDue)}<br /><span className="font-mono">oldest due {row.oldestDueOn}</span></td>
    </tr>
  );
}

function Aging() {
  const [term, setTerm] = React.useState('');
  const [q, setQ] = React.useState('');
  const [overdueOnly, setOverdueOnly] = React.useState(false);
  const [page, setPage] = React.useState(1);
  const [csvOpen, setCsvOpen] = React.useState(false);
  const [busy, setBusy] = React.useState(false);
  const [codeError, setCodeError] = React.useState<string | null>(null);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const [notice, setNotice] = React.useState<string | null>(null);
  const alertRef = useFocusOn(alertText);

  const res = useResource(() => creditBooksApi.aging({ q, overdueOnly, page }), `${q}|${overdueOnly}|${page}`);
  const data = res.data?.data;
  const pages = data ? Math.max(1, Math.ceil(data.companies.totalCount / data.companies.pageSize)) : 1;
  const failure = res.error ? creditError(res.error) : null;

  const download = async (code: string) => {
    setBusy(true);
    setCodeError(null);
    try {
      const bytes = await creditBooksApi.agingCsv(overdueOnly, code);
      saveFile(agingCsvName(overdueOnly, dhakaToday()), bytes);
      setCsvOpen(false);
      setAlertText(null);
      setNotice(`The file ${agingCsvName(overdueOnly, dhakaToday())} was downloaded. The export is recorded in the audit trail.`);
    } catch (err) {
      const f = creditError(err);
      if (isCreditStepUpFailure(f.code)) setCodeError(f.text);
      else { setCsvOpen(false); setNotice(null); setAlertText(f.status === 429 ? 'You exported too many times in a minute. Wait a minute and try again.' : f.text); }
    } finally { setBusy(false); }
  };

  return (
    <div className="space-y-5">
      <p className="text-sm text-text-muted">What each company still owes, by days past the due date. Only the part still owed counts. Hold means new credit orders are refused (15 days past due by default).</p>
      <form
        className="flex flex-wrap items-end gap-4"
        aria-label="Aging filters"
        onSubmit={(e) => { e.preventDefault(); setQ(term.trim()); setPage(1); }}
      >
        <Input label="Company name" type="search" value={term} onChange={(e) => setTerm(e.target.value)} autoComplete="off" data-testid="aging-q" />
        <label className="flex items-center gap-2 pb-2 text-sm font-semibold">
          <input type="checkbox" checked={overdueOnly} onChange={(e) => { setOverdueOnly(e.target.checked); setPage(1); }} data-testid="aging-overdue" />
          Only companies with something overdue
        </label>
        <Button type="submit" variant="outline">Search</Button>
        <Button type="button" onClick={() => { setNotice(null); setAlertText(null); setCodeError(null); setCsvOpen(true); }} data-testid="aging-csv">Download CSV</Button>
      </form>

      {notice && <Notice testId="aging-notice">{notice}</Notice>}
      {alertText && <FocusAlert ref={alertRef} testId="aging-alert">{alertText}</FocusAlert>}
      <FailureAlert failure={failure} onRetry={res.reload} />
      {res.loading && !data && <Loading />}

      {data && (
        <section aria-label="Aging report" className="space-y-3">
          <p className="text-sm" data-testid="aging-totals">
            As of <span className="font-mono">{data.asOf}</span>. Owed in total <strong className="font-mono">BDT {formatBdt(data.receivableMinor)}</strong>, of which overdue{' '}
            <strong className="font-mono">BDT {formatBdt(data.overdueMinor)}</strong> (all {data.companies.totalCount} matching companies, not only this page).
          </p>
          {data.companies.items.length === 0 ? (
            <p role="status" data-testid="aging-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">No company owes anything{q || overdueOnly ? ' that matches' : ''}.</p>
          ) : (
            <div className="overflow-x-auto rounded-md border border-surface-border">
              <table className="w-full text-left text-sm" data-testid="aging-table">
                <caption className="sr-only">Open receivables by company and days past due, in BDT. Most overdue first.</caption>
                <thead className="bg-bg-subtle text-xs text-text-muted">
                  <tr>
                    <th scope="col" className="px-3 py-2">Company</th>
                    <th scope="col" className="px-3 py-2 text-right">Open orders</th>
                    {AGING_BUCKETS.map((b) => <th key={b.key} scope="col" className="px-3 py-2 text-right">{b.label}</th>)}
                    <th scope="col" className="px-3 py-2 text-right">Owed</th>
                    <th scope="col" className="px-3 py-2 text-right">On account</th>
                    <th scope="col" className="px-3 py-2">Oldest</th>
                  </tr>
                </thead>
                <tbody>{data.companies.items.map((r) => <Row key={r.companyId} row={r} />)}</tbody>
                <tfoot>
                  <tr className="border-t-2 border-surface-border bg-bg-subtle font-semibold" data-testid="aging-total-row">
                    <th scope="row" className="px-3 py-2 text-left">Total, all matching companies</th>
                    <td />
                    {AGING_BUCKETS.map((b) => <td key={b.key} className="px-3 py-2 text-right font-mono">{formatBdt(data.totals[b.key])}</td>)}
                    <td className="px-3 py-2 text-right font-mono">{formatBdt(data.receivableMinor)}</td>
                    <td colSpan={2} />
                  </tr>
                </tfoot>
              </table>
            </div>
          )}
          <div className="flex flex-wrap items-center gap-3 text-xs">
            <Button type="button" variant="outline" size="sm" disabled={page <= 1 || res.loading} onClick={() => setPage(page - 1)}>Previous</Button>
            <span aria-live="polite">Page {data.companies.page} of {pages} ({data.companies.totalCount} companies, {AGING_PAGE_SIZE} a page)</span>
            <Button type="button" variant="outline" size="sm" disabled={page >= pages || res.loading} onClick={() => setPage(page + 1)}>Next</Button>
            <Button type="button" variant="ghost" size="sm" onClick={res.reload}>Refresh</Button>
          </div>
        </section>
      )}

      <StepUpDialog
        open={csvOpen}
        title="Download the aging of every company"
        description={`This file lists every company's debts${overdueOnly ? ' (only those with something overdue)' : ''}. Your authenticator code is needed, the download is recorded in the audit trail, and you can export 6 times a minute.`}
        confirmLabel="Download"
        busy={busy}
        error={codeError}
        onCancel={() => { if (!busy) setCsvOpen(false); }}
        onSubmit={download}
      />
    </div>
  );
}

export function CreditAgingView() {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="credit-aging" title="Credit aging">
      {canViewCredit(user?.permissions ?? []) ? <Aging /> : <NoAccess permission="credit.view" />}
    </ConsoleShell>
  );
}
