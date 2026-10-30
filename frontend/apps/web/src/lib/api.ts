import { MechWareApiClient, createApprovalsApi, createCompaniesApi } from '@mechware/api-client';

/** Base URL of the MECHWARE API. Must be same-site with the web app so the refresh cookie is sent. */
export const API_BASE_URL = process.env.NEXT_PUBLIC_API_BASE_URL ?? 'http://localhost:5000';

export const api = new MechWareApiClient({ baseUrl: API_BASE_URL });
export const companiesApi = createCompaniesApi(api);
export const approvalsApi = createApprovalsApi(api);

export { problemCode, problemFieldErrors, problemMessage, problemStatus } from '@mechware/api-client';
