import type { MechWareApiClient } from './index';
import type { QuoteTerms, QuoteTermsBody } from './quote-terms-model';

/** Default quote terms (`/api/v1/admin/quote-terms`, D4.20). Read needs quotes.view, save quotes.approve; 409 QUOTE_TERMS_CHANGED when `rowVersion` is old. */
export function createQuoteTermsApi(client: MechWareApiClient) {
  const url = '/api/v1/admin/quote-terms';
  return {
    get: () => client.fetch<{ data: QuoteTerms }>(url, { cache: 'no-store' }),
    save: (body: QuoteTermsBody) => client.fetch<{ data: QuoteTerms }>(url, { method: 'PUT', body: JSON.stringify(body) })
  };
}
