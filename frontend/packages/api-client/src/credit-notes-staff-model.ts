/**
 * Staff credit notes (E6.5d). Pure shapes and helpers, no runtime imports. Hand-written from the E6.5b HANDOFF
 * (`CreditNoteContracts.cs`, `AdminCreditNotesController.cs`). A request has no number; only an approval issues the 6.7.
 */
export type CreditNoteStatus = 'requested' | 'approved' | 'rejected' | 'withdrawn';
export interface CreditNoteLine { lineNo: number; kind: string; sku: string | null; descriptionEn: string; quantity: number; netMinor: number; vatMinor: number; totalMinor: number }
export interface CreditNoteRequest {
  id: string; originalInvoiceId: string; originalNumber: string; orderId: string; orderNumber: string; status: CreditNoteStatus | string; reason: string; reasonText: string;
  lines: CreditNoteLine[]; totalMinor: number; requestedBy: string; requestedAtUtc: string; decidedBy: string | null; decidedAtUtc: string | null;
  decisionReason: string | null; issuedInvoiceId: string | null; issuedNumber: string | null; replace?: boolean;
}
export interface CreditNotePage { items: CreditNoteRequest[]; page: number; pageSize: number; totalCount: number }
export interface CreditNoteApproval {
  request: CreditNoteRequest; creditNoteId: string; number: string; totalMinor: number; refundDueMinor: number; creditReleasedMinor: number; movedOnAccountMinor: number;
  replacementInvoiceId?: string | null; replacementNumber?: string | null;
}
export interface CreditNoteBody { reason: string; reasonText: string; lines?: Array<{ lineNo: number; quantity: number; netMinor?: number }>; replace?: boolean }

export const CREDIT_QUEUE_PAGE_SIZE = 50;
const has = (permissions: readonly string[], p: string) => permissions.some((x) => x.toLowerCase() === p);
export const canRequestCreditNote = (permissions: readonly string[]): boolean => has(permissions, 'invoices.credit_request');
export const canApproveCreditNote = (permissions: readonly string[]): boolean => has(permissions, 'invoices.credit_approve');

export const CREDIT_REASON_OPTIONS: ReadonlyArray<{ value: string; label: string }> = [
  { value: 'goods_returned', label: 'Goods returned' }, { value: 'price_reduction', label: 'Price reduction' }, { value: 'error', label: 'Error on the invoice' }, { value: 'other', label: 'Other' }
];
export const creditReasonText = (r: string | null | undefined): string => CREDIT_REASON_OPTIONS.find((o) => o.value === r)?.label ?? 'Other';
const STATUS: Record<string, string> = { requested: 'Waiting for a decision', approved: 'Approved', rejected: 'Rejected', withdrawn: 'Withdrawn' };
export const creditStatusText = (s: string | null | undefined): string => STATUS[s ?? ''] ?? 'Unknown status';

/** BDT text to poisha, no floats: "50", "1,250.5", "12.05". Anything else is null. */
export function creditBdtToMinor(text: string): number | null {
  const m = /^(\d{1,10})(?:\.(\d{1,2}))?$/.exec(text.trim().replace(/,/g, ''));
  return m ? Number(m[1]) * 100 + Number((m[2] ?? '').padEnd(2, '0') || '0') : null;
}

/** What the person typed for one invoice line; `maxQuantity`/`maxNetMinor` are the invoice line's own figures (the server also subtracts earlier credit notes). */
export interface CreditLineDraft { lineNo: number; maxQuantity: number; maxNetMinor: number; quantity: string; value: string }

/** The request body, or the first problem in words (checked before anything is sent). Lines with nothing typed are left out. */
export function buildCreditNoteBody(lines: readonly CreditLineDraft[], reason: string, reasonText: string): { body: CreditNoteBody } | { problem: string } {
  if (!CREDIT_REASON_OPTIONS.some((o) => o.value === reason)) return { problem: 'Choose a reason.' };
  const text = reasonText.trim();
  if (text.length < 5 || text.length > 500) return { problem: 'Write the reason in 5 to 500 characters.' };
  const out: CreditNoteBody['lines'] = [];
  for (const l of lines) {
    const qText = l.quantity.trim(), vText = l.value.trim();
    if (qText === '' && vText === '') continue;
    const q = qText === '' ? 0 : /^\d{1,6}$/.test(qText) ? Number(qText) : NaN;
    if (Number.isNaN(q) || q > l.maxQuantity) return { problem: `Line ${l.lineNo}: the quantity must be a whole number from 0 to ${l.maxQuantity}.` };
    let net: number | undefined;
    if (vText !== '') {
      const v = creditBdtToMinor(vText);
      if (v === null || v < 1) return { problem: `Line ${l.lineNo}: write the value as an amount in BDT, for example 250 or 250.50.` };
      if (v > l.maxNetMinor) return { problem: `Line ${l.lineNo}: the value is more than the line's net amount.` };
      net = v;
    }
    if (q === 0 && net === undefined) continue;
    out.push(net === undefined ? { lineNo: l.lineNo, quantity: q } : { lineNo: l.lineNo, quantity: q, netMinor: net });
  }
  if (out.length === 0) return { problem: 'Enter a quantity or a value on at least one line.' };
  return { body: { reason, reasonText: text, lines: out } };
}

