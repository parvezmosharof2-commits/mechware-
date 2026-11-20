/**
 * Console quote draft editor (D4.16). Pure shapes and helpers, no runtime imports (Node's type stripping must be able to test it).
 * The form keeps text exactly as typed (taka as text, quantities as text); only `buildDraftBody` turns it into the API body, and only
 * with the same string rule as the price-list editor (`takaToMinor`: no floats, at most two decimals). The server prices, checks and
 * flags everything again: what the preview says is advice, the save is the truth. Lines get NEW ids on every save (D4.7), so a line
 * is addressed by its position (`lineNo`, 1-based) and by a client key that never leaves the browser.
 */

export const QUOTE_EDITOR_MAX_LINES = 50;
export const QUOTE_VALIDITY_MIN = 1;
export const QUOTE_VALIDITY_MAX = 90;
/** Longer than this raises the "long validity" flag and the quote needs a second person. */
export const QUOTE_VALIDITY_USUAL = 30;
export const QUOTE_MAX_QUANTITY = 10_000;
export const QUOTE_MAX_PRICE_MINOR = 100_000_000_000;
export const QUOTE_LINE_NOTE_MAX = 300;
export const QUOTE_TERMS_MAX = 4000;
export const QUOTE_NOTE_MAX = 2000;
export const QUOTE_REASON_MIN = 5;
export const QUOTE_REASON_MAX = 500;
export const QUOTE_PREVIEW_DELAY_MS = 600;

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export const isEditorGuid = (v: string | null | undefined): boolean => GUID.test((v ?? '').trim());

// ---------- money (string rule, no floats) ----------

/** Decimal taka text to poisha: "12", "12.5", "1,234.50" are fine; three decimals, a sign or an exponent give null. Same rule as the price-list editor. */
export function poishaFromTaka(text: string): number | null {
  const t = text.trim().replace(/,/g, '').replace(/^৳\s*/, '');
  const m = /^(\d{1,12})(?:\.(\d{1,2}))?$/.exec(t);
  if (!m) return null;
  return Number(m[1]) * 100 + Number((m[2] ?? '').padEnd(2, '0') || '0');
}

/** Poisha to the text a price box shows: "1250.50" (no thousands separator, so it can be edited and parsed back). */
export function takaFromPoisha(minor: number | null | undefined): string {
  if (minor === null || minor === undefined || !Number.isSafeInteger(minor) || minor < 0) return '';
  return `${Math.floor(minor / 100)}.${String(minor % 100).padStart(2, '0')}`;
}

/** "1,234.50" for display. */
export function takaShown(minor: number | null | undefined): string {
  if (minor === null || minor === undefined || !Number.isFinite(minor)) return '';
  const abs = Math.abs(minor);
  return `${minor < 0 ? '-' : ''}${Math.floor(abs / 100).toLocaleString('en-US')}.${String(abs % 100).padStart(2, '0')}`;
}

// ---------- the form ----------

export interface EditorLine {
  /** Browser-only identity; the server's line id changes on every save. */
  key: string;
  variantId: string;
  sku: string;
  partNumber: string | null;
  title: string;
  quantity: string;
  priceText: string;
  lineNote: string;
  reason: string;
  rfqLineId: string | null;
  /** Hints from the part search (not saved, absent on a line read back from the server). */
  minOrderQty?: number;
  packSize?: number;
}

export interface EditorFee { key: string; sellerId: string; label: string; feeText: string }

export interface EditorForm {
  /** The contact as saved; `contactUserId` is sent only when it differs. */
  originalContactUserId: string;
  contactUserId: string;
  validityDays: string;
  paymentTerms: 'as_account' | 'advance';
  shippingMode: 'calculated' | 'fixed';
  shipToSiteId: string;
  fees: EditorFee[];
  termsText: string;
  buyerNote: string;
  internalNote: string;
  lines: EditorLine[];
}

