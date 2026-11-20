/**
 * Quote send and approval (D4.17): pure helpers for the console. No runtime imports (Node's type stripping must be able to test the file);
 * types come from the desk model with `import type`. Nothing here decides who may do what or computes money: it words what the API sent,
 * builds the acknowledgement the approver gave, and checks what can be checked BEFORE an authenticator code is asked for (a code is spent
 * even when the approval is then refused). The API checks everything again and its answer always wins.
 */
import type { StaffQuoteDetail, StaffQuoteFlags, StaffQuoteLine, StaffQuoteVersion } from './quotes-desk-model';

export const QUOTE_APPROVAL_PERMISSIONS = { approve: 'quotes.approve', approveBelowFloor: 'quotes.approve_below_floor', edit: 'quotes.edit' } as const;

const holds = (permissions: readonly string[], p: string): boolean => permissions.some((x) => x.toLowerCase() === p);
export const canApproveQuotes = (permissions: readonly string[]): boolean => holds(permissions, QUOTE_APPROVAL_PERMISSIONS.approve);
export const canApproveBelowFloor = (permissions: readonly string[]): boolean => holds(permissions, QUOTE_APPROVAL_PERMISSIONS.approveBelowFloor);

export const APPROVAL_COMMENT_MIN = 5;
export const APPROVAL_COMMENT_MAX = 500;
export const APPROVAL_PAGE_SIZE = 20;

// ---------- shapes (StaffQuoteSendDto, ApproveQuoteRequest, StaffApprovalItemDto, StaffApprovalPage) ----------

export interface QuoteSendResult { status: 'sent' | 'pending_approval' | string; flags: StaffQuoteFlags; quote: StaffQuoteDetail }

export interface QuoteApproveBody { contentHash: string; belowFloorLineIds: string[]; overAuthorityLineIds: string[]; comment?: string }

export interface QuoteApprovalItem {
  quoteId: string;
  number: string;
  versionNo: number;
  companyId: string;
  companyName: string;
  submittedBy: string;
  submitterName: string;
  submittedAt: string;
  lineCount: number;
  validityDays: number;
  goodsNetMinor: number;
  goodsTotalMinor: number;
  grandTotalMinor: number | null;
  flags: StaffQuoteFlags;
  canApprove: boolean;
}

export interface QuoteApprovalPage { items: QuoteApprovalItem[]; page: number; pageSize: number; total: number }

// ---------- the Send button of the editor ----------

export interface SendButtonInput {
  /** Whether the person holds quotes.edit. */
  canEdit: boolean;
  /** Unsaved changes on the screen: the send uses the SAVED version. */
  dirty: boolean;
  lineCount: number;
  /** Fields are all valid (the body could be built). */
  valid: boolean;
  /** The price check is still running or waiting for the typing to stop. */
  checking: boolean;
  /** The outcome of the check of exactly what is on the screen, or null when there is none yet. */
  outcome: string | null;
}

export interface SendButtonState { allowed: boolean; label: string; reason: string }

/** The label follows the check ("Send for approval" when a second person will be needed); the reason says why the button is off. */
export function sendButtonState(i: SendButtonInput): SendButtonState {
  const label = i.outcome === 'approval' ? 'Send for approval' : 'Send to the buyer';
  const off = (reason: string): SendButtonState => ({ allowed: false, label, reason });
  if (!i.canEdit) return off('Your role may read quotes but not send them (quotes.edit).');
  if (i.lineCount === 0) return off('Add at least one line before sending.');
  if (!i.valid) return off('Fix the marked fields before sending.');
  if (i.dirty) return off('Save the draft first: the send uses the saved version, not what is on the screen.');
  if (i.checking) return off('Wait for the price check to finish.');
  if (i.outcome === 'refused') return off('The price check refuses this draft. Fix the problems listed in it first.');
  return { allowed: true, label, reason: i.outcome === 'approval' ? 'A second person must approve it before the buyer sees it.' : i.outcome === 'send' ? 'Nothing needs a second person: the buyer can see it at once.' : 'The server checks the prices again when you send.' };
}

