'use client';

import * as React from 'react';
import dynamic from 'next/dynamic';
import { usePathname, useRouter } from 'next/navigation';
import { useLocale, useTranslations } from 'next-intl';
import { Filter, Search, X } from 'lucide-react';
import {
  Button,
  EmptyState,
  Pagination,
  PaginationContent,
  PaginationEllipsis,
  PaginationItem,
  PaginationLink,
  PaginationNext,
  PaginationPrevious,
} from '@mechware/ui';
import {
  DEFAULT_PAGE_SIZE,
  SEARCH_SORTS,
  formatPriceMinor,
  toSearchQueryString,
  type SearchParams,
  type SearchResponse,
  type SearchSort
} from '@mechware/api-client';
import { clearFilters, hasActiveFilters, maxPage, pageWindow, toggleBrand, withFilter } from '../../lib/search-ui';
import { FilterPanel } from './FilterPanel';
import { SearchSkeleton } from './SearchSkeleton';
import { ResultsTable } from './ResultsTable';
import { ResultsList } from './ResultsList';

const FilterDrawer = dynamic(() => import('./FilterDrawer'), { ssr: false });

export interface SearchViewProps {
  params: SearchParams;
  result: SearchResponse | null;
  errorStatus: number | null;
}

const lastSegment = (path: string) => path.split(/[/>]/).filter(Boolean).pop() ?? path;

function humanize(slug: string): string {
  return slug.replace(/[-_]+/g, ' ').replace(/\b\p{L}/gu, (c) => c.toUpperCase());
}

