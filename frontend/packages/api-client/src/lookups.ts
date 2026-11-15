import type { MechWareApiClient } from './index';
import { companyLookupQuery, variantLookupQuery } from './lookups-model';
import type { AdminVariant, CompanyLookupItem, LookupPage } from './lookups-model';

type Data<T> = { data: T };

/** Staff lookups for pickers: `GET /api/v1/admin/variants` (pricelists.edit or catalog.manage) and `GET /api/v1/admin/companies/lookup` (companies.view). */
export function createAdminLookupsApi(client: MechWareApiClient) {
  return {
    variants: (f: { sku?: string; q?: string; page?: number; pageSize?: number } = {}) =>
      client.fetch<Data<LookupPage<AdminVariant>>>(`/api/v1/admin/variants?${variantLookupQuery(f)}`),
    companies: (f: { q?: string; status?: string; page?: number; pageSize?: number } = {}) =>
      client.fetch<Data<LookupPage<CompanyLookupItem>>>(`/api/v1/admin/companies/lookup?${companyLookupQuery(f)}`)
  };
}
