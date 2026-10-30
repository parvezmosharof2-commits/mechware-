import type { MechWareApiClient } from './index';
import type {
  ApprovalRequestDto,
  ApprovalRuleDto,
  ApprovalRuleRequest,
  DecisionRequest,
  SubmitApprovalRequest,
  SubmitApprovalResult
} from './approvals-model';

type Data<T> = { data: T };

/** Typed wrappers for /api/v1/companies/{companyId}/approvals. */
export function createApprovalsApi(client: MechWareApiClient) {
  const base = (companyId: string) => `/api/v1/companies/${encodeURIComponent(companyId)}/approvals`;
  const json = (method: string, body?: unknown): RequestInit => ({ method, body: body === undefined ? undefined : JSON.stringify(body) });
  const id = encodeURIComponent;

  return {
    listRules: (companyId: string) => client.fetch<Data<ApprovalRuleDto[]>>(`${base(companyId)}/rules`),
    addRule: (companyId: string, body: ApprovalRuleRequest) => client.fetch<Data<ApprovalRuleDto>>(`${base(companyId)}/rules`, json('POST', body)),
    updateRule: (companyId: string, ruleId: string, body: ApprovalRuleRequest) =>
      client.fetch<Data<ApprovalRuleDto>>(`${base(companyId)}/rules/${id(ruleId)}`, json('PUT', body)),
    removeRule: (companyId: string, ruleId: string) => client.fetch<unknown>(`${base(companyId)}/rules/${id(ruleId)}`, json('DELETE')),

    submit: (companyId: string, body: SubmitApprovalRequest) => client.fetch<Data<SubmitApprovalResult>>(`${base(companyId)}/requests`, json('POST', body)),
    listMine: (companyId: string) => client.fetch<Data<ApprovalRequestDto[]>>(`${base(companyId)}/requests/mine`),
    listInbox: (companyId: string) => client.fetch<Data<ApprovalRequestDto[]>>(`${base(companyId)}/requests/inbox`),
    get: (companyId: string, requestId: string) => client.fetch<Data<ApprovalRequestDto>>(`${base(companyId)}/requests/${id(requestId)}`),
    decide: (companyId: string, requestId: string, body: DecisionRequest) =>
      client.fetch<Data<ApprovalRequestDto>>(`${base(companyId)}/requests/${id(requestId)}/decision`, json('POST', body)),
    cancel: (companyId: string, requestId: string) =>
      client.fetch<Data<ApprovalRequestDto>>(`${base(companyId)}/requests/${id(requestId)}/cancel`, json('POST'))
  };
}

export type ApprovalsApi = ReturnType<typeof createApprovalsApi>;
