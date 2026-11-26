import * as React from 'react';
import type { Metadata } from 'next';
import { getTranslations } from 'next-intl/server';
import { CompanyInvoicesView } from '../../../../../../components/invoices/CompanyInvoicesView';

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations('Invoices');
  return { title: t('companyMetaTitle'), robots: { index: false, follow: false } };
}

/** Private, per-person page: only the shell is rendered on the server; the list is fetched in the browser with no-store (E6.8b). */
export default async function CompanyInvoicesPage({ params }: { params: Promise<{ companyId: string }> }) {
  const { companyId } = await params;
  return (
    <React.Suspense fallback={null}>
      <CompanyInvoicesView companyId={decodeURIComponent(companyId)} />
    </React.Suspense>
  );
}
