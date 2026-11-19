import type { MechWareApiClient } from './index';
import type { CreateRfqInput, RfqDetail, RfqPageDto } from './rfq-buyer-model';
import { buildRfqBody, rfqPageQuery } from './rfq-buyer-model';

type Data<T> = { data: T };

/**
 * A company's own requests for quote (`/api/v1/companies/{id}/rfqs`, D4.5). The answers are private: the API sends `private, no-store`
 * and every call here also asks the browser not to store them. Create carries one Idempotency-Key per distinct body.
 * A 404 is the same for a stranger, a non-member and a colleague's request (see `classifyRfqError`).
 */
export function createBuyerRfqApi(client: MechWareApiClient) {
  const base = (companyId: string) => `/api/v1/companies/${encodeURIComponent(companyId)}/rfqs`;
  const read: RequestInit = { cache: 'no-store' };
  return {
    create: (companyId: string, input: CreateRfqInput, idempotencyKey: string) =>
      client
        .fetch<Data<RfqDetail>>(base(companyId), { method: 'POST', body: JSON.stringify(buildRfqBody(input)), headers: { 'Idempotency-Key': idempotencyKey }, cache: 'no-store' })
        .then((r) => r.data),
    list: (companyId: string, q: { status?: string; page?: number; pageSize?: number } = {}) =>
      client.fetch<Data<RfqPageDto>>(`${base(companyId)}?${rfqPageQuery(q)}`, read).then((r) => r.data),
    get: (companyId: string, rfqId: string) => client.fetch<Data<RfqDetail>>(`${base(companyId)}/${encodeURIComponent(rfqId)}`, read).then((r) => r.data),
    withdraw: (companyId: string, rfqId: string) =>
      client.fetch<Data<RfqDetail>>(`${base(companyId)}/${encodeURIComponent(rfqId)}/withdraw`, { method: 'POST', cache: 'no-store' }).then((r) => r.data)
  };
}

export type BuyerRfqApi = ReturnType<typeof createBuyerRfqApi>;
