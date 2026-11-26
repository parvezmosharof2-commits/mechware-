import type { MechWareApiClient } from './index';
import { CREDIT_QUEUE_PAGE_SIZE, type CreditNoteApproval, type CreditNoteBody, type CreditNotePage, type CreditNoteRequest } from './credit-notes-staff-model';

type Data<T> = { data: T };

/** Staff credit-note calls (E6.5b routes). Never cached. Approve and reject need the authenticator code in `X-Step-Up-Code`; request needs an Idempotency-Key. */
export function createStaffCreditNotesApi(client: MechWareApiClient) {
  const base = '/api/v1/admin';
  const read: RequestInit = { cache: 'no-store' };
  const post = (headers: Record<string, string>, body?: unknown): RequestInit => ({ method: 'POST', cache: 'no-store', body: body === undefined ? undefined : JSON.stringify(body), headers });
  const one = (id: string) => `${base}/credit-note-requests/${encodeURIComponent(id)}`;
  return {
    request: (invoiceId: string, body: CreditNoteBody, idempotencyKey: string) =>
      client.fetch<Data<CreditNoteRequest>>(`${base}/invoices/${encodeURIComponent(invoiceId)}/credit-notes`, post({ 'Idempotency-Key': idempotencyKey }, body)).then((r) => r.data),
    queue: (page: number, status = 'requested') =>
      client.fetch<Data<CreditNotePage>>(`${base}/credit-note-requests?status=${encodeURIComponent(status)}&page=${Math.max(1, page)}&pageSize=${CREDIT_QUEUE_PAGE_SIZE}`, read).then((r) => r.data),
    get: (id: string) => client.fetch<Data<CreditNoteRequest>>(one(id), read).then((r) => r.data),
    approve: (id: string, stepUpCode: string) => client.fetch<Data<CreditNoteApproval>>(`${one(id)}/approve`, post({ 'X-Step-Up-Code': stepUpCode })).then((r) => r.data),
    reject: (id: string, reason: string, stepUpCode: string) => client.fetch<Data<CreditNoteRequest>>(`${one(id)}/reject`, post({ 'X-Step-Up-Code': stepUpCode }, { reason })).then((r) => r.data),
    withdraw: (id: string) => client.fetch<Data<CreditNoteRequest>>(`${one(id)}/withdraw`, post({})).then((r) => r.data)
  };
}
export type StaffCreditNotesApi = ReturnType<typeof createStaffCreditNotesApi>;
