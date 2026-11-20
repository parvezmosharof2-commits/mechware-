'use client';

import * as React from 'react';
import Link from 'next/link';
import { useLocale, useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { QUOTE_FILTERS, classifyQuoteError, quotePageCount, quotedTotal, type BuyerQuotePage } from '@mechware/api-client';
import { useAuth } from '../../lib/auth';
import { buyerQuotesApi } from '../../lib/quotes-client';
import { useResource } from '../../lib/useResource';
import { ListSkeleton, PageShell, RequireSignIn, useLocalePath } from '../company/shared';
import { BuyerQuoteBadge, formatDay, useNumberFormat, useQuoteMoney } from './parts';

const PAGE_SIZE = 20;

type Loaded = { kind: 'ok'; page: BuyerQuotePage } | { kind: 'notFound' };

async function load(companyId: string, status: string, page: number): Promise<Loaded> {
  try {
    return { kind: 'ok', page: await buyerQuotesApi.list(companyId, { status, page, pageSize: PAGE_SIZE }) };
  } catch (e) {
    if (classifyQuoteError(e) === 'notFound') return { kind: 'notFound' }; // one neutral answer for every outsider
    throw e;
  }
}

function Panel({ companyId }: { companyId: string }) {
  const t = useTranslations('BuyerQuotes');
  const locale = useLocale() === 'bn' ? 'bn' : 'en';
  const fmt = useNumberFormat();
  const money = useQuoteMoney();
  const { user } = useAuth();
  const toPath = useLocalePath();
  const [page, setPage] = React.useState(1);
  const [status, setStatus] = React.useState('');
  const result = useResource(() => load(companyId, status, page), `${user?.id ?? ''}:${companyId}:${status}:${page}`);

  const filter = (
    <div>
      <label htmlFor="quote-filter" className="mb-1 block text-sm font-semibold">{t('filter')}</label>
      <select id="quote-filter" data-testid="quote-filter" value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }} className="h-11 w-full rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm sm:w-64">
        {QUOTE_FILTERS.map((f) => <option key={f} value={f}>{f ? t(`status_${f}` as 'status_sent') : t('filterAll')}</option>)}
      </select>
    </div>
  );

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
      <div data-testid="quote-not-found" className="space-y-2 rounded-lg border border-surface-border bg-bg-muted p-5">
        <h2 className="text-lg font-semibold">{t('notFoundTitle')}</h2>
        <p className="text-sm text-text-muted">{t('notFoundBody')}</p>
      </div>
    );
  }
  const pages = quotePageCount(r.page.total, r.page.pageSize || PAGE_SIZE);
  return (
    <div className="space-y-4" aria-busy={result.loading}>
      {filter}
      {r.page.items.length === 0 ? (
        <div data-testid="quote-empty" className="rounded-lg border border-surface-border bg-bg-muted p-5 text-sm">{t('listEmpty')}</div>
      ) : (
        <ul className="divide-y divide-surface-border rounded-lg border border-surface-border bg-bg-muted" data-testid="quote-list">
          {r.page.items.map((q) => (
            <li key={q.id}>
              <Link prefetch={false} href={toPath(`/account/company/${encodeURIComponent(companyId)}/quotes/${encodeURIComponent(q.id)}`)} className="flex flex-wrap items-center gap-x-4 gap-y-1 p-4 hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-accent">
                <span className="font-mono text-sm font-semibold">{q.number}</span>
                <BuyerQuoteBadge status={q.status} />
                <span className="text-sm font-semibold">{money(quotedTotal(q).minor)}{!quotedTotal(q).withDelivery && <span className="font-normal text-text-muted"> {t('goodsOnly')}</span>}</span>
                <span className="text-sm text-text-muted">{t('lineCount', { n: fmt(q.lineCount) })}</span>
                <span className="text-sm text-text-muted">{t('validUntilShort', { date: formatDay(q.validUntil, locale) })}</span>
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

export function QuoteListView({ companyId }: { companyId: string }) {
  const t = useTranslations('BuyerQuotes');
  const toPath = useLocalePath();
  return (
    <PageShell title={t('listTitle')} back={{ href: toPath(`/account/company/${encodeURIComponent(companyId)}`), label: t('backToCompany') }}>
      <RequireSignIn>
        <Panel companyId={companyId} />
      </RequireSignIn>
    </PageShell>
  );
}
