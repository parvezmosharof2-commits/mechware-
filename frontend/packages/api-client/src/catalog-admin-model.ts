/**
 * Staff product information (PIM) screens, C4.1/C4.2. Mirrors `CatalogAdminContracts.cs`. The admin API has no product list,
 * no title/description edit, no lifecycle verbs and no weight setter yet: the list is built from the variant lookup
 * (`GET /admin/variants`) and the edit page covers what `/admin/products/{id}/...` offers (supply, alternatives, order units,
 * part number). The server stays the judge; the checks here only save a round trip.
 */
import type { AdminVariant } from './lookups-model';

export interface AdminSuccessor { productId: string; slug: string; titleEn: string; status: string; isDropIn: boolean; note: string | null }
export interface AdminAlternative { productId: string; slug: string; titleEn: string; status: string; supplyStatus: string; listed: boolean }
export interface AdminVariantUnits { variantId: string; sku: string; minOrderQty: number; packSize: number; manufacturerPartNumber: string | null }
export interface ProductPim {
  id: string; slug: string; titleEn: string; status: string; supplyStatus: string;
  successor: AdminSuccessor | null; alternatives: AdminAlternative[]; variants: AdminVariantUnits[];
}

export type SupplyStatus = 'active' | 'nrnd' | 'obsolete';
export const SUPPLY_STATUSES: readonly { value: SupplyStatus; label: string }[] = [
  { value: 'active', label: 'Active (in supply)' },
  { value: 'nrnd', label: 'NRND (not recommended for new designs)' },
  { value: 'obsolete', label: 'Obsolete (end of life)' }
];
export const MAX_ALTERNATIVES = 10;
export const MAX_SUCCESSOR_NOTE = 500;
export const MAX_PART_NUMBER = 64;
export const MAX_QTY_PER_LINE = 10_000;
export const PRODUCT_LIST_PAGE_SIZE = 50;

/** Request body of `PUT /admin/products/{id}/supply`. */
export interface SetSupplyBody { supplyStatus: SupplyStatus; successorProductId?: string; successorIsDropIn?: boolean; successorNote?: string; clearSuccessor?: boolean }

const STATUS_LABELS: Record<string, string> = {
  draft: 'Draft', pending_review: 'Pending review', active: 'Published', rejected: 'Rejected', archived: 'Archived'
};
/** `PendingReview`, `pending_review` and `Pending review` all map to the same key. */
export function normaliseStatus(s: string): string {
  return s.trim().replace(/([a-z])([A-Z])/g, '$1_$2').replace(/[\s-]+/g, '_').toLowerCase();
}
export function productStatusLabel(s: string): string { return STATUS_LABELS[normaliseStatus(s)] ?? s; }
export function supplyStatusLabel(s: string): string { return SUPPLY_STATUSES.find((x) => x.value === s)?.label.split(' (')[0] ?? s; }
export const PRODUCT_STATUS_FILTERS: readonly { value: string; label: string }[] = [
  { value: '', label: 'Any status' }, ...Object.entries(STATUS_LABELS).map(([value, label]) => ({ value, label }))
];

// ---------- list (C4.1) ----------

export interface ProductRow {
  productId: string; title: string; brand: string | null; status: string; skus: string[]; partNumbers: string[];
  minPriceMinor: number; maxPriceMinor: number; inactiveSkus: number;
}

/** One row per product from a page of variants (the lookup returns SKUs; a product's other SKUs may be on another page). */
export function groupVariantsByProduct(items: readonly AdminVariant[]): ProductRow[] {
  const rows = new Map<string, ProductRow>();
  for (const v of items) {
    const row = rows.get(v.productId);
    if (!row) {
      rows.set(v.productId, {
        productId: v.productId, title: v.title, brand: v.brand, status: normaliseStatus(v.productStatus), skus: [v.sku],
        partNumbers: v.partNumber ? [v.partNumber] : [], minPriceMinor: v.priceMinor, maxPriceMinor: v.priceMinor, inactiveSkus: v.active ? 0 : 1
      });
    } else {
      row.skus.push(v.sku);
      if (v.partNumber) row.partNumbers.push(v.partNumber);
      row.minPriceMinor = Math.min(row.minPriceMinor, v.priceMinor);
      row.maxPriceMinor = Math.max(row.maxPriceMinor, v.priceMinor);
      if (!v.active) row.inactiveSkus += 1;
    }
  }
  return [...rows.values()];
}

/** Client-side filters over the loaded page only (the API cannot filter by status or brand yet). */
export function filterRows(rows: readonly ProductRow[], f: { status?: string; brand?: string }): ProductRow[] {
  return rows.filter((r) => (!f.status || r.status === f.status) && (!f.brand || r.brand === f.brand));
}
export function brandsOf(rows: readonly ProductRow[]): string[] {
  return [...new Set(rows.map((r) => r.brand).filter((b): b is string => !!b))].sort((a, b) => a.localeCompare(b));
}

/** Poisha to "1,234.50" without floats (model files cannot import each other at runtime under `node --test`). */
function taka(minor: number): string {
  const abs = Math.abs(minor);
  return `${minor < 0 ? '-' : ''}${Math.floor(abs / 100).toLocaleString('en-US')}.${String(abs % 100).padStart(2, '0')}`;
}
export function priceRange(r: Pick<ProductRow, 'minPriceMinor' | 'maxPriceMinor'>): string {
  return r.minPriceMinor === r.maxPriceMinor ? taka(r.minPriceMinor) : `${taka(r.minPriceMinor)} to ${taka(r.maxPriceMinor)}`;
}

