import { createCreditBooksApi } from '@mechware/api-client';
import { api } from './api';

// Its own module, so the pages that never touch the credit books do not carry this code (lib/api.ts is on every page).
export const creditBooksApi = createCreditBooksApi(api);
