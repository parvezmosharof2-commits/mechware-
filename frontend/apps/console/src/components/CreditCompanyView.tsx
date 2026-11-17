'use client';

import * as React from 'react';
import Link from 'next/link';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import {
  AGING_BUCKETS, STATEMENT_PAGE_SIZE, canAdjustCredit, canOverrideHold, canRecordCredit, canViewCredit, dhakaToday, formatBdt, ledgerKindText, overdueText,
  signedBdt, statementCsvName, statementRangeProblem
} from '@mechware/api-client';
import { creditError } from '../lib/api';
import { creditBooksApi } from '../lib/creditBooks';
import { useAuth } from '../lib/auth';
import { saveFile } from '../lib/download';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { CreditHoldPanel } from './CreditHoldPanel';
import { BackLink, FailureAlert, FocusAlert, Loading, NoAccess, Notice, useFocusOn } from './CreditShared';

function Books({ companyId }: { companyId: string }) {
  const { user } = useAuth();
  const permissions = user?.permissions ?? [];
  const [from, setFrom] = React.useState('');
  const [to, setTo] = React.useState('');
  const [range, setRange] = React.useState({ from: '', to: '' });
  const [rangeProblem, setRangeProblem] = React.useState<string | null>(null);
  const [page, setPage] = React.useState(1);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const alertRef = useFocusOn(alertText);

  const statement = useResource(() => creditBooksApi.statement(companyId, { ...range, page }), `${companyId}|${range.from}|${range.to}|${page}`);
  // The aging row of this one company (it has none when it owes nothing): hold state, buckets, on-account.
  const aging = useResource(() => creditBooksApi.aging({ q: '', overdueOnly: false, page: 1, companyId }), companyId);
  const st = statement.data?.data;
  const row = aging.data?.data.companies.items[0] ?? null;
  const pages = st ? Math.max(1, Math.ceil(st.lines.totalCount / st.lines.pageSize)) : 1;
  const failure = statement.error ? creditError(statement.error) : null;

  const apply = (e: React.FormEvent) => {
    e.preventDefault();
    const p = statementRangeProblem(from, to);
    setRangeProblem(p);
    if (!p) { setRange({ from, to }); setPage(1); }
  };

  const download = async () => {
    const p = statementRangeProblem(from, to);
    setRangeProblem(p);
    if (p || !st) return;
    setBusy(true); setNotice(null); setAlertText(null);
    try {
      const bytes = await creditBooksApi.statementCsv(companyId, { from, to });
      const name = statementCsvName(st.companyName, dhakaToday());
      saveFile(name, bytes);
      setNotice(`The file ${name} was downloaded. The export is recorded in the audit trail.`);
    } catch (err) {
      const f = creditError(err);
      setAlertText(f.status === 429 ? 'You exported too many times in a minute. Wait a minute and try again.' : f.status === 400 ? `${f.text} A statement file holds at most 20,000 lines: narrow the dates.` : f.text);
    } finally { setBusy(false); }
  };

  return (
    <div className="space-y-5">
      <BackLink href="/credit/companies">Choose another company</BackLink>
      <FailureAlert failure={failure} onRetry={statement.reload} />
      {statement.loading && !st && <Loading />}
      {st && (
        <>
          <section aria-label="Company summary" className="space-y-2 rounded-xl border border-surface-border bg-surface p-4" data-testid="company-summary">
            <h2 className="font-display text-xl font-bold">{st.companyName}</h2>
            <dl className="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-2">
              <div><dt className="inline text-text-muted">Owes now: </dt><dd className="inline font-mono" data-testid="owes-now">BDT {formatBdt(st.currentReceivableMinor)}</dd></div>
              <div><dt className="inline text-text-muted">On account: </dt><dd className="inline font-mono" data-testid="on-account-now">BDT {formatBdt(st.currentOnAccountMinor)}</dd></div>
              {row && <div><dt className="inline text-text-muted">Overdue: </dt><dd className="inline">{overdueText(row.maxDaysPastDue)} (BDT {formatBdt(row.overdueMinor)})</dd></div>}
              {row && <div><dt className="inline text-text-muted">By age: </dt><dd className="inline font-mono">{AGING_BUCKETS.map((b) => `${b.label} ${formatBdt(row.buckets[b.key])}`).join(' | ')}</dd></div>}
            </dl>
            <div className="flex flex-wrap gap-3 pt-1 text-sm font-semibold">
              {canRecordCredit(permissions) && <Link className="text-brand-accent underline" href={`/credit/companies/${companyId}/apply`}>Apply on-account money</Link>}
              {canAdjustCredit(permissions) && <Link className="text-brand-accent underline" href={`/credit/companies/${companyId}/adjust`}>Request an adjustment</Link>}
              <Link className="text-brand-accent underline" href={`/credit/adjustments?companyId=${companyId}`}>Adjustments of this company</Link>
            </div>
          </section>

          <CreditHoldPanel
            companyId={companyId}
            companyName={st.companyName}
            onHold={row?.onHold ?? false}
            overrideUntilUtc={row?.holdOverrideUntilUtc ?? null}
            canOverride={canOverrideHold(permissions)}
            onChanged={aging.reload}
          />

          <section aria-label="Statement" className="space-y-3">
            <h2 className="font-display text-lg font-bold">Statement</h2>
            <form onSubmit={apply} noValidate aria-label="Statement dates" className="flex flex-wrap items-end gap-4">
              <Input label="From (day)" type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
              <Input label="To (day)" type="date" value={to} onChange={(e) => setTo(e.target.value)} error={rangeProblem ?? undefined} />
              <Button type="submit" variant="outline">Show</Button>
              <Button type="button" onClick={download} isLoading={busy} disabled={busy} data-testid="statement-csv">Download CSV</Button>
            </form>
            {notice && <Notice testId="statement-notice">{notice}</Notice>}
            {alertText && <FocusAlert ref={alertRef} testId="statement-alert">{alertText}</FocusAlert>}
            {st.lines.items.length === 0 ? (
              <p role="status" data-testid="statement-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">No ledger line in this period.</p>
            ) : (
              <div className="overflow-x-auto rounded-md border border-surface-border">
                <table className="w-full text-left text-sm" data-testid="statement-table">
                  <caption className="sr-only">Ledger lines of {st.companyName}{range.from || range.to ? ` from ${range.from || 'the start'} to ${range.to || 'today'}` : ''}, in the order they were posted. Amounts in BDT.</caption>
                  <thead className="bg-bg-subtle text-xs text-text-muted">
                    <tr>
                      <th scope="col" className="px-3 py-2">No.</th>
                      <th scope="col" className="px-3 py-2">Day</th>
                      <th scope="col" className="px-3 py-2">What happened</th>
                      <th scope="col" className="px-3 py-2">Order or payment</th>
                      <th scope="col" className="px-3 py-2 text-right">Owed change</th>
                      <th scope="col" className="px-3 py-2 text-right">On-account change</th>
                      <th scope="col" className="px-3 py-2 text-right">Owed after</th>
                      <th scope="col" className="px-3 py-2 text-right">On account after</th>
                    </tr>
                  </thead>
                  <tbody>
                    {st.lines.items.map((l) => (
                      <tr key={l.seq} className="border-t border-surface-border align-top" data-testid="statement-row">
                        <td className="px-3 py-2 font-mono text-xs">{l.seq}</td>
                        <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">{l.occurredOn}</td>
                        <td className="px-3 py-2">{ledgerKindText(l.kind)}{l.reason ? <span className="block text-xs text-text-muted">{l.reason}</span> : null}</td>
                        <td className="px-3 py-2 font-mono text-xs">{l.orderNumber ?? ''}{l.orderNumber && l.paymentReference ? ' / ' : ''}{l.paymentReference ?? ''}</td>
                        <td className="px-3 py-2 text-right font-mono">{signedBdt(l.receivableDeltaMinor)}</td>
                        <td className="px-3 py-2 text-right font-mono">{signedBdt(l.onAccountDeltaMinor)}</td>
                        <td className="px-3 py-2 text-right font-mono">{formatBdt(l.receivableAfterMinor)}</td>
                        <td className="px-3 py-2 text-right font-mono">{formatBdt(l.onAccountAfterMinor)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            <div className="flex flex-wrap items-center gap-3 text-xs">
              <Button type="button" variant="outline" size="sm" disabled={page <= 1 || statement.loading} onClick={() => setPage(page - 1)}>Previous</Button>
              <span aria-live="polite">Page {st.lines.page} of {pages} ({st.lines.totalCount} lines, {STATEMENT_PAGE_SIZE} a page)</span>
              <Button type="button" variant="outline" size="sm" disabled={page >= pages || statement.loading} onClick={() => setPage(page + 1)}>Next</Button>
              <Button type="button" variant="ghost" size="sm" onClick={() => { statement.reload(); aging.reload(); }}>Refresh</Button>
            </div>
          </section>
        </>
      )}
    </div>
  );
}

export function CreditCompanyView({ id }: { id: string }) {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="credit-statement" title="Company credit books">
      {canViewCredit(user?.permissions ?? []) ? <Books companyId={id} /> : <NoAccess permission="credit.view" />}
    </ConsoleShell>
  );
}