// ---------- edit (C4.2) ----------

export type FieldErrors = Record<string, string>;

/** Order units, mirrors `ProductVariant.SetOrderUnits`: whole numbers, pack at least 1, minimum 1..10,000 and a whole number of packs. */
export function validateOrderUnits(minText: string, packText: string): { value: { minOrderQty: number; packSize: number } | null; errors: FieldErrors } {
  const errors: FieldErrors = {};
  const whole = (t: string) => (/^\d{1,9}$/.test(t.trim()) ? Number(t.trim()) : null);
  const min = whole(minText);
  const pack = whole(packText);
  if (pack === null || pack < 1) errors.packSize = 'Pack size is a whole number, at least 1.';
  if (min === null || min < 1 || min > MAX_QTY_PER_LINE) errors.minOrderQty = `Minimum order quantity is a whole number from 1 to ${MAX_QTY_PER_LINE.toLocaleString('en-US')}.`;
  else if (pack !== null && pack >= 1 && min % pack !== 0) errors.minOrderQty = 'The minimum order quantity must be a whole number of packs.';
  return Object.keys(errors).length ? { value: null, errors } : { value: { minOrderQty: min as number, packSize: pack as number }, errors };
}

/** Part number, mirrors `ProductVariant.SetManufacturerPartNumber`: blank clears it; at most 64 characters, two letters or digits. */
export function validatePartNumber(text: string): { value: string | null; error?: string } {
  const t = text.trim();
  if (!t) return { value: null };
  if (t.length > MAX_PART_NUMBER) return { value: null, error: `A part number can be at most ${MAX_PART_NUMBER} characters.` };
  if (/[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/u.test(t)) return { value: null, error: 'A part number cannot contain control or invisible characters.' };
  if (t.replace(/[^0-9A-Za-z০-৯]/g, '').length < 2) return { value: null, error: 'A part number needs at least two letters or digits.' };
  return { value: t };
}

export interface SupplyForm { supplyStatus: SupplyStatus; successorId: string | null; isDropIn: boolean; note: string }

export function supplyToForm(p: ProductPim): SupplyForm {
  return { supplyStatus: (p.supplyStatus as SupplyStatus) ?? 'active', successorId: p.successor?.productId ?? null, isDropIn: p.successor?.isDropIn ?? false, note: p.successor?.note ?? '' };
}

/** Builds the PUT body and mirrors the server's supply rules; `loaded` is the product as last read (to know whether it has a successor). */
export function supplyBody(form: SupplyForm, loaded: ProductPim): { body: SetSupplyBody | null; errors: FieldErrors } {
  const errors: FieldErrors = {};
  if (form.note.trim().length > MAX_SUCCESSOR_NOTE) errors.successorNote = `The note can be at most ${MAX_SUCCESSOR_NOTE} characters.`;
  if (form.supplyStatus === 'active' && form.successorId) errors.successorProductId = 'Only a product that is being phased out can name a successor.';
  if (form.successorId === loaded.id) errors.successorProductId = 'A product cannot be its own successor.';
  if (!form.successorId && (form.isDropIn || form.note.trim())) errors.successorProductId = 'Compatibility notes need a successor.';
  if (Object.keys(errors).length) return { body: null, errors };
  const body: SetSupplyBody = { supplyStatus: form.supplyStatus };
  if (form.successorId) {
    body.successorProductId = form.successorId;
    body.successorIsDropIn = form.isDropIn;
    body.successorNote = form.note.trim();
  } else if (loaded.successor) {
    body.clearSuccessor = true;
  }
  return { body, errors };
}

/** Adds a product id to the alternatives list: no duplicates, not itself, at most 10. */
export function addAlternative(ids: readonly string[], add: string, selfId: string): { ids: string[]; error?: string } {
  if (add === selfId) return { ids: [...ids], error: 'A product cannot be its own alternative.' };
  if (ids.includes(add)) return { ids: [...ids], error: 'That product is already listed.' };
  if (ids.length >= MAX_ALTERNATIVES) return { ids: [...ids], error: `A product can list at most ${MAX_ALTERNATIVES} alternatives.` };
  return { ids: [...ids, add] };
}
export function moveItem<T>(list: readonly T[], from: number, to: number): T[] {
  if (to < 0 || to >= list.length || from === to) return [...list];
  const out = [...list];
  const [x] = out.splice(from, 1);
  out.splice(to, 0, x);
  return out;
}

export function catalogAdminErrorText(e: { status: number | null; code: string | null; message: string; fields: FieldErrors }): string {
  if (e.status === 401) return 'Your session has ended. Sign in again.';
  if (e.status === 403) return 'Your role cannot edit products (catalog.manage is needed).';
  if (e.status === 404) return 'This product no longer exists.';
  if (e.status === 409) return e.code === 'CONCURRENT_MODIFICATION' ? 'Someone else changed this product. Reload to see their version; your edits stay on screen until you do.' : e.message || 'Another change got in the way. Reload and try again.';
  if (e.status === 400) return e.message || 'The server did not accept these values.';
  if (e.status !== null && e.status >= 500) return 'The server had a problem. Nothing was changed; try again in a moment.';
  return 'Could not reach the server. Check your connection and try again.';
}
