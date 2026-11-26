'use client';

import * as React from 'react';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import {
  canExportVat, isCreditStepUpFailure, isVatMonth, lastMonthDhaka, problemCode, problemMessage, problemStatus, vatBandText, vatBdt, vatCsvName,
  vatMonthCounts, vatMonthErrorText, vatRateText, type VatMonthReport
} from '@mechware/api-client';
import { vatMonthApi } from '../lib/vatMonth';
import { useAuth } from '../lib/auth';
import { saveFile } from '../lib/download';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { FocusAlert, Loading, NoAccess, Notice, useFocusOn } from './CreditShared';
import { StepUpDialog } from './StepUpDialog';

type Cell = React.ReactNode;
const money = (m: number): Cell => <span className="font-mono">{vatBdt(m)}</span>;

/** One section table: caption for screen readers, column headers with scope=col, numbers right-aligned. */
function Section({ id, title, caption, head, rows, empty }: { id: string; title: string; caption: string; head: [string, boolean][]; rows: Cell[][]; empty: string }) {
  return (
    <section aria-labelledby={id} className="space-y-2" data-testid={id}>
      <h2 id={id + '-h'} className="text-base font-semibold">{title}</h2>
      {rows.length === 0 ? (
        <p role="status" className="rounded-md border border-surface-border bg-surface p-3 text-sm text-text-muted">{empty}</p>
      ) : (
        <div className="overflow-x-auto rounded-md border border-surface-border">
          <table className="w-full text-left text-sm">
            <caption className="sr-only">{caption}</caption>
            <thead className="bg-bg-subtle text-xs text-text-muted">
              <tr>{head.map(([h, num]) => <th key={h} scope="col" className={`px-3 py-2 ${num ? 'text-right' : ''}`}>{h}</th>)}</tr>
            </thead>
            <tbody>
              {rows.map((r, i) => (
                <tr key={i} className="border-t border-surface-border align-top">
                  {r.map((c, j) => <td key={j} className={`px-3 py-2 ${head[j][1] ? 'text-right' : ''}`}>{c}</td>)}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

const MONEY4: [string, boolean][] = [['Net', true], ['SD', true], ['VAT', true], ['Total', true]];

function Report({ r }: { r: VatMonthReport }) {
  const t = r.totals;
  return (
    <div className="space-y-6" data-testid="vat-report">
      <p className="text-sm" data-testid="vat-totals">
        {r.month}: {vatMonthCounts(r)}. Output VAT <strong className="font-mono">BDT {vatBdt(t.salesVatMinor)}</strong>, less credit notes{' '}
        <strong className="font-mono">BDT {vatBdt(t.creditVatMinor)}</strong>, net VAT <strong className="font-mono">BDT {vatBdt(t.netVatMinor)}</strong>. Sales net{' '}
        <span className="font-mono">{vatBdt(t.salesNetMinor)}</span>, credit notes net <span className="font-mono">{vatBdt(t.creditNetMinor)}</span>. All amounts are in BDT. Buyer BINs show the last four digits only.
      </p>
      <Section id="vat-a" title="(a) Sales invoices (Mushak 6.3)" caption={`Sales invoices issued in ${r.month}, in BDT.`}
        head={[['Number', false], ['Issued (Dhaka)', false], ['Order', false], ['Buyer', false], ['BIN', false], ...MONEY4, ['VAT rate', false]]}
        rows={r.sales.map((s) => [s.number, s.issuedAtDhaka, s.orderNumber, s.buyerName, s.buyerBin ?? '-', money(s.netMinor), money(s.sdMinor), money(s.vatMinor), money(s.totalMinor), vatBandText(s.vatBand)])}
        empty="No sales invoices were issued in this month." />
      <Section id="vat-b" title="(b) Credit notes (Mushak 6.7)" caption={`Credit notes issued in ${r.month}, in BDT.`}
        head={[['Number', false], ['Issued (Dhaka)', false], ['Corrects', false], ['Buyer', false], ['BIN', false], ...MONEY4, ['VAT rate', false]]}
        rows={r.creditNotes.map((c) => [c.number, c.issuedAtDhaka, `${c.originalNumber} (${c.originalMonth})`, c.buyerName, c.buyerBin ?? '-', money(c.netMinor), money(c.sdMinor), money(c.vatMinor), money(c.totalMinor), vatBandText(c.vatBand)])}
        empty="No credit notes were issued in this month." />
      <section aria-labelledby="vat-c-h" className="space-y-2" data-testid="vat-c">
        <h2 id="vat-c-h" className="text-base font-semibold">(c) VAT withheld at source</h2>
        <p role="status" className="rounded-md border border-surface-border bg-surface p-3 text-sm text-text-muted">
          {r.withholdingsAvailable ? 'Withholding certificates are included in the file.' : 'Not recorded yet. Withholding certificates come with a later release, so add them by hand.'}
        </p>
      </section>
      <Section id="vat-d" title="(d) Totals by VAT rate" caption={`Sales and credit notes of ${r.month} per VAT rate, in BDT.`}
        head={[['Rate', false], ['Sales net', true], ['Sales SD', true], ['Sales VAT', true], ['Credit net', true], ['Credit SD', true], ['Credit VAT', true], ['Net VAT', true]]}
        rows={r.rateTotals.map((x) => [`${vatRateText(x.vatRate)} ${vatBandText(x.band)}`, money(x.salesNetMinor), money(x.salesSdMinor), money(x.salesVatMinor), money(x.creditNetMinor), money(x.creditSdMinor), money(x.creditVatMinor), money(x.salesVatMinor - x.creditVatMinor)])}
        empty="No amounts to split by rate." />
      <Section id="vat-e" title="(e) Large invoices (Mushak 6.10 candidates)" caption={`Single documents of ${r.month} above BDT ${vatBdt(r.mushak610ThresholdMinor)}.`}
        head={[['Kind', false], ['Number', false], ['Issued (Dhaka)', false], ['Buyer', false], ['BIN', false], ['Total', true]]}
        rows={r.mushak610Candidates.map((x) => [x.kind, x.number, x.issuedAtDhaka, x.buyerName, x.buyerBin ?? '-', money(x.totalMinor)])}
        empty={`No single document is above BDT ${vatBdt(r.mushak610ThresholdMinor)}.`} />
      <Section id="vat-f" title="(f) Paid but not shipped by month end" caption={`Orders paid by gateway in ${r.month} that had not shipped by the end of the month, in BDT.`}
        head={[['Order', false], ['Status', false], ['Paid (Dhaka)', false], ['Paid', true]]}
        rows={r.paidNotShipped.map((x) => [x.orderNumber, x.status, x.paidAtDhaka, money(x.paidMinor)])}
        empty="No such orders. Credit-account orders are not listed here." />
    </div>
  );
}

function VatMonth() {
  const [month, setMonth] = React.useState(() => lastMonthDhaka());
  const [csvOpen, setCsvOpen] = React.useState(false);
  const [busy, setBusy] = React.useState(false);
  const [codeError, setCodeError] = React.useState<string | null>(null);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const [notice, setNotice] = React.useState<string | null>(null);
  const alertRef = useFocusOn(alertText);
  const valid = isVatMonth(month);

  const res = useResource(() => vatMonthApi.preview(month), month, valid);
  const report = valid ? res.data?.data : null;
  const failure = res.error ? vatMonthErrorText({ status: problemStatus(res.error), message: problemMessage(res.error, '') }) : null;

  const download = async (code: string) => {
    setBusy(true);
    setCodeError(null);
    try {
      saveFile(vatCsvName(month), await vatMonthApi.csv(month, code));
      setCsvOpen(false);
      setAlertText(null);
      setNotice(`The file ${vatCsvName(month)} was downloaded. It has the full buyer BINs, and the export is recorded in the audit trail.`);
    } catch (err) {
      if (isCreditStepUpFailure(problemCode(err))) setCodeError('The code was not accepted. Check your authenticator app and try again.');
      else { setCsvOpen(false); setNotice(null); setAlertText(vatMonthErrorText({ status: problemStatus(err), message: problemMessage(err, '') })); }
    } finally { setBusy(false); }
  };

  return (
    <div className="space-y-5">
      <p className="text-sm text-text-muted">The working file for the monthly VAT return (Mushak 9.1) of one Dhaka calendar month, from real invoices only. Test (specimen) invoices never appear.</p>
      <p role="note" className="rounded-md border border-surface-border bg-surface p-3 text-sm" data-testid="vat-consultant-note">
        The VAT consultant has not checked this layout yet. Treat the file as a working draft and let them confirm it before you file.
      </p>
      <div className="flex flex-wrap items-end gap-4">
        <Input label="Month" type="month" value={month} onChange={(e) => setMonth(e.target.value)} error={valid ? undefined : 'Choose a month, for example 2026-10.'} data-testid="vat-month" />
        <Button type="button" variant="outline" onClick={res.reload} disabled={!valid || res.loading}>Refresh</Button>
        <Button type="button" disabled={!valid} onClick={() => { setNotice(null); setAlertText(null); setCodeError(null); setCsvOpen(true); }} data-testid="vat-csv">Download CSV</Button>
      </div>
      {notice && <Notice testId="vat-notice">{notice}</Notice>}
      {alertText && <FocusAlert ref={alertRef} testId="vat-alert">{alertText}</FocusAlert>}
      {failure && <div role="alert" className="rounded-md border border-surface-border bg-surface p-3 text-sm" data-testid="vat-error">{failure}</div>}
      {valid && res.loading && !report && <Loading />}
      {report && <Report r={report} />}
      <StepUpDialog
        open={csvOpen}
        title={`Download the VAT file for ${month}`}
        description="The file has the full buyer BINs. Your authenticator code is needed, the download is recorded in the audit trail, and you can export 6 times a minute."
        confirmLabel="Download"
        busy={busy}
        error={codeError}
        onCancel={() => { if (!busy) setCsvOpen(false); }}
        onSubmit={download}
      />
    </div>
  );
}

export function VatMonthView() {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="vat-month" title="Monthly VAT file (Mushak 9.1)">
      {canExportVat(user?.permissions ?? []) ? <VatMonth /> : <NoAccess permission="invoices.export" />}
    </ConsoleShell>
  );
}
