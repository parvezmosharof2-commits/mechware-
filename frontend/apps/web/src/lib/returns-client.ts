import { createReturnsApi } from '@mechware/api-client';
import { api } from './api';

/** Browser-only: the buyer's return calls need the Bearer token held in memory. */
export const returnsApi = createReturnsApi(api);
