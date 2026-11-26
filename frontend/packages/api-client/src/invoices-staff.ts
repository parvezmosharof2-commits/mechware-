import type { MechWareApiClient } from './index';
import type { InvoiceLocale } from './invoices-model';
import {
  isSha256Hex, staffInvoiceListQuery, staffInvoicePdfPath, type InvoiceDocumentMatch, type StaffInvoiceDetail, type StaffInvoiceFilters, type StaffInvoicePage
} from './invoices-staff-model';

type Data<T> = { data: T };

/**
 * Staff invoice calls (`invoices.view`; E6.8a reads, E6.4b PDF and lookup). Never cached. `check` sends only a SHA-256; an unknown document
 * is a 404 on the wire and `null` here. The PDF route is limited to 30 a minute per person.
 */
export function createStaffInvoicesApi(client: MechWareApiClient) {
  const init: RequestInit = { cache: 'no-store' };
  const base = '/api/v1/admin/invoices';
  return {
    list: (f: StaffInvoiceFilters) => client.fetch<Data<StaffInvoicePage>>(`${base}?${staffInvoiceListQuery(f)}`, init).then((r) => r.data),
    detail: (id: string) => client.fetch<Data<StaffInvoiceDetail>>(`${base}/${encodeURIComponent(id)}`, init).then((r) => r.data),
    pdf: (id: string, locale: InvoiceLocale) => client.fetchBytes(staffInvoicePdfPath(id, locale), init),
    check: async (sha256: string): Promise<InvoiceDocumentMatch | null> => {
      if (!isSha256Hex(sha256)) throw new Error('A SHA-256 is 64 hex digits.');
      try {
        return (await client.fetch<Data<InvoiceDocumentMatch>>(`/api/v1/admin/invoice-documents/${sha256.toLowerCase()}`, init)).data;
      } catch (err) {
        if ((err as { status?: number } | null)?.status === 404) return null;
        throw err;
      }
    }
  };
}
export type StaffInvoicesApi = ReturnType<typeof createStaffInvoicesApi>;
