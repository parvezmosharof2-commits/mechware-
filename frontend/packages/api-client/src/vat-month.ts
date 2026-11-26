import type { MechWareApiClient } from './index';
import { vatMonthQuery, type VatMonthReport } from './vat-month-model';

/**
 * Monthly Mushak-9.1 export (`invoices.export`, E6.7): JSON preview with BINs masked (not audited) and the CSV with full BINs, which needs
 * an authenticator code in `X-Step-Up-Code` (a code is spent even when the call is refused), is audited and is limited to 6 a minute.
 */
export function createVatMonthApi(client: MechWareApiClient) {
  const base = '/api/v1/admin/invoices/exports/vat-month';
  return {
    preview: (month: string) => client.fetch<{ data: VatMonthReport }>(`${base}?${vatMonthQuery(month, 'json')}`, { cache: 'no-store' }),
    csv: (month: string, stepUpCode: string) =>
      client.fetchBytes(`${base}?${vatMonthQuery(month, 'csv')}`, { headers: { 'X-Step-Up-Code': stepUpCode.trim() } })
  };
}
