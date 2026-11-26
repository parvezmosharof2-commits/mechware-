import { createInvoicesApi, invoicePdfFileName, type InvoiceLocale } from '@mechware/api-client';
import { api } from './api';

/** Browser-only: the Bearer token belongs to this origin. Every call is no-store (private to the signed-in buyer). */
export const invoicesApi = createInvoicesApi(api);

/** Fetches one invoice PDF through the API client (Bearer token) and hands it to the browser as a file. The language is chosen by the buyer. */
export async function downloadInvoicePdf(orderId: string, invoiceId: string, number: string, locale: InvoiceLocale): Promise<void> {
  const bytes = await invoicesApi.pdf(orderId, invoiceId, locale);
  const url = URL.createObjectURL(new Blob([bytes], { type: 'application/pdf' }));
  try {
    const a = document.createElement('a');
    a.href = url;
    a.download = invoicePdfFileName(number, locale);
    document.body.appendChild(a);
    a.click();
    a.remove();
  } finally {
    setTimeout(() => URL.revokeObjectURL(url), 10_000);
  }
}
