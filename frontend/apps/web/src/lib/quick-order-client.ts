import { createQuickOrderApi } from '@mechware/api-client';
import { api } from './api';

/** Browser-only: the guest cart cookie and the Bearer token belong to this origin, like the cart client. */
export const quickOrderApi = createQuickOrderApi(api);
