import { LEGAL_APPROVED, LEGAL_SLUGS } from '../content/legal';

/**
 * Static pages for the sitemap. The legal pages are left out on purpose while their text is a draft: they are `noindex`
 * and must not be advertised to crawlers. They join automatically once `LEGAL_APPROVED` is set after the lawyer's sign-off.
 */
export function staticPagePaths(): string[] {
  return ['/', '/help', ...(LEGAL_APPROVED ? LEGAL_SLUGS.map((s) => `/legal/${s}`) : [])];
}
