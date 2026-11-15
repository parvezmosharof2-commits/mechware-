import {
  MechWareApiClient, createAdminLookupsApi, createCatalogImportApi, createCompaniesApi, createPriceListsApi, createShippingZonesApi, createStaffApi, createStorefrontApi,
  mapServerFields, priceListErrorText, problemCode, problemFieldErrors, problemMessage, problemStatus, shippingErrorText
} from '@mechware/api-client';

export const API_BASE_URL = process.env.NEXT_PUBLIC_API_BASE_URL ?? 'http://localhost:5000';

export const api = new MechWareApiClient({ baseUrl: API_BASE_URL });
export const companiesApi = createCompaniesApi(api);
export const staffApi = createStaffApi(api);
export const catalogImportApi = createCatalogImportApi(api);
export const lookupsApi = createAdminLookupsApi(api);
export const priceListsApi = createPriceListsApi(api);
export const shippingZonesApi = createShippingZonesApi(api);
export const storefrontApi = createStorefrontApi(api);

export { problemCode, problemFieldErrors, problemMessage, problemStatus };

/** Plain-words text for a failed price-list call, and whether a retry makes sense (server error or no connection). */
export function priceListError(err: unknown): { text: string; retry: boolean; status: number | null; fields: Record<string, string> } {
  const status = problemStatus(err);
  const fields = problemFieldErrors(err);
  const text = priceListErrorText({ status, code: problemCode(err), message: problemMessage(err, ''), fields });
  return { text, retry: status === null || status >= 500, status, fields };
}

/** Plain-words text for a failed shipping-zone call, the server's field errors (by form key) and whether a retry makes sense. */
export function shippingError(err: unknown): { text: string; retry: boolean; status: number | null; code: string | null; fields: Record<string, string> } {
  const status = problemStatus(err);
  const fields = mapServerFields(problemFieldErrors(err));
  const code = problemCode(err);
  return { text: shippingErrorText({ status, code, message: problemMessage(err, ''), fields }), retry: status === null || status >= 500, status, code, fields };
}

/** Roles allowed into the operations console. Buyers (storefront customers) are not. */
export const STAFF_ROLES = ['admin', 'finance', 'support', 'moderator', 'seller'] as const;

export function isStaff(roles: readonly string[]): boolean {
  return roles.some((r) => (STAFF_ROLES as readonly string[]).includes(r.toLowerCase()));
}
