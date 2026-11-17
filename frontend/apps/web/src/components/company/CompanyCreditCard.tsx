'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { Progress } from '@mechware/ui/src/components/Progress';
import { creditUsagePercent, hasCreditBalance, type CompanyDto } from '@mechware/api-client';
import { useLocalePath, useMoney } from './shared';

/**
 * Credit summary on the company dashboard. A member whose role may not see the balance gets null amounts from the API (B3.9): then there
 * is no number, no error and no link, only a neutral "managed by your company admin" text. Loaded on demand like the dashboard panels.
 */
export function CompanyCreditCard({ company: c }: { company: CompanyDto }) {
  const t = useTranslations('Company');
  const money = useMoney();
  const toPath = useLocalePath();
  const usage = creditUsagePercent(c);
  return (
    <section className="rounded-lg border border-border-subtle bg-bg-surface p-5" aria-labelledby="credit-heading">
      <h2 id="credit-heading" className="text-sm font-semibold">
        {t('credit.title')}: {t(`credit.terms.${c.creditTerms}`)}
      </h2>
      {!hasCreditBalance(c) ? (
        <p className="mt-2 text-xs text-fg-muted" data-testid="credit-hidden-note">
          {c.creditTerms === 'none' ? t('credit.managedByMechware') : t('credit.managedByAdmin')}
        </p>
      ) : (
        <>
          <div className="mt-3">
            <Progress value={usage} aria-label={t('credit.used')} variant={usage >= 90 ? 'fault' : usage >= 80 ? 'warning' : 'accent'} />
          </div>
          <dl className="mt-3 grid grid-cols-3 gap-3 text-sm">
            <div>
              <dt className="text-xs text-fg-muted">{t('credit.limit')}</dt>
              <dd className="font-semibold tabular-nums">{money(c.creditLimit)}</dd>
            </div>
            <div>
              <dt className="text-xs text-fg-muted">{t('credit.used')}</dt>
              <dd className="font-semibold tabular-nums">{money(c.creditUsed)}</dd>
            </div>
            <div>
              <dt className="text-xs text-fg-muted">{t('credit.available')}</dt>
              <dd className="font-semibold tabular-nums">{money(c.creditAvailable)}</dd>
            </div>
          </dl>
          <p className="mt-3">
            <Link href={toPath(`/account/company/${encodeURIComponent(c.id)}/credit`)} className="text-sm font-semibold text-brand-primary hover:underline">
              {t('credit.openPanel')}
            </Link>
          </p>
        </>
      )}
    </section>
  );
}
