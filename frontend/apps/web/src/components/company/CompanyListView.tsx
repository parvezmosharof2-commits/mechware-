'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { Badge, Button, EmptyState, useToast } from '@mechware/ui';
import { roleNames, type CompanyStatus, type MyCompanyDto } from '@mechware/api-client';
import { companiesApi, problemMessage } from '../../lib/api';
import { useResource, type Resource } from '../../lib/useResource';
import { ListSkeleton, LoadError, PageShell, RequireSignIn, useLocalePath } from './shared';

export function StatusBadge({ status }: { status: CompanyStatus }) {
  const t = useTranslations('Company.status');
  const variant = status === 'active' ? 'success' : status === 'pending' ? 'low-stock' : 'deal';
  return <Badge variant={variant}>{t(status)}</Badge>;
}

/** Companies that invited me: nothing changes for me until I accept (threat model v1, M2). */
function Invitations({ onAnswered }: { onAnswered: () => void }) {
  const t = useTranslations('Company');
  const { toast } = useToast();
  const invitations = useResource(async () => (await companiesApi.listInvitations()).data, 'invitations');
  const [busy, setBusy] = React.useState<string | null>(null);

  const answer = async (companyId: string, accept: boolean) => {
    setBusy(companyId);
    try {
      if (accept) await companiesApi.acceptInvitation(companyId);
      else await companiesApi.declineInvitation(companyId);
      toast({ variant: 'success', title: accept ? t('invitations.accepted') : t('invitations.declined') });
      invitations.reload();
      onAnswered();
    } catch (err) {
      toast({ variant: 'fault', title: problemMessage(err, t('invitations.failed')) });
    } finally {
      setBusy(null);
    }
  };

  const items = invitations.data ?? [];
  if (invitations.loading || invitations.error || items.length === 0) return null;
  return (
    <section aria-labelledby="invitations-title" className="mb-6 rounded-lg border border-border-subtle bg-bg-surface p-4">
      <h2 id="invitations-title" className="font-semibold">
        {t('invitations.title')}
      </h2>
      <p className="mt-1 text-xs text-fg-muted">{t('invitations.help')}</p>
      <ul className="mt-3 space-y-3">
        {items.map((inv) => (
          <li key={inv.companyId} className="flex flex-wrap items-center justify-between gap-3">
            <div className="min-w-0">
              <span className="font-medium">{inv.companyLegalName}</span>
              <div className="text-xs text-fg-muted">
                {t('yourRoles')}: {roleNames(inv.roles).map((r) => t(`roles.${r}`)).join(', ')}
              </div>
            </div>
            <div className="flex gap-2">
              <Button size="sm" disabled={busy !== null} onClick={() => answer(inv.companyId, true)}>
                {t('invitations.accept')}
              </Button>
              <Button size="sm" variant="outline" disabled={busy !== null} onClick={() => answer(inv.companyId, false)}>
                {t('invitations.decline')}
              </Button>
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}

function List() {
  const mine = useResource(async () => (await companiesApi.listMine()).data, 'mine');
  return (
    <>
      <Invitations onAnswered={mine.reload} />
      <Mine mine={mine} />
    </>
  );
}

function Mine({ mine }: { mine: Resource<MyCompanyDto[]> }) {
  const t = useTranslations('Company');
  const toPath = useLocalePath();

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
