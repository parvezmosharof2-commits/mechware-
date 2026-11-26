import type { Metadata } from 'next';
import { getTranslations } from 'next-intl/server';
import { CompanyInvoiceDetailView } from '../../../../../../../components/invoices/CompanyInvoiceDetailView';

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations('Invoices');
  return { title: t('detailMetaTitle'), robots: { index: false, follow: false } };
}

export default async function CompanyInvoiceDetailPage({ params }: { params: Promise<{ companyId: string; invoiceId: string }> }) {
  const { companyId, invoiceId } = await params;
  return <CompanyInvoiceDetailView companyId={decodeURIComponent(companyId)} invoiceId={decodeURIComponent(invoiceId)} />;
}
