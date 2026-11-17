'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import {
  classifyCartError,
  classifyQuickOrderError,
  formatPriceMinor,
  planReorder,
  reorderReport,
  reorderText,
  reorderWanted,
  type OrderDetail,
  type ReorderCartOutcome,
  type ReorderPlan
} from '@mechware/api-client';
import { announceCart } from '../../lib/cart-events';

type Phase = 'idle' | 'checking' | 'adding' | 'done' | 'error';
type Report = ReturnType<typeof reorderReport>;

const CART_REASONS = ['insufficientStock', 'cartFull', 'rateLimited', 'unavailable', 'notFound'];
const SKIP_REASONS = ['notFound', 'obsolete', 'outOfStock', 'insufficientStock', 'qtyExceeds', 'ambiguous', 'changed', 'unreadable', 'tooMany'];

/** "Buy again" on the buyer's own order: the lines still on sale go into the cart at today's price; the rest are listed with the reason. */
export function ReorderPanel({ order, locale }: { order: OrderDetail; locale: 'en' | 'bn' }) {
  const t = useTranslations('Reorder');
  const prefix = locale === 'bn' ? '/bn' : '';
  const [phase, setPhase] = React.useState<Phase>('idle');
  const [errorKind, setErrorKind] = React.useState<'rateLimited' | 'unavailable' | 'other'>('other');
  const [report, setReport] = React.useState<Report | null>(null);
  const resultRef = React.useRef<HTMLDivElement>(null);
  const mounted = React.useRef(true);
  React.useEffect(() => () => { mounted.current = false; }, []);
  const fmt = React.useMemo(() => new Intl.NumberFormat(locale === 'bn' ? 'bn-BD' : 'en-US'), [locale]);
  const money = (minor: number) => formatPriceMinor(minor, order.currency, locale);
  const busy = phase === 'checking' || phase === 'adding';

  React.useEffect(() => {
    if (phase === 'done' || phase === 'error') resultRef.current?.focus();
  }, [phase]);

  const run = async () => {
    if (busy) return;
    const wanted = reorderWanted(order);
    setReport(null);
    if (wanted.length === 0) {
      setReport({ added: [], refused: [], skipped: [] });
      setPhase('done');
      return;
    }
    setPhase('checking');
    let plan: ReorderPlan;
    try {
      const { quickOrderApi } = await import('../../lib/quick-order-client');
      plan = planReorder(wanted, await quickOrderApi.validate(reorderText(wanted)));
    } catch (e) {
      if (!mounted.current) return;
      const kind = classifyQuickOrderError(e);
      setErrorKind(kind === 'rateLimited' ? 'rateLimited' : kind === 'unavailable' ? 'unavailable' : 'other');
      setPhase('error');
      return;
    }
    if (!mounted.current) return;
    setPhase('adding');
    const { cartApi } = await import('../../lib/cart-client');
    const outcomes: ReorderCartOutcome[] = [];
    let stop: string | null = null;
    for (const add of plan.add) {
      if (stop) {
        outcomes.push({ add, ok: false, reason: stop });
        continue;
      }
      try {
        announceCart((await cartApi.addItem(add.wanted.variantId, add.quantity)).lineCount);
        outcomes.push({ add, ok: true });
      } catch (e) {
        const reason = classifyCartError(e);
        if (reason === 'rateLimited') stop = reason; // every further call would be refused too
        outcomes.push({ add, ok: false, reason });
      }
    }
    if (!mounted.current) return;
    setReport(reorderReport(plan, outcomes));
    setPhase('done');
  };

  const cartReason = (r: string) => t(`cart_${CART_REASONS.includes(r) ? r : 'other'}` as 'cart_other');

  return (
    <section aria-labelledby="reorder-heading" className="space-y-3 rounded-lg border border-surface-border p-4">
      <h2 id="reorder-heading" className="font-display text-lg font-bold text-text">{t('title')}</h2>
      <p className="text-sm text-text-muted">{t('intro')}</p>
      <Button type="button" variant="accent" disabled={busy} aria-busy={busy} onClick={() => void run()} data-testid="reorder-button">
        {phase === 'checking' ? t('checking') : phase === 'adding' ? t('adding') : t('button')}
      </Button>
      <div ref={resultRef} tabIndex={-1} data-testid="reorder-result" className="space-y-3 outline-none focus-visible:ring-2 focus-visible:ring-accent" role={phase === 'error' ? 'alert' : 'status'} aria-live="polite">
        {phase === 'error' && <p className="text-text">{t(`error_${errorKind}`)}</p>}
        {phase === 'done' && report && (
          <>
            <p className="font-semibold text-text" data-testid="reorder-summary">
              {report.added.length === 0 ? t('addedNone') : t('addedCount', { count: report.added.length, n: fmt.format(report.added.length) })}
            </p>
            {report.added.length > 0 && (
              <>
                <ul className="divide-y divide-surface-border text-sm" data-testid="reorder-added">
                  {report.added.map((a) => (
                    <li key={a.wanted.variantId} className="py-2">
                      <p className="break-words font-medium text-text">{a.wanted.title}</p>
                      <p className="text-text-muted">
                        <span className="break-all font-mono">{a.wanted.sku}</span> · {t('qty', { count: a.quantity, n: fmt.format(a.quantity) })}
                        {a.unitPriceMinor !== null && <> · {t('priceNow', { amount: money(a.unitPriceMinor) })}</>}
                      </p>
                      {a.adjusted && <p className="text-text">{t('qtyAdjusted', { was: fmt.format(a.wanted.quantity), now: fmt.format(a.quantity) })}</p>}
                      {a.priceChanged && <p className="text-text">{t('priceChanged', { was: money(a.wanted.previousUnitPriceMinor) })}</p>}
                    </li>
                  ))}
                </ul>
                <Link prefetch={false} href={`${prefix}/cart`} className="inline-flex h-11 items-center rounded-md bg-accent px-6 text-sm font-semibold text-accent-fg hover:bg-accent-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent">
                  {t('goToCart')}
                </Link>
              </>
            )}
            {(report.skipped.length > 0 || report.refused.length > 0) && (
              <div className="rounded-md border border-fault/50 bg-fault/10 p-3" data-testid="reorder-notadded">
                <h3 className="font-semibold text-text">{t('notAddedTitle', { count: report.skipped.length + report.refused.length, n: fmt.format(report.skipped.length + report.refused.length) })}</h3>
                <ul className="mt-1 space-y-2 text-sm">
                  {report.skipped.map((s) => (
                    <li key={s.wanted.variantId}>
                      <span className="break-words font-medium text-text">{s.wanted.title}</span> <span className="break-all font-mono text-text-muted">({s.wanted.sku})</span>
                      <span className="block text-text">
                        {t(`skip_${SKIP_REASONS.includes(s.reason) ? s.reason : 'other'}` as 'skip_other', { available: fmt.format(s.available ?? 0) })}
                      </span>
                    </li>
                  ))}
                  {report.refused.map((r) => (
                    <li key={r.add.wanted.variantId}>
                      <span className="break-words font-medium text-text">{r.add.wanted.title}</span> <span className="break-all font-mono text-text-muted">({r.add.wanted.sku})</span>
                      <span className="block text-text">{cartReason(r.reason)}</span>
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </>
        )}
      </div>
    </section>
  );
}
