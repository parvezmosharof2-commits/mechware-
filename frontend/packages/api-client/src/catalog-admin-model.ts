/**
 * Staff product screens, C4.1/C4.2/C4.2b. Mirrors `CatalogAdminContracts.cs` (PIM: supply, alternatives, order units, part number)
 * and `ProductAdminContracts.cs` (list, detail, basics, variant weight/price/status, publish/archive/restore). The server stays
 * the judge; the checks here only save a round trip. Weight and price are checked as TEXT (no float arithmetic).
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

// ---------- admin product API (C4.2b) ----------

export interface ProductListItem {
  id: string; slug: string; titleEn: string; titleBn: string; brandId: string | null; brandName: string | null;
  categoryId: string; categoryName: string; categoryPath: string; sellerId: string; sellerStoreName: string;
  status: string; supplyStatus: string; variantCount: number; minPriceMinor: number | null; maxPriceMinor: number | null;
  updatedAtUtc: string | null; rowVersion: number;
}
export interface AdminProductDetailVariant {
  id: string; sku: string; isDefault: boolean; status: string; weightKg: number; priceMinor: number; compareAtPriceMinor: number | null;
  salePriceMinor: number | null; minOrderQty: number; packSize: number; manufacturerPartNumber: string | null;
}
export interface AdminProductDetail {
  id: string; slug: string; titleEn: string; titleBn: string; shortDescriptionEn: string; shortDescriptionBn: string; descriptionHtml: string;
  brandId: string | null; brandName: string | null; categoryId: string; categoryName: string; categoryPath: string;
  sellerId: string; sellerStoreName: string; status: string; allowedTransitions: string[]; rejectionReason: string | null; supplyStatus: string;
  lengthCm: number; widthCm: number; heightCm: number; updatedAtUtc: string | null; rowVersion: number; variants: AdminProductDetailVariant[];
}
export interface BrandOption { id: string; nameEn: string; nameBn: string | null; slug: string; isActive: boolean }
export interface CategoryOption { id: string; nameEn: string; nameBn: string | null; slug: string; parentId: string | null; path: string[]; isActive: boolean }
export interface ProductListFilters { q?: string; status?: string; brandId?: string; categoryId?: string; supplyStatus?: string; page?: number }
export interface ProductBasicsBody {
  titleEn: string; titleBn: string; shortDescriptionEn: string; shortDescriptionBn: string; descriptionHtml: string;
  brandId: string | null; categoryId: string; lengthCm?: number; widthCm?: number; heightCm?: number; expectedVersion: number;
}
export interface VariantEditBody { weightKg?: number; priceMinor?: number; status?: 'active' | 'inactive'; expectedVersion: number }

export const MAX_TITLE = 255;
export const MAX_SHORT_DESCRIPTION = 1000;
export const MAX_DESCRIPTION_HTML = 20_000;
export const MAX_WEIGHT_KG = 1000;
export const MAX_DIMENSION_CM = 1000;
export const MAX_UNIT_PRICE_MINOR = 100_000_000_000; // PriceListLimits.MaxUnitPriceMinor
export const ALLOWED_MARKUP_HINT = 'Plain text or simple HTML: paragraphs (p, br), strong, em, lists (ul, ol, li), headings (h2 to h4), links (a, http/https only) and tables. Scripts, styles, event handlers and other tags are refused.';

/** Query string of `GET /admin/products` (empties left out; 50 per page). */
export function productListQuery(f: ProductListFilters = {}): string {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries({ q: f.q?.trim(), status: f.status, brandId: f.brandId, categoryId: f.categoryId, supplyStatus: f.supplyStatus })) if (v) p.set(k, v);
  p.set('page', String(Math.max(1, Math.floor(f.page ?? 1))));
  p.set('pageSize', String(PRODUCT_LIST_PAGE_SIZE));
  return p.toString();
}
export function lookupQuery(q: string, pageSize = 20): string {
  const p = new URLSearchParams();
  if (q.trim()) p.set('q', q.trim());
  p.set('pageSize', String(pageSize));
  return p.toString();
}
export function categoryLabel(c: Pick<CategoryOption, 'nameEn' | 'path'>): string { return c.path.length ? c.path.join(' / ') : c.nameEn; }

export const SUPPLY_FILTERS: readonly { value: string; label: string }[] = [{ value: '', label: 'Any supply status' }, ...SUPPLY_STATUSES.map((s) => ({ value: s.value, label: s.label.split(' (')[0] }))];
export function statusBadgeVariant(s: string): 'success' | 'outline' | 'low-stock' {
  const n = normaliseStatus(s);
  return n === 'active' ? 'success' : n === 'pending_review' ? 'low-stock' : 'outline';
}

