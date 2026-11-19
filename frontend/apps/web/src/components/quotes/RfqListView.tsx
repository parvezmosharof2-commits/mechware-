'use client';

import * as React from 'react';
import Link from 'next/link';
import { useLocale, useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { classifyRfqError, rfqPageCount, rfqStatusKey, seesAllRfqs, type RfqPageDto } from '@mechware/api-client';
import { companiesApi } from '../../lib/api';
import { useAuth } from '../../lib/auth';
import { buyerRfqApi } from '../../lib/rfq-client';
import { useResource } from '../../lib/useResource';
import { ListSkeleton, PageShell, RequireSignIn, useLocalePath } from '../company/shared';
import { QuoteStatusBadge, formatDay, useNumberFormat } from './parts';

const PAGE_SIZE = 20;

type Loaded = { kind: 'ok'; page: RfqPageDto; all: boolean } | { kind: 'notFound' };

async function load(companyId: string, page: number): Promise<Loaded> {
  const [list, mine] = await Promise.allSettled([buyerRfqApi.list(companyId, { page, pageSize: PAGE_SIZE }), companiesApi.listMine()]);
  if (list.status === 'rejected') {
    // One 404 for every outsider (stranger, non-member, a company that cannot ask): one neutral text, never a crash.
    if (classifyRfqError(list.reason) === 'notFound') return { kind: 'notFound' };
    throw list.reason;
  }
  const roles = mine.status === 'fulfilled' ? (mine.value.data.find((m) => m.company.id.toLowerCase() === companyId.toLowerCase())?.roles ?? 0) : 0;
  return { kind: 'ok', page: list.value, all: seesAllRfqs(roles) };
}

function Panel({ companyId }: { companyId: string }) {
  const t = useTranslations('Quotes');
  const locale = useLocale() === 'bn' ? 'bn' : 'en';
  const fmt = useNumberFormat();
  const { user } = useAuth();
  const toPath = useLocalePath();
  const [page, setPage] = React.useState(1);
  const result = useResource(() => load(companyId, page), `${user?.id ?? ''}:${companyId}:${page}`);

  if (result.loading && !result.data) return <ListSkeleton rows={4} />;
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
      <div data-testid="rfq-not-found" className="space-y-2 rounded-lg border border-surface-border bg-bg-muted p-5">
        <h2 className="text-lg font-semibold">{t('notFoundTitle')}</h2>
        <p className="text-sm text-text-muted">{t('notFoundBody')}</p>
      </div>
    );
  }
  const pages = rfqPageCount(r.page.total, r.page.pageSize || PAGE_SIZE);
  return (
    <div className="space-y-4" aria-busy={result.loading}>
      <p className="text-sm text-text-muted" data-testid="rfq-scope">{r.all ? t('listScopeAll') : t('listScopeOwn')}</p>
      {r.page.items.length === 0 ? (
        <div data-testid="rfq-empty" className="space-y-3 rounded-lg border border-surface-border bg-bg-muted p-5">
          <p className="text-sm">{t('listEmpty')}</p>
          <Link prefetch={false} href={toPath('/request-quote?source=text')} className="text-sm font-semibold text-accent hover:underline">{t('askNew')}</Link>
        </div>
      ) : (
        <ul className="divide-y divide-surface-border rounded-lg border border-surface-border bg-bg-muted" data-testid="rfq-list">
          {r.page.items.map((q) => (
            <li key={q.id}>
              <Link prefetch={false} href={toPath(`/account/company/${encodeURIComponent(companyId)}/rfqs/${encodeURIComponent(q.id)}`)} className="flex flex-wrap items-center gap-x-4 gap-y-1 p-4 hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-accent">
                <span className="font-mono text-sm font-semibold">{q.number}</span>
                <QuoteStatusBadge status={q.status} withdrawn={q.withdrawnAt !== null} />
                <span className="text-sm text-text-muted">{t('lineCount', { count: q.lineCount, n: fmt(q.lineCount) })}</span>
                <span className="text-sm text-text-muted">{formatDay(q.createdAt, locale)}</span>
                {r.all && !q.requestedByMe && <span className="text-xs text-text-muted">{t('byColleague')}</span>}
                <span className="sr-only">{t(`status_${rfqStatusKey(q.status)}` as 'status_other')}</span>
              </Link>
            </li>
          ))}
        </ul>
      )}
      {pages > 1 && (
        <nav aria-label={t('pager')} className="flex items-center justify-between gap-3">
          <Button variant="outline" size="sm" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>{t('prev')}</Button>
          <span className="text-sm text-text-muted">{t('pageOf', { page: fmt(page), total: fmt(pages) })}</span>
          <Button variant="outline" size="sm" disabled={page >= pages} onClick={() => setPage((p) => p + 1)}>{t('next')}</Button>
        </nav>
      )}
    </div>
  );
}

export function RfqListView({ companyId }: { companyId: string }) {
  const t = useTranslations('Quotes');
  const toPath = useLocalePath();
  return (
    <PageShell title={t('listTitle')} back={{ href: toPath(`/account/company/${encodeURIComponent(companyId)}`), label: t('backToCompany') }}>
      <RequireSignIn>
        <Panel companyId={companyId} />
      </RequireSignIn>
    </PageShell>
  );
}
