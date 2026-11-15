// Quick-order view types and pure helpers (D5.3). The server (POST /api/v1/quick-order/validate, D5.2) is the only authority for
// part matching, prices and stock; everything here maps, groups and rewrites TEXT. Nothing computes money.

export const QUICK_ORDER_MAX_LINES = 500;
export const QUICK_ORDER_MAX_INPUT = 100_000;

export type QuickOrderStatus =
  | 'OK'
  | 'PART_NOT_FOUND'
  | 'PART_AMBIGUOUS'
  | 'PART_OBSOLETE'
  | 'OUT_OF_STOCK'
  | 'INSUFFICIENT_STOCK'
  | 'QTY_EXCEEDS_LIMIT';

export interface QuickOrderVariant {
  id: string;
  sku: string;
  partNumber: string | null;
  title: string;
  titleBn: string;
  brand: string | null;
  slug: string;
  options: Record<string, string>;
}

export interface QuickOrderLineNotice {
  kind: string;
  was: number | null;
  now: number | null;
}

export interface QuickOrderStock {
  available: number;
  status: 'in_stock' | 'low_stock' | 'out_of_stock' | (string & {});
}

export interface QuickOrderLine {
  lineNumbers: number[];
  raw: string;
  part: string;
  requestedQuantity: number;
  status: QuickOrderStatus | (string & {});
  message: string;
  valid: boolean;
  variant: QuickOrderVariant | null;
  quantity: number | null;
  notices: QuickOrderLineNotice[];
  stock: QuickOrderStock | null;
  unitPriceMinor: number | null;
  lineTotalMinor: number | null;
  priceSource: string | null;
  successor: { variant: QuickOrderVariant; isDropIn: boolean; note: string | null } | null;
  candidates: QuickOrderVariant[];
  suggestions: QuickOrderVariant[];
}

/** A parser issue: `lineNumber` is the physical line of the pasted text. Error lines have no entry in `lines`. */
export interface QuickOrderIssue {
  lineNumber: number;
  code: string;
  message: string;
  relatedLines: number[] | null;
}

export interface QuickOrderSummary {
  lines: number;
  valid: number;
  needsAttention: number;
  subtotalMinor: number;
  currency: string;
}

export interface QuickOrderResult {
  lines: QuickOrderLine[];
  errors: QuickOrderIssue[];
  notices: QuickOrderIssue[];
  summary: QuickOrderSummary;
}

export interface QuickOrderEnvelope {
  data: QuickOrderResult;
}

/** Message key (under `QuickOrder`) for a line status; an unknown status from a newer server gets a generic one. */
export function statusMessageKey(status: string): string {
  switch (status) {
    case 'OK':
      return 'status_ok';
    case 'PART_NOT_FOUND':
      return 'status_notFound';
    case 'PART_AMBIGUOUS':
      return 'status_ambiguous';
    case 'PART_OBSOLETE':
      return 'status_obsolete';
    case 'OUT_OF_STOCK':
      return 'status_outOfStock';
    case 'INSUFFICIENT_STOCK':
      return 'status_insufficientStock';
    case 'QTY_EXCEEDS_LIMIT':
      return 'status_qtyExceeds';
    default:
      return 'status_unknown';
  }
}

const ISSUE_CODES: ReadonlySet<string> = new Set([
  'LINE_TOO_LONG', 'PART_MISSING', 'PART_INVALID', 'PART_TOO_LONG', 'QTY_MISSING', 'QTY_INVALID',
  'QTY_NOT_INTEGER', 'QTY_TOO_LARGE', 'TOO_MANY_FIELDS'
]);

/** Message key for a parser issue code (the server's message is English only); unknown codes fall back to a generic text. */
export function issueMessageKey(code: string): string {
  return ISSUE_CODES.has(code) ? `issue_${code}` : 'issue_unknown';
}