export interface SendOutcome {
  kind: 'sent' | 'pending' | 'other';
  versionNo: number | null;
  validityDays: number | null;
  sentAt: string | null;
  validUntil: string | null;
  flags: StaffQuoteFlags;
}

/** What a send did, read from the version it concerned (the highest one in that state). The page words it (dates on the Dhaka clock). */
export function sendOutcome(result: QuoteSendResult): SendOutcome {
  const kind = result.status === 'sent' ? 'sent' : result.status === 'pending_approval' ? 'pending' : 'other';
  const wanted = kind === 'sent' ? 'sent' : 'pending_approval';
  let v: StaffQuoteVersion | null = null;
  for (const x of result.quote.versions) if (x.status === wanted && (!v || x.versionNo > v.versionNo)) v = x;
  return { kind, versionNo: v?.versionNo ?? null, validityDays: v?.validityDays ?? null, sentAt: v?.sentAt ?? null, validUntil: v?.validUntil ?? null, flags: result.flags };
}

/** The sentence under a send result, without dates (the page adds them). */
export function sendOutcomeTitle(o: SendOutcome): string {
  const no = o.versionNo === null ? 'The version' : `Version ${o.versionNo}`;
  if (o.kind === 'sent') return `${no} was sent to the buyer.`;
  if (o.kind === 'pending') return `${no} is waiting for approval. The buyer cannot see it yet.`;
  return 'The server answered with a state this screen does not know. Reload the quote to see where it stands.';
}

/** Flag counts for the queue, in words (the queue has no line numbers). */
export function queueFlagWords(f: StaffQuoteFlags | null | undefined): string[] {
  if (!f) return [];
  const out: string[] = [];
  const n = (k: number, w: string) => `${k} line${k === 1 ? '' : 's'} ${w}`;
  if (f.belowFloorLineIds.length > 0) out.push(n(f.belowFloorLineIds.length, 'below the margin floor'));
  if (f.overAuthorityLineIds.length > 0) out.push(n(f.overAuthorityLineIds.length, "beyond the preparer's price authority"));
  if (f.longValidity) out.push('valid for longer than the usual period');
  return out;
}

/** "3 days" or "2 hours" or "just now", for how long a version has waited. */
export function waitingText(iso: string, nowMs: number): string {
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return '';
  const minutes = Math.max(0, Math.floor((nowMs - t) / 60000));
  if (minutes < 2) return 'just now';
  if (minutes < 120) return `${minutes} minutes`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `${hours} hours`;
  return `${Math.floor(hours / 24)} days`;
}

export interface QueueRow {
  key: string;
  quoteId: string;
  number: string;
  versionNo: number;
  href: string;
  companyName: string;
  sender: string;
  submittedAt: string;
  lines: number;
  validityDays: number;
  goodsNetMinor: number;
  grandTotalMinor: number | null;
  flags: string[];
  canApprove: boolean;
  hint: string;
}

/** The queue as rows, oldest first whatever order the server sent. `canApprove` is the server's advisory flag; the decision screen explains. */
export function queueRows(items: readonly QuoteApprovalItem[]): QueueRow[] {
  return [...items]
    .sort((a, b) => a.submittedAt.localeCompare(b.submittedAt))
    .map((i) => ({
      key: `${i.quoteId}:${i.versionNo}`,
      quoteId: i.quoteId,
      number: i.number,
      versionNo: i.versionNo,
      href: `/quotes/approvals/${encodeURIComponent(i.quoteId)}/${Math.trunc(i.versionNo)}`,
      companyName: i.companyName,
      sender: i.submitterName,
      submittedAt: i.submittedAt,
      lines: i.lineCount,
      validityDays: i.validityDays,
      goodsNetMinor: i.goodsNetMinor,
      grandTotalMinor: i.grandTotalMinor,
      flags: queueFlagWords(i.flags),
      canApprove: i.canApprove,
      hint: i.canApprove ? 'You can decide on this version.'
        : i.flags.belowFloorLineIds.length > 0 ? 'You may not approve it: you prepared it, belong to the company, or lack the below-floor permission.'
          : 'You may not approve it: you prepared it or belong to the company.'
    }));
}

