'use client';

import * as React from 'react';
import { Button, Input } from '@mechware/ui';
import type { SellerPick } from '@mechware/api-client';
import { catalogImportApi, problemStatus } from '../lib/api';

function loadErrorText(err: unknown): string {
  const status = problemStatus(err);
  if (status === 401) return 'Your session has ended. Sign in again.';
  if (status === 403) return 'Your role does not allow listing sellers (catalog.manage).';
  return 'Could not load the seller list. Check your connection and try again.';
}

/**
 * Searchable list of approved sellers (store names). The list is a native listbox, so arrow keys, Home/End and type-ahead work
 * without extra code. Reports the chosen seller id (or '' when nothing is chosen).
 */
export function SellerPicker({ onChange }: { onChange: (sellerId: string, storeName: string) => void }) {
  const [search, setSearch] = React.useState('');
  const [term, setTerm] = React.useState('');
  const [items, setItems] = React.useState<SellerPick[]>([]);
  const [total, setTotal] = React.useState(0);
  const [page, setPage] = React.useState(1);
  const [state, setState] = React.useState<'loading' | 'ready' | 'error'>('loading');
  const [error, setError] = React.useState('');
  const [selected, setSelected] = React.useState('');
  const [attempt, setAttempt] = React.useState(0);
  const onChangeRef = React.useRef(onChange);
  onChangeRef.current = onChange;

  React.useEffect(() => {
    const t = setTimeout(() => { setPage(1); setTerm(search.trim()); }, 300);
    return () => clearTimeout(t);
  }, [search]);

  React.useEffect(() => {
    let current = true;
    setState('loading');
    catalogImportApi.listSellers(term, page).then(
      (res) => {
        if (!current) return;
        const next = page === 1 ? res.data.items : undefined;
        setItems((prev) => next ?? [...prev, ...res.data.items]);
        setTotal(res.data.totalCount);
        setState('ready');
        // Exactly one approved seller and no search active: nothing to choose, so choose it.
        if (page === 1 && !term && res.data.totalCount === 1 && res.data.items.length === 1) {
          setSelected(res.data.items[0].id);
          onChangeRef.current(res.data.items[0].id, res.data.items[0].storeName);
        }
      },
      (err) => { if (current) { setError(loadErrorText(err)); setState('error'); } }
    );
    return () => { current = false; };
  }, [term, page, attempt]);

  const choose = (id: string) => {
    setSelected(id);
    onChangeRef.current(id, items.find((s) => s.id === id)?.storeName ?? '');
  };

  return (
    <div className="space-y-2">
      <Input
        label="Seller"
        type="search"
        placeholder="Search by store name"
        helperText="Choose the approved seller the products will belong to."
        value={search}
        onChange={(e) => setSearch(e.target.value)}
        autoComplete="off"
        spellCheck={false}
      />
      {state === 'error' && (
        <div role="alert" className="flex flex-wrap items-center gap-3 text-sm font-medium text-fault">
          <span>{error}</span>
          <Button type="button" variant="outline" size="sm" onClick={() => setAttempt(attempt + 1)}>Try again</Button>
        </div>
      )}
      {state === 'loading' && items.length === 0 && <p role="status" className="text-xs text-text-muted">Loading sellers...</p>}
      {state === 'ready' && items.length === 0 && (
        <p role="status" data-testid="seller-empty" className="text-sm text-text-muted">
          {term ? `No approved sellers match "${term}".` : 'No approved sellers. A seller must be approved before products can be imported for it.'}
        </p>
      )}
      {items.length > 0 && (
        <select
          aria-label="Approved sellers"
          size={Math.min(6, Math.max(2, items.length))}
          value={selected}
          onChange={(e) => choose(e.target.value)}
          className="block w-full rounded-md border border-surface-border bg-surface p-1 text-sm"
          data-testid="seller-list"
        >
          <option value="" disabled hidden />
          {items.map((s) => <option key={s.id} value={s.id} className="px-2 py-1">{s.storeName}</option>)}
        </select>
      )}
      {state === 'ready' && items.length < total && (
        <Button type="button" variant="outline" size="sm" onClick={() => setPage(page + 1)}>Show more ({items.length} of {total})</Button>
      )}
    </div>
  );
}
