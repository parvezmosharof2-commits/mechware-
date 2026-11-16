'use client';

import * as React from 'react';
import { Button, Input } from '@mechware/ui';
import { exactSkuMatch, filterByText, flattenCategories, type FlatCategory } from '@mechware/api-client';
import { lookupsApi, marginFloorsApi, problemStatus, storefrontApi } from '../lib/api';

export interface PickedBrand { id: string; name: string }

/**
 * There is no brand list with ids for staff yet (follow-up), so a brand is found through the SKU of any one of its products:
 * the SKU gives the variant and its brand name, and the floors API answers the brand id for that variant.
 */
export function BrandBySku({ value, onChange, error, idPrefix }: { value: PickedBrand | null; onChange: (b: PickedBrand | null) => void; error?: string; idPrefix: string }) {
  const [sku, setSku] = React.useState('');
  const [busy, setBusy] = React.useState(false);
  const [problem, setProblem] = React.useState<string | null>(null);

  const find = async () => {
    if (!sku.trim()) return setProblem('Enter a SKU.');
    setBusy(true);
    setProblem(null);
    try {
      const page = await lookupsApi.variants({ sku: sku.trim(), pageSize: 5 });
      const hit = exactSkuMatch(page.data.items, sku);
      if (!hit) { setProblem(`No product has the SKU "${sku.trim()}".`); onChange(null); return; }
      if (!hit.brand) { setProblem('That product has no brand.'); onChange(null); return; }
      const eff = await marginFloorsApi.effective({ variantId: hit.variantId });
      if (!eff.data.brandId) { setProblem('That product has no brand.'); onChange(null); return; }
      onChange({ id: eff.data.brandId, name: hit.brand });
    } catch (err) {
      const s = problemStatus(err);
      setProblem(s === 403 ? 'Your role cannot look up products.' : 'Could not look that SKU up. Check your connection and try again.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="space-y-2" data-testid={`${idPrefix}-brand-picker`}>
      <div className="flex flex-wrap items-end gap-2">
        <div className="min-w-0 flex-1">
          <Input
            label="Brand: SKU of any product of the brand"
            helperText="Type the exact SKU of one product of the brand, then Find brand."
            value={sku}
            onChange={(e) => setSku(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); void find(); } }}
            autoComplete="off"
            spellCheck={false}
            disabled={busy}
            error={problem ?? error}
          />
        </div>
        <Button type="button" variant="outline" onClick={() => void find()} isLoading={busy} disabled={busy}>Find brand</Button>
      </div>
      {value && <p data-testid={`${idPrefix}-brand-chosen`} className="text-sm font-semibold">Brand: {value.name}</p>}
    </div>
  );
}

/** Searchable category list from the public category tree (names with their path). */
export function CategoryPicker({ value, onChange, error, idPrefix }: { value: string | null; onChange: (id: string | null, label: string) => void; error?: string; idPrefix: string }) {
  const [items, setItems] = React.useState<FlatCategory[] | null>(null);
  const [failed, setFailed] = React.useState(false);
  const [search, setSearch] = React.useState('');
  const [attempt, setAttempt] = React.useState(0);

  React.useEffect(() => {
    let current = true;
    setFailed(false);
    storefrontApi.categoryTree().then(
      (res) => { if (current) setItems(flattenCategories(res.data)); },
      () => { if (current) setFailed(true); }
    );
    return () => { current = false; };
  }, [attempt]);

  const shown = items ? filterByText(items, search) : [];
  const chosen = items?.find((c) => c.id === value) ?? null;

  return (
    <div className="space-y-2" data-testid={`${idPrefix}-category-picker`}>
      <Input label="Category" type="search" placeholder="Search categories" value={search} onChange={(e) => setSearch(e.target.value)} autoComplete="off" spellCheck={false} error={error} />
      {failed && (
        <div role="alert" className="flex flex-wrap items-center gap-3 text-sm font-medium text-fault">
          <span>Could not load the categories.</span>
          <Button type="button" variant="outline" size="sm" onClick={() => setAttempt(attempt + 1)}>Try again</Button>
        </div>
      )}
      {!items && !failed && <p role="status" className="text-xs text-text-muted">Loading categories...</p>}
      {items && shown.length === 0 && <p role="status" className="text-sm text-text-muted">No category matches &quot;{search.trim()}&quot;.</p>}
      {shown.length > 0 && (
        <select
          aria-label="Categories"
          size={Math.min(6, Math.max(2, shown.length))}
          value={value ?? ''}
          onChange={(e) => onChange(e.target.value || null, shown.find((c) => c.id === e.target.value)?.label ?? '')}
          className="block w-full rounded-md border border-surface-border bg-surface p-1 text-sm"
          data-testid={`${idPrefix}-category-list`}
        >
          <option value="" disabled hidden />
          {shown.map((c) => <option key={c.id} value={c.id} className="px-2 py-1">{c.label}</option>)}
        </select>
      )}
      {chosen && <p data-testid={`${idPrefix}-category-chosen`} className="text-sm font-semibold">Category: {chosen.label}</p>}
    </div>
  );
}
