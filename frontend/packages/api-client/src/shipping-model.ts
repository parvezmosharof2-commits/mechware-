/** Shapes and pure helpers for the staff shipping-zones editor (E8.4a API, E8.4b screens). Limits mirror the server. Money never uses floats. */

export const SHIPPING_MAX_BANDS = 20;
export const SHIPPING_MAX_BAND_GRAMS = 1_000_000; // 1,000 kg
export const SHIPPING_MAX_FEE_MINOR = 10_000_000; // 100,000.00 taka, also the cap's limit
export const SHIPPING_MAX_THRESHOLD_MINOR = 10_000_000_000;
export const SHIPPING_MAX_HEAVY_GRAMS = 1_000_000;
export const SHIPPING_MAX_OVERSIZE_MM = 20_000;
export const SHIPPING_MAX_DISTRICTS = 100;
export const SHIPPING_MAX_PREVIEW_LINES = 200;
export const SHIPPING_CODE_PATTERN = /^[a-z0-9][a-z0-9_-]{0,39}$/;

export interface ShippingBand { maxGrams: number; feeMinor: number }
export interface ShippingRates {
  bands: ShippingBand[];
  freeShippingThresholdMinor: number | null;
  heavyUnitGrams: number | null;
  oversizeDimensionMm: number | null;
  maxFeeMinor: number;
}
export interface ShippingZoneSummary {
  id: string; code: string; nameEn: string; nameBn: string; isActive: boolean; districtCount: number; bandCount: number; version: number | string;
}
export interface ShippingZoneDetail {
  id: string; code: string; nameEn: string; nameBn: string; isActive: boolean; version: number | string;
  districts: { id: string; nameEn: string; nameBn: string }[];
  rates: ShippingRates;
  createdAtUtc: string; updatedAtUtc: string;
}
export interface ShippingZoneBody {
  code?: string; nameEn: string; nameBn: string; isActive: boolean; districtIds: string[]; rates: ShippingRates;
}
export type ShippingZoneReplaceBody = ShippingZoneBody & { expectedVersion: number | string };
export interface CoverageDistrict {
  districtId: string; nameEn: string; nameBn: string; divisionNameEn: string; zoneId: string | null; zoneCode: string | null; zoneActive: boolean | null;
}
export interface ShippingCoverage { activeZoneCount: number; covered: CoverageDistrict[]; uncovered: CoverageDistrict[] }

export interface ShippingPreviewLine { sellerId?: string | null; quantity: number; unitWeightGrams: number; lengthMm?: number; widthMm?: number; heightMm?: number }
export interface ShippingPreviewRequest { districtId: string; subtotalMinor: number; lines: ShippingPreviewLine[] }
export interface ShippingPreviewResult {
  outcome: 'Priced' | 'QuoteOnly' | 'NoZone' | string;
  zoneId: string | null; zoneCode: string | null;
  perSeller: { sellerId: string | null; shippingMinor: number; chargeableGrams: number }[];
  totalMinor: number; freeShippingApplied: boolean;
  reasons: { code: string; lineIndex?: number | null; sellerId?: string | null }[];
}

// ---------- unit conversion (strings only) ----------

/** Same rule as price-lists-model `takaToMinor` (a sibling value import does not load under `node --test`): "12.5" taka to 1250 poisha, no floats. */
export function shipTakaToMinor(text: string): number | null {
  const m = /^(\d{1,12})(?:\.(\d{1,2}))?$/.exec(text.trim().replace(/,/g, '').replace(/^৳\s*/, ''));
  return m ? Number(m[1]) * 100 + Number((m[2] ?? '').padEnd(2, '0') || '0') : null;
}

/** "1.5" kg to 1500 g; at most 3 decimals, no sign or exponent. Null when the text is not a plain decimal. */
export function kgToGrams(text: string): number | null {
  const m = /^(\d{1,7})(?:\.(\d{1,3}))?$/.exec(text.trim());
  return m ? Number(m[1]) * 1000 + Number((m[2] ?? '').padEnd(3, '0') || '0') : null;
}

/** 1500 g to "1.5"; whole kg without decimals. */
export function gramsToKg(grams: number): string {
  const whole = Math.floor(grams / 1000);
  const frac = String(grams % 1000).padStart(3, '0').replace(/0+$/, '');
  return frac ? `${whole}.${frac}` : String(whole);
}

/** Poisha to an editable taka text without thousands separators: 5000 -> "50", 5050 -> "50.50". */
export function minorToTakaInput(minor: number): string {
  const frac = String(minor % 100).padStart(2, '0');
  return frac === '00' ? String(Math.floor(minor / 100)) : `${Math.floor(minor / 100)}.${frac}`;
}

