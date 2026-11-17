import type { Metadata } from 'next';
import { getTranslations } from 'next-intl/server';
import { CreditPanelView } from '../../../../../../components/company/CreditPanelView';

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations('CompanyCredit');
  return { title: t('metaTitle'), robots: { index: false, follow: false } };
}

/** Private, per-person page: the server sends only the shell; the numbers are fetched in the browser with no-store (B3.9). */
export default async function CompanyCreditPage({ params }: { params: Promise<{ companyId: string }> }) {
  const { companyId } = await params;
  return <CreditPanelView companyId={decodeURIComponent(companyId)} />;
}
