/** Staff picker lookups (E8.3b API, D2.11b screens): variants by SKU or text, companies by legal name. No BIN, no personal data. */

export interface AdminVariant {
  variantId: string;
  productId: string;
  sku: string;
  partNumber: string | null;
  title: string;
  brand: string | null;
  productStatus: string;
  active: boolean;
  /** List price, VAT-inclusive, poisha. */
  priceMinor: number;
  /** Only present for callers holding `pricing.view_cost`. */
  costMinor?: number;
}

export interface CompanyLookupItem {
  id: string;
  legalName: string;
  status: string;
}

export interface LookupPage<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export const LOOKUP_MAX_PAGE_SIZE = 50;

function paged(p: URLSearchParams, page?: number, pageSize?: number, fallbackSize = 25): string {
  p.set('page', String(Math.max(1, Math.floor(page ?? 1))));
  p.set('pageSize', String(Math.min(LOOKUP_MAX_PAGE_SIZE, Math.max(1, Math.floor(pageSize ?? fallbackSize)))));
  return p.toString();
}

/** Query for `GET /admin/variants`: exact SKU first (`sku`), free text (`q`) over SKU, part number and title. Empties are left out. */
export function variantLookupQuery(f: { sku?: string; q?: string; page?: number; pageSize?: number } = {}): string {
  const p = new URLSearchParams();
  if (f.sku?.trim()) p.set('sku', f.sku.trim());
  if (f.q?.trim()) p.set('q', f.q.trim());
  return paged(p, f.page, f.pageSize);
}

/** Query for `GET /admin/companies/lookup`. */
export function companyLookupQuery(f: { q?: string; status?: string; page?: number; pageSize?: number } = {}): string {
  const p = new URLSearchParams();
  if (f.q?.trim()) p.set('q', f.q.trim());
  if (f.status) p.set('status', f.status);
  return paged(p, f.page, f.pageSize, LOOKUP_MAX_PAGE_SIZE);
}

/** The variant whose SKU equals `sku` (case-insensitive), or null. The server already matches exactly; this guards against a looser reply. */
export function exactSkuMatch(items: readonly AdminVariant[], sku: string): AdminVariant | null {
  const wanted = sku.trim().toLowerCase();
  return items.find((v) => v.sku.toLowerCase() === wanted) ?? null;
}