/** E6.5e: replace an invoice (wrong buyer details). No lines = credit everything left; reason is always `error`. The server refuses it unless the invoice is still whole (409). */
export function buildReplacementBody(reasonText: string): { body: CreditNoteBody } | { problem: string } {
  const text = reasonText.trim();
  if (text.length < 5 || text.length > 500) return { problem: 'Write what was wrong in 5 to 500 characters.' };
  return { body: { reason: 'error', reasonText: text, replace: true } };
}
export const REPLACE_EXPLAIN = 'This credits the whole invoice with a credit note and issues a new tax invoice with the same lines and the buyer details as they are now. Another person must approve it, with their authenticator code. Until then nothing changes. It only works when no earlier credit note has been issued on this invoice.';
/** After approving a replacement: the 6.7 and the new 6.3. */
export const replacementApprovalText = (a: { replacementNumber?: string | null }): string | null =>
  a.replacementNumber ? `The new tax invoice ${a.replacementNumber} was issued in place of the old one.` : null;

/** Same details, same key: a retry replays; a changed form is a new request with a new key. */
export const creditRequestFingerprint = (invoiceId: string, body: CreditNoteBody): string => JSON.stringify([invoiceId, body]);

/** What this person may do with a request. Four eyes: nobody approves or rejects their own request; only the requester withdraws. */
export function creditDecision(req: Pick<CreditNoteRequest, 'status' | 'requestedBy'>, me: { id: string; permissions: readonly string[] }): { canDecide: boolean; canWithdraw: boolean; hint: string | null } {
  if (req.status !== 'requested') return { canDecide: false, canWithdraw: false, hint: `This request is ${creditStatusText(req.status).toLowerCase()}, so there is nothing to decide.` };
  const mine = req.requestedBy.toLowerCase() === me.id.toLowerCase();
  if (mine) return { canDecide: false, canWithdraw: canRequestCreditNote(me.permissions), hint: 'You asked for this credit note, so another person must approve or reject it (two people must agree). You can withdraw it instead.' };
  if (!canApproveCreditNote(me.permissions)) return { canDecide: false, canWithdraw: false, hint: 'Your role may not decide credit notes (invoices.credit_approve).' };
  return { canDecide: true, canWithdraw: false, hint: null };
}

const money = (m: number) => `BDT ${(m / 100).toFixed(2).replace(/\B(?=(\d{3})+\.)/g, ',')}`;
/** After an approval: the 6.7 number and, when the order is not on credit, the money owed back. */
export function creditApprovalText(a: Pick<CreditNoteApproval, 'number' | 'refundDueMinor' | 'creditReleasedMinor'>): string {
  const base = `Credit note ${a.number} was issued.`;
  if (a.refundDueMinor > 0) return `${base} Refund due: ${money(a.refundDueMinor)} is owed back to the buyer. Finance decides how it is paid out; nothing was sent automatically.`;
  if (a.creditReleasedMinor > 0) return `${base} ${money(a.creditReleasedMinor)} was taken off what the company owes on credit. No refund is due.`;
  return `${base} No refund is due.`;
}

const TEXT: Record<string, string> = {
  CREDIT_NOTE_CHANGED: 'Something changed since this was asked (another credit note, or the invoice). The page has been reloaded: check the figures and decide again, or ask for a new request.',
  CREDIT_NOTE_FOUR_EYES: 'You asked for this credit note, so you cannot decide it. Another person must approve or reject it.',
  CREDIT_NOTE_NOT_YOURS: 'Only the person who asked can withdraw this request.',
  CREDIT_NOTE_NOT_REQUESTED: 'Someone has already decided this request. The page has been reloaded.',
  CREDIT_NOTE_EXCEEDS_ORIGINAL: 'The credit is more than what is left on the invoice (earlier credit notes count). Lower the quantity or value.',
  CREDIT_NOTE_TOO_LATE: 'This invoice is too old for a credit note (the limit is 180 days).',
  CREDIT_NOTE_OF_CREDIT_NOTE: 'A credit note cannot be raised against another credit note. Use the original invoice.',
  CREDIT_REDUCTION_EXCEEDS_CHARGE: 'The credit is more than the order still owes on credit. Ask Finance to check the order before approving.',
  INVOICE_REPLACEMENT_NEEDS_FULL_CREDIT: 'An invoice can only be replaced when it has not been credited before (a replacement credits all of it). Use a normal credit note instead.',
  INVOICE_BUYER_BIN_INVALID: 'The buyer BIN is missing or no longer valid, so a new invoice cannot be issued. Fix the company details first.',
  IDEMPOTENCY_KEY_REUSED: 'This request was already sent with different details. Reload the page and start again.',
  STEP_UP_REQUIRED: 'This action needs the 6-digit code from your authenticator app.',
  STEP_UP_FAILED: 'The code was not accepted. Codes work once: wait for the next code (up to 30 seconds) and enter that one.'
};
export const isCreditNoteStepUp = (code: string | null | undefined): boolean => code === 'STEP_UP_FAILED' || code === 'STEP_UP_REQUIRED';
/** Reload the request after these: its facts are out of date. */
export const shouldReloadCreditNote = (code: string | null | undefined): boolean => code === 'CREDIT_NOTE_CHANGED' || code === 'CREDIT_NOTE_NOT_REQUESTED';

export function creditNoteErrorText(e: { status: number | null; code?: string | null; message?: string | null }): string {
  if (e.code && TEXT[e.code]) return TEXT[e.code]!;
  if (e.status === null) return 'The server cannot be reached. Check your connection and try again.';
  if (e.status === 401) return 'Your session ended. Sign in again.';
  if (e.status === 403) return 'Your role does not allow this action.';
  if (e.status === 404) return 'This invoice or credit note request was not found.';
  if (e.status === 400 && e.message) return e.message;
  if (e.status === 400) return 'The details were not accepted. Check the quantities, values and the reason.';
  if (e.status >= 500) return 'The server had a problem. Try again in a moment; if it keeps happening, tell the technical team.';
  return 'The request was refused.';
}
