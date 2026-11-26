'use client';

import * as React from 'react';
import { Button } from '@mechware/ui/src/components/Button';
import { challanFileName, invoicePdfFileName, staffChallanErrorText, staffInvoiceErrorText, type InvoiceLocale } from '@mechware/api-client';
import { problemCode, problemStatus, staffInvoicesApi } from '../lib/api';
import { saveFile } from '../lib/download';

/** Words for a failed invoice call; the tamper case (503 with the integrity code) is its own sentence. */
export const invoiceFailure = (err: unknown): string => staffInvoiceErrorText({ status: problemStatus(err), code: problemCode(err) });

/** Downloads one invoice PDF with the staff session and saves it; a refusal is shown in words (`data-testid="invoice-pdf-refusal"`). */
export function useInvoicePdf() {
  const [busy, setBusy] = React.useState<string | null>(null);
  const [refusal, setRefusal] = React.useState<string | null>(null);
  const download = async (invoiceId: string, number: string, locale: InvoiceLocale) => {
    setBusy(`${invoiceId}:${locale}`); setRefusal(null);
    try {
      saveFile(invoicePdfFileName(number, locale), await staffInvoicesApi.pdf(invoiceId, locale), 'application/pdf');
      return true;
    } catch (err) {
      setRefusal(invoiceFailure(err));
      return false;
    } finally { setBusy(null); }
  };
  const alert = refusal && (
    <p role="alert" data-testid="invoice-pdf-refusal" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{refusal}</p>
  );
  return { busy, download, alert, setFailure: setRefusal };
}

/** English and Bangla download buttons of one invoice. */
export function InvoicePdfButtons({ invoiceId, number }: { invoiceId: string; number: string }) {
  const pdf = useInvoicePdf();
  return (
    <div className="space-y-2">
      <div className="flex flex-wrap gap-2">
        <Button type="button" variant="outline" data-testid="invoice-pdf-en" disabled={pdf.busy !== null} onClick={() => void pdf.download(invoiceId, number, 'en')}>Download PDF (English)</Button>
        <Button type="button" variant="outline" data-testid="invoice-pdf-bn" disabled={pdf.busy !== null} onClick={() => void pdf.download(invoiceId, number, 'bn')}>Download PDF (Bangla)</Button>
      </div>
      {pdf.alert}
    </div>
  );
}

/** On the order: "Print Mushak-6.3" finds the order's tax invoice(s) and downloads the PDF. Needs `invoices.view` and at least one invoice number. */
export function PrintMushak({ orderId, orderNumber }: { orderId: string; orderNumber: string }) {
  const pdf = useInvoicePdf();
  const [none, setNone] = React.useState(false);
  const print = async (locale: InvoiceLocale) => {
    setNone(false);
    try {
      const page = await staffInvoicesApi.list({ number: '', orderNumber, companyId: '', companyName: '', from: '', to: '', kind: 'tax_invoice', mode: '', page: 1 });
      const mine = page.items.filter((i) => i.invoice.orderId === orderId);
      if (mine.length === 0) { setNone(true); return; }
      for (const i of mine) if (!(await pdf.download(i.invoice.id, i.invoice.number, locale))) return;
    } catch (err) { pdf.setFailure(invoiceFailure(err)); }
  };
  return (
    <div className="space-y-2" data-testid="print-mushak">
      <div className="flex flex-wrap gap-2">
        <Button type="button" data-testid="print-mushak-en" disabled={pdf.busy !== null} onClick={() => void print('en')}>Print Mushak-6.3 (English)</Button>
        <Button type="button" variant="outline" data-testid="print-mushak-bn" disabled={pdf.busy !== null} onClick={() => void print('bn')}>Print Mushak-6.3 (Bangla)</Button>
      </div>
      {none && <p role="status" className="text-sm text-text-muted">No tax invoice was found for this order.</p>}
      {pdf.alert}
    </div>
  );
}

/** On a shipped order: "Print challan" (no prices), English or Bangla. Needs `orders.fulfil` or `orders.view_all`; the caller gates it. */
export function PrintChallan({ orderId, orderNumber }: { orderId: string; orderNumber: string }) {
  const pdf = useInvoicePdf();
  const print = async (locale: InvoiceLocale) => {
    pdf.setFailure(null);
    try {
      saveFile(challanFileName(orderNumber, locale), await staffInvoicesApi.challan(orderId, locale), 'application/pdf');
    } catch (err) { pdf.setFailure(staffChallanErrorText({ status: problemStatus(err), code: problemCode(err) })); }
  };
  return (
    <div className="space-y-2" data-testid="print-challan">
      <div className="flex flex-wrap gap-2">
        <Button type="button" data-testid="print-challan-en" disabled={pdf.busy !== null} onClick={() => void print('en')}>Print challan (English)</Button>
        <Button type="button" variant="outline" data-testid="print-challan-bn" disabled={pdf.busy !== null} onClick={() => void print('bn')}>Print challan (Bangla)</Button>
      </div>
      {pdf.alert}
    </div>
  );
}