/** What the saved version looks like (the part of `StaffQuoteVersion` the editor reads). */
export interface EditorSourceLine {
  variantId: string; sku: string; partNumber: string | null; titleEn: string; quantity: number; unitPriceMinor: number; lineNote: string | null;
  belowFloorReason: string | null; rfqLineId: string | null;
}
export interface EditorSourceVersion {
  versionNo: number; status: string; rowVersion: number; validityDays: number; paymentTerms: string; shippingMode: string; shipToSiteId: string | null;
  /** Saved fixed-delivery fees (D4.18); null or absent for calculated delivery. */
  shippingFees?: { sellerId: string; shippingMinor: number }[] | null;
  termsText: string | null; buyerNote: string | null; internalNote: string | null; lines: EditorSourceLine[];
}

let keySeq = 0;
export const nextEditorKey = (prefix = 'k'): string => `${prefix}${++keySeq}`;

export function lineFromSource(l: EditorSourceLine, key = nextEditorKey('l')): EditorLine {
  return {
    key, variantId: l.variantId, sku: l.sku, partNumber: l.partNumber, title: l.titleEn, quantity: String(l.quantity), priceText: takaFromPoisha(l.unitPriceMinor),
    lineNote: l.lineNote ?? '', reason: l.belowFloorReason ?? '', rfqLineId: l.rfqLineId
  };
}

/** The saved version as form text. A fixed delivery reopens with its saved fees (seller names come from the sellers lookup). */
export function formFromVersion(v: EditorSourceVersion, contactUserId: string): EditorForm {
  return {
    originalContactUserId: contactUserId, contactUserId, validityDays: String(v.validityDays),
    paymentTerms: v.paymentTerms === 'advance' ? 'advance' : 'as_account', shippingMode: v.shippingMode === 'fixed' ? 'fixed' : 'calculated',
    shipToSiteId: v.shipToSiteId ?? '',
    fees: (v.shippingFees ?? []).map((f) => ({ key: nextEditorKey('f'), sellerId: f.sellerId, label: '', feeText: takaFromPoisha(f.shippingMinor) })), termsText: v.termsText ?? '', buyerNote: v.buyerNote ?? '', internalNote: v.internalNote ?? '',
    lines: [...v.lines].map((l) => lineFromSource(l))
  };
}

export interface VariantPick { variantId: string; sku: string; partNumber: string | null; title: string; priceMinor?: number; minOrderQty?: number; packSize?: number }

/** A new line for a picked variant: quantity 1, the price box empty so a price is always typed on purpose. */
export function newLineFor(v: VariantPick, key = nextEditorKey('l')): EditorLine {
  return { key, variantId: v.variantId, sku: v.sku, partNumber: v.partNumber, title: v.title, quantity: '1', priceText: '', lineNote: '', reason: '', rfqLineId: null, minOrderQty: v.minOrderQty, packSize: v.packSize };
}

/** Why a variant cannot be added, or null: the list is full or the item is already on the quote (the server refuses a duplicate item). */
export function addLineProblem(lines: readonly Pick<EditorLine, 'variantId' | 'sku'>[], variantId: string): string | null {
  if (lines.length >= QUOTE_EDITOR_MAX_LINES) return `A quote has at most ${QUOTE_EDITOR_MAX_LINES} lines. Split a larger one into two quotes.`;
  const same = lines.find((l) => l.variantId === variantId);
  return same ? `${same.sku} is already on this quote. Change its quantity instead.` : null;
}

export const removeLine = (lines: readonly EditorLine[], key: string): EditorLine[] => lines.filter((l) => l.key !== key);

// ---------- checks before anything is sent ----------

export interface EditorProblems {
  /** Keyed by form field: validityDays, contactUserId, shipToSiteId, termsText, buyerNote, internalNote, lines, shippingFees. */
  header: Record<string, string>;
  /** Keyed by the 1-based line number. */
  lines: Record<number, string>;
  /** Keyed by the 0-based fee row. */
  fees: Record<number, string>;
  count: number;
}

