'use client';

import * as React from 'react';
import Link from 'next/link';
import { Button, Input, Skeleton } from '@mechware/ui';
import {
  MAX_ALTERNATIVES, MAX_PART_NUMBER, SUPPLY_STATUSES, addAlternative, groupVariantsByProduct, moveItem, supplyBody, supplyToForm, validateOrderUnits, validatePartNumber,
  type FieldErrors, type AdminProductDetail, type ProductPim, type SupplyForm, type SupplyStatus
} from '@mechware/api-client';
import { catalogAdminApi, catalogError, lookupsApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { BasicsPanel, FieldError, LifecyclePanel, SECTION, SaveBar, VariantCommercial, useSaver } from './ProductPanels';
import { NoAccess } from './ShippingZonesView';

const SELECT = 'block w-full rounded-md border border-surface-border bg-surface p-2 text-sm';

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

function VariantRow({ pim, variant, onSaved, onReload }: { pim: ProductPim; variant: ProductPim['variants'][number]; onSaved: (p: ProductPim) => void; onReload: () => void }) {
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
      <p className="text-sm font-medium">Order units and part number of {variant.sku}</p>
      <div className="grid gap-3 sm:grid-cols-3">
        <Input label="Minimum order quantity" inputMode="numeric" value={min} disabled={saver.state.busy} onChange={(e) => setMin(e.target.value)} error={errors.minOrderQty} />
        <Input label="Pack size" inputMode="numeric" value={pack} disabled={saver.state.busy} onChange={(e) => setPack(e.target.value)} error={errors.packSize} />
        <Input label="Manufacturer part number" value={pn} maxLength={MAX_PART_NUMBER} disabled={saver.state.busy} onChange={(e) => setPn(e.target.value)} error={errors.manufacturerPartNumber} helperText="Blank clears it." />
      </div>
      <SaveBar state={saver.state} label={`Save ${variant.sku}`} onReload={onReload} testId={`variant-${variant.sku}`} />
    </form>
  );
}

function Variants({ detail, pim, onDetail, onPim, onReload }: { detail: AdminProductDetail; pim: ProductPim; onDetail: (d: AdminProductDetail) => void; onPim: (p: ProductPim) => void; onReload: () => void }) {
  return (
    <section aria-labelledby="variants" className={SECTION}>
      <h2 id="variants" className="font-display text-lg font-bold">Variants ({detail.variants.length})</h2>
      <p className="text-xs text-text-muted">Weight, price and status save with the product version. Order units: the minimum must be a whole number of packs. The server checks every value again.</p>
      <div className="space-y-5">
        {detail.variants.map((v) => {
          const units = pim.variants.find((x) => x.variantId === v.id);
          return (
            <div key={v.id} className="space-y-2" data-testid="sku-card">
              <VariantCommercial detail={detail} variant={v} onDetail={onDetail} onReload={onReload} />
              {units && <VariantRow pim={pim} variant={units} onSaved={onPim} onReload={onReload} />}
            </div>
          );
        })}
      </div>
    </section>
  );
}

function Editor({ id }: { id: string }) {
  const res = useResource(async () => { const [d, p] = await Promise.all([catalogAdminApi.productGet(id), catalogAdminApi.pim(id)]); return { detail: d.data, pim: p.data }; }, id);
  const [detail, setDetail] = React.useState<AdminProductDetail | null>(null);
  const [pim, setPim] = React.useState<ProductPim | null>(null);
  const [epoch, setEpoch] = React.useState(0);
  React.useEffect(() => { if (res.data) { setDetail(res.data.detail); setPim(res.data.pim); setEpoch((n) => n + 1); } }, [res.data]);
  const reload = () => res.reload();
  /** The supply, alternatives and order-unit saves touch the product row too: pick up its new version without touching the forms. */
  const afterPim = (p: ProductPim) => { setPim(p); void catalogAdminApi.productGet(id).then((d) => setDetail(d.data), () => undefined); };
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
      {res.loading && !detail && !failure && <div role="status"><span className="sr-only">Loading</span><Skeleton className="h-48 w-full" /></div>}
      {detail && pim && (
        <div key={epoch} className="space-y-5">
          <h2 className="sr-only">Product</h2>
          <p className="font-display text-xl font-bold" data-testid="product-title">{detail.titleEn}</p>
          <LifecyclePanel detail={detail} onDetail={setDetail} onReload={reload} />
          <BasicsPanel detail={detail} onDetail={setDetail} onReload={reload} />
          <SupplySection pim={pim} onSaved={afterPim} onReload={reload} />
          <AlternativesSection pim={pim} onSaved={afterPim} onReload={reload} />
          <Variants detail={detail} pim={pim} onDetail={setDetail} onPim={afterPim} onReload={reload} />
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
