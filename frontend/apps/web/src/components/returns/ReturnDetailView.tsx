'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { Skeleton } from '@mechware/ui/src/components/Skeleton';
import { canWithdrawReturn, problemCode, problemStatus, returnErrorKey, type BuyerReturn } from '@mechware/api-client';
import { useAuth } from '../../lib/auth';
import { classifyOrderError, formatOrderDate, orderHref } from '../../lib/orders-ui';
import { returnsApi } from '../../lib/returns-client';
import { useResource } from '../../lib/useResource';
import { ReturnAlert, ReturnStatusChip } from './ReturnParts';

const linkClass = 'inline-flex h-10 items-center rounded-md border border-surface-border px-4 text-sm font-semibold text-text hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';

function Withdraw({ ret, onDone, onFail }: { ret: BuyerReturn; onDone: (r: BuyerReturn) => void; onFail: (key: string) => void }) {
  const t = useTranslations('Returns');
  const [ask, setAsk] = React.useState(false);
  const [busy, setBusy] = React.useState(false);
  const run = async () => {
    setBusy(true);
    try {
      onDone(await returnsApi.withdraw(ret.id));
    } catch (e) {
      setAsk(false);
      onFail(returnErrorKey(problemStatus(e), problemCode(e)));
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="space-y-3">
      {ask ? (
        <div role="group" aria-label={t('withdraw.title')} className="space-y-2 rounded-md border border-surface-border p-3">
          <p className="text-sm text-text">{t('withdraw.confirm')}</p>
          <div className="flex flex-wrap gap-3">
            <Button type="button" variant="destructive" onClick={run} isLoading={busy} disabled={busy} data-testid="withdraw-confirm">{t('withdraw.yes')}</Button>
            <Button type="button" variant="outline" onClick={() => setAsk(false)} disabled={busy}>{t('withdraw.no')}</Button>
          </div>
        </div>
      ) : (
        <div className="space-y-1">
          <Button type="button" variant="outline" onClick={() => setAsk(true)} data-testid="withdraw-open">{t('withdraw.title')}</Button>
          <p className="text-xs text-text-muted">{t('withdraw.help')}</p>
        </div>
      )}
    </div>
  );
}

/** One return: status in words, dates, the staff's rejection reason, what was asked and accepted, and a withdraw button until staff decide. */
export function ReturnDetailView({ locale, returnId }: { locale: 'en' | 'bn'; returnId: string }) {
  const t = useTranslations('Returns');
  const { status } = useAuth();
  const prefix = locale === 'bn' ? '/bn' : '';
  const res = useResource(() => returnsApi.get(returnId), returnId, status === 'authenticated');
  const [override, setOverride] = React.useState<BuyerReturn | null>(null);
  const [failure, setFailure] = React.useState<string | null>(null);
  const back = <Link prefetch={false} href={`${prefix}/account/returns`} className={linkClass}>{t('backToReturns')}</Link>;

  if (res.error) {
    if (classifyOrderError(res.error) === 'notFound') {
      return (
        <div data-testid="return-not-found" className="space-y-4 rounded-lg border border-surface-border p-6">
          <h2 className="font-display text-xl font-bold text-text">{t('notFoundTitle')}</h2>
          <p className="text-text-muted">{t('notFoundBody')}</p>
          {back}
        </div>
      );
    }
    return (
      <div role="alert" className="space-y-3 rounded-lg border border-fault/50 bg-fault/10 p-4">
        <p className="text-text">{t('loadError')}</p>
        <Button onClick={res.reload}>{t('retry')}</Button>
      </div>
    );
  }
  const r = override ?? res.data;
  if (!r) return <div aria-hidden="true" className="space-y-3"><Skeleton className="h-12 w-full" /><Skeleton className="h-40 w-full" /></div>;

  const dates: Array<[string, string | null]> = [
    [t('detail.requestedAt'), r.requestedAtUtc], [t('detail.decidedAt'), r.decidedAtUtc], [t('detail.receivedAt'), r.receivedAtUtc],
    [t('detail.inspectedAt'), r.inspectedAtUtc], [t('detail.withdrawnAt'), r.withdrawnAtUtc]
  ];
  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <p data-testid="return-order-number" className="break-all font-mono text-xl font-bold text-text">{r.orderNumber}</p>
          <Link prefetch={false} href={orderHref(prefix, r.orderNumber)} className="text-sm font-semibold text-accent underline">{t('viewOrder')}</Link>
        </div>
        <ReturnStatusChip status={r.status} />
      </div>
      {r.status === 'rejected' && r.rejectionReason && (
        <p data-testid="return-rejection" className="rounded-md border border-fault/50 bg-fault/10 p-3 text-sm text-text">
          <span className="font-semibold">{t('detail.rejectionReason')}</span> {r.rejectionReason}
        </p>
      )}
      <dl className="space-y-1.5 rounded-lg border border-surface-border p-4 text-sm">
        {dates.filter(([, v]) => v).map(([k, v]) => (
          <div key={k} className="flex justify-between gap-4"><dt className="text-text-muted">{k}</dt><dd className="text-text">{formatOrderDate(v, locale)}</dd></div>
        ))}
        <div className="flex justify-between gap-4"><dt className="text-text-muted">{t('detail.reason')}</dt><dd className="text-text">{t(`reason.${r.reasonCode}`)}</dd></div>
        {r.note && <div className="flex justify-between gap-4"><dt className="text-text-muted">{t('detail.note')}</dt><dd className="break-words text-right text-text">{r.note}</dd></div>}
      </dl>
      <section aria-labelledby="return-lines" className="rounded-lg border border-surface-border p-4">
        <h2 id="return-lines" className="mb-2 font-display text-lg font-bold text-text">{t('detail.lines')}</h2>
        <ul className="divide-y divide-surface-border">
          {r.lines.map((l) => (
            <li key={l.orderItemId} data-testid="return-line" className="py-3">
              <p className="break-words font-medium text-text">{l.title}</p>
              <p className="text-sm text-text-muted"><span className="break-all font-mono">{l.sku}</span></p>
              <p className="text-sm text-text">
                {t('detail.asked', { count: l.quantity })} · <span data-testid="return-accepted">{l.acceptedQuantity === null ? t('detail.notInspected') : t('detail.accepted', { count: l.acceptedQuantity })}</span>
              </p>
            </li>
          ))}
        </ul>
      </section>
      {failure && <ReturnAlert>{t(`error.${failure}`)}</ReturnAlert>}
      {canWithdrawReturn(r.status) && (
        <Withdraw ret={r} onDone={(next) => { setFailure(null); setOverride(next); }}
          onFail={(key) => { setFailure(key); if (key === 'notWithdrawable') { setOverride(null); res.reload(); } }} />
      )}
      {back}
    </div>
  );
}