export function lineProblem(l: EditorLine): string | null {
  if (!/^\d+$/.test(l.quantity.trim())) return 'Quantity must be a whole number.';
  const q = Number(l.quantity.trim());
  if (q < 1) return 'Quantity must be at least 1.';
  if (q > QUOTE_MAX_QUANTITY) return `Quantity cannot be more than ${QUOTE_MAX_QUANTITY.toLocaleString('en-US')}.`;
  const minor = poishaFromTaka(l.priceText);
  if (minor === null) return 'Enter the net price in taka, with at most two decimals (for example 1250.50).';
  if (minor < 1) return 'The price must be at least 0.01.';
  if (minor > QUOTE_MAX_PRICE_MINOR) return 'The price is above the maximum of 1,000,000,000.00.';
  if (l.lineNote.trim().length > QUOTE_LINE_NOTE_MAX) return `The line note can be at most ${QUOTE_LINE_NOTE_MAX} characters.`;
  const r = l.reason.trim().length;
  if (r > 0 && r < QUOTE_REASON_MIN) return `A below-floor reason needs at least ${QUOTE_REASON_MIN} characters.`;
  if (r > QUOTE_REASON_MAX) return `A below-floor reason can be at most ${QUOTE_REASON_MAX} characters.`;
  return null;
}

export function editorProblems(f: EditorForm): EditorProblems {
  const header: Record<string, string> = {};
  const lines: Record<number, string> = {};
  const fees: Record<number, string> = {};
  const days = f.validityDays.trim();
  if (!/^\d+$/.test(days) || Number(days) < QUOTE_VALIDITY_MIN || Number(days) > QUOTE_VALIDITY_MAX) header.validityDays = `Enter whole days, ${QUOTE_VALIDITY_MIN} to ${QUOTE_VALIDITY_MAX}.`;
  if (f.contactUserId.trim() && !isEditorGuid(f.contactUserId)) header.contactUserId = 'Choose one of the listed contacts.';
  if (f.termsText.length > QUOTE_TERMS_MAX) header.termsText = `Terms can be at most ${QUOTE_TERMS_MAX} characters.`;
  if (f.buyerNote.length > QUOTE_NOTE_MAX) header.buyerNote = `The note can be at most ${QUOTE_NOTE_MAX} characters.`;
  if (f.internalNote.length > QUOTE_NOTE_MAX) header.internalNote = `The note can be at most ${QUOTE_NOTE_MAX} characters.`;
  if (f.lines.length > QUOTE_EDITOR_MAX_LINES) header.lines = `A quote has at most ${QUOTE_EDITOR_MAX_LINES} lines.`;
  f.lines.forEach((l, i) => { const p = lineProblem(l); if (p) lines[i + 1] = p; });
  if (f.shippingMode === 'fixed') {
    if (!isEditorGuid(f.shipToSiteId)) header.shipToSiteId = 'Fixed delivery needs the company\'s delivery site. Choose one.';
    if (f.fees.length === 0) header.shippingFees = 'Fixed delivery needs one fee for each seller of the lines.';
    const seen = new Set<string>();
    f.fees.forEach((fee, i) => {
      const id = fee.sellerId.trim().toLowerCase();
      const minor = poishaFromTaka(fee.feeText);
      if (!isEditorGuid(id)) fees[i] = 'This fee has no seller. Reload the page.';
      else if (seen.has(id)) fees[i] = 'This seller already has a fee.';
      else if (minor === null) fees[i] = 'Enter the fee in taka, zero or more, at most two decimals.';
      else if (minor > QUOTE_MAX_PRICE_MINOR) fees[i] = 'The fee is above the maximum.';
      seen.add(id);
    });
  }
  const count = Object.keys(header).length + Object.keys(lines).length + Object.keys(fees).length;
  return { header, lines, fees, count };
}

// ---------- the request body ----------

export interface QuoteDraftBody {
  rowVersion?: number;
  contactUserId?: string;
  validityDays: number;
  paymentTerms: string;
  shippingMode: string;
  shipToSiteId?: string;
  shippingFees?: { sellerId: string; shippingMinor: number }[];
  termsText?: string;
  buyerNote?: string;
  internalNote?: string;
  lines: { variantId: string; quantity: number; unitPriceMinor: number; lineNote?: string; belowFloorReason?: string; rfqLineId?: string }[];
}

