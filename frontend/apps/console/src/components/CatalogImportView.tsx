'use client';

import * as React from 'react';
import { Badge, Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@mechware/ui';
import {
  checkImportFile, errorsToCsv,
  type ImportCommitResult, type ImportFileProblem, type ImportReport
} from '@mechware/api-client';
import { catalogImportApi, problemCode, problemMessage, problemStatus } from '../lib/api';
import { useAuth } from '../lib/auth';
import { ConsoleShell } from './ConsoleShell';
import { SellerPicker } from './SellerPicker';

const PAGE_SIZE = 50;
const FILE_TEXT: Record<ImportFileProblem, string> = {
  'file.missing': 'Choose a CSV file first.',
  'file.notCsv': 'Only .csv files are accepted. In Excel, use "Save as CSV UTF-8".',
  'file.empty': 'The file is empty.',
  'file.tooLarge': 'The file is larger than 10 MB.'
};

function errorText(err: unknown): string {
  const status = problemStatus(err);
  if (status === 401) return 'Your session has ended. Sign in again.';
  if (status === 403) return 'Your role does not allow importing catalogue data (catalog.manage).';
  if (status === 413) return 'The file is too large (limit 10 MB).';
  if (status === 400) return problemMessage(err, 'The request was rejected.');
  if (status === 409) return problemMessage(err, 'Another change got in the way. Check the file again and retry.');
  if (status !== null && status >= 500) return 'The server had a problem. Try again in a moment; a failed request writes nothing.';
  return 'Could not reach the server. Check your connection and try again.';
}

/** A 422 IMPORT_REJECTED carries the report in `data`. */
function rejectedReport(err: unknown): ImportReport | null {
  if (problemStatus(err) !== 422 || problemCode(err) !== 'IMPORT_REJECTED') return null;
  const data = (err as { data?: ImportReport }).data;
  return data && Array.isArray(data.errors) ? data : null;
}

function Stat({ label, value }: { label: string; value: number }) {
  return (
    <div className="rounded-md border border-surface-border bg-bg-subtle p-3">
      <div className="text-xs font-semibold uppercase tracking-wide text-text-muted">{label}</div>
      <div className="mt-1 font-mono text-xl font-bold" data-testid={`stat-${label.toLowerCase().replace(/\W+/g, '-')}`}>{value.toLocaleString('en')}</div>
    </div>
  );
}

function ReportView({ report, title }: { report: ImportReport; title: string }) {
  const [page, setPage] = React.useState(1);
  React.useEffect(() => setPage(1), [report]);
  const pages = Math.max(1, Math.ceil(report.errors.length / PAGE_SIZE));
  const slice = report.errors.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE);

  const download = () => {
    const url = URL.createObjectURL(new Blob(['﻿' + errorsToCsv(report.errors)], { type: 'text/csv;charset=utf-8' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = 'import-errors.csv';
    a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <section aria-label={title} className="space-y-4">
      <h2 className="font-display text-lg font-bold">{title}</h2>
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        <Stat label="Rows" value={report.rows} />
        <Stat label="Valid rows" value={report.validRows} />
        <Stat label="Invalid rows" value={report.invalidRows} />
        <Stat label="Errors" value={report.errorCount} />
        <Stat label="New products" value={report.newProducts} />
        <Stat label="Existing products" value={report.existingProducts} />
        <Stat label="New variants" value={report.newVariants} />
        <Stat label="Existing variants" value={report.existingVariants} />
      </div>
      {report.errorCount === 0 ? (
        <p role="status" className="text-sm font-medium text-success">No errors. The file can be imported.</p>
      ) : (
        <div className="space-y-3">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <p className="text-sm text-text-muted">
              {report.errorCount} error{report.errorCount === 1 ? '' : 's'}
              {report.errorsTruncated && `; only the first ${report.errors.length} are listed`}. Row 1 is the header.
            </p>
            <Button type="button" variant="outline" size="sm" onClick={download}>Download errors (CSV)</Button>
          </div>
          <div className="overflow-x-auto rounded-md border border-surface-border">
            <table className="w-full text-left text-xs" data-testid="error-table">
              <thead className="bg-bg-subtle text-text-muted">
                <tr>
                  <th scope="col" className="px-3 py-2">Row</th>
                  <th scope="col" className="px-3 py-2">Column</th>
                  <th scope="col" className="px-3 py-2">Code</th>
                  <th scope="col" className="px-3 py-2">Message</th>
                </tr>
              </thead>
              <tbody>
                {slice.map((e, i) => (
                  <tr key={`${e.row}-${e.column}-${e.code}-${i}`} className="border-t border-surface-border">
                    <td className="px-3 py-2 font-mono">{e.row === 0 ? 'file' : e.row}</td>
                    <td className="px-3 py-2 font-mono">{e.column ?? ''}</td>
                    <td className="px-3 py-2 font-mono">{e.code}</td>
                    <td className="px-3 py-2">{e.message}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {pages > 1 && (
            <div className="flex items-center gap-3 text-xs">
              <Button type="button" variant="outline" size="sm" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</Button>
              <span>Page {page} of {pages}</span>
              <Button type="button" variant="outline" size="sm" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</Button>
            </div>
          )}
        </div>
      )}
    </section>
  );
}

function CatalogImport() {
  const [file, setFile] = React.useState<File | null>(null);
  const [fileError, setFileError] = React.useState<string | null>(null);
  const [sellerId, setSellerId] = React.useState('');
  const [sellerName, setSellerName] = React.useState('');
  const [checked, setChecked] = React.useState<{ report: ImportReport; file: File } | null>(null);
  const [rejected, setRejected] = React.useState<ImportReport | null>(null);
  const [result, setResult] = React.useState<ImportCommitResult | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState<'check' | 'commit' | null>(null);
  const [confirmOpen, setConfirmOpen] = React.useState(false);
  const inputRef = React.useRef<HTMLInputElement>(null);

  const pick = (e: React.ChangeEvent<HTMLInputElement>) => {
    const next = e.target.files?.[0] ?? null;
    const problem = next ? checkImportFile(next) : null;
    setFileError(problem ? FILE_TEXT[problem] : null);
    setFile(problem ? null : next);
    setChecked(null);
    setRejected(null);
    setResult(null);
    setError(null);
    if (problem && inputRef.current) inputRef.current.value = '';
  };

  const check = async () => {
    const problem = checkImportFile(file);
    if (problem || !file) return setFileError(FILE_TEXT[problem ?? 'file.missing']);
    setBusy('check');
    setError(null);
    setRejected(null);
    setResult(null);
    try {
      setChecked({ report: (await catalogImportApi.dryRun(file)).data, file });
    } catch (err) {
      setChecked(null);
      setError(errorText(err));
    } finally {
      setBusy(null);
    }
  };

  const sellerOk = sellerId !== '';
  const canImport = busy === null && checked !== null && checked.file === file && checked.report.errorCount === 0 && sellerOk;

  const openConfirm = () => {
    if (sellerOk) setConfirmOpen(true);
  };

  const commit = async () => {
    if (!file) return;
    setBusy('commit');
    setError(null);
    try {
      setResult((await catalogImportApi.commit(file, sellerId)).data);
      setChecked(null);
    } catch (err) {
      const report = rejectedReport(err);
      if (report) setRejected(report);
      else setError(errorText(err));
    } finally {
      setBusy(null);
      setConfirmOpen(false);
    }
  };

  const report = checked?.report;
  return (
    <div className="space-y-6">
      <div className="space-y-4 rounded-xl border border-surface-border bg-surface p-6">
        <p className="text-sm text-text-muted">
          Upload a CSV (UTF-8, up to 10 MB), check it, then import. A check writes nothing. An import writes the whole file or nothing, and importing the same file again changes nothing.
          Column rules: docs/catalog/IMPORT_FORMAT.md.
        </p>
        <div>
          <label htmlFor="import-file" className="block text-xs font-semibold text-text-muted">CSV file</label>
          <input
            id="import-file"
            ref={inputRef}
            type="file"
            accept=".csv,text/csv"
            onChange={pick}
            disabled={busy !== null}
            className="mt-1 block w-full text-sm"
          />
          {fileError && <p role="alert" className="mt-1 text-xs font-medium text-fault">{fileError}</p>}
          {file && !fileError && <p className="mt-1 text-xs text-text-muted">{file.name} ({(file.size / 1024).toFixed(1)} KB)</p>}
        </div>
        <div className="flex flex-wrap gap-3">
          <Button type="button" onClick={check} isLoading={busy === 'check'} disabled={busy !== null || !file}>Check file</Button>
        </div>
      </div>

      {error && <div role="alert" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{error}</div>}

      {report && (
        <div className="space-y-5 rounded-xl border border-surface-border bg-surface p-6">
          <ReportView report={report} title="Check result" />
          <div className="space-y-3 border-t border-surface-border pt-4">
            <SellerPicker onChange={(id, name) => { setSellerId(id); setSellerName(name); }} />
            <Button type="button" onClick={openConfirm} disabled={!canImport}>Import</Button>
            {report.errorCount === 0 && !sellerOk && <p className="text-xs text-text-muted">Choose a seller to enable the import.</p>}
            {report.errorCount > 0 && <p className="text-xs text-text-muted">Fix the errors in the file and check it again to enable the import.</p>}
          </div>
        </div>
      )}

      {rejected && (
        <div className="space-y-3 rounded-xl border border-fault bg-surface p-6">
          <p role="alert" className="text-sm font-semibold text-fault">The server rejected the import. Nothing was written.</p>
          <ReportView report={rejected} title="Rejected report" />
        </div>
      )}

      {result && (
        <section aria-label="Import result" className="space-y-4 rounded-xl border border-surface-border bg-surface p-6">
          <div className="flex items-center gap-2"><h2 className="font-display text-lg font-bold">Import finished</h2><Badge variant="success">Committed</Badge></div>
          <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
            <Stat label="Rows" value={result.rows} />
            <Stat label="Created products" value={result.createdProducts} />
            <Stat label="Created variants" value={result.createdVariants} />
            <Stat label="Updated products" value={result.updatedProducts} />
            <Stat label="Updated variants" value={result.updatedVariants} />
            <Stat label="Unchanged products" value={result.unchangedProducts} />
            <Stat label="Unchanged variants" value={result.unchangedVariants} />
          </div>
          <p className="text-xs text-text-muted">File SHA-256 (also in the audit log): <span data-testid="sha256" className="select-all break-all font-mono">{result.sha256}</span></p>
          <p className="text-xs text-text-muted">New products are drafts; publish them through the normal review path.</p>
        </section>
      )}

      <Dialog open={confirmOpen} onOpenChange={(next) => { if (!next && busy !== 'commit') setConfirmOpen(false); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Import {report?.rows.toLocaleString('en')} rows?</DialogTitle>
            <DialogDescription>
              {report && `${report.newProducts} new and ${report.existingProducts} existing products, ${report.newVariants} new and ${report.existingVariants} existing variants`}
              {' '}for seller {sellerName}. All of it is written, or none of it.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setConfirmOpen(false)} disabled={busy === 'commit'}>Cancel</Button>
            <Button type="button" onClick={commit} isLoading={busy === 'commit'}>Confirm import</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}

export function CatalogImportView() {
  const { user } = useAuth();
  const allowed = user?.permissions.some((p) => p.toLowerCase() === 'catalog.manage') ?? false;
  return (
    <ConsoleShell activeItemId="catalog-import" title="Bulk catalogue import">
      {allowed ? (
        <CatalogImport />
      ) : (
        <p role="alert" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">
          Your role does not allow this action (catalog.manage).
        </p>
      )}
    </ConsoleShell>
  );
}
