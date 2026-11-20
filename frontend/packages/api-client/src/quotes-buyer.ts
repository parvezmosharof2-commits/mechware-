import type { MechWareApiClient } from './index';
import type { AcceptPreview, BuyerQuoteDetail, BuyerQuotePage, QuoteChoice } from './quotes-buyer-model';
import { acceptBody, previewBody, quotePageQuery, quotePdfPath, rejectBody } from './quotes-buyer-model';
import type { PlacedOrder } from './checkout-model';

type Data<T> = { data: T };

/**
 * A company's own quotes (`/api/v1/companies/{id}/quotes`, D4.10). The answers are private (`private, no-store`) and every call here also
 * asks the browser not to store them. A 404 is one answer for a stranger, a colleague's quote, a draft and an unknown id.
 */
export function createBuyerQuotesApi(client: MechWareApiClient) {
  const base = (companyId: string) => `/api/v1/companies/${encodeURIComponent(companyId)}/quotes`;
  const one = (companyId: string, quoteId: string) => `${base(companyId)}/${encodeURIComponent(quoteId)}`;
  const read: RequestInit = { cache: 'no-store' };
  const post = (body: unknown, headers?: Record<string, string>): RequestInit => ({ method: 'POST', body: JSON.stringify(body), headers, cache: 'no-store' });
  return {
    list: (companyId: string, q: { status?: string; page?: number; pageSize?: number } = {}) =>
      client.fetch<Data<BuyerQuotePage>>(`${base(companyId)}?${quotePageQuery(q)}`, read).then((r) => r.data),
    get: (companyId: string, quoteId: string) => client.fetch<Data<BuyerQuoteDetail>>(one(companyId, quoteId), read).then((r) => r.data),
    byNumber: (number: string) =>
      client.fetch<Data<{ companyId: string; quoteId: string; number: string }>>(`/api/v1/quotes/by-number/${encodeURIComponent(number)}`, read).then((r) => r.data),
    acceptPreview: (companyId: string, quoteId: string, choice: QuoteChoice) =>
      client.fetch<Data<AcceptPreview>>(`${one(companyId, quoteId)}/accept-preview`, post(previewBody(choice))).then((r) => r.data),
    /** 201 with the order (and `quoteNumber`); 200 for a replay of the same key. `expectedGrandTotalMinor` is the previewed total. */
    accept: (companyId: string, quoteId: string, choice: QuoteChoice, previewedTotalMinor: number, idempotencyKey: string) =>
      client
        .fetch<Data<PlacedOrder & { quoteNumber?: string | null }>>(`${one(companyId, quoteId)}/accept`, post(acceptBody(choice, previewedTotalMinor), { 'Idempotency-Key': idempotencyKey }))
        .then((r) => r.data),
    reject: (companyId: string, quoteId: string, versionNo: number, reason: string) =>
      client.fetch<Data<BuyerQuoteDetail>>(`${one(companyId, quoteId)}/reject`, post(rejectBody(versionNo, reason))).then((r) => r.data),
    /** The PDF of a sent version as bytes (D4.11; rate-limited, hash re-checked by the server on every download). */
    pdf: (companyId: string, quoteId: string, versionNo: number, locale: 'en' | 'bn' = 'en') => client.fetchBytes(quotePdfPath(companyId, quoteId, versionNo, locale), read)
  };
}

export type BuyerQuotesApi = ReturnType<typeof createBuyerQuotesApi>;
