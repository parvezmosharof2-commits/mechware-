import type { Metadata } from 'next';
import { getTranslations } from 'next-intl/server';
import { RfqListView } from '../../../../../../components/quotes/RfqListView';

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations('Quotes');
  return { title: t('listMetaTitle'), robots: { index: false, follow: false } };
}

/** Private, per-person page: only the shell is rendered on the server; the list is fetched in the browser with no-store (D4.5). */
export default async function CompanyRfqsPage({ params }: { params: Promise<{ companyId: string }> }) {
  const { companyId } = await params;
  return <RfqListView companyId={decodeURIComponent(companyId)} />;
}
