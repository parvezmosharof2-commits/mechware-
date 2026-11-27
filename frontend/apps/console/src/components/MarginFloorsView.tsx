'use client';

import * as React from 'react';
import { Button, Input, Skeleton } from '@mechware/ui';
import { Modal, ModalFooter } from './Modal';
import {
  FLOOR_CHANGE_NOTE, FLOOR_PERMISSIONS, exactSkuMatch, SCOPE_LABEL, buildUpsertBody, effectiveSummary, findExistingRow, floorTarget, formatMarkup, groupFloors, isFloorStepUpFailure,
  markupToInput, scopeNeeds, updatedByText, type EffectiveFloor, type FloorScope, type MarginFloorRow, type UpsertFloorBody
} from '@mechware/api-client';
import { floorError, lookupsApi, marginFloorsApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { BrandBySku, CategoryPicker, type PickedBrand } from './FloorPickers';
import { StepUpDialog } from './StepUpDialog';

const when = (iso: string) => `${iso.slice(0, 16).replace('T', ' ')} UTC`;

interface FormState {
  mode: 'add' | 'edit';
  scope: FloorScope;
  brand: PickedBrand | null;
  categoryId: string | null;
  markup: string;
  row: MarginFloorRow | null;
}

type Flow =
  | { k: 'form' }
  | { k: 'confirm'; body: UpsertFloorBody; summary: string }
  | { k: 'code'; body: UpsertFloorBody; summary: string }
  | { k: 'del-confirm'; row: MarginFloorRow }
  | { k: 'del-code'; row: MarginFloorRow };

const EMPTY_FORM: FormState = { mode: 'add', scope: 'Brand', brand: null, categoryId: null, markup: '', row: null };

function Failure({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  const f = floorError(error);
  return (
    <div role="alert" className="flex flex-wrap items-center gap-3 rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">
      <span>{f.text}</span>
      {f.retry && <Button type="button" variant="outline" size="sm" onClick={onRetry}>Try again</Button>}
    </div>
  );
}

function EffectivePanel() {
  const [mode, setMode] = React.useState<'sku' | 'target'>('sku');
  const [sku, setSku] = React.useState('');
  const [brand, setBrand] = React.useState<PickedBrand | null>(null);
  const [categoryId, setCategoryId] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [problem, setProblem] = React.useState<string | null>(null);
  const [result, setResult] = React.useState<{ eff: EffectiveFloor; about: string } | null>(null);

  const run = async (e: React.FormEvent) => {
    e.preventDefault();
    setResult(null);
    setProblem(null);
    setBusy(true);
    try {
      if (mode === 'sku') {
        if (!sku.trim()) return setProblem('Enter a SKU.');
        const hit = exactSkuMatch((await lookupsApi.variants({ sku: sku.trim(), pageSize: 5 })).data.items, sku);
        if (!hit) return setProblem(`No product has the SKU "${sku.trim()}".`);
        const eff = await marginFloorsApi.effective({ variantId: hit.variantId });
        setResult({ eff: eff.data, about: `${hit.sku} - ${hit.title}${hit.brand ? ` (${hit.brand})` : ''}` });
      } else {
        if (!brand && !categoryId) return setProblem('Pick a brand, a category, or both.');
        const eff = await marginFloorsApi.effective({ brandId: brand?.id, categoryId: categoryId ?? undefined });
        setResult({ eff: eff.data, about: [brand ? `Brand ${brand.name}` : null, categoryId ? 'the chosen category' : null].filter(Boolean).join(' in ') });
      }
    } catch (err) {
      setProblem(floorError(err).text);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section aria-label="Effective floor" className="space-y-3 rounded-xl border border-surface-border bg-surface p-4" data-testid="effective-panel">
      <h2 className="font-display text-lg font-bold">Effective floor</h2>
      <p className="text-sm text-text-muted">Which row applies to a product? The most specific row wins: brand and category, then brand, then category (own category first, then its parents), then the global default.</p>
      <div className="flex gap-2" role="group" aria-label="Look up by">
        <Button type="button" size="sm" variant={mode === 'sku' ? 'primary' : 'outline'} aria-pressed={mode === 'sku'} onClick={() => { setMode('sku'); setResult(null); setProblem(null); }}>By SKU</Button>
        <Button type="button" size="sm" variant={mode === 'target' ? 'primary' : 'outline'} aria-pressed={mode === 'target'} onClick={() => { setMode('target'); setResult(null); setProblem(null); }}>By brand and category</Button>
      </div>
      <form onSubmit={run} noValidate className="space-y-3">
        {mode === 'sku' ? (
          <Input label="SKU" value={sku} onChange={(e) => setSku(e.target.value)} autoComplete="off" spellCheck={false} disabled={busy} error={problem ?? undefined} data-testid="effective-sku" />
        ) : (
          <div className="space-y-3">
            <BrandBySku idPrefix="eff" value={brand} onChange={setBrand} />
            <CategoryPicker idPrefix="eff" value={categoryId} onChange={(id) => setCategoryId(id)} />
            {problem && <p role="alert" className="text-sm font-medium text-fault">{problem}</p>}
          </div>
        )}
        <Button type="submit" isLoading={busy} disabled={busy} data-testid="effective-run">Show the floor</Button>
      </form>
      {result && (
        <div role="status" data-testid="effective-result" className="rounded-md border border-surface-border bg-bg-subtle p-3 text-sm">
          <p className="font-semibold">{result.about}</p>
          <p className="mt-1" data-testid="effective-summary">{effectiveSummary(result.eff)}</p>
        </div>
      )}
    </section>
  );
}

function Floors({ canManage }: { canManage: boolean }) {
  const floors = useResource(() => marginFloorsApi.list(), 'floors');
  const rows = floors.data?.data;
  const grouped = React.useMemo(() => (rows ? groupFloors(rows) : null), [rows]);

  const [flow, setFlow] = React.useState<Flow | null>(null);
  const [form, setForm] = React.useState<FormState>(EMPTY_FORM);
  const [formErrors, setFormErrors] = React.useState<{ shape?: string; markup?: string; server?: string }>({});
  const [busy, setBusy] = React.useState(false);
  const [codeError, setCodeError] = React.useState<string | null>(null);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [pageError, setPageError] = React.useState<string | null>(null);

  const openAdd = () => { setForm(EMPTY_FORM); setFormErrors({}); setNotice(null); setPageError(null); setFlow({ k: 'form' }); };
  const openEdit = (row: MarginFloorRow) => {
    setForm({
      mode: 'edit', scope: row.scope, brand: row.brandId ? { id: row.brandId, name: row.brandName ?? 'brand' } : null,
      categoryId: row.categoryId, markup: markupToInput(row.minMarkupPercent), row
    });
    setFormErrors({}); setNotice(null); setPageError(null); setFlow({ k: 'form' });
  };

  const review = (e: React.FormEvent) => {
    e.preventDefault();
    const built = buildUpsertBody({ scope: form.scope, brandId: form.brand?.id ?? null, categoryId: form.categoryId, markup: form.markup });
    if ('problem' in built) return setFormErrors(built.field === 'markup' ? { markup: built.problem } : { shape: built.problem });
    setFormErrors({});
    const existing = form.row ?? findExistingRow(rows ?? [], form.scope, form.brand?.id ?? null, form.categoryId);
    const target = form.row ? floorTarget(form.row) : form.scope === 'Brand' ? (form.brand?.name ?? 'brand') : form.scope === 'Category' ? 'the chosen category' : `${form.brand?.name ?? 'brand'} in the chosen category`;
    const summary = existing
      ? `Change the floor for "${target}" from ${formatMarkup(existing.minMarkupPercent)} % to ${formatMarkup(built.body.minMarkupPercent)} %.`
      : `Add a floor of ${formatMarkup(built.body.minMarkupPercent)} % for "${target}".`;
    setFlow({ k: 'confirm', body: built.body, summary });
  };

  const afterFailure = (err: unknown, backTo: Flow) => {
    const f = floorError(err);
    if (f.status === 404 || f.status === 409) floors.reload();
    if (backTo.k === 'form') { setFormErrors({ server: f.text }); setFlow(backTo); } else { setPageError(f.text); setFlow(null); }
  };

  const submitUpsert = async (code: string) => {
    if (flow?.k !== 'code') return;
    setBusy(true); setCodeError(null);
    try {
      const saved = await marginFloorsApi.upsert(flow.body, code);
      setFlow(null);
      setNotice(`Saved: ${formatMarkup(saved.data.minMarkupPercent)} % for ${floorTarget(saved.data)}.`);
      floors.reload();
    } catch (err) {
      const f = floorError(err);
      if (isFloorStepUpFailure(f.code)) setCodeError(f.text); else afterFailure(err, { k: 'form' });
    } finally { setBusy(false); }
  };

  const submitDelete = async (code: string) => {
    if (flow?.k !== 'del-code') return;
    const row = flow.row;
    setBusy(true); setCodeError(null);
    try {
      await marginFloorsApi.remove(row.id, code);
      setFlow(null);
      setNotice(`Deleted the floor for ${floorTarget(row)}. Products there now follow the next row down.`);
      floors.reload();
    } catch (err) {
      const f = floorError(err);
      if (isFloorStepUpFailure(f.code)) setCodeError(f.text); else afterFailure(err, { k: 'del-confirm', row });
    } finally { setBusy(false); }
  };

  const need = scopeNeeds(form.scope);
  const closeIdle = (next: boolean) => { if (!next && !busy) setFlow(null); };

  return (
    <div className="space-y-5">
      {notice && <div role="status" data-testid="floor-notice" className="rounded-md border border-surface-border bg-bg-subtle p-3 text-sm font-medium">{notice}</div>}
      {pageError && <div role="alert" data-testid="floor-page-error" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{pageError}</div>}
      {floors.error ? <Failure error={floors.error} onRetry={floors.reload} /> : null}
      {floors.loading && !rows && <div role="status"><span className="sr-only">Loading</span><Skeleton className="h-32 w-full" /></div>}

      {rows && grouped && (
        <>
          <section aria-label="Global floor" className="space-y-2 rounded-xl border-2 border-brand-accent bg-surface p-4" data-testid="global-card">
            <h2 className="font-display text-lg font-bold">Global default</h2>
            {grouped.global ? (
              <>
                <p className="font-mono text-3xl font-bold" data-testid="global-markup">{formatMarkup(grouped.global.minMarkupPercent)} %</p>
                <p className="text-sm text-text-muted">Minimum markup on cost. This applies to every product without a more specific row; there must always be one.</p>
                <p className="text-xs text-text-muted">Updated by {updatedByText(grouped.global)} on {when(grouped.global.updatedAtUtc)}</p>
                {canManage && <Button type="button" variant="outline" onClick={() => openEdit(grouped.global!)} disabled={busy} data-testid="global-edit">Change the global default</Button>}
              </>
            ) : (
              <p role="alert" className="text-sm font-bold text-fault">There is no global floor. Every price counts as below the floor until one is set. Ask a developer to restore it.</p>
            )}
          </section>

          <section aria-label="Specific floors" className="space-y-3">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <h2 className="font-display text-lg font-bold">Brand and category floors</h2>
              {canManage && <Button type="button" onClick={openAdd} disabled={busy} data-testid="add-floor">Add floor</Button>}
            </div>
            {grouped.specific.length === 0 ? (
              <p role="status" data-testid="empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">No brand or category floors yet; the global default applies everywhere.</p>
            ) : (
              <div className="overflow-x-auto rounded-md border border-surface-border">
                <table className="w-full text-left text-sm" data-testid="floor-table">
                  <caption className="sr-only">Brand and category margin floors, most specific first</caption>
                  <thead className="bg-bg-subtle text-xs text-text-muted">
                    <tr>
                      <th scope="col" className="px-3 py-2">Applies to</th>
                      <th scope="col" className="px-3 py-2">Kind</th>
                      <th scope="col" className="px-3 py-2 text-right">Min markup</th>
                      <th scope="col" className="px-3 py-2">Updated</th>
                      {canManage && <th scope="col" className="px-3 py-2"><span className="sr-only">Actions</span></th>}
                    </tr>
                  </thead>
                  <tbody>
                    {grouped.specific.map((r) => (
                      <tr key={r.id} className="border-t border-surface-border" data-testid="floor-row">
                        <td className="px-3 py-2 font-medium">{floorTarget(r)}</td>
                        <td className="px-3 py-2">{SCOPE_LABEL[r.scope]}</td>
                        <td className="px-3 py-2 text-right font-mono" data-testid="floor-markup">{formatMarkup(r.minMarkupPercent)} %</td>
                        <td className="px-3 py-2 text-xs text-text-muted">{updatedByText(r)}<br />{when(r.updatedAtUtc)}</td>
                        {canManage && (
                          <td className="px-3 py-2 text-right whitespace-nowrap">
                            <Button type="button" variant="outline" size="sm" onClick={() => openEdit(r)} disabled={busy} aria-label={`Change the floor for ${floorTarget(r)}`}>Change</Button>{' '}
                            <Button type="button" variant="outline" size="sm" onClick={() => { setNotice(null); setPageError(null); setFlow({ k: 'del-confirm', row: r }); }} disabled={busy} aria-label={`Delete the floor for ${floorTarget(r)}`}>Delete</Button>
                          </td>
                        )}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </>
      )}

      <EffectivePanel />

      <Modal open={flow?.k === 'form'} onOpenChange={closeIdle} contentClassName="max-h-[90vh] overflow-y-auto" title={<>{form.mode === 'add' ? 'Add a margin floor' : `Change the floor for ${form.row ? floorTarget(form.row) : ''}`}</>} description={<>The minimum markup on cost below which a price needs a reason and an administrator&apos;s approval. Markups only; costs are never shown here.</>}>
          <form onSubmit={review} noValidate className="space-y-3" aria-label="Floor form">
            {formErrors.server && <div role="alert" data-testid="floor-form-error" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{formErrors.server}</div>}
            {form.mode === 'add' && (
              <div className="flex flex-col gap-1.5">
                <label htmlFor="floor-scope" className="text-sm font-medium">Applies to</label>
                <select
                  id="floor-scope"
                  value={form.scope}
                  onChange={(e) => setForm({ ...form, scope: e.target.value as FloorScope, brand: null, categoryId: null })}
                  className="block w-full rounded-md border border-surface-border bg-surface p-2 text-sm"
                  data-testid="floor-scope"
                >
                  {(['Brand', 'Category', 'BrandAndCategory'] as FloorScope[]).map((s) => <option key={s} value={s}>{SCOPE_LABEL[s]}</option>)}
                </select>
              </div>
            )}
            {form.mode === 'add' && need.brand && <BrandBySku idPrefix="add" value={form.brand} onChange={(b) => setForm({ ...form, brand: b })} error={formErrors.shape?.includes('brand') ? formErrors.shape : undefined} />}
            {form.mode === 'add' && need.category && <CategoryPicker idPrefix="add" value={form.categoryId} onChange={(id) => setForm({ ...form, categoryId: id })} error={formErrors.shape?.includes('category') ? formErrors.shape : undefined} />}
            <Input
              label="Minimum markup on cost (%)"
              helperText="0 to 1000, at most 2 decimals, for example 12.5. Nothing is rounded for you."
              value={form.markup}
              onChange={(e) => setForm({ ...form, markup: e.target.value })}
              inputMode="decimal"
              autoComplete="off"
              error={formErrors.markup}
              data-testid="floor-markup-input"
            />
            <ModalFooter>
              <Button type="button" variant="outline" onClick={() => setFlow(null)} disabled={busy}>Cancel</Button>
              <Button type="submit" disabled={busy} data-testid="floor-review">Review</Button>
            </ModalFooter>
          </form>
      </Modal>

      <Modal open={flow?.k === 'confirm'} onOpenChange={(next) => { if (!next && !busy) setFlow({ k: 'form' }); }} title={<>Confirm the floor change</>} description={<>{flow?.k === 'confirm' ? flow.summary : ''}</>} descriptionTestId="floor-confirm-statement">
          <p className="text-sm" data-testid="floor-change-note">{FLOOR_CHANGE_NOTE}</p>
          <ModalFooter>
            <Button type="button" variant="outline" onClick={() => setFlow({ k: 'form' })} disabled={busy}>Back</Button>
            <Button type="button" onClick={() => { if (flow?.k === 'confirm') { setCodeError(null); setFlow({ k: 'code', body: flow.body, summary: flow.summary }); } }} disabled={busy} data-testid="floor-confirm">Continue to the code</Button>
          </ModalFooter>
      </Modal>

      <StepUpDialog
        open={flow?.k === 'code'}
        title="Confirm with your authenticator"
        description={flow?.k === 'code' ? flow.summary : ''}
        confirmLabel="Save floor"
        busy={busy}
        error={codeError}
        onSubmit={submitUpsert}
        onCancel={() => { if (!busy) setFlow({ k: 'form' }); }}
      />

      <Modal open={flow?.k === 'del-confirm'} onOpenChange={closeIdle} title={<>Delete this floor?</>} description={<>{flow?.k === 'del-confirm' ? `Delete the ${formatMarkup(flow.row.minMarkupPercent)} % floor for "${floorTarget(flow.row)}". Products there follow the next more general row.` : ''}</>} descriptionTestId="floor-delete-statement">
          <p className="text-sm">{FLOOR_CHANGE_NOTE}</p>
          <ModalFooter>
            <Button type="button" variant="outline" onClick={() => setFlow(null)} disabled={busy}>Cancel</Button>
            <Button type="button" onClick={() => { if (flow?.k === 'del-confirm') { setCodeError(null); setFlow({ k: 'del-code', row: flow.row }); } }} disabled={busy} data-testid="floor-delete-continue">Continue to the code</Button>
          </ModalFooter>
      </Modal>

      <StepUpDialog
        open={flow?.k === 'del-code'}
        title="Confirm the deletion"
        description={flow?.k === 'del-code' ? `Deleting the floor for "${floorTarget(flow.row)}".` : ''}
        confirmLabel="Delete floor"
        busy={busy}
        error={codeError}
        onSubmit={submitDelete}
        onCancel={() => { if (!busy) setFlow(null); }}
      />
    </div>
  );
}

export function MarginFloorsView() {
  const { user } = useAuth();
  const held = new Set((user?.permissions ?? []).map((p) => p.toLowerCase()));
  return (
    <ConsoleShell activeItemId="margin-floors" title="Margin floors">
      {held.has(FLOOR_PERMISSIONS.view) ? (
        <Floors canManage={held.has(FLOOR_PERMISSIONS.manage)} />
      ) : (
        <p role="alert" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">Your role does not allow this page ({FLOOR_PERMISSIONS.view}).</p>
      )}
    </ConsoleShell>
  );
}