/** Lifecycle buttons from the server's `allowedTransitions` (unknown verbs are not offered). */
export const LIFECYCLE_ACTIONS: Record<string, { label: string; confirm: string }> = {
  publish: { label: 'Publish', confirm: 'Publishing makes this product visible to buyers in the storefront and search.' },
  archive: { label: 'Archive', confirm: 'Archiving removes this product from the storefront and search. You can restore it to draft later.' },
  restore: { label: 'Restore', confirm: 'Restoring puts this product back to draft. It stays hidden from buyers until you publish it.' }
};
export function lifecycleButtons(allowed: readonly string[]): { action: string; label: string; confirm: string }[] {
  return allowed.filter((a) => a in LIFECYCLE_ACTIONS).map((a) => ({ action: a, label: LIFECYCLE_ACTIONS[a].label, confirm: LIFECYCLE_ACTIONS[a].confirm }));
}

export interface BasicsForm {
  titleEn: string; titleBn: string; shortDescriptionEn: string; shortDescriptionBn: string; descriptionHtml: string;
  brand: { id: string; label: string } | null; category: { id: string; label: string } | null; lengthCm: string; widthCm: string; heightCm: string;
}
export function basicsToForm(d: AdminProductDetail): BasicsForm {
  return {
    titleEn: d.titleEn, titleBn: d.titleBn, shortDescriptionEn: d.shortDescriptionEn, shortDescriptionBn: d.shortDescriptionBn, descriptionHtml: d.descriptionHtml,
    brand: d.brandId ? { id: d.brandId, label: d.brandName ?? 'Selected brand' } : null, category: { id: d.categoryId, label: d.categoryName },
    lengthCm: String(d.lengthCm), widthCm: String(d.widthCm), heightCm: String(d.heightCm)
  };
}

/** A dimension in cm as text: above 0, at most 1000, two decimals at most. Blank means "leave as it is". */
export function dimensionProblem(text: string): string | null {
  const t = text.trim();
  if (!t) return null;
  if (!/^\d{1,4}(\.\d{1,2})?$/.test(t) || !(Number(t) > 0) || Number(t) > MAX_DIMENSION_CM) return `A dimension is above 0 and at most ${MAX_DIMENSION_CM} cm, with at most 2 decimals.`;
  return null;
}

/** Whole-form check mirroring `ProductAdminService.UpdateAsync`; the body carries `expectedVersion`. */
export function validateBasics(f: BasicsForm, expectedVersion: number): { body: ProductBasicsBody | null; errors: FieldErrors } {
  const errors: FieldErrors = {};
  const len = (field: string, label: string, v: string, max: number, required: boolean) => {
    const n = v.trim().length;
    if (required && n === 0) errors[field] = `${label} is required.`;
    else if (n > max) errors[field] = `${label} is at most ${max.toLocaleString('en-US')} characters.`;
  };
  len('titleEn', 'English title', f.titleEn, MAX_TITLE, true);
  len('titleBn', 'Bengali title', f.titleBn, MAX_TITLE, true);
  len('shortDescriptionEn', 'English short description', f.shortDescriptionEn, MAX_SHORT_DESCRIPTION, false);
  len('shortDescriptionBn', 'Bengali short description', f.shortDescriptionBn, MAX_SHORT_DESCRIPTION, false);
  len('descriptionHtml', 'The description', f.descriptionHtml, MAX_DESCRIPTION_HTML, false);
  if (!f.category) errors.categoryId = 'A category is required.';
  for (const [k, v] of [['lengthCm', f.lengthCm], ['widthCm', f.widthCm], ['heightCm', f.heightCm]] as const) { const e = dimensionProblem(v); if (e) errors[k] = e; }
  if (Object.keys(errors).length || !f.category) return { body: null, errors };
  const dim = (v: string) => (v.trim() ? Number(v.trim()) : undefined);
  return {
    body: {
      titleEn: f.titleEn.trim(), titleBn: f.titleBn.trim(), shortDescriptionEn: f.shortDescriptionEn.trim(), shortDescriptionBn: f.shortDescriptionBn.trim(),
      descriptionHtml: f.descriptionHtml, brandId: f.brand?.id ?? null, categoryId: f.category.id,
      lengthCm: dim(f.lengthCm), widthCm: dim(f.widthCm), heightCm: dim(f.heightCm), expectedVersion
    },
    errors
  };
}

