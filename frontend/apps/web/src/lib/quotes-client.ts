import { createBuyerQuotesApi } from '@mechware/api-client';
import { api } from './api';

/** Browser-only: the Bearer token belongs to this origin. Every call is no-store (private to the signed-in member). */
export const buyerQuotesApi = createBuyerQuotesApi(api);

/**
 * The PDF download button waits for the D4.11 route (`GET /companies/{id}/quotes/{quoteId}/versions/{n}/pdf?locale=en|bn`, not in this schema.d.ts yet).
 * Set NEXT_PUBLIC_QUOTE_PDF=1 when the API has it; until then the button is not rendered.
 */
export const QUOTE_PDF_ENABLED = process.env.NEXT_PUBLIC_QUOTE_PDF === '1';

/** Fetches the PDF of one sent version through the API client (Bearer token) and hands it to the browser as a file. */
export async function downloadQuotePdf(companyId: string, quoteId: string, versionNo: number, locale: 'en' | 'bn', fileName: string): Promise<void> {
  const bytes = await buyerQuotesApi.pdf(companyId, quoteId, versionNo, locale);
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
