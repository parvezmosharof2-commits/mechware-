import type { MechWareApiClient } from './index';
import type {
  AdjustmentBody, AdjustmentQueueStatus, AppliedOnAccount, ApplyBody, ApprovedAdjustment, CreditAdjustment, CreditAdjustmentDetail,
  CreditAdjustmentListItem, CreditAgingReport, CreditHold, CreditStatement
} from './credit-books-model';
import { adjustmentsQuery, agingQuery, statementCsvQuery, statementQuery, type AgingFilter, type StatementFilter } from './credit-books-model';
import type { CreditPage } from './credit-payments-model';

type Data<T> = { data: T };

/**
 * The staff credit books (`/api/v1/admin`, B3.5b, B3.6, B3.7, B3.8): aging, statement, apply on-account, adjustments, hold override.
 * Reads need credit.view. Step-up codes go in `X-Step-Up-Code`: the all-company aging CSV, approving an adjustment and a hold
 * override always need one (a code is spent even when the call is refused); the statement CSV, requests, rejections and applies do not.
 * Idempotency-Key is 8-100 letters, digits, - or _. Response types are hand-written from the B3.5b-B3.8 HANDOFFs.
 */
export function createCreditBooksApi(client: MechWareApiClient) {
  const base = '/api/v1/admin';
  const id = (v: string) => encodeURIComponent(v);
  const stepUp = (code: string) => ({ 'X-Step-Up-Code': code.trim() });
  return {
    aging: (f: AgingFilter) => client.fetch<Data<CreditAgingReport>>(`${base}/credit/aging?${agingQuery(f)}`),
    /** The whole file as bytes (starts with a UTF-8 byte-order mark). Audited and rate-limited (6 a minute). */
    agingCsv: (overdueOnly: boolean, stepUpCode: string) =>
      client.fetchBytes(`${base}/credit/aging.csv?overdueOnly=${overdueOnly ? 'true' : 'false'}`, { headers: stepUp(stepUpCode) }),
    statement: (companyId: string, f: StatementFilter, pageSize?: number) =>
      client.fetch<Data<CreditStatement>>(`${base}/companies/${id(companyId)}/credit/statement?${statementQuery(f, pageSize)}`),
    statementCsv: (companyId: string, f: { from: string; to: string }) =>
      client.fetchBytes(`${base}/companies/${id(companyId)}/credit/statement.csv?${statementCsvQuery(f)}`),
    /** Always 200; `replayed` is true when the same key and body were already applied. */
    applyOnAccount: (companyId: string, body: ApplyBody, idempotencyKey: string) =>
      client.fetch<Data<AppliedOnAccount>>(`${base}/companies/${id(companyId)}/credit/apply-on-account`, {
        method: 'POST', body: JSON.stringify(body), headers: { 'Idempotency-Key': idempotencyKey }
      }),
    adjustments: (q: { status?: AdjustmentQueueStatus; companyId?: string; page?: number; pageSize?: number } = {}) =>
      client.fetch<Data<CreditPage<CreditAdjustmentListItem>>>(`${base}/credit/adjustments?${adjustmentsQuery(q)}`),
    adjustment: (adjustmentId: string) => client.fetch<Data<CreditAdjustmentDetail>>(`${base}/credit/adjustments/${id(adjustmentId)}`),
    /** 201, or 200 for a replay of the same key and body. Moves no money. */
    requestAdjustment: (companyId: string, body: AdjustmentBody, idempotencyKey: string) =>
      client.fetch<Data<CreditAdjustment>>(`${base}/companies/${id(companyId)}/credit/adjustments`, {
        method: 'POST', body: JSON.stringify(body), headers: { 'Idempotency-Key': idempotencyKey }
      }),
    approveAdjustment: (adjustmentId: string, stepUpCode: string) =>
      client.fetch<Data<ApprovedAdjustment>>(`${base}/credit/adjustments/${id(adjustmentId)}/approve`, { method: 'POST', headers: stepUp(stepUpCode) }),
    /** The requester may do this too (a withdrawal). No code. */
    rejectAdjustment: (adjustmentId: string, reason: string) =>
      client.fetch<Data<CreditAdjustment>>(`${base}/credit/adjustments/${id(adjustmentId)}/reject`, { method: 'POST', body: JSON.stringify({ reason }) }),
    /** `days` 0 ends a lift, 1 to 7 lifts the hold for that long. Admin only, always with a code. */
    holdOverride: (companyId: string, body: { days: number; reason: string }, stepUpCode: string) =>
      client.fetch<Data<CreditHold>>(`${base}/companies/${id(companyId)}/credit/hold-override`, { method: 'PUT', body: JSON.stringify(body), headers: stepUp(stepUpCode) })
  };
}

export type CreditBooksApi = ReturnType<typeof createCreditBooksApi>;
