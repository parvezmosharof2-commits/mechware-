import { createBuyerRfqApi } from '@mechware/api-client';
import { api } from './api';

/** Browser-only: the Bearer token belongs to this origin. Every call is no-store (private to the signed-in member). */
export const buyerRfqApi = createBuyerRfqApi(api);
