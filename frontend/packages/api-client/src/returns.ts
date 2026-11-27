import type { MechWareApiClient } from './index';
import { returnListQuery, type BuyerReturn, type ReturnPage, type ReturnPhoto, type StaffReturn, type StaffReturnFilters, type buildReturnBody, type inspectBody } from './returns-model';

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
    withdraw: (returnId: string) => client.fetch<Data<BuyerReturn>>(`/api/v1/returns/${id(returnId)}/withdraw`, { method: 'POST' }).then((r) => r.data),
    photos: (returnId: string) => client.fetch<Data<ReturnPhoto[]>>(`/api/v1/returns/${id(returnId)}/photos`).then((r) => r.data),
    /** Multipart field `file`; `onProgress` gets 0..1. */
    uploadPhoto: (returnId: string, file: Blob, onProgress?: (fraction: number) => void) => {
      const form = new FormData();
      form.append('file', file);
      return client.upload<Data<ReturnPhoto>>(`/api/v1/returns/${id(returnId)}/photos`, form, onProgress).then((r) => r.data);
    },
    /** The stored JPEG bytes (the route needs the Bearer token, so an img src cannot reach it). */
    photoBytes: (returnId: string, photoId: string) => client.fetchBytes(`/api/v1/returns/${id(returnId)}/photos/${id(photoId)}`),
    removePhoto: (returnId: string, photoId: string) => client.fetch<unknown>(`/api/v1/returns/${id(returnId)}/photos/${id(photoId)}`, { method: 'DELETE' }).then(() => undefined)
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
    photos: (returnId: string) => client.fetch<Data<ReturnPhoto[]>>(`${base}/${id(returnId)}/photos`).then((r) => r.data),
    photoBytes: (returnId: string, photoId: string) => client.fetchBytes(`${base}/${id(returnId)}/photos/${id(photoId)}`),
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
