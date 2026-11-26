import type { MechWareApiClient } from './index';
import type { BuyerInvoiceDetail, InvoiceLocale, InvoicePage, InvoiceSummary } from './invoices-model';
import { invoiceListQuery, invoicePdfPath } from './invoices-model';

type Data<T> = { data: T };

/**
 * A buyer's own invoices (E6.8a reads, E6.4b PDF). Every answer is private; each call also asks the browser not to store it. A 404 is
 * one body for a stranger, a wrong id, and a role that may not see them; see `classifyInvoiceError`. Staff reads are a separate module.
 */
export function createInvoicesApi(client: MechWareApiClient) {
  const init: RequestInit = { cache: 'no-store' };
  const order = (orderId: string) => `/api/v1/orders/${encodeURIComponent(orderId)}/invoices`;
  const company = (companyId: string) => `/api/v1/companies/${encodeURIComponent(companyId)}/invoices`;
  return {
    forOrder: (orderId: string) => client.fetch<Data<InvoiceSummary[]>>(order(orderId), init).then((r) => r.data),
    orderDetail: (orderId: string, invoiceId: string) =>
      client.fetch<Data<BuyerInvoiceDetail>>(`${order(orderId)}/${encodeURIComponent(invoiceId)}`, init).then((r) => r.data),
    pdf: (orderId: string, invoiceId: string, locale: InvoiceLocale) => client.fetchBytes(invoicePdfPath(orderId, invoiceId, locale), init),
    companyList: (companyId: string, q: { from?: string; to?: string; page?: number; pageSize?: number } = {}) =>
      client.fetch<Data<InvoicePage>>(`${company(companyId)}?${invoiceListQuery(q)}`, init).then((r) => r.data),
    companyDetail: (companyId: string, invoiceId: string) =>
      client.fetch<Data<BuyerInvoiceDetail>>(`${company(companyId)}/${encodeURIComponent(invoiceId)}`, init).then((r) => r.data)
  };
}

export type InvoicesApi = ReturnType<typeof createInvoicesApi>;
