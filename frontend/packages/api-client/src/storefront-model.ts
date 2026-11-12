// Types below are hand-written from the C# records (camelCase JSON): the storefront and search controllers return
// anonymous { data, meta } envelopes, so schema.d.ts does not describe their bodies.

export type SearchSort = 'relevance' | 'price-asc' | 'price-desc' | 'most-sold';
export const SEARCH_SORTS: readonly SearchSort[] = ['relevance', 'price-asc', 'price-desc', 'most-sold'];

export const MAX_PAGE = 200;
export const MAX_PAGE_SIZE = 50;
export const DEFAULT_PAGE_SIZE = 24;

export interface SearchParams {
  q?: string;
  page?: number;
  pageSize?: number;
  brand?: string[];
  category?: string;
  inStock?: boolean;
  minPriceMinor?: number;
  maxPriceMinor?: number;
  /** Each `code:min..max`, see attrParam. */
  attr?: string[];
  sort?: SearchSort;
}

export interface StorefrontMoney {
  amount: string;
  amountMinor: number;
  currency: string;
  formatted: string;
}

export interface SearchHit {
  id: string;
  slug: string;
  titleEn: string;
  titleBn: string;
  brandName: string;
  brandSlug: string;
  partNumbers: string[];
  priceMinor: number;
  currency: string;
  inStock: boolean;
  supplyStatus: string;
  ratingAvg: number;
}

export interface SearchMeta {
  page: number;
  pageSize: number;
  total: number;
  matchedBy: 'partNumber' | 'measure' | 'text' | string;
  processingMs: number;
  /** facet name -> value -> count */
  facets: Record<string, Record<string, number>>;
}

export interface SearchResponse {
  data: SearchHit[];
  meta: SearchMeta;
}

export interface AutocompleteSuggestion {
  productId: string;
  slug: string;
  titleEn: string;
  titleBn: string;
}

export interface BrandDto { name: string; slug: string; logoUrl: string | null; isVerified: boolean }
export interface SellerSummaryDto { storeName: string; slug: string; logoUrl: string | null; ratingAvg: number; ratingCount: number }
export interface BreadcrumbDto { nameEn: string; nameBn: string; slug: string }
export interface MediaDto { url: string; alt: string; type: string; width: number; height: number; blurDataUrl: string | null; variantId: string | null }
export interface StockDto { available: number; status: 'in_stock' | 'low_stock' | 'out_of_stock' | string }
export interface VariantPriceDto {
  price: StorefrontMoney;
  compareAt: StorefrontMoney | null;
  discountPercent: number;
  saleActive: boolean;
  taxIncluded: boolean;
  tax: StorefrontMoney | null;
}
export interface VariantDto {
  id: string;
  sku: string;
  options: Record<string, string>;
  isDefault: boolean;
  pricing: VariantPriceDto;
  stock: StockDto;
  minOrderQty: number;
  packSize: number;
  manufacturerPartNumber: string | null;
}
export interface SuccessorDto { slug: string; titleEn: string; titleBn: string; isDropIn: boolean; note: string | null }
export interface AlternativeDto { slug: string; titleEn: string; titleBn: string; brandName: string | null }
/** `kind` is the lower-cased document kind (datasheet, cad, certificate...); `url` is a download link on the API. */
export interface PublicDocumentDto { id: string; kind: string; fileName: string; contentType: string; sizeBytes: number; url: string }

/** A brand that has at least one published product (labels for brand facet slugs). */
export interface BrandListItem { slug: string; name: string; logoUrl: string | null }

export interface ProductDetail {
  id: string;
  slug: string;
  titleEn: string;
  titleBn: string;
  shortDescriptionEn: string;
  shortDescriptionBn: string;
  descriptionHtml: string;
  highlights: string[];
  specs: Record<string, Record<string, string>>;
  brand: BrandDto | null;
  seller: SellerSummaryDto;
  breadcrumbs: BreadcrumbDto[];
  condition: string;
  warrantyMonths: number;
  returnPolicyDays: number;
  ratingAvg: number;
  ratingCount: number;
  soldCount: number;
  datasheetUrl: string | null;
  cadModelUrl: string | null;
  media: MediaDto[];
  variants: VariantDto[];
  supplyStatus: string;
  successor: SuccessorDto | null;
  alternatives?: AlternativeDto[] | null;
  documents?: PublicDocumentDto[] | null;
}

export interface ProductCard {
  id: string;
  slug: string;
  titleEn: string;
  titleBn: string;
  brandName: string | null;
  imageUrl: string | null;
  imageAlt: string | null;
  price: StorefrontMoney;
  compareAt: StorefrontMoney | null;
  discountPercent: number;
  ratingAvg: number;
  ratingCount: number;
  stockStatus: string;
}

export interface CategoryNode {
  id: string;
  nameEn: string;
  nameBn: string;
  slug: string;
  path: string;
  depth: number;
  imageUrl: string | null;
  children: CategoryNode[];
}

