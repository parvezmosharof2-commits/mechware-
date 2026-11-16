import type { MechWareApiClient } from './index';
import { buildItemsQuery, type CompanyPricesResponse, type PriceAsk } from './company-prices-model';

/**
 * `GET /api/v1/companies/{companyId}/prices?items=variantId:qty,...` (D2.10). Private: call it from the browser only, with the
 * user's token, never from a server component or any shared cache. 401/404 mean "show the public price" to the caller.
 */
export function createCompanyPricesApi(client: MechWareApiClient) {
  return {
    get: (companyId: string, asks: readonly PriceAsk[], signal?: AbortSignal) =>
      client.fetch<CompanyPricesResponse>(
        `/api/v1/companies/${encodeURIComponent(companyId)}/prices?items=${buildItemsQuery(asks)}`,
        { signal, cache: 'no-store' }
      )
  };
}

export type CompanyPricesApi = ReturnType<typeof createCompanyPricesApi>;