/** Message key for a line notice kind. */
export function lineNoticeKey(kind: string): string {
  switch (kind) {
    case 'belowMinimumOrder':
      return 'notice_belowMinimumOrder';
    case 'quantityAdjustedToPack':
      return 'notice_quantityAdjustedToPack';
    case 'nrnd':
      return 'notice_nrnd';
    case 'sameVariantOnSeveralLines':
      return 'notice_sameVariantOnSeveralLines';
    default:
      return 'notice_unknown';
  }
}

export type QuickOrderTone = 'ok' | 'warning' | 'error';

/** Fine, a heads-up (still valid, but the server adjusted the quantity) or needing the buyer's attention. */
export function lineTone(line: Pick<QuickOrderLine, 'valid' | 'notices'>): QuickOrderTone {
  if (!line.valid) return 'error';
  return line.notices.some((n) => n.kind === 'belowMinimumOrder' || n.kind === 'quantityAdjustedToPack') ? 'warning' : 'ok';
}

/** A line the buyer can fix with one click (suggestions, candidates, successor, or the available stock). */
export function offersFix(line: QuickOrderLine): boolean {
  if (line.valid) return false;
  switch (line.status) {
    case 'PART_NOT_FOUND':
      return line.suggestions.length > 0;
    case 'PART_AMBIGUOUS':
      return line.candidates.length > 0;
    case 'PART_OBSOLETE':
      return line.successor !== null;
    case 'INSUFFICIENT_STOCK':
      return (line.stock?.available ?? 0) > 0;
    default:
      return false;
  }
}

export type QuickOrderRow =
  | { kind: 'line'; line: QuickOrderLine; order: number }
  | { kind: 'error'; issue: QuickOrderIssue; order: number };

/** Every result row (server lines and parser error lines) in the order of the pasted text. */
export function orderedRows(result: Pick<QuickOrderResult, 'lines' | 'errors'>): QuickOrderRow[] {
  const rows: QuickOrderRow[] = [
    ...result.lines.map((line): QuickOrderRow => ({ kind: 'line', line, order: line.lineNumbers[0] ?? 0 })),
    ...result.errors.map((issue): QuickOrderRow => ({ kind: 'error', issue, order: issue.lineNumber }))
  ];
  return rows.sort((a, b) => a.order - b.order);
}

/** Valid lines, and everything else ("needs attention", parser errors included) in text order. */
export function groupLines(result: Pick<QuickOrderResult, 'lines' | 'errors'>): {
  valid: QuickOrderLine[];
  attention: QuickOrderRow[];
} {
  const valid = result.lines.filter((l) => l.valid).sort((a, b) => (a.lineNumbers[0] ?? 0) - (b.lineNumbers[0] ?? 0));
  const attention = orderedRows(result).filter((r) => (r.kind === 'line' ? !r.line.valid : true));
  return { valid, attention };
}

/** Counts for the summary and the live region. Parser errors count as needing attention. */
export function summarize(result: Pick<QuickOrderResult, 'errors' | 'summary'>): { valid: number; needsAttention: number } {
  return { valid: result.summary.valid, needsAttention: result.summary.needsAttention + result.errors.length };
}

function splitLines(text: string): string[] {
  return text.split(/\r\n|\n|\r/);
}

/** Part number and quantity as ONE line the parser reads back unambiguously: `part, qty` (the part quoted if it holds a comma, semicolon or quote). */
export function formatLine(part: string, quantity: number): string {
  const clean = part.replace(/[\r\n\t]+/g, ' ').trim();
  const needsQuotes = /[",;]/.test(clean);
  return `${needsQuotes ? `"${clean.replace(/"/g, '""')}"` : clean}, ${Math.max(1, Math.trunc(quantity))}`;
}

/**
 * Replaces the physical lines `lineNumbers` (1-based) of `text` with one rewritten line. The first number gets the new text,
 * the others (merged duplicates) become blank so every other line keeps its number. Unknown numbers are ignored.
 */
