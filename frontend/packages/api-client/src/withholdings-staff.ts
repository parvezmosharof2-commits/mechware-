import type { MechWareApiClient } from './index';
import { WITHHOLDING_PAGE_SIZE, type TaxWithholding, type TaxWithholdingPage, type WithholdingBody, type WithholdingFilters } from './withholdings-staff-model';

type Data<T> = { data: T };

/** Staff withholding calls (E6.6b routes). Never cached. Record needs an Idempotency-Key; verify needs the authenticator code in `X-Step-Up-Code`. */
export function createStaffWithholdingsApi(client: MechWareApiClient) {
  const base = '/api/v1/admin';
  const read: RequestInit = { cache: 'no-store' };
  const post = (headers: Record<string, string>, body?: unknown): RequestInit => ({ method: 'POST', cache: 'no-store', body: body === undefined ? undefined : JSON.stringify(body), headers });
  const one = (id: string) => `${base}/withholdings/${encodeURIComponent(id)}`;
  return {
    record: (invoiceId: string, body: WithholdingBody, idempotencyKey: string) =>
      client.fetch<Data<TaxWithholding>>(`${base}/invoices/${encodeURIComponent(invoiceId)}/withholdings`, post({ 'Idempotency-Key': idempotencyKey }, body)).then((r) => r.data),
    list: (f: WithholdingFilters) => {
      const q = new URLSearchParams({ status: f.status, page: String(Math.max(1, f.page)), pageSize: String(WITHHOLDING_PAGE_SIZE) });
      if (f.kind) q.set('kind', f.kind);
      if (f.invoiceId) q.set('invoiceId', f.invoiceId);
      return client.fetch<Data<TaxWithholdingPage>>(`${base}/withholdings?${q.toString()}`, read).then((r) => r.data);
    },
    get: (id: string) => client.fetch<Data<TaxWithholding>>(one(id), read).then((r) => r.data),
    verify: (id: string, stepUpCode: string) => client.fetch<Data<TaxWithholding>>(`${one(id)}/verify`, post({ 'X-Step-Up-Code': stepUpCode })).then((r) => r.data),
    reject: (id: string, reason: string) => client.fetch<Data<TaxWithholding>>(`${one(id)}/reject`, post({}, { reason })).then((r) => r.data)
  };
}
export type StaffWithholdingsApi = ReturnType<typeof createStaffWithholdingsApi>;
