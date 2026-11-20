'use client';

import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { useLocalePath } from '../company/shared';

/** Company dashboard entry to the company's requests for quote (the list decides who sees all and who sees only their own). */
export function RfqLinkCard({ companyId }: { companyId: string }) {
  const t = useTranslations('Quotes');
  const toPath = useLocalePath();
  return (
    <section aria-labelledby="rfq-card" className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-surface-border bg-bg-muted p-4">
      <div>
        <h2 id="rfq-card" className="text-base font-semibold">{t('listTitle')}</h2>
        <p className="text-sm text-text-muted">{t('cardBody')}</p>
      </div>
      <div className="flex flex-wrap gap-3 text-sm font-semibold">
        <Link prefetch={false} href={toPath(`/account/company/${encodeURIComponent(companyId)}/rfqs`)} className="text-accent hover:underline" data-testid="rfq-card-list">{t('openList')}</Link>
        <Link prefetch={false} href={toPath(`/account/company/${encodeURIComponent(companyId)}/quotes`)} className="text-accent hover:underline" data-testid="quote-card-list">{t('openQuotes')}</Link>
        <Link prefetch={false} href={toPath('/request-quote?source=text')} className="text-accent hover:underline">{t('askNew')}</Link>
      </div>
    </section>
  );
}