export function rewriteLines(text: string, lineNumbers: readonly number[], replacement: string): string {
  const lines = splitLines(text);
  lineNumbers.forEach((n, i) => {
    if (n >= 1 && n <= lines.length) lines[n - 1] = i === 0 ? replacement : '';
  });
  return lines.join('\n');
}

/** Removes the physical lines `lineNumbers` (1-based) from the text, e.g. the lines that were just added to the cart. */
export function removeLines(text: string, lineNumbers: readonly number[]): string {
  const drop = new Set(lineNumbers);
  return splitLines(text).filter((_, i) => !drop.has(i + 1)).join('\n');
}

/** Fix a line's part number (suggestion, candidate, successor), keeping the quantity the buyer asked for unless one is given. */
export function fixPart(text: string, line: Pick<QuickOrderLine, 'lineNumbers' | 'requestedQuantity'>, newPart: string, quantity?: number): string {
  return rewriteLines(text, line.lineNumbers, formatLine(newPart, quantity ?? line.requestedQuantity));
}

/** Change a line's quantity, keeping the part exactly as typed. */
export function fixQuantity(text: string, line: Pick<QuickOrderLine, 'lineNumbers' | 'part'>, quantity: number): string {
  return rewriteLines(text, line.lineNumbers, formatLine(line.part, quantity));
}

/** The part number a fix writes into the text: the real part number if the variant has one, else its SKU. */
export function variantPartText(variant: Pick<QuickOrderVariant, 'sku' | 'partNumber'>): string {
  return variant.partNumber?.trim() || variant.sku;
}

/** The most the buyer can order of an INSUFFICIENT_STOCK line (the server's available count), or null. */
export function maxAvailableFix(line: Pick<QuickOrderLine, 'status' | 'stock'>): number | null {
  if (line.status !== 'INSUFFICIENT_STOCK') return null;
  const a = line.stock?.available ?? 0;
  return a > 0 ? a : null;
}

export interface CartBatchItem {
  variantId: string;
  quantity: number;
  /** The text lines this came from, for reporting. */
  lineNumbers: number[];
  label: string;
}

/** The cart calls behind "Add valid lines": one per valid line, with the server's (order-unit adjusted) quantity. */
export function buildCartBatch(lines: readonly QuickOrderLine[]): CartBatchItem[] {
  const out: CartBatchItem[] = [];
  for (const l of lines) {
    if (!l.valid || !l.variant || !l.quantity || l.quantity < 1) continue;
    out.push({ variantId: l.variant.id, quantity: l.quantity, lineNumbers: l.lineNumbers, label: variantPartText(l.variant) });
  }
  return out;
}

export type QuickOrderErrorKind = 'tooLarge' | 'tooManyLines' | 'invalid' | 'rateLimited' | 'unavailable' | 'other';

/** Buyer-facing class of a failed validate call. No answer at all (network) or a 5xx is `unavailable`: try again. */
export function classifyQuickOrderError(error: unknown): QuickOrderErrorKind {
  const e = (typeof error === 'object' && error !== null ? error : {}) as { status?: number; error?: { code?: string } };
  const status = typeof e.status === 'number' ? e.status : null;
  const code = e.error?.code ?? '';
  if (status === null || status >= 500) return 'unavailable';
  if (status === 413 || code === 'INPUT_TOO_LARGE') return 'tooLarge';
  if (code === 'TOO_MANY_LINES') return 'tooManyLines';
  if (status === 422 || code === 'INPUT_INVALID') return 'invalid';
  if (status === 429) return 'rateLimited';
  return 'other';
}

export type AddOutcome = { item: CartBatchItem; ok: true } | { item: CartBatchItem; ok: false; reason: string };

/** Counts for the result of adding a batch. */
export function addSummary(outcomes: readonly AddOutcome[]): { added: number; failed: number } {
  const added = outcomes.filter((o) => o.ok).length;
  return { added, failed: outcomes.length - added };
}
