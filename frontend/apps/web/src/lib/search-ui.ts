import type { SearchParams } from '@mechware/api-client';

/** Pure helpers for the search page: filter edits (each resets paging) and price/page conversions. */

/** Taka typed by the buyer ("1,250.5") to poisha; undefined for empty or invalid input. */
export function takaToMinor(input: string): number | undefined {
  const cleaned = input.replace(/[,\s]/g, '');
  if (!/^\d+(\.\d{1,2})?$/.test(cleaned)) return undefined;
  return Math.round(Number(cleaned) * 100);
}

/** Poisha to the plain number shown in a taka input ("1250.5"); empty when unset. */
export function minorToTaka(minor: number | undefined): string {
  if (minor === undefined) return '';
  return String(minor / 100);
}

export function maxPage(total: number, pageSize: number): number {
  return Math.max(1, Math.min(200, Math.ceil(total / Math.max(1, pageSize))));
}

/** Page numbers to show, with null for a gap: 1 … 4 5 [6] 7 8 … 20. */
export function pageWindow(current: number, last: number): (number | null)[] {
  const pages = new Set<number>([1, last, current - 1, current, current + 1]);
  const sorted = [...pages].filter((p) => p >= 1 && p <= last).sort((a, b) => a - b);
  const out: (number | null)[] = [];
  sorted.forEach((p, i) => {
    if (i > 0 && p - sorted[i - 1]! > 1) out.push(null);
    out.push(p);
  });
  return out;
}

/** Applies a filter change and sends the buyer back to page 1. */
export function withFilter(params: SearchParams, change: Partial<SearchParams>): SearchParams {
  return { ...params, ...change, page: undefined };
}

export function toggleBrand(params: SearchParams, slug: string): SearchParams {
  const current = params.brand ?? [];
  const brand = current.includes(slug) ? current.filter((b) => b !== slug) : [...current, slug];
  return withFilter(params, { brand: brand.length ? brand : undefined });
}

/** Everything except the text query (and sort/page size, which are preferences, not filters). */
export function clearFilters(params: SearchParams): SearchParams {
  return { q: params.q, sort: params.sort, pageSize: params.pageSize };
}

export function hasActiveFilters(params: SearchParams): boolean {
  return Boolean(
    params.brand?.length ||
      params.category ||
      params.inStock ||
      params.minPriceMinor !== undefined ||
      params.maxPriceMinor !== undefined ||
      params.attr?.length
  );
}
