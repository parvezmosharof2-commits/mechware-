/** Default quote terms (D4.21): the lines printed on every quote PDF, English and Bangla. Pure rules, no I/O. */

export const TERMS_MAX_LINES = 12;
export const TERMS_MAX_CHARS = 300;

export interface QuoteTerms {
  termsEn: string[];
  termsBn: string[];
  rowVersion: number;
  updatedAtUtc: string | null;
  updatedBy: string | null;
  updatedByName: string | null;
}
export interface QuoteTermsBody { termsEn: string[]; termsBn: string[]; rowVersion: number }
export type TermsLang = 'en' | 'bn';
export interface TermLine { key: number; text: string }
export interface TermsForm { en: TermLine[]; bn: TermLine[] }

export const canChangeTerms = (permissions: readonly string[]): boolean => permissions.some((p) => p.toLowerCase() === 'quotes.approve');

let keySeq = 0;
export const newTermLine = (text = ''): TermLine => ({ key: ++keySeq, text });
export const formFromTerms = (t: Pick<QuoteTerms, 'termsEn' | 'termsBn'>): TermsForm => ({ en: t.termsEn.map((x) => newTermLine(x)), bn: t.termsBn.map((x) => newTermLine(x)) });

/** Why a line cannot be saved, in words, or null. The server trims and checks again. */
export function termLineProblem(text: string): string | null {
  const n = text.trim().length;
  if (n === 0) return 'This line is empty. Write it or remove it.';
  if (n > TERMS_MAX_CHARS) return `This line is ${n - TERMS_MAX_CHARS} characters too long (at most ${TERMS_MAX_CHARS}).`;
  return null;
}
export const termCounter = (text: string): string => `${text.trim().length} / ${TERMS_MAX_CHARS}`;
export const canAddTerm = (lines: readonly TermLine[]): boolean => lines.length < TERMS_MAX_LINES;

export function termsProblems(f: TermsForm): { count: number; first: string | null } {
  let count = 0;
  let first: string | null = null;
  for (const [name, lines] of [['English', f.en], ['Bangla', f.bn]] as const) {
    lines.forEach((l, i) => {
      const p = termLineProblem(l.text);
      if (p) { count++; first ??= `${name} line ${i + 1}: ${p}`; }
    });
  }
  return { count, first };
}

export function moveTerm(lines: readonly TermLine[], index: number, by: -1 | 1): TermLine[] {
  const to = index + by;
  if (to < 0 || to >= lines.length) return [...lines];
  const next = [...lines];
  [next[index], next[to]] = [next[to]!, next[index]!];
  return next;
}
export const setTermText = (lines: readonly TermLine[], index: number, text: string): TermLine[] => lines.map((l, i) => (i === index ? { ...l, text } : l));
export const removeTerm = (lines: readonly TermLine[], index: number): TermLine[] => lines.filter((_, i) => i !== index);

const texts = (lines: readonly TermLine[]) => lines.map((l) => l.text.trim());
/** True when the lines differ from the saved ones (text and order; edge spaces do not count). */
export function termsDirty(form: TermsForm, base: TermsForm): boolean {
  return JSON.stringify([texts(form.en), texts(form.bn)]) !== JSON.stringify([texts(base.en), texts(base.bn)]);
}

/** The PUT body, or null while any line is invalid. */
export function termsBody(form: TermsForm, rowVersion: number): QuoteTermsBody | null {
  if (termsProblems(form).count > 0) return null;
  return { termsEn: texts(form.en), termsBn: texts(form.bn), rowVersion };
}

export const termsFailureKind = (code: string | null): 'stale' | 'other' => (code === 'QUOTE_TERMS_CHANGED' ? 'stale' : 'other');

export function termsErrorText(e: { status: number | null; code: string | null; message: string; fields: Record<string, string> }): string {
  if (e.code === 'QUOTE_TERMS_CHANGED') return 'Someone else changed these terms while you were editing. Your edits are still here and nothing of yours was saved.';
  const first = Object.values(e.fields)[0];
  if (e.status === 400) return first ?? 'The server refused these lines. Check the length of each line and try again.';
  if (e.status === null) return 'The server cannot be reached. Check your connection and try again.';
  if (e.status === 401) return 'Your session ended. Sign in again.';
  if (e.status === 403) return 'Your role may read these terms but not change them (quotes.approve).';
  if (e.status >= 500) return 'The server had a problem. Try again in a moment; if it keeps happening, tell the technical team.';
  return e.message || 'The request was refused.';
}
