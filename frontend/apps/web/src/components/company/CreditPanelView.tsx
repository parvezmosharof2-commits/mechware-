'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { classifyCreditError, type BuyerCredit, type CreditLoadKind } from '@mechware/api-client';
import { buyerCreditApi, companiesApi, problemStatus } from '../../lib/api';
import { useAuth } from '../../lib/auth';
import { useResource } from '../../lib/useResource';
import { BalanceSection, OpenItemsSection, StatementSection } from './CreditSections';
import { ListSkeleton, PageShell, RequireSignIn, useLocalePath } from './shared';

type Loaded = { kind: 'ok'; credit: BuyerCredit } | { kind: 'hidden' } | { kind: 'notFound' };

/**
 * The credit read answers 404 for a stranger AND for a member whose role may not see the balance (one body, on purpose). So the
 * person's own company list decides what the 404 means: a member gets the neutral "managed by your company admin" text, anyone else
 * the not-found text. Any other failure (network, 5xx, 429) is thrown so the page offers a retry.
 */
async function load(companyId: string): Promise<Loaded> {
  const [credit, mine] = await Promise.allSettled([buyerCreditApi.get(companyId), companiesApi.listMine()]);
  if (credit.status === 'fulfilled') return { kind: 'ok', credit: credit.value };
  const isMember = mine.status === 'fulfilled' && mine.value.data.some((m) => m.company.id.toLowerCase() === companyId.toLowerCase());
  const kind: CreditLoadKind = classifyCreditError(problemStatus(credit.reason), isMember);
  if (kind === 'hidden') return { kind };
  if (kind === 'notFound') return { kind };
  throw credit.reason;
}

function Message({ testId, title, body, action }: { testId: string; title: string; body: string; action?: React.ReactNode }) {
  return (
    <div data-testid={testId} className="space-y-3 rounded-lg border border-border-subtle bg-bg-surface p-5">
      <h2 className="text-lg font-semibold">{title}</h2>
      <p className="text-sm text-fg-secondary">{body}</p>
      {action}
    </div>
  );
}

function Panel({ companyId }: { companyId: string }) {
  const t = useTranslations('CompanyCredit');
  const { user } = useAuth();
  const toPath = useLocalePath();
  // The user id is part of the key: another person signing in on the same tab never sees the previous person's figures.
  const result = useResource(() => load(companyId), `${user?.id ?? ''}:${companyId}`);

  if (result.loading) return <ListSkeleton rows={3} />;
  if (result.error) {
    return (
      <div role="alert" className="space-y-3 rounded-lg border border-fault/50 bg-fault/10 p-4 text-sm">
        <p>{t('loadError')}</p>
        <Button variant="outline" size="sm" onClick={result.reload}>{t('retry')}</Button>
      </div>
    );
  }
  const r = result.data;
  if (!r || r.kind === 'notFound') {
    return (
      <Message
        testId="credit-not-found"
        title={t('notFoundTitle')}
        body={t('notFoundBody')}
        action={<Link href={toPath('/account/company')} className="text-sm font-semibold text-brand-primary hover:underline">{t('back')}</Link>}
      />
    );
  }
  if (r.kind === 'hidden') return <Message testId="credit-hidden" title={t('hiddenTitle')} body={t('hiddenBody')} />;
  return (
    <div className="space-y-6">
      <BalanceSection credit={r.credit} />
      <OpenItemsSection companyId={companyId} currency={r.credit.currency} />
      <StatementSection companyId={companyId} currency={r.credit.currency} />
    </div>
  );
}

export function CreditPanelView({ companyId }: { companyId: string }) {
  const t = useTranslations('CompanyCredit');
  const toPath = useLocalePath();
  return (
    <PageShell title={t('title')} back={{ href: toPath(`/account/company/${encodeURIComponent(companyId)}`), label: t('back') }}>
      <RequireSignIn>
        <Panel companyId={companyId} />
      </RequireSignIn>
    </PageShell>
  );
}
