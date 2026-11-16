/** Shapes and pure helpers for the staff margin-floors screen (D2.9 API, D2.12b screen). Markups only, never a cost. Percent values are parsed as strings, never rounded. */

export type FloorScope = 'Global' | 'Brand' | 'Category' | 'BrandAndCategory';

export const FLOOR_PERMISSIONS = { view: 'pricing.view_cost', manage: 'pricing.manage_floors' } as const;
export const FLOOR_MAX_PERCENT = 1000;
export const FLOOR_MAX_DECIMALS = 2;

export interface MarginFloorRow {
  id: string;
  scope: FloorScope;
  brandId: string | null;
  brandName: string | null;
  categoryId: string | null;
  categoryName: string | null;
  /** English names, root to the row's own category. */
  categoryPath: string[];
  minMarkupPercent: number;
  updatedBy: string | null;
  updatedByName: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface EffectiveFloor {
  variantId: string | null;
  brandId: string | null;
  categoryId: string | null;
  categoryAncestry: string[];
  floor: MarginFloorRow | null;
  minMarkupPercent: number | null;
  failClosed: boolean;
}

export interface UpsertFloorBody {
  scope: FloorScope;
  brandId?: string;
  categoryId?: string;
  minMarkupPercent: number;
}

export type EffectiveQuery = { variantId: string } | { brandId?: string; categoryId?: string };

export const FLOOR_SCOPES: readonly FloorScope[] = ['Global', 'Brand', 'Category', 'BrandAndCategory'];

export const SCOPE_LABEL: Record<FloorScope, string> = {
  Global: 'Every product',
  Brand: 'Brand',
  Category: 'Category',
  BrandAndCategory: 'Brand and category'
};

export function effectiveQuery(q: EffectiveQuery): string {
  const p = new URLSearchParams();
  if ('variantId' in q) p.set('variantId', q.variantId);
  else {
    if (q.brandId) p.set('brandId', q.brandId);
    if (q.categoryId) p.set('categoryId', q.categoryId);
  }
  return p.toString();
}

// ---------- the markup input ----------

/**
 * Problem with a typed markup, or null when it is fine. 0 to 1000, at most 2 decimals, digits and one dot only. A third
 * decimal is refused, never rounded. Checked on the string itself (no float maths). Call it BEFORE asking for a code.
 */
export function markupPercentProblem(input: string | null | undefined): string | null {
  const v = (input ?? '').trim();
  if (v.length === 0) return 'Enter the minimum markup in percent.';
  if (!/^\d+(\.\d*)?$/.test(v)) return 'Use digits and a dot only, for example 12.5.';
  const [whole, frac = ''] = v.split('.');
  if (v.endsWith('.')) return 'Add the digits after the dot, or remove the dot.';
  if (frac.length > FLOOR_MAX_DECIMALS) return 'At most 2 decimals. The value is not rounded for you; type it with 2 decimals or fewer.';
  const w = whole.replace(/^0+(?=\d)/, '');
  if (w.length > 4) return 'The markup cannot be more than 1000 %.';
  const n = Number(w);
  if (n > FLOOR_MAX_PERCENT || (n === FLOOR_MAX_PERCENT && /[1-9]/.test(frac))) return 'The markup cannot be more than 1000 %.';
  return null;
}

/** The number to send, from a string that passed `markupPercentProblem`; null otherwise. */
export function markupToNumber(input: string): number | null {
  if (markupPercentProblem(input)) return null;
  return Number(input.trim());
}

/** Two decimals for display, for example 10 becomes "10.00". */
export function formatMarkup(value: number | null | undefined): string {
  return value === null || value === undefined || Number.isNaN(value) ? '-' : value.toFixed(2);
}

/** What the box should hold when editing an existing value. */
export function markupToInput(value: number): string {
  const s = value.toFixed(2);
  return s.includes('.') ? s.replace(/0+$/, '').replace(/\.$/, '') : s;
}

// ---------- scope shape ----------

/** Which ids a scope needs. Global none, Brand brand only, Category category only, BrandAndCategory both. */
export function scopeNeeds(scope: FloorScope): { brand: boolean; category: boolean } {
  return { brand: scope === 'Brand' || scope === 'BrandAndCategory', category: scope === 'Category' || scope === 'BrandAndCategory' };
}

export function scopeShapeProblem(scope: FloorScope, brandId: string | null | undefined, categoryId: string | null | undefined): string | null {
  const need = scopeNeeds(scope);
  if (need.brand && !brandId) return 'Pick a brand.';
  if (need.category && !categoryId) return 'Pick a category.';
  if (!need.brand && brandId) return 'This scope takes no brand.';
  if (!need.category && categoryId) return 'This scope takes no category.';
  return null;
}

/** Request body for the PUT, or the first problem found. Ids the scope does not take are left out. Never rounds. */
export function buildUpsertBody(f: { scope: FloorScope; brandId: string | null; categoryId: string | null; markup: string }): { body: UpsertFloorBody } | { problem: string; field: 'shape' | 'markup' } {
  const need = scopeNeeds(f.scope);
  const shape = scopeShapeProblem(f.scope, need.brand ? f.brandId : null, need.category ? f.categoryId : null);
  if (shape) return { problem: shape, field: 'shape' };
  const p = markupPercentProblem(f.markup);
  if (p) return { problem: p, field: 'markup' };
  return {
    body: {
      scope: f.scope,
      ...(need.brand && f.brandId ? { brandId: f.brandId } : {}),
      ...(need.category && f.categoryId ? { categoryId: f.categoryId } : {}),
      minMarkupPercent: Number(f.markup.trim())
    }
  };
}

// ---------- display ----------

const PRECEDENCE: Record<FloorScope, number> = { BrandAndCategory: 0, Brand: 1, Category: 2, Global: 3 };

/** The row's target in words: brand, category path, or both. */
export function floorTarget(r: Pick<MarginFloorRow, 'scope' | 'brandName' | 'categoryName' | 'categoryPath'>): string {
  const cat = r.categoryPath.length > 0 ? r.categoryPath.join(' › ') : r.categoryName ?? '';
  switch (r.scope) {
    case 'Global': return 'Every product without a more specific row';
    case 'Brand': return r.brandName ?? 'Unknown brand';
    case 'Category': return cat || 'Unknown category';
    default: return `${r.brandName ?? 'Unknown brand'} in ${cat || 'unknown category'}`;
  }
}

export function updatedByText(r: Pick<MarginFloorRow, 'updatedBy' | 'updatedByName'>): string {
  return r.updatedByName ?? (r.updatedBy ? 'a staff member' : 'system');
}

/** The global row (if any) and the rest ordered the way the policy looks them up: brand+category, brand, category, then by name. */
export function groupFloors(rows: readonly MarginFloorRow[]): { global: MarginFloorRow | null; specific: MarginFloorRow[] } {
  const global = rows.find((r) => r.scope === 'Global') ?? null;
  const specific = rows
    .filter((r) => r.scope !== 'Global')
    .sort((a, b) => PRECEDENCE[a.scope] - PRECEDENCE[b.scope] || floorTarget(a).localeCompare(floorTarget(b)));
  return { global, specific };
}

/** The row an add would hit (same scope, brand, category), or null: then it is a new row. */
export function findExistingRow(rows: readonly MarginFloorRow[], scope: FloorScope, brandId: string | null, categoryId: string | null): MarginFloorRow | null {
  const need = scopeNeeds(scope);
  return rows.find((r) => r.scope === scope && (!need.brand || r.brandId === brandId) && (!need.category || r.categoryId === categoryId)) ?? null;
}

export function effectiveSummary(e: EffectiveFloor): string {
  if (e.failClosed || !e.floor) return 'No floor row applies, and there is no global row: every price would count as below the floor until one is set.';
  const how = e.floor.scope === 'Global' ? 'the global default' : `the ${SCOPE_LABEL[e.floor.scope].toLowerCase()} row for ${floorTarget(e.floor)}`;
  return `${formatMarkup(e.floor.minMarkupPercent)} % applies, from ${how}.`;
}

// ---------- errors ----------

export interface FloorErrorLike {
  status: number | null;
  code: string | null;
  message: string;
  fields: Record<string, string>;
}

export const FLOOR_ERROR_TEXT: Record<string, string> = {
  GLOBAL_FLOOR_REQUIRED: 'The global floor cannot be deleted: there must always be one. Change its markup instead.',
  STEP_UP_REQUIRED: 'This action needs the 6-digit code from your authenticator app.',
  STEP_UP_FAILED: 'The code was not accepted. Codes work once: wait for the next code (up to 30 seconds) and enter that one.'
};

export function isFloorStepUpFailure(code: string | null | undefined): boolean {
  return code === 'STEP_UP_FAILED' || code === 'STEP_UP_REQUIRED';
}

/** Plain words for a failed floors call. */
export function floorErrorText(e: FloorErrorLike): string {
  if (e.code && FLOOR_ERROR_TEXT[e.code]) return FLOOR_ERROR_TEXT[e.code];
  if (e.status === 401) return 'Your session has ended. Sign in again.';
  if (e.status === 403) return 'You do not have permission to do this.';
  if (e.status === 404) return 'That floor, brand, category or product no longer exists. The list was reloaded.';
  if (e.status === 409) return e.message || 'Another change got in the way. Reload and try again.';
  if (e.status === 400) {
    const first = e.fields.minMarkupPercent ?? e.fields.markup ?? Object.values(e.fields)[0];
    return first ? `${e.message || 'The request was rejected.'} ${first}`.trim() : e.message || 'The request was rejected.';
  }
  if (e.status !== null && e.status >= 500) return 'The server had a problem. Nothing was changed; try again in a moment.';
  return 'Could not reach the server. Check your connection and try again.';
}

/** The note shown with every change, in the confirm dialog. */
export const FLOOR_CHANGE_NOTE = 'Lowering a floor does not approve anything by itself; pending lists are re-checked when approved. An approved list is never touched.';

// ---------- picker helpers ----------

export interface FlatCategory { id: string; label: string }

interface CategoryLike { id: string; nameEn: string; children?: CategoryLike[] }

/** The category tree as one list, label = names root to leaf joined by a single angle quote. */
export function flattenCategories(tree: readonly CategoryLike[], parents: string[] = []): FlatCategory[] {
  const out: FlatCategory[] = [];
  for (const n of tree) {
    const names = [...parents, n.nameEn];
    out.push({ id: n.id, label: names.join(' › ') });
    if (n.children?.length) out.push(...flattenCategories(n.children, names));
  }
  return out;
}

/** Case-insensitive contains over the label; blank returns everything. */
export function filterByText<T extends { label: string }>(items: readonly T[], term: string): T[] {
  const t = term.trim().toLowerCase();
  return t ? items.filter((i) => i.label.toLowerCase().includes(t)) : [...items];
}
