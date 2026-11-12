import termsEn from './terms.en.json';
import termsBn from './terms.bn.json';
import privacyEn from './privacy.en.json';
import privacyBn from './privacy.bn.json';
import cookiesEn from './cookies.en.json';
import cookiesBn from './cookies.bn.json';
import returnsEn from './returns.en.json';
import returnsBn from './returns.bn.json';
import shippingEn from './shipping.en.json';
import shippingBn from './shipping.bn.json';
import creditEn from './credit.en.json';
import creditBn from './credit.bn.json';
import quoteTermsEn from './quote-terms.en.json';
import quoteTermsBn from './quote-terms.bn.json';

/**
 * Legal page texts. One JSON file per page and locale (`<slug>.<en|bn>.json`) so the lawyer's final text replaces a file
 * without any code change. Shape: `updated` (ISO date shown as "last updated"), `title`, optional `intro`, and
 * `sections`: `h` heading, `p` paragraphs, `l` bullet list, `after` paragraphs shown below the list.
 */
export type LegalSection = { h: string; p?: string[]; l?: string[]; after?: string[] };
export type LegalDoc = { updated: string; title: string; intro?: string; sections: LegalSection[] };

export const LEGAL_SLUGS = ['terms', 'privacy', 'cookies', 'returns', 'shipping', 'credit', 'quote-terms'] as const;
export type LegalSlug = (typeof LEGAL_SLUGS)[number];

/**
 * Set to true per page only after the lawyer has approved the text. It lifts the draft notice and `noindex` of that page.
 * The static sitemap lists the legal pages only when they are approved (see `sitemap-pages.ts`).
 */
export const LEGAL_APPROVED = false;

const DOCS: Record<LegalSlug, Record<'en' | 'bn', LegalDoc>> = {
  terms: { en: termsEn, bn: termsBn },
  privacy: { en: privacyEn, bn: privacyBn },
  cookies: { en: cookiesEn, bn: cookiesBn },
  returns: { en: returnsEn, bn: returnsBn },
  shipping: { en: shippingEn, bn: shippingBn },
  credit: { en: creditEn, bn: creditBn },
  'quote-terms': { en: quoteTermsEn, bn: quoteTermsBn }
};

export function isLegalSlug(value: string): value is LegalSlug {
  return (LEGAL_SLUGS as readonly string[]).includes(value);
}

export function legalDoc(slug: LegalSlug, locale: string): LegalDoc {
  return DOCS[slug][locale === 'bn' ? 'bn' : 'en'];
}