// ---------- the decision screen ----------

export interface LineRef { id: string; lineNo: number | null; sku: string; title: string; unitPriceMinor: number | null; reason: string | null }
export interface RequiredLines { belowFloorLineIds: string[]; overAuthorityLineIds: string[] }

/** The lines the approver must acknowledge: those the server flagged now when it said so (`override`), else the flags of the send. */
export function requiredLines(version: Pick<StaffQuoteVersion, 'flags'>, override: RequiredLines | null = null): RequiredLines {
  if (override) return { belowFloorLineIds: [...new Set(override.belowFloorLineIds)], overAuthorityLineIds: [...new Set(override.overAuthorityLineIds)] };
  return { belowFloorLineIds: [...new Set(version.flags?.belowFloorLineIds ?? [])], overAuthorityLineIds: [...new Set(version.flags?.overAuthorityLineIds ?? [])] };
}

/** Each id as a line of the version (by line number order); an id the version does not know is still listed, by id. */
export function lineRefs(ids: readonly string[], lines: readonly StaffQuoteLine[]): LineRef[] {
  const byId = new Map(lines.map((l) => [l.id, l] as const));
  return ids.map((id): LineRef => {
    const l = byId.get(id);
    return { id, lineNo: l?.lineNo ?? null, sku: l?.sku ?? '', title: l?.titleEn ?? `Line ${id.slice(0, 8)}`, unitPriceMinor: l?.unitPriceMinor ?? null, reason: l?.belowFloorReason ?? null };
  }).sort((a, b) => (a.lineNo ?? 1e9) - (b.lineNo ?? 1e9));
}

const GUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi;

/**
 * The server's QUOTE_FLAGS_NOT_ACKNOWLEDGED message names the lines flagged now:
 * "... Below the floor: [id, id]; over the discount authority: [id]." Returns null when the text has no such shape.
 */
export function flagsFromMessage(message: string | null | undefined): RequiredLines | null {
  const m = /below the floor:\s*\[([^\]]*)\];\s*over the discount authority:\s*\[([^\]]*)\]/i.exec(message ?? '');
  if (!m) return null;
  const ids = (s: string) => (s.match(GUID) ?? []).map((x) => x.toLowerCase());
  return { belowFloorLineIds: ids(m[1]), overAuthorityLineIds: ids(m[2]) };
}

export interface Ticks { belowFloor: ReadonlySet<string>; overAuthority: ReadonlySet<string> }

const sameSet = (a: ReadonlySet<string>, b: readonly string[]): boolean => a.size === new Set(b).size && b.every((x) => a.has(x));

/** The approver must tick exactly the flagged lines. A plain sentence says which part is missing, or null when it is exact. */
export function acknowledgementProblem(required: RequiredLines, ticks: Ticks): string | null {
  const need = required.belowFloorLineIds.length + required.overAuthorityLineIds.length;
  const extra = [...ticks.belowFloor].some((x) => !required.belowFloorLineIds.includes(x)) || [...ticks.overAuthority].some((x) => !required.overAuthorityLineIds.includes(x));
  if (extra) return 'A line is ticked that is not flagged. Tick only the flagged lines.';
  const missing = (required.belowFloorLineIds.filter((x) => !ticks.belowFloor.has(x)).length) + (required.overAuthorityLineIds.filter((x) => !ticks.overAuthority.has(x)).length);
  if (missing > 0) return need === 1 ? 'Tick the flagged line to show you have looked at it.' : `Tick every flagged line to show you have looked at it: ${missing} of ${need} still to tick.`;
  return sameSet(ticks.belowFloor, required.belowFloorLineIds) && sameSet(ticks.overAuthority, required.overAuthorityLineIds) ? null : 'The ticks do not match the flagged lines.';
}

