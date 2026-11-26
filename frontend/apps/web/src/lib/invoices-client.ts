import { challanFileName, createInvoicesApi, invoicePdfFileName, type InvoiceLocale } from '@mechware/api-client';
import { api } from './api';

/** Browser-only: the Bearer token belongs to this origin. Every call is no-store (private to the signed-in buyer). */
export const invoicesApi = createInvoicesApi(api);

function saveBytes(bytes: ArrayBuffer, fileName: string): void {
  const url = URL.createObjectURL(new Blob([bytes], { type: 'application/pdf' }));
  try {
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    a.remove();
  } finally {
    setTimeout(() => URL.revokeObjectURL(url), 10_000);
  }
}

/** Fetches one invoice PDF (Bearer token) and hands it to the browser as a file. With `companyId` it uses the company route (any company Admin/Buyer); without, the placer's order route. */
export async function downloadInvoicePdf(orderId: string, invoiceId: string, number: string, locale: InvoiceLocale, companyId?: string): Promise<void> {
  const bytes = companyId ? await invoicesApi.companyPdf(companyId, invoiceId, locale) : await invoicesApi.pdf(orderId, invoiceId, locale);
  saveBytes(bytes, invoicePdfFileName(number, locale));
}

/** The delivery challan of a shipped order, in the chosen language. */
export async function downloadChallan(orderId: string, orderNumber: string, locale: InvoiceLocale): Promise<void> {
  saveBytes(await invoicesApi.challan(orderId, locale), challanFileName(orderNumber, locale));
}