/**
 * The body of the PUT (with `rowVersion`) and of the price preview (without). Null when the form has problems: nothing half-built is
 * ever sent. Blank notes and reasons are left out; a reason is sent whenever one is typed (the server decides if it is needed).
 * `contactUserId` is sent only when changed. Calculated delivery sends no site and no fees (the server refuses them).
 */
export function buildDraftBody(f: EditorForm, rowVersion?: number): QuoteDraftBody | null {
  if (editorProblems(f).count > 0) return null;
  const body: QuoteDraftBody = {
    validityDays: Number(f.validityDays.trim()), paymentTerms: f.paymentTerms, shippingMode: f.shippingMode,
    lines: f.lines.map((l) => ({
      variantId: l.variantId, quantity: Number(l.quantity.trim()), unitPriceMinor: poishaFromTaka(l.priceText) as number,
      ...(l.lineNote.trim() ? { lineNote: l.lineNote.trim() } : {}), ...(l.reason.trim() ? { belowFloorReason: l.reason.trim() } : {}), ...(l.rfqLineId ? { rfqLineId: l.rfqLineId } : {})
    }))
  };
  if (rowVersion !== undefined) body.rowVersion = rowVersion;
  const contact = f.contactUserId.trim().toLowerCase();
  if (contact && contact !== f.originalContactUserId.trim().toLowerCase()) body.contactUserId = contact;
  if (f.shippingMode === 'fixed') {
    body.shipToSiteId = f.shipToSiteId.trim();
    body.shippingFees = f.fees.map((x) => ({ sellerId: x.sellerId.trim(), shippingMinor: poishaFromTaka(x.feeText) as number }));
  }
  if (f.termsText.trim()) body.termsText = f.termsText.trim();
  if (f.buyerNote.trim()) body.buyerNote = f.buyerNote.trim();
  if (f.internalNote.trim()) body.internalNote = f.internalNote.trim();
  return body;
}

/** The body without the token: two equal keys mean the preview need not be asked again. */
export const previewKey = (body: QuoteDraftBody): string => JSON.stringify({ ...body, rowVersion: undefined });

/** True when the screen holds something the server does not (compares the bodies without the token). */
export function isDirty(current: EditorForm, saved: EditorForm): boolean {
  const a = buildDraftBody(current);
  const b = buildDraftBody(saved);
  if (a && b) return previewKey(a) !== previewKey(b);
  return JSON.stringify(current) !== JSON.stringify(saved);
}

// ---------- the preview ----------

export interface QuotePreviewCost { costMinorAtCheck: number | null; floorMinorAtCheck: number | null }
export interface QuotePreviewLine {
  lineNo: number; variantId: string; sku: string; titleEn: string; quantity: number; unitPriceMinor: number; vatRate: number;
  lineNetMinor: number; lineTaxMinor: number; lineTotalMinor: number; publicUnitPriceMinor: number; referenceUnitPriceMinor: number | null;
  referenceSource: string | null; belowFloor: boolean; overAuthority: boolean; belowCost: boolean; reasonRequired: boolean;
  /** Absent unless the signed-in person holds pricing.view_cost. */
  cost?: QuotePreviewCost | null;
}
export interface QuotePricePreview {
  versionNo: number;
  outcome: 'send' | 'approval' | 'refused' | string;
  problems: string[];
  belowFloorLineNos: number[]; overAuthorityLineNos: number[]; belowCostLineNos: number[]; reasonRequiredLineNos: number[];
  longValidity: boolean;
  goodsNetMinor: number; taxMinor: number; goodsTotalMinor: number; shippingTotalMinor: number | null; grandTotalMinor: number | null;
  lines: QuotePreviewLine[];
}

const PROBLEM_TEXT: Record<string, string> = {
  NO_LINES: 'The quote has no line yet. Add at least one.',
  SHIPPING_FEES_INCOMPLETE: 'Fixed delivery needs a fee for every seller whose goods are on the quote.',
  QUOTE_BELOW_COST: 'A price is below cost. The system will not send that, so raise it.',
  BELOW_FLOOR_REASON_REQUIRED: 'A line below the margin floor needs a reason of at least 5 characters.'
};
export const previewProblemText = (code: string): string => PROBLEM_TEXT[code] ?? code.replace(/_/g, ' ').toLowerCase();

