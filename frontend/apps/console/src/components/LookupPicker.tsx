'use client';

import * as React from 'react';
import { Button, Input } from '@mechware/ui';
import { categoryLabel } from '@mechware/api-client';
import { catalogAdminApi, catalogError } from '../lib/api';

export interface Picked { id: string; label: string }

/** Searchable picker over the staff lookups: type, Find, choose. Blank search lists the first matches. */
function LookupPicker(props: {
  label: string; idPrefix: string; value: Picked | null; onChange: (v: Picked | null) => void; search: (q: string) => Promise<Picked[]>;
  clearLabel?: string; error?: string; disabled?: boolean; noneText: string;
}) {
  const [q, setQ] = React.useState('');
  const [busy, setBusy] = React.useState(false);
  const [problem, setProblem] = React.useState<string | null>(null);
  const [found, setFound] = React.useState<Picked[] | null>(null);
  const find = async () => {
    setBusy(true);
    setProblem(null);
    try { setFound(await props.search(q)); } catch (err) { setProblem(catalogError(err).text); } finally { setBusy(false); }
  };
  return (
    <div className="space-y-2" data-testid={`${props.idPrefix}-picker`}>
      <div className="flex flex-wrap items-end gap-2">
        <div className="min-w-0 flex-1 basis-48">
          <Input label={props.label} value={q} disabled={props.disabled} onChange={(e) => setQ(e.target.value)} autoComplete="off"
            onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); void find(); } }} error={problem ?? props.error} />
        </div>
        <Button type="button" variant="outline" onClick={() => void find()} isLoading={busy} disabled={busy || props.disabled}>Find</Button>
      </div>
      <p className="flex flex-wrap items-center gap-2 text-sm" data-testid={`${props.idPrefix}-chosen`}>
        <span className={props.value ? 'font-semibold' : 'text-text-muted'}>{props.value ? props.value.label : props.noneText}</span>
        {props.value && props.clearLabel && <Button type="button" size="sm" variant="outline" disabled={props.disabled} onClick={() => props.onChange(null)}>{props.clearLabel}</Button>}
      </p>
      {found && found.length === 0 && <p role="status" className="text-sm text-text-muted">Nothing matches.</p>}
      {found && found.length > 0 && (
        <ul className="max-h-48 divide-y divide-surface-border overflow-y-auto rounded-md border border-surface-border" aria-label={`${props.label} results`}>
          {found.map((r) => (
            <li key={r.id} className="flex flex-wrap items-center justify-between gap-2 p-2 text-sm">
              <span className="min-w-0 break-words">{r.label}</span>
              <Button type="button" size="sm" variant="outline" aria-label={`Use ${r.label}`} onClick={() => { props.onChange(r); setFound(null); setQ(''); }}>Use</Button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

export function BrandLookupPicker(p: { value: Picked | null; onChange: (v: Picked | null) => void; idPrefix: string; error?: string; disabled?: boolean; allowClear?: boolean; label?: string }) {
  return (
    <LookupPicker label={p.label ?? 'Brand'} idPrefix={`${p.idPrefix}-brand`} value={p.value} onChange={p.onChange} error={p.error} disabled={p.disabled}
      clearLabel={p.allowClear === false ? undefined : 'Clear brand'} noneText="No brand"
      search={async (q) => (await catalogAdminApi.brands(q)).data.items.map((b) => ({ id: b.id, label: b.nameEn }))} />
  );
}

export function CategoryLookupPicker(p: { value: Picked | null; onChange: (v: Picked | null) => void; idPrefix: string; error?: string; disabled?: boolean; clearable?: boolean; label?: string }) {
  return (
    <LookupPicker label={p.label ?? 'Category'} idPrefix={`${p.idPrefix}-category`} value={p.value} onChange={p.onChange} error={p.error} disabled={p.disabled}
      clearLabel={p.clearable ? 'Clear category' : undefined} noneText="No category chosen"
      search={async (q) => (await catalogAdminApi.categories(q)).data.items.map((c) => ({ id: c.id, label: categoryLabel(c) }))} />
  );
}
