/**
 * Staff VDS/AIT withholdings (E6.6c). Pure shapes and helpers, no runtime imports. Hand-written from the E6.6b HANDOFF
 * (`TaxWithholdingContracts.cs`, `AdminWithholdingsController.cs`). Amounts are whole poisha.
 */
export type WithholdingKind = 'vds' | 'ait';
export type WithholdingStatus = 'recorded' | 'verified' | 'rejected';
export interface TaxWithholding {
  id: string; kind: WithholdingKind | string; invoiceId: string; invoiceNumber: string; orderId: string; companyId: string; companyName: string; certificateNumber: string;
  certificateDate: string; amountMinor: number; status: WithholdingStatus | string; recordedBy: string; recordedAtUtc: string; decidedBy: string | null; decidedAtUtc: string | null; decisionReason: string | null;
}
export interface TaxWithholdingPage { items: TaxWithholding[]; page: number; pageSize: number; totalCount: number }
export interface WithholdingBody { kind: WithholdingKind; certificateNumber: string; certificateDate: string; amountMinor: number }
export interface WithholdingFilters { status: string; kind: string; invoiceId: string; page: number }

export const WITHHOLDING_PAGE_SIZE = 50;
const has = (permissions: readonly string[], p: string) => permissions.some((x) => x.toLowerCase() === p);
export const canRecordWithholding = (permissions: readonly string[]): boolean => has(permissions, 'credit.record_payment');
export const canVerifyWithholding = (permissions: readonly string[]): boolean => has(permissions, 'credit.verify_payment');

export const WITHHOLDING_KIND_OPTIONS: ReadonlyArray<{ value: WithholdingKind; label: string }> = [
  { value: 'vds', label: 'VDS (VAT withheld at source)' }, { value: 'ait', label: 'AIT (income tax withheld at source)' }
];
export const withholdingKindText = (k: string | null | undefined): string => (k === 'vds' ? 'VDS' : k === 'ait' ? 'AIT' : 'Unknown kind');
const STATUS: Record<string, string> = { recorded: 'Waiting for a second person', verified: 'Verified', rejected: 'Rejected' };
export const withholdingStatusText = (s: string | null | undefined): string => STATUS[s ?? ''] ?? 'Unknown status';
export const WITHHOLDING_STATUS_OPTIONS: ReadonlyArray<{ value: string; label: string }> = [
  { value: 'recorded', label: 'Waiting' }, { value: 'verified', label: 'Verified' }, { value: 'rejected', label: 'Rejected' }
];

/** Filters in the URL: an unknown status or kind falls back to the default (waiting, any kind). */
export function withholdingFiltersFromSearch(q: { get(name: string): string | null }): WithholdingFilters {
  const status = q.get('status') ?? '';
  const kind = q.get('kind') ?? '';
  const invoiceId = q.get('invoiceId') ?? '';
  const page = Number(q.get('page') ?? '1');
  return {
    status: STATUS[status] ? status : 'recorded', kind: kind === 'vds' || kind === 'ait' ? kind : '',
    invoiceId: /^[0-9a-f-]{32,36}$/i.test(invoiceId) ? invoiceId : '', page: Number.isInteger(page) && page >= 1 ? page : 1
  };
}
export function withholdingFiltersToSearch(f: WithholdingFilters): string {
  const p = new URLSearchParams();
  if (f.status !== 'recorded') p.set('status', f.status);
  if (f.kind) p.set('kind', f.kind);
  if (f.invoiceId) p.set('invoiceId', f.invoiceId);
  if (f.page > 1) p.set('page', String(f.page));
  return p.toString();
}

/** BDT text to poisha, no floats: "50", "1,250.5". Anything else is null. */
export function withholdingBdtToMinor(text: string): number | null {
  const m = /^(\d{1,10})(?:\.(\d{1,2}))?$/.exec(text.trim().replace(/,/g, ''));
  return m ? Number(m[1]) * 100 + Number((m[2] ?? '').padEnd(2, '0') || '0') : null;
}

/** The request body, or the first problem in words (checked before anything is sent). `today` is yyyy-MM-dd (Dhaka). */
export function buildWithholdingBody(input: { kind: string; certificateNumber: string; certificateDate: string; amount: string }, today: string): { body: WithholdingBody } | { problem: string } {
  if (input.kind !== 'vds' && input.kind !== 'ait') return { problem: 'Choose VDS or AIT.' };
  const number = input.certificateNumber.trim().toUpperCase().replace(/\s+/g, ' ');
  if (number.length < 1 || number.length > 40 || !/^[A-Z0-9/ -]+$/.test(number) || !/[A-Z0-9]/.test(number)) return { problem: 'Write the certificate number as printed: letters, digits, "/" and "-", up to 40 characters.' };
  if (!/^\d{4}-\d{2}-\d{2}$/.test(input.certificateDate) || Number.isNaN(Date.parse(input.certificateDate))) return { problem: 'Choose the date printed on the certificate.' };
  if (input.certificateDate > today) return { problem: 'The certificate date cannot be in the future.' };
  const amountMinor = withholdingBdtToMinor(input.amount);
  if (amountMinor === null || amountMinor < 1) return { problem: 'Write the amount in BDT, for example 1500 or 1500.50.' };
  return { body: { kind: input.kind, certificateNumber: number, certificateDate: input.certificateDate, amountMinor } };
}
/** Same details, same key: a retry replays; a changed form is a new certificate with a new key. */
export const withholdingFingerprint = (invoiceId: string, body: WithholdingBody): string => JSON.stringify([invoiceId, body]);

