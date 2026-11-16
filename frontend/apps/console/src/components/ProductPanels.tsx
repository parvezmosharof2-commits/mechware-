'use client';

import * as React from 'react';
import { Badge, Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, Input, Textarea } from '@mechware/ui';
import {
  ALLOWED_MARKUP_HINT, MAX_DESCRIPTION_HTML, MAX_SHORT_DESCRIPTION, MAX_TITLE, basicsToForm, formatTaka, lifecycleButtons, minorToTakaText, productStatusLabel, statusBadgeVariant,
  validateBasics, validateVariantEdit, weightToText,
  type BasicsForm, type FieldErrors, type AdminProductDetail, type AdminProductDetailVariant
} from '@mechware/api-client';
import { catalogAdminApi, catalogError } from '../lib/api';
import { BrandLookupPicker, CategoryLookupPicker } from './LookupPicker';

const SELECT = 'block w-full rounded-md border border-surface-border bg-surface p-2 text-sm';
export const SECTION = 'space-y-4 rounded-xl border border-surface-border bg-surface p-4';
export const FieldError = ({ text }: { text?: string }) => (text ? <p role="alert" className="mt-1 text-xs font-medium text-fault">{text}</p> : null);

export interface SaveState { busy: boolean; failure: string | null; conflict: boolean; fields: FieldErrors; saved: boolean }
const IDLE: SaveState = { busy: false, failure: null, conflict: false, fields: {}, saved: false };

/** Runs one save: no optimistic update; the server's answer replaces the data, a failure leaves the form as typed. */
export function useSaver() {
  const [state, setState] = React.useState<SaveState>(IDLE);
  const run = async <T,>(call: () => Promise<T>, onDone: (t: T) => void): Promise<boolean> => {
    setState({ ...IDLE, busy: true });
    try {
      const out = await call();
      onDone(out);
      setState({ ...IDLE, saved: true });
      return true;
    } catch (err) {
      const f = catalogError(err);
      setState({ busy: false, failure: f.text, conflict: f.conflict, fields: f.fields, saved: false });
      return false;
    }
  };
  return { state, run, fail: (fields: FieldErrors) => setState({ ...IDLE, failure: 'Fix the highlighted fields.', fields }), clear: () => setState(IDLE) };
}

export function SaveBar({ state, label, onReload, testId, hideButton }: { state: SaveState; label: string; onReload: () => void; testId: string; hideButton?: boolean }) {
  return (
    <div className="space-y-2">
      {state.failure && (
        <div role="alert" data-testid={`${testId}-failure`} className="flex flex-wrap items-center gap-3 rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">
          <span>{state.failure}</span>
          {state.conflict && <Button type="button" variant="outline" size="sm" onClick={onReload}>Reload</Button>}
        </div>
      )}
      {state.saved && <p role="status" data-testid={`${testId}-saved`} className="text-sm font-medium text-success">Saved.</p>}
      {!hideButton && <Button type="submit" isLoading={state.busy} disabled={state.busy}>{label}</Button>}
    </div>
  );
}