/** Comment of an approval (optional, at most 500) or of a refusal (required, 5 to 500). */
export function approvalCommentProblem(comment: string | null | undefined, required: boolean): string | null {
  const n = (comment ?? '').trim().length;
  if (n === 0) return required ? 'Enter a comment for the author (5 to 500 characters).' : null;
  if (n < APPROVAL_COMMENT_MIN && required) return `The comment must be ${APPROVAL_COMMENT_MIN} to ${APPROVAL_COMMENT_MAX} characters.`;
  return n > APPROVAL_COMMENT_MAX ? `The comment must be at most ${APPROVAL_COMMENT_MAX} characters.` : null;
}

/** The body of `POST .../approve`, or null when the hash is missing, the ticks are not exact or the comment is too long. The approver is the caller, never the body. */
export function buildApproveBody(version: Pick<StaffQuoteVersion, 'contentHash'>, required: RequiredLines, ticks: Ticks, comment: string): QuoteApproveBody | null {
  if (!version.contentHash || acknowledgementProblem(required, ticks) || approvalCommentProblem(comment, false)) return null;
  const body: QuoteApproveBody = { contentHash: version.contentHash, belowFloorLineIds: [...required.belowFloorLineIds], overAuthorityLineIds: [...required.overAuthorityLineIds] };
  if (comment.trim()) body.comment = comment.trim();
  return body;
}

export interface QuoteIssueHint { allowed: boolean; reason: string }

/** Whether to offer Approve. A hint: the server decides (it also knows company membership, which the page cannot see). */
export function approveHint(
  version: Pick<StaffQuoteVersion, 'status' | 'createdBy' | 'contributors' | 'submittedBy' | 'contentHash'>,
  me: { id: string; permissions: readonly string[] },
  required: RequiredLines
): QuoteIssueHint {
  const ids = new Set([version.createdBy, version.submittedBy, ...(version.contributors ?? [])].filter((x): x is string => !!x).map((x) => x.toLowerCase()));
  if (version.status !== 'pending_approval') return { allowed: false, reason: 'Only a version that is waiting for approval can be approved.' };
  if (!canApproveQuotes(me.permissions)) return { allowed: false, reason: 'Your role may not approve quotes (quotes.approve).' };
  if (ids.has(me.id.toLowerCase())) return { allowed: false, reason: 'You created, changed or sent this version, so someone else must approve it.' };
  if (required.belowFloorLineIds.length > 0 && !canApproveBelowFloor(me.permissions)) return { allowed: false, reason: 'A line is below the margin floor: an administrator with the below-floor permission (quotes.approve_below_floor) must approve this version.' };
  if (!version.contentHash) return { allowed: false, reason: 'This version has no content hash yet. Reload the page.' };
  return { allowed: true, reason: '' };
}

/** Recall: the editor or sender takes a version back from approval. */
export function recallHint(version: Pick<StaffQuoteVersion, 'status'>, permissions: readonly string[]): QuoteIssueHint {
  if (!holds(permissions, QUOTE_APPROVAL_PERMISSIONS.edit)) return { allowed: false, reason: 'Your role may read quotes but not change them (quotes.edit).' };
  return version.status === 'pending_approval' ? { allowed: true, reason: '' } : { allowed: false, reason: 'Only a version that is waiting for approval can be recalled.' };
}

/** Withdraw: only a version that is out with the buyer (Sent). An accepted one ends through its order. */
export function withdrawHint(version: Pick<StaffQuoteVersion, 'status'>, permissions: readonly string[]): QuoteIssueHint {
  if (!holds(permissions, QUOTE_APPROVAL_PERMISSIONS.edit)) return { allowed: false, reason: 'Your role may read quotes but not change them (quotes.edit).' };
  return version.status === 'sent' ? { allowed: true, reason: '' } : { allowed: false, reason: 'Only a version that was sent to the buyer can be withdrawn.' };
}

/** A send from the quote page needs only a draft with at least one line (the preview is the editor's job; the server decides). */
export function sendFromDetailHint(version: Pick<StaffQuoteVersion, 'status'> & { lines: readonly unknown[] }, permissions: readonly string[]): QuoteIssueHint {
  if (!holds(permissions, QUOTE_APPROVAL_PERMISSIONS.edit)) return { allowed: false, reason: 'Your role may read quotes but not send them (quotes.edit).' };
  if (version.status !== 'draft') return { allowed: false, reason: 'Only a draft can be sent.' };
  return version.lines.length === 0 ? { allowed: false, reason: 'Add at least one line in the editor before sending.' } : { allowed: true, reason: '' };
}

