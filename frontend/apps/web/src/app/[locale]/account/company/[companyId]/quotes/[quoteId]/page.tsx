import type { Metadata } from 'next';
import { getTranslations } from 'next-intl/server';
import { QuoteDetailView } from '../../../../../../../components/quotes/QuoteDetailView';

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations('BuyerQuotes');
  return { title: t('detailMetaTitle'), robots: { index: false, follow: false } };
}

export default async function CompanyQuotePage({ params }: { params: Promise<{ companyId: string; quoteId: string }> }) {
  const { companyId, quoteId } = await params;
  return <QuoteDetailView companyId={decodeURIComponent(companyId)} quoteId={decodeURIComponent(quoteId)} />;
}
