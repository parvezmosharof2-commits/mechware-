import type { MechWareApiClient } from './index';
import { problemStatus } from './problems';
import { toSearchQueryString } from './storefront-model';
import type {
  AutocompleteSuggestion,
  BrandListItem,
  CategoryNode,
  CategoryProductsMeta,
  PartNumberLookup,
  ProductCard,
  ProductDetail,
  SearchParams,
  SearchResponse
} from './storefront-model';

type Data<T> = { data: T };

/**
 * Typed wrappers for the public storefront: /api/v1/search and /api/v1/storefront. Anonymous: the shared client attaches a
 * token only when a session exists, and none of these calls needs one.
 */
export function createStorefrontApi(client: MechWareApiClient) {
  const withQuery = (path: string, query: URLSearchParams | string) => {
    const qs = query.toString();
    return qs ? `${path}?${qs}` : path;
  };

  return {
    search: (params: SearchParams = {}) => client.fetch<SearchResponse>(withQuery('/api/v1/search', toSearchQueryString(params))),

    autocomplete: (q: string, limit?: number) => {
      const query = new URLSearchParams({ q });
      if (limit !== undefined) query.set('limit', String(limit));
      return client.fetch<Data<AutocompleteSuggestion[]> & { meta: { processingMs: number } }>(
        withQuery('/api/v1/search/autocomplete', query)
      );
    },

    /** A retired slug answers 301; fetch follows it, so `data.slug` may differ from the one asked for (use it for the canonical URL). */
    productBySlug: (slug: string) => client.fetch<Data<ProductDetail>>(`/api/v1/storefront/products/${encodeURIComponent(slug)}`),

    brands: () => client.fetch<Data<BrandListItem[]>>('/api/v1/storefront/brands'),

    categoryTree: () => client.fetch<Data<CategoryNode[]>>('/api/v1/storefront/categories'),

    categoryProducts: (slug: string, page = 1, pageSize = 24) =>
      client.fetch<Data<ProductCard[]> & { meta: CategoryProductsMeta }>(
        withQuery(
          `/api/v1/storefront/categories/${encodeURIComponent(slug)}/products`,
          new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
        )
      ),

    /** Manufacturer part number lookup in any formatting; null when no product has it (404). */
    partNumber: async (q: string): Promise<PartNumberLookup | null> => {
      try {
        const res = await client.fetch<Data<PartNumberLookup>>(withQuery('/api/v1/storefront/pn', new URLSearchParams({ q })));
        return res.data;
      } catch (error) {
        if (problemStatus(error) === 404) return null;
        throw error;
      }
    }
  };
}

export type StorefrontApi = ReturnType<typeof createStorefrontApi>;
