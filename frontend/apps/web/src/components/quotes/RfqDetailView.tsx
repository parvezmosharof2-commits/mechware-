'use client';

import * as React from 'react';
import Link from 'next/link';
import { useSearchParams } from 'next/navigation';
import { useLocale, useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@mechware/ui/src/components/Dialog';
import { canWithdrawRfq, classifyRfqError, rfqLineKey, rfqLineCounts, variantTitle, type RfqDetail, type RfqErrorKind, type RfqLine } from '@mechware/api-client';
import { companiesApi } from '../../lib/api';
import { useAuth } from '../../lib/auth';
import { buyerRfqApi } from '../../lib/rfq-client';
import { useResource } from '../../lib/useResource';
import { ListSkeleton, PageShell, RequireSignIn, useLocalePath } from '../company/shared';
import { QuoteStatusBadge, formatDay, useNumberFormat } from './parts';

type Loaded = { kind: 'ok'; detail: RfqDetail; siteName: string | null } | { kind: 'notFound' };

async function load(companyId: string, rfqId: string): Promise<Loaded> {
  try {
    const detail = await buyerRfqApi.get(companyId, rfqId);
    let siteName: string | null = null;
    if (detail.rfq.shipToSiteId) {
      try {
        siteName = (await companiesApi.listSites(companyId)).data.find((s) => s.id === detail.rfq.shipToSiteId)?.name ?? null;
      } catch {
        siteName = null;
      }
    }
    return { kind: 'ok', detail, siteName };
  } catch (e) {
    if (classifyRfqError(e) === 'notFound') return { kind: 'notFound' };
    throw e;
  }
}

function LineRow({ line, locale }: { line: RfqLine; locale: 'en' | 'bn' }) {
  const t = useTranslations('Quotes');
  const fmt = useNumberFormat();
  const kind = rfqLineKey(line.matchStatus);
  return (
    <li className="space-y-1 p-4" data-testid="rfq-line" data-match={kind}>
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <span className="font-mono text-sm">{line.rawText}</span>
        <span className="text-sm text-text-muted">{t('qty', { n: fmt(line.quantity) })}</span>
      </div>
      <p className="text-sm">
        <span className="font-semibold">{t(`match_${kind}` as 'match_other')}</span>
        {line.variant && <span>{': '}{variantTitle(line.variant, locale)} <span className="font-mono text-text-muted">({line.variant.sku})</span></span>}
      </p>
      {kind === 'obsolete' && line.successor && (
        <p className="text-sm text-text-muted" data-testid="rfq-successor">
          {t('successor')} {variantTitle(line.successor, locale)} <span className="font-mono">({line.successor.sku})</span>
        </p>
      )}
      {kind === 'obsolete' && !line.successor && <p className="text-sm text-text-muted">{t('noSuccessor')}</p>}
      {kind === 'ambiguous' && <p className="text-sm text-text-muted">{t('ambiguousHint')}</p>}
      {kind === 'notFound' && <p className="text-sm text-text-muted">{t('notFoundHint')}</p>}
      {line.note && <p className="text-xs text-text-muted">{line.note}</p>}
    </li>
  );
}

function Panel({ companyId, rfqId }: { companyId: string; rfqId: string }) {
  const t = useTranslations('Quotes');
  const locale = useLocale() === 'bn' ? 'bn' : 'en';
  const fmt = useNumberFormat();
  const { user } = useAuth();
  const toPath = useLocalePath();
  const created = useSearchParams().get('created') === '1';
  const result = useResource(() => load(companyId, rfqId), `${user?.id ?? ''}:${companyId}:${rfqId}`);
  const [confirming, setConfirming] = React.useState(false);
  const [busy, setBusy] = React.useState(false);
  const [done, setDone] = React.useState<RfqDetail | null>(null);
  const [failure, setFailure] = React.useState<RfqErrorKind | null>(null);
  const withdrawing = React.useRef(false);

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
        <Link prefetch={false} href={toPath(`/account/company/${encodeURIComponent(companyId)}/rfqs`)} className="text-sm font-semibold text-accent hover:underline">{t('backToList')}</Link>
      </div>
    );
  }
  const { rfq, lines } = done ?? r.detail;
  const counts = rfqLineCounts(lines);

  const withdraw = async () => {
    if (withdrawing.current) return;
    withdrawing.current = true;
    setBusy(true);
    setFailure(null);
    try {
      setDone(await buyerRfqApi.withdraw(companyId, rfqId));
      setConfirming(false);
    } catch (e) {
      setFailure(classifyRfqError(e));
      setConfirming(false);
    } finally {
      withdrawing.current = false;
      setBusy(false);
    }
  };

  return (
    <div className="space-y-6">
      {created && !done && (
        <p role="status" data-testid="rfq-created" className="rounded-md border border-status-success/50 bg-status-success/10 px-4 py-3 text-sm">{t('createdNote', { number: rfq.number })}</p>
      )}
      {done && <p role="status" data-testid="rfq-withdrawn" className="rounded-md border border-surface-border-strong bg-bg-subtle px-4 py-3 text-sm">{t('withdrawnNote')}</p>}
      {failure && <p role="alert" data-testid="rfq-withdraw-error" className="rounded-md border border-fault/40 bg-fault/10 px-4 py-3 text-sm">{t(failure === 'closed' ? 'error_closed' : failure === 'notFound' ? 'notFoundBody' : 'withdrawFailed')}</p>}

      <section aria-labelledby="rfq-head" className="rounded-lg border border-surface-border bg-bg-muted p-4">
        <div className="flex flex-wrap items-center gap-3">
          <h2 id="rfq-head" className="font-mono text-lg font-bold" data-testid="rfq-number">{rfq.number}</h2>
          <QuoteStatusBadge status={rfq.status} withdrawn={rfq.withdrawnAt !== null} />
        </div>
        <dl className="mt-3 grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
          <div><dt className="text-text-muted">{t('sent')}</dt><dd>{formatDay(rfq.createdAt, locale)}</dd></div>
          <div><dt className="text-text-muted">{t('source')}</dt><dd>{t(`source_${['cart', 'text', 'csv', 'product'].includes(rfq.source) ? rfq.source : 'text'}` as 'source_text')}</dd></div>
          {rfq.neededBy && <div><dt className="text-text-muted">{t('neededBy')}</dt><dd>{formatDay(rfq.neededBy, locale)}</dd></div>}
          {rfq.shipToSiteId && <div><dt className="text-text-muted">{t('site')}</dt><dd>{r.siteName ?? t('siteGone')}</dd></div>}
          {rfq.closedAt && <div><dt className="text-text-muted">{t('closed')}</dt><dd>{formatDay(rfq.closedAt, locale)}</dd></div>}
          {rfq.closeReason && <div><dt className="text-text-muted">{t('closeReason')}</dt><dd>{rfq.closeReason}</dd></div>}
        </dl>
        {!rfq.requestedByMe && <p className="mt-2 text-sm text-text-muted">{t('byColleague')}</p>}
        {rfq.message && <p className="mt-3 whitespace-pre-line rounded bg-bg-subtle p-3 text-sm" data-testid="rfq-message">{rfq.message}</p>}
      </section>

      <section aria-labelledby="rfq-lines">
        <h2 id="rfq-lines" className="mb-1 text-lg font-bold">{t('linesHeading')} ({fmt(lines.length)})</h2>
        <p className="mb-2 text-sm text-text-muted" data-testid="rfq-counts">{t('linesSummary', { matched: fmt(counts.matched), obsolete: fmt(counts.obsolete), unmatched: fmt(counts.unmatched) })}</p>
        <ul className="divide-y divide-surface-border rounded-lg border border-surface-border bg-bg-muted">
          {lines.map((l) => <LineRow key={l.lineNo} line={l} locale={locale} />)}
        </ul>
        <p className="mt-2 text-xs text-text-muted">{t('noPriceNote')}</p>
      </section>

      {canWithdrawRfq(rfq) && (
        <div>
          <Button variant="outline" onClick={() => setConfirming(true)} disabled={busy} data-testid="rfq-withdraw">{t('withdraw')}</Button>
        </div>
      )}
      <Link prefetch={false} href={toPath(`/account/company/${encodeURIComponent(companyId)}/rfqs`)} className="inline-block text-sm font-semibold text-accent hover:underline">{t('backToList')}</Link>

      <Dialog open={confirming} onOpenChange={(open) => !open && !busy && setConfirming(false)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t('withdrawTitle', { number: rfq.number })}</DialogTitle>
            <DialogDescription>{t('withdrawBody')}</DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setConfirming(false)} disabled={busy}>{t('keep')}</Button>
            <Button variant="destructive" onClick={() => void withdraw()} isLoading={busy} data-testid="rfq-withdraw-confirm">{t('withdrawConfirm')}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}

export function RfqDetailView({ companyId, rfqId }: { companyId: string; rfqId: string }) {
  const t = useTranslations('Quotes');
  const toPath = useLocalePath();
  return (
    <PageShell title={t('detailTitle')} back={{ href: toPath(`/account/company/${encodeURIComponent(companyId)}/rfqs`), label: t('backToList') }}>
      <RequireSignIn>
        <React.Suspense fallback={<ListSkeleton rows={4} />}>
          <Panel companyId={companyId} rfqId={rfqId} />
        </React.Suspense>
      </RequireSignIn>
    </PageShell>
  );
}