export type OutcomeTone = 'ok' | 'approval' | 'refused';
/** What a send of this content would do, in words. Never a colour alone: the tone only picks emphasis. */
export function outcomeWords(outcome: string): { tone: OutcomeTone; text: string } {
  if (outcome === 'send') return { tone: 'ok', text: 'It could be sent straight away: nothing needs a second person.' };
  if (outcome === 'approval') return { tone: 'approval', text: 'Sending it would first need approval from a second person, because of the checks below.' };
  return { tone: 'refused', text: 'It cannot be sent as it is. Fix the problems below.' };
}

const lineList = (nos: readonly number[]): string => [...nos].sort((a, b) => a - b).join(', ');

/** The flags of a preview as sentences ("Line 2 is below the margin floor."), one per flag kind. */
export function previewFlagWords(p: Pick<QuotePricePreview, 'belowFloorLineNos' | 'belowCostLineNos' | 'overAuthorityLineNos' | 'longValidity'>): string[] {
  const out: string[] = [];
  if (p.belowCostLineNos.length > 0) out.push(`Below cost on line ${lineList(p.belowCostLineNos)}.`);
  if (p.belowFloorLineNos.length > 0) out.push(`Below the margin floor on line ${lineList(p.belowFloorLineNos)}.`);
  if (p.overAuthorityLineNos.length > 0) out.push(`Deeper discount than the preparer's price authority allows on line ${lineList(p.overAuthorityLineNos)}.`);
  if (p.longValidity) out.push(`Valid for more than ${QUOTE_VALIDITY_USUAL} days.`);
  return out;
}

/** Per-line statements, keyed by line number. */
export function previewLineNotes(p: QuotePricePreview): Map<number, string[]> {
  const m = new Map<number, string[]>();
  const add = (n: number, t: string) => m.set(n, [...(m.get(n) ?? []), t]);
  p.belowCostLineNos.forEach((n) => add(n, 'Below cost'));
  p.belowFloorLineNos.forEach((n) => add(n, 'Below the margin floor'));
  p.overAuthorityLineNos.forEach((n) => add(n, 'Beyond your price authority'));
  return m;
}

/** True when this line must carry a reason before the quote can be sent (the preview says so). */
export const lineNeedsReason = (p: Pick<QuotePricePreview, 'reasonRequiredLineNos'> | null, lineNo: number): boolean => !!p && p.reasonRequiredLineNos.includes(lineNo);

/** True when the API sent a cost object on at least one preview line (then the cost and floor columns are shown). Never inferred from permissions. */
export const previewHasCost = (p: Pick<QuotePricePreview, 'lines'> | null): boolean => !!p && p.lines.some((l) => l.cost != null);

const SOURCE_TEXT: Record<string, string> = {
  list: 'Public list price', sale: 'Sale price', quantity_break: 'Quantity break', group_list: 'Group list price', contract: 'Contract price', quote: 'Earlier quote'
};
/** "Where the buyer's own price comes from". Unknown codes are shown as the words in the code. */
export function referenceSourceText(source: string | null | undefined): string {
  if (!source) return '';
  return SOURCE_TEXT[source.toLowerCase().replace(/[-\s]/g, '_')] ?? source.replace(/[_-]/g, ' ').replace(/^./, (c) => c.toUpperCase());
}

/** How far below the buyer's own price this line is, as "12% below", or '' when it is not below. Whole percent, integer maths. */
export function discountWords(unitPriceMinor: number, referenceMinor: number | null | undefined): string {
  if (!referenceMinor || referenceMinor <= 0 || unitPriceMinor >= referenceMinor) return '';
  const pct = Math.floor(((referenceMinor - unitPriceMinor) * 100) / referenceMinor);
  return pct >= 1 ? `${pct}% below the buyer's price` : 'Less than 1% below the buyer\'s price';
}

// ---------- errors ----------