// ---------- form model and validation ----------

export interface BandInput { kg: string; taka: string }
export interface ZoneForm {
  code: string; nameEn: string; nameBn: string; isActive: boolean; districtIds: string[];
  bands: BandInput[]; freeThreshold: string; heavyKg: string; oversizeMm: string; cap: string;
}
/** Keys: code, nameEn, nameBn, districts, bands, bands.<i>.kg, bands.<i>.fee, freeThreshold, heavyKg, oversizeMm, cap. */
export type ZoneErrors = Record<string, string>;

export const emptyZoneForm = (): ZoneForm => ({
  code: '', nameEn: '', nameBn: '', isActive: false, districtIds: [], bands: [{ kg: '', taka: '' }], freeThreshold: '', heavyKg: '', oversizeMm: '', cap: ''
});

export function detailToForm(d: ShippingZoneDetail): ZoneForm {
  const r = d.rates;
  return {
    code: d.code, nameEn: d.nameEn, nameBn: d.nameBn, isActive: d.isActive, districtIds: d.districts.map((x) => x.id),
    bands: r.bands.map((b) => ({ kg: gramsToKg(b.maxGrams), taka: minorToTakaInput(b.feeMinor) })),
    freeThreshold: r.freeShippingThresholdMinor === null ? '' : minorToTakaInput(r.freeShippingThresholdMinor),
    heavyKg: r.heavyUnitGrams === null ? '' : gramsToKg(r.heavyUnitGrams),
    oversizeMm: r.oversizeDimensionMm === null ? '' : String(r.oversizeDimensionMm),
    cap: minorToTakaInput(r.maxFeeMinor)
  };
}

const MONEY_HINT = 'Enter taka with at most two decimals (for example 120 or 120.50).';

/** Validates the whole form and builds the request body. `body` is null while any error remains. */
export function formToBody(f: ZoneForm, isNew: boolean): { body: ShippingZoneBody | null; errors: ZoneErrors } {
  const errors: ZoneErrors = {};
  const code = f.code.trim().toLowerCase();
  if (isNew && !SHIPPING_CODE_PATTERN.test(code)) errors.code = 'Use 1 to 40 lowercase letters, digits, "-" or "_", starting with a letter or digit.';
  for (const k of ['nameEn', 'nameBn'] as const) {
    const n = f[k].trim();
    if (n.length < 1 || n.length > 100) errors[k] = 'Enter a name of 1 to 100 characters.';
  }
  if (f.districtIds.length > SHIPPING_MAX_DISTRICTS) errors.districts = `A zone can hold at most ${SHIPPING_MAX_DISTRICTS} districts.`;
  if (f.isActive && f.districtIds.length === 0) errors.districts = 'An active zone needs at least one district.';

  const money = (text: string, key: string, max: number, what: string, min = 1): number | null => {
    const v = shipTakaToMinor(text);
    if (v === null) { errors[key] = MONEY_HINT; return null; }
    if (v < min) { errors[key] = `${what} must be more than zero.`; return null; }
    if (v > max) { errors[key] = `${what} cannot be more than ${(max / 100).toLocaleString('en-US')} taka.`; return null; }
    return v;
  };

  // The server requires the cap (1 poisha up): it is the ceiling no band fee may pass.
  const cap = money(f.cap, 'cap', SHIPPING_MAX_FEE_MINOR, 'The fee cap');
  const bands: ShippingBand[] = [];
  if (f.bands.length > SHIPPING_MAX_BANDS) errors.bands = `A rate card can have at most ${SHIPPING_MAX_BANDS} weight bands.`;
  else if (f.bands.length === 0 && f.isActive) errors.bands = 'An active zone needs at least one weight band.';
  f.bands.forEach((b, i) => {
    const g = kgToGrams(b.kg);
    let grams: number | null = null;
    if (g === null) errors[`bands.${i}.kg`] = 'Enter the weight in kg with at most 3 decimals (for example 2.5).';
    else if (g < 1 || g > SHIPPING_MAX_BAND_GRAMS) errors[`bands.${i}.kg`] = 'A band weight is between 0.001 and 1,000 kg.';
    else grams = g;
    const fee = money(b.taka, `bands.${i}.fee`, SHIPPING_MAX_FEE_MINOR, 'The fee', 0);
    if (grams !== null && fee !== null) bands.push({ maxGrams: grams, feeMinor: fee });
  });
  if (!errors.bands && bands.length === f.bands.length) {
    for (let i = 1; i < bands.length; i++) {
      if (bands[i].maxGrams === bands[i - 1].maxGrams) errors[`bands.${i}.kg`] = 'Two bands cannot have the same weight.';
      else if (bands[i].maxGrams < bands[i - 1].maxGrams) errors[`bands.${i}.kg`] = 'Weights must go up from one band to the next.';
      else if (bands[i].feeMinor < bands[i - 1].feeMinor) errors[`bands.${i}.fee`] = 'A heavier band cannot cost less than the lighter one.';
    }
    if (cap !== null) bands.forEach((b, i) => { if (b.feeMinor > cap && !errors[`bands.${i}.fee`]) errors[`bands.${i}.fee`] = 'This fee is above the fee cap.'; });
  }

  const threshold = f.freeThreshold.trim() === '' ? null : money(f.freeThreshold, 'freeThreshold', SHIPPING_MAX_THRESHOLD_MINOR, 'The free-shipping threshold');
  let heavy: number | null = null;
  if (f.heavyKg.trim() !== '') {
    const g = kgToGrams(f.heavyKg);
    if (g === null || g < 1 || g > SHIPPING_MAX_HEAVY_GRAMS) errors.heavyKg = 'Enter the heavy-unit limit in kg (0.001 to 1,000, at most 3 decimals).';
    else heavy = g;
  }
  let oversize: number | null = null;
  if (f.oversizeMm.trim() !== '') {
    const mm = /^\d{1,6}$/.test(f.oversizeMm.trim()) ? Number(f.oversizeMm.trim()) : NaN;
    if (!(mm >= 1 && mm <= SHIPPING_MAX_OVERSIZE_MM)) errors.oversizeMm = `Enter whole millimetres, 1 to ${SHIPPING_MAX_OVERSIZE_MM.toLocaleString('en-US')}.`;
    else oversize = mm;
  }
  if (Object.keys(errors).length > 0) return { body: null, errors };
  return {
    errors,
    body: {
      ...(isNew ? { code } : {}), nameEn: f.nameEn.trim(), nameBn: f.nameBn.trim(), isActive: f.isActive, districtIds: f.districtIds,
      rates: { bands, freeShippingThresholdMinor: threshold, heavyUnitGrams: heavy, oversizeDimensionMm: oversize, maxFeeMinor: cap as number }
    }
  };
}