export interface CategoryProductsMeta { page: number; pageSize: number; totalCount: number }

export interface PartNumberMatch {
  slug: string;
  titleEn: string;
  titleBn: string;
  brandName: string | null;
  partNumber: string;
  sku: string;
  supplyStatus: string;
}

export interface PartNumberLookup {
  key: string;
  /** False: found by reading O as 0 and I/L as 1; ask the buyer "did you mean". */
  exact: boolean;
  matches: PartNumberMatch[];
}

function clampInt(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, Math.trunc(value)));
}

function cleanList(values: string[] | undefined): string[] {
  return (values ?? []).map((v) => v.trim()).filter((v) => v.length > 0);
}

/** `code:min..max`, either bound optional. */
export function attrParam(code: string, min?: number | null, max?: number | null): string {
  return `${code}:${min ?? ''}..${max ?? ''}`;
}

/** Canonical query string (no leading `?`): stable key order, defaults and empties omitted, brand/attr repeated. */
export function toSearchQueryString(params: SearchParams): string {
  const out = new URLSearchParams();
  const q = params.q?.trim();
  if (q) out.set('q', q);
  if (params.category?.trim()) out.set('category', params.category.trim());
  for (const b of cleanList(params.brand)) out.append('brand', b);
  if (params.inStock) out.set('inStock', 'true');
  if (params.minPriceMinor !== undefined && Number.isFinite(params.minPriceMinor)) out.set('minPriceMinor', String(Math.trunc(params.minPriceMinor)));
  if (params.maxPriceMinor !== undefined && Number.isFinite(params.maxPriceMinor)) out.set('maxPriceMinor', String(Math.trunc(params.maxPriceMinor)));
  for (const a of cleanList(params.attr)) out.append('attr', a);
  if (params.sort && params.sort !== 'relevance' && SEARCH_SORTS.includes(params.sort)) out.set('sort', params.sort);
  if (params.pageSize !== undefined && Number.isFinite(params.pageSize)) {
    const size = clampInt(params.pageSize, 1, MAX_PAGE_SIZE);
    if (size !== DEFAULT_PAGE_SIZE) out.set('pageSize', String(size));
  }
  if (params.page !== undefined && Number.isFinite(params.page)) {
    const page = clampInt(params.page, 1, MAX_PAGE);
    if (page !== 1) out.set('page', String(page));
  }
  return out.toString();
}

function parseInteger(raw: string | null): number | undefined {
  if (raw === null || !/^-?\d+$/.test(raw.trim())) return undefined;
  const n = Number(raw.trim());
  return Number.isSafeInteger(n) ? n : undefined;
}

/** Inverse of toSearchQueryString for URL-synced filters: invalid values are dropped, page/pageSize clamped. */
export function fromSearchParams(source: URLSearchParams): SearchParams {
  const result: SearchParams = {};
  const q = source.get('q')?.trim();
  if (q) result.q = q;
  const category = source.get('category')?.trim();
  if (category) result.category = category;
  const brand = cleanList(source.getAll('brand'));
  if (brand.length) result.brand = brand;
  const inStock = source.get('inStock');
  if (inStock === 'true' || inStock === '1') result.inStock = true;
  const min = parseInteger(source.get('minPriceMinor'));
  if (min !== undefined && min >= 0) result.minPriceMinor = min;
  const max = parseInteger(source.get('maxPriceMinor'));
  if (max !== undefined && max >= 0) result.maxPriceMinor = max;
  const attr = cleanList(source.getAll('attr'));
  if (attr.length) result.attr = attr;
  const sort = source.get('sort') as SearchSort | null;
  if (sort && sort !== 'relevance' && SEARCH_SORTS.includes(sort)) result.sort = sort;
  const pageSize = parseInteger(source.get('pageSize'));
  if (pageSize !== undefined) {
    const size = clampInt(pageSize, 1, MAX_PAGE_SIZE);
    if (size !== DEFAULT_PAGE_SIZE) result.pageSize = size;
  }
  const page = parseInteger(source.get('page'));
  if (page !== undefined) {
    const p = clampInt(page, 1, MAX_PAGE);
    if (p !== 1) result.page = p;
  }
  return result;
}

const SYMBOLS: Record<string, string> = { BDT: '৳', USD: '$', EUR: '€', GBP: '£' };

/** Minor units (poisha) to display text: "৳1,234.50"; the 'bn' locale uses Bangla digits and separators (bn-BD). */
export function formatPriceMinor(minor: number, currency = 'BDT', locale: string = 'en'): string {
  const negative = minor < 0;
  const major = Math.abs(minor) / 100;
  const tag = locale === 'bn' ? 'bn-BD' : 'en-US';
  const number = new Intl.NumberFormat(tag, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(major);
  const code = currency.toUpperCase();
  const symbol = SYMBOLS[code] ?? `${code} `;
  return `${negative ? '-' : ''}${symbol}${number}`;
}
