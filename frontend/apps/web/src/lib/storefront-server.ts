import { cache } from 'react';
import {
  MechWareApiClient,
  createStorefrontApi,
  problemStatus,
  type BrandListItem,
  type CategoryNode,
  type ProductDetail
} from '@mechware/api-client';

/** Server-only base URL of the API (inside docker it is the internal host); media URLs use the public one. */
const API_BASE_URL = process.env.API_INTERNAL_BASE_URL ?? process.env.NEXT_PUBLIC_API_BASE_URL ?? 'http://localhost:5000';
export const PUBLIC_API_BASE_URL = process.env.NEXT_PUBLIC_API_BASE_URL ?? 'http://localhost:5000';

// Anonymous, no refresh: a server render never touches the browser token vault.
export const storefront = createStorefrontApi(
  new MechWareApiClient({ baseUrl: API_BASE_URL, getAccessToken: () => null, autoRefreshOn401: false })
);

export type ProductResult = { kind: 'ok'; product: ProductDetail } | { kind: 'not-found' } | { kind: 'error'; status: number };

/** One fetch per request, shared by generateMetadata and the page. A retired slug is followed by fetch (301). */
export const loadProduct = cache(async (slug: string): Promise<ProductResult> => {
  try {
    return { kind: 'ok', product: (await storefront.productBySlug(slug)).data };
  } catch (error) {
    const status = problemStatus(error);
    if (status === 404) return { kind: 'not-found' };
    return { kind: 'error', status: status ?? 503 };
  }
});

/** Slow-changing reference data for the header and home page: cached by Next's data cache for five minutes. */
async function cachedGet<T>(path: string, fallback: T): Promise<T> {
  try {
    const res = await fetch(`${API_BASE_URL}${path}`, { next: { revalidate: 300 } });
    if (!res.ok) return fallback;
    return ((await res.json()) as { data: T }).data;
  } catch {
    return fallback;
  }
}

export const topCategories = cache(async (): Promise<CategoryNode[]> => cachedGet<CategoryNode[]>('/api/v1/storefront/categories', []));
export const allBrands = cache(async (): Promise<BrandListItem[]> => cachedGet<BrandListItem[]>('/api/v1/storefront/brands', []));