function ConfirmDialog(props: { open: boolean; title: string; body: React.ReactNode; confirmLabel: string; busy: boolean; onConfirm: () => void; onCancel: () => void; testId: string }) {
  return (
    <Dialog open={props.open} onOpenChange={(next) => { if (!next && !props.busy) props.onCancel(); }}>
      <DialogContent data-testid={props.testId}>
        <DialogHeader>
          <DialogTitle>{props.title}</DialogTitle>
          <DialogDescription>{props.body}</DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={props.onCancel} disabled={props.busy}>Cancel</Button>
          <Button type="button" onClick={props.onConfirm} isLoading={props.busy} disabled={props.busy}>{props.confirmLabel}</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

interface PanelProps { detail: AdminProductDetail; onDetail: (d: AdminProductDetail) => void; onReload: () => void }

export function BasicsPanel({ detail, onDetail, onReload }: PanelProps) {
  const [form, setForm] = React.useState<BasicsForm>(() => basicsToForm(detail));
  const [local, setLocal] = React.useState<FieldErrors>({});
  const saver = useSaver();
  const errors = { ...saver.state.fields, ...local };
  const busy = saver.state.busy;
  const set = (patch: Partial<BasicsForm>) => setForm({ ...form, ...patch });
  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    const { body, errors: bad } = validateBasics(form, detail.rowVersion);
    setLocal(bad);
    if (!body) return saver.fail(bad);
    void saver.run(async () => (await catalogAdminApi.productUpdate(detail.id, body)).data, (d) => { onDetail(d); setForm(basicsToForm(d)); });
  };
  const text = (key: keyof BasicsForm & string, label: string, max: number) => (
    <Input label={label} value={form[key] as string} maxLength={max + 50} disabled={busy} onChange={(e) => set({ [key]: e.target.value })} error={errors[key]} />
  );
  return (
    <form onSubmit={submit} aria-labelledby="basics" className={SECTION} noValidate>
      <h2 id="basics" className="font-display text-lg font-bold">Basics</h2>
      <dl className="grid gap-1 text-sm sm:grid-cols-[10rem_1fr]">
        <dt className="text-text-muted">Slug (never changes)</dt><dd className="font-mono text-xs">{detail.slug}</dd>
        <dt className="text-text-muted">Seller</dt><dd>{detail.sellerStoreName}</dd>
      </dl>
      <div className="grid gap-3 sm:grid-cols-2">
        {text('titleEn', 'Title (English)', MAX_TITLE)}
        {text('titleBn', 'Title (Bangla)', MAX_TITLE)}
        <Textarea label="Short description (English)" rows={3} value={form.shortDescriptionEn} disabled={busy} onChange={(e) => set({ shortDescriptionEn: e.target.value })} error={errors.shortDescriptionEn} helperText={`At most ${MAX_SHORT_DESCRIPTION.toLocaleString('en-US')} characters.`} />
        <Textarea label="Short description (Bangla)" rows={3} value={form.shortDescriptionBn} disabled={busy} onChange={(e) => set({ shortDescriptionBn: e.target.value })} error={errors.shortDescriptionBn} helperText={`At most ${MAX_SHORT_DESCRIPTION.toLocaleString('en-US')} characters.`} />
      </div>
      <Textarea label="Description (HTML)" rows={8} value={form.descriptionHtml} disabled={busy} onChange={(e) => set({ descriptionHtml: e.target.value })} error={errors.descriptionHtml}
        helperText={`${ALLOWED_MARKUP_HINT} At most ${MAX_DESCRIPTION_HTML.toLocaleString('en-US')} characters. One description for both languages.`} />
      <div className="grid gap-4 sm:grid-cols-2">
        <BrandLookupPicker idPrefix="basics" label="Brand" value={form.brand} onChange={(b) => set({ brand: b })} disabled={busy} error={errors.brandId} />
        <CategoryLookupPicker idPrefix="basics" label="Category" value={form.category} onChange={(c) => set({ category: c })} disabled={busy} error={errors.categoryId} />
      </div>
      <div className="grid gap-3 sm:grid-cols-3">
        {(['lengthCm', 'widthCm', 'heightCm'] as const).map((k, i) => (
          <Input key={k} label={`${['Length', 'Width', 'Height'][i]} (cm)`} inputMode="decimal" value={form[k]} disabled={busy} onChange={(e) => set({ [k]: e.target.value })} error={errors[k]} />
        ))}
      </div>
      <SaveBar state={saver.state} label="Save basics" onReload={onReload} testId="basics" />
    </form>
  );
}

/** Weight, VAT-inclusive list price and active/inactive of one SKU. A price change is confirmed first and is audited by the server. */
export function VariantCommercial({ detail, variant, onDetail, onReload }: PanelProps & { variant: AdminProductDetailVariant }) {
  const initial = (v: AdminProductDetailVariant) => ({ weight: weightToText(v.weightKg), price: minorToTakaText(v.priceMinor), status: v.status });
  const [f, setF] = React.useState(() => initial(variant));
  const [local, setLocal] = React.useState<FieldErrors>({});
  const [confirm, setConfirm] = React.useState<{ from: number; to: number } | null>(null);
  const saver = useSaver();
  const errors = { ...saver.state.fields, ...local };
  const busy = saver.state.busy;
  const send = (body: NonNullable<ReturnType<typeof validateVariantEdit>['body']>) =>
    saver.run(async () => (await catalogAdminApi.variantUpdate(detail.id, variant.id, body)).data, (d) => {
      onDetail(d);
      const nv = d.variants.find((x) => x.id === variant.id);
      if (nv) setF(initial(nv));
    }).then(() => setConfirm(null));
  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    const r = validateVariantEdit(variant, f, detail.rowVersion);
    setLocal(r.errors);
    if (r.errors.form) return saver.clear();
    if (!r.body) return saver.fail(r.errors);
    if (r.priceChange) { setConfirm(r.priceChange); pending.current = r.body; return; }
    void send(r.body);
  };
  const pending = React.useRef<NonNullable<ReturnType<typeof validateVariantEdit>['body']> | null>(null);
  return (
    <form onSubmit={submit} className="space-y-3 rounded-md border border-surface-border p-3" data-testid="variant-commercial" aria-label={`Price and weight of ${variant.sku}`} noValidate>
      <div className="flex flex-wrap items-center gap-3">
        <span className="font-mono text-sm font-semibold" data-testid="variant-sku">{variant.sku}</span>
        <Badge variant={variant.status === 'active' ? 'success' : 'outline'}>{variant.status === 'active' ? 'SKU active' : 'SKU inactive'}</Badge>
        {variant.weightKg <= 0 && <Badge variant="low-stock">No weight yet</Badge>}
      </div>
      <div className="grid gap-3 sm:grid-cols-3">
        <Input label="Weight (kg)" inputMode="decimal" value={f.weight} disabled={busy} onChange={(e) => setF({ ...f, weight: e.target.value })} error={errors.weightKg}
          helperText="Needed for shipping prices; orders are refused without it." />
        <Input label="List price, VAT included (BDT)" inputMode="decimal" value={f.price} disabled={busy} onChange={(e) => setF({ ...f, price: e.target.value })} error={errors.priceMinor}
          helperText="Changing it changes what customers pay at once." />
        <div>
          <label htmlFor={`status-${variant.id}`} className="mb-1 block text-sm font-medium">SKU status</label>
          <select id={`status-${variant.id}`} className={SELECT} value={f.status} disabled={busy} onChange={(e) => setF({ ...f, status: e.target.value })}>
            <option value="active">Active</option>
            <option value="inactive">Inactive</option>
          </select>
          <FieldError text={errors.status} />
        </div>
      </div>
      <FieldError text={errors.body} />
      <SaveBar state={saver.state} label="Save price and weight" onReload={onReload} testId={`commercial-${variant.sku}`} />
      <ConfirmDialog
        open={!!confirm} title={`Change the price of ${variant.sku}?`} confirmLabel="Change price" busy={busy} testId="price-confirm"
        body={confirm && <>From <strong>{formatTaka(confirm.from)}</strong> to <strong>{formatTaka(confirm.to)}</strong> BDT, VAT included. This changes what customers pay immediately. The change is recorded in the audit log.</>}
        onCancel={() => setConfirm(null)} onConfirm={() => { if (pending.current) void send(pending.current); }} />
    </form>
  );
}

