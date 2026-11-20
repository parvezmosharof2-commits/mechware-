import type { MechWareApiClient } from './index';
import type { DeskFilter, DeskPage, StaffQuoteDetail, StaffQuoteSummary, StaffRfqDetail, StaffRfqSummary } from './quotes-desk-model';
import { createQuoteBody, deskQuery } from './quotes-desk-model';
import type { QuoteDraftBody, QuotePricePreview } from './quote-editor-model';
import type { QuoteApprovalPage, QuoteApproveBody, QuoteSendResult } from './quote-approval-model';

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
      client.fetch<Data<QuotePricePreview>>(`${base}/quotes/${id(quoteId)}/versions/${Math.trunc(versionNo)}/price-preview`, { method: 'POST', body: JSON.stringify(body), cache: 'no-store' }),
    /** D4.8 (quotes.edit): sent, or pending_approval with the flags. A repeated click is 409 QUOTE_INVALID_TRANSITION. */
    sendVersion: (quoteId: string, versionNo: number) =>
      client.fetch<Data<QuoteSendResult>>(`${base}/quotes/${id(quoteId)}/versions/${Math.trunc(versionNo)}/send`, post()),
    /** quotes.approve; ALWAYS needs a fresh authenticator code (`X-Step-Up-Code`, purpose quote.approve): it is spent even when the approval is then refused. */
    approveVersion: (quoteId: string, versionNo: number, body: QuoteApproveBody, stepUpCode: string) =>
      client.fetch<Data<StaffQuoteDetail>>(`${base}/quotes/${id(quoteId)}/versions/${Math.trunc(versionNo)}/approve`, { ...post(body), headers: { 'X-Step-Up-Code': stepUpCode.trim() } }),
    /** quotes.approve, no code: the version goes back to its author as a draft. Comment 5-500. */
    rejectApproval: (quoteId: string, versionNo: number, comment: string) =>
      client.fetch<Data<StaffQuoteDetail>>(`${base}/quotes/${id(quoteId)}/versions/${Math.trunc(versionNo)}/reject-approval`, post({ comment: comment.trim() })),
    /** quotes.edit: takes a version waiting for approval back to a draft. */
    recallVersion: (quoteId: string, versionNo: number) =>
      client.fetch<Data<StaffQuoteDetail>>(`${base}/quotes/${id(quoteId)}/versions/${Math.trunc(versionNo)}/recall`, post()),
    /** quotes.edit: withdraws a Sent version. Reason 5-500. */
    withdrawVersion: (quoteId: string, versionNo: number, reason: string) =>
      client.fetch<Data<StaffQuoteDetail>>(`${base}/quotes/${id(quoteId)}/versions/${Math.trunc(versionNo)}/withdraw`, post({ reason: reason.trim() })),
    /** quotes.approve: versions waiting for approval, oldest first. */
    approvals: (page = 1, pageSize = 20) =>
      client.fetch<Data<QuoteApprovalPage>>(`${base}/quotes/approvals?page=${Math.max(1, Math.trunc(page))}&pageSize=${Math.max(1, Math.trunc(pageSize))}`, read)
  };
}

export type StaffQuotesApi = ReturnType<typeof createStaffQuotesApi>;
