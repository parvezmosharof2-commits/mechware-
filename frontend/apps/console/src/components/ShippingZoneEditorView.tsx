'use client';

import * as React from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { Badge, Button, Input, Skeleton } from '@mechware/ui';
import {
  SHIPPING_MAX_BANDS, SHIPPING_MAX_DISTRICTS, detailToForm, emptyZoneForm, formToBody, groupByDivision, otherZoneOwner,
  type CoverageDistrict, type ShippingZoneDetail, type ZoneErrors, type ZoneForm
} from '@mechware/api-client';
import { shippingError, shippingZonesApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { LoadFailure, NoAccess } from './ShippingZonesView';
import { TariffPreview } from './TariffPreview';
import { Modal, ModalFooter } from './Modal';

const CELL = 'block w-full rounded-md border border-surface-border bg-surface p-2 text-sm';
const FieldError = ({ text }: { text?: string }) => (text ? <p role="alert" className="mt-1 text-xs font-medium text-fault">{text}</p> : null);

function DistrictPicker({ districts, selected, ownZoneId, readOnly, error, onChange }: {
  districts: CoverageDistrict[]; selected: string[]; ownZoneId: string | null; readOnly: boolean; error?: string; onChange: (ids: string[]) => void;
}) {
  const [search, setSearch] = React.useState('');
  const term = search.trim().toLowerCase();
  const shown = term ? districts.filter((d) => d.nameEn.toLowerCase().includes(term) || d.nameBn.includes(search.trim())) : districts;
  const chosen = new Set(selected);
  const toggle = (id: string) => onChange(chosen.has(id) ? selected.filter((x) => x !== id) : [...selected, id]);
  return (
    <fieldset className="min-w-0 space-y-3" data-testid="district-picker">
      <legend className="text-sm font-semibold">Districts ({selected.length} of at most {SHIPPING_MAX_DISTRICTS})</legend>
      <p className="text-xs text-text-muted">A district can be in one zone only. Districts that belong to another zone are greyed out with that zone&apos;s code.</p>
      <Input label="Search districts" type="search" value={search} onChange={(e) => setSearch(e.target.value)} autoComplete="off" />
      <FieldError text={error} />
      <div className="max-h-80 space-y-3 overflow-y-auto rounded-md border border-surface-border p-3">
        {shown.length === 0 && <p className="text-sm text-text-muted">No district matches &quot;{search.trim()}&quot;.</p>}
        {groupByDivision(shown).map((g) => (
          <div key={g.division}>
            <p className="text-xs font-semibold uppercase tracking-wider text-text-muted">{g.division}</p>
            <ul className="mt-1 grid grid-cols-1 gap-x-4 gap-y-1 sm:grid-cols-2 lg:grid-cols-3">
              {g.districts.map((d) => {
                const owner = otherZoneOwner(d, ownZoneId);
                return (
                  <li key={d.districtId}>
                    <label className={`flex items-center gap-2 text-sm ${owner ? 'cursor-not-allowed text-text-muted' : ''}`}>
                      <input type="checkbox" checked={chosen.has(d.districtId)} disabled={readOnly || !!owner} onChange={() => toggle(d.districtId)} />
                      <span>{d.nameEn}{owner && <span className="ml-1 font-mono text-xs">(in {owner})</span>}</span>
                    </label>
                  </li>
                );
              })}
            </ul>
          </div>
        ))}
      </div>
    </fieldset>
  );
}

function ZoneFormView({ zone: initial, coverage, canManage, onChanged, onReload }: {
  zone: ShippingZoneDetail | null; coverage: CoverageDistrict[]; canManage: boolean; onChanged: () => void; onReload: () => void;
}) {
  const router = useRouter();
  const isNew = initial === null;
  const [zone, setZone] = React.useState<ShippingZoneDetail | null>(initial);
  const [form, setForm] = React.useState<ZoneForm>(initial ? detailToForm(initial) : emptyZoneForm());
  const [errors, setErrors] = React.useState<ZoneErrors>({});
  const [failure, setFailure] = React.useState<{ text: string; stale: boolean } | null>(null);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [confirmDelete, setConfirmDelete] = React.useState(false);
  const readOnly = !canManage;
  const patch = (p: Partial<ZoneForm>) => { setForm((f) => ({ ...f, ...p })); setNotice(null); };
  const setBand = (i: number, p: Partial<ZoneForm['bands'][number]>) => patch({ bands: form.bands.map((b, j) => (j === i ? { ...b, ...p } : b)) });

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    const { body, errors: found } = formToBody(form, isNew);
    setErrors(found);
    setNotice(null);
    if (!body) { setFailure({ text: 'Fix the highlighted fields, then save again.', stale: false }); return; }
    setBusy(true);
    setFailure(null);
    try {
      if (!zone) {
        const created = (await shippingZonesApi.create(body)).data;
        router.push(`/shipping/zones/${created.id}`);
        return;
      }
      const saved = (await shippingZonesApi.replace(zone.id, { ...body, expectedVersion: zone.version })).data;
      setZone(saved);
      setForm(detailToForm(saved));
      setNotice('Saved.');
      onChanged();
    } catch (err) {
      const f = shippingError(err);
      setErrors(f.fields);
      setFailure({ text: f.text, stale: f.code === 'SHIPPING_ZONE_STALE' });
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    if (!zone) return;
    setBusy(true);
    try {
      await shippingZonesApi.delete(zone.id);
      router.push('/shipping/zones');
    } catch (err) {
      setConfirmDelete(false);
      setFailure({ text: shippingError(err).text, stale: false });
      setBusy(false);
    }
  };

  return (
    <div className="space-y-6">
      {readOnly && <p role="status" data-testid="read-only" className="rounded-md border border-surface-border bg-surface p-3 text-sm text-text-muted">You can view this zone but not change it (shipping.manage is needed).</p>}
      <form onSubmit={save} noValidate className="space-y-6 rounded-xl border border-surface-border bg-surface p-4 sm:p-6" aria-label={isNew ? 'New shipping zone' : 'Shipping zone'}>
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
          <Input label="Code" value={form.code} onChange={(e) => patch({ code: e.target.value })} readOnly={!isNew || readOnly} disabled={!isNew} error={errors.code} maxLength={40}
            helperText={isNew ? 'Lowercase letters, digits, - and _. It cannot be changed later.' : 'The code never changes.'} />
          <Input label="Name (English)" value={form.nameEn} onChange={(e) => patch({ nameEn: e.target.value })} readOnly={readOnly} error={errors.nameEn} maxLength={100} />
          <Input label="Name (Bangla)" value={form.nameBn} onChange={(e) => patch({ nameBn: e.target.value })} readOnly={readOnly} error={errors.nameBn} maxLength={100} />
        </div>
        <div>
          <label className="flex items-center gap-2 text-sm font-semibold">
            <input type="checkbox" checked={form.isActive} disabled={readOnly} onChange={(e) => patch({ isActive: e.target.checked })} />
            Active (orders to its districts are priced from this zone)
          </label>
          {!isNew && zone && <p className="mt-1 text-xs text-text-muted">Saved state: <Badge variant={zone.isActive ? 'success' : 'outline'}>{zone.isActive ? 'Active' : 'Inactive'}</Badge></p>}
        </div>

        <DistrictPicker districts={coverage} selected={form.districtIds} ownZoneId={zone?.id ?? null} readOnly={readOnly} error={errors.districts} onChange={(ids) => patch({ districtIds: ids })} />

        <fieldset className="min-w-0 space-y-3" data-testid="band-editor">
          <legend className="text-sm font-semibold">Weight bands (at most {SHIPPING_MAX_BANDS})</legend>
          <p className="text-xs text-text-muted">Each band is the heaviest parcel it covers and its fee. Weights go up, fees never go down. A parcel above the last band becomes quote-only.</p>
          <FieldError text={errors.bands} />
          <div className="overflow-x-auto">
            <table className="w-full min-w-[22rem] text-left text-sm">
              <thead className="text-xs text-text-muted">
                <tr><th scope="col" className="pb-1 pr-2">Up to (kg)</th><th scope="col" className="pb-1 pr-2">Fee (taka)</th><th scope="col" className="pb-1"><span className="sr-only">Remove</span></th></tr>
              </thead>
              <tbody>
                {form.bands.map((b, i) => (
                  <tr key={i} data-testid="band-row" className="align-top">
                    <td className="py-1 pr-2">
                      <input aria-label={`Band ${i + 1} up to (kg)`} inputMode="decimal" className={CELL} value={b.kg} readOnly={readOnly} onChange={(e) => setBand(i, { kg: e.target.value })} aria-invalid={!!errors[`bands.${i}.kg`]} />
                      <FieldError text={errors[`bands.${i}.kg`]} />
                    </td>
                    <td className="py-1 pr-2">
                      <input aria-label={`Band ${i + 1} fee (taka)`} inputMode="decimal" className={CELL} value={b.taka} readOnly={readOnly} onChange={(e) => setBand(i, { taka: e.target.value })} aria-invalid={!!errors[`bands.${i}.fee`]} />
                      <FieldError text={errors[`bands.${i}.fee`]} />
                    </td>
                    <td className="py-1">
                      {!readOnly && <Button type="button" variant="outline" size="sm" disabled={busy} onClick={() => patch({ bands: form.bands.filter((_, j) => j !== i) })} aria-label={`Remove band ${i + 1}`}>Remove</Button>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {!readOnly && <Button type="button" variant="outline" size="sm" disabled={busy || form.bands.length >= SHIPPING_MAX_BANDS} onClick={() => patch({ bands: [...form.bands, { kg: '', taka: '' }] })}>Add band</Button>}
        </fieldset>

        <fieldset className="min-w-0 space-y-3">
          <legend className="text-sm font-semibold">Cap and limits (only the fee cap is required)</legend>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <Input label="Free shipping from subtotal (taka)" inputMode="decimal" value={form.freeThreshold} readOnly={readOnly} onChange={(e) => patch({ freeThreshold: e.target.value })} error={errors.freeThreshold} helperText="Leave empty for no free shipping." />
            <Input label="Fee cap (taka)" inputMode="decimal" value={form.cap} readOnly={readOnly} onChange={(e) => patch({ cap: e.target.value })} error={errors.cap} helperText="Required. The most any parcel costs; no band fee may be above it." />
            <Input label="Heavy-unit limit (kg)" inputMode="decimal" value={form.heavyKg} readOnly={readOnly} onChange={(e) => patch({ heavyKg: e.target.value })} error={errors.heavyKg} helperText="A single unit heavier than this is quote-only." />
            <Input label="Oversize side limit (mm)" inputMode="numeric" value={form.oversizeMm} readOnly={readOnly} onChange={(e) => patch({ oversizeMm: e.target.value })} error={errors.oversizeMm} helperText="Millimetres, not cm: 1 m = 1000 mm. A longer side is quote-only." />
          </div>
        </fieldset>

        {failure && (
          <div role="alert" data-testid="save-failure" className="flex flex-wrap items-center gap-3 rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">
            <span>{failure.text}</span>
            {failure.stale && <Button type="button" variant="outline" size="sm" onClick={onReload}>Reload</Button>}
          </div>
        )}
        {errors.expectedVersion && <FieldError text={errors.expectedVersion} />}
        {notice && <p role="status" data-testid="saved" className="text-sm font-semibold text-success">{notice}</p>}
        <div className="flex flex-wrap gap-3">
          {canManage && <Button type="submit" isLoading={busy} disabled={busy}>{isNew ? 'Create zone' : 'Save changes'}</Button>}
          <Link href="/shipping/zones" className="inline-flex items-center rounded-md border border-surface-border px-4 py-2 text-sm font-semibold">Back to zones</Link>
          {canManage && zone && <Button type="button" variant="destructive" disabled={busy || zone.isActive} onClick={() => setConfirmDelete(true)}>Delete zone</Button>}
        </div>
        {canManage && zone?.isActive && <p className="text-xs text-text-muted">An active zone cannot be deleted. Untick Active, save, then delete.</p>}
      </form>

      {!isNew && <TariffPreview districts={coverage} />}

      <Modal open={confirmDelete} onOpenChange={(next) => { if (!next && !busy) setConfirmDelete(false); }} title={<>Delete zone {zone?.code}?</>} description="The zone, its weight bands and its district assignments are removed for good. Its districts become uncovered.">
          <ModalFooter>
            <Button type="button" variant="outline" onClick={() => setConfirmDelete(false)} disabled={busy}>Cancel</Button>
            <Button type="button" variant="destructive" onClick={remove} isLoading={busy} disabled={busy}>Delete zone</Button>
          </ModalFooter>
      </Modal>
    </div>
  );
}

function Editor({ id, canManage }: { id: string | null; canManage: boolean }) {
  const zone = useResource(() => (id ? shippingZonesApi.get(id) : Promise.resolve(null)), `zone-${id ?? 'new'}`);
  const coverage = useResource(() => shippingZonesApi.coverage(), 'coverage');
  const error = zone.error ?? coverage.error;
  const cov = coverage.data?.data;
  const ready = cov && (!id || zone.data);
  if (error) return <LoadFailure error={error} onRetry={() => { zone.reload(); coverage.reload(); }} />;
  if (!cov || !ready) return <div role="status"><span className="sr-only">Loading</span><Skeleton className="h-64 w-full" /></div>;
  const all = [...cov.covered, ...cov.uncovered];
  return (
    <ZoneFormView
      key={`${id ?? 'new'}-${zone.data?.data.version ?? 0}`}
      zone={zone.data?.data ?? null}
      coverage={all}
      canManage={canManage}
      onChanged={coverage.reload}
      onReload={() => { zone.reload(); coverage.reload(); }}
    />
  );
}

export function ShippingZoneEditorView({ id }: { id?: string }) {
  const { user } = useAuth();
  const held = new Set((user?.permissions ?? []).map((p) => p.toLowerCase()));
  const needed = id ? 'shipping.view' : 'shipping.manage';
  return (
    <ConsoleShell activeItemId="shipping-zones" title={id ? 'Shipping zone' : 'New shipping zone'}>
      {held.has(needed) ? <Editor id={id ?? null} canManage={held.has('shipping.manage')} /> : <NoAccess permission={needed} />}
    </ConsoleShell>
  );
}