/** Server field key (`rates.bands[2].maxGrams`, `districtIds`, ...) to the form key used by ZoneErrors. */
export function serverFieldToForm(key: string): string {
  const k = key.toLowerCase();
  const band = /bands\[(\d+)\]\.(maxgrams|feeminor)/.exec(k);
  if (band) return `bands.${band[1]}.${band[2] === 'maxgrams' ? 'kg' : 'fee'}`;
  if (k.endsWith('bands')) return 'bands';
  if (k.includes('districtids')) return 'districts';
  if (k.includes('maxfeeminor')) return 'cap';
  if (k.includes('freeshippingthreshold')) return 'freeThreshold';
  if (k.includes('heavyunitgrams')) return 'heavyKg';
  if (k.includes('oversizedimensionmm')) return 'oversizeMm';
  if (k === 'code' || k === 'nameen' || k === 'namebn') return k === 'code' ? 'code' : k === 'nameen' ? 'nameEn' : 'nameBn';
  return key;
}

export function mapServerFields(fields: Record<string, string>): ZoneErrors {
  const out: ZoneErrors = {};
  for (const [k, v] of Object.entries(fields)) { const key = serverFieldToForm(k); if (!out[key]) out[key] = v; }
  return out;
}

// ---------- errors ----------

export const SHIPPING_CONFLICT_TEXT: Record<string, string> = {
  SHIPPING_ZONE_STALE: 'Someone else changed this zone. Reload to see their version; your edits on screen are not saved.',
  SHIPPING_ZONE_ACTIVE: 'An active zone cannot be deleted. Switch it to inactive, save, then delete it.',
  SHIPPING_DISTRICT_ALREADY_IN_ZONE: 'A district you picked already belongs to another zone. Remove it from the other zone first.',
  SHIPPING_ZONE_CODE_TAKEN: 'That zone code is already used. Choose another code.',
  SHIPPING_ZONE_CONFLICT: 'Another change happened at the same moment. Reload and try again.'
};

