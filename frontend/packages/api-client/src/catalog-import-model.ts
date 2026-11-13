/** Shapes and pure helpers for the bulk catalogue import (docs/catalog/IMPORT_FORMAT.md). */

export const IMPORT_MAX_BYTES = 10 * 1024 * 1024;

export interface ImportError {
  /** Spreadsheet row (header = 1); 0 means the whole file or the header. */
  row: number;
  column: string | null;
  code: string;
  message: string;
}

export interface ImportReport {
  rows: number;
  validRows: number;
  invalidRows: number;
  newProducts: number;
  existingProducts: number;
  newVariants: number;
  existingVariants: number;
  errorCount: number;
  errorsTruncated: boolean;
  errors: ImportError[];
}

export interface ImportCommitResult {
  sha256: string;
  rows: number;
  createdProducts: number;
  createdVariants: number;
  updatedProducts: number;
  updatedVariants: number;
  unchangedProducts: number;
  unchangedVariants: number;
}

export type ImportFileProblem = 'file.missing' | 'file.notCsv' | 'file.empty' | 'file.tooLarge';

/** Client-side pre-checks; the API repeats them. */
export function checkImportFile(file: { name: string; size: number } | null | undefined): ImportFileProblem | null {
  if (!file) return 'file.missing';
  if (!/\.csv$/i.test(file.name)) return 'file.notCsv';
  if (file.size === 0) return 'file.empty';
  if (file.size > IMPORT_MAX_BYTES) return 'file.tooLarge';
  return null;
}

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export function isGuid(value: string): boolean {
  return GUID.test(value.trim());
}

/** Cells a spreadsheet would run as a formula get a leading single quote. */
export function csvCell(value: unknown): string {
  let text = value === null || value === undefined ? '' : String(value);
  if (/^[=+\-@\t\r]/.test(text)) text = `'${text}`;
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

/** The error list as CSV (header row, CRLF line ends, formula-safe cells). */
export function errorsToCsv(errors: readonly ImportError[]): string {
  const lines = [['row', 'column', 'code', 'message'].join(',')];
  for (const e of errors) lines.push([e.row, e.column ?? '', e.code, e.message].map(csvCell).join(','));
  return lines.join('\r\n') + '\r\n';
}
