'use client';

import * as React from 'react';
import { Button, Input } from '@mechware/ui';
import type { CompanyDto } from '@mechware/api-client';
import { companiesApi, problemStatus } from '../lib/api';

const PAGE = 50;

function loadErrorText(err: unknown): string {
  const status = problemStatus(err);
  if (status === 401) return 'Your session has ended. Sign in again.';
  if (status === 403) return 'Your role does not allow listing companies (companies.view).';
  return 'Could not load the companies. Check your connection and try again.';
}

/** Id to legal name for the active companies (first 50); a missing name falls back to the id in the caller. */
export function useCompanyNames(): Record<string, string> {
  const [names, setNames] = React.useState<Record<string, string>>({});
  React.useEffect(() => {
    let current = true;
    companiesApi.admin.list({ status: 'active', pageSize: PAGE }).then(
      (res) => current && setNames(Object.fromEntries(res.data.items.map((c) => [c.id, c.legalName]))),
      () => undefined
    );
    return () => { current = false; };
  }, []);
  return names;
}

/** Searchable (client-side) list of active companies. The admin list has no search yet, so it pages and filters in the browser. */
export function CompanyPicker({ value, onChange, error }: { value: string; onChange: (companyId: string, legalName: string) => void; error?: string }) {
  const [search, setSearch] = React.useState('');
  const [items, setItems] = React.useState<CompanyDto[]>([]);
  const [total, setTotal] = React.useState(0);
  const [page, setPage] = React.useState(1);
  const [state, setState] = React.useState<'loading' | 'ready' | 'error'>('loading');
  const [message, setMessage] = React.useState('');
  const [attempt, setAttempt] = React.useState(0);

  React.useEffect(() => {
    let current = true;
    setState('loading');
    companiesApi.admin.list({ status: 'active', page, pageSize: PAGE }).then(
      (res) => {
        if (!current) return;
        setItems((prev) => (page === 1 ? res.data.items : [...prev, ...res.data.items]));
        setTotal(res.data.totalCount);
        setState('ready');
      },
      (err) => { if (current) { setMessage(loadErrorText(err)); setState('error'); } }
    );
    return () => { current = false; };
  }, [page, attempt]);

  const term = search.trim().toLowerCase();
  const shown = term ? items.filter((c) => c.legalName.toLowerCase().includes(term) || c.bin.includes(term)) : items;

  return (
    <div className="space-y-2">
      <Input
        label="Company"
        type="search"
        placeholder="Search by legal name or BIN"
        helperText="Only active companies are listed."
        value={search}
        onChange={(e) => setSearch(e.target.value)}
        autoComplete="off"
        spellCheck={false}
        error={error}
      />
      {state === 'error' && (
        <div role="alert" className="flex flex-wrap items-center gap-3 text-sm font-medium text-fault">
          <span>{message}</span>
          <Button type="button" variant="outline" size="sm" onClick={() => setAttempt(attempt + 1)}>Try again</Button>
        </div>
      )}
      {state === 'loading' && items.length === 0 && <p role="status" className="text-xs text-text-muted">Loading companies...</p>}
      {state === 'ready' && shown.length === 0 && (
        <p role="status" data-testid="company-empty" className="text-sm text-text-muted">
          {term ? `No active company matches "${search.trim()}".` : 'There are no active companies yet.'}
        </p>
      )}
      {shown.length > 0 && (
        <select
          aria-label="Active companies"
          size={Math.min(6, Math.max(2, shown.length))}
          value={value}
          onChange={(e) => onChange(e.target.value, items.find((c) => c.id === e.target.value)?.legalName ?? '')}
          className="block w-full rounded-md border border-surface-border bg-surface p-1 text-sm"
          data-testid="company-list"
        >
          <option value="" disabled hidden />
          {shown.map((c) => <option key={c.id} value={c.id} className="px-2 py-1">{c.legalName} (BIN {c.bin})</option>)}
        </select>
      )}
      {state === 'ready' && items.length < total && (
        <Button type="button" variant="outline" size="sm" onClick={() => setPage(page + 1)}>Show more ({items.length} of {total})</Button>
      )}
    </div>
  );
}
