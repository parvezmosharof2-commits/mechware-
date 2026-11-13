import { createOrdersApi } from '@mechware/api-client';
import { api } from './api';

/** Browser-only: checkout calls need the buyer's Bearer token held in memory. */
export const ordersApi = createOrdersApi(api);
