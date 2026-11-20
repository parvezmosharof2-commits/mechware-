import type { Metadata } from 'next';
import { getTranslations } from 'next-intl/server';
import { QuoteListView } from '../../../../../../components/quotes/QuoteListView';

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations('BuyerQuotes');
  return { title: t('listMetaTitle'), robots: { index: false, follow: false } };
}

/** Private, per-person page: only the shell is rendered on the server; the list is fetched in the browser with no-store (D4.10). */
export default async function CompanyQuotesPage({ params }: { params: Promise<{ companyId: string }> }) {
  const { companyId } = await params;
  return <QuoteListView companyId={decodeURIComponent(companyId)} />;
}
