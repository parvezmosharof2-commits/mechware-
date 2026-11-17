import type { MechWareApiClient } from './index';
import type { BuyerCredit, BuyerOpenItem, BuyerPage, BuyerStatement } from './credit-buyer-model';
import { buyerPageQuery, buyerStatementQuery } from './credit-buyer-model';

type Data<T> = { data: T };

/**
 * A company's own credit (`/api/v1/companies/{id}/credit...`, B3.9). Read-only. The answers are private to the signed-in member: the API
 * sends `Cache-Control: private, no-store`, and every call here also asks the browser not to store or reuse them (`cache: 'no-store'`).
 * A 404 means "not found" for a stranger and for a member whose role may not see the balance (same body); see `classifyCreditError`.
 */
export function createBuyerCreditApi(client: MechWareApiClient) {
  const base = (companyId: string) => `/api/v1/companies/${encodeURIComponent(companyId)}/credit`;
  const init: RequestInit = { cache: 'no-store' };
  return {
    get: (companyId: string) => client.fetch<Data<BuyerCredit>>(base(companyId), init).then((r) => r.data),
    openItems: (companyId: string, page = 1, pageSize = 10) =>
      client.fetch<Data<BuyerPage<BuyerOpenItem>>>(`${base(companyId)}/open-items?${buyerPageQuery({ page, pageSize })}`, init).then((r) => r.data),
    statement: (companyId: string, q: { from?: string; to?: string; page?: number; pageSize?: number } = {}) =>
      client.fetch<Data<BuyerStatement>>(`${base(companyId)}/statement?${buyerStatementQuery(q)}`, init).then((r) => r.data)
  };
}

export type BuyerCreditApi = ReturnType<typeof createBuyerCreditApi>;