/** Publish, archive and restore as the server allows them; each is confirmed and a refusal is shown in plain words. */
export function LifecyclePanel({ detail, onDetail, onReload }: PanelProps) {
  const [asking, setAsking] = React.useState<{ action: string; label: string; confirm: string } | null>(null);
  const saver = useSaver();
  const buttons = lifecycleButtons(detail.allowedTransitions);
  const go = () => {
    if (!asking) return;
    const call = { publish: catalogAdminApi.publish, archive: catalogAdminApi.archive, restore: catalogAdminApi.restore }[asking.action];
    if (!call) return;
    void saver.run(async () => (await call(detail.id, detail.rowVersion)).data, (d) => onDetail(d)).then((ok) => { if (ok) setAsking(null); });
  };
  return (
    <section aria-labelledby="lifecycle" className={SECTION}>
      <h2 id="lifecycle" className="font-display text-lg font-bold">Lifecycle</h2>
      <p className="flex flex-wrap items-center gap-2 text-sm">
        Status: <Badge variant={statusBadgeVariant(detail.status)} data-testid="product-status">{productStatusLabel(detail.status)}</Badge>
        {detail.rejectionReason && <span className="text-text-muted">Rejected: {detail.rejectionReason}</span>}
      </p>
      {buttons.length === 0 ? <p className="text-sm text-text-muted">No status change is possible from here.</p> : (
        <div className="flex flex-wrap gap-2">
          {buttons.map((b) => <Button key={b.action} type="button" variant={b.action === 'publish' ? 'primary' : 'outline'} disabled={saver.state.busy} onClick={() => { saver.clear(); setAsking(b); }}>{b.label}</Button>)}
        </div>
      )}
      <SaveBar state={asking ? { ...saver.state, failure: null } : saver.state} label="" onReload={onReload} testId="lifecycle" hideButton />
      <ConfirmDialog
        open={!!asking} title={`${asking?.label ?? ''} this product?`} confirmLabel={asking?.label ?? ''} busy={saver.state.busy} testId="lifecycle-confirm"
        body={<>{asking?.confirm}{saver.state.failure && <span role="alert" data-testid="lifecycle-refusal" className="mt-2 block font-medium text-fault">{saver.state.failure}</span>}</>}
        onCancel={() => { setAsking(null); saver.clear(); }} onConfirm={go} />
    </section>
  );
}
