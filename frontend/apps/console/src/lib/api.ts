import { MechWareApiClient, createCompaniesApi } from '@mechware/api-client';

export const API_BASE_URL = process.env.NEXT_PUBLIC_API_BASE_URL ?? 'http://localhost:5000';

export const api = new MechWareApiClient({ baseUrl: API_BASE_URL });
export const companiesApi = createCompaniesApi(api);

export { problemCode, problemFieldErrors, problemMessage, problemStatus } from '@mechware/api-client';

/** Roles allowed into the operations console. Buyers (storefront customers) are not. */
export const STAFF_ROLES = ['admin', 'finance', 'support', 'moderator', 'seller'] as const;

export function isStaff(roles: readonly string[]): boolean {
  return roles.some((r) => (STAFF_ROLES as readonly string[]).includes(r.toLowerCase()));
}
