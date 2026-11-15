'use client';

import * as React from 'react';
import { Button, Input } from '@mechware/ui';
import {
  emptyPreviewLine, formatTaka, groupByDivision, previewReasonText, previewToRequest, SHIPPING_MAX_PREVIEW_LINES,
  type CoverageDistrict, type PreviewForm, type ShippingPreviewResult
} from '@mechware/api-client';
import { shippingError, shippingZonesApi } from '../lib/api';

const SELECT = 'block w-full rounded-md border border-surface-border bg-surface p-2 text-sm';

/** "Try a tariff": the server prices the sample basket on the ACTIVE zones, exactly as an order would be. Nothing is computed here. */
export function TariffPreview({ districts, savedNote }: { districts: CoverageDistrict[]; savedNote?: string }) {
  const [form, setForm] = React.useState<PreviewForm>({ districtId: '', subtotal: '', lines: [emptyPreviewLine()] });
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [result, setResult] = React.useState<ShippingPreviewResult | null>(null);

  const setLine = (i: number, patch: Partial<PreviewForm['lines'][number]>) =>
    setForm((f) => ({ ...f, lines: f.lines.map((l, j) => (j === i ? { ...l, ...patch } : l)) }));

  const run = async (e: React.FormEvent) => {
    e.preventDefault();
    const { request, error: problem } = previewToRequest(form);
    setResult(null);
    if (!request) { setError(problem); return; }
    setBusy(true);
    setError(null);
    try {
      setResult((await shippingZonesApi.preview(request)).data);
    } catch (err) {
      setError(shippingError(err).text);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section aria-label="Try a tariff" className="space-y-4 rounded-xl border border-surface-border bg-surface p-4" data-testid="tariff-preview">
      <div>
        <h2 className="font-display text-lg font-bold">Try a tariff</h2>
        <p className="text-sm text-text-muted">The server prices a sample basket on the active zones exactly as it would price an order. {savedNote ?? 'It uses saved zones only: save your edits first.'}</p>
      </div>
      <form onSubmit={run} noValidate className="space-y-3">
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <div>
            <label htmlFor="tp-district" className="mb-1 block text-xs font-semibold text-text-muted">Destination district</label>
            <select id="tp-district" className={SELECT} value={form.districtId} onChange={(e) => setForm({ ...form, districtId: e.target.value })}>
              <option value="">Choose a district</option>
              {groupByDivision(districts).map((g) => (
                <optgroup key={g.division} label={g.division}>
                  {g.districts.map((d) => <option key={d.districtId} value={d.districtId}>{d.nameEn}</option>)}
                </optgroup>
              ))}
            </select>
          </div>
          <Input label="Order subtotal (taka)" inputMode="decimal" value={form.subtotal} onChange={(e) => setForm({ ...form, subtotal: e.target.value })} helperText="Used for the free-shipping threshold." />
        </div>
        {form.lines.map((l, i) => (
          <fieldset key={i} className="grid min-w-0 grid-cols-2 gap-2 rounded-md border border-surface-border p-3 sm:grid-cols-6" data-testid="preview-line">
            <legend className="px-1 text-xs font-semibold text-text-muted">Line {i + 1}</legend>
            <Input label="Quantity" inputMode="numeric" value={l.quantity} onChange={(e) => setLine(i, { quantity: e.target.value })} />
            <Input label="Unit weight (kg)" inputMode="decimal" value={l.kg} onChange={(e) => setLine(i, { kg: e.target.value })} />
            <Input label="Length (mm)" inputMode="numeric" value={l.lengthMm} onChange={(e) => setLine(i, { lengthMm: e.target.value })} />
            <Input label="Width (mm)" inputMode="numeric" value={l.widthMm} onChange={(e) => setLine(i, { widthMm: e.target.value })} />
            <Input label="Height (mm)" inputMode="numeric" value={l.heightMm} onChange={(e) => setLine(i, { heightMm: e.target.value })} />
            <div className="flex items-end">
              <Button type="button" variant="outline" size="sm" disabled={form.lines.length === 1 || busy} onClick={() => setForm({ ...form, lines: form.lines.filter((_, j) => j !== i) })} aria-label={`Remove line ${i + 1}`}>Remove</Button>
            </div>
          </fieldset>
        ))}
        <div className="flex flex-wrap gap-2">
          <Button type="button" variant="outline" size="sm" disabled={busy || form.lines.length >= SHIPPING_MAX_PREVIEW_LINES} onClick={() => setForm({ ...form, lines: [...form.lines, emptyPreviewLine()] })}>Add line</Button>
          <Button type="submit" size="sm" isLoading={busy} disabled={busy}>Price this basket</Button>
        </div>
      </form>

      {error && <div role="alert" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault" data-testid="preview-error">{error}</div>}
      {result && (
        <div role="status" className="space-y-2 rounded-md border border-surface-border bg-bg-subtle p-3 text-sm" data-testid="preview-result">
          {result.outcome === 'Priced' && (
            <>
              <p className="font-semibold" data-testid="preview-outcome">Priced{result.zoneCode ? ` by zone ${result.zoneCode}` : ''}</p>
              <p>Shipping total: <span className="font-mono font-bold" data-testid="preview-total">{formatTaka(result.totalMinor)}</span> BDT{result.freeShippingApplied ? ' (free shipping applied)' : ''}</p>
              <ul className="list-disc pl-5">
                {result.perSeller.map((s, i) => <li key={s.sellerId ?? i}>Seller {i + 1}: <span className="font-mono">{formatTaka(s.shippingMinor)}</span> BDT for {(s.chargeableGrams / 1000).toLocaleString('en-US', { maximumFractionDigits: 3 })} kg</li>)}
              </ul>
            </>
          )}
          {result.outcome === 'QuoteOnly' && (
            <>
              <p className="font-semibold" data-testid="preview-outcome">Quote only{result.zoneCode ? ` (zone ${result.zoneCode})` : ''}: no automatic price, staff must quote this order.</p>
              <ul className="list-disc pl-5">{result.reasons.map((r, i) => <li key={i}>{previewReasonText(r)}</li>)}</ul>
            </>
          )}
          {result.outcome === 'NoZone' && <p className="font-semibold" data-testid="preview-outcome">No zone: no active zone covers this district. Orders there would be refused in production.</p>}
          {!['Priced', 'QuoteOnly', 'NoZone'].includes(result.outcome) && <p data-testid="preview-outcome">Result: {result.outcome}</p>}
        </div>
      )}
    </section>
  );
}
