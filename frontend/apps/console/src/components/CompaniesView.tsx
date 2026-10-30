'use client';

import * as React from 'react';
import {
  Badge, Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
  EmptyState, ErrorState, Input, Progress, Skeleton, formatCurrency, useToast
} from '@mechware/ui';
import {
  buildCreditRequest, creditUsagePercent, creditTermsValue,
  type CompanyDto, type CompanyStatus, type CreditFormError, type CreditTermsName, type CreditTermsValue
} from '@mechware/api-client';
import { companiesApi, problemMessage, problemStatus } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';

const PAGE_SIZE = 20;
const STATUS_LABEL: Record<CompanyStatus, string> = { pending: 'Awaiting verification', active: 'Active', suspended: 'Suspended' };
const TERMS_LABEL: Record<CreditTermsName, string> = { none: 'No credit terms', net15: 'Net 15', net30: 'Net 30' };
const CREDIT_ERROR_TEXT: Record<CreditFormError, string> = {
  'terms.invalid': 'Choose valid credit terms.',
  'limit.format': 'Enter a valid, non-negative amount.',
  'limit.mustBeZero': 'With no credit terms the limit must be zero.',
  'limit.required': 'Net terms need a limit greater than zero.'
};

const money = (minor: number, currency: string) => formatCurrency(minor / 100, currency === 'USD' ? 'USD' : 'BDT', 'en');

function StatusBadge({ status }: { status: CompanyStatus }) {
  const variant = status === 'active' ? 'success' : status === 'pending' ? 'low-stock' : 'deal';
  return <Badge variant={variant}>{STATUS_LABEL[status]}</Badge>;
}

function errorText(err: unknown): string {
  const status = problemStatus(err);
  if (status === 403) return 'Your role does not allow this action.';
  if (status === 404) return 'This company no longer exists.';
  if (status === 409 || status === 400) return problemMessage(err, 'The change was rejected.');
  return 'Something went wrong. Please try again.';
}

type Action = { kind: 'activate' | 'suspend'; company: CompanyDto } | { kind: 'credit'; company: CompanyDto } | null;

