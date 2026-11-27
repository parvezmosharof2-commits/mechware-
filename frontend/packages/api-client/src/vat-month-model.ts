/**
 * The monthly Mushak-9.1 working file for staff (E6.7b). Pure shapes and helpers, no runtime imports (Node's type stripping must be
 * able to test it). Hand-written from `VatExportContracts.cs`. Amounts are whole poisha. The API checks `invoices.export`, the rate
 * limit and the authenticator code again; this only helps the person. The VAT consultant has not checked the layout yet.
 */
import type { VatVdsRow } from "./withholdings-staff-model";
export interface VatSaleRow { invoiceId: string; number: string; issuedAtDhaka: string; orderNumber: string; buyerName: string; buyerBin: string | null; netMinor: number; sdMinor: number; vatMinor: number; totalMinor: number; vatBand: string }
export interface VatCreditNoteRow { invoiceId: string; number: string; issuedAtDhaka: string; originalNumber: string; originalMonth: string; buyerName: string; buyerBin: string | null; netMinor: number; sdMinor: number; vatMinor: number; totalMinor: number; vatBand: string }
export interface VatRateTotal { band: string; vatRate: number; salesNetMinor: number; salesSdMinor: number; salesVatMinor: number; creditNetMinor: number; creditSdMinor: number; creditVatMinor: number }
export interface VatMushak610Row { kind: string; number: string; issuedAtDhaka: string; buyerName: string; buyerBin: string | null; totalMinor: number }
export interface VatPaidNotShippedRow { orderId: string; orderNumber: string; status: string; paidAtDhaka: string; paidMinor: number }
export interface VatMonthTotals { sales: number; salesNetMinor: number; salesSdMinor: number; salesVatMinor: number; salesTotalMinor: number; creditNotes: number; creditNetMinor: number; creditSdMinor: number; creditVatMinor: number; creditTotalMinor: number; netVatMinor: number }
export interface VatMonthReport {
  month: string; sales: VatSaleRow[]; creditNotes: VatCreditNoteRow[]; withholdingsAvailable: boolean; vds?: VatVdsRow[]; rateTotals: VatRateTotal[];
  mushak610ThresholdMinor: number; mushak610Candidates: VatMushak610Row[]; paidNotShipped: VatPaidNotShippedRow[]; totals: VatMonthTotals; consultantChecked: boolean;
}

export const VAT_EXPORT_PERMISSION = 'invoices.export';
export const canExportVat = (permissions: readonly string[]): boolean => permissions.some((p) => p.toLowerCase() === VAT_EXPORT_PERMISSION);

/** BDT text with two decimals and thousands separators ("1,250.50"). */
export function vatBdt(minor: number): string {
  const abs = Math.abs(minor);
  return `${minor < 0 ? '-' : ''}${Math.floor(abs / 100).toLocaleString('en-US')}.${String(abs % 100).padStart(2, '0')}`;
}

export const isVatMonth = (v: string): boolean => /^\d{4}-(0[1-9]|1[0-2])$/.test(v) && Number(v.slice(0, 4)) >= 2000;

/** Last calendar month in Dhaka (UTC+6) as yyyy-MM. */
export function lastMonthDhaka(now: number = Date.now()): string {
  const d = new Date(now + 6 * 3_600_000);
  const m = d.getUTCMonth();
  return `${m === 0 ? d.getUTCFullYear() - 1 : d.getUTCFullYear()}-${String(m === 0 ? 12 : m).padStart(2, '0')}`;
}

export const vatMonthQuery = (month: string, format: 'json' | 'csv'): string => `month=${encodeURIComponent(month)}&format=${format}`;
export const vatCsvName = (month: string): string => `mushak-9_1-${month}.csv`;

const BAND_TEXT: Record<string, string> = { standard: 'Standard (15%)', reduced: 'Reduced (under 15%)', zero_or_exempt: 'Zero-rated or exempt (0%)', other: 'Other', mixed: 'Mixed rates' };
export const vatBandText = (band: string): string => BAND_TEXT[band] ?? band;

/** 0.15 and 15 both read as "15%". */
export const vatRateText = (rate: number): string => `${+(rate <= 1 ? rate * 100 : rate).toFixed(2)}%`;

export const vatMonthCounts = (r: VatMonthReport): string =>
  `${r.totals.sales} sales invoice${r.totals.sales === 1 ? '' : 's'} and ${r.totals.creditNotes} credit note${r.totals.creditNotes === 1 ? '' : 's'}`;

/** Plain words for a refused 9.1 call (a wrong code is shown inside the dialog instead). */
export function vatMonthErrorText(e: { status: number | null; message?: string }): string {
  if (e.status === 429) return 'You asked for this too many times in a minute (6 a minute). Wait a minute and try again.';
  if (e.status === 403) return 'You do not have permission to export the VAT return. Ask an Admin to give you the Finance role (invoices.export).';
  if (e.status === 400) return 'Choose a month in the form year-month, for example 2026-10.';
  if (e.status === 401) return 'Your session ended. Sign in again.';
  if (e.status === null) return 'The server cannot be reached. Check your connection and try again.';
  if (e.status >= 500) return 'The server had a problem. Try again in a moment; if it keeps happening, tell the technical team.';
  return e.message || 'The request was refused.';
}