const EDITOR_ERROR_TEXT: Record<string, string> = {
  QUOTE_VERSION_CHANGED: 'Someone else saved this draft after you opened it, so your save was not applied. Your edits are still on the screen. Load the saved version to see what changed (this replaces your edits), or copy what you need first.',
  QUOTE_NOT_EDITABLE: 'This version is no longer a draft (it was sent, approved, withdrawn or closed), so it cannot be edited. Load the quote to see its state.',
  QUOTE_CONTACT_LOCKED: 'The buyer contact cannot be changed while a version waits for approval, is sent or is accepted. Withdraw or close that version first, or keep the contact as it is.',
  QUOTE_CONFLICT_OF_INTEREST: 'You belong to this company, so you may not edit its quotes. Ask a colleague who does not work for it.',
  QUOTE_CLOSED: 'This quote is closed, so it cannot be edited.',
  VARIANT_UNAVAILABLE: 'A part on the quote is no longer for sale. Remove it or replace it, then save again.'
};

export interface EditorFailure { status: number | null; code: string | null; message?: string; fields: Record<string, string> }

/** Where a refused save left things: `stale` (someone saved first: offer Load saved), `gone` (not a draft or closed: nothing to edit). */
export function editorFailureKind(code: string | null | undefined): 'stale' | 'gone' | 'other' {
  if (code === 'QUOTE_VERSION_CHANGED') return 'stale';
  if (code === 'QUOTE_NOT_EDITABLE' || code === 'QUOTE_CLOSED') return 'gone';
  return 'other';
}

export function editorErrorText(e: EditorFailure): string {
  if (e.code && EDITOR_ERROR_TEXT[e.code]) return EDITOR_ERROR_TEXT[e.code];
  const first = Object.values(e.fields)[0];
  if (e.status === 400 && first) return first;
  if (e.status === null) return 'The server cannot be reached. Your edits are still on the screen. Check your connection and try again.';
  if (e.status === 401) return 'Your session ended. Sign in again. Your edits on this page are lost when you leave it.';
  if (e.status === 403) return 'You do not have permission to edit this quote.';
  if (e.status === 404) return 'This quote or version was not found.';
  if (e.status === 429) return 'Too many requests. Wait a moment and try again.';
  if (e.status >= 500) return 'The server had a problem. Your edits are still on the screen. Try again in a moment.';
  return e.message || 'The request was refused.';
}

/**
 * The server's field names of a refused save or preview, sorted into the form: `lines[3]` is line 3, `shippingFees`, `shipToSiteId`,
 * `validityDays`, `termsText`, `buyerNote`, `internalNote`, `contactUserId` stay header fields. Anything else is returned as `general`.
 */
export function sortServerFields(fields: Record<string, string>): { header: Record<string, string>; lines: Record<number, string>; general: string[] } {
  const header: Record<string, string> = {};
  const lines: Record<number, string> = {};
  const general: string[] = [];
  const known = new Set(['validityDays', 'paymentTerms', 'shippingMode', 'shipToSiteId', 'shippingFees', 'termsText', 'buyerNote', 'internalNote', 'contactUserId', 'lines']);
  for (const [k, v] of Object.entries(fields)) {
    const m = /^lines\[(\d+)\]/.exec(k);
    if (m) lines[Number(m[1])] = v;
    else if (known.has(k.split('.')[0].split('[')[0])) header[k.split('.')[0].split('[')[0]] = v;
    else general.push(v);
  }
  return { header, lines, general };
}

/** The draft that can be edited: the latest version, when it is a Draft. */
export function editableVersion<V extends { versionNo: number; status: string }>(versions: readonly V[]): V | null {
  let best: V | null = null;
  for (const v of versions) if (!best || v.versionNo > best.versionNo) best = v;
  return best && best.status === 'draft' ? best : null;
}

/** True while any version waits for approval, is sent or is accepted: the server then refuses a contact change (409 QUOTE_CONTACT_LOCKED). */
export const contactLocked = (versions: readonly { status: string }[]): boolean => versions.some((v) => v.status === 'pending_approval' || v.status === 'sent' || v.status === 'accepted');