/** Weight in kg as text: above 0, at most 1000, three decimals at most. Pure string check; the number is only made at the end. */
export function weightProblem(text: string): string | null {
  const t = text.trim();
  const m = /^(\d{1,4})(?:\.(\d{1,3}))?$/.exec(t);
  if (!m) return /^\d+\.\d{4,}$/.test(t) ? 'Weight can have at most 3 decimals (grams).' : 'Weight is a number in kg, for example 0.250.';
  const whole = Number(m[1]);
  const frac = Number((m[2] ?? '').padEnd(3, '0') || '0');
  if (whole === 0 && frac === 0) return 'Weight must be more than 0.';
  if (whole > MAX_WEIGHT_KG || (whole === MAX_WEIGHT_KG && frac > 0)) return `Weight is at most ${MAX_WEIGHT_KG} kg.`;
  return null;
}

/** Taka text to poisha without floats ("1,234.5" ok, three decimals not); null when it is not a plain amount. */
export function priceTextToMinor(text: string): number | null {
  const m = /^(\d{1,12})(?:\.(\d{1,2}))?$/.exec(text.trim().replace(/,/g, ''));
  return m ? Number(m[1]) * 100 + Number((m[2] ?? '').padEnd(2, '0') || '0') : null;
}
export function priceTextProblem(text: string): string | null {
  const minor = priceTextToMinor(text);
  if (minor === null) return 'Enter the price in taka, for example 1250.50 (at most 2 decimals).';
  if (minor < 1) return 'The price must be more than 0.';
  if (minor > MAX_UNIT_PRICE_MINOR) return 'That price is above the allowed maximum.';
  return null;
}
export function minorToTakaText(minor: number): string { return `${Math.floor(minor / 100)}.${String(minor % 100).padStart(2, '0')}`; }
/** Weight as the input shows it: "0.25" stays "0.25", 0 (not set) is blank. */
export function weightToText(kg: number): string { return kg > 0 ? String(kg) : ''; }

/** Builds the variant PUT body from the typed values; only what changed is sent. `priceChange` is set when the price changed (the screen confirms it). */
export function validateVariantEdit(v: AdminProductDetailVariant, f: { weight: string; price: string; status: string }, expectedVersion: number):
  { body: VariantEditBody | null; errors: FieldErrors; priceChange: { from: number; to: number } | null } {
  const errors: FieldErrors = {};
  const body: VariantEditBody = { expectedVersion };
  let priceChange: { from: number; to: number } | null = null;
  if (f.weight.trim() !== weightToText(v.weightKg)) { const e = weightProblem(f.weight); if (e) errors.weightKg = e; else body.weightKg = Number(f.weight.trim()); }
  const minor = priceTextToMinor(f.price);
  if (minor !== v.priceMinor) { const e = priceTextProblem(f.price); if (e) errors.priceMinor = e; else if (minor !== null) { body.priceMinor = minor; priceChange = { from: v.priceMinor, to: minor }; } }
  if (f.status !== v.status) body.status = f.status === 'inactive' ? 'inactive' : 'active';
  if (Object.keys(errors).length) return { body: null, errors, priceChange: null };
  if (body.weightKg === undefined && body.priceMinor === undefined && body.status === undefined) return { body: null, errors: { form: 'Nothing changed.' }, priceChange: null };
  return { body, errors, priceChange };
}

/** Plain words for the server's lifecycle refusal codes (409). */
const REFUSALS: Record<string, string> = {
  NO_MEDIA: 'Add at least one image first. A product without an image cannot be published.',
  NO_VARIANT: 'Add at least one SKU first. A product without a SKU cannot be published.',
  NO_SELLABLE_VARIANT: 'Make at least one SKU active first. A product needs a sellable SKU to be published.',
  INVALID_PRODUCT_TRANSITION: 'The product is not in a state that allows this. Reload to see its current status.'
};

export function catalogAdminErrorText(e: { status: number | null; code: string | null; message: string; fields: FieldErrors }): string {
  if (e.status === 401) return 'Your session has ended. Sign in again.';
  if (e.status === 403) return 'Your role cannot edit products (catalog.manage is needed).';
  if (e.status === 404) return 'This product no longer exists.';
  if (e.status === 409) {
    if (e.code === 'PRODUCT_STALE' || e.code === 'CONCURRENT_MODIFICATION') return 'Someone else changed this product. Reload to see their version; your edits stay on screen until you do.';
    return (e.code && REFUSALS[e.code]) || e.message || 'Another change got in the way. Reload and try again.';
  }
  if (e.status === 400) return Object.keys(e.fields).length ? 'Fix the highlighted fields.' : e.message || 'The server did not accept these values.';
  if (e.status !== null && e.status >= 500) return 'The server had a problem. Nothing was changed; try again in a moment.';
  return 'Could not reach the server. Check your connection and try again.';
}
