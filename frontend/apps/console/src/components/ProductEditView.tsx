'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge, Button, Input, Skeleton } from '@mechware/ui';
import {
  MAX_ALTERNATIVES, MAX_PART_NUMBER, SUPPLY_STATUSES, addAlternative, formatTaka, groupVariantsByProduct, moveItem, normaliseStatus, productStatusLabel,
  supplyBody, supplyToForm, validateOrderUnits, validatePartNumber,
  type FieldErrors, type ProductPim, type SupplyForm, type SupplyStatus
} from '@mechware/api-client';
import { catalogAdminApi, catalogError, lookupsApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { NoAccess } from './ShippingZonesView';

const SELECT = 'block w-full rounded-md border border-surface-border bg-surface p-2 text-sm';
const SECTION = 'space-y-4 rounded-xl border border-surface-border bg-surface p-4';
const FieldError = ({ text }: { text?: string }) => (text ? <p role="alert" className="mt-1 text-xs font-medium text-fault">{text}</p> : null);

interface SaveState { busy: boolean; failure: string | null; conflict: boolean; fields: FieldErrors; saved: boolean }
const IDLE: SaveState = { busy: false, failure: null, conflict: false, fields: {}, saved: false };

/** Runs one save: no optimistic update; the server's answer replaces the data, a failure leaves the form as typed. */
function useSaver() {
  const [state, setState] = React.useState<SaveState>(IDLE);
  const run = async (call: () => Promise<ProductPim>, onDone: (p: ProductPim) => void) => {
    setState({ ...IDLE, busy: true });
    try {
      const pim = await call();
      onDone(pim);
      setState({ ...IDLE, saved: true });
    } catch (err) {
      const f = catalogError(err);
      setState({ busy: false, failure: f.text, conflict: f.conflict, fields: f.fields, saved: false });
    }
  };
  return { state, run, fail: (fields: FieldErrors) => setState({ ...IDLE, failure: 'Fix the highlighted fields.', fields }), clear: () => setState(IDLE) };
}

function SaveBar({ state, label, onReload, testId }: { state: SaveState; label: string; onReload: () => void; testId: string }) {
  return (
    <div className="space-y-2">
      {state.failure && (
        <div role="alert" data-testid={`${testId}-failure`} className="flex flex-wrap items-center gap-3 rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">
          <span>{state.failure}</span>
          {state.conflict && <Button type="button" variant="outline" size="sm" onClick={onReload}>Reload</Button>}
        </div>
      )}
      {state.saved && <p role="status" data-testid={`${testId}-saved`} className="text-sm font-medium text-success">Saved.</p>}
      <Button type="submit" isLoading={state.busy} disabled={state.busy}>{label}</Button>
    </div>
  );
}

/** Finds products by title, SKU or part number through the SKU lookup; one result per product. */
function ProductPicker({ label, excludeId, onPick, testId }: { label: string; excludeId: string; onPick: (id: string, title: string) => void; testId: string }) {
  const [q, setQ] = React.useState('');
  const [busy, setBusy] = React.useState(false);
  const [problem, setProblem] = React.useState<string | null>(null);
  const [found, setFound] = React.useState<{ productId: string; title: string; skus: string[] }[] | null>(null);
  const search = async () => {
    if (!q.trim()) { setProblem('Type a title, SKU or part number.'); return; }
    setBusy(true);
    setProblem(null);
    try {
      const page = await lookupsApi.variants({ q: q.trim(), pageSize: 20 });
      setFound(groupVariantsByProduct(page.data.items).filter((r) => r.productId !== excludeId));
    } catch (err) {
      setProblem(catalogError(err).text);
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="space-y-2" data-testid={testId}>
      <div className="flex flex-wrap items-end gap-2">
        <div className="min-w-0 flex-1 basis-56">
          <Input label={label} value={q} onChange={(e) => setQ(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); void search(); } }} autoComplete="off" error={problem ?? undefined} />
        </div>
        <Button type="button" variant="outline" onClick={() => void search()} isLoading={busy} disabled={busy}>Find</Button>
      </div>
      {found && found.length === 0 && <p role="status" className="text-sm text-text-muted">No other product matches.</p>}
      {found && found.length > 0 && (
        <ul className="divide-y divide-surface-border rounded-md border border-surface-border">
          {found.map((r) => (
            <li key={r.productId} className="flex flex-wrap items-center justify-between gap-2 p-2 text-sm">
              <span>{r.title} <span className="font-mono text-xs text-text-muted">{r.skus.slice(0, 2).join(', ')}</span></span>
              <Button type="button" size="sm" variant="outline" onClick={() => { onPick(r.productId, r.title); setFound(null); }}>Use</Button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function Basics({ pim }: { pim: ProductPim }) {
  return (
    <section aria-labelledby="basics" className={SECTION}>
      <h2 id="basics" className="font-display text-lg font-bold">Basics</h2>
      <dl className="grid gap-2 text-sm sm:grid-cols-[10rem_1fr]">
        <dt className="text-text-muted">Title (English)</dt><dd data-testid="product-title" className="font-semibold">{pim.titleEn}</dd>
        <dt className="text-text-muted">Slug</dt><dd className="font-mono text-xs">{pim.slug}</dd>
        <dt className="text-text-muted">Lifecycle status</dt><dd><Badge variant={normaliseStatus(pim.status) === 'active' ? 'success' : 'outline'} data-testid="product-status">{productStatusLabel(pim.status)}</Badge></dd>
      </dl>
      <p className="text-xs text-text-muted">
        Titles (English and Bangla), the description and attributes, publish, unpublish and archive are not editable here yet: the admin API has no endpoints for them (backend follow-up). Imports and sellers change them for now.
      </p>
    </section>
  );
}

function SupplySection({ pim, onSaved, onReload }: { pim: ProductPim; onSaved: (p: ProductPim) => void; onReload: () => void }) {
  const [form, setForm] = React.useState<SupplyForm>(() => supplyToForm(pim));
  const [successorTitle, setSuccessorTitle] = React.useState(pim.successor?.titleEn ?? '');
  const [local, setLocal] = React.useState<FieldErrors>({});
  const saver = useSaver();
  const errors = { ...saver.state.fields, ...local };
  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    const { body, errors: bad } = supplyBody(form, pim);
    setLocal(bad);
    if (!body) return saver.fail(bad);
    void saver.run(async () => (await catalogAdminApi.setSupply(pim.id, body)).data, (p) => { onSaved(p); setForm(supplyToForm(p)); setSuccessorTitle(p.successor?.titleEn ?? ''); });
  };
  return (
    <form onSubmit={submit} aria-labelledby="supply" className={SECTION} noValidate>
      <h2 id="supply" className="font-display text-lg font-bold">Supply status and successor</h2>
      <div>
        <label htmlFor="supply-status" className="mb-1 block text-sm font-medium">Supply status</label>
        <select id="supply-status" className={SELECT} value={form.supplyStatus} disabled={saver.state.busy} onChange={(e) => setForm({ ...form, supplyStatus: e.target.value as SupplyStatus })}>
          {SUPPLY_STATUSES.map((s) => <option key={s.value} value={s.value}>{s.label}</option>)}
        </select>
        <FieldError text={errors.supplyStatus} />
      </div>
      <div className="space-y-2">
        <p className="text-sm font-medium">Successor</p>
        {form.successorId ? (
          <p className="flex flex-wrap items-center gap-3 text-sm" data-testid="successor-chosen">
            <span className="font-semibold">{successorTitle || 'Selected product'}</span>
            <Button type="button" size="sm" variant="outline" disabled={saver.state.busy} onClick={() => { setForm({ ...form, successorId: null, isDropIn: false, note: '' }); setSuccessorTitle(''); }}>Remove successor</Button>
          </p>
        ) : <p className="text-sm text-text-muted">No successor. Only a phased-out product can name one; it must be published and in supply.</p>}
        <ProductPicker label="Find a successor (title, SKU or part number)" excludeId={pim.id} testId="successor-picker" onPick={(id, title) => { setForm({ ...form, successorId: id }); setSuccessorTitle(title); }} />
        <FieldError text={errors.successorProductId} />
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={form.isDropIn} disabled={saver.state.busy || !form.successorId} onChange={(e) => setForm({ ...form, isDropIn: e.target.checked })} />
          The successor is a drop-in replacement
        </label>
        <Input label="Compatibility note" value={form.note} disabled={saver.state.busy || !form.successorId} onChange={(e) => setForm({ ...form, note: e.target.value })} error={errors.successorNote} maxLength={500} />
      </div>
      <SaveBar state={saver.state} label="Save supply status" onReload={onReload} testId="supply" />
    </form>
  );
}

function AlternativesSection({ pim, onSaved, onReload }: { pim: ProductPim; onSaved: (p: ProductPim) => void; onReload: () => void }) {
  const toItems = (p: ProductPim) => p.alternatives.map((a) => ({ id: a.productId, title: a.titleEn, listed: a.listed }));
  const [items, setItems] = React.useState(() => toItems(pim));
  const [problem, setProblem] = React.useState<string | null>(null);
  const saver = useSaver();
  const add = (id: string, title: string) => {
    const r = addAlternative(items.map((i) => i.id), id, pim.id);
    if (r.error) return setProblem(r.error);
    setProblem(null);
    setItems([...items, { id, title, listed: true }]);
  };
  const move = (from: number, to: number) => setItems(moveItem(items, from, to));
  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    void saver.run(async () => (await catalogAdminApi.setAlternatives(pim.id, items.map((i) => i.id))).data, (p) => { onSaved(p); setItems(toItems(p)); });
  };
  return (
    <form onSubmit={submit} aria-labelledby="alts" className={SECTION}>
      <h2 id="alts" className="font-display text-lg font-bold">Alternatives ({items.length} of {MAX_ALTERNATIVES})</h2>
      {items.length === 0 ? <p className="text-sm text-text-muted" data-testid="alts-empty">No alternatives listed.</p> : (
        <ol className="divide-y divide-surface-border rounded-md border border-surface-border" data-testid="alts-list">
          {items.map((a, i) => (
            <li key={a.id} className="flex flex-wrap items-center justify-between gap-2 p-2 text-sm">
              <span>{i + 1}. {a.title}{!a.listed && <span className="ml-2 font-medium text-fault">Not listed for buyers: replace it</span>}</span>
              <span className="flex gap-1">
                <Button type="button" size="sm" variant="outline" aria-label={`Move ${a.title} up`} disabled={i === 0 || saver.state.busy} onClick={() => move(i, i - 1)}>Up</Button>
                <Button type="button" size="sm" variant="outline" aria-label={`Move ${a.title} down`} disabled={i === items.length - 1 || saver.state.busy} onClick={() => move(i, i + 1)}>Down</Button>
                <Button type="button" size="sm" variant="outline" aria-label={`Remove ${a.title}`} disabled={saver.state.busy} onClick={() => setItems(items.filter((x) => x.id !== a.id))}>Remove</Button>
              </span>
            </li>
          ))}
        </ol>
      )}
      <ProductPicker label="Add an alternative (title, SKU or part number)" excludeId={pim.id} testId="alt-picker" onPick={add} />
      <FieldError text={problem ?? saver.state.fields.alternativeProductIds} />
      <SaveBar state={saver.state} label="Save alternatives" onReload={onReload} testId="alts" />
    </form>
  );
}

function VariantRow({ pim, variant, extra, onSaved, onReload }: { pim: ProductPim; variant: ProductPim['variants'][number]; extra?: { priceMinor: number; active: boolean }; onSaved: (p: ProductPim) => void; onReload: () => void }) {
  const [min, setMin] = React.useState(String(variant.minOrderQty));
  const [pack, setPack] = React.useState(String(variant.packSize));
  const [pn, setPn] = React.useState(variant.manufacturerPartNumber ?? '');
  const [local, setLocal] = React.useState<FieldErrors>({});
  const saver = useSaver();
  const errors = { ...saver.state.fields, ...local };
  const sync = (p: ProductPim) => {
    const v = p.variants.find((x) => x.variantId === variant.variantId);
    if (v) { setMin(String(v.minOrderQty)); setPack(String(v.packSize)); setPn(v.manufacturerPartNumber ?? ''); }
    onSaved(p);
  };
  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    const units = validateOrderUnits(min, pack);
    const part = validatePartNumber(pn);
    const bad: FieldErrors = { ...units.errors, ...(part.error ? { manufacturerPartNumber: part.error } : {}) };
    setLocal(bad);
    if (Object.keys(bad).length) return saver.fail(bad);
    const unitsChanged = !!units.value && (units.value.minOrderQty !== variant.minOrderQty || units.value.packSize !== variant.packSize);
    const partChanged = part.value !== variant.manufacturerPartNumber;
    if (!unitsChanged && !partChanged) return saver.clear();
    void saver.run(async () => {
      let latest: ProductPim | null = null;
      if (unitsChanged && units.value) { latest = (await catalogAdminApi.setOrderUnits(pim.id, variant.variantId, units.value)).data; onSaved(latest); }
      if (partChanged) latest = (await catalogAdminApi.setPartNumber(pim.id, variant.variantId, part.value)).data;
      return latest as ProductPim;
    }, sync);
  };
  return (
    <form onSubmit={submit} className="space-y-3 rounded-md border border-surface-border p-3" data-testid="variant-row" aria-label={`SKU ${variant.sku}`} noValidate>
      <div className="flex flex-wrap items-center gap-3">
        <span className="font-mono text-sm font-semibold" data-testid="variant-sku">{variant.sku}</span>
        {extra && <span className="text-sm">List price {formatTaka(extra.priceMinor)} <span className="text-text-muted">(VAT included, set in price lists)</span></span>}
        {extra && <Badge variant={extra.active ? 'success' : 'outline'}>{extra.active ? 'SKU active' : 'SKU inactive'}</Badge>}
      </div>
      <div className="grid gap-3 sm:grid-cols-3">
        <Input label="Minimum order quantity" inputMode="numeric" value={min} disabled={saver.state.busy} onChange={(e) => setMin(e.target.value)} error={errors.minOrderQty} />
        <Input label="Pack size" inputMode="numeric" value={pack} disabled={saver.state.busy} onChange={(e) => setPack(e.target.value)} error={errors.packSize} />
        <Input label="Manufacturer part number" value={pn} maxLength={MAX_PART_NUMBER} disabled={saver.state.busy} onChange={(e) => setPn(e.target.value)} error={errors.manufacturerPartNumber} helperText="Blank clears it." />
      </div>
      <p className="text-xs text-text-muted" data-testid="weight-note">
        Weight (kg): needed for shipping prices; orders are refused without it. It cannot be changed here yet because the admin API has no weight endpoint (backend follow-up).
      </p>
      <SaveBar state={saver.state} label={`Save ${variant.sku}`} onReload={onReload} testId={`variant-${variant.sku}`} />
    </form>
  );
}

function Variants({ pim, onSaved, onReload }: { pim: ProductPim; onSaved: (p: ProductPim) => void; onReload: () => void }) {
  const skus = pim.variants.map((v) => v.sku).join('|');
  const extras = useResource(async () => {
    const out = new Map<string, { priceMinor: number; active: boolean }>();
    await Promise.all(pim.variants.map(async (v) => {
      const hit = (await lookupsApi.variants({ sku: v.sku, pageSize: 5 })).data.items.find((x) => x.variantId === v.variantId);
      if (hit) out.set(v.variantId, { priceMinor: hit.priceMinor, active: hit.active });
    }));
    return out;
  }, skus);
  return (
    <section aria-labelledby="variants" className={SECTION}>
      <h2 id="variants" className="font-display text-lg font-bold">Variants ({pim.variants.length})</h2>
      <p className="text-xs text-text-muted">Order units: the minimum must be a whole number of packs. The server checks every value again.</p>
      <div className="space-y-3">
        {pim.variants.map((v) => <VariantRow key={v.variantId} pim={pim} variant={v} extra={extras.data?.get(v.variantId)} onSaved={onSaved} onReload={onReload} />)}
      </div>
    </section>
  );
}

function Editor({ id }: { id: string }) {
  const res = useResource(() => catalogAdminApi.pim(id), id);
  const [pim, setPim] = React.useState<ProductPim | null>(null);
  const [epoch, setEpoch] = React.useState(0);
  React.useEffect(() => { if (res.data) { setPim(res.data.data); setEpoch((n) => n + 1); } }, [res.data]);
  const reload = () => res.reload();
  const failure = res.error ? catalogError(res.error) : null;
  return (
    <div className="space-y-5">
      <Link href="/catalog/products" className="inline-block text-sm font-semibold text-brand-accent underline">All products</Link>
      {failure && (
        <div role="alert" className="flex flex-wrap items-center gap-3 rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">
          <span>{failure.text}</span>
          {failure.retry && <Button type="button" variant="outline" size="sm" onClick={reload}>Try again</Button>}
        </div>
      )}
      {res.loading && !pim && !failure && <div role="status"><span className="sr-only">Loading</span><Skeleton className="h-48 w-full" /></div>}
      {pim && (
        <div key={epoch} className="space-y-5">
          <Basics pim={pim} />
          <SupplySection pim={pim} onSaved={setPim} onReload={reload} />
          <AlternativesSection pim={pim} onSaved={setPim} onReload={reload} />
          <Variants pim={pim} onSaved={setPim} onReload={reload} />
        </div>
      )}
    </div>
  );
}

export function ProductEditView({ id }: { id: string }) {
  const { user } = useAuth();
  const held = new Set((user?.permissions ?? []).map((p) => p.toLowerCase()));
  return (
    <ConsoleShell activeItemId="catalog-products" title="Edit product">
      {held.has('catalog.manage') ? <Editor id={id} /> : <NoAccess permission="catalog.manage" />}
    </ConsoleShell>
  );
}