/** Four eyes: the person who recorded a certificate cannot verify it. Anyone with `credit.record_payment` may reject. */
export function withholdingDecision(w: Pick<TaxWithholding, 'status' | 'recordedBy'>, me: { id: string; permissions: readonly string[] }): { canVerify: boolean; canReject: boolean; hint: string | null } {
  if (w.status !== 'recorded') return { canVerify: false, canReject: false, hint: `This certificate is ${withholdingStatusText(w.status).toLowerCase()}, so there is nothing to decide.` };
  const canReject = canRecordWithholding(me.permissions) || canVerifyWithholding(me.permissions);
  if (!canVerifyWithholding(me.permissions)) return { canVerify: false, canReject, hint: 'Your role may not verify certificates (credit.verify_payment).' };
  if (w.recordedBy.toLowerCase() === me.id.toLowerCase()) return { canVerify: false, canReject, hint: 'You recorded this certificate, so another person must verify it (two people must agree). You can still reject it.' };
  return { canVerify: true, canReject, hint: null };
}

const money = (m: number) => `BDT ${(m / 100).toFixed(2).replace(/\B(?=(\d{3})+\.)/g, ',')}`;
/** What a verified certificate does, in words, for the confirmation dialog. */
export const verifyWithholdingText = (w: Pick<TaxWithholding, 'kind' | 'certificateNumber' | 'amountMinor'>): string =>
  `This accepts ${withholdingKindText(w.kind)} certificate ${w.certificateNumber} and takes ${money(w.amountMinor)} off what the company owes on credit. It cannot be undone.`;

const TEXT: Record<string, string> = {
  WITHHOLDING_EXCEEDS_VAT: 'The amount is more than the VAT still left on this invoice (earlier VDS certificates and credit notes count).',
  WITHHOLDING_EXCEEDS_NET: 'The amount is more than the net value still left on this invoice (earlier AIT certificates and credit notes count).',
  WITHHOLDING_NEEDS_COMPANY_INVOICE: 'Only an invoice issued to a company can have a withholding certificate.',
  WITHHOLDING_NEEDS_CREDIT_ORDER: 'This order has nothing owed on credit, so there is nothing for a certificate to settle.',
  WITHHOLDING_CERTIFICATE_DUPLICATE: 'This company already has a certificate with this number and kind. Check the number, or open the earlier one.',
  WITHHOLDING_FOUR_EYES: 'You recorded this certificate, so you cannot verify it. Another person must.',
  WITHHOLDING_NOT_RECORDED: 'Someone has already decided this certificate. The page has been reloaded.',
  IDEMPOTENCY_KEY_REUSED: 'This request was already sent with different details. Reload the page and start again.',
  CREDIT_EXCEEDS_RECEIVABLE: 'The certificate is more than the company still owes on credit, so it cannot be applied. Ask Finance to check the account.',
  STEP_UP_REQUIRED: 'This action needs the 6-digit code from your authenticator app.',
  STEP_UP_FAILED: 'The code was not accepted. Codes work once: wait for the next code (up to 30 seconds) and enter that one.'
};
export const isWithholdingStepUp = (code: string | null | undefined): boolean => code === 'STEP_UP_FAILED' || code === 'STEP_UP_REQUIRED';
export const shouldReloadWithholding = (code: string | null | undefined): boolean => code === 'WITHHOLDING_NOT_RECORDED';

/** A cap message from the server (it states what is left, when it does) is added after the fixed text. */
export function withholdingErrorText(e: { status: number | null; code?: string | null; message?: string | null }): string {
  if (e.code && TEXT[e.code]) {
    const fixed = TEXT[e.code]!;
    return (e.code === 'WITHHOLDING_EXCEEDS_VAT' || e.code === 'WITHHOLDING_EXCEEDS_NET') && e.message ? `${fixed} ${e.message}` : fixed;
  }
  if (e.status === null) return 'The server cannot be reached. Check your connection and try again.';
  if (e.status === 401) return 'Your session ended. Sign in again.';
  if (e.status === 403) return 'Your role does not allow this action.';
  if (e.status === 404) return 'This certificate or invoice was not found.';
  if (e.status === 429) return 'Too many tries. Wait a minute and try again.';
  if (e.status >= 500) return 'The server had a problem. Try again in a moment.';
  return e.message || 'The request could not be completed.';
}

/** 9.1 section c: the verified VDS of the month and their total. */
export interface VatVdsRow { withholdingId: string; certificateNumber: string; certificateDate: string; verifiedAtDhaka: string; invoiceNumber: string; buyerName: string; buyerBin: string | null; amountMinor: number }
export const vdsTotalMinor = (rows: readonly Pick<VatVdsRow, 'amountMinor'>[]): number => rows.reduce((s, r) => s + r.amountMinor, 0);
