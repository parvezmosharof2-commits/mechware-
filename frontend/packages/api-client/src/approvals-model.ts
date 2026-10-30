/**
 * Approval rules and requests (brief v2 section 8.3): types and pure helpers. No runtime imports, so it can be
 * unit-tested directly. Field names mirror the API DTOs in ShopVerse.Application.Approvals.
 */

export type ApprovalStatus = 'pending' | 'approved' | 'rejected' | 'cancelled';
export type StepDecisionName = 'pending' | 'approved' | 'rejected';

export const MAX_CHAIN_LENGTH = 5;
export const MAX_COMMENT_LENGTH = 500;

export interface ApprovalRuleRequest {
  name: string;
  minAmountMinor: number;
  costCentreId?: string | null;
  categoryIds?: string[];
  approverUserIds: string[];
  isActive: boolean;
}

export interface ApprovalRuleDto {
  id: string;
  name: string;
  minAmountMinor: number;
  costCentreId: string | null;
  categoryIds: string[];
  approverUserIds: string[];
  isActive: boolean;
}

export interface SubmitApprovalRequest {
  subjectType: string;
  subjectId: string;
  amountMinor: number;
  costCentreId?: string | null;
  categoryIds?: string[];
}

export interface ApprovalStepDto {
  position: number;
  approverUserId: string;
  decision: StepDecisionName;
  comment: string | null;
  decidedAtUtc: string | null;
}

export interface ApprovalRequestDto {
  id: string;
  requesterUserId: string;
  subjectType: string;
  subjectId: string;
  amountMinor: number;
  costCentreId: string | null;
  categoryIds: string[];
  ruleId: string;
  ruleName: string;
  status: ApprovalStatus;
  currentApproverUserId: string | null;
  steps: ApprovalStepDto[];
  createdAtUtc: string;
}

export interface SubmitApprovalResult {
  approvalRequired: boolean;
  request: ApprovalRequestDto | null;
}

export interface DecisionRequest {
  approve: boolean;
  comment?: string | null;
}

export type RuleFormError =
  | 'name.required'
  | 'name.length'
  | 'amount.format'
  | 'chain.empty'
  | 'chain.tooLong'
  | 'chain.duplicate';

/** Same rules as the API's ApprovalRule.Update. Returns error codes (not text) so each UI localises them. */
export function validateRuleForm(input: { name: string; minAmountText: string; approverUserIds: readonly string[] }):
  Partial<Record<'name' | 'minAmount' | 'approverUserIds', RuleFormError>> {
  const errors: Partial<Record<'name' | 'minAmount' | 'approverUserIds', RuleFormError>> = {};
  const name = input.name.trim();
  if (name.length === 0) errors.name = 'name.required';
  else if (name.length > 100) errors.name = 'name.length';

  const raw = input.minAmountText.replace(/[,\s]/g, '').replace(/[০-৯]/g, (d) => String('০১২৩৪৫৬৭৮৯'.indexOf(d)));
  const amount = raw === '' ? 0 : Number(raw);
  if (!Number.isFinite(amount) || amount < 0) errors.minAmount = 'amount.format';

  const chain = input.approverUserIds;
  if (chain.length === 0) errors.approverUserIds = 'chain.empty';
  else if (chain.length > MAX_CHAIN_LENGTH) errors.approverUserIds = 'chain.tooLong';
  else if (new Set(chain).size !== chain.length) errors.approverUserIds = 'chain.duplicate';
  return errors;
}

/** Whole minor units from a typed major amount (BDT), or null when it is not a valid non-negative number. */
export function parseAmountToMinor(text: string): number | null {
  const raw = text.replace(/[,\s]/g, '').replace(/[০-৯]/g, (d) => String('০১২৩৪৫৬৭৮৯'.indexOf(d)));
  if (raw === '') return null;
  const n = Number(raw);
  if (!Number.isFinite(n) || n < 0) return null;
  return Math.round((n + Number.EPSILON) * 100);
}

export function isFinished(status: ApprovalStatus): boolean {
  return status !== 'pending';
}

/** Why the caller can or cannot decide on a request right now. */
export type DecideState = 'can_decide' | 'own_request' | 'not_your_turn' | 'finished';

export function decideState(request: Pick<ApprovalRequestDto, 'status' | 'requesterUserId' | 'currentApproverUserId'>, userId: string): DecideState {
  if (request.status !== 'pending') return 'finished';
  if (request.requesterUserId === userId) return 'own_request';
  return request.currentApproverUserId === userId ? 'can_decide' : 'not_your_turn';
}

/** Rejecting needs a comment; every comment is limited. */
export function validateDecision(approve: boolean, comment: string): 'comment.required' | 'comment.tooLong' | null {
  const c = comment.trim();
  if (!approve && c.length === 0) return 'comment.required';
  if (c.length > MAX_COMMENT_LENGTH) return 'comment.tooLong';
  return null;
}

/** First 8 characters of an id, for places that have no name to show. */
export function shortId(id: string): string {
  return id.slice(0, 8);
}
