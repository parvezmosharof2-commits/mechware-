import type { Metadata } from 'next';
import { getTranslations } from 'next-intl/server';
import { RfqDetailView } from '../../../../../../../components/quotes/RfqDetailView';

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations('Quotes');
  return { title: t('detailMetaTitle'), robots: { index: false, follow: false } };
}

export default async function CompanyRfqPage({ params }: { params: Promise<{ companyId: string; rfqId: string }> }) {
  const { companyId, rfqId } = await params;
  return <RfqDetailView companyId={decodeURIComponent(companyId)} rfqId={decodeURIComponent(rfqId)} />;
}
