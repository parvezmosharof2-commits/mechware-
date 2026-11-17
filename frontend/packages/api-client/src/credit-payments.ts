import type { MechWareApiClient } from './index';
import type { CreditPage, CreditPayment, CreditPaymentDetail, CreditPaymentListItem, CreditQueueStatus, RecordCreditPaymentBody } from './credit-payments-model';
import { paymentsQuery } from './credit-payments-model';

type Data<T> = { data: T };

/**
 * Staff credit payments (`/api/v1/admin`, B3.4 + B3.8). Reads need credit.view; record needs credit.record_payment and an
 * Idempotency-Key (8-100 letters, digits, - or _); verify, reject and bounce need credit.verify_payment. Verify needs an
 * authenticator code in `X-Step-Up-Code` (always by default), bounce always does, reject never. A code is spent even when the call
 * is then refused. Response types are hand-written from the B3.4/B3.8 HANDOFFs (the OpenAPI document has no response bodies).
 */
export function createCreditPaymentsApi(client: MechWareApiClient) {
  const base = '/api/v1/admin';
  const id = (v: string) => encodeURIComponent(v);
  const post = (path: string, body: unknown, headers?: Record<string, string>) =>
    client.fetch<Data<CreditPayment>>(`${base}${path}`, { method: 'POST', body: JSON.stringify(body), headers });
  const stepUp = (code: string) => ({ 'X-Step-Up-Code': code.trim() });
  return {
    list: (q: { status?: CreditQueueStatus; companyId?: string; page?: number; pageSize?: number } = {}) =>
      client.fetch<Data<CreditPage<CreditPaymentListItem>>>(`${base}/credit/payments?${paymentsQuery(q)}`),
    get: (paymentId: string) => client.fetch<Data<CreditPaymentDetail>>(`${base}/credit/payments/${id(paymentId)}`),
    /** 201 with the payment, or 200 for a replay of the same key and body. */
    record: (companyId: string, body: RecordCreditPaymentBody, idempotencyKey: string) =>
      post(`/companies/${id(companyId)}/credit/payments`, body, { 'Idempotency-Key': idempotencyKey }),
    /** The response carries `payment` plus what was released; the screens reload the payment instead of reading it. */
    verify: (paymentId: string, clearedOn: string, stepUpCode?: string) =>
      client.fetch<Data<{ payment: CreditPayment }>>(`${base}/credit/payments/${id(paymentId)}/verify`, {
        method: 'POST', body: JSON.stringify({ clearedOn }), headers: stepUpCode ? stepUp(stepUpCode) : undefined
      }),
    reject: (paymentId: string, reason: string) => post(`/credit/payments/${id(paymentId)}/reject`, { reason }),
    bounce: (paymentId: string, reason: string, stepUpCode: string) =>
      client.fetch<Data<{ payment: CreditPayment }>>(`${base}/credit/payments/${id(paymentId)}/bounce`, {
        method: 'POST', body: JSON.stringify({ reason }), headers: stepUp(stepUpCode)
      })
  };
}

export type CreditPaymentsApi = ReturnType<typeof createCreditPaymentsApi>;