export function shippingErrorText(e: { status: number | null; code: string | null; message: string; fields: Record<string, string> }): string {
  if (e.status === 401) return 'Your session has ended. Sign in again.';
  if (e.status === 403) return 'You do not have permission to do this.';
  if (e.status === 404) return 'This shipping zone no longer exists.';
  if (e.status === 409) {
    const base = (e.code && SHIPPING_CONFLICT_TEXT[e.code]) || 'Another change got in the way. Reload and try again.';
    return e.code === 'SHIPPING_DISTRICT_ALREADY_IN_ZONE' && e.message ? `${e.message} ${base}` : base;
  }
  if (e.status === 400) {
    const first = Object.values(e.fields)[0];
    return first ? `${e.message || 'The request was rejected.'} ${first}`.trim() : e.message || 'The request was rejected.';
  }
  if (e.status !== null && e.status >= 500) return 'The server had a problem. Nothing was changed; try again in a moment.';
  return 'Could not reach the server. Check your connection and try again.';
}

// ---------- coverage ----------

export function groupByDivision(districts: readonly CoverageDistrict[]): { division: string; districts: CoverageDistrict[] }[] {
  const map = new Map<string, CoverageDistrict[]>();
  for (const d of districts) map.set(d.divisionNameEn, [...(map.get(d.divisionNameEn) ?? []), d]);
  return [...map.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([division, list]) => ({ division, districts: [...list].sort((x, y) => x.nameEn.localeCompare(y.nameEn)) }));
}

/** Code of the OTHER zone that holds this district (it can be in one zone only), or null when it is free or ours. */
export function otherZoneOwner(d: CoverageDistrict, ownZoneId: string | null): string | null {
  return d.zoneId && d.zoneId !== ownZoneId ? d.zoneCode ?? 'another zone' : null;
}

// ---------- tariff preview ----------

export interface PreviewLineInput { quantity: string; kg: string; lengthMm: string; widthMm: string; heightMm: string }
export interface PreviewForm { districtId: string; subtotal: string; lines: PreviewLineInput[] }
export const emptyPreviewLine = (): PreviewLineInput => ({ quantity: '1', kg: '', lengthMm: '', widthMm: '', heightMm: '' });

export function previewToRequest(f: PreviewForm): { request: ShippingPreviewRequest | null; error: string | null } {
  if (!f.districtId) return { request: null, error: 'Choose the destination district.' };
  const subtotal = f.subtotal.trim() === '' ? 0 : shipTakaToMinor(f.subtotal);
  if (subtotal === null) return { request: null, error: `Subtotal: ${MONEY_HINT}` };
  if (f.lines.length < 1 || f.lines.length > SHIPPING_MAX_PREVIEW_LINES) return { request: null, error: 'Add between 1 and 200 lines.' };
  const lines: ShippingPreviewLine[] = [];
  for (const [i, l] of f.lines.entries()) {
    const n = i + 1;
    const qty = /^\d{1,6}$/.test(l.quantity.trim()) ? Number(l.quantity.trim()) : NaN;
    if (!(qty >= 1 && qty <= 100_000)) return { request: null, error: `Line ${n}: quantity is a whole number from 1 to 100,000.` };
    const g = kgToGrams(l.kg);
    if (g === null || g < 1 || g > 1_000_000) return { request: null, error: `Line ${n}: unit weight is 0.001 to 1,000 kg (at most 3 decimals).` };
    const line: ShippingPreviewLine = { quantity: qty, unitWeightGrams: g };
    for (const [key, text] of [['lengthMm', l.lengthMm], ['widthMm', l.widthMm], ['heightMm', l.heightMm]] as const) {
      if (text.trim() === '') continue;
      const v = /^\d{1,7}$/.test(text.trim()) ? Number(text.trim()) : NaN;
      if (!(v >= 1)) return { request: null, error: `Line ${n}: dimensions are whole millimetres.` };
      line[key] = v;
    }
    lines.push(line);
  }
  return { request: { districtId: f.districtId, subtotalMinor: subtotal, lines }, error: null };
}

export const PREVIEW_REASON_TEXT: Record<string, string> = {
  HEAVY_ITEM: 'A unit is heavier than the heavy-unit limit.',
  OVERSIZE_ITEM: 'A side is longer than the oversize limit.',
  ABOVE_LAST_BAND: 'The weight is above the last weight band.'
};

export function previewReasonText(r: { code: string; lineIndex?: number | null }): string {
  const base = PREVIEW_REASON_TEXT[r.code] ?? r.code;
  return r.lineIndex === null || r.lineIndex === undefined ? base : `Line ${r.lineIndex + 1}: ${base}`;
}