export function SearchView({ params, result, errorStatus }: SearchViewProps) {
  const t = useTranslations('Search');
  const locale = useLocale();
  const router = useRouter();
  const pathname = usePathname();
  const [pending, startTransition] = React.useTransition();
  const [drawerOpen, setDrawerOpen] = React.useState(false);
  const [query, setQuery] = React.useState(params.q ?? '');
  React.useEffect(() => setQuery(params.q ?? ''), [params.q]);

  /** Single write path: every control changes the URL; the server component re-renders with the new results. */
  const navigate = React.useCallback(
    (next: SearchParams) => {
      const qs = toSearchQueryString(next);
      startTransition(() => router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false }));
    },
    [router, pathname]
  );

  const meta = result?.meta;
  const hits = result?.data ?? [];
  const pageSize = meta?.pageSize ?? params.pageSize ?? DEFAULT_PAGE_SIZE;
  const page = meta?.page ?? params.page ?? 1;
  const last = maxPage(meta?.total ?? 0, pageSize);

  const brandNames = new Map(hits.map((h) => [h.brandSlug, h.brandName]));
  const brandFacet = meta?.facets?.brandSlug ?? {};
  // A selected value stays listed even when its facet count dropped to zero, so it can always be unchecked.
  const brandSlugs = new Set([...Object.keys(brandFacet), ...(params.brand ?? [])]);
  const brands = [...brandSlugs]
    .map((slug) => ({ slug, label: brandNames.get(slug) ?? humanize(slug), count: brandFacet[slug] ?? 0 }))
    .sort((a, b) => b.count - a.count || a.label.localeCompare(b.label));

  const categoryFacet = meta?.facets?.categoryPath ?? {};
  const categoryValues = new Set([...Object.keys(categoryFacet), ...(params.category ? [params.category] : [])]);
  const categories = [...categoryValues]
    .map((value) => ({ value, label: humanize(lastSegment(value)), count: categoryFacet[value] ?? 0 }))
    .sort((a, b) => b.count - a.count || a.label.localeCompare(b.label));

  const chips: { key: string; label: string; remove: SearchParams }[] = [];
  for (const slug of params.brand ?? []) {
    chips.push({ key: `brand-${slug}`, label: `${t('brand')}: ${brandNames.get(slug) ?? humanize(slug)}`, remove: toggleBrand(params, slug) });
  }
  if (params.category) {
    chips.push({ key: 'category', label: `${t('category')}: ${humanize(lastSegment(params.category))}`, remove: withFilter(params, { category: undefined }) });
  }
  if (params.inStock) chips.push({ key: 'instock', label: t('inStockOnly'), remove: withFilter(params, { inStock: undefined }) });
  if (params.minPriceMinor !== undefined)
    chips.push({ key: 'min', label: `${t('minPrice')}: ${formatPriceMinor(params.minPriceMinor, 'BDT', locale)}`, remove: withFilter(params, { minPriceMinor: undefined }) });
  if (params.maxPriceMinor !== undefined)
    chips.push({ key: 'max', label: `${t('maxPrice')}: ${formatPriceMinor(params.maxPriceMinor, 'BDT', locale)}`, remove: withFilter(params, { maxPriceMinor: undefined }) });

  const panel = (idPrefix: string) => (
    <FilterPanel idPrefix={idPrefix} params={params} brands={brands} categories={categories} onChange={navigate} />
  );

  const onSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    navigate({ ...params, q: query.trim() || undefined, page: undefined });
  };

  const sortLabels: Record<SearchSort, string> = {
    relevance: t('sortRelevance'),
    'price-asc': t('sortPriceAsc'),
    'price-desc': t('sortPriceDesc'),
    'most-sold': t('sortMostSold')
  };

  return (
    <main className="mx-auto max-w-7xl px-4 py-6">
      <h1 className="font-display text-2xl font-bold text-text mb-4">{t('title')}</h1>

      <form role="search" onSubmit={onSubmit} className="flex gap-2 mb-4">
        <div className="flex-1">
          <label htmlFor="search-q" className="sr-only">
            {t('searchLabel')}
          </label>
          <input
            id="search-q"
            type="search"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={t('searchPlaceholder')}
            maxLength={200}
            className="h-11 w-full rounded-md border border-surface-border-strong bg-bg-muted px-3.5 text-sm text-text placeholder:text-text-subtle outline-none focus-visible:ring-2 focus-visible:ring-accent"
          />
        </div>
        <Button type="submit" variant="accent" leftIcon={<Search className="h-4 w-4" />}>
          {t('searchButton')}
        </Button>
      </form>

      <div className="flex flex-wrap items-center gap-3 mb-3">
        <Button type="button" variant="secondary" className="md:hidden" onClick={() => setDrawerOpen(true)} leftIcon={<Filter className="h-4 w-4" />}>
          {t('filters')}
          {chips.length > 0 ? ` (${chips.length})` : ''}
        </Button>
        <p className="text-sm text-text-muted" role="status" aria-live="polite">
          {meta ? (
            <>
              {t('resultCount', { count: meta.total })}
              {meta.matchedBy === 'partNumber' && <span className="ml-2 font-semibold text-accent">{t('matchedPartNumber')}</span>}
            </>
          ) : null}
        </p>
        <div className="ml-auto w-56">
          <label htmlFor="search-sort" className="sr-only">
            {t('sortBy')}
          </label>
          <select
            id="search-sort"
            value={params.sort ?? 'relevance'}
            onChange={(e) => navigate({ ...params, sort: e.target.value as SearchSort, page: undefined })}
            className="h-11 w-full rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm text-text outline-none focus-visible:ring-2 focus-visible:ring-accent"
          >
            {SEARCH_SORTS.map((s) => (
              <option key={s} value={s}>
                {sortLabels[s]}
              </option>
            ))}
          </select>
        </div>
      </div>

      {chips.length > 0 && (
        <ul className="flex flex-wrap items-center gap-2 mb-4" aria-label={t('activeFilters')}>
          {chips.map((c) => (
            <li key={c.key}>
              <button
                type="button"
                onClick={() => navigate(c.remove)}
                aria-label={t('removeFilter', { label: c.label })}
                className="inline-flex items-center gap-1.5 rounded-full border border-surface-border-strong bg-bg-muted px-3 py-1 text-xs text-text hover:border-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
              >
                {c.label}
                <X className="h-3 w-3" aria-hidden="true" />
              </button>
            </li>
          ))}
          <li>
            <Button type="button" variant="link" size="sm" onClick={() => navigate(clearFilters(params))}>
              {t('clearAll')}
            </Button>
          </li>
        </ul>
      )}

      <div className="md:grid md:grid-cols-[16rem_1fr] md:gap-8">
        <aside className="hidden md:block" aria-label={t('filters')}>
          {panel('side')}
        </aside>

        <section aria-busy={pending} className={pending ? 'opacity-60 transition-opacity' : 'transition-opacity'}>
          {errorStatus !== null ? (
            <div role="alert" className="rounded-lg border border-fault/40 bg-fault/10 p-6 text-center">
              <p className="font-semibold text-text mb-1">{errorStatus === 503 ? t('errorUnavailable') : t('errorGeneric')}</p>
              <Button type="button" variant="secondary" className="mt-3" onClick={() => startTransition(() => router.refresh())}>
                {t('retry')}
              </Button>
            </div>
          ) : pending && hits.length === 0 ? (
            <SearchSkeleton />
          ) : hits.length === 0 ? (
            <EmptyState
              title={t('emptyTitle')}
              description={hasActiveFilters(params) ? t('emptyWithFilters') : t('emptyHint')}
              action={
                hasActiveFilters(params) ? (
                  <Button type="button" variant="secondary" onClick={() => navigate(clearFilters(params))}>
                    {t('clearAll')}
                  </Button>
                ) : undefined
              }
            />
          ) : (
            <>
              <ResultsTable hits={hits} locale={locale} query={params.q} highlight={meta?.matchedBy === 'partNumber'} t={t} />
              <ResultsList hits={hits} locale={locale} query={params.q} highlight={meta?.matchedBy === 'partNumber'} t={t} />

              {last > 1 && (
                <Pagination aria-label={t('pagination')} className="mt-6">
                  <PaginationContent className="flex-wrap justify-center">
                    <PaginationItem>
                      <PaginationPrevious disabled={page <= 1} onClick={() => navigate({ ...params, page: page - 1 })} />
                    </PaginationItem>
                    {pageWindow(page, last).map((p, i) =>
                      p === null ? (
                        <PaginationItem key={`gap-${i}`}>
                          <PaginationEllipsis />
                        </PaginationItem>
                      ) : (
                        <PaginationItem key={p}>
                          <PaginationLink isActive={p === page} aria-label={t('goToPage', { page: p })} onClick={() => navigate({ ...params, page: p })}>
                            {p}
                          </PaginationLink>
                        </PaginationItem>
                      )
                    )}
                    <PaginationItem>
                      <PaginationNext disabled={page >= last} onClick={() => navigate({ ...params, page: page + 1 })} />
                    </PaginationItem>
                  </PaginationContent>
                </Pagination>
              )}
            </>
          )}
        </section>
      </div>

      {drawerOpen && (
        <FilterDrawer open={drawerOpen} onOpenChange={setDrawerOpen} total={meta?.total ?? 0}>
          {panel('drawer')}
        </FilterDrawer>
      )}
    </main>
  );
}