// ---------- errors ----------

export interface IssueErrorLike { status: number | null; code: string | null; message: string; fields: Record<string, string> }

const ISSUE_ERROR_TEXT: Record<string, string> = {
  STEP_UP_REQUIRED: 'This action needs the 6-digit code from your authenticator app.',
  STEP_UP_FAILED: 'The code was not accepted. Wait for the next code in your authenticator app (each code works once) and try again.',
  QUOTE_FOUR_EYES: 'Whoever created, changed or sent a quote version cannot approve it. Another approver must.',
  QUOTE_CHANGED: 'This version, or the prices and costs it is checked against, changed after you opened it. The page was reloaded: look at it again before you approve.',
  QUOTE_FLAGS_NOT_ACKNOWLEDGED: 'The lines that need a decision changed since the version was sent (a cost, a floor or a price moved). The page was reloaded and now asks you to tick the lines flagged now.',
  BELOW_FLOOR_PERMISSION_REQUIRED: 'A line is below the margin floor. Only an administrator with the below-floor permission can approve it.',
  BELOW_FLOOR_REASON_REQUIRED: 'A line has fallen below the margin floor since it was sent and has no reason. Send the version back to its author instead.',
  QUOTE_BELOW_COST: 'A line is priced below its cost, which the settings do not allow. Send the version back to its author.',
  QUOTE_INVALID_TRANSITION: 'That step is no longer possible: the version was already sent, approved, sent back, recalled or withdrawn (maybe a moment ago, by you or someone else). The page was reloaded to show where it stands.',
  QUOTE_DELIVERY_SITE_GONE: 'The delivery site of this quote was removed from the company, so it cannot be sent. Edit the draft and choose another site, or use calculated delivery.',
  COMPANY_NOT_ACTIVE: 'The company is not active, so its quotes cannot be sent or approved.',
  QUOTE_CONFLICT_OF_INTEREST: 'You belong to this company, so you may not send or approve its quotes. Ask a colleague who does not work for it.',
  QUOTE_NOT_EDITABLE: 'This version is no longer a draft, so it cannot be sent. Reload the quote to see its state.'
};
const SEND_REASON_TEXT = 'A line below the margin floor needs a reason before the quote can be sent.';

/** Codes whose refusal means the screen shows an old state: it reloads by itself. */
export const ISSUE_RELOAD_CODES: ReadonlySet<string> = new Set([
  'QUOTE_CHANGED', 'QUOTE_FLAGS_NOT_ACKNOWLEDGED', 'QUOTE_INVALID_TRANSITION', 'QUOTE_VERSION_CHANGED', 'QUOTE_NOT_EDITABLE', 'QUOTE_CLOSED', 'QUOTE_OPEN_VERSION_EXISTS', 'QUOTE_ALREADY_ACCEPTED'
]);

/** Plain words for a send / approve / reject / recall / withdraw failure, or null when the code is not one of these (the caller falls back to the desk wording). */
export function issueErrorText(e: IssueErrorLike): string | null {
  if (e.code === 'VALIDATION_ERROR' || (e.status === 400 && !e.code)) {
    const reason = e.fields.belowFloorReason ?? Object.entries(e.fields).find(([k]) => /belowFloorReason/i.test(k))?.[1];
    if (reason) return SEND_REASON_TEXT;
  }
  return e.code && ISSUE_ERROR_TEXT[e.code] ? ISSUE_ERROR_TEXT[e.code] : null;
}

export const isStepUpRefusal = (code: string | null | undefined): boolean => code === 'STEP_UP_FAILED' || code === 'STEP_UP_REQUIRED';
export const shouldReloadAfterIssue = (code: string | null | undefined): boolean => !!code && ISSUE_RELOAD_CODES.has(code);

/** Said after any refusal that came with a code: the code is gone, the next one is needed. */
export const CODE_SPENT_NOTE = 'The code you typed has been used up even though the action was refused: the next one is needed for another try.';
