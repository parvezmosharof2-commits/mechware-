import type { MechWareApiClient } from './index';
import type {
  ApproveBody,
  CreatePriceListBody,
  PriceListDetail,
  PriceListFilters,
  PriceListPage,
  UpdatePriceListHeaderBody,
  UpsertEntryItem
} from './price-lists-model';
import { PRICE_LIST_MAX_BATCH, priceListQuery } from './price-lists-model';

type Data<T> = { data: T };

/** Staff price lists (`/api/v1/admin/price-lists`, D2.7). Decisions: D2.8 API, D2.12a screens. */
export function createPriceListsApi(client: MechWareApiClient) {
  const base = '/api/v1/admin/price-lists';
  const id = (v: string) => encodeURIComponent(v);
  const json = (method: string, body?: unknown): RequestInit => ({ method, body: body === undefined ? undefined : JSON.stringify(body) });
  return {
    list: (filters: PriceListFilters = {}) => client.fetch<Data<PriceListPage>>(`${base}?${priceListQuery(filters)}`),
    get: (listId: string) => client.fetch<Data<PriceListDetail>>(`${base}/${id(listId)}`),
    create: (body: CreatePriceListBody) => client.fetch<Data<PriceListDetail>>(base, json('POST', body)),
    updateHeader: (listId: string, body: UpdatePriceListHeaderBody) => client.fetch<Data<PriceListDetail>>(`${base}/${id(listId)}`, json('PUT', body)),
    /** Upsert by (variant, minQty); the server takes 1..500 per request and saves nothing if one is bad. */
    upsertEntries: (listId: string, entries: UpsertEntryItem[]) => {
      if (entries.length < 1 || entries.length > PRICE_LIST_MAX_BATCH) throw new RangeError(`A batch holds 1 to ${PRICE_LIST_MAX_BATCH} entries.`);
      return client.fetch<Data<PriceListDetail>>(`${base}/${id(listId)}/entries`, json('PUT', { entries }));
    },
    deleteEntry: (listId: string, entryId: string) => client.fetch<Data<PriceListDetail>>(`${base}/${id(listId)}/entries/${id(entryId)}`, json('DELETE')),
    submit: (listId: string) => client.fetch<Data<PriceListDetail>>(`${base}/${id(listId)}/submit`, json('POST')),
    revise: (listId: string) => client.fetch<Data<PriceListDetail>>(`${base}/${id(listId)}/revise`, json('POST')),
    /** Needs a fresh authenticator code (`X-Step-Up-Code`, purpose `pricelist.approve`); it is spent even when the approval is then refused. */
    approve: (listId: string, body: ApproveBody, stepUpCode: string) =>
      client.fetch<Data<PriceListDetail>>(`${base}/${id(listId)}/approve`, { ...json('POST', body), headers: { 'X-Step-Up-Code': stepUpCode.trim() } }),
    /** No step-up: a rejection never makes a price apply. */
    reject: (listId: string, reason: string) => client.fetch<Data<PriceListDetail>>(`${base}/${id(listId)}/reject`, json('POST', { reason: reason.trim() })),
    /** Purpose `pricelist.withdraw`. */
    withdraw: (listId: string, reason: string, stepUpCode: string) =>
      client.fetch<Data<PriceListDetail>>(`${base}/${id(listId)}/withdraw`, { ...json('POST', { reason: reason.trim() }), headers: { 'X-Step-Up-Code': stepUpCode.trim() } }),
    deleteDraft: (listId: string) => client.fetch<unknown>(`${base}/${id(listId)}`, json('DELETE'))
  };
}

export type PriceListsApi = ReturnType<typeof createPriceListsApi>;