function ConfirmDialog({ action, onClose, onDone }: { action: Extract<NonNullable<Action>, { kind: 'activate' | 'suspend' }> | null; onClose: () => void; onDone: () => void }) {
  const { toast } = useToast();
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  React.useEffect(() => setError(null), [action]);

  const run = async () => {
    if (!action) return;
    setBusy(true);
    setError(null);
    try {
      if (action.kind === 'activate') await companiesApi.admin.activate(action.company.id);
      else await companiesApi.admin.suspend(action.company.id);
      toast({ title: action.kind === 'activate' ? 'Company verified and activated.' : 'Company suspended.', variant: 'success' });
      onDone();
      onClose();
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  const activating = action?.kind === 'activate';
  return (
    <Dialog open={action !== null} onOpenChange={(next) => !next && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{activating ? 'Verify and activate this company?' : 'Suspend this company?'}</DialogTitle>
          <DialogDescription>
            {activating
              ? `Confirm that the trade licence ${action?.company.tradeLicenceNo} and BIN ${action?.company.bin} of ${action?.company.legalName} have been checked.`
              : `${action?.company.legalName} will keep read access but its admins can no longer change sites, cost centres or members, and credit cannot be used.`}
          </DialogDescription>
        </DialogHeader>
        {error && <div role="alert" className="rounded-md border border-fault bg-bg-subtle p-3 text-xs font-medium text-fault">{error}</div>}
        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant={activating ? 'primary' : 'destructive'} onClick={run} isLoading={busy}>{activating ? 'Activate' : 'Suspend'}</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function CreditDialog({ company, onClose, onDone }: { company: CompanyDto | null; onClose: () => void; onDone: () => void }) {
  const { toast } = useToast();
  const [terms, setTerms] = React.useState<CreditTermsValue>(0);
  const [limit, setLimit] = React.useState('');
  const [fieldError, setFieldError] = React.useState<string | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);

  React.useEffect(() => {
    if (!company) return;
    setTerms(creditTermsValue(company.creditTerms));
    setLimit(company.creditLimit.minorUnits === 0 ? '' : String(company.creditLimit.minorUnits / 100));
    setFieldError(null);
    setError(null);
  }, [company]);

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!company) return;
    const built = buildCreditRequest(terms, limit);
    if (!built.ok) {
      setFieldError(CREDIT_ERROR_TEXT[built.error]);
      return;
    }
    setFieldError(null);
    setBusy(true);
    setError(null);
    try {
      await companiesApi.admin.setCredit(company.id, built.request);
      toast({ title: 'Credit terms saved.', variant: 'success' });
      onDone();
      onClose();
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={company !== null} onOpenChange={(next) => !next && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Credit terms</DialogTitle>
          <DialogDescription>
            {company?.legalName}. Used so far: {company ? money(company.creditUsed.minorUnits, company.creditUsed.currency) : ''}. The limit cannot go below the amount already used.
          </DialogDescription>
        </DialogHeader>
        <form onSubmit={save} noValidate className="space-y-3">
          {error && <div role="alert" className="rounded-md border border-fault bg-bg-subtle p-3 text-xs font-medium text-fault">{error}</div>}
          <div>
            <label htmlFor="credit-terms" className="block text-xs font-semibold text-text-muted">Terms</label>
            <select
              id="credit-terms"
              value={terms}
              onChange={(e) => setTerms(Number(e.target.value) as CreditTermsValue)}
              className="mt-1 w-full rounded-md border border-surface-border bg-bg-subtle px-3 py-2 text-sm text-text"
            >
              <option value={0}>No credit terms</option>
              <option value={15}>Net 15 days</option>
              <option value={30}>Net 30 days</option>
            </select>
          </div>
          <Input label="Credit limit (BDT)" value={limit} onChange={(e) => setLimit(e.target.value)} error={fieldError ?? undefined} inputMode="decimal" />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
            <Button type="submit" isLoading={busy}>Save</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function CompaniesTable() {
  const { user } = useAuth();
  const perms = new Set((user?.permissions ?? []).map((p) => p.toLowerCase()));
  const canManage = perms.has('companies.manage');
  const canCredit = perms.has('companies.credit');

  const [status, setStatus] = React.useState('');
  const [page, setPage] = React.useState(1);
  const [action, setAction] = React.useState<Action>(null);
  const list = useResource(
    async () => (await companiesApi.admin.list({ status: status || undefined, page, pageSize: PAGE_SIZE })).data,
    `${status}:${page}`
  );

  const data = list.data;
  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1;
  const confirmAction = action && action.kind !== 'credit' ? action : null;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end gap-3">
        <div>
          <label htmlFor="status-filter" className="block text-xs font-semibold text-text-muted">Status</label>
          <select
            id="status-filter"
            value={status}
            onChange={(e) => { setStatus(e.target.value); setPage(1); }}
            className="mt-1 rounded-md border border-surface-border bg-bg-subtle px-3 py-2 text-sm text-text"
          >
            <option value="">All</option>
            <option value="pending">Awaiting verification</option>
            <option value="active">Active</option>
            <option value="suspended">Suspended</option>
          </select>
        </div>
        {data && <span className="pb-2 text-xs text-text-muted">{data.totalCount} companies</span>}
      </div>

      {list.loading ? (
        <div role="status" aria-live="polite" className="space-y-3">
          <span className="sr-only">Loading</span>
          {[0, 1, 2].map((i) => <Skeleton key={i} className="h-16 w-full" />)}
        </div>
      ) : list.error ? (
        <ErrorState code={problemStatus(list.error) ?? 'ERR'} title="Could not load companies" description={errorText(list.error)} onRetry={list.reload} illustration={<span aria-hidden />} />
      ) : (data?.items.length ?? 0) === 0 ? (
        <EmptyState title="No companies" description={status ? 'No company has this status.' : 'No company has registered yet.'} />
      ) : (
        <div className="relative overflow-x-auto rounded-xl border border-surface-border">
          <table className="w-full min-w-[56rem] text-left text-sm">
            <thead className="bg-bg-subtle text-xs uppercase tracking-wide text-text-muted">
              <tr>
                <th scope="col" className="px-4 py-3">Company</th>
                <th scope="col" className="px-4 py-3">BIN</th>
                <th scope="col" className="px-4 py-3">Status</th>
                <th scope="col" className="px-4 py-3">Credit</th>
                <th scope="col" className="px-4 py-3">Registered</th>
                <th scope="col" className="px-4 py-3"><span className="sr-only">Actions</span></th>
              </tr>
            </thead>
            <tbody className="divide-y divide-surface-border bg-surface">
              {data!.items.map((c) => {
                const usage = creditUsagePercent(c);
                return (
                  <tr key={c.id}>
                    <td className="px-4 py-3">
                      <div className="font-semibold">{c.legalName}</div>
                      <div className="font-mono text-xs text-text-muted">{c.tradeLicenceNo}</div>
                    </td>
                    <td className="px-4 py-3 font-mono text-xs">{c.bin}</td>
                    <td className="px-4 py-3"><StatusBadge status={c.status} /></td>
                    <td className="px-4 py-3">
                      <div className="text-xs">{TERMS_LABEL[c.creditTerms]}</div>
                      {c.creditTerms !== 'none' && (
                        <div className="mt-1 w-40 space-y-1">
                          <Progress value={usage} aria-label={`Credit used: ${usage}%`} variant={usage >= 90 ? 'fault' : usage >= 80 ? 'warning' : 'accent'} />
                          <div className="text-xs tabular-nums text-text-muted">{money(c.creditUsed.minorUnits, c.creditUsed.currency)} of {money(c.creditLimit.minorUnits, c.creditLimit.currency)}</div>
                        </div>
                      )}
                    </td>
                    <td className="px-4 py-3 text-xs text-text-muted">{new Date(c.createdAtUtc).toLocaleDateString('en-GB', { timeZone: 'Asia/Dhaka' })}</td>
                    <td className="px-4 py-3">
                      <div className="flex flex-wrap justify-end gap-2">
                        {canManage && c.status !== 'active' && <Button size="sm" onClick={() => setAction({ kind: 'activate', company: c })}>{c.status === 'pending' ? 'Verify' : 'Reactivate'}</Button>}
                        {canManage && c.status !== 'suspended' && <Button size="sm" variant="outline" onClick={() => setAction({ kind: 'suspend', company: c })}>Suspend</Button>}
                        {canCredit && <Button size="sm" variant="outline" onClick={() => setAction({ kind: 'credit', company: c })}>Credit</Button>}
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      {data && data.totalCount > data.pageSize && (
        <nav aria-label="Pagination" className="flex items-center justify-between">
          <Button variant="outline" size="sm" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>Previous</Button>
          <span className="text-xs text-text-muted">Page {page} of {totalPages}</span>
          <Button variant="outline" size="sm" disabled={page >= totalPages} onClick={() => setPage((p) => p + 1)}>Next</Button>
        </nav>
      )}

      <ConfirmDialog action={confirmAction} onClose={() => setAction(null)} onDone={list.reload} />
      <CreditDialog company={action?.kind === 'credit' ? action.company : null} onClose={() => setAction(null)} onDone={list.reload} />
    </div>
  );
}

export function CompaniesView() {
  return (
    <ConsoleShell activeItemId="companies" title="B2B companies">
      <CompaniesTable />
    </ConsoleShell>
  );
}
