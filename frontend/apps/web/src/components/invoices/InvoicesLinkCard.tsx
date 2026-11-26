'use client';

import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { useLocalePath } from '../company/shared';

/** Company dashboard entry to the company's invoices. The dashboard shows it only to Admin and Buyer members (the roles the API allows). */
export function InvoicesLinkCard({ companyId }: { companyId: string }) {
  const t = useTranslations('Invoices');
  const toPath = useLocalePath();
  return (
    <section aria-labelledby="invoices-card" className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-surface-border bg-bg-muted p-4">
      <div>
        <h2 id="invoices-card" className="text-base font-semibold">{t('companyTitle')}</h2>
        <p className="text-sm text-text-muted">{t('cardBody')}</p>
      </div>
      <Link prefetch={false} href={toPath(`/account/company/${encodeURIComponent(companyId)}/invoices`)} className="text-sm font-semibold text-accent hover:underline" data-testid="invoices-card-link">{t('openList')}</Link>
    </section>
  );
}
