'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { Badge, Button, EmptyState } from '@mechware/ui';
import { roleNames, type CompanyStatus } from '@mechware/api-client';
import { companiesApi } from '../../lib/api';
import { useResource } from '../../lib/useResource';
import { ListSkeleton, LoadError, PageShell, RequireSignIn, useLocalePath } from './shared';

export function StatusBadge({ status }: { status: CompanyStatus }) {
  const t = useTranslations('Company.status');
  const variant = status === 'active' ? 'success' : status === 'pending' ? 'low-stock' : 'deal';
  return <Badge variant={variant}>{t(status)}</Badge>;
}

function List() {
  const t = useTranslations('Company');
  const toPath = useLocalePath();
  const mine = useResource(async () => (await companiesApi.listMine()).data, 'mine');

  if (mine.loading) return <ListSkeleton />;
  if (mine.error) return <LoadError error={mine.error} onRetry={mine.reload} />;

  const items = mine.data ?? [];
  if (items.length === 0) {
    return (
      <EmptyState
        title={t('listEmptyTitle')}
        description={t('listEmptyBody')}
        action={
          <Link href={toPath('/account/company/register')}>
            <Button>{t('registerCta')}</Button>
          </Link>
        }
      />
    );
  }

  return (
    <>
      <ul className="space-y-3">
        {items.map(({ company, roles }) => (
          <li key={company.id} className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-border-subtle bg-bg-surface p-4">
            <div className="min-w-0">
              <div className="flex flex-wrap items-center gap-2">
                <span className="truncate font-semibold">{company.legalName}</span>
                <StatusBadge status={company.status} />
              </div>
              <div className="mt-1 text-xs text-fg-muted">
                {t('yourRoles')}: {roleNames(roles).map((r) => t(`roles.${r}`)).join(', ')}
              </div>
            </div>
            <Link href={toPath(`/account/company/${company.id}`)}>
              <Button variant="outline" size="sm">
                {t('open')}
              </Button>
            </Link>
          </li>
        ))}
      </ul>
      <div className="mt-6">
        <Link href={toPath('/account/company/register')}>
          <Button variant="secondary">{t('registerCta')}</Button>
        </Link>
      </div>
    </>
  );
}

export function CompanyListView() {
  const t = useTranslations('Company');
  return (
    <PageShell title={t('myCompanies')} subtitle={t('subtitle')}>
      <RequireSignIn>
        <List />
      </RequireSignIn>
    </PageShell>
  );
}
