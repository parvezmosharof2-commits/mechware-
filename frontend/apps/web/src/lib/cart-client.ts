import { createCartApi } from '@mechware/api-client';
import { api } from './api';

/** Browser-only cart client: the guest cart cookie is HttpOnly and set by the API, so these calls must run in the browser (credentials are included by the shared client). */
export const cartApi = createCartApi(api);
