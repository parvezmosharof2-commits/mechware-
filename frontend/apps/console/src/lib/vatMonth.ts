import { createVatMonthApi } from '@mechware/api-client';
import { api } from './api';

// Its own module, so the pages that never touch the VAT export do not carry this code.
export const vatMonthApi = createVatMonthApi(api);
