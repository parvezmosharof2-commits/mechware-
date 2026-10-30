'use client';

import * as React from 'react';
import { useTranslations } from 'next-intl';
import { AlertBanner, Progress, Tabs, TabsContent, TabsList, TabsTrigger } from '@mechware/ui';
import { CompanyRoles, creditUsagePercent, hasRole, roleNames } from '@mechware/api-client';
import { companiesApi } from '../../lib/api';
import { useResource } from '../../lib/useResource';
import { StatusBadge } from './CompanyListView';
import { CostCentresPanel } from './CostCentresPanel';
import { MembersPanel } from './MembersPanel';
import { SitesPanel } from './SitesPanel';
import { ListSkeleton, LoadError, PageShell, RequireSignIn, useLocalePath, useMoney } from './shared';

function Dashboard({ companyId }: { companyId: string }) {
  const t = useTranslations('Company');
  const money = useMoney();
  const company = useResource(async () => (await companiesApi.get(companyId)).data, companyId);
  const mine = useResource(async () => (await companiesApi.listMine()).data, `mine:${companyId}`);

  if (company.loading || mine.loading) return <ListSkeleton />;
  if (company.error) return <LoadError error={company.error} onRetry={company.reload} />;
  if (mine.error) return <LoadError error={mine.error} onRetry={mine.reload} />;
  const c = company.data!;
  const roles = mine.data?.find((m) => m.company.id === companyId)?.roles ?? 0;
  const isAdmin = hasRole(roles, CompanyRoles.Admin);
  const suspended = c.status === 'suspended';
  const canWrite = isAdmin && !suspended;
  const usage = creditUsagePercent(c);

  return (
    <div className="space-y-6">
      <section className="rounded-lg border border-border-subtle bg-bg-surface p-5">
        <div className="flex flex-wrap items-center gap-2">
          <h2 className="text-lg font-semibold">{c.legalName}</h2>
          <StatusBadge status={c.status} />
        </div>
        <dl className="mt-3 grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
          <div>
            <dt className="text-xs text-fg-muted">{t('register.tradeLicenceNo')}</dt>
            <dd className="font-mono">{c.tradeLicenceNo}</dd>
          </div>
          <div>
            <dt className="text-xs text-fg-muted">BIN</dt>
            <dd className="font-mono">{c.bin}</dd>
          </div>
          {c.tin && (
            <div>
              <dt className="text-xs text-fg-muted">TIN</dt>
              <dd className="font-mono">{c.tin}</dd>
            </div>
          )}
          <div>
            <dt className="text-xs text-fg-muted">{t('yourRoles')}</dt>
            <dd>{roleNames(roles).map((r) => t(`roles.${r}`)).join(', ')}</dd>
          </div>
        </dl>
        {c.status !== 'active' && (
          <div className="mt-4">
            <AlertBanner variant={c.status === 'pending' ? 'info' : 'warning'} title={t(`status.${c.status}`)}>
              {t(`statusHelp.${c.status}`)}
            </AlertBanner>
          </div>
        )}
      </section>

      <section className="rounded-lg border border-border-subtle bg-bg-surface p-5" aria-labelledby="credit-heading">
        <h2 id="credit-heading" className="text-sm font-semibold">
          {t('credit.title')}: {t(`credit.terms.${c.creditTerms}`)}
        </h2>
        {c.creditTerms === 'none' ? (
          <p className="mt-2 text-xs text-fg-muted">{t('credit.managedByMechware')}</p>
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
          </>
        )}
      </section>

      <Tabs defaultValue="sites">
        <TabsList>
          <TabsTrigger value="sites">{t('tabs.sites')}</TabsTrigger>
          <TabsTrigger value="costCentres">{t('tabs.costCentres')}</TabsTrigger>
          {isAdmin && <TabsTrigger value="members">{t('tabs.members')}</TabsTrigger>}
        </TabsList>
        <TabsContent value="sites">
          <SitesPanel companyId={companyId} canWrite={canWrite} />
        </TabsContent>
        <TabsContent value="costCentres">
          <CostCentresPanel companyId={companyId} canWrite={canWrite} />
        </TabsContent>
        {isAdmin && (
          <TabsContent value="members">
            <MembersPanel companyId={companyId} canWrite={canWrite} />
          </TabsContent>
        )}
      </Tabs>
    </div>
  );
}

export function CompanyDashboardView({ companyId }: { companyId: string }) {
  const t = useTranslations('Company');
  const toPath = useLocalePath();
  return (
    <PageShell title={t('title')} back={{ href: toPath('/account/company'), label: t('backToList') }}>
      <RequireSignIn>
        <Dashboard companyId={companyId} />
      </RequireSignIn>
    </PageShell>
  );
}
