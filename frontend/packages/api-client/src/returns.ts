import type { MechWareApiClient } from './index';
import { returnListQuery, type BuyerReturn, type ReturnPage, type StaffReturn, type StaffReturnFilters, type buildReturnBody, type inspectBody } from './returns-model';

type Data<T> = { data: T };

/** The buyer's own returns. `open` needs an `Idempotency-Key` (keep it for a retry after a network or 5xx error); a company order also carries `companyId` in the body. */
export function createReturnsApi(client: MechWareApiClient) {
  const id = (v: string) => encodeURIComponent(v);
  return {
    open: (orderNumber: string, body: ReturnType<typeof buildReturnBody>, idempotencyKey: string) =>
      client.fetch<Data<BuyerReturn>>(`/api/v1/orders/${id(orderNumber)}/returns`, { method: 'POST', headers: { 'Idempotency-Key': idempotencyKey }, body: JSON.stringify(body) }).then((r) => r.data),
    list: (page = 1, pageSize = 20) =>
      client.fetch<Data<ReturnPage<BuyerReturn>>>(`/api/v1/returns?page=${Math.max(1, Math.floor(page))}&pageSize=${Math.min(50, Math.max(1, Math.floor(pageSize)))}`).then((r) => r.data),
    get: (returnId: string) => client.fetch<Data<BuyerReturn>>(`/api/v1/returns/${id(returnId)}`).then((r) => r.data),
    withdraw: (returnId: string) => client.fetch<Data<BuyerReturn>>(`/api/v1/returns/${id(returnId)}/withdraw`, { method: 'POST' }).then((r) => r.data)
  };
}
export type ReturnsApi = ReturnType<typeof createReturnsApi>;

/** Staff return desk: reads need orders.view_all, every move needs returns.manage and the rowVersion of the last read. */
export function createStaffReturnsApi(client: MechWareApiClient) {
  const base = '/api/v1/admin/returns';
  const id = (v: string) => encodeURIComponent(v);
  const move = (returnId: string, step: string, body: object) =>
    client.fetch<Data<StaffReturn>>(`${base}/${id(returnId)}/${step}`, { method: 'POST', body: JSON.stringify(body) });
  return {
    list: (f: StaffReturnFilters) => client.fetch<Data<ReturnPage<StaffReturn>>>(`${base}?${returnListQuery(f)}`),
    get: (returnId: string) => client.fetch<Data<StaffReturn>>(`${base}/${id(returnId)}`),
    approve: (returnId: string, rowVersion: number) => move(returnId, 'approve', { rowVersion }),
    reject: (returnId: string, rowVersion: number, reason: string) => move(returnId, 'reject', { rowVersion, reason }),
    received: (returnId: string, rowVersion: number) => move(returnId, 'received', { rowVersion }),
    inspect: (returnId: string, body: ReturnType<typeof inspectBody>) => move(returnId, 'inspect', body),
    /** Reject a pending credit reduction (`payments.refund_approve`, second person, authenticator code). Answers the return, back at Inspected. */
    rejectMoneyBack: (returnId: string, reason: string, stepUpCode: string) =>
      client.fetch<Data<StaffReturn>>(`${base}/${id(returnId)}/money-back/reject`, { method: 'POST', headers: { 'X-Step-Up-Code': stepUpCode }, body: JSON.stringify({ reason }) })
  };
}
export type StaffReturnsApi = ReturnType<typeof createStaffReturnsApi>;
