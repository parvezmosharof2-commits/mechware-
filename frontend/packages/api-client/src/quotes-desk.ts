import type { MechWareApiClient } from './index';
import type { DeskFilter, DeskPage, StaffQuoteDetail, StaffQuoteSummary, StaffRfqDetail, StaffRfqSummary } from './quotes-desk-model';
import { createQuoteBody, deskQuery } from './quotes-desk-model';
import type { QuoteDraftBody, QuotePricePreview } from './quote-editor-model';

type Data<T> = { data: T };

/**
 * The staff quote desk (`/api/v1/admin`, D4.6). Reads need quotes.view, every command quotes.edit; line cost figures arrive only for
 * people who also hold pricing.view_cost. Every answer is private and never cached, so every read here says `no-store` too.
 * No command takes an authenticator code. `POST quotes` has no Idempotency-Key (D4.6 interpretation 6): the screen guards double submits.
 */
export function createStaffQuotesApi(client: MechWareApiClient) {
  const base = '/api/v1/admin';
  const id = (v: string) => encodeURIComponent(v);
  const read = { cache: 'no-store' } as const;
  const post = (body?: unknown) => ({ method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) }) as const;
  return {
    rfqs: (f: DeskFilter = {}) => client.fetch<Data<DeskPage<StaffRfqSummary>>>(`${base}/rfqs?${deskQuery(f)}`, read),
    rfq: (rfqId: string) => client.fetch<Data<StaffRfqDetail>>(`${base}/rfqs/${id(rfqId)}`, read),
    assignRfq: (rfqId: string, assigneeId: string | null) => client.fetch<Data<StaffRfqDetail>>(`${base}/rfqs/${id(rfqId)}/assign`, post({ assigneeId })),
    closeRfq: (rfqId: string, reason: string) => client.fetch<Data<StaffRfqDetail>>(`${base}/rfqs/${id(rfqId)}/close`, post({ reason })),
    quotes: (f: DeskFilter = {}) => client.fetch<Data<DeskPage<StaffQuoteSummary>>>(`${base}/quotes?${deskQuery(f)}`, read),
    quote: (quoteId: string) => client.fetch<Data<StaffQuoteDetail>>(`${base}/quotes/${id(quoteId)}`, read),
    createQuote: (input: { rfqId: string } | { companyId: string; contactUserId: string }) =>
      client.fetch<Data<StaffQuoteDetail>>(`${base}/quotes`, post(createQuoteBody(input))),
    reviseQuote: (quoteId: string) => client.fetch<Data<StaffQuoteDetail>>(`${base}/quotes/${id(quoteId)}/revise`, post()),
    closeQuote: (quoteId: string, reason: string) => client.fetch<Data<StaffQuoteDetail>>(`${base}/quotes/${id(quoteId)}/close`, post({ reason })),
    /** D4.7: replaces the whole Draft; `body.rowVersion` is required, a stale one is 409 QUOTE_VERSION_CHANGED. */
    saveDraft: (quoteId: string, versionNo: number, body: QuoteDraftBody) =>
      client.fetch<Data<StaffQuoteDetail>>(`${base}/quotes/${id(quoteId)}/versions/${Math.trunc(versionNo)}`, { method: 'PUT', body: JSON.stringify(body) }),
    /** D4.7: the same body, nothing saved. Cost and floor amounts arrive only with pricing.view_cost. */
    previewDraft: (quoteId: string, versionNo: number, body: QuoteDraftBody) =>
      client.fetch<Data<QuotePricePreview>>(`${base}/quotes/${id(quoteId)}/versions/${Math.trunc(versionNo)}/price-preview`, { method: 'POST', body: JSON.stringify(body), cache: 'no-store' })
  };
}

export type StaffQuotesApi = ReturnType<typeof createStaffQuotesApi>;
