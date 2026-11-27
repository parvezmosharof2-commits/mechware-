import { createNotificationsApi } from '@mechware/api-client';
import { api } from './api';

/** Browser-only: the buyer's notification calls need the Bearer token held in memory. */
export const notificationsApi = createNotificationsApi(api);
