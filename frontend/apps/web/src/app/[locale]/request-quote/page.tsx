import type { Metadata } from 'next';
import { getTranslations } from 'next-intl/server';
import { RequestQuoteView } from '../../../components/quotes/RequestQuoteView';

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations('Quotes');
  return { title: t('requestMetaTitle'), robots: { index: false, follow: false } };
}

/** Private, per-person page: the server sends only the shell; the company, sites and the request itself are handled in the browser (no-store). */
export default function RequestQuotePage() {
  return <RequestQuoteView />;
}
